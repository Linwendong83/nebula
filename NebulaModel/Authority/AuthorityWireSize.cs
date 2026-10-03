#region

using NebulaModel.Packets.Authority;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// Bytes one authority envelope occupies on the wire, as the DTO's own fields determine it
/// (TASKS.md A23, VALIDATION §9's "以实测DTO尺寸估算").
/// </summary>
/// <remarks>
/// <para>
/// The number is built from three terms: the fixed envelope header, the per-family scalar framing
/// the concrete packet class declares, and the declared payload length — which for the batch
/// families is the exact length of the blob the codec produced, not an approximation. That makes the
/// dominant term (the payload) exact and the fixed terms reproducible, which is what a bandwidth
/// budget needs: a metric that is off by a constant per message is fine, one that is off by a factor
/// is not.
/// </para>
/// <para>
/// Two honest caveats are recorded here rather than discovered later. The family discriminator is not
/// serialized at all — the concrete type carries it — but <see cref="AuthorityLimits.HeaderBytes"/>
/// already counts one byte for it, so every estimate is conservative by exactly one byte. And the
/// serializer's own framing (length prefixes, string encoding) is not modelled: the real socket
/// byte count is compared against this estimate in the A23 run and the difference is reported, not
/// assumed away.
/// </para>
/// </remarks>
public static class AuthorityWireSize
{
    /// <summary>
    /// Bytes of the base envelope's subscription epoch field.
    /// </summary>
    /// <remarks>
    /// Counted once, globally, because it is an inherited field on every family: the families that
    /// re-declare it in their own class list the rest of their scalars here and this one carries the
    /// field, so no family is counted twice.
    /// </remarks>
    public const int SubscriptionEpochBytes = 8;

    /// <summary>Estimated wire bytes of one envelope.</summary>
    public static long Of(AuthorityEnvelopePacket packet) =>
        packet == null ? 0 : ForFamily(packet.Family, packet.DeclaredPayloadLength);

    /// <summary>Estimated wire bytes of a family with the given declared payload length.</summary>
    public static long ForFamily(AuthorityFamily family, int declaredPayloadLength)
    {
        var payload = declaredPayloadLength > 0 ? declaredPayloadLength : 0;
        return AuthorityLimits.HeaderBytes + SubscriptionEpochBytes + FramingBytes(family) + payload;
    }

    /// <summary>
    /// The family's fixed scalar framing, counted field by field from the concrete packet class.
    /// </summary>
    /// <remarks>
    /// An unknown family counts the payload only. That is deliberately not a panic path: the estimate
    /// is a metric, and refusing to measure an unmodelled family would take the session down over
    /// bookkeeping instead of reporting it.
    /// </remarks>
    public static int FramingBytes(AuthorityFamily family)
    {
        switch (family)
        {
            // Mode(1) + Capabilities(4) + WelcomeHostTick(8).
            case AuthorityFamily.Welcome:
                return 13;
            // TargetKind(1) + TargetScope(4) + TargetNativeId(4) + TargetGeneration(8)
            // + TargetEpoch(16) + Category(1).
            case AuthorityFamily.Command:
                return 34;
            // ResultCode(1) + AppliedHostTick(8) + TransactionId(8) + ResourceRevision(8).
            case AuthorityFamily.CommandResult:
                return 25;
            // ScopeKind(1) + Scope(4) + BaselineId(8) + CutoffSequence(8) + TotalBytes(8)
            // + ChunkCount(4) + SnapshotHash(8).
            case AuthorityFamily.SnapshotBegin:
                return 41;
            // ScopeKind(1) + Scope(4) + BaselineId(8) + ChunkIndex(4).
            case AuthorityFamily.SnapshotChunk:
                return 17;
            // ScopeKind(1) + Scope(4) + BaselineId(8) + ChunkCount(4) + SnapshotHash(8).
            case AuthorityFamily.SnapshotCommit:
                return 25;
            // BaselineId(8) + LastAppliedSequence(8) + Accepted(1).
            case AuthorityFamily.SnapshotAck:
                return 17;
            // ScopeKind(1) + Scope(4) + DeclaredBaselineId(8) + RecordCount(4).
            case AuthorityFamily.WorldState:
                return 17;
            // ScopeKind(1) + Scope(4) + RecordCount(4).
            case AuthorityFamily.Lifecycle:
                return 9;
            // Op(1) + ScopeKind(1) + Scope(4) + DigestOnly(1) + Reason(1) + LastAppliedSequence(8).
            case AuthorityFamily.ScopeControl:
                return 16;
            // ScopeKind(1) + Scope(4) + BaselineId(8) + DeclaredStreamSequence(8) + MemberCount(4)
            // + Digest(8).
            case AuthorityFamily.ScopeDigest:
                return 33;
            default:
                return 0;
        }
    }
}
