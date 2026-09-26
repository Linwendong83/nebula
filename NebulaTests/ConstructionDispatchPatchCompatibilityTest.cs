using HarmonyLib;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests;

[TestClass]
public class ConstructionDispatchPatchCompatibilityTest
{
    [TestMethod]
    public void ConstructionHooksResolveAgainstGameMethodBodies()
    {
        AssertBody(typeof(ConstructionModuleComponent), nameof(ConstructionModuleComponent.SearchBuildTargets));
        AssertBody(typeof(ConstructionModuleComponent), nameof(ConstructionModuleComponent.InsertTmpBuildTarget));
        AssertBody(typeof(ConstructionModuleComponent), nameof(ConstructionModuleComponent.InsertBuildTarget));
        AssertBody(typeof(ConstructionModuleComponent), nameof(ConstructionModuleComponent.EjectMechaDrone));
        AssertBody(typeof(ConstructionModuleComponent), nameof(ConstructionModuleComponent.EjectBaseDrone));
        AssertBody(typeof(ConstructionSystem), nameof(ConstructionSystem.AddBuildTargetToModules));
        AssertBody(typeof(ConstructionSystem), nameof(ConstructionSystem.ResetDroneTargets));
        AssertBody(typeof(PlanetFactory), nameof(PlanetFactory.RemovePrebuildWithComponents));
    }

    private static void AssertBody(System.Type type, string name)
    {
        var method = AccessTools.Method(type, name);
        TestAssert.IsNotNull(method, $"Missing {type.Name}.{name}");
        var body = method.GetMethodBody()?.GetILAsByteArray();
        TestAssert.IsNotNull(body, $"Missing method body for {type.Name}.{name}");
        TestAssert.IsGreaterThan(5, body.Length,
            $"{type.Name}.{name} is a reference stub; run with the actual game assembly");
    }
}
