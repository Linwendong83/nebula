using System;
using System.IO;
using System.Reflection;
using NebulaModel.Logger;
using NebulaModel.Packets.Combat;

namespace NebulaWorld.Combat;

public partial class EnemyManager
{
    private const int EnemySnapshotVersion = 1;
    private static readonly (string EnemyField, string PoolField)[] groundComponents =
    [
        ("builderId", "builders"), ("dfGBaseId", "bases"), ("dfGConnectorId", "connectors"),
        ("dfGReplicatorId", "replicators"), ("dfGTurretId", "turrets"),
        ("dfGShieldId", "shields"), ("unitId", "units")
    ];
    private static readonly (string EnemyField, string PoolField)[] spaceComponents =
    [
        ("builderId", "builders"), ("dfSCoreId", "cores"), ("dfSNodeId", "nodes"),
        ("dfSConnectorId", "connectors"), ("dfSReplicatorId", "replicators"),
        ("dfSGammaId", "gammas"), ("dfSTurretId", "turrets"),
        ("dfTinderId", "tinders"), ("dfRelayId", "relays"), ("unitId", "units")
    ];

    public byte[] ExportEnemySnapshot(int scope, int enemyId)
    {
        try { return ExportEnemySnapshotCore(scope, enemyId); }
        catch (Exception e)
        {
            Log.Warn("Enemy snapshot export failed: " + e);
            return null;
        }
    }

    private static byte[] ExportEnemySnapshotCore(int scope, int enemyId)
    {
        if (!Multiplayer.Session.IsServer || enemyId <= 0) return null;
        var sector = GameMain.spaceSector;
        var factory = scope == 0 ? null : GameMain.galaxy.PlanetById(scope)?.factory;
        var pool = scope == 0 ? sector?.enemyPool : factory?.enemyPool;
        if (pool == null || enemyId >= pool.Length || pool[enemyId].id != enemyId) return null;
        ref var enemy = ref pool[enemyId];
        var system = scope == 0 ? (object)sector.GetHiveByAstroId(enemy.originAstroId) : factory.enemySystem;
        if (system == null) return null;

        var kind = GetSnapshotKind(scope, in enemy);
        var baseId = scope == 0 ? 0 : enemy.owner;
        var builderIndex = -1;
        var dockIndex = -1;
        if (enemy.builderId > 0)
        {
            var builders = scope == 0
                ? ((EnemyDFHiveSystem)system).builders
                : ((EnemyDFGroundSystem)system).builders;
            if (enemy.builderId >= builders.cursor || builders.buffer[enemy.builderId].id != enemy.builderId)
                return null;
            builderIndex = builders.buffer[enemy.builderId].builderIndex;
        }
        if (scope == 0 && enemy.dfRelayId > 0 &&
            enemy.dfRelayId < ((EnemyDFHiveSystem)system).relays.cursor)
            dockIndex = ((EnemyDFHiveSystem)system).relays.buffer[enemy.dfRelayId].dockIndex;
        if (scope == 0 && enemy.dfTinderId > 0 &&
            enemy.dfTinderId < ((EnemyDFHiveSystem)system).tinders.cursor)
            dockIndex = ((EnemyDFHiveSystem)system).tinders.buffer[enemy.dfTinderId].dockIndex;
        if (scope != 0 && enemy.unitId > 0)
            baseId = ((EnemyDFGroundSystem)system).units.buffer[enemy.unitId].baseId;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(EnemySnapshotVersion);
        writer.Write((byte)kind);
        writer.Write(baseId);
        writer.Write(builderIndex);
        writer.Write(dockIndex);
        enemy.Export(writer);
        foreach (var (enemyField, poolField) in scope == 0 ? spaceComponents : groundComponents)
        {
            var componentId = GetEnemyComponentId(in enemy, enemyField);
            var bytes = componentId > 0 ? ExportComponent(system, poolField, componentId) : null;
            if (componentId > 0 && bytes == null) return null;
            writer.Write(bytes?.Length ?? 0);
            if (bytes != null) writer.Write(bytes);
        }
        return stream.ToArray();
    }

    private bool ApplyEnemySnapshot(CombatEnemyStateResponsePacket packet)
    {
        if (!packet.Alive || packet.Snapshot == null || packet.Snapshot.Length == 0) return false;
        var scope = NormalizeEnemyScope(packet.AstroId);
        try
        {
            using var stream = new MemoryStream(packet.Snapshot, false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != EnemySnapshotVersion) return false;
            var kind = (EnemySnapshotKind)reader.ReadByte();
            var baseId = reader.ReadInt32();
            var builderIndex = reader.ReadInt32();
            var dockIndex = reader.ReadInt32();
            var hostEnemy = new EnemyData();
            hostEnemy.Import(reader);
            if (hostEnemy.id != packet.EnemyId || hostEnemy.protoId != packet.ProtoId ||
                hostEnemy.originAstroId != packet.OriginAstroId ||
                hostEnemy.modelIndex != packet.ModelIndex || hostEnemy.owner != packet.Owner ||
                hostEnemy.port != packet.Port || hostEnemy.dynamic != packet.Dynamic) return false;
            var components = scope == 0 ? spaceComponents : groundComponents;
            var blobs = new byte[components.Length][];
            for (var i = 0; i < blobs.Length; i++)
            {
                var length = reader.ReadInt32();
                if (length < 0 || length > 4 * 1024 * 1024 || length > stream.Length - stream.Position) return false;
                blobs[i] = reader.ReadBytes(length);
            }
            if (stream.Position != stream.Length) return false;

            var sector = GameMain.spaceSector;
            var factory = scope == 0 ? null : GameMain.galaxy.PlanetById(scope)?.factory;
            var pool = scope == 0 ? sector?.enemyPool : factory?.enemyPool;
            var hive = scope == 0 ? sector?.GetHiveByAstroId(hostEnemy.originAstroId) : null;
            if (pool == null || (scope == 0 && hive == null)) return false;

            // Reuse native component IDs when the local object has the same
            // topology. Base cores must be overlaid because removing one also
            // tears down references owned by the whole base or hive.
            var inPlace = CanOverlayEnemy(packet.EnemyId, in hostEnemy, pool, system: scope == 0 ? (object)hive : factory.enemySystem,
                components, blobs);
            if (!inPlace && (hostEnemy.dfGBaseId > 0 || hostEnemy.dfSCoreId > 0))
                return false; // core dependencies require the existing base/hive object
            if (!inPlace)
            {
                if (!CanRecreateEnemy(kind, scope, baseId, builderIndex, in hostEnemy, factory, hive))
                    return false;
                if (packet.EnemyId < pool.Length && pool[packet.EnemyId].id == packet.EnemyId)
                {
                    if (scope == 0)
                    {
                        using (Multiplayer.Session.Enemies.IsIncomingRequest.On())
                            sector.RemoveEnemyFinal(packet.EnemyId);
                    }
                    else
                    {
                        using (Multiplayer.Session.Combat.IsIncomingRequest.On())
                            factory.RemoveEnemyFinal(packet.EnemyId);
                    }
                }
                if (!RecreateEnemy(kind, scope, packet.EnemyId, baseId, builderIndex, dockIndex,
                    in hostEnemy, factory, hive)) return false;
                pool = scope == 0 ? sector.enemyPool : factory.enemyPool;
            }

            ref var localEnemy = ref pool[packet.EnemyId];
            if (localEnemy.id != packet.EnemyId) return false;
            var system = scope == 0 ? (object)hive : factory.enemySystem;
            if (!ComponentsMatch(in localEnemy, system, components, blobs)) return false;
            for (var i = 0; i < components.Length; i++)
            {
                if (blobs[i].Length == 0) continue;
                var localId = GetEnemyComponentId(in localEnemy, components[i].EnemyField);
                if (localId <= 0 || !ImportComponent(system, components[i].PoolField, localId,
                    packet.EnemyId, blobs[i])) return false;
            }

            // Keep local pool, rendering, collider, audio and hash IDs allocated
            // by the native creation path. Copy only portable enemy state.
            localEnemy.protoId = hostEnemy.protoId;
            localEnemy.modelIndex = hostEnemy.modelIndex;
            localEnemy.astroId = hostEnemy.astroId;
            localEnemy.originAstroId = hostEnemy.originAstroId;
            localEnemy.owner = hostEnemy.owner;
            localEnemy.port = hostEnemy.port;
            localEnemy.dynamic = hostEnemy.dynamic;
            localEnemy.isSpace = hostEnemy.isSpace;
            localEnemy.localized = hostEnemy.localized;
            localEnemy.stateFlags = hostEnemy.stateFlags;
            localEnemy.pos = hostEnemy.pos;
            localEnemy.rot = hostEnemy.rot;
            localEnemy.vel = hostEnemy.vel;
            ApplyAuthoritativeHealth(scope, packet);
            return true;
        }
        catch (Exception e)
        {
            Log.Warn("Enemy snapshot reconciliation failed: " + e);
            return false;
        }
    }

    private static bool CanOverlayEnemy(int enemyId, in EnemyData host, EnemyData[] pool,
        object system, (string EnemyField, string PoolField)[] components, byte[][] blobs)
    {
        if (enemyId >= pool.Length || pool[enemyId].id != enemyId) return false;
        ref var local = ref pool[enemyId];
        if (local.protoId != host.protoId || local.modelIndex != host.modelIndex ||
            local.originAstroId != host.originAstroId || local.owner != host.owner ||
            local.port != host.port) return false;
        return ComponentsMatch(in local, system, components, blobs);
    }

    private static bool ComponentsMatch(in EnemyData local, object system,
        (string EnemyField, string PoolField)[] components, byte[][] blobs)
    {
        for (var i = 0; i < components.Length; i++)
        {
            var componentId = GetEnemyComponentId(in local, components[i].EnemyField);
            if ((componentId > 0) != (blobs[i].Length > 0)) return false;
            if (componentId <= 0) continue;
            var buffer = GetComponentBuffer(system, components[i].PoolField);
            if (componentId <= 0 || buffer == null || componentId >= buffer.Length ||
                buffer.GetValue(componentId) == null) return false;
        }
        return true;
    }

    private static bool CanRecreateEnemy(EnemySnapshotKind kind, int scope, int baseId,
        int builderIndex, in EnemyData enemy, PlanetFactory factory, EnemyDFHiveSystem hive)
    {
        if (scope == 0)
        {
            if (hive == null) return false;
            if (kind == EnemySnapshotKind.SpaceUnit)
            {
                var form = 8113 - enemy.protoId;
                return form >= 0 && form < hive.forms.Length && enemy.port > 0 &&
                    enemy.port < hive.forms[form].units.Length;
            }
            return kind == EnemySnapshotKind.SpaceRelay || kind == EnemySnapshotKind.SpaceTinder ||
                (kind == EnemySnapshotKind.SpaceBuilder && builderIndex >= 0);
        }
        if (factory == null || baseId <= 0 || baseId >= factory.enemySystem.bases.cursor ||
            factory.enemySystem.bases.buffer[baseId] == null) return false;
        if (kind == EnemySnapshotKind.GroundUnit)
        {
            var form = enemy.protoId - 8128;
            var forms = factory.enemySystem.bases.buffer[baseId].forms;
            return form >= 0 && form < forms.Length && enemy.port > 0 && enemy.port < forms[form].units.Length;
        }
        return kind == EnemySnapshotKind.GroundBuilder && builderIndex >= 0;
    }

    private static bool RecreateEnemy(EnemySnapshotKind kind, int scope, int enemyId, int baseId,
        int builderIndex, int dockIndex, in EnemyData source, PlanetFactory factory, EnemyDFHiveSystem hive)
    {
        if (scope == 0)
        {
            using (Multiplayer.Session.Enemies.IsIncomingRequest.On())
            {
                SetSpaceSectorNextEnemyId(enemyId);
                int created;
                switch (kind)
                {
                    case EnemySnapshotKind.SpaceUnit:
                        var form = 8113 - source.protoId;
                        hive.forms[form].units[source.port] = 2;
                        var formTicks = (hive.starData.seed + GameMain.gameTick) % 1512000L;
                        created = hive.sector.CreateEnemyFinal(hive, source.protoId, hive.hiveAstroId,
                            source.port, (int)formTicks);
                        break;
                    case EnemySnapshotKind.SpaceRelay:
                    case EnemySnapshotKind.SpaceTinder:
                        created = hive.sector.CreateEnemyFinal(hive, source.protoId, hive.hiveAstroId,
                            source.pos, source.rot);
                        if (created == enemyId && kind == EnemySnapshotKind.SpaceRelay && dockIndex >= 0)
                        {
                            var relayId = hive.sector.enemyPool[created].dfRelayId;
                            hive.relays.buffer[relayId]?.SetDockIndex(dockIndex);
                        }
                        if (created == enemyId && kind == EnemySnapshotKind.SpaceTinder && dockIndex >= 0)
                        {
                            var tinderId = hive.sector.enemyPool[created].dfTinderId;
                            hive.tinders.buffer[tinderId].SetDockIndex(hive, dockIndex);
                        }
                        break;
                    case EnemySnapshotKind.SpaceBuilder:
                        created = hive.sector.CreateEnemyFinal(hive, builderIndex, false);
                        break;
                    default: return false;
                }
                return created == enemyId;
            }
        }

        using (Multiplayer.Session.Combat.IsIncomingRequest.On())
        {
            SetPlanetFactoryNextEnemyId(factory, enemyId);
            int created;
            if (kind == EnemySnapshotKind.GroundUnit)
            {
                var form = source.protoId - 8128;
                factory.enemySystem.bases.buffer[baseId].forms[form].units[source.port] = 1;
                var unitId = factory.enemySystem.ActivateUnit(baseId, form, source.port, GameMain.gameTick);
                created = unitId > 0 ? factory.enemySystem.units.buffer[unitId].enemyId : 0;
            }
            else if (kind == EnemySnapshotKind.GroundBuilder)
                created = factory.CreateEnemyFinal(baseId, builderIndex);
            else return false;
            return created == enemyId;
        }
    }

    private static EnemySnapshotKind GetSnapshotKind(int scope, in EnemyData enemy)
    {
        if (scope != 0) return enemy.unitId > 0 ? EnemySnapshotKind.GroundUnit : EnemySnapshotKind.GroundBuilder;
        if (enemy.dfRelayId > 0) return EnemySnapshotKind.SpaceRelay;
        if (enemy.dfTinderId > 0) return EnemySnapshotKind.SpaceTinder;
        return enemy.unitId > 0 ? EnemySnapshotKind.SpaceUnit : EnemySnapshotKind.SpaceBuilder;
    }

    private static int GetEnemyComponentId(in EnemyData enemy, string fieldName) =>
        (int)typeof(EnemyData).GetField(fieldName).GetValue(enemy);

    private static byte[] ExportComponent(object system, string poolName, int componentId)
    {
        var buffer = GetComponentBuffer(system, poolName);
        if (buffer == null || componentId >= buffer.Length) return null;
        var component = buffer.GetValue(componentId);
        if (component == null) return null;
        var export = component.GetType().GetMethod("Export", [typeof(BinaryWriter)]);
        if (export == null) return null;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        export.Invoke(component, [writer]);
        return stream.ToArray();
    }

    private static bool ImportComponent(object system, string poolName, int componentId,
        int enemyId, byte[] bytes)
    {
        var buffer = GetComponentBuffer(system, poolName);
        if (buffer == null || componentId >= buffer.Length) return false;
        var component = buffer.GetValue(componentId);
        if (component == null) return false;
        var type = component.GetType();
        var import = type.GetMethod("Import", [typeof(BinaryReader)]);
        if (import == null) return false;
        var idField = type.GetField("id");
        var enemyField = type.GetField("enemyId");
        var builderField = type.GetField("builderId");
        var hiveField = type.GetField("hive");
        var groundField = type.GetField("groundSystem");
        var localBuilderId = builderField?.GetValue(component);
        var localHive = hiveField?.GetValue(component);
        var localGround = groundField?.GetValue(component);
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        import.Invoke(component, [reader]);
        if (stream.Position != stream.Length) return false;
        idField?.SetValue(component, componentId);
        enemyField?.SetValue(component, enemyId);
        if (localBuilderId != null) builderField.SetValue(component, localBuilderId);
        if (localHive != null) hiveField.SetValue(component, localHive);
        if (localGround != null) groundField.SetValue(component, localGround);
        buffer.SetValue(component, componentId);
        return true;
    }

    private static Array GetComponentBuffer(object system, string poolName)
    {
        var pool = system.GetType().GetField(poolName)?.GetValue(system);
        return pool?.GetType().GetField("buffer")?.GetValue(pool) as Array;
    }

    private enum EnemySnapshotKind : byte
    {
        GroundUnit = 1,
        GroundBuilder = 2,
        SpaceUnit = 3,
        SpaceBuilder = 4,
        SpaceRelay = 5,
        SpaceTinder = 6
    }
}
