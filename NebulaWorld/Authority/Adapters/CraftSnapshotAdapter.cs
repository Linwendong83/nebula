#region

using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaModel.Logger;
using NebulaModel.Packets.Authority;

#endregion

namespace NebulaWorld.Authority.Adapters;

/// <summary>
/// The host's canonical view of player and base craft (TASKS.md A13).
/// </summary>
/// <remarks>
/// <para>
/// Covers both <see cref="PoolKind.GroundCraft"/> (one planet scope per factory) and
/// <see cref="PoolKind.SpaceCraft"/> (sector scope 0, one pool shared by the whole sector). It reads
/// the craft pools and the global skill pool (for HP), and encodes each craft as a
/// <see cref="CraftState"/>. Logic components (fleet/unit/drone/vehicle) stay host-only: the client
/// shell carries none of them, so vanilla fleet, targeting, ammo and destruction ticks skip it by
/// construction.
/// </para>
/// <para>
/// Reading never mutates: no combat-stat removal, no fleet execution, no destruction. A scope whose
/// factory (ground) or sector (space) is not loaded returns <c>false</c> (skip this scope this
/// frame), never an empty scope. Slot generations are minted per scope by
/// <see cref="SlotGenerationTracker"/>, so a destroyed and recycled slot is a new
/// <see cref="ObjectKey"/>, never a resurrection. Two players' craft never share a slot identity;
/// the owner is an attribute, not a second index.
/// </para>
/// </remarks>
public sealed class CraftSnapshotAdapter : IHostWorldView
{
    private readonly AuthorityEpoch epoch;
    private readonly Dictionary<int, SlotGenerationTracker> groundTrackers = [];
    private readonly SlotGenerationTracker spaceTracker = new();

    public long UnreadableScopes { get; private set; }
    public long CorruptReferences { get; private set; }
    public long UnknownKinds { get; private set; }

    public CraftSnapshotAdapter(AuthorityEpoch epoch)
    {
        if (!epoch.IsValid) throw new System.ArgumentException("The adapter needs a valid world epoch.", nameof(epoch));
        this.epoch = epoch;
    }

    public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
    {
        if (scope.Kind == PoolKind.GroundCraft)
        {
            return TryReadGroundMembers(scope.Scope, members);
        }
        if (scope.Kind == PoolKind.SpaceCraft && scope.Scope == AuthorityScope.Sector)
        {
            return TryReadSpaceMembers(members);
        }
        return false;
    }

    /// <summary>The authority identity of a living planet-craft slot, as the replication scan would mint it (A22).</summary>
    public bool TryGetGroundCraftKey(int planetId, int craftId, out ObjectKey key)
    {
        key = default;
        var factory = GameMain.galaxy?.PlanetById(planetId)?.factory;
        if (factory == null || craftId <= 0 || craftId >= factory.craftCursor ||
            craftId >= factory.craftPool.Length ||
            factory.craftPool[craftId].id != craftId ||
            factory.craftPool[craftId].prototype == ECraftProto.ConstructionDrone)
        {
            return false;
        }
        var generation = TrackerFor(planetId).ObserveOccupied(craftId);
        key = ObjectKey.Create(epoch, PoolKind.GroundCraft, planetId, craftId, generation);
        return true;
    }

    /// <summary>The authority identity of a living sector-craft slot, as the replication scan would mint it (A22).</summary>
    public bool TryGetSpaceCraftKey(int craftId, out ObjectKey key)
    {
        key = default;
        var sector = GameMain.spaceSector;
        if (sector == null || craftId <= 0 || craftId >= sector.craftCursor ||
            craftId >= sector.craftPool.Length ||
            sector.craftPool[craftId].id != craftId)
        {
            return false;
        }
        var generation = spaceTracker.ObserveOccupied(craftId);
        key = ObjectKey.Create(epoch, PoolKind.SpaceCraft, AuthorityScope.Sector, craftId, generation);
        return true;
    }

    public bool TryReadState(ObjectKey key, out byte[] state)
    {
        state = null;
        if (key.Kind == PoolKind.GroundCraft)
        {
            return TryReadGroundState(key, out state);
        }
        if (key.Kind == PoolKind.SpaceCraft && key.Scope == AuthorityScope.Sector)
        {
            return TryReadSpaceState(key, out state);
        }
        return false;
    }

    private bool TryReadGroundMembers(int planetId, List<ObjectKey> members)
    {
        var factory = GameMain.galaxy?.PlanetById(planetId)?.factory;
        if (factory == null)
        {
            UnreadableScopes++;
            return false;
        }
        var tracker = TrackerFor(planetId);
        var pool = factory.craftPool;
        for (var id = 1; id < factory.craftCursor; id++)
        {
            // Native construction owns these crafts, including client-local drone logic ids.
            // Replacing them with display-only authority shells would disconnect active drones.
            if (id >= pool.Length || pool[id].id != id ||
                pool[id].prototype == ECraftProto.ConstructionDrone) continue;
            var generation = tracker.ObserveOccupied(id);
            members.Add(ObjectKey.Create(epoch, PoolKind.GroundCraft, planetId, id, generation));
        }
        tracker.EndScan();
        return true;
    }

    private bool TryReadSpaceMembers(List<ObjectKey> members)
    {
        var sector = GameMain.spaceSector;
        if (sector == null)
        {
            UnreadableScopes++;
            return false;
        }
        var pool = sector.craftPool;
        for (var id = 1; id < sector.craftCursor; id++)
        {
            if (id >= pool.Length || pool[id].id != id) continue;
            var generation = spaceTracker.ObserveOccupied(id);
            members.Add(ObjectKey.Create(epoch, PoolKind.SpaceCraft, AuthorityScope.Sector, id, generation));
        }
        spaceTracker.EndScan();
        return true;
    }

    private bool TryReadGroundState(ObjectKey key, out byte[] state)
    {
        state = null;
        var factory = GameMain.galaxy?.PlanetById(key.Scope)?.factory;
        if (factory == null || key.NativeId >= factory.craftCursor ||
            key.NativeId >= factory.craftPool.Length ||
            factory.craftPool[key.NativeId].id != key.NativeId)
        {
            return false;
        }
        if (!TryReadCraftState(in factory.craftPool[key.NativeId], false, out var decoded))
        {
            return false;
        }
        if (!CraftStateCodec.TryEncode(decoded, out state))
        {
            CorruptReferences++;
            return false;
        }
        return true;
    }

    private bool TryReadSpaceState(ObjectKey key, out byte[] state)
    {
        state = null;
        var sector = GameMain.spaceSector;
        if (sector == null || key.NativeId >= sector.craftCursor ||
            key.NativeId >= sector.craftPool.Length ||
            sector.craftPool[key.NativeId].id != key.NativeId)
        {
            return false;
        }
        if (!TryReadCraftState(in sector.craftPool[key.NativeId], true, out var decoded))
        {
            return false;
        }
        if (!CraftStateCodec.TryEncode(decoded, out state))
        {
            CorruptReferences++;
            return false;
        }
        return true;
    }

    private bool TryReadCraftState(in CraftData craft, bool wantSpace, out CraftState state)
    {
        state = default;
        if (craft.isSpace != wantSpace)
        {
            UnknownKinds++;
            return false;
        }
        var hasCombatStat = false;
        int hp = 0, hpMax = 0, hpRecover = 0, hpIncoming = 0;
        var combatStatId = craft.combatStatId;
        if (combatStatId > 0)
        {
            var stats = GameMain.data.spaceSector.skillSystem.combatStats;
            if (combatStatId < stats.cursor && stats.buffer[combatStatId].id == combatStatId &&
                stats.buffer[combatStatId].objectType == (int)EObjectType.Craft &&
                stats.buffer[combatStatId].objectId == craft.id)
            {
                ref var stat = ref stats.buffer[combatStatId];
                hasCombatStat = true;
                hp = stat.hp;
                hpMax = stat.hpMax;
                hpRecover = stat.hpRecover;
                hpIncoming = stat.hpIncoming;
            }
            else
            {
                CorruptReferences++;
                Log.Warn($"[authority] craft {craft.id} has a dangling combatStatId {combatStatId}");
            }
        }
        state = new CraftState(hasCombatStat, craft.dynamic, craft.isSpace,
            craft.protoId, craft.modelIndex, craft.port, (byte)craft.prototype, craft.stateFlags,
            craft.astroId, craft.owner, craft.fleetId,
            craft.pos.x, craft.pos.y, craft.pos.z,
            craft.rot.x, craft.rot.y, craft.rot.z, craft.rot.w,
            craft.dynamic ? craft.vel.x : 0f, craft.dynamic ? craft.vel.y : 0f, craft.dynamic ? craft.vel.z : 0f,
            hp, hpMax, hpRecover, hpIncoming);
        return true;
    }

    private SlotGenerationTracker TrackerFor(int planetId)
    {
        if (!groundTrackers.TryGetValue(planetId, out var tracker))
        {
            tracker = new SlotGenerationTracker();
            groundTrackers.Add(planetId, tracker);
        }
        return tracker;
    }
}
