using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using NebulaAPI.Networking;
using NebulaModel.DataStructures;
using NebulaModel.Packets.Combat;

namespace NebulaWorld.Combat;

public sealed class BattleVisualManager : IDisposable
{
    private readonly Dictionary<(ushort Owner, bool World, int Star, int Planet), CachedFrame> frames = new();
    private readonly HashSet<(bool World, int Scope, BattleEffectKind Kind, int Id, long Generation)> emitted = new();
    private readonly Dictionary<(bool World, int Scope, BattleEffectKind Kind, int Id), (long Generation, long Tick, int Life)> effectGenerations = new();
    private long nextSequence;
    private bool captureWorld;
    private bool captureFull;
    private int captureStar;
    private long nextGeneration = DateTime.UtcNow.Ticks;
    private long lastTick = -1;
    private BattleVisualRenderer renderer;

    public void GameTick()
    {
        if (!Multiplayer.Session.IsGameLoaded || !GameMain.data.gameDesc.isCombatMode) return;
        var tick = GameMain.gameTick;
        if (lastTick == tick) return;
        lastTick = tick;
        if (Multiplayer.Session.IsServer) CaptureWorld(tick % 6 == 0);
        if (tick % 600 == 0)
        {
            emitted.Clear();
            foreach (var key in effectGenerations.Where(x => tick - x.Value.Tick > 120).Select(x => x.Key).ToArray())
                effectGenerations.Remove(key);
        }
        foreach (var cached in frames.Values)
            foreach (var effect in cached.Effects.Where(x => x.Value.Tick + x.Value.Effect.Life <= tick).Select(x => x.Key).ToArray())
                cached.Effects.Remove(effect);
        foreach (var key in frames.Where(x => tick - x.Value.Tick > 180).Select(x => x.Key).ToArray())
        {
            renderer?.RemoveSource(key);
            frames.Remove(key);
        }
    }

    private void CaptureWorld(bool full)
    {
        // Shared world attacks originate on the host. Owner-side fleet visuals stay native.
        captureWorld = true;
        captureFull = full;
        var stars = new HashSet<int>();
        foreach (var player in Multiplayer.Session.Server.Players.Connected.Values)
            if (player.Data.LocalStarId > 0) stars.Add(player.Data.LocalStarId);
        var skills = GameMain.spaceSector?.skillSystem;
        if (skills == null) return;
        foreach (var star in stars)
        {
            captureStar = star;
            var frame = new BattleVisualFrame();
            Collect(skills.mechaLocalLaserOneShots.buffer, skills.mechaLocalLaserOneShots.cursor, BattleEffectKind.MechaGroundLaser, frame,
                x => x.id, x => x.life, x => PlayerStar(x.caster.id) == star, (x, w) => x.Export(w));
            Collect(skills.mechaLocalGaussProjectiles.buffer, skills.mechaLocalGaussProjectiles.cursor, BattleEffectKind.MechaGroundGauss, frame,
                x => x.id, x => x.life, x => PlayerStar(x.caster.id) == star, (x, w) => x.Export(w));
            Collect(skills.mechaSpaceLaserOneShots.buffer, skills.mechaSpaceLaserOneShots.cursor, BattleEffectKind.MechaSpaceLaser, frame,
                x => x.id, x => x.life, x => PlayerStar(x.caster.id) == star, (x, w) => x.Export(w));
            Collect(skills.mechaSpaceGaussProjectiles.buffer, skills.mechaSpaceGaussProjectiles.cursor, BattleEffectKind.MechaSpaceGauss, frame,
                x => x.id, x => x.life, x => PlayerStar(x.caster.id) == star, (x, w) => x.Export(w));
            Collect(skills.mechaPlasmas.buffer, skills.mechaPlasmas.cursor, BattleEffectKind.MechaPlasma, frame,
                x => x.id, x => x.life, x => PlayerStar(x.caster.id) == star, (x, w) => x.Export(w));
            Collect(skills.mechaMissiles.buffer, skills.mechaMissiles.cursor, BattleEffectKind.MechaMissile, frame,
                x => x.id, x => x.life, x => PlayerStar(x.caster.id) == star, (x, w) => x.Export(w));
            Collect(skills.mechaLocalCannonades.buffer, skills.mechaLocalCannonades.cursor, BattleEffectKind.MechaLocalCannon, frame,
                x => x.id, x => x.life, x => PlayerStar(x.caster.id) == star, (x, w) => x.Export(w));
            Collect(skills.mechaSpaceCannonades.buffer, skills.mechaSpaceCannonades.cursor, BattleEffectKind.MechaSpaceCannon, frame,
                x => x.id, x => x.life, x => PlayerStar(x.caster.id) == star, (x, w) => x.Export(w));
            Collect(skills.mechaShieldBursts.buffer, skills.mechaShieldBursts.cursor, BattleEffectKind.MechaShieldBurst, frame,
                x => x.id, x => x.life, x => PlayerStar(x.caster.id) == star, (x, w) => x.Export(w));
            Collect(skills.explosiveUnitBombs.buffer, skills.explosiveUnitBombs.cursor, BattleEffectKind.ExplosiveBomb, frame,
                x => x.id, x => x.life, x => PlayerStar(x.caster.id) == star, (x, w) => x.Export(w));
            Collect(skills.liquidBombs.buffer, skills.liquidBombs.cursor, BattleEffectKind.LiquidBomb, frame,
                x => x.id, x => x.life, x => PlayerStar(x.caster.id) == star, (x, w) => x.Export(w));
            Collect(skills.emCapsuleBombs.buffer, skills.emCapsuleBombs.cursor, BattleEffectKind.EMBomb, frame,
                x => x.id, x => x.life, x => PlayerStar(x.caster.id) == star, (x, w) => x.Export(w));
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
            SendCaptured(frame, star, full);
        }
    }

    private static int StarOf(int astro)
    {
        return ResolveStarOf(astro);
    }

    private static int PlayerStar(int id)
    {
        var player = Multiplayer.Session.Server.Players.Get((ushort)id);
        return player?.Data.LocalStarId ?? (id == Multiplayer.Session.LocalPlayer.Id ? GameMain.localStar?.id ?? -1 : -1);
    }

    private static int ResolveStarOf(int astro)
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
            var missile = kind == BattleEffectKind.TurretMissile || kind == BattleEffectKind.MechaMissile;
            var remaining = missile ? (nativeLife > 0 ? 3600 - nativeLife : -nativeLife) : nativeLife;
            if (id(item) != i || remaining <= 0 || !include(item)) continue;
            var scope = captureStar;
            var key = (captureWorld, scope, kind, i);
            var tick = GameMain.gameTick;
            var fresh = !effectGenerations.TryGetValue(key, out var previous) || previous.Tick < tick - 1 ||
                (missile ? nativeLife > 0 && (previous.Life <= 0 || nativeLife < previous.Life) : nativeLife > previous.Life);
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

    private void SendCaptured(BattleVisualFrame frame, int star, bool full)
    {
        if (!full)
        {
            frame.Effects.RemoveAll(x => !emitted.Add((true, star, x.Kind, x.Id, x.Generation)));
            if (frame.Effects.Count == 0) return;
        }
        else foreach (var effect in frame.Effects) emitted.Add((true, star, effect.Kind, effect.Id, effect.Generation));
        var packet = new BattleVisualPacket
        {
            Owner = 0,
            WorldEffects = true,
            StarId = star,
            PlanetId = 0,
            Tick = GameMain.gameTick,
            Sequence = ++nextSequence,
            EffectsFull = full,
            Data = frame.Export()
        };
        Receive(packet, frame, display: false);
        foreach (var player in Multiplayer.Session.Server.Players.Connected.Values)
            if (player.Data.LocalStarId == star) player.SendPacket(packet);
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
        if (renderer == null && cached.Units.Count == 0 && cached.Effects.Count == 0) return;
        renderer ??= new BattleVisualRenderer();
        renderer.Receive(key, packet, frame);
    }

    public void SendInitial(INebulaConnection connection, int star, int planet)
    {
        foreach (var entry in frames)
        {
            if (entry.Key.Star != star || (entry.Key.Planet != 0 && entry.Key.Planet != planet)) continue;
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
    }

    public void Dispose()
    {
        renderer?.Dispose(); renderer = null;
        frames.Clear(); emitted.Clear(); effectGenerations.Clear();
    }

    private sealed class CachedFrame
    {
        public long Sequence;
        public long Tick;
        public List<FleetVisualData> Units = new();
        public readonly Dictionary<(BattleEffectKind Kind, int Id, long Generation), (long Tick, BattleEffectData Effect)> Effects = new();
    }
}
