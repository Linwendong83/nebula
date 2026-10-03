#region

using System;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// One build owner's host-accepted state for candidate filtering (TASKS.md A18, pure model).
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="RepairOwnerSnapshot"/> for the build switch: planet, position and switches
/// come from the host's accepted presence — <see cref="HostPlayerRegistry"/> (which already tracks
/// <c>BuildEnabled</c> next to <c>RepairEnabled</c>) for players, the build base source for battle
/// bases — never from the host's own local planet. There is no host-planet field on this type, so
/// "放置者离星由 owner 星球判定" is structural, like the A16 repair rule.
/// </para>
/// <para>
/// Energy is a boolean on purpose, like A16: the production adapter reads the real core/base
/// energy and answers sufficient/insufficient, while tests inject the value directly. The dispatch
/// rule never sees a raw energy number, and per-tick flight/build energy is spent through
/// <see cref="HostResourceLedger"/> by the executor, never asserted by a client packet.
/// </para>
/// </remarks>
public readonly struct BuildOwnerSnapshot : IEquatable<BuildOwnerSnapshot>
{
    public BuildOwnerSnapshot(ConstructionOwnerKey owner, int planetId,
        float posX, float posY, float posZ, float speed,
        bool isAvailable, bool constructEnabled, bool droneEnabled,
        float range, bool energySufficient)
    {
        Owner = owner;
        PlanetId = planetId;
        PosX = posX;
        PosY = posY;
        PosZ = posZ;
        Speed = speed;
        IsAvailable = isAvailable;
        ConstructEnabled = constructEnabled;
        DroneEnabled = droneEnabled;
        Range = range;
        EnergySufficient = energySufficient;
    }

    /// <summary>Which seat would fly the drone.</summary>
    public ConstructionOwnerKey Owner { get; }

    /// <summary>Host-accepted planet of the owner, or 0 when unknown.</summary>
    public int PlanetId { get; }

    public float PosX { get; }

    public float PosY { get; }

    public float PosZ { get; }

    /// <summary>Host-accepted scalar speed. Only mecha owners are gated by it.</summary>
    public float Speed { get; }

    /// <summary>Registry eligibility: online, alive, and allowed to own drone work.</summary>
    public bool IsAvailable { get; }

    /// <summary>Construction build switch (vanilla <c>droneConstructEnabled</c>), as the host last accepted it.</summary>
    public bool ConstructEnabled { get; }

    /// <summary>Master construction-drone switch.</summary>
    public bool DroneEnabled { get; }

    /// <summary>Vanilla build area (mecha) or base build range (base).</summary>
    public float Range { get; }

    /// <summary>Whether the owner can pay flight and build energy this tick.</summary>
    public bool EnergySufficient { get; }

    public bool IsMecha => Owner.IsPlayer;

    public bool Equals(BuildOwnerSnapshot other) =>
        Owner.Equals(other.Owner) && PlanetId == other.PlanetId &&
        PosX == other.PosX && PosY == other.PosY && PosZ == other.PosZ && Speed == other.Speed &&
        IsAvailable == other.IsAvailable && ConstructEnabled == other.ConstructEnabled &&
        DroneEnabled == other.DroneEnabled && Range == other.Range &&
        EnergySufficient == other.EnergySufficient;

    public override bool Equals(object obj) => obj is BuildOwnerSnapshot other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Owner.GetHashCode();
            hash = (hash * 397) ^ PlanetId;
            hash = (hash * 397) ^ PosX.GetHashCode();
            hash = (hash * 397) ^ PosY.GetHashCode();
            hash = (hash * 397) ^ PosZ.GetHashCode();
            hash = (hash * 397) ^ Speed.GetHashCode();
            hash = (hash * 397) ^ IsAvailable.GetHashCode();
            hash = (hash * 397) ^ ConstructEnabled.GetHashCode();
            hash = (hash * 397) ^ DroneEnabled.GetHashCode();
            hash = (hash * 397) ^ Range.GetHashCode();
            hash = (hash * 397) ^ EnergySufficient.GetHashCode();
            return hash;
        }
    }

    public override string ToString() =>
        Owner + "|planet=" + PlanetId + "|speed=" + Speed + "|range=" + Range;
}

/// <summary>
/// One prebuild's host-accepted record for build candidate filtering (TASKS.md A18, pure model).
/// </summary>
/// <remarks>
/// <para>
/// The production adapter builds these from the real <c>prebuildPool</c> without mutating it;
/// tests build them by hand. <see cref="Target"/> is the full <see cref="ObjectKey"/> including
/// generation: a caller that names another generation of the same slot gets
/// <see cref="ConstructionDispatchReason.WrongGeneration"/>, never an implicit slide onto the
/// new object. <see cref="Kind"/> is derived from <see cref="IsDestroyed"/> by the service:
/// green prebuilds take <see cref="ConstructionTaskKind.Build"/>, destroyed ones take
/// <see cref="ConstructionTaskKind.Reconstruct"/> (auto-rebuild and ruin handling share this path).
/// </para>
/// <para>
/// Material recipe and placement state arrive as primitives — the production adapter reads the
/// real <c>itemRequired/protoId</c>, tests inject canned numbers — so this file never copies a
/// vanilla recipe table. A prebuild that still needs items (<see cref="ItemRequired"/> != 0) is
/// <see cref="ConstructionDispatchReason.TargetNotReady"/>, not dispatchable: legacy
/// <c>IsReady</c> requires <c>itemRequired == 0</c> for the same reason.
/// </para>
/// </remarks>
public readonly struct BuildTargetSnapshot : IEquatable<BuildTargetSnapshot>
{
    public BuildTargetSnapshot(ObjectKey target, int planetId,
        int itemRequired, bool isDestroyed, int currentBuilders)
    {
        Target = target;
        PlanetId = planetId;
        ItemRequired = itemRequired;
        IsDestroyed = isDestroyed;
        CurrentBuilders = currentBuilders;
    }

    /// <summary>Full prebuild key including generation.</summary>
    public ObjectKey Target { get; }

    /// <summary>Planet the prebuild lives on.</summary>
    public int PlanetId { get; }

    /// <summary>Remaining material requirement. Non-zero means the site is not green yet.</summary>
    public int ItemRequired { get; }

    /// <summary>True for ruins / destroyed sites awaiting reconstruction.</summary>
    public bool IsDestroyed { get; }

    /// <summary>Live build/reconstruct tasks holding a builder slot on exactly this key (ledger-derived).</summary>
    public int CurrentBuilders { get; }

    public bool Equals(BuildTargetSnapshot other) =>
        Target.Equals(other.Target) && PlanetId == other.PlanetId &&
        ItemRequired == other.ItemRequired && IsDestroyed == other.IsDestroyed &&
        CurrentBuilders == other.CurrentBuilders;

    public override bool Equals(object obj) => obj is BuildTargetSnapshot other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Target.GetHashCode();
            hash = (hash * 397) ^ PlanetId;
            hash = (hash * 397) ^ ItemRequired;
            hash = (hash * 397) ^ IsDestroyed.GetHashCode();
            hash = (hash * 397) ^ CurrentBuilders;
            return hash;
        }
    }

    public override string ToString() =>
        Target + "|need=" + ItemRequired + "|ruin=" + IsDestroyed + "|builders=" + CurrentBuilders;
}

/// <summary>
/// Material requirement of one build target for one owner (TASKS.md A18, pure model).
/// </summary>
/// <remarks>
/// The caller (production adapter or test) supplies the recipe; the executor spends it through
/// <see cref="HostResourceLedger"/> exactly once when the build finishes. Player owners spend
/// <see cref="LedgerResourceKind.InventoryItem"/>, base owners spend
/// <see cref="LedgerResourceKind.BaseStockItem"/>. A free requirement (no item or non-positive
/// count) spends nothing and still completes: the task lifecycle must not depend on a balance
/// that does not exist.
/// </remarks>
public readonly struct BuildMaterialRequirement : IEquatable<BuildMaterialRequirement>
{
    public BuildMaterialRequirement(int itemId, long count)
    {
        ItemId = itemId;
        Count = count;
    }

    public int ItemId { get; }

    public long Count { get; }

    public bool IsFree => ItemId <= 0 || Count <= 0;

    public bool Equals(BuildMaterialRequirement other) =>
        ItemId == other.ItemId && Count == other.Count;

    public override bool Equals(object obj) => obj is BuildMaterialRequirement other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return (ItemId * 397) ^ Count.GetHashCode();
        }
    }

    public override string ToString() => "item=" + ItemId + "x" + Count;
}

/// <summary>
/// Pure build candidate filter (TASKS.md A18).
/// </summary>
/// <remarks>
/// <para>
/// Same gate order as the A16 repair filter so coincident failures report deterministically:
/// shape → generation → record readiness → owner eligibility → speed → range → demand coverage →
/// energy → budget. The reason codes are the shared <see cref="ConstructionDispatchReason"/>
/// vocabulary: <see cref="ConstructionDispatchReason.NoDamageRecord"/> is unused here (a passed
/// snapshot is the record); a prebuild that is not green or whose ruin state mismatches the task
/// kind is <see cref="ConstructionDispatchReason.TargetNotReady"/>; a prebuild that already has
/// a builder is <see cref="ConstructionDispatchReason.DemandCovered"/>, which is the ledger form
/// of the legacy claim's one-owner-per-prebuild rule.
/// </para>
/// </remarks>
public static class HostBuildDispatch
{
    /// <summary>Vanilla mecha build gate, same 20 m/s as the repair gate.</summary>
    public const float MechaSpeedLimit = 20f;

    /// <summary>
    /// Decides whether one owner may build one prebuild this tick.
    /// </summary>
    /// <param name="kind">Build for green sites, Reconstruct for destroyed ones.</param>
    public static ConstructionDispatchReason EvaluateBuild(
        in BuildOwnerSnapshot owner,
        in ObjectKey target,
        in BuildTargetSnapshot snapshot,
        ConstructionTaskKind kind,
        float distanceSquared,
        int idleDrones)
    {
        // 1. Shapes. A build task only ever addresses Prebuild keys.
        if (kind != ConstructionTaskKind.Build && kind != ConstructionTaskKind.Reconstruct)
            return ConstructionDispatchReason.TargetNotReady;
        if (!target.IsValid || target.Kind != PoolKind.Prebuild)
            return ConstructionDispatchReason.TargetNotReady;
        if (!snapshot.Target.IsValid || snapshot.Target.Kind != PoolKind.Prebuild)
            return ConstructionDispatchReason.TargetNotReady;
        if (snapshot.PlanetId <= 0)
            return ConstructionDispatchReason.TargetNotReady;
        if (float.IsNaN(distanceSquared) || distanceSquared < 0f)
            return ConstructionDispatchReason.TargetNotReady;

        // 2. Generation: the request must name exactly the record's generation.
        if (!target.Epoch.Equals(snapshot.Target.Epoch) ||
            target.Scope != snapshot.Target.Scope ||
            target.NativeId != snapshot.Target.NativeId ||
            target.Generation != snapshot.Target.Generation)
            return ConstructionDispatchReason.WrongGeneration;
        if (target.Scope != snapshot.PlanetId)
            return ConstructionDispatchReason.TargetNotReady;

        // 3. Record readiness. Green sites build, destroyed sites reconstruct, and a site that
        // still needs materials is not green yet (legacy IsReady requires itemRequired == 0).
        if (kind == ConstructionTaskKind.Build && snapshot.IsDestroyed)
            return ConstructionDispatchReason.TargetNotReady;
        if (kind == ConstructionTaskKind.Reconstruct && !snapshot.IsDestroyed)
            return ConstructionDispatchReason.TargetNotReady;
        if (snapshot.ItemRequired != 0)
            return ConstructionDispatchReason.TargetNotReady;

        // 4. Owner eligibility. Unknown, offline, dead, virtual-server and stale-seat owners all
        // report Disabled; the caller distinguishes them in logs, not in the reason code.
        if (!owner.Owner.IsValid)
            return ConstructionDispatchReason.Disabled;
        if (!owner.IsAvailable || !owner.ConstructEnabled || !owner.DroneEnabled)
            return ConstructionDispatchReason.Disabled;

        // 5. Vanilla speed gate, mecha owners only. Bases do not move.
        if (owner.IsMecha && owner.Speed > MechaSpeedLimit)
            return ConstructionDispatchReason.SpeedTooHigh;

        // 6. Planet and range, both from the candidate owner's state.
        if (owner.PlanetId != snapshot.PlanetId)
            return ConstructionDispatchReason.OutOfRange;
        if (owner.Range <= 0f)
            return ConstructionDispatchReason.OutOfRange;
        var reach = (double)owner.Range * owner.Range;
        if ((double)distanceSquared > reach)
            return ConstructionDispatchReason.OutOfRange;

        // 7. Demand coverage: one builder per prebuild, like the legacy claim's exclusivity.
        if (snapshot.CurrentBuilders > 0)
            return ConstructionDispatchReason.DemandCovered;

        // 8. Resources: energy before budget, matching the vanilla launch order.
        if (!owner.EnergySufficient)
            return ConstructionDispatchReason.NoEnergy;
        if (idleDrones <= 0)
            return ConstructionDispatchReason.NoIdleDrone;

        return ConstructionDispatchReason.None;
    }
}

/// <summary>
/// What one live build task should do this tick (TASKS.md A18, pure decision).
/// </summary>
public enum HostBuildTaskFate : byte
{
    Keep = 0,
    CompleteBuild = 1,
    Cancel = 2
}

/// <summary>
/// Owner-lifecycle and target-lifecycle gates for live build tasks (TASKS.md A18, pure model).
/// </summary>
/// <remarks>
/// <para>
/// Speed and energy never cancel a live task: vanilla only gates new launches above 20 m/s, and
/// a dry drone pauses with ratio zero while new dispatches stay gated by
/// <see cref="ConstructionDispatchReason.NoEnergy"/> (same rule as A17 repairs). Cancelling live
/// work on every brownout would churn budgets and leak material reservations.
/// </para>
/// </remarks>
public static class HostBuildLifecycle
{
    /// <summary>
    /// Owner-side cancel reason for a live task, or null when the owner may keep working.
    /// </summary>
    public static ConstructionCancelReason? OwnerCancelReason(in BuildOwnerSnapshot owner,
        int buildPlanet)
    {
        if (!owner.Owner.IsValid || !owner.IsAvailable)
            return ConstructionCancelReason.OwnerOffline;
        if (!owner.ConstructEnabled || !owner.DroneEnabled)
            return ConstructionCancelReason.SwitchDisabled;
        if (owner.PlanetId != buildPlanet)
            return ConstructionCancelReason.OwnerLeftPlanet;
        return null;
    }

    /// <summary>
    /// Target-side fate for a live task. A reconstructed site completes; a lost record or a
    /// generation mismatch cancels; otherwise the task keeps working.
    /// </summary>
    /// <param name="present">False when the prebuild no longer has a host record this tick.</param>
    public static (HostBuildTaskFate Fate, ConstructionCancelReason Cancel) TargetFate(
        in ObjectKey taskTarget, ConstructionTaskKind kind, ConstructionTaskStage stage,
        bool present, in BuildTargetSnapshot snapshot)
    {
        if (!present)
        {
            // A drone flying home outlives its site: the build already finished through the
            // writer, so the task completes. Any earlier stage lost its site before finishing.
            if (stage == ConstructionTaskStage.Returning)
                return (HostBuildTaskFate.CompleteBuild, ConstructionCancelReason.None);
            return (HostBuildTaskFate.Cancel, ConstructionCancelReason.TargetDemolished);
        }
        if (!taskTarget.IsValid || taskTarget.Kind != PoolKind.Prebuild)
            return (HostBuildTaskFate.Cancel, ConstructionCancelReason.TargetDemolished);
        if (!taskTarget.Epoch.Equals(snapshot.Target.Epoch) ||
            taskTarget.Scope != snapshot.Target.Scope ||
            taskTarget.NativeId != snapshot.Target.NativeId ||
            taskTarget.Generation != snapshot.Target.Generation)
            return (HostBuildTaskFate.Cancel, ConstructionCancelReason.TargetRecycled);
        if (kind == ConstructionTaskKind.Build && snapshot.IsDestroyed)
            return (HostBuildTaskFate.Cancel, ConstructionCancelReason.TargetDestroyed);
        if (kind == ConstructionTaskKind.Reconstruct && !snapshot.IsDestroyed)
        {
            // The ruin is already whole: same shape as a repair reaching full HP.
            return (HostBuildTaskFate.CompleteBuild, ConstructionCancelReason.None);
        }
        if (snapshot.ItemRequired != 0)
            return (HostBuildTaskFate.Cancel, ConstructionCancelReason.InsufficientStock);
        return (HostBuildTaskFate.Keep, ConstructionCancelReason.None);
    }
}
