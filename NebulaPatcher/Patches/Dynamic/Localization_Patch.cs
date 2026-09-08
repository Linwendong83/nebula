using HarmonyLib;
using NebulaModel.Utils;

namespace NebulaPatcher.Patches.Dynamic;

[HarmonyPatch(typeof(Localization), nameof(Localization.Translate), typeof(string))]
internal static class Localization_Patch
{
    [HarmonyPrefix]
    private static bool Translate_Prefix(string __0, ref string __result)
    {
        if (!Localization.Loaded || !NebulaLocalization.TryTranslate(__0, Localization.isZHCN, out var translation))
        {
            return true;
        }
        __result = translation;
        return false;
    }
}
