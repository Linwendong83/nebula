#region

using NebulaAPI.Packets;
using NebulaModel.Authority;
using NebulaModel.Logger;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Authority;
using NebulaWorld;
using NebulaWorld.Authority;

#endregion

namespace NebulaNetwork.PacketProcessors.Authority;

/// <summary>
/// Client to host: one intent (DESIGN 5.1).
/// </summary>
/// <remarks>
/// The processor validates the envelope and the command key, then stops. Handing the intent to the
/// host rule engine is A04's frame-boundary queue; DESIGN 6 forbids doing that work on the socket
/// callback, which is where this runs.
/// </remarks>
[RegisterPacketProcessor]
internal class AuthorityCommandProcessor : PacketProcessor<AuthorityCommandPacket>
{
    protected override void ProcessPacket(AuthorityCommandPacket packet, NebulaConnection conn)
    {
        if (IsClient) return;
        if (!AuthorityPacketAdmission.Admit(packet, AuthorityDirection.ClientToServer, conn, HostTick(),
                out var context, out var reject))
        {
            Log.Warn($"[authority] command refused from connection {conn?.Id}: {reject}");
            return;
        }

        var runtime = Multiplayer.Session.AuthorityRuntime;
        var playerId = packet.ClaimedPlayerId;
        // The gate validated this message against the epoch of the connection it arrived on, so the
        // command key is built from that same epoch rather than one re-derived here.
        var key = new CommandKey(context.Epoch, context.Connection, packet.Sequence);
        var keyReject = AuthorityEnvelopeGate.ValidateCommandKey(context, key);
        if (keyReject.IsRejected)
        {
            Log.Warn($"[authority] command key refused: {keyReject}");
            return;
        }

        // A target is optional: some intents address no object. When one is present it must be a
        // legal key for its pool, or the host would later act on a scope that does not exist.
        if (packet.TargetNativeId > 0)
        {
            if (!packet.TryGetTargetKey(out var target))
            {
                Log.Warn("[authority] command target could not be reassembled");
                return;
            }
            var targetReject = AuthorityEnvelopeGate.ValidateObjectKey(context, target);
            if (targetReject.IsRejected)
            {
                Log.Warn($"[authority] command target refused: {targetReject}");
                return;
            }
        }

        // The socket thread stops here. The command is queued for the frame boundary, where the
        // dedup window decides whether it runs; a full queue is back-pressure and the command is
        // refused rather than accepted and dropped later.
        if (runtime is null || !runtime.TryEnqueueHostCommand(key, packet, playerId, conn?.Id ?? 0,
                HostTick()))
        {
            Log.Warn($"[authority] command {key} refused: host queue is unavailable or full " +
                     $"(queued={runtime?.Commands?.Count ?? 0}/{runtime?.Commands?.Capacity ?? 0})");
            return;
        }
    }

    private static long HostTick() => Multiplayer.Session.IsGameLoaded ? GameMain.gameTick : 0;
}

/// <summary>
/// Host to client: the answer to one command (DESIGN 5.2).
/// </summary>
/// <remarks>
/// The result is applied to the client's pending-command bookkeeping, which A04 owns. A03 only
/// guarantees a result cannot arrive on the host, where there is no command to answer.
/// </remarks>
[RegisterPacketProcessor]
internal class AuthorityCommandResultProcessor : PacketProcessor<AuthorityCommandResultPacket>
{
    protected override void ProcessPacket(AuthorityCommandResultPacket packet, NebulaConnection conn)
    {
        if (IsHost) return;
        if (!AuthorityPacketAdmission.Admit(packet, AuthorityDirection.ServerToClient, conn, 0, out var reject))
        {
            Log.Warn($"[authority] command result refused: {reject}");
            return;
        }
    }
}

/// <summary>Host to client: the room's authority identity (DESIGN 5.2).</summary>
[RegisterPacketProcessor]
internal class AuthorityWelcomeProcessor : PacketProcessor<AuthorityWelcomePacket>
{
    protected override void ProcessPacket(AuthorityWelcomePacket packet, NebulaConnection conn)
    {
        if (IsHost) return;

        var identity = Multiplayer.Session.Authority;
        var runtime = Multiplayer.Session.AuthorityRuntime;

        // The welcome is the bootstrap message: it is what establishes the world epoch, so it is
        // validated by the bootstrap entry point rather than the ordinary gate, which would require
        // an epoch the client cannot know yet.
        var reject = AuthorityEnvelopeGateRuntime.ValidateBootstrap(packet, identity.Context,
            AuthorityDirection.ServerToClient);
        if (reject.IsRejected)
        {
            Log.Warn($"[authority] welcome refused: {reject}");
            return;
        }

        // A05: the guard policy is verified before this client adopts the world. Adopting the epoch
        // first would leave the session half-entered — a world identity with no runtime behind it —
        // which is the state DESIGN 1.8 forbids. A failed check refuses the welcome outright, so the
        // session keeps no epoch and the gate goes on refusing every authority message.
        if (!AuthorityRuleGuard.VerifyLoadOnce())
        {
            Log.Error("[authority] welcome refused; the guard policy is not satisfied: " +
                      AuthorityRuleGuard.LoadFailure);
            return;
        }

        // A client has no world of its own, so it adopts the host's. The runtime then owns the
        // epoch, which is what makes every later message's epoch check meaningful.
        var worldEpoch = new AuthorityEpoch(packet.EpochHigh, packet.EpochLow);
        if (!identity.TryAdoptWorldEpoch(worldEpoch))
        {
            Log.Warn($"[authority] welcome epoch {worldEpoch} refused; this session already has a world");
            return;
        }
        runtime?.BeginAuthorityWorld(worldEpoch, isHost: false);

        Multiplayer.Session.Authority.SetConnection(new ConnectionEpoch(packet.ConnectionEpoch),
            packet.ClaimedPlayerId);
        runtime?.SetConnection(new ConnectionEpoch(packet.ConnectionEpoch), packet.ClaimedPlayerId);

        // The replica's snapshot acknowledgements travel with this session's transport identity
        // (DESIGN 4.1): the sink fills the claimed id and connection epoch at send time.
        if (runtime != null)
        {
            runtime.SnapshotAckSink = new NetworkSnapshotAckSink(
                () => Multiplayer.Session.LocalPlayer?.Id ?? 0,
                () => Multiplayer.Session.Authority.Context.Connection.Value,
                ack => Multiplayer.Session.Network.SendPacket(ack));
            runtime.ScopeControlSink = new NetworkScopeControlSink(
                () => Multiplayer.Session.LocalPlayer?.Id ?? 0,
                () => Multiplayer.Session.Authority.Context.Connection.Value,
                () => Multiplayer.Session.Authority.Context.Epoch,
                control => Multiplayer.Session.Network.SendPacket(control));
        }
        Log.Info($"[authority] welcome accepted: epoch={worldEpoch} " +
                 $"capabilities={(AuthorityCapability)packet.Capabilities} tick={packet.WelcomeHostTick}");
    }
}

/// <summary>Host to client: a new baseline for one scope begins (DESIGN 9.2).</summary>
/// <remarks>
/// The processor validates the envelope and the Begin's bookkeeping, then queues the packet for the
/// frame boundary. Opening the staging area on the socket thread would let a baseline message move
/// replica state outside the apply window, which is the loophole the window exists to close.
/// </remarks>
[RegisterPacketProcessor]
internal class AuthoritySnapshotBeginProcessor : PacketProcessor<AuthoritySnapshotBeginPacket>
{
    protected override void ProcessPacket(AuthoritySnapshotBeginPacket packet, NebulaConnection conn)
    {
        if (IsHost) return;
        if (!AuthorityPacketAdmission.Admit(packet, AuthorityDirection.ServerToClient, conn, 0, out var reject))
        {
            Log.Warn($"[authority] snapshot begin refused: {reject}");
            return;
        }
        if (!packet.TryGetScopeKey(out var scope))
        {
            Log.Warn($"[authority] snapshot begin has an illegal scope {packet.ScopeKind}:{packet.Scope}");
            return;
        }
        if (packet.BaselineId <= 0 || packet.ChunkCount < 0 || packet.TotalBytes < 0 ||
            packet.SubscriptionEpoch <= 0)
        {
            Log.Warn($"[authority] snapshot begin has invalid bookkeeping baseline={packet.BaselineId} " +
                     $"chunks={packet.ChunkCount} bytes={packet.TotalBytes} epoch={packet.SubscriptionEpoch}");
            return;
        }

        var runtime = Multiplayer.Session.AuthorityRuntime;
        var applyScope = new ApplyScope(scope, transactionId: 0, streamSequence: packet.Sequence,
            hostTick: packet.HostTick);
        if (runtime is null || !runtime.TryEnqueueReplicaMessage(packet, applyScope))
        {
            Log.Warn($"[authority] snapshot begin scope={scope} refused: replica inbox is unavailable or full");
            return;
        }
    }
}

/// <summary>Host to client: one bounded chunk of a baseline (DESIGN 5.2).</summary>
[RegisterPacketProcessor]
internal class AuthoritySnapshotChunkProcessor : PacketProcessor<AuthoritySnapshotChunkPacket>
{
    protected override void ProcessPacket(AuthoritySnapshotChunkPacket packet, NebulaConnection conn)
    {
        if (IsHost) return;
        if (!AuthorityPacketAdmission.Admit(packet, AuthorityDirection.ServerToClient, conn, 0, out var reject))
        {
            Log.Warn($"[authority] snapshot chunk refused: {reject}");
            return;
        }
        if (!packet.TryGetScopeKey(out var scope))
        {
            Log.Warn($"[authority] snapshot chunk has an illegal scope {packet.ScopeKind}:{packet.Scope}");
            return;
        }
        if (packet.Data == null || packet.ChunkIndex < 0 || packet.BaselineId <= 0 ||
            packet.SubscriptionEpoch <= 0)
        {
            Log.Warn($"[authority] snapshot chunk is empty, negative, or unkeyed");
            return;
        }

        var runtime = Multiplayer.Session.AuthorityRuntime;
        var applyScope = new ApplyScope(scope, transactionId: 0, streamSequence: packet.Sequence,
            hostTick: packet.HostTick);
        if (runtime is null || !runtime.TryEnqueueReplicaMessage(packet, applyScope))
        {
            Log.Warn($"[authority] snapshot chunk baseline={packet.BaselineId} refused: " +
                     "replica inbox is unavailable or full");
            return;
        }
    }
}

/// <summary>Host to client: the baseline is complete (DESIGN 9.2).</summary>
[RegisterPacketProcessor]
internal class AuthoritySnapshotCommitProcessor : PacketProcessor<AuthoritySnapshotCommitPacket>
{
    protected override void ProcessPacket(AuthoritySnapshotCommitPacket packet, NebulaConnection conn)
    {
        if (IsHost) return;
        if (!AuthorityPacketAdmission.Admit(packet, AuthorityDirection.ServerToClient, conn, 0, out var reject))
        {
            Log.Warn($"[authority] snapshot commit refused: {reject}");
            return;
        }
        if (!packet.TryGetScopeKey(out var scope))
        {
            Log.Warn($"[authority] snapshot commit has an illegal scope {packet.ScopeKind}:{packet.Scope}");
            return;
        }
        if (packet.BaselineId <= 0 || packet.ChunkCount < 0 || packet.SubscriptionEpoch <= 0)
        {
            Log.Warn($"[authority] snapshot commit has invalid bookkeeping baseline={packet.BaselineId} " +
                     $"chunks={packet.ChunkCount}");
            return;
        }

        var runtime = Multiplayer.Session.AuthorityRuntime;
        var applyScope = new ApplyScope(scope, transactionId: 0, streamSequence: packet.Sequence,
            hostTick: packet.HostTick);
        if (runtime is null || !runtime.TryEnqueueReplicaMessage(packet, applyScope))
        {
            Log.Warn($"[authority] snapshot commit baseline={packet.BaselineId} refused: " +
                     "replica inbox is unavailable or full");
            return;
        }
    }
}

/// <summary>Client to host: a baseline was applied up to a sequence (DESIGN 9.2).</summary>
/// <remarks>
/// The ack is queued for the frame boundary: the replicator's subscriber cursors move only where
/// A01 proved the world quiescent, never on the socket thread.
/// </remarks>
[RegisterPacketProcessor]
internal class AuthoritySnapshotAckProcessor : PacketProcessor<AuthoritySnapshotAckPacket>
{
    protected override void ProcessPacket(AuthoritySnapshotAckPacket packet, NebulaConnection conn)
    {
        if (IsClient) return;
        if (!AuthorityPacketAdmission.Admit(packet, AuthorityDirection.ClientToServer, conn, HostTick(), out var reject))
        {
            Log.Warn($"[authority] snapshot ack refused from connection {conn?.Id}: {reject}");
            return;
        }
        if (packet.BaselineId <= 0 || packet.LastAppliedSequence < 0)
        {
            Log.Warn($"[authority] snapshot ack has invalid bookkeeping baseline={packet.BaselineId} " +
                     $"sequence={packet.LastAppliedSequence}");
            return;
        }

        var runtime = Multiplayer.Session.AuthorityRuntime;
        if (runtime is null || !runtime.NotifySnapshotAck(packet.ClaimedPlayerId, packet.BaselineId,
                packet.LastAppliedSequence, packet.Accepted))
        {
            Log.Warn($"[authority] snapshot ack baseline={packet.BaselineId} refused: host is not " +
                     "an authority world or the ack queue is full");
            return;
        }
    }

    private static long HostTick() => Multiplayer.Session.IsGameLoaded ? GameMain.gameTick : 0;
}

/// <summary>Host to client: absolute state for known objects of one scope (DESIGN 5.2).</summary>
[RegisterPacketProcessor]
internal class AuthorityWorldStateProcessor : PacketProcessor<AuthorityWorldStatePacket>
{
    protected override void ProcessPacket(AuthorityWorldStatePacket packet, NebulaConnection conn)
    {
        if (IsHost) return;
        if (!AuthorityPacketAdmission.Admit(packet, AuthorityDirection.ServerToClient, conn, 0, out var reject))
        {
            Log.Warn($"[authority] world state refused: {reject}");
            return;
        }
        if (!packet.TryGetScopeKey(out var scope))
        {
            Log.Warn($"[authority] world state has an illegal scope {packet.ScopeKind}:{packet.Scope}");
            return;
        }
        if (!packet.AreRecordsConsistent())
        {
            Log.Warn($"[authority] world state record bookkeeping is inconsistent");
            return;
        }

        var context = Multiplayer.Session.Authority.Context;
        if (!AuthorityWorldStateCodec.TryDecode(packet.Data, 0, packet.Data.Length, packet.RecordCount,
                context, out var records, out var decodeReject))
        {
            Log.Warn($"[authority] world state refused: {decodeReject}");
            return;
        }

        // The socket thread decodes and queues; it does not write the replica. The frame boundary
        // applies the message inside a validated scope, which is the only place a replica may change.
        var runtime = Multiplayer.Session.AuthorityRuntime;
        var applyScope = new ApplyScope(scope, transactionId: 0, streamSequence: packet.Sequence,
            hostTick: packet.HostTick);
        if (runtime is null || !runtime.TryEnqueueReplicaMessage(packet, applyScope))
        {
            Log.Warn($"[authority] world state scope={scope} refused: replica inbox is unavailable or full");
            return;
        }
    }
}

/// <summary>Host to client: membership changes of one scope (DESIGN 5.2's WorldLifecycleBatch).</summary>
/// <remarks>
/// The processor mirrors the world-state path: validate, decode for the session context, then queue
/// for the frame boundary. Applying a spawn on the socket thread would let identity be established
/// outside the apply window, which is the exact loophole the window exists to close.
/// </remarks>
[RegisterPacketProcessor]
internal class AuthorityLifecycleProcessor : PacketProcessor<AuthorityLifecyclePacket>
{
    protected override void ProcessPacket(AuthorityLifecyclePacket packet, NebulaConnection conn)
    {
        if (IsHost) return;
        if (!AuthorityPacketAdmission.Admit(packet, AuthorityDirection.ServerToClient, conn, 0, out var reject))
        {
            Log.Warn($"[authority] lifecycle refused: {reject}");
            return;
        }
        if (!packet.TryGetScopeKey(out var scope))
        {
            Log.Warn($"[authority] lifecycle has an illegal scope {packet.ScopeKind}:{packet.Scope}");
            return;
        }
        if (!packet.AreRecordsConsistent())
        {
            Log.Warn($"[authority] lifecycle record bookkeeping is inconsistent");
            return;
        }

        var context = Multiplayer.Session.Authority.Context;
        if (!AuthorityLifecycleCodec.TryDecode(packet.Data, 0, packet.Data.Length, packet.RecordCount,
                context, out var records, out var decodeReject))
        {
            Log.Warn($"[authority] lifecycle refused: {decodeReject}");
            return;
        }

        var runtime = Multiplayer.Session.AuthorityRuntime;
        var applyScope = new ApplyScope(scope, transactionId: 0, streamSequence: packet.Sequence,
            hostTick: packet.HostTick);
        if (runtime is null || !runtime.TryEnqueueReplicaMessage(packet, applyScope))
        {
            Log.Warn($"[authority] lifecycle scope={scope} refused: replica inbox is unavailable or full");
            return;
        }
    }
}

/// <summary>Client to host: subscribe, unsubscribe or resync one scope (DESIGN 5.1/9.3, A20).</summary>
/// <remarks>
/// The processor validates the envelope and the scope, then queues the request for the frame
/// boundary. The host-side eligibility check (registry presence, accepted planet) runs there too,
/// where the replicator's cursors may move — never on the socket thread.
/// </remarks>
[RegisterPacketProcessor]
internal class AuthorityScopeControlProcessor : PacketProcessor<AuthorityScopeControlPacket>
{
    protected override void ProcessPacket(AuthorityScopeControlPacket packet, NebulaConnection conn)
    {
        if (IsClient) return;
        if (!AuthorityPacketAdmission.Admit(packet, AuthorityDirection.ClientToServer, conn, HostTick(),
                out var context, out var reject))
        {
            Log.Warn($"[authority] scope control refused from connection {conn?.Id}: {reject}");
            return;
        }
        if ((ScopeControlOp)packet.Op == ScopeControlOp.None)
        {
            Log.Warn("[authority] scope control without an operation");
            return;
        }
        if (!packet.TryGetScopeKey(out var scope))
        {
            Log.Warn($"[authority] scope control has an illegal scope {packet.ScopeKind}:{packet.Scope}");
            return;
        }

        var runtime = Multiplayer.Session.AuthorityRuntime;
        var op = (ScopeControlOp)packet.Op;
        if (runtime is null || !runtime.TryEnqueueScopeControl(packet.ClaimedPlayerId, op, scope,
                (ScopeRecoveryReason)packet.Reason, packet.DigestOnly, packet.SubscriptionEpoch,
                packet.LastAppliedSequence))
        {
            Log.Warn($"[authority] scope control {op} for {scope} refused: host is not an authority " +
                     "world or the queue is full");
            return;
        }
    }

    private static long HostTick() => Multiplayer.Session.IsGameLoaded ? GameMain.gameTick : 0;
}

/// <summary>Host to client: the canonical digest of one scope at one stream position (DESIGN 9.3, A20).</summary>
/// <remarks>
/// The digest is queued for the frame boundary like every stream-adjacent message, so verification
/// happens against the replica state the frame has actually applied — a digest applied on the socket
/// thread could race the stream it is checking.
/// </remarks>
[RegisterPacketProcessor]
internal class AuthorityScopeDigestProcessor : PacketProcessor<AuthorityScopeDigestPacket>
{
    protected override void ProcessPacket(AuthorityScopeDigestPacket packet, NebulaConnection conn)
    {
        if (IsHost) return;
        if (!AuthorityPacketAdmission.Admit(packet, AuthorityDirection.ServerToClient, conn, 0, out var reject))
        {
            Log.Warn($"[authority] scope digest refused: {reject}");
            return;
        }
        if (!packet.TryGetScopeKey(out var scope))
        {
            Log.Warn($"[authority] scope digest has an illegal scope {packet.ScopeKind}:{packet.Scope}");
            return;
        }
        if (!packet.HasValidBookkeeping())
        {
            Log.Warn($"[authority] scope digest has invalid bookkeeping sequence={packet.DeclaredStreamSequence} " +
                     $"members={packet.MemberCount} epoch={packet.SubscriptionEpoch}");
            return;
        }

        var runtime = Multiplayer.Session.AuthorityRuntime;
        var applyScope = new ApplyScope(scope, transactionId: 0, streamSequence: packet.Sequence,
            hostTick: packet.HostTick);
        if (runtime is null || !runtime.TryEnqueueReplicaMessage(packet, applyScope))
        {
            Log.Warn($"[authority] scope digest for {scope} refused: replica inbox is unavailable or full");
            return;
        }
    }
}
