using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using NebulaAPI.GameState;
using NebulaWorld;
using UnityEngine;

namespace AuthorityBaseline;

/// <summary>
/// Observation-only Harmony probes for the A00 baseline.
///
/// Rules for every handler in this file:
///  * Prefixes read values and never assign to game fields or ref parameters.
///  * Every hook body is wrapped so a diagnostic failure cannot change game behaviour.
///  * No hook returns false: vanilla execution always proceeds unmodified.
/// </summary>
internal static class BaselineHooks
{
    private static Harmony harmony;
    private static readonly ConcurrentDictionary<int, StatMemory> CombatMemory = new();
    private static readonly ConcurrentDictionary<int, StatRepairMemory> ConstructMemory = new();
    private static int[] watchedCombatStats = Array.Empty<int>();
    private static int[] watchedConstructStats = Array.Empty<int>();

    private sealed class StatMemory
    {
        public int Hp;
        public int ObjectId;
        public int ObjectType;
    }

    private sealed class StatRepairMemory
    {
        public int RepairerCount;
        public int EntityId;
    }

    [ThreadStatic] private static RepairProbe repairProbe;
    [ThreadStatic] private static LaunchProbe launchProbe;
    [ThreadStatic] private static int hpProbeDepth;

    private struct RepairProbe
    {
        public bool Active;
        public int EntityId;
        public int CombatStatId;
        public int ConstructStatId;
        public int HpBefore;
        public int RepairerBefore;
    }

    private sealed class LaunchProbe
    {
        public bool Active;
        public Dictionary<int, DroneState> Drones;
        public Dictionary<int, int> RepairerCounts;
    }

    private struct DroneState
    {
        public int Stage;
        public int Target;
        public int Owner;
    }

    public static void SetWatched(int[] combatStatIds, int[] constructStatIds)
    {
        watchedCombatStats = combatStatIds ?? Array.Empty<int>();
        watchedConstructStats = constructStatIds ?? Array.Empty<int>();
    }

    private static bool Watching(int[] set, int id)
    {
        if (BaselineRuntime.Verbose) return true;
        return Array.BinarySearch(set, id) >= 0;
    }

    public static void Install()
    {
        harmony = new Harmony("nebula.tests.authority-baseline");
        Patch("CombatStat.TickSkillLogic", typeof(CombatStat), nameof(CombatStat.TickSkillLogic),
            nameof(TickSkillLogic_Prefix), nameof(TickSkillLogic_Postfix));
        Patch("CombatStat.HandleFullHp", typeof(CombatStat), nameof(CombatStat.HandleFullHp),
            nameof(HandleFullHp_Prefix), nameof(HandleFullHp_Postfix));
        Patch("CombatStat.HandleZeroHp", typeof(CombatStat), nameof(CombatStat.HandleZeroHp),
            nameof(HandleZeroHp_Prefix), nameof(HandleZeroHp_Postfix));
        Patch("SkillSystem.DamageObject", typeof(SkillSystem), nameof(SkillSystem.DamageObject),
            nameof(DamageObject_Prefix), null);
        Patch("SkillSystem.DamageGroundObjectByLocalCaster", typeof(SkillSystem),
            nameof(SkillSystem.DamageGroundObjectByLocalCaster), nameof(DamageGroundLocal_Prefix), null);
        Patch("SkillSystem.DamageGroundObjectByRemoteCaster", typeof(SkillSystem),
            nameof(SkillSystem.DamageGroundObjectByRemoteCaster), nameof(DamageGroundRemote_Prefix), null);
        Patch("ConstructionSystem.AddConstructStat", typeof(ConstructionSystem), nameof(ConstructionSystem.AddConstructStat),
            null, nameof(AddConstructStat_Postfix));
        Patch("ConstructionSystem.RemoveConstructStat", typeof(ConstructionSystem), nameof(ConstructionSystem.RemoveConstructStat),
            nameof(RemoveConstructStat_Prefix), null);
        Patch("ConstructionSystem.Repair", typeof(ConstructionSystem), "Repair",
            nameof(Repair_Prefix), nameof(Repair_Postfix));
        Patch("ConstructionSystem.DetermineLaunch", typeof(ConstructionSystem), "DetermineLaunch",
            nameof(DetermineLaunch_Prefix), nameof(DetermineLaunch_Postfix));
        Patch("ConstructionSystem.UpdateModules", typeof(ConstructionSystem), "UpdateModules",
            nameof(UpdateModules_Prefix), nameof(UpdateModules_Postfix));
        Patch("ConstructStat.GameTick", typeof(ConstructStat), nameof(ConstructStat.GameTick),
            nameof(ConstructStatGameTick_Prefix), nameof(ConstructStatGameTick_Postfix));
        Patch("SimulatedWorld.OnPlayerJoinedGame", typeof(SimulatedWorld), nameof(SimulatedWorld.OnPlayerJoinedGame),
            null, nameof(OnPlayerJoined_Postfix));
        Patch("SimulatedWorld.OnPlayerLeftGame", typeof(SimulatedWorld), nameof(SimulatedWorld.OnPlayerLeftGame),
            null, nameof(OnPlayerLeft_Postfix));
        // A01: the frame boundary must be measured, not assumed (see FrameProbe).
        FrameProbe.Install();
    }

    private static void Patch(string label, Type type, string methodName, string prefix, string postfix)
    {
        try
        {
            var target = AccessTools.Method(type, methodName);
            if (target == null)
            {
                Fail(label, "method not found");
                return;
            }
            var prefixMethod = prefix == null ? null : AccessTools.Method(typeof(BaselineHooks), prefix);
            var postfixMethod = postfix == null ? null : AccessTools.Method(typeof(BaselineHooks), postfix);
            if ((prefix != null && prefixMethod == null) || (postfix != null && postfixMethod == null))
            {
                Fail(label, "handler not found");
                return;
            }
            harmony.Patch(target,
                prefixMethod == null ? null : new HarmonyMethod(prefixMethod),
                postfixMethod == null ? null : new HarmonyMethod(postfixMethod));
            lock (BaselineRuntime.HookReport) BaselineRuntime.HookReport.Add("ok " + label + " -> " + target);
        }
        catch (Exception error)
        {
            Fail(label, error.GetType().Name + ": " + error.Message);
        }
    }

    private static void Fail(string label, string message)
    {
        BaselineRuntime.HookFailures++;
        lock (BaselineRuntime.HookReport) BaselineRuntime.HookReport.Add("FAIL " + label + " " + message);
    }

    private static void Emit(BaselineRecord record)
    {
        var log = BaselineRuntime.Log;
        if (log == null) return;
        log.Emit(record);
    }

    private static string TickNote()
    {
        return "tick=" + BaselineRuntime.GameTick();
    }

    // ---------------- CombatStat: per-tick hp transitions (covers regen, zero-hp, full-hp) ------

    private static void TickSkillLogic_Prefix(ref CombatStat __instance)
    {
        try
        {
            if (hpProbeDepth != 0) return;
            hpProbeDepth++;
            var memory = CombatMemory.GetOrAdd(__instance.id, _ => new StatMemory());
            if (memory.ObjectId != __instance.objectId || memory.ObjectType != __instance.objectType)
            {
                memory.ObjectId = __instance.objectId;
                memory.ObjectType = __instance.objectType;
                memory.Hp = __instance.hp;
                return;
            }
            memory.Hp = __instance.hp;
        }
        catch (Exception)
        {
            // diagnostic only
        }
        finally
        {
            hpProbeDepth--;
        }
    }

    private static void TickSkillLogic_Postfix(ref CombatStat __instance)
    {
        try
        {
            if (!Watching(watchedCombatStats, __instance.id)) return;
            if (!CombatMemory.TryGetValue(__instance.id, out var memory)) return;
            if (memory.ObjectId != __instance.objectId || memory.ObjectType != __instance.objectType) return;
            if (memory.Hp == __instance.hp) return;
            var record = BaselineRuntime.Log.New("hp.tick", "CombatStat.TickSkillLogic");
            record.PlanetId = __instance.astroId;
            record.ObjectKey = BaselineWorld.ObjectKeyText(KindOf(__instance.objectType), __instance.originAstroId, __instance.objectId, -1);
            record.RawSlot = BaselineWorld.RawSlotText(__instance.id, 0);
            record.HpBefore = memory.Hp;
            record.HpAfter = __instance.hp;
            record.HpMax = __instance.hpMax;
            record.HpRecover = __instance.hpRecover;
            record.HpIncoming = __instance.hpIncoming;
            record.Owner = "objectType:" + __instance.objectType;
            record.Reason = "TickSkillLogic";
            Emit(record);
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    private static string KindOf(int objectType)
    {
        // EObjectType and ETargetType share the low values but are distinct wire enums
        // (DESIGN 4.1). Only the objectType value itself is recorded as a raw fact here.
        switch (objectType)
        {
            case 0: return "entity";
            case 1: return "vegetable";
            case 2: return "vein";
            case 3: return "prebuild";
            case 4: return "enemy";
            case 5: return "ruin";
            case 6: return "craft";
            default: return "objectType:" + objectType;
        }
    }

    private static void HandleFullHp_Prefix(ref CombatStat __instance)
    {
        try
        {
            var record = BaselineRuntime.Log.New("hp.fullhp.begin", "CombatStat.HandleFullHp");
            record.PlanetId = __instance.astroId;
            record.ObjectKey = BaselineWorld.ObjectKeyText(KindOf(__instance.objectType), __instance.originAstroId, __instance.objectId, -1);
            record.RawSlot = BaselineWorld.RawSlotText(__instance.id, 0);
            record.HpBefore = __instance.hp;
            record.HpMax = __instance.hpMax;
            record.Reason = "HandleFullHp";
            Emit(record);
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    private static void HandleFullHp_Postfix(ref CombatStat __instance)
    {
        try
        {
            var record = BaselineRuntime.Log.New("hp.fullhp.end", "CombatStat.HandleFullHp");
            record.PlanetId = __instance.astroId;
            record.ObjectKey = BaselineWorld.ObjectKeyText(KindOf(__instance.objectType), __instance.originAstroId, __instance.objectId, -1);
            record.RawSlot = BaselineWorld.RawSlotText(__instance.id, 0);
            record.HpAfter = __instance.hp;
            record.Reason = "HandleFullHp";
            Emit(record);
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    private static void HandleZeroHp_Prefix(ref CombatStat __instance)
    {
        try
        {
            var record = BaselineRuntime.Log.New("hp.zerohp.begin", "CombatStat.HandleZeroHp");
            record.PlanetId = __instance.astroId;
            record.ObjectKey = BaselineWorld.ObjectKeyText(KindOf(__instance.objectType), __instance.originAstroId, __instance.objectId, -1);
            record.RawSlot = BaselineWorld.RawSlotText(__instance.id, 0);
            record.HpBefore = __instance.hp;
            record.HpMax = __instance.hpMax;
            record.Reason = "HandleZeroHp";
            Emit(record);
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    private static void HandleZeroHp_Postfix(ref CombatStat __instance)
    {
        try
        {
            var record = BaselineRuntime.Log.New("hp.zerohp.end", "CombatStat.HandleZeroHp");
            record.PlanetId = __instance.astroId;
            record.ObjectKey = BaselineWorld.ObjectKeyText(KindOf(__instance.objectType), __instance.originAstroId, __instance.objectId, -1);
            record.RawSlot = BaselineWorld.RawSlotText(__instance.id, 0);
            record.HpAfter = __instance.hp;
            record.Reason = "HandleZeroHp";
            Emit(record);
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    // ---------------- Damage entries: who applied what, to which object ------------------------

    private static bool ShouldLogDamage(string kind, int scopeId, int nativeId)
    {
        if (BaselineRuntime.Verbose) return true;
        return BaselineRuntime.FindTracked(kind, scopeId, nativeId) != null;
    }

    private static void DamageObject_Prefix(int damage, int slice, ref SkillTarget target, ref SkillTarget caster)
    {
        try
        {
            var kind = KindOf((int)target.type);
            if (!ShouldLogDamage(kind, target.astroId, target.id)) return;
            var record = BaselineRuntime.Log.New("damage.entry", "SkillSystem.DamageObject");
            record.PlanetId = target.astroId;
            record.ObjectKey = BaselineWorld.ObjectKeyText(kind, target.astroId, target.id, -1);
            record.Owner = BaselineWorld.CasterText(caster);
            record.Note = "damage=" + damage + " slice=" + slice;
            record.Reason = "DamageObject";
            var snapshot = BaselineRuntime.FindTracked(kind, target.astroId, target.id);
            if (snapshot != null)
            {
                var resolved = BaselineWorld.Resolve(snapshot);
                if (resolved.Resolved) record.HpBefore = resolved.Hp;
            }
            Emit(record);
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    private static void DamageGroundLocal_Prefix(PlanetFactory factory, int damage, int slice,
        ref SkillTargetLocal target, ref SkillTargetLocal caster)
    {
        try
        {
            var planetId = factory?.planetId ?? -1;
            var kind = KindOf((int)target.type);
            if (!ShouldLogDamage(kind, planetId, target.id)) return;
            var record = BaselineRuntime.Log.New("damage.ground.local", "SkillSystem.DamageGroundObjectByLocalCaster");
            record.PlanetId = planetId;
            record.ObjectKey = BaselineWorld.ObjectKeyText(kind, planetId, target.id, -1);
            record.Owner = BaselineWorld.CasterText(caster);
            record.Note = "damage=" + damage + " slice=" + slice;
            record.Reason = "DamageGroundObjectByLocalCaster";
            var tracked = BaselineRuntime.FindTracked(kind, planetId, target.id);
            if (tracked != null)
            {
                var resolved = BaselineWorld.Resolve(tracked);
                if (resolved.Resolved)
                {
                    record.HpBefore = resolved.Hp;
                    record.RawSlot = resolved.RawSlot;
                    record.RepairerBefore = resolved.RepairerCount;
                }
            }
            Emit(record);
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    private static void DamageGroundRemote_Prefix(PlanetFactory factory, int damage, int slice,
        ref SkillTargetLocal target, ref SkillTargetLocal caster)
    {
        try
        {
            var planetId = factory?.planetId ?? -1;
            var kind = KindOf((int)target.type);
            if (!ShouldLogDamage(kind, planetId, target.id)) return;
            var record = BaselineRuntime.Log.New("damage.ground.remote", "SkillSystem.DamageGroundObjectByRemoteCaster");
            record.PlanetId = planetId;
            record.ObjectKey = BaselineWorld.ObjectKeyText(kind, planetId, target.id, -1);
            record.Owner = BaselineWorld.CasterText(caster);
            record.Note = "damage=" + damage + " slice=" + slice;
            record.Reason = "DamageGroundObjectByRemoteCaster";
            Emit(record);
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    // ---------------- Construction: damage records and the repair chain ------------------------

    private static void AddConstructStat_Postfix(ConstructionSystem __instance, int entityId, int damage, int __result)
    {
        try
        {
            var record = BaselineRuntime.Log.New("construct.add", "ConstructionSystem.AddConstructStat");
            record.PlanetId = __instance.planet?.id ?? -1;
            record.ObjectKey = BaselineWorld.ObjectKeyText("entity", record.PlanetId, entityId, -1);
            record.RawSlot = BaselineWorld.RawSlotText(0, __result);
            record.RepairerAfter = 0;
            record.Reason = "AddConstructStat";
            record.Note = "damage=" + damage + " newConstructStatId=" + __result;
            var tracked = BaselineRuntime.FindTracked("entity", record.PlanetId, entityId);
            if (tracked != null)
            {
                var resolved = BaselineWorld.Resolve(tracked);
                if (resolved.Resolved)
                {
                    record.HpBefore = resolved.Hp;
                    record.RepairerBefore = resolved.RepairerCount;
                }
            }
            Emit(record);
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    private static void RemoveConstructStat_Prefix(ConstructionSystem __instance, int constructStatId)
    {
        try
        {
            var stats = __instance.constructStats;
            if (stats?.buffer == null || constructStatId <= 0 || constructStatId >= stats.buffer.Length) return;
            ref var stat = ref stats.buffer[constructStatId];
            if (stat.id != constructStatId) return;
            var record = BaselineRuntime.Log.New("construct.remove", "ConstructionSystem.RemoveConstructStat");
            record.PlanetId = __instance.planet?.id ?? -1;
            record.ObjectKey = BaselineWorld.ObjectKeyText("entity", record.PlanetId, stat.entityId, -1);
            record.RawSlot = BaselineWorld.RawSlotText(0, constructStatId);
            record.RepairerBefore = stat.repairerCount;
            record.Reason = "RemoveConstructStat";
            record.Note = "damageRegister=" + stat.damageRegister + " damageRate=" + stat.damageRate.ToString("F2");
            Emit(record);
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    private static void Repair_Prefix(ConstructionSystem __instance, int entityId, float ratio, long time)
    {
        try
        {
            repairProbe = default;
            var factory = __instance.factory;
            if (factory?.entityPool == null || entityId <= 0 || entityId >= factory.entityPool.Length) return;
            ref var entity = ref factory.entityPool[entityId];
            if (entity.id != entityId) return;
            var probe = new RepairProbe
            {
                Active = true,
                EntityId = entityId,
                CombatStatId = entity.combatStatId,
                ConstructStatId = entity.constructStatId
            };
            var stats = factory.skillSystem?.combatStats;
            if (stats?.buffer != null && entity.combatStatId > 0 && entity.combatStatId < stats.buffer.Length &&
                stats.buffer[entity.combatStatId].id == entity.combatStatId)
                probe.HpBefore = stats.buffer[entity.combatStatId].hp;
            var constructStats = __instance.constructStats;
            if (constructStats?.buffer != null && entity.constructStatId > 0 &&
                entity.constructStatId < constructStats.buffer.Length &&
                constructStats.buffer[entity.constructStatId].id == entity.constructStatId)
                probe.RepairerBefore = constructStats.buffer[entity.constructStatId].repairerCount;
            repairProbe = probe;
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    private static void Repair_Postfix(ConstructionSystem __instance, int entityId, float ratio, long time, bool __result)
    {
        try
        {
            var probe = repairProbe;
            repairProbe = default;
            if (!probe.Active || probe.EntityId != entityId) return;
            var factory = __instance.factory;
            var record = BaselineRuntime.Log.New("repair.tick", "ConstructionSystem.Repair");
            record.PlanetId = __instance.planet?.id ?? -1;
            record.ObjectKey = BaselineWorld.ObjectKeyText("entity", record.PlanetId, entityId, -1);
            record.RawSlot = BaselineWorld.RawSlotText(probe.CombatStatId, probe.ConstructStatId);
            record.HpBefore = probe.HpBefore;
            record.RepairerBefore = probe.RepairerBefore;
            var stats = factory?.skillSystem?.combatStats;
            if (stats?.buffer != null && probe.CombatStatId > 0 && probe.CombatStatId < stats.buffer.Length &&
                stats.buffer[probe.CombatStatId].id == probe.CombatStatId)
                record.HpAfter = stats.buffer[probe.CombatStatId].hp;
            var constructStats = __instance.constructStats;
            if (constructStats?.buffer != null && probe.ConstructStatId > 0 &&
                probe.ConstructStatId < constructStats.buffer.Length &&
                constructStats.buffer[probe.ConstructStatId].id == probe.ConstructStatId)
            {
                ref var stat = ref constructStats.buffer[probe.ConstructStatId];
                record.RepairerAfter = stat.repairerCount;
                record.Owner = stat.repairerModuleId == -1 ? "mecha:-1" : "module:" + stat.repairerModuleId;
            }
            record.Reason = __result ? "RepairReturnedTrue" : "RepairReturnedFalse";
            record.Note = "ratio=" + ratio.ToString("F3") + " time=" + time;
            Emit(record);
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    private static void DetermineLaunch_Prefix(ConstructionSystem __instance)
    {
        try
        {
            if (BaselineRuntime.TrackedSnapshot().Length == 0)
            {
                launchProbe = default;
                return;
            }
            var probe = new LaunchProbe
            {
                Active = true,
                Drones = new Dictionary<int, DroneState>(),
                RepairerCounts = new Dictionary<int, int>()
            };
            var drones = __instance.drones;
            if (drones?.buffer != null)
            {
                var limit = Math.Min(drones.buffer.Length, 4096);
                for (var i = 1; i < limit; i++)
                {
                    ref var drone = ref drones.buffer[i];
                    if (drone.id != i) continue;
                    probe.Drones[i] = new DroneState { Stage = drone.stage, Target = drone.targetObjectId, Owner = drone.owner };
                }
            }
            var stats = __instance.constructStats;
            if (stats?.buffer != null)
            {
                var limit = Math.Min(stats.buffer.Length, stats.cursor);
                for (var i = 1; i < limit; i++)
                {
                    ref var stat = ref stats.buffer[i];
                    if (stat.id != i) continue;
                    probe.RepairerCounts[i] = stat.repairerCount;
                }
            }
            launchProbe = probe;
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    private static void DetermineLaunch_Postfix(ConstructionSystem __instance)
    {
        try
        {
            var probe = launchProbe;
            launchProbe = default;
            if (!probe.Active) return;
            var planetId = __instance.planet?.id ?? -1;
            var drones = __instance.drones;
            if (drones?.buffer != null)
            {
                var limit = Math.Min(drones.buffer.Length, 4096);
                for (var i = 1; i < limit; i++)
                {
                    ref var drone = ref drones.buffer[i];
                    if (drone.id != i) continue;
                    if (probe.Drones.TryGetValue(i, out var before))
                    {
                        if (before.Stage == drone.stage && before.Target == drone.targetObjectId) continue;
                    }
                    var record = BaselineRuntime.Log.New("repair.launch", "ConstructionSystem.DetermineLaunch");
                    record.PlanetId = planetId;
                    record.ObjectKey = BaselineWorld.ObjectKeyText("drone", planetId, drone.id, -1);
                    record.Owner = drone.owner == 0 ? "mecha:0" : "module:" + drone.owner;
                    record.Reason = probe.Drones.ContainsKey(i) ? "DroneStateChanged" : "DroneCreated";
                    record.Note = "stage=" + drone.stage + " target=" + drone.targetObjectId +
                                  " priority=" + drone.priority;
                    if (drone.targetObjectId > 0)
                    {
                        var tracked = BaselineRuntime.FindTracked("entity", planetId, drone.targetObjectId);
                        if (tracked != null)
                        {
                            var resolved = BaselineWorld.Resolve(tracked);
                            if (resolved.Resolved)
                            {
                                record.HpBefore = resolved.Hp;
                                record.RepairerAfter = resolved.RepairerCount;
                                record.RawSlot = resolved.RawSlot;
                            }
                        }
                    }
                    Emit(record);
                }
            }
            var stats = __instance.constructStats;
            if (stats?.buffer != null)
            {
                var limit = Math.Min(stats.buffer.Length, stats.cursor);
                for (var i = 1; i < limit; i++)
                {
                    ref var stat = ref stats.buffer[i];
                    if (stat.id != i) continue;
                    if (!probe.RepairerCounts.TryGetValue(i, out var before) || before == stat.repairerCount) continue;
                    var record = BaselineRuntime.Log.New("repair.count", "ConstructionSystem.DetermineLaunch");
                    record.PlanetId = planetId;
                    record.ObjectKey = BaselineWorld.ObjectKeyText("entity", planetId, stat.entityId, -1);
                    record.RawSlot = BaselineWorld.RawSlotText(0, i);
                    record.RepairerBefore = before;
                    record.RepairerAfter = stat.repairerCount;
                    record.Owner = stat.repairerModuleId == -1 ? "mecha:-1" : "module:" + stat.repairerModuleId;
                    record.Reason = "DetermineLaunchReservation";
                    Emit(record);
                }
            }
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    private static void UpdateModules_Prefix(ConstructionSystem __instance, long time, float dt)
    {
        try
        {
            if (BaselineRuntime.TrackedSnapshot().Length == 0)
            {
                launchProbe = default;
                return;
            }
            var probe = new LaunchProbe { Active = true, RepairerCounts = new Dictionary<int, int>() };
            var stats = __instance.constructStats;
            if (stats?.buffer != null)
            {
                var limit = Math.Min(stats.buffer.Length, stats.cursor);
                for (var i = 1; i < limit; i++)
                {
                    ref var stat = ref stats.buffer[i];
                    if (stat.id != i) continue;
                    probe.RepairerCounts[i] = stat.repairerCount;
                }
            }
            launchProbe = probe;
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    private static void UpdateModules_Postfix(ConstructionSystem __instance, long time, float dt)
    {
        try
        {
            var probe = launchProbe;
            launchProbe = default;
            if (!probe.Active || probe.RepairerCounts == null) return;
            var stats = __instance.constructStats;
            if (stats?.buffer == null) return;
            var planetId = __instance.planet?.id ?? -1;
            var limit = Math.Min(stats.buffer.Length, stats.cursor);
            for (var i = 1; i < limit; i++)
            {
                ref var stat = ref stats.buffer[i];
                if (stat.id != i) continue;
                if (!probe.RepairerCounts.TryGetValue(i, out var before) || before == stat.repairerCount) continue;
                var record = BaselineRuntime.Log.New("repair.count", "ConstructionSystem.UpdateModules");
                record.PlanetId = planetId;
                record.ObjectKey = BaselineWorld.ObjectKeyText("entity", planetId, stat.entityId, -1);
                record.RawSlot = BaselineWorld.RawSlotText(0, i);
                record.RepairerBefore = before;
                record.RepairerAfter = stat.repairerCount;
                record.Owner = stat.repairerModuleId == -1 ? "mecha:-1" : "module:" + stat.repairerModuleId;
                record.Reason = stat.repairerCount == 0 ? "UpdateModulesReset" : "UpdateModulesCountChange";
                record.Note = "damageRate=" + stat.damageRate.ToString("F3");
                Emit(record);
            }
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    private static void ConstructStatGameTick_Prefix(ConstructStat __instance, PlanetFactory factory, ConstructionSystem constructionSystem)
    {
        try
        {
            if (!Watching(watchedConstructStats, __instance.id)) return;
            var memory = ConstructMemory.GetOrAdd(__instance.id, _ => new StatRepairMemory());
            if (memory.EntityId != __instance.entityId)
            {
                memory.EntityId = __instance.entityId;
                memory.RepairerCount = __instance.repairerCount;
                return;
            }
            memory.RepairerCount = __instance.repairerCount;
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    private static void ConstructStatGameTick_Postfix(ConstructStat __instance, PlanetFactory factory, ConstructionSystem constructionSystem)
    {
        try
        {
            if (!Watching(watchedConstructStats, __instance.id)) return;
            if (!ConstructMemory.TryGetValue(__instance.id, out var memory)) return;
            if (memory.EntityId != __instance.entityId) return;
            if (memory.RepairerCount == __instance.repairerCount) return;
            var record = BaselineRuntime.Log.New("repair.count", "ConstructStat.GameTick");
            record.PlanetId = factory?.planetId ?? -1;
            record.ObjectKey = BaselineWorld.ObjectKeyText("entity", record.PlanetId, __instance.entityId, -1);
            record.RawSlot = BaselineWorld.RawSlotText(0, __instance.id);
            record.RepairerBefore = memory.RepairerCount;
            record.RepairerAfter = __instance.repairerCount;
            record.Owner = __instance.repairerModuleId == -1 ? "mecha:-1" : "module:" + __instance.repairerModuleId;
            record.Reason = "ConstructStatGameTick";
            record.Note = "damageRate=" + __instance.damageRate.ToString("F3") +
                          " damageRegister=" + __instance.damageRegister;
            Emit(record);
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    // ---------------- Session lifecycle (scenario 3) -------------------------------------------

    private static void OnPlayerJoined_Postfix(INebulaPlayer player)
    {
        try
        {
            var record = BaselineRuntime.Log.New("session.join", "SimulatedWorld.OnPlayerJoinedGame");
            record.Owner = player == null ? "unknown" : "player:" + player.Id;
            record.Reason = "PlayerJoined";
            record.Note = player?.Data == null
                ? "no player data"
                : "username=" + player.Data.Username + " planet=" + player.Data.LocalPlanetId;
            record.PlanetId = player?.Data?.LocalPlanetId ?? -1;
            Emit(record);
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    private static void OnPlayerLeft_Postfix(INebulaPlayer player)
    {
        try
        {
            var record = BaselineRuntime.Log.New("session.leave", "SimulatedWorld.OnPlayerLeftGame");
            record.Owner = player == null ? "unknown" : "player:" + player.Id;
            record.Reason = "PlayerLeft";
            record.Note = player?.Data == null
                ? "no player data"
                : "username=" + player.Data.Username + " planet=" + player.Data.LocalPlanetId;
            record.PlanetId = player?.Data?.LocalPlanetId ?? -1;
            Emit(record);
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }
}
