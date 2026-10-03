#region

using System;
using System.Collections.Generic;
using System.IO;
using NebulaAPI.GameState;
using NebulaModel.Authority;
using NebulaModel.DataStructures;
using NebulaModel.Logger;
using NebulaModel.Networking.Serialization;
using NebulaModel.Utils;
using NebulaWorld.Authority;

#endregion

namespace NebulaWorld;

public static class SaveManager
{
    private const string FILE_EXTENSION = ".server";
    private const ushort REVISION = 10;
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
    private static bool serverDataLoadFailed;
    private static bool authoritySidecarRejected;
    private static AuthoritySaveState authorityRestoreState;
    private static AuthoritySidecarDecision authorityDecision;
    private static string authorityDecisionReason;

    /// <summary>Latest sidecar decision of this process, for logging and tests.</summary>
    public static AuthoritySidecarDecision AuthorityDecision => authorityDecision;

    /// <summary>Why <see cref="AuthorityDecision"/> was reached. Always set after a load.</summary>
    public static string AuthorityDecisionReason => authorityDecisionReason;

    /// <summary>
    /// Takes the decoded sidecar pending restore, if the last load produced one. Consuming is
    /// one-shot: the world seeds from it exactly once, on its first load.
    /// </summary>
    public static bool TryConsumeAuthorityRestore(out AuthoritySaveState state)
    {
        state = authorityRestoreState;
        authorityRestoreState = null;
        return state != null && authorityDecision == AuthoritySidecarDecision.Restore;
    }

    public static bool CanSave => !serverDataLoadFailed && !authoritySidecarRejected;
    public static IReadOnlyDictionary<string, IPlayerData> PlayerSaves => playerSaves;

    public static void SaveServerData(string saveName)
    {
        if (serverDataLoadFailed)
            throw new InvalidOperationException("Multiplayer data could not be loaded; refusing to overwrite the save.");
        Multiplayer.Session.Kills.CapturePlayersForSave();
        Multiplayer.Session.Vegetation.CaptureRemoteForSave();
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
        hostData.VegetableCollectionData = VegetableCollectionState.Capture(GameMain.mainPlayer.vegetableCollection);
        Multiplayer.Session.LocalPlayer.Data.Serialize(netDataWriter);

        if (File.Exists(path) && !File.Exists(path + ".pre-v10")) File.Copy(path, path + ".pre-v10");
        AtomicFile.Write(path, netDataWriter.CopyData());

        // A21: the authority sidecar carries the host-only facts the vanilla save cannot — ledger
        // balances, drone budget capacities, and the live task table for the load transaction's
        // one-time reclaim audit. Written only by a host authority session, next to the .server
        // file it belongs to.
        WriteAuthoritySidecar(path, saveName);

        // If the saveName is the autoSave, we need to rotate the server autosave file.
        if (saveName == GameSave.AutoSaveTmp)
        {
            HandleAutoSave();
        }
    }

    private static void WriteAuthoritySidecar(string serverPath, string saveName)
    {
        var session = Multiplayer.Session;
        if (session?.AuthorityRuntime == null ||
            AuthorityLocalOptions.Mode != AuthorityMode.HostAuthority)
        {
            return;
        }
        var state = AuthoritySaveAdapter.Capture(session.AuthorityRuntime, WorldId);
        if (state == null)
        {
            Log.Info($"[authority] skipping sidecar for {saveName}: session is not a host authority world");
            return;
        }
        var bytes = AuthoritySaveCodec.Encode(state);
        if (bytes == null)
        {
            Log.Error($"[authority] refusing to write the sidecar for {saveName}: a record exceeded " +
                      "its sidecar ceiling; the save is left without one and the next load migrates legacy-style");
            return;
        }
        AtomicFile.Write(serverPath + AuthoritySaveAdapter.FileExtension, bytes);
        Log.Info($"[authority] sidecar written for {saveName} (players={state.Players.Count} " +
                 $"accounts={state.Accounts.Count} budgets={state.Budgets.Count} tasks={state.Tasks.Count})");
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
        serverDataLoadFailed = false;
        authoritySidecarRejected = false;
        authorityRestoreState = null;
        authorityDecision = AuthoritySidecarDecision.None;
        authorityDecisionReason = null;
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
                // Supported revision: 5~10. Revision 10 adds each player's vegetation collection.
                if (revision is < 5 or > REVISION)
                {
                    throw new Exception($"Unsupported version {revision}");
                }
            }

            if (revision >= 9) SetWorldId(netDataReader.GetString());
            if (revision < REVISION) BackupLegacySave(saveName);

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
            serverDataLoadFailed = true;
            playerSaves.Clear();
            Log.WarnInform("Multiplayer data load failed; saving is disabled to preserve the original file:\n".Translate() + e.Message);
            Log.Warn(e);
            return;
        }

        DecideAuthoritySidecar(path);
    }

    /// <summary>
    /// Reads the sidecar next to a successfully loaded <c>.server</c> file and decides what the
    /// host authority world will do with it (TASKS.md A21).
    /// </summary>
    /// <remarks>
    /// A refused sidecar disables saving the same way a failed multiplayer load does: overwriting
    /// a file this build cannot read — typically written by a newer schema — would destroy the
    /// only copy, so the refusal is durable until a person intervenes. The reason is always
    /// logged, so the refusal is explained rather than silent.
    /// </remarks>
    private static void DecideAuthoritySidecar(string serverPath)
    {
        var sidecarPath = serverPath + AuthoritySaveAdapter.FileExtension;
        var exists = File.Exists(sidecarPath);
        var decoded = false;
        AuthoritySaveState state = null;
        var decodeReason = null as string;
        if (exists)
        {
            try
            {
                var bytes = File.ReadAllBytes(sidecarPath);
                decoded = AuthoritySaveCodec.TryDecode(bytes, out state, out decodeReason);
            }
            catch (Exception e)
            {
                decoded = false;
                decodeReason = "sidecar file could not be read: " + e.Message;
            }
        }

        var authorityMode = AuthorityLocalOptions.Mode == AuthorityMode.HostAuthority;
        authorityDecision = AuthoritySavePolicy.Decide(authorityMode, exists, decoded, decodeReason,
            state?.WorldId, WorldId, out authorityDecisionReason);

        switch (authorityDecision)
        {
            case AuthoritySidecarDecision.Restore:
                authorityRestoreState = state;
                Log.Info("[authority] sidecar accepted: " + authorityDecisionReason);
                break;
            case AuthoritySidecarDecision.LegacyMigration:
                Log.Info("[authority] " + authorityDecisionReason);
                break;
            case AuthoritySidecarDecision.Refused:
                authoritySidecarRejected = true;
                Log.WarnInform("Authority sidecar refused; saving is disabled to preserve the original file:\n".Translate() +
                               authorityDecisionReason);
                Log.Warn("[authority] sidecar refused: " + authorityDecisionReason);
                break;
        }
    }

    private static void BackupLegacySave(string saveName)
    {
        var backupRoot = Path.Combine(GameConfig.gameSaveFolder, "Nebula", "compat-backups",
            Path.GetFileName(saveName) + ".pre-v10");
        var marker = Path.Combine(backupRoot, "complete");
        if (File.Exists(marker)) return;
        Directory.CreateDirectory(backupRoot);
        foreach (var extension in new[] { ".dsv", FILE_EXTENSION, ".server.world-id" })
        {
            var source = Path.Combine(GameConfig.gameSaveFolder, saveName + extension);
            if (File.Exists(source)) File.Copy(source, Path.Combine(backupRoot, Path.GetFileName(source)), true);
        }
        var worldFolder = Path.Combine(GameConfig.gameSaveFolder, "Nebula", WorldId);
        CopyDirectory(worldFolder, Path.Combine(backupRoot, "world"));
        var propertyRoot = Path.Combine(GameConfig.propertyFolder, "Nebula");
        if (Directory.Exists(propertyRoot))
        {
            foreach (var identityFolder in Directory.GetDirectories(propertyRoot))
            {
                var source = Path.Combine(identityFolder, WorldId + ".transactions");
                if (!File.Exists(source)) continue;
                var destination = Path.Combine(backupRoot, "properties", Path.GetFileName(identityFolder));
                Directory.CreateDirectory(destination);
                File.Copy(source, Path.Combine(destination, Path.GetFileName(source)), true);
            }
        }
        AtomicFile.Write(marker, System.Text.Encoding.UTF8.GetBytes("complete"));
        Log.Info($"Preserved pre-v10 multiplayer save in {backupRoot}");
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(file, target, true);
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
