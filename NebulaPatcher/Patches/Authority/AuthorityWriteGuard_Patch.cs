#region

using HarmonyLib;
using NebulaModel.Authority;

#endregion

namespace NebulaPatcher.Patches.Authority;

/// <summary>
/// Write-layer guards for the vanilla methods that A01 identified but that had no mod patch yet
/// (TASKS.md A05, DESIGN 6).
/// </summary>
/// <remarks>
/// <para>
/// DESIGN 6 asks for two layers: a semantic one that stops a migrated rule from running at all, and a
/// write-layer one that catches anything that slips past the first. The patches in
/// <c>Patches/Dynamic</c> are the semantic layer for the paths the mod already wrapped. This file is
/// the write layer for the remaining A01 writers, so a rule that reaches a protected field by a route
/// the mod never patched is still refused on a client instead of silently diverging.
/// </para>
/// <para>
/// These prefixes only decide; they change no argument and add no behaviour of their own. In a legacy
/// room they return the vanilla decision, so nothing here is reachable until the mode is entered.
/// </para>
/// </remarks>
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
