#region

using NebulaModel.Authority;
using NebulaModel.Logger;

#endregion

namespace NebulaWorld.Authority;

/// <summary>
/// What this session believes about the room's authority identity.
/// </summary>
/// <remarks>
/// <para>
/// A03 only needs this to be correct enough to answer the packet gate, so it holds no world state
/// and starts in <see cref="AuthorityMode.Legacy"/>. That default is what keeps every existing
/// build unchanged: until A04 sets a real epoch and mode, every authority packet is refused with
/// <see cref="AuthorityRejectCode.NotAuthorityMode"/>.
/// </para>
/// <para>
/// The epoch is deliberately not generated here. A world epoch must be drawn once per world load and
/// travel in the welcome message, which is A04's responsibility; inventing one at construction would
/// make two peers disagree about the world's identity.
/// </para>
/// </remarks>
public sealed class AuthoritySessionState
{
    private AuthorityMode mode = AuthorityMode.Legacy;
    private AuthoritySchema schema = AuthoritySchema.None;
    private AuthorityEpoch epoch;
    private ConnectionEpoch connection;
    private ushort localPlayerId;
    private bool isHost;

    /// <summary>Mode the room negotiated. Legacy until a handshake or A04 says otherwise.</summary>
    public AuthorityMode Mode => mode;

    /// <summary>Schema the room negotiated.</summary>
    public AuthoritySchema Schema => schema;

    /// <summary>Epoch of the loaded world, invalid before one is loaded.</summary>
    public AuthorityEpoch Epoch => epoch;

    /// <summary>Connection epoch clients must name on their commands.</summary>
    public ConnectionEpoch Connection => connection;

    /// <summary>Player id the transport assigned to this connection.</summary>
    public ushort LocalPlayerId => localPlayerId;

    /// <summary>True when this side is the host.</summary>
    public bool IsHost => isHost;

    /// <summary>True once a world epoch exists and the room is in authority mode.</summary>
    public bool IsActive => mode == AuthorityMode.HostAuthority && epoch.IsValid;

    /// <summary>
    /// Records the mode agreed during the handshake.
    /// </summary>
    /// <remarks>
    /// Only the mode is taken from the peer; schema and capabilities come from the local options
    /// that the handshake already compared. Copying them from the packet would let a peer talk the
    /// host into a different protocol.
    /// </remarks>
    public void OnPeerNegotiated(AuthorityMode negotiatedMode)
    {
        if (negotiatedMode != AuthorityMode.HostAuthority)
        {
            Reset();
            return;
        }

        mode = AuthorityMode.HostAuthority;
        schema = AuthorityLocalOptions.Schema;
    }

    /// <summary>
    /// Sets the world identity for an authority room. Called by A04 when a world is loaded.
    /// </summary>
    public void BeginAuthorityWorld(AuthorityEpoch worldEpoch, bool isHost)
    {
        if (!worldEpoch.IsValid)
        {
            Log.Warn("[authority] refusing to begin an authority world with an invalid epoch");
            return;
        }
        mode = AuthorityMode.HostAuthority;
        schema = AuthorityLocalOptions.Schema;
        epoch = worldEpoch;
        this.isHost = isHost;
    }

    /// <summary>Assigns this connection's epoch and player id, which every command must echo.</summary>
    public void SetConnection(ConnectionEpoch connectionEpoch, ushort playerId)
    {
        connection = connectionEpoch;
        localPlayerId = playerId;
    }

    /// <summary>
    /// Adopts the world epoch a host stated in its welcome.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The welcome is the one message that cannot be validated against a known epoch, because it is
    /// what establishes it: a client has no world identity until the host names one. This method is
    /// therefore the single, explicit bootstrap path, and it is deliberately narrow.
    /// </para>
    /// <para>
    /// It only works while this session has no epoch, so a second welcome cannot silently replace
    /// the world under a running client; after the first one, every message must match the adopted
    /// epoch through the normal gate. The mode must already be the negotiated authority mode, which
    /// the handshake set, so a welcome cannot create authority where none was agreed.
    /// </para>
    /// </remarks>
    /// <returns>True when the epoch was adopted, false when this session already has one.</returns>
    public bool TryAdoptWorldEpoch(AuthorityEpoch worldEpoch)
    {
        if (!worldEpoch.IsValid) return false;
        if (mode != AuthorityMode.HostAuthority) return false;
        if (epoch.IsValid) return false;
        epoch = worldEpoch;
        return true;
    }

    /// <summary>Returns to the legacy state. Called when leaving a room or loading a different world.</summary>
    public void Reset()
    {
        mode = AuthorityMode.Legacy;
        schema = AuthoritySchema.None;
        epoch = default;
        connection = default;
        localPlayerId = 0;
        isHost = false;
    }

    /// <summary>
    /// The immutable snapshot the packet gate validates against.
    /// </summary>
    /// <remarks>
    /// The gate takes a value so it cannot observe the session changing underneath it mid-message,
    /// and so the same gate can be driven directly from tests.
    /// </remarks>
    public AuthoritySessionContext Context =>
        new(mode, schema, epoch, connection, localPlayerId, isHost);

    /// <summary>
    /// The context the gate must use for a message that arrived on one connection.
    /// </summary>
    /// <remarks>
    /// On the host every connection has its own player id, so a single session-wide id would accept
    /// a command that claims someone else's seat. <paramref name="connectionPlayerId"/> is what the
    /// transport assigned to this connection, and it is what the claimed id must equal. On the
    /// client the local id is the only one there is, so the argument is ignored there.
    /// </remarks>
    public AuthoritySessionContext ContextForConnection(ushort connectionPlayerId) =>
        ContextForConnection(connectionPlayerId, connection);

    /// <summary>
    /// The context the gate must use for a message that arrived on one connection, naming the epoch
    /// the host assigned to <em>that</em> connection.
    /// </summary>
    /// <remarks>
    /// The session-wide <see cref="Connection"/> is only meaningful on a client, which has exactly
    /// one connection. On a host every client has its own epoch, so validating all of them against
    /// one value would let a reconnecting client replay an old sequence number. A04 supplies the
    /// per-connection epoch it assigned; the session-wide value remains the fallback for a peer that
    /// has not been given one yet.
    /// </remarks>
    public AuthoritySessionContext ContextForConnection(ushort connectionPlayerId, ConnectionEpoch connectionEpoch) =>
        isHost
            ? new AuthoritySessionContext(mode, schema, epoch, connectionEpoch, connectionPlayerId, true)
            : Context;

    /// <summary>
    /// True when an authority room is running on the host with a world epoch. Used to decide whether
    /// per-connection identity is meaningful.
    /// </summary>
    public bool RequiresConnectionIdentity => isHost && IsActive;
}
