using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NebulaModel.DataStructures;
using UnityEngine;

namespace NebulaWorld.Combat;

public sealed class BattleImpactCapture : IDisposable
{
    private readonly ConcurrentDictionary<(int Kind, int Id, long Born), Impact> authoritative = new();
    private readonly ConcurrentDictionary<(int Kind, int Id), long> predicted = new();
    private readonly Dictionary<int, DataPoolRenderer<ParticleData>> filtered = new();

    public int[] Begin(SkillSystem skills)
    {
        if (!ReferenceEquals(skills, GameMain.spaceSector?.skillSystem) || skills.hitEffects == null) return null;
        var snapshot = new int[skills.hitEffects.Length * 2];
        for (var i = 0; i < skills.hitEffects.Length; i++)
        {
            snapshot[i * 2] = skills.hitEffects[i]?.cursor ?? 0;
            snapshot[i * 2 + 1] = skills.hitEffects[i]?.recycleCursor ?? 0;
        }
        return snapshot;
    }

    public void End(SkillSystem skills, int[] snapshot)
    {
        if (snapshot == null) return;
        for (var kind = 0; kind < skills.hitEffects.Length; kind++)
        {
            var pool = skills.hitEffects[kind];
            if (pool == null) continue;
            for (var id = Math.Max(1, snapshot[kind * 2]); id < pool.cursor; id++) Capture(kind, id, pool);
            for (var slot = pool.recycleCursor; slot < snapshot[kind * 2 + 1]; slot++) Capture(kind, pool.recycleIds[slot], pool);
        }
    }

    private void Capture(int kind, int id, DataPoolRenderer<ParticleData> pool)
    {
        if (id <= 0 || id >= pool.cursor || pool.buffer[id].id != id) return;
        var particle = pool.buffer[id];
        var born = GameMain.gameTick - particle.time;
        if (Multiplayer.Session.IsClient) { predicted[(kind, id)] = born; return; }
        if (particle.astroId != 0)
        {
            var planet = GameMain.galaxy.PlanetById(particle.astroId);
            if (planet != null)
            {
                particle.upos = planet.uPosition + (VectorLF3)(planet.runtimeRotation * particle.pos);
                particle.dir = planet.runtimeRotation * particle.dir;
                particle.vel = planet.runtimeRotation * particle.vel;
            }
            else GameMain.spaceSector.TransformFromAstro(particle.astroId, out particle.upos, particle.pos);
        }
        var nearestStar = -1;
        var distance = double.MaxValue;
        foreach (var star in GameMain.galaxy.stars)
        {
            var delta = star.uPosition - particle.upos;
            var sqr = delta.x * delta.x + delta.y * delta.y + delta.z * delta.z;
            if (sqr < distance) { distance = sqr; nearestStar = star.id; }
        }
        particle.astroId = 0;
        authoritative.TryAdd((kind, id, born), new Impact { Star = nearestStar, Born = born, Particle = particle });
    }

    public void Append(int star, BattleVisualFrame frame)
    {
        var tick = GameMain.gameTick;
        foreach (var entry in authoritative)
        {
            var impact = entry.Value;
            var life = impact.Particle.duration - (int)(tick - impact.Born);
            if (life <= 0) { authoritative.TryRemove(entry.Key, out _); continue; }
            if (impact.Star != star || frame.Effects.Count >= BattleVisualFrame.MaxEffects) continue;
            var particle = impact.Particle;
            particle.time = (int)(tick - impact.Born);
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            particle.Export(writer);
            frame.Effects.Add(new BattleEffectData
            {
                Kind = BattleEffectKind.Impact,
                Id = particle.id,
                Subtype = entry.Key.Kind,
                Generation = impact.Born * 128 + entry.Key.Kind,
                Life = life,
                Payload = stream.ToArray()
            });
        }
    }

    public bool RenderNative(DataPoolRenderer<ParticleData> pool)
    {
        if (predicted.IsEmpty) return false;
        var pools = GameMain.spaceSector?.skillSystem?.hitEffects;
        if (pools == null) return false;
        var kind = Array.IndexOf(pools, pool);
        if (kind < 0) return false; // The independent replica renderer is never filtered.
        if (!filtered.TryGetValue(kind, out var copy))
        {
            copy = (DataPoolRenderer<ParticleData>)Activator.CreateInstance(pool.GetType());
            copy.InitRenderer(Configs.combat.skillHitDescs[kind]);
            copy.ResetPool();
            filtered[kind] = copy;
        }
        copy.SetCapacity(pool.capacity);
        Array.Copy(pool.buffer, copy.buffer, pool.cursor);
        copy.cursor = pool.cursor;
        copy.recycleCursor = pool.recycleCursor;
        for (var id = 1; id < pool.cursor; id++)
        {
            if (!predicted.TryGetValue((kind, id), out var born)) continue;
            var particle = pool.buffer[id];
            if (particle.id == id && Math.Abs(GameMain.gameTick - particle.time - born) <= 1) copy.buffer[id].id = 0;
            else predicted.TryRemove((kind, id), out _);
        }
        copy.Render(); // A separate buffer, so native simulation is never changed by rendering.
        return true;
    }

    public void Dispose()
    {
        foreach (var pool in filtered.Values) pool.FreeRenderer();
        filtered.Clear(); predicted.Clear(); authoritative.Clear();
    }

    private sealed class Impact
    {
        public int Star;
        public long Born;
        public ParticleData Particle;
    }
}
