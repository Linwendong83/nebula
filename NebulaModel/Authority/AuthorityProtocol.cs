using System;

namespace NebulaModel.Authority;

/// <summary>
/// Which side decides the shared world (DESIGN 1 and 3).
/// </summary>
/// <remarks>
/// A room runs in exactly one mode. The mode is negotiated during the handshake and never changes
/// for the lifetime of a session, which is what makes "旧/新模式拒绝混房" enforceable: a client that
/// declares a different mode is refused instead of being half-accepted and then diverging.
/// </remarks>
public enum AuthorityMode : byte
{
    /// <summary>Unspecified. A session must never run in this mode; it only exists so a missing field is detectable.</summary>
    None = 0,

    /// <summary>Every peer simulates the shared world and exchanges results, as before the redesign.</summary>
    Legacy = 1,

    /// <summary>The host executes the shared rules; clients send intents and display host facts.</summary>
    HostAuthority = 2
}

/// <summary>Version of the authority DTO schema, negotiated separately from <c>SessionProtocol.Version</c>.</summary>
/// <remarks>
/// DESIGN 4.2 asks for the authority schema to be validated in the handshake. Keeping it a distinct
/// number means the transport protocol can stay compatible while a DTO layout change forces a
/// deliberate rejection rather than a misparse.
/// </remarks>
public enum AuthoritySchema : byte
{
    /// <summary>No authority schema. Only valid together with <see cref="AuthorityMode.Legacy"/>.</summary>
    None = 0,

    /// <summary>First authority schema, defined by the A03 envelopes.</summary>
    V1 = 1
}

/// <summary>
/// Features the host supports in authority mode.
/// </summary>
/// <remarks>
/// Capabilities are for compatibility checking only. DESIGN 5.2 is explicit that a player cannot
/// choose whether the host arbitrates damage, so a client may only require capabilities the host
/// advertised; it can never negotiate one on for itself.
/// </remarks>
[Flags]
public enum AuthorityCapability : uint
{
    None = 0,

    /// <summary>Combat state (enemy/player/craft HP, shield, death) is replicated.</summary>
    Combat = 1u << 0,

    /// <summary>Construction and repair task state is replicated.</summary>
    Construction = 1u << 1,

    /// <summary>Scoped snapshots and baseline installation are available.</summary>
    Snapshot = 1u << 2,

    /// <summary>Per-player resource ledger facts are replicated.</summary>
    Resources = 1u << 3,

    /// <summary>Visual effect events are replicated without carrying damage.</summary>
    Effects = 1u << 4,

    /// <summary>All capabilities the A03 schema can advertise.</summary>
    All = Combat | Construction | Snapshot | Resources | Effects
}

/// <summary>Which DTO family an envelope carries (DESIGN 5.2).</summary>
/// <remarks>
/// The families are separate packet types on the wire; this enum is the discriminator the gate uses
/// to decide direction and size limits without knowing the concrete type.
/// </remarks>
public enum AuthorityFamily : byte
{
    None = 0,

    /// <summary>Host to client: mode, epoch, capabilities, host tick.</summary>
    Welcome = 1,

    /// <summary>Client to host: one intent with a dedup key.</summary>
    Command = 2,

    /// <summary>Host to client: the cached answer to one command.</summary>
    CommandResult = 3,

    /// <summary>Host to client: a new baseline for one scope starts.</summary>
    SnapshotBegin = 4,

    /// <summary>Host to client: one bounded chunk of a baseline.</summary>
    SnapshotChunk = 5,

    /// <summary>Host to client: the baseline is complete and its hash is known.</summary>
    SnapshotCommit = 6,

    /// <summary>Client to host: the baseline was applied up to a sequence.</summary>
    SnapshotAck = 7,

    /// <summary>Host to client: absolute state for known objects in a scope.</summary>
    WorldState = 8,

    /// <summary>
    /// Host to client: membership changes of one scope (DESIGN 5.2's WorldLifecycleBatch).
    /// </summary>
    /// <remarks>
    /// A06 keeps lifecycle separate from <see cref="WorldState"/> because the two mean different
    /// things on the replica: a lifecycle record establishes identity or publishes death, while a
    /// state record may only be applied to an identity that already exists ("新对象先有身份再有
    /// HP"). Mixing them into one batch would let a half-applied batch decide its own meaning.
    /// </remarks>
    Lifecycle = 9,

    /// <summary>
    /// Client to host: subscribe, unsubscribe or resync one scope (DESIGN 5.1's Subscribe/Resync, A20).
    /// </summary>
    ScopeControl = 10,

    /// <summary>
    /// Host to client: the canonical digest of one scope at one stream position (DESIGN 9.3, A20).
    /// </summary>
    ScopeDigest = 11
}

/// <summary>What one <see cref="AuthorityFamily.ScopeControl"/> message asks for.</summary>
public enum ScopeControlOp : byte
{
    None = 0,

    /// <summary>Begin (or upgrade/downgrade) a subscription of one scope.</summary>
    Subscribe = 1,

    /// <summary>Leave one scope. The host keeps simulating it regardless.</summary>
    Unsubscribe = 2,

    /// <summary>The scope's stream is broken; the host must re-baseline it (DESIGN 9.3).</summary>
    Resync = 3
}

/// <summary>Which way an envelope is allowed to travel.</summary>
public enum AuthorityDirection : byte
{
    ClientToServer = 0,
    ServerToClient = 1
}

/// <summary>Why an authority message was refused. Every rejection is one of these, never a silent drop.</summary>
public enum AuthorityRejectCode : byte
{
    None = 0,

    /// <summary>The session is not running in authority mode. New-protocol packets must not be applied.</summary>
    NotAuthorityMode = 1,

    /// <summary>The message declares a different authority schema than the session negotiated.</summary>
    SchemaMismatch = 2,

    /// <summary>The client requires a capability the host does not advertise.</summary>
    CapabilityMismatch = 3,

    /// <summary>The family byte is not a known family.</summary>
    UnknownFamily = 4,

    /// <summary>The message traveled the wrong way, for example a command arriving from the host.</summary>
    WrongDirection = 5,

    /// <summary>The message names a different authority epoch than the loaded world.</summary>
    EpochMismatch = 6,

    /// <summary>The message claims a player identity that does not match its connection.</summary>
    ForgedPlayer = 7,

    /// <summary>The scope in the message is not legal for its pool kind.</summary>
    IllegalScope = 8,

    /// <summary>A declared length is negative.</summary>
    NegativeLength = 9,

    /// <summary>A declared payload exceeds the limit for its family.</summary>
    PayloadTooLarge = 10,

    /// <summary>A snapshot chunk exceeds the chunk limit.</summary>
    FragmentTooLarge = 11,

    /// <summary>The envelope is truncated or internally inconsistent.</summary>
    MalformedEnvelope = 12,

    /// <summary>An object key is malformed or names a different world than the envelope.</summary>
    InvalidObjectKey = 13,

    /// <summary>A command key is malformed.</summary>
    InvalidCommandKey = 14
}

/// <summary>One rejection: a code plus a short, loggable explanation.</summary>
/// <remarks>
/// The detail is for diagnostics. Callers branch on <see cref="Code"/> only, so wording changes can
/// never change behaviour.
/// </remarks>
public readonly struct AuthorityReject
{
    public AuthorityReject(AuthorityRejectCode code, string detail = null)
    {
        Code = code;
        Detail = detail;
    }

    public AuthorityRejectCode Code { get; }

    public string Detail { get; }

    public bool IsRejected => Code != AuthorityRejectCode.None;

    public static AuthorityReject Accepted => new(AuthorityRejectCode.None);

    public override string ToString() =>
        Detail == null ? Code.ToString() : Code + ": " + Detail;
}

/// <summary>
/// The size and count ceilings of the authority protocol (DESIGN 5.2).
/// </summary>
/// <remarks>
/// These are the starting values the design names. They live in one place so the gate, the codec and
/// the tests all agree; a deployment that needs larger values must change them deliberately and
/// re-run the back-pressure validation rather than silently raising a bound at one call site.
/// </remarks>
public static class AuthorityLimits
{
    /// <summary>Maximum size of one snapshot chunk payload, 64 KiB.</summary>
    public const int ChunkMaxBytes = 64 * 1024;

    /// <summary>Maximum size of one command payload, 16 KiB.</summary>
    public const int CommandPayloadMaxBytes = 16 * 1024;

    /// <summary>Maximum decompressed size of one scope snapshot, 64 MiB.</summary>
    public const long ScopeSnapshotMaxBytes = 64L * 1024 * 1024;

    /// <summary>Maximum pending data per connection, 128 MiB.</summary>
    public const long ConnectionPendingMaxBytes = 128L * 1024 * 1024;

    /// <summary>Maximum number of queued commands per connection.</summary>
    public const int CommandQueueMax = 1024;

    /// <summary>
    /// Maximum scopes one subscriber may hold at once. A subscribe past it is refused rather than
    /// silently growing delivery cost; a subscriber that needs more scopes is a design question,
    /// not a per-call-site decision.
    /// </summary>
    public const int ScopesPerSubscriberMax = 16;

    /// <summary>Digest statements buffered per scope while the stream catches up to them.</summary>
    public const int PendingDigestsPerScopeMax = 4;

    /// <summary>Maximum size of a fixed-size world-state record in a <see cref="AuthorityFamily.WorldState"/> batch.</summary>
    public const int StateRecordMaxBytes = 256;

    /// <summary>Maximum number of state records in one <see cref="AuthorityFamily.WorldState"/> batch.</summary>
    public const int StateRecordCountMax = 4096;

    /// <summary>Maximum number of lifecycle records in one <see cref="AuthorityFamily.Lifecycle"/> batch.</summary>
    public const int LifecycleRecordCountMax = 4096;

    /// <summary>
    /// Bytes of one wire-encoded lifecycle record: operation byte, <see cref="ObjectKey"/>, revision.
    /// </summary>
    public const int LifecycleRecordBytes = 1 + ObjectKeyBytes + 8;

    /// <summary>
    /// Bytes of the fixed envelope header: schema, family, epoch, connection epoch, sequence, host
    /// tick, claimed player id, payload length.
    /// </summary>
    public const int HeaderBytes =
        1 + 1 + AuthorityEpoch.SizeBytes + 8 + 8 + 8 + 2 + 4;

    /// <summary>Bytes of one wire-encoded <see cref="ObjectKey"/>.</summary>
    public const int ObjectKeyBytes = AuthorityEpoch.SizeBytes + 1 + 4 + 4 + 8;

    /// <summary>
    /// Payload ceiling for a family. Zero means the family carries no variable payload.
    /// </summary>
    public static int PayloadMaxBytes(AuthorityFamily family)
    {
        switch (family)
        {
            case AuthorityFamily.Command:
                return CommandPayloadMaxBytes;
            case AuthorityFamily.SnapshotChunk:
                return ChunkMaxBytes;
            case AuthorityFamily.CommandResult:
                return CommandPayloadMaxBytes;
            case AuthorityFamily.SnapshotCommit:
                return 128;
            case AuthorityFamily.SnapshotAck:
                return 64;
            case AuthorityFamily.Welcome:
                return 64;
            case AuthorityFamily.SnapshotBegin:
                return 64;
            case AuthorityFamily.WorldState:
                // One record is a key, a revision and a bounded state blob; the batch ceiling is the
                // per-record ceiling times the record count, plus the small per-record framing.
                return AuthorityLimits.StateRecordCountMax *
                       (AuthorityLimits.ObjectKeyBytes + 8 + AuthorityLimits.StateRecordMaxBytes + 16);
            case AuthorityFamily.Lifecycle:
                // One record is a fixed-size operation, key and revision; the ceiling is the record
                // count times that fixed size, plus the small per-record framing.
                return AuthorityLimits.LifecycleRecordCountMax *
                       (AuthorityLimits.LifecycleRecordBytes + 8);
            case AuthorityFamily.ScopeControl:
            case AuthorityFamily.ScopeDigest:
                // Both carry fixed scalar fields and no variable payload; the ceiling exists so a
                // declared length can never grow into an allocation.
                return 32;
            default:
                return 0;
        }
    }
}

/// <summary>Which direction each family may travel.</summary>
public static class AuthorityFamilyDirection
{
    /// <summary>Resolves the only legal direction for a family.</summary>
    public static bool TryGetDirection(AuthorityFamily family, out AuthorityDirection direction)
    {
        switch (family)
        {
            case AuthorityFamily.Command:
            case AuthorityFamily.SnapshotAck:
            case AuthorityFamily.ScopeControl:
                direction = AuthorityDirection.ClientToServer;
                return true;
            case AuthorityFamily.Welcome:
            case AuthorityFamily.CommandResult:
            case AuthorityFamily.SnapshotBegin:
            case AuthorityFamily.SnapshotChunk:
            case AuthorityFamily.SnapshotCommit:
            case AuthorityFamily.WorldState:
            case AuthorityFamily.Lifecycle:
            case AuthorityFamily.ScopeDigest:
                direction = AuthorityDirection.ServerToClient;
                return true;
            default:
                direction = default;
                return false;
        }
    }

    /// <summary>True when a message of this family may arrive from <paramref name="direction"/>.</summary>
    public static bool IsAllowed(AuthorityFamily family, AuthorityDirection direction) =>
        TryGetDirection(family, out var expected) && expected == direction;
}

/// <summary>
/// The handshake decision about whether two peers may share a room (DESIGN 4.2).
/// </summary>
/// <remarks>
/// Host-authority is the only multiplayer mode: both peers must declare it with the same schema,
/// and a client may only require capabilities the host advertises. Legacy rooms were removed, so
/// a legacy declaration is refused even when both sides agree on it. There is no downgrade path,
/// because a half-negotiated room is exactly the mixed-authority state the design forbids.
/// </remarks>
public static class AuthorityNegotiation
{
    /// <summary>
    /// Decides whether a client's declaration is compatible with the host's.
    /// </summary>
    /// <param name="hostMode">Mode the host runs.</param>
    /// <param name="hostSchema">Schema the host speaks.</param>
    /// <param name="hostCapabilities">Capabilities the host advertises.</param>
    /// <param name="clientMode">Mode the client declares.</param>
    /// <param name="clientSchema">Schema the client speaks.</param>
    /// <param name="clientRequiredCapabilities">Capabilities the client needs from the host.</param>
    /// <param name="reject">Why the pair is incompatible, or <see cref="AuthorityRejectCode.None"/>.</param>
    public static bool IsCompatible(AuthorityMode hostMode, AuthoritySchema hostSchema,
        AuthorityCapability hostCapabilities, AuthorityMode clientMode, AuthoritySchema clientSchema,
        AuthorityCapability clientRequiredCapabilities, out AuthorityReject reject)
    {
        if (hostMode == AuthorityMode.None || clientMode == AuthorityMode.None)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "missing authority mode");
            return false;
        }

        if (hostMode != AuthorityMode.HostAuthority || clientMode != AuthorityMode.HostAuthority)
        {
            // Legacy rooms were removed: host-authority is the only multiplayer mode. A legacy
            // declaration is refused even when both sides agree on it.
            reject = new AuthorityReject(AuthorityRejectCode.NotAuthorityMode,
                "host=" + hostMode + " client=" + clientMode);
            return false;
        }

        if (hostSchema != clientSchema)
        {
            reject = new AuthorityReject(AuthorityRejectCode.SchemaMismatch,
                "host=" + hostSchema + " client=" + clientSchema);
            return false;
        }

        var unsupported = clientRequiredCapabilities & ~hostCapabilities;
        if (unsupported != AuthorityCapability.None)
        {
            reject = new AuthorityReject(AuthorityRejectCode.CapabilityMismatch, "missing=" + unsupported);
            return false;
        }

        reject = AuthorityReject.Accepted;
        return true;
    }

    /// <summary>True when <paramref name="capabilities"/> contains nothing outside <see cref="AuthorityCapability.All"/>.</summary>
    public static bool AreCapabilitiesKnown(AuthorityCapability capabilities) =>
        (capabilities & ~AuthorityCapability.All) == AuthorityCapability.None;
}

/// <summary>
/// What the local session believes about the world it is in.
/// </summary>
/// <remarks>
/// This is the gate's only input besides the message itself, so the gate stays a pure function that
/// tests can drive directly. A04 owns filling it from the live session; before that it stays in
/// <see cref="Legacy"/>, which is why authority packets are refused on every current build.
/// </remarks>
public readonly struct AuthoritySessionContext
{
    public AuthoritySessionContext(AuthorityMode mode, AuthoritySchema schema, AuthorityEpoch epoch,
        ConnectionEpoch connection, ushort connectedPlayerId, bool isHost)
    {
        Mode = mode;
        Schema = schema;
        Epoch = epoch;
        Connection = connection;
        ConnectedPlayerId = connectedPlayerId;
        IsHost = isHost;
    }

    /// <summary>Mode this session runs. Authority packets are refused unless this is <see cref="AuthorityMode.HostAuthority"/>.</summary>
    public AuthorityMode Mode { get; }

    /// <summary>Schema this session negotiated.</summary>
    public AuthoritySchema Schema { get; }

    /// <summary>Epoch of the loaded world, or an invalid epoch before one is loaded.</summary>
    public AuthorityEpoch Epoch { get; }

    /// <summary>Epoch of the connection a client command must name, or an invalid epoch when unset.</summary>
    public ConnectionEpoch Connection { get; }

    /// <summary>
    /// Player id the transport assigned to this connection. A packet's claimed id is only an
    /// assertion and must equal this, because DESIGN 4.1 takes identity from the connection.
    /// </summary>
    public ushort ConnectedPlayerId { get; }

    /// <summary>True on the host side of the session.</summary>
    public bool IsHost { get; }

    /// <summary>A session that has not negotiated authority: the state of every build before A04.</summary>
    public static AuthoritySessionContext Legacy =>
        new(AuthorityMode.Legacy, AuthoritySchema.None, default, default, 0, false);
}
