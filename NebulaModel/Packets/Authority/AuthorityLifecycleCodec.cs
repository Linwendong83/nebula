#region

using System.Collections.Generic;
using NebulaModel.Authority;

#endregion

namespace NebulaModel.Packets.Authority;

/// <summary>Which membership change one lifecycle record publishes.</summary>
public enum AuthorityLifecycleOp : byte
{
    /// <summary>Not a lifecycle operation. A record with this value is refused.</summary>
    None = 0,

    /// <summary>The object exists. Its identity is established; state may follow.</summary>
    Spawn = 1,

    /// <summary>The object of this exact key is dead. The revision is its final one.</summary>
    Despawn = 2
}

/// <summary>One membership change inside an <see cref="AuthorityLifecyclePacket"/>.</summary>
public readonly struct AuthorityLifecycleRecord
{
    public AuthorityLifecycleRecord(AuthorityLifecycleOp op, in ObjectKey key, long revision)
    {
        Op = op;
        Key = key;
        Revision = revision;
    }

    public AuthorityLifecycleOp Op { get; }

    public ObjectKey Key { get; }

    /// <summary>Revision the change was published at. For a despawn this is the final revision.</summary>
    public long Revision { get; }
}

/// <summary>
/// Encoding of the records inside one <see cref="AuthorityLifecyclePacket"/>.
/// </summary>
/// <remarks>
/// <para>
/// The layout is fixed: record count, then per record the operation byte, the object key in the
/// <see cref="ObjectKeyCodec"/> layout and the revision. Every record is exactly
/// <see cref="AuthorityLimits.LifecycleRecordBytes"/> long, so a batch's size is a linear function
/// of its count and neither side can be surprised by the other's framing.
/// </para>
/// <para>
/// Like the world-state codec, every key is validated against the session while decoding, so an
/// illegal scope or a foreign epoch is refused before a replica can see the record.
/// </para>
/// </remarks>
public static class AuthorityLifecycleCodec
{
    /// <summary>
    /// Encodes records, refusing a batch that would exceed the batch ceiling.
    /// </summary>
    public static bool TryEncode(IReadOnlyList<AuthorityLifecycleRecord> records, out byte[] data)
    {
        data = null;
        if (records == null) return false;
        if (records.Count > AuthorityLimits.LifecycleRecordCountMax) return false;

        var writer = new AuthorityPayloadWriter();
        writer.WriteInt(records.Count);
        foreach (var record in records)
        {
            if (record.Op == AuthorityLifecycleOp.None) return false;
            writer.WriteByte((byte)record.Op);
            var keyBytes = new byte[AuthorityLimits.ObjectKeyBytes];
            ObjectKeyCodec.WriteTo(keyBytes, 0, record.Key);
            writer.TryWriteBytes(keyBytes, AuthorityLimits.ObjectKeyBytes);
            writer.WriteLong(record.Revision);
        }

        data = writer.ToArray();
        return data.Length <= AuthorityLimits.PayloadMaxBytes(AuthorityFamily.Lifecycle);
    }

    /// <summary>
    /// Decodes records from a validated payload region.
    /// </summary>
    /// <remarks>
    /// The declared record count must equal the number actually present and every record must fit
    /// exactly, so a truncated or padded batch is refused rather than applied in part.
    /// </remarks>
    public static bool TryDecode(byte[] source, int offset, int length, int declaredRecordCount,
        in AuthoritySessionContext context, out List<AuthorityLifecycleRecord> records, out AuthorityReject reject)
    {
        records = null;
        reject = AuthorityReject.Accepted;
        if (!AuthorityPayloadReader.TryCreate(source, offset, length, out var reader, out reject)) return false;
        if (!reader.TryReadInt(out var count))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "missing record count");
            return false;
        }
        if (count < 0 || count > AuthorityLimits.LifecycleRecordCountMax)
        {
            reject = new AuthorityReject(AuthorityRejectCode.PayloadTooLarge, "records=" + count);
            return false;
        }
        if (count != declaredRecordCount)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope,
                "declared=" + declaredRecordCount + " encoded=" + count);
            return false;
        }

        var result = new List<AuthorityLifecycleRecord>(count);
        for (var i = 0; i < count; i++)
        {
            if (!reader.TryReadEnumByte(out AuthorityLifecycleOp op) || op == AuthorityLifecycleOp.None)
            {
                reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "record " + i + " op");
                return false;
            }
            if (!reader.TryReadBytes(AuthorityLimits.ObjectKeyBytes, out var keyBytes))
            {
                reject = new AuthorityReject(AuthorityRejectCode.InvalidObjectKey, "record " + i);
                return false;
            }
            if (!ObjectKeyCodec.TryRead(keyBytes, 0, context, out var key, out reject)) return false;
            if (!reader.TryReadLong(out var revision) || revision <= 0)
            {
                reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "record " + i + " revision");
                return false;
            }
            result.Add(new AuthorityLifecycleRecord(op, key, revision));
        }

        if (!reader.EndOfPayload)
        {
            // Trailing bytes mean the sender and receiver disagree about the record layout.
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "trailing bytes");
            return false;
        }

        records = result;
        return true;
    }
}
