#region

using System.Collections.Generic;
using NebulaAPI.Networking;
using NebulaModel.Authority;
using NebulaModel.Logger;
using NebulaModel.Networking;
using NebulaModel.Packets.Authority;
using NebulaWorld;
using NebulaWorld.Authority;

#endregion

namespace NebulaNetwork.PacketProcessors.Authority;

/// <summary>
/// Shared admission for every authority envelope processor.
/// </summary>
/// <remarks>
/// <para>
/// Every authority family goes through <see cref="Admit"/> before it does anything else, so the
/// direction, schema, epoch, size and identity checks cannot be forgotten in one family and kept in
/// another. A family that needs more validation adds it after admission, never instead of it.
/// </para>
/// <para>
/// The class deliberately performs no world work. A03 establishes the gate and the wire format; A04
/// installs the frame-boundary queue that turns an admitted command into host work, and DESIGN 6
/// requires that queue to be the only place the world is touched.
/// </para>
/// </remarks>
internal static class AuthorityPacketAdmission
{
    /// <summary>Number of messages each family has been refused for, keyed by reject code.</summary>
    private static readonly Dictionary<AuthorityRejectCode, long> rejectCounts = [];

    /// <summary>Rejections seen so far, for tests and diagnostics.</summary>
    internal static IReadOnlyDictionary<AuthorityRejectCode, long> RejectCounts => rejectCounts;

    /// <summary>
    /// Runs the gate for one packet received on a specific connection.
    /// </summary>
    /// <remarks>
    /// DESIGN 4.1: identity comes from the connection, so on the host the claimed id is compared
    /// with the id the transport assigned to <em>this</em> connection. A connection with no player
    /// record cannot assert any id, so its message is refused as a forgery rather than accepted.
    /// </remarks>
    /// <param name="packet">The received envelope.</param>
    /// <param name="direction">Direction the packet arrived from.</param>
    /// <param name="conn">Connection the packet arrived on, or null for host-originated messages.</param>
    /// <param name="hostTick">Current host tick, for the log line.</param>
    /// <param name="context">The context the gate validated against, so callers reuse it.</param>
    /// <param name="reject">The rejection, or <see cref="AuthorityRejectCode.None"/>.</param>
    /// <remarks>
    /// The resolved context is handed back deliberately. A caller that needs to check a command key
    /// or an object key must do it against the same connection epoch the gate used; re-deriving the
    /// context would let the two checks disagree about which connection a message came from.
    /// </remarks>
    internal static bool Admit(AuthorityEnvelopePacket packet, AuthorityDirection direction, NebulaConnection conn,
        long hostTick, out AuthoritySessionContext context, out AuthorityReject reject)
    {
        var session = Multiplayer.Session;
        var state = session?.Authority;
        if (state is null)
        {
            context = AuthoritySessionContext.Uninitialized;
            reject = Record(new AuthorityReject(AuthorityRejectCode.SessionNotReady, "no authority session"));
            return false;
        }

        context = ResolveContext(state, direction, conn);
        reject = AuthorityEnvelopeGateRuntime.Validate(packet, context, direction, hostTick);
        if (!reject.IsRejected) return true;

        Record(reject);
        return false;
    }

    /// <summary>
    /// Runs the gate for a message whose context the caller does not need.
    /// </summary>
    internal static bool Admit(AuthorityEnvelopePacket packet, AuthorityDirection direction, NebulaConnection conn,
        long hostTick, out AuthorityReject reject) =>
        Admit(packet, direction, conn, hostTick, out _, out reject);

    /// <summary>
    /// Picks the context the gate must validate against.
    /// </summary>
    /// <remarks>
    /// A client-to-server message is checked against the connection it arrived on, because only the
    /// host has several connections to tell apart. Everything else uses the session's own identity.
    /// </remarks>
    private static AuthoritySessionContext ResolveContext(AuthoritySessionState state,
        AuthorityDirection direction, NebulaConnection conn)
    {
        if (direction != AuthorityDirection.ClientToServer || !state.IsHost || conn is null)
        {
            return state.Context;
        }

        // An unknown connection resolves to the invalid player id, so its claim cannot match and the
        // message is refused as a forgery. That is deliberate: admitting it would trust the packet.
        var player = Multiplayer.Session?.Server?.Players?.Get(conn, EConnectionStatus.Connected)
                     ?? Multiplayer.Session?.Server?.Players?.Get(conn, EConnectionStatus.Syncing);
        var playerId = player?.Id ?? 0;

        // Every client has its own connection epoch, so the message is checked against the epoch the
        // host assigned to this connection rather than one session-wide value. Otherwise a
        // reconnecting client could replay a sequence number the host still remembers.
        var epoch = Multiplayer.Session?.AuthorityRuntime?.ConnectionEpochFor(playerId) ?? default;
        return epoch.IsValid ? state.ContextForConnection(playerId, epoch) : state.ContextForConnection(playerId);
    }

    /// <summary>Counts one rejection by code so a misbehaving peer is visible without reading logs.</summary>
    private static AuthorityReject Record(AuthorityReject reject)
    {
        rejectCounts.TryGetValue(reject.Code, out var count);
        rejectCounts[reject.Code] = count + 1;
        return reject;
    }

    /// <summary>Clears the per-code counters. Used by tests.</summary>
    internal static void ResetCounters()
    {
        rejectCounts.Clear();
        AuthorityEnvelopeGateRuntime.ResetCounters();
    }
}
