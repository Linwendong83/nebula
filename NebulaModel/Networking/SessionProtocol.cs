namespace NebulaModel.Networking;

public static class SessionProtocol
{
    /// <summary>Transport protocol supported by multiplayer sessions.</summary>
    public const int Version = 3;

    public const string HandshakeKey = "dsp.nebula.protocol";

    /// <summary>Handshake entry carrying the replication schema and required capabilities.</summary>
    public const string AuthorityHandshakeKey = "dsp.nebula.authority";
}
