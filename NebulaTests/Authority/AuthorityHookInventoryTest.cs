using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using HarmonyLib;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests;

/// <summary>
/// A01: verifies that every hook target and protected field recorded in
/// <c>docs/host-authority/authority-hooks.json</c> still resolves against the installed game
/// assembly, and that each recorded writer really does store into its field.
///
/// VALIDATION.md §1 warns that a passing "target resolves" check is not sufficient on its own.
/// These tests therefore also re-derive the writer set from IL and compare it to the inventory,
/// so a stale or hand-edited JSON file fails loudly instead of being trusted.
///
/// The JSON is a required artifact: if it is missing, these tests fail rather than skip. A skip
/// here would let the whole inventory rot without anyone noticing.
/// </summary>
[TestClass]
public class AuthorityHookInventoryTest
{
    private static string InventoryPath
    {
        get
        {
            // Walk up from the test output directory to the repository root.
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "docs", "host-authority", "authority-hooks.json");
                if (File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }
            return null;
        }
    }

    /// <summary>
    /// Enumerates types without throwing. The game assembly cannot be fully loaded in a plain
    /// test process (Unity native types are absent), so GetTypes() raises
    /// ReflectionTypeLoadException; the successfully loaded types are still usable.
    /// </summary>
    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            return error.Types.Where(t => t != null);
        }
    }

    /// <summary>
    /// Resolves a label's type segment, which may be a bare name ("CombatStat") or fully
    /// namespace-qualified ("NebulaNetwork.Server").
    /// </summary>
    private static Type ResolveType(Assembly assembly, string typeName)
    {
        var direct = assembly.GetType(typeName, throwOnError: false);
        if (direct != null) return direct;
        return SafeTypes(assembly).FirstOrDefault(t => t.Name == typeName || t.FullName == typeName);
    }

    private static JsonDocument LoadInventory()
    {
        var path = InventoryPath;
        TestAssert.IsNotNull(path,
            "docs/host-authority/authority-hooks.json not found. It is the A01 deliverable; " +
            "regenerate it with tools/authority/build-hooks-json.py.");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    [TestMethod]
    public void EveryRecordedHookTargetResolvesAgainstTheInstalledGameAssembly()
    {
        using var inventory = LoadInventory();
        var gameAssembly = typeof(GameLogic).Assembly;
        var failures = new List<string>();

        var targets = inventory.RootElement.GetProperty("targets").EnumerateArray()
            .Select(t => t.GetProperty("label").GetString())
            .Concat(inventory.RootElement.GetProperty("schedulerTargets").EnumerateArray()
                .Select(t => t.GetProperty("label").GetString()));

        foreach (var label in targets)
        {
            // Labels are "Type.Method"; the method may be non-public, so resolve by name across
            // all binding flags rather than assuming a public surface.
            var separator = label.LastIndexOf('.');
            if (separator <= 0) { failures.Add(label + ": malformed label"); continue; }
            var typeName = label.Substring(0, separator);
            var methodName = label.Substring(separator + 1);

            var type = ResolveType(gameAssembly, typeName);
            if (type == null)
            {
                // A few targets live in the mod assembly (e.g. DroneManager, SimulatedWorld).
                type = ResolveType(typeof(NebulaWorld.Multiplayer).Assembly, typeName);
            }
            if (type == null)
            {
                type = ResolveType(typeof(global::NebulaNetwork.Server).Assembly, typeName);
            }
            if (type == null)
            {
                failures.Add(label + ": type not found in any assembly (looked for '" + typeName + "')");
                continue;
            }

            var method = AccessTools.Method(type, methodName);
            if (method == null) { failures.Add(label + ": method not found on " + type.FullName); continue; }

            // A game method with no body is a reference stub, which would make the whole
            // inventory meaningless; the mod's own methods are fine either way.
            if (method.DeclaringType?.Assembly == gameAssembly)
            {
                var body = method.GetMethodBody()?.GetILAsByteArray();
                if (body == null || body.Length <= 5)
                {
                    failures.Add(label + ": game method has no real body (reference stub?)");
                }
            }
        }

        TestAssert.IsEmpty(failures, "Hook inventory targets failed to resolve:\n" + string.Join("\n", failures));
    }

    [TestMethod]
    public void EveryRecordedProtectedFieldExists()
    {
        using var inventory = LoadInventory();
        var gameAssembly = typeof(GameLogic).Assembly;
        var writers = inventory.RootElement.GetProperty("protectedFieldWriters");
        var failures = new List<string>();
        var checkedCount = 0;

        foreach (var entry in writers.EnumerateObject())
        {
            var separator = entry.Name.IndexOf('.');
            if (separator <= 0) { failures.Add(entry.Name + ": malformed field key"); continue; }
            var typeName = entry.Name.Substring(0, separator);
            var fieldName = entry.Name.Substring(separator + 1);

            var type = gameAssembly.GetType(typeName, throwOnError: false)
                       ?? SafeTypes(gameAssembly).FirstOrDefault(t => t.Name == typeName);
            if (type == null) { failures.Add(entry.Name + ": type not found"); continue; }

            var field = type.GetField(fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (field == null) { failures.Add(entry.Name + ": field not found"); continue; }
            checkedCount++;

            // The recorded writer list must match what IL actually shows, in BOTH directions.
            // Checking only "actual is a subset of recorded" would accept an inventory padded
            // with writers that do not exist, which is exactly how a hand-edited or stale file
            // would sneak through.
            var actual = FindWriters(gameAssembly, field);
            var recorded = entry.Value.EnumerateArray().Select(v => v.GetString()).ToHashSet();
            foreach (var writer in actual)
            {
                if (!recorded.Contains(writer))
                {
                    failures.Add(entry.Name + ": writer " + writer +
                                 " exists in IL but is missing from the inventory");
                }
            }
            foreach (var writer in recorded)
            {
                if (!actual.Contains(writer))
                {
                    failures.Add(entry.Name + ": inventory claims writer " + writer +
                                 " but no IL store to this field was found");
                }
            }
        }

        TestAssert.IsTrue(checkedCount > 0, "No protected fields were checked; the inventory looks empty.");
        TestAssert.IsEmpty(failures, "Protected field inventory is out of date:\n" + string.Join("\n", failures));
    }

    [TestMethod]
    public void RecordedWriterCountsAreNonZeroForDamagedFields()
    {
        using var inventory = LoadInventory();
        var writers = inventory.RootElement.GetProperty("protectedFieldWriters");
        var empty = new List<string>();
        foreach (var entry in writers.EnumerateObject())
        {
            if (entry.Value.GetArrayLength() == 0) empty.Add(entry.Name);
        }
        // A field with no writer at all is possible (write-only via property or reflection), but
        // for the combat/construction fields A01 tracks it would mean the scan silently failed.
        TestAssert.IsEmpty(empty,
            "These protected fields recorded zero writers, which means the scan likely failed: " +
            string.Join(", ", empty));
    }

    /// <summary>
    /// Re-derives field writers from IL, mirroring the harness scanner. Kept independent of
    /// tools/authority so a bug in one implementation cannot hide a bug in the other.
    /// </summary>
    private static HashSet<string> FindWriters(Assembly assembly, FieldInfo field)
    {
        var result = new HashSet<string>();
        var module = field.Module;
        // Scan EVERY method in the assembly, not just the field's declaring type. A writer can
        // live anywhere: GameHistoryData.UnlockTechFunction writes Mecha.hp when the hp-upgrade
        // tech is unlocked, and a declaring-type-only scan would miss it (and would then wrongly
        // reject a correct inventory).
        foreach (var type in SafeTypes(assembly))
        {
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Instance | BindingFlags.Static |
                                          BindingFlags.DeclaredOnly);
            }
            catch (Exception) { continue; }

            foreach (var method in methods)
            {
                byte[] il;
                try
                {
                    var body = method.GetMethodBody();
                    if (body == null) continue;
                    il = body.GetILAsByteArray();
                    if (il == null) continue;
                }
                catch (Exception) { continue; }

                if (StoresField(il, module, field)) result.Add(type.Name + "." + method.Name);
            }
        }
        return result;
    }

    private static bool StoresField(byte[] il, Module module, FieldInfo field)
    {
        var index = 0;
        while (index < il.Length)
        {
            short value;
            var first = il[index++];
            if (first == 0xFE)
            {
                if (index >= il.Length) return false;
                value = (short)(0xFE00 | il[index++]);
            }
            else { value = first; }

            var operandSize = OperandSize(value);
            if (operandSize == InlineSwitchMarker)
            {
                // InlineSwitch: a 4-byte count followed by that many 4-byte targets. Methods with
                // a switch statement (GameHistoryData.UnlockTechFunction is one) abort a walker
                // that does not model this, which silently hides their writers.
                if (index + 4 > il.Length) return false;
                var count = BitConverter.ToInt32(il, index);
                operandSize = 4 + count * 4;
            }
            else if (operandSize < 0)
            {
                return false;
            }

            // stfld = 0x7D, stsfld = 0x80. Resolved by name at runtime so this cannot drift.
            if ((value == Stfld || value == Stsfld) && operandSize == 4 && index + 4 <= il.Length)
            {
                var token = BitConverter.ToInt32(il, index);
                try
                {
                    var resolved = module.ResolveField(token);
                    if (resolved != null &&
                        resolved.MetadataToken == field.MetadataToken &&
                        resolved.Module == field.Module &&
                        resolved.DeclaringType == field.DeclaringType)
                    {
                        return true;
                    }
                }
                catch (Exception) { /* not a field token */ }
            }
            index += operandSize;
        }
        return false;
    }

    /// <summary>Sentinel returned by <see cref="OperandSize"/> for the variable-length switch.</summary>
    private const int InlineSwitchMarker = -1;

    private static readonly short Stfld = OpCodeValue("Stfld");
    private static readonly short Stsfld = OpCodeValue("Stsfld");

    private static short OpCodeValue(string name)
    {
        var field = typeof(OpCodes).GetField(name, BindingFlags.Public | BindingFlags.Static);
        TestAssert.IsNotNull(field, "OpCodes." + name + " missing");
        return ((OpCode)field.GetValue(null)).Value;
    }

    private static int OperandSize(short value)
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(OpCode)) continue;
            var opcode = (OpCode)field.GetValue(null);
            if (opcode.Value != value) continue;
            switch (opcode.OperandType)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                case OperandType.ShortInlineBrTarget: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.InlineI:
                case OperandType.ShortInlineR: return 4;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch: return InlineSwitchMarker;
                default: return -2; // unknown: abort the walk rather than desync
            }
        }
        return -1;
    }
}
