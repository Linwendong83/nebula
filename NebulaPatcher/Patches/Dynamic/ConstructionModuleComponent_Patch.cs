using HarmonyLib;
using NebulaWorld;
using NebulaWorld.Factory;

namespace NebulaPatcher.Patches.Dynamic;

[HarmonyPatch(typeof(ConstructionModuleComponent))]
internal class ConstructionModuleComponent_Patch
{
    [System.ThreadStatic]
    private static PlanetFactory currentFactory;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionModuleComponent.SearchBuildTargets))]
    public static void SearchBuildTargets_Prefix(PlanetFactory factory, out PlanetFactory __state)
    {
        __state = currentFactory;
        currentFactory = factory;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(ConstructionModuleComponent.SearchBuildTargets))]
    public static void SearchBuildTargets_Postfix(PlanetFactory __state) => currentFactory = __state;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionModuleComponent.InsertTmpBuildTarget))]
    public static bool InsertTmpBuildTarget_Prefix(ConstructionModuleComponent __instance, int objectId)
    {
        return !Multiplayer.IsActive ||
               Multiplayer.Session.BuildDispatch.CanQueue(currentFactory, __instance, objectId);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionModuleComponent.InsertBuildTarget))]
    public static bool InsertBuildTarget_Prefix(ConstructionModuleComponent __instance, PrebuildData[] prebuildPool,
        int objectId)
    {
        if (!Multiplayer.IsActive) return true;
        var factory = currentFactory ?? BuildDispatchManager.FindFactory(prebuildPool);
        return Multiplayer.Session.BuildDispatch.CanQueue(factory, __instance, objectId);
    }
}
