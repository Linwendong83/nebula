#region

using System;
using System.ComponentModel;
using BepInEx.Configuration;
using NebulaModel.Attributes;
using UnityEngine;
using UnityEngine.UI;

#endregion

namespace NebulaModel;

[Serializable]
public class MultiplayerOptions : ICloneable
{
    [DisplayName("Enable Achievement")]
    [Description("Toggle to enable achievement in multiplayer game")]
    public bool EnableAchievement { get; set; } = true;

    [DisplayName("Server Password")]
    [Description("If provided, this will set a password for your hosted server.")]
    [UIContentType(InputField.ContentType.Password)]
    public string ServerPassword { get; set; } = string.Empty;

    [DisplayName("Server Description")]
    [Description("Custom text shown to players browsing the server list.")]
    public string ServerDescription { get; set; } = string.Empty;

    [DisplayName("Host Port")]
    [UIRange(1, ushort.MaxValue)]
    public ushort HostPort { get; set; } = 8469;

    [DisplayName("Enable UPnp/Pmp Support")]
    [Description(
        "If enabled, attempt to automatically create a port mapping using UPnp/Pmp (only works if your router has this feature and it is enabled)")]
    public bool EnableUPnpOrPmpSupport { get; set; } = false;

    [DisplayName("Cleanup inactive sessions")]
    [Description(
        "If disabled the underlying networking library will not cleanup inactive connections. This might solve issues with clients randomly disconnecting and hosts having a 'System.ObjectDisposedException'.")]
    public bool CleanupInactiveSessions { get; set; } = false;

    [DisplayName("Chat Hotkey")]
    [Description("Keyboard shortcut to toggle the chat window")]
    public KeyboardShortcut ChatHotkey { get; set; } = new(KeyCode.BackQuote, KeyCode.LeftAlt);

    [DisplayName("Player List Hotkey")]
    [Description("Keyboard shortcut to display the Connected Players Window")]
    public KeyboardShortcut PlayerListHotkey { get; set; } = new(KeyCode.BackQuote);


    // Detail function group buttons
    public bool ShowDetailPowerGrid { get; set; }
    public bool ShowDetailVeinDistribution { get; set; }
    public bool ShowDetailSpaceNavigation { get; set; } = true;
    public bool ShowDetailDefenseArea { get; set; }
    public bool ShowDetailBuildingAlarm { get; set; } = true;
    public bool ShowDetailBuildingIcon { get; set; } = true;
    public bool ShowGuidingLight { get; set; } = true;
    public bool ShowDetailHpBars { get; set; } = true;

    public bool RemoteAccessEnabled { get; set; } = false;
    public string RemoteAccessPassword { get; set; } = "";
    public bool AutoPauseEnabled { get; set; } = true;


    public object Clone()
    {
        return MemberwiseClone();
    }
}
