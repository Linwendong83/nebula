#region

using System;
using System.Collections.Generic;
using NebulaModel.Authority;

#endregion

namespace NebulaWorld.Authority.Adapters;

/// <summary>
/// Writes host space-enemy facts into the client's sector pool (TASKS.md A13's binding side).
/// </summary>
/// <remarks>
/// <para>
/// The game-side <see cref="IReplicaMirrorObserver"/> for <see cref="PoolKind.SpaceEnemy"/>. Thin by
/// design: it guards the apply window and delegates every decision to the pure
/// <see cref="SpaceEnemyBinding"/> core. All writes land inside the replica's apply call, which the
/// session only opens at the frame boundary.
/// </para>
/// <para>
/// The sector pool is never re-imported (one pool for the whole sector), so unlike the per-planet
/// bindings there is no factory-instance tracking: the core lives as long as the scope does. A
/// missing sector defers the baseline visibly instead of guessing.
/// </para>
/// <para>
/// The shell carries no logic components, so the vanilla hive, AI, hatred and attack ticks skip it
/// by construction. Despawn uses the pure removal path; the binding core never calls a Kill path
/// that would generate drops, statistics or experience.
/// </para>
/// </remarks>
public sealed class SpaceEnemyReplicaBinding : IReplicaMirrorObserver
{
    private readonly Func<bool> applyWindowOpen;
    private SpaceEnemyBinding core;

    public ClientWorldReplica Replica { get; set; }
    public long RefusalsOutsideApplyWindow { get; private set; }
    public long DeferredBaselines { get; private set; }

    public SpaceEnemyReplicaBinding(Func<bool> applyWindowOpen = null)
    {
        this.applyWindowOpen = applyWindowOpen ?? DefaultApplyWindowOpen;
    }

    public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state)
    {
        if (scope.Kind != PoolKind.SpaceEnemy) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            return;
        }
        var binding = CoreFor();
        if (binding == null) return;
        binding.ApplyState(key, state);
    }

    public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
    {
        if (scope.Kind != PoolKind.SpaceEnemy) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            return;
        }
        if (GameMain.spaceSector == null)
        {
            DeferredBaselines++;
            return;
        }
        var binding = CoreFor();
        if (binding == null) return;
        binding.ReconcileBaseline(members);
    }

    public void OnMemberRemoved(ScopeKey scope, in ObjectKey key)
    {
        if (scope.Kind != PoolKind.SpaceEnemy) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            return;
        }
        core?.RemoveMember(key);
    }

    public void Reset()
    {
        core = null;
        RefusalsOutsideApplyWindow = 0;
        DeferredBaselines = 0;
    }

    private SpaceEnemyBinding CoreFor()
    {
        if (core == null)
        {
            if (GameMain.spaceSector == null) return null;
            core = new SpaceEnemyBinding(new SpaceEnemyReplicaPools());
        }
        return core;
    }

    private static bool DefaultApplyWindowOpen()
    {
        var session = Multiplayer.Session;
        return session?.AuthorityRuntime?.ApplyContext?.IsActive ?? false;
    }
}
