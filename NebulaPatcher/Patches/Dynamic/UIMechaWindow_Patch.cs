#region

using HarmonyLib;
using NebulaModel.Authority;

#endregion

namespace NebulaPatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIMechaWindow))]
internal class UIMechaWindow_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIMechaWindow.OnReplaceFuelButtonClick))]
    public static bool OnReplaceFuelButtonClick_Prefix()
    {
        return AuthorityRuleGuard.AllowHostRule(
            AuthorityHookLabels.UIMechaWindowOnReplaceFuelButtonClick,
            detail: "fuel-ui");
    }
}
