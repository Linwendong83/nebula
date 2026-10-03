#region

using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaPatcher.Patches.Authority;
using NebulaWorld.Combat;

#endregion

namespace NebulaPatcher.Patches.Transpilers;

/// <summary>
/// Rewrites the vanilla "player id 1" test in the damage entries to the session's player id.
/// </summary>
/// <remarks>
/// <para>
/// The vanilla damage entries hard-code player id 1 for the local mecha. In a room every peer has its
/// own id, so the test has to become the local player's id or only one player would ever be treated
/// as the caster. A11/A19 replace this shared-player-context approach with an explicit caster/target
/// context; until then the transformation must actually apply, which is why A05 verifies the exact
/// match count instead of letting a silent no-op leave the vanilla constant in place.
/// </para>
/// <para>
/// There is no catch-all around the transformation. A05 forbids "catch and continue as vanilla": a
/// transformation that throws is a real defect, and the failure has to reach
/// <see cref="AuthorityTranspilerGuard"/> so entering the new mode is refused rather than running a
/// half-patched rule.
/// </para>
/// </remarks>
[HarmonyPatch(typeof(SkillSystem))]
internal class SkillSystem_Transpiler
{
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(SkillSystem.DamageGroundObjectByLocalCaster))]
    [HarmonyPatch(nameof(SkillSystem.DamageGroundObjectByRemoteCaster))]
    public static IEnumerable<CodeInstruction> DamageGroundObject_Transpiler(
        IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        /*  Overwrite player id 1 to PlayerId
            from:
                else if (target.type == ETargetType.Player && target.id == 1)
            to:
                else if (target.type == ETargetType.Player && target.id == NebulaWorld.Combat.CombatManager.PlayerId)
        */
        var matches = new[]
        {
            new CodeMatch(i => i.IsLdarg()),
            new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(SkillTargetLocal), nameof(SkillTargetLocal.id))),
            new CodeMatch(OpCodes.Ldc_I4_1),
            new CodeMatch(OpCodes.Bne_Un)
        };

        var codes = new List<CodeInstruction>(instructions);
        var label = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name + ".playerId";
        if (!AuthorityTranspilerGuard.VerifyCount(label, codes, 1, exact: true, matches))
        {
            // The method is left untouched; the recorded miss makes the new mode refuse to load.
            return codes;
        }

        return new CodeMatcher(codes)
            .End()
            .MatchBack(true, matches)
            .Advance(-1)
            .Set(OpCodes.Call, AccessTools.DeclaredPropertyGetter(typeof(CombatManager),
                nameof(CombatManager.PlayerId)))
            .InstructionEnumeration();
    }

    [HarmonyTranspiler]
    [HarmonyPatch(nameof(SkillSystem.DamageObject))]
    public static IEnumerable<CodeInstruction> DamageObject_Transpiler(
        IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        /*  Overwrite player id 1 to PlayerId
            from:
                if (target.id == 1)
            to:
                if (target.id == NebulaWorld.Combat.CombatManager.PlayerId)
        */
        var matches = new[]
        {
            new CodeMatch(i => i.IsLdarg()),
            new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(SkillTarget), nameof(SkillTarget.id))),
            new CodeMatch(OpCodes.Ldc_I4_1),
            new CodeMatch(OpCodes.Bne_Un)
        };

        var codes = new List<CodeInstruction>(instructions);
        var label = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name + ".playerId";
        if (!AuthorityTranspilerGuard.VerifyCount(label, codes, 1, exact: true, matches))
        {
            return codes;
        }

        return new CodeMatcher(codes)
            .End()
            .MatchBack(true, matches)
            .Advance(-1)
            .Set(OpCodes.Call, AccessTools.DeclaredPropertyGetter(typeof(CombatManager),
                nameof(CombatManager.PlayerId)))
            .InstructionEnumeration();
    }
}
