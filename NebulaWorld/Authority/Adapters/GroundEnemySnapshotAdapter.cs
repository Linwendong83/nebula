#region

using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaModel.Logger;
using NebulaModel.Packets.Authority;

#endregion

namespace NebulaWorld.Authority.Adapters;

/// <summary>
/// The host's canonical view of one planet's ground enemies (TASKS.md A12).
/// </summary>
/// <remarks>
/// <para>
/// The second production <see cref="IHostWorldView"/> after the factory combat domain, covering the
/// <see cref="PoolKind.GroundEnemy"/> pool of each loaded factory. It reads the enemy pool, the
/// global skill pool (for HP) and the ground system's component ids (for kind/linkage), and encodes
/// them as a <see cref="GroundEnemyState"/> per enemy. The old snapshot's
/// <c>groundComponents</c> string table is the source of the kind list, but the wire carries only
/// the <see cref="GroundEnemyKind"/> enum — never a field name.
/// </para>
/// <para>
/// Reading never mutates: no <c>HandleFullHp/HandleZeroHp</c>, no stat removal, no activation. A
/// planet whose factory is not loaded returns <c>false</c> (skip this scope this frame), never an
/// empty scope — an empty scope would publish every enemy's death. Slot generations are minted per
/// scope by <see cref="SlotGenerationTracker"/>, so a killed and recycled slot is a new
/// <see cref="ObjectKey"/>, never a resurrection.
/// </para>
/// <para>
/// Members are reported base cores first, then dependents, so a subscriber that applies spawns in
/// log order sees the parent identity before the child's state (the inversion rule is still enforced
/// client-side by deferral, but ordering makes the common case converge in one frame).
/// </para>
/// </remarks>
public sealed class GroundEnemySnapshotAdapter : IHostWorldView
{
    private readonly AuthorityEpoch epoch;
    private readonly Dictionary<int, SlotGenerationTracker> trackers = [];

    public long UnreadableScopes { get; private set; }
    public long CorruptReferences { get; private set; }
    public long UnknownKinds { get; private set; }

    public GroundEnemySnapshotAdapter(AuthorityEpoch epoch)
    {
        if (!epoch.IsValid) throw new System.ArgumentException("The adapter needs a valid world epoch.", nameof(epoch));
        this.epoch = epoch;
    }

    public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
    {
        if (scope.Kind != PoolKind.GroundEnemy)
        {
            return false;
        }
        var factory = FactoryFor(scope.Scope);
        if (factory == null)
        {
            UnreadableScopes++;
            return false;
        }
        var tracker = TrackerFor(scope.Scope);
        var pool = factory.enemyPool;
        var baseFirst = new List<int>();
        var rest = new List<int>();
        for (var id = 1; id < factory.enemyCursor; id++)
        {
            if (id >= pool.Length || pool[id].id != id) continue;
            if (pool[id].dfGBaseId > 0) baseFirst.Add(id);
            else rest.Add(id);
        }
        foreach (var id in baseFirst)
        {
            var generation = tracker.ObserveOccupied(id);
            members.Add(ObjectKey.Create(epoch, PoolKind.GroundEnemy, scope.Scope, id, generation));
        }
        foreach (var id in rest)
        {
            var generation = tracker.ObserveOccupied(id);
            members.Add(ObjectKey.Create(epoch, PoolKind.GroundEnemy, scope.Scope, id, generation));
        }
        tracker.EndScan();
        return true;
    }

    public bool TryReadState(ObjectKey key, out byte[] state)
    {
        state = null;
        if (key.Kind != PoolKind.GroundEnemy) return false;
        var factory = FactoryFor(key.Scope);
        if (factory == null || key.NativeId >= factory.enemyCursor ||
            key.NativeId >= factory.enemyPool.Length ||
            factory.enemyPool[key.NativeId].id != key.NativeId)
        {
            return false;
        }
        if (!TryReadEnemyState(factory, key.NativeId, out var decoded))
        {
            return false;
        }
        if (!GroundEnemyStateCodec.TryEncode(decoded, out state))
        {
            CorruptReferences++;
            return false;
        }
        return true;
    }

    private bool TryReadEnemyState(PlanetFactory factory, int enemyId, out GroundEnemyState state)
    {
        state = default;
        ref var enemy = ref factory.enemyPool[enemyId];
        var kind = GroundEnemyKinds.FromComponentIds(enemy.dfGBaseId, enemy.unitId, enemy.builderId,
            enemy.dfGTurretId, enemy.dfGShieldId, enemy.dfGConnectorId, enemy.dfGReplicatorId);
        if (kind == GroundEnemyKind.Unknown)
        {
            UnknownKinds++;
            return false;
        }
        var hasCombatStat = false;
        int hp = 0, hpMax = 0, hpRecover = 0, hpIncoming = 0;
        var combatStatId = enemy.combatStatId;
        if (combatStatId > 0)
        {
            var stats = GameMain.data.spaceSector.skillSystem.combatStats;
            if (combatStatId < stats.cursor && stats.buffer[combatStatId].id == combatStatId &&
                stats.buffer[combatStatId].objectType == (int)EObjectType.Enemy &&
                stats.buffer[combatStatId].objectId == enemyId &&
                stats.buffer[combatStatId].astroId == factory.planetId)
            {
                ref var stat = ref stats.buffer[combatStatId];
                hasCombatStat = true;
                hp = stat.hp;
                hpMax = stat.hpMax;
                hpRecover = stat.hpRecover;
                hpIncoming = stat.hpIncoming;
            }
            else
            {
                CorruptReferences++;
                Log.Warn($"[authority] ground enemy {enemyId} on {factory.planet.name} has a dangling combatStatId {combatStatId}");
            }
        }
        var baseId = ResolveBaseId(factory, in enemy);
        var builderIndex = ResolveBuilderIndex(factory, in enemy);
        var level = ResolveLevel(factory, in enemy, kind);
        var animation = factory.enemyAnimPool[enemyId];
        var unit = enemy.unitId > 0 && enemy.unitId < factory.enemySystem.units.cursor &&
            factory.enemySystem.units.buffer[enemy.unitId].id == enemy.unitId ? factory.enemySystem.units.buffer[enemy.unitId] : default;
        state = new GroundEnemyState(kind, hasCombatStat, enemy.dynamic,
            enemy.protoId, enemy.modelIndex, enemy.owner, enemy.port, enemy.stateFlags,
            enemy.astroId, enemy.originAstroId, baseId, builderIndex, level,
            (float)enemy.pos.x, (float)enemy.pos.y, (float)enemy.pos.z,
            enemy.rot.x, enemy.rot.y, enemy.rot.z, enemy.rot.w,
            enemy.dynamic ? enemy.vel.x : 0f, enemy.dynamic ? enemy.vel.y : 0f, enemy.dynamic ? enemy.vel.z : 0f,
            hp, hpMax, hpRecover, hpIncoming,
            animation.time, animation.prepare_length, animation.working_length, animation.state, animation.power,
            unit.anim, unit.disturbValue, unit.steering, unit.speed);
        return true;
    }

    private static int ResolveBaseId(PlanetFactory factory, in EnemyData enemy)
    {
        if (enemy.dfGBaseId > 0) return enemy.dfGBaseId;
        if (enemy.unitId > 0)
        {
            var units = factory.enemySystem.units;
            if (enemy.unitId < units.cursor &&
                units.buffer[enemy.unitId].id == enemy.unitId)
            {
                return units.buffer[enemy.unitId].baseId;
            }
        }
        return enemy.owner;
    }

    private static int ResolveBuilderIndex(PlanetFactory factory, in EnemyData enemy)
    {
        if (enemy.builderId <= 0) return 0;
        var builders = factory.enemySystem.builders;
        if (enemy.builderId < builders.cursor &&
            builders.buffer[enemy.builderId].id == enemy.builderId)
        {
            return builders.buffer[enemy.builderId].builderIndex;
        }
        return 0;
    }

    private static int ResolveLevel(PlanetFactory factory, in EnemyData enemy, GroundEnemyKind kind)
    {
        try
        {
            if (kind == GroundEnemyKind.GroundUnit && enemy.unitId > 0)
            {
                var units = factory.enemySystem.units;
                if (enemy.unitId < units.cursor &&
                    units.buffer[enemy.unitId].id == enemy.unitId)
                {
                    return units.buffer[enemy.unitId].level;
                }
            }
            if (kind == GroundEnemyKind.GroundBase && enemy.dfGBaseId > 0)
            {
                var bases = factory.enemySystem.bases;
                if (enemy.dfGBaseId < bases.cursor && bases.buffer[enemy.dfGBaseId] != null &&
                    bases.buffer[enemy.dfGBaseId].id == enemy.dfGBaseId)
                {
                    return bases.buffer[enemy.dfGBaseId].evolve.level;
                }
            }
        }
        catch (System.Exception e)
        {
            Log.Warn("[authority] ground enemy level read failed: " + e.Message);
        }
        return 0;
    }

    /// <summary>The authority identity of a living enemy slot, as the replication scan would mint it (A22).</summary>
    /// <seealso cref="TryReadMembers"/>
    public bool TryGetEnemyKey(int planetId, int enemyId, out ObjectKey key)
    {
        key = default;
        var factory = FactoryFor(planetId);
        if (factory == null || enemyId <= 0 || enemyId >= factory.enemyCursor ||
            enemyId >= factory.enemyPool.Length ||
            factory.enemyPool[enemyId].id != enemyId)
        {
            return false;
        }
        var generation = TrackerFor(planetId).ObserveOccupied(enemyId);
        key = ObjectKey.Create(epoch, PoolKind.GroundEnemy, planetId, enemyId, generation);
        return true;
    }

    private static PlanetFactory FactoryFor(int planetId)
    {
        var planet = GameMain.galaxy?.PlanetById(planetId);
        return planet?.factory;
    }

    private SlotGenerationTracker TrackerFor(int planetId)
    {
        if (!trackers.TryGetValue(planetId, out var tracker))
        {
            tracker = new SlotGenerationTracker();
            trackers.Add(planetId, tracker);
        }
        return tracker;
    }
}
