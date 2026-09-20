using HarmonyLib;
using NebulaWorld;

namespace NebulaPatcher.Patches.Dynamic;

[HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.AddCraftData))]
internal static class GroundCraftVisualGeneration_Patch
{
    [HarmonyPostfix]
    public static void Postfix(PlanetFactory __instance, int __result)
    {
        if (Multiplayer.IsActive) Multiplayer.Session.BattleVisuals.CraftCreated(__instance.planetId, __result);
    }
}

[HarmonyPatch(typeof(SpaceSector), nameof(SpaceSector.AddCraftData))]
internal static class SpaceCraftVisualGeneration_Patch
{
    [HarmonyPostfix]
    public static void Postfix(int __result)
    {
        if (Multiplayer.IsActive) Multiplayer.Session.BattleVisuals.CraftCreated(0, __result);
    }
}

[HarmonyPatch(typeof(SkillSystem))]
internal static class AuthoritativeAttackRendering_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(SkillSystem.UpdateTurretMissileRenderingData))]
    public static void Missiles_Postfix(SkillSystem __instance)
    {
        if (UseReplica(__instance)) __instance.turretMissileRenderer.instCursor = 0;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(SkillSystem.UpdateLancerLaserSweepRenderingData))]
    public static void Sweeps_Postfix(SkillSystem __instance)
    {
        if (UseReplica(__instance)) __instance.lancerLaserSweepRenderer.instCursor = 0;
    }

    private static bool UseReplica(SkillSystem system) => Multiplayer.IsActive && Multiplayer.Session.IsClient &&
        ReferenceEquals(system, GameMain.spaceSector?.skillSystem);
}

[HarmonyPatch(typeof(DataPoolRenderer<GeneralProjectile>), nameof(DataPoolRenderer<GeneralProjectile>.Render))]
internal static class AuthoritativePlasmaRendering_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(DataPoolRenderer<GeneralProjectile> __instance) => !Multiplayer.IsActive ||
        Multiplayer.Session.IsServer || !ReferenceEquals(__instance, GameMain.spaceSector?.skillSystem?.turretPlasmas);
}

[HarmonyPatch(typeof(DataPoolRenderer<GeneralExpImpProjectile>), nameof(DataPoolRenderer<GeneralExpImpProjectile>.Render))]
internal static class AuthoritativeBomberRendering_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(DataPoolRenderer<GeneralExpImpProjectile> __instance) => !Multiplayer.IsActive ||
        Multiplayer.Session.IsServer || !ReferenceEquals(__instance, GameMain.spaceSector?.skillSystem?.humpbackProjectiles);
}
