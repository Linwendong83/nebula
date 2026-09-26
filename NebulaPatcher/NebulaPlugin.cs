#region

using System;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using NebulaAPI.Interfaces;
using NebulaModel.Logger;
using NebulaNetwork;
using NebulaPatcher.Logger;
using NebulaPatcher.MonoBehaviours;
using NebulaPatcher.Patches.Dynamic;
using NebulaPatcher.Patches.Misc;
using NebulaWorld;
using UnityEngine;

#endregion

namespace NebulaPatcher;

[BepInPlugin(PluginInfo.PLUGIN_ID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
[BepInDependency("dsp.common-api.CommonAPI", BepInDependency.DependencyFlags.SoftDependency)]
public class NebulaPlugin : BaseUnityPlugin, IMultiplayerMod
{
    private bool gameBuildChecked;
    private void Awake()
    {
        Log.Init(new BepInExLogger(Logger));

        NebulaModel.Config.ModInfo = Info;
        NebulaModel.Config.LoadOptions();
        NebulaModel.Config.LoadCommandLineOptions();
        if (!NebulaModel.Config.CommandLineOptions.VerifyStartupRequirements())
        {
            Application.Quit();
        }
        Multiplayer.IsDedicated = NebulaModel.Config.CommandLineOptions.IsDedicatedServer;

        try
        {
            Initialize();
        }
        catch (Exception ex)
        {
            Log.Error("Unhandled exception occurred while initializing Nebula:", ex);
            enabled = false;
            if (Multiplayer.IsDedicated) Application.Quit(1);
        }
    }

    private void Update()
    {
        CheckLoadedGameBuild();
    }

    private void CheckLoadedGameBuild()
    {
        if (gameBuildChecked || GameConfig.gameVersion.Build == 0) return;
        gameBuildChecked = true;
        if (VerifyGameBuild()) return;
        enabled = false;
        if (Multiplayer.IsDedicated) Application.Quit(1);
    }

    private static bool VerifyGameBuild()
    {
        var runningVersion = GameConfig.gameVersion.ToFullString();
        if (string.Equals(runningVersion, DSPGameVersion.VERSION, StringComparison.Ordinal))
        {
            Multiplayer.ProtocolReady = true;
            Log.Info($"Verified Dyson Sphere Program {runningVersion}; multiplayer protocol enabled.");
            return true;
        }
        Multiplayer.ProtocolReady = false;
        new Harmony(PluginInfo.PLUGIN_ID).UnpatchSelf();
        Log.Error($"Unsupported game version {runningVersion}; expected {DSPGameVersion.VERSION}. Multiplayer is disabled.");
        return false;
    }

    public string Version => PluginInfo.PLUGIN_DISPLAY_VERSION;

    public bool CheckVersion(string hostVersion, string clientVersion)
    {
        if (string.Equals(hostVersion, clientVersion, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string Normalize(string ver)
        {
            if (string.IsNullOrEmpty(ver)) return string.Empty;
            int plusIndex = ver.IndexOf('+');
            return plusIndex >= 0 ? ver.Substring(0, plusIndex).Trim() : ver.Trim();
        }

        string normHost = Normalize(hostVersion);
        string normClient = Normalize(clientVersion);

        if (string.Equals(normHost, normClient, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        bool hostIsDev = normHost.Contains("-dev");
        bool clientIsDev = normClient.Contains("-dev");

        if (hostIsDev != clientIsDev)
        {
            Log.Warn($"[Nebula] Connection rejected due to mod version mismatch: Host ({hostVersion}) and Client ({clientVersion}). Official and Fork-dev versions cannot play together due to custom synchronization features.");
            return false;
        }

        Log.Warn($"[Nebula] Connection rejected due to mod version mismatch: Host ({hostVersion}) vs Client ({clientVersion}).");
        return false;
    }

    private static void Initialize()
    {
        InitPatches();
        AddNebulaBootstrapper();
    }

    public static void StartDedicatedServer(string saveName)
    {
        if (!VerifyGameBuild()) { Application.Quit(1); return; }
        if (!ValidateDedicatedGpu()) return;
        // Mimic UI buttons clicking
        UIMainMenu_Patch.OnMultiplayerButtonClick();
        if (!GameSave.SaveExist(saveName))
        {
            return;
        }
        // Modified from DoLoadSelectedGame
        Log.Info($"Starting dedicated server, loading save : {saveName}");
        DSPGame.StartGame(saveName);
        Log.Info($"Listening server on port {NebulaModel.Config.Options.HostPort}");
        Multiplayer.HostGame(new Server(NebulaModel.Config.Options.HostPort, true));
        FPSController.SetFixUPS(NebulaModel.Config.CommandLineOptions.UpsValue);
    }

    public static void StartDedicatedServer(GameDesc gameDesc)
    {
        if (!VerifyGameBuild()) { Application.Quit(1); return; }
        if (!ValidateDedicatedGpu()) return;
        // Mimic UI buttons clicking
        UIMainMenu_Patch.OnMultiplayerButtonClick();
        if (gameDesc == null)
        {
            return;
        }
        // Modified from DoLoadSelectedGame
        Log.Info("Starting dedicated server, create new game from parameters:");
        Log.Info(
            $"seed={gameDesc.galaxySeed} starCount={gameDesc.starCount} resourceMultiplier={gameDesc.resourceMultiplier:F1}");
        DSPGame.StartGameSkipPrologue(gameDesc);
        Log.Info($"Listening server on port {NebulaModel.Config.Options.HostPort}");
        Multiplayer.HostGame(new Server(NebulaModel.Config.Options.HostPort, false));
        FPSController.SetFixUPS(NebulaModel.Config.CommandLineOptions.UpsValue);
    }

    private static bool ValidateDedicatedGpu()
    {
        try
        {
            if (!HeadlessShieldPipeline.Ready) throw new InvalidOperationException("Shield backend patches are incomplete; refusing to start.");
            HeadlessShieldBackend.Initialize();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("[headless] Could not initialize the configured shield backend.", ex);
            Application.Quit(1);
            return false;
        }
    }

    private static void InitPatches()
    {
        try
        {
            var runningVersion = GameConfig.gameVersion;
            if (runningVersion.Major != 0 || runningVersion.Minor != 10 || runningVersion.Release != 35)
                throw new InvalidOperationException($"Unsupported game version {runningVersion.ToFullString()}; expected {DSPGameVersion.VERSION}.");
            Log.Info("Patching Dyson Sphere Program...");
            Log.Info($"Applying patches from {PluginInfo.PLUGIN_NAME} {PluginInfo.PLUGIN_DISPLAY_VERSION} made for game version {DSPGameVersion.VERSION}");
            Log.BeginPatchAudit();
            var timer = new HighStopwatch();
            timer.Begin();
#if DEBUG
            if (Directory.Exists("./mmdump"))
            {
                foreach (var file in new DirectoryInfo("./mmdump").GetFiles())
                {
                    file.Delete();
                }

                Environment.SetEnvironmentVariable("MONOMOD_DMD_TYPE", "cecil");
                Environment.SetEnvironmentVariable("MONOMOD_DMD_DUMP", "./mmdump");
            }
#endif
            var harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), PluginInfo.PLUGIN_ID);
            harmony.PatchAll(typeof(Fix_Patches));
            if (Multiplayer.IsDedicated)
            {
                Log.Info("Patching for headless mode...");
                harmony.PatchAll(typeof(Dedicated_Server_Patches));
                harmony.PatchAll(typeof(HeadlessShieldPipeline));
                harmony.PatchAll(typeof(Headless_Steam_Patches));
                Headless_Steam_Patches.Apply(harmony);
            }
            if (Log.PatchDiagnosticCount != 0)
                throw new InvalidOperationException($"{Log.PatchDiagnosticCount} patch compatibility diagnostics were emitted; multiplayer startup is disabled.");
#if DEBUG
            Environment.SetEnvironmentVariable("MONOMOD_DMD_DUMP", "");
#endif

            Log.Info("Patching completed successfully. Time cost: " + timer.duration);
            Multiplayer.ProtocolReady = false; // GlobalObject.ReadVersionList loads the exact build later.
        }
        catch (Exception ex)
        {
            Log.Error("Unhandled exception occurred while patching the game:", ex);
            Multiplayer.ProtocolReady = false;
            new Harmony(PluginInfo.PLUGIN_ID).UnpatchSelf();
            // Show error in UIFatalErrorTip to inform normal users
            Harmony.CreateAndPatchAll(typeof(UIFatalErrorTip_Patch));
            Log.Error($"Nebula Multiplayer Mod is incompatible with game version, expected version {DSPGameVersion.VERSION}\nUnhandled exception occurred while patching the game.");
            throw new InvalidOperationException("Nebula's protocol patches were not installed completely; multiplayer startup is disabled.", ex);
        }
        finally { Log.EndPatchAudit(); }
    }

    private static void AddNebulaBootstrapper()
    {
        Log.Info("Applying Nebula Behaviors..");

        var nebulaRoot = new GameObject { name = "Nebula Multiplayer Mod" };
        nebulaRoot.AddComponent<NebulaBootstrapper>();

        Log.Info("Behaviors applied.");
    }
}
