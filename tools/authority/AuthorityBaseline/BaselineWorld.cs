using System;
using NebulaWorld;

namespace AuthorityBaseline;

/// <summary>
/// A read-only snapshot of one observed object. Every field is copied out of the vanilla pools;
/// nothing here mutates game state.
/// </summary>
internal struct TargetSnapshot
{
    public bool Resolved;
    public string UnresolvedReason;
    public string ObjectKey;
    public string RawSlot;
    public int CombatStatId;
    public int ConstructStatId;
    /// <summary>
    /// True only when the object's combatStatId resolved to a live stat in the pool. Without
    /// this flag a stale or recycled stat id is indistinguishable from "hp is zero", which
    /// would make the evidence actively misleading.
    /// </summary>
    public bool HasCombatStat;
    public bool HasConstructStat;
    public int Hp;
    public int HpMax;
    public int HpRecover;
    public int HpIncoming;
    public int RepairerCount;
    public long Generation;
    public string Owner;
    public bool Alive;
}

/// <summary>Pool reads shared by the hooks and the frame sampler.</summary>
internal static class BaselineWorld
{
    public static PlanetFactory Factory(int planetId)
    {
        try
        {
            var planet = GameMain.galaxy?.PlanetById(planetId);
            return planet?.factory;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string ObjectKeyText(string kind, int scopeId, int nativeId, long generation)
    {
        // DESIGN 4.1 ObjectKey = AuthorityEpoch + PoolKind + ScopeId + NativeId + Generation.
        // A00 has no AuthorityEpoch yet (A02); it is written as "-" so the missing identity layer
        // is visible in the evidence instead of being silently faked.
        var epoch = BaselineRuntime.MultiplayerActive && Multiplayer.Session != null
            ? "-"
            : "-";
        return "epoch=" + epoch + "|kind=" + kind + "|scope=" + scopeId +
               "|native=" + nativeId + "|gen=" + generation;
    }

    public static string RawSlotText(int combatStatId, int constructStatId)
    {
        return "combatStatId=" + combatStatId + ",constructStatId=" + constructStatId;
    }

    public static long Generation(int astroId, int nativeId)
    {
        try
        {
            if (!BaselineRuntime.MultiplayerActive) return -1;
            return Multiplayer.Session.Generations.Get(astroId, nativeId);
        }
        catch (Exception)
        {
            return -1;
        }
    }

    public static TargetSnapshot Resolve(TrackedTarget target)
    {
        var factory = Factory(target.PlanetId);
        if (factory == null)
        {
            return new TargetSnapshot { UnresolvedReason = "FactoryNotLoaded", ObjectKey = ObjectKeyText(target.Kind, target.PlanetId, target.NativeId, -1) };
        }
        return target.Kind == "enemy" ? ResolveEnemy(factory, target.NativeId) : ResolveBuilding(factory, target.NativeId);
    }

    public static TargetSnapshot ResolveEnemy(PlanetFactory factory, int enemyId)
    {
        var snapshot = new TargetSnapshot
        {
            UnresolvedReason = null,
            CombatStatId = 0,
            ConstructStatId = 0,
            Generation = -1
        };
        if (factory.enemyPool == null || enemyId <= 0 || enemyId >= factory.enemyPool.Length)
        {
            snapshot.UnresolvedReason = "EnemySlotOutOfRange";
            snapshot.ObjectKey = ObjectKeyText("enemy", factory.planetId, enemyId, -1);
            return snapshot;
        }
        ref var enemy = ref factory.enemyPool[enemyId];
        if (enemy.id != enemyId)
        {
            snapshot.UnresolvedReason = "EnemySlotEmpty";
            snapshot.ObjectKey = ObjectKeyText("enemy", factory.planetId, enemyId, -1);
            return snapshot;
        }
        snapshot.Resolved = true;
        snapshot.Alive = true;
        snapshot.Generation = Generation(factory.planetId, enemyId);
        snapshot.CombatStatId = enemy.combatStatId;
        snapshot.Owner = enemy.owner > 0 ? "base:" + enemy.owner : "none";
        snapshot.RawSlot = RawSlotText(enemy.combatStatId, 0);
        snapshot.ObjectKey = ObjectKeyText("enemy", factory.planetId, enemyId, snapshot.Generation);
        ReadCombatStat(factory, enemy.combatStatId, ref snapshot);
        return snapshot;
    }

    public static TargetSnapshot ResolveBuilding(PlanetFactory factory, int entityId)
    {
        var snapshot = new TargetSnapshot
        {
            UnresolvedReason = null,
            CombatStatId = 0,
            ConstructStatId = 0,
            Generation = -1
        };
        if (factory.entityPool == null || entityId <= 0 || entityId >= factory.entityPool.Length)
        {
            snapshot.UnresolvedReason = "EntitySlotOutOfRange";
            snapshot.ObjectKey = ObjectKeyText("entity", factory.planetId, entityId, -1);
            return snapshot;
        }
        ref var entity = ref factory.entityPool[entityId];
        if (entity.id != entityId)
        {
            snapshot.UnresolvedReason = "EntitySlotEmpty";
            snapshot.ObjectKey = ObjectKeyText("entity", factory.planetId, entityId, -1);
            return snapshot;
        }
        snapshot.Resolved = true;
        snapshot.Alive = true;
        snapshot.Generation = Generation(factory.planetId, entityId);
        snapshot.CombatStatId = entity.combatStatId;
        snapshot.ConstructStatId = entity.constructStatId;
        snapshot.RawSlot = RawSlotText(entity.combatStatId, entity.constructStatId);
        snapshot.ObjectKey = ObjectKeyText("entity", factory.planetId, entityId, snapshot.Generation);
        ReadCombatStat(factory, entity.combatStatId, ref snapshot);
        ReadConstructStat(factory, entity.constructStatId, ref snapshot);
        return snapshot;
    }

    public static void ReadCombatStat(PlanetFactory factory, int combatStatId, ref TargetSnapshot snapshot)
    {
        var stats = factory?.skillSystem?.combatStats;
        if (stats?.buffer == null || combatStatId <= 0 || combatStatId >= stats.buffer.Length) return;
        ref var stat = ref stats.buffer[combatStatId];
        if (stat.id != combatStatId) return;
        snapshot.HasCombatStat = true;
        snapshot.Hp = stat.hp;
        snapshot.HpMax = stat.hpMax;
        snapshot.HpRecover = stat.hpRecover;
        snapshot.HpIncoming = stat.hpIncoming;
    }

    public static void ReadConstructStat(PlanetFactory factory, int constructStatId, ref TargetSnapshot snapshot)
    {
        var stats = factory?.constructionSystem?.constructStats;
        if (stats?.buffer == null || constructStatId <= 0 || constructStatId >= stats.buffer.Length) return;
        ref var stat = ref stats.buffer[constructStatId];
        if (stat.id != constructStatId) return;
        snapshot.HasConstructStat = true;
        snapshot.RepairerCount = stat.repairerCount;
        // repairerModuleId: -1 means the single vanilla player mecha, > 0 is a construction module
        // (battle base). Vanilla has exactly one player, so this is the raw fact A00 records.
        snapshot.Owner = stat.repairerModuleId == -1
            ? "mecha:-1"
            : "module:" + stat.repairerModuleId;
    }

    public static string CasterText(SkillTarget caster)
    {
        return "type=" + (int)caster.type + ",id=" + caster.id + ",astro=" + caster.astroId;
    }

    public static string CasterText(SkillTargetLocal caster)
    {
        return "type=" + (int)caster.type + ",id=" + caster.id;
    }
}
