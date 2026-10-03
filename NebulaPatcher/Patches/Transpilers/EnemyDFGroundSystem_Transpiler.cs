#region

using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaModel.Logger;
using NebulaWorld;

#endregion

namespace NebulaPatcher.Patches.Transpilers;

[HarmonyPatch(typeof(EnemyDFGroundSystem))]
internal class EnemyDFGroundSystem_Transpiler
{
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.GameTickLogic_Prepare))]
    public static IEnumerable<CodeInstruction> GameTickLogic_Prepare_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        try
        {
            /*  Overwrite the condition of local player and position in MP
                First make it skips the original logic that only work for single player, by setting local_player_exist = false
            to:
                if (isLocalLoaded && !Multiplayer.IsActive) {
                    ...
                }
                else {
                    ...
                }
            */

            var codeMatcher = new CodeMatcher(instructions)
                .MatchForward(true,
                    new CodeMatch(OpCodes.Ldarg_0),
                    new CodeMatch(OpCodes.Call, AccessTools.DeclaredPropertyGetter(typeof(EnemyDFGroundSystem), nameof(EnemyDFGroundSystem.isLocalLoaded))),
                    new CodeMatch(OpCodes.Brfalse));

            var jumpOperand = codeMatcher.Instruction.operand;
            codeMatcher.Advance(1)
                .Insert(
                    new CodeInstruction(OpCodes.Call, AccessTools.DeclaredPropertyGetter(typeof(Multiplayer), nameof(Multiplayer.IsActive))),
                    new CodeInstruction(OpCodes.Brtrue_S, jumpOperand)
                );

            return codeMatcher.InstructionEnumeration();
        }
        catch (System.Exception e)
        {
            Log.Error("Transpiler GameTickLogic_Prepare failed. Ground Dark Fog unit target will not in sync.");
            Log.Error(e);
            return instructions;
        }
    }

    [HarmonyTranspiler]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.GameTickLogic_Unit))]
    public static IEnumerable<CodeInstruction> GameTickLogic_Unit_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        // Retired: hatred-target sync traffic was removed with the legacy enemy-state chain.
        // Host AI and replica pose/state own targeting now; the vanilla switch runs unmodified.
        return instructions;
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(GameLogic), nameof(GameLogic._enemy_ground_unit_parallel))]
    public static IEnumerable<CodeInstruction> GameTickLogic_Unit_Parallel_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        // Retired: see GameTickLogic_Unit_Transpiler.
        return instructions;
    }

    [HarmonyTranspiler]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.DeactivateUnit))]
    public static IEnumerable<CodeInstruction> DeactivateUnit_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        // Retired: the deactivation broadcast was removed; vanilla RemoveEnemyFinal runs.
        return instructions;
    }
}
