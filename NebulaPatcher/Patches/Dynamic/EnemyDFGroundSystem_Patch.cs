#region

using System;
using HarmonyLib;
using NebulaModel.Authority;
using NebulaModel.Logger;
using NebulaWorld;

#endregion

namespace NebulaPatcher.Patches.Dynamic;

[HarmonyPatch(typeof(EnemyDFGroundSystem))]
internal class EnemyDFGroundSystem_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.ExecuteDeferredEnemyChange))]
    public static bool ExecuteDeferredEnemyChange_Prefix(EnemyDFGroundSystem __instance)
    {
        if (!Multiplayer.IsActive) return true;

        if (Multiplayer.Session.IsClient)
        {
            // Wait for server to authorize
            __instance._rmv_id_list?.Clear();
            __instance._add_bidx_list?.Clear();
            return false;
        }

        if (__instance._rmv_id_list?.Count > 0)
        {
            foreach (var enemyId in __instance._rmv_id_list)
            {
                __instance.factory.RemoveEnemyFinal(enemyId);
            }
            __instance._rmv_id_list.Clear();
        }
        if (__instance._add_bidx_list?.Count > 0)
        {
            foreach (var (baseId, builderIndex) in __instance._add_bidx_list)
            {
                __instance.factory.CreateEnemyFinal(baseId, builderIndex);
            }
            __instance._add_bidx_list.Clear();
        }
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.InitiateUnitDeferred))]
    public static bool InitiateUnitDeferred_Prefix()
    {
        // Skip InitiateUnit in multiplayer game
        return !Multiplayer.IsActive;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.ExecuteDeferredUnitFormation))]
    public static bool ExecuteDeferredUnitFormation_Prefix(EnemyDFGroundSystem __instance)
    {
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsClient)
        {
            // Wait for server to authorize
            __instance._initiate_unit_list?.Clear();
            __instance._activate_unit_list?.Clear();
            __instance._deactivate_unit_list?.Clear();
            return false;
        }

        // Skip InitiateUnit in multiplayer game
        __instance._initiate_unit_list?.Clear();
        if (__instance._activate_unit_list?.Count > 0)
        {
            foreach (var item in __instance._activate_unit_list)
            {
                int unitId = __instance.ActivateUnit(item.baseId, item.formId, item.port, item.gameTick);
                if (unitId > 0)
                {
                    ref var ptr = ref __instance.units.buffer[unitId];
                    if (ptr.id == unitId)
                    {
                        ptr.stateTick = item.stateTick;
                        ptr.behavior = item.behavior;
                    }
                }
            }
            __instance._activate_unit_list.Clear();
        }
        if (__instance._deactivate_unit_list?.Count > 0)
        {
            foreach (var unitId in __instance._deactivate_unit_list)
            {
                __instance.DeactivateUnit(unitId);
            }
            __instance._deactivate_unit_list.Clear();
        }
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.NotifyEnemyKilled))]
    public static bool NotifyEnemyKilled_Prefix()
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        // Wait for server to authorize
        return Multiplayer.Session.Combat.IsIncomingRequest;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.NotifyBaseRemoving))]
    public static bool NotifyBaseRemoving_Prefix()
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        // Wait for DFRelayLeaveBasePacket from server
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.PostKeyTick))]
    public static bool PostKeyTick_Prefix()
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        // Don't remove unit in formation because it may still waiting for server
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.RemoveBasePit))]
    public static bool RemoveBasePit_Prefix(EnemyDFGroundSystem __instance, int pitRuinId)
    {
        if (!Multiplayer.IsActive) return true;

        var buffer = __instance.bases.buffer;
        var cursor = __instance.bases.cursor;
        for (var baseId = 1; baseId < cursor; baseId++)
        {
            var dfgbaseComponent = buffer[baseId];
            if (dfgbaseComponent?.id == baseId && dfgbaseComponent.ruinId == pitRuinId)
            {
                if (Multiplayer.Session.IsServer)
                {
                    return true;
                }
                else
                {
                    // Client waits for the host's world state.
                    return false;
                }
            }
        }
        // Is it possible to go here in vanilla?
        __instance.factory.RemoveRuinWithComponet(pitRuinId);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.RemoveDFGBaseComponent))]
    public static bool RemoveDFGBaseComponent_Prefix(EnemyDFGroundSystem __instance, int id)
    {
        if (!Multiplayer.IsActive) return true;

        if (Multiplayer.Session.IsServer)
        {
            return true;
        }

        // Client should wait for server to approve the removal of base from the base buffer
        return Multiplayer.Session.Combat.IsIncomingRequest;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.KeyTickLogic))]
    public static void KeyTickLogic_Prefix(EnemyDFGroundSystem __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return;

        // Fix NRE in EnemyDFGroundSystem.KeyTickLogic (System.Int64 time);(IL_0929)
        var cursor = __instance.builders.cursor;
        var buffer = __instance.builders.buffer;
        var baseBuffer = __instance.bases.buffer;
        var enemyPool = __instance.factory.enemyPool;
        for (var builderId = 1; builderId < cursor; builderId++)
        {
            ref var builder = ref buffer[builderId];
            if (builder.id == builderId)
            {
                if (baseBuffer[enemyPool[builder.enemyId].owner] == null)
                {
                    var msg = string.Format("Remove EnemyDFGroundSystem enemy[{0}]: owner = {1}".Translate(), builder.enemyId, enemyPool[builder.enemyId].owner);
                    Log.WarnInform(msg);

                    __instance.factory.enemyPool[builder.enemyId].SetEmpty();
                    __instance.builders.Remove(builderId);
                }
            }
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.GameTickLogic_Unit))]
    public static bool GameTickLogic_Unit_Prefix()
    {
        // I01: migrated ground-unit AI (serial). Host runs the real AI; a client outside a replica
        // apply must not run it at all. Pose/state arrive via the replica instead. Legacy rooms and
        // single-player run vanilla (the guard allows when not in authority mode).
        return AuthorityRuleGuard.AllowHostRule(
            AuthorityHookLabels.EnemyDFGroundSystemGameTickLogicUnit,
            detail: "ground-unit-ai");
    }

}
