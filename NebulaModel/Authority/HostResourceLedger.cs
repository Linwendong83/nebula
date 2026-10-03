#region

using System;
using System.Collections.Generic;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// Which seat owns a ledger account.
/// </summary>
public enum LedgerOwnerKind : byte
{
    Unknown = 0,
    Player = 1,
    BattleBase = 2
}

/// <summary>
/// One ledger account owner: a durable player or a host-identified battle base.
/// </summary>
/// <remarks>
/// <para>
/// Player balances key by the durable <c>PersistentId</c>, never by the reusable session
/// <c>PlayerId</c>. That is what makes "重连不重置余额" structural: a reconnect changes the
/// session seat and the connection epoch, but the owner — and every balance under it — is the
/// same value.
/// </para>
/// <para>
/// Base balances key by the host's <see cref="ObjectKey"/> for the base. The key carries the
/// world epoch, so a reloaded world cannot inherit the previous world's base stock.
/// </para>
/// </remarks>
public readonly struct LedgerOwner : IEquatable<LedgerOwner>
{
    private readonly LedgerOwnerKind kind;
    private readonly string persistentId;
    private readonly ObjectKey baseKey;

    private LedgerOwner(LedgerOwnerKind kind, string persistentId, ObjectKey baseKey)
    {
        this.kind = kind;
        this.persistentId = persistentId;
        this.baseKey = baseKey;
    }

    public static LedgerOwner ForPlayer(string persistentId)
    {
        if (string.IsNullOrEmpty(persistentId)) return default;
        return new LedgerOwner(LedgerOwnerKind.Player, persistentId, default);
    }

    public static LedgerOwner ForBase(ObjectKey baseKey)
    {
        if (!baseKey.IsValid) return default;
        return new LedgerOwner(LedgerOwnerKind.BattleBase, null, baseKey);
    }

    public LedgerOwnerKind Kind => kind;

    public string PersistentId => persistentId;

    public ObjectKey BaseKey => baseKey;

    public bool IsValid =>
        kind == LedgerOwnerKind.Player ? !string.IsNullOrEmpty(persistentId) :
        kind == LedgerOwnerKind.BattleBase && baseKey.IsValid;

    public bool Equals(LedgerOwner other) =>
        kind == other.kind &&
        string.Equals(persistentId, other.persistentId, StringComparison.Ordinal) &&
        baseKey.Equals(other.baseKey);

    public override bool Equals(object obj) => obj is LedgerOwner other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = (int)kind;
            hash = (hash * 397) ^ (persistentId != null ? persistentId.GetHashCode() : 0);
            hash = (hash * 397) ^ baseKey.GetHashCode();
            return hash;
        }
    }

    public static bool operator ==(LedgerOwner left, LedgerOwner right) => left.Equals(right);

    public static bool operator !=(LedgerOwner left, LedgerOwner right) => !left.Equals(right);

    public override string ToString() =>
        kind == LedgerOwnerKind.Player ? "player(" + persistentId + ")" :
        kind == LedgerOwnerKind.BattleBase ? "base(" + baseKey + ")" : "invalid-owner";
}

/// <summary>
/// Which balance of an owner a ledger entry addresses. Item pools carry an item id; energy and
/// singleton pools require item id 0.
/// </summary>
/// <remarks>
/// The split mirrors <c>MechaData</c> / <c>MechaFightData</c> field-for-field so A10 can map each
/// vanilla read/write to exactly one ledger key: core/reactor energy, sand, every storage pool,
/// loaded ammo/bomb counters, and the two quota pools. <see cref="HostResourceFields"/> records
/// the vanilla/mod/cross-domain writer of each key; every key is open at A09 (see its table).
/// </remarks>
public enum LedgerResourceKind : byte
{
    Unknown = 0,
    CoreEnergy = 1,
    ReactorEnergy = 2,
    BaseEnergy = 3,
    Sand = 4,
    DroneSlot = 5,
    InventoryItem = 6,
    AmmoBullet = 7,
    AmmoStorageItem = 8,
    BombStorageItem = 9,
    FighterStorageItem = 10,
    ReactorStorageItem = 11,
    WarpStorageItem = 12,
    DeliveryItem = 13,
    ForgeItem = 14,
    BaseStockItem = 15
}

/// <summary>
/// One addressable balance: owner plus resource plus item.
/// </summary>
public readonly struct LedgerResourceKey : IEquatable<LedgerResourceKey>
{
    private readonly LedgerOwner owner;
    private readonly LedgerResourceKind kind;
    private readonly int itemId;

    public LedgerResourceKey(LedgerOwner owner, LedgerResourceKind kind, int itemId)
    {
        this.owner = owner;
        this.kind = kind;
        this.itemId = itemId;
    }

    public LedgerOwner Owner => owner;

    public LedgerResourceKind Kind => kind;

    public int ItemId => itemId;

    /// <summary>True for the three energy balances, which are doubles.</summary>
    public static bool IsDoubleKind(LedgerResourceKind kind) =>
        kind == LedgerResourceKind.CoreEnergy ||
        kind == LedgerResourceKind.ReactorEnergy ||
        kind == LedgerResourceKind.BaseEnergy;

    /// <summary>True for item pools, which require a positive item id.</summary>
    public static bool IsItemKind(LedgerResourceKind kind)
    {
        switch (kind)
        {
            case LedgerResourceKind.InventoryItem:
            case LedgerResourceKind.AmmoBullet:
            case LedgerResourceKind.AmmoStorageItem:
            case LedgerResourceKind.BombStorageItem:
            case LedgerResourceKind.FighterStorageItem:
            case LedgerResourceKind.ReactorStorageItem:
            case LedgerResourceKind.WarpStorageItem:
            case LedgerResourceKind.DeliveryItem:
            case LedgerResourceKind.ForgeItem:
            case LedgerResourceKind.BaseStockItem:
                return true;
            default:
                return false;
        }
    }

    public bool IsValid
    {
        get
        {
            if (!owner.IsValid || kind == LedgerResourceKind.Unknown) return false;
            if (IsDoubleKind(kind)) return itemId == 0 && IsOwnerCompatible();
            if (kind == LedgerResourceKind.Sand || kind == LedgerResourceKind.DroneSlot)
                return itemId == 0;
            if (IsItemKind(kind)) return itemId > 0 && IsOwnerCompatible();
            return false;
        }
    }

    private bool IsOwnerCompatible()
    {
        switch (kind)
        {
            case LedgerResourceKind.CoreEnergy:
            case LedgerResourceKind.ReactorEnergy:
            case LedgerResourceKind.Sand:
            case LedgerResourceKind.InventoryItem:
            case LedgerResourceKind.AmmoBullet:
            case LedgerResourceKind.AmmoStorageItem:
            case LedgerResourceKind.BombStorageItem:
            case LedgerResourceKind.FighterStorageItem:
            case LedgerResourceKind.ReactorStorageItem:
            case LedgerResourceKind.WarpStorageItem:
            case LedgerResourceKind.DeliveryItem:
            case LedgerResourceKind.ForgeItem:
                return owner.Kind == LedgerOwnerKind.Player;
            case LedgerResourceKind.BaseEnergy:
            case LedgerResourceKind.BaseStockItem:
                return owner.Kind == LedgerOwnerKind.BattleBase;
            case LedgerResourceKind.DroneSlot:
                return owner.Kind == LedgerOwnerKind.Player || owner.Kind == LedgerOwnerKind.BattleBase;
            default:
                return false;
        }
    }

    public bool Equals(LedgerResourceKey other) =>
        owner.Equals(other.owner) && kind == other.kind && itemId == other.itemId;

    public override bool Equals(object obj) => obj is LedgerResourceKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = owner.GetHashCode();
            hash = (hash * 397) ^ (int)kind;
            hash = (hash * 397) ^ itemId;
            return hash;
        }
    }

    public static bool operator ==(LedgerResourceKey left, LedgerResourceKey right) => left.Equals(right);

    public static bool operator !=(LedgerResourceKey left, LedgerResourceKey right) => !left.Equals(right);

    public override string ToString() => owner + "|" + kind + "|item=" + itemId;
}

/// <summary>
/// Groups every balance movement of one command (DESIGN 4.1).
/// </summary>
public readonly struct HostTransactionId : IEquatable<HostTransactionId>
{
    private readonly AuthorityEpoch epoch;
    private readonly long sequence;

    public HostTransactionId(AuthorityEpoch epoch, long sequence)
    {
        this.epoch = epoch;
        this.sequence = sequence;
    }

    public AuthorityEpoch Epoch => epoch;

    public long Sequence => sequence;

    public bool IsValid => epoch.IsValid && sequence > 0;

    public bool Equals(HostTransactionId other) => epoch.Equals(other.epoch) && sequence == other.sequence;

    public override bool Equals(object obj) => obj is HostTransactionId other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return (epoch.GetHashCode() * 397) ^ sequence.GetHashCode();
        }
    }

    public static bool operator ==(HostTransactionId left, HostTransactionId right) => left.Equals(right);

    public static bool operator !=(HostTransactionId left, HostTransactionId right) => !left.Equals(right);

    public override string ToString() => "epoch=" + epoch + "|tx=" + sequence;
}

/// <summary>How a transaction begin was decided.</summary>
public enum LedgerBeginResult : byte
{
    Begun = 0,
    Duplicate = 1,
    TooOld = 2,
    WrongEpoch = 3,
    Invalid = 4
}

/// <summary>Why a reserve or credit was refused. Success is <see cref="Ok"/>.</summary>
public enum LedgerReserveCode : byte
{
    Ok = 0,
    BadTransaction = 1,
    InvalidOwner = 2,
    InvalidAmount = 3,
    WrongKind = 4,
    StaleRevision = 5,
    Insufficient = 6
}

/// <summary>State of one reservation inside its transaction.</summary>
public enum LedgerReservationState : byte
{
    Reserved = 0,
    Committed = 1,
    Released = 2
}

/// <summary>
/// The host's resource truth: per-owner balances with reserve/commit/release, per-key revisions,
/// and per-connection at-most-once transactions (TASKS.md A09, pure model).
/// </summary>
/// <remarks>
/// <para>
/// Flow per command (DESIGN 7.2 <c>Validate -&gt; Reserve -&gt; ExecuteOnce -&gt; Commit -&gt;
/// Publish</c>): <see cref="BeginTransaction"/> admits the <see cref="CommandKey"/> exactly
/// once through a per-connection <see cref="CommandWindow"/>; each <c>TryReserve</c> checks the
/// caller's expected revision and deducts immediately; <see cref="CommitTransaction"/> seals the
/// transaction and caches its outcome so a retry reads the same answer; <see cref="AbortTransaction"/>
/// or <see cref="ReleaseReservation"/> refunds before the commit. After the commit no refund
/// path exists: a committed spend is final, which is what keeps "最多提交一次" from becoming
/// "扣了又退".
/// </para>
/// <para>
/// The ledger is role-agnostic: it never branches on <see cref="HostPlayerRole"/>. Host and
/// remote owners take the same code path with the same checks, so there is no "remote infinite
/// energy" branch to retire later. Whether an owner may act at all is the caller's check against
/// <see cref="HostPlayerRegistry"/>; the ledger only guarantees the math.
/// </para>
/// <para>
/// Balances key by durable owner (<see cref="LedgerOwner"/>): a reconnect keeps every balance
/// because the owner did not change. Dedup windows key by connection epoch, so the same sequence
/// on a new connection is a new transaction while a retry on the old connection reads the cache.
/// </para>
/// <para>
/// Not thread-safe: only the frame boundary touches it.
/// </para>
/// </remarks>
public sealed class HostResourceLedger
{
    private sealed class DoubleAccount
    {
        public double Balance;
        public long Revision;
    }

    private sealed class LongAccount
    {
        public long Balance;
        public long Revision;
    }

    private sealed class Reservation
    {
        public long ReservationId;
        public HostTransactionId Transaction;
        public LedgerResourceKey Key;
        public bool IsDouble;
        public double DoubleAmount;
        public long LongAmount;
        public LedgerReservationState State;
    }

    private sealed class PendingTransaction
    {
        public CommandKey Key;
        public HostTransactionId Transaction;
        public readonly List<long> Reservations = new();
    }

    private readonly AuthorityEpoch epoch;
    private readonly int resultCapacity;
    private readonly Dictionary<LedgerResourceKey, DoubleAccount> doubles = new();
    private readonly Dictionary<LedgerResourceKey, LongAccount> longs = new();
    private readonly Dictionary<long, Reservation> reservations = new();
    private readonly Dictionary<HostTransactionId, PendingTransaction> pending =
        new();
    private readonly Dictionary<CommandKey, HostTransactionId> pendingByKey =
        new();
    private readonly Dictionary<ulong, CommandWindow> windows = new();

    private long nextTransactionSequence;
    private long nextReservationId;
    private long ledgerRevision;

    private long reservesTotal;
    private long creditsTotal;
    private long commitsTotal;
    private long abortsTotal;
    private long releasesTotal;
    private long duplicateHits;

    public HostResourceLedger(AuthorityEpoch epoch, int resultCapacity = AuthorityLimits.CommandQueueMax)
    {
        if (!epoch.IsValid) throw new ArgumentException("A ledger needs a world epoch.", nameof(epoch));
        if (resultCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(resultCapacity));
        this.epoch = epoch;
        this.resultCapacity = resultCapacity;
    }

    public AuthorityEpoch Epoch => epoch;

    /// <summary>Global monotonic revision, bumped on every balance movement.</summary>
    public long LedgerRevision => System.Threading.Interlocked.Read(ref ledgerRevision);

    public long NextTransactionSequence => System.Threading.Interlocked.Read(ref nextTransactionSequence) + 1;

    public long ReservesTotal => System.Threading.Interlocked.Read(ref reservesTotal);

    public long CreditsTotal => System.Threading.Interlocked.Read(ref creditsTotal);

    public long CommitsTotal => System.Threading.Interlocked.Read(ref commitsTotal);

    public long AbortsTotal => System.Threading.Interlocked.Read(ref abortsTotal);

    public long ReleasesTotal => System.Threading.Interlocked.Read(ref releasesTotal);

    public long DuplicateHits => System.Threading.Interlocked.Read(ref duplicateHits);

    public int PendingTransactionCount => pending.Count;

    /// <summary>
    /// Host-only seed for world load and migration. Not reachable from any client packet.
    /// </summary>
    public void SeedDouble(LedgerOwner owner, LedgerResourceKind kind, double balance)
    {
        var key = new LedgerResourceKey(owner, kind, 0);
        if (!key.IsValid || !LedgerResourceKey.IsDoubleKind(kind))
            throw new ArgumentException("Invalid double seed key: " + key);
        if (double.IsNaN(balance) || double.IsInfinity(balance) || balance < 0)
            throw new ArgumentOutOfRangeException(nameof(balance));
        if (!doubles.TryGetValue(key, out var account))
        {
            account = new DoubleAccount();
            doubles[key] = account;
        }
        account.Balance = balance;
        account.Revision++;
        System.Threading.Interlocked.Increment(ref ledgerRevision);
    }

    /// <summary>
    /// Host-only seed for world load and migration. Not reachable from any client packet.
    /// </summary>
    public void SeedLong(LedgerOwner owner, LedgerResourceKind kind, int itemId, long balance)
    {
        var key = new LedgerResourceKey(owner, kind, itemId);
        if (!key.IsValid || LedgerResourceKey.IsDoubleKind(kind))
            throw new ArgumentException("Invalid long seed key: " + key);
        if (balance < 0) throw new ArgumentOutOfRangeException(nameof(balance));
        if (!longs.TryGetValue(key, out var account))
        {
            account = new LongAccount();
            longs[key] = account;
        }
        account.Balance = balance;
        account.Revision++;
        System.Threading.Interlocked.Increment(ref ledgerRevision);
    }

    public bool TryGetDouble(LedgerOwner owner, LedgerResourceKind kind, out double balance, out long revision)
    {
        var key = new LedgerResourceKey(owner, kind, 0);
        if (key.IsValid && LedgerResourceKey.IsDoubleKind(kind) && doubles.TryGetValue(key, out var account))
        {
            balance = account.Balance;
            revision = account.Revision;
            return true;
        }
        balance = 0;
        revision = 0;
        return false;
    }

    public bool TryGetLong(LedgerOwner owner, LedgerResourceKind kind, int itemId, out long balance, out long revision)
    {
        var key = new LedgerResourceKey(owner, kind, itemId);
        if (key.IsValid && !LedgerResourceKey.IsDoubleKind(kind) && longs.TryGetValue(key, out var account))
        {
            balance = account.Balance;
            revision = account.Revision;
            return true;
        }
        balance = 0;
        revision = 0;
        return false;
    }

    /// <summary>Current revision of one key, or 0 when the account was never seeded.</summary>
    public long RevisionOf(LedgerResourceKey key)
    {
        if (!key.IsValid) return 0;
        if (LedgerResourceKey.IsDoubleKind(key.Kind))
            return doubles.TryGetValue(key, out var d) ? d.Revision : 0;
        return longs.TryGetValue(key, out var l) ? l.Revision : 0;
    }

    /// <summary>
    /// Admits one command as one transaction. A duplicate returns the cached outcome.
    /// </summary>
    public LedgerBeginResult BeginTransaction(CommandKey key, out HostTransactionId transaction,
        out CommandOutcome cached)
    {
        transaction = default;
        cached = default;
        if (!key.IsValid) return LedgerBeginResult.Invalid;
        if (!key.Epoch.Equals(epoch)) return LedgerBeginResult.WrongEpoch;

        var window = GetOrCreateWindow(key.Connection);
        var admission = window.Admit(key, out cached);
        switch (admission)
        {
            case CommandAdmission.Execute:
                var sequence = System.Threading.Interlocked.Increment(ref nextTransactionSequence);
                transaction = new HostTransactionId(epoch, sequence);
                var record = new PendingTransaction { Key = key, Transaction = transaction };
                pending[transaction] = record;
                pendingByKey[key] = transaction;
                return LedgerBeginResult.Begun;
            case CommandAdmission.Duplicate:
                System.Threading.Interlocked.Increment(ref duplicateHits);
                transaction = new HostTransactionId(epoch, cached.TransactionId);
                return LedgerBeginResult.Duplicate;
            case CommandAdmission.TooOld:
                return LedgerBeginResult.TooOld;
            case CommandAdmission.WrongEpoch:
                return LedgerBeginResult.WrongEpoch;
            default:
                return LedgerBeginResult.Invalid;
        }
    }

    public LedgerReserveCode TryReserveDouble(HostTransactionId transaction, LedgerOwner owner,
        LedgerResourceKind kind, double amount, long expectedRevision, out long reservationId,
        out long newRevision)
    {
        reservationId = 0;
        newRevision = 0;
        if (!transaction.IsValid || !transaction.Epoch.Equals(epoch) || !pending.ContainsKey(transaction))
            return LedgerReserveCode.BadTransaction;
        var key = new LedgerResourceKey(owner, kind, 0);
        if (!owner.IsValid) return LedgerReserveCode.InvalidOwner;
        if (!key.IsValid || !LedgerResourceKey.IsDoubleKind(kind)) return LedgerReserveCode.WrongKind;
        if (double.IsNaN(amount) || double.IsInfinity(amount) || amount <= 0)
            return LedgerReserveCode.InvalidAmount;

        if (!doubles.TryGetValue(key, out var account))
        {
            account = new DoubleAccount();
            doubles[key] = account;
        }
        if (account.Revision != expectedRevision) return LedgerReserveCode.StaleRevision;
        if (account.Balance < amount) return LedgerReserveCode.Insufficient;

        account.Balance -= amount;
        account.Revision++;
        System.Threading.Interlocked.Increment(ref ledgerRevision);
        newRevision = account.Revision;

        var id = System.Threading.Interlocked.Increment(ref nextReservationId);
        reservations[id] = new Reservation
        {
            ReservationId = id,
            Transaction = transaction,
            Key = key,
            IsDouble = true,
            DoubleAmount = amount,
            State = LedgerReservationState.Reserved
        };
        pending[transaction].Reservations.Add(id);
        reservationId = id;
        System.Threading.Interlocked.Increment(ref reservesTotal);
        return LedgerReserveCode.Ok;
    }

    public LedgerReserveCode TryReserveLong(HostTransactionId transaction, LedgerOwner owner,
        LedgerResourceKind kind, int itemId, long amount, long expectedRevision, out long reservationId,
        out long newRevision)
    {
        reservationId = 0;
        newRevision = 0;
        if (!transaction.IsValid || !transaction.Epoch.Equals(epoch) || !pending.ContainsKey(transaction))
            return LedgerReserveCode.BadTransaction;
        var key = new LedgerResourceKey(owner, kind, itemId);
        if (!owner.IsValid) return LedgerReserveCode.InvalidOwner;
        if (!key.IsValid || LedgerResourceKey.IsDoubleKind(kind)) return LedgerReserveCode.WrongKind;
        if (amount <= 0) return LedgerReserveCode.InvalidAmount;

        if (!longs.TryGetValue(key, out var account))
        {
            account = new LongAccount();
            longs[key] = account;
        }
        if (account.Revision != expectedRevision) return LedgerReserveCode.StaleRevision;
        if (account.Balance < amount) return LedgerReserveCode.Insufficient;

        account.Balance -= amount;
        account.Revision++;
        System.Threading.Interlocked.Increment(ref ledgerRevision);
        newRevision = account.Revision;

        var id = System.Threading.Interlocked.Increment(ref nextReservationId);
        reservations[id] = new Reservation
        {
            ReservationId = id,
            Transaction = transaction,
            Key = key,
            IsDouble = false,
            LongAmount = amount,
            State = LedgerReservationState.Reserved
        };
        pending[transaction].Reservations.Add(id);
        reservationId = id;
        System.Threading.Interlocked.Increment(ref reservesTotal);
        return LedgerReserveCode.Ok;
    }

    /// <summary>
    /// Credits income (pickup, supply, production share) into a pending transaction.
    /// </summary>
    /// <remarks>
    /// Credits never fail for lack of funds. The same <see cref="CommandKey"/> retried after the
    /// commit reads the cached outcome instead of crediting twice.
    /// </remarks>
    public LedgerReserveCode CreditDouble(HostTransactionId transaction, LedgerOwner owner,
        LedgerResourceKind kind, double amount, out long newRevision)
    {
        newRevision = 0;
        if (!transaction.IsValid || !transaction.Epoch.Equals(epoch) || !pending.ContainsKey(transaction))
            return LedgerReserveCode.BadTransaction;
        var key = new LedgerResourceKey(owner, kind, 0);
        if (!owner.IsValid) return LedgerReserveCode.InvalidOwner;
        if (!key.IsValid || !LedgerResourceKey.IsDoubleKind(kind)) return LedgerReserveCode.WrongKind;
        if (double.IsNaN(amount) || double.IsInfinity(amount) || amount <= 0)
            return LedgerReserveCode.InvalidAmount;

        if (!doubles.TryGetValue(key, out var account))
        {
            account = new DoubleAccount();
            doubles[key] = account;
        }
        account.Balance += amount;
        account.Revision++;
        System.Threading.Interlocked.Increment(ref ledgerRevision);
        newRevision = account.Revision;
        System.Threading.Interlocked.Increment(ref creditsTotal);
        return LedgerReserveCode.Ok;
    }

    /// <summary>
    /// Credits income (pickup, supply, production share) into a pending transaction.
    /// </summary>
    public LedgerReserveCode CreditLong(HostTransactionId transaction, LedgerOwner owner,
        LedgerResourceKind kind, int itemId, long amount, out long newRevision)
    {
        newRevision = 0;
        if (!transaction.IsValid || !transaction.Epoch.Equals(epoch) || !pending.ContainsKey(transaction))
            return LedgerReserveCode.BadTransaction;
        var key = new LedgerResourceKey(owner, kind, itemId);
        if (!owner.IsValid) return LedgerReserveCode.InvalidOwner;
        if (!key.IsValid || LedgerResourceKey.IsDoubleKind(kind)) return LedgerReserveCode.WrongKind;
        if (amount <= 0) return LedgerReserveCode.InvalidAmount;

        if (!longs.TryGetValue(key, out var account))
        {
            account = new LongAccount();
            longs[key] = account;
        }
        account.Balance += amount;
        account.Revision++;
        System.Threading.Interlocked.Increment(ref ledgerRevision);
        newRevision = account.Revision;
        System.Threading.Interlocked.Increment(ref creditsTotal);
        return LedgerReserveCode.Ok;
    }

    /// <summary>
    /// Seals a pending transaction and caches its outcome for retries.
    /// </summary>
    /// <remarks>
    /// The commit itself moves no balance: reserves already deducted, credits already added. It
    /// only flips reservations to <see cref="LedgerReservationState.Committed"/> and records the
    /// outcome in the connection's dedup window. A second commit of the same key is answered from
    /// that window, never executed twice.
    /// </remarks>
    public bool CommitTransaction(HostTransactionId transaction, CommandResultCode code,
        long appliedHostTick, out CommandOutcome outcome)
    {
        outcome = default;
        if (!transaction.IsValid || !transaction.Epoch.Equals(epoch)) return false;
        if (!pending.TryGetValue(transaction, out var record)) return false;

        foreach (var id in record.Reservations)
        {
            if (reservations.TryGetValue(id, out var reservation) &&
                reservation.State == LedgerReservationState.Reserved)
            {
                reservation.State = LedgerReservationState.Committed;
            }
        }

        outcome = new CommandOutcome(code, appliedHostTick, transaction.Sequence, LedgerRevision);
        var window = GetOrCreateWindow(record.Key.Connection);
        if (!window.Complete(record.Key, outcome)) return false;
        pending.Remove(transaction);
        pendingByKey.Remove(record.Key);
        System.Threading.Interlocked.Increment(ref commitsTotal);
        return true;
    }

    /// <summary>
    /// Cancels a pending transaction before its commit: every reserve is refunded exactly once.
    /// </summary>
    /// <remarks>
    /// Only valid while nothing was committed. A committed transaction is final; a second abort
    /// is a no-op that refunds nothing, which is what keeps "取消/重复取消不增发".
    /// </remarks>
    public bool AbortTransaction(HostTransactionId transaction)
    {
        if (!transaction.IsValid || !transaction.Epoch.Equals(epoch)) return false;
        if (!pending.TryGetValue(transaction, out var record)) return false;

        foreach (var id in record.Reservations)
        {
            RefundReservation(id);
        }
        pending.Remove(transaction);
        pendingByKey.Remove(record.Key);
        var window = GetOrCreateWindow(record.Key.Connection);
        window.Abandon(record.Key);
        System.Threading.Interlocked.Increment(ref abortsTotal);
        return true;
    }

    /// <summary>
    /// Opens a host-internal transaction: one host-tick effect with no client command behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Continuous fire ticks, host-computed damage side-effects and other frame work the host runs
    /// on its own clock (DESIGN 7.1 "连续开火由主机时钟推进") cannot name a <see cref="CommandKey"/>:
    /// there is no client retry to deduplicate. They still need <c>Reserve -&gt; ExecuteOnce -&gt;
    /// Commit</c> through one transaction so a tick spends exactly once, which is what this path
    /// provides. It shares the transaction sequence with client commands, so a
    /// <see cref="HostTransactionId"/> never repeats within an epoch regardless of which path
    /// minted it.
    /// </para>
    /// <para>
    /// No dedup window is touched: the frame boundary runs each host tick once, which is the
    /// exactly-once property here. Client-command windows are unaffected.
    /// </para>
    /// </remarks>
    public HostTransactionId BeginHostTransaction()
    {
        var sequence = System.Threading.Interlocked.Increment(ref nextTransactionSequence);
        var transaction = new HostTransactionId(epoch, sequence);
        pending[transaction] = new PendingTransaction { Key = default, Transaction = transaction };
        return transaction;
    }

    /// <summary>
    /// Seals a host-internal transaction opened with <see cref="BeginHostTransaction"/>.
    /// </summary>
    public bool CommitHostTransaction(HostTransactionId transaction, CommandResultCode code,
        long appliedHostTick, out CommandOutcome outcome)
    {
        outcome = default;
        if (!transaction.IsValid || !transaction.Epoch.Equals(epoch)) return false;
        if (!pending.TryGetValue(transaction, out _)) return false;

        foreach (var id in pending[transaction].Reservations)
        {
            if (reservations.TryGetValue(id, out var reservation) &&
                reservation.State == LedgerReservationState.Reserved)
            {
                reservation.State = LedgerReservationState.Committed;
            }
        }

        outcome = new CommandOutcome(code, appliedHostTick, transaction.Sequence, LedgerRevision);
        pending.Remove(transaction);
        System.Threading.Interlocked.Increment(ref commitsTotal);
        return true;
    }

    /// <summary>
    /// Cancels a host-internal transaction before its commit, refunding every reserve once.
    /// </summary>
    public bool AbortHostTransaction(HostTransactionId transaction)
    {
        if (!transaction.IsValid || !transaction.Epoch.Equals(epoch)) return false;
        if (!pending.TryGetValue(transaction, out var record)) return false;

        foreach (var id in record.Reservations)
        {
            RefundReservation(id);
        }
        pending.Remove(transaction);
        System.Threading.Interlocked.Increment(ref abortsTotal);
        return true;
    }

    /// <summary>
    /// Releases one reservation before its transaction commits (task cancel, target lost).
    /// </summary>
    /// <remarks>
    /// After the commit this returns false: committed spends are final. A second release of the
    /// same reservation also returns false and moves no balance, so no cancel can mint.
    /// </remarks>
    public bool ReleaseReservation(long reservationId)
    {
        if (!reservations.TryGetValue(reservationId, out var reservation)) return false;
        if (reservation.State != LedgerReservationState.Reserved) return false;
        RefundReservation(reservationId);
        System.Threading.Interlocked.Increment(ref releasesTotal);
        return true;
    }

    /// <summary>
    /// Drops one connection's dedup window. Balances survive: they key by persistent owner.
    /// </summary>
    public bool ForgetConnection(ConnectionEpoch connection)
    {
        if (!connection.IsValid) return false;
        return windows.Remove(connection.Value);
    }

    /// <summary>True when the window already answered this key.</summary>
    public bool TryGetCachedOutcome(CommandKey key, out CommandOutcome outcome)
    {
        outcome = default;
        if (!key.IsValid) return false;
        return windows.TryGetValue(key.Connection.Value, out var window) &&
               window.TryGetOutcome(key, out outcome);
    }

    /// <summary>
    /// Captures every balance into the save-sidecar record form (TASKS.md A21, host-only).
    /// </summary>
    /// <remarks>
    /// Only balances are captured: pending transactions and dedup windows die with the session,
    /// because an unconfirmed command must never re-execute in the new epoch. The caller writes
    /// the result into the sidecar file; no client packet reaches this path.
    /// </remarks>
    public List<AuthoritySaveAccount> CaptureAccountsForSave()
    {
        var accounts = new List<AuthoritySaveAccount>(doubles.Count + longs.Count);
        foreach (var pair in doubles)
        {
            accounts.Add(new AuthoritySaveAccount
            {
                OwnerKind = pair.Key.Owner.Kind,
                PersistentId = pair.Key.Owner.PersistentId,
                BaseScope = pair.Key.Owner.BaseKey.Scope,
                BaseNativeId = pair.Key.Owner.BaseKey.NativeId,
                BaseGeneration = pair.Key.Owner.BaseKey.Generation,
                ResourceKind = pair.Key.Kind,
                ItemId = pair.Key.ItemId,
                IsDouble = true,
                DoubleBalance = pair.Value.Balance
            });
        }
        foreach (var pair in longs)
        {
            accounts.Add(new AuthoritySaveAccount
            {
                OwnerKind = pair.Key.Owner.Kind,
                PersistentId = pair.Key.Owner.PersistentId,
                BaseScope = pair.Key.Owner.BaseKey.Scope,
                BaseNativeId = pair.Key.Owner.BaseKey.NativeId,
                BaseGeneration = pair.Key.Owner.BaseKey.Generation,
                ResourceKind = pair.Key.Kind,
                ItemId = pair.Key.ItemId,
                IsDouble = false,
                LongBalance = pair.Value.Balance
            });
        }
        return accounts;
    }

    private void RefundReservation(long reservationId)
    {
        if (!reservations.TryGetValue(reservationId, out var reservation)) return;
        if (reservation.State != LedgerReservationState.Reserved) return;
        if (reservation.IsDouble)
        {
            if (doubles.TryGetValue(reservation.Key, out var account))
            {
                account.Balance += reservation.DoubleAmount;
                account.Revision++;
                System.Threading.Interlocked.Increment(ref ledgerRevision);
            }
        }
        else
        {
            if (longs.TryGetValue(reservation.Key, out var account))
            {
                account.Balance += reservation.LongAmount;
                account.Revision++;
                System.Threading.Interlocked.Increment(ref ledgerRevision);
            }
        }
        reservation.State = LedgerReservationState.Released;
    }

    private CommandWindow GetOrCreateWindow(ConnectionEpoch connection)
    {
        if (!windows.TryGetValue(connection.Value, out var window))
        {
            window = new CommandWindow(epoch, connection, resultCapacity);
            windows.Add(connection.Value, window);
        }
        return window;
    }
}
