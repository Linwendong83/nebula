using System;
using System.Collections.Generic;
using NebulaAPI.DataStructures;
using NebulaModel.DataStructures;
using UnityEngine;

namespace NebulaWorld.Factory;

/// <summary>Coordinates ownership of green prebuilds; vanilla modules still order and fly their drones.</summary>
public sealed class BuildDispatchManager : IDisposable
{
    private readonly BuildTargetClaims claims = new();
    private readonly HashSet<(int PlanetId, int PrebuildId)> pending = new();
    private readonly HashSet<PlanetFactory> seededFactories = new();
    private readonly Dictionary<ushort, BuilderCapability> remoteBuilders = new();

    public void Dispose()
    {
        OnFactoriesUnloaded();
        remoteBuilders.Clear();
        GC.SuppressFinalize(this);
    }

    public void OnFactoriesUnloaded()
    {
        claims.Clear();
        pending.Clear();
        seededFactories.Clear();
    }

    public bool TryGet(int planetId, int prebuildId, out BuildTargetClaim claim) =>
        claims.TryGet(planetId, prebuildId, out claim);

    public static PlanetFactory FindFactory(PrebuildData[] pool)
    {
        var data = GameMain.data;
        if (data == null || pool == null) return null;
        for (var i = 0; i < data.factoryCount; i++)
        {
            var factory = data.factories[i];
            if (factory != null && ReferenceEquals(factory.prebuildPool, pool)) return factory;
        }
        return null;
    }

    public void ReadyLocally(PlanetFactory factory, int prebuildId)
    {
        if (factory == null) return;
        if (Multiplayer.Session.IsServer) ObserveReady(factory, prebuildId);
        if (claims.TryGet(factory.planetId, prebuildId, out var claim) && !claim.Launched)
            RefreshLocalQueue(factory, claim);
    }

    public bool CanQueue(PlanetFactory factory, ConstructionModuleComponent module, int prebuildId)
    {
        if (factory == null || module == null || prebuildId <= 0 || prebuildId >= factory.prebuildCursor ||
            !claims.TryGet(factory.planetId, prebuildId, out var claim)) return false;
        if (claim.Launched) return false;
        ref var prebuild = ref factory.prebuildPool[prebuildId];
        if (prebuild.id != prebuildId || prebuild.itemRequired != 0 || prebuild.isDestroyed || prebuild.builderLaunched)
            return false;
        if (!module.droneEnabled || !module.droneConstructEnabled || module.droneCount <= 0) return false;
        if (module.entityId != 0)
            return claim.OwnerKind == BuildOwnerKind.Base && claim.OwnerId == module.entityId;
        if (claim.OwnerKind != BuildOwnerKind.Player || claim.OwnerId != Multiplayer.Session.LocalPlayer.Id ||
            factory != GameMain.mainPlayer.factory || GameMain.mainPlayer.speed > 20f) return false;
        return (prebuild.pos - GameMain.mainPlayer.position).sqrMagnitude <=
               GameMain.mainPlayer.mecha.buildArea * GameMain.mainPlayer.mecha.buildArea;
    }

    public void ObserveReady(PlanetFactory factory, int prebuildId)
    {
        if (!Multiplayer.Session.IsServer || !IsReady(factory, prebuildId) ||
            claims.TryGet(factory.planetId, prebuildId, out _)) return;
        pending.Add((factory.planetId, prebuildId));
        if (Multiplayer.Session.IsGameLoaded) TryAssign(factory, prebuildId);
    }

    public void Tick()
    {
        if (!Multiplayer.Session.IsGameLoaded || GameMain.galaxy == null) return;
        if (!Multiplayer.Session.IsServer) return;
        if (GameMain.data == null) return;
        var data = GameMain.data;
        for (var i = 0; i < data.factoryCount; i++)
        {
            var factory = data.factories[i];
            if (factory == null || !seededFactories.Add(factory)) continue;
            SeedFactory(factory);
            ClearQueuedTargets(factory);
        }
        if (GameMain.gameTick % 30 != 0) return;
        var invalid = new List<BuildTargetClaim>();
        foreach (var claim in claims.All)
        {
            if (claim.Launched) continue;
            var factory = GameMain.galaxy.PlanetById(claim.PlanetId)?.factory;
            var baseInvalid = false;
            if (factory != null && claim.OwnerKind == BuildOwnerKind.Base)
            {
                baseInvalid = !BaseStillAvailable(factory, claim.OwnerId);
            }
            if (factory == null || !IsReady(factory, claim.PrebuildId) ||
                claim.OwnerKind == BuildOwnerKind.Player && claim.OwnerId == Multiplayer.Session.LocalPlayer.Id &&
                !CanQueue(factory, GameMain.mainPlayer.mecha.constructionModule, claim.PrebuildId) ||
                baseInvalid)
                invalid.Add(claim);
        }
        foreach (var claim in invalid) Release(claim, retry: true);
        if (pending.Count == 0) return;
        var tasks = new List<(int PlanetId, int PrebuildId)>(pending);
        foreach (var task in tasks)
        {
            var factory = GameMain.galaxy.PlanetById(task.PlanetId)?.factory;
            if (!IsReady(factory, task.PrebuildId)) pending.Remove(task);
            else TryAssign(factory, task.PrebuildId);
        }
    }

    private static bool BaseStillAvailable(PlanetFactory factory, int entityId)
    {
        if (entityId <= 0 || entityId >= factory.entityCursor || factory.entityPool[entityId].id != entityId)
            return false;
        var moduleId = factory.entityPool[entityId].constructionModuleId;
        if (moduleId <= 0 || moduleId >= factory.constructionSystem.constructionModules.cursor) return false;
        var module = factory.constructionSystem.constructionModules.buffer[moduleId];
        if (module == null || !module.droneEnabled || !module.droneConstructEnabled || module.droneCount <= 0 ||
            module.battleBaseId <= 0 || module.battleBaseId >= factory.defenseSystem.battleBases.cursor)
            return false;
        var baseComponent = factory.defenseSystem.battleBases.buffer[module.battleBaseId];
        return baseComponent != null && baseComponent.id == module.battleBaseId;
    }

    private void SeedFactory(PlanetFactory factory)
    {
        for (var i = 1; i < factory.prebuildCursor; i++)
        {
            ref var prebuild = ref factory.prebuildPool[i];
            if (prebuild.id != i || prebuild.itemRequired != 0 || prebuild.isDestroyed ||
                claims.TryGet(factory.planetId, i, out _)) continue;
            if (prebuild.builderLaunched && TryFindLaunchedOwner(factory, i, out var kind, out var ownerId))
            {
                var claim = claims.Assign(factory.planetId, i, kind, ownerId);
                claims.MarkLaunched(factory.planetId, i, claim.Generation, kind, ownerId);
            }
            else
            {
                prebuild.builderLaunched = false;
                prebuild.builderModuleId = 0;
                prebuild.builderValue = 0f;
                pending.Add((factory.planetId, i));
            }
        }
    }

    private static bool TryFindLaunchedOwner(PlanetFactory factory, int prebuildId,
        out BuildOwnerKind kind, out int ownerId)
    {
        kind = BuildOwnerKind.None;
        ownerId = 0;
        var drones = factory.constructionSystem.drones;
        for (var i = 1; i < drones.cursor; i++)
        {
            ref var drone = ref drones.buffer[i];
            if (drone.id != i || drone.stage == 0 || drone.stage == 4 ||
                drone.targetObjectId != -prebuildId && drone.nextTarget1ObjectId != -prebuildId &&
                drone.nextTarget2ObjectId != -prebuildId && drone.nextTarget3ObjectId != -prebuildId) continue;
            if (drone.owner == 0)
            {
                kind = BuildOwnerKind.Player;
                ownerId = Multiplayer.Session.LocalPlayer.Id;
                return true;
            }
            if (drone.owner >= factory.constructionSystem.constructionModules.cursor) continue;
            var module = factory.constructionSystem.constructionModules.buffer[drone.owner];
            if (module == null || module.id != drone.owner || module.entityId <= 0) continue;
            kind = BuildOwnerKind.Base;
            ownerId = module.entityId;
            return true;
        }
        return false;
    }

    public void SeedForSnapshot(PlanetFactory factory)
    {
        if (Multiplayer.Session.IsServer && factory != null && seededFactories.Add(factory))
        {
            SeedFactory(factory);
            ClearQueuedTargets(factory);
        }
        if (Multiplayer.Session.IsServer && factory != null)
        {
            var tasks = new List<(int PlanetId, int PrebuildId)>(pending);
            foreach (var task in tasks)
                if (task.PlanetId == factory.planetId) TryAssign(factory, task.PrebuildId);
        }
    }

    public void InitializeLoadedFactories()
    {
        if (!Multiplayer.Session.IsServer || GameMain.data == null) return;
        for (var i = 0; i < GameMain.data.factoryCount; i++)
        {
            var factory = GameMain.data.factories[i];
            if (factory == null || !seededFactories.Add(factory)) continue;
            SeedFactory(factory);
            ClearQueuedTargets(factory);
        }
        var tasks = new List<(int PlanetId, int PrebuildId)>(pending);
        foreach (var task in tasks)
        {
            var factory = GameMain.galaxy.PlanetById(task.PlanetId)?.factory;
            if (factory != null) TryAssign(factory, task.PrebuildId);
        }
    }

    private static bool IsReady(PlanetFactory factory, int prebuildId)
    {
        if (factory == null || prebuildId <= 0 || prebuildId >= factory.prebuildCursor) return false;
        ref var prebuild = ref factory.prebuildPool[prebuildId];
        return prebuild.id == prebuildId && prebuild.itemRequired == 0 && !prebuild.isDestroyed &&
               !prebuild.builderLaunched;
    }

    private void TryAssign(PlanetFactory factory, int prebuildId)
    {
        if (!IsReady(factory, prebuildId)) return;
        if (!PickOwner(factory, prebuildId, out var kind, out var ownerId)) return;
        pending.Remove((factory.planetId, prebuildId));
        var claim = claims.Assign(factory.planetId, prebuildId, kind, ownerId);
        RefreshLocalQueue(factory, claim);
    }

    private bool PickOwner(PlanetFactory factory, int prebuildId, out BuildOwnerKind kind, out int ownerId)
    {
        var selectedKind = BuildOwnerKind.None;
        var selectedId = 0;
        var pos = factory.prebuildPool[prebuildId].pos;
        var bestScore = 0f;

        void Consider(BuildOwnerKind candidateKind, int candidateId, Vector3 location, float range,
            bool enabled, int droneCount)
        {
            if (!enabled || droneCount <= 0 || CountQueued(factory.planetId, candidateKind, candidateId) >= 120)
                return;
            var distance = (pos - location).sqrMagnitude;
            var score = BuildCandidateScore.Calculate(candidateKind, distance, range);
            if (BuildCandidateScore.Beats(score, candidateKind, candidateId, bestScore, selectedKind, selectedId))
            {
                bestScore = score;
                selectedKind = candidateKind;
                selectedId = candidateId;
            }
        }

        var local = GameMain.mainPlayer;
        if (local?.factory == factory && local.planetId == factory.planetId && !Multiplayer.IsDedicated)
        {
            var module = local.mecha.constructionModule;
            Consider(BuildOwnerKind.Player, Multiplayer.Session.LocalPlayer.Id, local.position, local.mecha.buildArea,
                module.droneEnabled && module.droneConstructEnabled && local.speed <= 20f, module.droneCount);
        }

        // The player who placed the prebuild builds it. Another player or a battle base is only a
        // fallback once that player has no drone, has left the planet, or is outside build range.
        if (Multiplayer.Session.Factories.TryGetPrebuildRequest(factory.planetId, prebuildId, out var placerId) &&
            TrySelectPlacer(factory, pos, placerId, out kind, out ownerId)) return true;

        foreach (var entry in Multiplayer.Session.Server.Players.Connected)
        {
            var player = entry.Value;
            if (player.Data.LocalPlanetId != factory.planetId ||
                !remoteBuilders.TryGetValue(player.Id, out var capability)) continue;
            Consider(BuildOwnerKind.Player, player.Id, player.Data.LocalPlanetPosition.ToVector3(),
                capability.BuildArea, capability.Enabled && capability.CanLaunch, capability.DroneCount);
        }

        var modules = factory.constructionSystem.constructionModules;
        for (var i = 1; i < modules.cursor; i++)
        {
            var module = modules.buffer[i];
            if (module == null || module.id != i || module.entityId <= 0 || module.entityId >= factory.entityCursor)
                continue;
            ref var entity = ref factory.entityPool[module.entityId];
            if (entity.id != module.entityId || module.battleBaseId <= 0 ||
                module.battleBaseId >= factory.defenseSystem.battleBases.cursor) continue;
            var battleBase = factory.defenseSystem.battleBases.buffer[module.battleBaseId];
            if (battleBase == null || battleBase.id != module.battleBaseId ||
                battleBase.energy < Configs.freeMode.droneEjectEnergy) continue;
            Consider(BuildOwnerKind.Base, module.entityId, entity.pos, module.baseBuildRange,
                module.droneEnabled && module.droneConstructEnabled, module.droneCount);
        }
        kind = selectedKind;
        ownerId = selectedId;
        return kind != BuildOwnerKind.None;
    }

    private bool TrySelectPlacer(PlanetFactory factory, Vector3 pos, ushort placerId,
        out BuildOwnerKind kind, out int ownerId)
    {
        kind = BuildOwnerKind.None;
        ownerId = 0;
        var selectedKind = BuildOwnerKind.None;
        var selectedId = 0;
        var score = 0f;
        void Remember(Vector3 location, float range, bool enabled, int droneCount)
        {
            if (!enabled || droneCount <= 0 ||
                CountQueued(factory.planetId, BuildOwnerKind.Player, placerId) >= 120) return;
            var candidate = BuildCandidateScore.Calculate(BuildOwnerKind.Player,
                (pos - location).sqrMagnitude, range);
            if (candidate <= score) return;
            score = candidate;
            selectedKind = BuildOwnerKind.Player;
            selectedId = placerId;
        }

        var local = GameMain.mainPlayer;
        if (local != null && Multiplayer.Session.LocalPlayer.Id == placerId && local.factory == factory &&
            local.planetId == factory.planetId && !Multiplayer.IsDedicated)
        {
            var module = local.mecha.constructionModule;
            Remember(local.position, local.mecha.buildArea,
                module.droneEnabled && module.droneConstructEnabled && local.speed <= 20f, module.droneCount);
        }
        foreach (var entry in Multiplayer.Session.Server.Players.Connected)
        {
            var player = entry.Value;
            if (player.Id != placerId || player.Data.LocalPlanetId != factory.planetId ||
                !remoteBuilders.TryGetValue(player.Id, out var capability)) continue;
            Remember(player.Data.LocalPlanetPosition.ToVector3(), capability.BuildArea,
                capability.Enabled && capability.CanLaunch, capability.DroneCount);
        }
        kind = selectedKind;
        ownerId = selectedId;
        return kind != BuildOwnerKind.None;
    }

    private int CountQueued(int planetId, BuildOwnerKind kind, int ownerId)
    {
        var count = 0;
        foreach (var claim in claims.All)
            if (claim.PlanetId == planetId && !claim.Launched && claim.OwnerKind == kind && claim.OwnerId == ownerId)
                count++;
        return count;
    }

    public void UpdateRemoteBuilder(ushort playerId, float buildArea, int droneCount, bool enabled, bool canLaunch)
    {
        if (!Multiplayer.Session.IsServer) return;
        if (float.IsNaN(buildArea) || float.IsInfinity(buildArea)) buildArea = 0f;
        remoteBuilders[playerId] = new BuilderCapability(Math.Max(0f, Math.Min(buildArea, 1000f)),
            Math.Max(0, Math.Min(droneCount, 256)), enabled, canLaunch);
    }

    public void PlayerLeft(ushort playerId)
    {
        remoteBuilders.Remove(playerId);
        if (!Multiplayer.Session.IsServer) return;
        var released = new List<BuildTargetClaim>();
        foreach (var claim in claims.All)
            if (claim.OwnerKind == BuildOwnerKind.Player && claim.OwnerId == playerId) released.Add(claim);
        foreach (var claim in released) Release(claim, retry: true);
    }

    private void Release(BuildTargetClaim claim, bool retry)
    {
        if (!claims.Remove(claim.PlanetId, claim.PrebuildId, claim.Generation)) return;
        var factory = GameMain.galaxy.PlanetById(claim.PlanetId)?.factory;
        if (factory != null)
        {
            ResetLocalPrebuildClaim(factory, claim.PrebuildId);
            RefreshLocalQueue(factory, claim);
            if (retry && IsReady(factory, claim.PrebuildId))
                pending.Add((claim.PlanetId, claim.PrebuildId));
        }
    }

    public void TargetRemoved(PlanetFactory factory, int prebuildId)
    {
        pending.Remove((factory.planetId, prebuildId));
        if (claims.TryGet(factory.planetId, prebuildId, out var claim))
        {
            if (Multiplayer.Session.IsServer) Release(claim, retry: false);
            else claims.Remove(claim.PlanetId, claim.PrebuildId, claim.Generation);
        }
    }

    public void ReleaseLocalTarget(PlanetFactory factory, int prebuildId, ConstructionModuleComponent module)
    {
        if (factory == null || prebuildId <= 0 || !claims.TryGet(factory.planetId, prebuildId, out var claim))
            return;
        var owns = module.entityId == 0
            ? claim.OwnerKind == BuildOwnerKind.Player && claim.OwnerId == Multiplayer.Session.LocalPlayer.Id
            : claim.OwnerKind == BuildOwnerKind.Base && claim.OwnerId == module.entityId;
        if (!owns) return;
        if (Multiplayer.Session.IsServer) Release(claim, retry: true);
        else
        {
            claims.Remove(claim.PlanetId, claim.PrebuildId, claim.Generation);
            ResetLocalPrebuildClaim(factory, prebuildId);
        }
    }

    public void ReleaseLocalPlayerTargets(int planetId)
    {
        var localId = Multiplayer.Session.LocalPlayer.Id;
        var released = new List<BuildTargetClaim>();
        foreach (var claim in claims.All)
            if (claim.PlanetId == planetId && claim.OwnerKind == BuildOwnerKind.Player &&
                claim.OwnerId == localId && !claim.Launched) released.Add(claim);
        foreach (var claim in released)
        {
            if (Multiplayer.Session.IsServer) Release(claim, retry: true);
            else claims.Remove(claim.PlanetId, claim.PrebuildId, claim.Generation);
        }
        var module = GameMain.mainPlayer?.mecha?.constructionModule;
        if (module?.buildTargets != null)
        {
            Array.Clear(module.buildTargets, 0, module.buildTargets.Length);
            module.buildTargetTotalCount = 0;
        }
    }

    public void AbortLocalConstructionDrones(PlanetFactory factory)
    {
        if (factory == null) return;
        var drones = factory.constructionSystem.drones;
        for (var i = 1; i < drones.cursor; i++)
        {
            ref var drone = ref drones.buffer[i];
            if (drone.id != i || drone.owner != 0 || drone.stage == 0 || drone.stage == 4 ||
                drone.targetObjectId >= 0 && drone.nextTarget1ObjectId >= 0 &&
                drone.nextTarget2ObjectId >= 0 && drone.nextTarget3ObjectId >= 0) continue;
            factory.constructionSystem.ResetDroneTargets(ref drone);
            drone.stage = 4;
            drone.movement = 0;
        }
    }

    private void RefreshLocalQueue(PlanetFactory factory, BuildTargetClaim claim)
    {
        if (factory == null || claim.PrebuildId <= 0) return;
        ConstructionModuleComponent module = null;
        if (claim.OwnerKind == BuildOwnerKind.Player && claim.OwnerId == Multiplayer.Session.LocalPlayer.Id &&
            GameMain.mainPlayer.factory == factory)
            module = GameMain.mainPlayer.mecha.constructionModule;
        else if (claim.OwnerKind == BuildOwnerKind.Base && claim.OwnerId > 0 &&
                 claim.OwnerId < factory.entityCursor && factory.entityPool[claim.OwnerId].id == claim.OwnerId)
        {
            var moduleId = factory.entityPool[claim.OwnerId].constructionModuleId;
            if (moduleId > 0 && moduleId < factory.constructionSystem.constructionModules.cursor)
                module = factory.constructionSystem.constructionModules.buffer[moduleId];
        }
        if (module == null) return;
        module.SearchBuildTargets(factory, GameMain.mainPlayer, true);
    }

    private static void ResetLocalPrebuildClaim(PlanetFactory factory, int prebuildId)
    {
        if (factory == null || prebuildId <= 0 || prebuildId >= factory.prebuildCursor ||
            factory.prebuildPool[prebuildId].id != prebuildId) return;
        ref var prebuild = ref factory.prebuildPool[prebuildId];
        prebuild.builderLaunched = false;
        prebuild.builderModuleId = 0;
        prebuild.builderValue = 0f;
    }

    public byte[] ExportSnapshot(int planetId)
    {
        var entries = new List<BuildTargetClaim>();
        foreach (var claim in claims.All)
            if (claim.PlanetId == planetId) entries.Add(claim);
        return BuildTargetClaimSnapshot.Export(entries, planetId);
    }

    public void ImportSnapshot(PlanetFactory factory, byte[] data)
    {
        if (factory == null || data == null) return;
        var imported = BuildTargetClaimSnapshot.Import(data, factory.planetId, factory.prebuildCursor);
        claims.ClearPlanet(factory.planetId);
        ClearQueuedTargets(factory);
        foreach (var claim in imported)
        {
            var id = claim.PrebuildId;
            if (id > 0 && id < factory.prebuildCursor && factory.prebuildPool[id].id == id)
                claims.Apply(claim);
        }
        foreach (var claim in claims.All)
            if (claim.PlanetId == factory.planetId && !claim.Launched) RefreshLocalQueue(factory, claim);
    }

    public void RefreshFactoryQueues(PlanetFactory factory)
    {
        if (factory == null) return;
        foreach (var claim in claims.All)
            if (claim.PlanetId == factory.planetId && !claim.Launched) RefreshLocalQueue(factory, claim);
    }

    private static void ClearQueuedTargets(PlanetFactory factory)
    {
        var modules = factory.constructionSystem.constructionModules;
        for (var i = 1; i < modules.cursor; i++)
        {
            var module = modules.buffer[i];
            if (module?.buildTargets == null) continue;
            Array.Clear(module.buildTargets, 0, module.buildTargets.Length);
            module.buildTargetTotalCount = 0;
        }
        if (GameMain.mainPlayer.factory == factory)
        {
            var module = GameMain.mainPlayer.mecha.constructionModule;
            if (module.buildTargets != null) Array.Clear(module.buildTargets, 0, module.buildTargets.Length);
            module.buildTargetTotalCount = 0;
        }
    }

    private readonly struct BuilderCapability
    {
        public BuilderCapability(float buildArea, int droneCount, bool enabled, bool canLaunch)
        {
            BuildArea = buildArea;
            DroneCount = droneCount;
            Enabled = enabled;
            CanLaunch = canLaunch;
        }
        public float BuildArea { get; }
        public int DroneCount { get; }
        public bool Enabled { get; }
        public bool CanLaunch { get; }
    }
}
