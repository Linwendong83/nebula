#region

using HarmonyLib;
using NebulaModel;
using NebulaModel.Logger;
using NebulaNetwork;
using NebulaWorld;

#endregion

namespace NebulaPatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UILoadGameWindow))]
internal class UILoadGameWindow_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UILoadGameWindow.DoLoadSelectedGame))]
    public static bool DoLoadSelectedGame_Prefix()
    {
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsClient) return false;
        if (Multiplayer.Session.IsGameLoaded)
        {
            Multiplayer.LeaveGame();
            Multiplayer.IsInMultiplayerMenu = true;
        }
        return true;
    }
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UILoadGameWindow.DoLoadSelectedGame))]
    public static void DoLoadSelectedGame_Postfix()
    {
        if (!Multiplayer.IsInMultiplayerMenu)
        {
            return;
        }
        Log.Info($"Listening server on port {Config.Options.HostPort}");
        Multiplayer.HostGame(new Server(Config.Options.HostPort, true));
    }
}
