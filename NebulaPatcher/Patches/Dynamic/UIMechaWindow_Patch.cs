#region

using HarmonyLib;
using NebulaModel.Authority;

#endregion

namespace NebulaPatcher.Patches.Dynamic;

/// <summary>
/// I01: local fuel UI writes <c>Mecha.reactorEnergy</c> outside any host ledger transaction.
/// </summary>
/// <remarks>
/// The host runs it (the host is the fuel authority); a client outside a replica apply is refused,
/// so fuel via UI on a client is unavailable in authority mode until a host command path exists.
/// Legacy rooms and single-player run vanilla.
/// </remarks>
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
