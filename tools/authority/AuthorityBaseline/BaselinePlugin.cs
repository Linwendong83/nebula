using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using HarmonyLib;
using NebulaModel.Packets.Session;
using NebulaNetwork;
using NebulaWorld;
using UnityEngine;

namespace AuthorityBaseline;

/// <summary>
/// A00 baseline recorder. Loaded only by tools/authority/run-authority-instance.ps1 inside an
/// isolated portable game directory; it is never part of a release bundle.
///
/// Contract: observation only. This plugin does not change hp, repair counts, resources, task
/// state or spawns. The only mutations it performs are the explicit, logged fault-injection
/// commands from the control file (scenario driver) and the isolated per-instance player key.
/// </summary>
[BepInPlugin("nebula.tests.authority-baseline", "Authority baseline recorder", "1.0.0")]
[BepInDependency("dsp.nebula-multiplayer")]
public sealed class BaselinePlugin : BaseUnityPlugin
{
    private string role;
    private string controlDirectory;
    private string logDirectory;
    private string hostAddress = "127.0.0.1";
    private int hostPort = 28469;
    private bool verbose;
    private float lifetimeSeconds = 1800f;
    private bool startRequested;
    private bool goalAnswered;
    /// <summary>Set by the reconnect command so the join loop runs again after leaving.</summary>
    public static bool RejoinRequested;
    private float began;
    private float lastStatus;
    private float lastManifest;
    private float lastJoinAttempt;
    private int joinAttempts;
    private const float JoinRetrySeconds = 20f;
    private int errors;
    private string lastLogMessage = "";

    private void Awake()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i + 1 < args.Length; i++)
        {
            switch (args[i])
            {
                case "-authority-role": role = args[i + 1]; break;
                case "-authority-control": controlDirectory = args[i + 1]; break;
                case "-authority-log": logDirectory = args[i + 1]; break;
                case "-authority-host": hostAddress = args[i + 1]; break;
                case "-authority-port":
                    int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out hostPort);
                    break;
                case "-authority-verbose": verbose = true; break;
                case "-authority-lifetime":
                    if (i + 1 < args.Length &&
                        float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture,
                            out var lifetime) && lifetime > 0)
                    {
                        lifetimeSeconds = lifetime;
                    }
                    break;
            }
        }
        if (string.IsNullOrEmpty(role))
        {
            enabled = false;
            return;
        }
        role = role.Trim();
        controlDirectory ??= Path.Combine(Path.GetTempPath(), "authority-baseline", role);
        logDirectory ??= controlDirectory;
        Directory.CreateDirectory(controlDirectory);
        Directory.CreateDirectory(logDirectory);
        began = Time.realtimeSinceStartup;

        BaselineRuntime.Role = role;
        BaselineRuntime.LogDirectory = logDirectory;
        BaselineRuntime.Verbose = verbose;
        BaselineRuntime.RunId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture) + "-" + role;
        BaselineRuntime.Log = new BaselineLog(Path.Combine(logDirectory, role + ".jsonl"));
        BaselineRuntime.Log.MarkMainThread();

        BaselineScenarios.ControlDirectory = controlDirectory;
        BaselineScenarios.Role = role;

        var identity = new Harmony("nebula.tests.authority-identity");
        var identityTarget = AccessTools.Method(typeof(NebulaModel.Utils.CryptoUtils), nameof(NebulaModel.Utils.CryptoUtils.GetOrCreateUserCert));
        if (identityTarget != null)
        {
            identity.Patch(identityTarget, prefix: new HarmonyMethod(typeof(BaselinePlugin), nameof(IsolatedIdentity)));
        }
        BaselineHooks.Install();

        Application.logMessageReceived += OnLog;
        Logger.LogInfo("[authority] role=" + role + " run=" + BaselineRuntime.RunId +
                       " log=" + logDirectory + " hooksFailed=" + BaselineRuntime.HookFailures);
        WriteManifest();
        BaselineScenarios.WriteStatus();
    }

    /// <summary>
    /// Gives every isolated instance its own player key so three instances on one machine are
    /// three players instead of one identity reconnecting.
    /// </summary>
    private static bool IsolatedIdentity(ref RSA __result)
    {
        var path = Path.Combine(GameConfig.gameDocumentFolder, "authority-player.key");
        var rsa = RSA.Create();
        rsa.KeySize = 1024;
        if (File.Exists(path)) rsa.FromXmlString(File.ReadAllText(path));
        else File.WriteAllText(path, rsa.ToXmlString(true));
        __result = rsa;
        return false;
    }

    private void OnLog(string text, string stack, LogType type)
    {
        if (type != LogType.Exception && type != LogType.Error) return;
        errors++;
        lastLogMessage = text.Replace('\n', ' ');
    }

    private void Update()
    {
        try
        {
            if (Time.realtimeSinceStartup - began > lifetimeSeconds)
            {
                BaselineScenarios.Outcome = "timeout";
                BaselineScenarios.WriteStatus();
                Application.Quit();
                return;
            }
            HideFirstRunLanguagePicker();
            JoinIfNeeded();
            StartIfNeeded();
            AnswerGoalSelection();
            BaselineSampler.Tick();
            BaselineScenarios.Tick();
            if (Time.realtimeSinceStartup - lastStatus > 1f)
            {
                lastStatus = Time.realtimeSinceStartup;
                BaselineScenarios.WriteStatus();
            }
            if (Time.realtimeSinceStartup - lastManifest > 30f)
            {
                lastManifest = Time.realtimeSinceStartup;
                WriteManifest();
            }
        }
        catch (Exception error)
        {
            errors++;
            lastLogMessage = error.GetType().Name + ": " + error.Message.Replace('\n', ' ');
            BaselineScenarios.Outcome = "update-error:" + lastLogMessage;
            Logger.LogError("[authority] " + error);
        }
    }

    private void HideFirstRunLanguagePicker()
    {
        var splash = UIRoot.instance?.launchSplash;
        if (splash?.languageSelectGroup != null && splash.languageSelectGroup.gameObject.activeSelf)
        {
            splash.languageSelectGroup.gameObject.SetActive(false);
        }
    }

    private void JoinIfNeeded()
    {
        if (role == "host" || role == "uidump") return;
        // The reconnect command cleared the session; resume the normal retry loop.
        if (RejoinRequested)
        {
            RejoinRequested = false;
            lastJoinAttempt = 0f;
            goalAnswered = false;
            startRequested = false;
            Logger.LogInfo("[authority] rejoin requested; retrying connection");
        }
        // A dedicated host rejects connections until its world finishes loading, and the exact
        // completion time is not predictable from the client side. Retry until the session is
        // live instead of treating the first refusal as final.
        if (Time.realtimeSinceStartup - lastJoinAttempt < JoinRetrySeconds) return;
        // Once a session exists, stop retrying. Multiplayer.JoinGame calls DSPGame.EndGame()
        // when a world was already loaded, so a retry after the host accepted us would tear
        // down the world we just received.
        if (Multiplayer.IsActive)
        {
            RejoinRequested = false;
            return;
        }
        if (UIRoot.instance?.uiMainMenu?.active != true || !UIRoot.instance.launchSplash.willdone) return;
        // ClientSocket_OnOpen sends LobbyRequest with GameMain.data.account.userName. Joining
        // before that data exists makes the socket open throw and the connection never completes.
        if (GameMain.data?.account == null)
        {
            lastJoinAttempt = Time.realtimeSinceStartup;
            Logger.LogInfo("[authority] waiting for GameMain.data before joining");
            return;
        }
        lastJoinAttempt = Time.realtimeSinceStartup;
        joinAttempts++;
        Multiplayer.IsInMultiplayerMenu = true;
        Multiplayer.JoinGame(new Client(hostAddress, hostPort, "ws"));
        Logger.LogInfo("[authority] join attempt #" + joinAttempts + " -> " + hostAddress + ":" + hostPort);
    }

    /// <summary>
    /// A joining client whose goal level was never recorded for this server is shown the goal
    /// picker and stops loading until it is answered. Pick the middle level (Key) the same way
    /// the picker's own handler does, so the client can finish loading without a GUI.
    /// </summary>
    private void AnswerGoalSelection()
    {
        if (role == "host" || role == "uidump") return;
        if (goalAnswered) return;
        if (!Multiplayer.IsActive || Multiplayer.Session?.Goals == null) return;
        if (!Multiplayer.Session.Goals.AwaitingSelection) return;
        goalAnswered = true;
        Multiplayer.Session.Goals.SelectInitialLevel((int)EGoalLevel.Key, UIRoot.instance?.goalSetting);
        Logger.LogInfo("[authority] answered goal selection with Key");
    }

    private void StartIfNeeded()
    {
        if (role == "host" || role == "uidump") return;
        if (startRequested) return;
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsInLobby) return;
        startRequested = true;
        Multiplayer.Session.Network.SendPacket(new StartGameMessage());
        Logger.LogInfo("[authority] requested game start");
    }

    private void WriteManifest()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"harness\": \"AuthorityBaseline/1.0.0\",");
            sb.AppendLine("  \"runId\": \"" + BaselineRuntime.RunId + "\",");
            sb.AppendLine("  \"role\": \"" + role + "\",");
            sb.AppendLine("  \"gameTick\": " + BaselineRuntime.GameTick() + ",");
            sb.AppendLine("  \"isServer\": " + (BaselineRuntime.IsServer ? "true" : "false") + ",");
            sb.AppendLine("  \"isClient\": " + (BaselineRuntime.IsClient ? "true" : "false") + ",");
            sb.AppendLine("  \"players\": " + BaselineRuntime.PlayerCount() + ",");
            sb.AppendLine("  \"localPlanet\": " + (GameMain.localPlanet?.id ?? -1) + ",");
            sb.AppendLine("  \"birthPlanet\": " + (GameMain.galaxy?.birthPlanetId ?? -1) + ",");
            sb.AppendLine("  \"starCount\": " + (GameMain.galaxy?.starCount ?? -1) + ",");
            sb.AppendLine("  \"worldId\": " + (Multiplayer.IsActive ? SaveManager.WorldId : 0) + ",");
            sb.AppendLine("  \"sessionProtocolVersion\": " + NebulaModel.Networking.SessionProtocol.Version + ",");
            sb.AppendLine("  \"hooksFailed\": " + BaselineRuntime.HookFailures + ",");
            sb.AppendLine("  \"writeFailures\": " + BaselineRuntime.WriteFailures + ",");
            sb.AppendLine("  \"logErrors\": " + errors + ",");
            sb.AppendLine("  \"lastLogError\": \"" + Escape(lastLogMessage) + "\",");
            sb.AppendLine("  \"outcome\": \"" + Escape(BaselineScenarios.Outcome) + "\",");
            sb.AppendLine("  \"commandsExecuted\": " + BaselineScenarios.CommandsExecuted + ",");
            sb.AppendLine("  \"joinAttempts\": " + joinAttempts + ",");
            sb.AppendLine("  \"trackedObjects\": " + BaselineRuntime.TrackedSnapshot().Length);
            sb.AppendLine("}");
            File.WriteAllText(Path.Combine(logDirectory, role + "-manifest.json"), sb.ToString());
        }
        catch (Exception)
        {
            // diagnostic only
        }
    }

    private static string Escape(string value)
    {
        return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private void OnDestroy()
    {
        try
        {
            WriteManifest();
            BaselineScenarios.WriteStatus();
        }
        catch (Exception)
        {
            // diagnostic only
        }
        Application.logMessageReceived -= OnLog;
        BaselineRuntime.Log?.Dispose();
        BaselineRuntime.Log = null;
    }
}
