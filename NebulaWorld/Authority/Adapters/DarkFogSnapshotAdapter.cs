using System.Collections.Generic;
using System.IO;
using NebulaModel.Authority;

namespace NebulaWorld.Authority.Adapters;

/// <summary>Small UI facts independent of instantiated enemies and local preview lifetimes.</summary>
public sealed class DarkFogSnapshotAdapter : IHostWorldView
{
    private readonly AuthorityEpoch epoch;
    private readonly Dictionary<ScopeKey, SlotGenerationTracker> trackers = new();
    public DarkFogSnapshotAdapter(AuthorityEpoch epoch) => this.epoch = epoch;

    public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
    {
        if (scope.Kind != PoolKind.HiveSummary && scope.Kind != PoolKind.Base) return false;
        if (!trackers.TryGetValue(scope, out var tracker)) trackers[scope] = tracker = new SlotGenerationTracker();
        if (scope.Kind == PoolKind.HiveSummary)
        {
            var hives = GameMain.spaceSector?.dfHivesByAstro;
            if (hives == null) return false;
            for (var id = 1; id < hives.Length; id++)
                if (hives[id] != null) members.Add(ObjectKey.Create(epoch, scope.Kind, scope.Scope, id, tracker.ObserveOccupied(id)));
        }
        else
        {
            var bases = GameMain.galaxy?.PlanetById(scope.Scope)?.factory?.enemySystem?.bases;
            if (bases?.buffer == null) return false;
            for (var id = 1; id < bases.cursor; id++)
                if (bases.buffer[id]?.id == id) members.Add(ObjectKey.Create(epoch, scope.Kind, scope.Scope, id, tracker.ObserveOccupied(id)));
        }
        tracker.EndScan();
        return true;
    }

    public bool TryReadState(ObjectKey key, out byte[] state)
    {
        state = null;
        if (!key.Epoch.Equals(epoch)) return false;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)2);
        if (key.Kind == PoolKind.HiveSummary)
        {
            var hives = GameMain.spaceSector?.dfHivesByAstro;
            if (hives == null || key.NativeId >= hives.Length || hives[key.NativeId] == null) return false;
            var hive = hives[key.NativeId];
            writer.Write(hive.starData.id); writer.Write(hive.hiveOrbitIndex);
            writer.Write(hive.realized); writer.Write(hive.isEmpty); writer.Write(hive.isAlive);
            writer.Write(hive.rootEnemyId);
            hive.evolve.Export(writer);
            writer.Write(hive.currentIncomingAssaultingUnitCount);
            writer.Write(hive.currentIncomingAssaultingUnitUPos.x);
            writer.Write(hive.currentIncomingAssaultingUnitUPos.y);
            writer.Write(hive.currentIncomingAssaultingUnitUPos.z);
            writer.Write(hive.totalAvailableBuildingCount); writer.Write(hive.totalUnitCount);
            var orbit = hive.hiveAstroOrbit;
            writer.Write(orbit.orbitRadius); writer.Write(orbit.orbitInclination); writer.Write(orbit.orbitLongitude);
            writer.Write(orbit.orbitalPeriod); writer.Write(orbit.orbitPhase);
            writer.Write(orbit.orbitRotation.x); writer.Write(orbit.orbitRotation.y); writer.Write(orbit.orbitRotation.z); writer.Write(orbit.orbitRotation.w);
            writer.Write(orbit.orbitNormal.x); writer.Write(orbit.orbitNormal.y); writer.Write(orbit.orbitNormal.z);
            writer.Write(hive.orbitRadius);
        }
        else if (key.Kind == PoolKind.Base)
        {
            var bases = GameMain.galaxy?.PlanetById(key.Scope)?.factory?.enemySystem?.bases;
            if (bases?.buffer == null || key.NativeId >= bases.cursor || bases.buffer[key.NativeId]?.id != key.NativeId) return false;
            var item = bases.buffer[key.NativeId];
            writer.Write(item.enemyId); writer.Write(item.ruinId);
            item.evolve.Export(writer);
            writer.Write(item.currentIncomingAssaultingUnitCount);
            writer.Write(item.currentIncomingAssaultingUnitPos.x);
            writer.Write(item.currentIncomingAssaultingUnitPos.y);
            writer.Write(item.currentIncomingAssaultingUnitPos.z);
            writer.Write(item.totalAvailableBuildingCount); writer.Write(item.totalUnitCount);
        }
        else return false;
        state = stream.ToArray();
        return true;
    }
}
