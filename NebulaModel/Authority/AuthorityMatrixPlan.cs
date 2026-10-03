#region

using System.Collections.Generic;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// The C/R/L scenario x N fault matrix (TASKS.md A22/A23, VALIDATION §4–§7).
/// </summary>
/// <remarks>
/// <para>
/// Pure model, no Unity/game reference. The runtime driver
/// (<c>tools/authority/invoke-authority-matrix.ps1</c>) enumerates <see cref="PlanMatrix"/> in
/// order and records HostTick-aligned logs plus canonical snapshots per case; the assertions here
/// only lock the coverage contract so a matrix run can never silently drop a forced scenario or a
/// fault family.
/// </para>
/// <para>
/// Scenario ids follow VALIDATION §5–§7 verbatim. The forced set is the G1 gate plus the two user
/// symptoms (R01/R06/R12): every forced scenario runs clean (N0) and under each of N3–N7, and each
/// of N3–N7 covers at least one death, one repair and one snapshot-lifecycle case.
/// </para>
/// </remarks>
public static class AuthorityMatrixPlan
{
    /// <summary>One deterministic matrix case: a scenario under one fault config.</summary>
    public sealed class MatrixCase
    {
        public MatrixCase(string scenarioId, string scenarioName, string faultId, string faultSpec,
            string[] requiredInvariants, string[] evidence)
        {
            ScenarioId = scenarioId;
            ScenarioName = scenarioName;
            FaultId = faultId;
            FaultSpec = faultSpec;
            RequiredInvariants = requiredInvariants;
            Evidence = evidence;
        }

        public string ScenarioId { get; }

        public string ScenarioName { get; }

        public string FaultId { get; }

        /// <summary>Fault spec in <see cref="AuthorityLaunchOptions"/> syntax; empty means clean.</summary>
        public string FaultSpec { get; }

        /// <summary>VALIDATION §3 invariant ids this case must check (I01–I12 subset).</summary>
        public string[] RequiredInvariants { get; }

        /// <summary>Evidence artifacts this case must publish per VALIDATION §2.</summary>
        public string[] Evidence { get; }
    }

    /// <summary>Forced scenarios: the G1 gate plus R01/R06/R12 user-symptom regressions.</summary>
    public static readonly string[] ForcedScenarios =
    [
        "C01", "C02", "C08", "C10",
        "R01", "R03", "R06", "R12",
        "L01", "L02", "L05",
    ];

    /// <summary>Fault configs every forced scenario must run under (clean + N3–N6 + A23's N7).</summary>
    /// <remarks>
    /// N7 is here rather than in a separate list because the matrix is the contract for "which
    /// scenarios were exercised under which transport conditions", and A23 makes back pressure a
    /// property of every case: the send and receive budgets are on by default in authority mode, so
    /// what the N7 cases add is the transport half — a delayed link that builds a real backlog, which
    /// is when the budgets are what decides the frame's cost.
    /// </remarks>
    public static readonly string[] RequiredFaults =
    [
        "N0", "N3", "N4", "N5", "N6", "N7",
    ];

    private static readonly Dictionary<string, string> ScenarioNames = new()
    {
        { "C01", "host-client per-weapon kill, one death each" },
        { "C02", "two clients plus turret, continuous laser and AOE" },
        { "C08", "target dies then its slot is reused, delayed old hit" },
        { "C10", "client forges final damage/full-hp/kill" },
        { "R01", "damaged building, only a far-away client present" },
        { "R03", "two players plus two bases cover one building, third observer joins" },
        { "R06", "damaged building snapshot while someone joins/leaves the planet" },
        { "R12", "repair, combat-stat reclaim, damage again" },
        { "L01", "late join during combat, snapshot plus backlog" },
        { "L02", "A-B-A fast planet switch, stale snapshot arrives last" },
        { "L05", "save mid-fight then restart" },
    };

    private static readonly Dictionary<string, string> FaultSpecs = new()
    {
        { "N0", "" },
        { "N3", "copies=1;reorder=1" },
        { "N4", "droplifecycle;dropstate" },
        { "N5", "corruptchunk" },
        { "N6", "pause" },
        // N7 (A23): a held-then-released link so a backlog really builds while combat continues. The
        // budget half of N7 is unconditional in new mode, so this is the transport half only.
        { "N7", "delay=1;copies=1" },
    };

    private static readonly string[] DefaultEvidence =
    [
        "host-tick-log",
        "canonical-snapshot",
        "scope-digest",
    ];

    private static string[] InvariantsFor(string scenarioId) => scenarioId switch
    {
        "C01" => ["I02", "I03", "I09", "I10"],
        "C02" => ["I01", "I02", "I09", "I10"],
        "C08" => ["I03", "I10"],
        "C10" => ["I01", "I02"],
        "R01" => ["I05", "I06", "I07", "I08"],
        "R03" => ["I05", "I06", "I07"],
        "R06" => ["I04", "I10", "I11"],
        "R12" => ["I04", "I06", "I11"],
        "L01" => ["I10", "I11"],
        "L02" => ["I03", "I10"],
        "L05" => ["I08", "I10"],
        _ => ["I10"],
    };

    /// <summary>Deterministic enumeration: scenarios in forced order, faults in required order.</summary>
    public static List<MatrixCase> PlanMatrix()
    {
        var cases = new List<MatrixCase>(ForcedScenarios.Length * RequiredFaults.Length);
        foreach (var scenarioId in ForcedScenarios)
        {
            var name = ScenarioNames.TryGetValue(scenarioId, out var n) ? n : scenarioId;
            var invariants = InvariantsFor(scenarioId);
            foreach (var faultId in RequiredFaults)
            {
                var spec = FaultSpecs.TryGetValue(faultId, out var s) ? s : "";
                cases.Add(new MatrixCase(scenarioId, name, faultId, spec, invariants, DefaultEvidence));
            }
        }
        return cases;
    }

    /// <summary>True when the fault spec parses under the launch-spec rules (empty means clean).</summary>
    public static bool FaultSpecParses(string spec)
    {
        if (string.IsNullOrEmpty(spec)) return true;
        return AuthorityLaunchOptions.TryParseFaultSpec(spec, out _, out _);
    }
}
