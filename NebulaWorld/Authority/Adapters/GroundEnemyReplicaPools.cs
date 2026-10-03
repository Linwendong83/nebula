#region

using NebulaModel.Authority;
using NebulaModel.Packets.Authority;

#endregion

namespace NebulaWorld.Authority.Adapters;

/// <summary>
/// The client-local pool mechanics behind <see cref="GroundEnemyBinding"/> (TASKS.md A12).
/// </summary>
/// <remarks>
/// <para>
/// Every method here touches vanilla pools; none decides whether a write should happen — that is the
/// binding core's call. The shell is display-only by construction: logic component ids
/// (builder/unit/turret/shield/connector/replicator) stay zero, so the vanilla AI, hatred, attack and
/// base-manufacture ticks skip it. Rendering, colliders, audio and the spatial hash are built through
/// the same display path the vanilla creation uses (<c>CreateEnemyDisplayComponents</c>), which is why
/// "主池、空间哈希、物理/渲染引用的重绑定" holds without running any rule.
/// </para>
/// <para>
/// Removal is always the pure path (<c>RemoveEnemyWithComponents</c>), never
/// <c>KillEnemyFinally</c>: despawn must not generate drops, statistics or experience. The host owns
/// the single death transaction (A19); the client only removes the mirror.
/// </para>
/// </remarks>
public sealed class GroundEnemyReplicaPools : IGroundEnemyPools
{
    private readonly int planetId;

    public GroundEnemyReplicaPools(int planetId)
    {
        this.planetId = planetId;
    }

    public bool EnemyExists(int enemyId)
    {
        var factory = Factory;
        return factory != null && enemyId > 0 && enemyId < factory.enemyCursor &&
            enemyId < factory.enemyPool.Length && factory.enemyPool[enemyId].id == enemyId;
    }

    public void CreateEnemyShell(int enemyId, in GroundEnemyState state)
    {
        var factory = Factory;
        if (factory == null || enemyId <= 0) return;
        EnsureCapacity(factory, enemyId);
        if (factory.enemyPool[enemyId].id == enemyId)
        {
            RemoveEnemyShell(enemyId);
        }
        RemoveFromRecycle(factory, enemyId);
        if (enemyId >= factory.enemyCursor)
        {
            factory.enemyCursor = enemyId + 1;
        }
        ref var enemy = ref factory.enemyPool[enemyId];
        enemy.SetEmpty();
        enemy.id = enemyId;
        enemy.protoId = state.ProtoId;
        enemy.modelIndex = state.ModelIndex;
        enemy.astroId = planetId;
        enemy.originAstroId = state.OriginAstroId != 0 ? state.OriginAstroId : planetId;
        enemy.owner = state.Owner;
        enemy.port = state.Port;
        enemy.dynamic = state.IsDynamic;
        enemy.isSpace = false;
        enemy.localized = true;
        enemy.stateFlags = state.StateFlags;
        enemy.pos.x = state.PosX;
        enemy.pos.y = state.PosY;
        enemy.pos.z = state.PosZ;
        enemy.rot.x = state.RotX;
        enemy.rot.y = state.RotY;
        enemy.rot.z = state.RotZ;
        enemy.rot.w = state.RotW;
        enemy.vel.x = state.VelX;
        enemy.vel.y = state.VelY;
        enemy.vel.z = state.VelZ;
        enemy.combatStatId = 0;
        enemy.builderId = 0;
        enemy.dfGBaseId = 0;
        enemy.dfGConnectorId = 0;
        enemy.dfGReplicatorId = 0;
        enemy.dfGTurretId = 0;
        enemy.dfGShieldId = 0;
        enemy.unitId = 0;
        enemy.modelId = 0;
        enemy.mmblockId = 0;
        enemy.colliderId = 0;
        enemy.audioId = 0;
        if (enemy.dynamic)
        {
            enemy.hashAddress = factory.hashSystemDynamic.AddObjectToBucket(enemyId, enemy.pos, EObjectType.Enemy);
        }
        else
        {
            enemy.hashAddress = factory.hashSystemStatic.AddObjectToBucket(enemyId, enemy.pos, EObjectType.Enemy);
        }
        try
        {
            factory.CreateEnemyDisplayComponents(enemyId);
        }
        catch (System.Exception e)
        {
            NebulaModel.Logger.Log.Warn("[authority] ground enemy display creation failed for " + enemyId + ": " + e.Message);
        }
        WriteCombatStat(factory, enemyId, in state);
    }

    public void WriteEnemyState(int enemyId, in GroundEnemyState state)
    {
        var factory = Factory;
        if (factory == null || enemyId <= 0 || enemyId >= factory.enemyPool.Length) return;
        ref var enemy = ref factory.enemyPool[enemyId];
        if (enemy.id != enemyId) return;
        enemy.protoId = state.ProtoId;
        enemy.modelIndex = state.ModelIndex;
        enemy.owner = state.Owner;
        enemy.port = state.Port;
        enemy.stateFlags = state.StateFlags;
        var moved = enemy.pos.x != state.PosX || enemy.pos.y != state.PosY || enemy.pos.z != state.PosZ;
        enemy.pos.x = state.PosX;
        enemy.pos.y = state.PosY;
        enemy.pos.z = state.PosZ;
        enemy.rot.x = state.RotX;
        enemy.rot.y = state.RotY;
        enemy.rot.z = state.RotZ;
        enemy.rot.w = state.RotW;
        enemy.vel.x = state.VelX;
        enemy.vel.y = state.VelY;
        enemy.vel.z = state.VelZ;
        if (moved)
        {
            RefreshHash(factory, enemyId);
        }
        WriteCombatStat(factory, enemyId, in state);
    }

    public void RemoveEnemyShell(int enemyId)
    {
        var factory = Factory;
        if (factory == null || enemyId <= 0 || enemyId >= factory.enemyPool.Length) return;
        if (factory.enemyPool[enemyId].id != enemyId) return;
        factory.RemoveEnemyWithComponents(enemyId);
    }

    private void WriteCombatStat(PlanetFactory factory, int enemyId, in GroundEnemyState state)
    {
        ref var enemy = ref factory.enemyPool[enemyId];
        var skill = GameMain.data.spaceSector.skillSystem;
        if (state.HasCombatStat)
        {
            int statId = enemy.combatStatId;
            if (statId <= 0 || statId >= skill.combatStats.cursor || skill.combatStats.buffer[statId].id != statId)
            {
                statId = skill.combatStats.Add().id;
                enemy.combatStatId = statId;
            }
            ref var stat = ref skill.combatStats.buffer[statId];
            stat.hp = state.Hp;
            stat.hpMax = state.HpMax;
            stat.hpRecover = state.HpRecover;
            stat.hpIncoming = state.HpIncoming;
            stat.astroId = stat.originAstroId = planetId;
            stat.objectType = (int)EObjectType.Enemy;
            stat.objectId = enemyId;
            stat.localPos = factory.enemyPool[enemyId].pos;
            stat.size = 1f;
        }
        else if (enemy.combatStatId != 0)
        {
            var statId = enemy.combatStatId;
            if (statId > 0 && statId < skill.combatStats.cursor && skill.combatStats.buffer[statId].id == statId)
            {
                skill.OnRemovingSkillTarget(statId, skill.combatStats.buffer[statId].originAstroId, ETargetType.CombatStat);
                skill.combatStats.Remove(statId);
            }
            enemy.combatStatId = 0;
        }
    }

    private static void EnsureCapacity(PlanetFactory factory, int enemyId)
    {
        while (enemyId >= factory.enemyCapacity)
        {
            factory.SetEnemyCapacity(factory.enemyCapacity * 2);
        }
    }

    private static void RemoveFromRecycle(PlanetFactory factory, int enemyId)
    {
        for (var i = 0; i < factory.enemyRecycleCursor; i++)
        {
            if (factory.enemyRecycle[i] == enemyId)
            {
                var last = factory.enemyRecycleCursor - 1;
                factory.enemyRecycle[i] = factory.enemyRecycle[last];
                factory.enemyRecycleCursor = last;
                break;
            }
        }
    }

    private static void RefreshHash(PlanetFactory factory, int enemyId)
    {
        try
        {
            ref var enemy = ref factory.enemyPool[enemyId];
            if (enemy.dynamic)
            {
                factory.hashSystemDynamic.RemoveObjectFromBucket(enemy.hashAddress);
                enemy.hashAddress = factory.hashSystemDynamic.AddObjectToBucket(enemyId, enemy.pos, EObjectType.Enemy);
            }
            else
            {
                factory.hashSystemStatic.RemoveObjectFromBucket(enemy.hashAddress);
                enemy.hashAddress = factory.hashSystemStatic.AddObjectToBucket(enemyId, enemy.pos, EObjectType.Enemy);
            }
        }
        catch (System.Exception e)
        {
            NebulaModel.Logger.Log.Warn("[authority] ground enemy hash refresh failed for " + enemyId + ": " + e.Message);
        }
    }

    private PlanetFactory Factory => GameMain.galaxy?.PlanetById(planetId)?.factory;
}
