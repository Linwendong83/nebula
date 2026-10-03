#region

using System;
using System.Collections.Generic;
using NebulaModel.Packets.Authority;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// The client's visual-event table: predicted muzzles plus authoritative host effects
/// (TASKS.md A14, pure model).
/// </summary>
/// <remarks>
/// <para>
/// A prediction is created the moment the client pulls the trigger, so input stays
/// responsive. The host answer carries the same <see cref="EffectState.CauseConnection"/> /
/// <see cref="EffectState.CauseSequence"/> the prediction was filed under, so the arrival
/// merges by replacing the prediction instead of stacking a second muzzle beside it. Either
/// arrival order converges: the prediction is filed before any host event for it can exist,
/// and a rejected command revokes its prediction through <see cref="NoteCommandResult"/>
/// (DESIGN 5.1 "发送失败/拒绝后撤销 UI 预测").
/// </para>
/// <para>
/// Authoritative events expire by <c>StartTick + Life</c>: ending removes the shell, and no
/// other end signal exists, which is what makes "弹道/持续激光结束无残影" structural.
/// Unconfirmed predictions expire after <see cref="PredictionTtlTicks"/> so a lost host
/// event cannot leave a stuck muzzle. The table holds no HP, shield, ledger or pool
/// reference, so rendering for any length of time cannot move a protected number — the
/// executable form of "客户端渲染10秒不改变 hp/盾/库存".
/// </para>
/// <para>
/// An ended event id retires: a delayed duplicate arriving after expiry is refused rather
/// than resurrected. Retired ids are reclaimed on epoch change (<see cref="Clear"/>), so a
/// long heavy-combat session holds one entry per event until then (see the card's blockers).
/// </para>
/// </remarks>
public sealed class EffectBinding
{
    /// <summary>How long an unconfirmed prediction stays visible (10 s at 60 Hz).</summary>
    public const long PredictionTtlTicks = 600;

    private sealed class PredictedEntry
    {
        public AuthorityEffectKind Kind;
        public int Style;
        public long ClientTick;
        public long? TransactionId;
    }

    private sealed class ActiveEntry
    {
        public EffectState State;
    }

    private readonly AuthorityEpoch epoch;
    private readonly Dictionary<CommandKey, PredictedEntry> predicted = new();
    private readonly Dictionary<long, ActiveEntry> active = new();
    private readonly Dictionary<long, long> transactionToEffect = new();
    private readonly HashSet<long> retired = new();

    private long effectsApplied;
    private long effectsRefused;
    private long effectsDuplicated;
    private long predictionsMade;
    private long predictionsMerged;
    private long predictionsRevoked;
    private long predictionsExpired;
    private long effectsExpired;

    public EffectBinding(AuthorityEpoch epoch)
    {
        if (!epoch.IsValid) throw new ArgumentException("An effect binding needs a valid epoch.", nameof(epoch));
        this.epoch = epoch;
    }

    public AuthorityEpoch Epoch => epoch;

    public int PredictedCount => predicted.Count;

    public int ActiveCount => active.Count;

    public long EffectsApplied => effectsApplied;

    public long EffectsRefused => effectsRefused;

    public long EffectsDuplicated => effectsDuplicated;

    public long PredictionsMade => predictionsMade;

    public long PredictionsMerged => predictionsMerged;

    public long PredictionsRevoked => predictionsRevoked;

    public long PredictionsExpired => predictionsExpired;

    public long EffectsExpired => effectsExpired;

    public bool HasPredicted(in CommandKey key) => predicted.ContainsKey(key);

    public bool HasActive(long effectId) => active.ContainsKey(effectId);

    /// <summary>
    /// Files a client-side muzzle prediction. A retry of the same key must not stack a second one.
    /// </summary>
    public bool Predict(in CommandKey key, AuthorityEffectKind kind, int style, long clientTick)
    {
        if (!key.IsValid || !key.Epoch.Equals(epoch))
        {
            effectsRefused++;
            return false;
        }
        if (kind == AuthorityEffectKind.Unknown || !Enum.IsDefined(typeof(AuthorityEffectKind), kind) ||
            style < 0 || clientTick < 0)
        {
            effectsRefused++;
            return false;
        }
        if (predicted.ContainsKey(key))
        {
            effectsDuplicated++;
            return false;
        }
        predicted[key] = new PredictedEntry { Kind = kind, Style = style, ClientTick = clientTick };
        predictionsMade++;
        return true;
    }

    /// <summary>
    /// Links a command outcome to its prediction. A rejection revokes the muzzle (UI rollback);
    /// an acceptance is recorded so the later host event groups with it (A19 damage settlement).
    /// </summary>
    public bool NoteCommandResult(in CommandKey key, long transactionId, long appliedTick)
    {
        if (!key.IsValid || !key.Epoch.Equals(epoch) || transactionId < 0 || appliedTick < 0)
        {
            effectsRefused++;
            return false;
        }
        if (!predicted.TryGetValue(key, out var entry))
        {
            // No prediction filed (autonomous continuation, or the prediction already merged
            // through the cause fields when the host event arrived first). Still a legal answer.
            return true;
        }
        if (transactionId <= 0)
        {
            predicted.Remove(key);
            predictionsRevoked++;
            return true;
        }
        entry.TransactionId = transactionId;
        return true;
    }

    /// <summary>
    /// Installs one authoritative host event, merging the prediction it was caused by.
    /// A repeated delivery is idempotent; a reused id for a different event is refused.
    /// </summary>
    public bool ApplyHostEffect(in EffectState effect)
    {
        if (effect.Kind == AuthorityEffectKind.Unknown || effect.EffectId <= 0 ||
            effect.Life <= 0 || effect.Life > AuthorityEffectDefaults.MaxLifeTicks ||
            effect.StartTick < 0 || effect.Style < 0 || effect.TransactionId < 0)
        {
            effectsRefused++;
            return false;
        }
        if (effect.Caster.IsValid && !effect.Caster.Epoch.Equals(epoch))
        {
            effectsRefused++;
            return false;
        }
        if (effect.Target.IsValid && !effect.Target.Epoch.Equals(epoch))
        {
            effectsRefused++;
            return false;
        }
        if (!effect.Caster.IsValid && !effect.Target.IsValid && effect.TransactionId <= 0 && !effect.HasCause)
        {
            effectsRefused++;
            return false;
        }
        if (active.TryGetValue(effect.EffectId, out var existing))
        {
            if (SameEvent(existing.State, in effect))
            {
                effectsDuplicated++;
                return true;
            }
            effectsRefused++;
            return false;
        }
        if (retired.Contains(effect.EffectId))
        {
            // Ended is ended: a delayed duplicate of a finished event must not resurrect it.
            effectsRefused++;
            return false;
        }
        // The cause fields name the exact prediction this event answers, so the merge is
        // immediate in either network order: the prediction is always filed first.
        if (effect.HasCause)
        {
            CommandKey? match = null;
            foreach (var key in predicted.Keys)
            {
                if (key.Connection.Value == effect.CauseConnection && key.Sequence == effect.CauseSequence)
                {
                    match = key;
                    break;
                }
            }
            if (match.HasValue)
            {
                predicted.Remove(match.Value);
                predictionsMerged++;
            }
        }
        active[effect.EffectId] = new ActiveEntry { State = effect };
        if (effect.TransactionId > 0)
        {
            transactionToEffect[effect.TransactionId] = effect.EffectId;
        }
        effectsApplied++;
        return true;
    }

    /// <summary>
    /// Ends what the host tick says is over. Returns the number of shells removed.
    /// </summary>
    /// <remarks>
    /// Authoritative events end by <c>StartTick + Life</c> and never linger; unconfirmed
    /// predictions end by <see cref="PredictionTtlTicks"/>. Both removals are pure expiry —
    /// neither heals, kills, refunds, nor touches any number outside this table.
    /// </remarks>
    public int Tick(long hostTick)
    {
        if (hostTick < 0)
        {
            effectsRefused++;
            return 0;
        }
        var removed = 0;
        List<long> doomed = null;
        foreach (var pair in active)
        {
            if (pair.Value.State.StartTick + pair.Value.State.Life <= hostTick)
            {
                doomed ??= new List<long>();
                doomed.Add(pair.Key);
            }
        }
        if (doomed != null)
        {
            foreach (var id in doomed)
            {
                if (active.TryGetValue(id, out var entry) && entry.State.TransactionId > 0)
                {
                    if (transactionToEffect.TryGetValue(entry.State.TransactionId, out var mapped) &&
                        mapped == id)
                    {
                        transactionToEffect.Remove(entry.State.TransactionId);
                    }
                }
                active.Remove(id);
                retired.Add(id);
                effectsExpired++;
                removed++;
            }
        }
        List<CommandKey> stale = null;
        foreach (var pair in predicted)
        {
            if (pair.Value.ClientTick + PredictionTtlTicks <= hostTick)
            {
                stale ??= new List<CommandKey>();
                stale.Add(pair.Key);
            }
        }
        if (stale != null)
        {
            foreach (var key in stale)
            {
                predicted.Remove(key);
                predictionsExpired++;
                removed++;
            }
        }
        return removed;
    }

    public void Clear()
    {
        predicted.Clear();
        active.Clear();
        transactionToEffect.Clear();
        retired.Clear();
    }

    private static bool SameEvent(in EffectState left, in EffectState right) =>
        left.EffectId == right.EffectId && left.Kind == right.Kind && left.Style == right.Style &&
        left.TransactionId == right.TransactionId && left.StartTick == right.StartTick &&
        left.Life == right.Life && left.CauseConnection == right.CauseConnection &&
        left.CauseSequence == right.CauseSequence && left.Caster.Equals(right.Caster) &&
        left.Target.Equals(right.Target);
}
