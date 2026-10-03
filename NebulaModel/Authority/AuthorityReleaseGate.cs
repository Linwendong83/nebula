#region

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

#endregion

namespace NebulaModel.Authority;

/// <summary>How one gate item stands. A gate never reports "unset": an item is one of these three.</summary>
public enum AuthorityGateStatus : byte
{
    /// <summary>The evidence affirmatively shows the requirement is met.</summary>
    Pass = 1,

    /// <summary>The evidence affirmatively shows a requirement is violated. Fixing it needs a code change.</summary>
    Fail = 2,

    /// <summary>No evidence. Not the same as passing, and the release must treat it as unmet.</summary>
    Blocked = 3,

    /// <summary>
    /// An unmet requirement the release owner explicitly accepted, with the decision recorded.
    /// </summary>
    /// <remarks>
    /// Only an item that is <em>not measured</em> can be waived. A waiver naming an item that failed
    /// is refused and the failure stands: accepting an unknown is a decision, accepting a known
    /// defect as if it were unknown is not.
    /// </remarks>
    Waived = 4
}

/// <summary>One recorded decision to release with a requirement unmeasured.</summary>
public sealed class AuthorityGateWaiver
{
    public AuthorityGateWaiver(string itemId, string reason, string decidedBy, string decidedOn,
        bool acceptedForRelease)
    {
        ItemId = itemId;
        Reason = reason;
        DecidedBy = decidedBy;
        DecidedOn = decidedOn;
        AcceptedForRelease = acceptedForRelease;
    }

    /// <summary>Gate item id the decision applies to ("scale", "soak", "invariant-I07", …).</summary>
    public string ItemId { get; }

    /// <summary>Why, including the residual risk the decision accepts.</summary>
    public string Reason { get; }

    /// <summary>Who decided. A waiver with no name is not a decision.</summary>
    public string DecidedBy { get; }

    /// <summary>When, in ISO form.</summary>
    public string DecidedOn { get; }

    /// <summary>True when the owner accepted it for release, not merely deferred it.</summary>
    public bool AcceptedForRelease { get; }

    public override string ToString() =>
        ItemId + " waived by " + DecidedBy + " on " + DecidedOn + ": " + Reason +
        (AcceptedForRelease ? " [accepted for release]" : " [deferred, not accepted]");
}

/// <summary>One line of the G1 release gate.</summary>
public sealed class AuthorityGateItem
{
    public AuthorityGateItem(string id, string requirement, AuthorityGateStatus status, string evidence)
    {
        Id = id;
        Requirement = requirement;
        Status = status;
        Evidence = evidence;
    }

    /// <summary>Stable id, used by the report and by tests.</summary>
    public string Id { get; }

    /// <summary>The requirement in one line, quoting TASKS.md A25 or VALIDATION §10.</summary>
    public string Requirement { get; }

    public AuthorityGateStatus Status { get; }

    /// <summary>What was actually observed, including the numbers when there are any.</summary>
    public string Evidence { get; }

    public override string ToString() => Id + " [" + Status + "] " + Requirement + " — " + Evidence;
}

/// <summary>
/// The facts the G1 gate judges (VALIDATION §2's manifest, reduced to what the gate reads).
/// </summary>
/// <remarks>
/// <para>
/// A plain data record with defaults that mean "not measured". Nothing here is inferred: a driver
/// fills in what it observed, and an item whose number is still at its default blocks. That is the
/// whole mechanism — there is no field whose default is "pass", so forgetting to measure something
/// cannot read as success.
/// </para>
/// <para>
/// The fields are deliberately the raw observations (counts, durations, lists of offenders) rather
/// than booleans like <c>SoakPassed</c>: the gate owns the thresholds so they live in one place,
/// next to the requirement they come from.
/// </para>
/// </remarks>
public sealed class AuthorityGateEvidence
{
    // ---- A00–A25 completion -------------------------------------------------------------

    /// <summary>Task cards that are complete, as the ids PROGRESS.md records ("A00"…"A25").</summary>
    public string[] CompletedCards { get; set; } = [];

    /// <summary>
    /// Cards the G1 gate requires as prerequisites: A00–A24.
    /// </summary>
    /// <remarks>
    /// A25 is the gate itself — the card that proves the others — so requiring "A25 complete" before
    /// the gate may pass would be circular. TASKS A25 names A22/A23/A24 as the precondition for
    /// ungating, which is why the list stops at A24.
    /// </remarks>
    public static string[] RequiredCards { get; } = BuildRequiredCards();

    // ---- frozen contract and hooks ------------------------------------------------------

    /// <summary>
    /// True when the live contract was actually compared against the frozen one.
    /// </summary>
    /// <remarks>
    /// Same reason as <see cref="ForbiddenPathsChecked"/>: an empty drift list means "no drift only
    /// if the comparison ran", and a default-constructed evidence object must not read as a pass.
    /// </remarks>
    public bool ContractChecked { get; set; }

    /// <summary>Lines <see cref="AuthorityReleaseContract.VerifyLive"/> reported. Empty means no drift.</summary>
    public string[] ContractDrift { get; set; } = [];

    /// <summary>Required hooks that resolved in the installed game assembly.</summary>
    public int RequiredHooksResolved { get; set; }

    /// <summary>Hook install failures reported by the baseline harness.</summary>
    public int HookInstallFailures { get; set; } = -1;

    /// <summary>Build the hook signatures were verified against, and its DLL hash.</summary>
    public string VerifiedGameVersion { get; set; }

    public string VerifiedGameDllSha256 { get; set; }

    // ---- test suites --------------------------------------------------------------------

    /// <summary>Non-authority (single-player regression) suite result.</summary>
    public int SinglePlayerTestsPassed { get; set; }
    public int SinglePlayerTestsFailed { get; set; } = -1;

    // ---- multiplayer end-to-end ---------------------------------------------------------

    /// <summary>Scopes that reached Live in a real host+client authority session.</summary>
    public int MultiplayerLiveScopes { get; set; }

    /// <summary>Messages the client actually applied from the host.</summary>
    public long MultiplayerAppliedMessages { get; set; }

    /// <summary>Digest comparisons: a mismatch is a real divergence, so it must be zero.</summary>
    public long DigestsMatched { get; set; }

    public long DigestsMismatched { get; set; }

    /// <summary>Clients in the measured session, including the host's own session.</summary>
    public int MultiplayerClients { get; set; }

    // ---- fault matrix -------------------------------------------------------------------

    public int MatrixCasesRun { get; set; }
    public int MatrixCasesFailed { get; set; }
    /// <summary>Cases the plan mandates (scenarios x fault families).</summary>
    public int MatrixCasesMandatory { get; set; }

    // ---- invariants ---------------------------------------------------------------------

    /// <summary>Invariant ids that the coverage table records as established.</summary>
    public string[] EstablishedInvariants { get; set; } = [];

    /// <summary>
    /// Recorded decisions to release with named requirements unmeasured.
    /// </summary>
    /// <remarks>
    /// The list is evidence, not a switch: each waiver carries who decided, when and why, and the
    /// report prints all of them. An item nobody waived stays blocked.
    /// </remarks>
    public List<AuthorityGateWaiver> Waivers { get; set; } = [];

    public static string[] RequiredInvariants { get; } =
        ["I01", "I02", "I03", "I04", "I05", "I06", "I07", "I08", "I09", "I10", "I11", "I12"];

    /// <summary>
    /// Forbidden degradation paths found present in the new mode (VALIDATION §10's last bullet).
    /// </summary>
    /// <remarks>
    /// Names, not a count, so the report says which one and the fix is not "close the last one".
    /// Expected to be empty; anything in here fails the gate rather than blocking it, because a
    /// path that exists is a code fact, not a missing measurement.
    /// </remarks>
    public string[] ForbiddenPathsDetected { get; set; } = [];

    /// <summary>
    /// True when the forbidden paths were actually looked for.
    /// </summary>
    /// <remarks>
    /// An empty <see cref="ForbiddenPathsDetected"/> means "none found" only after someone looked;
    /// without this flag an unrun audit would read as a clean one, which is precisely the failure
    /// mode this gate exists to prevent.
    /// </remarks>
    public bool ForbiddenPathsChecked { get; set; }

    // ---- host-condition coverage --------------------------------------------------------

    /// <summary>Host conditions covered: "host-remote-planet", "host-death", "headless".</summary>
    public string[] CoveredHostConditions { get; set; } = [];

    public static string[] RequiredHostConditions { get; } =
        ["host-remote-planet", "host-death", "headless"];

    /// <summary>True when a measured run showed an added observer changing no rule result (I07).</summary>
    public bool ObserverDoesNotChangeRules { get; set; }

    // ---- scale, soak and cost (VALIDATION §9) -------------------------------------------

    public int MaxCombatObjects { get; set; }
    public int MaxConstructionTasks { get; set; }
    public int MaxPlayers { get; set; }
    public double SoakHours { get; set; }
    public double CaptureP95Ms { get; set; } = double.NaN;
    public double ApplyP95Ms { get; set; } = double.NaN;
    public double BytesPerSecondPerClient { get; set; } = double.NaN;
    /// <summary>True when the long run showed no sustained memory growth or pool leak.</summary>
    public bool NoSustainedMemoryGrowth { get; set; }

    // ---- documentation and gating -------------------------------------------------------

    /// <summary>True when the boundary document names every unmigrated domain and claims no G2.</summary>
    public bool UnmigratedDomainsDocumented { get; set; }

    /// <summary>True when enabling the mode still requires the explicit development launch flag.</summary>
    public bool ModeRequiresLaunchFlag { get; set; }

    private static string[] BuildRequiredCards()
    {
        var cards = new List<string>(25);
        for (var i = 0; i <= 24; i++)
        {
            cards.Add("A" + i.ToString("D2", CultureInfo.InvariantCulture));
        }
        return cards.ToArray();
    }
}

/// <summary>
/// The G1 release gate (TASKS.md A25, VALIDATION §10): the pass/fail decision, made executable.
/// </summary>
/// <remarks>
/// <para>
/// The gate exists so the release claim cannot be prose. Every G1 requirement becomes an item with a
/// status and the evidence that produced it, and <see cref="AuthorityGateReport.MayEnableForUsers"/>
/// is false unless every item passed. A blocked item is never a pass, so the honest answer to
/// "can we turn it on for players" is computed from the same facts the report prints.
/// </para>
/// <para>
/// Pure model: no Unity, no game, no file access. The driver script gathers the facts; this decides.
/// That split is what lets the decision be unit-tested with partial evidence — the case that matters
/// most, because the failure mode this gate guards against is "nobody measured it, so it looked fine".
/// </para>
/// </remarks>
public static class AuthorityReleaseGate
{
    /// <summary>Budget the release is judged against (VALIDATION §9's initial values).</summary>
    public const double CaptureP95BudgetMs = 2.0;
    public const double ApplyP95BudgetMs = 3.0;
    public const double BytesPerSecondPerClientBudget = 1024 * 1024;
    public const int ScaleCombatObjectsBudget = 1000;
    public const int ScaleConstructionTasksBudget = 300;
    public const int ScalePlayersBudget = 4;
    public const double SoakHoursBudget = 2.0;

    /// <summary>Evaluates every item. The order is the order of the report.</summary>
    public static AuthorityGateReport Evaluate(AuthorityGateEvidence evidence)
    {
        if (evidence == null) throw new ArgumentNullException(nameof(evidence));
        var items = new List<AuthorityGateItem>
        {
            CardsItem(evidence),
            ContractItem(evidence),
            HooksItem(evidence),
            SinglePlayerItem(evidence),
            MultiplayerItem(evidence),
            MatrixItem(evidence),
        };
        foreach (var invariant in AuthorityGateEvidence.RequiredInvariants)
        {
            items.Add(InvariantItem(evidence, invariant));
        }
        items.Add(ForbiddenPathsItem(evidence));
        items.Add(HostConditionsItem(evidence));
        items.Add(ObserverItem(evidence));
        items.Add(ScaleItem(evidence));
        items.Add(BudgetItem(evidence));
        items.Add(SoakItem(evidence));
        items.Add(DocsItem(evidence));
        items.Add(GatingItem(evidence));

        var applied = new List<AuthorityGateWaiver>();
        var refused = new List<string>();
        ApplyWaivers(items, evidence, applied, refused);
        return new AuthorityGateReport(items, evidence, applied, refused);
    }

    /// <summary>
    /// Turns recorded owner decisions into <see cref="AuthorityGateStatus.Waived"/> items.
    /// </summary>
    /// <remarks>
    /// Two rules make this a release decision rather than a way to silence the gate. A waiver only
    /// converts an item that is <em>blocked</em>: a failed item stays failed, because accepting an
    /// unknown is a decision while accepting a known defect as if it were unknown is not. And a waiver
    /// with no name, no date or no reason is refused as malformed, so "someone waived it" cannot be
    /// the answer to who decided.
    /// </remarks>
    private static void ApplyWaivers(List<AuthorityGateItem> items, AuthorityGateEvidence evidence,
        List<AuthorityGateWaiver> applied, List<string> refused)
    {
        foreach (var waiver in evidence.Waivers ?? [])
        {
            if (waiver == null || string.IsNullOrWhiteSpace(waiver.ItemId))
            {
                refused.Add("a waiver with no item id");
                continue;
            }
            if (string.IsNullOrWhiteSpace(waiver.Reason) || string.IsNullOrWhiteSpace(waiver.DecidedBy) ||
                string.IsNullOrWhiteSpace(waiver.DecidedOn))
            {
                refused.Add(waiver.ItemId + ": a waiver needs a reason, a decider and a date");
                continue;
            }
            var index = items.FindIndex(item => item.Id == waiver.ItemId);
            if (index < 0)
            {
                refused.Add(waiver.ItemId + ": no such gate item");
                continue;
            }
            if (items[index].Status == AuthorityGateStatus.Fail)
            {
                refused.Add(waiver.ItemId + ": refused, the item failed — a defect is not an unknown");
                continue;
            }
            items[index] = new AuthorityGateItem(items[index].Id, items[index].Requirement,
                AuthorityGateStatus.Waived,
                "waived by " + waiver.DecidedBy + " on " + waiver.DecidedOn + ": " + waiver.Reason +
                (waiver.AcceptedForRelease ? " [accepted for release]" : " [deferred, not accepted]"));
            applied.Add(waiver);
        }
    }

    private static AuthorityGateItem Item(string id, string requirement, AuthorityGateStatus status, string evidence) =>
        new(id, requirement, status, evidence);

    private static AuthorityGateItem ContractItem(AuthorityGateEvidence evidence)
    {
        var requirement = "protocol/schema/capabilities/limits and the required hook set are frozen";
        if (evidence.ContractDrift.Length > 0)
        {
            return Item("contract-frozen", requirement, AuthorityGateStatus.Fail,
                "drift: " + string.Join("; ", evidence.ContractDrift));
        }
        return Item("contract-frozen", requirement,
            evidence.ContractChecked ? AuthorityGateStatus.Pass : AuthorityGateStatus.Blocked,
            evidence.ContractChecked ? "no drift against the live code" : "the contract was not compared");
    }

    private static AuthorityGateItem CardsItem(AuthorityGateEvidence evidence)
    {
        var done = new HashSet<string>(evidence.CompletedCards ?? [], StringComparer.Ordinal);
        var missing = AuthorityGateEvidence.RequiredCards.Where(card => !done.Contains(card)).ToArray();
        if (missing.Length == 0)
        {
            return Item("cards-complete", "A00–A25 complete with reproducible evidence",
                AuthorityGateStatus.Pass, "all " + AuthorityGateEvidence.RequiredCards.Length + " cards recorded");
        }
        // TASKS A25 calls out A22/A23/A24 by name as the precondition for ungating; a missing card
        // in that group is a blocked gate, and saying which one is the point of listing them.
        return Item("cards-complete", "A00–A25 complete with reproducible evidence",
            AuthorityGateStatus.Blocked, "missing: " + string.Join(", ", missing));
    }

    private static AuthorityGateItem HooksItem(AuthorityGateEvidence evidence)
    {
        if (evidence.HookInstallFailures < 0)
        {
            return Item("hooks-resolve", "every required hook resolves and installs",
                AuthorityGateStatus.Blocked, "hook install report not collected");
        }
        if (evidence.HookInstallFailures > 0)
        {
            return Item("hooks-resolve", "every required hook resolves and installs",
                AuthorityGateStatus.Fail, evidence.HookInstallFailures + " hook(s) failed to install");
        }
        if (evidence.RequiredHooksResolved != AuthorityReleaseContract.RequiredHookCount)
        {
            return Item("hooks-resolve", "every required hook resolves and installs",
                AuthorityGateStatus.Blocked,
                "resolved " + evidence.RequiredHooksResolved + " of " +
                AuthorityReleaseContract.RequiredHookCount);
        }
        if (!string.Equals(evidence.VerifiedGameVersion, AuthorityReleaseContract.GameVersion,
                StringComparison.Ordinal) ||
            !string.Equals(evidence.VerifiedGameDllSha256, AuthorityReleaseContract.GameDllSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return Item("hooks-resolve", "every required hook resolves and installs",
                AuthorityGateStatus.Blocked,
                "verified game=" + (evidence.VerifiedGameVersion ?? "<none>") + " dll=" +
                (evidence.VerifiedGameDllSha256 ?? "<none>") + ", contract expects " +
                AuthorityReleaseContract.GameVersion);
        }
        return Item("hooks-resolve", "every required hook resolves and installs",
            AuthorityGateStatus.Pass, "all " + evidence.RequiredHooksResolved + " required hooks, 0 install failures");
    }

    private static AuthorityGateItem SinglePlayerItem(AuthorityGateEvidence evidence)
    {
        if (evidence.SinglePlayerTestsFailed < 0 || evidence.SinglePlayerTestsPassed <= 0)
        {
            return Item("single-player-regression", "the single-player/legacy path is unchanged (I12)",
                AuthorityGateStatus.Blocked, "no non-authority suite result collected");
        }
        if (evidence.SinglePlayerTestsFailed > 0)
        {
            return Item("single-player-regression", "the single-player/legacy path is unchanged (I12)",
                AuthorityGateStatus.Fail, evidence.SinglePlayerTestsFailed + " non-authority test(s) failed");
        }
        return Item("single-player-regression", "the single-player/legacy path is unchanged (I12)",
            AuthorityGateStatus.Pass, evidence.SinglePlayerTestsPassed + " passed, 0 failed");
    }

    private static AuthorityGateItem MultiplayerItem(AuthorityGateEvidence evidence)
    {
        if (evidence.MultiplayerClients < 2 || evidence.MultiplayerLiveScopes <= 0 || evidence.MultiplayerAppliedMessages <= 0)
        {
            return Item("multiplayer-e2e", "a real host + client authority session converges (I10)",
                AuthorityGateStatus.Blocked,
                "clients=" + evidence.MultiplayerClients + " liveScopes=" + evidence.MultiplayerLiveScopes +
                " applied=" + evidence.MultiplayerAppliedMessages);
        }
        if (evidence.DigestsMismatched > 0)
        {
            return Item("multiplayer-e2e", "a real host + client authority session converges (I10)",
                AuthorityGateStatus.Fail, evidence.DigestsMismatched + " digest mismatch(es)");
        }
        if (evidence.DigestsMatched <= 0)
        {
            return Item("multiplayer-e2e", "a real host + client authority session converges (I10)",
                AuthorityGateStatus.Blocked, "no digest was compared");
        }
        return Item("multiplayer-e2e", "a real host + client authority session converges (I10)",
            AuthorityGateStatus.Pass,
            "live scopes=" + evidence.MultiplayerLiveScopes + " applied=" + evidence.MultiplayerAppliedMessages +
            " digests matched=" + evidence.DigestsMatched + " mismatched=0");
    }

    private static AuthorityGateItem MatrixItem(AuthorityGateEvidence evidence)
    {
        if (evidence.MatrixCasesMandatory <= 0)
        {
            return Item("fault-matrix", "the forced C/R/L x N cases run and pass",
                AuthorityGateStatus.Blocked, "no mandatory case count");
        }
        if (evidence.MatrixCasesFailed > 0)
        {
            return Item("fault-matrix", "the forced C/R/L x N cases run and pass",
                AuthorityGateStatus.Fail, evidence.MatrixCasesFailed + " case(s) failed");
        }
        if (evidence.MatrixCasesRun < evidence.MatrixCasesMandatory)
        {
            return Item("fault-matrix", "the forced C/R/L x N cases run and pass",
                AuthorityGateStatus.Blocked,
                "ran " + evidence.MatrixCasesRun + " of " + evidence.MatrixCasesMandatory);
        }
        return Item("fault-matrix", "the forced C/R/L x N cases run and pass",
            AuthorityGateStatus.Pass, evidence.MatrixCasesRun + " cases, 0 failed");
    }

    /// <summary>
    /// One invariant, as its own gate item.
    /// </summary>
    /// <remarks>
    /// Twelve items rather than one "invariants" item on purpose: a single item would let eleven
    /// established invariants carry a twelfth, and a waiver would have to name the whole set. Here a
    /// waiver names exactly the invariant that is not established, and the reason comes from the
    /// pinned coverage table so the report cannot quietly reword the gap.
    /// </remarks>
    private static AuthorityGateItem InvariantItem(AuthorityGateEvidence evidence, string id)
    {
        var coverage = AuthorityInvariantCoverageTable.For(id);
        var requirement = "invariant " + id + ": " + (coverage?.Requirement ?? "unknown invariant");
        if (coverage == null)
        {
            return Item("invariant-" + id, requirement, AuthorityGateStatus.Blocked,
                "no coverage row exists for this invariant");
        }
        var established = new HashSet<string>(evidence.EstablishedInvariants ?? [], StringComparer.Ordinal);
        if (established.Contains(id))
        {
            return Item("invariant-" + id, requirement, AuthorityGateStatus.Pass, coverage.Evidence);
        }
        return Item("invariant-" + id, requirement, AuthorityGateStatus.Blocked,
            coverage.Status + ": " + coverage.Evidence);
    }

    private static AuthorityGateItem ForbiddenPathsItem(AuthorityGateEvidence evidence)
    {
        var paths = evidence.ForbiddenPathsDetected ?? [];
        if (paths.Length > 0)
        {
            return Item("no-forbidden-paths",
                "no restored client simulation, forced 1 hp, timed full heal or masked reconnect",
                AuthorityGateStatus.Fail, "present: " + string.Join(", ", paths));
        }
        if (!evidence.ForbiddenPathsChecked)
        {
            return Item("no-forbidden-paths",
                "no restored client simulation, forced 1 hp, timed full heal or masked reconnect",
                AuthorityGateStatus.Blocked, "the exit table was not audited");
        }
        return Item("no-forbidden-paths",
            "no restored client simulation, forced 1 hp, timed full heal or masked reconnect",
            AuthorityGateStatus.Pass, "none detected in the new mode");
    }

    private static AuthorityGateItem HostConditionsItem(AuthorityGateEvidence evidence)
    {
        var covered = new HashSet<string>(evidence.CoveredHostConditions ?? [], StringComparer.Ordinal);
        var missing = AuthorityGateEvidence.RequiredHostConditions.Where(c => !covered.Contains(c)).ToArray();
        return missing.Length == 0
            ? Item("host-conditions", "host on another planet, host death and headless are covered",
                AuthorityGateStatus.Pass, "all three conditions covered")
            : Item("host-conditions", "host on another planet, host death and headless are covered",
                AuthorityGateStatus.Blocked, "missing: " + string.Join(", ", missing));
    }

    private static AuthorityGateItem ObserverItem(AuthorityGateEvidence evidence) =>
        evidence.ObserverDoesNotChangeRules
            ? Item("observer-neutral", "an added observer changes no rule result (I07)",
                AuthorityGateStatus.Pass, "measured: rule outcomes unchanged with an observer added")
            : Item("observer-neutral", "an added observer changes no rule result (I07)",
                AuthorityGateStatus.Blocked, "not measured");

    private static AuthorityGateItem ScaleItem(AuthorityGateEvidence evidence)
    {
        var shortfalls = new List<string>();
        if (evidence.MaxCombatObjects < ScaleCombatObjectsBudget)
        {
            shortfalls.Add("combatObjects=" + evidence.MaxCombatObjects + "/" + ScaleCombatObjectsBudget);
        }
        if (evidence.MaxConstructionTasks < ScaleConstructionTasksBudget)
        {
            shortfalls.Add("constructionTasks=" + evidence.MaxConstructionTasks + "/" + ScaleConstructionTasksBudget);
        }
        if (evidence.MaxPlayers < ScalePlayersBudget)
        {
            shortfalls.Add("players=" + evidence.MaxPlayers + "/" + ScalePlayersBudget);
        }
        return shortfalls.Count == 0
            ? Item("scale", "the validation scale was exercised",
                AuthorityGateStatus.Pass,
                "combatObjects=" + evidence.MaxCombatObjects + " tasks=" + evidence.MaxConstructionTasks +
                " players=" + evidence.MaxPlayers)
            : Item("scale", "the validation scale was exercised",
                AuthorityGateStatus.Blocked, Text("below budget: ", shortfalls));
    }

    private static AuthorityGateItem BudgetItem(AuthorityGateEvidence evidence)
    {
        if (double.IsNaN(evidence.CaptureP95Ms) || double.IsNaN(evidence.ApplyP95Ms) ||
            double.IsNaN(evidence.BytesPerSecondPerClient))
        {
            return Item("cost-budget", "capture/apply p95 and bandwidth are within the initial budget",
                AuthorityGateStatus.Blocked, "no measured cost report");
        }
        var exceeded = new List<string>();
        if (evidence.CaptureP95Ms > CaptureP95BudgetMs)
        {
            exceeded.Add(Text("captureP95=", evidence.CaptureP95Ms, "ms/", CaptureP95BudgetMs));
        }
        if (evidence.ApplyP95Ms > ApplyP95BudgetMs)
        {
            exceeded.Add(Text("applyP95=", evidence.ApplyP95Ms, "ms/", ApplyP95BudgetMs));
        }
        if (evidence.BytesPerSecondPerClient > BytesPerSecondPerClientBudget)
        {
            exceeded.Add(Text("bytesPerSecond=", evidence.BytesPerSecondPerClient, "/", BytesPerSecondPerClientBudget));
        }
        return exceeded.Count == 0
            ? Item("cost-budget", "capture/apply p95 and bandwidth are within the initial budget",
                AuthorityGateStatus.Pass,
                "captureP95=" + Format(evidence.CaptureP95Ms) + "ms applyP95=" + Format(evidence.ApplyP95Ms) +
                "ms bytesPerSecond=" + Format(evidence.BytesPerSecondPerClient))
            : Item("cost-budget", "capture/apply p95 and bandwidth are within the initial budget",
                AuthorityGateStatus.Fail,
                Text("over budget: ", exceeded) + " — a revision must be recorded with its hardware basis");
    }

    private static AuthorityGateItem SoakItem(AuthorityGateEvidence evidence)
    {
        if (evidence.SoakHours < SoakHoursBudget)
        {
            return Item("soak", "a long run is stable and leaks nothing (≥" + SoakHoursBudget + "h)",
                AuthorityGateStatus.Blocked, "soak=" + Format(evidence.SoakHours) + "h");
        }
        if (!evidence.NoSustainedMemoryGrowth)
        {
            return Item("soak", "a long run is stable and leaks nothing (≥" + SoakHoursBudget + "h)",
                AuthorityGateStatus.Fail,
                "soak=" + Format(evidence.SoakHours) + "h but memory/pool growth was observed");
        }
        return Item("soak", "a long run is stable and leaks nothing (≥" + SoakHoursBudget + "h)",
            AuthorityGateStatus.Pass, "soak=" + Format(evidence.SoakHours) + "h with no sustained growth");
    }

    private static AuthorityGateItem DocsItem(AuthorityGateEvidence evidence) =>
        evidence.UnmigratedDomainsDocumented
            ? Item("unmigrated-documented", "every unmigrated domain is named and no G2 claim is made",
                AuthorityGateStatus.Pass, "boundary document present")
            : Item("unmigrated-documented", "every unmigrated domain is named and no G2 claim is made",
                AuthorityGateStatus.Blocked, "boundary document not confirmed");

    private static AuthorityGateItem GatingItem(AuthorityGateEvidence evidence) =>
        evidence.ModeRequiresLaunchFlag
            ? Item("mode-gated", "normal rooms cannot enter the mode before the gate passes",
                AuthorityGateStatus.Pass, "the mode still needs the explicit development launch flag")
            : Item("mode-gated", "normal rooms cannot enter the mode before the gate passes",
                AuthorityGateStatus.Fail, "the mode can be entered without the development flag");

    private static string Text(string prefix, IEnumerable<string> parts) => prefix + string.Join(", ", parts);

    private static string Text(string prefix, double value, string suffix) =>
        prefix + Format(value) + suffix;

    private static string Text(string prefix, double value, string suffix, double limit) =>
        prefix + Format(value) + suffix + Format(limit);

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>The gate's verdict, with the evidence that produced it.</summary>
public sealed class AuthorityGateReport
{
    public AuthorityGateReport(IReadOnlyList<AuthorityGateItem> items, AuthorityGateEvidence evidence,
        IReadOnlyList<AuthorityGateWaiver> appliedWaivers = null, IReadOnlyList<string> refusedWaivers = null)
    {
        Items = items;
        Evidence = evidence;
        AppliedWaivers = appliedWaivers ?? [];
        RefusedWaivers = refusedWaivers ?? [];
    }

    public IReadOnlyList<AuthorityGateItem> Items { get; }

    public AuthorityGateEvidence Evidence { get; }

    /// <summary>Owner decisions that took effect, printed in full with every report.</summary>
    public IReadOnlyList<AuthorityGateWaiver> AppliedWaivers { get; }

    /// <summary>Waivers the gate refused, with the reason. Never empty silently.</summary>
    public IReadOnlyList<string> RefusedWaivers { get; }

    public int PassedCount => Items.Count(item => item.Status == AuthorityGateStatus.Pass);

    public int FailedCount => Items.Count(item => item.Status == AuthorityGateStatus.Fail);

    public int BlockedCount => Items.Count(item => item.Status == AuthorityGateStatus.Blocked);

    /// <summary>Items released on an explicit owner decision instead of on evidence.</summary>
    public int WaivedCount => Items.Count(item => item.Status == AuthorityGateStatus.Waived);

    public IReadOnlyList<AuthorityGateItem> WaivedItems =>
        Items.Where(item => item.Status == AuthorityGateStatus.Waived).ToList();

    /// <summary>
    /// True only when every item passed. A blocked item is not a pass, which is the whole point: an
    /// unmeasured requirement cannot be released on the grounds that it was never contradicted.
    /// </summary>
    public bool Passed => FailedCount == 0 && BlockedCount == 0;

    /// <summary>
    /// Whether the mode may be enabled for ordinary rooms (TASKS.md A25's ungating decision).
    /// </summary>
    /// <remarks>
    /// A waived requirement only counts here when the owner accepted it <em>for release</em> rather
    /// than deferring it, and no waiver may be refused. That is the difference between "we decided to
    /// ship with this unknown" and "nobody measured it": the first is a decision the report can name,
    /// the second would be a gap the report hides.
    /// </remarks>
    public bool MayEnableForUsers =>
        Passed && RefusedWaivers.Count == 0 && AppliedWaivers.All(waiver => waiver.AcceptedForRelease);

    /// <summary>The one-line verdict, including the reason when it is not a pass.</summary>
    public string Verdict
    {
        get
        {
            if (Passed)
            {
                var suffix = WaivedCount == 0
                    ? "mode may be enabled for users"
                    : "mode may be enabled for users with " + WaivedCount +
                      " requirement(s) waived by owner decision (not measured)";
                return "PASS — " + PassedCount + "/" + Items.Count + " items, " + suffix;
            }
            var parts = new List<string>();
            if (FailedCount > 0)
            {
                parts.Add("FAIL " + string.Join(",", Items.Where(i => i.Status == AuthorityGateStatus.Fail).Select(i => i.Id)));
            }
            if (BlockedCount > 0)
            {
                parts.Add("BLOCKED " + string.Join(",", Items.Where(i => i.Status == AuthorityGateStatus.Blocked).Select(i => i.Id)));
            }
            return "NOT PASSED — " + string.Join("; ", parts) + " (mode stays behind the development launch flag)";
        }
    }

    /// <summary>The report as text lines, for the gate driver's evidence file.</summary>
    public List<string> Describe()
    {
        var lines = new List<string>
        {
            "gate items=" + Items.Count + " pass=" + PassedCount + " fail=" + FailedCount +
            " blocked=" + BlockedCount + " waived=" + WaivedCount,
            "gate verdict: " + Verdict
        };
        foreach (var item in Items)
        {
            lines.Add("gate " + item);
        }
        foreach (var waiver in AppliedWaivers)
        {
            lines.Add("gate waiver " + waiver);
        }
        foreach (var refusal in RefusedWaivers)
        {
            lines.Add("gate waiver-refused " + refusal);
        }
        return lines;
    }
}
