#region

using System;
using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaModel.Logger;

#endregion

namespace NebulaWorld.Authority.Adapters;

/// <summary>
/// Writes host ground-enemy facts into the client's local pools (TASKS.md A12's binding side).
/// </summary>
/// <remarks>
/// <para>
/// The game-side <see cref="IReplicaMirrorObserver"/> for <see cref="PoolKind.GroundEnemy"/>. Thin by
/// design: it guards the apply window, tracks which factory instance a scope's binding belongs to,
/// and delegates every decision to the pure <see cref="GroundEnemyBinding"/> core. All writes land
/// inside the replica's apply call, which the session only opens at the frame boundary.
/// </para>
/// <para>
/// A re-imported factory is a new pool with new indexes. An instance change replaces the core
/// wholesale — without releasing the old shells into the fresh pool — because the old local slot
/// numbers are lies about it. The next baseline or state record converges the scope; nothing is
/// guessed in between.
/// </para>
/// <para>
/// The shell carries no logic components, so the vanilla AI, hatred, attack and base-manufacture
/// ticks skip it by construction. Despawn uses the pure removal path; the binding core never calls a
/// Kill path that would generate drops, statistics or experience.
/// </para>
/// </remarks>
public sealed class GroundEnemyReplicaBinding : IReplicaMirrorObserver, IReplicaBaselineReadiness
{
    private sealed class ScopeBinding
    {
        public PlanetFactory Factory;
        public EnemyData[] EnemyPool;
        public GroundEnemyBinding Core;
    }

    private readonly Func<bool> applyWindowOpen;
    private readonly Dictionary<int, ScopeBinding> scopes = [];

    public ClientWorldReplica Replica { get; set; }
    public long RefusalsOutsideApplyWindow { get; private set; }
    public long DeferredBaselines { get; private set; }

    public bool IsReadyForBaseline(ScopeKey scope) => scope.Kind != PoolKind.GroundEnemy ||
        (FactoryFor(scope.Scope)?.planet.factoryLoaded == true && GameMain.spaceSector?.skillSystem != null);

    public GroundEnemyReplicaBinding(Func<bool> applyWindowOpen = null)
    {
        this.applyWindowOpen = applyWindowOpen ?? DefaultApplyWindowOpen;
    }

    public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state)
    {
        if (scope.Kind != PoolKind.GroundEnemy) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            return;
        }
        var binding = BindingFor(scope);
        if (binding.Factory == null || binding.Core == null) return;
        binding.Core.ApplyState(key, state);
    }

    public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
    {
        if (scope.Kind != PoolKind.GroundEnemy) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            return;
        }
        var factory = FactoryFor(scope.Scope);
        if (factory == null)
        {
            DeferredBaselines++;
            return;
        }
        var binding = BindingFor(scope);
        if (binding.Factory != factory)
        {
            binding.Factory = factory;
            binding.Core = CreateCore(scope.Scope);
        }
        var keep = new HashSet<int>();
        foreach (var member in members) keep.Add(member.Key.NativeId);
        var pools = new GroundEnemyReplicaPools(scope.Scope);
        for (var id = 1; id < factory.enemyCursor; id++)
            if (factory.enemyPool[id].id == id && !keep.Contains(id)) pools.RemoveEnemyShell(id);
        binding.Core.ReconcileBaseline(members);
    }

    public void OnMemberRemoved(ScopeKey scope, in ObjectKey key)
    {
        if (scope.Kind != PoolKind.GroundEnemy) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            return;
        }
        if (scopes.TryGetValue(scope.Scope, out var binding) && binding.Core != null)
        {
            binding.Core.RemoveMember(key);
        }
    }

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
            var factory = FactoryFor(scope.Scope);
            binding = new ScopeBinding { Factory = factory, Core = factory != null ? CreateCore(scope.Scope) : null };
            scopes.Add(scope.Scope, binding);
        }
        if (binding.Factory != FactoryFor(scope.Scope) || binding.EnemyPool != FactoryFor(scope.Scope)?.enemyPool)
        {
            binding.Factory = FactoryFor(scope.Scope);
            binding.EnemyPool = binding.Factory?.enemyPool;
            binding.Core = binding.Factory != null ? CreateCore(scope.Scope) : null;
        }
        if (binding.Core == null && binding.Factory == null)
        {
            var factory = FactoryFor(scope.Scope);
            if (factory != null)
            {
                binding.Factory = factory;
                binding.Core = CreateCore(scope.Scope);
            }
        }
        return binding;
    }

    private GroundEnemyBinding CreateCore(int planetId) => new(planetId, new GroundEnemyReplicaPools(planetId),
        (key, localId) => Replica?.SetLocalComponentBinding(key, localId));

    private static PlanetFactory FactoryFor(int planetId) => GameMain.galaxy?.PlanetById(planetId)?.factory;

    private static bool DefaultApplyWindowOpen()
    {
        var session = Multiplayer.Session;
        return session?.AuthorityRuntime?.ApplyContext?.IsActiveOnCurrentThread ?? false;
    }
}
