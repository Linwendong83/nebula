#region

using HarmonyLib;
using NebulaModel.Authority;
using NebulaWorld;

#endregion

namespace NebulaPatcher.Patches.Dynamic;

[HarmonyPatch(typeof(CombatStat))]
internal class CombatStat_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(CombatStat.HandleZeroHp))]
    public static bool HandleZeroHp_Prefix(ref CombatStat __instance)
    {
        if (!AuthorityRuleGuard.AllowHostRule(AuthorityHookLabels.CombatStatHandleZeroHp,
                detail: $"objectType={__instance.objectType} objectId={__instance.objectId}"))
        {
            return false;
        }

        if (Multiplayer.IsActive &&
            Multiplayer.Session?.AuthorityRuntime?.IsHostAuthority == true)
        {
            Multiplayer.Session.AuthorityRuntime.HostDeathCapture?.Capture(
                __instance.originAstroId, __instance.objectType, __instance.objectId);
        }

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(CombatStat.TickSkillLogic))]
    public static bool TickSkillLogic_Prefix(ref CombatStat __instance)
    {
        // A05: a client that ran the vanilla tick would keep a second regen/death model alive. The
        // guard refuses the whole call outside a replica apply, so the client's health mirror can
        // only ever move because the host said so.
        if (!AuthorityRuleGuard.AllowHostRule(AuthorityHookLabels.CombatStatTickSkillLogic,
                detail: $"objectType={__instance.objectType} objectId={__instance.objectId}"))
        {
            return false;
        }

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(CombatStat.HandleFullHp))]
    public static bool HandleFullHp_Prefix(ref CombatStat __instance)
    {
        if (!AuthorityRuleGuard.AllowHostRule(AuthorityHookLabels.CombatStatHandleFullHp,
                detail: $"objectType={__instance.objectType} objectId={__instance.objectId}"))
        {
            return false;
        }

        return true;
    }
}
