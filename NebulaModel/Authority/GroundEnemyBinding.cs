#region

using System;
using System.Collections.Generic;
using NebulaModel.Packets.Authority;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// The narrow view of a client's local enemy pools that the ground binding writes through.
/// </summary>
/// <remarks>
/// The binding's decisions (create, update, release, defer on a missing base core) are model logic
/// and run in plain tests; touching vanilla pools is the game adapter's job. Host pool indexes never
/// cross this seam as raw integers to be trusted — the <see cref="ObjectKey"/> carries identity and
/// generation, and the local slot is the host's <c>NativeId</c> installed at the same position
/// (DESIGN 4.1: "允许按主机槽位安装 enemy/entity 主池镜像"), with cursor/recycle maintained by the
/// game adapter. The client never allocates an authoritative slot on its own.
/// </remarks>
public interface IGroundEnemyPools
{
    /// <summary>True when the local enemy slot exists and is live.</summary>
    bool EnemyExists(int enemyId);

    /// <summary>Creates the display-only shell for a host slot (no logic components, no AI).</summary>
    void CreateEnemyShell(int enemyId, in GroundEnemyState state);

    /// <summary>Overwrites the shell's absolute pose/HP/kind from the host record.</summary>
    void WriteEnemyState(int enemyId, in GroundEnemyState state);

    /// <summary>Pure removal (no drops, statistics or experience). Never a Kill path.</summary>
    void RemoveEnemyShell(int enemyId);
}

/// <summary>
/// Rebuilds client-local ground enemies from host facts (TASKS.md A12).
/// </summary>
/// <remarks>
/// <para>
/// Keyed by <see cref="ObjectKey"/> — identity, not slot number. A recycled host slot arrives under a
/// new generation and gets a fresh shell; the old key's shell is released with its key, so a reused
/// slot never inherits the destroyed object's display ("目标死亡后立即复用原ID").
/// </para>
/// <para>
/// Base dependency: a record whose <c>BaseId != 0</c> and whose kind is not <c>GroundBase</c> is a
/// child of the base core that owns that component slot. The child is deferred while its base core is
/// unknown and retried when the base arrives — the parent-child inversion rule. A baseline that drops
/// the base core evicts the core; dependents the baseline also dropped are evicted with it, and a
/// dependent the baseline keeps without its base stays deferred (visible, never guessed).
/// </para>
/// </remarks>
public sealed class GroundEnemyBinding
{
    private sealed class LocalBinding
    {
        public GroundEnemyKind Kind;
        public int BaseId;
    }

    private readonly IGroundEnemyPools pools;
    private readonly int planetId;
    private readonly Action<ObjectKey, int> componentBindingRecorded;
    private readonly Dictionary<ObjectKey, LocalBinding> bindings = [];
    private readonly Dictionary<int, ObjectKey> slotOwner = [];
    private readonly HashSet<int> knownBases = [];
    private readonly Dictionary<ObjectKey, byte[]> deferred = [];

    private long statesApplied;
    private long statesRefused;
    private long statesDeferred;
    private long shellsCreated;
    private long shellsRemoved;
    private long membersRemoved;

    public GroundEnemyBinding(int planetId, IGroundEnemyPools pools,
        Action<ObjectKey, int> componentBindingRecorded = null)
    {
        if (planetId <= 0 || planetId > AuthorityScope.MaxPlanetId)
        {
            throw new ArgumentException("A ground enemy binding serves one planet scope.", nameof(planetId));
        }
        this.planetId = planetId;
        this.pools = pools ?? throw new ArgumentNullException(nameof(pools));
        this.componentBindingRecorded = componentBindingRecorded;
    }

    public int BoundCount => bindings.Count;
    public long StatesApplied => statesApplied;
    public long StatesRefused => statesRefused;
    public long StatesDeferred => statesDeferred;
    public long ShellsCreated => shellsCreated;
    public long ShellsRemoved => shellsRemoved;
    public long MembersRemoved => membersRemoved;
    public int DeferredCount => deferred.Count;

    public bool HasBinding(in ObjectKey key) => bindings.ContainsKey(key);

    public bool ApplyState(in ObjectKey key, byte[] state)
    {
        if (key.Kind != PoolKind.GroundEnemy || key.Scope != planetId)
        {
            statesRefused++;
            return false;
        }
        if (state == null || !GroundEnemyStateCodec.TryDecode(state, 0, state.Length, out var decoded, out _))
        {
            statesRefused++;
            return false;
        }
        return ApplyDecoded(key, in decoded, state);
    }

    private bool ApplyDecoded(in ObjectKey key, in GroundEnemyState decoded, byte[] raw)
    {
        if (NeedsBaseCore(in decoded) && !knownBases.Contains(decoded.BaseId))
        {
            deferred[key] = raw;
            statesDeferred++;
            return false;
        }
        deferred.Remove(key);

        if (!bindings.TryGetValue(key, out var binding))
        {
            binding = new LocalBinding { Kind = decoded.Kind, BaseId = decoded.BaseId };
            // A recycled slot arrives as a new key while the old key's despawn may still be in
            // flight (the host publishes Spawn → State → Despawn within one frame). Taking
            // ownership here is what keeps the old generation's trailing despawn from deleting the
            // new generation's shell.
            pools.CreateEnemyShell(key.NativeId, in decoded);
            bindings[key] = binding;
            slotOwner[key.NativeId] = key;
            shellsCreated++;
            componentBindingRecorded?.Invoke(key, key.NativeId);
        }
        else
        {
            binding.Kind = decoded.Kind;
            binding.BaseId = decoded.BaseId;
            slotOwner[key.NativeId] = key;
            pools.WriteEnemyState(key.NativeId, in decoded);
        }
        if (decoded.Kind == GroundEnemyKind.GroundBase && decoded.BaseId != 0)
        {
            knownBases.Add(decoded.BaseId);
            RetryDeferredForBase(decoded.BaseId);
        }
        statesApplied++;
        return true;
    }

    public void RemoveMember(in ObjectKey key)
    {
        deferred.Remove(key);
        if (!bindings.TryGetValue(key, out var binding))
        {
            return;
        }
        var wasBaseCore = binding.Kind == GroundEnemyKind.GroundBase && binding.BaseId != 0;
        bindings.Remove(key);
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
        if (wasBaseCore)
        {
            RecomputeKnownBases();
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
        if (deferred.Count > 0)
        {
            List<ObjectKey> retry = new(deferred.Keys);
            foreach (var key in retry)
            {
                if (!deferred.TryGetValue(key, out var raw)) continue;
                if (!GroundEnemyStateCodec.TryDecode(raw, 0, raw.Length, out var decoded, out _)) continue;
                if (!NeedsBaseCore(in decoded) || knownBases.Contains(decoded.BaseId))
                {
                    deferred.Remove(key);
                    ApplyDecoded(key, in decoded, raw);
                }
            }
        }
    }

    private static bool NeedsBaseCore(in GroundEnemyState state) =>
        state.Kind != GroundEnemyKind.GroundBase && state.Kind != GroundEnemyKind.GroundUnit && state.BaseId != 0;

    private void RecomputeKnownBases()
    {
        knownBases.Clear();
        foreach (var pair in bindings.Values)
        {
            if (pair.Kind == GroundEnemyKind.GroundBase && pair.BaseId != 0)
            {
                knownBases.Add(pair.BaseId);
            }
        }
    }

    private void RetryDeferredForBase(int baseId)
    {
        if (deferred.Count == 0) return;
        List<ObjectKey> retry = null;
        foreach (var pair in deferred)
        {
            if (!GroundEnemyStateCodec.TryDecode(pair.Value, 0, pair.Value.Length, out var decoded, out _)) continue;
            if (decoded.BaseId == baseId && decoded.Kind != GroundEnemyKind.GroundBase)
            {
                retry ??= new List<ObjectKey>();
                retry.Add(pair.Key);
            }
        }
        if (retry == null) return;
        foreach (var key in retry)
        {
            if (!deferred.TryGetValue(key, out var raw)) continue;
            if (!GroundEnemyStateCodec.TryDecode(raw, 0, raw.Length, out var decoded, out _)) continue;
            deferred.Remove(key);
            ApplyDecoded(key, in decoded, raw);
        }
    }
}
