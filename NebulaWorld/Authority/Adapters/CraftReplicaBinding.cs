#region

using System;
using System.Collections.Generic;
using NebulaModel.Authority;

#endregion

namespace NebulaWorld.Authority.Adapters;

/// <summary>
/// Writes host craft facts into the client's ground and sector pools (TASKS.md A13's binding side).
/// </summary>
/// <remarks>
/// <para>
/// The game-side <see cref="IReplicaMirrorObserver"/> for <see cref="PoolKind.GroundCraft"/> and
/// <see cref="PoolKind.SpaceCraft"/>. Thin by design: it guards the apply window, tracks which
/// factory instance a ground scope's binding belongs to, and delegates every decision to the pure
/// <see cref="CraftBinding"/> cores. All writes land inside the replica's apply call, which the
/// session only opens at the frame boundary.
/// </para>
/// <para>
/// A re-imported ground factory is a new pool with new indexes. An instance change replaces the
/// core wholesale — without releasing the old shells into the fresh pool — because the old local
/// slot numbers are lies about it. The sector pool is never re-imported, so the space core lives as
/// long as the scope does. The next baseline or state record converges the scope; nothing is
/// guessed in between.
/// </para>
/// <para>
/// Shells carry no fleet/unit/drone logic, so vanilla fleet, targeting, ammo and destruction ticks
/// skip them by construction. Despawn uses the pure removal path; the cores never call a
/// destruction rule that would generate drops, statistics or refunds.
/// </para>
/// </remarks>
public sealed class CraftReplicaBinding : IReplicaMirrorObserver
{
    private sealed class ScopeBinding
    {
        public PoolKind Kind;
        public PlanetFactory Factory;
        public CraftBinding Core;
    }

    private readonly Func<bool> applyWindowOpen;
    private readonly Dictionary<(PoolKind, int), ScopeBinding> scopes = [];

    public ClientWorldReplica Replica { get; set; }
    public long RefusalsOutsideApplyWindow { get; private set; }
    public long DeferredBaselines { get; private set; }

    public CraftReplicaBinding(Func<bool> applyWindowOpen = null)
    {
        this.applyWindowOpen = applyWindowOpen ?? DefaultApplyWindowOpen;
    }

    public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state)
    {
        if (scope.Kind != PoolKind.GroundCraft && scope.Kind != PoolKind.SpaceCraft) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            return;
        }
        var binding = BindingFor(scope);
        if (binding?.Core == null) return;
        if (scope.Kind == PoolKind.GroundCraft && binding.Factory == null) return;
        if (scope.Kind == PoolKind.SpaceCraft && GameMain.spaceSector == null) return;
        binding.Core.ApplyState(key, state);
    }

    public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
    {
        if (scope.Kind != PoolKind.GroundCraft && scope.Kind != PoolKind.SpaceCraft) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            return;
        }
        if (scope.Kind == PoolKind.GroundCraft)
        {
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
                binding.Core = CreateCore(scope.Kind, scope.Scope);
            }
            binding.Core.ReconcileBaseline(members);
            return;
        }
        if (GameMain.spaceSector == null)
        {
            DeferredBaselines++;
            return;
        }
        var space = BindingFor(scope);
        space.Core.ReconcileBaseline(members);
    }

    public void OnMemberRemoved(ScopeKey scope, in ObjectKey key)
    {
        if (scope.Kind != PoolKind.GroundCraft && scope.Kind != PoolKind.SpaceCraft) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            return;
        }
        if (scopes.TryGetValue((scope.Kind, scope.Scope), out var binding) && binding.Core != null)
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
        if (!scopes.TryGetValue((scope.Kind, scope.Scope), out var binding))
        {
            if (scope.Kind == PoolKind.GroundCraft)
            {
                var factory = FactoryFor(scope.Scope);
                binding = new ScopeBinding
                {
                    Kind = scope.Kind,
                    Factory = factory,
                    Core = factory != null ? CreateCore(scope.Kind, scope.Scope) : null
                };
            }
            else
            {
                binding = new ScopeBinding
                {
                    Kind = scope.Kind,
                    Factory = null,
                    Core = GameMain.spaceSector != null ? CreateCore(scope.Kind, scope.Scope) : null
                };
            }
            scopes.Add((scope.Kind, scope.Scope), binding);
        }
        if (binding.Core == null)
        {
            if (scope.Kind == PoolKind.GroundCraft)
            {
                var factory = FactoryFor(scope.Scope);
                if (factory != null)
                {
                    binding.Factory = factory;
                    binding.Core = CreateCore(scope.Kind, scope.Scope);
                }
            }
            else if (GameMain.spaceSector != null)
            {
                binding.Core = CreateCore(scope.Kind, scope.Scope);
            }
        }
        return binding;
    }

    private static CraftBinding CreateCore(PoolKind kind, int scope)
    {
        if (kind == PoolKind.GroundCraft)
        {
            return new CraftBinding(kind, scope, new GroundCraftReplicaPools(scope));
        }
        return new CraftBinding(kind, scope, new SpaceCraftReplicaPools());
    }

    private static PlanetFactory FactoryFor(int planetId) => GameMain.galaxy?.PlanetById(planetId)?.factory;

    private static bool DefaultApplyWindowOpen()
    {
        var session = Multiplayer.Session;
        return session?.AuthorityRuntime?.ApplyContext?.IsActive ?? false;
    }
}
