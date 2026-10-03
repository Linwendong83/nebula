#region

using System.Linq;
using NebulaModel.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

#endregion

namespace NebulaTests.Authority;

/// <summary>
/// A25: the I01–I12 coverage table is a record of what is established, not a summary, and its
/// statuses cannot be upgraded without evidence appearing.
/// </summary>
/// <remarks>
/// The table is what the gate's twelve invariant items read, so it is the one place where "we think
/// this holds" could quietly become "this holds". These assertions pin the three rows that are not
/// established, require every row to name its evidence, and require the ids to be exactly VALIDATION
/// §3's — so adding, dropping or upgrading a row is a deliberate edit with a failing test attached.
/// </remarks>
[TestClass]
public class AuthorityInvariantCoverageTest
{
    [TestMethod]
    public void TheTableCoversExactlyValidationSectionsTwelveInvariants()
    {
        var ids = AuthorityInvariantCoverageTable.All.Select(row => row.Id).ToArray();
        CollectionAssert.AreEqual(AuthorityGateEvidence.RequiredInvariants, ids,
            "The coverage table must be the gate's invariant list, in VALIDATION's order.");
        TestAssert.AreEqual(12, ids.Length);
    }

    [TestMethod]
    public void EveryRowNamesItsEvidenceAndItsRequirement()
    {
        foreach (var row in AuthorityInvariantCoverageTable.All)
        {
            TestAssert.IsTrue(row.Requirement.Length > 20, row.Id + " states no requirement");
            TestAssert.IsTrue(row.Evidence.Length > 20, row.Id + " names no evidence");
            if (row.Status != AuthorityInvariantStatus.Established)
            {
                // A row that is not established has to say what is missing, in words a reviewer can
                // check against the code — that is the difference between a gap and a shrug.
                TestAssert.IsTrue(row.Evidence.Contains("no evidence") || row.Evidence.Contains("not"),
                    row.Id + " is " + row.Status + " but does not name the gap");
            }
        }
    }

    [TestMethod]
    public void TheUnestablishedRowsAreTheOnesTheReleaseKnownsAbout()
    {
        // Pinned: I07 needs a live three-peer run, and I12 needs the headless-versus-host comparison.
        // I01's client surface is closed (ground/hive AI ticks plus the charger, fuel and tech-HP
        // writers are guarded host rules), so closing either remaining row must change this list.
        var notFullyEstablished = AuthorityInvariantCoverageTable.NotFullyEstablished
            .Select(row => row.Id).OrderBy(id => id).ToArray();
        CollectionAssert.AreEqual(new[] { "I07", "I12" }, notFullyEstablished,
            "The rows that are not established are the release's honest residual.");

        TestAssert.AreEqual(AuthorityInvariantStatus.Established,
            AuthorityInvariantCoverageTable.For("I01").Status);
        TestAssert.AreEqual(AuthorityInvariantStatus.NotEstablished,
            AuthorityInvariantCoverageTable.For("I07").Status);
        TestAssert.AreEqual(AuthorityInvariantStatus.Partial,
            AuthorityInvariantCoverageTable.For("I12").Status);
        TestAssert.AreEqual(10, AuthorityInvariantCoverageTable.Established.Count);
    }

    [TestMethod]
    public void I01sRowNamesTheGuardsThatCloseIt()
    {
        // I01 is the invariant the whole design is about, so its row has to carry the specific
        // reason it is established: every entry of the former client surface names its guard.
        var row = AuthorityInvariantCoverageTable.For("I01");
        TestAssert.AreEqual(AuthorityInvariantStatus.Established, row.Status);
        foreach (var keyword in new[] { "GameTickLogic_Unit", "_enemy_ground_unit_parallel", "GameTickLogic", "PowerSystem", "UIMechaWindow", "UnlockTechFunction" })
        {
            StringAssert.Contains(row.Evidence, keyword,
                "I01's evidence must name the guard for " + keyword);
        }
        TestAssert.AreEqual(0, AuthorityLegacyExitAudit.KnownGaps.Count,
            "I01 is established, so no audit gap may remain.");
    }

    [TestMethod]
    public void AnUnknownInvariantIdHasNoRow()
    {
        TestAssert.IsNull(AuthorityInvariantCoverageTable.For("I99"));
        TestAssert.IsNull(AuthorityInvariantCoverageTable.For(null));
    }
}
