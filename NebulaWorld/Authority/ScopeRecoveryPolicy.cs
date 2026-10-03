#region

#endregion

namespace NebulaWorld.Authority;

/// <summary>
/// Authority-mode policy for the legacy enemy state repair channel (TASKS.md A20, pure decision).
/// </summary>
/// <remarks>
/// <para>
/// The legacy room repairs a diverged enemy by standing the dying one at 1 HP and querying the host
/// (<c>CombatStat.HandleZeroHp</c> → <c>CombatEnemyStateRequestPacket</c>), then re-importing a
/// reflection snapshot; three failed repairs per enemy trip an automatic fast reconnect
/// (<c>EnemyManager.State.ReportSnapshotFailure</c>). In a host authority world none of that is a
/// rule: HP and death reach the client as absolute replica facts, a broken scope recovers through a
/// subscription resync (A20), and a reconnect is a user decision — "自动断线重连仅可作为用户明确
/// 选择的最后恢复方式，不能掩盖一致性失败" (DESIGN 9.3). The policy answers which legacy entry must
/// be refused or stripped of its automatic recovery. Legacy rooms keep every path byte for byte.
/// </para>
/// <para>
/// Pure like <see cref="HostDeathPolicy"/>: call sites check the live mode bit and ask here what to
/// do, so tests drive the decisions without a game process.
/// </para>
/// </remarks>
public static class ScopeRecoveryPolicy
{
    /// <summary>True when the client must not stage the 1-HP state query for an enemy.</summary>
    /// <remarks>
    /// This is the production call site A19 deferred: the A05 guard already refuses
    /// <c>HandleZeroHp</c> on a client outside a replica apply, and this decision records the
    /// query-loop retirement in one auditable place alongside the loop's other entries.
    /// </remarks>
    public static bool MustNotQueryEnemyState(bool isHostAuthority) => isHostAuthority;

    /// <summary>True when a snapshot repair failure must not trigger the automatic fast reconnect.</summary>
    /// <remarks>
    /// The explainable error and the failure counting stay; the recovery is an explicit scope
    /// resync (A20) or a user action, never an automatic reconnect that would hide a consistency
    /// failure behind a fresh load.
    /// </remarks>
    public static bool MustNotAutoReconnectOnSnapshotFailure(bool isHostAuthority) => isHostAuthority;

    /// <summary>True when the host must refuse to answer the legacy enemy state query.</summary>
    /// <remarks>
    /// In the new mode the query family would hand a client a repaired legacy snapshot next to its
    /// replica mirror — two authorities for one enemy. The replica and its resync path are the only
    /// repair channel.
    /// </remarks>
    public static bool ShouldRefuseLegacyEnemyStateRequest(bool isHostAuthority) => isHostAuthority;

    /// <summary>Why a refusal happened, for logs. Callers branch on the bool, never on this text.</summary>
    public static string RefusalReason(string path) =>
        "legacy " + path + " repairs a diverged enemy outside the replica; scope resync owns recovery in host authority mode (A20)";
}
