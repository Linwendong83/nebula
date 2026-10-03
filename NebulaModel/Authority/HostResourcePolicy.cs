#region

using System;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// Which A10 subdivision owns a ledger balance (TASKS.md A10).
/// </summary>
/// <remarks>
/// <para>
/// A10 is split into three reviewable submissions, each of which must list its writers:
/// </para>
/// <list type="number">
/// <item>A10-1 mecha energy and ammo: core/reactor energy, ammo bullet and storages, bomb stock,
/// reactor/warp fuel — flight, charging, generation, auto-replenish, firing and fuel supply.</item>
/// <item>A10-2 inventory, construction material and fleet: sand, player package, delivery, forge,
/// fighter hangar and player-side drone quota — take/put, manufacture, death drops.</item>
/// <item>A10-3 base, turret and factory boundary: battle-base energy/stock and base-side drone
/// quota — base launch share, turret ammo/power, factory pool transfers (host pool is the source;
/// full production simulation stays in W02).</item>
/// </list>
/// <para>
/// The mapping is structural, not a comment: <see cref="HostResourcePolicy.SubdivisionFor"/>
/// assigns every <see cref="LedgerResourceKind"/> to exactly one subdivision so a later card can
/// tell which A10 submission a balance came from. <c>DroneSlot</c> is the only kind both a mecha
/// and a base may own; the key overload refines it by owner.
/// </para>
/// </remarks>
public enum HostResourceSubdivision : byte
{
    Unknown = 0,
    MechaEnergyAmmo = 1,
    InventoryFleet = 2,
    BaseFactory = 3
}

/// <summary>
/// Which legacy packet a resource decision is about (TASKS.md A10 wire scope).
/// </summary>
/// <remarks>
/// Only the two client-to-host full overwrites are refused in authority mode:
/// <see cref="MechaData"/> (periodic client snapshot) and <see cref="LifeSnapshot"/> (client
/// snapshot bytes via <c>PlayerLifeManager.StoreServer</c>). Host-to-client facts
/// (<see cref="SandCount"/>, <see cref="GiveItem"/>) stay allowed because they carry host truth
/// to the display, not client claims to the ledger. Factory UI transfers are not refused here;
/// they are replaced by commands in A11/A18 and fully simulated in W02.
/// </remarks>
public enum HostResourcePacketKind : byte
{
    Unknown = 0,
    MechaData = 1,
    LifeSnapshot = 2,
    SandCount = 3,
    GiveItem = 4
}

/// <summary>
/// Authority-mode field policy for the resource ledger (TASKS.md A10, pure model).
/// </summary>
/// <remarks>
/// <para>
/// The rule is deny-by-default for client facts: in a host authority world no client packet may
/// write a ledger balance. The host seeds balances from its own state (host-only entry, never
/// from a client packet) and thereafter only frame-boundary transactions move them. What a client
/// packet may still carry in authority mode is nothing in A10 — the whitelist is empty on
/// purpose (fail-closed). Later cards add narrow input commands (A11 combat inputs, A16/A18
/// construction options) without reopening the full overwrite.
/// </para>
/// <para>
/// This type is pure: it takes a mode flag, never a session or a game object, so tests drive it
/// without a running game. Processors pass the live session's
/// <c>IsHostAuthority</c> bit; the decision here is what they enforce.
/// </para>
/// </remarks>
public static class HostResourcePolicy
{
    /// <summary>True for every ledger balance: no client fact may write it in authority mode.</summary>
    public static bool IsProtected(LedgerResourceKind kind) => kind != LedgerResourceKind.Unknown;

    /// <summary>Which A10 subdivision a balance belongs to (kind-only view).</summary>
    /// <remarks>
    /// <c>DroneSlot</c> without an owner maps to <see cref="HostResourceSubdivision.InventoryFleet"/>;
    /// use <see cref="SubdivisionFor(LedgerResourceKey)"/> when the owner is known so a base-owned
    /// quota lands in <see cref="HostResourceSubdivision.BaseFactory"/>.
    /// </remarks>
    public static HostResourceSubdivision SubdivisionFor(LedgerResourceKind kind)
    {
        switch (kind)
        {
            case LedgerResourceKind.CoreEnergy:
            case LedgerResourceKind.ReactorEnergy:
            case LedgerResourceKind.AmmoBullet:
            case LedgerResourceKind.AmmoStorageItem:
            case LedgerResourceKind.BombStorageItem:
            case LedgerResourceKind.ReactorStorageItem:
            case LedgerResourceKind.WarpStorageItem:
                return HostResourceSubdivision.MechaEnergyAmmo;
            case LedgerResourceKind.Sand:
            case LedgerResourceKind.InventoryItem:
            case LedgerResourceKind.DeliveryItem:
            case LedgerResourceKind.ForgeItem:
            case LedgerResourceKind.FighterStorageItem:
            case LedgerResourceKind.DroneSlot:
                return HostResourceSubdivision.InventoryFleet;
            case LedgerResourceKind.BaseEnergy:
            case LedgerResourceKind.BaseStockItem:
                return HostResourceSubdivision.BaseFactory;
            default:
                return HostResourceSubdivision.Unknown;
        }
    }

    /// <summary>Which A10 subdivision a balance belongs to (owner-aware for drone quota).</summary>
    public static HostResourceSubdivision SubdivisionFor(LedgerResourceKey key)
    {
        if (!key.IsValid) return HostResourceSubdivision.Unknown;
        if (key.Kind == LedgerResourceKind.DroneSlot)
        {
            return key.Owner.Kind == LedgerOwnerKind.BattleBase
                ? HostResourceSubdivision.BaseFactory
                : HostResourceSubdivision.InventoryFleet;
        }
        return SubdivisionFor(key.Kind);
    }

    /// <summary>Short subdivision tag used in logs and tests ("A10-1", "A10-2", "A10-3").</summary>
    public static string SubdivisionTag(HostResourceSubdivision subdivision)
    {
        switch (subdivision)
        {
            case HostResourceSubdivision.MechaEnergyAmmo: return "A10-1";
            case HostResourceSubdivision.InventoryFleet: return "A10-2";
            case HostResourceSubdivision.BaseFactory: return "A10-3";
            default: return "A10-?";
        }
    }

    /// <summary>
    /// Whether a legacy packet must be refused instead of applied in a host authority world.
    /// </summary>
    /// <param name="packet">Which legacy packet arrived.</param>
    /// <param name="isHostAuthority">The live session's host-authority bit.</param>
    /// <returns>True when the packet must be dropped without touching world or ledger state.</returns>
    /// <remarks>
    /// Legacy rooms (<paramref name="isHostAuthority"/> false) never refuse: the old overwrite
    /// path runs exactly as before. Authority hosts refuse only the two client-to-host full
    /// overwrites. Host-to-client facts are never refused here — they are the host telling the
    /// display what it decided.
    /// </remarks>
    public static bool ShouldRefuseLegacyOverwrite(HostResourcePacketKind packet, bool isHostAuthority)
    {
        if (!isHostAuthority) return false;
        switch (packet)
        {
            case HostResourcePacketKind.MechaData:
            case HostResourcePacketKind.LifeSnapshot:
                return true;
            default:
                return false;
        }
    }

    /// <summary>Why a refusal happened, for logs. Callers branch on the bool, never on this text.</summary>
    public static string RefusalReason(HostResourcePacketKind packet)
    {
        switch (packet)
        {
            case HostResourcePacketKind.MechaData:
                return "client MechaData must not overwrite host ledger balances (A10 whitelist is deny-all)";
            case HostResourcePacketKind.LifeSnapshot:
                return "client life snapshot must not overwrite host ledger balances (A10 whitelist is deny-all)";
            default:
                return "no refusal";
        }
    }
}
