using System;
using System.Collections.Generic;
using NebulaModel.Logger;
using NebulaModel.Packets.Combat;
using NebulaModel.Utils;
using NebulaWorld.GameStates;
using UnityEngine;

namespace NebulaWorld.Combat;

public partial class EnemyManager
{
    private const long StateRetryTicks = 120;
    private readonly object stateGate = new();
    private readonly Dictionary<(int AstroId, int EnemyId), PendingEnemyState> pendingEnemyStates = new();
    private readonly Queue<CombatEnemyStateResponsePacket> receivedEnemyStates = new();
    private readonly Dictionary<(int AstroId, int EnemyId), long> authoritativeRemovals = new();
    private long nextEnemyStateRequestId;
    private static bool automaticCombatReconnectAttempted;
    private static string automaticReconnectWorldId;

    private sealed class PendingEnemyState
    {
        public long Generation;
        public long RequestId;
        public long NextSendTick;
        public bool IncludeSnapshot;
        public int StarId;
        public bool Warned;
        public int SnapshotFailures;
    }

    public void RequestAuthoritativeState(int astroId, int enemyId)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsClient || enemyId <= 0) return;
        var scope = NormalizeEnemyScope(astroId);
        var generation = Multiplayer.Session.Generations.Peek(scope, enemyId);
        lock (stateGate)
        {
            var key = (scope, enemyId);
            if (pendingEnemyStates.TryGetValue(key, out var pending) && pending.Generation == generation)
                return;
            pendingEnemyStates[key] = new PendingEnemyState
            {
                Generation = generation,
                RequestId = ++nextEnemyStateRequestId,
                NextSendTick = 0,
                StarId = GameMain.localStar?.id ?? -1
            };
        }
    }

    public void ReceiveAuthoritativeState(CombatEnemyStateResponsePacket packet)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsClient) return;
        var copy = new CombatEnemyStateResponsePacket
        {
            AstroId = packet.AstroId,
            EnemyId = packet.EnemyId,
            ExpectedGeneration = packet.ExpectedGeneration,
            RequestId = packet.RequestId,
            Generation = packet.Generation,
            Alive = packet.Alive,
            HasCombatStat = packet.HasCombatStat,
            OriginAstroId = packet.OriginAstroId,
            ProtoId = packet.ProtoId,
            ModelIndex = packet.ModelIndex,
            Owner = packet.Owner,
            Port = packet.Port,
            Dynamic = packet.Dynamic,
            Hp = packet.Hp,
            HpMax = packet.HpMax,
            HpRecover = packet.HpRecover,
            HpIncoming = packet.HpIncoming,
            Snapshot = packet.Snapshot == null ? Array.Empty<byte>() : (byte[])packet.Snapshot.Clone()
        };
        lock (stateGate) receivedEnemyStates.Enqueue(copy);
    }

    public void RecordAuthoritativeRemoval(int astroId, int enemyId, long generation)
    {
        if (enemyId <= 0 || generation <= 0) return;
        var key = (NormalizeEnemyScope(astroId), enemyId);
        lock (stateGate)
        {
            if (!authoritativeRemovals.TryGetValue(key, out var previous) || generation > previous)
                authoritativeRemovals[key] = generation;
        }
    }

    public void TickAuthoritativeState()
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsClient) return;

        var responses = new List<CombatEnemyStateResponsePacket>();
        lock (stateGate)
        {
            while (receivedEnemyStates.Count > 0) responses.Add(receivedEnemyStates.Dequeue());
        }
        foreach (var response in responses) ApplyAuthoritativeState(response);

        var requests = new List<CombatEnemyStateRequestPacket>();
        var tick = GameMain.gameTick;
        lock (stateGate)
        {
            var stale = new List<(int AstroId, int EnemyId)>();
            foreach (var pair in pendingEnemyStates)
            {
                if (!IsEnemyScopeLoaded(pair.Key.AstroId) ||
                    pair.Value.StarId != (GameMain.localStar?.id ?? -1))
                {
                    stale.Add(pair.Key);
                    continue;
                }
                var currentGeneration = Multiplayer.Session.Generations.Peek(pair.Key.AstroId, pair.Key.EnemyId);
                if (currentGeneration != pair.Value.Generation)
                {
                    pair.Value.Generation = currentGeneration;
                    pair.Value.RequestId = ++nextEnemyStateRequestId;
                    pair.Value.IncludeSnapshot = true;
                    pair.Value.NextSendTick = tick + 30; // let ordinary spawn packets arrive first
                    continue;
                }
                if (tick < pair.Value.NextSendTick) continue;
                pair.Value.NextSendTick = tick + StateRetryTicks;
                requests.Add(new CombatEnemyStateRequestPacket
                {
                    AstroId = pair.Key.AstroId,
                    EnemyId = pair.Key.EnemyId,
                    ExpectedGeneration = pair.Value.Generation,
                    RequestId = pair.Value.RequestId,
                    IncludeSnapshot = pair.Value.IncludeSnapshot
                });
            }
            foreach (var key in stale) pendingEnemyStates.Remove(key);
        }
        foreach (var request in requests) Multiplayer.Session.Client.SendPacket(request);
    }

    private void ApplyAuthoritativeState(CombatEnemyStateResponsePacket packet)
    {
        var scope = NormalizeEnemyScope(packet.AstroId);
        if (packet.EnemyId <= 0 || !IsEnemyScopeLoaded(scope)) return;
        var key = (scope, packet.EnemyId);
        var localGeneration = Multiplayer.Session.Generations.Peek(scope, packet.EnemyId);
        if (localGeneration != packet.ExpectedGeneration) return;
        if (packet.RequestId != 0)
        {
            lock (stateGate)
            {
                if (!pendingEnemyStates.TryGetValue(key, out var pending) ||
                    pending.RequestId != packet.RequestId || pending.Generation != packet.ExpectedGeneration)
                    return;
            }
        }
        if (packet.Alive)
        {
            lock (stateGate)
            {
                if (authoritativeRemovals.TryGetValue(key, out var removedGeneration) &&
                    packet.Generation <= removedGeneration)
                {
                    CompleteEnemyStateRequest(key, packet.RequestId);
                    return;
                }
            }
        }
        if (!packet.Alive)
        {
            RecordAuthoritativeRemoval(scope, packet.EnemyId, packet.Generation);
            ApplyAuthoritativeDeath(scope, packet.EnemyId);
            Multiplayer.Session.Generations.ReconcileFromHost(scope, packet.EnemyId,
                localGeneration, packet.Generation);
            CompleteEnemyStateRequest(key, packet.RequestId);
            return;
        }

        var identityMatches = EnemyIdentityMatches(scope, packet);
        if (packet.Generation != localGeneration || !identityMatches)
        {
            // A recycled ID or missing local instance cannot be repaired by
            // changing only health or generation.
            if (packet.Snapshot == null || packet.Snapshot.Length == 0)
            {
                var snapshotWasRequested = false;
                lock (stateGate)
                {
                    if (pendingEnemyStates.TryGetValue(key, out var existing) &&
                        existing.IncludeSnapshot && existing.RequestId == packet.RequestId)
                    {
                        snapshotWasRequested = true;
                    }
                    else
                    {
                        pendingEnemyStates[key] = new PendingEnemyState
                        {
                            Generation = localGeneration,
                            RequestId = ++nextEnemyStateRequestId,
                            NextSendTick = 0,
                            IncludeSnapshot = true,
                            StarId = GameMain.localStar?.id ?? -1
                        };
                    }
                }
                if (snapshotWasRequested)
                    ReportSnapshotFailure(key, scope, packet.EnemyId, packet.Generation);
                return;
            }
            if (!ApplyEnemySnapshot(packet))
            {
                ReportSnapshotFailure(key, scope, packet.EnemyId, packet.Generation);
                return;
            }
            Multiplayer.Session.Generations.ReconcileFromHost(scope, packet.EnemyId,
                localGeneration, packet.Generation);
            CompleteEnemyStateRequest(key, packet.RequestId);
            return;
        }

        ApplyAuthoritativeHealth(scope, packet);
        CompleteEnemyStateRequest(key, packet.RequestId);
    }

    private void CompleteEnemyStateRequest((int AstroId, int EnemyId) key, long requestId)
    {
        if (requestId == 0) return;
        lock (stateGate)
        {
            if (pendingEnemyStates.TryGetValue(key, out var pending) && pending.RequestId == requestId)
                pendingEnemyStates.Remove(key);
        }
    }

    private void ReportSnapshotFailure((int AstroId, int EnemyId) key, int scope, int enemyId,
        long generation)
    {
        var reconnect = false;
        lock (stateGate)
        {
            if (!pendingEnemyStates.TryGetValue(key, out var failed)) return;
            var worldId = SaveManager.WorldId;
            if (automaticReconnectWorldId != worldId)
            {
                automaticReconnectWorldId = worldId;
                automaticCombatReconnectAttempted = false;
            }
            if (!failed.Warned)
            {
                failed.Warned = true;
                Log.Warn($"Enemy snapshot could not be applied: scope={scope}, id={enemyId}, generation={generation}");
            }
            failed.SnapshotFailures++;
            if (failed.SnapshotFailures >= 3 && !automaticCombatReconnectAttempted)
            {
                automaticCombatReconnectAttempted = true;
                reconnect = true;
            }
        }
        if (reconnect)
            UnityDispatchQueue.RunOnMainThread(() =>
            {
                if (Multiplayer.IsActive && Multiplayer.Session.IsClient &&
                    !GameStatesManager.DuringReconnect && UIRoot.instance?.uiGame?.escMenu != null)
                {
                    Log.Warn("Enemy snapshot repair failed repeatedly; reconnecting to reload authoritative state.");
                    GameStatesManager.DoFastReconnect();
                }
            });
    }

    private static void ApplyAuthoritativeDeath(int scope, int enemyId)
    {
        if (scope == 0)
        {
            var sector = GameMain.spaceSector;
            if (sector == null || enemyId >= sector.enemyPool.Length || sector.enemyPool[enemyId].id != enemyId) return;
            using (Multiplayer.Session.Enemies.IsIncomingRequest.On())
                sector.KillEnemyFinal(enemyId, ref CombatStat.empty);
        }
        else
        {
            var factory = GameMain.galaxy.PlanetById(scope)?.factory;
            if (factory == null || enemyId >= factory.enemyPool.Length || factory.enemyPool[enemyId].id != enemyId) return;
            using (Multiplayer.Session.Combat.IsIncomingRequest.On())
                factory.KillEnemyFinally(enemyId, ref CombatStat.empty);
        }
    }

    private static bool EnemyIdentityMatches(int scope, CombatEnemyStateResponsePacket packet)
    {
        var pool = scope == 0 ? GameMain.spaceSector?.enemyPool
            : GameMain.galaxy.PlanetById(scope)?.factory?.enemyPool;
        return pool != null && packet.EnemyId < pool.Length &&
            pool[packet.EnemyId].id == packet.EnemyId &&
            pool[packet.EnemyId].protoId == packet.ProtoId &&
            pool[packet.EnemyId].originAstroId == packet.OriginAstroId &&
            pool[packet.EnemyId].modelIndex == packet.ModelIndex &&
            pool[packet.EnemyId].owner == packet.Owner &&
            pool[packet.EnemyId].port == packet.Port &&
            pool[packet.EnemyId].dynamic == packet.Dynamic;
    }

    private static void ApplyAuthoritativeHealth(int scope, CombatEnemyStateResponsePacket packet)
    {
        var enemyId = packet.EnemyId;
        var sector = GameMain.spaceSector;
        var factory = scope == 0 ? null : GameMain.galaxy.PlanetById(scope)?.factory;
        var pool = scope == 0 ? sector?.enemyPool : factory?.enemyPool;
        if (pool == null || enemyId >= pool.Length) return;
        ref var enemy = ref pool[enemyId];
        if (enemy.id != enemyId || enemy.protoId != packet.ProtoId ||
            enemy.originAstroId != packet.OriginAstroId) return;

        var skillSystem = scope == 0 ? sector.skillSystem : factory.skillSystem;
        var stats = skillSystem.combatStats;
        var statId = enemy.combatStatId;
        var hasLocalStat = statId > 0 && statId < stats.cursor && stats.buffer[statId].id == statId &&
            stats.buffer[statId].objectType == (int)EObjectType.Enemy &&
            stats.buffer[statId].objectId == enemyId &&
            stats.buffer[statId].originAstroId == packet.OriginAstroId;
        if (!packet.HasCombatStat)
        {
            if (hasLocalStat) stats.buffer[statId].HandleFullHp(GameMain.data, skillSystem);
            else enemy.combatStatId = 0;
            return;
        }

        if (!hasLocalStat)
        {
            ref var created = ref stats.Add();
            created.originAstroId = packet.OriginAstroId;
            created.astroId = packet.OriginAstroId;
            created.objectType = (int)EObjectType.Enemy;
            created.objectId = enemyId;
            created.dynamic = enemy.dynamic ? 1 : 0;
            var barPos = enemy.pos;
            var barHeights = SkillSystem.BarHeightByModelIndex;
            if (!enemy.dynamic && barHeights != null && enemy.modelIndex >= 0 && enemy.modelIndex < barHeights.Length)
                barPos += enemy.pos.normalized * barHeights[enemy.modelIndex];
            created.localPos = (Vector3)barPos;
            var barWidths = SkillSystem.BarWidthByModelIndex;
            created.size = barWidths != null && enemy.modelIndex >= 0 && enemy.modelIndex < barWidths.Length
                ? barWidths[enemy.modelIndex] : 1f;
            enemy.combatStatId = created.id;
            statId = created.id;
        }
        ref var stat = ref stats.buffer[statId];
        stat.hpMax = Math.Max(1, packet.HpMax);
        stat.hp = Math.Max(1, Math.Min(packet.Hp, stat.hpMax));
        stat.hpRecover = packet.HpRecover;
        stat.hpIncoming = packet.HpIncoming;
    }

    private static int NormalizeEnemyScope(int astroId) => astroId > 1000000 ? 0 : astroId;

    private static bool IsEnemyScopeLoaded(int scope) => scope == 0
        ? GameMain.spaceSector != null
        : GameMain.galaxy?.PlanetById(scope)?.factory != null;

    private void ClearStateRequests()
    {
        lock (stateGate)
        {
            pendingEnemyStates.Clear();
            receivedEnemyStates.Clear();
        }
    }

    private void ClearGroundStateRequests()
    {
        lock (stateGate)
        {
            var ground = new List<(int AstroId, int EnemyId)>();
            foreach (var key in pendingEnemyStates.Keys)
                if (key.AstroId != 0) ground.Add(key);
            foreach (var key in ground) pendingEnemyStates.Remove(key);
            var spaceResponses = new Queue<CombatEnemyStateResponsePacket>();
            while (receivedEnemyStates.Count > 0)
            {
                var response = receivedEnemyStates.Dequeue();
                if (NormalizeEnemyScope(response.AstroId) == 0) spaceResponses.Enqueue(response);
            }
            while (spaceResponses.Count > 0) receivedEnemyStates.Enqueue(spaceResponses.Dequeue());
        }
    }

    private void ClearAuthoritativeRemovals()
    {
        lock (stateGate) authoritativeRemovals.Clear();
    }
}
