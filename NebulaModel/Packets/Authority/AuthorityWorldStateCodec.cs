#region

using System.Collections.Generic;
using NebulaModel.Authority;

#endregion

namespace NebulaModel.Packets.Authority;

/// <summary>
/// Encoding of the records inside one <see cref="AuthorityWorldStatePacket"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each record is written and read field by field, with the state blob length-prefixed and capped
/// per record. Nothing here reflects over property names, so a renamed field cannot silently change
/// the wire format (TASKS.md A12).
/// </para>
/// <para>
/// The codec works on plain arrays, so the whole batch path is testable without a game process. The
/// packet's own declared payload length already bounds the total; the per-record ceiling is what
/// stops one record from consuming the whole batch budget.
/// </para>
/// </remarks>
public static class AuthorityWorldStateCodec
{
    /// <summary>
    /// Encodes records, refusing a batch that would exceed the batch ceiling.
    /// </summary>
    /// <param name="records">Records to encode.</param>
    /// <param name="data">Encoded blob, or null when the batch is too large.</param>
    public static bool TryEncode(IReadOnlyList<AuthorityWorldStateRecord> records, out byte[] data)
    {
        data = null;
        if (records == null) return false;
        if (records.Count > AuthorityLimits.StateRecordCountMax) return false;

        var writer = new AuthorityPayloadWriter();
        writer.WriteInt(records.Count);
        foreach (var record in records)
        {
            var keyBytes = new byte[AuthorityLimits.ObjectKeyBytes];
            ObjectKeyCodec.WriteTo(keyBytes, 0, record.Key);
            writer.WriteInt(keyBytes.Length);
            writer.TryWriteBytes(keyBytes, AuthorityLimits.ObjectKeyBytes);
            writer.WriteLong(record.Revision);
            if (record.State == null || record.State.Length > AuthorityLimits.StateRecordMaxBytes) return false;
            writer.WriteInt(record.State.Length);
            writer.TryWriteBytes(record.State, AuthorityLimits.StateRecordMaxBytes);
        }

        data = writer.ToArray();
        return data.Length <= AuthorityLimits.PayloadMaxBytes(AuthorityFamily.WorldState);
    }

    /// <summary>
    /// Decodes records from a validated payload region.
    /// </summary>
    /// <remarks>
    /// The declared record count must equal the number actually present, so a truncated or padded
    /// batch is refused rather than silently applied in part. Every key is checked against the
    /// session, which is where an illegal scope or a foreign epoch is caught.
    /// </remarks>
    public static bool TryDecode(byte[] source, int offset, int length, int declaredRecordCount,
        in AuthoritySessionContext context, out List<AuthorityWorldStateRecord> records, out AuthorityReject reject)
    {
        records = null;
        reject = AuthorityReject.Accepted;
        if (!AuthorityPayloadReader.TryCreate(source, offset, length, out var reader, out reject)) return false;
        if (!reader.TryReadInt(out var count))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "missing record count");
            return false;
        }
        if (count < 0 || count > AuthorityLimits.StateRecordCountMax)
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

        var result = new List<AuthorityWorldStateRecord>(count);
        for (var i = 0; i < count; i++)
        {
            if (!reader.TryReadInt(out var keyLength) || keyLength != AuthorityLimits.ObjectKeyBytes)
            {
                reject = new AuthorityReject(AuthorityRejectCode.InvalidObjectKey, "record " + i);
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
            if (!reader.TryReadInt(out var stateLength) || stateLength < 0 ||
                stateLength > AuthorityLimits.StateRecordMaxBytes)
            {
                reject = new AuthorityReject(AuthorityRejectCode.PayloadTooLarge, "record " + i + " state");
                return false;
            }
            if (!reader.TryReadBytes(AuthorityLimits.StateRecordMaxBytes, out var state))
            {
                reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "record " + i + " state");
                return false;
            }
            result.Add(new AuthorityWorldStateRecord(key, revision, state));
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

/// <summary>One object's absolute state inside a world-state batch.</summary>
public readonly struct AuthorityWorldStateRecord
{
    public AuthorityWorldStateRecord(in ObjectKey key, long revision, byte[] state)
    {
        Key = key;
        Revision = revision;
        State = state;
    }

    public ObjectKey Key { get; }

    public long Revision { get; }

    /// <summary>Opaque canonical state. Its layout is owned by the adapter that produced it.</summary>
    public byte[] State { get; }
}
