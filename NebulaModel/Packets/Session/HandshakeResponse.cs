#region

using NebulaModel.DataStructures;

#endregion

namespace NebulaModel.Packets.Session;

public class HandshakeResponse
{
    public HandshakeResponse() { }

    public HandshakeResponse(in GameDesc gameDesc, byte[] combatSettingsData, bool isNewPlayer, PlayerData localPlayerData, byte[] modsSettings,
        int settingsCount, ushort numPlayers)
    {
        GalaxyAlgo = gameDesc.galaxyAlgo;
        GoalLevel = (int)gameDesc.goalLevel;
        GalaxySeed = gameDesc.galaxySeed;
        StarCount = gameDesc.starCount;
        ResourceMultiplier = gameDesc.resourceMultiplier;
        IsPeaceMode = gameDesc.isPeaceMode;
        IsSandboxMode = gameDesc.isSandboxMode;
        SavedThemeIds = gameDesc.savedThemeIds;
        CombatSettingsData = combatSettingsData;
        IsNewPlayer = isNewPlayer;
        LocalPlayerData = localPlayerData;
        ModsSettings = modsSettings;
        ModsSettingsCount = settingsCount;
        NumPlayers = numPlayers;
    }

    public int GalaxyAlgo { get; set; }
    public int GoalLevel { get; set; }
    public int GalaxySeed { get; set; }
    public int StarCount { get; set; }
    public float ResourceMultiplier { get; set; }
    public bool IsPeaceMode { get; set; }
    public bool IsSandboxMode { get; set; }
    public int[] SavedThemeIds { get; set; }
    public byte[] CombatSettingsData { get; set; }
    public bool IsNewPlayer { get; set; }
    public PlayerData LocalPlayerData { get; set; }
    public byte[] ModsSettings { get; set; }
    public int ModsSettingsCount { get; set; }
    public ushort NumPlayers { get; set; }

    /// <summary>
    /// Authority mode the host confirmed, so the client asserts the agreement instead of assuming it.
    /// </summary>
    /// <remarks>
    /// The host already refuses an incompatible declaration, so this field exists to catch the other
    /// direction: a client that negotiated one mode must not silently proceed as if the host had
    /// agreed to another. Zero means the host did not state a mode, which a client treats as a
    /// protocol error rather than as legacy.
    /// </remarks>
    public byte AuthorityMode { get; set; }
}
