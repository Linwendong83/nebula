#region

using NebulaModel.DataStructures;

#endregion

namespace NebulaModel.Packets.Session;

public class StartGameMessage
{
    public StartGameMessage() { }

    public StartGameMessage(bool isAllowedToStart, PlayerData localPlayerData, bool isNewPlayer = false)
    {
        IsAllowedToStart = isAllowedToStart;
        LocalPlayerData = localPlayerData;
        IsNewPlayer = isNewPlayer;
    }

    public bool IsAllowedToStart { get; set; }
    public PlayerData LocalPlayerData { get; set; }
    public bool IsNewPlayer { get; set; }
}
