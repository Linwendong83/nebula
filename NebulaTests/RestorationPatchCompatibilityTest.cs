using System.Reflection;
using HarmonyLib;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests;

[TestClass]
public class RestorationPatchCompatibilityTest
{
    [TestMethod]
    public void HiddenGamePropertiesUsedByRemotePlayersHaveSetters()
    {
        foreach (var (type, name) in new[]
                 {
                     (typeof(Player), nameof(Player.transform)),
                     (typeof(Player), nameof(Player.mecha)),
                     (typeof(Player), nameof(Player.animator)),
                     (typeof(Player), nameof(Player.controller)),
                     (typeof(Player), nameof(Player.cameraTarget)),
                     (typeof(Player), nameof(Player.vegetableCollection)),
                     (typeof(MechaForge), nameof(MechaForge.mecha)),
                     (typeof(ManualBehaviour), nameof(ManualBehaviour.data))
                 })
        {
            var property = AccessTools.Property(type, name);
            TestAssert.IsNotNull(property, type.Name + "." + name);
            TestAssert.IsNotNull(property.GetSetMethod(true), type.Name + "." + name);
        }
        var armor = (MechaArmorModel)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
            typeof(MechaArmorModel));
        var player = new Player();
        global::NebulaModel.Utils.NativeGameAccess.SetHiddenProperty(armor, nameof(ManualBehaviour.data), player);
        TestAssert.AreSame(player, armor.data);
    }

    [TestMethod]
    public void NewPatchesResolveAgainstRealGameMethodBodies()
    {
        var instructions = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(Player), nameof(Player.PrepareRedeploy)));
        TestAssert.IsGreaterThan(5, instructions.Count, "Run with the actual game's Assembly-CSharp.dll, not reference stubs.");
        var harmony = new Harmony("nebula.tests.restoration-compatibility");
        var assembly = typeof(NebulaPatcher.NebulaPlugin).Assembly;
        string[] classes =
        [
            "UIGalaxySelect_Patch", "LobbyGoalSetting_Patch", "PropertyLogic_Patch", "PropertySystem_Patch",
            "UIPropertyRealize_Patch", "BuyoutTech_Patch", "UIDFCommunicatorWindow_Patch", "PlayerAction_Death_Patch",
            "MechaArmorModel_Patch", "ArmorWreckage_Patch",
            "UIDeathPanel_Patch", "GoalLogic_Patch", "PersonalGoalData_Patch", "GoalStage_Patch", "GoalLevelRestore_Patch", "SkillSystem_Patch",
            "HeadlessGoalDeterminator_Patch", "HeadlessGoalLoad_Patch", "SharedGoalSetting_Patch", "SharedGoalIgnore_Patch",
            "SharedGoalInventory_Patch", "SharedGoalWarpStorage_Patch", "KillStatistics_Patch", "KillAttribution_Patch",
            "RemoteKillStatisticsUI_Patch", "PlayerVegetation_Patch", "VegetableCollection_Patch",
            "GroundEnemyGeneration_Patch", "SpaceEnemyGeneration_Patch",
            "GroundCraftVisualGeneration_Patch", "SpaceCraftVisualGeneration_Patch", "AuthoritativeAttackRendering_Patch",
            "AuthoritativePlasmaRendering_Patch", "AuthoritativeBomberRendering_Patch", "BattleImpact_Patch", "PredictedWorldImpactRendering_Patch"
        ];
        var patchTools = typeof(Harmony).Assembly.GetType("HarmonyLib.PatchTools", true)!;
        var resolve = AccessTools.Method(patchTools, "GetOriginalMethod");
        foreach (var name in classes)
        {
            var type = assembly.GetType("NebulaPatcher.Patches.Dynamic." + name, true)!;
            var processor = harmony.CreateClassProcessor(type);
            var patches = (System.Collections.IEnumerable)AccessTools.Field(typeof(PatchClassProcessor), "patchMethods").GetValue(processor);
            var count = 0;
            foreach (var patch in patches)
            {
                var info = (HarmonyMethod)AccessTools.Field(patch.GetType(), "info").GetValue(patch);
                var target = (MethodBase?)resolve.Invoke(null, [info]);
                TestAssert.IsNotNull(target, name + "." + info.method.Name);
                TestAssert.IsNotEmpty(PatchProcessor.GetOriginalInstructions(target), target.ToString());
                foreach (var parameter in info.method.GetParameters().Where(x => !x.Name!.StartsWith("__")))
                {
                    TestAssert.IsTrue(target.GetParameters().Any(x => x.Name == parameter.Name),
                        $"{name}.{info.method.Name}: original argument {parameter.Name} is missing");
                    var original = target.GetParameters().Single(x => x.Name == parameter.Name).ParameterType;
                    var patchType = parameter.ParameterType;
                    if (original.IsByRef) original = original.GetElementType()!;
                    if (patchType.IsByRef) patchType = patchType.GetElementType()!;
                    TestAssert.AreEqual(original, patchType, $"{name}.{info.method.Name}: {parameter.Name} has a different native layout");
                }
                count++;
            }
            TestAssert.IsGreaterThan(0, count, name);
        }
    }

    [TestMethod]
    public void VisualProjectileDecodingDisarmsTheNativePayload()
    {
        var projectile = new GeneralMissile
        { id = 1, life = 20, damage = 12345, damageIncoming = 12345, mask = ETargetTypeMask.Enemy };
        using var stream = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(stream);
        projectile.Export(writer);
        var method = typeof(NebulaWorld.Combat.BattleVisualRenderer).GetMethod("Decode", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = (GeneralMissile)method.Invoke(null, [new global::NebulaModel.DataStructures.BattleEffectData
        { Kind = global::NebulaModel.DataStructures.BattleEffectKind.TurretMissile, Payload = stream.ToArray() }])!;
        TestAssert.AreEqual(0, result.damage);
        TestAssert.AreEqual(0, result.damageIncoming);
        TestAssert.AreEqual((ETargetTypeMask)0, result.mask);
    }

    [TestMethod]
    public void NativeTrashPoolUsesZeroAsItsFirstValidIdentifier()
    {
        var pool = (TrashContainer)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(TrashContainer));
        pool.trashObjPool = new TrashObject[8];
        pool.trashDataPool = new TrashData[8];
        pool.trashRecycle = new int[8];
        AccessTools.Field(typeof(TrashContainer), "trashCapacity").SetValue(pool, 8);
        var item = new TrashObject { item = 1101, count = 37 };
        TestAssert.AreEqual(0, pool.NewTrash(item, new TrashData()));
        TestAssert.AreEqual(37, pool.trashObjPool[0].count);
    }
}
