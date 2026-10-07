using System;
using HarmonyLib;
using NebulaWorld.Combat;

namespace NebulaPatcher.Patches.Authority;

[HarmonyPatch]
internal static class CombatTargetScope_Patch
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(LocalGeneralProjectile), nameof(LocalGeneralProjectile.TickSkillLogic))]
    [HarmonyPatch(typeof(LocalLaserOneShot), nameof(LocalLaserOneShot.TickSkillLogic))]
    [HarmonyPatch(typeof(LocalLaserContinuous), nameof(LocalLaserContinuous.TickSkillLogic))]
    public static void Local_Prefix(SkillSystem skillSystem, SkillTargetLocal ___target, out PlayerSkillScope __state)
    {
        __state = new PlayerSkillScope(skillSystem);
        CombatTargetContext.TargetMecha = ___target.type == ETargetType.Player && CombatTargetContext.IsPlayerTarget(___target.id)
            ? CombatTargetContext.ResolveDamageMecha(GameMain.spaceSector.skillSystem, ___target.id) : null;
    }

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(GeneralProjectile), nameof(GeneralProjectile.TickSkillLogic))]
    [HarmonyPatch(typeof(GeneralExpImpProjectile), nameof(GeneralExpImpProjectile.TickSkillLogic))]
    [HarmonyPatch(typeof(GeneralMissile), nameof(GeneralMissile.TickSkillLogic))]
    [HarmonyPatch(typeof(SpaceLaserOneShot), nameof(SpaceLaserOneShot.TickSkillLogic))]
    public static void Space_Prefix(SkillSystem skillSystem, SkillTarget ___target, out PlayerSkillScope __state)
    {
        __state = new PlayerSkillScope(skillSystem);
        CombatTargetContext.TargetMecha = ___target.type == ETargetType.Player && CombatTargetContext.IsPlayerTarget(___target.id)
            ? CombatTargetContext.ResolveDamageMecha(GameMain.spaceSector.skillSystem, ___target.id) : null;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(LocalGeneralProjectile), nameof(LocalGeneralProjectile.TickSkillLogic))]
    [HarmonyPatch(typeof(LocalLaserOneShot), nameof(LocalLaserOneShot.TickSkillLogic))]
    [HarmonyPatch(typeof(LocalLaserContinuous), nameof(LocalLaserContinuous.TickSkillLogic))]
    [HarmonyPatch(typeof(GeneralProjectile), nameof(GeneralProjectile.TickSkillLogic))]
    [HarmonyPatch(typeof(GeneralExpImpProjectile), nameof(GeneralExpImpProjectile.TickSkillLogic))]
    [HarmonyPatch(typeof(GeneralMissile), nameof(GeneralMissile.TickSkillLogic))]
    [HarmonyPatch(typeof(SpaceLaserOneShot), nameof(SpaceLaserOneShot.TickSkillLogic))]
    public static Exception Finalizer(PlayerSkillScope __state, Exception __exception)
    {
        __state.Restore();
        return __exception;
    }
}
