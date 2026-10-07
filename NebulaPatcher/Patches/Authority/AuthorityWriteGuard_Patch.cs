#region

using HarmonyLib;
using NebulaModel.Authority;

#endregion

namespace NebulaPatcher.Patches.Authority;

/// <summary>Prevents clients from deciding protected world facts outside replica application.</summary>
[HarmonyPatch]
public class AuthorityWriteGuard_Patch
{
    /// <summary>
    /// <c>ConstructStat.GameTick</c> decays <c>damageRate</c> and zeroes <c>damageRegister</c>, and
    /// removes the construct stat once a building is undamaged with no combat stat.
    /// </summary>
    /// <remarks>
    /// Removing the record is a shared fact: a client that did it locally would drop its own repair
    /// state while the host still had the building damaged, which is the client-side half of E06.
    /// </remarks>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ConstructStat), nameof(ConstructStat.GameTick))]
    public static bool ConstructStatGameTick_Prefix() =>
        AuthorityRuleGuard.AllowHostRule(AuthorityHookLabels.ConstructStatGameTick);
}
