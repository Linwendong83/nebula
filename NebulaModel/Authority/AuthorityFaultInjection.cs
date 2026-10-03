#region

using System;
using System.Collections.Generic;
using NebulaModel.Packets.Authority;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// Deterministic S2C fault rules for the replication link (VALIDATION §4).
/// </summary>
/// <remarks>
/// <para>
/// The injection layer is the application/baseline boundary, not the WebSocket transport: the
/// native network stays reliable and ordered, and every fault here is one the replica protocol must
/// recover from anyway — delay, duplication, logical reordering, an application pause, explicit
/// message loss, and baseline corruption. There is no randomness anywhere: a fault happens exactly
/// when a rule names it, so a failing scenario is reproducible by construction.
/// </para>
/// <para>
/// Drop rules are armed-once: the link consumes the flag on the first matching packet. Snapshot
/// corruption is armed-once per conversation the same way, so a later clean baseline passes
/// untouched. The link mutates the rules object it was given; the harness owns that object and
/// re-arms or disarms it between phases of a scenario.
/// </para>
/// </remarks>
public sealed class FaultInjectionRules
{
    /// <summary>Pumps a packet waits before it is deliverable. Zero delivers at the next pump.</summary>
    public int DelayPumps { get; set; }

    /// <summary>Extra identical copies forwarded after the first (N3 duplication).</summary>
    public int ExtraCopies { get; set; }

    /// <summary>Adjacent inversions applied to one pump's release batch (N3 logical reorder).</summary>
    /// <remarks>Reversing the first <c>depth+1</c> packets of a batch guarantees at least one
    /// stream-sequence inversion, which the replica must see as a gap — never as noise.</remarks>
    public int ReorderDepth { get; set; }

    /// <summary>Holds every packet while true; release keeps send order (N6 pause).</summary>
    public bool Paused { get; set; }

    /// <summary>Confines the drop rules to one scope; null applies them to every scope (N4).</summary>
    public ScopeKey? DropOnlyScope { get; set; }

    /// <summary>Drops the next lifecycle batch (spawn or despawn) — one identity event is lost.</summary>
    public bool DropNextLifecycle { get; set; }

    /// <summary>Drops the next absolute state batch — one HP/task fact is lost.</summary>
    public bool DropNextWorldState { get; set; }

    /// <summary>Drops the next digest — verification waits for the next interval.</summary>
    public bool DropNextDigest { get; set; }

    /// <summary>Delays one extra copy of the next delivered packet by <see cref="DelayedDuplicatePumps"/>
    /// pumps (N3's "旧 generation 尾包延迟"). Armed-once; the requeued copy never re-duplicates.</summary>
    public bool DelayDuplicateNextPacket { get; set; }

    /// <summary>How long <see cref="DelayDuplicateNextPacket"/>'s extra copy travels behind.</summary>
    public int DelayedDuplicatePumps { get; set; }

    /// <summary>Zero-based index of the chunk to lose (N5); negative disables.</summary>
    public int DropChunkIndex { get; set; } = -1;

    /// <summary>Zero-based index of the chunk to deliver twice (N5); negative disables.</summary>
    public int DuplicateChunkIndex { get; set; } = -1;

    /// <summary>Shortens the next chunk by one byte so reassembly cannot match Begin's declaration (N5).</summary>
    public bool TruncateChunk { get; set; }

    /// <summary>Flips one byte of the next chunk so the image no longer hashes to Commit's value (N5).</summary>
    public bool CorruptChunkBytes { get; set; }

    /// <summary>Flips the next Commit's declared hash so it contradicts Begin and the image (N5).</summary>
    public bool CorruptCommitHash { get; set; }
}

/// <summary>
/// A faulty S2C link: the replicator hands packets here, and <see cref="Pump"/> releases them to
/// the client under the armed rules.
/// </summary>
/// <remarks>
/// <para>
/// The link is an <see cref="IReplicationSink"/>, so it sits between <see cref="HostWorldReplicator"/>
/// and the client's inbox without either side knowing it exists. Delivery is pump-driven and
/// deterministic: <see cref="Pump"/> advances one delivery cycle and forwards everything whose delay
/// elapsed, in send order unless a reorder rule says otherwise. <see cref="Flush"/> drains everything
/// held — paused, delayed or both — strictly in send order, which is what "the application pause
/// ended" means in a test.
/// </para>
/// <para>
/// The link never inspects payload bytes: drops and reorders decide on the packet family, and the
/// snapshot faults mutate only the envelope bookkeeping (chunk length, chunk bytes, commit hash) the
/// staging area already has to survive. Nothing here is reachable from a live session — the runtime
/// wires the replicator straight to the network sink; this type exists for the fault harness.
/// </para>
/// </remarks>
public sealed class FaultyAuthorityLink : IReplicationSink
{
    private sealed class PendingSend
    {
        public ushort Subscriber;
        public AuthorityEnvelopePacket Packet;
        public long DuePump;
        public bool SnapshotFaultsApplied;
        public int LocalExtraCopies;
    }

    private readonly FaultInjectionRules rules;
    private readonly IReplicationSink downstream;
    private readonly List<PendingSend> pending = [];
    private readonly List<PendingSend> dueScratch = [];
    private long pumps;

    /// <param name="rules">The armed rules. The link consumes armed-once flags on use.</param>
    /// <param name="downstream">Where released packets are delivered — the client's inbox applier.</param>
    public FaultyAuthorityLink(FaultInjectionRules rules, IReplicationSink downstream)
    {
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        this.downstream = downstream ?? throw new ArgumentNullException(nameof(downstream));
    }

    /// <summary>Packets accepted from the host.</summary>
    public long SentTotal { get; private set; }

    /// <summary>First-copy deliveries that reached the downstream sink.</summary>
    public long DeliveredTotal { get; private set; }

    /// <summary>Extra identical copies delivered on top of the first.</summary>
    public long DuplicateDeliveries { get; private set; }

    /// <summary>Packets the armed drop rules consumed.</summary>
    public long DroppedTotal { get; private set; }

    /// <summary>Extra copies requeued to arrive later (N3's delayed old-generation tail).</summary>
    public long RequeuedDuplicates { get; private set; }

    /// <summary>Packets currently held by the pause or an unelapsed delay.</summary>
    public int HeldCount => pending.Count;

    /// <param name="subscriberId">Subscriber the host addressed.</param>
    /// <param name="packet">The packet the host produced.</param>
    public void Send(ushort subscriberId, AuthorityEnvelopePacket packet)
    {
        SentTotal++;
        pending.Add(new PendingSend
        {
            Subscriber = subscriberId,
            Packet = packet,
            DuePump = rules.Paused ? -1L : pumps + Math.Max(0, rules.DelayPumps)
        });
    }

    /// <summary>Advances one delivery cycle and releases everything due.</summary>
    public void Pump()
    {
        pumps++;
        if (rules.Paused) return;
        ReleaseDue();
    }

    /// <summary>
    /// Drains every held packet in strict send order, ignoring delay and reorder. This is the
    /// deterministic form of "the pause ended": the backlog is delivered whole and in order, which
    /// is exactly what a reliable transport guarantees once it stops being starved.
    /// </summary>
    public void Flush()
    {
        foreach (var entry in pending)
        {
            entry.DuePump = pumps;
        }
        ReleaseDue();
    }

    private void ReleaseDue()
    {
        // Packets sent while paused carry no due pump; once the pause is over they are due.
        foreach (var entry in pending)
        {
            if (entry.DuePump < 0) entry.DuePump = pumps;
        }

        dueScratch.Clear();
        for (var i = pending.Count - 1; i >= 0; i--)
        {
            var entry = pending[i];
            if (entry.DuePump <= pumps)
            {
                dueScratch.Add(entry);
                pending.RemoveAt(i);
            }
        }
        dueScratch.Reverse(); // back to send order

        if (rules.ReorderDepth > 0 && dueScratch.Count >= 2)
        {
            var span = Math.Min(rules.ReorderDepth + 1, dueScratch.Count);
            dueScratch.Reverse(0, span);
        }

        foreach (var entry in dueScratch)
        {
            Deliver(entry);
        }
    }

    private void Deliver(PendingSend entry)
    {
        if (IsDropped(entry.Packet))
        {
            DroppedTotal++;
            return;
        }
        ApplySnapshotFaults(entry);
        if (entry.Packet == null)
        {
            DroppedTotal++;
            return;
        }

        downstream.Send(entry.Subscriber, entry.Packet);
        DeliveredTotal++;
        var copies = rules.ExtraCopies + entry.LocalExtraCopies;
        for (var i = 0; i < copies; i++)
        {
            downstream.Send(entry.Subscriber, entry.Packet);
            DuplicateDeliveries++;
        }

        if (rules.DelayDuplicateNextPacket)
        {
            rules.DelayDuplicateNextPacket = false;
            pending.Add(new PendingSend
            {
                Subscriber = entry.Subscriber,
                Packet = entry.Packet,
                DuePump = pumps + Math.Max(1, rules.DelayedDuplicatePumps),
                SnapshotFaultsApplied = true
            });
            RequeuedDuplicates++;
        }
    }

    /// <summary>Decides and consumes a drop rule. The scope filter spares every other scope.</summary>
    private bool IsDropped(AuthorityEnvelopePacket packet)
    {
        if (rules.DropOnlyScope.HasValue && !ScopeOf(packet).Equals(rules.DropOnlyScope.Value)) return false;
        if (rules.DropNextLifecycle && packet is AuthorityLifecyclePacket)
        {
            rules.DropNextLifecycle = false;
            return true;
        }
        if (rules.DropNextWorldState && packet is AuthorityWorldStatePacket)
        {
            rules.DropNextWorldState = false;
            return true;
        }
        if (rules.DropNextDigest && packet is AuthorityScopeDigestPacket)
        {
            rules.DropNextDigest = false;
            return true;
        }
        return false;
    }

    /// <summary>Applies the armed-once snapshot corruption to one packet's first forward.</summary>
    private void ApplySnapshotFaults(PendingSend entry)
    {
        if (entry.SnapshotFaultsApplied) return;

        if (entry.Packet is AuthoritySnapshotChunkPacket chunk)
        {
            if (rules.DropChunkIndex >= 0 && chunk.ChunkIndex == rules.DropChunkIndex)
            {
                rules.DropChunkIndex = -1;
                entry.SnapshotFaultsApplied = true;
                entry.Packet = null;
                return;
            }
            if (rules.DuplicateChunkIndex >= 0 && chunk.ChunkIndex == rules.DuplicateChunkIndex)
            {
                rules.DuplicateChunkIndex = -1;
                entry.LocalExtraCopies = 1;
            }
            if (rules.TruncateChunk && chunk.Data is { Length: > 0 })
            {
                rules.TruncateChunk = false;
                var shortened = new byte[chunk.Data.Length - 1];
                Buffer.BlockCopy(chunk.Data, 0, shortened, 0, shortened.Length);
                chunk.Data = shortened;
            }
            if (rules.CorruptChunkBytes && chunk.Data is { Length: > 0 })
            {
                rules.CorruptChunkBytes = false;
                chunk.Data[chunk.Data.Length - 1] ^= 0xFF;
            }
        }
        else if (entry.Packet is AuthoritySnapshotCommitPacket commit && rules.CorruptCommitHash)
        {
            rules.CorruptCommitHash = false;
            commit.SnapshotHash ^= 0x9E3779B97F4A7C15;
        }

        entry.SnapshotFaultsApplied = true;
    }

    private static ScopeKey ScopeOf(AuthorityEnvelopePacket packet) =>
        packet switch
        {
            AuthorityLifecyclePacket lifecycle => lifecycle.TryGetScopeKey(out var scope) ? scope : default,
            AuthorityWorldStatePacket worldState => worldState.TryGetScopeKey(out var state) ? state : default,
            AuthorityScopeDigestPacket digest => digest.TryGetScopeKey(out var digestScope) ? digestScope : default,
            _ => default
        };
}
