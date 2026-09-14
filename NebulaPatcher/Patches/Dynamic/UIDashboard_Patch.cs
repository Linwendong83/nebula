#region

using System;
using System.IO;
using HarmonyLib;
using NebulaModel.Logger;
using NebulaModel.Packets.Players;
using NebulaWorld;

#endregion

namespace NebulaPatcher.Patches.Dynamic;

[HarmonyPatch]
internal class UIDashboard_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(UIDashboard), nameof(UIDashboard._OnClose))]
    public static void UIDashboard_OnClose_Postfix(UIDashboard __instance)
    {
        try
        {
            __instance.CollectStates();
        }
        catch (Exception e)
        {
            Log.Warn($"Failed to collect dashboard states: {e}");
        }
        SyncDashboardToServer();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CustomCharts), nameof(CustomCharts.CreateOrFindStatPlan))]
    public static void CustomCharts_CreateOrFindStatPlan_Postfix()
    {
        SyncDashboardToServer();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CustomCharts), nameof(CustomCharts.RemoveStatPlan))]
    public static void CustomCharts_RemoveStatPlan_Postfix()
    {
        SyncDashboardToServer();
    }

    public static void SyncDashboardToServer()
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost)
        {
            return;
        }
        if (GameMain.data?.statistics?.charts == null)
        {
            return;
        }

        try
        {
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms))
            {
                GameMain.data.statistics.charts.Export(writer);
            }
            var data = ms.ToArray();
            Multiplayer.Session.Network.SendPacket(new PlayerDashboardPacket(Multiplayer.Session.LocalPlayer.Id, data));
        }
        catch (Exception e)
        {
            Log.Warn($"Failed to sync dashboard to server: {e}");
        }
    }
}
