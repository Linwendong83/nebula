#region

using System;
using NebulaAPI.Networking;
using NebulaModel.Authority;
using NebulaModel.Logger;
using NebulaModel.Packets.Authority;

#endregion

namespace NebulaModel.Networking;

/// <summary>
/// The single runtime checkpoint every authority message passes.
/// </summary>
/// <remarks>
/// <para>
/// TASKS.md A03 requires C2S to reach the host directly and S2C to come only from the bound server
/// connection, with no routing through StarBroadcast/PlanetBroadcast before validation. The
/// authority families are separate top-level packet types, so a peer that tries to smuggle one
/// through a broadcast router hits a type mismatch instead of a wrapper the host would forward
/// first and inspect later.
/// </para>
/// <para>
/// The gate is pure and holds no game reference, so the whole admission path is exercised by plain
/// tests. A04 owns turning accepted messages into work; until then this only counts and logs.
/// </para>
/// </remarks>
public static class AuthorityEnvelopeGateRuntime
{
    private static long rejectedTotal;
    private static long acceptedTotal;

    /// <summary>Messages refused since the process started. A non-zero count on a live host is a finding.</summary>
    public static long RejectedTotal => rejectedTotal;

    /// <summary>Messages that passed the gate since the process started.</summary>
    public static long AcceptedTotal => acceptedTotal;

    /// <summary>
    /// Validates a packet's header and family before anything else touches it.
    /// </summary>
    /// <param name="packet">The concrete envelope, used for its family.</param>
    /// <param name="context">What this session believes about itself.</param>
    /// <param name="direction">Which way the message traveled.</param>
    /// <param name="hostTick">Current host tick, for the log line.</param>
    public static AuthorityReject Validate(AuthorityEnvelopePacket packet, in AuthoritySessionContext context,
        AuthorityDirection direction, long hostTick = 0)
    {
        if (packet == null)
        {
            return Count(new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "null packet"), hostTick);
        }

        var header = packet.ToHeader(packet.DeclaredPayloadLength);
        var reject = AuthorityEnvelopeGate.Validate(context, direction, header);

        // A batch whose parallel counts disagree is malformed even when the header is fine, so the
        // family-specific consistency check belongs here rather than in the family's consumer.
        if (!reject.IsRejected)
        {
            reject = packet switch
            {
                AuthorityWorldStatePacket worldState => ValidateWorldState(worldState),
                AuthorityLifecyclePacket lifecycle => ValidateLifecycle(lifecycle),
                AuthoritySnapshotBeginPacket begin => ValidateSnapshotBegin(begin),
                AuthoritySnapshotChunkPacket chunk => ValidateSnapshotChunk(chunk),
                AuthoritySnapshotCommitPacket commit => ValidateSnapshotCommit(commit),
                _ => AuthorityReject.Accepted
            };
        }

        return Count(reject, hostTick);
    }

    private static AuthorityReject ValidateWorldState(AuthorityWorldStatePacket packet) =>
        !packet.AreRecordsConsistent() ? new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "world state record count")
        : packet.SubscriptionEpoch <= 0 ? new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "world state subscription epoch")
        : AuthorityReject.Accepted;

    private static AuthorityReject ValidateLifecycle(AuthorityLifecyclePacket packet) =>
        !packet.AreRecordsConsistent() ? new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "lifecycle record count")
        : packet.SubscriptionEpoch <= 0 ? new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "lifecycle subscription epoch")
        : AuthorityReject.Accepted;

    private static AuthorityReject ValidateSnapshotBegin(AuthoritySnapshotBeginPacket packet) =>
        !packet.TryGetScopeKey(out _) ? new AuthorityReject(AuthorityRejectCode.IllegalScope, "snapshot begin scope")
        : packet.BaselineId <= 0 || packet.SubscriptionEpoch <= 0
            ? new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "snapshot begin identity")
        : packet.ChunkCount < 0 || packet.TotalBytes <= 0 ||
          packet.ChunkCount != AuthoritySnapshotCodec.ChunkCountFor(packet.TotalBytes)
            ? new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "snapshot begin bookkeeping")
        : AuthorityReject.Accepted;

    private static AuthorityReject ValidateSnapshotChunk(AuthoritySnapshotChunkPacket packet) =>
        !packet.TryGetScopeKey(out _) ? new AuthorityReject(AuthorityRejectCode.IllegalScope, "snapshot chunk scope")
        : packet.BaselineId <= 0 || packet.SubscriptionEpoch <= 0 || packet.ChunkIndex < 0 || packet.Data == null
            ? new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "snapshot chunk bookkeeping")
        : AuthorityReject.Accepted;

    private static AuthorityReject ValidateSnapshotCommit(AuthoritySnapshotCommitPacket packet) =>
        !packet.TryGetScopeKey(out _) ? new AuthorityReject(AuthorityRejectCode.IllegalScope, "snapshot commit scope")
        : packet.BaselineId <= 0 || packet.SubscriptionEpoch <= 0 || packet.ChunkCount < 0
            ? new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "snapshot commit bookkeeping")
        : AuthorityReject.Accepted;

    /// <summary>
    /// Validates the bootstrap welcome, which is the one message that precedes the world epoch.
    /// </summary>
    /// <remarks>
    /// The bootstrap takes a different path because it is what establishes the epoch, but it must
    /// still be counted like every other rejection: a welcome that a hostile peer spams is exactly
    /// the kind of thing the counters exist to make visible.
    /// </remarks>
    public static AuthorityReject ValidateBootstrap(AuthorityEnvelopePacket packet,
        in AuthoritySessionContext context, AuthorityDirection direction, long hostTick = 0)
    {
        if (packet == null)
        {
            return Count(new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "null packet"), hostTick);
        }
        var reject = AuthorityEnvelopeGate.ValidateBootstrapWelcome(context, direction,
            packet.ToHeader(packet.DeclaredPayloadLength));
        return Count(reject, hostTick);
    }

    /// <summary>Records a rejection and logs it once, so a malformed peer is visible without spam.</summary>
    private static AuthorityReject Count(AuthorityReject reject, long hostTick)
    {
        if (!reject.IsRejected)
        {
            acceptedTotal++;
            return reject;
        }

        var total = ++rejectedTotal;
        Log.Warn($"[authority] rejected message #{total} at tick {hostTick}: {reject}");
        return reject;
    }

    /// <summary>Resets the counters. Used by tests and when a session ends.</summary>
    public static void ResetCounters()
    {
        rejectedTotal = 0;
        acceptedTotal = 0;
    }
}
