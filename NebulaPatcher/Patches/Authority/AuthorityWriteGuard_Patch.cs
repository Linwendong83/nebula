#region

using HarmonyLib;
using NebulaModel.Authority;
using UnityEngine;

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
    public static bool ConstructStatGameTick_Prefix(ref ConstructStat __instance)
    {
        return AuthorityRuleGuard.AllowHostRule(AuthorityHookLabels.ConstructStatGameTick,
            detail: $"stat={__instance.id} entity={__instance.entityId}");
    }

    /// <summary>
    /// <c>DroneComponent.InternalUpdate</c> moves a drone and charges flight and repair energy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A01's read-only field scan records no writer for this method because the energy never goes
    /// through a field store; it is charged through the reference the caller passed. That is exactly
    /// why it needs a guard: the write is real but invisible to the inventory, and the caller-supplied
    /// reference is what the mod currently fills with a dummy value for remote drones (E05).
    /// </para>
    /// <para>
    /// The method has two overloads — one charges the mecha's <c>double</c> core energy and one
    /// charges a battle base's <c>long</c> energy — and both are writers of a shared resource, so both
    /// are guarded. The argument types are spelled out because a name-only patch would be ambiguous.
    /// </para>
    /// </remarks>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DroneComponent), nameof(DroneComponent.InternalUpdate),
        [typeof(CraftData), typeof(PlanetFactory), typeof(Vector3), typeof(float), typeof(float),
            typeof(double), typeof(double), typeof(double), typeof(double), typeof(float)],
        [ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal,
            ArgumentType.Ref, ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out])]
    public static bool DroneComponentInternalUpdateMecha_Prefix(ref DroneComponent __instance)
    {
        return AuthorityRuleGuard.AllowHostRule(AuthorityHookLabels.DroneComponentInternalUpdate,
            detail: $"drone={__instance.id} owner={__instance.owner} stage={__instance.stage} energy=mecha");
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DroneComponent), nameof(DroneComponent.InternalUpdate),
        [typeof(CraftData), typeof(PlanetFactory), typeof(Vector3), typeof(float), typeof(float),
            typeof(long), typeof(double), typeof(double), typeof(float)],
        [ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal,
            ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out])]
    public static bool DroneComponentInternalUpdateBase_Prefix(ref DroneComponent __instance)
    {
        return AuthorityRuleGuard.AllowHostRule(AuthorityHookLabels.DroneComponentInternalUpdate,
            detail: $"drone={__instance.id} owner={__instance.owner} stage={__instance.stage} energy=base");
    }
}
