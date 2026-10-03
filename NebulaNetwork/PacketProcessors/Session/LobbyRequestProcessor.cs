#region

using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using NebulaAPI;
using NebulaAPI.GameState;
using NebulaAPI.Interfaces;
using NebulaAPI.Networking;
using NebulaAPI.Packets;
using NebulaModel;
using NebulaModel.Authority;
using NebulaModel.DataStructures;
using NebulaModel.Logger;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Authority;
using NebulaModel.Packets.Players;
using NebulaModel.Packets.Session;
using NebulaModel.Packets.Universe;
using NebulaModel.Utils;
using NebulaWorld;

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

        if (!ModsVersionCheck(packet, out var disconnectionReason, out var reasonMessage,
                out var clientAuthorityMode, out _, out _))
        {
            Log.Warn("Reject connection because mods mismatch");

            Multiplayer.Session.Server.Disconnect(conn, disconnectionReason, reasonMessage);
            // pendingPlayers.Remove(conn);
            return;
        }

        // The peer declared authority mode and the host agreed to it; remember it on the session so
        // the packet gate knows which rule set this room runs.
        Multiplayer.Session.Authority.OnPeerNegotiated(clientAuthorityMode);


        var isNewUser = false;

        //TODO: some validation of client cert / generating auth challenge for the client
        // Load old data of the client
        var clientCertHash = CryptoUtils.Hash(packet.ClientCert);
        if (Players.Connected.Values.Concat(Players.Syncing.Values).Concat(Players.Pending.Values)
            .Any(other => !ReferenceEquals(other.Connection, conn) &&
                          (other.Data as PlayerData)?.PersistentId == clientCertHash))
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

        // While the host's game is loaded, skip the lobby page for every joining player:
        // the page offers nothing a running server does not already own. Only a host still
        // configuring a new game keeps players in the lobby as a waiting room.
        if (Multiplayer.Session.IsGameLoaded)
        {
            Multiplayer.Session.Server.Players.TryUpgrade(player, EConnectionStatus.Syncing);

            if (isNewUser)
            {
                // A first-time player carries no spawn data yet: assign the host's birth
                // planet before the data is sent, like the lobby start button used to.
                NebulaWorld.Player.SpawnManager.SetBirthPoint((PlayerData)player.Data);
            }

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
            // The mode is stated explicitly from what the check above actually accepted, so the
            // client can assert the same agreement instead of inferring it.
            player.SendPacket(new HandshakeResponse(in gameDesc, combatSettingsData, isNewUser, (PlayerData)player.Data, modsSettings,
                modSettingCount, Multiplayer.Session.NumPlayers)
            {
                AuthorityMode = (byte)clientAuthorityMode
            });

            SendAuthorityWelcome(player);
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
                Multiplayer.Session.NumPlayers));

            // Send overriden Planet and Star names
            player.SendPacket(new NameInputPacket(GameMain.galaxy));
        }
    }

    /// <summary>
    /// Sends the joining client the world identity it must use on every command.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is where a client learns the world epoch. Each client also gets its own connection epoch
    /// so a reconnect cannot replay a sequence number the host still remembers; the epoch is drawn
    /// here, when the connection is accepted, and travels only in this welcome.
    /// </para>
    /// <para>
    /// Nothing is sent in legacy mode. A legacy room has no world identity to state, and sending one
    /// would invite a client to act on authority messages the handshake never agreed to.
    /// </para>
    /// </remarks>
    private static void SendAuthorityWelcome(INebulaPlayer player)
    {
        var runtime = Multiplayer.Session.AuthorityRuntime;
        var identity = Multiplayer.Session.Authority;
        if (runtime is null || !identity.IsHost || !identity.IsActive) return;

        var connectionEpoch = runtime.AssignConnectionEpoch(player.Id);
        var header = new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.Welcome, identity.Epoch,
            connectionEpoch, sequence: 1, hostTick: GameMain.gameTick, claimedPlayerId: player.Id,
            payloadLength: 0);
        var welcome = AuthorityWelcomePacket.Create(header, AuthorityMode.HostAuthority,
            AuthorityLocalOptions.OfferedCapabilities);
        player.SendPacket(welcome);
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

    private static bool ModsVersionCheck(in LobbyRequest packet, out DisconnectionReason reason, out string reasonString,
        out AuthorityMode clientMode, out AuthoritySchema clientSchema,
        out AuthorityCapability clientRequiredCapabilities)
    {
        reason = DisconnectionReason.Normal;
        reasonString = null;
        clientMode = AuthorityMode.None;
        clientSchema = AuthoritySchema.None;
        clientRequiredCapabilities = AuthorityCapability.None;
        var clientMods = new Dictionary<string, string>();
        var protocolSeen = false;
        var authoritySeen = false;

        if (packet.ModsVersion == null || packet.ModsCount < 0 || packet.ModsCount > 256)
        {
            reason = DisconnectionReason.InvalidData;
            return false;
        }

        using (var reader = new BinaryUtils.Reader(packet.ModsVersion))
        {
            for (var i = 0; i < packet.ModsCount; i++)
            {
                var guid = reader.BinaryReader.ReadString();
                var version = reader.BinaryReader.ReadString();

                if (guid == SessionProtocol.HandshakeKey)
                {
                    protocolSeen = true;
                    if (version == SessionProtocol.Version.ToString()) continue;
                    reason = DisconnectionReason.ModVersionMismatch;
                    reasonString = $"Nebula protocol;{version};{SessionProtocol.Version}";
                    return false;
                }

                if (guid == SessionProtocol.AuthorityHandshakeKey)
                {
                    authoritySeen = true;
                    if (!AuthorityHandshake.TryDecode(version, out clientMode, out clientSchema,
                            out clientRequiredCapabilities))
                    {
                        // An unreadable declaration is a protocol error, never a silent fallback to
                        // legacy: falling back would put two rule sets in one room.
                        reason = DisconnectionReason.ModVersionMismatch;
                        reasonString = $"Nebula authority;{version};{AuthorityLocalOptions.Declaration}";
                        return false;
                    }
                    continue;
                }

                if (!Chainloader.PluginInfos.ContainsKey(guid))
                {
                    reason = DisconnectionReason.ModIsMissingOnServer;
                    reasonString = guid;
                    return false;
                }

                clientMods.Add(guid, version);
            }
        }

        if (!protocolSeen)
        {
            reason = DisconnectionReason.ModVersionMismatch;
            reasonString = $"Nebula protocol;legacy;{SessionProtocol.Version}";
            return false;
        }

        if (!authoritySeen)
        {
            reason = DisconnectionReason.ModVersionMismatch;
            reasonString = $"Nebula authority;missing;{AuthorityLocalOptions.Declaration}";
            return false;
        }

        // Modes and schemas must match exactly, and a client may only require capabilities the host
        // already advertises. This is the check that refuses a mixed room.
        if (!AuthorityNegotiation.IsCompatible(AuthorityLocalOptions.Mode, AuthorityLocalOptions.Schema,
                AuthorityLocalOptions.OfferedCapabilities, clientMode, clientSchema,
                clientRequiredCapabilities, out var authorityReject))
        {
            reason = DisconnectionReason.ModVersionMismatch;
            reasonString = "Nebula authority;" + authorityReject;
            return false;
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
