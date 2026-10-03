#region

using HarmonyLib;
using NebulaModel.Authority;
using NebulaWorld;
using NebulaWorld.Authority;

#endregion

namespace NebulaPatcher.Patches.Dynamic;

[HarmonyPatch(typeof(ConstructionSystem))]
internal class ConstructionSystem_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionSystem.UpdateDrones))]
    public static bool UpdateDrones(ConstructionSystem __instance, ObjectRenderer[] renderers, bool sync_gpu_inst, float dt, long time)
    {
        if (!AuthorityRuleGuard.AllowHostRule(AuthorityHookLabels.ConstructionSystemUpdateDrones))
        {
            // The client's own drone presentation comes from the host task batch (A17); letting the
            // vanilla loop run here would charge a local energy value for a shared repair.
            return false;
        }

        // A17: in host authority mode the vanilla motion/repair loop never runs on any peer.
        // The host executor owns dispatch, per-owner real-energy ratios and the single Repair
        // call; the client presents TaskBatch display only. Single-player and legacy fallback
        // rooms run vanilla as before (multiplayer authority world required).
        if (Multiplayer.IsActive && Multiplayer.Session?.AuthorityRuntime?.IsHostAuthority == true &&
            HostConstructionPolicy.ShouldSuppressVanillaDroneLoop(isHostAuthority: true))
        {
            return false;
        }

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionSystem.UpdateModules))]
    public static bool UpdateModules_Prefix(ConstructionSystem __instance, long time, float dt)
    {
        // A17/A18: the per-module build/repair drive is host work, and the "every drone is idle
        // so reset repairerCount" self-heal (E05) must not run on a client, because that reset is
        // exactly what hides the private drone pool from vanilla. The host executor replaces both
        // repair and build dispatch/execution and derives repairerCount from its ledger
        // (HostRepairerCountPolicy); prebuild intake from the real pools lands with the game
        // harness (A22). Until then the only safe new-mode decision is to keep the whole method
        // off on every peer. Legacy rooms run vanilla.
        if (!AuthorityRuleGuard.AllowHostRule(AuthorityHookLabels.ConstructionSystemUpdateModules))
        {
            return false;
        }
        // A17: in host authority mode the whole module drive is retired with the dispatch it
        // feeds. The host executor replaces repair dispatch/execution and derives repairerCount
        // from its ledger (HostRepairerCountPolicy); builds await A18. Single-player and legacy
        // fallback rooms run vanilla (multiplayer authority world required).
        if (Multiplayer.IsActive && Multiplayer.Session?.AuthorityRuntime?.IsHostAuthority == true &&
            HostConstructionPolicy.ShouldSuppressIdleReset(isHostAuthority: true))
        {
            return false;
        }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionSystem.Repair))]
    public static bool Repair_Prefix(int entityId)
    {
        // A00 found this never runs when the battle base has energy 0, and A17 has to compute the
        // ratio from the real owner energy. Writing building hp is a host fact either way.
        return AuthorityRuleGuard.AllowHostRule(AuthorityHookLabels.ConstructionSystemRepair, detail: $"entity={entityId}");
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionSystem.DetermineLaunch))]
    public static bool DetermineLaunch_Prefix()
    {
        // Vanilla dispatch has a single player and treats drone owner 0 as the host mecha, so on a
        // client it can only ever produce the wrong owner. A18 replaces the ownership decision.
        if (!AuthorityRuleGuard.AllowHostRule(AuthorityHookLabels.ConstructionSystemDetermineLaunch))
        {
            return false;
        }
        // A17: in host authority mode even the host must not run vanilla dispatch. It serves one
        // player, reserves the same idle slots the ledger owns and launches with the host view's
        // planet. The host executor is the sole dispatcher; single-player and legacy fallback
        // rooms run vanilla as before (multiplayer authority world required).
        if (Multiplayer.IsActive && Multiplayer.Session?.AuthorityRuntime?.IsHostAuthority == true &&
            HostConstructionPolicy.ShouldSuppressDetermineLaunch(isHostAuthority: true))
        {
            return false;
        }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionSystem.AddConstructStat))]
    public static bool AddConstructStat_Prefix(int entityId, int damage)
    {
        // Creating the damage record is what makes repair possible, so it is a host fact.
        // Vanilla names are (entityId, damage) in this game build; Harmony binds by name.
        return AuthorityRuleGuard.AllowHostRule(AuthorityHookLabels.ConstructionSystemAddConstructStat,
            detail: $"entity={entityId} damage={damage}");
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionSystem.RemoveConstructStat))]
    public static bool RemoveConstructStat_Prefix(int constructStatId)
    {
        return AuthorityRuleGuard.AllowHostRule(AuthorityHookLabels.ConstructionSystemRemoveConstructStat,
            detail: $"constructStat={constructStatId}");
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionSystem.AddBuildTargetToModules))]
    public static bool AddBuildTargetToModules_Prefix(ConstructionSystem __instance, int objectId)
    {
        if (!AuthorityRuleGuard.AllowHostRule(AuthorityHookLabels.ConstructionSystemAddBuildTargetToModules))
        {
            return false;
        }
        // A18: in host authority mode green prebuilds do not enter legacy claims. The host task
        // ledger owns build dispatch; intake from the real prebuild pool lands with the game
        // harness (A22). Until then this is fail-closed: swallow without queuing anywhere.
        // Single-player runs vanilla (multiplayer authority world required).
        if (Multiplayer.IsActive && Multiplayer.Session?.AuthorityRuntime?.IsHostAuthority == true &&
            HostConstructionPolicy.MustUseHostTaskForBuildDispatch(isHostAuthority: true))
        {
            return false;
        }
        if (!Multiplayer.IsActive) return true;
        Multiplayer.Session.BuildDispatch.ReadyLocally(__instance.factory, objectId);
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(ConstructionSystem.ResetDroneTargets))]
    public static void ResetDroneTargets_Postfix(ConstructionSystem __instance, ref DroneComponent drone)
    {
        if (!Multiplayer.IsActive) return;
        // A24: this postfix maintains the legacy dispatch claim, which the new mode neither reads nor
        // carries (A18/A24). The vanilla reset itself must still run — the host needs it for its own
        // drone pool — so the guard sits on the claim side only.
        if (!AuthorityRuleGuard.AllowLegacyPath(AuthorityHookLabels.ConstructionSystemResetDroneTargets,
                detail: $"planet={__instance.factory?.planetId} owner={drone.owner}"))
        {
            return;
        }
        if (drone.owner < 0 || drone.owner >= __instance.constructionModules.cursor) return;
        var module = drone.owner == 0 ? GameMain.mainPlayer.mecha.constructionModule :
            __instance.constructionModules.buffer[drone.owner];
        if (module == null) return;
        // Reset clears every carried target and then queues it again. Keep the claim while this
        // module still has the prebuild queued, so a finished drone does not hand it to someone else.
        ReleaseIfAbandoned(__instance, module, drone.targetObjectId);
        ReleaseIfAbandoned(__instance, module, drone.nextTarget1ObjectId);
        ReleaseIfAbandoned(__instance, module, drone.nextTarget2ObjectId);
        ReleaseIfAbandoned(__instance, module, drone.nextTarget3ObjectId);
    }

    private static void ReleaseIfAbandoned(ConstructionSystem system, ConstructionModuleComponent module, int objectId)
    {
        if (objectId >= 0 || ModuleStillTargets(module, -objectId)) return;
        Multiplayer.Session.BuildDispatch.ReleaseLocalTarget(system.factory, -objectId, module);
    }

    private static bool ModuleStillTargets(ConstructionModuleComponent module, int prebuildId)
    {
        var targets = module.buildTargets;
        if (targets == null) return false;
        for (var i = 0; i < targets.Length; i++)
            if (targets[i].objectId == prebuildId) return true;
        return false;
    }
}
