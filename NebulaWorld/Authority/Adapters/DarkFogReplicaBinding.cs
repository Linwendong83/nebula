using System;
using System.Collections.Generic;
using System.IO;
using NebulaModel.Authority;
using UnityEngine;

namespace NebulaWorld.Authority.Adapters;

public sealed class DarkFogReplicaBinding : IReplicaMirrorObserver, IReplicaBaselineReadiness
{
    public readonly Dictionary<int, bool> HiveAlive = new();
    public readonly Dictionary<int, int> HiveBuildings = new();
    public readonly Dictionary<int, int> HiveUnits = new();
    public readonly Dictionary<(int Planet, int Base), int> BaseBuildings = new();
    public readonly Dictionary<(int Planet, int Base), int> BaseUnits = new();
    public bool IsReadyForBaseline(ScopeKey scope) => scope.Kind == PoolKind.HiveSummary
        ? GameMain.spaceSector?.dfHivesByAstro != null
        : scope.Kind != PoolKind.Base || GameMain.galaxy?.PlanetById(scope.Scope)?.factory?.planet.factoryLoaded == true;

    public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state)
    {
        if (scope.Kind != PoolKind.HiveSummary && scope.Kind != PoolKind.Base) return;
        if (!Multiplayer.Session.AuthorityRuntime.ApplyContext.IsActiveOnCurrentThread) throw new InvalidOperationException("Dark fog state outside replica apply.");
        if (state == null || state.Length > 256) throw new InvalidDataException("Invalid dark fog summary.");
        using var stream = new MemoryStream(state, false);
        using var reader = new BinaryReader(stream);
        if (reader.ReadByte() != 2) throw new InvalidDataException("Unknown dark fog summary version.");
        if (scope.Kind == PoolKind.HiveSummary)
        {
            var sector = GameMain.spaceSector;
            var starId = reader.ReadInt32(); var orbit = reader.ReadInt32();
            var realized = reader.ReadBoolean(); var empty = reader.ReadBoolean(); var alive = reader.ReadBoolean();
            var root = reader.ReadInt32();
            var evolve = new EvolveData(); evolve.Import(reader);
            var incoming = reader.ReadInt32();
            var position = new VectorLF3(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
            var buildings = reader.ReadInt32(); var unitCount = reader.ReadInt32();
            if (starId <= 0 || GameMain.galaxy.StarById(starId) == null || evolve.level < 0 || evolve.level > 100 || key.NativeId > 65536)
                throw new InvalidDataException("Invalid hive identity or level.");
            var star = GameMain.galaxy.StarById(starId);
            if (orbit < 0 || orbit >= star.hiveAstroOrbits.Length) throw new InvalidDataException("Invalid hive orbit index.");
            if (key.NativeId >= sector.dfHivesByAstro.Length) Array.Resize(ref sector.dfHivesByAstro, key.NativeId + 1);
            var hive = sector.dfHivesByAstro[key.NativeId];
            if (hive == null)
            {
                hive = new EnemyDFHiveSystem(); hive.Init(GameMain.data, starId, orbit); hive.InitFormations();
                hive.hiveAstroId = 1000000 + key.NativeId;
                hive.hiveAstroOrbit = hive.starData.hiveAstroOrbits[orbit];
                hive.pbuilders = new GrowthPattern_DFSpace.Builder[2];
                hive.nextSibling = sector.dfHives[hive.starData.index];
                sector.dfHives[hive.starData.index] = hive;
                sector.dfHivesByAstro[key.NativeId] = hive;
            }
            hive.realized = realized; hive.isEmpty = empty; hive.rootEnemyId = root;
            var hostOrbit = hive.hiveAstroOrbit;
            hostOrbit.orbitRadius = reader.ReadSingle(); hostOrbit.orbitInclination = reader.ReadSingle(); hostOrbit.orbitLongitude = reader.ReadSingle();
            hostOrbit.orbitalPeriod = reader.ReadDouble(); hostOrbit.orbitPhase = reader.ReadSingle();
            hostOrbit.orbitRotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            hostOrbit.orbitNormal = new VectorLF3(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
            hive.orbitRadius = reader.ReadDouble();
            if (hostOrbit.orbitalPeriod <= 0 || double.IsNaN(hostOrbit.orbitalPeriod) || double.IsInfinity(hostOrbit.orbitalPeriod))
                throw new InvalidDataException("Invalid hive orbital period.");
            hostOrbit.PredictPose(GameMain.gameTick, hive.starData.uPosition, ref sector.astros[key.NativeId]);
            if (hive.pbuilders != null && hive.pbuilders.Length > 1) hive.pbuilders[1].instId = root;
            hive.evolve = evolve; hive.currentIncomingAssaultingUnitCount = incoming;
            hive.currentIncomingAssaultingUnitUPos = position;
            HiveAlive[hive.hiveAstroId] = alive;
            HiveBuildings[hive.hiveAstroId] = buildings; HiveUnits[hive.hiveAstroId] = unitCount;
        }
        else
        {
            var factory = GameMain.galaxy.PlanetById(scope.Scope).factory;
            var enemyId = reader.ReadInt32(); var ruinId = reader.ReadInt32();
            var evolve = new EvolveData(); evolve.Import(reader);
            var incoming = reader.ReadInt32();
            var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var buildings = reader.ReadInt32(); var unitCount = reader.ReadInt32();
            if (evolve.level < 0 || evolve.level > 100 || key.NativeId > 65536) throw new InvalidDataException("Invalid base identity or level.");
            var pool = factory.enemySystem.bases;
            if (pool.capacity <= key.NativeId) pool.SetCapacity(Math.Max(key.NativeId + 1, pool.capacity * 2));
            var item = pool.buffer[key.NativeId];
            if (item?.id != key.NativeId)
            {
                item = new DFGBaseComponent { id = key.NativeId, groundSystem = factory.enemySystem };
                item.InitFormations(); item.InitIncomingSkills();
                pool.buffer[key.NativeId] = item;
                pool.cursor = Math.Max(pool.cursor, key.NativeId + 1);
            }
            item.enemyId = enemyId; item.ruinId = ruinId; item.evolve = evolve;
            item.currentIncomingAssaultingUnitCount = incoming; item.currentIncomingAssaultingUnitPos = position;
            item.pbuilders ??= new GrowthPattern_DFGround.Builder[0];
            BaseBuildings[(scope.Scope, key.NativeId)] = buildings; BaseUnits[(scope.Scope, key.NativeId)] = unitCount;
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing dark fog summary data.");
    }

    public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
    {
        if (scope.Kind != PoolKind.HiveSummary && scope.Kind != PoolKind.Base) return;
        var keep = new HashSet<int>();
        foreach (var member in members) keep.Add(member.Key.NativeId);
        if (scope.Kind == PoolKind.Base)
        {
            var pool = GameMain.galaxy.PlanetById(scope.Scope).factory.enemySystem.bases;
            for (var id = 1; id < pool.cursor; id++)
                if (pool.buffer[id]?.id == id && !keep.Contains(id)) pool.Remove(id);
        }
        else
            foreach (var id in new List<int>(HiveAlive.Keys))
                if (!keep.Contains(id - 1000000)) HiveAlive[id] = false;
        foreach (var member in members) OnStateApplied(scope, member.Key, member.Revision, member.State);
    }

    public void OnMemberRemoved(ScopeKey scope, in ObjectKey key)
    {
        if (scope.Kind == PoolKind.HiveSummary) HiveAlive[1000000 + key.NativeId] = false;
        else if (scope.Kind == PoolKind.Base)
        {
            var pool = GameMain.galaxy?.PlanetById(scope.Scope)?.factory?.enemySystem?.bases;
            if (pool != null && key.NativeId < pool.cursor && pool.buffer[key.NativeId]?.id == key.NativeId) pool.Remove(key.NativeId);
        }
    }
}
