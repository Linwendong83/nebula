#region

using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaModel.Logger;
using NebulaModel.Packets.Authority;

#endregion

namespace NebulaWorld.Authority.Adapters;

/// <summary>
/// The host's canonical view of one planet's building combat state (TASKS.md A08).
/// </summary>
/// <remarks>
/// <para>
/// This is the first production <see cref="IHostWorldView"/>: the factory combat domain, covering
/// the <see cref="PoolKind.Entity"/> pool of each loaded factory. It reads exactly two vanilla
/// sources — the entity's combat-stat reference resolved against the global skill pool, and its
/// construct-stat reference resolved against the factory's own construction system — and encodes
/// them as a <see cref="FactoryCombatState"/> per entity.
/// </para>
/// <para>
/// Reading never mutates. The legacy path healed every building when someone requested the factory
/// (E06); this adapter is what replaces that, so the reads below must not call
/// <c>HandleFullHp</c>, remove stats or touch any writer — a snapshot that treats is the same bug
/// it exists to remove, just laundered through the replicator.
/// </para>
/// <para>
/// Unreadable is not empty. A planet whose factory is not loaded returns
/// <c>TryReadMembers = false</c>, which the replicator treats as "skip this scope this frame" —
/// never as "publish a wave of deaths". Slot generations are minted per scope by
/// <see cref="SlotGenerationTracker"/>, so a dismantled and rebuilt entity in the same slot is a
/// new <see cref="ObjectKey"/>, never a resurrection of the old one.
/// </para>
/// </remarks>
public sealed class FactoryCombatSnapshotAdapter : IHostWorldView
{
    private readonly AuthorityEpoch epoch;
    private readonly Dictionary<int, SlotGenerationTracker> trackers = [];
    private static readonly byte[] undamagedState = EncodeUndamagedState();

    /// <summary>Scopes skipped because their factory was not readable. Persistent growth means a subscriber is watching a planet the host never loads.</summary>
    public long UnreadableScopes { get; private set; }

    /// <summary>
    /// Entity references that pointed at a stat which is missing or belongs to another object.
    /// A vanilla invariant violation on the host; published as "no stat" and counted, never guessed around.
    /// </summary>
    public long CorruptReferences { get; private set; }

    public FactoryCombatSnapshotAdapter(AuthorityEpoch epoch)
    {
        if (!epoch.IsValid) throw new System.ArgumentException("The adapter needs a valid world epoch.", nameof(epoch));
        this.epoch = epoch;
    }

    /// <summary>The member keys of one planet's entity pool, each with its slot generation.</summary>
    public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
    {
        if (scope.Kind != PoolKind.Entity)
        {
            // A08 covers the building pool only; other pools are later cards' adapters.
            return false;
        }
        var factory = FactoryFor(scope.Scope);
        if (factory == null)
        {
            UnreadableScopes++;
            return false;
        }

        var tracker = TrackerFor(scope.Scope);
        var pool = factory.entityPool;
        for (var id = 1; id < factory.entityCursor; id++)
        {
            if (pool[id].id != id) continue;
            var generation = tracker.ObserveOccupied(id);
            members.Add(ObjectKey.Create(epoch, PoolKind.Entity, scope.Scope, id, generation));
        }
        tracker.EndScan();
        return true;
    }

    /// <summary>The canonical combat/construct state of one entity, encoded for the wire.</summary>
    public bool TryReadState(ObjectKey key, out byte[] state)
    {
        state = null;
        if (key.Kind != PoolKind.Entity) return false;
        var factory = FactoryFor(key.Scope);
        if (factory == null || key.NativeId >= factory.entityCursor ||
            factory.entityPool[key.NativeId].id != key.NativeId)
        {
            return false;
        }
        var entityState = ReadEntityState(factory, key.NativeId);
        if (!entityState.HasCombatStat && !entityState.HasConstructStat)
        {
            // Most buildings have no damage records. Their immutable wire state is identical;
            // reuse it instead of allocating a writer and primitive buffers per building per tick.
            state = undamagedState;
            return true;
        }
        if (!FactoryCombatStateCodec.TryEncode(entityState, out state))
        {
            // The codec's fixed layout cannot exceed its own ceiling; refusal here is a defect.
            throw new System.InvalidOperationException("A factory combat state exceeded its own codec.");
        }
        return true;
    }

    private static byte[] EncodeUndamagedState()
    {
        FactoryCombatStateCodec.TryEncode(default, out var data);
        return data;
    }

    /// <summary>
    /// Reads one entity's combat and construct facts through their vanilla references, validating
    /// each reference before trusting what it points at.
    /// </summary>
    private FactoryCombatState ReadEntityState(PlanetFactory factory, int entityId)
    {
        ref var entity = ref factory.entityPool[entityId];
        var state = new FactoryCombatState();

        var combatStatId = entity.combatStatId;
        if (combatStatId > 0)
        {
            var stats = GameMain.data.spaceSector.skillSystem.combatStats;
            if (combatStatId < stats.cursor && stats.buffer[combatStatId].id == combatStatId &&
                stats.buffer[combatStatId].objectType == (int)EObjectType.Entity &&
                stats.buffer[combatStatId].objectId == entityId &&
                stats.buffer[combatStatId].astroId == factory.planet.astroId)
            {
                ref var stat = ref stats.buffer[combatStatId];
                state = new FactoryCombatState(true, stat.hp, stat.hpMax, stat.hpRecover, stat.hpIncoming,
                    state.HasConstructStat, state.DamageRate, state.RepairerCount, state.RepairerModuleId,
                    state.RepairerValue);
            }
            else
            {
                CorruptReferences++;
                Log.Warn($"[authority] entity {entityId} on {factory.planet.name} has a dangling combatStatId {combatStatId}");
            }
        }

        var constructStatId = entity.constructStatId;
        if (constructStatId > 0)
        {
            var constructs = factory.constructionSystem.constructStats;
            if (constructStatId < constructs.cursor && constructs.buffer[constructStatId].id == constructStatId &&
                constructs.buffer[constructStatId].entityId == entityId)
            {
                ref var construct = ref constructs.buffer[constructStatId];
                state = new FactoryCombatState(state.HasCombatStat, state.Hp, state.HpMax, state.HpRecover,
                    state.HpIncoming, true, construct.damageRate, construct.repairerCount, construct.repairerModuleId,
                    construct.repairerValue);
            }
            else
            {
                CorruptReferences++;
                Log.Warn($"[authority] entity {entityId} on {factory.planet.name} has a dangling constructStatId {constructStatId}");
            }
        }

        return state;
    }

    /// <summary>
    /// The authority identity of a living entity slot, as the replication scan would mint it (A22).
    /// </summary>
    /// <remarks>
    /// The death capture calls this inside the vanilla death commit, where the slot is provably
    /// still occupied. Observing here keeps the tracker's generation in step with the scan: a dying
    /// object takes the identity clients were given, and the slot's next occupant gets the next
    /// generation, never the dead one's.
    /// </remarks>
    public bool TryGetEntityKey(int planetId, int entityId, out ObjectKey key)
    {
        key = default;
        var factory = FactoryFor(planetId);
        if (factory == null || entityId <= 0 || entityId >= factory.entityCursor ||
            factory.entityPool[entityId].id != entityId)
        {
            return false;
        }
        var generation = TrackerFor(planetId).ObserveOccupied(entityId);
        key = ObjectKey.Create(epoch, PoolKind.Entity, planetId, entityId, generation);
        return true;
    }

    private static PlanetFactory FactoryFor(int planetId)
    {
        var planet = GameMain.galaxy?.PlanetById(planetId);
        // A planet whose factory is not realized is unreadable, not empty: treating it as an empty
        // scope would publish every building's death.
        return planet?.factory;
    }

    private SlotGenerationTracker TrackerFor(int planetId)
    {
        if (!trackers.TryGetValue(planetId, out var tracker))
        {
            tracker = new SlotGenerationTracker();
            trackers.Add(planetId, tracker);
        }
        return tracker;
    }
}
