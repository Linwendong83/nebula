#region

using System;
using NebulaModel;
using NebulaModel.DataStructures.Chat;
using NebulaModel.Logger;
using NebulaModel.Packets.Chat;
using NebulaModel.Utils;
using NebulaWorld.Chat;
using UnityEngine;
using UnityEngine.UI;

#endregion

namespace NebulaWorld.MonoBehaviours.Local.Chat;

#pragma warning disable CS0169, CS0414, CS0649, IDE0060
public class ChatManager : MonoBehaviour
{
    public static ChatManager Instance;
    private static bool showedWelcome = false;

    private Image backgroundImage;
    private IChatView currentChatView;
    private ChatViewMode currentViewMode;

    // References to view components
    private ChatWindow tmproChatView;
    private IMGUIChatView imguiChatView;
    private GameObject chatWindowGameObject;

    private void Awake()
    {
        Instance = this;

        // Entry-level disable: Do not initialize any chat views or display welcome message
    }

    private void Update()
    {
        // Entry-level disable: Hotkey is disabled to prevent opening chat window

        // Discard any outgoing messages
        _ = ChatService.Instance.GetQueuedMessage();

        // Handle warning messages from Log system (clear without adding to chat)
        if (Log.LastWarnMsg != null)
        {
            Log.LastWarnMsg = null;
        }
    }

    private void OnDestroy()
    {
        Log.Debug("ChatManager destroy");
        Config.OnConfigApplied -= ApplyConfig;

        if (currentChatView != null)
        {
            currentChatView.OnMessageSubmitted -= OnUserMessageSubmitted;
        }

        Instance = null;
    }

    /// <summary>
    /// Switches between different chat view implementations
    /// </summary>
    /// <param name="viewType">The type of view to switch to</param>
    /// <param name="preserveState">Whether to preserve window state (open/closed)</param>
    public void SwitchChatView(ChatViewMode viewType, bool preserveState = true)
    {
        // Entry-level disable: Chat views are disabled
        return;
    }

    private void InitTMProChatView()
    {
        var parent = UIRoot.instance.uiGame.inventoryWindow.transform.parent;
        var chatGo = parent.Find("Chat Window") ? parent.Find("Chat Window").gameObject : null;

        if (chatGo == null)
        {
            // Create chat window when there is no existing one
            var prefab = AssetLoader.AssetBundle.LoadAsset<GameObject>("Assets/Prefab/ChatV2.prefab");
            chatGo = Instantiate(prefab, parent, false);
            chatGo.name = "Chat Window";

            var trans = (RectTransform)chatGo.transform;
            var options = Config.Options;

            var defaultPos = ChatUtils.GetDefaultPosition(options.DefaultChatPosition, options.DefaultChatSize);
            var defaultSize = ChatUtils.GetDefaultSize(options.DefaultChatSize);

            trans.sizeDelta = defaultSize;
            trans.anchoredPosition = defaultPos;

            try
            {
                // TODO: Fix ChatV2.prefab to get rid of warnings
                var removeComponent = chatGo.GetComponent("CommonAPI.MaterialFixer");
                if (removeComponent != null)
                {
                    Destroy(removeComponent);
                }

                var backgroundGo = chatGo.transform.Find("Main/background").gameObject;
                DestroyImmediate(backgroundGo.GetComponent<TranslucentImage>());
                backgroundImage = backgroundGo.AddComponent<Image>();
                backgroundImage.color = new Color(0f, 0f, 0f, options.ChatWindowOpacity);

                backgroundGo = chatGo.transform.Find("Main/EmojiPicker/background").gameObject;
                DestroyImmediate(backgroundGo.GetComponent<TranslucentImage>());
                var emojiPickerBackground = backgroundGo.AddComponent<Image>();
                emojiPickerBackground.color = new Color(0f, 0f, 0f, 1f);

                var notifications = chatGo.transform.Find("NotificationsMask/Notifications");
                notifications.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);

                var uiWindowDrag = chatGo.GetComponent<UIWindowDrag>();
                uiWindowDrag.screenRect = GameObject.Find("UI Root/Overlay Canvas/In Game/Windows").GetComponent<RectTransform>();
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        chatWindowGameObject = chatGo;
        ChatLocalization.InitializeWindow(chatGo);
        chatWindowGameObject.SetActive(true);

        // Initialize both view types
        tmproChatView = chatGo.transform.GetComponentInChildren<ChatWindow>();
        if (tmproChatView == null)
        {
            Log.Error("Failed to find ChatWindow component");
        }
    }

    private void ReplayRecentMessages()
    {
        // Clear the new view first
        currentChatView.ClearMessages(_ => true);

        // Replay all messages from ChatService history
        var history = ChatService.Instance.MessageHistory;
        foreach (var message in history)
        {
            currentChatView.AddMessage(message);
        }
    }

    private void OnUserMessageSubmitted(string input)
    {
        var userName = GetUserName();
        ChatService.Instance.ProcessUserInput(input, userName);
    }

    private static void ApplyConfig()
    {
        if (Instance == null || Instance.currentChatView == null)
        {
            return;
        }

        var options = Config.Options;

        if (Instance.currentViewMode != options.ChatViewMode)
        {
            Instance.SwitchChatView(options.ChatViewMode);
        }

        // Only update position for TMPro view (IMGUI handles its own positioning)
        if (Instance.currentViewMode == ChatViewMode.TMPro && Instance.tmproChatView != null)
        {
            var defaultPos = ChatUtils.GetDefaultPosition(options.DefaultChatPosition, options.DefaultChatSize);
            var defaultSize = ChatUtils.GetDefaultSize(options.DefaultChatSize);

            var trans = (RectTransform)Instance.tmproChatView.transform;
            trans.anchoredPosition = defaultPos;
            trans.sizeDelta = defaultSize;
        }

        if (Instance.backgroundImage != null)
        {
            Instance.backgroundImage.color = new Color(0f, 0f, 0f, options.ChatWindowOpacity);
        }
    }

    private static string GetUserName()
    {
        return Multiplayer.Session?.LocalPlayer?.Data?.Username ?? "Unknown";
    }

    #region Public API for External Code

    /// <summary>
    /// Sends a chat message to be displayed (convenience method for external code)
    /// </summary>
    /// <param name="text">The message text</param>
    /// <param name="messageType">The type of message</param>
    public void SendChatMessage(string text, ChatMessageType messageType = ChatMessageType.SystemInfoMessage)
    {
        // Entry-level disable: Silently drop message
    }

    /// <summary>
    /// Inserts text into the chat input box
    /// </summary>
    /// <param name="text">The text to insert</param>
    /// <param name="forceOpenChatWindow">Whether to force open the chat window</param>
    public void InsertTextToChatbox(string text, bool forceOpenChatWindow)
    {
        // Entry-level disable: Do not insert or open chat window
        return;
    }

    /// <summary>
    /// Checks if the pointer is currently inside the chat area
    /// </summary>
    public bool IsPointerIn()
    {
        return false;
    }

    /// <summary>
    /// Checks if the pointer is currently inside the chat area
    /// </summary>
    public bool IsChatViewActive()
    {
        return false;
    }

    #endregion
}
