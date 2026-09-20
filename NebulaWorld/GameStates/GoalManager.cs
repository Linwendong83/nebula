using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NebulaModel.Packets.GameStates;

namespace NebulaWorld.GameStates;

public sealed class GoalManager : IDisposable
{
    private const int GoalLevelKey = -1708469030;
    public static void RestoreLevel(GameData data)
    {
        if (data.gameDesc.goalLevel == EGoalLevel.None)
        {
            data.history.featureValues.TryGetValue(GoalLevelKey, out var level);
            data.gameDesc.goalLevel = level >= (int)EGoalLevel.Off && level <= (int)EGoalLevel.Full ? (EGoalLevel)level : EGoalLevel.Full;
        }
        if (Multiplayer.Session.IsServer) data.history.featureValues[GoalLevelKey] = (int)data.gameDesc.goalLevel;
    }
    public static readonly HashSet<string> PersonalDeterminators = new(StringComparer.Ordinal)
    {
        "GD_CameraRotate", "GD_ControlorMove", "GD_MechaFuelUsed", "GD_ReplenishMechaAmmo",
        "GD_CheckGroundSquadron", "GD_CheckSpaceSquadron", "GD_CreateBlueprint", "GD_UseBlueprint",
        "GD_SailOrbitCheck", "GD_StarTravelledCheck", "GD_SpaceCapsuleRecycled", "GD_MechaAndFactoryItemProduction"
    };
    private readonly Dictionary<int, GoalData> personalData = new();
    private readonly Dictionary<int, GoalDeterminator> collectors = new();
    private readonly Dictionary<int, long> sentProgress = new();
    private readonly Dictionary<(ushort Player, int Goal), (long Sequence, long Value)> observed = new();
    private byte[] lastSnapshot;
    private long version;
    private long observationSequence;
    private long lastTick = -1;
    private bool snapshotReady;
    public bool CollectingPersonal { get; private set; }
    public bool Applying { get; private set; }
    public bool Dirty { get; set; }

    public GoalData GetPersonalGoal(int id)
    {
        if (!personalData.TryGetValue(id, out var data))
            personalData[id] = data = new GoalData(id) { _stage = EGoalStage.Enabled, isPatched = true };
        return data;
    }

    public byte[] Export()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1);
        writer.Write(GameMain.data.gameDesc.isSandboxMode);
        writer.Write(GameMain.sandboxToolsEnabled);
        writer.Write((int)GameMain.data.gameDesc.goalLevel);
        var system = GameMain.data.goalSystem;
        writer.Write(system.goalDatas.Count);
        foreach (var entry in system.goalDatas.OrderBy(x => x.Key))
        {
            var data = entry.Value;
            writer.Write(entry.Key); writer.Write((int)data.stage);
            writer.Write(data.currentValue); writer.Write(data.targetValue);
            writer.Write(data.isManualIgnore); writer.Write(data.isPatched); writer.Write(data.currentIgnoreLevel);
        }
        writer.Write(system.queueCursor);
        for (var i = 0; i < system.queueCursor; i++) writer.Write(system.goalQueue[i]);
        return stream.ToArray();
    }

    public void Apply(byte[] bytes)
    {
        Applying = true;
        try
        {
            using var stream = new MemoryStream(bytes, false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != 1) throw new InvalidDataException("Unknown goal snapshot version");
            GameMain.data.gameDesc.isSandboxMode = reader.ReadBoolean();
            GameMain.sandboxToolsEnabled = reader.ReadBoolean();
            var level = (EGoalLevel)reader.ReadInt32();
            if (level < EGoalLevel.Off || level > EGoalLevel.Full) level = EGoalLevel.Full;
            var levelChanged = GameMain.data.gameDesc.goalLevel != level;
            GameMain.data.gameDesc.goalLevel = level;
            var count = reader.ReadInt32();
            if (count < 0 || count > LDB.goals.Length) throw new InvalidDataException("Invalid goal count");
            var system = GameMain.data.goalSystem;
            for (var i = 0; i < count; i++)
            {
                var id = reader.ReadInt32(); var stage = (EGoalStage)reader.ReadInt32();
                var current = reader.ReadInt64(); var target = reader.ReadInt64();
                var ignore = reader.ReadBoolean(); var patched = reader.ReadBoolean(); var ignoreLevel = reader.ReadInt32();
                if (!system.goalDatas.TryGetValue(id, out var data)) throw new InvalidDataException("Unknown goal prototype");
                var changed = data.stage != stage;
                data._stage = stage; data.currentValue = current; data.targetValue = target;
                data.isManualIgnore = ignore; data.isPatched = patched; data.currentIgnoreLevel = ignoreLevel;
                // displayingState belongs to each UI, not to the host's renderer.
                if (changed) GameMain.gameScenario?.goalLogic?.NotifyOnGoalStageChanged(id, (int)stage);
            }
            var queueCount = reader.ReadInt32();
            if (queueCount < 0 || queueCount > system.goalQueue.Length) throw new InvalidDataException("Invalid goal queue");
            Array.Clear(system.goalQueue, 0, system.goalQueue.Length);
            system.queueCursor = queueCount;
            for (var i = 0; i < queueCount; i++) system.goalQueue[i] = reader.ReadInt32();
            if (levelChanged)
            {
                GameMain.gameScenario?.goalLogic?.NotifyOnGoalLevelChanged();
                UIRoot.instance?.uiGame?.goalPanel?.Reset();
            }
            snapshotReady = true;
        }
        finally { Applying = false; }
    }

    public void Receive(GoalSnapshotPacket packet)
    {
        if (packet.Version <= version) return;
        Apply(packet.Data);
        version = packet.Version;
    }

    public void Command(GoalCommandPacket packet)
    {
        if (Multiplayer.Session.IsClient) { Multiplayer.Session.Network.SendPacket(packet); return; }
        if (packet.ChangeLevel)
        {
            if (packet.Value < (int)EGoalLevel.Off || packet.Value > (int)EGoalLevel.Full) return;
            GameMain.data.gameDesc.goalLevel = (EGoalLevel)packet.Value;
            GameMain.history.featureValues[GoalLevelKey] = packet.Value;
            GameMain.gameScenario.goalLogic.NotifyOnGoalLevelChanged();
            UIRoot.instance?.uiGame?.goalPanel?.Reset();
        }
        else if (GameMain.data.goalSystem.goalDatas.TryGetValue(packet.Value, out var goal))
        {
            if (!goal.isIgnoredOrCompleted) goal.stage = EGoalStage.Ignored;
            goal.isManualIgnore = true;
        }
        Dirty = true;
    }

    public void GameTick()
    {
        if (!Multiplayer.Session.IsGameLoaded || GameMain.data?.goalSystem == null) return;
        var tick = GameMain.gameTick;
        if (tick == lastTick) return;
        lastTick = tick;
        if (Multiplayer.Session.IsServer)
        {
            if (!Dirty && tick % 30 != 0) return;
            Dirty = false;
            var bytes = Export();
            if (lastSnapshot != null && bytes.SequenceEqual(lastSnapshot)) return;
            lastSnapshot = bytes;
            Multiplayer.Session.Server.SendPacket(new GoalSnapshotPacket { Version = ++version, Data = bytes });
            return;
        }
        if (!snapshotReady || !GameMain.gameScenario.goalLogic.isInit) return;
        CollectingPersonal = true;
        try
        {
            foreach (var goal in GameMain.data.goalSystem.goalDatas.Values)
            {
                var proto = LDB.goals.Select(goal.protoId);
                if (proto == null || !PersonalDeterminators.Contains(proto.DeterminatorName)) continue;
                if (goal.isIgnoredOrCompleted)
                {
                    if (collectors.TryGetValue(goal.protoId, out var old)) { old.Free(); collectors.Remove(goal.protoId); }
                    continue;
                }
                if (!collectors.TryGetValue(goal.protoId, out var collector))
                {
                    var type = typeof(GoalDeterminator).Assembly.GetType(proto.DeterminatorName);
                    collector = (GoalDeterminator)Activator.CreateInstance(type);
                    var parameters = proto.DeterminatorParams;
                    if (proto.DeterminatorName == "GD_MechaAndFactoryItemProduction")
                    {
                        if (parameters == null || parameters.Length < 3 || parameters[2] == 2) continue; // Factory-only.
                        parameters = (long[])parameters.Clone();
                        parameters[2] = 1; // Only this player's forge; factory production remains server-owned.
                        if (parameters.Length > 3) parameters[3] = 0;
                    }
                    collector.Init(GameMain.data, goal.protoId, parameters);
                    collectors.Add(goal.protoId, collector);
                }
                if (collector.isInitedAndNotFinished) collector.GameTick(tick);
                var local = collector.goalData;
                if (local == null || local.currentValue <= 0) continue;
                sentProgress.TryGetValue(goal.protoId, out var last);
                var progress = local.currentValue;
                if (progress <= last && local.stage != EGoalStage.Completed) continue;
                if (tick % 30 != 0 && local.stage != EGoalStage.Completed) continue;
                if (progress == last) continue;
                sentProgress[goal.protoId] = progress;
                Multiplayer.Session.Network.SendPacket(new GoalObservationPacket
                {
                    GoalId = goal.protoId,
                    Sequence = ++observationSequence,
                    Value = progress,
                    Target = local.targetValue,
                    Complete = local.stage == EGoalStage.Completed
                });
            }
        }
        finally { CollectingPersonal = false; }
    }

    public void Observe(ushort playerId, GoalObservationPacket packet)
    {
        var proto = LDB.goals.Select(packet.GoalId);
        if (proto == null || !PersonalDeterminators.Contains(proto.DeterminatorName) || packet.Sequence <= 0 ||
            packet.Value < 0 || packet.Target <= 0) return;
        var goal = GameMain.data.goalSystem.GetGoalDataById(packet.GoalId);
        if (goal == null || goal.isIgnoredOrCompleted) return;
        var key = (playerId, packet.GoalId);
        observed.TryGetValue(key, out var previous);
        if (packet.Sequence <= previous.Sequence || packet.Value < previous.Value) return;
        var target = goal.targetValue;
        if (target <= 0 || packet.Target != target || packet.Value > target) return;
        observed[key] = (packet.Sequence, packet.Value);
        var progress = proto.DeterminatorName == "GD_MechaAndFactoryItemProduction" ?
            goal.currentValue + packet.Value - previous.Value : Math.Max(goal.currentValue, packet.Value);
        goal.SetProgress(progress, target);
        if (goal.currentValue >= target && (packet.Complete || proto.DeterminatorName == "GD_MechaAndFactoryItemProduction"))
            goal.stage = EGoalStage.Completed;
        Dirty = true;
    }

    public void RemovePlayer(ushort id)
    {
        foreach (var key in observed.Keys.Where(x => x.Player == id).ToArray()) observed.Remove(key);
    }

    public void Dispose()
    {
        foreach (var collector in collectors.Values) collector.Free();
        collectors.Clear(); personalData.Clear(); observed.Clear(); sentProgress.Clear();
    }
}
