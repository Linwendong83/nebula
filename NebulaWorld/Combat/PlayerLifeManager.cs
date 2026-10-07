using System;
using System.Collections.Generic;
using NebulaModel.DataStructures;
using NebulaModel.Logger;
using NebulaModel.Networking.Serialization;
using NebulaModel.Packets.Combat.Mecha;
using NebulaWorld.GameStates;

namespace NebulaWorld.Combat;

public sealed class PlayerLifeManager : IDisposable
{
    private PlayerLifePacket pending;
    private long lastSend;
    private long lastCheckpoint;
    private int lastStage = -1;
    private readonly Dictionary<ushort, long> received = new();
    private readonly HashSet<ushort> refusedSnapshotPlayers = new();

    public bool RecordSnapshotRefusal(ushort playerId) => refusedSnapshotPlayers.Add(playerId);

    public void Publish()
    {
        if (!Multiplayer.Session.IsGameLoaded || Multiplayer.Session.IsDedicated) return;
        var bytes = MetadataTransactionManager.CapturePlayer();
        var data = (PlayerData)Multiplayer.Session.LocalPlayer.Data;
        Multiplayer.Session.CombatAuthority.RememberPersonalCheckpoint(data.Life.Revision);
        lastCheckpoint = DateTime.UtcNow.Ticks;
        pending = new PlayerLifePacket
        { PlayerId = data.PlayerId, Life = data.Life, PlayerSnapshot = bytes,
            CombatRevision = Multiplayer.Session.CombatAuthority.LastAppliedRevision,
            CoreEnergyDebitAcknowledged = Multiplayer.Session.CombatAuthority.CoreDebitAcknowledged,
            LastCombatCommand = Multiplayer.Session.CombatAuthority.LastAppliedCommand,
            DebitItemsAcknowledged = Multiplayer.Session.CombatAuthority.DebitItemsAcknowledged,
            DebitTotalsAcknowledged = Multiplayer.Session.CombatAuthority.DebitTotalsAcknowledged };
        lastStage = data.Life.RespawnStage;
        Send();
    }

    private void Send()
    {
        if (pending == null) return;
        lastSend = DateTime.UtcNow.Ticks;
        if (Multiplayer.Session.IsServer)
        {
            StoreServer((PlayerData)Multiplayer.Session.LocalPlayer.Data, pending.PlayerSnapshot);
            pending.PlayerSnapshot = Array.Empty<byte>();
            Multiplayer.Session.Server.SendPacket(pending);
            pending = null;
        }
        else Multiplayer.Session.Network.SendPacket(pending);
    }

    public void Acknowledge(long revision)
    {
        if (pending?.Life.Revision <= revision) pending = null;
    }

    public void GameTick()
    {
        if (!Multiplayer.Session.IsGameLoaded || Multiplayer.Session.IsDedicated) return;
        var action = GameMain.mainPlayer.controller.actionDeath;
        if (!GameMain.mainPlayer.isAlive && action.respawning && action.respawnStage != lastStage) Publish();
        else if (pending != null && DateTime.UtcNow.Ticks - lastSend > TimeSpan.TicksPerSecond * 2) Send();
        else if (DateTime.UtcNow.Ticks - lastCheckpoint >= TimeSpan.TicksPerSecond * 5) Publish();
    }

    public static bool StoreServer(PlayerData player, byte[] snapshot, long commandAck = 0, double debitAck = 0,
        int[] itemAcks = null, int[] debitAcks = null, long combatRevision = 0)
    {
        // Only the processor for an authenticated connection may call this. Personal inventory,
        // energy and life still have a local game adapter; the authority sidecar stores none of
        // them. Persist their latest checkpoint in the same .server save as the world.
        var checkpoint = TryReadSnapshot(snapshot);
        itemAcks ??= Array.Empty<int>(); debitAcks ??= Array.Empty<int>();
        if (Multiplayer.Session?.IsServer == true &&
            !Multiplayer.Session.CombatAuthority.ValidateCheckpoint(player.PlayerId, commandAck, debitAck, itemAcks, debitAcks)) return false;
        var requestedRespawn = checkpoint?.Life?.IsAlive == true;
        var personalFight = checkpoint?.Mecha?.FightData;
        if (player.CombatAuthoritative && !Multiplayer.Session.CombatAuthority.ValidatePersonalStocks(player.PlayerId, combatRevision, personalFight)) return false;
        if (!player.TryApplyPersonalSnapshot(checkpoint)) return false;
        if (Multiplayer.Session?.IsServer == true && player.CombatAuthoritative)
            Multiplayer.Session.CombatAuthority.AcceptCheckpoint(player.PlayerId, player, checkpoint, commandAck, debitAck,
                itemAcks, debitAcks, requestedRespawn, personalFight, combatRevision);
        return true;
    }

    public static void RestoreServer(PlayerData player)
    {
        // The loaded .server file is the recovery point. Older per-player .bin files belong
        // to a different world save moment and must not replace its inventory or life.
    }

    /// <summary>
    ///     Snapshots on disk can come from older builds with a shorter PlayerData layout. A parse
    ///     failure must degrade to "no snapshot" — it used to escape into the join handshake and
    ///     leave the connecting player without any response at all.
    /// </summary>
    private static PlayerData TryReadSnapshot(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > 4 * 1024 * 1024) return null;
        try
        {
            var snapshot = new PlayerData();
            snapshot.Deserialize(new NetDataReader(bytes));
            return snapshot;
        }
        catch (Exception e)
        {
            Log.Warn($"Ignoring unreadable player snapshot: {e.Message}");
            return null;
        }
    }

    public void RestoreLocal(PlayerData data)
    {
        // A cached local death is not evidence that the player is still dead. The server's
        // selected save already supplied the matching inventory and latest life state.
        var life = data.Life;
        PlayerLifeData.NextRevision(life.Revision);
        PlayerLifeData.CurrentTransactionId = life.TransactionId;
        PlayerLifeData.CurrentRedeployItemsDropped = life.RedeployItemsDropped;
        var player = GameMain.mainPlayer;
        player.isAlive = life.IsAlive;
        player.deathCount = life.DeathCount;
        player.timeSinceKilled = life.TimeSinceKilled;
        player.invincibleTicks = life.InvincibleTicks;
        if (life.IsAlive) return;
        player.mecha.hp = 0;
        player.mechaArmorModel.Kill();
        player.mechaArmorModel.wreckagesCenterUPos = player.uPosition;
        var action = player.controller.actionDeath;
        action.ResetRespawnState();
        action.selectedRespawnOption = -1;
        // Restart positioning/loading, but the durable redeploy flag prevents a second inventory drop.
        action.respawnMode = life.RespawnMode;
        action.respawnStage = 0;
        action.respawnTick = 0;
        lastStage = -1;
    }

    public void ApplyRemote(ushort playerId, PlayerLifeData life)
    {
        if (life == null || playerId == Multiplayer.Session.LocalPlayer.Id) return;
        if (received.TryGetValue(playerId, out var revision) && revision >= life.Revision) return;
        using (Multiplayer.Session.World.GetRemotePlayersModels(out var models))
        {
            if (!models.TryGetValue(playerId, out var model)) return;
            received[playerId] = life.Revision;
            using var scope = new RemoteWreckageScope(model);
            var player = model.PlayerInstance;
            if (!life.IsAlive && player.isAlive)
            {
                player.mecha.Kill();
                player.mechaArmorModel.Kill();
            }
            else if (life.IsAlive && !player.isAlive)
            {
                player.mechaArmorModel.ClearWreckages();
                player.mecha.Respawn();
                player.mechaArmorModel.Respawn();
            }
            player.isAlive = life.IsAlive;
            player.deathCount = life.DeathCount;
            player.timeSinceKilled = life.TimeSinceKilled;
            player.invincibleTicks = life.InvincibleTicks;
            if (model.RespawnVisual.Observe(model.Life, life)) player.mechaArmorModel.PrepareRespawn();
            model.Life = life;
        }
    }

    public static void TickRemote(RemotePlayerModel model)
    {
        var life = model.Life;
        if (model.PlayerInstance.isAlive) return;
        using var scope = new RemoteWreckageScope(model);
        var armor = model.PlayerInstance.mechaArmorModel;
        if (life.RespawnMode == 2) armor.WreckagesRespawnLogic(model.RespawnVisual.Advance());
        else
        {
            armor.GameTickWreckages();
            armor.SyncWreckagesTrans();
        }
    }

    public void Remove(ushort playerId) => received.Remove(playerId);
    public void Dispose()
    {
        pending = null; received.Clear();
        refusedSnapshotPlayers.Clear();
        PlayerLifeData.CurrentTransactionId = "";
        PlayerLifeData.CurrentRedeployItemsDropped = false;
    }
}

public sealed class RemoteWreckageScope : IDisposable
{
    private readonly List<ArmorWreckage> previous;
    private readonly RemotePlayerModel model;
    public RemoteWreckageScope(RemotePlayerModel remote)
    {
        model = remote;
        previous = MechaArmorModel.all_wreckages;
        MechaArmorModel.all_wreckages = remote.Wreckages;
    }
    public void Dispose()
    {
        model.Wreckages = MechaArmorModel.all_wreckages ?? new List<ArmorWreckage>();
        MechaArmorModel.all_wreckages = previous;
    }
}
