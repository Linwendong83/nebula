using System;
using NebulaAPI.Packets;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Combat;
using NebulaWorld;

namespace NebulaNetwork.PacketProcessors.Combat;

[RegisterPacketProcessor]
public class CombatEnemyStateRequestProcessor : PacketProcessor<CombatEnemyStateRequestPacket>
{
    protected override void ProcessPacket(CombatEnemyStateRequestPacket packet, NebulaConnection conn)
    {
        if (!IsHost || !Multiplayer.Session.IsGameLoaded || Players.Get(conn) == null ||
            packet.EnemyId <= 0 || packet.RequestId <= 0) return;
        SendState(conn, packet.AstroId, packet.EnemyId, packet.ExpectedGeneration,
            packet.RequestId, packet.IncludeSnapshot);
    }

    internal static void SendState(NebulaConnection conn, int astroId, int enemyId,
        long expectedGeneration, long requestId, bool includeSnapshot)
    {
        if (enemyId <= 0) return;
        var scope = astroId > 1000000 ? 0 : astroId;
        if (scope != 0 && (scope <= 100 || scope > 204899 || scope % 100 == 0)) return;
        var sector = GameMain.spaceSector;
        var planet = scope == 0 ? null : GameMain.galaxy?.PlanetById(scope);
        var factory = planet?.factory;
        var pool = scope == 0 ? sector?.enemyPool : factory?.enemyPool;
        if ((scope == 0 && sector == null) || (scope != 0 && factory == null)) return;
        var alive = pool != null && enemyId < pool.Length && pool[enemyId].id == enemyId;
        var response = new CombatEnemyStateResponsePacket
        {
            AstroId = scope,
            EnemyId = enemyId,
            ExpectedGeneration = expectedGeneration,
            RequestId = requestId,
            Generation = alive ? Multiplayer.Session.Generations.Get(scope, enemyId)
                : Multiplayer.Session.Generations.Peek(scope, enemyId),
            Alive = alive
        };
        if (alive)
        {
            ref var enemy = ref pool[enemyId];
            response.OriginAstroId = enemy.originAstroId;
            response.ProtoId = enemy.protoId;
            response.ModelIndex = enemy.modelIndex;
            response.Owner = enemy.owner;
            response.Port = enemy.port;
            response.Dynamic = enemy.dynamic;
            var skillSystem = scope == 0 ? sector.skillSystem : factory.skillSystem;
            var statId = enemy.combatStatId;
            var stats = skillSystem.combatStats;
            if (statId > 0 && statId < stats.cursor && stats.buffer[statId].id == statId &&
                stats.buffer[statId].objectType == (int)EObjectType.Enemy &&
                stats.buffer[statId].objectId == enemyId &&
                stats.buffer[statId].originAstroId == enemy.originAstroId)
            {
                ref var stat = ref stats.buffer[statId];
                response.HasCombatStat = true;
                response.Hp = stat.hp;
                response.HpMax = stat.hpMax;
                response.HpRecover = stat.hpRecover;
                response.HpIncoming = stat.hpIncoming;
            }
            if (includeSnapshot)
                response.Snapshot = Multiplayer.Session.Enemies.ExportEnemySnapshot(scope, enemyId) ?? Array.Empty<byte>();
        }
        conn.SendPacket(response);
    }
}
