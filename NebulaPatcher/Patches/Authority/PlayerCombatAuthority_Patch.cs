using System;
using HarmonyLib;
using NebulaModel.Packets.Authority;
using NebulaWorld;

namespace NebulaPatcher.Patches.Authority;

[HarmonyPatch]
internal static class PlayerCombatAuthority_Patch
{
    private static bool Client => Multiplayer.IsActive && Multiplayer.Session.IsClient && Multiplayer.Session.Authority.IsActive;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StorageComponent), nameof(StorageComponent.NotifyStorageChange))]
    public static void PersonalStockChanged_Postfix(StorageComponent __instance)
    {
        if (Client) Multiplayer.Session.CombatAuthority.MarkPersonalStockChange(__instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerAction_Combat), nameof(PlayerAction_Combat.ShootTarget))]
    public static bool ShootTarget_Prefix(EAmmoType ammoType, SkillTarget target, ref bool __result)
    {
        if (!Client) return true;
        __result = Multiplayer.Session.CombatAuthority.Send(new CombatIntent(CombatIntentKind.Shoot, (int)ammoType), target);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerAction_Combat), nameof(PlayerAction_Combat.Bombing))]
    public static bool Bombing_Prefix(PlayerAction_Combat __instance, ref VectorLF3 initialVel)
    {
        if (!Client) return true;
        if (Multiplayer.Session.CombatAuthority.Send(new CombatIntent(CombatIntentKind.Bomb, x: initialVel.x, y: initialVel.y, z: initialVel.z)))
            __instance.mecha.bombFire = 10;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerAction_Combat), nameof(PlayerAction_Combat.ShieldBurst))]
    public static bool ShieldBurst_Prefix(PlayerAction_Combat __instance)
    {
        if (!Client) return true;
        Multiplayer.Session.CombatAuthority.Send(new CombatIntent(CombatIntentKind.ShieldBurst, x: __instance.mecha.energyShieldBurstProgress));
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Mecha), nameof(Mecha.UpdateCombatStats))]
    public static bool UpdateCombatStats_Prefix() => !Client;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Mecha), nameof(Mecha.TakeDamage))]
    public static bool TakeDamage_Prefix() => !Client;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.GameTick))]
    public static bool CombatModuleTick_Prefix() => !Client;

    public readonly struct AmmoPrediction
    {
        public AmmoPrediction(Mecha mecha)
        { Energy = mecha.coreEnergy; Bullets = mecha.ammoBulletCount; Laser = mecha.laserEnergy; }
        public double Energy { get; }
        public int Bullets { get; }
        public long Laser { get; }
    }
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Mecha), nameof(Mecha.TickAmmoFireCondition))]
    [HarmonyPatch(typeof(Mecha), nameof(Mecha.TickLaserFireCondition))]
    public static void FireClock_Prefix(Mecha __instance, out AmmoPrediction __state) => __state = new AmmoPrediction(__instance);

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Mecha), nameof(Mecha.TickAmmoFireCondition))]
    [HarmonyPatch(typeof(Mecha), nameof(Mecha.TickLaserFireCondition))]
    public static Exception FireClock_Finalizer(Mecha __instance, AmmoPrediction __state, Exception __exception)
    {
        if (Client)
        { __instance.coreEnergy = __state.Energy; __instance.ammoBulletCount = __state.Bullets; __instance.laserEnergy = __state.Laser; }
        return __exception;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Mecha), nameof(Mecha.LoadAmmo))]
    public static bool LoadAmmo_Prefix() => !Client;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.LaunchFleet))]
    public static bool LaunchFleet_Prefix(CombatModuleComponent __instance, int fleetIndex, Player player)
    {
        if (!Client) return true;
        if (player != GameMain.mainPlayer) return false;
        var argument = fleetIndex + (__instance == player.mecha.spaceCombatModule ? 100 : 0);
        Multiplayer.Session.CombatAuthority.Send(new CombatIntent(CombatIntentKind.FleetLaunch, argument));
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.RecycleFleet))]
    public static bool RecallFleet_Prefix(CombatModuleComponent __instance, int fleetIndex)
    {
        if (!Client) return true;
        var mecha = GameMain.mainPlayer.mecha;
        if (__instance != mecha.groundCombatModule && __instance != mecha.spaceCombatModule) return false;
        Multiplayer.Session.CombatAuthority.Send(new CombatIntent(CombatIntentKind.FleetRecall,
            fleetIndex + (__instance == mecha.spaceCombatModule ? 100 : 0)));
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.ChangeFleetConfig))]
    public static bool FleetConfig_Prefix(CombatModuleComponent __instance, int fleetIndex, int newConfigId)
    {
        if (!Client) return true;
        var mecha = GameMain.mainPlayer.mecha;
        if (__instance != mecha.groundCombatModule && __instance != mecha.spaceCombatModule) return false;
        Multiplayer.Session.CombatAuthority.Send(new CombatIntent(CombatIntentKind.FleetConfigure,
            fleetIndex + (__instance == mecha.spaceCombatModule ? 100 : 0), newConfigId));
        return false;
    }
}
