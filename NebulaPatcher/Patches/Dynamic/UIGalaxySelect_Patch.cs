using HarmonyLib;
using NebulaModel.Packets.Session;
using NebulaWorld;
using UnityEngine.UI;

namespace NebulaPatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIGalaxySelect))]
internal class UIGalaxySelect_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIGalaxySelect.OnSeedInputSubmit))]
    [HarmonyPatch(nameof(UIGalaxySelect.OnSeedInputValueChange))]
    [HarmonyPatch(nameof(UIGalaxySelect.OnStarCountSliderValueChange))]
    [HarmonyPatch(nameof(UIGalaxySelect.OnResourceMultiplierValueChange))]
    [HarmonyPatch(nameof(UIGalaxySelect.OnSandboxToggleValueChanged))]
    [HarmonyPatch(nameof(UIGalaxySelect.OnDarkFogToggleValueChanged))]
    [HarmonyPatch(nameof(UIGalaxySelect.Rerand))]
    public static bool ClientSettings_Prefix() => !Multiplayer.IsActive || Multiplayer.Session.IsServer;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIGalaxySelect._OnOpen))]
    public static void OnOpen_Postfix(UIGalaxySelect __instance)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsClient) return;
        SetSettingsInteractable(__instance, false);
    }

    private static void SetSettingsInteractable(UIGalaxySelect instance, bool value)
    {
        // Retain vanilla layout. The server owns generation settings.
        var group = instance.transform.Find("setting-group");
        foreach (var control in instance.GetComponentsInChildren<Selectable>(true))
        {
            if ((group != null && control.transform.IsChildOf(group)) || control.name == "random-button")
                control.interactable = value;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIGalaxySelect.ApplySetting))]
    public static bool ApplySetting_Prefix(UIGalaxySelect __instance)
    {
        if (!Multiplayer.IsInMultiplayerMenu || !Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsServer) return true; // Includes vanilla goal selection.
        if (!__instance.uiCombat.active) Multiplayer.Session.Network.SendPacket(new StartGameMessage());
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIGalaxySelect._OnClose))]
    public static void OnClose_Prefix(UIGalaxySelect __instance)
    {
        if (Multiplayer.IsInMultiplayerMenu && Multiplayer.IsActive && Multiplayer.Session.IsInLobby)
        {
            Multiplayer.ShouldReturnToJoinMenu = false;
            Multiplayer.Session.IsInLobby = false;
            Multiplayer.LeaveGame();
        }
        SetSettingsInteractable(__instance, true);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIGalaxySelect.UpdateParametersUIDisplay))]
    public static void UpdateParametersUIDisplay_Postfix(UIGalaxySelect __instance)
    {
        if (!Multiplayer.IsInMultiplayerMenu || !Multiplayer.IsActive || !Multiplayer.Session.IsServer) return;
        var desc = __instance.gameDesc;
        var packet = new LobbyUpdateValues(desc.galaxyAlgo, desc.galaxySeed, desc.starCount,
            desc.resourceMultiplier, desc.isSandboxMode, desc.isPeaceMode, desc.combatSettings)
        { GoalLevel = (int)desc.goalLevel };
        var server = Multiplayer.Session.Server;
        server.SendToPlayers(server.Players.Syncing, packet);
        server.SendToPlayers(server.Players.Pending, packet);
    }
}

[HarmonyPatch(typeof(UIGoalSetting), nameof(UIGoalSetting.OnGoalButtonClick))]
internal class LobbyGoalSetting_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(UIGoalSetting __instance, int _data)
    {
        if (!Multiplayer.IsActive || !__instance.galaxySelect.active) return true;
        if (Multiplayer.Session.IsClient) return false;
        if (_data < (int)EGoalLevel.Off || _data > (int)EGoalLevel.Full) return false;
        __instance.gameDesc.goalLevel = (EGoalLevel)_data;
        UIGalaxySelect_Patch.UpdateParametersUIDisplay_Postfix(__instance.galaxySelect);
        Multiplayer.Session.IsInLobby = false;
        DSPGame.StartGameSkipPrologue(__instance.gameDesc);
        return false;
    }
}
