#region

using System;
using System.Collections.Generic;
using System.Linq;
using NebulaModel.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

#endregion

namespace NebulaTests.Authority;

/// <summary>
/// A25: the frozen G1 contract and the executable release gate.
/// </summary>
/// <remarks>
/// Two jobs are tested here, and they fail in different ways if left undone. The contract test makes
/// "frozen" mean "a silent change breaks the build": the expected numbers are written as literals in
/// this file, not read back from the contract, because comparing the contract against itself would
/// pass no matter what the contract said. The gate tests pin the property that matters most — an
/// unmeasured requirement is never a pass — by driving partial evidence and asserting the status of
/// every item rather than just the overall verdict.
/// </remarks>
[TestClass]
public class AuthorityReleaseGateTest
{
    // ---- frozen contract -----------------------------------------------------------------

    [TestMethod]
    public void TheFrozenContractMatchesTheLiveCode()
    {
        var drift = AuthorityReleaseContract.VerifyLive();
        TestAssert.AreEqual(0, drift.Count,
            "The released contract drifted from the code: " + string.Join("; ", drift) +
            ". A deliberate change is made in AuthorityReleaseContract and recorded in RELEASE-G1.md.");
    }

    [TestMethod]
    public void TheFrozenNumbersAreTheOnesThisReleaseVerified()
    {
        // Literals on purpose: this is the freeze. If these change, the release notes and the
        // validation that used them have to be revisited, which is exactly what should be hard.
        TestAssert.AreEqual(3, AuthorityReleaseContract.ProtocolVersion);
        TestAssert.AreEqual(2, AuthorityReleaseContract.LegacyProtocolVersion);
        TestAssert.AreEqual(1, (int)AuthorityReleaseContract.SchemaVersion);
        TestAssert.AreEqual((uint)AuthorityCapability.All, AuthorityReleaseContract.Capabilities);
        TestAssert.AreEqual("0.10.35.29104", AuthorityReleaseContract.GameVersion);
        TestAssert.AreEqual("6C122E5443E6843979B4064050DFCB5E0D75577A0B64F6AE4111290238B33C12",
            AuthorityReleaseContract.GameDllSha256);
        TestAssert.AreEqual(15, AuthorityReleaseContract.RequiredHookCount);
        TestAssert.AreEqual(0x40F4CB8FC785CD44UL, AuthorityReleaseContract.RequiredHookFingerprint);

        var expected = new List<(string, long)>
        {
            ("ChunkMaxBytes", 64L * 1024),
            ("CommandPayloadMaxBytes", 16L * 1024),
            ("ScopeSnapshotMaxBytes", 64L * 1024 * 1024),
            ("ConnectionPendingMaxBytes", 128L * 1024 * 1024),
            ("CommandQueueMax", 1024L),
            ("ScopesPerSubscriberMax", 16L),
            ("PendingDigestsPerScopeMax", 4L),
            ("StateRecordMaxBytes", 256L),
            ("StateRecordCountMax", 4096L),
            ("LifecycleRecordCountMax", 4096L),
        };
        TestAssert.AreEqual(expected.Count, AuthorityReleaseContract.FrozenLimits.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            TestAssert.AreEqual(expected[i].Item1, AuthorityReleaseContract.FrozenLimits[i].Name);
            TestAssert.AreEqual(expected[i].Item2, AuthorityReleaseContract.FrozenLimits[i].Value,
                "Frozen limit " + expected[i].Item1 + " changed.");
        }
    }

    [TestMethod]
    public void TheHookFingerprintPinsTheRequiredSetAndIgnoresOrder()
    {
        var labels = AuthorityRuleGuard.RequiredPolicy.Select(entry => entry.Label).ToList();
        TestAssert.AreEqual(AuthorityReleaseContract.RequiredHookCount, labels.Count);
        TestAssert.AreEqual(AuthorityReleaseContract.RequiredHookFingerprint,
            AuthorityReleaseContract.FingerprintOf(labels),
            "The required hook set in the policy table is not the frozen one.");

        TestAssert.AreEqual(AuthorityReleaseContract.FingerprintOf(["a", "b"]),
            AuthorityReleaseContract.FingerprintOf(["b", "a"]), "The fingerprint must not depend on order.");
        TestAssert.AreNotEqual(AuthorityReleaseContract.FingerprintOf(["a", "b"]),
            AuthorityReleaseContract.FingerprintOf(["a", "b", "c"]), "A missing hook must change the value.");
        TestAssert.AreNotEqual(AuthorityReleaseContract.FingerprintOf(["ab", "c"]),
            AuthorityReleaseContract.FingerprintOf(["a", "bc"]), "Labels must not run together.");
    }

    // ---- gate: the default is never a pass -----------------------------------------------

    [TestMethod]
    public void UnmeasuredEvidenceBlocksAndNeverPasses()
    {
        var report = AuthorityReleaseGate.Evaluate(new AuthorityGateEvidence());

        TestAssert.IsFalse(report.Passed, "An empty evidence set cannot pass the G1 gate.");
        TestAssert.IsFalse(report.MayEnableForUsers, "The mode stays behind the flag until the gate passes.");
        TestAssert.AreEqual(0, report.PassedCount, "No item may pass without evidence.");
        TestAssert.IsTrue(report.BlockedCount >= 20, "Unmeasured items must block, not silently pass.");

        var blocked = report.Items.Where(i => i.Status == AuthorityGateStatus.Blocked).Select(i => i.Id).ToList();
        foreach (var id in new[]
                 {
                     "contract-frozen", "cards-complete", "hooks-resolve", "single-player-regression",
                     "multiplayer-e2e", "fault-matrix", "invariant-I01", "invariant-I02", "invariant-I07",
                     "invariant-I12", "no-forbidden-paths", "host-conditions",
                     "observer-neutral", "scale", "cost-budget", "soak", "unmigrated-documented"
                 })
        {
            TestAssert.IsTrue(blocked.Contains(id), id + " must block when it was not measured.");
        }

        // The one item that is affirmatively wrong in a legacy-default build is the ungating check.
        TestAssert.AreEqual(AuthorityGateStatus.Fail,
            report.Items.Single(i => i.Id == "mode-gated").Status);
        StringAssert.Contains(report.Verdict, "NOT PASSED");
        StringAssert.Contains(report.Verdict, "mode stays behind the development launch flag");

        // Blocked is not a pass even when nothing failed: this is the rule that stops an unmeasured
        // requirement from being released on the grounds that nothing contradicted it.
        var noFailures = new AuthorityGateEvidence { ModeRequiresLaunchFlag = true };
        var blockedOnly = AuthorityReleaseGate.Evaluate(noFailures);
        TestAssert.AreEqual(0, blockedOnly.FailedCount);
        TestAssert.IsTrue(blockedOnly.BlockedCount > 0);
        TestAssert.IsFalse(blockedOnly.Passed, "A gate with blocked items must never report a pass.");
        TestAssert.IsFalse(blockedOnly.MayEnableForUsers);
    }

    [TestMethod]
    public void TheMissingCardIsNamedAndA24IsTheReasonToday()
    {
        // The real state of the workspace when A25 was implemented: A00–A23 are recorded, A24 is
        // not. TASKS A25 names A22/A23/A24 as the precondition for ungating, so the report has to
        // say which card is missing rather than "not complete".
        var evidence = new AuthorityGateEvidence
        {
            ContractChecked = true,
            CompletedCards = AuthorityGateEvidence.RequiredCards.Where(c => c != "A23").ToArray()
        };
        var item = AuthorityReleaseGate.Evaluate(evidence).Items.Single(i => i.Id == "cards-complete");

        TestAssert.AreEqual(AuthorityGateStatus.Blocked, item.Status);
        StringAssert.Contains(item.Evidence, "A23");
        TestAssert.IsFalse(AuthorityReleaseGate.Evaluate(evidence).MayEnableForUsers);
    }

    // ---- gate: satisfiable ---------------------------------------------------------------

    [TestMethod]
    public void CompleteEvidencePassesTheGate()
    {
        var report = AuthorityReleaseGate.Evaluate(Passing());

        TestAssert.IsTrue(report.Passed, "The gate must be satisfiable: " + report.Verdict);
        TestAssert.IsTrue(report.MayEnableForUsers);
        TestAssert.AreEqual(report.Items.Count, report.PassedCount);
        StringAssert.Contains(report.Verdict, "PASS");
    }

    [TestMethod]
    public void EveryItemIsExercisedByThePassingEvidence()
    {
        // A gate item that can never pass would make the release unreachable; a gate item that can
        // never fail would make it meaningless. This pins that the passing set turns every id green.
        var report = AuthorityReleaseGate.Evaluate(Passing());
        var ids = new HashSet<string>(report.Items.Select(i => i.Id));
        TestAssert.AreEqual(26, ids.Count, "The passing evidence must cover every gate item.");
        foreach (var item in report.Items) TestAssert.AreEqual(AuthorityGateStatus.Pass, item.Status, item.Id);
    }

    // ---- gate: the two ways to not pass --------------------------------------------------

    [TestMethod]
    public void ABudgetOverrunFailsAndAsksForARevisionWithItsBasis()
    {
        var evidence = Passing();
        evidence.CaptureP95Ms = 5.0;
        var report = AuthorityReleaseGate.Evaluate(evidence);

        var item = report.Items.Single(i => i.Id == "cost-budget");
        TestAssert.AreEqual(AuthorityGateStatus.Fail, item.Status,
            "A measured overrun is a fact, not a missing measurement.");
        StringAssert.Contains(item.Evidence, "revision");
        TestAssert.IsFalse(report.Passed);
    }

    [TestMethod]
    public void AFailedSinglePlayerSuiteFailsAndAMismatchedDigestFails()
    {
        var regressed = Passing();
        regressed.SinglePlayerTestsFailed = 1;
        TestAssert.AreEqual(AuthorityGateStatus.Fail,
            AuthorityReleaseGate.Evaluate(regressed).Items.Single(i => i.Id == "single-player-regression").Status);

        var diverged = Passing();
        diverged.DigestsMismatched = 1;
        TestAssert.AreEqual(AuthorityGateStatus.Fail,
            AuthorityReleaseGate.Evaluate(diverged).Items.Single(i => i.Id == "multiplayer-e2e").Status);
    }

    [TestMethod]
    public void AForbiddenPathFailsAndAnUnauditedExitTableBlocks()
    {
        var present = Passing();
        present.ForbiddenPathsDetected = ["client damage packet accepted"];
        present.ForbiddenPathsChecked = true;
        var failed = AuthorityReleaseGate.Evaluate(present).Items.Single(i => i.Id == "no-forbidden-paths");
        TestAssert.AreEqual(AuthorityGateStatus.Fail, failed.Status);
        StringAssert.Contains(failed.Evidence, "client damage packet accepted");

        var unaudited = Passing();
        unaudited.ForbiddenPathsChecked = false;
        TestAssert.AreEqual(AuthorityGateStatus.Blocked,
            AuthorityReleaseGate.Evaluate(unaudited).Items.Single(i => i.Id == "no-forbidden-paths").Status);
    }

    [TestMethod]
    public void AShortSoakBlocksWhileGrowthInALongRunFails()
    {
        var shortSoak = Passing();
        shortSoak.SoakHours = 0.5;
        TestAssert.AreEqual(AuthorityGateStatus.Blocked,
            AuthorityReleaseGate.Evaluate(shortSoak).Items.Single(i => i.Id == "soak").Status);

        var leaky = Passing();
        leaky.SoakHours = 3.0;
        leaky.NoSustainedMemoryGrowth = false;
        TestAssert.AreEqual(AuthorityGateStatus.Fail,
            AuthorityReleaseGate.Evaluate(leaky).Items.Single(i => i.Id == "soak").Status);
    }

    [TestMethod]
    public void ScaleAndHostConditionsAndInvariantsEachBlockOnTheirOwnShortfall()
    {
        var small = Passing();
        small.MaxCombatObjects = 999;
        var scale = AuthorityReleaseGate.Evaluate(small).Items.Single(i => i.Id == "scale");
        TestAssert.AreEqual(AuthorityGateStatus.Blocked, scale.Status);
        StringAssert.Contains(scale.Evidence, "combatObjects=999/1000");

        var noHeadless = Passing();
        noHeadless.CoveredHostConditions = ["host-remote-planet", "host-death"];
        var conditions = AuthorityReleaseGate.Evaluate(noHeadless).Items.Single(i => i.Id == "host-conditions");
        TestAssert.AreEqual(AuthorityGateStatus.Blocked, conditions.Status);
        StringAssert.Contains(conditions.Evidence, "headless");

        var missingInvariant = Passing();
        missingInvariant.EstablishedInvariants = AuthorityInvariantCoverageTable.Established
            .Where(id => id != "I07").ToArray();
        var invariant = AuthorityReleaseGate.Evaluate(missingInvariant).Items.Single(i => i.Id == "invariant-I07");
        TestAssert.AreEqual(AuthorityGateStatus.Blocked, invariant.Status);
        StringAssert.Contains(invariant.Evidence, "no evidence");
    }

    [TestMethod]
    public void AHookFailureOrAnUnverifiedBuildBlocksTheHookItem()
    {
        var failures = Passing();
        failures.HookInstallFailures = 1;
        TestAssert.AreEqual(AuthorityGateStatus.Fail,
            AuthorityReleaseGate.Evaluate(failures).Items.Single(i => i.Id == "hooks-resolve").Status);

        var wrongBuild = Passing();
        wrongBuild.VerifiedGameDllSha256 = "0000";
        var item = AuthorityReleaseGate.Evaluate(wrongBuild).Items.Single(i => i.Id == "hooks-resolve");
        TestAssert.AreEqual(AuthorityGateStatus.Blocked, item.Status);
        StringAssert.Contains(item.Evidence, "contract expects");

        var fewerHooks = Passing();
        fewerHooks.RequiredHooksResolved = 14;
        TestAssert.AreEqual(AuthorityGateStatus.Blocked,
            AuthorityReleaseGate.Evaluate(fewerHooks).Items.Single(i => i.Id == "hooks-resolve").Status);
    }

    [TestMethod]
    public void TheGateCoverageMatchesTheMatrixPlanItJudges()
    {
        // The fault-matrix item's budget is the plan's own case count, so the two cannot drift into
        // "the gate demands 55 while the plan runs 66".
        var mandatory = AuthorityMatrixPlan.ForcedScenarios.Length * AuthorityMatrixPlan.RequiredFaults.Length;
        var evidence = Passing();
        evidence.MatrixCasesMandatory = mandatory;
        evidence.MatrixCasesRun = mandatory - 1;
        var item = AuthorityReleaseGate.Evaluate(evidence).Items.Single(i => i.Id == "fault-matrix");
        TestAssert.AreEqual(AuthorityGateStatus.Blocked, item.Status);
        StringAssert.Contains(item.Evidence, "ran " + (mandatory - 1) + " of " + mandatory);
        TestAssert.AreEqual(66, mandatory, "11 forced scenarios x 6 fault families (N0/N3/N4/N5/N6/N7).");
    }

    [TestMethod]
    public void TheWorkspaceVerdictIsAPassOnTheOwnersRecordedWaivers()
    {
        // The G1 gate after I01, from the artifacts this session produced:
        //   cards        PROGRESS.md (A00-A24 recorded; A25 is this gate)
        //   hooks        docs/host-authority/authority-hooks.json (installed 22, failed 0)
        //   singleplayer TestResults/authority/a24-final-nonauthority.log (103 passed / 0 failed)
        //   multiplayer  TestResults/authority/run-a23perf2 (3 scopes Live, 310 applied, 18 digests, 0 mismatch)
        //   cost         run-a23perf2 perf reports (capture p95 0.084ms, apply p95 0.001ms, 1494 B/s)
        //   waivers      docs/host-authority/release-waivers.json, read from disk below
        // This is the executable form of the release decision: the gate passes for I01 on evidence
        // (the I01 guards own the client surface) and for the remaining seven items *because* the
        // owner recorded seven decisions to ship with those requirements unmeasured, not because
        // they were measured. The waived set is pinned, so closing one of them — or adding a new
        // decision — has to change this test on purpose.
        var evidence = new AuthorityGateEvidence
        {
            CompletedCards = (string[])AuthorityGateEvidence.RequiredCards.Clone(),
            ContractChecked = true,
            RequiredHooksResolved = AuthorityReleaseContract.RequiredHookCount,
            HookInstallFailures = 0,
            VerifiedGameVersion = AuthorityReleaseContract.GameVersion,
            VerifiedGameDllSha256 = AuthorityReleaseContract.GameDllSha256,
            SinglePlayerTestsPassed = 103,
            SinglePlayerTestsFailed = 0,
            MultiplayerClients = 2,
            MultiplayerLiveScopes = 3,
            MultiplayerAppliedMessages = 310,
            DigestsMatched = 18,
            DigestsMismatched = 0,
            MatrixCasesMandatory =
                AuthorityMatrixPlan.ForcedScenarios.Length * AuthorityMatrixPlan.RequiredFaults.Length,
            MatrixCasesRun = 0,
            MatrixCasesFailed = 0,
            Waivers = WaiversFromDisk(),
            EstablishedInvariants = AuthorityInvariantCoverageTable.Established.ToArray(),
            ForbiddenPathsChecked = true,
            CoveredHostConditions = [],
            ObserverDoesNotChangeRules = false,
            MaxCombatObjects = 0,
            MaxConstructionTasks = 0,
            MaxPlayers = 0,
            SoakHours = 0,
            CaptureP95Ms = 0.084,
            ApplyP95Ms = 0.001,
            BytesPerSecondPerClient = 1494,
            NoSustainedMemoryGrowth = false,
            UnmigratedDomainsDocumented = true,
            ModeRequiresLaunchFlag = true
        };

        var report = AuthorityReleaseGate.Evaluate(evidence);
        TestAssert.AreEqual(0, report.RefusedWaivers.Count,
            "Every recorded waiver must be well formed and must target an unmeasured item: " +
            string.Join("\n", report.RefusedWaivers));
        TestAssert.AreEqual(0, report.FailedCount,
            "Nothing is affirmatively broken in the measured scope: " + report.Verdict);

        // The owner waived the requirements that need the live multi-instance runs; the gate turns
        // each named item into Waived and keeps everything else measured. The waived set is pinned so
        // that closing one of them (or adding a new decision) has to change this list. I01 is not
        // here: it passes on evidence since its guards own the client surface.
        var waived = report.Items.Where(i => i.Status == AuthorityGateStatus.Waived)
            .Select(i => i.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "fault-matrix", "host-conditions", "invariant-I07", "invariant-I12",
                "observer-neutral", "scale", "soak"
            },
            waived,
            "The waived set is exactly the owner's recorded decisions — nothing else.");

        var passed = report.Items.Where(i => i.Status == AuthorityGateStatus.Pass)
            .Select(i => i.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "cards-complete", "contract-frozen", "cost-budget", "hooks-resolve", "invariant-I01",
                "invariant-I02",
                "invariant-I03", "invariant-I04", "invariant-I05", "invariant-I06", "invariant-I08",
                "invariant-I09", "invariant-I10", "invariant-I11", "mode-gated", "multiplayer-e2e",
                "no-forbidden-paths", "single-player-regression", "unmigrated-documented"
            },
            passed,
            "These are measured and green; the cost numbers hold for the measured scale only.");

        TestAssert.AreEqual(0, report.RefusedWaivers.Count,
            "Every recorded waiver must be well formed and must target an unmeasured item: " +
            string.Join("\n", report.RefusedWaivers));
        TestAssert.AreEqual(0, report.BlockedCount);
        TestAssert.IsTrue(report.Passed, "With the owner's decisions applied the gate passes: " + report.Verdict);
        TestAssert.IsTrue(report.MayEnableForUsers, "Each waiver is accepted for release, so ungating is allowed.");
        StringAssert.Contains(report.Verdict, "waived by owner decision");
        StringAssert.Contains(string.Join("\n", report.Describe()), "gate waiver");
    }

    /// <summary>
    /// Reads the owner's decisions from the reviewable artifact, the same file the gate script reads.
    /// </summary>
    /// <remarks>
    /// Reading it here rather than hardcoding the list means the test verifies the file that ships:
    /// a typo in an item id, a missing date or a waiver aimed at a failed item shows up as a gate
    /// refusal in the assertions below instead of passing silently.
    /// </remarks>
    private static List<AuthorityGateWaiver> WaiversFromDisk()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var path = Path.Combine(directory.FullName, "docs", "host-authority", "release-waivers.json");
            if (File.Exists(path))
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                var waivers = new List<AuthorityGateWaiver>();
                foreach (var entry in document.RootElement.GetProperty("waivers").EnumerateArray())
                {
                    waivers.Add(new AuthorityGateWaiver(
                        entry.GetProperty("itemId").GetString(),
                        entry.GetProperty("reason").GetString(),
                        entry.GetProperty("decidedBy").GetString(),
                        entry.GetProperty("decidedOn").GetString(),
                        entry.GetProperty("acceptedForRelease").GetBoolean()));
                }
                return waivers;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("docs/host-authority/release-waivers.json not found.");
    }

    [TestMethod]
    public void AWaiverNeverRescuesAFailedItem()
    {
        // The rule the whole mechanism rests on: accepting an unknown is a decision, and this is not.
        var evidence = Passing();
        evidence.DigestsMismatched = 1;                      // makes multiplayer-e2e fail
        evidence.Waivers =
        [
            new AuthorityGateWaiver("multiplayer-e2e", "we will look at it later", "someone", "2026-10-02", true)
        ];
        var report = AuthorityReleaseGate.Evaluate(evidence);

        TestAssert.AreEqual(AuthorityGateStatus.Fail,
            report.Items.Single(i => i.Id == "multiplayer-e2e").Status,
            "A failed item stays failed no matter who waived it.");
        TestAssert.AreEqual(0, report.WaivedCount);
        TestAssert.IsFalse(report.Passed);
        TestAssert.IsFalse(report.MayEnableForUsers);
        StringAssert.Contains(string.Join("\n", report.RefusedWaivers), "a defect is not an unknown");
    }

    [TestMethod]
    public void AMalformedWaiverIsRefusedRatherThanApplied()
    {
        var unnamed = Passing();
        unnamed.MaxCombatObjects = 0;               // makes "scale" an unmeasured item
        unnamed.Waivers = [new AuthorityGateWaiver("scale", "we are busy", decidedBy: "", decidedOn: "2026-10-02", true)];
        var report = AuthorityReleaseGate.Evaluate(unnamed);
        TestAssert.AreEqual(AuthorityGateStatus.Blocked, report.Items.Single(i => i.Id == "scale").Status);
        TestAssert.IsFalse(report.MayEnableForUsers, "A waiver nobody signed cannot ungate the release.");
        StringAssert.Contains(string.Join("\n", report.RefusedWaivers), "needs a reason, a decider and a date");

        var unknownItem = Passing();
        unknownItem.Waivers = [new AuthorityGateWaiver("no-such-item", "typo", "owner", "2026-10-02", true)];
        var second = AuthorityReleaseGate.Evaluate(unknownItem);
        StringAssert.Contains(string.Join("\n", second.RefusedWaivers), "no such gate item");
    }

    [TestMethod]
    public void ADeferredWaiverKeepsTheModeGated()
    {
        // "Accepted for release" is the difference between a decision and a deferral. A deferred
        // waiver must not ungate the mode even though the item is no longer blocking.
        var evidence = Passing();
        evidence.Waivers =
        [
            new AuthorityGateWaiver("soak", "postponed to the next cycle", "owner", "2026-10-02",
                acceptedForRelease: false)
        ];
        var report = AuthorityReleaseGate.Evaluate(evidence);

        TestAssert.IsTrue(report.Passed, "The item is waived, so nothing blocks: " + report.Verdict);
        TestAssert.AreEqual(1, report.WaivedCount);
        TestAssert.IsFalse(report.MayEnableForUsers, "A deferral is not an acceptance.");
    }

    private static AuthorityGateEvidence Passing()
    {
        return new AuthorityGateEvidence
        {
            CompletedCards = (string[])AuthorityGateEvidence.RequiredCards.Clone(),
            ContractChecked = true,
            ContractDrift = [],
            RequiredHooksResolved = AuthorityReleaseContract.RequiredHookCount,
            HookInstallFailures = 0,
            VerifiedGameVersion = AuthorityReleaseContract.GameVersion,
            VerifiedGameDllSha256 = AuthorityReleaseContract.GameDllSha256,
            SinglePlayerTestsPassed = 103,
            SinglePlayerTestsFailed = 0,
            MultiplayerClients = 2,
            MultiplayerLiveScopes = 3,
            MultiplayerAppliedMessages = 312,
            DigestsMatched = 60,
            DigestsMismatched = 0,
            MatrixCasesMandatory = AuthorityMatrixPlan.ForcedScenarios.Length * AuthorityMatrixPlan.RequiredFaults.Length,
            MatrixCasesRun = AuthorityMatrixPlan.ForcedScenarios.Length * AuthorityMatrixPlan.RequiredFaults.Length,
            MatrixCasesFailed = 0,
            EstablishedInvariants = (string[])AuthorityGateEvidence.RequiredInvariants.Clone(),
            ForbiddenPathsChecked = true,
            CoveredHostConditions = (string[])AuthorityGateEvidence.RequiredHostConditions.Clone(),
            ObserverDoesNotChangeRules = true,
            MaxCombatObjects = AuthorityReleaseGate.ScaleCombatObjectsBudget,
            MaxConstructionTasks = AuthorityReleaseGate.ScaleConstructionTasksBudget,
            MaxPlayers = AuthorityReleaseGate.ScalePlayersBudget,
            SoakHours = AuthorityReleaseGate.SoakHoursBudget,
            CaptureP95Ms = 0.084,
            ApplyP95Ms = 0.001,
            BytesPerSecondPerClient = 1494,
            NoSustainedMemoryGrowth = true,
            UnmigratedDomainsDocumented = true,
            ModeRequiresLaunchFlag = true
        };
    }
}
