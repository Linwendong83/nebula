#region

using System;
using System.Collections.Generic;
using System.Linq;
using NebulaAPI;
using NebulaAPI.DataStructures;
using NebulaAPI.GameState;
using NebulaModel;
using NebulaModel.DataStructures;
using NebulaModel.DataStructures.Chat;
using NebulaModel.Logger;
using NebulaModel.Networking;
using NebulaModel.Packets.Players;
using NebulaModel.Packets.Session;
using NebulaModel.Packets.Trash;
using NebulaModel.Packets.Universe;
using NebulaModel.Packets.Warning;
using NebulaModel.Utils;
using NebulaWorld.MonoBehaviours;
using NebulaWorld.MonoBehaviours.Local;
using NebulaWorld.MonoBehaviours.Local.Chat;
using NebulaWorld.MonoBehaviours.Remote;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

#endregion

namespace NebulaWorld;

/// <summary>
///     This class keeps track of our simulated world. It holds all temporary entities like remote player models
///     and also helps to execute some remote player actions that you would want to replicate on the local client.
/// </summary>
public class SimulatedWorld : IDisposable
{
    private readonly ThreadSafe threadSafe = new();
    private LocalPlayerMovement localPlayerMovement;

    private bool IsPlayerJoining { get; set; }

    public void Dispose()
    {
        using (GetRemotePlayersModels(out var remotePlayersModels))
        {
            foreach (var model in remotePlayersModels.Values)
            {
                model.Destroy();
            }

            remotePlayersModels.Clear();
        }

        Object.Destroy(localPlayerMovement);
        SetPauseIndicator(true);
        GC.SuppressFinalize(this);
    }

    public Locker GetRemotePlayersModels(out Dictionary<ushort, RemotePlayerModel> remotePlayersModels)
    {
        return threadSafe.RemotePlayersModels.GetLocked(out remotePlayersModels);
    }

    public void SetupInitialPlayerState()
    {
        if (!Multiplayer.Session.IsGameLoaded)
        {
            Log.Warn("Trying to setup initial player state before the game is loaded!");
            return;
        }

        if (!Multiplayer.Session.LocalPlayer.IsInitialDataReceived)
        {
            Log.Warn("Trying to setup initial player state before the player data was received!");
            return;
        }

        var player = Multiplayer.Session.LocalPlayer as LocalPlayer;

        // If not a new client, we need to update the player position to put him where he was previously
        if (player is { IsClient: true })
        {
            GameMain.mainPlayer.planetId = player.Data.LocalPlanetId;
            if (player.Data.LocalPlanetId == -1)
            {
                GameMain.mainPlayer.uPosition =
                    new VectorLF3(player.Data.UPosition.x, player.Data.UPosition.y, player.Data.UPosition.z);
            }
            else
            {
                GameMain.mainPlayer.position = player.Data.LocalPlanetPosition.ToVector3();
                GameMain.mainPlayer.transform.localPosition = GameMain.mainPlayer.position;
                GameMain.mainPlayer.uPosition = GameMain.localPlanet.uPosition +
                    (VectorLF3)(GameMain.localPlanet.runtimeRotation * GameMain.mainPlayer.position);
            }
            GameMain.mainPlayer.uRotation = Quaternion.Euler(player.Data.Rotation.ToVector3());

            // Load client's saved data from the last session.
            if (!player.IsNewPlayer)
            {
                player.Data.Mecha.UpdateMech(GameMain.mainPlayer);
                FixPlayerAfterImport();
                Multiplayer.Session.Life.RestoreLocal((PlayerData)player.Data);
                Multiplayer.Session.Vegetation.RestoreLocal(((PlayerData)player.Data).VegetableCollectionData);
            }
        }

        // Initialization on the host side after game is loaded
        Multiplayer.Session.Factories.InitializePrebuildRequests();

        if (player is { IsClient: true })
        {
            // Update player's Mecha tech bonuses
            ((MechaData)player.Data.Mecha).TechBonuses.UpdateMech(GameMain.mainPlayer.mecha);

            if (player.IsNewPlayer)
            {
                // Set mecha to full energy, shield and hp so new client won't have low stats when starting
                GameMain.mainPlayer.mecha.coreEnergy = GameMain.mainPlayer.mecha.coreEnergyCap;
                GameMain.mainPlayer.mecha.energyShieldEnergy = GameMain.mainPlayer.mecha.energyShieldCapacity;
                GameMain.mainPlayer.mecha.hp = GameMain.mainPlayer.mecha.hpMaxApplied;
                GameMain.mainPlayer.mecha.constructionModule.droneEnabled = true;
                if (GameMain.history.logisticShipWarpDrive)
                {
                    // If warp has unlocked, give new client few warpers
                    GameMain.mainPlayer.TryAddItemToPackage(1210, 5, 0, false);
                }
            }
            else
            {
                // Prevent old client from dropping into gas gaint
                var planet = GameMain.galaxy.PlanetById(player.Data.LocalPlanetId);
                if (planet != null)
                {
                    var altitude = GameMain.mainPlayer.transform.localPosition.magnitude - planet.realRadius;
                    if (altitude > 5f || planet.type == EPlanetType.Gas)
                    {
                        GameMain.mainPlayer.movementState = EMovementState.Fly;
                    }
                }
            }

            // Refresh Logistics Distributor traffic for player delivery package changes
            GameMain.mainPlayer.factory?.transport.RefreshDispenserTraffic();

            // Notify the server that we are done loading the game
            var clientCert = CryptoUtils.GetPublicKey(CryptoUtils.GetOrCreateUserCert());
            Multiplayer.Session.Network.SendPacket(new SyncComplete(clientCert));
            Multiplayer.Session.Life.Publish(); // Persist the initial inventory before the first periodic update.

            // Subscribe for the local star events
            Multiplayer.Session.Network.SendPacket(new PlayerUpdateLocalStarId(Multiplayer.Session.LocalPlayer.Id, GameMain.data.localStar?.id ?? -1));

            // Request latest warning signal
            Multiplayer.Session.Network.SendPacket(new WarningDataRequest(WarningRequestEvent.Signal));

            // Hide the "Joining Game" popup
            InGamePopup.FadeOut();
        }

        // Set the name of local player in starmap from Icarus to user name
        if (player != null)
        {
            GameMain.mainPlayer.mecha.appearance.overrideName = " " + player.Data.Username + " ";
        }
        // Finally we need add the local player components to the player character
        localPlayerMovement = GameMain.mainPlayer.gameObject.AddComponentIfMissing<LocalPlayerMovement>();
        // ChatManager should exist continuously until the game is closed
        GameMain.mainPlayer.gameObject.AddComponentIfMissing<ChatManager>();
        // The player list is part of ChatManager's canvas overlay.
    }

    public static void FixPlayerAfterImport()
    {
        var player = GameMain.mainPlayer;

        // Mimic MechaForge.Init(Mecha _mecha)
        player.mecha.forge.SetHiddenProperty(nameof(MechaForge.mecha), GameMain.mainPlayer.mecha);
        player.mecha.forge.SetHiddenProperty(nameof(MechaForge.player), GameMain.mainPlayer);
        player.mecha.forge.gameHistory = GameMain.data.history;
        player.mecha.forge.gameHistory = GameMain.data.history;
        player.mecha.forge.bottleneckItems = new HashSet<int>();

        // Unlock the mecha functions as Mecha.Init()
        player.mecha.energyShieldBurstUnlocked = true;
        player.mecha.constructionModule.droneEnabled = true;
        player.mecha.constructionModule.droneConstructEnabled = true;
        player.mecha.constructionModule.droneRepairEnabled = true;

        // Inventory Capacity level 7 will increase package columncount from 10 -> 12
        var packageRowCount = (player.package.size - 1) / player.GetPackageColumnCount() + 1;
        // Make sure all slots are available on UI
        player.package.SetSize(player.packageColCount * packageRowCount);
        player.deliveryPackage.rowCount = packageRowCount;
        player.deliveryPackage.NotifySizeChange();

        // do we need to do something about the spaceSector?
        player.mecha.groundCombatModule.AfterImport(GameMain.data);
        player.mecha.spaceCombatModule.AfterImport(GameMain.data);

        // Set fleetId = 0, fleetAstroId = 0 and fighter.craftId = 0
        var moduleFleets = player.mecha.groundCombatModule.moduleFleets;
        for (var index = 0; index < moduleFleets.Length; index++)
        {
            moduleFleets[index].ClearFleetForeignKey();
        }
        moduleFleets = player.mecha.spaceCombatModule.moduleFleets;
        for (var index = 0; index < moduleFleets.Length; index++)
        {
            moduleFleets[index].ClearFleetForeignKey();
        }
    }

    public void OnPlayerJoining(string username)
    {
        if (IsPlayerJoining)
        {
            return;
        }
        IsPlayerJoining = true;
        Multiplayer.Session.CanPause = true;
        GameMain.isFullscreenPaused = true;
        InGamePopup.ShowInfo("Loading".Translate(),
            string.Format("{0} joining the game, please wait\n(Use BulletTime mod to unfreeze the game)".Translate(),
                username), null);
    }

    public static void OnPlayerJoinedGame(INebulaPlayer player)
    {
        Multiplayer.Session.World.SpawnRemotePlayerModel(player.Data);

        // Sync overrideName of planets and stars
        player.SendPacket(new NameInputPacket(GameMain.galaxy));

        // Initialize the new player's view without interrupting anybody else's charging.
        Multiplayer.Session.PowerTowers.SendSnapshot(player.Connection);

        // Sync enemyDropBans for joined client
        using (var writer = new BinaryUtils.Writer())
        {
            writer.BinaryWriter.Write(GameMain.data.trashSystem.enemyDropBans.Count);
            foreach (var itemId in GameMain.data.trashSystem.enemyDropBans)
            {
                writer.BinaryWriter.Write(itemId);
            }
            player.SendPacket(new TrashSystemLootFilterPacket(writer.CloseAndGetBytes()));
        }

        // (Host only) Trigger when a new client added to connected players
        Log.Info($"Client{player.Data.PlayerId} - {player.Data.Username} joined");
        try
        {
            NebulaModAPI.OnPlayerJoinedGame?.Invoke(player.Data);
        }
        catch (Exception e)
        {
            Log.Error("NebulaModAPI.OnPlayerJoinedGame error:\n" + e);
        }
    }

    public static void OnPlayerLeftGame(INebulaPlayer player)
    {
        Multiplayer.Session.World.DestroyRemotePlayerModel(player.Id);

        // (Host only) Trigger when a connected client leave the game
        Log.Info($"Client{player.Data.PlayerId} - {player.Data.Username} left");
        try
        {
            NebulaModAPI.OnPlayerLeftGame?.Invoke(player.Data);
        }
        catch (Exception e)
        {
            Log.Error("NebulaModAPI.OnPlayerLeftGame error:\n" + e);
        }
    }

    public void OnAllPlayersSyncCompleted()
    {
        IsPlayerJoining = false;
        InGamePopup.FadeOut();
        GameMain.isFullscreenPaused = false;
        Multiplayer.Session.CanPause = false;
    }

    public void SpawnRemotePlayerModel(IPlayerData playerData)
    {
        using (GetRemotePlayersModels(out var remotePlayersModels))
        {
            if (remotePlayersModels.ContainsKey(playerData.PlayerId))
            {
                return;
            }
            Log.Info($"Spawn player model {playerData.PlayerId} {playerData.Username}");
            var model = new RemotePlayerModel(playerData.PlayerId, playerData.Username);
            model.Movement.LocalStarId = playerData.LocalStarId;
            model.Movement.localPlanetId = playerData.LocalPlanetId;
            remotePlayersModels.Add(playerData.PlayerId, model);
            Multiplayer.Session.Life.ApplyRemote(playerData.PlayerId, ((PlayerData)playerData).Life);

            ChatManager.Instance?.NotifyPlayerPresence(playerData.Username, true);
        }
    }

    public void DestroyRemotePlayerModel(ushort playerId)
    {
        using (GetRemotePlayersModels(out var remotePlayersModels))
        {
            if (!remotePlayersModels.TryGetValue(playerId, out var player))
            {
                return;
            }
            ChatManager.Instance?.NotifyPlayerPresence(player.Username, false);

            player.Destroy();
            remotePlayersModels.Remove(playerId);
            if (remotePlayersModels.Count == 0 && Config.Options.AutoPauseEnabled)
            {
                Multiplayer.Session.CanPause = true;
            }
        }
    }

    public void UpdateRemotePlayerRealtimeState(PlayerMovement packet)
    {
        using (GetRemotePlayersModels(out var remotePlayersModels))
        {
            if (!remotePlayersModels.TryGetValue(packet.PlayerId, out var player))
            {
                return;
            }
            player.Movement.UpdatePosition(packet);
            player.Animator.UpdateState(packet);

            // Mark to let combat manager update energyShieldEnergy
            player.MechaInstance.energyShieldEnergyRate = (packet.Flags & PlayerMovement.EFlags.hasShield) != 0 ? 1 : 2;
        }
    }

    public void RenderPlayerNameTagsOnStarmap(UIStarmap starmap)
    {
        // Make a copy of the "Icarus" text from the starmap
        var starmap_playerNameText = starmap.playerNameText;
        var starmap_playerTrack = starmap.playerTrack;

        using (GetRemotePlayersModels(out var remotePlayersModels))
        {
            foreach (var playerModel in remotePlayersModels.Select(player => player.Value))
            {
                Text nameText;
                Transform starmapTracker;
                if (playerModel.StarmapNameText != null && playerModel.StarmapTracker != null)
                {
                    nameText = playerModel.StarmapNameText;
                    starmapTracker = playerModel.StarmapTracker;
                }
                else
                {
                    // Make an instance of the "Icarus" text to represent the other player name
                    nameText = playerModel.StarmapNameText =
                        Object.Instantiate(starmap_playerNameText, starmap_playerNameText.transform.parent);
                    nameText.text = $"{playerModel.Username}";
                    nameText.gameObject.SetActive(true);

                    // Make an instance the player tracker object
                    starmapTracker = playerModel.StarmapTracker =
                        Object.Instantiate(starmap_playerTrack, starmap_playerTrack.parent);
                    starmapTracker.gameObject.SetActive(true);
                }

                VectorLF3 adjustedVector;
                if (playerModel.Movement.localPlanetId > 0)
                {
                    // Get the position of the planet
                    var planet = GameMain.galaxy.PlanetById(playerModel.Movement.localPlanetId);
                    adjustedVector = planet.uPosition;

                    // Add the local position of the player
                    var localPlanetPosition = playerModel.Movement.GetLastPosition().LocalPlanetPosition.ToVector3();
                    adjustedVector += (VectorLF3)Maths.QRotate(planet.runtimeRotation, localPlanetPosition);
                }
                else
                {
                    // Just use the raw uPos as we don't care too much about precise locations
                    adjustedVector = playerModel.Movement.absolutePosition;
                }

                // Scale as required
                adjustedVector = (adjustedVector - starmap.viewTargetUPos) * 0.00025;

                // Get the point on the screen that represents the world position
                if (!starmap.WorldPointIntoScreen(adjustedVector, out var rectPoint))
                {
                    continue;
                }

                // Put the marker directly on the location of the player
                starmapTracker.position = adjustedVector;

                if (playerModel.Movement.localPlanetId > 0)
                {
                    var planet = GameMain.galaxy.PlanetById(playerModel.Movement.localPlanetId);
                    var rotation = planet.runtimeRotation *
                                   Quaternion.LookRotation(playerModel.PlayerModelTransform.forward,
                                       playerModel.Movement.GetLastPosition().LocalPlanetPosition.ToVector3());
                    starmapTracker.rotation = rotation;
                }
                else
                {
                    var rotation = Quaternion.LookRotation(playerModel.PlayerModelTransform.forward,
                        playerModel.PlayerTransform.localPosition);
                    starmapTracker.rotation = rotation;
                }

                starmapTracker.localScale = UIStarmap.isChangingToMilkyWay
                    ? Vector3.zero
                    : Vector3.one * (starmap.screenCamera.transform.position - starmapTracker.position).magnitude;

                // Put their name above or below it
                nameText.rectTransform.anchoredPosition = new Vector2(rectPoint.x + (rectPoint.x > 600f ? -35 : 35),
                    rectPoint.y + (rectPoint.y > -350.0 ? -19f : 19f));
                nameText.gameObject.SetActive(!UIStarmap.isChangingToMilkyWay);
            }
        }
    }

    public void ClearPlayerNameTagsOnStarmap()
    {
        using (GetRemotePlayersModels(out var remotePlayersModels))
        {
            foreach (var player in remotePlayersModels)
            {
                // Destroy the marker and name so they don't linger and cause problems
                Object.Destroy(player.Value.StarmapNameText.gameObject);
                Object.Destroy(player.Value.StarmapTracker.gameObject);

                // Null them out so they can be recreated next time the map is opened
                player.Value.StarmapNameText = null;
                player.Value.StarmapTracker = null;
            }
        }
    }

    public void RenderPlayerNameTagsInGame()
    {
        using (GetRemotePlayersModels(out var remotePlayersModels))
        {
            foreach (var playerModel in remotePlayersModels.Select(player => player.Value))
            {
                if (playerModel.InGameNameText != null || playerModel.PlayerTransform == null)
                {
                    continue;
                }

                var sourceText = UIRoot.instance?.uiGame?.sailIndicator?.targetText;
                if (sourceText == null || GameCamera.main == null)
                {
                    continue;
                }

                var tagObject = new GameObject("Nebula Player Name Tag");
                tagObject.transform.SetParent(playerModel.PlayerTransform, false);
                var tag = tagObject.AddComponent<RemotePlayerNameTag>();
                if (tag.Initialize(playerModel, sourceText.font))
                {
                    playerModel.InGameNameText = tag.NameText;
                }
                else
                {
                    Object.Destroy(tagObject);
                }
            }
        }
    }

    public static void SetPauseIndicator(bool canPause)
    {
        //Tell the user if the game is paused or not
        var targetObject = GameObject.Find("UI Root/Overlay Canvas/In Game/Esc Menu/pause-text");
        if (targetObject == null)
        {
            return;
        }
        var pauseText = targetObject.GetComponent<Text>();
        var pauseLocalizer = targetObject.GetComponent<Localizer>();
        if (!pauseText || !pauseLocalizer)
        {
            return;
        }
        if (!canPause)
        {
            pauseText.text = "--  Nebula Multiplayer  --".Translate();
            pauseLocalizer.stringKey = "--  Nebula Multiplayer  --";
        }
        else
        {
            pauseText.text = "游戏已暂停".Translate();
            pauseLocalizer.stringKey = "游戏已暂停";
        }
    }

    public static int GetUniverseObserveLevel()
    {
        var level = 0;
        // the tech ids of the 4 tiers of Universe Exploration from https://dsp-wiki.com/Upgrades
        for (var i = 4104; i >= 4101; i--)
        {
            if (!GameMain.history.TechUnlocked(i))
            {
                continue;
            }
            // set level to last digit of tech id
            level = i % 10;
            break;
        }
        return level;
    }

    private sealed class ThreadSafe
    {
        internal readonly Dictionary<ushort, RemotePlayerModel> RemotePlayersModels = new();
    }
}
