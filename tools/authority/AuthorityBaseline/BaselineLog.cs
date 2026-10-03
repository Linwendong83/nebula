using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace AuthorityBaseline;

/// <summary>
/// One observation record. The field set is the A00 minimum: host tick, thread, role, event,
/// ObjectKey/raw slot, hp before/after, repairerCount before/after, owner,
/// command/transaction/revision, reason code.
///
/// Null means "this build has no such value yet" and must stay null rather than being filled
/// with a guess. A00 has no authority epoch/command/transaction layer (A02/A03), so those
/// fields are written as null and are listed as remaining gaps in docs/host-authority/PROGRESS.md.
/// </summary>
internal sealed class BaselineRecord
{
    public long Seq;
    public string RunId = "";
    public string Role = "";
    public bool IsServer;
    public long Tick = -1;
    public int ThreadId;
    public string ThreadKind = "unknown";
    public string Event = "";
    public string Source = "";
    public int PlanetId = -1;
    public string ObjectKey;
    public string RawSlot;
    public int? HpBefore;
    public int? HpAfter;
    public int? HpMax;
    public int? HpRecover;
    public int? HpIncoming;
    public int? RepairerBefore;
    public int? RepairerAfter;
    public string Owner;
    public string Command;
    public string Transaction;
    public string Revision;
    public string Reason;
    public string Note;

    public string ToJson()
    {
        var sb = new StringBuilder(320);
        sb.Append('{');
        Num(sb, "seq", Seq);
        Str(sb, "runId", RunId);
        Str(sb, "role", Role);
        Bool(sb, "isServer", IsServer);
        Num(sb, "tick", Tick);
        Num(sb, "thread", ThreadId);
        Str(sb, "threadKind", ThreadKind);
        Str(sb, "event", Event);
        Str(sb, "source", Source);
        Num(sb, "planetId", PlanetId);
        Str(sb, "objectKey", ObjectKey);
        Str(sb, "rawSlot", RawSlot);
        NullableNum(sb, "hpBefore", HpBefore);
        NullableNum(sb, "hpAfter", HpAfter);
        NullableNum(sb, "hpMax", HpMax);
        NullableNum(sb, "hpRecover", HpRecover);
        NullableNum(sb, "hpIncoming", HpIncoming);
        NullableNum(sb, "repairerBefore", RepairerBefore);
        NullableNum(sb, "repairerAfter", RepairerAfter);
        Str(sb, "owner", Owner);
        Str(sb, "command", Command);
        Str(sb, "transaction", Transaction);
        Str(sb, "revision", Revision);
        Str(sb, "reason", Reason);
        Str(sb, "note", Note);
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendKey(StringBuilder sb, string key)
    {
        if (sb.Length > 1) sb.Append(',');
        sb.Append('"').Append(key).Append("\":");
    }

    private static void Str(StringBuilder sb, string key, string value)
    {
        AppendKey(sb, key);
        if (value == null) { sb.Append("null"); return; }
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    private static void Num(StringBuilder sb, string key, long value)
    {
        AppendKey(sb, key);
        sb.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void NullableNum(StringBuilder sb, string key, int? value)
    {
        AppendKey(sb, key);
        sb.Append(value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null");
    }

    private static void Bool(StringBuilder sb, string key, bool value)
    {
        AppendKey(sb, key);
        sb.Append(value ? "true" : "false");
    }
}

/// <summary>Append-only JSONL sink. Thread safe: hooks run on worker threads and the socket thread.</summary>
internal sealed class BaselineLog : IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter writer;
    private long seq;
    private int mainThreadId = -1;

    public BaselineLog(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");
        FilePath = path;
        writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    public string FilePath { get; }

    public void MarkMainThread()
    {
        mainThreadId = Thread.CurrentThread.ManagedThreadId;
    }

    public string ThreadKind()
    {
        var id = Thread.CurrentThread.ManagedThreadId;
        if (id == mainThreadId) return "main";
        var name = Thread.CurrentThread.Name;
        if (!string.IsNullOrEmpty(name)) return "named:" + name;
        return "worker";
    }

    public long NextSeq() => Interlocked.Increment(ref seq);

    public void Write(BaselineRecord record)
    {
        var line = record.ToJson();
        lock (gate)
        {
            writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            try { writer.Flush(); } catch (Exception) { /* diagnostic sink */ }
            try { writer.Dispose(); } catch (Exception) { /* diagnostic sink */ }
        }
    }

    /// <summary>Convenience for hook sites: fills the ambient fields so each hook only sets its own.</summary>
    public BaselineRecord New(string evt, string source)
    {
        return new BaselineRecord
        {
            Seq = NextSeq(),
            RunId = BaselineRuntime.RunId,
            Role = BaselineRuntime.Role,
            IsServer = BaselineRuntime.IsServer,
            Tick = BaselineRuntime.GameTick(),
            ThreadId = Thread.CurrentThread.ManagedThreadId,
            ThreadKind = ThreadKind(),
            Event = evt,
            Source = source,
            // A00 has no command/transaction layer yet (A02/A03 add them). They stay null on
            // purpose so the evidence shows the gap instead of a fabricated id.
            Command = null,
            Transaction = null
        };
    }

    public void Emit(BaselineRecord record)
    {
        try
        {
            Write(record);
        }
        catch (Exception)
        {
            // A diagnostic sink must never break the game. The failure is surfaced through
            // BaselineRuntime.WriteFailures and reported in the run status file instead.
            Interlocked.Increment(ref BaselineRuntime.WriteFailures);
        }
    }

    public static IEnumerable<string> Header()
    {
        yield return "# AuthorityBaseline JSONL (A00). One record per line; see tools/authority/README.md.";
    }
}
