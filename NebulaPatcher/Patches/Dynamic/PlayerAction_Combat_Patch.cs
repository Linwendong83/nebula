#region

using HarmonyLib;
using NebulaWorld;

#endregion

namespace NebulaPatcher.Patches.Dynamic;

[HarmonyPatch(typeof(PlayerAction_Combat))]
internal class PlayerAction_Combat_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlayerAction_Combat.ActivateBaseEnemyManually))]
    public static bool ActivateBaseEnemyManually_Prefix(PlayerAction_Combat __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer)
        {
            return true;
        }

        // Clients do not decide base activation; the host's world state governs.
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlayerAction_Combat.ActivateNearbyEnemyBase))]
    public static bool ActivateNearbyEnemyBase_Prefix()
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer)
        {
            return true;
        }

        // Triggered for the host by its own AI tick (UpdateHatred); clients take host state.
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlayerAction_Combat.ActivateHiveEnemyManually))]
    public static bool ActivateHiveEnemyManually_Prefix(PlayerAction_Combat __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlayerAction_Combat.ActivateNearbyEnemyHive))]
    public static bool ActivateNearbyEnemyHive(PlayerAction_Combat __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;

        return false;
    }
}
