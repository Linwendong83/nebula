#region

using NebulaModel.Authority;

#endregion

namespace NebulaModel.Packets.Authority;

/// <summary>
/// Common wire shape of every authority envelope (DESIGN 5.2).
/// </summary>
/// <remarks>
/// <para>
/// The fixed header is a set of scalars so the <see cref="AuthorityEnvelopeGate"/> can validate a
/// message without knowing its family payload. Concrete families add their own fields; the family
/// itself is derived from the concrete type rather than carried in a field, which is what keeps a
/// family mismatch a type-system question instead of a runtime enum comparison.
/// </para>
/// <para>
/// The epoch is split into two <c>ulong</c> fields because the network serializer has no 128-bit
/// integer. The split is an encoding detail of this DTO only; the model always sees one
/// <see cref="AuthorityEpoch"/>.
/// </para>
/// </remarks>
public abstract class AuthorityEnvelopePacket
{
    /// <summary>Authority DTO schema the sender speaks.</summary>
    public byte Schema { get; set; }

    /// <summary>High half of the authority epoch.</summary>
    public ulong EpochHigh { get; set; }

    /// <summary>Low half of the authority epoch.</summary>
    public ulong EpochLow { get; set; }

    /// <summary>Connection epoch the message belongs to, or zero for host-originated streams.</summary>
    public ulong ConnectionEpoch { get; set; }

    /// <summary>Per-connection stream position.</summary>
    public long Sequence { get; set; }

    /// <summary>Host rule tick the message was produced on.</summary>
    public long HostTick { get; set; }

    /// <summary>Player id the sender claims. Checked against the connection, never trusted.</summary>
    public ushort ClaimedPlayerId { get; set; }

    /// <summary>
    /// Subscription epoch a per-scope stream packet belongs to (DESIGN 4.2). The families that
    /// ride the stream set it; the replica refuses any packet whose value is not its subscription's.
    /// Snapshot control packets and the welcome leave it at zero — their conversation is complete
    /// per message and does not share the stream's sequence space.
    /// </summary>
    public long SubscriptionEpoch { get; set; }

    /// <summary>Family this concrete type carries. Not serialized: it is the type itself.</summary>
    public abstract AuthorityFamily Family { get; }

    /// <summary>Reassembles the header for validation.</summary>
    public AuthorityEnvelopeHeader ToHeader(int payloadLength) =>
        new((AuthoritySchema)Schema, Family, new AuthorityEpoch(EpochHigh, EpochLow),
            new ConnectionEpoch(ConnectionEpoch), Sequence, HostTick, ClaimedPlayerId, payloadLength);

    /// <summary>Fills the header fields from a validated header.</summary>
    protected void CopyHeaderFrom(in AuthorityEnvelopeHeader header)
    {
        Schema = (byte)header.Schema;
        EpochHigh = header.Epoch.High;
        EpochLow = header.Epoch.Low;
        ConnectionEpoch = header.Connection.Value;
        Sequence = header.Sequence;
        HostTick = header.HostTick;
        ClaimedPlayerId = header.ClaimedPlayerId;
    }

    /// <summary>Number of bytes this message declares as payload, for the gate's size check.</summary>
    public virtual int DeclaredPayloadLength => 0;

    /// <summary>
    /// Returns a message a deferred consumer can own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transport registers every processor through <c>SubscribeReusable</c>, which deserializes
    /// each incoming packet into the <em>same</em> instance per type. A processor that queues the
    /// packet for the frame boundary (A04's "收包只入队") would then apply whatever the next packet
    /// of that type overwrote it with. A22 found exactly that: queued snapshot/digest messages
    /// arrived at the applier naming a different scope than the one they were enqueued for.
    /// </para>
    /// <para>
    /// The copy is shallow for value fields, which is correct because the reusable deserializer
    /// assigns fresh arrays rather than mutating the existing ones; the four array-carrying
    /// families override <see cref="CloneMutableBuffers"/> to copy their buffer as well, so the
    /// guarantee does not depend on the serializer's allocation behaviour.
    /// </para>
    /// </remarks>
    public AuthorityEnvelopePacket CreateOwnedCopy()
    {
        var copy = (AuthorityEnvelopePacket)MemberwiseClone();
        copy.CloneMutableBuffers();
        return copy;
    }

    /// <summary>Deep-copies this family's mutable buffers onto a freshly cloned message.</summary>
    protected virtual void CloneMutableBuffers()
    {
    }
}

/// <summary>
/// Host to client: the room's authority identity (DESIGN 5.2).
/// </summary>
/// <remarks>
/// The welcome is what turns a client from "connected" into "a peer of this authority session": it
/// carries the mode, schema, capability list, world epoch and the connection epoch the client must
/// use on its commands.
/// </remarks>
public class AuthorityWelcomePacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.Welcome;

    /// <summary>Mode the room runs. Must equal what the client negotiated.</summary>
    public byte Mode { get; set; }

    /// <summary>Capabilities the host advertises.</summary>
    public uint Capabilities { get; set; }

    /// <summary>Host tick at the moment of the welcome, so a client can anchor its clock.</summary>
    public long WelcomeHostTick { get; set; }

    public static AuthorityWelcomePacket Create(in AuthorityEnvelopeHeader header, AuthorityMode mode,
        AuthorityCapability capabilities)
    {
        var packet = new AuthorityWelcomePacket { Mode = (byte)mode, Capabilities = (uint)capabilities, WelcomeHostTick = header.HostTick };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}

/// <summary>Client to host: one intent (DESIGN 5.1).</summary>
/// <remarks>
/// The command carries its dedup key, its target, and an opaque versioned payload. It never carries
/// a trusted final damage, balance or kill result; those are the host's to compute.
/// </remarks>
public class AuthorityCommandPacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.Command;

    /// <summary>Target object, or an invalid key when the command has no object target.</summary>
    public byte TargetKind { get; set; }
    public int TargetScope { get; set; }
    public int TargetNativeId { get; set; }
    public long TargetGeneration { get; set; }
    public ulong TargetEpochHigh { get; set; }
    public ulong TargetEpochLow { get; set; }

    /// <summary>Command category from DESIGN 5.1, kept a byte so an unknown value is detectable.</summary>
    public byte Category { get; set; }

    /// <summary>Versioned command body. Bounded by <see cref="AuthorityLimits.CommandPayloadMaxBytes"/>.</summary>
    public byte[] Payload { get; set; }

    public override int DeclaredPayloadLength => Payload?.Length ?? 0;

    protected override void CloneMutableBuffers()
    {
        if (Payload != null) Payload = (byte[])Payload.Clone();
    }

    /// <summary>Reassembles the target key, or false when the command targets no object.</summary>
    public bool TryGetTargetKey(out ObjectKey key)
    {
        key = new ObjectKey(new AuthorityEpoch(TargetEpochHigh, TargetEpochLow), (PoolKind)TargetKind,
            TargetScope, TargetNativeId, TargetGeneration);
        return TargetNativeId > 0;
    }

    public static AuthorityCommandPacket Create(in AuthorityEnvelopeHeader header, in ObjectKey target,
        byte category, byte[] payload)
    {
        var packet = new AuthorityCommandPacket
        {
            TargetKind = (byte)target.Kind,
            TargetScope = target.Scope,
            TargetNativeId = target.NativeId,
            TargetGeneration = target.Generation,
            TargetEpochHigh = target.Epoch.High,
            TargetEpochLow = target.Epoch.Low,
            Category = category,
            Payload = payload
        };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}

/// <summary>Host to client: the answer to one command (DESIGN 5.2).</summary>
public class AuthorityCommandResultPacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.CommandResult;

    /// <summary>Result code from <see cref="CommandResultCode"/>.</summary>
    public byte ResultCode { get; set; }

    /// <summary>Host tick the command took effect on.</summary>
    public long AppliedHostTick { get; set; }

    /// <summary>Groups the lifecycle effects the command caused.</summary>
    public long TransactionId { get; set; }

    /// <summary>Resource ledger revision after the command.</summary>
    public long ResourceRevision { get; set; }

    public static AuthorityCommandResultPacket Create(in AuthorityEnvelopeHeader header, in CommandOutcome outcome)
    {
        var packet = new AuthorityCommandResultPacket
        {
            ResultCode = (byte)outcome.Code,
            AppliedHostTick = outcome.AppliedHostTick,
            TransactionId = outcome.TransactionId,
            ResourceRevision = outcome.ResourceRevision
        };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}

/// <summary>Host to client: a new baseline for one scope begins (DESIGN 9.2).</summary>
public class AuthoritySnapshotBeginPacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.SnapshotBegin;

    public byte ScopeKind { get; set; }
    public int Scope { get; set; }

    /// <summary>Identity of this baseline; increments must declare it.</summary>
    public long BaselineId { get; set; }

    /// <summary>Subscription epoch the chunks belong to; a resubscribe invalidates the old one.</summary>
    public long SubscriptionEpoch { get; set; }

    /// <summary>Stream sequence the baseline covers; the cutoff for tombstone reclamation.</summary>
    public long CutoffSequence { get; set; }

    /// <summary>Total bytes of the snapshot before chunking.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Number of chunks the client should expect.</summary>
    public int ChunkCount { get; set; }

    /// <summary>Hash of the whole snapshot, checked at commit.</summary>
    public ulong SnapshotHash { get; set; }

    /// <summary>Reassembles the scope key, or false when the scope is not legal for its kind.</summary>
    public bool TryGetScopeKey(out ScopeKey scopeKey)
    {
        scopeKey = new ScopeKey((PoolKind)ScopeKind, Scope);
        return scopeKey.IsValid;
    }

    public static AuthoritySnapshotBeginPacket Create(in AuthorityEnvelopeHeader header, in ScopeKey scope,
        long baselineId, long subscriptionEpoch, long cutoffSequence, long totalBytes, int chunkCount,
        ulong snapshotHash)
    {
        var packet = new AuthoritySnapshotBeginPacket
        {
            ScopeKind = (byte)scope.Kind,
            Scope = scope.Scope,
            BaselineId = baselineId,
            SubscriptionEpoch = subscriptionEpoch,
            CutoffSequence = cutoffSequence,
            TotalBytes = totalBytes,
            ChunkCount = chunkCount,
            SnapshotHash = snapshotHash
        };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}

/// <summary>Host to client: one bounded chunk of a baseline (DESIGN 5.2).</summary>
/// <remarks>
/// The chunk is the family whose size the design names explicitly, so its limit is enforced with a
/// dedicated reject code rather than the generic payload ceiling.
/// </remarks>
public class AuthoritySnapshotChunkPacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.SnapshotChunk;

    public byte ScopeKind { get; set; }
    public int Scope { get; set; }

    /// <summary>Baseline this chunk belongs to.</summary>
    public long BaselineId { get; set; }

    /// <summary>Subscription epoch the baseline was produced for.</summary>
    public long SubscriptionEpoch { get; set; }

    /// <summary>Zero-based chunk index.</summary>
    public int ChunkIndex { get; set; }

    /// <summary>Chunk bytes. Bounded by <see cref="AuthorityLimits.ChunkMaxBytes"/>.</summary>
    public byte[] Data { get; set; }

    public override int DeclaredPayloadLength => Data?.Length ?? 0;

    protected override void CloneMutableBuffers()
    {
        if (Data != null) Data = (byte[])Data.Clone();
    }

    /// <summary>Reassembles the scope key, or false when the scope is not legal for its kind.</summary>
    public bool TryGetScopeKey(out ScopeKey scopeKey)
    {
        scopeKey = new ScopeKey((PoolKind)ScopeKind, Scope);
        return scopeKey.IsValid;
    }

    public static AuthoritySnapshotChunkPacket Create(in AuthorityEnvelopeHeader header, in ScopeKey scope,
        long baselineId, long subscriptionEpoch, int chunkIndex, byte[] data)
    {
        var packet = new AuthoritySnapshotChunkPacket
        {
            ScopeKind = (byte)scope.Kind,
            Scope = scope.Scope,
            BaselineId = baselineId,
            SubscriptionEpoch = subscriptionEpoch,
            ChunkIndex = chunkIndex,
            Data = data
        };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}

/// <summary>Host to client: the baseline is complete (DESIGN 9.2).</summary>
public class AuthoritySnapshotCommitPacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.SnapshotCommit;

    public byte ScopeKind { get; set; }
    public int Scope { get; set; }

    public long BaselineId { get; set; }

    /// <summary>Subscription epoch the baseline was produced for.</summary>
    public long SubscriptionEpoch { get; set; }

    public int ChunkCount { get; set; }

    /// <summary>Hash of the reassembled snapshot. A mismatch discards the staging buffer.</summary>
    public ulong SnapshotHash { get; set; }

    /// <summary>Reassembles the scope key, or false when the scope is not legal for its kind.</summary>
    public bool TryGetScopeKey(out ScopeKey scopeKey)
    {
        scopeKey = new ScopeKey((PoolKind)ScopeKind, Scope);
        return scopeKey.IsValid;
    }

    public static AuthoritySnapshotCommitPacket Create(in AuthorityEnvelopeHeader header, in ScopeKey scope,
        long baselineId, long subscriptionEpoch, int chunkCount, ulong snapshotHash)
    {
        var packet = new AuthoritySnapshotCommitPacket
        {
            ScopeKind = (byte)scope.Kind,
            Scope = scope.Scope,
            BaselineId = baselineId,
            SubscriptionEpoch = subscriptionEpoch,
            ChunkCount = chunkCount,
            SnapshotHash = snapshotHash
        };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}

/// <summary>Client to host: a baseline was applied up to a sequence (DESIGN 9.2).</summary>
public class AuthoritySnapshotAckPacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.SnapshotAck;

    public long BaselineId { get; set; }

    /// <summary>Highest sequence applied contiguously. The host may reclaim tombstones up to it.</summary>
    public long LastAppliedSequence { get; set; }

    /// <summary>False when the client rejected the baseline and is asking for a new one.</summary>
    public bool Accepted { get; set; }

    public static AuthoritySnapshotAckPacket Create(in AuthorityEnvelopeHeader header, long baselineId,
        long lastAppliedSequence, bool accepted)
    {
        var packet = new AuthoritySnapshotAckPacket
        {
            BaselineId = baselineId,
            LastAppliedSequence = lastAppliedSequence,
            Accepted = accepted
        };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}

/// <summary>Host to client: absolute state for known objects of one scope (DESIGN 5.2).</summary>
/// <remarks>
/// <para>
/// HP is sent as an absolute value with its maximum, never as a delta for the client to accumulate,
/// and the batch declares the baseline its revisions belong to. A batch whose declared baseline
/// does not match the installed one is refused instead of applied to a different world.
/// </para>
/// <para>
/// The records travel as one opaque blob rather than as parallel arrays, and are written and read
/// with <see cref="AuthorityWorldStateCodec"/>. The blob is bounded twice: by this packet's declared
/// payload length and again per record inside the codec, so a peer cannot make the reader allocate
/// more than the ceiling regardless of what the blob claims.
/// </para>
/// </remarks>
public class AuthorityWorldStatePacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.WorldState;

    public byte ScopeKind { get; set; }
    public int Scope { get; set; }

    /// <summary>Baseline these increments are computed against.</summary>
    public long DeclaredBaselineId { get; set; }

    /// <summary>Number of records the blob contains. Checked against the decoded count.</summary>
    public int RecordCount { get; set; }

    /// <summary>Encoded records, bounded by <see cref="AuthorityLimits.StateRecordMaxBytes"/> each.</summary>
    public byte[] Data { get; set; }

    public override int DeclaredPayloadLength => Data?.Length ?? 0;

    protected override void CloneMutableBuffers()
    {
        if (Data != null) Data = (byte[])Data.Clone();
    }

    /// <summary>
    /// True when the batch's own bookkeeping agrees with its blob.
    /// </summary>
    /// <remarks>
    /// The declared record count must be inside the ceiling and the blob must be present. The
    /// count actually encoded in the blob is checked during decoding, where a mismatch is refused
    /// rather than applied in part.
    /// </remarks>
    public bool AreRecordsConsistent() =>
        Data != null && RecordCount >= 0 && RecordCount <= AuthorityLimits.StateRecordCountMax;

    public bool TryGetScopeKey(out ScopeKey scopeKey)
    {
        scopeKey = new ScopeKey((PoolKind)ScopeKind, Scope);
        return scopeKey.IsValid;
    }

    public static AuthorityWorldStatePacket Create(in AuthorityEnvelopeHeader header, in ScopeKey scope,
        long declaredBaselineId, int recordCount, byte[] data)
    {
        var packet = new AuthorityWorldStatePacket
        {
            ScopeKind = (byte)scope.Kind,
            Scope = scope.Scope,
            DeclaredBaselineId = declaredBaselineId,
            RecordCount = recordCount,
            Data = data
        };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}

/// <summary>
/// Host to client: membership changes of one scope (DESIGN 5.2's WorldLifecycleBatch, A06).
/// </summary>
/// <remarks>
/// <para>
/// A spawn establishes identity; a despawn publishes death with its final revision. State records
/// travel in <see cref="AuthorityWorldStatePacket"/> and may only be applied to an identity this
/// family already established, which is what makes "delta before spawn" a refused message instead
/// of a guessed object. The records are encoded by <see cref="AuthorityLifecycleCodec"/>.
/// </para>
/// </remarks>
public class AuthorityLifecyclePacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.Lifecycle;

    public byte ScopeKind { get; set; }
    public int Scope { get; set; }

    /// <summary>Number of records the blob contains. Checked against the decoded count.</summary>
    public int RecordCount { get; set; }

    /// <summary>Encoded records, each exactly <see cref="AuthorityLimits.LifecycleRecordBytes"/>.</summary>
    public byte[] Data { get; set; }

    public override int DeclaredPayloadLength => Data?.Length ?? 0;

    protected override void CloneMutableBuffers()
    {
        if (Data != null) Data = (byte[])Data.Clone();
    }

    /// <summary>True when the batch's own bookkeeping agrees with its blob.</summary>
    public bool AreRecordsConsistent() =>
        Data != null && RecordCount >= 0 && RecordCount <= AuthorityLimits.LifecycleRecordCountMax;

    public bool TryGetScopeKey(out ScopeKey scopeKey)
    {
        scopeKey = new ScopeKey((PoolKind)ScopeKind, Scope);
        return scopeKey.IsValid;
    }

    public static AuthorityLifecyclePacket Create(in AuthorityEnvelopeHeader header, in ScopeKey scope,
        int recordCount, byte[] data)
    {
        var packet = new AuthorityLifecyclePacket
        {
            ScopeKind = (byte)scope.Kind,
            Scope = scope.Scope,
            RecordCount = recordCount,
            Data = data
        };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}

/// <summary>Client to host: subscribe, unsubscribe or resync one scope (DESIGN 5.1/9.3, A20).</summary>
/// <remarks>
/// <para>
/// Subscription is the client asking for delivery, never for simulation: the host runs every scope
/// regardless. A resync is the replica's one recovery path after its stream or digest broke — the
/// host answers with a fresh baseline, and the old stream's tail stays refusable by the new
/// subscription epoch.
/// </para>
/// <para>
/// The player id rides the header as an assertion only; the host resolves identity and scope
/// eligibility from its own registry, so a forged request changes nothing but a log line.
/// </para>
/// </remarks>
public class AuthorityScopeControlPacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.ScopeControl;

    /// <summary>What is being asked, from <see cref="ScopeControlOp"/>.</summary>
    public byte Op { get; set; }

    public byte ScopeKind { get; set; }
    public int Scope { get; set; }

    /// <summary>True for a digest-only subscription: summaries without baseline or stream.</summary>
    public bool DigestOnly { get; set; }

    /// <summary>Why a resync was requested, from <see cref="ScopeRecoveryReason"/>; zero otherwise.</summary>
    public byte Reason { get; set; }

    /// <summary>The client's current subscription epoch for the scope, or 0 when it has none.</summary>
    public long SubscriptionEpoch { get; set; }

    /// <summary>The client's last applied stream sequence, for host diagnostics only.</summary>
    public long LastAppliedSequence { get; set; }

    /// <summary>Reassembles the scope key, or false when the scope is not legal for its kind.</summary>
    public bool TryGetScopeKey(out ScopeKey scopeKey)
    {
        scopeKey = new ScopeKey((PoolKind)ScopeKind, Scope);
        return scopeKey.IsValid;
    }

    public static AuthorityScopeControlPacket Create(in AuthorityEnvelopeHeader header, ScopeControlOp op,
        in ScopeKey scope, bool digestOnly, ScopeRecoveryReason reason, long subscriptionEpoch,
        long lastAppliedSequence)
    {
        var packet = new AuthorityScopeControlPacket
        {
            Op = (byte)op,
            ScopeKind = (byte)scope.Kind,
            Scope = scope.Scope,
            DigestOnly = digestOnly,
            Reason = (byte)reason,
            SubscriptionEpoch = subscriptionEpoch,
            LastAppliedSequence = lastAppliedSequence
        };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}

/// <summary>Host to client: the canonical digest of one scope at one stream position (DESIGN 9.3, A20).</summary>
/// <remarks>
/// <para>
/// The digest is the periodic integrity check and the observation summary: for a live subscriber it
/// must match the replica's mirrors exactly once the replica has applied
/// <see cref="DeclaredStreamSequence"/> — earlier is a future fact to defer, later is superseded. A
/// mismatch suspends the scope and requests a baseline; it is never papered over.
/// </para>
/// <para>
/// For a digest-only observer the same message *is* the summary: membership count and content hash
/// of a scope the observer deliberately does not hold.
/// </para>
/// </remarks>
public class AuthorityScopeDigestPacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.ScopeDigest;

    public byte ScopeKind { get; set; }
    public int Scope { get; set; }

    /// <summary>Subscription epoch the digest was stamped for; a superseded subscription ignores it.</summary>
    public long SubscriptionEpoch { get; set; }

    /// <summary>Baseline the digested membership belongs to; 0 for a digest-only observation.</summary>
    public long BaselineId { get; set; }

    /// <summary>Stream sequence the digest describes. The replica must have applied exactly it.</summary>
    public long DeclaredStreamSequence { get; set; }

    /// <summary>Number of live members the digest covers.</summary>
    public int MemberCount { get; set; }

    /// <summary>Content hash over member identity, revisions and canonical state.</summary>
    public ulong Digest { get; set; }

    /// <summary>Reassembles the scope key, or false when the scope is not legal for its kind.</summary>
    public bool TryGetScopeKey(out ScopeKey scopeKey)
    {
        scopeKey = new ScopeKey((PoolKind)ScopeKind, Scope);
        return scopeKey.IsValid;
    }

    public static AuthorityScopeDigestPacket Create(in AuthorityEnvelopeHeader header, in ScopeKey scope,
        long subscriptionEpoch, long baselineId, long declaredStreamSequence, int memberCount, ulong digest)
    {
        var packet = new AuthorityScopeDigestPacket
        {
            ScopeKind = (byte)scope.Kind,
            Scope = scope.Scope,
            SubscriptionEpoch = subscriptionEpoch,
            BaselineId = baselineId,
            DeclaredStreamSequence = declaredStreamSequence,
            MemberCount = memberCount,
            Digest = digest
        };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}
