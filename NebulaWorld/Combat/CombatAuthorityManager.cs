using System;
using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaModel.DataStructures;
using NebulaModel.Networking.Serialization;
using NebulaModel.Packets.Authority;
using NebulaWorld.Authority;
using UnityEngine;
using NebulaModel.Utils;
using System.Linq;

namespace NebulaWorld.Combat;

/// <summary>Host-owned native combat actors. Personal movement and inventory remain checkpoints.</summary>
public sealed class CombatAuthorityManager : IHostCommandExecutor, IHostTickAware, IDisposable
{
    [ThreadStatic] public static bool ReclaimingFleets;
    private sealed class Actor
    {
        public global::Player Player;
        public Mecha Mecha;
        public NebulaAPI.DataStructures.IMechaFightData DetachedFight;
        public PlayerData Data;
        public PlayerAction_Combat Action;
        public long LastCommand;
        public long Revision;
        public double CoreDebit;
        public long LastBombTick = -10000;
        public long LastBurstTick;
        public readonly CombatConsumptionLedger Debits = new();
        public readonly Dictionary<int, int> InventoryShadow = new();
        public readonly Dictionary<int, int> InventoryCounts = new();
        public readonly Queue<(ObjectKey Target, EAmmoType Ammo, long Tick)> AmmoIntents = new();
        public readonly Queue<(ObjectKey Target, EAmmoType Ammo, long Tick)> LaserIntents = new();
        public int CombatPlanet;
        public double FrameEnergy;
        public double FrameDebit;
        public readonly Dictionary<long, StorageComponent[]> StockHistory = new();
        public readonly Dictionary<long, long> StockPersonalRevisions = new();
    }
    private readonly Dictionary<ushort, Actor> actors = new();
    private sealed class Checkpoint
    {
        public PlayerData Data;
        public byte[] Snapshot;
        public ConnectionEpoch Connection;
        public long Command;
        public long CombatRevision;
        public double CoreDebit;
        public int[] Items;
        public int[] Totals;
        public Action<bool> Finish;
    }
    private readonly object checkpointGate = new();
    private readonly Dictionary<ushort, Checkpoint> checkpoints = new();
    private long checkpointBytes;
    private readonly Dictionary<ushort, long> received = new();
    private readonly Dictionary<ushort, ulong> receivedConnections = new();
    private long sequence;
    private AuthorityEpoch sendingEpoch;
    private ConnectionEpoch sendingConnection;
    private AuthorityEpoch receivingEpoch;
    private long lastSettingsTick = -1000;
    private long lastCompletedTick = -1;
    private AuthorityEpoch hostEpoch;
    private readonly Dictionary<int, int> acknowledgedDebits = new();
    private StorageComponent[] observedStocks;
    private readonly Dictionary<long, StorageComponent[]> publishedStockHistory = new();
    private long publishedPersonalRevision;
    private long observedPersonalRevision;
    private bool personalStocksDirty;
    public void MarkPersonalStockChange(StorageComponent storage)
    {
        if (Multiplayer.Session.IsServer || Multiplayer.Session.AuthorityRuntime.ApplyContext.IsActiveOnCurrentThread) return;
        var mecha = GameMain.mainPlayer?.mecha;
        if (storage == mecha?.ammoStorage || storage == mecha?.bombStorage || storage == mecha?.fighterStorage)
            personalStocksDirty = true;
    }
    public void RememberPersonalCheckpoint(long revision)
    {
        var mecha = GameMain.mainPlayer.mecha;
        publishedPersonalRevision = revision;
        publishedStockHistory[revision] = CopyStocks(mecha);
        foreach (var old in publishedStockHistory.Keys.OrderBy(x => x).Take(Math.Max(0, publishedStockHistory.Count - 128)).ToArray()) publishedStockHistory.Remove(old);
        personalStocksDirty = false;
    }
    public int[] DebitItemsAcknowledged => acknowledgedDebits.Keys.ToArray();
    public int[] DebitTotalsAcknowledged => acknowledgedDebits.Values.ToArray();
    public IHostWorldView TargetWorld { get; set; }
    public long HostTick { get; set; }
    public long LastAppliedRevision { get; private set; }
    public double CoreDebitAcknowledged { get; private set; }
    public long LastAppliedCommand { get; private set; }
    public bool Owns(global::Player player)
    {
        foreach (var actor in actors.Values) if (actor.Player == player) return true;
        return false;
    }
    public Mecha MechaFor(ushort playerId) => actors.TryGetValue(playerId, out var actor) && actor.DetachedFight == null ? actor.Player.mecha : null;

    public void PreserveDisconnectedActor(ushort playerId)
    {
        if (!Multiplayer.Session.IsServer || !actors.TryGetValue(playerId, out var actor) || actor.DetachedFight != null) return;
        // Native Player.Free releases every storage/module by reference. Server checkpoints
        // must own independent copies before the Unity presentation model is destroyed.
        actor.Data.Mecha.CoreEnergy = actor.Mecha.coreEnergy;
        actor.Data.Mecha.FightData = new MechaFightData(actor.Player);
        var writer = new NetDataWriter(); actor.Data.Mecha.Serialize(writer);
        var retained = new MechaData(); retained.Deserialize(new NetDataReader(writer.CopyData()));
        actor.Data.Mecha = retained; actor.DetachedFight = retained.FightData;
    }

    public bool QueueCheckpoint(PlayerData data, byte[] snapshot, long command, double debit, int[] items, int[] totals, Action<bool> finish, long combatRevision = 0)
    {
        if (snapshot == null || snapshot.Length > 4 * 1024 * 1024 || items == null || totals == null || items.Length != totals.Length || items.Length > 512) return false;
        lock (checkpointGate)
        {
            checkpoints.TryGetValue(data.PlayerId, out var previous);
            var bytes = checkpointBytes - (previous?.Snapshot.Length ?? 0) + snapshot.Length;
            if (bytes > 8 * 1024 * 1024 || previous == null && checkpoints.Count >= 128) return false;
            checkpoints[data.PlayerId] = new Checkpoint { Data = data, Snapshot = (byte[])snapshot.Clone(),
                Connection = Multiplayer.Session.AuthorityRuntime.ConnectionEpochFor(data.PlayerId), Command = command, CoreDebit = debit,
                Items = (int[])items.Clone(), Totals = (int[])totals.Clone(), Finish = finish, CombatRevision = combatRevision };
            checkpointBytes = bytes;
            return true;
        }
    }

    public bool ValidateCheckpoint(ushort id, long commandAck, double debitAck, int[] itemAcks, int[] debitAcks)
    {
        if (!actors.TryGetValue(id, out var actor)) return true;
        if (commandAck < 0 || commandAck > actor.LastCommand || debitAck < 0 || debitAck > actor.CoreDebit ||
            itemAcks == null || debitAcks == null || itemAcks.Length != debitAcks.Length || itemAcks.Length > 512) return false;
        var seen = new HashSet<int>();
        for (var i = 0; i < itemAcks.Length; i++)
        {
            actor.Debits.Totals.TryGetValue(itemAcks[i], out var total);
            if (itemAcks[i] <= 0 || !seen.Add(itemAcks[i]) || debitAcks[i] < 0 || debitAcks[i] > total) return false;
        }
        return true;
    }

    public void BeginFrame(long tick)
    {
        HostTick = tick;
        var session = Multiplayer.Session;
        if (!session.IsServer || !session.IsGameLoaded || !GameMain.data.gameDesc.isCombatMode) return;
        if (!hostEpoch.Equals(session.Authority.Epoch))
        { Dispose(); hostEpoch = session.Authority.Epoch; lastCompletedTick = -1; }
        List<Checkpoint> pending;
        lock (checkpointGate) { pending = checkpoints.Values.ToList(); checkpoints.Clear(); checkpointBytes = 0; }
        foreach (var checkpoint in pending)
        {
            var current = session.Server.Players.Get(checkpoint.Data.PlayerId);
            var valid = current != null && ReferenceEquals(current.Data, checkpoint.Data) &&
                session.AuthorityRuntime.ConnectionEpochFor(checkpoint.Data.PlayerId).Equals(checkpoint.Connection);
            try
            {
                var accepted = valid && PlayerLifeManager.StoreServer(checkpoint.Data, checkpoint.Snapshot, checkpoint.Command,
                    checkpoint.CoreDebit, checkpoint.Items, checkpoint.Totals, checkpoint.CombatRevision);
                checkpoint.Finish?.Invoke(accepted);
            }
            catch (Exception error) { NebulaModel.Logger.Log.Warn("[authority] personal checkpoint failed: " + error); }
        }
        var online = new HashSet<ushort>();
        using (session.World.GetRemotePlayersModels(out var models))
            foreach (var pair in session.Server.Players.Connected)
            {
                var data = (PlayerData)pair.Value.Data;
                if (!models.TryGetValue(data.PlayerId, out var model) || model.PlayerTransform == null || data.Mecha.ReactorStorage == null) continue;
                online.Add(data.PlayerId);
                if (actors.TryGetValue(data.PlayerId, out var actor) && actor.Player == model.PlayerInstance) continue;
                // Remote Player has no local inventory UI initialization. Install the approved
                // checkpoint directly rather than invoking the local-player restore adapter.
                model.PlayerInstance.SetHiddenProperty(nameof(global::Player.package), data.Mecha.Inventory);
                model.PlayerInstance.SetHiddenProperty(nameof(global::Player.deliveryPackage), data.Mecha.DeliveryPackage);
                model.MechaInstance.coreEnergy = data.Mecha.CoreEnergy;
                model.MechaInstance.reactorEnergy = data.Mecha.ReactorEnergy;
                model.MechaInstance.reactorStorage = data.Mecha.ReactorStorage;
                model.MechaInstance.warpStorage = data.Mecha.WarpStorage;
                data.Mecha.FightData.UpdateMech(model.PlayerInstance);
                // Personal checkpoints intentionally omit tech bonuses; derive combat limits from host research.
                new PlayerTechBonuses(GameMain.mainPlayer.mecha).UpdateMech(model.MechaInstance);
                model.PlayerInstance.isAlive = data.Life.IsAlive;
                model.PlayerInstance.deathCount = data.Life.DeathCount;
                model.PlayerInstance.invincibleTicks = data.Life.InvincibleTicks;
                var action = new PlayerAction_Combat(); action.Init(model.PlayerInstance);
                actors[data.PlayerId] = new Actor { Player = model.PlayerInstance, Mecha = model.MechaInstance, Data = data, Action = action, CombatPlanet = data.LocalPlanetId };
                RememberStocks(actors[data.PlayerId]);
                data.CombatAuthoritative = true;
                ResetInventoryShadow(actors[data.PlayerId]);
            }
        foreach (var id in new List<ushort>(actors.Keys))
            if (!online.Contains(id))
            {
                ReclaimFleets(actors[id]);
                actors[id].Data.CombatAuthoritative = false; actors.Remove(id);
            }
        foreach (var actor in actors.Values)
        {
            actor.FrameEnergy = actor.Player.mecha.coreEnergy;
            actor.FrameDebit = actor.CoreDebit;
            if (!actor.Player.isAlive) continue;
            Prepare(actor);
            var factory = actor.Player.factory;
            if (factory != null)
                for (var id = 1; id < factory.enemySystem.bases.cursor; id++)
                {
                    var item = factory.enemySystem.bases.buffer[id];
                    if (item?.id == id && item.enemyId > 0 && item.enemyId < factory.enemyCursor &&
                        factory.enemyPool[item.enemyId].id == item.enemyId &&
                        (factory.enemyPool[item.enemyId].pos - (VectorLF3)actor.Player.position).sqrMagnitude < 8100)
                        item.UnderAttack(actor.Player.position, 50f);
                }
            var star = actor.Action.localStar;
            if (star != null)
                for (var hive = GameMain.spaceSector.dfHives[star.index]; hive != null; hive = hive.nextSibling)
                    if (!hive.realized && !hive.isEmpty &&
                        (GameMain.spaceSector.astros[hive.hiveAstroId - 1000000].uPos - actor.Player.uPosition).sqrMagnitude < 400000000)
                        hive.Realize();
            actor.Action.ActivateNearbyEnemyHive();
        }
    }

    private static void Prepare(Actor actor)
    {
        if (!actor.Player.isAlive || actor.CombatPlanet != actor.Data.LocalPlanetId)
        { actor.AmmoIntents.Clear(); actor.LaserIntents.Clear(); actor.CombatPlanet = actor.Data.LocalPlanetId; }
        var planet = GameMain.galaxy.PlanetById(actor.Data.LocalPlanetId);
        var star = GameMain.galaxy.StarById(actor.Data.LocalStarId);
        actor.Player.SetHiddenProperty(nameof(global::Player.planetData), planet);
        actor.Player.position = new Vector3(actor.Data.LocalPlanetPosition.x, actor.Data.LocalPlanetPosition.y, actor.Data.LocalPlanetPosition.z);
        actor.Player.uPosition = new VectorLF3(actor.Data.UPosition.x, actor.Data.UPosition.y, actor.Data.UPosition.z);
        actor.Player.movementState = actor.Data.AcceptedMovementState;
        actor.Player.warpCommand = actor.Data.AcceptedWarping;
        actor.Player.uVelocity = new VectorLF3(actor.Data.AcceptedVelocityU.x, actor.Data.AcceptedVelocityU.y, actor.Data.AcceptedVelocityU.z);
        actor.Player.controller.velocity = new Vector3(actor.Data.AcceptedVelocityL.x, actor.Data.AcceptedVelocityL.y, actor.Data.AcceptedVelocityL.z);
        var rotation = Quaternion.Euler(actor.Data.Rotation.x, actor.Data.Rotation.y, actor.Data.Rotation.z);
        actor.Player.uRotation = planet == null ? rotation : planet.runtimeRotation * rotation;
        actor.Action.localPlanet = planet;
        actor.Action.localStar = star;
        actor.Action.localFactory = planet?.factory;
        actor.Action.localAstroId = planet?.astroId ?? 0;
        actor.Action.localPlayerPos = actor.Player.position;
        GameMain.history.GetCombatUpgradeData(ref actor.Action.combatUpgradeData);
    }

    private static void CountInventory(Actor actor)
    {
        actor.InventoryCounts.Clear();
        foreach (var grid in actor.Player.package.grids)
            if (grid.itemId > 0 && grid.count > 0)
            { actor.InventoryCounts.TryGetValue(grid.itemId, out var count); actor.InventoryCounts[grid.itemId] = count + grid.count; }
    }
    private static void ResetInventoryShadow(Actor actor)
    {
        CountInventory(actor); actor.InventoryShadow.Clear();
        foreach (var pair in actor.InventoryCounts) actor.InventoryShadow[pair.Key] = pair.Value;
    }
    private static void CaptureInventoryDebits(Actor actor)
    {
        CountInventory(actor);
        foreach (var pair in actor.InventoryShadow)
        {
            actor.InventoryCounts.TryGetValue(pair.Key, out var count);
            if (count < pair.Value) actor.Debits.Debit(pair.Key, pair.Value - count);
        }
        actor.InventoryShadow.Clear();
        foreach (var pair in actor.InventoryCounts) actor.InventoryShadow[pair.Key] = pair.Value;
    }

    public void SendSettings(long tick)
    {
        if (personalStocksDirty && Multiplayer.Session.IsGameLoaded) Multiplayer.Session.Life.Publish();
        if (tick - lastSettingsTick < 15 || !Multiplayer.Session.IsGameLoaded) return;
        lastSettingsTick = tick;
        var mecha = GameMain.mainPlayer.mecha;
        var flags = mecha.ammoSelectSlot & 3;
        if (mecha.laserActive) flags |= 4;
        if (mecha.groundCombatModule.moduleEnabled) flags |= 8;
        if (mecha.spaceCombatModule.moduleEnabled) flags |= 16;
        if (mecha.autoReplenishAmmo) flags |= 32;
        if (mecha.autoReplenishHangar) flags |= 64;
        if (mecha.autoReplenishFuel) flags |= 128;
        for (var i = 0; i < mecha.spaceCombatModule.fleetCount; i++) if (mecha.spaceCombatModule.moduleFleets[i].fleetEnabled) flags |= 1 << (8 + i);
        if (mecha.groundCombatModule.moduleFleets[0].fleetEnabled) flags |= 1 << 16;
        Send(new CombatIntent(CombatIntentKind.Settings, flags, mecha.coreEnergy, CoreDebitAcknowledged));
    }

    public CommandOutcome Execute(in QueuedHostCommand command)
    {
        if (command.Packet.Category != CombatIntent.Category || !CombatIntent.TryDecode(command.Packet.Payload, out var intent))
            return new CommandOutcome(CommandResultCode.RejectedInvalid);
        if (!actors.TryGetValue(command.ConnectionPlayerId, out var actor)) return new CommandOutcome(CommandResultCode.RejectedNotReady);
        actor.LastCommand = Math.Max(actor.LastCommand, command.Key.Sequence);
        if (!actor.Player.isAlive) return new CommandOutcome(CommandResultCode.RejectedNotReady);
        Prepare(actor);
        var mecha = actor.Player.mecha;
        var beforeEnergy = mecha.coreEnergy;
        bool applied;
        using (CombatManager.CombatAs(command.ConnectionPlayerId))
        {
            switch (intent.Kind)
            {
                case CombatIntentKind.Settings:
                    if (intent.X < 0 || intent.X > mecha.coreEnergyCap || intent.Y < 0 || intent.Y > actor.CoreDebit)
                        return new CommandOutcome(CommandResultCode.RejectedInvalid);
                    mecha.coreEnergy = Math.Max(0, intent.X - (actor.CoreDebit - intent.Y));
                    actor.FrameEnergy += mecha.coreEnergy - beforeEnergy;
                    mecha.ammoSelectSlot = intent.Argument & 3;
                    mecha.laserActive = (intent.Argument & 4) != 0;
                    mecha.groundCombatModule.moduleEnabled = (intent.Argument & 8) != 0;
                    mecha.spaceCombatModule.moduleEnabled = (intent.Argument & 16) != 0;
                    mecha.autoReplenishAmmo = (intent.Argument & 32) != 0;
                    mecha.autoReplenishHangar = (intent.Argument & 64) != 0;
                    mecha.autoReplenishFuel = (intent.Argument & 128) != 0;
                    for (var i = 0; i < mecha.spaceCombatModule.fleetCount; i++) mecha.spaceCombatModule.moduleFleets[i].fleetEnabled = (intent.Argument & (1 << (8 + i))) != 0;
                    mecha.groundCombatModule.moduleFleets[0].fleetEnabled = (intent.Argument & (1 << 16)) != 0;
                    if (mecha.ammoSelectSlot == 0) actor.AmmoIntents.Clear();
                    if (!mecha.laserActive) actor.LaserIntents.Clear();
                    return new CommandOutcome(CommandResultCode.Applied, HostTick);
                case CombatIntentKind.Shoot:
                case CombatIntentKind.Wake:
                    if (actor.Player.warping) return new CommandOutcome(CommandResultCode.RejectedNotReady);
                    if (!command.Packet.TryGetTargetKey(out var key) || TargetWorld == null ||
                        !TargetWorld.TryReadState(key, out _) || (key.Kind != PoolKind.GroundEnemy && key.Kind != PoolKind.SpaceEnemy))
                        return new CommandOutcome(CommandResultCode.RejectedTarget);
                    var target = new SkillTarget { type = ETargetType.Enemy, id = key.NativeId,
                        astroId = key.Kind == PoolKind.GroundEnemy ? key.Scope : GameMain.spaceSector.enemyPool[key.NativeId].originAstroId };
                    if (key.Kind == PoolKind.GroundEnemy && key.Scope != actor.Data.LocalPlanetId)
                        return new CommandOutcome(CommandResultCode.RejectedTarget);
                    if (key.Kind == PoolKind.SpaceEnemy && !actor.Player.sailing) return new CommandOutcome(CommandResultCode.RejectedTarget);
                    var ammo = (EAmmoType)intent.Argument;
                    if (ammo <= EAmmoType.None || ammo > EAmmoType.Laser) return new CommandOutcome(CommandResultCode.RejectedInvalid);
                    GameMain.spaceSector.skillSystem.GetObjectUPositionAndVelocity(ref target, out var position, out _);
                    var range = key.Kind == PoolKind.GroundEnemy ? actor.Action.GetAmmoLocalAttackRange(ammo) : actor.Action.GetAmmoSpaceAttackRange(ammo);
                    if ((position - mecha.skillTargetUCenter).sqrMagnitude > range * range * 1.05)
                        return new CommandOutcome(CommandResultCode.RejectedTarget);
                    if (intent.Kind == CombatIntentKind.Wake)
                    {
                        if (key.Kind == PoolKind.SpaceEnemy) GameMain.spaceSector.GetHiveByAstroId(target.astroId)?.Realize();
                        return new CommandOutcome(CommandResultCode.Applied, HostTick);
                    }
                    var inputs = ammo == EAmmoType.Laser ? actor.LaserIntents : actor.AmmoIntents;
                    if (inputs.Count >= 64) return new CommandOutcome(CommandResultCode.RejectedNotReady);
                    inputs.Enqueue((key, ammo, HostTick));
                    applied = true;
                    break;
                case CombatIntentKind.Bomb:
                    if (actor.Player.warping) return new CommandOutcome(CommandResultCode.RejectedNotReady);
                    if (HostTick - actor.LastBombTick < 10 || mecha.bombStorage.grids[0].count <= 0)
                        return new CommandOutcome(CommandResultCode.RejectedResource);
                    var velocity = new VectorLF3(intent.X, intent.Y, intent.Z);
                    var velocityLimit = Math.Max(2000, mecha.maxSailSpeed + 1000);
                    if (velocity.sqrMagnitude > velocityLimit * velocityLimit) return new CommandOutcome(CommandResultCode.RejectedInvalid);
                    var cast = mecha.skillBombingUCenter;
                    actor.Action.Bombing(ref velocity, ref cast, HostTick);
                    actor.LastBombTick = HostTick;
                    applied = true;
                    break;
                case CombatIntentKind.ShieldBurst:
                    // Progress is an input hint bounded by elapsed host time and the actual shield.
                    mecha.energyShieldBurstProgress = Math.Min(mecha.energyShieldPercentage,
                        Math.Min(Math.Max(0, Math.Min(1, intent.X)), Math.Max(0, HostTick - actor.LastBurstTick) / 75.0));
                    if (!mecha.energyShieldBurstUnlocked || !mecha.energyShieldBurstReady) return new CommandOutcome(CommandResultCode.RejectedResource);
                    actor.Action.ShieldBurst(); mecha.ResetShieldBurstProgress(); actor.LastBurstTick = HostTick; applied = true;
                    break;
                case CombatIntentKind.FleetLaunch:
                case CombatIntentKind.FleetRecall:
                case CombatIntentKind.FleetConfigure:
                    var module = intent.Argument >= 100 ? mecha.spaceCombatModule : mecha.groundCombatModule;
                    var fleet = intent.Argument % 100;
                    if (intent.Argument < 0 || intent.Argument >= 200 || fleet < 0 || fleet >= module.fleetCount)
                        return new CommandOutcome(CommandResultCode.RejectedInvalid);
                    if (intent.Kind == CombatIntentKind.FleetConfigure)
                    {
                        var configuration = (int)intent.X;
                        if (configuration != intent.X || (module.isSpace
                            ? !FleetProto.kMechaFleetSpaceIds.Contains(configuration)
                            : LDB.items.Select(configuration)?.isGroundFighter != true))
                            return new CommandOutcome(CommandResultCode.RejectedInvalid);
                        if (module.moduleFleets[fleet].fleetId > 0) module.RecycleFleet(fleet);
                        if (module.moduleFleets[fleet].fleetId > 0) return new CommandOutcome(CommandResultCode.RejectedNotReady);
                        module.ChangeFleetConfig(fleet, configuration, mecha.fighterStorage, actor.Player);
                    }
                    else if (intent.Kind == CombatIntentKind.FleetRecall) module.RecycleFleet(fleet);
                    else module.LaunchFleet(fleet, intent.Argument >= 100 ? null : actor.Player.factory, actor.Player);
                    applied = true;
                    break;
                default: return new CommandOutcome(CommandResultCode.RejectedInvalid);
            }
        }
        actor.CoreDebit += Math.Max(0, beforeEnergy - mecha.coreEnergy);
        CaptureInventoryDebits(actor);
        return new CommandOutcome(applied ? CommandResultCode.Applied : CommandResultCode.RejectedTarget, HostTick);
    }

    private SkillTarget FirePending(Actor actor, bool laser)
    {
        var queue = laser ? actor.LaserIntents : actor.AmmoIntents;
        while (queue.Count > 0)
        {
            var input = queue.Dequeue(); var key = input.Target;
            if (HostTick - input.Tick > 120) continue;
            if (TargetWorld == null || !TargetWorld.TryReadState(key, out _) ||
                key.Kind == PoolKind.GroundEnemy && key.Scope != actor.Data.LocalPlanetId) continue;
            var target = new SkillTarget { type = ETargetType.Enemy, id = key.NativeId,
                astroId = key.Kind == PoolKind.GroundEnemy ? key.Scope : GameMain.spaceSector.enemyPool[key.NativeId].originAstroId };
            GameMain.spaceSector.skillSystem.GetObjectUPositionAndVelocity(ref target, out var position, out _);
            var range = key.Kind == PoolKind.GroundEnemy ? actor.Action.GetAmmoLocalAttackRange(input.Ammo) : actor.Action.GetAmmoSpaceAttackRange(input.Ammo);
            if ((position - actor.Player.mecha.skillTargetUCenter).sqrMagnitude > range * range * 1.05) continue;
            var mecha = actor.Player.mecha;
            if (!laser)
            {
                if (mecha.ammoBulletCount <= 0) mecha.LoadAmmo();
                if (input.Ammo != mecha.activeAmmoType || mecha.ammoBulletCount <= 0) return SkillTarget.none;
            }
            if (!actor.Action.ShootTarget(input.Ammo, target)) continue;
            if (!laser) mecha.ammoBulletCount--;
            return target;
        }
        return SkillTarget.none;
    }

    public void CompleteFrame(long tick)
    {
        if (!Multiplayer.Session.IsServer || !Multiplayer.Session.IsGameLoaded) return;
        if (lastCompletedTick == tick) return;
        lastCompletedTick = tick;
        foreach (var pair in actors)
        {
            var actor = pair.Value; var player = actor.Player; var mecha = player.mecha;
            // Disconnect removes the Unity model before the next completed frame is drained.
            // Let BeginFrame reclaim the actor instead of blocking that cleanup behind a dead transform.
            if (player.transform == null) continue;
            Prepare(actor);
            if (player.isAlive)
            {
                if (player.invincibleTicks > 0) player.invincibleTicks--;
                mecha.UpdateCombatStats(1.0 / 60);
                if (mecha.ammoBulletCount <= 0 && mecha.ammoSelectSlot > 0) mecha.LoadAmmo();
                var upgrade = default(CombatUpgradeData); GameMain.history.GetCombatUpgradeData(ref upgrade);
                using (CombatManager.CombatAs(pair.Key))
                {
                    if (mecha.ammoSelectSlot > 0) mecha.TickAmmoFireCondition(() => FirePending(actor, false));
                    if (mecha.laserActive) mecha.TickLaserFireCondition(() => FirePending(actor, true));
                    if (player.factory != null) mecha.groundCombatModule.GameTick(tick, player.factory, player, ref upgrade);
                    mecha.spaceCombatModule.GameTick(tick, player.factory, player, ref upgrade);
                }
            }
            actor.CoreDebit = actor.FrameDebit + Math.Max(actor.CoreDebit - actor.FrameDebit, Math.Max(0, actor.FrameEnergy - mecha.coreEnergy));
            CaptureInventoryDebits(actor);
            actor.Data.Mecha.FightData = new MechaFightData(player);
            actor.Data.Mecha.CoreEnergy = mecha.coreEnergy;
            actor.Data.Life.IsAlive = player.isAlive; actor.Data.Life.DeathCount = player.deathCount;
            actor.Data.Life.InvincibleTicks = player.invincibleTicks;
            if (tick % 6 != 0) continue;
            var writer = new NetDataWriter(); actor.Data.Mecha.FightData.Serialize(writer);
            var payload = writer.CopyData();
            if (payload.Length > 65536) throw new InvalidOperationException("Player combat payload exceeds its bound.");
            var packet = new AuthorityPlayerCombatStatePacket { PlayerId = pair.Key,
                LastCommandSequence = actor.LastCommand, CombatRevision = ++actor.Revision, CoreEnergyDebitTotal = actor.CoreDebit,
                PersonalRevision = actor.Data.Life.Revision,
                ActorConnection = Multiplayer.Session.AuthorityRuntime.ConnectionEpochFor(pair.Key).Value,
                IsAlive = player.isAlive, DeathCount = player.deathCount, InvincibleTicks = player.invincibleTicks,
                HpRecoverCD = mecha.hpRecoverCD, ShieldRecoverCD = mecha.energyShieldRecoverCD, CombatData = payload };
            RememberStocks(actor);
            packet.DebitItems = actor.Debits.Totals.Keys.ToArray();
            packet.DebitTotals = actor.Debits.Totals.Values.ToArray();
            packet.SetHeader(new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.PlayerCombatState,
                Multiplayer.Session.Authority.Epoch, default, actor.Revision, tick, 0, packet.DeclaredPayloadLength));
            foreach (var recipient in Multiplayer.Session.Server.Players.Connected.Values)
                if (recipient.Id == pair.Key || recipient.Data.LocalStarId == actor.Data.LocalStarId)
                {
                    recipient.SendPacket(packet);
                    Multiplayer.Session.AuthorityRuntime.Metrics.RecordPacketSent(packet.Family, AuthorityWireSize.Of(packet));
                }
        }
    }

    public bool Send(CombatIntent intent, SkillTarget target = default)
    {
        var session = Multiplayer.Session;
        if (session.IsServer || !session.Authority.IsActive) return false;
        if (!sendingEpoch.Equals(session.Authority.Epoch) || !sendingConnection.Equals(session.Authority.Connection))
        { sendingEpoch = session.Authority.Epoch; sendingConnection = session.Authority.Connection; sequence = 0; }
        var key = default(ObjectKey);
        if (target.id > 0)
        {
            var ground = target.astroId > 0 && target.astroId <= 1000000;
            if (!session.AuthorityRuntime.WorldReplica.TryFindKey(ground ? PoolKind.GroundEnemy : PoolKind.SpaceEnemy,
                    ground ? target.astroId : 0, target.id, out key)) return false;
        }
        var payload = intent.Encode();
        var header = new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.Command,
            sendingEpoch, sendingConnection, ++sequence, GameMain.gameTick, session.LocalPlayer.Id, payload.Length);
        session.Network.SendPacket(AuthorityCommandPacket.Create(header, key, CombatIntent.Category, payload));
        return true;
    }

    public bool ApplyState(AuthorityPlayerCombatStatePacket packet)
    {
        var epoch = new AuthorityEpoch(packet.EpochHigh, packet.EpochLow);
        if (!receivingEpoch.Equals(epoch))
        { receivingEpoch = epoch; received.Clear(); receivedConnections.Clear(); acknowledgedDebits.Clear(); CoreDebitAcknowledged = 0; LastAppliedRevision = 0;
            observedStocks = null; publishedStockHistory.Clear(); publishedPersonalRevision = 0; observedPersonalRevision = 0; personalStocksDirty = false; }
        if (receivedConnections.TryGetValue(packet.PlayerId, out var connection) && packet.ActorConnection < connection) return true;
        if (connection != packet.ActorConnection) { receivedConnections[packet.PlayerId] = packet.ActorConnection; received.Remove(packet.PlayerId); }
        if (received.TryGetValue(packet.PlayerId, out var previous) && packet.CombatRevision <= previous) return true;
        var fight = new MechaFightData(); fight.Deserialize(new NetDataReader(packet.CombatData));
        using (Multiplayer.Session.World.GetRemotePlayersModels(out var models))
        {
            var own = packet.PlayerId == Multiplayer.Session.LocalPlayer.Id;
            var player = own ? GameMain.mainPlayer : models.TryGetValue(packet.PlayerId, out var model) ? model.PlayerInstance : null;
            if (player == null) return false;
            var mecha = player.mecha;
            // A packet produced before the paid respawn completed cannot replay the same death.
            var pendingRespawn = own && player.isAlive && !packet.IsAlive && packet.DeathCount <= player.deathCount;
            var respawnHp = mecha.hp; var respawnShield = mecha.energyShieldEnergy;
            var muzzle = mecha.ammoMuzzleFire; var round = mecha.ammoRoundFire; var muzzleIndex = mecha.ammoMuzzleIndex;
            var laserFire = mecha.laserFire; var bombFire = mecha.bombFire;
            var personalStocks = own ? CopyStocks(mecha) : null;
            var stockBaseline = own && packet.PersonalRevision > observedPersonalRevision && publishedStockHistory.TryGetValue(packet.PersonalRevision, out var confirmedStock)
                ? confirmedStock : observedStocks;
            var serverStocks = own ? new[] { CloneStock(fight.AmmoStorage), CloneStock(fight.BombStorage), CloneStock(fight.FighterStorage) } : null;
            if (own)
            {
                fight.AmmoSelectSlot = mecha.ammoSelectSlot; fight.LaserActive = mecha.laserActive;
                fight.AutoReplenishFuel = mecha.autoReplenishFuel; fight.AutoReplenishAmmo = mecha.autoReplenishAmmo;
                fight.AutoReplenishHangar = mecha.autoReplenishHangar;
                fight.GroundCombatModule.moduleEnabled = mecha.groundCombatModule.moduleEnabled;
                fight.SpaceCombatModule.moduleEnabled = mecha.spaceCombatModule.moduleEnabled;
                CopyFleetSettings(fight.GroundCombatModule, mecha.groundCombatModule);
                CopyFleetSettings(fight.SpaceCombatModule, mecha.spaceCombatModule);
            }
            fight.UpdateMech(player);
            if (pendingRespawn) { mecha.hp = respawnHp; mecha.energyShieldEnergy = respawnShield; }
            mecha.hpRecoverCD = packet.HpRecoverCD; mecha.energyShieldRecoverCD = packet.ShieldRecoverCD;
            if (own)
            {
                if (stockBaseline != null && (personalStocksDirty || packet.PersonalRevision < publishedPersonalRevision))
                {
                    mecha.ammoStorage = MergeStock(serverStocks[0], stockBaseline[0], personalStocks[0]);
                    mecha.bombStorage = MergeStock(serverStocks[1], stockBaseline[1], personalStocks[1]);
                    mecha.fighterStorage = MergeStock(serverStocks[2], stockBaseline[2], personalStocks[2]);
                }
                observedStocks = serverStocks;
                if (stockBaseline != null)
                    foreach (var pending in publishedStockHistory.Keys.Where(x => x > packet.PersonalRevision).ToArray())
                        for (var i = 0; i < 3; i++) publishedStockHistory[pending][i] = MergeStock(serverStocks[i], stockBaseline[i], publishedStockHistory[pending][i]);
                foreach (var acknowledged in publishedStockHistory.Keys.Where(x => x <= packet.PersonalRevision).ToArray()) publishedStockHistory.Remove(acknowledged);
                observedPersonalRevision = packet.PersonalRevision;
                mecha.ammoMuzzleFire = muzzle; mecha.ammoRoundFire = round; mecha.ammoMuzzleIndex = muzzleIndex;
                mecha.laserFire = laserFire; mecha.bombFire = bombFire;
                for (var i = 0; i < packet.DebitItems.Length; i++)
                {
                    var item = packet.DebitItems[i]; var total = packet.DebitTotals[i];
                    acknowledgedDebits.TryGetValue(item, out var oldTotal);
                    if (!CombatConsumptionLedger.TryDelta(oldTotal, total, out var delta)) return false;
                    var paid = 0;
                    if (delta > 0) { paid = delta; player.package.TakeTailItems(ref item, ref paid, out _); }
                    // An item moved out of the package is still owed. Retry the unpaid balance
                    // on later facts; acknowledging it would allow a stale checkpoint to refund it.
                    acknowledgedDebits[packet.DebitItems[i]] = oldTotal + paid;
                }
                mecha.coreEnergy = Math.Max(0, mecha.coreEnergy - Math.Max(0, packet.CoreEnergyDebitTotal - CoreDebitAcknowledged));
                CoreDebitAcknowledged = packet.CoreEnergyDebitTotal; LastAppliedRevision = packet.CombatRevision;
                LastAppliedCommand = packet.LastCommandSequence;
                if (!pendingRespawn && !packet.IsAlive && player.isAlive) player.Kill();
            }
            else player.isAlive = packet.IsAlive;
            player.deathCount = packet.DeathCount; player.invincibleTicks = packet.InvincibleTicks;
        }
        received[packet.PlayerId] = packet.CombatRevision;
        return true;
    }

    public void AcceptCheckpoint(ushort playerId, PlayerData data, PlayerData snapshot, long commandAck, double debitAck,
        int[] itemAcks, int[] debitAcks, bool requestedRespawn, NebulaAPI.DataStructures.IMechaFightData personalFight, long combatRevision = 0)
    {
        if (!actors.TryGetValue(playerId, out var actor)) return;
        if (debitAck > actor.CoreDebit || itemAcks == null || debitAcks == null || itemAcks.Length != debitAcks.Length || itemAcks.Length > 512)
            throw new InvalidOperationException("Invalid combat checkpoint acknowledgement.");
        var acknowledgements = new Dictionary<int, int>();
        for (var i = 0; i < itemAcks.Length; i++) acknowledgements[itemAcks[i]] = debitAcks[i];
        foreach (var debit in actor.Debits.Totals)
        {
            acknowledgements.TryGetValue(debit.Key, out var ack);
            var count = actor.Debits.Pending(debit.Key, ack); var item = debit.Key;
            if (count > 0) snapshot.Mecha.Inventory.TakeTailItems(ref item, ref count, out _);
        }
        actor.Player.SetHiddenProperty(nameof(global::Player.package), snapshot.Mecha.Inventory);
        ResetInventoryShadow(actor);
        actor.Player.mecha.coreEnergy = Math.Max(0, snapshot.Mecha.CoreEnergy - Math.Max(0, actor.CoreDebit - debitAck));
        data.Mecha.CoreEnergy = actor.Player.mecha.coreEnergy;
        if (actor.StockHistory.TryGetValue(combatRevision, out var baseline))
        {
            var source = personalFight;
            // Apply only personal transfers since the combat facts the sender observed.
            // Copying an entire old storage would refund ammunition consumed since those facts.
                actor.Player.mecha.ammoStorage = MergeStock(actor.Player.mecha.ammoStorage, baseline[0], source.AmmoStorage);
                actor.Player.mecha.bombStorage = MergeStock(actor.Player.mecha.bombStorage, baseline[1], source.BombStorage);
                actor.Player.mecha.fighterStorage = MergeStock(actor.Player.mecha.fighterStorage, baseline[2], source.FighterStorage);
        }
        if (commandAck >= actor.LastCommand)
        {
            var source = personalFight;
            actor.Player.mecha.ammoSelectSlot = source.AmmoSelectSlot;
            actor.Player.mecha.laserActive = source.LaserActive;
            actor.Player.mecha.autoReplenishAmmo = source.AutoReplenishAmmo;
            actor.Player.mecha.autoReplenishHangar = source.AutoReplenishHangar;
            CopyFleetSettings(actor.Player.mecha.groundCombatModule, source.GroundCombatModule);
            CopyFleetSettings(actor.Player.mecha.spaceCombatModule, source.SpaceCombatModule);
        }
        if (!actor.Player.isAlive && requestedRespawn &&
            Multiplayer.Session.PropertyTransactions.OwnsCommittedRespawn(snapshot.Life.TransactionId, data.PersistentId, actor.Player.deathCount))
        {
            actor.Player.mecha.Respawn(); actor.Player.isAlive = true; actor.Player.invincibleTicks = 180;
            data.Life.IsAlive = true;
        }
        actor.Data = data;
    }

    public bool ValidatePersonalStocks(ushort playerId, long revision, NebulaAPI.DataStructures.IMechaFightData incoming)
    {
        if (!actors.TryGetValue(playerId, out var actor)) return true;
        if (incoming == null || !actor.StockHistory.TryGetValue(revision, out var baseline) ||
            !actor.StockPersonalRevisions.TryGetValue(revision, out var personalRevision) || personalRevision < actor.Data.Life.Revision) return false;
        try
        {
            MergeStock(actor.Player.mecha.ammoStorage, baseline[0], incoming.AmmoStorage);
            MergeStock(actor.Player.mecha.bombStorage, baseline[1], incoming.BombStorage);
            MergeStock(actor.Player.mecha.fighterStorage, baseline[2], incoming.FighterStorage);
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    public Mecha CraftMecha(bool space, int scope, int craftId)
    {
        var pool = space ? GameMain.spaceSector?.craftPool : GameMain.galaxy?.PlanetById(scope)?.factory?.craftPool;
        if (pool == null || craftId <= 0 || craftId >= pool.Length || pool[craftId].id != craftId) return null;
        var craft = pool[craftId];
        if (craft.owner > 0)
        {
            if (craft.owner >= pool.Length || pool[craft.owner].id != craft.owner) return null;
            craft = pool[craft.owner];
        }
        if (craft.owner != -1 || craft.fleetId <= 0) return null;
        foreach (var actor in actors.Values)
        {
            var module = actor.DetachedFight == null
                ? (space ? actor.Mecha.spaceCombatModule : actor.Mecha.groundCombatModule)
                : (space ? actor.DetachedFight.SpaceCombatModule : actor.DetachedFight.GroundCombatModule);
            if (craft.port < 0 || craft.port >= module.fleetCount) continue;
            var fleet = module.moduleFleets[craft.port];
            if (fleet.fleetId == craft.fleetId && (space || fleet.fleetAstroId == scope)) return actor.Mecha;
        }
        return null;
    }

    public static StarData CasterStar() => Multiplayer.IsActive && Multiplayer.Session.IsServer &&
        Multiplayer.Session.CombatAuthority.actors.TryGetValue((ushort)CombatManager.PlayerId, out var actor)
        ? GameMain.galaxy.StarById(actor.Data.LocalStarId) : GameMain.localStar;

    private void ReclaimFleets(Actor actor)
    {
        var previous = ReclaimingFleets; ReclaimingFleets = true;
        try
        {
            var fight = actor.DetachedFight ?? new MechaFightData(actor.Player);
            foreach (var module in new[] { fight.GroundCombatModule, fight.SpaceCombatModule })
                for (var i = 0; i < module.fleetCount; i++)
                {
                    ref var fleet = ref module.moduleFleets[i];
                    var factory = module.isSpace ? null : GameMain.galaxy.PlanetById(fleet.fleetAstroId)?.factory;
                    var pool = module.isSpace ? GameMain.spaceSector.craftPool : factory?.craftPool;
                    var fleets = module.isSpace ? GameMain.spaceSector.combatSpaceSystem.fleets : factory?.combatGroundSystem.fleets;
                    if (pool != null && fleet.fleetId > 0 && fleet.fleetId < fleets.cursor && fleets.buffer[fleet.fleetId].id == fleet.fleetId)
                    {
                        var root = fleets.buffer[fleet.fleetId].craftId;
                        if (CraftMecha(module.isSpace, factory?.planetId ?? 0, root) == actor.Mecha)
                        {
                            var owned = pool.Where(x => x.id > 0 && x.owner == root).Select(x => x.id).ToArray();
                            foreach (var craft in owned)
                                if (module.isSpace) GameMain.spaceSector.RemoveCraftWithComponents(craft); else factory.RemoveCraftWithComponents(craft);
                            if (module.isSpace) GameMain.spaceSector.RemoveCraftWithComponents(root); else factory.RemoveCraftWithComponents(root);
                        }
                    }
                    fleet.fleetId = 0; fleet.fleetAstroId = 0; fleet.inCommand = false;
                    for (var port = 0; port < fleet.fighters.Length; port++) fleet.fighters[port].craftId = 0;
                }
            actor.Data.Mecha.FightData = fight;
        }
        finally { ReclaimingFleets = previous; }
    }

    private static void CopyFleetSettings(CombatModuleComponent target, CombatModuleComponent source)
    {
        for (var i = 0; i < Math.Min(target.fleetCount, source.fleetCount); i++)
            target.moduleFleets[i].fleetEnabled = source.moduleFleets[i].fleetEnabled;
    }

    private static StorageComponent CloneStock(StorageComponent source)
    {
        var result = new StorageComponent(source.size) { type = source.type, bans = source.bans };
        Array.Copy(source.grids, result.grids, source.grids.Length);
        return result;
    }

    private static void RememberStocks(Actor actor)
    {
        actor.StockHistory[actor.Revision] = CopyStocks(actor.Player.mecha);
        actor.StockPersonalRevisions[actor.Revision] = actor.Data.Life.Revision;
        foreach (var revision in actor.StockHistory.Keys.Where(x => x < actor.Revision - 127).ToArray())
        { actor.StockHistory.Remove(revision); actor.StockPersonalRevisions.Remove(revision); }
    }
    private static StorageComponent[] CopyStocks(Mecha mecha) => new[] { CloneStock(mecha.ammoStorage), CloneStock(mecha.bombStorage), CloneStock(mecha.fighterStorage) };

    internal static StorageComponent MergeStock(StorageComponent current, StorageComponent baseline, StorageComponent incoming)
    {
        var result = CloneStock(current);
        var items = baseline.grids.Concat(incoming.grids).Where(x => x.itemId > 0).Select(x => x.itemId).Distinct();
        foreach (var itemId in items)
        {
            var delta = incoming.grids.Where(x => x.itemId == itemId).Sum(x => x.count) - baseline.grids.Where(x => x.itemId == itemId).Sum(x => x.count);
            if (delta < 0)
            {
                var item = itemId; var count = -delta; result.TakeTailItems(ref item, ref count, out _);
                if (count != -delta) throw new InvalidOperationException("Combat stock transfer exceeds the available stock.");
            }
            else if (delta > 0)
            {
                var inc = Math.Max(0, incoming.grids.Where(x => x.itemId == itemId).Sum(x => x.inc) - baseline.grids.Where(x => x.itemId == itemId).Sum(x => x.inc));
                if (result.AddItemStacked(itemId, delta, inc, out _) != delta)
                    throw new InvalidOperationException("Combat stock transfer exceeds storage capacity.");
            }
        }
        return result;
    }

    public void Dispose() { foreach (var actor in actors.Values) actor.Data.CombatAuthoritative = false; actors.Clear(); received.Clear(); }
}
