#region

using System.IO;
using HarmonyLib;
using NebulaModel.Packets.Combat;
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
    public static void DamageObject_Prefix(ref int damage, int slice, ref SkillTarget target, ref SkillTarget caster, out bool __state)
    {
        __state = Multiplayer.IsActive;
        if (__state) damageObjectDepth++;
        if (!(caster.type == ETargetType.Craft || caster.type == ETargetType.Player)
            || target.type != ETargetType.Enemy
            || !Multiplayer.IsActive || Multiplayer.Session.Combat.IsIncomingRequest.Value) return;

        if (caster.type == ETargetType.Player && caster.id != Multiplayer.Session.LocalPlayer.Id ||
            Multiplayer.Session.IsClient && caster.type == ETargetType.Craft && !OwnsCraft(caster.astroId, caster.id))
        {
            damage = 0;
            return;
        }
        if (damage <= 0) return;

        if (target.astroId > 1000000) // Sync for space enemy
        {
            var packet = new CombatStatDamagePacket(damage, slice, in target, in caster)
            {
                // Native targeting needs a player proxy; SourceType retains the actual attacker for statistics.
                CasterType = (short)ETargetType.Player,
                CasterId = Multiplayer.Session.LocalPlayer.Id,
                TargetGeneration = Multiplayer.Session.Generations.Get(target.astroId, target.id)
            };
            Multiplayer.Session.Network.SendPacket(packet);
        }
        else if (Multiplayer.Session.IsServer || target.astroId == GameMain.localPlanet?.id)
        {
            var packet = new CombatStatDamagePacket(damage, slice, in target, in caster)
            {
                // Retain the original source type separately from the native targeting proxy.
                CasterType = (short)ETargetType.Player,
                CasterId = Multiplayer.Session.LocalPlayer.Id,
                TargetGeneration = Multiplayer.Session.Generations.Get(target.astroId, target.id)
            };
            Multiplayer.Session.Network.SendPacket(packet);
        }
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
            if (fleet.fighters != null) foreach (var fighter in fleet.fighters)
                if (fighter.craftId == id) return true;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(SkillSystem.DamageGroundObjectByLocalCaster))]
    public static void DamageGroundObjectByLocalCaster_Prefix(PlanetFactory factory, ref int damage, int slice, ref SkillTargetLocal target, ref SkillTargetLocal caster)
    {
        if (caster.type != ETargetType.Craft
            || target.type != ETargetType.Enemy
            || !Multiplayer.IsActive || Multiplayer.Session.Combat.IsIncomingRequest.Value) return;
        if (Multiplayer.Session.IsClient && !OwnsCraft(factory.planetId, caster.id))
        {
            damage = 0;
            return;
        }
        if (damageObjectDepth > 0 || damage <= 0) return; // DamageObject already routed this hit.
        if (Multiplayer.Session.IsServer || factory == GameMain.localPlanet?.factory)
        {
            var globalTarget = new SkillTarget { id = target.id, type = target.type, astroId = factory.planetId };
            var globalCaster = new SkillTarget { id = caster.id, type = caster.type, astroId = factory.planetId };
            var packet = new CombatStatDamagePacket(damage, slice, in globalTarget, in globalCaster)
            {
                // Retain the original source type separately from the native targeting proxy.
                CasterType = (short)ETargetType.Player,
                CasterId = Multiplayer.Session.LocalPlayer.Id,
                TargetGeneration = Multiplayer.Session.Generations.Get(factory.planetId, target.id)
            };
            Multiplayer.Session.Network.SendPacket(packet);
        }
    }
}
