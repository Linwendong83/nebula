#region

#endregion

namespace NebulaWorld.Authority;

/// <summary>
/// Authority-mode policy for the legacy damage/death chain (TASKS.md A19, pure decision).
/// </summary>
/// <remarks>
/// <para>
/// The old room runs damage as a peer-side fact relay: a client computes a hit, hands the final
/// number to <c>CombatStatDamagePacket</c>, the host applies it and rebroadcasts, a dying enemy is
/// stood up at 1 HP and asked about (<c>CombatStat.HandleZeroHp</c>), buildings report their own
/// full-heal (<c>CombatStatFullHpPacket</c>), and the host replays enemy/entity kills as
/// <c>DFGKillEnemyPacket</c>/<c>KillEntityRequest</c> for the clients to run
/// <c>KillEnemyFinally</c>/<c>KillEntityFinally</c> themselves — each replay a second drop/statistics
/// source. In a host authority world none of those is a rule: intents travel as authority commands
/// (A11), damage and death are host facts carried by the replica (A06/A08/A12/A13), and the client
/// displays them. The policy answers which legacy entry must be refused, suppressed or left alone.
/// Legacy rooms keep every path byte for byte.
/// </para>
/// <para>
/// Pure like <see cref="HostCombatPolicy"/>: call sites check the live mode bit and ask here what to
/// do, so tests drive the decisions without a game process.
/// </para>
/// </remarks>
public static class HostDeathPolicy
{
    /// <summary>True when a legacy CombatStatDamagePacket must be refused without touching the world.</summary>
    public static bool ShouldRefuseLegacyDamageFact(bool isHostAuthority) => isHostAuthority;

    /// <summary>True when a legacy CombatStatFullHpPacket must be refused (absolute HP replaces it).</summary>
    public static bool ShouldRefuseLegacyFullHpFact(bool isHostAuthority) => isHostAuthority;

    /// <summary>True when a legacy kill replay packet (DFGKillEnemy / KillEntityRequest) must be refused.</summary>
    /// <remarks>
    /// The replay would run vanilla <c>KillEnemyFinally</c>/<c>KillEntityFinally</c> on the receiving
    /// peer — a second drop/statistics/structure-removal source next to the host's one death
    /// transaction. In the new mode the replica lifecycle carries the removal.
    /// </remarks>
    public static bool ShouldRefuseLegacyKillReplay(bool isHostAuthority) => isHostAuthority;

    /// <summary>True when the host must stop broadcasting legacy death/damage facts to peers.</summary>
    /// <remarks>
    /// The host's own vanilla rule still runs; only the legacy relay is suppressed, because the
    /// replica is the delivery path in the new mode and a broadcast would make the old packet family
    /// live again behind the mode's back.
    /// </remarks>
    public static bool MustSuppressLegacyDeathBroadcast(bool isHostAuthority) => isHostAuthority;

    /// <summary>
    /// True when the client's "stand the dying enemy at 1 HP and query the host" path must not run.
    /// </summary>
    /// <remarks>
    /// Enforced by the A05 guard (<c>AllowHostRule</c> refuses <c>CombatStat.HandleZeroHp</c> on a
    /// client outside a replica apply), which is why no patch branch consults this — the decision is
    /// recorded so the A19 card's "禁用客户端1血查询" is one auditable place.
    /// </remarks>
    public static bool MustNotStageOneHpQuery(bool isHostAuthority) => isHostAuthority;

    /// <summary>True when the client's local regen/death tick must not run (host regen is the truth).</summary>
    /// <remarks>
    /// Enforced by the A05 guard on <c>CombatStat.TickSkillLogic</c>. Vanilla regen still runs on the
    /// host and reaches clients as absolute HP; HP never rises on a client by itself.
    /// </remarks>
    public static bool MustNotSelfHealOnClient(bool isHostAuthority) => isHostAuthority;

    /// <summary>Why a refusal happened, for logs. Callers branch on the bool, never on this text.</summary>
    public static string RefusalReason(string path) =>
        "legacy " + path + " asserts a shared death/damage fact; host death transactions own it in host authority mode (A19)";

    /// <summary>
    /// The combat fields the new mode treats as display-only (A19's "只展示影响的字段" policy).
    /// </summary>
    /// <remarks>
    /// These never participate in a rule — not hit tests, not repair ranges, not resources — and the
    /// replica may carry them only for rendering. Everything else a damage/death path touches
    /// (hp, hpMax, hpRecover, hpIncoming, shield, combatStatId/constructStatId references, drops,
    /// kill counts) is a protected host fact. A game update that adds a field here must classify it
    /// explicitly; an unlisted field defaults to protected.
    /// </remarks>
    public static bool IsDisplayOnlyCombatField(string fieldName)
    {
        switch (fieldName)
        {
            // Blood-bar anchoring on the combat stat.
            case "localPos":
            case "size":
            // Per-impact muzzle/impact bookkeeping the legacy MechaShoot path skipped syncing.
            case "lastImpact":
            // GPU renderer and collider handles are always local (DESIGN 4.1).
            case "modelId":
            case "colliderId":
                return true;
            default:
                return false;
        }
    }
}
