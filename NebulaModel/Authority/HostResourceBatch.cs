#region

using System;
using System.Collections.Generic;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// Priority of one resource movement inside a host frame (TASKS.md A10 acceptance ordering).
/// </summary>
/// <remarks>
/// <para>
/// Lower runs first. The order is what makes "工厂 UI 转移与生产同帧有确定顺序" structural:
/// factory production shares land before player transfers spend them, and spends run last so a
/// frame that both earns and spends has one answer no matter which packet arrived first.
/// </para>
/// <list type="number">
/// <item>ProductionIncome: factory/power share credited by the host (A10-3 boundary, W02 owns the full sim).</item>
/// <item>SupplyIncome: pickup, refund, fuel supply credited by the host.</item>
/// <item>Transfer: player take/put between package and a host-owned pool.</item>
/// <item>Spend: fire, repair, flight, manufacture consumption.</item>
/// </list>
/// <para>
/// The value is deliberately the sort key: sorting by it (then by kind, owner, item and sequence)
/// is the deterministic plan, not arrival order.
/// </para>
/// </remarks>
public enum HostResourceOpType : byte
{
    ProductionIncome = 0,
    SupplyIncome = 1,
    Transfer = 2,
    Spend = 3
}

/// <summary>
/// One resource movement a frame plan orders (primitives only, no game types).
/// </summary>
/// <remarks>
/// Spends and transfers carry the caller's <see cref="ExpectedRevision"/> for the balance they
/// touch; a stale read is refused rather than applied on top of a newer truth. Pure incomes
/// (production/supply credits) carry no expected revision: they add, never check-then-deduct.
/// <see cref="Sequence"/> is the stable tie-break (command sequence or plan order); it never
/// comes from a client wall clock.
/// </remarks>
public readonly struct HostResourceOp : IEquatable<HostResourceOp>
{
    public HostResourceOp(HostResourceOpType opType, LedgerOwner owner, LedgerResourceKind kind,
        int itemId, double doubleAmount, long longAmount, long expectedRevision, long sequence)
    {
        OpType = opType;
        Owner = owner;
        Kind = kind;
        ItemId = itemId;
        DoubleAmount = doubleAmount;
        LongAmount = longAmount;
        ExpectedRevision = expectedRevision;
        Sequence = sequence;
    }

    public HostResourceOpType OpType { get; }

    public LedgerOwner Owner { get; }

    public LedgerResourceKind Kind { get; }

    public int ItemId { get; }

    public double DoubleAmount { get; }

    public long LongAmount { get; }

    public long ExpectedRevision { get; }

    public long Sequence { get; }

    public LedgerResourceKey Key => new(Owner, Kind, ItemId);

    public bool IsDouble => LedgerResourceKey.IsDoubleKind(Kind);

    public static HostResourceOp SpendDouble(LedgerOwner owner, LedgerResourceKind kind,
        double amount, long expectedRevision, long sequence) =>
        new(HostResourceOpType.Spend, owner, kind, 0, amount, 0, expectedRevision, sequence);

    public static HostResourceOp SpendLong(LedgerOwner owner, LedgerResourceKind kind, int itemId,
        long amount, long expectedRevision, long sequence) =>
        new(HostResourceOpType.Spend, owner, kind, itemId, 0, amount, expectedRevision, sequence);

    public static HostResourceOp TransferLong(LedgerOwner owner, LedgerResourceKind kind, int itemId,
        long amount, long expectedRevision, long sequence) =>
        new(HostResourceOpType.Transfer, owner, kind, itemId, 0, amount, expectedRevision, sequence);

    public static HostResourceOp CreditDouble(HostResourceOpType incomeType, LedgerOwner owner,
        LedgerResourceKind kind, double amount, long sequence) =>
        new(incomeType, owner, kind, 0, amount, 0, 0, sequence);

    public static HostResourceOp CreditLong(HostResourceOpType incomeType, LedgerOwner owner,
        LedgerResourceKind kind, int itemId, long amount, long sequence) =>
        new(incomeType, owner, kind, itemId, 0, amount, 0, sequence);

    public bool Equals(HostResourceOp other) =>
        OpType == other.OpType && Owner.Equals(other.Owner) && Kind == other.Kind &&
        ItemId == other.ItemId && DoubleAmount.Equals(other.DoubleAmount) &&
        LongAmount == other.LongAmount && ExpectedRevision == other.ExpectedRevision &&
        Sequence == other.Sequence;

    public override bool Equals(object obj) => obj is HostResourceOp other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = (int)OpType;
            hash = (hash * 397) ^ Owner.GetHashCode();
            hash = (hash * 397) ^ (int)Kind;
            hash = (hash * 397) ^ ItemId;
            hash = (hash * 397) ^ DoubleAmount.GetHashCode();
            hash = (hash * 397) ^ LongAmount.GetHashCode();
            hash = (hash * 397) ^ ExpectedRevision.GetHashCode();
            hash = (hash * 397) ^ Sequence.GetHashCode();
            return hash;
        }
    }

    public static bool operator ==(HostResourceOp left, HostResourceOp right) => left.Equals(right);

    public static bool operator !=(HostResourceOp left, HostResourceOp right) => !left.Equals(right);

    public override string ToString() =>
        OpType + " " + Owner + "|" + Kind + "|item=" + ItemId + " seq=" + Sequence;
}

/// <summary>
/// Deterministic frame plan for resource movements (TASKS.md A10, pure model).
/// </summary>
/// <remarks>
/// <para>
/// The network may deliver production shares, transfers and spends in any order. The host sorts
/// them before touching the ledger, so the same set always commits in the same order and the
/// ledger's revision checks decide each spend against the same predecessor. Shuffling the input
/// must not change the output: that is the property the ordering test pins.
/// </para>
/// <para>
/// The plan only orders; it never touches the ledger itself. Callers execute the ordered ops
/// through <see cref="HostResourceLedger"/> (spends via <c>TryReserve</c>, incomes via
/// <c>Credit</c>) inside one transaction per command, committing only when every reserve in that
/// command succeeded. A failed reserve aborts its own transaction and refunds it; other
/// commands in the same frame are unaffected except through the shared revisions.
/// </para>
/// </remarks>
public static class HostResourceBatch
{
    /// <summary>
    /// Sorts movements into execution order. The result is a new list; the input is untouched.
    /// </summary>
    public static List<HostResourceOp> Order(IReadOnlyList<HostResourceOp> ops)
    {
        var ordered = new List<HostResourceOp>(ops.Count);
        for (var i = 0; i < ops.Count; i++) ordered.Add(ops[i]);
        ordered.Sort(Compare);
        return ordered;
    }

    internal static int Compare(HostResourceOp left, HostResourceOp right)
    {
        var byType = left.OpType.CompareTo(right.OpType);
        if (byType != 0) return byType;
        var byKind = left.Kind.CompareTo(right.Kind);
        if (byKind != 0) return byKind;
        var byOwner = string.CompareOrdinal(left.Owner.ToString(), right.Owner.ToString());
        if (byOwner != 0) return byOwner;
        var byItem = left.ItemId.CompareTo(right.ItemId);
        if (byItem != 0) return byItem;
        return left.Sequence.CompareTo(right.Sequence);
    }
}
