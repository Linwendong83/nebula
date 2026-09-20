#region

using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using NebulaAPI;
using NebulaAPI.GameState;
using NebulaAPI.Interfaces;
using NebulaAPI.Networking;
using NebulaAPI.Packets;
using NebulaModel;
using NebulaModel.DataStructures;
using NebulaModel.Logger;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Players;
using NebulaModel.Packets.Session;
using NebulaModel.Packets.Universe;
using NebulaModel.Utils;
using NebulaWorld;
using NebulaWorld.SocialIntegration;

#endregion

namespace NebulaNetwork.PacketProcessors.Session;

[RegisterPacketProcessor]
public class LobbyRequestProcessor : PacketProcessor<LobbyRequest>
{
    protected override void ProcessPacket(LobbyRequest packet, NebulaConnection conn)
    {
        if (IsClient)
        {
            return;
        }

        var player = Players.Get(conn, EConnectionStatus.Pending);

        if (player is null)
        {
            Multiplayer.Session.Server.Disconnect(conn, DisconnectionReason.InvalidData);
            Log.Warn("WARNING: Player tried to enter lobby without being in the pending list");
            return;
        }

        if (GameMain.isFullscreenPaused)
        {
            Log.Warn("Reject connection because server is still loading");
            Multiplayer.Session.Server.Disconnect(conn, DisconnectionReason.HostStillLoading);
            // pendingPlayers.Remove(conn);
            return;
        }

        if (!ModsVersionCheck(packet, out var disconnectionReason, out var reasonMessage))
        {
            Log.Warn("Reject connection because mods mismatch");

            Multiplayer.Session.Server.Disconnect(conn, disconnectionReason, reasonMessage);
            // pendingPlayers.Remove(conn);
            return;
        }


        var isNewUser = false;

        //TODO: some validation of client cert / generating auth challenge for the client
        // Load old data of the client
        var clientCertHash = CryptoUtils.Hash(packet.ClientCert);
        if (Players.Connected.Values.Concat(Players.Syncing.Values).Concat(Players.Pending.Values)
            .Any(other => other.Connection != conn && (other.Data as PlayerData)?.PersistentId == clientCertHash))
        {
            Server.Disconnect(conn, DisconnectionReason.InvalidData, "This player identity is already connected. Disconnect it before joining again.");
            return;
        }
        ((PlayerData)player.Data).PersistentId = clientCertHash;
        if (SaveManager.PlayerSaves.TryGetValue(clientCertHash, out var value))
        {
            var playerData = value;
            player.LoadUserData(playerData);
            // Old servers could save an entry before the first inventory snapshot arrived.
            isNewUser = playerData.Mecha?.ReactorStorage == null;
        }
        else
        {
            // store player data once he fully loaded into the game (SyncCompleteProcessor)
            isNewUser = true;
        }

        // Add the username to the player data
        ((PlayerData)player.Data).PersistentId = clientCertHash;
        player.Data.Username = !string.IsNullOrWhiteSpace(packet.Username) ? packet.Username : $"Player {player.Id}";
        if (Multiplayer.Session.IsGameLoaded)
        {
            NebulaWorld.Combat.PlayerLifeManager.RestoreServer((PlayerData)player.Data);
            Multiplayer.Session.PropertyTransactions.RestoreServerPlayer((PlayerData)player.Data);
        }

        if (!((PlayerData)player.Data).SessionCounted)
        {
            ((PlayerData)player.Data).SessionCounted = true;
            Multiplayer.Session.NumPlayers += 1;
        }
        DiscordManager.UpdateRichPresence();

        // if user is known and host is ingame dont put him into lobby but let him join the game
        if (!isNewUser && Multiplayer.Session.IsGameLoaded)
        {
            Multiplayer.Session.Server.Players.TryUpgrade(player, EConnectionStatus.Syncing);

            Multiplayer.Session.World.OnPlayerJoining(player.Data.Username);

            // Make sure that each player that is currently in the game receives that a new player as join so they can create its RemotePlayerCharacter
            var pdata = new PlayerJoining((PlayerData)player.Data.CreateCopyWithoutMechaData(),
                Multiplayer.Session.NumPlayers); // Remove inventory from mecha data

            Server.SendPacket(pdata);

            //Add current tech bonuses to the connecting player based on the Host's mecha
            ((MechaData)player.Data.Mecha).TechBonuses = new PlayerTechBonuses(GameMain.mainPlayer.mecha);

            var gameDesc = GameMain.data.gameDesc;
            byte[] combatSettingsData;
            using (var p = new BinaryUtils.Writer())
            {
                gameDesc.combatSettings.Export(p.BinaryWriter);
                combatSettingsData = p.CloseAndGetBytes();
            }
            var modsSettings = GetModSetting(out var modSettingCount);
            player.SendPacket(new HandshakeResponse(in gameDesc, combatSettingsData, false, (PlayerData)player.Data, modsSettings,
                modSettingCount, Config.Options.SyncSoil, Multiplayer.Session.NumPlayers, DiscordManager.GetPartyId()));
        }
        else
        {
            var gameDesc = Multiplayer.Session.IsGameLoaded ? GameMain.data.gameDesc : UIRoot.instance.galaxySelect.gameDesc;
            byte[] combatSettingsData;
            using (var p = new BinaryUtils.Writer())
            {
                gameDesc.combatSettings.Export(p.BinaryWriter);
                combatSettingsData = p.CloseAndGetBytes();
            }
            var modsSettings = GetModSetting(out var modSettingCount);
            player.SendPacket(new LobbyResponse(in gameDesc, combatSettingsData, modsSettings, modSettingCount,
                Multiplayer.Session.NumPlayers, DiscordManager.GetPartyId()));

            // Send overriden Planet and Star names
            player.SendPacket(new NameInputPacket(GameMain.galaxy));
        }
    }

    private static byte[] GetModSetting(out int settingsCount)
    {
        settingsCount = 0;
        using var p = new BinaryUtils.Writer();
        foreach (var pluginInfo in Chainloader.PluginInfos)
        {
            if (pluginInfo.Value.Instance is not IMultiplayerModWithSettings mod)
            {
                continue;
            }
            p.BinaryWriter.Write(pluginInfo.Key);
            mod.Export(p.BinaryWriter);
            settingsCount++;
        }
        return p.CloseAndGetBytes();
    }

    private static bool ModsVersionCheck(in LobbyRequest packet, out DisconnectionReason reason, out string reasonString)
    {
        reason = DisconnectionReason.Normal;
        reasonString = null;
        var clientMods = new Dictionary<string, string>();

        Log.Info("Packet null: " + (packet == null));
        Log.Info("ModsVersion null: " + (packet?.ModsVersion == null));

        using (var reader = new BinaryUtils.Reader(packet.ModsVersion))
        {
            for (var i = 0; i < packet.ModsCount; i++)
            {
                var guid = reader.BinaryReader.ReadString();
                var version = reader.BinaryReader.ReadString();

                if (!Chainloader.PluginInfos.ContainsKey(guid))
                {
                    reason = DisconnectionReason.ModIsMissingOnServer;
                    reasonString = guid;
                    return false;
                }

                clientMods.Add(guid, version);
            }
        }

        foreach (var pluginInfo in Chainloader.PluginInfos)
        {
            if (pluginInfo.Value.Instance is IMultiplayerMod mod)
            {
                if (!clientMods.TryGetValue(pluginInfo.Key, out var value))
                {
                    reason = DisconnectionReason.ModIsMissing;
                    reasonString = pluginInfo.Key;
                    return false;
                }

                if (mod.CheckVersion(mod.Version, value))
                {
                    continue;
                }

                reason = DisconnectionReason.ModVersionMismatch;
                reasonString = $"{pluginInfo.Key};{value};{mod.Version}";
                return false;
            }

            foreach (var dependency in pluginInfo.Value.Dependencies)
            {
                if (dependency.DependencyGUID != NebulaModAPI.API_GUID)
                {
                    continue;
                }

                var hostVersion = pluginInfo.Value.Metadata.Version.ToString();
                if (!clientMods.TryGetValue(pluginInfo.Key, out var value))
                {
                    reason = DisconnectionReason.ModIsMissing;
                    reasonString = pluginInfo.Key;
                    return false;
                }

                if (value == hostVersion)
                {
                    continue;
                }

                reason = DisconnectionReason.ModVersionMismatch;
                reasonString = $"{pluginInfo.Key};{value};{hostVersion}";
                return false;
            }
        }

        if (packet.GameVersionSig == GameConfig.gameVersion.sig)
        {
            return true;
        }

        reason = DisconnectionReason.GameVersionMismatch;
        reasonString = $"{packet.GameVersionSig};{GameConfig.gameVersion.sig}";
        return false;
    }
}
