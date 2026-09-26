#region

using HarmonyLib;
using NebulaModel;
using NebulaModel.Logger;
using NebulaModel.Utils;
using NebulaWorld;
using NebulaWorld.GameStates;

#endregion

namespace NebulaPatcher.Patches.Dynamic;

[HarmonyPatch(typeof(VFPreload))]
internal class VFPreload_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(VFPreload), nameof(VFPreload.InvokeOnLoad))]
    public static void InvokeOnLoad_Postfix()
    {
        if (!Multiplayer.IsDedicated)
        {
            return;
        }
        VFAudio.audioVolume = 0f;
        NativeInterop.HideWindow();
        NativeInterop.SetConsoleCtrlHandler();
        // Logging to provide progression to user
        Log.Info($"Loading game version {GameConfig.gameVersion.ToFullString()}");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(VFPreload), nameof(VFPreload.InvokeOnLoadHalf))]
    public static void InvokeOnLoadHalf_Postfix()
    {
        if (Multiplayer.IsDedicated)
        {
            Log.Info("VFPreload.InvokeOnLoadHalf");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(VFPreload), nameof(VFPreload.Start))]
    public static void Start_Postfix(VFPreload __instance)
    {
        if (!Multiplayer.IsDedicated)
        {
            return;
        }
        // The splash screen waits for the language selection panel to fade out, which requires a click.
        // Nothing can click it in headless mode, so the preload coroutine would never finish.
        if (__instance.languageSelectGroup != null)
        {
            __instance.languageSelectGroup.gameObject.SetActive(false);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(VFPreload), nameof(VFPreload.IsMenuDemoLoaded))]
    public static bool IsMenuDemoLoaded_Prefix(ref bool __result)
    {
        if (!Multiplayer.IsDedicated)
        {
            return true;
        }
        // Dedicated_Server_Patches skips StartDemoGame, so there is no demo to wait for.
        __result = true;
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(VFPreload.InvokeOnLoadWorkEnded))]
    public static void InvokeOnLoadWorkEnded_Postfix()
    {
        if (GameStatesManager.DuringReconnect)
        {
            var connection = Multiplayer.LastConnection;
            if (connection != null)
            {
                var record = ServerMemoryStore.Instance.FindServer(connection.RecordId);
                UIMainMenu_Patch.JoinGame(record?.Address ?? connection.Address,
                    !string.IsNullOrEmpty(record?.Password) ? record.Password : connection.Password, record?.Id);
            }
            GameStatesManager.DuringReconnect = false;
        }

        if (!Multiplayer.IsDedicated)
        {
            return;
        }
        if (Config.CommandLineOptions.ShouldLoadGame)
        {
            NebulaPlugin.StartDedicatedServer(Config.CommandLineOptions.SaveName);
        }
        else if (Config.CommandLineOptions.ShouldCreateNewGame)
        {
            NebulaPlugin.StartDedicatedServer(Config.CommandLineOptions.NewGameDesc);
        }
        else
        {
            Log.Warn("No game start option provided!");
        }
    }
}
