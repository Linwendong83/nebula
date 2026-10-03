#region

using NebulaModel.Authority;
using NebulaModel.Logger;
using NebulaModel.Packets.Authority;

#endregion

namespace NebulaWorld.Authority;

/// <summary>
/// Drives the host replicator from the A04 capture seam.
/// </summary>
/// <remarks>
/// The frame patch calls <see cref="IAuthorityFrameCapture.Capture"/> at the point A01 proved
/// quiescent; this adapter forwards it to the replicator's canonical scan. It exists so the
/// replicator itself can stay a pure-model type in NebulaModel, free of the session types.
/// </remarks>
public sealed class HostReplicationCapture : IAuthorityFrameCapture
{
    private readonly HostWorldReplicator replicator;

    public HostReplicationCapture(HostWorldReplicator replicator)
    {
        this.replicator = replicator ?? throw new System.ArgumentNullException(nameof(replicator));
    }

    public void Capture(long hostTick) => replicator.CaptureFrame(hostTick);
}

/// <summary>
/// The A06 replica-message applier: decodes queued envelopes and writes the mirror — but only
/// inside the apply window the session opened for exactly that message's scope.
/// </summary>
/// <remarks>
/// <para>
/// This is the adapter A04's <see cref="IReplicaMessageApplier"/> seam was waiting for. The session
/// drains its inbox at the frame boundary and opens an <see cref="ReplicaApplyContext"/> scope per
/// message; before this applier writes anything it re-checks <see cref="ReplicaApplyContext.Allows"/>,
/// so a mirror write is only possible inside a validated, scope-bound apply (A05's requirement that
/// the two mechanisms connect here, with one permission path).
/// </para>
/// <para>
/// The scope named by the message is also re-derived from the packet and required to match, so a
/// packet that claims planet 101 cannot be applied under an open window for planet 102.
/// </para>
/// </remarks>
public sealed class AuthorityReplicaApplier : IReplicaMessageApplier
{
    private readonly AuthoritySessionState identity;
    private readonly ReplicaApplyContext applyContext;
    private readonly ClientWorldReplica replica;

    public AuthorityReplicaApplier(AuthoritySessionState identity, ReplicaApplyContext applyContext,
        ClientWorldReplica replica)
    {
        this.identity = identity ?? throw new System.ArgumentNullException(nameof(identity));
        this.applyContext = applyContext ?? throw new System.ArgumentNullException(nameof(applyContext));
        this.replica = replica ?? throw new System.ArgumentNullException(nameof(replica));
    }

    /// <summary>The replica this applier writes.</summary>
    public ClientWorldReplica Replica => replica;

    public bool Apply(in PendingReplicaMessage message)
    {
        if (message.Packet == null) return false;
        if (!applyContext.Allows(message.Scope))
        {
            // Not inside the window opened for this scope: refuse rather than write. The session's
            // drain always opens the window, so reaching this is a wiring defect, not a race.
            LogRefusal(message, "no-open-apply-window");
            return false;
        }

        var applied = message.Packet switch
        {
            AuthorityLifecyclePacket lifecycle => ApplyLifecycle(lifecycle, message.Scope),
            AuthorityWorldStatePacket worldState => ApplyWorldState(worldState, message.Scope),
            AuthoritySnapshotBeginPacket begin => ApplySnapshotBegin(begin, message.Scope),
            AuthoritySnapshotChunkPacket chunk => ApplySnapshotChunk(chunk, message.Scope),
            AuthoritySnapshotCommitPacket commit => ApplySnapshotCommit(commit, message.Scope),
            AuthorityScopeDigestPacket digest => ApplyScopeDigest(digest, message.Scope),
            _ => false
        };
        if (!applied)
        {
            LogRefusal(message, "applier-refused");
        }
        return applied;
    }

    private long refusalsLogged;

    /// <summary>
    /// Diagnostic (A22 matrix): a refused replica message used to be a silent counter. During
    /// bring-up that hid why a scope never left Snapshotting, so the first few refusals per
    /// process are named. Bounded so a broken stream cannot spam the log.
    /// </summary>
    private void LogRefusal(in PendingReplicaMessage message, string reason)
    {
        if (System.Threading.Interlocked.Increment(ref refusalsLogged) > 40) return;
        Log.Warn($"[authority] replica message refused ({reason}): " +
                 $"{message.Packet.GetType().Name} scope={message.Scope} " +
                 $"seq={message.Packet.Sequence} subEpoch={message.Packet.SubscriptionEpoch}");
    }

    private bool ApplyScopeDigest(AuthorityScopeDigestPacket packet, in ApplyScope messageScope)
    {
        if (!packet.TryGetScopeKey(out var scope) || !scope.Equals(messageScope.Scope))
        {
            LogScopeGuard("digest", scope, messageScope.Scope);
            return false;
        }
        // The digest is verified inside the same apply window as the stream it checks, so a
        // mismatch suspends the scope before any later queued message can write it.
        return replica.ReceiveScopeDigest(scope, packet.SubscriptionEpoch, packet.HostTick,
            packet.DeclaredStreamSequence, packet.BaselineId, packet.MemberCount, packet.Digest);
    }

    /// <summary>Bounded diagnostic: a scope guard rejected a stream packet (A22 matrix bring-up).</summary>
    private void LogScopeGuard(string what, in ScopeKey packetScope, in ScopeKey windowScope)
    {
        if (System.Threading.Interlocked.Increment(ref refusalsLogged) > 40) return;
        Log.Warn($"[authority] replica {what} scope guard: packet={packetScope} window={windowScope}");
    }

    private bool ApplyLifecycle(AuthorityLifecyclePacket packet, in ApplyScope messageScope)
    {
        if (!packet.TryGetScopeKey(out var scope) || !scope.Equals(messageScope.Scope)) return false;
        if (!AuthorityLifecycleCodec.TryDecode(packet.Data, 0, packet.Data.Length, packet.RecordCount,
                identity.Context, out var records, out _))
        {
            return false;
        }
        return replica.ApplyLifecycle(scope, packet.SubscriptionEpoch, packet.Sequence, records);
    }

    private bool ApplyWorldState(AuthorityWorldStatePacket packet, in ApplyScope messageScope)
    {
        if (!packet.TryGetScopeKey(out var scope) || !scope.Equals(messageScope.Scope)) return false;
        if (!AuthorityWorldStateCodec.TryDecode(packet.Data, 0, packet.Data.Length, packet.RecordCount,
                identity.Context, out var records, out _))
        {
            return false;
        }
        return replica.ApplyWorldState(scope, packet.SubscriptionEpoch, packet.Sequence,
            packet.DeclaredBaselineId, records);
    }

    private bool ApplySnapshotBegin(AuthoritySnapshotBeginPacket packet, in ApplyScope messageScope)
    {
        if (!packet.TryGetScopeKey(out var scope) || !scope.Equals(messageScope.Scope)) return false;
        return replica.BeginSnapshot(scope, packet.SubscriptionEpoch, packet.BaselineId, packet.ChunkCount,
            packet.TotalBytes, packet.SnapshotHash);
    }

    private bool ApplySnapshotChunk(AuthoritySnapshotChunkPacket packet, in ApplyScope messageScope)
    {
        if (!packet.TryGetScopeKey(out var scope) || !scope.Equals(messageScope.Scope)) return false;
        return replica.ReceiveSnapshotChunk(scope, packet.SubscriptionEpoch, packet.BaselineId,
            packet.ChunkIndex, packet.Data);
    }

    private bool ApplySnapshotCommit(AuthoritySnapshotCommitPacket packet, in ApplyScope messageScope)
    {
        if (!packet.TryGetScopeKey(out var scope) || !scope.Equals(messageScope.Scope)) return false;
        return replica.CommitSnapshot(scope, packet.SubscriptionEpoch, packet.BaselineId, packet.ChunkCount,
            packet.SnapshotHash);
    }
}
