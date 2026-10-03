#region

using System.IO;
using HarmonyLib;
using NebulaModel.Authority;
using NebulaWorld;

#endregion

namespace NebulaPatcher.Patches.Dynamic;

[HarmonyPatch(typeof(SkillSystem))]
internal class SkillSystem_Patch
{
    [System.ThreadStatic] private static int damageObjectDepth;
    [HarmonyPrefix]
    [HarmonyPatch(nameof(SkillSystem.Export))]
    public static bool Export_Prefix(SkillSystem __instance, BinaryWriter w)
    {
        if (!NebulaWorld.Combat.CombatManager.SerializeOverwrite) return true;

        w.Write(3); // version 3
        __instance.combatStats.Export(w);
        w.Write(__instance.removedSkillTargets.Count);
        foreach (var skillTarget in __instance.removedSkillTargets)
        {
            w.Write(skillTarget.id);
            w.Write(skillTarget.astroId);
            w.Write((int)skillTarget.type);
        }
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(SkillSystem.Import))]
    public static bool Import_Prefix(SkillSystem __instance, BinaryReader r)
    {
        if (!NebulaWorld.Combat.CombatManager.SerializeOverwrite) return true;

        _ = r.ReadInt32();
        __instance.combatStats.Import(r);
        var count = r.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            SkillTarget skillTarget;
            skillTarget.id = r.ReadInt32();
            skillTarget.astroId = r.ReadInt32();
            skillTarget.type = (ETargetType)r.ReadInt32();
            __instance.removedSkillTargets.Add(skillTarget);
        }
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(SkillSystem.CollectPlayerStates))]
    public static void CollectPlayerStates_Postfix(SkillSystem __instance)
    {
        if (!Multiplayer.IsActive) return;

        // Set those flags to false so AddSpaceEnemyHatred can add threat correctly for client's skill in host
        __instance.playerIsSailing = false;
        __instance.playerIsWarping = false;
        // Set this flag to true so AddSpaceEnemyHatred can add threat correctly from craft/skill of other players even if host is dead (dedicated server)
        __instance.playerAlive = true;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(SkillSystem.AfterTick))]
    public static void AfterTick_Postfix(SkillSystem __instance)
    {
        if (!Multiplayer.IsActive) return;

        // Restore the modified player states
        __instance.CollectPlayerStates();
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(SkillSystem.MechaEnergyShieldResist),
        [typeof(SkillTarget), typeof(int)],
        [ArgumentType.Normal, ArgumentType.Ref])]
    [HarmonyPatch(nameof(SkillSystem.MechaEnergyShieldResist),
        [typeof(SkillTargetLocal), typeof(int), typeof(int)],
        [ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Ref])]
    public static bool MechaEnergyShieldResist_Prefix(SkillSystem __instance, ref bool __result, ref int damage)
    {
        if (__instance.mecha == GameMain.mainPlayer.mecha) return true;

        damage = 0;
        __result = true;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(SkillSystem.DamageObject))]
    public static bool DamageObject_Prefix(ref int damage, int slice, ref SkillTarget target, ref SkillTarget caster, out bool __state)
    {
        // A05 installs the decision once, for all three damage entries. A client may keep computing
        // damage as a local prediction, but it may not hand that number to the shared world: the
        // guard refuses the vanilla damage call outside a replica apply, and the old
        // damage-packet path underneath is only reachable in a legacy room.
        __state = Multiplayer.IsActive;
        if (!AuthorityRuleGuard.AllowHostRule(AuthorityHookLabels.SkillSystemDamageObject,
                detail: $"target={target.type}:{target.id} caster={caster.type}:{caster.id}"))
        {
            return false;
        }

        if (__state) damageObjectDepth++;
        // Only player/craft casters dealing damage to enemies reach the shared rule; everything
        // else (including replica-applied facts) runs vanilla.
        if (!(caster.type == ETargetType.Craft || caster.type == ETargetType.Player)
            || target.type != ETargetType.Enemy
            || !Multiplayer.IsActive || Multiplayer.Session.Combat.IsIncomingRequest.Value) return true;

        return true;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(nameof(SkillSystem.DamageObject))]
    public static System.Exception DamageObject_Finalizer(System.Exception __exception, bool __state)
    {
        if (__state) damageObjectDepth--;
        return __exception;
    }

    private static bool OwnsCraft(int astro, int id)
    {
        var ground = astro > 100 && astro <= 204899 && astro % 100 != 0;
        var module = ground ? GameMain.mainPlayer.mecha.groundCombatModule : GameMain.mainPlayer.mecha.spaceCombatModule;
        if (module?.moduleFleets == null) return false;
        foreach (var fleet in module.moduleFleets)
        {
            if (fleet.fighters == null) continue;
            foreach (var fighter in fleet.fighters)
            {
                if (fighter.craftId == id) return true;
            }
        }
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(SkillSystem.DamageGroundObjectByLocalCaster))]
    public static bool DamageGroundObjectByLocalCaster_Prefix(PlanetFactory factory, ref int damage, int slice, ref SkillTargetLocal target, ref SkillTargetLocal caster)
    {
        if (!AuthorityRuleGuard.AllowHostRule(AuthorityHookLabels.SkillSystemDamageGroundObjectByLocalCaster,
                detail: $"planet={factory?.planetId} target={target.type}:{target.id}"))
        {
            return false;
        }

        if (caster.type != ETargetType.Craft
            || target.type != ETargetType.Enemy
            || !Multiplayer.IsActive || Multiplayer.Session.Combat.IsIncomingRequest.Value) return true;
        if (Multiplayer.Session.IsClient && !OwnsCraft(factory.planetId, caster.id))
        {
            damage = 0;
            return true;
        }
        if (damageObjectDepth > 0 || damage <= 0) return true; // DamageObject already routed this hit.

        return true;
    }

    /// <summary>
    /// The remote-caster ground damage entry.
    /// </summary>
    /// <remarks>
    /// A01 records this path as one of the writers of <c>EntityData.constructStatId</c>, which is
    /// what starts the E04 damage to construct record to repair chain, and it is the entry a caster
    /// on another astro reaches. The mod has no prefix here today, so A05 adds one whose only job is
    /// the guard: on a client the call is refused outside a replica apply, and the prefix never
    /// touches the damage arguments themselves.
    /// </remarks>
    [HarmonyPrefix]
    [HarmonyPatch(nameof(SkillSystem.DamageGroundObjectByRemoteCaster))]
    public static bool DamageGroundObjectByRemoteCaster_Prefix(PlanetFactory factory, ref SkillTargetLocal target)
    {
        return AuthorityRuleGuard.AllowHostRule(AuthorityHookLabels.SkillSystemDamageGroundObjectByRemoteCaster,
            detail: $"planet={factory?.planetId} target={target.type}:{target.id}");
    }
}
