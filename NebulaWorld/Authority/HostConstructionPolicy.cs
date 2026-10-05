#region

#endregion

namespace NebulaWorld.Authority;

/// <summary>
/// Policy for a fully integrated task-ledger construction executor (pure model).
/// </summary>
/// <remarks>
/// The production session currently uses server-owned BuildDispatch claims and native drones.
/// These suppression decisions apply only when a real world, energy and presentation adapter is
/// installed for the task executor; authority combat mode alone is insufficient to enable them.
/// <para>
/// The old room dispatches repairs from every peer: vanilla <c>DetermineLaunch</c> serves its
/// single <c>player</c>, module <c>PreLaunchDrone</c> (inside <c>UpdateModules</c>) competes for
/// the same idle slots, the vanilla <c>UpdateDrones</c> loop calls <c>Repair</c> with whatever
/// energy the local peer has (and <c>FindNextRepair</c> claims the next target on its own), the
/// <c>UpdateModules</c> tail zeroes every <c>repairerCount</c> when all drones look idle, and the
/// mod's private <c>DroneManager</c> pool advances remote drones with a dummy infinite energy
/// (E05). In a host authority world every one of those is a peer deciding shared repair facts,
/// so the mode suppresses them all and takes repairs from the host task ledger instead (A15/A16
/// truth, this card's execution). Legacy rooms keep the old paths byte for byte.
/// </para>
/// <para>
/// Pure like <see cref="HostCombatPolicy"/>: call sites check the live mode bit and ask here
/// what to do, so tests drive the decision without a game process.
/// </para>
/// </remarks>
public static class HostConstructionPolicy
{
    /// <summary>True when the vanilla drone-motion loop must not run (host executor drives).</summary>
    public static bool ShouldSuppressVanillaDroneLoop(bool isHostAuthority) => isHostAuthority;

    /// <summary>True when vanilla dispatch must not reserve slots or launch drones.</summary>
    public static bool ShouldSuppressDetermineLaunch(bool isHostAuthority) => isHostAuthority;

    /// <summary>True when the "all idle so clear repairerCount" self-heal must not run.</summary>
    public static bool ShouldSuppressIdleReset(bool isHostAuthority) => isHostAuthority;

    /// <summary>True when recycle must follow the task owner's planet, not the host view.</summary>
    public static bool MustUseOwnerPlanetForRecycle(bool isHostAuthority) => isHostAuthority;

    /// <summary>True when client drones are display-only TaskBatch state (no Repair call).</summary>
    public static bool MustPresentDronesFromTasks(bool isHostAuthority) => isHostAuthority;

    /// <summary>True when build dispatch must come from the host task ledger, not client launch facts (A18).</summary>
    public static bool MustUseHostTaskForBuildDispatch(bool isHostAuthority) => isHostAuthority;

    /// <summary>True when a legacy build-launch fact packet must be refused without touching the world (A18).</summary>
    public static bool ShouldRefuseLegacyBuildLaunch(bool isHostAuthority) => isHostAuthority;

    /// <summary>True when a client-asserted material placement must not rewrite the host prebuild (A18).</summary>
    public static bool ShouldRefuseClientMaterialClaim(bool isHostAuthority) => isHostAuthority;

    /// <summary>
    /// True when the legacy build-target claim conversation must carry no traffic at all (A24).
    /// </summary>
    /// <remarks>
    /// A18 retired the claim conversation as a *rule* input but left its packets flowing: a client
    /// still answered an assignment, still reported ready, still released a target, and the host still
    /// folded all of it into a claim table that nothing reads in the new mode. A24's acceptance is
    /// that no old world-fact packet is processed in the new mode, and "processed into a table that is
    /// ignored" is still processing — it also makes the wire unreadable, because a reviewer cannot
    /// tell the live protocol from the retired one. Both directions are refused at the boundary; the
    /// claim state that remains is local display state only.
    /// </remarks>
    public static bool ShouldRefuseLegacyClaimProtocol(bool isHostAuthority) => isHostAuthority;

    /// <summary>Why the claim conversation was refused, for logs.</summary>
    public static string ClaimSuppressionReason(string path) =>
        "legacy " + path + " carries build-target claim facts; the host task ledger owns build " +
        "dispatch in host authority mode (A18/A24)";

    /// <summary>Why a suppression happened, for logs. Callers branch on the bool, never on this text.</summary>
    public static string SuppressionReason(string path) =>
        "legacy " + path + " decides shared repair facts; host task ledger owns dispatch in host authority mode (A17)";

    /// <summary>Why a build-path refusal happened, for logs. Callers branch on the bool, never on this text.</summary>
    public static string BuildSuppressionReason(string path) =>
        "legacy " + path + " decides shared build facts; host task ledger owns build dispatch in host authority mode (A18)";
}
