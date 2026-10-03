using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace AuthorityBaseline;

/// <summary>
/// A01 write-entry scanner.
///
/// DESIGN §1 requires "every mutable field has exactly one rule writer". Before that can be
/// enforced the writers have to be enumerated from the real assembly rather than from a reading
/// of the source. This walks the installed game types and reports, per protected field, every
/// method whose IL stores into it.
///
/// Static reflection only: it reads metadata, never invokes game code.
/// </summary>
internal static class WriteScanner
{
    /// <summary>
    /// Fields DESIGN §1 protects. The list is explicit on purpose: a scanner that guessed which
    /// fields matter would hide exactly the writers A01 exists to surface.
    /// </summary>
    private static readonly (string Type, string Field, string Reason)[] ProtectedFields =
    {
        ("CombatStat", "hp", "enemy/building/player hp; the field the two simulators disagree on (E01)"),
        ("CombatStat", "hpMax", "max hp; scaling/technology input to every hp comparison"),
        ("CombatStat", "hpRecover", "vanilla regen rate; the real source of observed hp increases"),
        ("CombatStat", "hpIncoming", "pending damage accumulation; must not leak between casts"),
        ("CombatStat", "warningId", "damage warning lifecycle; a second writer means duplicate UI"),
        ("ConstructStat", "damageRegister", "accumulated damage; drives repair eligibility (E04)"),
        ("ConstructStat", "damageRate", "damage per second; repair scoring input"),
        ("ConstructStat", "repairerCount", "repairer occupancy; the count clients currently mutate (E05)"),
        ("ConstructStat", "repairerModuleId", "which owner holds the repair slot"),
        ("ConstructStat", "repairerValue", "repair priority value"),
        ("EntityData", "combatStatId", "entity -> combat stat reference; cleared by HandleFullHp (E06)"),
        ("EntityData", "constructStatId", "entity -> construct stat reference (E06)"),
        ("EntityData", "stateFlags", "building state bits written by many systems"),
        ("EnemyData", "combatStatId", "enemy -> combat stat reference"),
        // isInvincible/isAssaultingUnit/willBroadcast/counterAttack are bit flags packed into
        // stateFlags (see EnemyData.cs), not separate fields. Tracking stateFlags covers them.
        ("EnemyData", "stateFlags", "enemy state bits incl. isInvincible (0x80) and isAssaultingUnit (0x40)"),
        ("DroneComponent", "stage", "drone lifecycle stage; drives the whole repair state machine"),
        ("DroneComponent", "targetObjectId", "drone target; a second writer re-targets the drone"),
        ("DroneComponent", "owner", "drone owner; vanilla only understands a single player (E07)"),
        ("DroneComponent", "priority", "drone priority ordering"),
        ("BattleBaseComponent", "energy", "battle base energy; the repair dispatch gate found in A00"),
        // droneCount/droneIdleCount are properties over _droneCount/_droneIdleCount.
        ("ConstructionModuleComponent", "_droneIdleCount", "idle drone budget; shared with build claims"),
        ("ConstructionModuleComponent", "_droneCount", "drone capacity; changes with upgrades/tech"),
        ("ConstructionModuleComponent", "_droneAliveCount", "alive drone count; mid-flight accounting"),
        ("Mecha", "coreEnergy", "mecha energy; spent by flight, repair, and weapons"),
        ("Mecha", "reactorEnergy", "mecha reactor energy; the other half of the energy budget"),
        // Player.sandCount and Player.inhandItemCount are auto-properties whose backing fields
        // are compiler-generated ("<name>k__BackingField") and therefore not addressable here.
        // Their only writers are the property setters inside Player, so scanning them would
        // report nothing useful; A24 audits them through the setter call sites instead.
        // Field names verified against Mecha.cs. The remote-player laser energy that
        // CombatManager.ShootTarget forces to int.MaxValue (E02) lives in the mod's
        // CombatManager, not here; the game-side counterpart is the mecha energy pair below.
        ("Mecha", "energyShieldEnergy", "mecha shield energy; drained by shield hits (E02)"),
        ("Mecha", "hp", "mecha hp; the player-side twin of CombatStat.hp"),
        ("Mecha", "hpRecover", "mecha regen rate"),
        ("Mecha", "ammoBulletCount", "loaded bullet count backing the primary weapon"),
        ("Mecha", "bombFire", "bomb firing state"),
        ("Mecha", "energyShieldBurstProgress", "shield burst charge; consumed on release"),
        ("SkillSystem", "playerAlive", "shared player-state switch used during damage resolution"),
        ("SkillSystem", "playerIsSailing", "shared player-state switch; affects threat calculations"),
        ("SkillSystem", "playerIsWarping", "shared player-state switch; affects threat calculations"),
        // CombatManager.PlayerId lives in the Nebula mod assembly, not the game assembly, so it
        // is not scannable here. The game-side equivalent is SkillSystem's player-state switches
        // above (playerAlive / playerIsSailing / playerIsWarping), which are tracked and which
        // SkillSystem_Patch already mutates around damage resolution.
    };

    /// <summary>Types whose callable mutation surface an adapter author needs.</summary>
    private static readonly string[] WatchedTypeNames =
    {
        "CombatStat", "ConstructStat", "DroneComponent", "BattleBaseComponent", "ConstructionModuleComponent",
        "EnemyData", "EntityData", "Mecha", "Player", "PlayerAction_Combat", "SkillSystem",
        "ConstructionSystem", "EnemyDFGroundSystem", "EnemyDFHiveSystem", "SpaceSector", "DFGBaseComponent",
        "CraftData", "FactorySystem", "PowerSystem", "PlanetFactory",
    };

    /// <summary>Methods whose IL could not be walked. Surfaced so a false negative is visible.</summary>
    private static int walkFailures;

    /// <summary>
    /// Operand sizes derived from the runtime's own opcode definitions. A hand-written table
    /// silently desyncs the IL walk when a size is wrong, which makes the scanner report
    /// "no writers" for fields that do have writers - the most dangerous failure mode for an
    /// inventory whose whole job is to prove writer coverage.
    /// </summary>
    private static readonly Dictionary<short, int> OperandSizes = BuildOperandSizes();

    /// <summary>
    /// Opcode values resolved by name from the runtime. Hardcoding the hex values is what made
    /// the first version of this scanner report zero writers for every field: stfld is 0x7D, not
    /// the 0x80 that was written down, and a wrong value silently matches nothing.
    /// </summary>
    private static readonly short OpStfld = OpCodeValue("stfld");
    private static readonly short OpStsfld = OpCodeValue("stsfld");
    private static readonly HashSet<short> ArrayStoreOpcodes = BuildArrayStoreOpcodes();

    private static HashSet<short> BuildArrayStoreOpcodes()
    {
        var set = new HashSet<short>();
        foreach (var name in new[] { "stelem.i1", "stelem.i2", "stelem.i4", "stelem.i8", "stelem.r4", "stelem.r8", "stelem.ref" })
        {
            var value = OpCodeValueOrNull(name);
            if (value.HasValue) set.Add(value.Value);
        }
        return set;
    }

    /// <summary>
    /// Resolves an opcode by name. Returns null instead of throwing so one renamed opcode cannot
    /// take down the whole scanner through a type-initializer failure.
    /// </summary>
    private static short? OpCodeValueOrNull(string name)
    {
        // OpCodes exposes its fields in PascalCase ("Stfld"), while the IL mnemonic is lowercase
        // ("stfld"). Match case-insensitively so callers can use the mnemonic they read in IL.
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(OpCode)) continue;
            if (!string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            return ((OpCode)field.GetValue(null)).Value;
        }
        return null;
    }

    private static short OpCodeValue(string name)
    {
        var value = OpCodeValueOrNull(name);
        if (!value.HasValue) throw new InvalidOperationException("Unknown opcode: " + name);
        return value.Value;
    }

    private static Dictionary<short, int> BuildOperandSizes()
    {
        var sizes = new Dictionary<short, int>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(OpCode)) continue;
            var opcode = (OpCode)field.GetValue(null);
            sizes[opcode.Value] = OperandSizeOf(opcode.OperandType);
        }
        return sizes;
    }

    /// <summary>
    /// Reports the resolved opcode values in the scan output. Without this, a wrong lookup would
    /// again produce a silent zero-writer report instead of a visible misconfiguration.
    /// </summary>
    private static string OpcodeSummary()
    {
        return "stfld=0x" + OpStfld.ToString("X2") +
               " stsfld=0x" + OpStsfld.ToString("X2") +
               " arrayStores=" + ArrayStoreOpcodes.Count;
    }

    private static int OperandSizeOf(OperandType type)
    {
        switch (type)
        {
            case OperandType.InlineNone: return 0;
            case OperandType.ShortInlineI: return 1;
            case OperandType.ShortInlineVar: return 1;
            case OperandType.ShortInlineBrTarget: return 1;
            case OperandType.InlineVar: return 2;
            case OperandType.InlineBrTarget: return 4;
            case OperandType.InlineField: return 4;
            case OperandType.InlineMethod: return 4;
            case OperandType.InlineSig: return 4;
            case OperandType.InlineString: return 4;
            case OperandType.InlineTok: return 4;
            case OperandType.InlineType: return 4;
            case OperandType.InlineI: return 4;
            case OperandType.InlineSwitch: return -1; // variable length, handled by the walker
            case OperandType.InlineI8: return 8;
            case OperandType.InlineR: return 8;
            case OperandType.ShortInlineR: return 4;
            default: return -2; // unknown: abort the walk instead of desyncing
        }
    }

    private struct IlInstruction
    {
        public short OpcodeValue;
        public int OperandOffset;
        public int OperandSize;
    }

    /// <summary>
    /// Walks IL and returns each instruction, or null when the walk cannot be completed
    /// (unknown operand size). Returning null lets callers report "unknown" rather than
    /// silently concluding "no writers".
    /// </summary>
    private static List<IlInstruction> ReadIl(byte[] il)
    {
        if (il == null) return null;
        var result = new List<IlInstruction>();
        var index = 0;
        while (index < il.Length)
        {
            short value;
            var first = il[index++];
            if (first == 0xFE)
            {
                if (index >= il.Length) return null;
                value = (short)(0xFE00 | il[index++]);
            }
            else
            {
                value = first;
            }
            if (!OperandSizes.TryGetValue(value, out var size)) return null;
            if (size == -1)
            {
                // InlineSwitch: a 4-byte count followed by that many 4-byte branch targets.
                if (index + 4 > il.Length) return null;
                var count = BitConverter.ToInt32(il, index);
                size = 4 + count * 4;
            }
            else if (size < 0)
            {
                return null;
            }
            result.Add(new IlInstruction
            {
                OpcodeValue = value,
                OperandOffset = index,
                OperandSize = size
            });
            index += size;
        }
        return result;
    }

    /// <summary>
    /// True when the IL contains stfld (0x80) or stsfld (0x81) whose metadata token is the
    /// given field's token. Comparing tokens rather than names makes the match exact.
    /// </summary>
    private static bool StoresField(byte[] il, Module module, FieldInfo field, out bool walkOk)
    {
        var instructions = ReadIl(il);
        if (instructions == null)
        {
            walkOk = false;
            return false;
        }
        walkOk = true;
        foreach (var instruction in instructions)
        {
            if (instruction.OpcodeValue != OpStfld && instruction.OpcodeValue != OpStsfld) continue;
            if (instruction.OperandSize != 4) continue;
            if (instruction.OperandOffset + 4 > il.Length) continue;
            var token = BitConverter.ToInt32(il, instruction.OperandOffset);
            // IL field operands are metadata tokens that may be FieldDef (same module) or
            // MemberRef (cross-module). Resolving through the module handles both; comparing
            // the raw token against FieldInfo.MetadataToken silently matches nothing whenever
            // the reference is a MemberRef.
            FieldInfo resolved;
            try
            {
                resolved = module.ResolveField(token);
            }
            catch (Exception)
            {
                continue;
            }
            if (resolved == null) continue;
            if (resolved.MetadataToken == field.MetadataToken &&
                resolved.Module == field.Module &&
                resolved.DeclaringType == field.DeclaringType)
            {
                return true;
            }
        }
        return false;
    }

    public static void Dump(string directory, string role)
    {
        var path = Path.Combine(directory, role + "-writescan.txt");
        var sb = new StringBuilder();
        var assembly = typeof(GameMain).Assembly;
        sb.AppendLine("role=" + role);
        sb.AppendLine("assembly=" + assembly.Location);
        sb.AppendLine();

        var allMethods = CollectMethods(assembly);
        sb.AppendLine("methodsScanned=" + allMethods.Count);
        sb.AppendLine();

        // Self-test: CombatStat.TickSkillLogic is known to store hpRecover/hp (E01). If the
        // scanner cannot see that writer, every "writerCount: 0" below is meaningless, so say
        // so loudly rather than reporting a false all-clear.
        var selfTest = RunSelfTest(assembly, allMethods);
        sb.AppendLine("selfTest=" + selfTest);
        sb.AppendLine("opcodes=" + OpcodeSummary());
        sb.AppendLine();

        sb.AppendLine("=== protected field writers ===");
        sb.AppendLine("Each row is a method whose IL stores into the field. More than one writer means");
        sb.AppendLine("the DESIGN 1 single-writer rule needs an explicit owner decision for that field.");
        sb.AppendLine();
        foreach (var (typeName, fieldName, reason) in ProtectedFields)
        {
            var type = assembly.GetType(typeName, throwOnError: false);
            if (type == null)
            {
                sb.AppendLine("## " + typeName + "." + fieldName + "  [TYPE NOT FOUND]");
                sb.AppendLine();
                continue;
            }
            var field = type.GetField(fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (field == null)
            {
                sb.AppendLine("## " + typeName + "." + fieldName + "  [FIELD NOT FOUND]");
                sb.AppendLine();
                continue;
            }
            sb.AppendLine("## " + typeName + "." + fieldName + "  (" + field.FieldType.Name + ")");
            sb.AppendLine("   why: " + reason);
            var sites = FindWriters(allMethods, field);
            sb.AppendLine("   writerCount: " + sites.Count);
            foreach (var site in sites)
            {
                sb.AppendLine("      " + site.TypeName + "." + site.MethodName + "(" + site.Parameters + ")");
            }
            sb.AppendLine();
        }

        sb.AppendLine("=== public/protected mutation surface of watched types ===");
        sb.AppendLine("Fields excluded; this lists callable entry points an adapter would use.");
        sb.AppendLine();
        foreach (var typeName in WatchedTypeNames)
        {
            var type = assembly.GetType(typeName, throwOnError: false);
            if (type == null)
            {
                sb.AppendLine("## " + typeName + "  [TYPE NOT FOUND]");
                sb.AppendLine();
                continue;
            }
            sb.AppendLine("## " + type.FullName);
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Instance | BindingFlags.Static |
                                          BindingFlags.DeclaredOnly);
            Array.Sort(methods, (a, b) => string.CompareOrdinal(a.Name, b.Name));
            var listed = 0;
            foreach (var method in methods)
            {
                if (method.IsSpecialName) continue;
                if (method.Name.StartsWith("get_", StringComparison.Ordinal) ||
                    method.Name.StartsWith("set_", StringComparison.Ordinal)) continue;
                if (!MethodMayMutate(method)) continue;
                sb.AppendLine("   " + Visibility(method) + " " + (method.IsStatic ? "static " : "") +
                              TypeName(method.ReturnType) + " " + method.Name +
                              "(" + Parameters(method) + ")");
                listed++;
            }
            if (listed == 0) sb.AppendLine("   (no mutating methods)");
            sb.AppendLine();
        }

        sb.AppendLine("=== scanner health ===");
        sb.AppendLine("ilWalkFailures=" + walkFailures);
        sb.AppendLine("hookFailures=" + BaselineRuntime.HookFailures);
        lock (BaselineRuntime.HookReport)
        {
            foreach (var line in BaselineRuntime.HookReport) sb.AppendLine(line);
        }

        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>
    /// Proves the IL walk and token matching work by finding writers that are known to exist.
    /// Each anchor is a (type, field, method) triple verified by hand in the decompiled source,
    /// so a scanner that cannot see them is provably broken rather than merely unlucky.
    /// Returns a human-readable verdict included at the top of every scan.
    /// </summary>
    private static string RunSelfTest(Assembly assembly, List<MethodEntry> methods)
    {
        // Each anchor was read out of the decompiled source, not assumed:
        //   CombatStat.cs TickSkillLogic:   hp += hpRecover; hp = hpMax;
        //   CombatStat.cs HandleFullHp:     entityPool[objectId].combatStatId = 0   (the E06 wipe)
        //   ConstructStat.cs RefreshDamageRate (called by GameTick): damageRegister = 0
        var anchors = new[]
        {
            ("CombatStat", "hp", "TickSkillLogic"),
            ("EntityData", "combatStatId", "HandleFullHp"),
            ("ConstructStat", "damageRegister", "RefreshDamageRate"),
        };
        var failures = new List<string>();
        var checkedCount = 0;
        foreach (var (typeName, fieldName, methodName) in anchors)
        {
            var type = assembly.GetType(typeName, throwOnError: false);
            if (type == null) { failures.Add(typeName + " type missing"); continue; }
            var field = type.GetField(fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (field == null) { failures.Add(typeName + "." + fieldName + " missing"); continue; }
            var writers = FindWriters(methods, field);
            var found = false;
            foreach (var writer in writers)
            {
                if (writer.MethodName == methodName) found = true;
            }
            checkedCount++;
            if (!found)
            {
                failures.Add(typeName + "." + fieldName + " writer " + methodName +
                             " not detected (found " + writers.Count + " writers)");
            }
        }
        if (failures.Count > 0)
        {
            return "FAIL (" + string.Join("; ", failures) + ") - all writerCount values below are unreliable";
        }
        return "PASS (" + checkedCount + " known writer anchors detected)";
    }

    private sealed class MethodEntry
    {
        public Type Type;
        public MethodInfo Method;
    }

    private static List<MethodEntry> CollectMethods(Assembly assembly)
    {
        var list = new List<MethodEntry>();
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            types = error.Types;
        }
        foreach (var type in types)
        {
            if (type == null) continue;
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Instance | BindingFlags.Static |
                                          BindingFlags.DeclaredOnly);
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var method in methods)
            {
                list.Add(new MethodEntry { Type = type, Method = method });
            }
        }
        return list;
    }

    private sealed class WriteSite
    {
        public string TypeName;
        public string MethodName;
        public string Parameters;
    }

    private static List<WriteSite> FindWriters(List<MethodEntry> methods, FieldInfo field)
    {
        var sites = new List<WriteSite>();
        var module = field.Module;
        foreach (var entry in methods)
        {
            byte[] il;
            try
            {
                var body = entry.Method.GetMethodBody();
                if (body == null) continue;
                il = body.GetILAsByteArray();
                if (il == null) continue;
            }
            catch (Exception)
            {
                continue;
            }
            if (!StoresField(il, module, field, out var walkOk))
            {
                if (!walkOk) walkFailures++;
                continue;
            }
            sites.Add(new WriteSite
            {
                TypeName = entry.Type.Name,
                MethodName = entry.Method.Name,
                Parameters = Parameters(entry.Method)
            });
        }
        return sites;
    }

    /// <summary>
    /// Over-approximates: true when the method's IL contains any field or array-element store.
    /// Over-approximating is the safe direction for an inventory that must not miss a writer.
    /// </summary>
    private static bool MethodMayMutate(MethodInfo method)
    {
        try
        {
            var body = method.GetMethodBody();
            if (body == null) return false;
            var instructions = ReadIl(body.GetILAsByteArray());
            if (instructions == null) return true; // unknown: list it rather than hide it
            foreach (var instruction in instructions)
            {
                if (instruction.OpcodeValue == OpStfld ||
                    instruction.OpcodeValue == OpStsfld ||
                    ArrayStoreOpcodes.Contains(instruction.OpcodeValue))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            return true;
        }
        return false;
    }

    private static string Visibility(MethodInfo method)
    {
        if (method.IsPublic) return "public";
        if (method.IsFamily) return "protected";
        if (method.IsAssembly) return "internal";
        if (method.IsFamilyOrAssembly) return "protected internal";
        return "private";
    }

    private static string Parameters(MethodInfo method)
    {
        var parts = new List<string>();
        foreach (var parameter in method.GetParameters())
        {
            parts.Add(TypeName(parameter.ParameterType) + " " + parameter.Name);
        }
        return string.Join(", ", parts);
    }

    private static string TypeName(Type type)
    {
        if (type == null) return "void";
        if (!type.IsGenericType) return type.Name;
        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick > 0) name = name.Substring(0, tick);
        var args = new List<string>();
        foreach (var argument in type.GetGenericArguments()) args.Add(TypeName(argument));
        return name + "<" + string.Join(",", args) + ">";
    }
}
