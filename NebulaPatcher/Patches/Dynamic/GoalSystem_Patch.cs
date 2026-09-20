using HarmonyLib;
using NebulaModel.Packets.GameStates;
using NebulaWorld;
using NebulaWorld.GameStates;

namespace NebulaPatcher.Patches.Dynamic;

[HarmonyPatch(typeof(GameScenarioLogic), nameof(GameScenarioLogic.Init))]
internal static class GoalLevelRestore_Patch
{
    [HarmonyPrefix]
    public static void Prefix(GameScenarioLogic __instance)
    {
        if (Multiplayer.IsActive) GoalManager.RestoreLevel(__instance.gameData);
    }
}

[HarmonyPatch(typeof(GoalLogic))]
internal static class GoalLogic_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(GoalLogic.InitDeterminator))]
    [HarmonyPatch(nameof(GoalLogic.GameTick))]
    [HarmonyPatch(nameof(GoalLogic.OnGoalStageChanged))]
    public static bool Authoritative_Prefix() => !Multiplayer.IsActive || Multiplayer.Session.IsServer;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(GoalLogic.CheckGoalLogicActive))]
    public static bool Active_Prefix(GoalLogic __instance, ref bool __result)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        // Retain the UI event bus even though clients do not run authoritative determinators.
        __result = __instance.isInit;
        return false;
    }
}

[HarmonyPatch(typeof(GoalSystem), nameof(GoalSystem.GetGoalDataById))]
internal static class PersonalGoalData_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(int _protoId, ref GoalData __result)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.Goals.CollectingPersonal) return true;
        __result = Multiplayer.Session.Goals.GetPersonalGoal(_protoId);
        return false;
    }
}

[HarmonyPatch(typeof(GoalData), nameof(GoalData.stage), MethodType.Setter)]
internal static class GoalStage_Patch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsServer) Multiplayer.Session.Goals.Dirty = true;
    }
}

[HarmonyPatch(typeof(DeterminatorBase), nameof(DeterminatorBase.GameTick))]
internal static class HeadlessGoalDeterminator_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(DeterminatorBase __instance)
    {
        return !Multiplayer.IsActive || !Multiplayer.Session.IsDedicated ||
            __instance is not GoalDeterminator || !GoalManager.PersonalDeterminators.Contains(__instance.GetType().Name);
    }
}

[HarmonyPatch(typeof(GoalDeterminator))]
internal static class HeadlessGoalLoad_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(GoalDeterminator.DetermineOnLoad))]
    [HarmonyPatch(nameof(GoalDeterminator.DetermineOnPatch))]
    public static bool Prefix(GoalDeterminator __instance) => !Multiplayer.IsActive || !Multiplayer.Session.IsDedicated ||
        !GoalManager.PersonalDeterminators.Contains(__instance.GetType().Name);
}

[HarmonyPatch(typeof(UIGoalSetting), nameof(UIGoalSetting.OnGoalButtonClick))]
internal static class SharedGoalSetting_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(UIGoalSetting __instance, int _data)
    {
        if (!Multiplayer.IsActive || __instance.galaxySelect.active || __instance.loadGameWindow.active) return true;
        Multiplayer.Session.Goals.Command(new GoalCommandPacket { ChangeLevel = true, Value = _data });
        __instance.CloseSettingWindow();
        return false;
    }
}

[HarmonyPatch(typeof(UIGoalGroupEntry), nameof(UIGoalGroupEntry.OnClickIgnoreButton))]
internal static class SharedGoalIgnore_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(UIGoalGroupEntry __instance)
    {
        if (!Multiplayer.IsActive) return true;
        Multiplayer.Session.Goals.Command(new GoalCommandPacket { Value = __instance.protoId });
        return false;
    }
}

[HarmonyPatch(typeof(GoalTools), nameof(GoalTools.ItemPlayerStorageCount))]
internal static class SharedGoalInventory_Patch
{
    [HarmonyPostfix]
    public static void Postfix(int _itemId, ref long __result)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsServer) return;
        if (Multiplayer.Session.IsDedicated) __result = 0;
        foreach (var player in Multiplayer.Session.Server.Players.Connected.Values)
        {
            var mecha = player.Data.Mecha;
            long count = mecha.Inventory?.GetItemCount(_itemId) ?? 0;
            if (mecha.DeliveryPackage?.grids != null)
                foreach (var grid in mecha.DeliveryPackage.grids)
                    if (grid.itemId == _itemId) count += grid.count;
            __result = System.Math.Max(__result, count);
        }
    }
}

[HarmonyPatch(typeof(GoalTools), nameof(GoalTools.GetMechaWarpStorage))]
internal static class SharedGoalWarpStorage_Patch
{
    [HarmonyPostfix]
    public static void Postfix(ref long __result)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsServer) return;
        if (Multiplayer.Session.IsDedicated) __result = 0;
        foreach (var player in Multiplayer.Session.Server.Players.Connected.Values)
            __result = System.Math.Max(__result, player.Data.Mecha.WarpStorage?.GetItemCount(1210) ?? 0);
    }
}
