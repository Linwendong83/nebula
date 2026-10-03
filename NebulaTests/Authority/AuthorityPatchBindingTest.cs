#region

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

#endregion

namespace NebulaTests.Authority;

/// <summary>
/// A22 runtime smoke found this: a new <c>ConstructionSystem_Patch</c> prefix declared
/// <c>(int objectId, int objectType)</c> while vanilla 29104 declares
/// <c>AddConstructStat(int entityId, int damage)</c>. Harmony binds prefix parameters by name,
/// so the whole <c>PatchAll</c> threw in the game and no instance could start — while every
/// existing test stayed green, because resolving a target ("AllHarmonyPatchCompatibilityTest")
/// never installs it.
/// </summary>
/// <remarks>
/// This test replays Harmony's by-name binding statically: for every prefix/postfix in the
/// patcher, each non-magic parameter must name a formal parameter of its resolved target,
/// read from the same game reference metadata the build compiles against. Transpiler targets
/// must at least resolve. It runs in the plain test process; the game instance smoke remains
/// the integration proof.
/// </remarks>
[TestClass]
public class AuthorityPatchBindingTest
{
    private static readonly HashSet<string> MagicNames = new(StringComparer.Ordinal)
    {
        "__instance", "__result", "__state", "__args", "__originalMethod", "__runOriginal", "__exception",
    };

    private static bool IsMagic(string name)
    {
        if (MagicNames.Contains(name)) return true;
        if (name.StartsWith("___", StringComparison.Ordinal)) return true;
        // Harmony positional original-argument injection: __0, __1, ...
        if (name.Length > 2 && name[0] == '_' && name[1] == '_')
        {
            for (var i = 2; i < name.Length; i++)
                if (name[i] < '0' || name[i] > '9')
                    return false;
            return name.Length > 2;
        }
        return false;
    }

    private sealed class PatchTarget
    {
        public Type DeclaringType;
        public string MethodName;
        public Type[] ArgumentTypes;
    }

    private static List<PatchTarget> TargetsOf(Type patchClass, MethodInfo patchMethod)
    {
        var classLevel = patchClass.GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == typeof(HarmonyPatch).FullName).ToList();
        var methodLevel = patchMethod.GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == typeof(HarmonyPatch).FullName).ToList();

        Type classType = null;
        foreach (var a in classLevel)
        {
            if (a.ConstructorArguments.Count >= 1 && a.ConstructorArguments[0].Value is Type t && classType == null)
                classType = t;
        }

        var targets = new List<PatchTarget>();
        if (methodLevel.Count == 0)
        {
            // Class-level [HarmonyPatch(typeof(T), "Method")] with a bare [HarmonyPrefix] method.
            if (classType != null)
            {
                foreach (var a in classLevel)
                {
                    var target = new PatchTarget { DeclaringType = classType };
                    foreach (var arg in a.ConstructorArguments)
                    {
                        if (arg.Value is string s) target.MethodName ??= s;
                        else if (arg.ArgumentType.IsArray) target.ArgumentTypes ??= ReadTypeArray(arg);
                    }
                    if (target.MethodName != null) targets.Add(target);
                }
            }
            return targets;
        }
        foreach (var a in methodLevel)
        {
            // Supported HarmonyPatch forms: (Type), (name), (Type, name), (name, Type[]),
            // (Type, name, Type[]). A MethodType enum argument, when present, is ignored.
            var target = new PatchTarget { DeclaringType = classType };
            foreach (var arg in a.ConstructorArguments)
            {
                if (arg.Value is Type t)
                {
                    if (target.DeclaringType == null) target.DeclaringType = t;
                }
                else if (arg.Value is string s)
                {
                    target.MethodName ??= s;
                }
                else if (arg.ArgumentType.IsArray)
                {
                    target.ArgumentTypes ??= ReadTypeArray(arg);
                }
            }
            if (target.DeclaringType != null && target.MethodName != null) targets.Add(target);
        }
        return targets;
    }

    private static Type[] ReadTypeArray(CustomAttributeTypedArgument arg)
    {
        if (arg.Value is System.Collections.IList list)
        {
            var types = new List<Type>();
            foreach (CustomAttributeTypedArgument item in list)
                if (item.Value is Type t)
                    types.Add(t);
            return types.ToArray();
        }
        return null;
    }

    private const BindingFlags AnyMethod =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static MethodInfo ResolveTarget(PatchTarget target, out string error)
    {
        error = null;
        MethodInfo[] candidates;
        try
        {
            candidates = target.DeclaringType.GetMethods(AnyMethod)
                .Where(m => m.Name == target.MethodName).ToArray();
        }
        catch (Exception e)
        {
            error = "cannot enumerate " + target.DeclaringType.FullName + ": " + e.GetType().Name;
            return null;
        }
        if (candidates.Length == 0)
        {
            // Property accessors patched via (name, MethodType.Getter/Setter).
            var property = target.DeclaringType.GetProperty(target.MethodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            var accessor = property?.GetGetMethod(true) ?? property?.GetSetMethod(true);
            if (accessor != null) return accessor;
            error = "no method " + target.MethodName + " on " + target.DeclaringType.FullName;
            return null;
        }
        if (target.ArgumentTypes != null)
        {
            foreach (var m in candidates)
            {
                var ps = m.GetParameters();
                if (ps.Length != target.ArgumentTypes.Length) continue;
                var match = true;
                for (var i = 0; i < ps.Length; i++)
                {
                    var want = target.ArgumentTypes[i];
                    var got = ps[i].ParameterType;
                    if (got.IsByRef) got = got.GetElementType();
                    if (got != want) { match = false; break; }
                }
                if (match) return m;
            }
            error = "no overload of " + target.MethodName + " matches the declared argument types";
            return null;
        }
        if (candidates.Length > 1)
        {
            error = "ambiguous " + target.MethodName + " (" + candidates.Length +
                    " overloads, no argument types declared)";
            return null;
        }
        return candidates[0];
    }

    [TestMethod]
    public void EveryPrefixAndPostfixBindsItsParametersByName()
    {
        var failures = new List<string>();
        var checkedPatches = 0;
        var patcher = typeof(NebulaPatcher.NebulaPlugin).Assembly;
        foreach (var patchClass in patcher.GetTypes())
        {
            MethodInfo[] methods;
            try
            {
                methods = patchClass.GetMethods(AnyMethod | BindingFlags.DeclaredOnly);
            }
            catch
            {
                continue;
            }
            foreach (var patchMethod in methods)
            {
                var isPrefix = patchMethod.GetCustomAttributesData()
                    .Any(a => a.AttributeType.FullName == typeof(HarmonyPrefix).FullName);
                var isPostfix = patchMethod.GetCustomAttributesData()
                    .Any(a => a.AttributeType.FullName == typeof(HarmonyPostfix).FullName);
                if (!isPrefix && !isPostfix) continue;
                var targets = TargetsOf(patchClass, patchMethod);
                if (targets.Count == 0)
                {
                    failures.Add(patchClass.FullName + "." + patchMethod.Name + ": no resolvable target");
                    continue;
                }
                foreach (var target in targets)
                {
                    var resolved = ResolveTarget(target, out var error);
                    if (resolved == null)
                    {
                        failures.Add(patchClass.FullName + "." + patchMethod.Name + " -> " +
                                     target.DeclaringType.FullName + "." + target.MethodName + ": " + error);
                        continue;
                    }
                    checkedPatches++;
                    var formalNames = new HashSet<string>(
                        resolved.GetParameters().Select(p => p.Name), StringComparer.Ordinal);
                    foreach (var p in patchMethod.GetParameters())
                    {
                        if (IsMagic(p.Name)) continue;
                        if (!formalNames.Contains(p.Name))
                        {
                            failures.Add(patchClass.FullName + "." + patchMethod.Name +
                                         ": parameter '" + p.Name + "' not found in " +
                                         target.DeclaringType.Name + "." + target.MethodName + "(" +
                                         string.Join(", ", formalNames) + ")");
                        }
                    }
                }
            }
        }
        TestAssert.IsTrue(checkedPatches > 50,
            "Expected to check dozens of patches, saw " + checkedPatches + "; the scan is broken.");
        TestAssert.IsEmpty(failures, "Patch parameter binding failures:\n" + string.Join("\n", failures));
    }

    [TestMethod]
    public void TheFixedConstructionPrefixesAreCovered()
    {
        // Direct regression pin for the smoke failure: these exact (patch, target-param) pairs
        // must exist, independent of the broad scan above.
        var patcher = typeof(NebulaPatcher.NebulaPlugin).Assembly;
        var patchClass = patcher.GetType("NebulaPatcher.Patches.Dynamic.ConstructionSystem_Patch");
        TestAssert.IsNotNull(patchClass);
        var add = patchClass.GetMethod("AddConstructStat_Prefix",
            BindingFlags.Public | BindingFlags.Static);
        TestAssert.IsNotNull(add);
        TestAssert.IsTrue(add.GetParameters().Select(p => p.Name).SequenceEqual(["entityId", "damage"]),
            "AddConstructStat_Prefix must bind (entityId, damage), saw: " +
            string.Join(",", add.GetParameters().Select(p => p.Name)));
        var remove = patchClass.GetMethod("RemoveConstructStat_Prefix",
            BindingFlags.Public | BindingFlags.Static);
        TestAssert.IsNotNull(remove);
        TestAssert.IsTrue(remove.GetParameters().Select(p => p.Name).SequenceEqual(["constructStatId"]),
            "RemoveConstructStat_Prefix must bind (constructStatId)");
    }
}
