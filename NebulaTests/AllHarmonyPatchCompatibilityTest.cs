using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NebulaModel.Logger;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests;

[TestClass]
[DoNotParallelize]
public class AllHarmonyPatchCompatibilityTest
{
    [TestMethod]
    public void EveryDeclaredGamePatchResolvesAgainstTheInstalledGameBody()
    {
        var method = AccessTools.Method(typeof(GameLogic), nameof(GameLogic.LogicFrame));
        TestAssert.IsGreaterThan(5, method.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0,
            "Run with the real game assembly, not reference stubs.");

        var harmony = new Harmony("nebula.tests.all-patches");
        var failures = new List<string>();
        var patchTools = typeof(Harmony).Assembly.GetType("HarmonyLib.PatchTools", true)!;
        var resolve = AccessTools.Method(patchTools, "GetOriginalMethod");
        var patchMethods = AccessTools.Field(typeof(PatchClassProcessor), "patchMethods");
        foreach (var patchClass in typeof(NebulaPatcher.NebulaPlugin).Assembly.GetTypes().Where(type =>
                     type.Namespace?.StartsWith("NebulaPatcher.Patches.", StringComparison.Ordinal) == true &&
                     type.GetCustomAttributesData().Any(attribute => attribute.AttributeType == typeof(HarmonyPatch))))
        {
            try
            {
                var processor = harmony.CreateClassProcessor(patchClass);
                foreach (var patch in (System.Collections.IEnumerable)patchMethods.GetValue(processor))
                {
                    var info = (HarmonyMethod)AccessTools.Field(patch.GetType(), "info").GetValue(patch);
                    var target = (MethodBase)resolve.Invoke(null, new object[] { info });
                    if (target == null) failures.Add(patchClass.FullName + "." + info.method.Name + ": target missing");
                    else if (target.DeclaringType?.Assembly == typeof(GameLogic).Assembly &&
                             target.GetMethodBody() == null)
                        failures.Add(patchClass.FullName + "." + info.method.Name + ": missing game body");
                }
            }
            catch (Exception ex) { failures.Add(patchClass.FullName + ": " + ex.GetType().Name + " " + ex.Message); }
        }
        TestAssert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }
}
