#region

using System;
using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaModel.Logger;

#endregion

namespace NebulaWorld.Authority.Adapters;

/// <summary>
/// Writes host combat facts into the client's local pools (TASKS.md A08's binding side).
/// </summary>
/// <remarks>
/// <para>
/// This is the game-side <see cref="IReplicaMirrorObserver"/>. It is intentionally thin: it guards
/// the apply window, tracks which factory instance a scope's binding belongs to, repairs the
/// references a factory import leaves dangling, and delegates every decision to the pure
/// <see cref="FactoryCombatBinding"/> core. All writes land inside the replica's apply call, which
/// the session only opens at the frame boundary — the apply safe point the task card requires.
/// </para>
/// <para>
/// The factory-instance tracking exists because a client unloads factories when it leaves a star
/// and re-imports them on return. The re-imported factory is a new pool with new indexes; the old
/// binding core's local stat ids would be lies about it. An instance change therefore replaces the
/// core wholesale — <em>without</em> releasing the old ids into the new pool — and repairs the
/// imported references once, before the mirror reconciles.
/// </para>
/// <para>
/// Only the <see cref="PoolKind.Entity"/> pool is bound here (A08's scope). Craft, vegetation and
/// veins keep their legacy client behaviour until A12/A13 migrate them; enemies keep the existing
/// sync path untouched.
/// </para>
/// </remarks>
public sealed class FactoryCombatReplicaBinding : IReplicaMirrorObserver
{
    private sealed class ScopeBinding
    {
        /// <summary>The factory instance the current core was built against, or null until one was seen.</summary>
        public PlanetFactory Factory;

        /// <summary>True once this factory instance's imported references were repaired.</summary>
        public bool ReferencesRepaired;

        public FactoryCombatBinding Core;
    }

    private readonly Func<bool> applyWindowOpen;
    private readonly Dictionary<int, ScopeBinding> scopes = [];

    /// <summary>The replica whose mirror records local component bindings. Set at wiring time.</summary>
    public ClientWorldReplica Replica { get; set; }

    /// <summary>Observer calls refused because no apply window was open. Non-zero means the guard or the wiring broke; fail-closed.</summary>
    public long RefusalsOutsideApplyWindow { get; private set; }

    /// <summary>Baselines that arrived before the local factory existed. They are deferred, not guessed.</summary>
    public long DeferredBaselines { get; private set; }

    /// <param name="applyWindowOpen">
    /// Injected so tests can drive the guard; production passes the live session's apply context.
    /// </param>
    public FactoryCombatReplicaBinding(Func<bool> applyWindowOpen = null)
    {
        this.applyWindowOpen = applyWindowOpen ?? DefaultApplyWindowOpen;
    }

    public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state)
    {
        if (scope.Kind != PoolKind.Entity) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            return;
        }
        var binding = BindingFor(scope);
        if (binding == null) return;
        binding.Core.ApplyState(key, state);
    }

    public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
    {
        if (scope.Kind != PoolKind.Entity) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            return;
        }
        var factory = FactoryFor(scope.Scope);
        if (factory == null)
        {
            // The baseline can install before the client's factory import finishes. Reconciling
            // against a factory we cannot see would defer every record anyway; counting makes the
            // gap visible and the next baseline or state record converges the scope.
            DeferredBaselines++;
            return;
        }

        var binding = BindingFor(scope);
        if (binding.Factory != factory)
        {
            // New pool (first sight, or a re-import after unload): the old core's local ids are
            // meaningless here. Replace it without releasing into the fresh pool, repair the
            // dangling imported references once, then reconcile the baseline.
            binding.Factory = factory;
            binding.ReferencesRepaired = false;
            binding.Core = CreateCore(scope.Scope);
        }
        if (!binding.ReferencesRepaired)
        {
            NeutralizeImportedReferences(factory);
            binding.ReferencesRepaired = true;
        }
        binding.Core.ReconcileBaseline(members);
    }

    public void OnMemberRemoved(ScopeKey scope, in ObjectKey key)
    {
        if (scope.Kind != PoolKind.Entity) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            return;
        }
        scopes.TryGetValue(scope.Scope, out var binding);
        binding?.Core.RemoveMember(key);
    }

    /// <summary>Forgets every scope binding. Called when the replica is reset.</summary>
    public void Reset()
    {
        scopes.Clear();
        RefusalsOutsideApplyWindow = 0;
        DeferredBaselines = 0;
    }

    private ScopeBinding BindingFor(ScopeKey scope)
    {
        if (!scopes.TryGetValue(scope.Scope, out var binding))
        {
            binding = new ScopeBinding { Core = CreateCore(scope.Scope) };
            scopes.Add(scope.Scope, binding);
        }
        return binding;
    }

    private FactoryCombatBinding CreateCore(int planetId) => new(planetId, new FactoryCombatReplicaPools(planetId),
        (key, localId) => Replica?.SetLocalComponentBinding(key, localId));

    /// <summary>
    /// Zeroes the combat-stat references a factory import carried over, without removing any pool
    /// entry: the values name the <em>host's</em> global skill pool slots and dangle on this
    /// installation (E06). Entities are re-bound from the mirror immediately after; craft,
    /// vegetation and veins have no replica binding yet and stay cleared until A12/A13. The enemy
    /// pool is untouched — its stats are client-visible facts of the existing sync path.
    /// </summary>
    private static void NeutralizeImportedReferences(PlanetFactory factory)
    {
        var count = 0;
        for (var i = 1; i < factory.entityCursor; i++)
        {
            if (factory.entityPool[i].id == i && factory.entityPool[i].combatStatId != 0)
            {
                factory.entityPool[i].combatStatId = 0;
                count++;
            }
        }
        for (var i = 1; i < factory.craftCursor; i++)
        {
            if (factory.craftPool[i].id == i && factory.craftPool[i].combatStatId != 0)
            {
                factory.craftPool[i].combatStatId = 0;
                count++;
            }
        }
        for (var i = 1; i < factory.vegeCursor; i++)
        {
            if (factory.vegePool[i].id == i && factory.vegePool[i].combatStatId != 0)
            {
                factory.vegePool[i].combatStatId = 0;
                count++;
            }
        }
        for (var i = 1; i < factory.veinCursor; i++)
        {
            if (factory.veinPool[i].id == i && factory.veinPool[i].combatStatId != 0)
            {
                factory.veinPool[i].combatStatId = 0;
                count++;
            }
        }
        if (count > 0)
        {
            Log.Info($"[authority] neutralized {count} imported combat references on {factory.planet.name} (rebuilt from the replica)");
        }
    }

    private static PlanetFactory FactoryFor(int planetId) => GameMain.galaxy?.PlanetById(planetId)?.factory;

    private static bool DefaultApplyWindowOpen()
    {
        var session = Multiplayer.Session;
        return session?.AuthorityRuntime?.ApplyContext?.IsActive ?? false;
    }
}
