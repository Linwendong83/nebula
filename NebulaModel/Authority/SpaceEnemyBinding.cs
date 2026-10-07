#region

using System;
using System.Collections.Generic;
using NebulaModel.Packets.Authority;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// The narrow view of a client's local sector enemy pool that the space binding writes through.
/// </summary>
/// <remarks>
/// The binding's decisions (create, update, release) are model logic and run in plain tests;
/// touching vanilla pools is the game adapter's job. Host pool indexes never cross this seam as raw
/// integers to be trusted — the <see cref="ObjectKey"/> carries identity and generation, and the
/// local slot is the host's <c>NativeId</c> installed at the same position (DESIGN 4.1), with
/// cursor/recycle maintained by the game adapter. The client never allocates an authoritative slot
/// on its own. Unlike the ground binding there is no base-core deferral: space children name their
/// hive by <c>OriginAstroId</c> (an attribute, not a parent slot), and a relay that lands becomes a
/// ground-base key in another pool rather than a reinterpretation of the space slot.
/// </remarks>
public interface ISpaceEnemyPools
{
    /// <summary>True when the local sector enemy slot exists and is live.</summary>
    bool EnemyExists(int enemyId);

    /// <summary>Creates the display-only shell for a host slot (no logic components, no AI).</summary>
    void CreateEnemyShell(int enemyId, in SpaceEnemyState state);

    /// <summary>Overwrites the shell's absolute pose/HP/kind from the host record.</summary>
    void WriteEnemyState(int enemyId, in SpaceEnemyState state);

    /// <summary>Pure removal (no drops, statistics or experience). Never a Kill path.</summary>
    void RemoveEnemyShell(int enemyId);
}

/// <summary>
/// Rebuilds client-local space enemies from host facts (TASKS.md A13).
/// </summary>
/// <remarks>
/// <para>
/// Keyed by <see cref="ObjectKey"/> — identity, not slot number. A recycled host slot arrives under a
/// new generation and gets a fresh shell; the old key's shell is released with its key, so a reused
/// slot never inherits the destroyed object's display. Cross-astro migration keeps the same key:
/// the sector scope is 0 and the astro ids are attributes, so a hive unit that moves stars updates
/// in place rather than respawning.
/// </para>
/// </remarks>
public sealed class SpaceEnemyBinding
{
    private readonly ISpaceEnemyPools pools;
    private readonly Dictionary<ObjectKey, SpaceEnemyKind> bindings = [];
    private readonly Dictionary<int, ObjectKey> slotOwner = [];

    private long statesApplied;
    private long statesRefused;
    private long shellsCreated;
    private long shellsRemoved;
    private long membersRemoved;

    public SpaceEnemyBinding(ISpaceEnemyPools pools)
    {
        this.pools = pools ?? throw new ArgumentNullException(nameof(pools));
    }

    public int BoundCount => bindings.Count;
    public long StatesApplied => statesApplied;
    public long StatesRefused => statesRefused;
    public long ShellsCreated => shellsCreated;
    public long ShellsRemoved => shellsRemoved;
    public long MembersRemoved => membersRemoved;

    public bool HasBinding(in ObjectKey key) => bindings.ContainsKey(key);

    public bool ApplyState(in ObjectKey key, byte[] state)
    {
        if (key.Kind != PoolKind.SpaceEnemy || key.Scope != AuthorityScope.Sector)
        {
            statesRefused++;
            return false;
        }
        if (state == null || !SpaceEnemyStateCodec.TryDecode(state, 0, state.Length, out var decoded, out _))
        {
            statesRefused++;
            return false;
        }
        return ApplyDecoded(key, in decoded);
    }

    private bool ApplyDecoded(in ObjectKey key, in SpaceEnemyState decoded)
    {
        if (!bindings.TryGetValue(key, out var kind))
        {
            // A recycled slot arrives as a new key while the old key's despawn may still be in
            // flight (the host publishes Spawn → State → Despawn within one frame). Taking
            // ownership here is what keeps the old generation's trailing despawn from deleting the
            // new generation's shell.
            pools.CreateEnemyShell(key.NativeId, in decoded);
            bindings[key] = decoded.Kind;
            slotOwner[key.NativeId] = key;
            shellsCreated++;
        }
        else
        {
            bindings[key] = decoded.Kind;
            slotOwner[key.NativeId] = key;
            pools.WriteEnemyState(key.NativeId, in decoded);
        }
        statesApplied++;
        return true;
    }

    public void RemoveMember(in ObjectKey key)
    {
        if (!bindings.Remove(key))
        {
            return;
        }
        // Only the slot's current owner may release the shell. A recycled slot's old generation
        // despawns after the new generation already took ownership (Spawn → State → Despawn order);
        // that trailing despawn must not delete the new shell.
        if (slotOwner.TryGetValue(key.NativeId, out var owner) && owner.Equals(key))
        {
            slotOwner.Remove(key.NativeId);
            if (pools.EnemyExists(key.NativeId))
            {
                pools.RemoveEnemyShell(key.NativeId);
                shellsRemoved++;
            }
        }
        membersRemoved++;
    }

    public void ReconcileBaseline(IReadOnlyList<SnapshotMemberRecord> members)
    {
        var keep = new HashSet<ObjectKey>();
        foreach (var member in members)
        {
            keep.Add(member.Key);
        }
        List<ObjectKey> doomed = null;
        foreach (var key in bindings.Keys)
        {
            if (!keep.Contains(key))
            {
                doomed ??= new List<ObjectKey>();
                doomed.Add(key);
            }
        }
        if (doomed != null)
        {
            foreach (var key in doomed)
            {
                RemoveMember(key);
            }
        }
        foreach (var member in members)
        {
            if (member.State != null)
            {
                ApplyState(member.Key, member.State);
            }
        }
    }
}
