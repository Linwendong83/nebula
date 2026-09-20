using System;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using NebulaModel.DataStructures;
using NebulaModel.Packets.GameStates;
using NebulaModel.Packets.Session;
using NebulaNetwork;
using NebulaWorld;
using UnityEngine;

namespace RestorationSmoke;

// Loaded only by scripts/verify_restoration_runtime.ps1 in an isolated portable game directory.
[BepInPlugin("nebula.tests.restoration-smoke", "Restoration smoke driver", "1.0.0")]
[BepInDependency("dsp.nebula-multiplayer")]
public sealed class SmokePlugin : BaseUnityPlugin
{
    private string role;
    private string control;
    private bool joined;
    private bool started;
    private float began;
    private float reportTime;
    private string lastCommand = "";
    private string outcome = "idle";
    private int errors;
    private NebulaWorld.Combat.BattleVisualRenderer visualSample;
    private float visualUntil;
    private static int pendingKillStat;

    private void Awake()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "-restoration-role") role = args[i + 1];
            if (args[i] == "-restoration-control") control = args[i + 1];
        }
        if (role == null || control == null) { enabled = false; return; }
        Directory.CreateDirectory(control);
        began = Time.realtimeSinceStartup;
        new Harmony("nebula.tests.isolated-identity").Patch(
            AccessTools.Method(typeof(NebulaModel.Utils.CryptoUtils), nameof(NebulaModel.Utils.CryptoUtils.GetOrCreateUserCert)),
            prefix: new HarmonyMethod(typeof(SmokePlugin), nameof(IsolatedIdentity)));
        var statistics = new Harmony("nebula.tests.statistics-tick");
        statistics.Patch(AccessTools.Method(typeof(KillStatistics), nameof(KillStatistics.GameTick)),
            prefix: new HarmonyMethod(typeof(SmokePlugin), nameof(InjectKillStat)));
        statistics.Patch(AccessTools.Method(typeof(KillStatistics), nameof(KillStatistics.GameTick_Parallel)),
            prefix: new HarmonyMethod(typeof(SmokePlugin), nameof(InjectParallelKillStat)));
        Application.logMessageReceived += OnLog;
        Logger.LogInfo("[smoke] Started " + role + "; data root=" + GameConfig.gameDocumentFolder);
    }

    private void OnLog(string text, string stack, LogType type)
    {
        if (type == LogType.Exception || type == LogType.Error) errors++;
    }

    private static bool IsolatedIdentity(ref System.Security.Cryptography.RSA __result)
    {
        var path = Path.Combine(GameConfig.gameDocumentFolder, "smoke-player.key");
        var rsa = System.Security.Cryptography.RSA.Create();
        rsa.KeySize = 1024;
        if (File.Exists(path)) rsa.FromXmlString(File.ReadAllText(path));
        else File.WriteAllText(path, rsa.ToXmlString(true));
        __result = rsa;
        return false;
    }

    private static void InjectKillStat()
    {
        if (System.Threading.Interlocked.Exchange(ref pendingKillStat, 0) == 0) return;
        GameMain.statistics.kill.RegisterFactoryKillStat(GameMain.galaxy.PlanetById(GameMain.galaxy.birthPlanetId).factory.index, 400);
    }

    private static void InjectParallelKillStat(int threadOrdinal)
    {
        if (threadOrdinal == 0) InjectKillStat();
    }

    private void Update()
    {
        try
        {
            // A fresh isolated profile waits for the first-run language picker. Use its default language.
            if (UIRoot.instance?.launchSplash?.languageSelectGroup?.gameObject.activeSelf == true)
                UIRoot.instance.launchSplash.languageSelectGroup.gameObject.SetActive(false);
            if (Time.realtimeSinceStartup - began > 900) { outcome = "timeout"; Report(); Application.Quit(); return; }
            if (role != "server" && !joined && UIRoot.instance?.uiMainMenu?.active == true && UIRoot.instance.launchSplash.willdone)
            {
                joined = true;
                Multiplayer.IsInMultiplayerMenu = true;
                Multiplayer.JoinGame(new Client("127.0.0.1", 28469, "ws"));
                Logger.LogInfo("[smoke] Joining localhost");
            }
            if (role != "server" && Multiplayer.IsActive && Multiplayer.Session.IsInLobby && !started)
            {
                started = true;
                Multiplayer.Session.Network.SendPacket(new StartGameMessage());
            }
            var path = Path.Combine(control, role + ".command");
            if (File.Exists(path))
            {
                var command = File.ReadAllText(path).Trim();
                if (command != lastCommand && command.Length > 0)
                {
                    var words = command.Split(' ');
                    if (words[1] == "quit" || words[1] == "disconnect" || words[1] == "reconnect" ||
                        Multiplayer.IsActive && Multiplayer.Session.IsGameLoaded)
                    {
                        lastCommand = command;
                        Execute(words);
                    }
                    else outcome = "waiting:" + words[1];
                }
            }
            if (Time.realtimeSinceStartup - reportTime > 1) { reportTime = Time.realtimeSinceStartup; Report(); }
            if (visualSample != null)
            {
                visualSample.Draw();
                if (Time.realtimeSinceStartup >= visualUntil) { visualSample.Dispose(); visualSample = null; outcome = "ok:visual"; }
            }
        }
        catch (Exception error)
        {
            outcome = error.GetType().Name + ":" + error.Message.Replace('\n', ' ');
            Logger.LogError("[smoke] " + error);
            Report();
        }
    }

    private void Execute(string[] words)
    {
        began = Time.realtimeSinceStartup;
        var action = words[1];
        outcome = "ok:" + action;
        if (action == "quit") { Report(); Application.Quit(); return; }
        if (action == "disconnect") { Multiplayer.LeaveGame(); return; }
        if (action == "reconnect")
        {
            if (Multiplayer.IsActive) Multiplayer.LeaveGame();
            joined = started = false;
            return;
        }
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsGameLoaded) throw new InvalidOperationException("Game is not ready");
        switch (action)
        {
            case "peak":
                var manager = Multiplayer.Session.Metadata;
                var ledger = (MetadataLedger)AccessTools.Field(manager.GetType(), "ledger").GetValue(manager);
                var online = (System.Collections.Generic.HashSet<string>)AccessTools.Field(manager.GetType(), "online").GetValue(manager);
                ledger.Advance(new[] { int.Parse(words[2]), 0, 0, 0, 0, 0 }, online);
                AccessTools.Method(manager.GetType(), "Persist").Invoke(manager, null);
                break;
            case "wallet":
                var key = GameMain.data.GetClusterSeedKey() + 1000000000000L;
                foreach (var item in PropertySystem.matrixIds) DSPGame.propertySystem.SetItemProduction(key, item, 100000);
                NebulaWorld.GameStates.PropertyAccountStore.Save();
                break;
            case "items":
                GameMain.mainPlayer.TryAddItemToPackage(1101, 37, 0, false);
                Multiplayer.Session.Life.Publish();
                break;
            case "buy":
                var tech = LDB.techs.dataArray.First(x => x.Published && !GameMain.history.TechUnlocked(x.ID) &&
                    GameMain.history.HasPreTechUnlocked(x.ID) && x.itemArray.Length > 0 &&
                    x.itemArray.All(item => item.ID >= 6001 && item.ID <= 6006));
                GameMain.history.BuyoutTech(tech.ID);
                outcome = "buy:" + tech.ID;
                break;
            case "kill":
                GameMain.mainPlayer.Kill();
                break;
            case "reassemble":
                GameMain.mainPlayer.controller.actionDeath.selectedRespawnOption = PlayerAction_Death.kRespawnCostsCount;
                GameMain.mainPlayer.controller.actionDeath.Respawn(2);
                break;
            case "redeploy":
                GameMain.mainPlayer.controller.actionDeath.selectedRespawnOption = 0;
                GameMain.mainPlayer.controller.actionDeath.Respawn(3);
                break;
            case "goal":
                Multiplayer.Session.Goals.Command(new GoalCommandPacket { ChangeLevel = true, Value = int.Parse(words[2]) });
                break;
            case "stats":
                if (Multiplayer.Session.IsClient)
                    Multiplayer.Session.Network.SendPacket(new NebulaModel.Packets.Statistics.KillStatisticsRequest { Subscribe = true });
                break;
            case "damage":
                var factory = GameMain.galaxy.PlanetById(GameMain.galaxy.birthPlanetId).factory;
                var enemy = factory.enemyPool.First(x => x.id > 0);
                var target = new SkillTarget { id = enemy.id, astroId = factory.planetId, type = ETargetType.Enemy };
                var caster = new SkillTarget();
                GameMain.spaceSector.skillSystem.DamageObject(100000000, 1, ref target, ref caster);
                outcome = "damage:" + enemy.modelIndex;
                break;
            case "killstat":
                pendingKillStat = 1;
                break;
            case "save":
                GameSave.SaveCurrentGame("restoration-smoke");
                break;
            case "render":
                using (var renderer = new NebulaWorld.Combat.BattleVisualRenderer()) renderer.Draw();
                break;
            case "visual":
                visualSample?.Dispose();
                visualSample = new NebulaWorld.Combat.BattleVisualRenderer();
                visualUntil = Time.realtimeSinceStartup + 2;
                var frame = new BattleVisualFrame();
                var model = LDB.items.Select(5101).ModelIndex;
                frame.Units.Add(new FleetVisualData
                {
                    Id = 1,
                    Generation = 1,
                    Model = model,
                    Astro = GameMain.localPlanet.id,
                    Space = false,
                    Position = GameMain.mainPlayer.position + GameMain.mainPlayer.position.normalized * 8,
                    Rotation = Quaternion.identity,
                    Animation = new AnimData { time = 0.5f, power = 1, state = 1 }
                });
                frame.Units.Add(new FleetVisualData
                {
                    Id = 2,
                    Generation = 1,
                    Model = LDB.items.Select(5111).ModelIndex,
                    Astro = GameMain.localStar.astroId,
                    Space = true,
                    Position = GameMain.mainPlayer.uPosition - GameMain.localStar.uPosition,
                    Rotation = Quaternion.identity,
                    Animation = new AnimData { time = 0.5f, power = 1, state = 1 }
                });
                var upos = GameMain.mainPlayer.uPosition;
                AddEffect(frame, BattleEffectKind.SpaceLaser, w =>
                {
                    var effect = new SpaceLaserOneShot { id = 2, life = 120, beginPosU = upos, endPosU = upos + new VectorLF3(20, 0, 0) };
                    effect.Export(w);
                });
                AddEffect(frame, BattleEffectKind.TurretPlasma, w =>
                {
                    var effect = new GeneralProjectile { id = 3, life = 120, lifemax = 120, uPos = upos, uVel = new Vector3(1, 0, 0) };
                    effect.Export(w);
                });
                AddEffect(frame, BattleEffectKind.BomberProjectile, w =>
                {
                    var effect = new GeneralExpImpProjectile { id = 4, life = 120, lifemax = 120, uPos = upos, uVel = new Vector3(0, 1, 0) };
                    effect.Export(w);
                });
                AddEffect(frame, BattleEffectKind.LancerSweep, w =>
                {
                    var effect = new SpaceLaserSweep
                    {
                        id = 5,
                        life = 120,
                        lifemax = 120,
                        astroId = GameMain.localStar.astroId,
                        beginPos = upos - GameMain.localStar.uPosition,
                        endPos = upos - GameMain.localStar.uPosition + new VectorLF3(20, 0, 0)
                    };
                    effect.Export(w); w.Write(effect.endPos.x); w.Write(effect.endPos.y); w.Write(effect.endPos.z);
                });
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    var missile = new GeneralMissile
                    {
                        id = 1,
                        modelIndex = 431,
                        life = 1,
                        nearAstroId = GameMain.localPlanet.id,
                        uPos = GameMain.mainPlayer.uPosition,
                        uRot = Quaternion.identity,
                        uVel = new Vector3(0, 5, 0)
                    };
                    missile.Export(writer);
                    frame.Effects.Add(new BattleEffectData
                    { Kind = BattleEffectKind.TurretMissile, Id = 1, Generation = 1, Life = 180, Payload = stream.ToArray() });
                }
                visualSample.Receive((65000, false, GameMain.localStar.id, GameMain.localPlanet.id),
                    new NebulaModel.Packets.Combat.BattleVisualPacket { Tick = GameMain.gameTick, UnitsFull = true, EffectsFull = true }, frame);
                break;
        }
        Logger.LogInfo("[smoke] " + outcome);
    }

    private void Report()
    {
        var ready = Multiplayer.IsActive && Multiplayer.Session.IsGameLoaded && GameMain.data != null;
        var text = "ready=" + (ready ? "1" : "0") + "\nerrors=" + errors + "\nresult=" + outcome + "\n";
        text += "preload=" + (UIRoot.instance?.launchSplash?.progress ?? -1) + "\nmenu=" + (UIRoot.instance?.uiMainMenu?.active == true ? 1 : 0) + "\n";
        if (ready)
        {
            text += "tick=" + GameMain.gameTick + "\nbirth=" + GameMain.galaxy.birthPlanetId +
                "\nplanet=" + (GameMain.localPlanet?.id ?? -1) + "\ngoal=" + (int)GameMain.data.gameDesc.goalLevel +
                "\nalive=" + (GameMain.mainPlayer.isAlive ? 1 : 0) + "\ndeaths=" + GameMain.mainPlayer.deathCount +
                "\nblue=" + DSPGame.propertySystem.GetItemProduction(GameMain.data.GetClusterSeedKey(), 6001) +
                "\nspent=" + DSPGame.propertySystem.GetItemTotalConsumption(6001) +
                "\npackage=" + GameMain.mainPlayer.package.grids.Sum(x => (long)x.count) +
                "\ntrash=" + GameMain.data.trashSystem.trashCount +
                "\nkills=" + (Multiplayer.Session.IsServer ?
                    GameMain.statistics.kill.factoryKillStatPool.Where(x => x != null).Sum(x => x.killStatPool.Where(s => s != null).Sum(s => (long)s.total.Last())) :
                    Multiplayer.Session.Kills.RemotePlanets.Sum(id => Multiplayer.Session.Kills.GetRemote(id).killStatPool.Where(s => s != null).Sum(s => (long)s.total.Last()))) +
                "\nplayers=" + (Multiplayer.Session.IsServer ? Multiplayer.Session.Server.Players.Connected.Count : Multiplayer.Session.NumPlayers) +
                "\nworld=" + SaveManager.WorldId + "\n";
        }
        File.WriteAllText(Path.Combine(control, role + ".status"), text);
    }

    private static void AddEffect(BattleVisualFrame frame, BattleEffectKind kind, Action<BinaryWriter> export)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        export(writer);
        frame.Effects.Add(new BattleEffectData { Kind = kind, Id = (int)kind + 10, Generation = 1, Life = 120, Payload = stream.ToArray() });
    }

    private void OnDestroy()
    {
        visualSample?.Dispose();
        Application.logMessageReceived -= OnLog;
    }
}
