#region

using System;
using System.Collections.Generic;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// Whether a ledger balance is a double (energy) or a long (counts).
/// </summary>
public enum LedgerBalanceType : byte
{
    Double = 0,
    Long = 1
}

/// <summary>
/// One row of the A09 field-writer table: who may change a ledger balance today.
/// </summary>
/// <remarks>
/// Every <see cref="LedgerResourceKind"/> (except <see cref="LedgerResourceKind.Unknown"/>) has
/// exactly one row. At A09 every row is open (<see cref="IsClosed"/> is false): the vanilla rule
/// writers below still run on every peer and the legacy packets
/// (<c>PlayerMechaData</c>, life snapshots, drone eject facts) still overwrite state. No client
/// fact packet may therefore write the ledger — there is no such path in A09, and A10 adds the
/// whitelist that closes each row. Marking a row closed early would be the lie TASKS.md forbids.
/// </remarks>
public sealed class HostResourceFieldRecord
{
    public HostResourceFieldRecord(LedgerResourceKind kind, string displayName,
        LedgerBalanceType balanceType, string ownerKind, string[] vanillaWriters,
        string[] modWriters, string[] crossDomainSources, bool isClosed, string blocker)
    {
        Kind = kind;
        DisplayName = displayName;
        BalanceType = balanceType;
        OwnerKind = ownerKind;
        VanillaWriters = vanillaWriters ?? Array.Empty<string>();
        ModWriters = modWriters ?? Array.Empty<string>();
        CrossDomainSources = crossDomainSources ?? Array.Empty<string>();
        IsClosed = isClosed;
        Blocker = blocker;
    }

    public LedgerResourceKind Kind { get; }

    public string DisplayName { get; }

    public LedgerBalanceType BalanceType { get; }

    /// <summary>"Player", "BattleBase" or "Either". Must agree with <see cref="LedgerResourceKey"/>.</summary>
    public string OwnerKind { get; }

    /// <summary>Vanilla writers from A01's IL scan (<c>authority-hooks.json</c>).</summary>
    public string[] VanillaWriters { get; }

    /// <summary>Nebula writers that still overwrite the same state in legacy rooms.</summary>
    public string[] ModWriters { get; }

    /// <summary>Cross-domain producers/consumers owned by later cards (A10 subdivisions, W00).</summary>
    public string[] CrossDomainSources { get; }

    /// <summary>False for every row at A09. A10 flips rows as it migrates them.</summary>
    public bool IsClosed { get; }

    /// <summary>Which card owns closing this row.</summary>
    public string Blocker { get; }
}

/// <summary>
/// The A09 writer table for every ledger balance (TASKS.md A09 "建立字段写入表").
/// </summary>
/// <remarks>
/// <para>
/// Vanilla lists are copied from <c>docs/host-authority/authority-hooks.json</c>
/// <c>protectedFieldWriters</c>; they are not re-typed by hand per call site. Storage-pool rows
/// (inventory, ammo/bomb/fighter/reactor/warp/delivery/forge/base stock) have no single A01
/// field: their state lives in <c>StorageComponent</c> / <c>DeliveryPackage</c> /
/// <c>MechaForge</c> pools whose producers span factory, logistics and player actions, so those
/// rows name the cross-domain sources explicitly and leave closure to A10 (mecha-adjacent pools)
/// and W00 (factory/logistics producers).
/// </para>
/// <para>
/// Tests pin the table: every ledger kind has a row, every row is open at A09, and the owner-kind
/// column agrees with <see cref="LedgerResourceKey"/>. A row may only be marked closed by the
/// card named in its <see cref="HostResourceFieldRecord.Blocker"/>.
/// </para>
/// </remarks>
public static class HostResourceFields
{
    private static readonly HostResourceFieldRecord[] Records =
    {
        new(LedgerResourceKind.CoreEnergy, "Mecha.coreEnergy", LedgerBalanceType.Double, "Player",
            new[]
            {
                "PowerSystem.GameTick", "UnitComponent.UpdateMechaEnergy",
                "UnitComponent.RunBehavior_Engage_AttackLaser_Ground",
                "UnitComponent.RunBehavior_Engage_AttackPlasma_Ground",
                "UnitComponent.RunBehavior_Engage_DefenseShield_Ground",
                "UnitComponent.RunBehavior_Engage_SAttackLaser_Large",
                "UnitComponent.RunBehavior_Engage_SAttackPlasma_Small",
                "UnitComponent.RunBehavior_Orbiting",
                "UnitComponent.RunBehavior_Recycled_Ground",
                "UnitComponent.RunBehavior_Recycled_Space",
                "UnitComponent.RunBehavior_SeekForm_Ground",
                "UnitComponent.RunBehavior_SeekForm_Space",
                "PlayerAction_Inspect.GameTick", "PlayerAction_Mine.GameTick",
                "PlayerAction_Test.Update",
                "Mecha.Init", "Mecha.SetForNewGame", "Mecha.GenerateEnergy", "Mecha.Import",
                "Mecha.UseEnergy", "Mecha.UpdateCombatStats", "Mecha.Respawn", "Mecha.Kill",
                "Mecha.TickAmmoFireCondition"
            },
            new[]
            {
                "PlayerMechaDataProcessor (client full-cover overwrite)",
                "PlayerLifeManager.StoreServer/RestoreServer (snapshot overwrite)"
            },
            new[] { "Factory power allocation/consumption share (A10-3, W02)", "Charging/fuel补给 share (A10-1)" },
            false, "A10"),
        new(LedgerResourceKind.ReactorEnergy, "Mecha.reactorEnergy", LedgerBalanceType.Double, "Player",
            new[]
            {
                "Mecha.Init", "Mecha.SetForNewGame", "Mecha.GenerateEnergy", "Mecha.Import",
                "Mecha.PrepareRedeploy", "UIMechaWindow.OnReplaceFuelButtonClick"
            },
            new[]
            {
                "PlayerMechaDataProcessor (client full-cover overwrite)",
                "PlayerLifeManager.StoreServer/RestoreServer (snapshot overwrite)"
            },
            new[] { "Fuel补给/manufacturing share (A10-1)", "Factory production input (W02)" },
            false, "A10"),
        new(LedgerResourceKind.BaseEnergy, "BattleBaseComponent.energy", LedgerBalanceType.Double, "BattleBase",
            new[]
            {
                "BattleBaseComponent.Init", "BattleBaseComponent.Reset", "BattleBaseComponent.Import",
                "BattleBaseComponent.InternalUpdate", "ConstructionSystem.DetermineLaunch",
                "UnitComponent.UpdateBattleBaseEnergy",
                "UnitComponent.RunBehavior_Engage_AttackLaser_Ground",
                "UnitComponent.RunBehavior_Engage_AttackPlasma_Ground",
                "UnitComponent.RunBehavior_Engage_DefenseShield_Ground",
                "UnitComponent.RunBehavior_Recycled_Ground",
                "UnitComponent.RunBehavior_SeekForm_Ground"
            },
            new[] { "ConstructionSystem_Patch legacy paths (guarded at A05, migrated at A17)" },
            new[] { "Factory power grid share (A10-3, W02)", "Base drone launch share (A17)" },
            false, "A10"),
        new(LedgerResourceKind.Sand, "Player.sandCount", LedgerBalanceType.Long, "Player",
            new[]
            {
                "Player.set_sandCount (auto-property; call sites pending A24 audit)",
                "PlayerAction_Mine.GameTick (soil collection)"
            },
            new[]
            {
                "PlayerMechaDataProcessor (client full-cover overwrite)",
                "PlayerSandCountProcessor (client-reported increments)"
            },
            new[] { "Terrain/reform share (W01)", "Foundation stock share (W01)" },
            false, "A10"),
        new(LedgerResourceKind.DroneSlot, "Construction drone quota", LedgerBalanceType.Long, "Either",
            new[]
            {
                "ConstructionModuleComponent.set_droneCount",
                "ConstructionModuleComponent.set_droneIdleCount",
                "ConstructionModuleComponent.set_droneAliveCount",
                "ConstructionModuleComponent.CreateDrones",
                "ConstructionModuleComponent.EjectMechaDrone",
                "ConstructionModuleComponent.EjectBaseDrone",
                "ConstructionModuleComponent.RecycleDrone",
                "ConstructionSystem.DetermineLaunch", "ConstructionSystem.UpdateDrones",
                "ConstructionSystem.UpdateModules", "ConstructionSystem.OnDronePriorityChange"
            },
            new[]
            {
                "DroneManager private remote pool (E05; legacy-remove at A17)",
                "BuildDispatchManager claim accounting (migrated at A18)"
            },
            new[] { "DroneBudget invariant idle+reserved+active+returning==total (A15)" },
            false, "A10"),
        new(LedgerResourceKind.InventoryItem, "player.package (StorageComponent)", LedgerBalanceType.Long, "Player",
            new[] { "StorageComponent.Export/Import (pool mechanics; producers span domains)" },
            new[]
            {
                "PlayerMechaDataProcessor (client full-cover overwrite)",
                "PlayerGiveItemProcessor (client-asserted transfer)"
            },
            new[]
            {
                "PlayerAction_Build/Mine take/put (A10-2)", "MechaForge manufacture (A10-2, W02)",
                "Factory/cargo/logistics transfers (A10-3, W02-W04)", "Death-drop share (A11/A19)"
            },
            false, "A10"),
        new(LedgerResourceKind.AmmoBullet, "Mecha.ammoBulletCount", LedgerBalanceType.Long, "Player",
            new[]
            {
                "PlayerAction_Combat.AmmoFireProcedure", "Mecha.Init", "Mecha.SetForNewGame",
                "Mecha.Import", "Mecha.PrepareRedeploy", "Mecha.LoadAmmo"
            },
            new[] { "PlayerMechaDataProcessor (client full-cover overwrite)" },
            new[] { "Auto-replenish share (A10-1)", "Fire-path deduction order (A11)" },
            false, "A10"),
        new(LedgerResourceKind.AmmoStorageItem, "Mecha.ammoStorage", LedgerBalanceType.Long, "Player",
            new[] { "StorageComponent.Export/Import; PlayerAction_Combat.AmmoFireProcedure/LoadAmmo" },
            new[] { "PlayerMechaDataProcessor (client full-cover overwrite)" },
            new[] { "Auto-replenish share (A10-1)", "Factory ammo supply (A10-2, W02)" },
            false, "A10"),
        new(LedgerResourceKind.BombStorageItem, "Mecha.bombStorage + bombFire", LedgerBalanceType.Long, "Player",
            new[]
            {
                "PlayerAction_Combat.Bombing", "Mecha.Init", "Mecha.SetForNewGame", "Mecha.Import",
                "Mecha.Respawn", "Mecha.PrepareRedeploy", "Mecha.TickBombFireCondition"
            },
            new[] { "PlayerMechaDataProcessor (client full-cover overwrite)" },
            new[] { "Bomb stock share (A10-1)", "Fire-path deduction order (A11)" },
            false, "A10"),
        new(LedgerResourceKind.FighterStorageItem, "Mecha.fighterStorage", LedgerBalanceType.Long, "Player",
            new[] { "StorageComponent.Export/Import; fleet deploy/recycle paths (A13)" },
            new[] { "PlayerMechaDataProcessor (client full-cover overwrite)" },
            new[] { "Hangar auto-replenish share (A10-2)", "Fleet order share (A11/A13)" },
            false, "A10"),
        new(LedgerResourceKind.ReactorStorageItem, "Mecha.reactorStorage", LedgerBalanceType.Long, "Player",
            new[] { "StorageComponent.Export/Import; Mecha.GenerateEnergy fuel intake" },
            new[] { "PlayerMechaDataProcessor (client full-cover overwrite)" },
            new[] { "Fuel补给 share (A10-1)", "Factory fuel production (W02)" },
            false, "A10"),
        new(LedgerResourceKind.WarpStorageItem, "Mecha.warpStorage", LedgerBalanceType.Long, "Player",
            new[] { "StorageComponent.Export/Import; sail/warp consumption paths" },
            new[] { "PlayerMechaDataProcessor (client full-cover overwrite)" },
            new[] { "Warp fuel share (A10-1)", "Logistics supply (W04)" },
            false, "A10"),
        new(LedgerResourceKind.DeliveryItem, "player.deliveryPackage", LedgerBalanceType.Long, "Player",
            new[] { "DeliveryPackage.Export/Import; logistics bot take/put" },
            new[] { "PlayerMechaDataProcessor (client full-cover overwrite)" },
            new[] { "Ground/air logistics share (A10-2, W03)" },
            false, "A10"),
        new(LedgerResourceKind.ForgeItem, "Mecha.forge queue + storage", LedgerBalanceType.Long, "Player",
            new[] { "MechaForge.Export/Import; manufacture queue paths" },
            new[] { "PlayerMechaDataProcessor (client full-cover overwrite)" },
            new[] { "Manufacture share (A10-2, W02)", "Hand-craft settings (W01)" },
            false, "A10"),
        new(LedgerResourceKind.BaseStockItem, "Battle-base stock", LedgerBalanceType.Long, "BattleBase",
            new[] { "BattleBaseComponent stock pools (Import/InternalUpdate)" },
            new[] { "ConstructionSystem_Patch legacy paths (guarded at A05, migrated at A17)" },
            new[] { "Factory pool transfers (A10-3, W02)", "Base construction share (A18)" },
            false, "A10")
    };

    /// <summary>Every ledger balance's writer row.</summary>
    public static IReadOnlyList<HostResourceFieldRecord> All => Records;

    public static bool TryGet(LedgerResourceKind kind, out HostResourceFieldRecord record)
    {
        foreach (var candidate in Records)
        {
            if (candidate.Kind == kind)
            {
                record = candidate;
                return true;
            }
        }
        record = null;
        return false;
    }

    /// <summary>False for every kind at A09. Kept as a lookup so A10 flips rows explicitly.</summary>
    public static bool IsClosed(LedgerResourceKind kind) =>
        TryGet(kind, out var record) && record.IsClosed;
}
