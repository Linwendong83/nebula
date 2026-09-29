#region

using HarmonyLib;
using NebulaWorld;

#endregion

namespace NebulaPatcher.Patches.Dynamic;

[HarmonyPatch(typeof(ConstructionSystem))]
internal class ConstructionSystem_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionSystem.UpdateDrones))]
    public static void UpdateDrones(ConstructionSystem __instance, ObjectRenderer[] renderers, bool sync_gpu_inst, float dt, long time)
    {
        if (!Multiplayer.IsActive) return;

        // Update remote drones from other players
        var factory = __instance.factory;
        Multiplayer.Session.Drones.UpdateDrones(factory, renderers, sync_gpu_inst, dt, time);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionSystem.AddBuildTargetToModules))]
    public static bool AddBuildTargetToModules_Prefix(ConstructionSystem __instance, int objectId)
    {
        if (!Multiplayer.IsActive) return true;
        Multiplayer.Session.BuildDispatch.ReadyLocally(__instance.factory, objectId);
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(ConstructionSystem.ResetDroneTargets))]
    public static void ResetDroneTargets_Postfix(ConstructionSystem __instance, ref DroneComponent drone)
    {
        if (!Multiplayer.IsActive) return;
        if (drone.owner < 0 || drone.owner >= __instance.constructionModules.cursor) return;
        var module = drone.owner == 0 ? GameMain.mainPlayer.mecha.constructionModule :
            __instance.constructionModules.buffer[drone.owner];
        if (module == null) return;
        // Reset clears every carried target and then queues it again. Keep the claim while this
        // module still has the prebuild queued, so a finished drone does not hand it to someone else.
        ReleaseIfAbandoned(__instance, module, drone.targetObjectId);
        ReleaseIfAbandoned(__instance, module, drone.nextTarget1ObjectId);
        ReleaseIfAbandoned(__instance, module, drone.nextTarget2ObjectId);
        ReleaseIfAbandoned(__instance, module, drone.nextTarget3ObjectId);
    }

    private static void ReleaseIfAbandoned(ConstructionSystem system, ConstructionModuleComponent module, int objectId)
    {
        if (objectId >= 0 || ModuleStillTargets(module, -objectId)) return;
        Multiplayer.Session.BuildDispatch.ReleaseLocalTarget(system.factory, -objectId, module);
    }

    private static bool ModuleStillTargets(ConstructionModuleComponent module, int prebuildId)
    {
        var targets = module.buildTargets;
        if (targets == null) return false;
        for (var i = 0; i < targets.Length; i++)
            if (targets[i].objectId == prebuildId) return true;
        return false;
    }
}
