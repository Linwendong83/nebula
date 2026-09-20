using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using NebulaModel.DataStructures;
using NebulaModel.Packets.Combat;
using UnityEngine;

namespace NebulaWorld.Combat;

/// <summary>
/// Owns GPU instances and shader data only. Never attaches a craft to a factory/sector or calls skill logic.
/// </summary>
public sealed class BattleVisualRenderer : IDisposable
{
    private readonly GPUInstancingManager ground = new();
    private readonly SSGPUInstancingManager space = new();
    private readonly SkillSystem visuals;
    private readonly Dictionary<(ushort Owner, bool World, int Star, int Planet), Source> sources = new();
    private AnimData[] animations = new AnimData[64];
    private ComputeBuffer animationBuffer;
    private readonly Queue<int> freeAnimationIds = new();
    private int nextAnimationId = 1;

    public BattleVisualRenderer()
    {
        if (Multiplayer.Session.IsDedicated) throw new InvalidOperationException("Headless sessions cannot create visual replicas");
        ground.Init();
        space.Init();
        space.sectorModel = GameMain.spaceSector.model;
        // The normal constructor rebuilds static combat tables. A renderer must not touch those tables.
        visuals = (SkillSystem)FormatterServices.GetUninitializedObject(typeof(SkillSystem));
        visuals.gameData = GameMain.data;
        visuals.sector = GameMain.spaceSector;
        visuals.history = GameMain.history;
        visuals.Init();
        visuals.SetForNewGame();
        animationBuffer = new ComputeBuffer(animations.Length, AnimData.dataLen);
    }

    public void Receive((ushort Owner, bool World, int Star, int Planet) key, BattleVisualPacket packet, BattleVisualFrame frame)
    {
        if (!sources.TryGetValue(key, out var source)) sources[key] = source = new Source();
        source.LastTick = GameMain.gameTick;
        if (packet.UnitsFull)
        {
            var active = new HashSet<(int, long)>(frame.Units.Select(x => (x.Id, x.Generation)));
            foreach (var id in source.Units.Keys.Where(x => !active.Contains(x)).ToArray()) RemoveUnit(source, id);
            foreach (var unit in frame.Units)
            {
                var id = (unit.Id, unit.Generation);
                if (!source.Units.TryGetValue(id, out var proxy))
                {
                    var descriptor = LDB.models.Select(unit.Model);
                    if (descriptor == null || descriptor.ObjectType != EObjectType.Craft) continue;
                    var animationId = AllocateAnimation();
                    proxy = new UnitProxy { Current = unit, Previous = unit, AnimationId = animationId };
                    proxy.ModelId = unit.Space ? space.AddModel(unit.Model, animationId, unit.Astro, unit.Position, unit.Rotation, false) :
                        ground.AddModel(unit.Model, animationId, unit.Position, unit.Rotation, false);
                    source.Units.Add(id, proxy);
                }
                proxy.Previous = proxy.Current;
                proxy.Current = unit;
                proxy.ReceivedAt = Time.realtimeSinceStartup;
            }
        }
        var present = new HashSet<(BattleEffectKind, int, long)>();
        foreach (var effect in frame.Effects)
        {
            var id = (effect.Kind, effect.Id, effect.Generation);
            present.Add(id);
            if (packet.Tick + effect.Life <= GameMain.gameTick) continue;
            if (!source.Effects.TryGetValue(id, out var proxy))
            {
                proxy = new EffectProxy { Kind = effect.Kind, Subtype = effect.Subtype, PoolId = AllocateEffect(effect) };
                if (proxy.PoolId == 0) continue;
                source.Effects.Add(id, proxy);
            }
            proxy.Sample = Decode(effect);
            proxy.SampleTick = packet.Tick;
            proxy.Life = effect.Life;
        }
        if (packet.EffectsFull)
            foreach (var id in source.Effects.Keys.Where(x => !present.Contains(x)).ToArray()) RemoveEffect(source, id);
    }

    private int AllocateAnimation()
    {
        var id = freeAnimationIds.Count > 0 ? freeAnimationIds.Dequeue() : nextAnimationId++;
        if (id < animations.Length) return id;
        Array.Resize(ref animations, animations.Length * 2);
        animationBuffer.Release();
        animationBuffer = new ComputeBuffer(animations.Length, AnimData.dataLen);
        return id;
    }

    private int AllocateEffect(BattleEffectData effect) => effect.Kind switch
    {
        BattleEffectKind.GroundLaser => visuals.fighterLasers.Add().id,
        BattleEffectKind.GroundPlasma => visuals.fighterPlasmas.Add().id,
        BattleEffectKind.GroundShieldPlasma => visuals.fighterShieldPlasmas.Add().id,
        BattleEffectKind.SpaceLaser => visuals.warshipTypeFLasers.Add().id,
        BattleEffectKind.SpacePlasmaF => visuals.warshipTypeFPlasmas.Add().id,
        BattleEffectKind.SpacePlasmaA => visuals.warshipTypeAPlasmas.Add().id,
        BattleEffectKind.TurretMissile => visuals.turretMissiles.Add().id,
        BattleEffectKind.TurretPlasma => visuals.turretPlasmas.Add().id,
        BattleEffectKind.LancerSweep => visuals.lancerLaserSweeps.Add().id,
        BattleEffectKind.BomberProjectile => visuals.humpbackProjectiles.Add().id,
        BattleEffectKind.Impact when effect.Subtype > 0 && effect.Subtype < visuals.hitEffects.Length && visuals.hitEffects[effect.Subtype] != null =>
            visuals.hitEffects[effect.Subtype].Add().id,
        _ => 0
    };

    private static object Decode(BattleEffectData effect)
    {
        using var stream = new MemoryStream(effect.Payload, false); using var reader = new BinaryReader(stream);
        switch (effect.Kind)
        {
            case BattleEffectKind.GroundLaser:
                var laser = new LocalLaserOneShot(); laser.Import(reader); laser.damage = 0; laser.mask = 0; return laser;
            case BattleEffectKind.GroundPlasma:
            case BattleEffectKind.GroundShieldPlasma:
                var local = new LocalGeneralProjectile(); local.Import(reader); local.damage = 0; local.mask = 0; return local;
            case BattleEffectKind.SpaceLaser:
                var spaceLaser = new SpaceLaserOneShot(); spaceLaser.Import(reader); spaceLaser.damage = 0; spaceLaser.mask = 0; return spaceLaser;
            case BattleEffectKind.TurretMissile:
                var missile = new GeneralMissile(); missile.Import(reader); missile.damage = 0; missile.damageIncoming = 0; missile.mask = 0; return missile;
            case BattleEffectKind.SpacePlasmaF:
            case BattleEffectKind.SpacePlasmaA:
            case BattleEffectKind.TurretPlasma:
                var projectile = new GeneralProjectile(); projectile.Import(reader); projectile.damage = 0; projectile.damageIncoming = 0; projectile.mask = 0; return projectile;
            case BattleEffectKind.LancerSweep:
                var sweep = new SpaceLaserSweep(); sweep.Import(reader); sweep.damage = 0; sweep.mask = 0;
                sweep.endPos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()); return sweep;
            case BattleEffectKind.BomberProjectile:
                var bomb = new GeneralExpImpProjectile(); bomb.Import(reader); bomb.damage = 0; bomb.mask = 0; return bomb;
            case BattleEffectKind.Impact:
                var particle = new ParticleData(); particle.Import(reader); return particle;
            default: throw new InvalidDataException("Unknown combat visual effect");
        }
    }

    public void Draw()
    {
        if (GameMain.inOtherScene || GameCamera.main == null) return;
        foreach (var key in sources.Keys.ToArray())
        {
            if (key.Star != GameMain.localStar?.id || (key.Planet > 0 && key.Planet != GameMain.localPlanet?.id) ||
                GameMain.gameTick - sources[key].LastTick > 180)
            { RemoveSource(key); continue; }
            var source = sources[key];
            foreach (var proxy in source.Units.Values)
            {
                var current = proxy.Current;
                var t = Mathf.Clamp01((Time.realtimeSinceStartup - proxy.ReceivedAt) * 10f);
                Vector3 pos = proxy.Previous.Position + (current.Position - proxy.Previous.Position) * t;
                var rot = Quaternion.Slerp(proxy.Previous.Rotation, current.Rotation, t);
                animations[proxy.AnimationId] = current.Animation;
                if (current.Space) space.AlterModel(current.Model, proxy.ModelId, proxy.AnimationId, current.Astro, pos, rot, false);
                else ground.AlterModel(current.Model, proxy.ModelId, proxy.AnimationId, pos, rot, false);
            }
            foreach (var id in source.Effects.Keys.ToArray())
            {
                var effect = source.Effects[id];
                var age = (int)Math.Max(0, GameMain.gameTick - effect.SampleTick);
                if (age >= effect.Life) { RemoveEffect(source, id); continue; }
                UpdateEffect(effect, age);
            }
        }
        ground.LateUpdate(); space.LateUpdate();
        ground.SyncAllGPUBuffer(); space.SyncAllGPUBuffer();
        animationBuffer.SetData(animations);
        var camera = GameCamera.main;
        var position = camera.transform.position;
        var forward = camera.transform.forward;
        var dot = Mathf.Cos(Mathf.Atan(Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f) * camera.aspect * 1.05f));
        if (GameMain.localPlanet != null)
        {
            var planet = GameMain.localPlanet;
            foreach (var model in ground.objectRenderers)
                if (model != null && model.instCursor > 1)
                    model.Render(position, forward, dot, planet.realRadius, LDB.themes.Select(planet.theme).CullingRadius, animationBuffer);
        }
        var sectorModel = GameMain.spaceSector.model;
        if (sectorModel?.astroBuffer != null)
            foreach (var model in space.objectRenderers)
                if (model != null && model.instCursor > 1)
                    model.Render(position, forward, dot, animationBuffer, sectorModel.astroBuffer, sectorModel.galaxyAstroBuffer);
        // Only renderer methods are called. These detached pools never run damage or target selection.
        visuals.RendererUpdate();
        visuals.RendererDraw();
    }

    private void UpdateEffect(EffectProxy proxy, int age)
    {
        var seconds = age / 60f;
        var remaining = proxy.Life - age;
        var rotation = Quaternion.Inverse(GameMain.data.relativeRot);
        var origin = GameMain.data.relativePos;
        switch (proxy.Kind)
        {
            case BattleEffectKind.GroundLaser:
                var laser = (LocalLaserOneShot)proxy.Sample;
                laser.id = proxy.PoolId; laser.life = remaining;
                visuals.fighterLasers.buffer[proxy.PoolId] = laser;
                break;
            case BattleEffectKind.GroundPlasma:
            case BattleEffectKind.GroundShieldPlasma:
                var local = (LocalGeneralProjectile)proxy.Sample;
                local.id = proxy.PoolId; local.life = remaining; local.pos += local.dir * (local.speed * seconds);
                (proxy.Kind == BattleEffectKind.GroundPlasma ? visuals.fighterPlasmas : visuals.fighterShieldPlasmas).buffer[proxy.PoolId] = local;
                break;
            case BattleEffectKind.SpaceLaser:
                var spaceLaser = (SpaceLaserOneShot)proxy.Sample;
                spaceLaser.id = proxy.PoolId; spaceLaser.life = remaining; spaceLaser.deltaPos = spaceLaser.endVelU * seconds;
                visuals.warshipTypeFLasers.buffer[proxy.PoolId] = spaceLaser;
                break;
            case BattleEffectKind.TurretMissile:
                var missile = (GeneralMissile)proxy.Sample;
                missile.id = proxy.PoolId; missile.life += age;
                missile.uPos += (VectorLF3)(missile.uVel * seconds); missile.vel = rotation * missile.uVel / 60f;
                visuals.turretMissiles.buffer[proxy.PoolId] = missile;
                var trails = visuals.turretMissileTrails;
                trails.SetTrailCapacity(visuals.turretMissiles.capacity);
                trails.trailCursor = visuals.turretMissiles.cursor;
                if (missile.life > 0 && missile.nearAstroId > 0)
                {
                    var starAstro = missile.nearAstroId / 100 * 100;
                    var tick = (uint)(GameMain.gameTick & 0x7fffffff);
                    trails.smokePool[proxy.PoolId * trails.trailStride + tick % trails.trailStride] = new SmokeData
                    {
                        type = (uint)Math.Max(0, missile.modelIndex - 431),
                        createTime = tick,
                        astroId = starAstro,
                        pos = missile.uPos - GameMain.galaxy.astrosData[starAstro].uPos,
                        vel = missile.uVel
                    };
                }
                break;
            case BattleEffectKind.SpacePlasmaF:
            case BattleEffectKind.SpacePlasmaA:
            case BattleEffectKind.TurretPlasma:
                var projectile = (GeneralProjectile)proxy.Sample;
                projectile.id = proxy.PoolId; projectile.life = remaining;
                projectile.uPos += (VectorLF3)(projectile.uVel * seconds);
                projectile.rPos = rotation * (Vector3)(projectile.uPos - origin);
                projectile.rVelObj = rotation * projectile.uVelObj;
                var pool = proxy.Kind == BattleEffectKind.SpacePlasmaF ? visuals.warshipTypeFPlasmas :
                    proxy.Kind == BattleEffectKind.SpacePlasmaA ? visuals.warshipTypeAPlasmas : visuals.turretPlasmas;
                pool.buffer[proxy.PoolId] = projectile;
                break;
            case BattleEffectKind.LancerSweep:
                var sweep = (SpaceLaserSweep)proxy.Sample;
                sweep.id = proxy.PoolId; sweep.life = remaining;
                visuals.lancerLaserSweeps.buffer[proxy.PoolId] = sweep;
                break;
            case BattleEffectKind.BomberProjectile:
                var bomber = (GeneralExpImpProjectile)proxy.Sample;
                bomber.id = proxy.PoolId; bomber.life = remaining;
                bomber.uPos += (VectorLF3)(bomber.uVel * seconds);
                bomber.rPos = rotation * (Vector3)(bomber.uPos - origin); bomber.rVelObj = rotation * bomber.uVelObj;
                visuals.humpbackProjectiles.buffer[proxy.PoolId] = bomber;
                break;
            case BattleEffectKind.Impact:
                var particle = (ParticleData)proxy.Sample;
                particle.id = proxy.PoolId; particle.time += age;
                if (particle.astroId == 0)
                {
                    particle.pos = rotation * (Vector3)(particle.upos - origin);
                    particle.dir = rotation * particle.dir;
                    particle.vel = rotation * particle.vel;
                }
                visuals.hitEffects[proxy.Subtype].buffer[proxy.PoolId] = particle;
                break;
        }
    }

    public void RemoveSource((ushort Owner, bool World, int Star, int Planet) key)
    {
        if (!sources.TryGetValue(key, out var source)) return;
        foreach (var id in source.Units.Keys.ToArray()) RemoveUnit(source, id);
        foreach (var id in source.Effects.Keys.ToArray()) RemoveEffect(source, id);
        sources.Remove(key);
    }

    private void RemoveUnit(Source source, (int, long) id)
    {
        var proxy = source.Units[id];
        if (proxy.Current.Space) space.RemoveModel(proxy.Current.Model, proxy.ModelId, false);
        else ground.RemoveModel(proxy.Current.Model, proxy.ModelId, false);
        freeAnimationIds.Enqueue(proxy.AnimationId);
        source.Units.Remove(id);
    }

    private void RemoveEffect(Source source, (BattleEffectKind, int, long) id)
    {
        var effect = source.Effects[id];
        switch (effect.Kind)
        {
            case BattleEffectKind.GroundLaser: visuals.fighterLasers.Remove(effect.PoolId); break;
            case BattleEffectKind.GroundPlasma: visuals.fighterPlasmas.Remove(effect.PoolId); break;
            case BattleEffectKind.GroundShieldPlasma: visuals.fighterShieldPlasmas.Remove(effect.PoolId); break;
            case BattleEffectKind.SpaceLaser: visuals.warshipTypeFLasers.Remove(effect.PoolId); break;
            case BattleEffectKind.SpacePlasmaF: visuals.warshipTypeFPlasmas.Remove(effect.PoolId); break;
            case BattleEffectKind.SpacePlasmaA: visuals.warshipTypeAPlasmas.Remove(effect.PoolId); break;
            case BattleEffectKind.TurretMissile:
                var trails = visuals.turretMissileTrails;
                if (trails.smokePool != null && (effect.PoolId + 1) * trails.trailStride <= trails.smokePool.Length)
                    Array.Clear(trails.smokePool, effect.PoolId * trails.trailStride, trails.trailStride);
                visuals.turretMissiles.Remove(effect.PoolId);
                break;
            case BattleEffectKind.TurretPlasma: visuals.turretPlasmas.Remove(effect.PoolId); break;
            case BattleEffectKind.LancerSweep: visuals.lancerLaserSweeps.Remove(effect.PoolId); break;
            case BattleEffectKind.BomberProjectile: visuals.humpbackProjectiles.Remove(effect.PoolId); break;
            case BattleEffectKind.Impact: visuals.hitEffects[effect.Subtype].Remove(effect.PoolId); break;
        }
        source.Effects.Remove(id);
    }

    public void Dispose()
    {
        visuals.Free(); ground.Free(); space.Free(); animationBuffer?.Release();
        animationBuffer = null; sources.Clear();
    }

    private sealed class Source
    {
        public long LastTick;
        public readonly Dictionary<(int, long), UnitProxy> Units = new();
        public readonly Dictionary<(BattleEffectKind, int, long), EffectProxy> Effects = new();
    }
    private sealed class UnitProxy
    {
        public FleetVisualData Previous;
        public FleetVisualData Current;
        public float ReceivedAt;
        public int ModelId;
        public int AnimationId;
    }
    private sealed class EffectProxy
    {
        public BattleEffectKind Kind;
        public int Subtype;
        public int PoolId;
        public object Sample;
        public long SampleTick;
        public int Life;
    }
}
