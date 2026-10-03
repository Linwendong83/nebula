#region

using System;
using System.Collections.Generic;
using System.Linq;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// What the new mode does with an old path, or with a writer of a protected field (TASKS.md A24).
/// </summary>
/// <remarks>
/// The vocabulary is the point of the audit: every entry names an owner category rather than saying
/// "handled". <see cref="NeedsReview"/> is a first-class outcome, not a gap in the table — a writer
/// this release cannot yet claim an owner for is exactly the finding an audit exists to publish.
/// </remarks>
public enum AuthorityExitDisposition : byte
{
    /// <summary>A policy hook whose client execution the guard refuses (host owns it, and only inside a validated apply).</summary>
    GuardedHostRule = 1,

    /// <summary>A path the new mode retires on every peer (the guard refuses it and counts the refusal).</summary>
    RetiredPath = 2,

    /// <summary>Frame boundary or lifecycle bookkeeping: classified, but not a writer of protected state.</summary>
    NotARuleWriter = 3,

    /// <summary>Save/load/import: the host's load is authoritative; a client's own load is corrected by the replica.</summary>
    WorldLoadPath = 4,

    /// <summary>Object creation/removal: a host lifecycle fact; the client's objects come from the replica bindings.</summary>
    HostLifecyclePath = 5,

    /// <summary>UI, cutscene or test-only code that cannot decide shared facts.</summary>
    LocalToolingOnly = 6,

    /// <summary>
    /// A vanilla internal of a migrated chain, reached only from policy-classified entry points.
    /// </summary>
    /// <remarks>
    /// This is a *design-level* disposition: the entry points are classified and guarded, so a client
    /// does not run the chain that reaches these writers. Per-writer IL reachability is not verified
    /// method by method, which is why the audit counts this category separately instead of folding it
    /// into "verified".
    /// </remarks>
    VanillaChainBehindGuardedEntry = 7,

    /// <summary>
    /// A writer whose owning path this release cannot name, with the reason recorded.
    /// </summary>
    /// <remarks>
    /// It writes a field G1 protects, from a path outside the migrated surface — for example a G2
    /// (unmigrated) domain or a UI action. Publishing the list is the deliverable: the alternative is
    /// the state DESIGN §7.2 forbids, where a protected field's production is only partially owned
    /// and the release pretends the boundary is closed.
    /// </remarks>
    NeedsReview = 8,

    /// <summary>No disposition was declared. The audit fails on this; it is never a passing state.</summary>
    Unclassified = 9
}

/// <summary>One row of TASKS.md's exit table, with the code the claim rests on.</summary>
public sealed class AuthorityExitRow
{
    public AuthorityExitRow(string legacyArtifact, string newModeHandling, string enforcingCard,
        AuthorityExitDisposition disposition, string codeAnchor)
    {
        LegacyArtifact = legacyArtifact;
        NewModeHandling = newModeHandling;
        EnforcingCard = enforcingCard;
        Disposition = disposition;
        CodeAnchor = codeAnchor;
    }

    /// <summary>The old packet family, patch or manager the row retires.</summary>
    public string LegacyArtifact { get; }

    /// <summary>What the new mode does with it (the exit table's middle column).</summary>
    public string NewModeHandling { get; }

    /// <summary>Card that moved the capability, i.e. where the new behaviour lives.</summary>
    public string EnforcingCard { get; }

    public AuthorityExitDisposition Disposition { get; }

    /// <summary>Repository-relative file the claim can be read in. Checked to exist by the audit test.</summary>
    public string CodeAnchor { get; }
}

/// <summary>One protected-field write and the disposition of the writer that performs it.</summary>
public sealed class AuthorityWriteAuditEntry
{
    public AuthorityWriteAuditEntry(string field, string writer, AuthorityExitDisposition disposition,
        string basis)
    {
        Field = field;
        Writer = writer;
        Disposition = disposition;
        Basis = basis;
    }

    public string Field { get; }

    public string Writer { get; }

    public AuthorityExitDisposition Disposition { get; }

    /// <summary>Why this disposition, in one line a reviewer can check.</summary>
    public string Basis { get; }
}

/// <summary>The audit's result: every writer accounted for, with the residual named.</summary>
public sealed class AuthorityWriteAuditReport
{
    public AuthorityWriteAuditReport(IReadOnlyList<AuthorityWriteAuditEntry> entries)
    {
        Entries = entries;
    }

    public IReadOnlyList<AuthorityWriteAuditEntry> Entries { get; }

    public int FieldCount => Entries.Select(entry => entry.Field).Distinct(StringComparer.Ordinal).Count();

    public int WriterCount => Entries.Select(entry => entry.Writer).Distinct(StringComparer.Ordinal).Count();

    public IEnumerable<AuthorityWriteAuditEntry> With(AuthorityExitDisposition disposition) =>
        Entries.Where(entry => entry.Disposition == disposition);

    public int CountOf(AuthorityExitDisposition disposition) => With(disposition).Count();

    /// <summary>Writers with no declared owner. Any of these means the audit is not finished.</summary>
    public IReadOnlyList<AuthorityWriteAuditEntry> Unclassified => With(AuthorityExitDisposition.Unclassified).ToList();

    /// <summary>Writers whose owner is named as a gap, with the reason.</summary>
    public IReadOnlyList<AuthorityWriteAuditEntry> NeedsReview => With(AuthorityExitDisposition.NeedsReview).ToList();

    /// <summary>The design-level category, counted separately so it cannot be read as verified.</summary>
    public IReadOnlyList<AuthorityWriteAuditEntry> VanillaChain =>
        With(AuthorityExitDisposition.VanillaChainBehindGuardedEntry).ToList();

    /// <summary>One line per disposition, then the residual lists.</summary>
    public List<string> Describe()
    {
        var lines = new List<string>
        {
            "audit fields=" + FieldCount + " writerEntries=" + Entries.Count +
            " distinctWriters=" + WriterCount
        };
        foreach (var disposition in Enum.GetValues(typeof(AuthorityExitDisposition)))
        {
            var count = CountOf((AuthorityExitDisposition)disposition);
            if (count > 0) lines.Add("audit " + disposition + "=" + count);
        }
        if (Unclassified.Count > 0)
        {
            lines.Add("audit UNCLASSIFIED (audit incomplete): " +
                      string.Join(", ", Unclassified.Select(entry => entry.Field + "<-" + entry.Writer)));
        }
        foreach (var entry in NeedsReview)
        {
            lines.Add("audit needs-review " + entry.Field + " <- " + entry.Writer + ": " + entry.Basis);
        }
        return lines;
    }
}

/// <summary>
/// The A24 audit of the old implementation: the exit table, and the ownership of every write to a
/// protected field.
/// </summary>
/// <remarks>
/// <para>
/// The table and the classification live in the model so the audit is data a reviewer can read and a
/// test can police; the inventory itself (<c>docs/host-authority/authority-hooks.json</c>, the field
/// to-writers map A01 derived from real IL) is fed in by the caller, so nothing here duplicates it or
/// can drift from it.
/// </para>
/// <para>
/// The outcome this card must not hide: several writers have no owner this release can name. They are
/// published as <see cref="AuthorityExitDisposition.NeedsReview"/> with the domain that owns the
/// decision, which is what makes them work items instead of surprises.
/// </para>
/// </remarks>
public static class AuthorityLegacyExitAudit
{
    /// <summary>
    /// TASKS.md's exit table. Every row is the capability-moved rule: the new mode's handling exists
    /// before the old path may be removed, and the legacy path stays for legacy rooms meanwhile.
    /// </summary>
    /// <remarks>
    /// Rows are removed as their code is deleted — a row must never survive the code it describes
    /// (see <c>EveryExitTableRowPointsAtCodeThatExists</c>). Deleted in the legacy-removal pass:
    /// CombatStatDamagePacket/Processor, CombatStatFullHpPacket/Processor,
    /// EnemyManager.State/Snapshot, the DFG/DFS/DFHive/DFRelay/DFTinder packet families and their
    /// processors, CombatEnemyStateRequest/Response, EnemyDFGroundSystem_Transpiler,
    /// MechaShoot/MechaBomb/MechaShieldBurst packets and processors, BattleVisualProcessor,
    /// PlayerEjectMechaDronePacket/Processor, DroneManager remote pool,
    /// BuildTargetAssignment/Reply/Ready/BaseReleaseAck/BuildDroneLaunch packets and processors,
    /// PlayerMechaData packet and processor, AuthorityRoutedPacketGuard.
    /// </remarks>
    public static readonly IReadOnlyList<AuthorityExitRow> ExitTable = new[]
    {
        Row("CombatStat_Patch client 1-hp query and local hpRecover",
            "retired; replica guard plus presentation pings instead",
            "A14/A19", AuthorityExitDisposition.RetiredPath,
            "NebulaPatcher/Patches/Dynamic/CombatStat_Patch.cs"),
        Row("SkillSystem_Common_Patch shared player context",
            "explicit host proxy context; the multi-player resolution it still needs moves into the adapter",
            "A11", AuthorityExitDisposition.GuardedHostRule,
            "NebulaPatcher/Patches/Dynamic/SkillSystem_Patch.cs"),
        Row("ConstructionModuleComponent_Patch launch postfix",
            "host task execution; the client shows the task batch",
            "A17/A18", AuthorityExitDisposition.RetiredPath,
            "NebulaPatcher/Patches/Dynamic/ConstructionModuleComponent_Patch.cs"),
        Row("BuildDispatch client launch/revoke ACK rule dependency",
            "host task/budget; the verified selection policy is kept for legacy rooms only",
            "A18/A24", AuthorityExitDisposition.RetiredPath,
            "NebulaWorld/Factory/BuildDispatchManager.cs"),
        Row("Factory load forced full heal and combat-stat clearing",
            "consistent snapshot plus local reference mapping",
            "A08", AuthorityExitDisposition.RetiredPath,
            "NebulaNetwork/PacketProcessors/Planet/FactoryLoadRequestProcessor.cs"),
        Row("PlayerMechaData and life snapshot full overwrite",
            "authoritative whitelist/ledger plus separate input",
            "A10/A21", AuthorityExitDisposition.RetiredPath,
            "NebulaWorld/Combat/PlayerLifeManager.cs"),
        Row("BattleVisual client world-effect reports",
            "host is the effect source; the renderer is reused",
            "A14", AuthorityExitDisposition.RetiredPath,
            "NebulaWorld/Combat/BattleVisualManager.cs"),
        Row("Broadcast routers wrapping authoritative requests/results",
            "direct to the server plus direction validation",
            "A03/A24", AuthorityExitDisposition.RetiredPath,
            "NebulaNetwork/PacketProcessors/Routers/PlanetBroadcastProcessor.cs")
    };

    /// <summary>
    /// Rows A24 recorded as not yet satisfied, with the domain that owns closing them.
    /// </summary>
    /// <remarks>
    /// I01 closed the four rows A24 published: the ground/hive AI ticks and the three cross-domain
    /// writers are now guarded host rules (see the I01 labels in <see cref="AuthorityRuleGuard"/>).
    /// The list is kept as an empty record so the audit still reports where the gaps were and a
    /// future gap has a pinned place to land.
    /// </remarks>
    public static readonly IReadOnlyList<AuthorityExitRow> KnownGaps = [];

    /// <summary>
    /// Writers whose owner this release names as a gap, with the reason and the domain that owns the
    /// decision. Each entry is a published finding, not a waiver.
    /// </summary>
    /// <remarks>
    /// I01 closed the three writers A24 could not name an owner for: PowerSystem's charger write,
    /// the fuel UI write and the tech HP write are now guarded host rules (see the I01 labels in
    /// <see cref="AuthorityRuleGuard"/>). The map is kept empty so a future unowned writer still has
    /// a pinned place to land instead of falling to <see cref="AuthorityExitDisposition.Unclassified"/>.
    /// </remarks>
    private static readonly Dictionary<string, string> ReviewWriters = new(StringComparer.Ordinal);

    /// <summary>Writers that run when a world is loaded or created from scratch.</summary>
    private static readonly HashSet<string> WorldLoadWriters = new(StringComparer.Ordinal)
    {
        "Mecha.Init", "Mecha.SetForNewGame", "Mecha.PrepareRedeploy",
        "BattleBaseComponent.Init", "ConstructionSystem.AfterDronesImport"
    };

    /// <summary>Writers that create or remove objects and components.</summary>
    private static readonly HashSet<string> HostLifecycleWriters = new(StringComparer.Ordinal)
    {
        "PlanetFactory.CreateEnemyFinal", "PlanetFactory.CreateEnemyPlanetBase",
        "PlanetFactory.RemoveEnemyWithComponents", "PlanetFactory.RemoveEntityWithComponents",
        "SpaceSector.CreateEnemyFinal", "SpaceSector.RemoveEnemyWithComponents",
        "CombatGroundSystem.NewUnitComponent", "CombatSpaceSystem.NewUnitComponent",
        "ConstructionSystem.NewDroneComponent", "ConstructionModuleComponent.CreateDrones",
        "ConstructionModuleComponent.RecycleDrone", "EntityData.SetNull", "BattleBaseComponent.Reset",
        // Removal side of the two lifecycles the domain cards established: a destroyed hive takes its
        // corrupted units with it (A13), and removing a damage record clears the entity's reference to
        // it (A15). Both are host lifecycle facts the replica rebuilds, not peer decisions.
        "EnemyDFHiveSystem.KillCorruptedUnits", "ConstructionSystem.ClearReferenceOnStatRemove"
    };

    /// <summary>Writers that cannot decide shared facts: UI, cutscene, test-only code.</summary>
    private static readonly HashSet<string> LocalToolingWriters = new(StringComparer.Ordinal)
    {
        "UIMechaEditor.CalcMechaProperty", "CutsceneDirector.CreateEnemyWithModelOnly",
        "TestCombatDetails.DestroyIcrusShiled", "TestRepairBuilding.Update", "PlayerAction_Test.Update"
    };

    /// <summary>
    /// Vanilla internals of the migrated combat/construction chains.
    /// </summary>
    /// <remarks>
    /// Listed explicitly rather than inferred, so a writer the inventory has never seen falls to
    /// <see cref="AuthorityExitDisposition.Unclassified"/> and fails the audit test instead of being
    /// absorbed into this category.
    /// </remarks>
    private static readonly HashSet<string> VanillaChainWriters = new(StringComparer.Ordinal)
    {
        "BattleBaseComponent.InternalUpdate", "BattleBaseComponent.energy",
        "ConstructionModuleComponent.ChangeDronesPriority", "ConstructionModuleComponent.PreLaunchDrone",
        // The mod's launch-reporting postfixes are deleted; the vanilla eject entries remain and run
        // under the guarded dispatch (DetermineLaunch/UpdateDrones), so they are vanilla internals
        // of the migrated chain like the entries around them.
        "ConstructionModuleComponent.EjectMechaDrone", "ConstructionModuleComponent.EjectBaseDrone",
        "ConstructionModuleComponent.set_droneAliveCount", "ConstructionModuleComponent.set_droneCount",
        "ConstructionModuleComponent.set_droneIdleCount", "ConstructionSystem.ExecuteBuildTasks",
        "ConstructionSystem.OnDronePriorityChange", "ConstructStat.RefreshDamageRate",
        "DroneComponent.InternalUpdate", "EnemyData.set_counterAttack", "EnemyData.set_isAssaultingUnit",
        "EnemyData.set_isInvincible", "EnemyData.set_willBroadcast", "EntityData.set_ignoreRepairWarning",
        "EntityData.set_localized", "Mecha.ChargeShieldBurst", "Mecha.EnergyShieldResist",
        "Mecha.GenerateEnergy", "Mecha.Kill", "Mecha.LoadAmmo", "Mecha.Respawn",
        "Mecha.ResetShieldBurstProgress", "Mecha.ShieldBurstComsumeEnergy", "Mecha.TakeDamage",
        "Mecha.TickAmmoFireCondition", "Mecha.TickBombFireCondition", "Mecha.UpdateCombatStats",
        "Mecha.UseEnergy", "PlayerAction_Combat.AmmoFireProcedure", "PlayerAction_Combat.Bombing",
        "PlayerAction_Inspect.GameTick", "PlayerAction_Mine.GameTick",
        "SkillSystem.AddCombatStatHPIncoming", "SkillSystem.CollectPlayerStates", "SkillSystem.GetCombatStat",
        "UnitComponent.RunBehavior_Engage_AttackLaser_Ground",
        "UnitComponent.RunBehavior_Engage_AttackPlasma_Ground",
        "UnitComponent.RunBehavior_Engage_DefenseShield_Ground",
        "UnitComponent.RunBehavior_Engage_SAttackLaser_Large",
        "UnitComponent.RunBehavior_Engage_SAttackPlasma_Small",
        "UnitComponent.RunBehavior_Orbiting", "UnitComponent.RunBehavior_Recycled_Ground",
        "UnitComponent.RunBehavior_Recycled_Space", "UnitComponent.RunBehavior_SeekForm_Ground",
        "UnitComponent.RunBehavior_SeekForm_Space", "UnitComponent.UpdateBattleBaseEnergy",
        "UnitComponent.UpdateMechaEnergy",
        // A24: recorded by A01 only as a writer, never as a target label; the mod patches it, so the
        // A05 table now classifies it as a retired path (see AuthorityHookLabels).
        "ConstructionSystem.ResetDroneTargets"
    };

    /// <summary>
    /// Classifies every protected-field write the inventory records.
    /// </summary>
    /// <param name="fieldToWriters">The A01 field → writers map, read from the inventory by the caller.</param>
    /// <param name="policyLabels">Labels the executable policy table classifies.</param>
    public static AuthorityWriteAuditReport AuditWriters(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> fieldToWriters,
        ICollection<string> policyLabels)
    {
        if (fieldToWriters == null) throw new ArgumentNullException(nameof(fieldToWriters));
        var labels = policyLabels ?? new HashSet<string>();
        var entries = new List<AuthorityWriteAuditEntry>();
        foreach (var field in fieldToWriters)
        {
            foreach (var writer in field.Value.Distinct(StringComparer.Ordinal))
            {
                var (disposition, basis) = Classify(field.Key, writer, labels);
                entries.Add(new AuthorityWriteAuditEntry(field.Key, writer, disposition, basis));
            }
        }
        return new AuthorityWriteAuditReport(entries);
    }

    private static (AuthorityExitDisposition Disposition, string Basis) Classify(string field, string writer,
        ICollection<string> policyLabels)
    {
        if (ReviewWriters.TryGetValue(writer, out var reason))
        {
            return (AuthorityExitDisposition.NeedsReview, reason);
        }
        if (policyLabels.Contains(writer))
        {
            var mode = AuthorityRuleGuard.ModeFor(writer);
            switch (mode)
            {
                case AuthorityPatchMode.HostRule:
                    return (AuthorityExitDisposition.GuardedHostRule,
                        "policy table classifies it as a host rule; the guard refuses client execution");
                case AuthorityPatchMode.LegacyRemove:
                    return (AuthorityExitDisposition.RetiredPath,
                        "policy table retires it in the new mode; the guard refuses it and counts the refusal");
                default:
                    return (AuthorityExitDisposition.NotARuleWriter,
                        "policy table classifies it without a rule role (mode " + mode + ")");
            }
        }
        if (writer.EndsWith(".Import", StringComparison.Ordinal) || WorldLoadWriters.Contains(writer))
        {
            return (AuthorityExitDisposition.WorldLoadPath,
                "runs on world load; the host's load is authoritative and a client's own load is corrected by the replica (A08/A21)");
        }
        if (HostLifecycleWriters.Contains(writer))
        {
            return (AuthorityExitDisposition.HostLifecyclePath,
                "object/component create or remove; a host lifecycle fact in the new mode, mirrored from the replica bindings (A12/A13/A17)");
        }
        if (LocalToolingWriters.Contains(writer))
        {
            return (AuthorityExitDisposition.LocalToolingOnly,
                "UI/cutscene/test-only code; it cannot decide a shared fact");
        }
        if (VanillaChainWriters.Contains(writer))
        {
            return (AuthorityExitDisposition.VanillaChainBehindGuardedEntry,
                "vanilla internal of a migrated chain; reached only from policy-classified entry points (per-writer IL reachability is the residual)");
        }
        return (AuthorityExitDisposition.Unclassified,
            "no owner declared for this writer of " + field + "; the audit is incomplete until one is named");
    }

    private static AuthorityExitRow Row(string legacyArtifact, string newModeHandling, string enforcingCard,
        AuthorityExitDisposition disposition, string codeAnchor) =>
        new(legacyArtifact, newModeHandling, enforcingCard, disposition, codeAnchor);
}
