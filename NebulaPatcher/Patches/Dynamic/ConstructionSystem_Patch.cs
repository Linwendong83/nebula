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

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionSystem.ResetDroneTargets))]
    public static void ResetDroneTargets_Prefix(ConstructionSystem __instance, ref DroneComponent drone)
    {
        if (!Multiplayer.IsActive) return;
        if (drone.owner < 0 || drone.owner >= __instance.constructionModules.cursor) return;
        var module = drone.owner == 0 ? GameMain.mainPlayer.mecha.constructionModule :
            __instance.constructionModules.buffer[drone.owner];
        if (module == null) return;
        if (drone.targetObjectId < 0)
            Multiplayer.Session.BuildDispatch.ReleaseLocalTarget(__instance.factory, -drone.targetObjectId, module);
        if (drone.nextTarget1ObjectId < 0)
            Multiplayer.Session.BuildDispatch.ReleaseLocalTarget(__instance.factory, -drone.nextTarget1ObjectId, module);
        if (drone.nextTarget2ObjectId < 0)
            Multiplayer.Session.BuildDispatch.ReleaseLocalTarget(__instance.factory, -drone.nextTarget2ObjectId, module);
        if (drone.nextTarget3ObjectId < 0)
            Multiplayer.Session.BuildDispatch.ReleaseLocalTarget(__instance.factory, -drone.nextTarget3ObjectId, module);
    }
}
