#region

using System;
using System.Collections.Generic;
using System.IO;
using NebulaAPI.GameState;
using NebulaModel.DataStructures;
using NebulaModel.Logger;
using NebulaModel.Networking.Serialization;
using NebulaModel.Utils;

#endregion

namespace NebulaWorld;

public static class SaveManager
{
    private const string FILE_EXTENSION = ".server";
    private const ushort REVISION = 9;
    public static string WorldId { get; private set; } = Guid.NewGuid().ToString("N");

    public static void SetWorldId(string worldId)
    {
        if (!Guid.TryParseExact(worldId, "N", out _)) throw new InvalidDataException("Invalid multiplayer world ID");
        WorldId = worldId;
    }

    public static void BindWorldIdentity(GameData data)
    {
        const int first = -1708469020;
        const int tag = -1708469024;
        const int magic = 0x4e42574c;
        var values = data.history.featureValues;
        if (values.TryGetValue(tag, out var marker) && marker == magic)
        {
            var bytes = new byte[16];
            for (var i = 0; i < 4; i++)
            {
                if (!values.TryGetValue(first - i, out var part)) throw new InvalidDataException("Incomplete multiplayer world identity");
                Array.Copy(BitConverter.GetBytes(part), 0, bytes, i * 4, 4);
            }
            WorldId = new Guid(bytes).ToString("N");
            return;
        }
        var identity = Guid.ParseExact(WorldId, "N").ToByteArray();
        for (var i = 0; i < 4; i++) values[first - i] = BitConverter.ToInt32(identity, i * 4);
        values[tag] = magic;
    }

    private static readonly Dictionary<string, IPlayerData> playerSaves = new();
    private static string pendingLoadFileName;
    private static bool pendingLoadSaveFile;
    private static bool hasPendingLoad;
    public static IReadOnlyDictionary<string, IPlayerData> PlayerSaves => playerSaves;

    public static void SaveServerData(string saveName)
    {
        Multiplayer.Session.Kills.CapturePlayersForSave();
        var path = GameConfig.gameSaveFolder + saveName + FILE_EXTENSION;
        // var playerManager = Multiplayer.Session.Network.PlayerManager;
        var netDataWriter = new NetDataWriter();
        netDataWriter.Put("REV");
        netDataWriter.Put(REVISION);
        netDataWriter.Put(WorldId);

        netDataWriter.Put(playerSaves.Count + 1);
        //Add data about all players
        foreach (var data in playerSaves)
        {
            var hash = data.Key;
            netDataWriter.Put(hash);
            data.Value.Serialize(netDataWriter);
        }

        Log.Info(
            $"Saving server data to {saveName + FILE_EXTENSION}, Revision:{REVISION} PlayerCount:{playerSaves.Count}");

        //Add host's data
        netDataWriter.Put(CryptoUtils.GetCurrentUserPublicKeyHash());
        var hostData = (PlayerData)Multiplayer.Session.LocalPlayer.Data;
        hostData.Life = PlayerLifeData.Capture(GameMain.mainPlayer, hostData.Life.Revision, hostData.Life.TransactionId);
        Multiplayer.Session.LocalPlayer.Data.Serialize(netDataWriter);

        if (File.Exists(path) && !File.Exists(path + ".pre-v9")) File.Copy(path, path + ".pre-v9");
        AtomicFile.Write(path, netDataWriter.CopyData());

        // If the saveName is the autoSave, we need to rotate the server autosave file.
        if (saveName == GameSave.AutoSaveTmp)
        {
            HandleAutoSave();
        }
    }

    private static void HandleAutoSave()
    {
        var str1 = GameConfig.gameSaveFolder + GameSave.AutoSaveTmp + FILE_EXTENSION;
        var str2 = GameConfig.gameSaveFolder + GameSave.AutoSave0 + FILE_EXTENSION;
        var str3 = GameConfig.gameSaveFolder + GameSave.AutoSave1 + FILE_EXTENSION;
        var str4 = GameConfig.gameSaveFolder + GameSave.AutoSave2 + FILE_EXTENSION;
        var str5 = GameConfig.gameSaveFolder + GameSave.AutoSave3 + FILE_EXTENSION;

        if (!File.Exists(str1))
        {
            return;
        }

        if (File.Exists(str5))
        {
            File.Delete(str5);
        }

        if (File.Exists(str4))
        {
            File.Move(str4, str5);
        }

        if (File.Exists(str3))
        {
            File.Move(str3, str4);
        }

        if (File.Exists(str2))
        {
            File.Move(str2, str3);
        }

        File.Move(str1, str2);
    }

    public static void LoadServerData(bool loadSaveFile)
    {
        // A dedicated server opens its session before the world is created, so GameMain.data is still
        // null here and player data cannot be deserialized (CombatModuleComponent.Init needs GameData).
        // Remember the request and finish it in EnsureServerDataLoaded() once the world exists.
        // GameData.Import() resets DSPGame.LoadFile to "" while reading the save, so capture the name
        // now instead of relying on it later.
        if (loadSaveFile && GameMain.data == null)
        {
            pendingLoadFileName = DSPGame.LoadFile;
            pendingLoadSaveFile = true;
            hasPendingLoad = true;
            return;
        }

        LoadServerDataNow(loadSaveFile, DSPGame.LoadFile);
    }

    // Completes a LoadServerData() call that had to wait for the world to be created.
    public static void EnsureServerDataLoaded()
    {
        if (!hasPendingLoad || GameMain.data == null)
        {
            return;
        }

        hasPendingLoad = false;
        LoadServerDataNow(pendingLoadSaveFile, pendingLoadFileName);
    }

    private static void LoadServerDataNow(bool loadSaveFile, string saveName)
    {
        playerSaves.Clear();
        WorldId = Guid.NewGuid().ToString("N");

        if (!loadSaveFile)
        {
            return;
        }
        var path = GameConfig.gameSaveFolder + saveName + FILE_EXTENSION;
        var identityPath = path + ".world-id";
        if (File.Exists(identityPath)) SetWorldId(System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(identityPath)));
        else AtomicFile.Write(identityPath, System.Text.Encoding.UTF8.GetBytes(WorldId));
        if (!File.Exists(path))
        {
            Log.Info($"No server file");
            return;
        }

        try
        {
            var source = File.ReadAllBytes(path);
            var netDataReader = new NetDataReader(source);
            ushort revision;

            var revString = netDataReader.GetString();
            if (revString != "REV")
            {
                throw new Exception("Incorrect header");
            }

            revision = netDataReader.GetUShort();
            Log.Info($"Loading server data revision {revision} (Latest {REVISION})");
            if (revision != REVISION)
            {
                // Supported revision: 5~9. Revision 8 uses the full current MechaData layout.
                if (revision is < 5 or > REVISION)
                {
                    throw new Exception($"Unsupported version {revision}");
                }
            }

            if (revision >= 9) SetWorldId(netDataReader.GetString());

            var playerNum = netDataReader.GetInt();


            for (var i = 0; i < playerNum; i++)
            {
                var hash = netDataReader.GetString();
                PlayerData playerData = null;
                switch (revision)
                {
                    case REVISION:
                        playerData = netDataReader.Get(() => new PlayerData());
                        break;
                    case >= 5:
                        playerData = new PlayerData();
                        playerData.Import(netDataReader, revision);
                        break;
                }

                if (!playerSaves.ContainsKey(hash) && playerData != null)
                {
                    playerData.PersistentId = hash;
                    playerSaves.Add(hash, playerData);
                }
                else if (playerData == null)
                {
                    Log.Warn($"Could not load player data from unsupported save file revision {revision}");
                }
            }
        }
        catch (Exception e)
        {
            playerSaves.Clear();
            Log.WarnInform("Skipping server data due to exception:\n".Translate() + e.Message);
            Log.Warn(e);
            return;
        }
    }

    public static bool TryAdd(string clientCertHash, IPlayerData playerData)
    {
        ((PlayerData)playerData).PersistentId = clientCertHash;
        if (playerSaves.ContainsKey(clientCertHash))
        {
            return false;
        }

        playerSaves.Add(clientCertHash, playerData);
        return true;
    }

    public static bool TryRemove(string clientCertHash)
    {
        return playerSaves.Remove(clientCertHash);
    }
}
