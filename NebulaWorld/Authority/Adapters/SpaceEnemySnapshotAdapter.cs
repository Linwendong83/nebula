#region

using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaModel.Logger;
using NebulaModel.Packets.Authority;

#endregion

namespace NebulaWorld.Authority.Adapters;

/// <summary>
/// The host's canonical view of the sector's space enemies (TASKS.md A13).
/// </summary>
/// <remarks>
/// <para>
/// The third production <see cref="IHostWorldView"/> after factory combat and ground enemies,
/// covering the <see cref="PoolKind.SpaceEnemy"/> pool of the sector (scope 0, one pool shared by
/// every hive). It reads the sector enemy pool, the global skill pool (for HP) and the hive
/// system's component ids (for kind/linkage), and encodes them as a <see cref="SpaceEnemyState"/>
/// per enemy. The old snapshot's <c>spaceComponents</c> string table is the source of the kind
/// list, but the wire carries only the <see cref="SpaceEnemyKind"/> enum — never a field name.
/// </para>
/// <para>
/// Reading never mutates: no <c>HandleFullHp/HandleZeroHp</c>, no stat removal, no activation. A
/// missing sector returns <c>false</c> (skip this scope this frame), never an empty scope — an
/// empty scope would publish every space enemy's death. Slot generations are minted by
/// <see cref="SlotGenerationTracker"/>, so a killed and recycled slot is a new
/// <see cref="ObjectKey"/>, never a resurrection. Cross-astro migration keeps the same key: the
/// sector scope is 0 and the astro ids are attributes.
/// </para>
/// <para>
/// Members are reported cores first, then the rest, so a subscriber that applies spawns in log
/// order sees hive structure before its units. A relay that lands and becomes a ground base is a
/// different pool (GroundEnemy) under a new key — the space key despawns and the ground key spawns
/// — so this adapter never reinterprets one pool's slot as another's.
/// </para>
/// </remarks>
public sealed class SpaceEnemySnapshotAdapter : IHostWorldView
{
    private readonly AuthorityEpoch epoch;
    private readonly SlotGenerationTracker tracker = new();

    public long UnreadableScopes { get; private set; }
    public long CorruptReferences { get; private set; }
    public long UnknownKinds { get; private set; }

    public SpaceEnemySnapshotAdapter(AuthorityEpoch epoch)
    {
        if (!epoch.IsValid) throw new System.ArgumentException("The adapter needs a valid world epoch.", nameof(epoch));
        this.epoch = epoch;
    }

    public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
    {
        if (scope.Kind != PoolKind.SpaceEnemy || scope.Scope != AuthorityScope.Sector)
        {
            return false;
        }
        var sector = GameMain.spaceSector;
        if (sector == null)
        {
            UnreadableScopes++;
            return false;
        }
        var pool = sector.enemyPool;
        var coresFirst = new List<int>();
        var rest = new List<int>();
        for (var id = 1; id < sector.enemyCursor; id++)
        {
            if (id >= pool.Length || pool[id].id != id) continue;
            if (pool[id].dfSCoreId > 0) coresFirst.Add(id);
            else rest.Add(id);
        }
        foreach (var id in coresFirst)
        {
            var generation = tracker.ObserveOccupied(id);
            members.Add(ObjectKey.Create(epoch, PoolKind.SpaceEnemy, AuthorityScope.Sector, id, generation));
        }
        foreach (var id in rest)
        {
            var generation = tracker.ObserveOccupied(id);
            members.Add(ObjectKey.Create(epoch, PoolKind.SpaceEnemy, AuthorityScope.Sector, id, generation));
        }
        tracker.EndScan();
        return true;
    }

    /// <summary>The authority identity of a living sector-enemy slot, as the replication scan would mint it (A22).</summary>
    public bool TryGetEnemyKey(int enemyId, out ObjectKey key)
    {
        key = default;
        var sector = GameMain.spaceSector;
        if (sector == null || enemyId <= 0 || enemyId >= sector.enemyCursor ||
            enemyId >= sector.enemyPool.Length ||
            sector.enemyPool[enemyId].id != enemyId)
        {
            return false;
        }
        var generation = tracker.ObserveOccupied(enemyId);
        key = ObjectKey.Create(epoch, PoolKind.SpaceEnemy, AuthorityScope.Sector, enemyId, generation);
        return true;
    }

    public bool TryReadState(ObjectKey key, out byte[] state)
    {
        state = null;
        if (key.Kind != PoolKind.SpaceEnemy || key.Scope != AuthorityScope.Sector) return false;
        var sector = GameMain.spaceSector;
        if (sector == null || key.NativeId >= sector.enemyCursor ||
            key.NativeId >= sector.enemyPool.Length ||
            sector.enemyPool[key.NativeId].id != key.NativeId)
        {
            return false;
        }
        if (!TryReadEnemyState(sector, key.NativeId, out var decoded))
        {
            return false;
        }
        if (!SpaceEnemyStateCodec.TryEncode(decoded, out state))
        {
            CorruptReferences++;
            return false;
        }
        return true;
    }

    private bool TryReadEnemyState(SpaceSector sector, int enemyId, out SpaceEnemyState state)
    {
        state = default;
        ref var enemy = ref sector.enemyPool[enemyId];
        var kind = SpaceEnemyKinds.FromComponentIds(enemy.dfSCoreId, enemy.dfSNodeId,
            enemy.dfSConnectorId, enemy.dfSReplicatorId, enemy.dfSGammaId, enemy.dfSTurretId,
            enemy.dfTinderId, enemy.dfRelayId, enemy.unitId, enemy.builderId);
        if (kind == SpaceEnemyKind.Unknown)
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
                stats.buffer[combatStatId].objectId == enemyId)
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
                Log.Warn($"[authority] space enemy {enemyId} has a dangling combatStatId {combatStatId}");
            }
        }
        var dockIndex = ResolveDockIndex(sector, in enemy);
        var builderIndex = ResolveBuilderIndex(sector, in enemy);
        var level = ResolveLevel(sector, in enemy, kind);
        var animation = sector.enemyAnimPool[enemyId];
        state = new SpaceEnemyState(kind, hasCombatStat, enemy.dynamic,
            enemy.protoId, enemy.modelIndex, enemy.owner, enemy.port, enemy.stateFlags,
            enemy.astroId, enemy.originAstroId, dockIndex, builderIndex, level,
            enemy.pos.x, enemy.pos.y, enemy.pos.z,
            enemy.rot.x, enemy.rot.y, enemy.rot.z, enemy.rot.w,
            enemy.dynamic ? enemy.vel.x : 0f, enemy.dynamic ? enemy.vel.y : 0f, enemy.dynamic ? enemy.vel.z : 0f,
            hp, hpMax, hpRecover, hpIncoming,
            animation.time, animation.prepare_length, animation.working_length, animation.state, animation.power);
        return true;
    }

    private static int ResolveDockIndex(SpaceSector sector, in EnemyData enemy)
    {
        try
        {
            if (enemy.dfRelayId <= 0 && enemy.dfTinderId <= 0) return 0;
            var hive = sector.GetHiveByAstroId(enemy.originAstroId);
            if (hive == null) return 0;
            if (enemy.dfRelayId > 0 && enemy.dfRelayId < hive.relays.cursor &&
                hive.relays.buffer[enemy.dfRelayId] != null)
            {
                return hive.relays.buffer[enemy.dfRelayId].dockIndex;
            }
            if (enemy.dfTinderId > 0 && enemy.dfTinderId < hive.tinders.cursor &&
                hive.tinders.buffer[enemy.dfTinderId].id == enemy.dfTinderId)
            {
                return hive.tinders.buffer[enemy.dfTinderId].dockIndex;
            }
        }
        catch (System.Exception e)
        {
            Log.Warn("[authority] space enemy dock read failed: " + e.Message);
        }
        return 0;
    }

    private static int ResolveBuilderIndex(SpaceSector sector, in EnemyData enemy)
    {
        if (enemy.builderId <= 0) return 0;
        try
        {
            var hive = sector.GetHiveByAstroId(enemy.originAstroId);
            if (hive == null) return 0;
            if (enemy.builderId < hive.builders.cursor &&
                hive.builders.buffer[enemy.builderId].id == enemy.builderId)
            {
                return hive.builders.buffer[enemy.builderId].builderIndex;
            }
        }
        catch (System.Exception e)
        {
            Log.Warn("[authority] space enemy builder read failed: " + e.Message);
        }
        return 0;
    }

    private static int ResolveLevel(SpaceSector sector, in EnemyData enemy, SpaceEnemyKind kind)
    {
        try
        {
            var hive = sector.GetHiveByAstroId(enemy.originAstroId);
            if (hive == null) return 0;
            if (kind == SpaceEnemyKind.SpaceCore)
            {
                return hive.evolve.level;
            }
        }
        catch (System.Exception e)
        {
            Log.Warn("[authority] space enemy level read failed: " + e.Message);
        }
        return 0;
    }
}
