#region

using System.Collections.Generic;
using NebulaModel.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

#endregion

namespace NebulaTests.Authority;

/// <summary>
/// A22/A23: the C/R/L x N matrix contract. Every forced scenario runs clean and under each of
/// N3–N7, every fault spec parses under the launch-spec rules, and each of N3–N7 covers a death,
/// a repair and a snapshot-lifecycle case — so a matrix run can never silently drop the coverage
/// VALIDATION §5–§7 requires.
/// </summary>
[TestClass]
public class AuthorityMatrixPlanTest
{
    [TestMethod]
    public void TheMatrixCoversEveryForcedScenarioUnderEveryRequiredFault()
    {
        var cases = AuthorityMatrixPlan.PlanMatrix();
        TestAssert.AreEqual(
            AuthorityMatrixPlan.ForcedScenarios.Length * AuthorityMatrixPlan.RequiredFaults.Length,
            cases.Count);
        var seen = new HashSet<string>();
        foreach (var c in cases) seen.Add(c.ScenarioId + "-" + c.FaultId);
        foreach (var scenario in AuthorityMatrixPlan.ForcedScenarios)
        foreach (var fault in AuthorityMatrixPlan.RequiredFaults)
            TestAssert.IsTrue(seen.Contains(scenario + "-" + fault), "Missing matrix case " + scenario + "-" + fault);
    }

    [TestMethod]
    public void TheMatrixOrderIsDeterministic()
    {
        var first = AuthorityMatrixPlan.PlanMatrix();
        var second = AuthorityMatrixPlan.PlanMatrix();
        TestAssert.AreEqual(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            TestAssert.AreEqual(first[i].ScenarioId, second[i].ScenarioId);
            TestAssert.AreEqual(first[i].FaultId, second[i].FaultId);
        }
        TestAssert.AreEqual("C01", first[0].ScenarioId);
        TestAssert.AreEqual("N0", first[0].FaultId);
    }

    [TestMethod]
    public void EveryFaultSpecParsesAndCleanMeansEmpty()
    {
        foreach (var c in AuthorityMatrixPlan.PlanMatrix())
        {
            TestAssert.IsTrue(AuthorityMatrixPlan.FaultSpecParses(c.FaultSpec),
                "Unparsable fault spec in " + c.ScenarioId + "-" + c.FaultId + ": '" + c.FaultSpec + "'");
            if (c.FaultId == "N0") TestAssert.AreEqual("", c.FaultSpec);
            else TestAssert.IsFalse(string.IsNullOrEmpty(c.FaultSpec), c.FaultId + " must inject something");
        }
    }

    [TestMethod]
    public void EveryCaseNamesInvariantsAndEvidence()
    {
        foreach (var c in AuthorityMatrixPlan.PlanMatrix())
        {
            TestAssert.IsTrue(c.RequiredInvariants.Length > 0, c.ScenarioId + " names no invariant");
            TestAssert.IsTrue(c.Evidence.Length > 0, c.ScenarioId + " names no evidence");
            TestAssert.IsFalse(string.IsNullOrEmpty(c.ScenarioName), c.ScenarioId + " has no name");
            var evidence = string.Join(",", c.Evidence);
            TestAssert.IsTrue(evidence.Contains("host-tick-log"), c.ScenarioId + " must log host ticks");
        }
    }

    [TestMethod]
    public void EachFaultFamilyCoversDeathRepairAndLifecycle()
    {
        var cases = AuthorityMatrixPlan.PlanMatrix();
        foreach (var fault in new[] { "N3", "N4", "N5", "N6", "N7" })
        {
            TestAssert.IsTrue(cases.Exists(c => c.FaultId == fault && c.ScenarioId.StartsWith("C")),
                fault + " covers no combat/death case");
            TestAssert.IsTrue(cases.Exists(c => c.FaultId == fault && c.ScenarioId.StartsWith("R")),
                fault + " covers no repair case");
            TestAssert.IsTrue(cases.Exists(c => c.FaultId == fault && c.ScenarioId.StartsWith("L")),
                fault + " covers no snapshot/lifecycle case");
        }
    }
}
