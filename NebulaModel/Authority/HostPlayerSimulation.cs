#region

using System;
using System.Collections.Generic;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// Host-side target checks for combat intents, in primitives only (TASKS.md A11, pure model).
/// </summary>
/// <remarks>
/// <para>
/// The pure simulation never touches game pools: it asks this interface whether a target key names
/// a live object of the right generation and whether the owner's host-accepted pose can engage it
/// with the given weapon. Tests inject a fake; the game adapter (A11 <c>PlayerCombatAdapter</c>)
/// answers from the real pools, generation table and range/ray rules.
/// </para>
/// </remarks>
public interface IPlayerCombatTargetRules
{
    /// <summary>True when the key names a target the host may engage this tick.</summary>
    bool IsTargetKnown(in ObjectKey target, out string reason);

    /// <summary>True when the owner's host-accepted pose may engage the target with this weapon.</summary>
    bool IsInRange(string persistentId, in ObjectKey target, PlayerWeaponKind weapon, out string reason);
}

/// <summary>
/// One owner's host-side combat runtime: HP, cooldowns and continuous-fire state (TASKS.md A11).
/// </summary>
/// <remarks>
/// <para>
/// This is the per-player host combat proxy DESIGN 7.1 asks for, kept as primitives so it runs in a
/// plain test process. Identity and eligibility (<see cref="HostPlayerRegistry"/>) and balances
/// (<see cref="HostResourceLedger"/>) live elsewhere on purpose: this table holds only what the
/// combat rule mutates — hitpoints, cooldowns, continuous-fire sessions and burst progress.
/// </para>
/// <para>
/// There is deliberately no static current-player: every method takes the persistent owner, so two
/// owners firing at different targets on the same tick cannot share or leak state. The vanilla
/// <c>CombatManager.PlayerId</c> global switch is replaced on the game side by
/// <c>HostCombatScope</c> (try/finally restore); here the isolation is structural.
/// </para>
/// <para>
/// Hitpoints here are the host's combat truth for the mecha itself. Publishing them to clients as
/// absolute WorldState and aggregating death drops/statistics belong to A19; this table records the
/// death and stops the owner's fire, which is what makes "机甲受击、死亡、复活请求由同一权威链处理"
/// start here without claiming the full lifecycle.
/// </para>
/// </remarks>
public sealed class HostPlayerCombatState
{
    internal HostPlayerCombatState(string persistentId, int maxHp, long shield)
    {
        PersistentId = persistentId;
        MaxHp = maxHp;
        Hp = maxHp;
        Shield = shield;
        IsAlive = true;
    }

    public string PersistentId { get; }

    public int MaxHp { get; internal set; }

    public int Hp { get; internal set; }

    public long Shield { get; internal set; }

    /// <summary>Combat alive. Independent of the registry presence flag; both must pass to fire.</summary>
    public bool IsAlive { get; internal set; }

    /// <summary>Host tick before which the next trigger pull is refused as cooldown.</summary>
    public long CooldownUntilTick { get; internal set; }

    /// <summary>True while host-clocked continuous fire is open for this owner.</summary>
    public bool ContinuousFiring { get; internal set; }

    public PlayerWeaponKind ContinuousWeapon { get; internal set; }

    public int ContinuousAmmoItemId { get; internal set; }

    public ObjectKey ContinuousTarget { get; internal set; }

    public bool HasContinuousTarget { get; internal set; }

    public long ContinuousStartedTick { get; internal set; }

    public long ContinuousLastTick { get; internal set; }

    /// <summary>Mutations since creation, for diagnostics and digest input.</summary>
    public long Revision { get; internal set; }

    /// <summary>Host tick of the last applied damage, or 0 when never hit.</summary>
    public long LastDamageTick { get; internal set; }

    /// <summary>Host tick of the current death, or 0 while alive.</summary>
    public long DeathTick { get; internal set; }
}

/// <summary>
/// What validation decided for one intent, before any balance moves.
/// </summary>
public readonly struct PlayerCombatPlan
{
    public PlayerCombatPlan(bool accepted, CommandResultCode code, PlayerCombatRejectReason reason,
        LedgerOwner owner, List<HostResourceOp> ops, ObjectKey target, bool hasTarget)
    {
        Accepted = accepted;
        Code = code;
        Reason = reason;
        Owner = owner;
        Ops = ops;
        Target = target;
        HasTarget = hasTarget;
    }

    public bool Accepted { get; }

    public CommandResultCode Code { get; }

    public PlayerCombatRejectReason Reason { get; }

    public LedgerOwner Owner { get; }

    public List<HostResourceOp> Ops { get; }

    public ObjectKey Target { get; }

    public bool HasTarget { get; }
}

/// <summary>
/// Result of host-side damage applied to one mecha (host computed, never a client number).
/// </summary>
public readonly struct HostDamageResult
{
    public HostDamageResult(bool applied, bool died, int hpBefore, int hpAfter)
    {
        Applied = applied;
        Died = died;
        HpBefore = hpBefore;
        HpAfter = hpAfter;
    }

    public bool Applied { get; }

    public bool Died { get; }

    public int HpBefore { get; }

    public int HpAfter { get; }
}

/// <summary>
/// Per-owner host combat simulation (TASKS.md A11, pure model).
/// </summary>
/// <remarks>
/// <para>
/// Flow per intent (DESIGN 7.2): <c>Validate</c> (this type, pure reads) → <c>Reserve</c> (ledger,
/// by the executor) → <c>Apply</c> (this type, mutate) → <c>Commit</c> (ledger, by the executor).
/// Validation never mutates; application never touches the ledger. That split is what lets the
/// executor abort (refund) when a reserve fails after validation passed.
/// </para>
/// </remarks>
public sealed class HostPlayerSimulation
{
    private readonly Dictionary<string, HostPlayerCombatState> states =
        new(StringComparer.Ordinal);

    /// <summary>Combat states currently tracked.</summary>
    public int Count => states.Count;

    /// <summary>Persistent ids with continuous fire open. Snapshot semantics for the frame tick loop.</summary>
    public IEnumerable<string> FiringOwners
    {
        get
        {
            foreach (var pair in states)
            {
                if (pair.Value.ContinuousFiring) yield return pair.Key;
            }
        }
    }

    /// <summary>
    /// Creates or returns the owner's combat state. Host-only seed path (world load/migration);
    /// unreachable from any client packet.
    /// </summary>
    public HostPlayerCombatState EnsureOwner(string persistentId, int maxHp)
    {
        if (string.IsNullOrEmpty(persistentId))
            throw new ArgumentException("A combat owner needs a persistent id.", nameof(persistentId));
        if (maxHp <= 0) throw new ArgumentOutOfRangeException(nameof(maxHp));
        if (states.TryGetValue(persistentId, out var existing))
        {
            existing.MaxHp = maxHp;
            return existing;
        }
        var state = new HostPlayerCombatState(persistentId, maxHp, shield: 0);
        states[persistentId] = state;
        return state;
    }

    public bool TryGetState(string persistentId, out HostPlayerCombatState state)
    {
        state = null;
        return persistentId != null && states.TryGetValue(persistentId, out state);
    }

    /// <summary>Drops everything. Used when the world or session ends.</summary>
    public void Clear() => states.Clear();

    /// <summary>Default mecha HP for a freshly tracked owner. Real seeding reads the game (A21).</summary>
    public const int DefaultMaxHp = 1000;

    /// <summary>
    /// Validates one decoded intent without mutating anything. The executor reserves <see cref="PlayerCombatPlan.Ops"/>
    /// through the ledger and only then calls <see cref="Apply"/> .
    /// </summary>
    public PlayerCombatPlan Validate(HostPlayerRegistry registry, HostResourceLedger ledger,
        ushort sessionPlayerId, in PlayerCombatRequest request, ObjectKey target, bool hasTarget,
        long hostTick, PlayerCombatCosts costs, IPlayerCombatTargetRules rules, long commandSequence)
    {
        if (registry == null || ledger == null)
            return Refused(CommandResultCode.RejectedInvalid, PlayerCombatRejectReason.InvalidPayload,
                default, target, hasTarget);
        if (!registry.TryGetBySession(sessionPlayerId, out var presence) || presence == null)
            return Refused(CommandResultCode.RejectedUnauthorized, PlayerCombatRejectReason.Unauthorized,
                default, target, hasTarget);
        if (presence.IsVirtualServer || !presence.IsOnline || !presence.CanOwnCombat)
            return Refused(CommandResultCode.RejectedUnauthorized, PlayerCombatRejectReason.Unauthorized,
                default, target, hasTarget);

        var owner = LedgerOwner.ForPlayer(presence.PersistentId);
        if (!owner.IsValid)
            return Refused(CommandResultCode.RejectedInvalid, PlayerCombatRejectReason.InvalidPayload,
                default, target, hasTarget);

        var state = TryGetState(presence.PersistentId, out var existing)
            ? existing
            : EnsureOwner(presence.PersistentId, DefaultMaxHp);
        // Note: maxHp seeding from the game pools belongs to A21 (world load/migration). The pure
        // model keeps the first-seen maximum; tests pin it explicitly through EnsureOwner.

        // Respawn is the only intent a dead owner may send.
        if (!state.IsAlive || !presence.IsAlive)
        {
            if (request.Action != PlayerCombatAction.Respawn)
                return Refused(CommandResultCode.RejectedUnauthorized, PlayerCombatRejectReason.NotAlive,
                    owner, target, hasTarget);
        }
        else if (request.Action == PlayerCombatAction.Respawn)
        {
            return Refused(CommandResultCode.RejectedInvalid, PlayerCombatRejectReason.AlreadyAlive,
                owner, target, hasTarget);
        }

        // Input freshness: the hint expires; the future is invalid. Ordering never uses it.
        if (hostTick - request.InputTick > costs.MaxInputAgeTicks)
            return Refused(CommandResultCode.RejectedStale, PlayerCombatRejectReason.ExpiredInput,
                owner, target, hasTarget);
        if (request.InputTick > hostTick + costs.FutureToleranceTicks)
            return Refused(CommandResultCode.RejectedInvalid, PlayerCombatRejectReason.FutureInput,
                owner, target, hasTarget);

        // Target presence must match what the action needs; the envelope key was already gated by A03.
        if (PlayerCombatCommand.RequiresTarget(request.Action) != hasTarget)
            return Refused(CommandResultCode.RejectedTarget, PlayerCombatRejectReason.BadTarget,
                owner, target, hasTarget);
        if (hasTarget && (!target.IsValid || !target.Epoch.Equals(ledger.Epoch)))
            return Refused(CommandResultCode.RejectedTarget, PlayerCombatRejectReason.BadTarget,
                owner, target, hasTarget);

        // Continuous bookkeeping before cooldown so stop-during-cooldown still lands.
        if (request.Action == PlayerCombatAction.StartContinuous && state.ContinuousFiring)
            return Refused(CommandResultCode.RejectedDuplicate, PlayerCombatRejectReason.AlreadyFiring,
                owner, target, hasTarget);
        if (request.Action == PlayerCombatAction.StopContinuous && !state.ContinuousFiring)
            return Refused(CommandResultCode.RejectedInvalid, PlayerCombatRejectReason.NotFiring,
                owner, target, hasTarget);

        // Cooldown gates trigger pulls, not stop/respawn.
        if (NeedsCooldown(request.Action) && hostTick < state.CooldownUntilTick)
            return Refused(CommandResultCode.RejectedStale, PlayerCombatRejectReason.Cooldown,
                owner, target, hasTarget);

        // Target liveness and range come from the injected rules; a null table is fail-closed.
        if (hasTarget)
        {
            if (rules == null)
                return Refused(CommandResultCode.RejectedNotReady, PlayerCombatRejectReason.NoTargetRules,
                    owner, target, hasTarget);
            if (!rules.IsTargetKnown(target, out _))
                return Refused(CommandResultCode.RejectedTarget, PlayerCombatRejectReason.BadTarget,
                    owner, target, hasTarget);
            if (!rules.IsInRange(presence.PersistentId, target, request.Weapon, out _))
                return Refused(CommandResultCode.RejectedTarget, PlayerCombatRejectReason.OutOfRange,
                    owner, target, hasTarget);
        }

        // Resource dry-check: same revision and balance read the executor will reserve against.
        // A mismatch here becomes Stale/Insufficient there; checking now keeps the refusal reason
        // precise instead of a generic resource failure after a pointless reserve attempt.
        var ops = BuildOps(owner, request, request.ExpectedRevision, commandSequence, costs);
        foreach (var op in ops)
        {
            var check = DryCheck(ledger, op);
            if (check != PlayerCombatRejectReason.None)
            {
                var code = check == PlayerCombatRejectReason.StaleRevision
                    ? CommandResultCode.RejectedStale
                    : CommandResultCode.RejectedResource;
                return Refused(code, check, owner, target, hasTarget);
            }
        }

        return new PlayerCombatPlan(true, CommandResultCode.Applied, PlayerCombatRejectReason.None,
            owner, ops, target, hasTarget);
    }

    /// <summary>
    /// Mutates combat state after the executor reserved every op. Must not throw for validated plans.
    /// </summary>
    public void Apply(HostPlayerCombatState state, in PlayerCombatRequest request, ObjectKey target,
        bool hasTarget, long hostTick, PlayerCombatCosts costs)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        switch (request.Action)
        {
            case PlayerCombatAction.PrimaryFire:
            case PlayerCombatAction.LaserFire:
            case PlayerCombatAction.BombDrop:
            case PlayerCombatAction.ShieldBurst:
                state.CooldownUntilTick = hostTick + costs.CooldownTicksPerShot;
                state.Revision++;
                break;
            case PlayerCombatAction.StartContinuous:
                state.ContinuousFiring = true;
                state.ContinuousWeapon = request.Weapon;
                state.ContinuousAmmoItemId = request.AmmoItemId;
                state.ContinuousTarget = target;
                state.HasContinuousTarget = hasTarget;
                state.ContinuousStartedTick = hostTick;
                state.ContinuousLastTick = hostTick;
                state.CooldownUntilTick = hostTick + costs.CooldownTicksPerShot;
                state.Revision++;
                break;
            case PlayerCombatAction.StopContinuous:
                state.ContinuousFiring = false;
                state.HasContinuousTarget = false;
                state.Revision++;
                break;
            case PlayerCombatAction.Respawn:
                state.IsAlive = true;
                state.Hp = state.MaxHp;
                state.DeathTick = 0;
                state.ContinuousFiring = false;
                state.HasContinuousTarget = false;
                state.CooldownUntilTick = hostTick;
                state.Revision++;
                break;
            default:
                throw new ArgumentException("Unknown combat action: " + request.Action, nameof(request));
        }
    }

    /// <summary>
    /// Host-side damage to one mecha. Amount is host computed, never a client number (DESIGN 1.3).
    /// Shield absorption formulas stay in A19; here damage reduces HP and records death.
    /// </summary>
    public HostDamageResult ApplyHostDamage(string persistentId, int damage, long hostTick)
    {
        if (string.IsNullOrEmpty(persistentId) || damage <= 0) return new HostDamageResult(false, false, 0, 0);
        if (!states.TryGetValue(persistentId, out var state) || !state.IsAlive)
            return new HostDamageResult(false, false, 0, 0);
        var before = state.Hp;
        state.LastDamageTick = hostTick;
        var after = before - damage;
        if (after <= 0)
        {
            state.Hp = 0;
            state.IsAlive = false;
            state.DeathTick = hostTick;
            // Death releases continuous fire immediately; task/lease release is A17/A19.
            state.ContinuousFiring = false;
            state.HasContinuousTarget = false;
            state.Revision++;
            return new HostDamageResult(true, true, before, 0);
        }
        state.Hp = after;
        state.Revision++;
        return new HostDamageResult(true, false, before, after);
    }

    /// <summary>
    /// Advances one owner's continuous fire when its interval is due. Pure planning: returns the
    /// single spend op for this tick, or null when no shot is due. Stopping (target lost, dry,
    /// offline, dead) is reported through <paramref name="shouldStop"/> with its reason.
    /// </summary>
    public HostResourceOp? PlanContinuousTick(HostPlayerRegistry registry, HostResourceLedger ledger,
        string persistentId, long hostTick, PlayerCombatCosts costs, IPlayerCombatTargetRules rules,
        long sequence, out PlayerCombatRejectReason shouldStop)
    {
        shouldStop = PlayerCombatRejectReason.None;
        if (registry == null || ledger == null ||
            !states.TryGetValue(persistentId, out var state) || !state.ContinuousFiring)
            return null;
        if (!registry.TryGetByPersistent(persistentId, out var presence) || presence == null ||
            !presence.IsOnline || !presence.IsAlive || !state.IsAlive ||
            presence.IsVirtualServer)
        {
            shouldStop = PlayerCombatRejectReason.Unauthorized;
            return null;
        }
        if (hostTick - state.ContinuousLastTick < costs.ContinuousTickInterval) return null;
        if (rules == null)
        {
            shouldStop = PlayerCombatRejectReason.NoTargetRules;
            return null;
        }
        if (!state.HasContinuousTarget)
        {
            shouldStop = PlayerCombatRejectReason.BadTarget;
            return null;
        }
        if (!rules.IsTargetKnown(state.ContinuousTarget, out _) ||
            !rules.IsInRange(persistentId, state.ContinuousTarget, state.ContinuousWeapon, out _))
        {
            shouldStop = PlayerCombatRejectReason.OutOfRange;
            return null;
        }
        var owner = LedgerOwner.ForPlayer(persistentId);
        // Host-internal ticks read host truth: the op carries the live revision, so the dry-check
        // below only fails on insufficient balance, never on a stale client read.
        var key = ContinuousTickKey(owner, state);
        var liveRevision = key.IsValid ? ledger.RevisionOf(key) : 0;
        var op = ContinuousTickOp(owner, state, sequence, liveRevision, costs);
        if (op == null)
        {
            shouldStop = PlayerCombatRejectReason.UnknownAction;
            return null;
        }
        var check = DryCheck(ledger, op.Value);
        if (check != PlayerCombatRejectReason.None)
        {
            shouldStop = check;
            return null;
        }
        return op;
    }

    /// <summary>Confirms a planned tick after the executor reserved and committed it.</summary>
    public void ConfirmContinuousTick(string persistentId, long hostTick, PlayerCombatCosts costs)
    {
        if (persistentId == null || !states.TryGetValue(persistentId, out var state)) return;
        state.ContinuousLastTick = hostTick;
        state.CooldownUntilTick = hostTick + costs.CooldownTicksPerShot;
        state.Revision++;
    }

    /// <summary>Stops continuous fire without spending (target lost, dry, offline, dead, explicit stop).</summary>
    public void StopContinuous(string persistentId)
    {
        if (persistentId == null || !states.TryGetValue(persistentId, out var state)) return;
        state.ContinuousFiring = false;
        state.HasContinuousTarget = false;
        state.Revision++;
    }

    private static bool NeedsCooldown(PlayerCombatAction action)
    {
        switch (action)
        {
            case PlayerCombatAction.PrimaryFire:
            case PlayerCombatAction.LaserFire:
            case PlayerCombatAction.BombDrop:
            case PlayerCombatAction.ShieldBurst:
            case PlayerCombatAction.StartContinuous:
                return true;
            default:
                return false;
        }
    }

    private static PlayerCombatPlan Refused(CommandResultCode code, PlayerCombatRejectReason reason,
        LedgerOwner owner, ObjectKey target, bool hasTarget) =>
        new(false, code, reason, owner, new List<HostResourceOp>(), target, hasTarget);

    /// <summary>
    /// Builds the ledger spends for one intent. One trigger pull spends exactly once: ammo weapons
    /// spend one loaded round, laser/shield spend host-configured energy, bombs spend one stock item.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single-op shape is deliberate: the payload carries one expected revision for one balance,
    /// so an intent touches one balance. A weapon that charged both energy and ammo would need two
    /// revisions; the adapter must then split it into the upper-flow order E02 demands (ammo decrease
    /// in <c>AmmoFireProcedure</c>'s caller, not inside <c>ShootTarget</c>) rather than stuffing two
    /// spends behind one revision. That split is recorded here so a later card cannot "just call
    /// ShootTarget and count it as deducted".
    /// </para>
    /// </remarks>
    public static List<HostResourceOp> BuildOps(LedgerOwner owner, in PlayerCombatRequest request,
        long expectedRevision, long sequence, PlayerCombatCosts costs)
    {
        var ops = new List<HostResourceOp>();
        switch (request.Action)
        {
            case PlayerCombatAction.PrimaryFire:
                ops.Add(HostResourceOp.SpendLong(owner, LedgerResourceKind.AmmoBullet,
                    request.AmmoItemId, 1, expectedRevision, sequence));
                break;
            case PlayerCombatAction.LaserFire:
                ops.Add(HostResourceOp.SpendDouble(owner, LedgerResourceKind.CoreEnergy,
                    costs.LaserEnergyPerShot, expectedRevision, sequence));
                break;
            case PlayerCombatAction.BombDrop:
                ops.Add(HostResourceOp.SpendLong(owner, LedgerResourceKind.BombStorageItem,
                    request.ProtoId, 1, expectedRevision, sequence));
                break;
            case PlayerCombatAction.ShieldBurst:
                ops.Add(HostResourceOp.SpendDouble(owner, LedgerResourceKind.CoreEnergy,
                    costs.ShieldEnergyPerBurst, expectedRevision, sequence));
                break;
            case PlayerCombatAction.StartContinuous:
                if (request.Weapon == PlayerWeaponKind.Laser)
                    ops.Add(HostResourceOp.SpendDouble(owner, LedgerResourceKind.CoreEnergy,
                        costs.LaserEnergyPerShot, expectedRevision, sequence));
                else
                    ops.Add(HostResourceOp.SpendLong(owner, LedgerResourceKind.AmmoBullet,
                        request.AmmoItemId, 1, expectedRevision, sequence));
                break;
            case PlayerCombatAction.StopContinuous:
            case PlayerCombatAction.Respawn:
                break;
            default:
                break;
        }
        return HostResourceBatch.Order(ops);
    }

    private static LedgerResourceKey ContinuousTickKey(LedgerOwner owner, HostPlayerCombatState state)
    {
        if (state.ContinuousWeapon == PlayerWeaponKind.Laser)
            return new LedgerResourceKey(owner, LedgerResourceKind.CoreEnergy, 0);
        if (PlayerCombatCommand.IsAmmoWeapon(state.ContinuousWeapon))
            return new LedgerResourceKey(owner, LedgerResourceKind.AmmoBullet, state.ContinuousAmmoItemId);
        return default;
    }

    private static HostResourceOp? ContinuousTickOp(LedgerOwner owner, HostPlayerCombatState state,
        long sequence, long liveRevision, PlayerCombatCosts costs)
    {
        if (state.ContinuousWeapon == PlayerWeaponKind.Laser)
        {
            return HostResourceOp.SpendDouble(owner, LedgerResourceKind.CoreEnergy,
                costs.LaserEnergyPerShot, liveRevision, sequence);
        }
        if (PlayerCombatCommand.IsAmmoWeapon(state.ContinuousWeapon))
        {
            return HostResourceOp.SpendLong(owner, LedgerResourceKind.AmmoBullet,
                state.ContinuousAmmoItemId, 1, liveRevision, sequence);
        }
        return null;
    }

    private static PlayerCombatRejectReason DryCheck(HostResourceLedger ledger, in HostResourceOp op)
    {
        var key = op.Key;
        var current = ledger.RevisionOf(key);
        if (current != op.ExpectedRevision) return PlayerCombatRejectReason.StaleRevision;
        if (op.IsDouble)
        {
            if (!ledger.TryGetDouble(key.Owner, key.Kind, out var balance, out _) ||
                balance < op.DoubleAmount)
                return PlayerCombatRejectReason.Insufficient;
        }
        else
        {
            if (!ledger.TryGetLong(key.Owner, key.Kind, key.ItemId, out var balance, out _) ||
                balance < op.LongAmount)
                return PlayerCombatRejectReason.Insufficient;
        }
        return PlayerCombatRejectReason.None;
    }
}
