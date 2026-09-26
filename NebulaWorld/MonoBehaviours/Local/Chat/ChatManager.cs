using System;
using NebulaModel;
using NebulaModel.DataStructures.Chat;
using NebulaModel.Logger;
using NebulaModel.Packets.Chat;
using NebulaWorld.Chat;
using UnityEngine;

namespace NebulaWorld.MonoBehaviours.Local.Chat;

/// <summary>Connects the quiet in-game overlay to Nebula's existing chat transport.</summary>
public class ChatManager : MonoBehaviour
{
    public static ChatManager Instance;
    private PreviewOverlayView view;

    private void Awake()
    {
        Instance = this;
        Config.OnConfigApplied += ApplyConfig;
        TryCreateView();
    }

    private void Update()
    {
        if (view == null) TryCreateView();
    }

    private void TryCreateView()
    {
        if (Multiplayer.IsDedicated || view != null || UIRoot.instance?.uiGame?.inventoryWindow == null) return;
        var parent = UIRoot.instance.uiGame.inventoryWindow.transform.parent;
        var root = new GameObject("Nebula Overlay", typeof(RectTransform));
        root.transform.SetParent(parent, false);
        view = root.AddComponent<PreviewOverlayView>();
        view.OnMessageSubmitted += OnMessageSubmitted;
    }

    private void OnDestroy()
    {
        Config.OnConfigApplied -= ApplyConfig;
        if (view != null)
        {
            view.OnMessageSubmitted -= OnMessageSubmitted;
            Destroy(view.gameObject);
        }
        if (Instance == this) Instance = null;
    }

    private static void ApplyConfig()
    {
        if (Instance?.view != null) Instance.view.RefreshLayout();
    }

    private static void OnMessageSubmitted(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || !Multiplayer.IsActive || Multiplayer.Session == null) return;
        var name = Multiplayer.Session.LocalPlayer?.Data?.Username ?? "Unknown";
        // The preview treats slash-prefixed text as ordinary chat, so bypass command parsing.
        ChatService.Instance.AddMessage(input, ChatMessageType.PlayerMessage, name);
        Multiplayer.Session.Network.SendPacket(new NewChatMessagePacket(ChatMessageType.PlayerMessage, input,
            DateTime.Now, name));
    }

    public void SendChatMessage(string text, ChatMessageType messageType = ChatMessageType.SystemInfoMessage)
    {
        ChatService.Instance.AddMessage(text, messageType);
    }

    public void NotifyPlayerPresence(string name, bool joined)
    {
        if (view == null) TryCreateView();
        view?.AddPresence(name, joined);
    }

    public bool IsPointerIn() => view != null && view.IsPointerIn;
}
