using System;
using System.Collections;
using HarmonyLib;
using NebulaModel;
using NebulaModel.Logger;
using NebulaModel.Networking;
using NebulaNetwork;
using NebulaWorld;
using NebulaWorld.MonoBehaviours.Local;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NebulaPatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIMainMenu))]
internal static class UIMainMenu_Patch
{
    private static RectTransform multiplayerButton;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMainMenu._OnOpen))]
    public static void _OnOpen_Postfix()
    {
        Multiplayer.IsLeavingGame = false;
        var group = GameObject.Find("Main Menu/button-group")?.GetComponent<RectTransform>();
        if (group == null) return;
        if (multiplayerButton == null)
        {
            var template = group.Find("button-new")?.GetComponent<RectTransform>();
            if (template == null) return;
            multiplayerButton = Object.Instantiate(template, group, false);
            multiplayerButton.name = "button-multiplayer";
            var position = multiplayerButton.anchoredPosition;
            multiplayerButton.anchoredPosition = new Vector2(position.x,
                position.y + multiplayerButton.sizeDelta.y + 10);
            NebulaLocalizedText.Set(multiplayerButton.GetComponentInChildren<Text>(), "Multiplayer");
            var button = multiplayerButton.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(new UnityAction(OnMultiplayerButtonClick));
        }
        MultiplayerPage.Create();
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIMainMenu.UpdateDemoScene))]
    [HarmonyPatch(nameof(UIMainMenu._OnUpdate))]
    public static void OnEscSwitch()
    {
        if (!VFInput.escape) return;
        if (UIRoot.instance.loadGameWindow.active && Multiplayer.IsInMultiplayerMenu)
        {
            UIRoot.instance.loadGameWindow.OnCancelClick(0);
            VFInput.UseEscape();
        }
        else if (MultiplayerPage.IsOpen)
        {
            MultiplayerPage.Close();
            VFInput.UseEscape();
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMainMenu.OnUpdateLogButtonClick))]
    public static void OnUpdateLogButtonClick_Postfix()
    {
        if (MultiplayerPage.IsOpen) MultiplayerPage.Close();
    }

    public static void OnMultiplayerButtonClick()
    {
        if (Multiplayer.IsDedicated) { Multiplayer.IsInMultiplayerMenu = true; return; }
        MultiplayerPage.Open();
    }

    public static void JoinGame(string address, string password = "", string recordId = null)
    {
        if (!ServerAddress.TryParse(address, Config.Options.HostPort, out var parsed))
        {
            InGamePopup.ShowWarning("Invalid Address".Translate(), "Enter a valid server address".Translate(), "OK".Translate());
            return;
        }
        Multiplayer.ShouldReturnToJoinMenu = true;
        UIRoot.instance.StartCoroutine(TryConnect(parsed, address.Trim(), password ?? "", recordId));
    }

    private static IEnumerator TryConnect(ServerAddress parsed, string address, string password, string recordId)
    {
        InGamePopup.ShowInfo("Connecting".Translate(), "Connecting to server...".Translate(), null);
        if (MultiplayerPage.IsOpen) MultiplayerPage.HideForConnection();
        yield return new WaitForSeconds(.5f);
        try
        {
            Multiplayer.JoinGame(new Client(parsed.Host, parsed.Port, parsed.Protocol, password), recordId,
                address, password);
            InGamePopup.FadeOut();
        }
        catch (Exception e)
        {
            Log.Error("ConnectToServer error:\n" + e);
            InGamePopup.FadeOut();
            InGamePopup.ShowWarning("Connect failed".Translate(),
                "Was not able to connect to server".Translate(), "OK".Translate());
            MultiplayerPage.ShowAfterDisconnect();
        }
    }
}
