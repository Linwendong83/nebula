#region

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// The frozen G1 contract: the numbers a released authority mode must not change silently
/// (TASKS.md A25's "冻结 schema/必要方法指纹/协议版本").
/// </summary>
/// <remarks>
/// <para>
/// These are not documentation comments — every one of them is checked against the live code by
/// <see cref="VerifyLive"/>, and <c>AuthorityReleaseGateTest</c> fails when they drift. That is the
/// point: "frozen" has to mean "a change breaks a test", or the freeze is a paragraph someone can
/// edit. A deliberate change is made here, in one place, with the release notes updated.
/// </para>
/// <para>
/// The required-hook fingerprint is a hash over the sorted labels of
/// <see cref="AuthorityRuleGuard.RequiredPolicy"/>. Every hook in that set is one the fail-closed
/// gate refuses to load without (DESIGN 1.8), so renaming one, or dropping one from the required
/// set, changes the fingerprint and breaks the release test — which is exactly the kind of change
/// that has to be a decision rather than an accident.
/// </para>
/// </remarks>
public static class AuthorityReleaseContract
{
    /// <summary>Transport protocol version the released mode speaks (never downgraded at runtime).</summary>
    public const int ProtocolVersion = 3;

    /// <summary>Transport version that still means "peers decide the shared world themselves".</summary>
    public const int LegacyProtocolVersion = 2;

    /// <summary>Authority DTO schema of this release.</summary>
    public const byte SchemaVersion = 1;

    /// <summary>Capabilities the host must advertise; a client may not negotiate one for itself.</summary>
    public const uint Capabilities = (uint)AuthorityCapability.All;

    /// <summary>Game build the frozen hook signatures were verified against.</summary>
    public const string GameVersion = "0.10.35.29104";

    /// <summary>SHA-256 of that build's <c>Assembly-CSharp.dll</c> (uppercase hex).</summary>
    public const string GameDllSha256 =
        "6C122E5443E6843979B4064050DFCB5E0D75577A0B64F6AE4111290238B33C12";

    /// <summary>Number of hooks the mode refuses to load without.</summary>
    /// <remarks>
    /// Fifteen from the A01 inventory. The three mod-side remote-drone pool entries the mod added
    /// itself (<c>DroneManager.EjectMechaDroneFromOtherPlayer</c>, <c>DroneManager.UpdateDrones</c>,
    /// <c>DroneManager.CancelRemoteBuildTarget</c>) were removed with the pool: the legacy
    /// launch-claim conversation is deleted, so there is no retired path left to verify.
    /// </remarks>
    public const int RequiredHookCount = 15;

    /// <summary>FNV-1a 64 over the sorted required-hook labels.</summary>
    public const ulong RequiredHookFingerprint = 0x40F4CB8FC785CD44UL;

    /// <summary>
    /// The size and count ceilings a released mode is validated against (DESIGN 5.2).
    /// </summary>
    /// <remarks>
    /// Written as literal numbers, deliberately not as references to <see cref="AuthorityLimits"/>:
    /// a list that reads the live constants would change with them and could never detect a drift.
    /// <see cref="VerifyLive"/> compares each literal against the live constant, so a bump is a
    /// release decision with a failing test attached rather than a silent change to what the A23
    /// budgets and the A25 validation measured.
    /// </remarks>
    public static readonly IReadOnlyList<(string Name, long Value)> FrozenLimits = new[]
    {
        ("ChunkMaxBytes", 64L * 1024),
        ("CommandPayloadMaxBytes", 16L * 1024),
        ("ScopeSnapshotMaxBytes", 64L * 1024 * 1024),
        ("ConnectionPendingMaxBytes", 128L * 1024 * 1024),
        ("CommandQueueMax", 1024L),
        ("ScopesPerSubscriberMax", 16L),
        ("PendingDigestsPerScopeMax", 4L),
        ("StateRecordMaxBytes", 256L),
        ("StateRecordCountMax", 4096L),
        ("LifecycleRecordCountMax", 4096L),
    };

    /// <summary>
    /// FNV-1a 64 over a label set, sorted so the order of discovery cannot change the value.
    /// </summary>
    /// <remarks>
    /// The same function is used by the test that pins the constant and by
    /// <see cref="VerifyLive"/>, so "the fingerprint agrees" cannot be true because two
    /// implementations agree with each other rather than with the table.
    /// </remarks>
    public static ulong FingerprintOf(IEnumerable<string> labels)
    {
        if (labels == null) return 0;
        var sorted = new List<string>(labels);
        sorted.Sort(StringComparer.Ordinal);
        var hash = 0xCBF29CE484222325UL;
        foreach (var label in sorted)
        {
            if (label == null) continue;
            foreach (var b in Encoding.UTF8.GetBytes(label))
            {
                hash ^= b;
                hash *= 0x100000001B3UL;
            }
            // A separator keeps {ab, c} and {a, bc} from hashing alike.
            hash ^= 0x1F;
            hash *= 0x100000001B3UL;
        }
        return hash;
    }

    /// <summary>
    /// Compares every frozen value against the live code, returning one line per drift.
    /// </summary>
    /// <remarks>
    /// An empty result is the only "the contract holds". Nothing here throws: a release checklist
    /// wants every drift listed at once, not the first one to be found.
    /// </remarks>
    public static List<string> VerifyLive()
    {
        var drift = new List<string>();
        Compare(drift, "protocol version", ProtocolVersion, NebulaModel.Networking.SessionProtocol.Version);
        Compare(drift, "legacy protocol version", LegacyProtocolVersion,
            NebulaModel.Networking.SessionProtocol.LegacyVersion);
        Compare(drift, "schema version", SchemaVersion, (byte)AuthoritySchema.V1);
        Compare(drift, "capabilities", Capabilities, (uint)AuthorityCapability.All);

        var required = new List<string>();
        foreach (var entry in AuthorityRuleGuard.RequiredPolicy)
        {
            required.Add(entry.Label);
        }
        Compare(drift, "required hook count", RequiredHookCount, required.Count);
        Compare(drift, "required hook fingerprint", RequiredHookFingerprint, FingerprintOf(required));

        foreach (var (name, value) in FrozenLimits)
        {
            if (!TryLiveLimit(name, out var live))
            {
                drift.Add(name + ": the frozen name matches no live limit");
                continue;
            }
            Compare(drift, "limit " + name, value, live);
        }
        return drift;
    }

    /// <summary>One line per frozen fact, for the release report.</summary>
    public static List<string> Describe()
    {
        var lines = new List<string>
        {
            "contract protocolVersion=" + ProtocolVersion + " legacyVersion=" + LegacyProtocolVersion +
            " schema=" + SchemaVersion + " capabilities=0x" + Capabilities.ToString("X", CultureInfo.InvariantCulture),
            "contract game=" + GameVersion + " dllSha256=" + GameDllSha256,
            "contract requiredHooks=" + RequiredHookCount + " fingerprint=0x" +
            RequiredHookFingerprint.ToString("X16", CultureInfo.InvariantCulture)
        };
        foreach (var (name, value) in FrozenLimits)
        {
            lines.Add("contract limit " + name + "=" + value.ToString(CultureInfo.InvariantCulture));
        }
        return lines;
    }

    private static bool TryLiveLimit(string name, out long value)
    {
        switch (name)
        {
            case "ChunkMaxBytes": value = AuthorityLimits.ChunkMaxBytes; return true;
            case "CommandPayloadMaxBytes": value = AuthorityLimits.CommandPayloadMaxBytes; return true;
            case "ScopeSnapshotMaxBytes": value = AuthorityLimits.ScopeSnapshotMaxBytes; return true;
            case "ConnectionPendingMaxBytes": value = AuthorityLimits.ConnectionPendingMaxBytes; return true;
            case "CommandQueueMax": value = AuthorityLimits.CommandQueueMax; return true;
            case "ScopesPerSubscriberMax": value = AuthorityLimits.ScopesPerSubscriberMax; return true;
            case "PendingDigestsPerScopeMax": value = AuthorityLimits.PendingDigestsPerScopeMax; return true;
            case "StateRecordMaxBytes": value = AuthorityLimits.StateRecordMaxBytes; return true;
            case "StateRecordCountMax": value = AuthorityLimits.StateRecordCountMax; return true;
            case "LifecycleRecordCountMax": value = AuthorityLimits.LifecycleRecordCountMax; return true;
            default:
                value = 0;
                return false;
        }
    }

    private static void Compare<T>(List<string> drift, string what, T frozen, T live)
    {
        if (!EqualityComparer<T>.Default.Equals(frozen, live))
        {
            drift.Add(what + ": contract=" + frozen + " live=" + live);
        }
    }
}
