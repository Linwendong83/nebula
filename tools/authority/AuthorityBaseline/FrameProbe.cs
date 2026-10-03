using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using HarmonyLib;
using NebulaWorld;

namespace AuthorityBaseline;

/// <summary>
/// A01 frame-boundary probe.
///
/// The design assumes there is a "safe frame point" where a host may apply accepted commands and
/// capture facts. That is only true if every writer has stopped by then. This probe measures it
/// instead of assuming it:
///
///  * <c>GameMain.FixedUpdate</c> calls <c>GameLogic.LogicFrame</c> once per game tick on the main
///    thread. <c>ThreadManager.ProcessFrame</c> then walks the task list, with workers walking the
///    same list in parallel and syncing only at <c>phaseBarrierMask</c> tasks.
///  * Because only tasks whose enum value % 10 == 0 are phase-barrier synced, main and workers can
///    be at different points after the last such task. So the end of <c>LogicFrame</c> is NOT
///    automatically a point where all worker writes have stopped.
///
/// The probe counts workers still inside <c>OnGameLogicFrame</c> at the moment a
/// <c>LogicFrame</c> postfix runs, and records the ordered relationship between the network
/// <c>Update</c> (where packets are dequeued) and the logic frame.
///
/// Read-only: counters and a bounded log. No game state is touched.
/// </summary>
internal static class FrameProbe
{
    private static Harmony harmony;

    // Workers currently executing inside GameLogic.OnGameLogicFrame.
    private static int workersInsideTask;
    // Set while the main thread is between ProcessFrame entry and exit.
    private static int mainInsideProcessFrame;
    private static long logicFrameCount;
    private static long processFrameCount;

    // Frame-end observations: how often a LogicFrame postfix ran while workers were still in a task.
    private static int frameEndSamples;
    private static int frameEndWithWorkersBusy;
    private static int frameEndMaxWorkersBusy;
    private static long frameEndSumWorkersBusy;
    // Instrument sanity: the highest concurrent worker count ever observed. If this never
    // reaches the worker count, the counter itself is broken and the 0% frame-end result
    // would be meaningless.
    private static int maxConcurrentWorkersEver;

    // Per-task attribution: which thread ordinals executed this task value, and how often.
    private static readonly ConcurrentDictionary<int, TaskStat> TaskStats = new();

    // Ordered structural event log, captured only while recordRemaining > 0.
    private static readonly object LogGate = new();
    private static readonly List<string> EventLog = new();
    private static int recordRemaining;
    private static long eventSequence;
    private static int lastSerialThreadCount = -1;

    private sealed class TaskStat
    {
        public int Count;
        public int MainThreadCount;
        public int WorkerCount;
        public readonly ConcurrentDictionary<int, int> ThreadOrdinals = new();
        public int LastThreadCount;
    }

    public static void Install()
    {
        harmony = new Harmony("nebula.tests.authority-frameprobe");
        Patch("GameLogic.LogicFrame", typeof(GameLogic), "LogicFrame", nameof(LogicFrame_Prefix), nameof(LogicFrame_Postfix));
        Patch("GameLogic.OnGameLogicFrame", typeof(GameLogic), "OnGameLogicFrame",
            nameof(OnGameLogicFrame_Prefix), nameof(OnGameLogicFrame_Postfix));
        Patch("ThreadManager.ProcessFrame", typeof(ThreadManager), "ProcessFrame",
            nameof(ProcessFrame_Prefix), nameof(ProcessFrame_Postfix));
        Patch("GameMain.FixedUpdate", typeof(GameMain), "FixedUpdate", nameof(FixedUpdate_Prefix), nameof(FixedUpdate_Postfix));
        Patch("GameMain.Update", typeof(GameMain), "Update", nameof(GameUpdate_Prefix), nameof(GameUpdate_Postfix));
        // Packet dequeuing happens here; its position relative to the logic frame is what A01
        // needs in order to place "apply accepted commands before the rules run".
        Patch("NebulaNetwork.Server.Update", typeof(NebulaNetwork.Server), "Update",
            nameof(ServerUpdate_Prefix), nameof(ServerUpdate_Postfix));
        Patch("NebulaNetwork.Client.Update", typeof(NebulaNetwork.Client), "Update",
            nameof(ClientUpdate_Prefix), nameof(ClientUpdate_Postfix));
    }

    private static void Patch(string label, Type type, string methodName, string prefix, string postfix)
    {
        try
        {
            var target = AccessTools.Method(type, methodName);
            if (target == null)
            {
                BaselineRuntime.HookFailures++;
                lock (BaselineRuntime.HookReport) BaselineRuntime.HookReport.Add("FAIL " + label + " method not found");
                return;
            }
            var prefixMethod = prefix == null ? null : AccessTools.Method(typeof(FrameProbe), prefix);
            var postfixMethod = postfix == null ? null : AccessTools.Method(typeof(FrameProbe), postfix);
            harmony.Patch(target,
                prefixMethod == null ? null : new HarmonyMethod(prefixMethod),
                postfixMethod == null ? null : new HarmonyMethod(postfixMethod));
            lock (BaselineRuntime.HookReport) BaselineRuntime.HookReport.Add("ok " + label + " -> " + target);
        }
        catch (Exception error)
        {
            BaselineRuntime.HookFailures++;
            lock (BaselineRuntime.HookReport) BaselineRuntime.HookReport.Add("FAIL " + label + " " + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Starts capturing the ordered structural event log for the next <paramref name="frames"/> frames.</summary>
    public static void StartRecording(int frames)
    {
        lock (LogGate)
        {
            EventLog.Clear();
            eventSequence = 0;
            recordRemaining = Math.Max(1, frames);
        }
    }

    private static void RecordEvent(string text)
    {
        if (recordRemaining <= 0) return;
        var tick = BaselineRuntime.GameTick();
        var thread = Thread.CurrentThread.ManagedThreadId;
        lock (LogGate)
        {
            EventLog.Add($"seq={Interlocked.Increment(ref eventSequence)} tick={tick} thread={thread} {text}");
        }
    }

    private static bool Recording => recordRemaining > 0;

    // ---------------- structural boundaries ----------------

    private static void FixedUpdate_Prefix()
    {
        try { if (Recording) RecordEvent("GameMain.FixedUpdate enter"); } catch (Exception) { }
    }

    private static void FixedUpdate_Postfix()
    {
        try
        {
            if (!Recording) return;
            RecordEvent("GameMain.FixedUpdate exit");
            lock (LogGate)
            {
                if (recordRemaining > 0) recordRemaining--;
            }
        }
        catch (Exception) { }
    }

    private static void GameUpdate_Prefix()
    {
        try { if (Recording) RecordEvent("GameMain.Update enter"); } catch (Exception) { }
    }

    private static void GameUpdate_Postfix()
    {
        try { if (Recording) RecordEvent("GameMain.Update exit"); } catch (Exception) { }
    }

    private static void ServerUpdate_Prefix()
    {
        try { if (Recording) RecordEvent("Server.Update enter (packet queue drain)"); } catch (Exception) { }
    }

    private static void ServerUpdate_Postfix()
    {
        try { if (Recording) RecordEvent("Server.Update exit"); } catch (Exception) { }
    }

    private static void ClientUpdate_Prefix()
    {
        try { if (Recording) RecordEvent("Client.Update enter (packet queue drain)"); } catch (Exception) { }
    }

    private static void ClientUpdate_Postfix()
    {
        try { if (Recording) RecordEvent("Client.Update exit"); } catch (Exception) { }
    }

    // LogicFrame entry for frame N+1 is structurally after frame N's task walk for every
    // participant: workers that finished frame N block on frameBarrier inside ProcessFrame, and
    // that barrier cannot release until the main thread arrives. So entry to LogicFrame is a
    // candidate safe point, unlike its exit.
    private static int frameStartSamples;
    private static int frameStartWithWorkersBusy;
    private static int frameStartMaxWorkersBusy;

    private static void LogicFrame_Prefix()
    {
        try
        {
            Interlocked.Increment(ref logicFrameCount);
            var busy = Volatile.Read(ref workersInsideTask);
            Interlocked.Increment(ref frameStartSamples);
            if (busy > 0)
            {
                Interlocked.Increment(ref frameStartWithWorkersBusy);
                var previous = Volatile.Read(ref frameStartMaxWorkersBusy);
                while (busy > previous &&
                       Interlocked.CompareExchange(ref frameStartMaxWorkersBusy, busy, previous) != previous)
                {
                    previous = Volatile.Read(ref frameStartMaxWorkersBusy);
                }
            }
            if (Recording) RecordEvent("GameLogic.LogicFrame enter (workersInsideTask=" + busy + ")");
        }
        catch (Exception) { }
    }

    private static void LogicFrame_Postfix()
    {
        try
        {
            // This is the point Nebula's GameLogic_Patch.LogicFrame_Postfix uses today.
            var busy = Volatile.Read(ref workersInsideTask);
            Interlocked.Increment(ref frameEndSamples);
            if (busy > 0)
            {
                Interlocked.Increment(ref frameEndWithWorkersBusy);
                Interlocked.Add(ref frameEndSumWorkersBusy, busy);
                var previous = Volatile.Read(ref frameEndMaxWorkersBusy);
                while (busy > previous &&
                       Interlocked.CompareExchange(ref frameEndMaxWorkersBusy, busy, previous) != previous)
                {
                    previous = Volatile.Read(ref frameEndMaxWorkersBusy);
                }
            }
            if (Recording) RecordEvent("GameLogic.LogicFrame exit (workersInsideTask=" + busy + ")");
        }
        catch (Exception) { }
    }

    private static void ProcessFrame_Prefix(long frameCounter)
    {
        try
        {
            Interlocked.Increment(ref processFrameCount);
            Interlocked.Exchange(ref mainInsideProcessFrame, 1);
            if (Recording) RecordEvent("ThreadManager.ProcessFrame enter");
        }
        catch (Exception) { }
    }

    private static void ProcessFrame_Postfix(long frameCounter)
    {
        try
        {
            var busy = Volatile.Read(ref workersInsideTask);
            Interlocked.Exchange(ref mainInsideProcessFrame, 0);
            if (Recording) RecordEvent("ThreadManager.ProcessFrame exit (workersInsideTask=" + busy + ")");
        }
        catch (Exception) { }
    }

    // ---------------- per-task attribution ----------------

    private static void OnGameLogicFrame_Prefix(int iTask, int threadOrdinal, int threadCount)
    {
        try
        {
            if (threadOrdinal >= 0)
            {
                var now = Interlocked.Increment(ref workersInsideTask);
                var previous = Volatile.Read(ref maxConcurrentWorkersEver);
                while (now > previous &&
                       Interlocked.CompareExchange(ref maxConcurrentWorkersEver, now, previous) != previous)
                {
                    previous = Volatile.Read(ref maxConcurrentWorkersEver);
                }
            }
            lastSerialThreadCount = threadCount;
            var stat = TaskStats.GetOrAdd(iTask, _ => new TaskStat());
            Interlocked.Increment(ref stat.Count);
            if (threadOrdinal < 0) Interlocked.Increment(ref stat.MainThreadCount);
            else Interlocked.Increment(ref stat.WorkerCount);
            stat.ThreadOrdinals.AddOrUpdate(threadOrdinal, 1, (_, value) => value + 1);
            stat.LastThreadCount = threadCount;
        }
        catch (Exception) { }
    }

    private static void OnGameLogicFrame_Postfix(int iTask, int threadOrdinal, int threadCount)
    {
        try
        {
            if (threadOrdinal >= 0) Interlocked.Decrement(ref workersInsideTask);
        }
        catch (Exception) { }
    }

    // ---------------- reporting ----------------

    public static string TaskName(int value)
    {
        try
        {
            if (GameMain.logic != null)
            {
                var manager = GameMain.logic.threadController?.threadManager;
                if (manager != null)
                {
                    for (var i = 0; i < manager.taskCount; i++)
                    {
                        if (manager.GetTaskEnumValue(i) == value) return manager.GetTaskEnumName(i);
                    }
                }
            }
        }
        catch (Exception) { }
        return "task" + value;
    }

    public static void Dump(string directory, string role)
    {
        var path = Path.Combine(directory, role + "-frameprobe.txt");
        var sb = new StringBuilder();
        sb.AppendLine("role=" + role + " tick=" + BaselineRuntime.GameTick());
        sb.AppendLine("logicFrames=" + Interlocked.Read(ref logicFrameCount));
        sb.AppendLine("processFrames=" + Interlocked.Read(ref processFrameCount));
        sb.AppendLine("lastThreadCount=" + lastSerialThreadCount +
                      " (<=1 means the serial path, >=2 means the parallel path)");
        sb.AppendLine();
        sb.AppendLine("--- frame START (LogicFrame entry) ---");
        var startSamples = Volatile.Read(ref frameStartSamples);
        var startBusy = Volatile.Read(ref frameStartWithWorkersBusy);
        sb.AppendLine("samples=" + startSamples);
        sb.AppendLine("withWorkersBusy=" + startBusy +
                      (startSamples > 0 ? " (" + (100.0 * startBusy / startSamples).ToString("F1") + "%)" : ""));
        sb.AppendLine("maxWorkersBusy=" + Volatile.Read(ref frameStartMaxWorkersBusy));
        sb.AppendLine();
        sb.AppendLine("--- frame END (LogicFrame exit / the point Nebula patches today) ---");
        var samples = Volatile.Read(ref frameEndSamples);
        var busy = Volatile.Read(ref frameEndWithWorkersBusy);
        sb.AppendLine("samples=" + samples);
        sb.AppendLine("withWorkersBusy=" + busy +
                      (samples > 0 ? " (" + (100.0 * busy / samples).ToString("F1") + "%)" : ""));
        sb.AppendLine("maxWorkersBusy=" + Volatile.Read(ref frameEndMaxWorkersBusy));
        sb.AppendLine("instrumentCheck.maxConcurrentWorkersEver=" + Volatile.Read(ref maxConcurrentWorkersEver) +
                      "  (must reach the worker count; otherwise the busy counter is broken)");
        sb.AppendLine("sumWorkersBusy=" + Interlocked.Read(ref frameEndSumWorkersBusy));
        sb.AppendLine("Structure: phaseBarrierMask syncs only tasks whose enum value % 10 == 0.");
        sb.AppendLine("The last such task is WarningSystem=4100; StatisticsPostTick=4201, Scenario=4301 and");
        sb.AppendLine("CollectPreferences=4401 run with no barrier after them, so main and workers may");
        sb.AppendLine("legitimately be at different points when LogicFrame returns.");
        sb.AppendLine("VERDICT frame end: " + (busy > 0
            ? "NOT quiescent in this run."
            : "quiescent in this run, but NOT structurally guaranteed (no barrier follows the last task)."));
        sb.AppendLine("VERDICT frame start: " + (startBusy > 0
            ? "NOT quiescent in this run - investigate."
            : "quiescent; consistent with the frameBarrier at ProcessFrame entry."));
        sb.AppendLine();
        sb.AppendLine("--- per-task attribution (value, name, count, main, worker, ordinals) ---");
        var keys = new List<int>(TaskStats.Keys);
        keys.Sort();
        foreach (var key in keys)
        {
            var stat = TaskStats[key];
            var ordinals = new List<int>(stat.ThreadOrdinals.Keys);
            ordinals.Sort();
            var ordinalText = new StringBuilder();
            foreach (var ordinal in ordinals)
            {
                if (ordinalText.Length > 0) ordinalText.Append(',');
                ordinalText.Append(ordinal).Append('x').Append(stat.ThreadOrdinals[ordinal]);
            }
            sb.AppendLine("task=" + key +
                          " name=" + TaskName(key) +
                          " count=" + stat.Count +
                          " main=" + stat.MainThreadCount +
                          " worker=" + stat.WorkerCount +
                          " lastThreadCount=" + stat.LastThreadCount +
                          " ordinals=[" + ordinalText + "]");
        }
        sb.AppendLine();
        sb.AppendLine("--- ordered structural events ---");
        lock (LogGate)
        {
            if (EventLog.Count == 0) sb.AppendLine("(no recording captured; run 'frameprobe' first)");
            foreach (var line in EventLog) sb.AppendLine(line);
        }
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>Count of task values observed, used by the hook tests to assert coverage.</summary>
    public static int ObservedTaskCount => TaskStats.Count;

    public static int FrameEndWithWorkersBusy => Volatile.Read(ref frameEndWithWorkersBusy);

    public static int FrameEndSamples => Volatile.Read(ref frameEndSamples);
}
