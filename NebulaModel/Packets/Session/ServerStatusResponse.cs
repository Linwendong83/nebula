namespace NebulaModel.Packets.Session;

public class ServerStatusResponse
{
    public ServerStatusResponse() { }

    public ServerStatusResponse(ushort numPlayers, bool isGameLoaded, int gameVersionSig, string nebulaVersion, string description)
    {
        NumPlayers = numPlayers;
        IsGameLoaded = isGameLoaded;
        GameVersionSig = gameVersionSig;
        NebulaVersion = nebulaVersion;
        Description = description;
    }

    public ushort NumPlayers { get; set; }
    public bool IsGameLoaded { get; set; }
    public int GameVersionSig { get; set; }
    public string NebulaVersion { get; set; }
    public string Description { get; set; }
}
