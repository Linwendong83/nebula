#region

using NebulaModel.Authority;
using NebulaModel.Packets.Authority;

#endregion

namespace NebulaWorld.Authority.Adapters;

/// <summary>
/// The client-local pool mechanics behind <see cref="SpaceEnemyBinding"/> (TASKS.md A13).
/// </summary>
/// <remarks>
/// <para>
/// Every method here touches vanilla pools; none decides whether a write should happen — that is the
/// binding core's call. The shell is display-only by construction: logic component ids
/// (core/node/connector/replicator/gamma/turret/tinder/relay/unit/builder) stay zero, so the
/// vanilla hive manufacture, AI, hatred and attack ticks skip it. Rendering, colliders and audio
/// are built through the same display path the vanilla creation uses
/// (<c>SpaceSector.CreateEnemyDisplayComponents</c>), which is why hive/relay/tinder topology shows
/// without running any rule.
/// </para>
/// <para>
/// Removal is always the pure path (<c>SpaceSector.RemoveEnemyWithComponents</c>), never
/// <c>KillEnemyFinal</c>: despawn must not generate drops, statistics or experience. The host owns
/// the single death transaction (A19); the client only removes the mirror.
/// </para>
/// </remarks>
public sealed class SpaceEnemyReplicaPools : ISpaceEnemyPools
{
    public bool EnemyExists(int enemyId)
    {
        var sector = GameMain.spaceSector;
        return sector != null && enemyId > 0 && enemyId < sector.enemyCursor &&
            enemyId < sector.enemyPool.Length && sector.enemyPool[enemyId].id == enemyId;
    }

    public void CreateEnemyShell(int enemyId, in SpaceEnemyState state)
    {
        var sector = GameMain.spaceSector;
        if (sector == null || enemyId <= 0) return;
        EnsureCapacity(sector, enemyId);
        if (sector.enemyPool[enemyId].id == enemyId)
        {
            RemoveEnemyShell(enemyId);
        }
        RemoveFromRecycle(sector, enemyId);
        if (enemyId >= sector.enemyCursor)
        {
            sector.enemyCursor = enemyId + 1;
        }
        ref var enemy = ref sector.enemyPool[enemyId];
        enemy.SetEmpty();
        enemy.id = enemyId;
        enemy.protoId = state.ProtoId;
        enemy.modelIndex = state.ModelIndex;
        enemy.astroId = state.AstroId;
        enemy.originAstroId = state.OriginAstroId != 0 ? state.OriginAstroId : state.AstroId;
        enemy.owner = state.Owner;
        enemy.port = state.Port;
        enemy.dynamic = state.IsDynamic;
        enemy.isSpace = true;
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
        enemy.dfSCoreId = 0;
        enemy.dfSNodeId = 0;
        enemy.dfSConnectorId = 0;
        enemy.dfSReplicatorId = 0;
        enemy.dfSGammaId = 0;
        enemy.dfSTurretId = 0;
        enemy.dfTinderId = 0;
        enemy.dfRelayId = 0;
        enemy.unitId = 0;
        enemy.modelId = 0;
        enemy.mmblockId = 0;
        enemy.colliderId = 0;
        enemy.audioId = 0;
        try
        {
            sector.CreateEnemyDisplayComponents(enemyId, null, false);
        }
        catch (System.Exception e)
        {
            NebulaModel.Logger.Log.Warn("[authority] space enemy display creation failed for " + enemyId + ": " + e.Message);
        }
        WriteCombatStat(sector, enemyId, in state);
    }

    public void WriteEnemyState(int enemyId, in SpaceEnemyState state)
    {
        var sector = GameMain.spaceSector;
        if (sector == null || enemyId <= 0 || enemyId >= sector.enemyPool.Length) return;
        ref var enemy = ref sector.enemyPool[enemyId];
        if (enemy.id != enemyId) return;
        enemy.protoId = state.ProtoId;
        enemy.modelIndex = state.ModelIndex;
        enemy.owner = state.Owner;
        enemy.port = state.Port;
        enemy.stateFlags = state.StateFlags;
        enemy.astroId = state.AstroId;
        enemy.originAstroId = state.OriginAstroId;
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
        WriteCombatStat(sector, enemyId, in state);
    }

    public void RemoveEnemyShell(int enemyId)
    {
        var sector = GameMain.spaceSector;
        if (sector == null || enemyId <= 0 || enemyId >= sector.enemyPool.Length) return;
        if (sector.enemyPool[enemyId].id != enemyId) return;
        sector.RemoveEnemyWithComponents(enemyId);
    }

    private void WriteCombatStat(SpaceSector sector, int enemyId, in SpaceEnemyState state)
    {
        ref var enemy = ref sector.enemyPool[enemyId];
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
            stat.astroId = state.AstroId;
            stat.originAstroId = state.OriginAstroId;
            stat.objectType = (int)EObjectType.Enemy;
            stat.objectId = enemyId;
            stat.localPos = enemy.pos;
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

    private static void EnsureCapacity(SpaceSector sector, int enemyId)
    {
        while (enemyId >= sector.enemyCapacity)
        {
            sector.SetEnemyCapacity(sector.enemyCapacity * 2);
        }
    }

    private static void RemoveFromRecycle(SpaceSector sector, int enemyId)
    {
        for (var i = 0; i < sector.enemyRecycleCursor; i++)
        {
            if (sector.enemyRecycle[i] == enemyId)
            {
                var last = sector.enemyRecycleCursor - 1;
                sector.enemyRecycle[i] = sector.enemyRecycle[last];
                sector.enemyRecycleCursor = last;
                break;
            }
        }
    }
}
