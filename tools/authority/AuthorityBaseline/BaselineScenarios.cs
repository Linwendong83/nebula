using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NebulaWorld;
using UnityEngine;

namespace AuthorityBaseline;

/// <summary>
/// Reproducible baseline scenarios, driven from a control file so they can run without a GUI.
///
/// The injected actions are fault injection for the baseline record, not user-facing behaviour:
/// every one of them is written to the log as a driver.* event so that injected damage can never
/// be confused with organic gameplay in the evidence. The logger hooks themselves stay read-only.
/// </summary>
internal static class BaselineScenarios
{
    public static string ControlDirectory;
    public static string Role;
    public static string Outcome = "idle";
    public static int Errors;
    public static int CommandsExecuted;

    private static string lastCommand = "";
    private static AttackPlan plan;
    private static readonly List<string> Transcript = new();

    private sealed class AttackPlan
    {
        public string Kind; // "enemy" | "building"
        public int PlanetId;
        public int NativeId;
        public int Amount;
        public int IntervalTicks;
        public int Remaining;
        public long NextTick;
        public bool AsPlayerCaster;
    }

    public static void Tick()
    {
        try
        {
            RunPlan();
            Poll();
        }
        catch (Exception error)
        {
            Errors++;
            Outcome = "error:" + error.GetType().Name + ":" + error.Message.Replace('\n', ' ');
            WriteStatus();
        }
    }

    private static void RunPlan()
    {
        if (plan == null) return;
        if (!BaselineRuntime.MultiplayerActive || !Multiplayer.Session.IsGameLoaded) return;
        var tick = BaselineRuntime.GameTick();
        if (tick < plan.NextTick) return;
        plan.NextTick = tick + Math.Max(1, plan.IntervalTicks);
        var applied = ApplyDamage(plan.Kind, plan.PlanetId, plan.NativeId, plan.Amount, plan.AsPlayerCaster);
        var record = BaselineRuntime.Log.New("driver.inject", "BaselineScenarios");
        record.PlanetId = plan.PlanetId;
        record.ObjectKey = BaselineWorld.ObjectKeyText(plan.Kind, plan.PlanetId, plan.NativeId, -1);
        record.Owner = plan.AsPlayerCaster ? "player:" + LocalPlayerId() : "driver";
        record.Reason = "InjectedDamage";
        record.Note = "kind=" + plan.Kind + " amount=" + plan.Amount + " applied=" + applied +
                      " remaining=" + plan.Remaining;
        BaselineRuntime.Log.Emit(record);
        plan.Remaining--;
        if (plan.Remaining <= 0)
        {
            Outcome = "ok:plan-complete:" + plan.Kind + "#" + plan.NativeId;
            plan = null;
        }
    }

    private static int LocalPlayerId()
    {
        try
        {
            return BaselineRuntime.MultiplayerActive ? Multiplayer.Session.LocalPlayer.Id : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Applies one vanilla damage event. Returns the damage actually handed to vanilla.</summary>
    private static int ApplyDamage(string kind, int planetId, int nativeId, int amount, bool playerCaster)
    {
        var factory = BaselineWorld.Factory(planetId);
        if (factory == null) return 0;
        var casterId = playerCaster ? LocalPlayerId() : 0;
        if (kind == "enemy")
        {
            var target = new SkillTarget { id = nativeId, astroId = planetId, type = ETargetType.Enemy };
            var caster = new SkillTarget
            {
                id = casterId,
                astroId = planetId,
                type = playerCaster ? ETargetType.Player : ETargetType.None
            };
            GameMain.spaceSector.skillSystem.DamageObject(amount, 1, ref target, ref caster);
            return amount;
        }
        var localTarget = new SkillTargetLocal { id = nativeId, type = ETargetType.None };
        var localCaster = new SkillTargetLocal
        {
            id = casterId,
            type = playerCaster ? ETargetType.Player : ETargetType.None
        };
        GameMain.spaceSector.skillSystem.DamageGroundObjectByLocalCaster(factory, amount, 1, ref localTarget, ref localCaster);
        return amount;
    }

    private static void Poll()
    {
        if (string.IsNullOrEmpty(ControlDirectory)) return;
        var path = Path.Combine(ControlDirectory, Role + ".command");
        if (!File.Exists(path)) return;
        var command = File.ReadAllText(path).Trim();
        if (command.Length == 0 || command == lastCommand) return;
        lastCommand = command;
        var words = command.Split(' ');
        try
        {
            Execute(words);
        }
        catch (Exception error)
        {
            Errors++;
            Outcome = "command-error:" + DescribeException(error);
        }
        CommandsExecuted++;
        Transcript.Add(command + " -> " + Outcome);
        WriteStatus();
    }

    /// <summary>
    /// Flattens an exception chain. A TypeInitializationException's own message says nothing
    /// about the actual failure, and that detail is exactly what a diagnostic harness exists to
    /// report instead of hiding.
    /// </summary>
    private static string DescribeException(Exception error)
    {
        var sb = new StringBuilder();
        var current = error;
        var depth = 0;
        while (current != null && depth < 5)
        {
            if (depth > 0) sb.Append(" <- ");
            sb.Append(current.GetType().Name).Append(": ").Append(current.Message);
            current = current.InnerException;
            depth++;
        }
        return sb.ToString().Replace('\n', ' ').Replace('\r', ' ');
    }

    private static void Execute(string[] words)
    {
        var verb = words.Length > 1 ? words[1] : "status";
        switch (verb)
        {
            case "status":
                Outcome = "ok:status";
                break;
            case "quit":
                Outcome = "ok:quit";
                WriteStatus();
                Application.Quit();
                break;
            case "observe":
                Observe(words);
                break;
            case "clear":
                BaselineRuntime.Reset();
                Outcome = "ok:cleared";
                break;
            case "attack":
                PlanAttack("enemy", words);
                break;
            case "damagebuilding":
                PlanAttack("building", words);
                break;
            case "sample":
                BaselineSampler.SampleTargets(true);
                Outcome = "ok:sampled";
                break;
            case "sweep":
                WriteSweep();
                break;
            case "frameprobe":
                // "frameprobe start N" arms the ordered event log; "frameprobe dump" writes it.
                // They must be separate commands: the log needs real frames to elapse between them.
                if (words.Length > 2 && words[2] == "dump")
                {
                    FrameProbe.Dump(BaselineRuntime.LogDirectory, Role);
                    Outcome = "ok:frameprobe-dump";
                }
                else
                {
                    FrameProbe.StartRecording(Arg(words, 2, 3));
                    Outcome = "ok:frameprobe-armed";
                }
                break;
            case "writescan":
                WriteScanner.Dump(BaselineRuntime.LogDirectory, Role);
                Outcome = "ok:writescan";
                break;
            case "scan":
                WriteScan();
                break;
            case "setupbase":
                SetupBattleBase();
                break;
            case "disconnect":
                Outcome = "ok:disconnecting";
                WriteStatus();
                Multiplayer.LeaveGame();
                return;
            case "reconnect":
                // Clears the local session so the driver's join loop runs again. This is the
                // path that triggers a fresh factory load on the host (E06).
                if (Multiplayer.IsActive) Multiplayer.LeaveGame();
                Outcome = "ok:reconnecting";
                BaselinePlugin.RejoinRequested = true;
                return;
            case "probe":
                WriteProbe();
                break;
            case "fault":
                ApplyFault(words);
                break;
            case "faultclear":
                NebulaWorld.Authority.AuthorityFaultControl.Clear();
                EmitFaultRecord("clear", "ok");
                Outcome = "ok:fault-clear:" + NebulaWorld.Authority.AuthorityFaultControl.Describe();
                break;            case "faultstatus":
                Outcome = "ok:fault-status:enabled=" +
                          (NebulaWorld.Authority.AuthorityFaultControl.IsEnabled ? 1 : 0) +
                          " armed=" + (NebulaWorld.Authority.AuthorityFaultControl.IsArmed ? 1 : 0) +
                          " " + NebulaWorld.Authority.AuthorityFaultControl.Describe();
                break;
            case "replica":
                WriteReplica();
                break;
            case "perf":
                WritePerf();
                break;
            default:
                Outcome = "unknown-command:" + verb;
                break;
        }
    }

    /// <summary>
    /// A22 matrix driver verb: re-arms the runtime fault rules from a launch-spec string.
    /// Same refuse-the-whole-spec semantics as launch: a bad spec leaves the armed rules
    /// untouched and is reported, never a silently clean run. Logged as driver.fault so the
    /// HostTick-aligned evidence can tell injected faults from organic gameplay.
    /// </summary>
    private static void ApplyFault(string[] words)
    {
        if (words.Length < 3 || string.IsNullOrWhiteSpace(words[2]))
        {
            Outcome = "fault-failed:EmptySpec";
            return;
        }
        var spec = words[2].Trim();
        if (NebulaWorld.Authority.AuthorityFaultControl.TryApplySpec(spec, out var error))
        {
            EmitFaultRecord(spec, "ok");
            Outcome = "ok:fault-armed:" + NebulaWorld.Authority.AuthorityFaultControl.Describe();
        }
        else
        {
            EmitFaultRecord(spec, "refused:" + error);
            Outcome = "fault-failed:" + error;
        }
    }

    /// <summary>
    /// A22 matrix diagnostic: dumps the authority replica/replicator state so a silent
    /// subscription or baseline failure becomes a readable fact instead of a guess.
    /// Read-only; never touches world state.
    /// </summary>
    private static void WriteReplica()
    {
        var path = Path.Combine(BaselineRuntime.LogDirectory, Role + "-replica.txt");
        var sb = new StringBuilder();
        sb.AppendLine("role=" + Role + " tick=" + BaselineRuntime.GameTick());
        try
        {
            var session = Multiplayer.Session;
            var runtime = session?.AuthorityRuntime;
            if (runtime == null)
            {
                sb.AppendLine("runtime=null (no authority session)");
            }
            else
            {
                sb.AppendLine("isHostAuthority=" + (runtime.IsHostAuthority ? 1 : 0));
                sb.AppendLine("identityActive=" + (runtime.Identity.IsActive ? 1 : 0) +
                              " isHost=" + (runtime.Identity.IsHost ? 1 : 0) +
                              " epoch=" + runtime.Identity.Epoch);
                sb.AppendLine("framesDrained=" + runtime.FramesDrained +
                              " inboundApplied=" + runtime.InboundApplied +
                              " inboundDropped=" + runtime.InboundDropped +
                              " acksDropped=" + runtime.AcksDropped +
                              " scopeControlHandled=" + runtime.ScopeControlHandled +
                              " scopeControlRefused=" + runtime.ScopeControlRefused +
                              " scopeControlDropped=" + runtime.ScopeControlDropped);
                var replica = runtime.WorldReplica;
                if (replica != null)
                {
                    sb.AppendLine("replica=mirrors:" + replica.Mirrors.Count +
                                  " needsResync:" + replica.ScopesNeedingResync.Count +
                                  " applied:" + replica.AppliedTotal +
                                  " snapshotsInstalled:" + replica.SnapshotsInstalledTotal +
                                  " gap:" + replica.GapTotal +
                                  " resyncsRequested:" + replica.ResyncsRequestedTotal +
                                  " unsentControl:" + replica.UnsentScopeControlTotal +
                                  " suspended:" + replica.StreamSuspendedTotal +
                                  " unknownObject:" + replica.UnknownObjectTotal +
                                  " duplicate:" + replica.DuplicateTotal +
                                  " stale:" + replica.StaleTotal +
                                  " tombstoneRefused:" + replica.TombstoneRefusedTotal +
                                  " baselineRefused:" + replica.BaselineRefusedTotal +
                                  " sequenceDropped:" + replica.SequenceDroppedTotal +
                                  " streamBeforeBaseline:" + replica.StreamBeforeBaselineTotal +
                                  " digestsMatched:" + replica.DigestsMatchedTotal +
                                  " digestsMismatched:" + replica.DigestsMismatchedTotal +
                                  " digestsSuperseded:" + replica.DigestsSupersededTotal +
                                  " digestsDropped:" + replica.DigestsDroppedTotal +
                                  " digestSummaries:" + replica.DigestSummariesRecordedTotal);
                    sb.AppendLine("standingScopes=" + runtime.StandingScopes.Count);
                    foreach (var scope in runtime.StandingScopes)
                    {
                        if (replica.Versions.TryGetScope(scope, out var state))
                        {
                            sb.AppendLine("scope " + scope + " phase=" + state.Phase +
                                          " baseline=" + state.BaselineId +
                                          " applied=" + state.LastAppliedSequence +
                                          " subEpoch=" + state.SubscriptionEpoch);
                        }
                        else
                        {
                            sb.AppendLine("scope " + scope + " phase=<no-version-state>");
                        }
                    }
                }
                else
                {
                    sb.AppendLine("replica=null (host or no client world)");
                }
                var replicator = runtime.HostReplicator;
                if (replicator != null)
                {
                    sb.AppendLine("replicator=scopes:" + replicator.ScopeCount +
                                  " captured:" + replicator.FramesCaptured +
                                  " events:" + replicator.EventsAppended +
                                  " packets:" + replicator.PacketsSent +
                                  " snapshots:" + replicator.SnapshotsSent +
                                  " refused:" + replicator.SnapshotRefusedTotal +
                                  " acksOk:" + replicator.AcksAccepted +
                                  " acksRejected:" + replicator.AcksRejected +
                                  " resyncs:" + replicator.ResyncsServed +
                                  " digests:" + replicator.DigestsPublished +
                                  " lastCaptureTick:" + replicator.LastCaptureTick);
                    foreach (var line in replicator.DescribeScopes()) sb.AppendLine(line);
                }
                else
                {
                    sb.AppendLine("replicator=null (no registered world view)");
                }
                var link = runtime.FaultLink;
                sb.AppendLine(link == null
                    ? "faultLink=null (not wired at registration)"
                    : "faultLink=sent:" + link.SentTotal +
                      " delivered:" + link.DeliveredTotal +
                      " duplicates:" + link.DuplicateDeliveries +
                      " dropped:" + link.DroppedTotal +
                      " requeued:" + link.RequeuedDuplicates +
                      " held:" + link.HeldCount +
                      " rules=" + NebulaWorld.Authority.AuthorityFaultControl.Describe());
            }
        }
        catch (Exception error)
        {
            sb.AppendLine("replica error: " + error.GetType().Name + ": " + error.Message);
        }
        File.WriteAllText(path, sb.ToString());
        Outcome = "ok:replica:" + path;
    }

    /// <summary>
    /// A23 matrix diagnostic: writes this instance's replication performance report.
    /// </summary>
    /// <remarks>
    /// The report is the evidence VALIDATION §9's budget is judged against, so it carries the budget
    /// that produced it, the capture/apply latency distributions, the per-family byte rates and the
    /// queue depths. It is observation only — reading the counters touches no world state.
    /// </remarks>
    private static void WritePerf()
    {
        var path = Path.Combine(BaselineRuntime.LogDirectory, Role + "-perf.txt");
        var sb = new StringBuilder();
        sb.AppendLine("role=" + Role + " tick=" + BaselineRuntime.GameTick());
        try
        {
            var runtime = Multiplayer.Session?.AuthorityRuntime;
            if (runtime == null)
            {
                sb.AppendLine("runtime=null (no authority session)");
            }
            else
            {
                foreach (var line in runtime.Metrics.Describe()) sb.AppendLine(line);
                sb.AppendLine("inboundApplied=" + runtime.InboundApplied +
                              " inboundDropped=" + runtime.InboundDropped +
                              " framesDrained=" + runtime.FramesDrained);
                var replicator = runtime.HostReplicator;
                if (replicator != null)
                {
                    foreach (var line in replicator.DescribePerf()) sb.AppendLine(line);
                }
                else
                {
                    sb.AppendLine("replicator=null (host or no registered world view)");
                }
                sb.AppendLine("csv:");
                sb.Append(runtime.Metrics.ToCsv());
            }
        }
        catch (Exception error)
        {
            sb.AppendLine("perf error: " + error.GetType().Name + ": " + error.Message);
        }
        File.WriteAllText(path, sb.ToString());
        Outcome = "ok:perf:" + path;
    }

    private static void EmitFaultRecord(string spec, string result)
    {        try
        {
            var record = BaselineRuntime.Log.New("driver.fault", "BaselineScenarios");
            record.Reason = "FaultSpec";
            record.Note = "role=" + Role + " spec=" + spec + " result=" + result +
                          " armed=" + NebulaWorld.Authority.AuthorityFaultControl.Describe();
            BaselineRuntime.Log.Emit(record);
        }
        catch (Exception)
        {
            // Logging must never break the verb itself; the Outcome string carries the result.
        }
    }

    private static int Arg(string[] words, int index, int fallback)
    {
        if (index >= words.Length) return fallback;
        return int.TryParse(words[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private static void Observe(string[] words)
    {
        var kind = words.Length > 2 ? words[2] : "enemy";
        var planetId = Arg(words, 3, 0);
        var nativeId = Arg(words, 4, 0);
        if (planetId <= 0) planetId = GameMain.galaxy.birthPlanetId;
        var factory = BaselineWorld.Factory(planetId);
        if (factory == null)
        {
            Outcome = "observe-failed:FactoryNotLoaded:" + planetId;
            return;
        }
        if (kind == "enemy")
        {
            if (nativeId <= 0) nativeId = PickEnemy(factory);
            if (nativeId <= 0)
            {
                Outcome = "observe-failed:NoEnemyOnPlanet:" + planetId;
                return;
            }
            BaselineRuntime.Track(new TrackedTarget { Kind = "enemy", PlanetId = planetId, NativeId = nativeId });
            Outcome = "ok:observing-enemy:" + planetId + ":" + nativeId;
            return;
        }
        if (nativeId <= 0) nativeId = PickBuilding(factory);
        if (nativeId <= 0)
        {
            Outcome = "observe-failed:NoBuildingOnPlanet:" + planetId;
            return;
        }
        BaselineRuntime.Track(new TrackedTarget { Kind = "building", PlanetId = planetId, NativeId = nativeId });
        Outcome = "ok:observing-building:" + planetId + ":" + nativeId;
    }

    public static int PickEnemy(PlanetFactory factory)
    {
        if (factory?.enemyPool == null) return 0;
        for (var i = 1; i < factory.enemyPool.Length; i++)
        {
            ref var enemy = ref factory.enemyPool[i];
            if (enemy.id == i && !enemy.isInvincible) return i;
        }
        return 0;
    }

    /// <summary>
    /// Picks a repair-capable building near the local player: a battle base when one is close
    /// (it has its own construction module), otherwise the nearest entity with a combat stat.
    /// </summary>
    public static int PickBuilding(PlanetFactory factory)
    {
        if (factory?.entityPool == null) return 0;
        var origin = GameMain.mainPlayer != null ? GameMain.mainPlayer.position : Vector3.zero;
        var bestBase = 0;
        var bestBaseDistance = float.MaxValue;
        var bestEntity = 0;
        var bestEntityDistance = float.MaxValue;
        for (var i = 1; i < factory.entityPool.Length; i++)
        {
            ref var entity = ref factory.entityPool[i];
            if (entity.id != i) continue;
            var distance = (entity.pos - origin).sqrMagnitude;
            if (entity.battleBaseId > 0 && distance < bestBaseDistance)
            {
                bestBaseDistance = distance;
                bestBase = i;
            }
            if (entity.combatStatId > 0 && distance < bestEntityDistance)
            {
                bestEntityDistance = distance;
                bestEntity = i;
            }
        }
        if (bestBase > 0) return bestBase;
        if (bestEntity > 0) return bestEntity;
        // No combat building yet: take the closest entity at all so the repair chain can be observed.
        for (var i = 1; i < factory.entityPool.Length; i++)
        {
            ref var entity = ref factory.entityPool[i];
            if (entity.id != i) continue;
            var distance = (entity.pos - origin).sqrMagnitude;
            if (distance < bestEntityDistance)
            {
                bestEntityDistance = distance;
                bestEntity = i;
            }
        }
        return bestEntity;
    }

    private static void PlanAttack(string kind, string[] words)
    {
        var count = Math.Max(1, Arg(words, 2, 1));
        var amount = Math.Max(1, Arg(words, 3, 100));
        var interval = Math.Max(1, Arg(words, 4, 10));
        var asPlayer = Arg(words, 5, 1) != 0;
        var targets = BaselineRuntime.TrackedSnapshot();
        TrackedTarget target = null;
        foreach (var candidate in targets)
        {
            if (candidate.Kind == kind)
            {
                target = candidate;
                break;
            }
        }
        if (target == null)
        {
            var planetId = GameMain.galaxy.birthPlanetId;
            var factory = BaselineWorld.Factory(planetId);
            if (factory == null)
            {
                Outcome = "attack-failed:FactoryNotLoaded";
                return;
            }
            var nativeId = kind == "enemy" ? PickEnemy(factory) : PickBuilding(factory);
            if (nativeId <= 0)
            {
                Outcome = "attack-failed:NoTarget:" + kind;
                return;
            }
            target = new TrackedTarget { Kind = kind, PlanetId = planetId, NativeId = nativeId };
            BaselineRuntime.Track(target);
        }
        plan = new AttackPlan
        {
            Kind = kind,
            PlanetId = target.PlanetId,
            NativeId = target.NativeId,
            Amount = amount,
            IntervalTicks = interval,
            Remaining = count,
            NextTick = BaselineRuntime.GameTick() + interval,
            AsPlayerCaster = asPlayer
        };
        Outcome = "ok:plan:" + kind + "#" + target.NativeId + " count=" + count + " amount=" + amount +
                  " interval=" + interval + " playerCaster=" + asPlayer;
    }

    /// <summary>
    /// Reports where combat and repair targets actually exist in this sandbox: per planet, how many
    /// live enemies and how many combat-stat buildings. A00 needs this because a fresh galaxy has
    /// no dark fog on the birth planet, and "no target" must be a recorded fact rather than a
    /// silent scenario failure.
    /// </summary>
    /// <summary>
    /// Scenario-2 setup: places one battle base next to the host mecha through the vanilla
    /// entity creation path, so the repair scenario has a building that both has a combat stat
    /// and carries its own construction module (E04/E07).
    ///
    /// This is harness setup, not part of the mod: it is logged as driver.setup and is only ever
    /// issued by the scenario script against a throwaway isolated sandbox.
    /// </summary>
    private static void SetupBattleBase()
    {
        var planetId = GameMain.localPlanet?.id ?? GameMain.galaxy.birthPlanetId;
        var factory = BaselineWorld.Factory(planetId);
        if (factory == null)
        {
            Outcome = "setup-failed:FactoryNotLoaded:" + planetId;
            return;
        }
        var player = GameMain.mainPlayer;
        if (player == null)
        {
            Outcome = "setup-failed:NoMainPlayer";
            return;
        }
        // Find a battle-base prefab from the item database rather than hardcoding an item id.
        ItemProto item = null;
        PrefabDesc desc = null;
        foreach (var candidate in LDB.items.dataArray)
        {
            var prefab = candidate?.prefabDesc;
            if (prefab == null || !prefab.isBattleBase) continue;
            item = candidate;
            desc = prefab;
            break;
        }
        if (item == null)
        {
            Outcome = "setup-failed:NoBattleBasePrefab";
            return;
        }
        var position = player.position + player.position.normalized * 12f;
        var entity = default(EntityData);
        entity.protoId = (short)item.ID;
        entity.modelIndex = (short)item.ModelIndex;
        entity.pos = position;
        entity.rot = Quaternion.identity;
        entity.alt = position.magnitude;
        entity.tilt = 0f;
        var entityId = factory.AddEntityDataWithComponents(entity, 0);
        var record = BaselineRuntime.Log.New("driver.setup", "BaselineScenarios");
        record.PlanetId = planetId;
        record.ObjectKey = BaselineWorld.ObjectKeyText("entity", planetId, entityId, -1);
        record.Reason = "BattleBaseSetup";
        record.Note = "protoId=" + item.ID + " modelIndex=" + item.ModelIndex +
                      " entityId=" + entityId + " isBattleBase=" + desc.isBattleBase;
        BaselineRuntime.Log.Emit(record);
        if (entityId <= 0)
        {
            Outcome = "setup-failed:AddEntityDataWithComponentsReturned:" + entityId;
            return;
        }
        BaselineRuntime.Track(new TrackedTarget { Kind = "building", PlanetId = planetId, NativeId = entityId });
        Outcome = "ok:base-setup:" + planetId + ":" + entityId;
    }

    private static void WriteScan()
    {
        var path = Path.Combine(BaselineRuntime.LogDirectory, Role + "-scan.txt");
        var sb = new StringBuilder();
        sb.AppendLine("role=" + Role + " tick=" + BaselineRuntime.GameTick());
        sb.AppendLine("localPlanet=" + (GameMain.localPlanet?.id ?? -1) +
                      " birthPlanet=" + GameMain.galaxy?.birthPlanetId);
        sb.AppendLine("planetId\tenemies\tinvincible\tenemiesWithStat\tbuildingsWithCombatStat\tbuildingsWithConstructStat\tbattleBases\tlocalLoaded");
        try
        {
            var galaxy = GameMain.galaxy;
            if (galaxy?.stars != null)
            {
                foreach (var star in galaxy.stars)
                {
                    if (star?.planets == null) continue;
                    foreach (var planet in star.planets)
                    {
                        if (planet == null) continue;
                        var factory = planet.factory;
                        if (factory == null) continue;
                    var enemies = 0;
                    var invincible = 0;
                    var enemiesWithStat = 0;
                    if (factory.enemyPool != null)
                    {
                        for (var i = 1; i < factory.enemyPool.Length; i++)
                        {
                            ref var enemy = ref factory.enemyPool[i];
                            if (enemy.id != i) continue;
                            enemies++;
                            if (enemy.isInvincible) invincible++;
                            if (enemy.combatStatId > 0) enemiesWithStat++;
                        }
                    }
                    var buildingsWithCombat = 0;
                    var buildingsWithConstruct = 0;
                    var battleBases = 0;
                    if (factory.entityPool != null)
                    {
                        for (var i = 1; i < factory.entityPool.Length; i++)
                        {
                            ref var entity = ref factory.entityPool[i];
                            if (entity.id != i) continue;
                            if (entity.combatStatId > 0) buildingsWithCombat++;
                            if (entity.constructStatId > 0) buildingsWithConstruct++;
                            if (entity.battleBaseId > 0) battleBases++;
                        }
                    }
                    sb.Append(planet.id).Append('\t')
                      .Append(enemies).Append('\t')
                      .Append(invincible).Append('\t')
                      .Append(enemiesWithStat).Append('\t')
                      .Append(buildingsWithCombat).Append('\t')
                      .Append(buildingsWithConstruct).Append('\t')
                      .Append(battleBases).Append('\t')
                      .Append(planet.factoryLoaded ? 1 : 0).AppendLine();
                    }
                }
            }
            else
            {
                sb.AppendLine("galaxy or stars unavailable");
            }
            // Dark fog lives in the space sector for hive astros and in per-planet ground systems
            // for bases. Neither shows up in a factory-only scan, so report both explicitly.
            sb.AppendLine();
            sb.AppendLine("--- dark fog occupancy ---");
            var sector = GameMain.spaceSector;
            if (sector == null)
            {
                sb.AppendLine("spaceSector unavailable");
            }
            else
            {
                sb.AppendLine("spaceEnemies=" + sector.enemyCount +
                              " hiveSystems=" + (sector.dfHives?.Length ?? 0));
                var hiveAstros = 0;
                var hivesWithUnits = 0;
                if (sector.dfHivesByAstro != null)
                {
                    for (var i = 0; i < sector.dfHivesByAstro.Length; i++)
                    {
                        var hive = sector.dfHivesByAstro[i];
                        if (hive == null || !hive.realized) continue;
                        hiveAstros++;
                        var units = 0;
                        if (sector.enemyPool != null)
                        {
                            for (var e = 1; e < sector.enemyPool.Length; e++)
                            {
                                if (sector.enemyPool[e].id != e) continue;
                                if (sector.enemyPool[e].astroId == i + 1000000) units++;
                            }
                        }
                        if (units > 0) hivesWithUnits++;
                        sb.AppendLine("hiveAstro=" + (i + 1000000) + " hiveAstroId=" + hive.hiveAstroId +
                                      " level=" + hive.evolve.level + " realized=" + hive.realized + " units=" + units);
                    }
                }
                sb.AppendLine("hiveAstros=" + hiveAstros + " hivesWithUnits=" + hivesWithUnits);
                var groundBases = 0;
                var groundUnits = 0;
                if (GameMain.galaxy?.stars != null)
                {
                    foreach (var star in GameMain.galaxy.stars)
                    {
                        if (star?.planets == null) continue;
                        foreach (var planet in star.planets)
                        {
                            try
                            {
                                var factory = planet?.factory;
                                if (factory == null) continue;
                                var bases = factory.enemySystem?.bases;
                                var live = 0;
                                if (bases?.buffer != null)
                                {
                                    for (var b = 1; b < bases.buffer.Length; b++)
                                    {
                                        var entry = bases.buffer[b];
                                        if (entry == null || entry.id != b) continue;
                                        live++;
                                    }
                                }
                                var units = 0;
                                if (factory.enemyPool != null)
                                {
                                    for (var e = 1; e < factory.enemyPool.Length; e++)
                                    {
                                        if (factory.enemyPool[e].id != e) continue;
                                        units++;
                                    }
                                }
                                if (live > 0 || units > 0)
                                {
                                    groundBases += live;
                                    groundUnits += units;
                                    sb.AppendLine("groundPlanet=" + planet.id + " bases=" + live + " units=" + units);
                                }
                            }
                            catch (Exception error)
                            {
                                sb.AppendLine("groundScanError planet=" + planet?.id + " " +
                                              error.GetType().Name + ": " + error.Message);
                            }
                        }
                    }
                }
                sb.AppendLine("groundBasesTotal=" + groundBases + " groundUnitsTotal=" + groundUnits);
            }
        }
        catch (Exception error)
        {
            sb.AppendLine("scan error: " + error.GetType().Name + ": " + error.Message);
        }
        File.WriteAllText(path, sb.ToString());
        Outcome = "ok:scan:" + path;
    }

    private static void WriteSweep()
    {
        var path = Path.Combine(BaselineRuntime.LogDirectory, Role + "-sweep.txt");
        var sb = new StringBuilder();
        sb.AppendLine("role=" + Role + " runId=" + BaselineRuntime.RunId + " tick=" + BaselineRuntime.GameTick());
        lock (BaselineRuntime.EnemySweep)
        {
            foreach (var entry in BaselineRuntime.EnemySweep)
            {
                sb.AppendLine("planet=" + entry.PlanetId + " enemy=" + entry.EnemyId +
                              " samples=" + entry.Samples +
                              " firstTick=" + entry.FirstTick + " lastTick=" + entry.LastTick +
                              " minHp=" + (entry.MinHp == int.MaxValue ? "n/a" : entry.MinHp.ToString()) +
                              " maxHp=" + (entry.MaxHp == int.MinValue ? "n/a" : entry.MaxHp.ToString()) +
                              " lastHp=" + (entry.LastHp == int.MinValue ? "n/a" : entry.LastHp.ToString()) +
                              " hpIncreases=" + entry.HpIncreases +
                              " zeroHpSamples=" + entry.ZeroHpSamples);
            }
        }
        File.WriteAllText(path, sb.ToString());
        Outcome = "ok:sweep:" + path;
    }

    private static void WriteProbe()
    {
        var path = Path.Combine(BaselineRuntime.LogDirectory, Role + "-probe.txt");
        var sb = new StringBuilder();
        sb.AppendLine("role=" + Role);
        sb.AppendLine("runId=" + BaselineRuntime.RunId);
        sb.AppendLine("tick=" + BaselineRuntime.GameTick());
        sb.AppendLine("isServer=" + BaselineRuntime.IsServer);
        sb.AppendLine("isClient=" + BaselineRuntime.IsClient);
        sb.AppendLine("players=" + BaselineRuntime.PlayerCount());
        sb.AppendLine("localPlanet=" + (GameMain.localPlanet?.id ?? -1));
        sb.AppendLine("birthPlanet=" + GameMain.galaxy?.birthPlanetId);
        sb.AppendLine("starCount=" + (GameMain.galaxy?.starCount ?? -1));
        sb.AppendLine("worldId=" + (Multiplayer.IsActive ? SaveManager.WorldId : 0));
        // A host can report IsGameLoaded while its logic frame is still paused; clients are then
        // rejected with HostStillLoading. Record both so a stalled host is visible in the probe.
        sb.AppendLine("isFullscreenPaused=" + GameMain.isFullscreenPaused);
        sb.AppendLine("gamePaused=" + GameMain.isPaused);
        sb.AppendLine("hookFailures=" + BaselineRuntime.HookFailures);
        sb.AppendLine("writeFailures=" + BaselineRuntime.WriteFailures);
        sb.AppendLine("tracked=" + BaselineRuntime.TrackedSnapshot().Length);
        sb.AppendLine("rosterErrors=" + BaselineSampler.RosterErrors);
        sb.AppendLine("commands=" + CommandsExecuted);
        AppendRepairEligibility(sb);
        sb.AppendLine("--- hooks ---");
        lock (BaselineRuntime.HookReport)
        {
            foreach (var line in BaselineRuntime.HookReport) sb.AppendLine(line);
        }
        sb.AppendLine("--- transcript ---");
        foreach (var line in Transcript) sb.AppendLine(line);
        File.WriteAllText(path, sb.ToString());
        Outcome = "ok:probe:" + path;
    }

    /// <summary>
    /// Records why the vanilla repair chain did or did not dispatch for the tracked building.
    /// A00 must not guess this: when a damaged building sits at repairerCount 0, the baseline
    /// record needs the actual gate values (switch state, build area, distance, energy, module
    /// idle count) rather than a narrative.
    /// </summary>
    private static void AppendRepairEligibility(StringBuilder sb)
    {
        sb.AppendLine("--- repair eligibility (host mecha + base modules) ---");
        try
        {
            var player = GameMain.mainPlayer;
            if (player == null)
            {
                sb.AppendLine("no main player");
                return;
            }
            var mecha = player.mecha;
            var module = mecha?.constructionModule;
            sb.AppendLine("mecha: position=" + player.position.ToString("F1") +
                          " buildArea=" + (mecha?.buildArea ?? -1f).ToString("F1") +
                          " droneEnabled=" + (module?.droneEnabled ?? false) +
                          " droneRepairEnabled=" + (module?.droneRepairEnabled ?? false));
            if (module != null)
            {
                sb.AppendLine("mecha module: idle=" + module.droneIdleCount +
                              " alive=" + module.droneAliveCount +
                              " total=" + module.droneCount +
                              " repairTargets=" + module.repairTargetTotalCount +
                              " buildTargets=" + module.buildTargetTotalCount +
                              " priority=" + module.dronePriority);
            }
            foreach (var target in BaselineRuntime.TrackedSnapshot())
            {
                var snapshot = BaselineWorld.Resolve(target);
                var factory = BaselineWorld.Factory(target.PlanetId);
                if (factory?.entityPool == null || target.NativeId >= factory.entityPool.Length) continue;
                ref var entity = ref factory.entityPool[target.NativeId];
                if (entity.id != target.NativeId) continue;
                var distance = (entity.pos - player.position).magnitude;
                var withinBuildArea = distance <= (mecha?.buildArea ?? 0f);
                sb.AppendLine("target#" + target.NativeId + ": distance=" + distance.ToString("F1") +
                              " buildArea=" + (mecha?.buildArea ?? -1f).ToString("F1") +
                              " withinArea=" + withinBuildArea +
                              " combatStat=" + snapshot.CombatStatId +
                              " constructStat=" + snapshot.ConstructStatId +
                              " hp=" + (snapshot.HasCombatStat ? snapshot.Hp.ToString() : "n/a") +
                              " hpMax=" + (snapshot.HasCombatStat ? snapshot.HpMax.ToString() : "n/a") +
                              " repairerCount=" + (snapshot.HasConstructStat ? snapshot.RepairerCount : 0) +
                              " constructOwner=" + snapshot.Owner);
                var construction = factory.constructionSystem;
                if (construction?.constructStats?.buffer != null && snapshot.ConstructStatId > 0 &&
                    snapshot.ConstructStatId < construction.constructStats.buffer.Length)
                {
                    ref var stat = ref construction.constructStats.buffer[snapshot.ConstructStatId];
                    if (stat.id == snapshot.ConstructStatId)
                    {
                        sb.AppendLine("  constructStat: damageRegister=" + stat.damageRegister +
                                      " damageRate=" + stat.damageRate.ToString("F2") +
                                      " damageRateLastFrame=" + stat.damageRateLastFrame.ToString("F2") +
                                      " repairerModuleId=" + stat.repairerModuleId +
                                      " repairerValue=" + stat.repairerValue.ToString("F3"));
                    }
                }
                var combat = factory.skillSystem?.combatStats;
                if (combat?.buffer != null && snapshot.CombatStatId > 0 &&
                    snapshot.CombatStatId < combat.buffer.Length)
                {
                    ref var stat = ref combat.buffer[snapshot.CombatStatId];
                    if (stat.id == snapshot.CombatStatId)
                    {
                        sb.AppendLine("  combatStat: hp=" + stat.hp + "/" + stat.hpMax +
                                      " warningId=" + stat.warningId +
                                      " dynamic=" + stat.dynamic);
                    }
                }
                var constructionModule = factory.entityPool[target.NativeId].constructionModuleId;
                var battleBaseId = factory.entityPool[target.NativeId].battleBaseId;
                sb.AppendLine("  entity.constructionModuleId=" + constructionModule +
                              " battleBaseId=" + battleBaseId);
                // ConstructionModuleComponent.GameTick returns before any dispatch when the
                // battle base has no energy, so this is the first gate to record.
                var battleBases = factory.defenseSystem?.battleBases;
                long baseEnergy = -1;
                if (battleBases?.buffer != null && battleBaseId > 0 && battleBaseId < battleBases.buffer.Length)
                {
                    var baseComponent = battleBases.buffer[battleBaseId];
                    if (baseComponent != null && baseComponent.id == battleBaseId)
                    {
                        baseEnergy = baseComponent.energy;
                        sb.AppendLine("  battleBase: energy=" + baseComponent.energy +
                                      " energyMax=" + baseComponent.energyMax +
                                      " energyPerTick=" + baseComponent.energyPerTick);
                    }
                }
                var mechaModule = mecha?.constructionModule;
                var mechaIdle = mechaModule?.droneIdleCount ?? 0;
                var mechaEnabled = mechaModule?.droneEnabled ?? false;
                var mechaRepair = mechaModule?.droneRepairEnabled ?? false;
                // Naming follows the reason codes DESIGN 8.2 asks the dispatcher to expose, so the
                // baseline record and the future HostConstructionService speak the same language.
                string code;
                if (!snapshot.HasConstructStat) code = "NoDamageRecord";
                else if (snapshot.RepairerCount > 0) code = "AlreadyRepairing";
                else if (constructionModule > 0 && baseEnergy == 0) code = "NoEnergy";
                else if (constructionModule > 0 && baseEnergy < 0) code = "TargetNotReady";
                else if (constructionModule == 0 && !mechaEnabled) code = "Disabled";
                else if (constructionModule == 0 && !mechaRepair) code = "Disabled";
                else if (constructionModule == 0 && mechaIdle <= 0) code = "NoIdleDrone";
                else if (!withinBuildArea) code = "OutOfRange";
                else code = "Undetermined";
                sb.AppendLine("  dispatchReasonCode=" + code);
                if (constructionModule > 0 && construction?.constructionModules?.buffer != null &&
                    constructionModule < construction.constructionModules.buffer.Length)
                {
                    ref var cm = ref construction.constructionModules.buffer[constructionModule];
                    if (cm.id == constructionModule)
                    {
                        sb.AppendLine("  baseModule: droneEnabled=" + cm.droneEnabled +
                                      " droneRepairEnabled=" + cm.droneRepairEnabled +
                                      " idle=" + cm.droneIdleCount +
                                      " total=" + cm.droneCount +
                                      " repairTargets=" + cm.repairTargetTotalCount +
                                      " buildTargets=" + cm.buildTargetTotalCount +
                                      " autoReconstruct=" + cm.autoReconstruct);
                    }
                }
            }
        }
        catch (Exception error)
        {
            sb.AppendLine("eligibility error: " + error.GetType().Name + ": " + error.Message);
        }
    }

    public static void WriteStatus()
    {
        if (string.IsNullOrEmpty(ControlDirectory)) return;
        var sb = new StringBuilder();
        sb.AppendLine("role=" + Role);
        sb.AppendLine("ready=" + (BaselineRuntime.MultiplayerActive && Multiplayer.Session.IsGameLoaded ? 1 : 0));
        sb.AppendLine("tick=" + BaselineRuntime.GameTick());
        sb.AppendLine("isServer=" + BaselineRuntime.IsServer);
        sb.AppendLine("players=" + BaselineRuntime.PlayerCount());
        sb.AppendLine("outcome=" + Outcome);
        sb.AppendLine("command=" + lastCommand);
        sb.AppendLine("errors=" + Errors);
        sb.AppendLine("hookFailures=" + BaselineRuntime.HookFailures);
        sb.AppendLine("writeFailures=" + BaselineRuntime.WriteFailures);
        sb.AppendLine("tracked=" + BaselineRuntime.TrackedSnapshot().Length);
        sb.AppendLine("log=" + Path.Combine(BaselineRuntime.LogDirectory, Role + ".jsonl"));
        foreach (var target in BaselineRuntime.TrackedSnapshot())
        {
            var snapshot = BaselineWorld.Resolve(target);
            sb.AppendLine("target=" + target.Kind + "#" + target.NativeId +
                          " resolved=" + snapshot.Resolved +
                          " hasCombatStat=" + (snapshot.HasCombatStat ? 1 : 0) +
                          " hp=" + (snapshot.HasCombatStat ? snapshot.Hp + "/" + snapshot.HpMax : "n/a") +
                          " hpRecover=" + (snapshot.HasCombatStat ? snapshot.HpRecover : 0) +
                          " hasConstructStat=" + (snapshot.HasConstructStat ? 1 : 0) +
                          " repairerCount=" + (snapshot.HasConstructStat ? snapshot.RepairerCount : 0) +
                          " owner=" + snapshot.Owner +
                          " combatStat=" + snapshot.CombatStatId +
                          " constructStat=" + snapshot.ConstructStatId +
                          " reason=" + snapshot.UnresolvedReason);
        }
        File.WriteAllText(Path.Combine(ControlDirectory, Role + ".status"), sb.ToString());
    }
}
