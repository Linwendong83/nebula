using System;
using HarmonyLib;
using NebulaWorld;

namespace NebulaPatcher.Patches.Dynamic;

[HarmonyPatch]
internal static class BattleImpact_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GeneralMissile), nameof(GeneralMissile.TickSkillLogic))]
    public static void Missile_Prefix(ref GeneralMissile __instance, SkillSystem skillSystem, out int[] __state)
    {
        __state = Begin(__instance.caster.type == ETargetType.None, skillSystem);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GeneralProjectile), nameof(GeneralProjectile.TickSkillLogic))]
    public static void Projectile_Prefix(ref GeneralProjectile __instance, SkillSystem skillSystem, out int[] __state)
    {
        __state = Begin(__instance.caster.type == ETargetType.None, skillSystem);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SpaceLaserSweep), nameof(SpaceLaserSweep.TickSkillLogic))]
    public static void Sweep_Prefix(ref SpaceLaserSweep __instance, SkillSystem skillSystem, out int[] __state)
    {
        __state = Begin(__instance.caster.type == ETargetType.Enemy, skillSystem);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GeneralExpImpProjectile), nameof(GeneralExpImpProjectile.TickSkillLogic))]
    public static void Bomber_Prefix(ref GeneralExpImpProjectile __instance, SkillSystem skillSystem, out int[] __state)
    {
        __state = Begin(__instance.caster.type == ETargetType.Enemy, skillSystem);
    }

    private static int[] Begin(bool worldAttack, SkillSystem skills) => worldAttack && Multiplayer.IsActive &&
        Multiplayer.Session.IsGameLoaded ? Multiplayer.Session.Impacts.Begin(skills) : null;

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(GeneralMissile), nameof(GeneralMissile.TickSkillLogic))]
    [HarmonyPatch(typeof(GeneralProjectile), nameof(GeneralProjectile.TickSkillLogic))]
    [HarmonyPatch(typeof(SpaceLaserSweep), nameof(SpaceLaserSweep.TickSkillLogic))]
    [HarmonyPatch(typeof(GeneralExpImpProjectile), nameof(GeneralExpImpProjectile.TickSkillLogic))]
    public static Exception Finalizer(SkillSystem skillSystem, int[] __state, Exception __exception)
    {
        if (__state != null && Multiplayer.IsActive) Multiplayer.Session.Impacts.End(skillSystem, __state);
        return __exception;
    }
}

[HarmonyPatch(typeof(DataPoolRenderer<ParticleData>), nameof(DataPoolRenderer<ParticleData>.Render))]
internal static class PredictedWorldImpactRendering_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(DataPoolRenderer<ParticleData> __instance) => !Multiplayer.IsActive ||
        !Multiplayer.Session.IsClient || !Multiplayer.Session.IsGameLoaded || !Multiplayer.Session.Impacts.RenderNative(__instance);
}
