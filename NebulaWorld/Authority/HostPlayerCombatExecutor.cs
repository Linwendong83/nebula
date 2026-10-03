#region

using System;
using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaModel.Packets.Authority;

#endregion

namespace NebulaWorld.Authority;

/// <summary>
/// Lets the session hand the frame tick to the combat executor before it drains.
/// </summary>
public interface IHostTickAware
{
    long HostTick { set; }
}

/// <summary>
/// One vanilla skill the host decided to generate, recorded for the effect path (A14/A19).
/// </summary>
/// <remarks>
/// <para>
/// A11 validates intents and spends resources, but it does not call vanilla skill generation yet:
/// projectiles and beams stay in the host skill pool (DESIGN 7.2 "投射物/持续激光只在主机权威池生成"),
/// whose EffectBatch publication belongs to A14 and whose damage chain belongs to A19. Recording the
/// intent here — once per applied command, inside the same frame work that spent the cost — is what
/// keeps "一次攻击只创建一次权威攻击实例" structural once those cards execute it.
/// </para>
/// </remarks>
public readonly struct PendingVanillaSkill
{
    public PendingVanillaSkill(string persistentId, PlayerCombatAction action, PlayerWeaponKind weapon,
        int ammoItemId, ObjectKey target, bool hasTarget, long hostTick, long transactionId,
        in CommandKey cause)
    {
        PersistentId = persistentId;
        Action = action;
        Weapon = weapon;
        AmmoItemId = ammoItemId;
        Target = target;
        HasTarget = hasTarget;
        HostTick = hostTick;
        TransactionId = transactionId;
        Cause = cause;
    }

    public string PersistentId { get; }

    public PlayerCombatAction Action { get; }

    public PlayerWeaponKind Weapon { get; }

    public int AmmoItemId { get; }

    public ObjectKey Target { get; }

    public bool HasTarget { get; }

    public long HostTick { get; }

    public long TransactionId { get; }

    /// <summary>
    /// Client command this skill answers, for effect prediction merge (A14). Invalid for
    /// host-internal continuous ticks, which carry no client prediction to merge.
    /// </summary>
    public CommandKey Cause { get; }
}

/// <summary>
/// Host executor for player-combat intents (TASKS.md A11).
/// </summary>
/// <remarks>
/// <para>
/// Owns the DESIGN 7.2 order per command: <c>Validate</c> (simulation, pure) → <c>Reserve</c>
/// (ledger) → <c>ExecuteOnce</c> (simulation mutate + one vanilla-skill record) → <c>Commit</c>
/// (ledger) → <c>Publish</c> (A14/A19 read <see cref="PendingSkills"/>). A validation refusal moves
/// no balance and opens no ledger transaction; a reserve failure aborts (refunds) its own
/// transaction; only a fully reserved intent mutates combat state.
/// </para>
/// <para>
/// Categories other than <see cref="PlayerCombatCommand.Category"/> are answered
/// <see cref="CommandResultCode.RejectedNotReady"/>: construction, fleet and other intents belong to
/// later cards and must not be mistaken for accepted work.
/// </para>
/// <para>
/// Frame-thread only, like the queue that drains into it. The current host tick arrives through
/// <see cref="IHostTickAware"/> before each drain; command input hints expire against it.
/// </para>
/// </remarks>
public sealed class HostPlayerCombatExecutor : IHostCommandExecutor, IHostTickAware
{
    private readonly HostPlayerRegistry registry;
    private readonly HostResourceLedger ledger;
    private readonly HostPlayerSimulation simulation;
    private readonly List<PendingVanillaSkill> pendingSkills = new();

    private long continuousSequence;

    public HostPlayerCombatExecutor(HostPlayerRegistry registry, HostResourceLedger ledger,
        HostPlayerSimulation simulation, IHostCombatRules rules)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        this.simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
        Rules = rules;
    }

    /// <summary>Target truth and costs. Null stays fail-closed: fire intents answer NotReady.</summary>
    public IHostCombatRules Rules { get; set; }

    /// <summary>
    /// The host's death ledger. Null keeps damage fail-closed without death transactions; the
    /// session (A19) wires it so a mecha death opens exactly one death transaction and releases the
    /// owner's construction tasks once.
    /// </summary>
    public HostDeathLedger DeathLedger { get; set; }

    /// <summary>Player deaths opened since creation, for diagnostics.</summary>
    public long DeathsRecorded { get; private set; }

    /// <summary>Host tick of the frame being drained. Set by the session before each drain.</summary>
    public long HostTick { get; set; }

    /// <summary>Vanilla skills decided but not yet generated (A14 executes, A19 settles).</summary>
    public IReadOnlyList<PendingVanillaSkill> PendingSkills => pendingSkills;

    /// <summary>Vanilla skills recorded since creation, for diagnostics.</summary>
    public long SkillsRecordedTotal { get; private set; }

    public CommandOutcome Execute(in QueuedHostCommand command)
    {
        var packet = command.Packet;
        if (packet == null || packet.Category != PlayerCombatCommand.Category)
        {
            return new CommandOutcome(CommandResultCode.RejectedNotReady);
        }
        if (!PlayerCombatCommand.TryDecode(packet.Payload, out var request, out _))
        {
            return new CommandOutcome(CommandResultCode.RejectedInvalid);
        }

        var hasTarget = packet.TargetNativeId > 0;
        ObjectKey target = default;
        if (hasTarget)
        {
            if (!packet.TryGetTargetKey(out target))
            {
                return new CommandOutcome(CommandResultCode.RejectedTarget);
            }
        }

        var costs = Rules?.Costs ?? PlayerCombatCosts.Default;
        var plan = simulation.Validate(registry, ledger, command.ConnectionPlayerId, request,
            target, hasTarget, HostTick, costs, Rules, command.Key.Sequence);
        if (!plan.Accepted)
        {
            return new CommandOutcome(plan.Code);
        }

        var begin = ledger.BeginTransaction(command.Key, out var tx, out var cached);
        switch (begin)
        {
            case LedgerBeginResult.Duplicate:
                return cached;
            case LedgerBeginResult.TooOld:
                return new CommandOutcome(CommandResultCode.RejectedDuplicate);
            case LedgerBeginResult.WrongEpoch:
            case LedgerBeginResult.Invalid:
                return new CommandOutcome(CommandResultCode.RejectedInvalid);
            default:
                break;
        }

        foreach (var op in plan.Ops)
        {
            var reserve = Reserve(ledger, tx, op, out _);
            if (reserve != LedgerReserveCode.Ok)
            {
                ledger.AbortTransaction(tx);
                return reserve == LedgerReserveCode.StaleRevision
                    ? new CommandOutcome(CommandResultCode.RejectedStale)
                    : new CommandOutcome(CommandResultCode.RejectedResource);
            }
        }

        if (!simulation.TryGetState(plan.Owner.PersistentId, out var state) || state == null)
        {
            ledger.AbortTransaction(tx);
            return new CommandOutcome(CommandResultCode.RejectedInvalid);
        }
        simulation.Apply(state, request, target, hasTarget, HostTick, costs);

        if (!ledger.CommitTransaction(tx, CommandResultCode.Applied, HostTick, out var outcome))
        {
            return new CommandOutcome(CommandResultCode.RejectedInvalid);
        }

        RecordVanillaSkill(plan.Owner.PersistentId, request, target, hasTarget, outcome.TransactionId,
            command.Key);
        return outcome;
    }

    /// <summary>
    /// Advances every open continuous fire whose interval is due (host clock, DESIGN 7.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called once per host frame boundary after the command drain. Each due tick spends through its
    /// own host-internal transaction (no client key to deduplicate); a tick that cannot spend stops
    /// the session with its reason instead of retrying. An offline, dead or virtual owner stops
    /// without spending.
    /// </para>
    /// </remarks>
    public void TickContinuous()
    {
        foreach (var persistentId in ContinuousOwnersSnapshot())
        {
            var costs = Rules?.Costs ?? PlayerCombatCosts.Default;
            var sequence = ++continuousSequence;
            var op = simulation.PlanContinuousTick(registry, ledger, persistentId, HostTick, costs,
                Rules, sequence, out var shouldStop);
            if (op == null)
            {
                // No shot due this tick, or the session ended: stop only when told to.
                if (shouldStop != PlayerCombatRejectReason.None)
                    simulation.StopContinuous(persistentId);
                continue;
            }

            var tx = ledger.BeginHostTransaction();
            var reserve = Reserve(ledger, tx, op.Value, out _);
            if (reserve != LedgerReserveCode.Ok)
            {
                ledger.AbortHostTransaction(tx);
                simulation.StopContinuous(persistentId);
                continue;
            }
            if (!simulation.TryGetState(persistentId, out var state) || state == null)
            {
                ledger.AbortHostTransaction(tx);
                simulation.StopContinuous(persistentId);
                continue;
            }
            simulation.ConfirmContinuousTick(persistentId, HostTick, costs);
            if (ledger.CommitHostTransaction(tx, CommandResultCode.Applied, HostTick, out var outcome))
            {
                RecordVanillaSkill(persistentId,
                    new PlayerCombatRequest(
                        state.ContinuousWeapon == PlayerWeaponKind.Laser
                            ? PlayerCombatAction.LaserFire
                            : PlayerCombatAction.PrimaryFire,
                        state.ContinuousWeapon, state.ContinuousAmmoItemId,
                        HostTick, outcome.ResourceRevision, 0, 0),
                    state.ContinuousTarget, state.HasContinuousTarget, outcome.TransactionId, default);
            }
        }
    }

    /// <summary>
    /// Host-computed damage to one mecha (enemy fire, hazards). Never a client number.
    /// A lethal amount opens exactly one death transaction (A19): the tombstone plus one binding of
    /// statistics, drops and the owner's construction-task release. A repeat report of the same
    /// death moves nothing.
    /// </summary>
    public HostDamageResult ApplyHostDamage(string persistentId, int damage)
    {
        var result = simulation.ApplyHostDamage(persistentId, damage, HostTick);
        if (!result.Died || DeathLedger == null) return result;

        ConstructionOwnerKey owner = default;
        if (registry.TryGetByPersistent(persistentId, out var presence) && presence != null)
        {
            owner = ConstructionOwnerKey.ForPlayer(persistentId, presence.SessionPlayerId);
        }
        var receipt = DeathLedger.OpenPlayerDeath(persistentId, owner, HostTick);
        if (receipt.IsFirst) DeathsRecorded++;
        return result;
    }

    /// <summary>Takes one recorded vanilla skill for the effect path. Null when the queue is empty.</summary>
    public bool TryTakePendingSkill(out PendingVanillaSkill skill)
    {
        if (pendingSkills.Count == 0)
        {
            skill = default;
            return false;
        }
        skill = pendingSkills[0];
        pendingSkills.RemoveAt(0);
        return true;
    }

    private List<string> ContinuousOwnersSnapshot()
    {
        // The simulation owns the table; this snapshot keeps the tick loop re-entrant-safe.
        var owners = new List<string>();
        foreach (var persistentId in simulation.FiringOwners)
        {
            owners.Add(persistentId);
        }
        return owners;
    }

    private void RecordVanillaSkill(string persistentId, in PlayerCombatRequest request,
        ObjectKey target, bool hasTarget, long transactionId, in CommandKey cause)
    {
        // Stop/respawn generate no skill; every trigger pull generates exactly one record.
        switch (request.Action)
        {
            case PlayerCombatAction.PrimaryFire:
            case PlayerCombatAction.LaserFire:
            case PlayerCombatAction.BombDrop:
            case PlayerCombatAction.ShieldBurst:
            case PlayerCombatAction.StartContinuous:
                break;
            default:
                return;
        }
        pendingSkills.Add(new PendingVanillaSkill(persistentId, request.Action, request.Weapon,
            request.AmmoItemId, target, hasTarget, HostTick, transactionId, cause));
        SkillsRecordedTotal++;
    }

    private static LedgerReserveCode Reserve(HostResourceLedger ledger, HostTransactionId tx,
        in HostResourceOp op, out long reservation)
    {
        reservation = 0;
        if (op.IsDouble)
        {
            return ledger.TryReserveDouble(tx, op.Owner, op.Kind, op.DoubleAmount,
                op.ExpectedRevision, out reservation, out _);
        }
        return ledger.TryReserveLong(tx, op.Owner, op.Kind, op.ItemId, op.LongAmount,
            op.ExpectedRevision, out reservation, out _);
    }
}
