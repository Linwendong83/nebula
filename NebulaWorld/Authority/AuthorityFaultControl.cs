#region

using NebulaModel.Authority;

#endregion

namespace NebulaWorld.Authority;

/// <summary>
/// The runtime fault-injection surface for the multi-instance matrix (TASKS.md A22, VALIDATION §4).
/// </summary>
/// <remarks>
/// <para>
/// When the process was launched with <c>-nebula-authority-faults=&lt;spec&gt;</c>, the host's
/// replication delivery goes through a <see cref="FaultyAuthorityLink"/> armed with these rules;
/// without the flag the link does not exist and this class is inert. The rules object here is the
/// same instance the link reads, so the driver (or a control verb) can re-arm faults mid-run —
/// pause delivery, arm one message loss, corrupt the next baseline — and every change is
/// deterministic, exactly like the model harness.
/// </remarks>
public static class AuthorityFaultControl
{
    private static FaultInjectionRules rules;

    /// <summary>
    /// The wired rules object, or null when this process has no fault link.
    /// </summary>
    /// <remarks>
    /// Non-null means the process was launched with a fault spec and the host's replication sink
    /// was wrapped in a <see cref="FaultyAuthorityLink"/> that holds this exact object. It stays
    /// non-null for the process lifetime so the driver can re-arm it repeatedly; "off" is the
    /// object's values being all-zero, not the reference disappearing. Nulling it here would
    /// detach the live link and every later <c>fault</c> verb would arm an object nobody reads
    /// (A22 matrix bug: after the first clean case, all fault cases silently ran clean).
    /// </remarks>
    public static FaultInjectionRules Rules => rules;

    /// <summary>True when this process has a fault link wired (it may still be running clean).</summary>
    public static bool IsEnabled => rules != null;

    /// <summary>True when the wired rules have any active fault. False when clean or unwired.</summary>
    public static bool IsArmed => rules != null && !IsAllZero(rules);

    /// <summary>Arms (non-null) or detaches (null) the fault link for this process.</summary>
    public static void Configure(FaultInjectionRules newRules) => rules = newRules;

    /// <summary>
    /// Re-arms the fault rules mid-run from a launch-spec string (A22 matrix driver verb).
    /// </summary>
    /// <remarks>
    /// Uses the same refuse-the-whole-spec semantics as
    /// <see cref="NebulaModel.Authority.AuthorityLaunchOptions.TryParseFaultSpec"/>: an unknown
    /// key, a bad value or an empty spec leaves the currently armed rules untouched and reports
    /// the reason, so a typo can never turn a fault scenario into a silently clean run. The parsed
    /// values are copied into the wired object the link reads (mutated in place so the link stays
    /// attached); if this process has no link, the parsed object becomes the wired rules for the
    /// next world registration.
    /// </remarks>
    /// <returns>True when the spec parsed and took effect; false with <paramref name="error"/> set.</returns>
    public static bool TryApplySpec(string spec, out string error)
    {
        if (!NebulaModel.Authority.AuthorityLaunchOptions.TryParseFaultSpec(spec, out var parsed, out error))
        {
            return false;
        }
        var live = rules;
        if (live != null)
        {
            CopyInto(parsed, live);
        }
        else
        {
            rules = parsed;
        }
        error = null;
        return true;
    }

    /// <summary>
    /// Returns the wired link to a clean state by zeroing its rules in place.
    /// </summary>
    /// <remarks>
    /// Deliberately keeps the reference: zeroing detaches nothing, so the next <see cref="TryApplySpec"/>
    /// still mutates the object the live link reads. Use <see cref="Configure"/> with null to detach.
    /// </remarks>
    public static void Clear()
    {
        var live = rules;
        if (live != null)
        {
            CopyInto(new FaultInjectionRules(), live);
        }
    }

    /// <summary>One-line human-readable summary of the wired rules, or "off" when none is wired.</summary>
    public static string Describe()
    {
        var live = rules;
        if (live == null) return "off";
        return "delay=" + live.DelayPumps +
               " copies=" + live.ExtraCopies +
               " reorder=" + live.ReorderDepth +
               " paused=" + (live.Paused ? 1 : 0) +
               " droplifecycle=" + (live.DropNextLifecycle ? 1 : 0) +
               " dropstate=" + (live.DropNextWorldState ? 1 : 0) +
               " dropdigest=" + (live.DropNextDigest ? 1 : 0) +
               " dropchunk=" + live.DropChunkIndex +
               " dupchunk=" + live.DuplicateChunkIndex +
               " truncate=" + (live.TruncateChunk ? 1 : 0) +
               " corruptchunk=" + (live.CorruptChunkBytes ? 1 : 0) +
               " corrupthash=" + (live.CorruptCommitHash ? 1 : 0);
    }

    private static bool IsAllZero(FaultInjectionRules r) =>
        r.DelayPumps == 0 && r.ExtraCopies == 0 && r.ReorderDepth == 0 && !r.Paused &&
        !r.DropNextLifecycle && !r.DropNextWorldState && !r.DropNextDigest &&
        !r.DelayDuplicateNextPacket && r.DelayedDuplicatePumps == 0 &&
        r.DropChunkIndex < 0 && r.DuplicateChunkIndex < 0 &&
        !r.TruncateChunk && !r.CorruptChunkBytes && !r.CorruptCommitHash && r.DropOnlyScope == null;

    private static void CopyInto(FaultInjectionRules from, FaultInjectionRules into)
    {
        into.DelayPumps = from.DelayPumps;
        into.ExtraCopies = from.ExtraCopies;
        into.ReorderDepth = from.ReorderDepth;
        into.Paused = from.Paused;
        into.DropOnlyScope = from.DropOnlyScope;
        into.DropNextLifecycle = from.DropNextLifecycle;
        into.DropNextWorldState = from.DropNextWorldState;
        into.DropNextDigest = from.DropNextDigest;
        into.DelayDuplicateNextPacket = from.DelayDuplicateNextPacket;
        into.DelayedDuplicatePumps = from.DelayedDuplicatePumps;
        into.DropChunkIndex = from.DropChunkIndex;
        into.DuplicateChunkIndex = from.DuplicateChunkIndex;
        into.TruncateChunk = from.TruncateChunk;
        into.CorruptChunkBytes = from.CorruptChunkBytes;
        into.CorruptCommitHash = from.CorruptCommitHash;
    }
}
