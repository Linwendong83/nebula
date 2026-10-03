#region

using System;
using NebulaModel.Authority;
using NebulaWorld.Combat;

#endregion

namespace NebulaWorld.Authority;

/// <summary>
/// Host-side combat rules: target truth plus the cost table (TASKS.md A11, adapter seam).
/// </summary>
/// <remarks>
/// <para>
/// The pure simulation (<see cref="HostPlayerSimulation"/>) asks only for primitives. This interface
/// is what the game answers with: target liveness/generation from the real pools, range/ray from the
/// host-accepted pose, and per-weapon costs from mecha/tech config. Tests inject a fake; the
/// production implementation reads the pools (its full pool coverage lands with A12/A13, so the
/// session wires no rules until then and fire intents stay <c>NotReady</c> fail-closed).
/// </para>
/// </remarks>
public interface IHostCombatRules : IPlayerCombatTargetRules
{
    /// <summary>Per-weapon costs the host charges. Never taken from a client packet.</summary>
    PlayerCombatCosts Costs { get; }
}

/// <summary>
/// Explicit caster context for one host combat execution (DESIGN 7.1, TASKS.md A11).
/// </summary>
/// <remarks>
/// <para>
/// Vanilla routes several combat paths through the shared <c>CombatManager.PlayerId</c> global
/// (see the <c>PlayerAction_Combat</c> transpilers). The host must therefore state, for exactly one
/// execution, whose rules are running — and restore the previous value even when the execution
/// throws. That try/finally lives in <see cref="CombatManager.CombatPlayerScope"/> (the only place
/// with setter access); this type is the adapter-level alias so callers do not depend on the
/// manager's nesting. Construct it in a <c>using</c>, run the vanilla rule for that player, and
/// the previous id is restored on dispose.
/// </para>
/// <para>
/// Long-lived state stays in <see cref="HostPlayerSimulation"/> keyed by persistent owner, never in
/// this scope and never in the static. Two owners interleaved on the same thread each hold their own
/// scope; nesting restores in reverse order, so there is no cross-talk by construction.
/// </para>
/// </remarks>
public sealed class HostCombatScope : IDisposable
{
    private readonly CombatManager.CombatPlayerScope inner;

    public HostCombatScope(ushort actingPlayerId)
    {
        inner = CombatManager.CombatAs(actingPlayerId);
        ActingPlayerId = actingPlayerId;
    }

    /// <summary>The player whose rules run inside this scope.</summary>
    public ushort ActingPlayerId { get; }

    /// <summary>Runs <paramref name="action"/> as <paramref name="playerId"/>, restoring afterwards.</summary>
    public static void ExecuteAs(ushort playerId, Action action)
    {
        using (new HostCombatScope(playerId))
        {
            action();
        }
    }

    public void Dispose()
    {
        inner.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Authority-mode policy for the legacy combat facts (TASKS.md A11, pure decision).
/// </summary>
/// <remarks>
/// <para>
/// The old room broadcasts firing facts: <c>MechaShoot</c> (with an ammo claim), <c>MechaBomb</c>
/// (with a velocity/rotation claim) and <c>MechaShieldBurst</c> (with shield energy claims), and the
/// host replays them through vanilla with infinite laser/shield energy
/// (<c>CombatManager.ShootTarget</c> sets <c>laserEnergy = int.MaxValue</c>;
/// <c>CombatManager.GameTick</c> sets remote <c>energyShieldEnergy = int.MaxValue</c>).
/// In a host authority world every one of those is a client asserting a fact instead of sending an
/// intent, so the mode refuses them and charges real costs from the ledger instead. Legacy rooms
/// keep the old path byte for byte.
/// </para>
/// <para>
/// Pure like <see cref="HostResourcePolicy"/>: call sites check the live mode bit and ask here what
/// to do, so tests drive the decision without a game process.
/// </para>
/// </remarks>
public static class HostCombatPolicy
{
    /// <summary>True when a legacy combat fact packet must be refused without touching the world.</summary>
    public static bool ShouldRefuseLegacyCombatFact(bool isHostAuthority) => isHostAuthority;

    /// <summary>True when infinite laser/shield energy must not be applied to any mecha.</summary>
    public static bool MustUseMeteredEnergy(bool isHostAuthority) => isHostAuthority;

    /// <summary>Why a refusal happened, for logs. Callers branch on the bool, never on this text.</summary>
    public static string RefusalReason(string packet) =>
        "client " + packet + " must not assert a combat fact in host authority mode (A11 intents only)";
}
