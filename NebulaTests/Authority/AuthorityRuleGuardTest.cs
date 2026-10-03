using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using HarmonyLib;
using NebulaModel.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A05: the mode router decides from the session, and every refusal is counted and explainable.
/// </summary>
/// <remarks>
/// The guard is the executable form of "客户端不在合法 replica apply 之外修改已迁移字段" (VALIDATION
/// I01) and of "客户端非法写入计数为零". It is a pure function of
/// <see cref="AuthorityGuardSession"/>, so the whole policy is driven here without a game process.
/// </remarks>
[TestClass]
public class AuthorityRuleGuardTest
{
    private const string TickHook = AuthorityHookLabels.CombatStatTickSkillLogic;
    private const string RetiredHook = AuthorityHookLabels.ConstructionSystemResetDroneTargets;

    [TestInitialize]
    public void BeforeEach()
    {
        AuthorityRuleGuard.ResetCounters();
        AuthorityRuleGuard.ResetVerification();
        AuthorityRuleGuard.ClearVerifiers();
        AuthorityRuleGuard.Probe = null;
        AuthorityRuleGuard.RefusalSink = null;
    }

    [TestCleanup]
    public void AfterEach() => BeforeEach();

    private static void UseSession(AuthorityMode mode, bool active, bool isHost, bool applyActive) =>
        AuthorityRuleGuard.Probe = () => new AuthorityGuardSession(mode, active, isHost, applyActive);

    [TestMethod]
    public void ALegacyRoomRunsTheVanillaPath()
    {
        // The default state of every build before A25, and of single-player. Refusing here would be
        // the regression the whole card must avoid.
        AuthorityRuleGuard.Probe = null;
        TestAssert.IsTrue(AuthorityRuleGuard.AllowHostRule(TickHook));
        TestAssert.IsTrue(AuthorityRuleGuard.AllowLegacyPath(RetiredHook));
        TestAssert.AreEqual(1L, AuthorityRuleGuard.OutcomeCount(TickHook, AuthorityGuardOutcome.LegacySession));
        TestAssert.AreEqual(0L, AuthorityRuleGuard.RefusalCount(TickHook));
    }

    [TestMethod]
    public void AnAuthoritySessionWithoutAWorldStillRunsTheVanillaPath()
    {
        // Negotiated but no epoch yet: the mode is not live, so there is no host fact to diverge from.
        UseSession(AuthorityMode.HostAuthority, active: false, isHost: false, applyActive: false);
        TestAssert.IsTrue(AuthorityRuleGuard.AllowHostRule(TickHook));
        TestAssert.AreEqual(1L,
            AuthorityRuleGuard.OutcomeCount(TickHook, AuthorityGuardOutcome.NoAuthorityWorld));
    }

    [TestMethod]
    public void AClientMayNotDecideAHostFact()
    {
        UseSession(AuthorityMode.HostAuthority, active: true, isHost: false, applyActive: false);
        TestAssert.IsFalse(AuthorityRuleGuard.AllowHostRule(TickHook),
            "A client outside a replica apply must not run a host rule.");
        TestAssert.AreEqual(1L, AuthorityRuleGuard.RefusalCount(TickHook));
        TestAssert.AreEqual(1L,
            AuthorityRuleGuard.OutcomeCount(TickHook, AuthorityGuardOutcome.ClientRuleRefused));
    }

    [TestMethod]
    public void AClientInsideAReplicaApplyMayWriteItsMirror()
    {
        // The apply window is the only legal client write, and ReplicaApplyContext.Allows still
        // decides which scope; this guard only decides whether the call is allowed at all.
        UseSession(AuthorityMode.HostAuthority, active: true, isHost: false, applyActive: true);
        TestAssert.IsTrue(AuthorityRuleGuard.AllowHostRule(TickHook));
        TestAssert.AreEqual(0L, AuthorityRuleGuard.RefusalCount(TickHook));
        TestAssert.AreEqual(1L, AuthorityRuleGuard.OutcomeCount(TickHook, AuthorityGuardOutcome.ReplicaApply));
    }

    [TestMethod]
    public void TheHostIsTheRuleOwner()
    {
        UseSession(AuthorityMode.HostAuthority, active: true, isHost: true, applyActive: false);
        TestAssert.IsTrue(AuthorityRuleGuard.AllowHostRule(TickHook));
        TestAssert.AreEqual(0L, AuthorityRuleGuard.RefusalCount(TickHook));
    }

    [TestMethod]
    public void ARetiredPathIsRefusedOnEveryPeer()
    {
        // The dispatch-claim postfix target: the claim conversation is deleted, so the retired path
        // is refused on every peer in the new mode.
        UseSession(AuthorityMode.HostAuthority, active: true, isHost: true, applyActive: false);
        TestAssert.IsFalse(AuthorityRuleGuard.AllowLegacyPath(RetiredHook));
        TestAssert.AreEqual(1L, AuthorityRuleGuard.RefusalCount(RetiredHook));

        UseSession(AuthorityMode.HostAuthority, active: true, isHost: false, applyActive: true);
        TestAssert.IsFalse(AuthorityRuleGuard.AllowLegacyPath(RetiredHook),
            "A replica apply does not license a retired path.");
    }

    [TestMethod]
    public void ARetiredPathStillRunsInALegacyRoom()
    {
        AuthorityRuleGuard.Probe = null;
        TestAssert.IsTrue(AuthorityRuleGuard.AllowLegacyPath(RetiredHook));
        TestAssert.AreEqual(0L, AuthorityRuleGuard.RefusalCount(RetiredHook));
    }

    [TestMethod]
    public void RefusalsCarryTheCallSiteForDiagnosis()
    {
        // The design requires an explained failure. A refusal without a caller stack would make a
        // stray client write unattributable, which is how it survives to a release.
        UseSession(AuthorityMode.HostAuthority, active: true, isHost: false, applyActive: false);
        var lines = new List<string>();
        AuthorityRuleGuard.RefusalSink = lines.Add;

        AuthorityRuleGuard.AllowHostRule(TickHook, "target=enemy:7");

        var refusal = AuthorityRuleGuard.RecentRefusals.Single();
        TestAssert.AreEqual(TickHook, refusal.Hook);
        TestAssert.AreEqual(AuthorityGuardOutcome.ClientRuleRefused, refusal.Outcome);
        TestAssert.AreEqual("target=enemy:7", refusal.Detail);
        TestAssert.IsNotNull(refusal.Stack, "A client-rule refusal must record where it came from.");
        TestAssert.IsTrue(refusal.Stack.Contains(nameof(RefusalsCarryTheCallSiteForDiagnosis)),
            "The stack must point at the actual caller, not at the guard.");
        TestAssert.IsTrue(lines.Any(line => line.Contains(TickHook)),
            "A refusal must be reported, never silently swallowed.");
    }

    [TestMethod]
    public void TheRefusalBufferIsBounded()
    {
        // A misbehaving peer must not be able to grow the diagnostic buffer without bound.
        UseSession(AuthorityMode.HostAuthority, active: true, isHost: false, applyActive: false);
        for (var i = 0; i < AuthorityRuleGuard.RecentRefusalCapacity * 3; i++)
        {
            AuthorityRuleGuard.AllowHostRule(TickHook);
        }

        TestAssert.AreEqual(AuthorityRuleGuard.RecentRefusalCapacity,
            AuthorityRuleGuard.RecentRefusals.Count);
        TestAssert.AreEqual((long)AuthorityRuleGuard.RecentRefusalCapacity * 3,
            AuthorityRuleGuard.RefusalCount(TickHook), "Counting must continue past the buffer.");
    }

    [TestMethod]
    public void AnUnclassifiedHookIsNotGuardedByThePolicyTable()
    {
        // A05 routes the entries A01 named. A method the policy does not classify is not patched at
        // all, so it never asks the guard; this test pins that the classification is reported as
        // "none" rather than being invented, so a label typo cannot silently become a guarded rule.
        TestAssert.AreEqual(AuthorityPatchMode.None, AuthorityRuleGuard.ModeFor("Some.Other.Method"));
        TestAssert.IsFalse(AuthorityRuleGuard.IsRequired("Some.Other.Method"));
        TestAssert.IsFalse(AuthorityRuleGuard.RefusalsByHook.ContainsKey("Some.Other.Method"));

        // A legacy room keeps the vanilla path whatever the label is.
        AuthorityRuleGuard.Probe = null;
        TestAssert.IsTrue(AuthorityRuleGuard.AllowHostRule("Some.Other.Method"));
    }

    [TestMethod]
    public void ThePolicyTableClassifiesTheA01HostRules()
    {
        TestAssert.AreEqual(AuthorityPatchMode.HostRule,
            AuthorityRuleGuard.ModeFor(AuthorityHookLabels.SkillSystemDamageObject));
        TestAssert.AreEqual(AuthorityPatchMode.HostRule,
            AuthorityRuleGuard.ModeFor(AuthorityHookLabels.ConstructionSystemRepair));
        TestAssert.AreEqual(AuthorityPatchMode.LegacyRemove,
            AuthorityRuleGuard.ModeFor(AuthorityHookLabels.ConstructionSystemResetDroneTargets));
        TestAssert.IsTrue(AuthorityRuleGuard.IsRequired(AuthorityHookLabels.CombatStatHandleZeroHp));
    }

    [TestMethod]
    public void TheI01GuardsCloseTheClientWriteSurface()
    {
        // I01: every entry of the former client surface is a guarded host rule, and none of them is
        // required (the frozen 15-hook contract is untouched; the vanilla bodies must still run on
        // the host and in single-player rooms).
        foreach (var label in new[]
                 {
                     AuthorityHookLabels.PowerSystemGameTick,
                     AuthorityHookLabels.UIMechaWindowOnReplaceFuelButtonClick,
                     AuthorityHookLabels.GameHistoryDataUnlockTechFunction,
                     AuthorityHookLabels.EnemyDFGroundSystemGameTickLogicUnit,
                     AuthorityHookLabels.GameLogicEnemyGroundUnitParallel,
                     AuthorityHookLabels.EnemyDFHiveSystemGameTickLogic
                 })
        {
            TestAssert.AreEqual(AuthorityPatchMode.HostRule, AuthorityRuleGuard.ModeFor(label),
                label + " must be a host rule.");
            TestAssert.IsFalse(AuthorityRuleGuard.IsRequired(label),
                label + " must not change the frozen required-hook contract.");
        }

        // A client outside a replica apply is refused and counted; the host runs; legacy runs.
        foreach (var label in new[]
                 {
                     AuthorityHookLabels.PowerSystemGameTick,
                     AuthorityHookLabels.EnemyDFGroundSystemGameTickLogicUnit,
                     AuthorityHookLabels.GameLogicEnemyGroundUnitParallel,
                     AuthorityHookLabels.EnemyDFHiveSystemGameTickLogic
                 })
        {
            UseSession(AuthorityMode.HostAuthority, active: true, isHost: false, applyActive: false);
            AuthorityRuleGuard.ResetCounters();
            TestAssert.IsFalse(AuthorityRuleGuard.AllowHostRule(label),
                "A client must not run " + label + ".");
            TestAssert.AreEqual(1L, AuthorityRuleGuard.RefusalCount(label));

            UseSession(AuthorityMode.HostAuthority, active: true, isHost: true, applyActive: false);
            TestAssert.IsTrue(AuthorityRuleGuard.AllowHostRule(label),
                "The host must run " + label + ".");

            AuthorityRuleGuard.Probe = null;
            TestAssert.IsTrue(AuthorityRuleGuard.AllowHostRule(label),
                "Legacy must run " + label + ".");
        }
    }
}

/// <summary>
/// A05: the policy table and the A01 inventory must describe the same hook set.
/// </summary>
/// <remarks>
/// <para>
/// The classification is executable code, and the inventory is a generated artifact. If the two drift
/// apart, a hook the evidence says matters would run unguarded while every test still passed. This
/// test makes the drift a failure in both directions: a hook in the inventory that the policy forgot,
/// and a policy entry that no longer exists in the evidence.
/// </para>
/// </remarks>
[TestClass]
public class AuthorityGuardPolicyInventoryTest
{
    /// <summary>Hooks the mod guards that A01 recorded as writers rather than as target labels.</summary>
    private static readonly string[] WriterHooks =
    [
        AuthorityHookLabels.ConstructionSystemUpdateDrones,
        AuthorityHookLabels.DroneComponentInternalUpdate,
        AuthorityHookLabels.PowerSystemGameTick,
        AuthorityHookLabels.UIMechaWindowOnReplaceFuelButtonClick,
        AuthorityHookLabels.GameHistoryDataUnlockTechFunction
    ];

    /// <summary>Hook categories declared by the A05 policy file for entries A01 does not list.</summary>
    private static readonly string[] DeclaredAdditionalCategories = ["writer", "mod-side", "mod-patched"];

    private static string InventoryPath
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "docs", "host-authority",
                    "authority-hooks.json");
                if (File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }
            return null;
        }
    }

    private static string PolicyPath
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "docs", "host-authority",
                    "authority-hook-policy.json");
                if (File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }
            return null;
        }
    }

    private static HashSet<string> InventoryLabels()
    {
        var path = InventoryPath;
        TestAssert.IsNotNull(path,
            "docs/host-authority/authority-hooks.json not found; it is the A01 deliverable.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var labels = document.RootElement.GetProperty("targets").EnumerateArray()
            .Select(target => target.GetProperty("label").GetString())
            .Concat(document.RootElement.GetProperty("schedulerTargets").EnumerateArray()
                .Select(target => target.GetProperty("label").GetString()));
        return labels.ToHashSet();
    }

    private static HashSet<string> PolicyFileLabels()
    {
        var path = PolicyPath;
        TestAssert.IsNotNull(path,
            "docs/host-authority/authority-hook-policy.json not found; it is the A05 deliverable.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var labels = root.GetProperty("policy").EnumerateArray()
            .Select(entry => entry.GetProperty("label").GetString())
            .Concat(root.GetProperty("additionalHooks").EnumerateArray()
                .Select(entry => entry.GetProperty("label").GetString()));
        return labels.Where(label => label != null).Select(label => label!).ToHashSet();
    }

    private static HashSet<string> AdditionalHookLabels(string category)
    {
        var path = PolicyPath;
        TestAssert.IsNotNull(path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("additionalHooks").EnumerateArray()
            .Where(entry => entry.GetProperty("category").GetString() == category)
            .Select(entry => entry.GetProperty("label").GetString()!)
            .ToHashSet();
    }

    private static HashSet<string> RecordedWriters()
    {
        var path = InventoryPath;
        TestAssert.IsNotNull(path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var writers = new HashSet<string>();
        foreach (var entry in document.RootElement.GetProperty("protectedFieldWriters").EnumerateObject())
        {
            foreach (var writer in entry.Value.EnumerateArray())
            {
                var label = writer.GetString();
                if (label != null) writers.Add(label);
            }
        }
        return writers;
    }

    [TestMethod]
    public void EveryDeclaredHookIsInTheA01InventoryOrDeclaredAsAnAddition()
    {
        var inventory = InventoryLabels();
        var declared = PolicyFileLabels();
        var undeclared = declared
            .Where(label => !inventory.Contains(label) && !DeclaredAdditionalCategories
                .Any(category => AdditionalHookLabels(category).Contains(label)))
            .ToList();
        TestAssert.IsEmpty(undeclared,
            "The policy classifies hooks that the A01 inventory does not record and that the policy " +
            "file does not declare as additions:\n" + string.Join("\n", undeclared));
    }

    [TestMethod]
    public void EveryDeclaredWriterHookIsRecordedAsAWriterByA01()
    {
        // The "writer" category is the one addition that can be checked against the evidence: A01
        // records these methods as writers of a protected field even though it did not list them as
        // target labels. If a writer disappears from the inventory, the classification needs review.
        var writers = RecordedWriters();
        var offenders = WriterHooks.Where(label => !writers.Contains(label)).ToList();
        TestAssert.IsEmpty(offenders,
            "These hooks are declared as A01-recorded writers but the inventory no longer records them " +
            "as writers of any protected field:\n" + string.Join("\n", offenders));
    }

    [TestMethod]
    public void TheDeclaredAdditionalCategoriesAreTheOnesTheTestKnows()
    {
        // A new category must be added to the test's list, so an entry cannot be filed under an
        // invented category to get past the inventory check.
        var path = PolicyPath;
        TestAssert.IsNotNull(path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var categories = document.RootElement.GetProperty("additionalHooks").EnumerateArray()
            .Select(entry => entry.GetProperty("category").GetString()!)
            .Distinct()
            .ToList();
        TestAssert.IsTrue(categories.All(DeclaredAdditionalCategories.Contains),
            "Unknown additional-hook categories: " +
            string.Join(", ", categories.Where(category => !DeclaredAdditionalCategories.Contains(category))));
    }

    [TestMethod]
    public void EveryA01HostRuleHookIsClassifiedByThePolicyTable()
    {
        var inventory = InventoryLabels();
        var policy = PolicyFileLabels();
        var missing = inventory.Where(label => !policy.Contains(label)).ToList();
        TestAssert.IsEmpty(missing,
            "The A01 inventory records hooks the A05 policy table does not classify. An unclassified " +
            "hook is allowed to run on a client, which is the failure A05 exists to prevent:\n" +
            string.Join("\n", missing));
    }

    [TestMethod]
    public void TheExecutableTableAndTheDocumentedTableAgree()
    {
        // The JSON is the reviewable statement; the code is what runs. Both directions are checked so
        // neither can be updated without the other.
        var documented = PolicyFileLabels();
        var executable = AuthorityRuleGuard.Policy.Select(entry => entry.Label).ToHashSet();

        var onlyDocumented = documented.Except(executable).ToList();
        var onlyExecutable = executable.Except(documented).ToList();
        TestAssert.IsEmpty(onlyDocumented,
            "Documented but not enforced: " + string.Join(", ", onlyDocumented));
        TestAssert.IsEmpty(onlyExecutable,
            "Enforced but not documented: " + string.Join(", ", onlyExecutable));
    }

    [TestMethod]
    public void TheRequiredFlagsAgreeBetweenTheTableAndTheDocumentation()
    {
        // A "required" flag that only exists in the document is worse than no flag: the reviewer
        // believes the mode will refuse to load, while the running code never checks.
        var documented = RequiredFlagsFromPolicyFile();
        var offenders = new List<string>();
        foreach (var entry in AuthorityRuleGuard.Policy)
        {
            if (!documented.TryGetValue(entry.Label, out var isRequired)) continue;
            if (isRequired != entry.Required)
            {
                offenders.Add($"{entry.Label}: documented={isRequired} enforced={entry.Required}");
            }
        }
        TestAssert.IsEmpty(offenders,
            "The required flag disagrees between the policy file and the executable table:\n" +
            string.Join("\n", offenders));
    }

    [TestMethod]
    public void TheFrameBoundaryIsARequiredHook()
    {
        // The drain A04 installs is only safe because it hangs on the method A01 proved quiescent.
        // If that method vanished, the mode would run without a frame boundary, so it must be
        // required rather than merely classified.
        TestAssert.IsTrue(AuthorityRuleGuard.IsRequired(AuthorityHookLabels.ThreadManagerProcessFrame));
        TestAssert.IsTrue(AuthorityRuleGuard.IsRequired(AuthorityHookLabels.GameLogicLogicFrame));
    }

    private static Dictionary<string, bool> RequiredFlagsFromPolicyFile()
    {
        var path = PolicyPath;
        TestAssert.IsNotNull(path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var flags = new Dictionary<string, bool>();
        foreach (var entry in root.GetProperty("policy").EnumerateArray()
                     .Concat(root.GetProperty("additionalHooks").EnumerateArray()))
        {
            flags[entry.GetProperty("label").GetString()!] = entry.GetProperty("required").GetBoolean();
        }
        return flags;
    }

    [TestMethod]
    public void EveryRequiredHostRuleWritesAFieldTheInventoryTracks()
    {
        // A "required host rule" that writes nothing the inventory tracks would mean the role is
        // mis-assigned, or that A01 missed a field. Either way the classification needs review.
        var path = InventoryPath;
        TestAssert.IsNotNull(path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var writersByLabel = root.GetProperty("targets").EnumerateArray()
            .ToDictionary(target => target.GetProperty("label").GetString(),
                target => target.GetProperty("writes").GetArrayLength());

        // The inventory records writers twice: as a target with its write list, and as a field with
        // the methods that store into it. A rule can legitimately appear in only one of the two —
        // ConstructionSystem.UpdateDrones is a writer of DroneComponent.stage in the field map but is
        // not an A01 target — so the assertion asks whether the inventory knows the rule at all,
        // rather than whether one particular projection lists it.
        var writersFromFieldMap = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.GetProperty("protectedFieldWriters").EnumerateObject())
        {
            foreach (var writer in field.Value.EnumerateArray())
            {
                writersFromFieldMap.Add(writer.GetString());
            }
        }

        var offenders = new List<string>();
        foreach (var entry in AuthorityRuleGuard.RequiredPolicy)
        {
            if (entry.Mode != AuthorityPatchMode.HostRule) continue;
            if (!writersByLabel.TryGetValue(entry.Label, out var writerCount))
            {
                // Silently skipping here would hide exactly the case this assertion exists for: a
                // required host rule the inventory records nowhere.
                if (!writersFromFieldMap.Contains(entry.Label))
                {
                    offenders.Add(entry.Label + " (absent from both inventory projections)");
                }
                continue;
            }
            if (writerCount == 0) offenders.Add(entry.Label);
        }

        TestAssert.IsEmpty(offenders,
            "These hooks are required host rules but the A01 inventory records no protected field for " +
            "them, so the role looks mis-assigned:\n" + string.Join("\n", offenders));
    }

    [TestMethod]
    public void EveryRequiredHookResolvesAgainstTheInstalledAssemblies()
    {
        // This is the runtime check the mode performs before it is entered; running it here means a
        // game update that renames a method fails the build instead of failing a live room.
        var failures = AuthorityRuleGuard.VerifyRequiredHooks();
        TestAssert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public void AMissingRequiredHookIsReported()
    {
        // Negative control: the verification must actually be able to fail. A resolver that finds
        // nothing stands in for a game update that renamed the method.
        var failures = AuthorityRuleGuard.VerifyRequiredHooks(_ => null);
        TestAssert.AreEqual(AuthorityRuleGuard.RequiredPolicy.Count, failures.Count);
        TestAssert.IsTrue(failures.All(failure => failure.Contains("not found")),
            "A missing hook must be reported as such:\n" + string.Join("\n", failures));
    }
}

/// <summary>
/// A05: entering the mode is refused when a required hook or transformation is missing.
/// </summary>
[TestClass]
public class AuthorityGuardLoadGateTest
{
    [TestInitialize]
    public void BeforeEach()
    {
        AuthorityRuleGuard.ResetCounters();
        AuthorityRuleGuard.ResetVerification();
        AuthorityRuleGuard.ClearVerifiers();
    }

    [TestCleanup]
    public void AfterEach() => BeforeEach();

    [TestMethod]
    public void TheModeIsVerifiedOnceAndTheResultIsRemembered()
    {
        TestAssert.IsTrue(AuthorityRuleGuard.VerifyLoadOnce(),
            "With the installed assemblies present the mode must be enterable.");
        TestAssert.IsTrue(AuthorityRuleGuard.IsLoadVerified);
        TestAssert.IsNull(AuthorityRuleGuard.LoadFailure);
    }

    [TestMethod]
    public void AFailedVerifierStopsTheModeAndReportsWhy()
    {
        // DESIGN 1.8: a missing hook stops the new mode instead of falling back to dual simulation.
        AuthorityRuleGuard.RegisterVerifier(() => ["transpiler X never ran"]);
        TestAssert.IsFalse(AuthorityRuleGuard.VerifyLoadOnce());
        TestAssert.IsFalse(AuthorityRuleGuard.IsLoadVerified);
        TestAssert.IsTrue(AuthorityRuleGuard.LoadFailure.Contains("transpiler X never ran"));
    }

    [TestMethod]
    public void AVerifierThatThrowsIsAFailureRatherThanAnException()
    {
        // A verifier must not be able to take the whole startup path down with an unhandled throw.
        AuthorityRuleGuard.RegisterVerifier(() => throw new InvalidOperationException("verifier broke"));
        TestAssert.IsFalse(AuthorityRuleGuard.VerifyLoadOnce());
        TestAssert.IsTrue(AuthorityRuleGuard.LoadFailure.Contains("verifier broke"));
    }
}

/// <summary>
/// A05: a required method transformation must actually match the installed game assembly.
/// </summary>
/// <remarks>
/// The failure this covers cannot be caught by resolving a method name: the pattern silently matches
/// nothing, the patch reports success, and the vanilla rule keeps running. The tests run the real
/// transpilers over the real method bodies and assert the recorded match counts.
/// </remarks>
[TestClass]
public class AuthorityTranspilerGuardTest
{
    private static readonly BindingFlags Statics = BindingFlags.Static | BindingFlags.Public |
                                                   BindingFlags.NonPublic;

    [TestInitialize]
    public void BeforeEach() => NebulaPatcher.Patches.Authority.AuthorityTranspilerGuard.Reset();

    [TestCleanup]
    public void AfterEach() => BeforeEach();

    [TestMethod]
    public void TheDamageEntryTransformationsMatchTheInstalledGameAssembly()
    {
        // Invoke the real transpiler for every method it targets, so the counts under test are the
        // counts the game will see. The single transpiler method is registered against both ground
        // damage entries, and Harmony calls it once per target.
        var skillSystem = typeof(NebulaPatcher.NebulaPlugin).Assembly.GetType(
            "NebulaPatcher.Patches.Transpilers.SkillSystem_Transpiler", true)!;

        ApplyAndAssertCount(skillSystem, "DamageObject_Transpiler", typeof(SkillSystem),
            nameof(SkillSystem.DamageObject), "SkillSystem.DamageObject.playerId");
        ApplyAndAssertCount(skillSystem, "DamageGroundObject_Transpiler", typeof(SkillSystem),
            nameof(SkillSystem.DamageGroundObjectByLocalCaster),
            "SkillSystem.DamageGroundObjectByLocalCaster.playerId");
        ApplyAndAssertCount(skillSystem, "DamageGroundObject_Transpiler", typeof(SkillSystem),
            nameof(SkillSystem.DamageGroundObjectByRemoteCaster),
            "SkillSystem.DamageGroundObjectByRemoteCaster.playerId");
    }

    [TestMethod]
    public void EveryRequiredTransformationIsSatisfiedAfterTheyAllRan()
    {
        // The load gate's real input: run every transformation against the installed assembly and
        // require an empty failure list. This is what stops the mode from loading on a game update
        // that moved one of the patterns.
        var assembly = typeof(NebulaPatcher.NebulaPlugin).Assembly;
        var skillSystem = assembly.GetType("NebulaPatcher.Patches.Transpilers.SkillSystem_Transpiler", true)!;
        var module = assembly.GetType(
            "NebulaPatcher.Patches.Transpilers.ConstructionModuleComponent_Transpiler", true)!;

        RunTranspiler(skillSystem, "DamageObject_Transpiler", typeof(SkillSystem),
            nameof(SkillSystem.DamageObject));
        RunTranspiler(skillSystem, "DamageGroundObject_Transpiler", typeof(SkillSystem),
            nameof(SkillSystem.DamageGroundObjectByLocalCaster));
        RunTranspiler(skillSystem, "DamageGroundObject_Transpiler", typeof(SkillSystem),
            nameof(SkillSystem.DamageGroundObjectByRemoteCaster));
        RunTranspiler(module, "PlaceItems_Transpiler", typeof(ConstructionModuleComponent),
            nameof(ConstructionModuleComponent.PlaceItems));

        var failures = NebulaPatcher.Patches.Authority.AuthorityTranspilerGuard.VerifyRequired();
        TestAssert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public void ThePrebuildItemReportTransformationMatchesTheInstalledGameAssembly()
    {
        var transpiler = typeof(NebulaPatcher.NebulaPlugin).Assembly.GetType(
            "NebulaPatcher.Patches.Transpilers.ConstructionModuleComponent_Transpiler", true)!;
        ApplyAndAssertCount(transpiler, "PlaceItems_Transpiler", typeof(ConstructionModuleComponent),
            nameof(ConstructionModuleComponent.PlaceItems),
            "ConstructionModuleComponent.PlaceItems.itemRequired");
    }

    [TestMethod]
    public void EveryRequiredTransformationIsReportedWhenNoneApplied()
    {
        // Negative control: with no transpiler having run, every required transformation must be
        // reported. Without this, the load gate could pass because it checked nothing.
        var failures = NebulaPatcher.Patches.Authority.AuthorityTranspilerGuard.VerifyRequired();
        TestAssert.AreEqual(NebulaPatcher.Patches.Authority.AuthorityTranspilerGuard.Required.Count,
            failures.Count);
        TestAssert.IsTrue(failures.All(failure => failure.Contains("never ran")),
            string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public void AnUnexpectedMatchCountIsAFailure()
    {
        // The transformation relies on the pattern occurring exactly once. A second occurrence means
        // the game changed shape and the rewrite may now be touching the wrong branch, so it must be
        // treated as a failure rather than as a bonus match.
        var codes = new List<CodeInstruction> { new(OpCodes.Ldc_I4_1), new(OpCodes.Ldc_I4_1) };
        var label = "test.unexpected";
        TestAssert.IsFalse(NebulaPatcher.Patches.Authority.AuthorityTranspilerGuard.VerifyCount(
            label, codes, 1, exact: true, new CodeMatch(OpCodes.Ldc_I4_1)));
        TestAssert.AreEqual(2,
            NebulaPatcher.Patches.Authority.AuthorityTranspilerGuard.ObservedMatches(label));
    }

    private static void ApplyAndAssertCount(Type transpilerType, string methodName, Type targetType,
        string targetMethodName, string label)
    {
        RunTranspiler(transpilerType, methodName, targetType, targetMethodName);

        var observed = NebulaPatcher.Patches.Authority.AuthorityTranspilerGuard.ObservedMatches(label);
        TestAssert.IsTrue(observed >= 1,
            $"The transpiler {label} recorded {observed} matches; the transformation did not apply to " +
            "the installed game assembly.");
    }

    /// <summary>
    /// Runs one transpiler over the real method body and asserts it produced a rewritten body.
    /// </summary>
    private static void RunTranspiler(Type transpilerType, string methodName, Type targetType,
        string targetMethodName)
    {
        var transpiler = transpilerType.GetMethod(methodName, Statics);
        TestAssert.IsNotNull(transpiler, transpilerType.Name + "." + methodName + " not found");

        var target = AccessTools.Method(targetType, targetMethodName);
        TestAssert.IsNotNull(target, "Missing " + targetType.Name + "." + targetMethodName);
        var body = target.GetMethodBody()?.GetILAsByteArray();
        TestAssert.IsGreaterThan(5, body?.Length ?? 0,
            targetType.Name + "." + targetMethodName + " is a reference stub; run with the real game assembly");

        var instructions = PatchProcessor.GetOriginalInstructions(target, null);
        TestAssert.IsNotEmpty(instructions,
            "No instructions read for " + targetType.Name + "." + targetMethodName);

        object[] arguments = transpiler.GetParameters().Length == 1
            ? [instructions]
            : [instructions, target];

        var result = ((System.Collections.IEnumerable)transpiler.Invoke(null, arguments)!)
            .Cast<CodeInstruction>().ToList();
        TestAssert.IsNotEmpty(result, "The transpiler produced no instructions.");
    }
}
