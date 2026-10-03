namespace NebulaModel.Networking;

public static class SessionProtocol
{
    /// <summary>
    /// Version of the transport protocol, raised to 3 when the authority envelopes were introduced.
    /// </summary>
    /// <remarks>
    /// A legacy peer declares 2 and is refused by the handshake, which is what keeps a mixed room
    /// from forming: version 2 peers and version 3 peers never share a world.
    /// </remarks>
    public const int Version = 3;

    /// <summary>Version that still means "peers simulate the shared world themselves".</summary>
    public const int LegacyVersion = 2;

    public const string HandshakeKey = "dsp.nebula.protocol";

    /// <summary>Handshake entry that carries the authority mode, schema and required capabilities.</summary>
    /// <remarks>
    /// It travels in the same mod-version list as <see cref="HandshakeKey"/> so an older host, which
    /// does not know the entry, fails the existing "mod is missing" check instead of silently
    /// accepting a peer whose authority declaration it never read.
    /// </remarks>
    public const string AuthorityHandshakeKey = "dsp.nebula.authority";
}
