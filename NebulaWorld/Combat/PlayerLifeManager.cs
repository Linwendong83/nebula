using System;
using System.Collections.Generic;
using System.IO;
using NebulaModel.DataStructures;
using NebulaModel.Networking.Serialization;
using NebulaModel.Packets.Combat.Mecha;
using NebulaModel.Utils;
using NebulaWorld.GameStates;

namespace NebulaWorld.Combat;

public sealed class PlayerLifeManager : IDisposable
{
    private PlayerLifePacket pending;
    private long lastSend;
    private int lastStage = -1;
    private readonly Dictionary<ushort, long> received = new();

    public void Publish()
    {
        if (!Multiplayer.Session.IsGameLoaded || Multiplayer.Session.IsDedicated) return;
        var bytes = MetadataTransactionManager.CapturePlayer();
        var data = (PlayerData)Multiplayer.Session.LocalPlayer.Data;
        AtomicFile.Write(LocalPath(), bytes);
        pending = new PlayerLifePacket
        { PlayerId = data.PlayerId, Life = data.Life, PlayerSnapshot = bytes };
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
    }

    public static void StoreServer(PlayerData player, byte[] snapshot)
    {
        var restored = new PlayerData();
        restored.Deserialize(new NetDataReader(snapshot));
        if (restored.Life.Revision < player.Life.Revision) return;
        restored.PlayerId = player.PlayerId;
        restored.Username = player.Username;
        var writer = new NetDataWriter();
        restored.Serialize(writer);
        AtomicFile.Write(ServerPath(player.PersistentId), writer.CopyData());
        // Network identity and username remain those authenticated by the session.
        player.Mecha = restored.Mecha;
        player.Life = restored.Life;
        player.LocalPlanetId = restored.LocalPlanetId;
        player.LocalPlanetPosition = restored.LocalPlanetPosition;
        player.UPosition = restored.UPosition;
    }

    public static void RestoreServer(PlayerData player)
    {
        var path = ServerPath(player.PersistentId);
        if (!File.Exists(path)) return;
        var snapshot = new PlayerData();
        snapshot.Deserialize(new NetDataReader(File.ReadAllBytes(path)));
        if (snapshot.Life.Revision <= player.Life.Revision) return;
        player.Mecha = snapshot.Mecha;
        player.Life = snapshot.Life;
        player.LocalPlanetId = snapshot.LocalPlanetId;
        player.LocalPlanetPosition = snapshot.LocalPlanetPosition;
        player.UPosition = snapshot.UPosition;
    }

    public void RestoreLocal(PlayerData data)
    {
        var path = LocalPath();
        if (File.Exists(path))
        {
            var snapshot = new PlayerData();
            snapshot.Deserialize(new NetDataReader(File.ReadAllBytes(path)));
            if (snapshot.Life.Revision > data.Life.Revision &&
                (snapshot.Life.DeathCount > data.Life.DeathCount || !snapshot.Life.IsAlive))
            {
                data.Life = snapshot.Life;
                snapshot.Mecha.UpdateMech(GameMain.mainPlayer);
                SimulatedWorld.FixPlayerAfterImport();
            }
        }
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
            if (life.RespawnMode == 2 && model.Life.RespawnMode != 2) player.mechaArmorModel.PrepareRespawn();
            model.Life = life;
        }
    }

    public static void TickRemote(RemotePlayerModel model)
    {
        var life = model.Life;
        if (model.PlayerInstance.isAlive) return;
        using var scope = new RemoteWreckageScope(model);
        var armor = model.PlayerInstance.mechaArmorModel;
        if (life.RespawnMode == 2) armor.WreckagesRespawnLogic(life.RespawnTick++);
        else
        {
            armor.GameTickWreckages();
            armor.SyncWreckagesTrans();
        }
    }

    public void Remove(ushort playerId) => received.Remove(playerId);
    private static string LocalPath() => Path.Combine(GameConfig.propertyFolder, "Nebula",
        AtomicFile.IdentityFileName(MetadataManager.LocalIdentity), SaveManager.WorldId + ".life");
    private static string ServerPath(string identity) => Path.Combine(GameConfig.gameSaveFolder, "Nebula",
        SaveManager.WorldId, "players", AtomicFile.IdentityFileName(identity) + ".bin");
    public void Dispose()
    {
        pending = null; received.Clear();
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
