using System;
using System.Collections.Generic;
using System.Text;
using NebulaWorld;
using UnityEngine;

namespace AuthorityBaseline;

/// <summary>
/// Frame-side sampler. The vanilla rules run inside the logic frame; this runs after it on the
/// main thread and records the per-frame before/after of every object under observation.
/// It only reads pools. It is the A00 backstop for changes that no single hook covers.
/// </summary>
internal static class BaselineSampler
{
    private const float SampleInterval = 0.25f;
    private const float HeartbeatInterval = 5f;
    private static float lastSample;
    private static float lastHeartbeat;
    private static int rosterErrors;

    public static void Tick()
    {
        try
        {
            var now = Time.realtimeSinceStartup;
            if (now - lastSample >= SampleInterval)
            {
                lastSample = now;
                SampleTargets(false);
            }
            if (now - lastHeartbeat >= HeartbeatInterval)
            {
                lastHeartbeat = now;
                SampleTargets(true);
                SampleRoster();
            }
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    public static void SampleTargets(bool heartbeat)
    {
        var targets = BaselineRuntime.TrackedSnapshot();
        if (targets.Length == 0) return;
        RefreshWatchedStats(targets);
        foreach (var target in targets)
        {
            try
            {
                var snapshot = BaselineWorld.Resolve(target);
                // A missing combat/construct stat is its own observable state (the E06
                // HandleFullHp path clears combatStatId). Comparing it against a previous
                // sample's hp would report a phantom "hp went to 0", so the presence flags
                // are part of the change detection.
                var changed = !target.HasSample ||
                              target.LastHasCombatStat != snapshot.HasCombatStat ||
                              target.LastHasConstructStat != snapshot.HasConstructStat ||
                              target.LastHp != snapshot.Hp ||
                              target.LastRepairer != snapshot.RepairerCount ||
                              target.LastCombatStatId != snapshot.CombatStatId ||
                              target.LastConstructStatId != snapshot.ConstructStatId ||
                              target.LastOwner != snapshot.Owner ||
                              target.LastGeneration != snapshot.Generation ||
                              !snapshot.Resolved;
                if (!changed && !heartbeat) continue;
                var record = BaselineRuntime.Log.New(heartbeat ? "sample.heartbeat" : "sample.change", "BaselineSampler");
                record.PlanetId = target.PlanetId;
                record.ObjectKey = snapshot.ObjectKey;
                record.RawSlot = snapshot.RawSlot;
                record.Owner = snapshot.Owner;
                record.Revision = snapshot.Generation >= 0 ? "gen=" + snapshot.Generation : null;
                if (snapshot.Resolved)
                {
                    if (snapshot.HasCombatStat)
                    {
                        record.HpAfter = snapshot.Hp;
                        record.HpMax = snapshot.HpMax;
                        record.HpRecover = snapshot.HpRecover;
                        record.HpIncoming = snapshot.HpIncoming;
                    }
                    if (snapshot.HasConstructStat) record.RepairerAfter = snapshot.RepairerCount;
                }
                if (target.HasSample)
                {
                    record.HpBefore = target.LastHasCombatStat ? target.LastHp : (int?)null;
                    record.RepairerBefore = target.LastHasConstructStat ? target.LastRepairer : (int?)null;
                }
                record.Reason = snapshot.Resolved
                    ? (heartbeat ? "Heartbeat" : "StateChanged")
                    : snapshot.UnresolvedReason;
                record.Note = "trackedAs=" + target.Kind + "#" + target.NativeId +
                              " hasCombatStat=" + (snapshot.HasCombatStat ? "true" : "false") +
                              " hasConstructStat=" + (snapshot.HasConstructStat ? "true" : "false") +
                              (heartbeat ? " heartbeat" : "");
                BaselineRuntime.Log.Emit(record);

                target.HasSample = true;
                target.LastHasCombatStat = snapshot.HasCombatStat;
                target.LastHasConstructStat = snapshot.HasConstructStat;
                target.LastHp = snapshot.Hp;
                target.LastRepairer = snapshot.RepairerCount;
                target.LastCombatStatId = snapshot.CombatStatId;
                target.LastConstructStatId = snapshot.ConstructStatId;
                target.LastOwner = snapshot.Owner;
                target.LastGeneration = snapshot.Generation;
                if (heartbeat) RecordSweep(target, snapshot);
            }
            catch (Exception)
            {
                // diagnostic only
            }
        }
    }

    private static void RecordSweep(TrackedTarget target, TargetSnapshot snapshot)
    {
        if (target.Kind != "enemy" || !snapshot.Resolved || !snapshot.HasCombatStat) return;
        lock (BaselineRuntime.EnemySweep)
        {
            EnemySweepEntry entry = null;
            foreach (var candidate in BaselineRuntime.EnemySweep)
            {
                if (candidate.PlanetId == target.PlanetId && candidate.EnemyId == target.NativeId)
                {
                    entry = candidate;
                    break;
                }
            }
            if (entry == null)
            {
                entry = new EnemySweepEntry
                {
                    PlanetId = target.PlanetId,
                    EnemyId = target.NativeId,
                    FirstTick = BaselineRuntime.GameTick()
                };
                BaselineRuntime.EnemySweep.Add(entry);
            }
            entry.LastTick = BaselineRuntime.GameTick();
            entry.Samples++;
            if (entry.LastHp != int.MinValue && snapshot.Hp > entry.LastHp) entry.HpIncreases++;
            if (snapshot.Hp <= 0) entry.ZeroHpSamples++;
            entry.LastHp = snapshot.Hp;
            if (snapshot.Hp < entry.MinHp) entry.MinHp = snapshot.Hp;
            if (snapshot.Hp > entry.MaxHp) entry.MaxHp = snapshot.Hp;
        }
    }

    private static void RefreshWatchedStats(TrackedTarget[] targets)
    {
        var combat = new List<int>();
        var construct = new List<int>();
        foreach (var target in targets)
        {
            try
            {
                var snapshot = BaselineWorld.Resolve(target);
                if (snapshot.CombatStatId > 0) combat.Add(snapshot.CombatStatId);
                if (snapshot.ConstructStatId > 0) construct.Add(snapshot.ConstructStatId);
            }
            catch (Exception)
            {
                // diagnostic only
            }
        }
        combat.Sort();
        construct.Sort();
        BaselineHooks.SetWatched(combat.ToArray(), construct.ToArray());
    }

    /// <summary>Host-side roster sample: who is connected and on which planet (scenario 3).</summary>
    public static void SampleRoster()
    {
        if (!BaselineRuntime.MultiplayerActive) return;
        var record = BaselineRuntime.Log.New("session.roster", "BaselineSampler");
        record.Reason = "Heartbeat";
        record.Note = RosterText();
        BaselineRuntime.Log.Emit(record);
    }

    private static string RosterText()
    {
        var sb = new StringBuilder(256);
        try
        {
            sb.Append("isServer=").Append(BaselineRuntime.IsServer)
              .Append(" isClient=").Append(BaselineRuntime.IsClient)
              .Append(" players=").Append(BaselineRuntime.PlayerCount());
            sb.Append(" localPlanet=").Append(GameMain.localPlanet?.id ?? -1);
            if (BaselineRuntime.IsServer)
            {
                sb.Append(" connected=[");
                var first = true;
                foreach (var pair in Multiplayer.Session.Server.Players.Connected)
                {
                    if (!first) sb.Append(';');
                    first = false;
                    sb.Append(pair.Value.Id).Append(':').Append(pair.Value.Data?.Username ?? "?")
                      .Append('@').Append(pair.Value.Data?.LocalPlanetId ?? -1);
                }
                sb.Append(']');
            }
            else
            {
                sb.Append(" remoteModels=[");
                var first = true;
                using (Multiplayer.Session.World.GetRemotePlayersModels(out var models))
                {
                    foreach (var pair in models)
                    {
                        if (!first) sb.Append(';');
                        first = false;
                        sb.Append(pair.Key).Append('@').Append(pair.Value.Movement.localPlanetId);
                    }
                }
                sb.Append(']');
            }
        }
        catch (Exception error)
        {
            rosterErrors++;
            sb.Append(" rosterError=").Append(error.GetType().Name);
        }
        return sb.ToString();
    }

    public static int RosterErrors => rosterErrors;
}
