#region

using System;
using System.Collections.Generic;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// Schema version of the authority save sidecar (TASKS.md A21).
/// </summary>
/// <remarks>
/// <para>
/// The sidecar is a separate, versioned file next to the existing <c>.server</c> save, so a
/// version bump here never has to touch the multiplayer save revision. A reader that meets a
/// schema it does not know refuses the file with an explanation instead of guessing — DESIGN 11
/// requires "未知新版本拒绝读取".
/// </para>
/// </remarks>
public static class AuthoritySidecarSchema
{
    /// <summary>The only schema this build writes and reads.</summary>
    public const int Current = 1;
}

/// <summary>
/// One host-tracked player persisted in the sidecar.
/// </summary>
/// <remarks>
/// Only the durable identity and the presence the host believes are saved. The session seat and
/// the connection epoch are deliberately absent: they die with the session, and a reconnecting
/// player is re-seated by the registry with fresh values. A restored entry is offline until the
/// player actually joins, so it can never own combat or drone work.
/// </remarks>
public sealed class AuthoritySavePlayer
{
    public string PersistentId;
    public HostPlayerRole Role;
    public int PlanetId;
    public int StarId;
    public bool IsAlive;
    public bool RepairEnabled;
    public bool BuildEnabled;
    public int DroneTotal;
}

/// <summary>
/// One persisted ledger balance.
/// </summary>
/// <remarks>
/// <para>
/// Player balances key by the durable <c>PersistentId</c> and restore unchanged under the new
/// epoch — that is the structural form of "之后重连取主机账本，不从旧本地存档覆盖".
/// </para>
/// <para>
/// Base balances key by the host's base <see cref="ObjectKey"/>. The saved epoch is not written
/// per account: it is stored once in the state header as provenance, and the restore re-keys the
/// base identity under the new epoch at the same scope, slot and generation. If the reloaded
/// world's adapter mints a different generation for that slot, re-seeding from the vanilla facts
/// (A22) reconciles; a wrong guess can never mix two worlds because the epoch differs.
/// </para>
/// </remarks>
public sealed class AuthoritySaveAccount
{
    public LedgerOwnerKind OwnerKind;
    public string PersistentId;
    public int BaseScope;
    public int BaseNativeId;
    public long BaseGeneration;
    public LedgerResourceKind ResourceKind;
    public int ItemId;
    public bool IsDouble;
    public double DoubleBalance;
    public long LongBalance;
}

/// <summary>
/// One persisted drone budget.
/// </summary>
/// <remarks>
/// Only the owner and the capacity <c>total</c> are saved. The bucket split is not: on load every
/// restored budget is entirely idle, which is the one-time reclaim rule of DESIGN 11 — task run
/// positions are not persisted, so there is no honest bucket to restore and no
/// <c>repairerCount</c> ghost may survive the reload.
/// </remarks>
public sealed class AuthoritySaveBudget
{
    public ConstructionOwnerKind OwnerKind;
    public string PersistentId;
    public int BaseScope;
    public int BaseNativeId;
    public long BaseGeneration;
    public int Total;
}

/// <summary>
/// One task that was live when the world was saved.
/// </summary>
/// <remarks>
/// <para>
/// Tasks are provenance, not live state: the restore never re-inserts them into the task ledger.
/// Their owners cannot be seated yet (players have no session seat at load) and their target keys
/// belong to the saved epoch, so "restoring" them would either strand a budget slot on a seatless
/// owner or guess a new generation for the target — exactly the resurrection DESIGN 4.1 forbids.
/// </para>
/// <para>
/// The load transaction reclaims them once (counted in the restore report), returns every slot to
/// idle, and the host re-derives repair work from its real damage records after load. That is what
/// makes "可恢复任务不丢" (re-dispatch from restored damage facts) hold without "不复制" breaking
/// (no double occupancy).
/// </para>
/// </remarks>
public sealed class AuthoritySaveTask
{
    public long Sequence;
    public ConstructionOwnerKind OwnerKind;
    public string PersistentId;
    public int BaseScope;
    public int BaseNativeId;
    public long BaseGeneration;
    public PoolKind TargetKind;
    public int TargetScope;
    public int TargetNativeId;
    public long TargetGeneration;
    public ConstructionTaskKind TaskKind;
    public ConstructionTaskStage Stage;
    public long Revision;
    public long LastHostTick;
    public ConstructionCancelReason CancelReason;
}

/// <summary>
/// The host facts captured into one sidecar (TASKS.md A21, DESIGN 11).
/// </summary>
/// <remarks>
/// <para>
/// Everything here is host-only by construction: only a host authority session can capture, and
/// only the host's load applies. A client never reads or writes one.
/// </para>
/// <para>
/// The saved epoch is provenance only. The load always mints a fresh world epoch (DESIGN 4.1:
/// every load or host restart is a new world), so nothing in this file can authorize anything in
/// the new one; packets from the saved epoch are refused by the envelope gate's epoch check.
/// </para>
/// </remarks>
public sealed class AuthoritySaveState
{
    /// <summary>Schema this state was decoded from or should be written as.</summary>
    public int Schema;

    /// <summary>Persistent world identity the sidecar belongs to. A mismatch refuses the file.</summary>
    public string WorldId;

    /// <summary>Epoch of the world that saved this file. Provenance only, never reused.</summary>
    public AuthorityEpoch SavedEpoch;

    /// <summary>Host tick at save time, for diagnostics.</summary>
    public long SavedHostTick;

    public readonly List<AuthoritySavePlayer> Players = [];
    public readonly List<AuthoritySaveAccount> Accounts = [];
    public readonly List<AuthoritySaveBudget> Budgets = [];
    public readonly List<AuthoritySaveTask> Tasks = [];

    /// <summary>Number of tasks that were live (held a budget slot) when saved.</summary>
    public int LiveTaskCount
    {
        get
        {
            var count = 0;
            foreach (var task in Tasks)
            {
                if (task != null && task.Stage is >= ConstructionTaskStage.Reserved
                    and <= ConstructionTaskStage.Returning)
                {
                    count++;
                }
            }
            return count;
        }
    }
}

/// <summary>
/// What a load should do with the authority sidecar (TASKS.md A21, pure decision).
/// </summary>
public enum AuthoritySidecarDecision : byte
{
    /// <summary>Not a host authority world: the sidecar is ignored and the legacy path runs.</summary>
    None = 0,

    /// <summary>Sidecar decoded: seed balances and budgets under the new epoch.</summary>
    Restore = 1,

    /// <summary>
    /// Host authority mode but no sidecar file: a legacy save is being migrated once. The ledger
    /// starts empty and is seeded from the vanilla facts by the game adapters; damaged buildings
    /// and drone work are re-derived, never refilled.
    /// </summary>
    LegacyMigration = 2,

    /// <summary>
    /// A sidecar exists but cannot be used: unknown schema, corrupt bytes, or a world identity
    /// that does not match the save it sits next to. Refused with an explanation; the host must
    /// not overwrite it with a new file until a person decides.
    /// </summary>
    Refused = 3
}

/// <summary>
/// The load-time decision for one sidecar file, as pure rules (TASKS.md A21).
/// </summary>
public static class AuthoritySavePolicy
{
    /// <summary>
    /// Decides what a load does with the sidecar next to a <c>.server</c> save.
    /// </summary>
    /// <param name="authorityMode">True when this host runs the authority mode.</param>
    /// <param name="sidecarExists">True when a sidecar file is present.</param>
    /// <param name="decoded">True when the codec accepted the bytes.</param>
    /// <param name="decodeReason">Codec's refusal reason when <paramref name="decoded"/> is false.</param>
    /// <param name="sidecarWorldId">World identity claimed by the sidecar, or null when undecoded.</param>
    /// <param name="saveWorldId">World identity of the <c>.server</c> save just loaded.</param>
    /// <param name="reason">Explanation for the decision, always non-empty when refused.</param>
    public static AuthoritySidecarDecision Decide(bool authorityMode, bool sidecarExists, bool decoded,
        string decodeReason, string sidecarWorldId, string saveWorldId, out string reason)
    {
        reason = null;
        if (!authorityMode)
        {
            reason = "legacy mode: sidecar ignored";
            return AuthoritySidecarDecision.None;
        }
        if (!sidecarExists)
        {
            reason = "no sidecar: one-time legacy migration, balances start empty until the " +
                     "host seeds them from vanilla facts";
            return AuthoritySidecarDecision.LegacyMigration;
        }
        if (!decoded)
        {
            reason = "sidecar unreadable: " + (string.IsNullOrEmpty(decodeReason) ? "unknown reason" : decodeReason);
            return AuthoritySidecarDecision.Refused;
        }
        if (!string.Equals(sidecarWorldId, saveWorldId, StringComparison.Ordinal))
        {
            reason = "sidecar belongs to world " + (sidecarWorldId ?? "-") + ", save is world " +
                     (saveWorldId ?? "-");
            return AuthoritySidecarDecision.Refused;
        }
        reason = "sidecar matches the save; balances and budgets restore under the new epoch";
        return AuthoritySidecarDecision.Restore;
    }
}

/// <summary>
/// Result of applying a decoded sidecar to a freshly begun host world (pure model).
/// </summary>
public sealed class AuthoritySaveRestoreReport
{
    public int SeededPlayers;
    public int SeededAccounts;
    public int SeededBudgets;
    public int SkippedPlayerBudgets;
    public int ReclaimedTasks;
    public int ReclaimedSlots;
    public string Error;

    public bool Succeeded => string.IsNullOrEmpty(Error);

    public override string ToString() =>
        (Succeeded ? "ok" : "failed: " + Error) +
        "|players=" + SeededPlayers + "|accounts=" + SeededAccounts +
        "|budgets=" + SeededBudgets + "|playerBudgetsDeferred=" + SkippedPlayerBudgets +
        "|reclaimedTasks=" + ReclaimedTasks + "|reclaimedSlots=" + ReclaimedSlots;
}

/// <summary>
/// Applies a decoded sidecar to a freshly begun host world's model state (TASKS.md A21).
/// </summary>
/// <remarks>
/// <para>
/// The apply is fail-closed in one pass: every record is validated <em>before</em> anything is
/// seeded, so a corrupt or hand-edited sidecar cannot leave the ledger half-populated. Any invalid
/// record aborts the whole restore with the reason, and the host keeps an empty ledger rather than
/// a half-truth.
/// </para>
/// <para>
/// The one-time reclaim rule (DESIGN 11) is the contract for tasks and player budgets: tasks are
/// never re-inserted, every slot returns to idle, and a player's saved capacity waits in the
/// registry entry until the player rejoins and the dispatch adapter re-seats them. Nothing is
/// refilled: balances restore exactly as saved, damaged buildings come back only through the
/// vanilla save's own damage facts.
/// </para>
/// </remarks>
public static class AuthoritySaveRestore
{
    /// <summary>
    /// Validates and seeds <paramref name="ledger"/>, <paramref name="tasks"/> and
    /// <paramref name="players"/> from the sidecar under the new world's epoch.
    /// </summary>
    public static AuthoritySaveRestoreReport Apply(AuthoritySaveState state, AuthorityEpoch newEpoch,
        HostResourceLedger ledger, ConstructionTaskLedger tasks, HostPlayerRegistry players)
    {
        var report = new AuthoritySaveRestoreReport();
        if (state == null)
        {
            report.Error = "no sidecar state";
            return report;
        }
        if (!newEpoch.IsValid)
        {
            report.Error = "restore needs a valid new epoch";
            return report;
        }
        if (ledger == null || tasks == null || players == null)
        {
            report.Error = "restore needs the host ledger, task ledger and player registry";
            return report;
        }
        if (state.Schema != AuthoritySidecarSchema.Current)
        {
            report.Error = "sidecar schema " + state.Schema + " is not the supported schema " +
                           AuthoritySidecarSchema.Current;
            return report;
        }

        // Validate everything first, then seed: a rejected sidecar must not half-populate a world.
        var errors = new List<string>();
        foreach (var player in state.Players)
        {
            if (player == null || string.IsNullOrEmpty(player.PersistentId) ||
                player.Role == HostPlayerRole.Unknown || player.DroneTotal < 0)
            {
                errors.Add("bad player record");
            }
        }
        var accountOwners = new List<LedgerOwner>();
        var accountDoubles = new List<bool>();
        foreach (var account in state.Accounts)
        {
            if (account == null)
            {
                errors.Add("null account record");
                accountOwners.Add(default);
                accountDoubles.Add(false);
                continue;
            }
            var owner = RekeyOwner(account.OwnerKind, account.PersistentId,
                account.BaseScope, account.BaseNativeId, account.BaseGeneration, newEpoch);
            var key = new LedgerResourceKey(owner, account.ResourceKind, account.ItemId);
            if (!owner.IsValid || !key.IsValid ||
                LedgerResourceKey.IsDoubleKind(account.ResourceKind) != account.IsDouble ||
                account.IsDouble && (double.IsNaN(account.DoubleBalance) ||
                                     double.IsInfinity(account.DoubleBalance) || account.DoubleBalance < 0) ||
                !account.IsDouble && account.LongBalance < 0)
            {
                errors.Add("bad account record: " + account.ResourceKind);
            }
            accountOwners.Add(owner);
            accountDoubles.Add(account.IsDouble);
        }
        foreach (var budget in state.Budgets)
        {
            if (budget == null || budget.Total < 0 || budget.Total > DroneBudget.MaxTotal)
            {
                errors.Add("bad budget record");
                continue;
            }
            if (budget.OwnerKind == ConstructionOwnerKind.BattleBase &&
                !RekeyBase(budget.BaseScope, budget.BaseNativeId, budget.BaseGeneration, newEpoch).IsValid)
            {
                errors.Add("bad base budget key");
            }
            else if (budget.OwnerKind == ConstructionOwnerKind.Player &&
                     string.IsNullOrEmpty(budget.PersistentId))
            {
                errors.Add("bad player budget owner");
            }
            else if (budget.OwnerKind is not (ConstructionOwnerKind.Player or ConstructionOwnerKind.BattleBase))
            {
                errors.Add("unknown budget owner kind");
            }
        }
        if (errors.Count > 0)
        {
            report.Error = string.Join("; ", errors);
            return report;
        }

        // Players restore as offline persistent entries: durable identity and capacity survive,
        // but nothing can act through them until the player rejoins and is re-seated.
        foreach (var player in state.Players)
        {
            if (players.RestoreSavedPlayer(player.PersistentId, player.Role, player.PlanetId,
                    player.StarId, player.IsAlive, player.RepairEnabled, player.BuildEnabled,
                    player.DroneTotal))
            {
                report.SeededPlayers++;
            }
        }

        for (var i = 0; i < state.Accounts.Count; i++)
        {
            var account = state.Accounts[i];
            var owner = accountOwners[i];
            if (accountDoubles[i])
            {
                ledger.SeedDouble(owner, account.ResourceKind, account.DoubleBalance);
            }
            else
            {
                ledger.SeedLong(owner, account.ResourceKind, account.ItemId, account.LongBalance);
            }
            report.SeededAccounts++;
        }

        foreach (var budget in state.Budgets)
        {
            if (budget.OwnerKind == ConstructionOwnerKind.BattleBase)
            {
                var owner = ConstructionOwnerKey.ForBase(
                    RekeyBase(budget.BaseScope, budget.BaseNativeId, budget.BaseGeneration, newEpoch));
                // ForBase validates the key shape; the capacity seeds all-idle per the reclaim rule.
                if (owner.IsValid)
                {
                    tasks.EnsureBudget(owner, budget.Total);
                    report.SeededBudgets++;
                    continue;
                }
            }
            // A player budget cannot be seated at load: the saved capacity is already restored in
            // the registry entry, and the dispatch adapter re-creates the budget at join.
            report.SkippedPlayerBudgets++;
        }

        report.ReclaimedTasks = state.Tasks.Count;
        report.ReclaimedSlots = state.LiveTaskCount;
        return report;
    }

    private static LedgerOwner RekeyOwner(LedgerOwnerKind kind, string persistentId,
        int baseScope, int baseNativeId, long baseGeneration, AuthorityEpoch newEpoch)
    {
        return kind switch
        {
            LedgerOwnerKind.Player => LedgerOwner.ForPlayer(persistentId),
            LedgerOwnerKind.BattleBase => LedgerOwner.ForBase(
                RekeyBase(baseScope, baseNativeId, baseGeneration, newEpoch)),
            _ => default
        };
    }

    private static ObjectKey RekeyBase(int baseScope, int baseNativeId, long baseGeneration,
        AuthorityEpoch newEpoch)
    {
        if (baseNativeId <= 0 || baseGeneration <= 0) return default;
        // The saved base identity is re-keyed under the new epoch at the same scope, slot and
        // generation. A different epoch makes it incomparable to the old world's keys, which is
        // the property the identity layer exists for.
        return ObjectKey.TryCreate(newEpoch, PoolKind.Base, baseScope, baseNativeId, baseGeneration,
            out var key) ? key : default;
    }
}
