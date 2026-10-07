using System;
using System.Collections.Generic;
using System.Reflection;

namespace NebulaModel.Authority;

/// <summary>The responsibility of a game or mod hook in the multiplayer runtime.</summary>
public enum AuthorityHookRole : byte
{
    /// <summary>
    /// Unspecified. Only ever the state of a hook the policy table did not classify, which means the
    /// guard lets the vanilla path run.
    /// </summary>
    None = 0,

    /// <summary>
    /// The vanilla method decides shared world facts. On a client it does not run outside a
    /// validated replica apply.
    /// </summary>
    HostRule = 1,

    /// <summary>
    /// The method maintains mirrored state. Legal only inside the replica apply window.
    /// </summary>
    ReplicaApply = 2,

    /// <summary>
    /// Visual or audio only. Runs on every peer, but must not write a protected field.
    /// </summary>
    Presentation = 3,

    /// <summary>Lifecycle bookkeeping that changes no shared fact.</summary>
    LifecycleObserve = 5,

    /// <summary>Native drone execution with server-assigned claims and host-only building commits.</summary>
    NativeConstruction = 6
}

/// <summary>
/// Why the guard allowed or refused one entry point.
/// </summary>
/// <remarks>
/// Every value is a distinct cause, because the A05 acceptance requires that a refused client write
/// can be told apart from a legitimate vanilla run and from a peer that is simply presenting a host
/// fact. <see cref="None"/> means "allowed and nothing notable about it".
/// </remarks>
public enum AuthorityGuardOutcome : byte
{
    None = 0,

    /// <summary>An authority session exists but has no loaded world yet; the vanilla path still runs.</summary>
    NoAuthorityWorld = 2,

    /// <summary>The peer is inside a validated replica apply and may write its mirror.</summary>
    ReplicaApply = 3,

    /// <summary>A client tried to run a host rule outside any replica apply. This count must stay zero.</summary>
    ClientRuleRefused = 4
}

/// <summary>
/// One refusal recorded for diagnosis, with the caller stack that produced it.
/// </summary>
public readonly struct AuthorityGuardRefusal
{
    public AuthorityGuardRefusal(string hook, AuthorityGuardOutcome outcome, string detail, string stack)
    {
        Hook = hook;
        Outcome = outcome;
        Detail = detail;
        Stack = stack;
    }

    /// <summary>The hook that was refused, as labelled by the policy table.</summary>
    public string Hook { get; }

    public AuthorityGuardOutcome Outcome { get; }

    /// <summary>Extra text supplied by the call site, or null.</summary>
    public string Detail { get; }

    /// <summary>Caller stack at the moment of the refusal, or null when none was captured.</summary>
    public string Stack { get; }
}

/// <summary>
/// The labels the policy table and the call sites share.
/// </summary>
/// <remarks>
/// Constants rather than string literals at each call site: a mistyped label would otherwise fall
/// through to the unclassified case and silently allow a client write, which is the one failure the
/// guard exists to prevent.
/// </remarks>
public static class AuthorityHookLabels
{
    public const string CombatStatTickSkillLogic = "CombatStat.TickSkillLogic";
    public const string CombatStatHandleFullHp = "CombatStat.HandleFullHp";
    public const string CombatStatHandleZeroHp = "CombatStat.HandleZeroHp";
    public const string SkillSystemDamageObject = "SkillSystem.DamageObject";
    public const string SkillSystemDamageGroundObjectByLocalCaster = "SkillSystem.DamageGroundObjectByLocalCaster";
    public const string SkillSystemDamageGroundObjectByRemoteCaster = "SkillSystem.DamageGroundObjectByRemoteCaster";
    public const string ConstructionSystemAddConstructStat = "ConstructionSystem.AddConstructStat";
    public const string ConstructionSystemRemoveConstructStat = "ConstructionSystem.RemoveConstructStat";
    public const string ConstructionSystemRepair = "ConstructionSystem.Repair";
    public const string ConstructionSystemDetermineLaunch = "ConstructionSystem.DetermineLaunch";
    public const string ConstructionSystemUpdateModules = "ConstructionSystem.UpdateModules";
    public const string ConstructionSystemUpdateDrones = "ConstructionSystem.UpdateDrones";
    public const string ConstructionSystemAddBuildTargetToModules = "ConstructionSystem.AddBuildTargetToModules";
    /// <summary>
    /// The mod's dispatch-claim postfix target on the vanilla drone reset (A24).
    /// </summary>
    /// <remarks>
    /// A01 recorded it only as a *writer* of <c>DroneComponent.targetObjectId</c>, never as a target
    /// label, so A05's classification — which walks the target list — never saw it even though the
    /// mod patches it. A24 found it while auditing protected-field writers: an unclassified patch
    /// target is a client write nobody declared an owner for.
    /// </remarks>
    public const string ConstructionSystemResetDroneTargets = "ConstructionSystem.ResetDroneTargets";

    public const string ConstructStatGameTick = "ConstructStat.GameTick";
    public const string DroneComponentInternalUpdate = "DroneComponent.InternalUpdate";

    public const string SimulatedWorldOnPlayerJoinedGame = "SimulatedWorld.OnPlayerJoinedGame";

    public const string SimulatedWorldOnPlayerLeftGame = "SimulatedWorld.OnPlayerLeftGame";

    public const string GameLogicLogicFrame = "GameLogic.LogicFrame";

    public const string ThreadManagerProcessFrame = "ThreadManager.ProcessFrame";

    public const string NebulaNetworkServerUpdate = "NebulaNetwork.Server.Update";

    /// <summary>
    /// I01: client-side write surface closures. Each is a vanilla entry that writes a G1-protected
    /// balance from a path outside the migrated ledger, so a client running it outside a replica
    /// apply diverges. Guarded as host rules (host runs, client refused outside apply).
    /// </summary>
    public const string PowerSystemGameTick = "PowerSystem.GameTick";

    public const string UIMechaWindowOnReplaceFuelButtonClick = "UIMechaWindow.OnReplaceFuelButtonClick";

    public const string GameHistoryDataUnlockTechFunction = "GameHistoryData.UnlockTechFunction";

    /// <summary>
    /// I01: migrated enemy/unit AI entries. The host runs the real AI; a client must not run it at
    /// all (DESIGN 6). Effects were already contained at the guarded damage/repair/death entries with
    /// pose overwritten by the replica, but the rule itself still ticked. Guarded as host rules.
    /// </summary>
    public const string EnemyDFGroundSystemGameTickLogicUnit = "EnemyDFGroundSystem.GameTickLogic_Unit";

    /// <summary>
    /// The parallel unit loop lives on <c>GameLogic</c>, not on the ground system: guarding only the
    /// serial entry misses the actual path (README).
    /// </summary>
    public const string GameLogicEnemyGroundUnitParallel = "GameLogic._enemy_ground_unit_parallel";

    public const string EnemyDFHiveSystemGameTickLogic = "EnemyDFHiveSystem.GameTickLogic";
}

/// <summary>World readiness, host identity and the validated replica application window.</summary>
public readonly struct AuthorityGuardSession
{
    public AuthorityGuardSession(bool isActive, bool isHost, bool replicaApplyActive)
    {
        IsActive = isActive;
        IsHost = isHost;
        ReplicaApplyActive = replicaApplyActive;
    }

    public bool IsActive { get; }

    public bool IsHost { get; }

    /// <summary>True while the frame boundary has a replica apply open.</summary>
    public bool ReplicaApplyActive { get; }

    /// <summary>The state of a peer that is not in an authority room.</summary>
    public static AuthorityGuardSession Inactive => default;
}

/// <summary>
/// One entry of the A05 policy table.
/// </summary>
public readonly struct AuthorityHookPolicy
{
    public AuthorityHookPolicy(string label, string typeName, string methodName, AuthorityHookRole role,
        bool required, string owner)
    {
        Label = label;
        TypeName = typeName;
        MethodName = methodName;
        Role = role;
        Required = required;
        Owner = owner;
    }

    /// <summary>Hook identity, matching the A01 inventory label.</summary>
    public string Label { get; }

    /// <summary>Declaring type name, as the A01 inventory spells it.</summary>
    public string TypeName { get; }

    /// <summary>Method name to resolve when verifying the hook exists.</summary>
    public string MethodName { get; }

    public AuthorityHookRole Role { get; }

    /// <summary>True when multiplayer may not be entered unless this hook resolves.</summary>
    public bool Required { get; }

    /// <summary>Card that replaces the vanilla behaviour with the host-authoritative one.</summary>
    public string Owner { get; }
}

/// <summary>Permits host rule execution and validated client mirror writes.</summary>
public static class AuthorityRuleGuard
{
    private static readonly object gate = new();
    private static readonly Dictionary<(string Hook, AuthorityGuardOutcome Outcome), long> outcomes = [];
    private static readonly Dictionary<string, long> refusalsByHook = [];
    private static readonly HashSet<(string Hook, AuthorityGuardOutcome Outcome)> reportedRefusals = [];
    private static readonly Dictionary<string, AuthorityHookRole> roles = [];
    private static readonly Dictionary<string, bool> required = [];
    private static readonly List<AuthorityGuardRefusal> recentRefusals = [];

    private static Func<AuthorityGuardSession> probe;
    private static Action<string> refusalSink;
    private static readonly List<Func<IReadOnlyList<string>>> extraVerifiers = [];
    private static readonly Dictionary<string, Type> resolvedTypes = [];
    private static int loadedAssemblyCount = -1;
    private static bool installed;
    private static bool loadVerified;
    private static string loadFailure;

    /// <summary>Maximum refusals kept with their stacks, so a runaway peer cannot exhaust memory.</summary>
    public const int RecentRefusalCapacity = 64;

    /// <summary>
    /// Supplies the live session state. Registered by the world layer; unset means inactive.
    /// </summary>
    public static Func<AuthorityGuardSession> Probe
    {
        get => probe;
        set => probe = value;
    }

    /// <summary>
    /// Receives the first diagnostic for each hook/outcome in a session. Counters include repeats.
    /// </summary>
    /// <remarks>
    /// A sink rather than a direct logger call, because multiplayerl has no logger dependency and a
    /// silent refusal is exactly what the design forbids.
    /// </remarks>
    public static Action<string> RefusalSink
    {
        get => refusalSink;
        set => refusalSink = value;
    }

    /// <summary>True once the policy table was installed and every required hook resolved.</summary>
    public static bool IsLoadVerified
    {
        get
        {
            lock (gate)
            {
                return loadVerified;
            }
        }
    }

    /// <summary>Why starting multiplayer was refused, or null when it was not.</summary>
    public static string LoadFailure
    {
        get
        {
            lock (gate)
            {
                return loadFailure;
            }
        }
    }

    /// <summary>The session state the guard is currently deciding against.</summary>
    public static AuthorityGuardSession CurrentSession() => probe?.Invoke() ?? AuthorityGuardSession.Inactive;

    /// <summary>The policy table, installed on first use.</summary>
    public static IReadOnlyList<AuthorityHookPolicy> Policy
    {
        get
        {
            EnsureInstalled();
            lock (gate)
            {
                return policyTable.ToArray();
            }
        }
    }

    /// <summary>Hooks whose policy requires a resolvable vanilla target.</summary>
    public static IReadOnlyList<AuthorityHookPolicy> RequiredPolicy
    {
        get
        {
            EnsureInstalled();
            var list = new List<AuthorityHookPolicy>();
            lock (gate)
            {
                foreach (var entry in policyTable)
                {
                    if (entry.Required) list.Add(entry);
                }
            }
            return list;
        }
    }

    /// <summary>The role one hook was classified with, or <see cref="AuthorityHookRole.None"/>.</summary>
    public static AuthorityHookRole RoleFor(string label)
    {
        EnsureInstalled();
        lock (gate)
        {
            return roles.TryGetValue(label ?? string.Empty, out var role) ? role : AuthorityHookRole.None;
        }
    }

    /// <summary>True when the hook is recorded as required by the policy table.</summary>
    public static bool IsRequired(string label)
    {
        EnsureInstalled();
        lock (gate)
        {
            return required.TryGetValue(label ?? string.Empty, out var value) && value;
        }
    }

    /// <summary>Decisions recorded per hook and outcome.</summary>
    public static IReadOnlyDictionary<string, long> Outcomes
    {
        get
        {
            lock (gate)
            {
                var result = new Dictionary<string, long>();
                foreach (var entry in outcomes)
                    result[entry.Key.Hook + ":" + entry.Key.Outcome] = entry.Value;
                return result;
            }
        }
    }

    /// <summary>Refusals recorded per hook. A non-zero entry is a finding.</summary>
    public static IReadOnlyDictionary<string, long> RefusalsByHook
    {
        get
        {
            lock (gate)
            {
                return new Dictionary<string, long>(refusalsByHook);
            }
        }
    }

    /// <summary>Refusals kept for diagnosis, oldest first.</summary>
    public static IReadOnlyList<AuthorityGuardRefusal> RecentRefusals
    {
        get
        {
            lock (gate)
            {
                return recentRefusals.ToArray();
            }
        }
    }

    public static bool AllowHostRule(string label, string detail = null)
    {
        var session = CurrentSession();
        if (!session.IsActive)
        {
            return Decide(label, AuthorityGuardOutcome.NoAuthorityWorld, detail);
        }
        if (session.ReplicaApplyActive)
        {
            return Decide(label, AuthorityGuardOutcome.ReplicaApply, detail);
        }
        if (!session.IsHost)
        {
            return Decide(label, AuthorityGuardOutcome.ClientRuleRefused, detail);
        }
        return Decide(label, AuthorityGuardOutcome.None, detail);
    }

    /// <summary>Number of refusals recorded for a hook.</summary>
    public static long RefusalCount(string label)
    {
        lock (gate)
        {
            return refusalsByHook.TryGetValue(label ?? string.Empty, out var count) ? count : 0;
        }
    }

    /// <summary>Number of decisions recorded for a hook with one outcome.</summary>
    public static long OutcomeCount(string label, AuthorityGuardOutcome outcome)
    {
        lock (gate)
        {
            return outcomes.TryGetValue((HookKey(label), outcome), out var count) ? count : 0;
        }
    }

    /// <summary>Clears counters and the staged refusals. Used when a room ends and by tests.</summary>
    public static void ResetCounters()
    {
        lock (gate)
        {
            outcomes.Clear();
            refusalsByHook.Clear();
            reportedRefusals.Clear();
            recentRefusals.Clear();
        }
    }

    /// <summary>Forgets the verification result. Used by tests and by leaving a room.</summary>
    public static void ResetVerification()
    {
        lock (gate)
        {
            loadVerified = false;
            loadFailure = null;
        }
    }

    /// <summary>Classifies one hook. Called by the installer; also usable by tests.</summary>
    public static void Classify(string label, AuthorityHookRole role, bool isRequired)
    {
        if (string.IsNullOrEmpty(label)) throw new ArgumentException("A hook label is required.", nameof(label));
        lock (gate)
        {
            roles[label] = role;
            required[label] = isRequired;
        }
    }

    /// <summary>
    /// Resolves every required hook and returns the ones that do not exist.
    /// </summary>
    /// <remarks>
    /// A required hook whose target cannot be resolved means multiplayer would run without its guard, so
    /// the list must be empty before multiplayer is entered. Resolution is by simple type name across
    /// the loaded assemblies, which is enough because the A01 inventory names game and mod types that
    /// are always loaded in a running game and in the test process.
    /// </remarks>
    public static IReadOnlyList<string> VerifyRequiredHooks() => VerifyRequiredHooks(ResolveType);

    /// <summary>
    /// <see cref="VerifyRequiredHooks()"/> with an injected resolver, so tests can prove the failure
    /// path without a game assembly.
    /// </summary>
    public static IReadOnlyList<string> VerifyRequiredHooks(Func<string, Type> resolveType)
    {
        if (resolveType == null) throw new ArgumentNullException(nameof(resolveType));
        var failures = new List<string>();
        foreach (var entry in RequiredPolicy)
        {
            var type = resolveType(entry.TypeName);
            if (type == null)
            {
                failures.Add(entry.Label + ": type " + entry.TypeName + " not found");
                continue;
            }
            if (FindMethod(type, entry.MethodName) == null)
            {
                failures.Add(entry.Label + ": method " + entry.MethodName + " not found on " + entry.TypeName);
            }
        }
        return failures;
    }

    /// <summary>
    /// Registers an extra verification step run before multiplayer is entered.
    /// </summary>
    /// <remarks>
    /// Method transformations cannot be verified by resolving a method name: a transpiler that
    /// silently matched nothing leaves a method that exists but runs the wrong rule. The patcher
    /// therefore registers a verifier that reports which of its required transformations actually
    /// applied, and a failure there stops multiplayer just like a missing method does.
    /// </remarks>
    public static void RegisterVerifier(Func<IReadOnlyList<string>> verifier)
    {
        if (verifier == null) throw new ArgumentNullException(nameof(verifier));
        lock (gate)
        {
            if (!extraVerifiers.Contains(verifier)) extraVerifiers.Add(verifier);
        }
    }

    /// <summary>Forgets every registered verifier. Used by tests.</summary>
    public static void ClearVerifiers()
    {
        lock (gate)
        {
            extraVerifiers.Clear();
        }
    }

    /// <summary>Runs every registered verifier and returns their combined failures.</summary>
    public static IReadOnlyList<string> VerifyRegistered()
    {
        Func<IReadOnlyList<string>>[] snapshot;
        lock (gate)
        {
            snapshot = extraVerifiers.ToArray();
        }
        var failures = new List<string>();
        foreach (var verifier in snapshot)
        {
            IReadOnlyList<string> result;
            try
            {
                result = verifier();
            }
            catch (Exception e)
            {
                failures.Add("verifier threw " + e.GetType().Name + ": " + e.Message);
                continue;
            }
            if (result != null) failures.AddRange(result);
        }
        return failures;
    }

    /// <summary>
    /// Verifies the required hooks at most once per process.
    /// </summary>
    /// <remarks>
    /// Called at the point multiplayer would become live. A failure here is reported and multiplayer is not
    /// entered, which is DESIGN 1.8: a missing hook prevents multiplayer startup.
    /// </remarks>
    /// <returns>True when multiplayer may be entered.</returns>
    public static bool VerifyLoadOnce()
    {
        lock (gate)
        {
            if (loadVerified || loadFailure != null) return loadVerified;
        }

        var failures = new List<string>(VerifyRequiredHooks());
        failures.AddRange(VerifyRegistered());
        lock (gate)
        {
            if (failures.Count == 0)
            {
                loadVerified = true;
                return true;
            }
            loadFailure = string.Join("; ", failures);
            return false;
        }
    }

    /// <summary>Resolves a type by simple or full name across the loaded assemblies.</summary>
    /// <remarks>
    /// <para>
    /// Scanning can itself load an assembly: asking an assembly for its types resolves the types it
    /// references, and the game assembly is only loaded the first time something touches it. A single
    /// pass therefore misses the assembly being loaded by that very pass, which showed up as the
    /// first hook of the list failing to resolve while the second hook on the same type resolved.
    /// The snapshot is retaken whenever the assembly count grows, so the result does not depend on
    /// which test or which game system happened to load the assembly first.
    /// </para>
    /// <para>
    /// A type that cannot be loaded is skipped rather than fatal: the game assembly has types whose
    /// layout the runtime rejects, and one of them must not hide every hook behind it.
    /// </para>
    /// </remarks>
    public static Type ResolveType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return null;

        // Enumerating every type of every loaded assembly is expensive and throws for the game
        // assembly (its Unity-dependent types cannot all load), so a resolved name is remembered.
        // The cache is dropped when a new assembly appears, because that is exactly what can turn a
        // previous miss into a hit.
        lock (gate)
        {
            if (loadedAssemblyCount == AppDomain.CurrentDomain.GetAssemblies().Length &&
                resolvedTypes.TryGetValue(typeName, out var cached))
            {
                return cached;
            }
        }

        var resolved = ResolveTypeUncached(typeName);
        lock (gate)
        {
            loadedAssemblyCount = AppDomain.CurrentDomain.GetAssemblies().Length;
            resolvedTypes[typeName] = resolved;
        }
        return resolved;
    }

    private static Type ResolveTypeUncached(string typeName)
    {
        var scanned = 0;
        while (true)
        {
            var snapshot = AppDomain.CurrentDomain.GetAssemblies();
            if (snapshot.Length == scanned) return null;

            for (var i = scanned; i < snapshot.Length; i++)
            {
                var direct = TryGetType(snapshot[i], typeName);
                if (direct != null) return direct;

                foreach (var type in SafeTypes(snapshot[i]))
                {
                    if (type.Name == typeName || type.FullName == typeName) return type;
                }
            }
            scanned = snapshot.Length;
        }
    }

    private static Type TryGetType(Assembly assembly, string typeName)
    {
        try
        {
            return assembly.GetType(typeName, false);
        }
        catch (Exception)
        {
            // A type that the runtime refuses to load is not the type being looked for.
            return null;
        }
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            types = error.Types;
        }
        catch (Exception)
        {
            return [];
        }
        if (types == null) return [];
        var usable = new List<Type>(types.Length);
        foreach (var type in types)
        {
            if (type != null) usable.Add(type);
        }
        return usable;
    }

    private static MethodInfo FindMethod(Type type, string methodName) =>
        type.GetMethod(methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

    private static bool Decide(string label, AuthorityGuardOutcome outcome, string detail)
    {
        Record(label, outcome, detail);
        return outcome != AuthorityGuardOutcome.ClientRuleRefused;
    }

    private static string HookKey(string label) => string.IsNullOrEmpty(label) ? "(unlabelled)" : label;

    private static void Record(string label, AuthorityGuardOutcome outcome, string detail)
    {
        var key = HookKey(label);
        var refused = outcome == AuthorityGuardOutcome.ClientRuleRefused;
        string stack = null;
        var shouldReport = false;

        lock (gate)
        {
            var outcomeKey = (key, outcome);
            outcomes.TryGetValue(outcomeKey, out var count);
            outcomes[outcomeKey] = count + 1;

            if (!refused) return;

            refusalsByHook.TryGetValue(key, out var refusals);
            refusalsByHook[key] = refusals + 1;
            // Normal client ticks hit the guards for every entity. Sampling once per hook avoids
            // allocating and formatting thousands of identical stacks while keeping exact counts.
            if (!reportedRefusals.Add(outcomeKey)) return;
            shouldReport = true;
            if (outcome == AuthorityGuardOutcome.ClientRuleRefused)
            {
                stack = new System.Diagnostics.StackTrace(1, false).ToString();
            }
            if (recentRefusals.Count < RecentRefusalCapacity)
            {
                recentRefusals.Add(new AuthorityGuardRefusal(key, outcome, detail, stack));
            }
        }

        if (!shouldReport) return;
        var suffix = string.IsNullOrEmpty(detail) ? string.Empty : " (" + detail + ")";
        var line = outcome == AuthorityGuardOutcome.ClientRuleRefused
            ? "[authority] illegal client write refused: " + key + suffix
            : "[authority] retired path refused: " + key + suffix;
        refusalSink?.Invoke(line);
        if (stack != null) refusalSink?.Invoke(stack);
    }

    /// <summary>Installs the policy table once.</summary>
    /// <remarks>
    /// The table is code rather than a shipped data file on purpose: a file an installer forgets to
    /// copy would turn multiplayer off without a compile error. The test that keeps it equal to the A01
    /// inventory reads the JSON deliverable and compares, so the classification is still checked
    /// against the evidence instead of being trusted.
    /// </remarks>
    public static void EnsureInstalled()
    {
        lock (gate)
        {
            if (installed) return;
            installed = true;
        }
        foreach (var entry in policyTable)
        {
            Classify(entry.Label, entry.Role, entry.Required);
        }
    }

    private static readonly List<AuthorityHookPolicy> policyTable =
    [
        new(AuthorityHookLabels.CombatStatTickSkillLogic, "CombatStat", "TickSkillLogic",
            AuthorityHookRole.HostRule, true, "A19"),
        new(AuthorityHookLabels.CombatStatHandleFullHp, "CombatStat", "HandleFullHp",
            AuthorityHookRole.HostRule, true, "A08/A19"),
        new(AuthorityHookLabels.CombatStatHandleZeroHp, "CombatStat", "HandleZeroHp",
            AuthorityHookRole.HostRule, true, "A19"),
        new(AuthorityHookLabels.SkillSystemDamageObject, "SkillSystem", "DamageObject",
            AuthorityHookRole.HostRule, true, "A19"),
        new(AuthorityHookLabels.SkillSystemDamageGroundObjectByLocalCaster, "SkillSystem",
            "DamageGroundObjectByLocalCaster", AuthorityHookRole.HostRule, true, "A19"),
        new(AuthorityHookLabels.SkillSystemDamageGroundObjectByRemoteCaster, "SkillSystem",
            "DamageGroundObjectByRemoteCaster", AuthorityHookRole.HostRule, true, "A19"),
        new(AuthorityHookLabels.ConstructionSystemAddConstructStat, "ConstructionSystem", "AddConstructStat",
            AuthorityHookRole.HostRule, true, "A15/A18"),
        new(AuthorityHookLabels.ConstructionSystemRemoveConstructStat, "ConstructionSystem", "RemoveConstructStat",
            AuthorityHookRole.HostRule, true, "A15"),
        new(AuthorityHookLabels.ConstructionSystemRepair, "ConstructionSystem", "Repair",
            AuthorityHookRole.HostRule, true, "A17"),
        new(AuthorityHookLabels.ConstructionSystemDetermineLaunch, "ConstructionSystem", "DetermineLaunch",
            AuthorityHookRole.NativeConstruction, true, "BuildDispatch"),
        new(AuthorityHookLabels.ConstructionSystemUpdateModules, "ConstructionSystem", "UpdateModules",
            AuthorityHookRole.NativeConstruction, true, "BuildDispatch"),
        new(AuthorityHookLabels.ConstructionSystemUpdateDrones, "ConstructionSystem", "UpdateDrones",
            AuthorityHookRole.NativeConstruction, true, "BuildDispatch"),
        new(AuthorityHookLabels.ConstructStatGameTick, "ConstructStat", "GameTick",
            AuthorityHookRole.HostRule, true, "A15"),
        // Personal drone energy and motion follow native execution. Shared HP is guarded at Repair.
        new(AuthorityHookLabels.DroneComponentInternalUpdate, "DroneComponent", "InternalUpdate",
            AuthorityHookRole.NativeConstruction, false, "BuildDispatch"),
        // Scheduler and lifecycle entries. They are classified so "every A01 target has a role" holds,
        // but none of them is a rule writer, so none is required to resolve.
        new(AuthorityHookLabels.SimulatedWorldOnPlayerJoinedGame, "NebulaWorld.SimulatedWorld",
            "OnPlayerJoinedGame", AuthorityHookRole.LifecycleObserve, false, "A21"),
        new(AuthorityHookLabels.SimulatedWorldOnPlayerLeftGame, "NebulaWorld.SimulatedWorld",
            "OnPlayerLeftGame", AuthorityHookRole.LifecycleObserve, false, "A21"),
        // The frame boundary is required, not merely observed: if the method A04 hangs the drain on
        // disappears, multiplayer would run with no frame boundary at all, so it must fail to load.
        new(AuthorityHookLabels.GameLogicLogicFrame, "GameLogic", "LogicFrame",
            AuthorityHookRole.None, true, "A04"),
        new(AuthorityHookLabels.ThreadManagerProcessFrame, "ThreadManager", "ProcessFrame",
            AuthorityHookRole.None, true, "A04"),
        new(AuthorityHookLabels.NebulaNetworkServerUpdate, "NebulaNetwork.Server", "Update",
            AuthorityHookRole.None, false, "A04"),
        // Native construction intake forwards ready sites to the server-owned claim table.
        new(AuthorityHookLabels.ConstructionSystemAddBuildTargetToModules, "ConstructionSystem",
            "AddBuildTargetToModules", AuthorityHookRole.NativeConstruction, false, "BuildDispatch"),
        // Claim release remains part of the live server-owned construction protocol.
        new(AuthorityHookLabels.ConstructionSystemResetDroneTargets, "ConstructionSystem",
            "ResetDroneTargets", AuthorityHookRole.NativeConstruction, false, "BuildDispatch"),
        // I01: the client-side write surface. Host runs, client refused outside a replica apply.
        // Required=false so the frozen contract (15 required hooks) is untouched: the vanilla bodies
        // must still run on the host and in single-player, so a missing target must not stop the
        // multiplayer by itself — the audit test pins the classification instead. The enforcement lives in
        // the patches named by each label's owner card.
        new(AuthorityHookLabels.PowerSystemGameTick, "PowerSystem",
            "GameTick", AuthorityHookRole.HostRule, false, "I01/A10"),
        new(AuthorityHookLabels.UIMechaWindowOnReplaceFuelButtonClick, "UIMechaWindow",
            "OnReplaceFuelButtonClick", AuthorityHookRole.HostRule, false, "I01/A10"),
        new(AuthorityHookLabels.GameHistoryDataUnlockTechFunction, "GameHistoryData",
            "UnlockTechFunction", AuthorityHookRole.HostRule, false, "I01/A10"),
        new(AuthorityHookLabels.EnemyDFGroundSystemGameTickLogicUnit, "EnemyDFGroundSystem",
            "GameTickLogic_Unit", AuthorityHookRole.HostRule, false, "I01/A12"),
        new(AuthorityHookLabels.GameLogicEnemyGroundUnitParallel, "GameLogic",
            "_enemy_ground_unit_parallel", AuthorityHookRole.HostRule, false, "I01/A12"),
        new(AuthorityHookLabels.EnemyDFHiveSystemGameTickLogic, "EnemyDFHiveSystem",
            "GameTickLogic", AuthorityHookRole.HostRule, false, "I01/A13")
    ];
}
