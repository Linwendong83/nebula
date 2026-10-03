#region

using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaModel.Packets.Factory;
using NebulaPatcher.Patches.Authority;
using NebulaWorld;

#endregion

namespace NebulaPatcher.Patches.Transpilers;

/// <summary>
/// Reports the material a prebuild consumed so the other peers see the same required item count.
/// </summary>
/// <remarks>
/// The insertion point is a long instruction pattern, and the transformation is what keeps a client's
/// build from consuming material only locally. A05 requires a checked match count: a silent no-op
/// would leave the peers disagreeing about what a construction site still needs, which is the kind of
/// divergence the guard exists to make visible. A18 takes over the material transaction itself.
/// </remarks>
[HarmonyPatch(typeof(ConstructionModuleComponent))]
internal class ConstructionModuleComponent_Transpiler
{
    [HarmonyTranspiler, HarmonyPriority(Priority.High)]
    [HarmonyPatch(nameof(ConstructionModuleComponent.PlaceItems))]
    public static IEnumerable<CodeInstruction> PlaceItems_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        /*  Sync Prebuild.itemRequired changes by player, insert local method call after player.package.TakeTailItems
            After:  player.package.TakeTailItems(ref itemId, ref count, out inc, false);
            Insert: SendPacket(factory, ptr3, count);
            Before: Assert.True(count == ptr3.itemRequired);
        */
        var matches = new[]
        {
            new CodeMatch(OpCodes.Ldarg_2),
            new CodeMatch(OpCodes.Callvirt, AccessTools.DeclaredPropertyGetter(typeof(Player), nameof(Player.package))),
            new CodeMatch(OpCodes.Ldloca_S),
            new CodeMatch(OpCodes.Ldloca_S),
            new CodeMatch(OpCodes.Ldloca_S),
            new CodeMatch(OpCodes.Ldc_I4_0),
            new CodeMatch(i => i.opcode == OpCodes.Callvirt && ((MethodInfo)i.operand).Name == "TakeTailItems"),
            new CodeMatch(OpCodes.Ldloc_S),
            new CodeMatch(OpCodes.Ldloc_S),
            new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(PrebuildData), nameof(PrebuildData.itemRequired)))
        };

        var codes = new List<CodeInstruction>(instructions);
        var label = typeof(ConstructionModuleComponent).Name + "." + nameof(ConstructionModuleComponent.PlaceItems) +
                    ".itemRequired";
        if (!AuthorityTranspilerGuard.VerifyCount(label, codes, 1, exact: false, matches))
        {
            // The method is left untouched; the recorded miss makes the new mode refuse to load.
            return codes;
        }

        return new CodeMatcher(codes)
            .MatchForward(true, matches)
            .Repeat(
                matcher => matcher
                    .Advance(-2)
                    .InsertAndAdvance(
                        new CodeInstruction(OpCodes.Ldarg_1),
                        new CodeInstruction(OpCodes.Ldloc_S, matcher.InstructionAt(1).operand),
                        new CodeInstruction(OpCodes.Ldloc_S, matcher.InstructionAt(-4).operand),
                        new CodeInstruction(OpCodes.Call,
                            AccessTools.Method(typeof(ConstructionModuleComponent_Transpiler), nameof(SendPacket)))
                    )
            )
            .InstructionEnumeration();
    }

    private static void SendPacket(PlanetFactory factory, ref PrebuildData prebuild, int itemCount)
    {
        if (!Multiplayer.IsActive) return;

        var packet = new PrebuildItemRequiredUpdate(factory.planetId, prebuild.id, itemCount);
        Multiplayer.Session.Network.SendPacketToLocalStar(packet);
    }
}
