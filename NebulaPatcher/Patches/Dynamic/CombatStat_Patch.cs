#region

using HarmonyLib;
using NebulaModel.Authority;
using NebulaWorld;

#endregion

namespace NebulaPatcher.Patches.Dynamic;

[HarmonyPatch(typeof(CombatStat))]
internal class CombatStat_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(CombatStat.HandleZeroHp))]
    public static bool HandleZeroHp_Prefix(ref CombatStat __instance)
    {
        // A05: in the new mode the client must not manufacture a death at all. The old path stood
        // the dying enemy up at 1 hp and asked the host to decide, which keeps two health models
        // alive; the new mode gets an absolute host value through the replica instead. The guard
        // returns the legacy decision in a legacy room, so this changes nothing there.
        if (!AuthorityRuleGuard.AllowHostRule(AuthorityHookLabels.CombatStatHandleZeroHp,
                detail: $"objectType={__instance.objectType} objectId={__instance.objectId}"))
        {
            return false;
        }

        // A22: HandleZeroHp is the vanilla commit where every damage-target class dies once —
        // kill statistics, then the per-kind KillXxxFinally. On the host in authority mode that
        // vanilla chain stays the rule (statistics and drops are vanilla-delegated); the capture
        // adds the authority bookkeeping: the death is identified here, where the pool slot is
        // provably still occupied, and the ledger commits the tombstone and the one construction-
        // task release at the frame boundary. The ledger dedups every later report of the same
        // generation. Single-player runs the vanilla path untouched.
        if (Multiplayer.IsActive &&
            Multiplayer.Session?.AuthorityRuntime?.IsHostAuthority == true)
        {
            Multiplayer.Session.AuthorityRuntime.HostDeathCapture?.Capture(
                __instance.originAstroId, __instance.objectType, __instance.objectId);
        }

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(CombatStat.TickSkillLogic))]
    public static bool TickSkillLogic_Prefix(ref CombatStat __instance)
    {
        // A05: a client that ran the vanilla tick would keep a second regen/death model alive. The
        // guard refuses the whole call outside a replica apply, so the client's health mirror can
        // only ever move because the host said so.
        if (!AuthorityRuleGuard.AllowHostRule(AuthorityHookLabels.CombatStatTickSkillLogic,
                detail: $"objectType={__instance.objectType} objectId={__instance.objectId}"))
        {
            return false;
        }

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(CombatStat.HandleFullHp))]
    public static bool HandleFullHp_Prefix(ref CombatStat __instance)
    {
        // A05: clearing the entity's combatStatId is what E06 turned into "a joining client wipes
        // the host's damage records". On a client that is a world write; the new mode only lets it
        // happen inside a replica apply, and A08 removes it from the host's factory-load path.
        if (!AuthorityRuleGuard.AllowHostRule(AuthorityHookLabels.CombatStatHandleFullHp,
                detail: $"objectType={__instance.objectType} objectId={__instance.objectId}"))
        {
            return false;
        }

        return true;
    }
}
