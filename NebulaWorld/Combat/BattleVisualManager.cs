using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NebulaAPI.Networking;
using NebulaModel.DataStructures;
using NebulaModel.Packets.Combat;

namespace NebulaWorld.Combat;

public sealed class BattleVisualManager : IDisposable
{
    private readonly Dictionary<(ushort Owner, bool World, int Star, int Planet), CachedFrame> frames = new();
    private readonly Dictionary<(int Astro, int Id), long> craftGenerations = new();
    private readonly HashSet<(bool World, int Scope, BattleEffectKind Kind, int Id, long Generation)> emitted = new();
    private readonly Dictionary<int, HashSet<(int Id, long Generation)>> previousUnits = new();
    private readonly Dictionary<(bool World, int Scope, BattleEffectKind Kind, int Id), (long Generation, long Tick, int Life)> effectGenerations = new();
    private long nextGeneration = DateTime.UtcNow.Ticks;
    private long nextSequence;
    private long lastTick = -1;
    private BattleVisualRenderer renderer;
    private bool captureWorld;
    private bool captureFull;
    private int captureStar;

    public void CraftCreated(int astro, int id)
    {
        if (id > 0) craftGenerations[(astro, id)] = ++nextGeneration;
    }

    public void GameTick()
    {
        if (!Multiplayer.Session.IsGameLoaded || !GameMain.data.gameDesc.isCombatMode) return;
        var tick = GameMain.gameTick;
        if (lastTick == tick) return;
        lastTick = tick;
        var full = tick % 6 == 0;
        if (!Multiplayer.Session.IsDedicated) CapturePlayer(full);
        if (Multiplayer.Session.IsServer) CaptureWorld(full);
        foreach (var cached in frames.Values)
            foreach (var effect in cached.Effects.Where(x => x.Value.Tick + x.Value.Effect.Life <= tick).Select(x => x.Key).ToArray())
                cached.Effects.Remove(effect);
        foreach (var key in frames.Where(x => tick - x.Value.Tick > 180).Select(x => x.Key).ToArray())
        {
            renderer?.RemoveSource(key);
            frames.Remove(key);
        }
        if (tick % 600 == 0)
        {
            emitted.Clear();
            foreach (var key in effectGenerations.Where(x => tick - x.Value.Tick > 120).Select(x => x.Key).ToArray())
                effectGenerations.Remove(key);
        }
    }

    private void CapturePlayer(bool full)
    {
        captureWorld = false;
        captureFull = full;
        var star = GameMain.localStar?.id ?? -1;
        if (star < 0) return;
        var mecha = GameMain.mainPlayer.mecha;
        var ground = new BattleVisualFrame();
        var space = new BattleVisualFrame();
        var groundIds = new HashSet<int>();
        var spaceIds = new HashSet<int>();
        var planet = GameMain.localPlanet;
        if (planet?.factory != null)
            CollectUnits(mecha.groundCombatModule, planet.factory.craftPool, planet.factory.craftAnimPool,
                planet.id, false, ground, groundIds);
        CollectUnits(mecha.spaceCombatModule, GameMain.spaceSector.craftPool, GameMain.spaceSector.craftAnimPool,
            0, true, space, spaceIds);
        var skills = GameMain.spaceSector.skillSystem;
        Collect(skills.fighterLasers.buffer, skills.fighterLasers.cursor, BattleEffectKind.GroundLaser, ground,
            x => x.id, x => x.life, x => x.caster.type == ETargetType.Craft && groundIds.Contains(x.caster.id), (x, w) => x.Export(w));
        Collect(skills.fighterPlasmas.buffer, skills.fighterPlasmas.cursor, BattleEffectKind.GroundPlasma, ground,
            x => x.id, x => x.life, x => x.caster.type == ETargetType.Craft && groundIds.Contains(x.caster.id), (x, w) => x.Export(w));
        Collect(skills.fighterShieldPlasmas.buffer, skills.fighterShieldPlasmas.cursor, BattleEffectKind.GroundShieldPlasma, ground,
            x => x.id, x => x.life, x => x.caster.type == ETargetType.Craft && groundIds.Contains(x.caster.id), (x, w) => x.Export(w));
        Collect(skills.warshipTypeFLasers.buffer, skills.warshipTypeFLasers.cursor, BattleEffectKind.SpaceLaser, space,
            x => x.id, x => x.life, x => x.caster.type == ETargetType.Craft && spaceIds.Contains(x.caster.id), (x, w) => x.Export(w));
        Collect(skills.warshipTypeFPlasmas.buffer, skills.warshipTypeFPlasmas.cursor, BattleEffectKind.SpacePlasmaF, space,
            x => x.id, x => x.life, x => x.caster.type == ETargetType.Craft && spaceIds.Contains(x.caster.id), (x, w) => x.Export(w));
        Collect(skills.warshipTypeAPlasmas.buffer, skills.warshipTypeAPlasmas.cursor, BattleEffectKind.SpacePlasmaA, space,
            x => x.id, x => x.life, x => x.caster.type == ETargetType.Craft && spaceIds.Contains(x.caster.id), (x, w) => x.Export(w));
        SendCaptured(space, false, star, 0, full);
        if (planet != null) SendCaptured(ground, false, star, planet.id, full);
    }

    private void CollectUnits(CombatModuleComponent module, CraftData[] crafts, AnimData[] animations, int scope,
        bool space, BattleVisualFrame frame, HashSet<int> ids)
    {
        if (module?.moduleFleets == null || crafts == null) return;
        foreach (var fleet in module.moduleFleets)
        {
            if (fleet.fighters == null) continue;
            foreach (var fighter in fleet.fighters)
            {
                var id = fighter.craftId;
                if (id <= 0 || id >= crafts.Length || crafts[id].id != id || !ids.Add(id)) continue;
                if (!craftGenerations.TryGetValue((scope, id), out var generation))
                    craftGenerations[(scope, id)] = generation = ++nextGeneration;
                var craft = crafts[id];
                frame.Units.Add(new FleetVisualData
                {
                    Id = id,
                    Generation = generation,
                    Model = craft.modelIndex,
                    Astro = craft.astroId,
                    Space = space,
                    Position = craft.pos,
                    Rotation = craft.rot,
                    Animation = animations[id]
                });
            }
        }
    }

    private void CaptureWorld(bool full)
    {
        captureWorld = true;
        captureFull = full;
        var stars = new HashSet<int>();
        if (!Multiplayer.Session.IsDedicated && GameMain.localStar != null) stars.Add(GameMain.localStar.id);
        foreach (var player in Multiplayer.Session.Server.Players.Connected.Values)
            if (player.Data.LocalStarId > 0) stars.Add(player.Data.LocalStarId);
        var skills = GameMain.spaceSector.skillSystem;
        foreach (var star in stars)
        {
            captureStar = star;
            var frame = new BattleVisualFrame();
            Collect(skills.turretMissiles.buffer, skills.turretMissiles.cursor, BattleEffectKind.TurretMissile, frame,
                x => x.id, x => x.life, x => StarOf(x.caster.astroId) == star, (x, w) => x.Export(w));
            Collect(skills.turretPlasmas.buffer, skills.turretPlasmas.cursor, BattleEffectKind.TurretPlasma, frame,
                x => x.id, x => x.life, x => StarOf(x.caster.astroId) == star, (x, w) => x.Export(w));
            Collect(skills.lancerLaserSweeps.buffer, skills.lancerLaserSweeps.cursor, BattleEffectKind.LancerSweep, frame,
                x => x.id, x => x.life, x => StarOf(x.caster.astroId) == star,
                (x, w) => { x.Export(w); w.Write(x.endPos.x); w.Write(x.endPos.y); w.Write(x.endPos.z); });
            Collect(skills.humpbackProjectiles.buffer, skills.humpbackProjectiles.cursor, BattleEffectKind.BomberProjectile, frame,
                x => x.id, x => x.life, x => StarOf(x.caster.astroId) == star, (x, w) => x.Export(w));
            Multiplayer.Session.Impacts.Append(star, frame);
            SendCaptured(frame, true, star, 0, full);
        }
    }

    private static int StarOf(int astro)
    {
        if (astro > 1000000) return GameMain.spaceSector.GetHiveByAstroId(astro)?.starData.id ?? -1;
        return astro > 0 ? astro / 100 : -1;
    }

    private void Collect<T>(T[] buffer, int cursor, BattleEffectKind kind, BattleVisualFrame frame,
        Func<T, int> id, Func<T, int> life, Func<T, bool> include, Action<T, BinaryWriter> export) where T : struct
    {
        if (buffer == null) return;
        for (var i = 1; i < cursor && frame.Effects.Count < BattleVisualFrame.MaxEffects; i++)
        {
            var item = buffer[i];
            var nativeLife = life(item);
            var remaining = kind == BattleEffectKind.TurretMissile ? (nativeLife > 0 ? 3600 - nativeLife : -nativeLife) : nativeLife;
            if (id(item) != i || remaining <= 0 || !include(item)) continue;
            var scope = captureWorld ? captureStar : kind < BattleEffectKind.SpaceLaser ? GameMain.localPlanet?.id ?? 0 : 0;
            var key = (captureWorld, scope, kind, i);
            var tick = GameMain.gameTick;
            var fresh = !effectGenerations.TryGetValue(key, out var previous) || previous.Tick < tick - 1 ||
                (kind == BattleEffectKind.TurretMissile ? nativeLife > 0 && (previous.Life <= 0 || nativeLife < previous.Life) : nativeLife > previous.Life);
            var generation = fresh ? ++nextGeneration : previous.Generation;
            effectGenerations[key] = (generation, tick, nativeLife);
            if (!captureFull && emitted.Contains((captureWorld, scope, kind, i, generation))) continue;
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            export(item, writer);
            frame.Effects.Add(new BattleEffectData
            {
                Kind = kind,
                Id = i,
                Generation = generation,
                Life = remaining,
                Payload = stream.ToArray()
            });
        }
    }

    private void SendCaptured(BattleVisualFrame frame, bool world, int star, int planet, bool full)
    {
        var owner = world ? (ushort)0 : Multiplayer.Session.LocalPlayer.Id;
        var units = new HashSet<(int, long)>(frame.Units.Select(x => (x.Id, x.Generation)));
        var unitChanged = !world && (!previousUnits.TryGetValue(planet, out var prior) || !units.SetEquals(prior));
        if (!world) previousUnits[planet] = units;
        var sourceScope = world ? star : planet;
        if (!full)
        {
            frame.Effects.RemoveAll(x => !emitted.Add((world, sourceScope, x.Kind, x.Id, x.Generation)));
            if (!unitChanged) frame.Units.Clear();
            if (!unitChanged && frame.Effects.Count == 0) return;
        }
        else foreach (var effect in frame.Effects) emitted.Add((world, sourceScope, effect.Kind, effect.Id, effect.Generation));
        var packet = new BattleVisualPacket
        {
            Owner = owner,
            WorldEffects = world,
            StarId = star,
            PlanetId = planet,
            Tick = GameMain.gameTick,
            Sequence = ++nextSequence,
            UnitsFull = !world && (full || unitChanged),
            EffectsFull = full,
            Data = frame.Export()
        };
        if (Multiplayer.Session.IsServer) Relay(packet, frame);
        else Multiplayer.Session.Network.SendPacket(packet);
    }

    public void Relay(BattleVisualPacket packet, BattleVisualFrame frame)
    {
        if (!packet.WorldEffects)
            foreach (var old in frames.Keys.Where(x => !x.World && x.Owner == packet.Owner && x.Star != packet.StarId).ToArray())
                ClearSource(old);
        Receive(packet, frame, false);
        foreach (var player in Multiplayer.Session.Server.Players.Connected.Values)
        {
            if (player.Id == packet.Owner || player.Data.LocalStarId != packet.StarId ||
                (packet.PlanetId > 0 && player.Data.LocalPlanetId != packet.PlanetId)) continue;
            player.SendPacket(packet);
        }
        if (!Multiplayer.Session.IsDedicated && !packet.WorldEffects && packet.Owner != Multiplayer.Session.LocalPlayer.Id)
            Receive(packet, frame, true);
    }

    public void Receive(BattleVisualPacket packet, BattleVisualFrame frame, bool display = true)
    {
        var key = (packet.Owner, packet.WorldEffects, packet.StarId, packet.PlanetId);
        if (packet.Clear)
        {
            renderer?.RemoveSource(key);
            frames.Remove(key);
            return;
        }
        if (!frames.TryGetValue(key, out var cached)) frames.Add(key, cached = new CachedFrame());
        if (packet.Sequence < cached.Sequence || (packet.Sequence == cached.Sequence && !display)) return;
        cached.Sequence = packet.Sequence;
        cached.Tick = GameMain.gameTick;
        if (packet.UnitsFull) cached.Units = frame.Units;
        if (packet.EffectsFull) cached.Effects.Clear();
        foreach (var effect in frame.Effects)
            cached.Effects[(effect.Kind, effect.Id, effect.Generation)] = (packet.Tick, effect);
        if (cached.Effects.Count > BattleVisualFrame.MaxEffects)
            foreach (var id in cached.Effects.OrderBy(x => x.Value.Tick).Take(cached.Effects.Count - BattleVisualFrame.MaxEffects).Select(x => x.Key).ToArray())
                cached.Effects.Remove(id);
        if (!display || Multiplayer.Session.IsDedicated || packet.Owner == Multiplayer.Session.LocalPlayer.Id && !packet.WorldEffects) return;
        if (packet.StarId != GameMain.localStar?.id || (packet.PlanetId > 0 && packet.PlanetId != GameMain.localPlanet?.id)) return;
        renderer ??= new BattleVisualRenderer();
        renderer.Receive(key, packet, frame);
    }

    public void SendInitial(INebulaConnection connection, int star, int planet)
    {
        foreach (var entry in frames)
        {
            if (entry.Key.Star != star || (entry.Key.Planet != 0 && entry.Key.Planet != planet)) continue;
            var frame = new BattleVisualFrame(); frame.Units.AddRange(entry.Value.Units);
            foreach (var value in entry.Value.Effects.Values)
            {
                if (value.Tick + value.Effect.Life <= GameMain.gameTick) continue;
                // Preserve the original frame time by sending each living effect separately.
                var effectFrame = new BattleVisualFrame(); effectFrame.Effects.Add(value.Effect);
                connection.SendPacket(new BattleVisualPacket
                {
                    Owner = entry.Key.Owner,
                    WorldEffects = entry.Key.World,
                    StarId = star,
                    PlanetId = entry.Key.Planet,
                    Sequence = entry.Value.Sequence,
                    Tick = value.Tick,
                    Data = effectFrame.Export()
                });
            }
            connection.SendPacket(new BattleVisualPacket
            {
                Owner = entry.Key.Owner,
                WorldEffects = entry.Key.World,
                StarId = star,
                PlanetId = entry.Key.Planet,
                Sequence = entry.Value.Sequence,
                Tick = GameMain.gameTick,
                UnitsFull = true,
                Data = frame.Export()
            });
        }
    }

    public void Draw() => renderer?.Draw();

    public void RemoveOwner(ushort owner)
    {
        foreach (var key in frames.Keys.Where(x => x.Owner == owner && !x.World).ToArray())
        {
            renderer?.RemoveSource(key);
            frames.Remove(key);
        }
    }

    public void LeavePlanet(ushort owner, int planet)
    {
        foreach (var key in frames.Keys.Where(x => !x.World && x.Owner == owner && x.Planet == planet && planet > 0).ToArray())
            ClearSource(key);
    }

    private void ClearSource((ushort Owner, bool World, int Star, int Planet) key)
    {
        renderer?.RemoveSource(key);
        frames.Remove(key);
        if (Multiplayer.Session.IsServer)
            Multiplayer.Session.Server.SendPacket(new BattleVisualPacket
            {
                Owner = key.Owner,
                WorldEffects = key.World,
                StarId = key.Star,
                PlanetId = key.Planet,
                Sequence = ++nextSequence,
                Tick = GameMain.gameTick,
                Clear = true,
                Data = new BattleVisualFrame().Export()
            });
    }

    public void Dispose()
    {
        renderer?.Dispose(); renderer = null;
        frames.Clear(); craftGenerations.Clear(); emitted.Clear(); previousUnits.Clear(); effectGenerations.Clear();
    }

    private sealed class CachedFrame
    {
        public long Sequence;
        public long Tick;
        public List<FleetVisualData> Units = new();
        public readonly Dictionary<(BattleEffectKind Kind, int Id, long Generation), (long Tick, BattleEffectData Effect)> Effects = new();
    }
}
