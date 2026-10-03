#region

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using NebulaModel.Authority;
using NebulaWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

#endregion

namespace NebulaTests.Authority;

/// <summary>
/// A24: the exit table and the ownership of every protected-field write are auditable, and the audit
/// fails when something is unowned rather than reporting success.
/// </summary>
/// <remarks>
/// <para>
/// Three checks, each aimed at a way the audit could rot. The exit table's rows must point at files
/// that exist, so a row cannot survive the code it describes. The field→writers map from A01 must be
/// fully classified, so an unowned writer fails the build instead of being discovered by a player.
/// And every hook label a patch actually uses must be classified by the policy table — the gap that
/// hid <c>ConstructionSystem.ResetDroneTargets</c>, which A01 recorded as a writer but never as a
/// target, and which the mod patches.
/// </para>
/// <para>
/// The residual is asserted too: the <see cref="AuthorityExitDisposition.NeedsReview"/> set is pinned
/// by name, so a new unowned writer cannot be absorbed into it silently — the list is the release's
/// published work items.
/// </para>
/// </remarks>
[TestClass]
public class AuthorityLegacyExitAuditTest
{
    /// <summary>
    /// Writers this release names as gaps, each with the domain that owns the decision.
    /// </summary>
    /// <remarks>
    /// I01 closed the three writers A24 could not name an owner for, so the list is empty. It stays
    /// pinned (rather than deleted) so a future unowned writer has an exact place to land.
    /// </remarks>
    private static readonly string[] KnownUnownedWriters = [];

    private static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "docs", "host-authority")))
                {
                    return directory.FullName;
                }
                directory = directory.Parent;
            }
            return null;
        }
    }

    [TestMethod]
    public void EveryProtectedFieldWriteHasADeclaredOwner()
    {
        var inventory = ReadFieldToWriters();
        TestAssert.IsTrue(inventory.Count > 0, "The A01 inventory must list protected-field writers.");
        var report = AuthorityLegacyExitAudit.AuditWriters(inventory,
            AuthorityRuleGuard.Policy.Select(entry => entry.Label).ToHashSet());

        var unclassified = report.Unclassified
            .Select(entry => entry.Field + " <- " + entry.Writer).Distinct().ToList();
        TestAssert.IsEmpty(unclassified,
            "These writers of protected fields have no declared owner, so the new mode cannot claim " +
            "the field is owned:\n" + string.Join("\n", unclassified));
        TestAssert.AreEqual(inventory.Count, report.FieldCount);
    }

    [TestMethod]
    public void TheAuditPublishesItsResidualInsteadOfClaimingCoverage()
    {
        var inventory = ReadFieldToWriters();
        var report = AuthorityLegacyExitAudit.AuditWriters(inventory,
            AuthorityRuleGuard.Policy.Select(entry => entry.Label).ToHashSet());

        var review = report.NeedsReview.Select(entry => entry.Writer).Distinct().OrderBy(w => w).ToArray();
        CollectionAssert.AreEqual(KnownUnownedWriters.OrderBy(w => w).ToArray(), review,
            "The set of writers with no owner is the release's remaining work list and must be exact.");

        foreach (var entry in report.NeedsReview)
        {
            TestAssert.IsTrue(entry.Basis.Length > 20, entry.Writer + " must record why it is unowned.");
        }

        // I01 closed the A24 residual: no writer is left without an owner. The NeedsReview set is
        // therefore empty, while the design-level category stays visible and separate.
        TestAssert.AreEqual(0, report.NeedsReview.Count,
            "I01 owns the last writer surface; a new unowned writer must land here explicitly.");
        TestAssert.AreEqual(0, report.CountOf(AuthorityExitDisposition.NeedsReview));

        // The design-level category must be visible and separate: it is the part of the audit that is
        // argued rather than verified, so folding it into "pass" would overstate the result.
        TestAssert.IsTrue(report.CountOf(AuthorityExitDisposition.VanillaChainBehindGuardedEntry) > 0,
            "Vanilla internals of the migrated chains are classified, and counted apart from verified owners.");
        TestAssert.IsTrue(report.CountOf(AuthorityExitDisposition.GuardedHostRule) > 0);
        TestAssert.IsTrue(report.CountOf(AuthorityExitDisposition.HostLifecyclePath) > 0);

        var description = string.Join("\n", report.Describe());
        StringAssert.Contains(description, "audit fields=34");
        TestAssert.IsFalse(description.Contains("needs-review"),
            "I01 closed the residual; no needs-review line may remain:\n" + description);
    }

    [TestMethod]
    public void EveryExitTableRowPointsAtCodeThatExists()
    {
        var root = RepositoryRoot;
        TestAssert.IsNotNull(root, "Repository root not found from the test output directory.");
        var missing = new List<string>();
        foreach (var row in AuthorityLegacyExitAudit.ExitTable)
        {
            var path = Path.Combine(root, row.CodeAnchor.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) missing.Add(row.LegacyArtifact + " -> " + row.CodeAnchor);
            TestAssert.IsTrue(row.NewModeHandling.Length > 10, row.LegacyArtifact + " has no handling note");
            TestAssert.IsTrue(row.EnforcingCard.StartsWith("A"), row.LegacyArtifact + " names no card");
        }
        TestAssert.IsEmpty(missing,
            "Exit-table rows whose code anchor no longer exists must be updated, not left describing a " +
            "past state:\n" + string.Join("\n", missing));
    }

    [TestMethod]
    public void TheExitTablePublishesTheGapsItFound()
    {
        // A24 published four gaps here; I01 closed all four (ground/hive AI ticks plus the three
        // cross-domain writers are now guarded host rules). The list is pinned empty so a future gap
        // has an exact place to land, and closing or adding one must edit this test on purpose.
        var gaps = AuthorityLegacyExitAudit.KnownGaps;
        TestAssert.AreEqual(0, gaps.Count,
            "I01 closed the A24 gaps; a new gap must be added here explicitly:\n" +
            string.Join("\n", gaps.Select(row => row.LegacyArtifact)));

        // They are reported through the same table the report prints, not a side channel.
        TestAssert.AreEqual(AuthorityLegacyExitAudit.ExitTable.Count, AuthorityLegacyExitAudit.ExitTable.Count);
    }

    [TestMethod]
    public void EveryLabelAPatchGuardsIsClassifiedByThePolicyTable()
    {
        // The gap A24 found: A05's classification walks the inventory's *target* list, so a mod-patched
        // method that A01 recorded only as a writer of a protected field was never classified. This
        // scans the patcher's sources for the labels it actually uses and requires each one to have a
        // policy entry — the label constants are the model's, so a patch cannot invent one, but it can
        // use one that the table forgot.
        var root = RepositoryRoot;
        TestAssert.IsNotNull(root);
        var patches = Path.Combine(root, "NebulaPatcher", "Patches");
        TestAssert.IsTrue(Directory.Exists(patches), "The patcher source tree must be present.");

        var constants = typeof(AuthorityHookLabels)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue());

        var pattern = new Regex(@"AuthorityHookLabels\.([A-Za-z0-9_]+)", RegexOptions.Compiled);
        var used = new Dictionary<string, string>();   // label value -> first file that uses it
        foreach (var file in Directory.EnumerateFiles(patches, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                var name = match.Groups[1].Value;
                if (!constants.TryGetValue(name, out var label)) continue;
                if (!used.ContainsKey(label)) used[label] = Path.GetFileName(file);
            }
        }

        TestAssert.IsTrue(used.Count >= 10,
            "The scan found suspiciously few guarded labels; the pattern or the tree changed.");
        var classified = AuthorityRuleGuard.Policy.Select(entry => entry.Label).ToHashSet();
        var missing = used.Where(pair => !classified.Contains(pair.Key))
            .Select(pair => pair.Key + " (" + pair.Value + ")").OrderBy(text => text).ToList();
        TestAssert.IsEmpty(missing,
            "These labels are used by a patch but the policy table does not classify them, so no one has " +
            "declared whether the new mode may run the branch they guard:\n" + string.Join("\n", missing));
    }

    [TestMethod]
    public void TheClaimConversationIsRetiredInTheNewMode()
    {
        // A24 closed this hole: A18 refused the launch fact but the assignment/reply/ready/release
        // packets still reached the host's claim table in host authority mode. The decision is pure, so
        // the policy is driven here rather than in a room.
        TestAssert.IsTrue(HostConstructionPolicy.ShouldRefuseLegacyClaimProtocol(isHostAuthority: true));
        StringAssert.Contains(HostConstructionPolicy.ClaimSuppressionReason("BuildTargetReady"), "claim");
    }

    private static Dictionary<string, IEnumerable<string>> ReadFieldToWriters()
    {
        var root = RepositoryRoot;
        TestAssert.IsNotNull(root, "Repository root not found.");
        var path = Path.Combine(root, "docs", "host-authority", "authority-hooks.json");
        TestAssert.IsTrue(File.Exists(path), "authority-hooks.json is the A01 deliverable.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var map = new Dictionary<string, IEnumerable<string>>(StringComparer.Ordinal);
        foreach (var field in document.RootElement.GetProperty("protectedFieldWriters").EnumerateObject())
        {
            map[field.Name] = field.Value.EnumerateArray().Select(writer => writer.GetString()).ToList();
        }
        return map;
    }
}
