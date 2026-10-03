#region

using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaModel.Packets.Authority;

#endregion

namespace NebulaWorld.Authority;

/// <summary>
/// Host-side publisher turning decided combat skills into visual events (TASKS.md A14).
/// </summary>
/// <remarks>
/// <para>
/// The executor (A11) records one <see cref="PendingVanillaSkill"/> per applied trigger
/// pull, inside the same frame work that spent the cost. This emitter drains those records
/// into one <see cref="EffectState"/> each, which is what keeps "一次攻击只创建一次权威攻击
/// 实例" structural: a shot that spent once publishes once, and a rejected shot publishes
/// nothing. Stop/Respawn records carry no skill and emit nothing.
/// </para>
/// <para>
/// The event carries no damage, HP, kill or loot number — those settle in A19. It carries
/// the ledger <see cref="EffectState.TransactionId"/> so the later damage groups with the
/// visible shot, and the client <see cref="PendingVanillaSkill.Cause"/> so the arrival
/// merges with the muzzle the client predicted instead of stacking a second one. Vanilla
/// skill-pool generation (projectiles/beams in the host pool, DESIGN 7.2) still goes
/// through the pending-skill queue the executor owns; this emitter is the EffectBatch half
/// of that queue, and the network delivery half belongs to A20.
/// </para>
/// <para>
/// Frame-thread only, like the queue it drains. Bomb style currently carries no proto id:
/// <see cref="PendingVanillaSkill"/> holds the ammo item id but not the bomb proto, so a
/// bomb effect publishes style 0 until that record is extended (see blockers).
/// </para>
/// </remarks>
public sealed class HostEffectEmitter
{
    private readonly Queue<EffectState> pending = new();
    private long nextEffectId;

    private long emittedTotal;
    private long skippedTotal;
    private long takenTotal;

    /// <summary>Events published since creation, for diagnostics.</summary>
    public long EmittedTotal => System.Threading.Interlocked.Read(ref emittedTotal);

    /// <summary>Skill records that carry no visual event (stop/respawn), for diagnostics.</summary>
    public long SkippedTotal => System.Threading.Interlocked.Read(ref skippedTotal);

    /// <summary>Events taken by the delivery sink since creation, for diagnostics.</summary>
    public long TakenTotal => System.Threading.Interlocked.Read(ref takenTotal);

    /// <summary>Events waiting for the delivery sink (A20).</summary>
    public int PendingCount
    {
        get
        {
            lock (pending)
            {
                return pending.Count;
            }
        }
    }

    /// <summary>
    /// Publishes one visual event for a decided skill, or skips records with no event.
    /// </summary>
    /// <returns>False when the record carries no visual event; nothing is queued then.</returns>
    public bool TryEmit(in PendingVanillaSkill skill, out EffectState effect)
    {
        effect = default;
        if (!TryMapKind(skill, out var kind))
        {
            System.Threading.Interlocked.Increment(ref skippedTotal);
            return false;
        }
        if (skill.HostTick < 0 || skill.TransactionId <= 0)
        {
            System.Threading.Interlocked.Increment(ref skippedTotal);
            return false;
        }
        var id = ++nextEffectId;
        var style = MapStyle(skill, kind);
        var caster = default(ObjectKey);
        var target = skill.HasTarget ? skill.Target : default;
        var causeConnection = skill.Cause.IsValid ? skill.Cause.Connection.Value : 0;
        var causeSequence = skill.Cause.IsValid ? skill.Cause.Sequence : 0;
        effect = new EffectState(id, kind, style, skill.TransactionId, skill.HostTick,
            AuthorityEffectDefaults.LifeFor(kind), causeConnection, causeSequence, caster, target);
        lock (pending)
        {
            pending.Enqueue(effect);
        }
        System.Threading.Interlocked.Increment(ref emittedTotal);
        return true;
    }

    /// <summary>Takes one queued event for the delivery sink. Null queue when empty.</summary>
    public bool TryTake(out EffectState effect)
    {
        lock (pending)
        {
            if (pending.Count == 0)
            {
                effect = default;
                return false;
            }
            effect = pending.Dequeue();
        }
        System.Threading.Interlocked.Increment(ref takenTotal);
        return true;
    }

    public void Clear()
    {
        lock (pending)
        {
            pending.Clear();
        }
        nextEffectId = 0;
    }

    private static bool TryMapKind(in PendingVanillaSkill skill, out AuthorityEffectKind kind)
    {
        kind = AuthorityEffectKind.Unknown;
        switch (skill.Action)
        {
            case PlayerCombatAction.PrimaryFire:
                kind = AuthorityEffectKind.MechaProjectile;
                return true;
            case PlayerCombatAction.LaserFire:
                kind = AuthorityEffectKind.MechaBeam;
                return true;
            case PlayerCombatAction.BombDrop:
                kind = AuthorityEffectKind.BombFall;
                return true;
            case PlayerCombatAction.ShieldBurst:
                kind = AuthorityEffectKind.ShieldBurst;
                return true;
            case PlayerCombatAction.StartContinuous:
                kind = skill.Weapon == PlayerWeaponKind.Laser
                    ? AuthorityEffectKind.MechaBeam
                    : AuthorityEffectKind.MechaProjectile;
                return true;
            default:
                return false;
        }
    }

    private static int MapStyle(in PendingVanillaSkill skill, AuthorityEffectKind kind)
    {
        switch (kind)
        {
            case AuthorityEffectKind.MechaProjectile:
                return skill.AmmoItemId;
            default:
                return 0;
        }
    }
}
