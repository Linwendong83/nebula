using System;
using System.Collections.Generic;
using System.Linq;
using NebulaAPI.Networking;
using NebulaModel.DataStructures;
using NebulaModel.Packets.Combat;

namespace NebulaWorld.Combat;

public sealed class BattleVisualManager : IDisposable
{
    private readonly Dictionary<(ushort Owner, bool World, int Star, int Planet), CachedFrame> frames = new();
    private readonly Dictionary<(int Astro, int Id), long> craftGenerations = new();
    private long nextGeneration = DateTime.UtcNow.Ticks;
    private long lastTick = -1;
    private BattleVisualRenderer renderer;

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
        // Retired: the legacy capture (each peer reporting its own effects, the server relaying
        // world effects) is gone. Host-authored effects are the only source. The expiry below only
        // drains entries others still insert through Receive.
        foreach (var cached in frames.Values)
            foreach (var effect in cached.Effects.Where(x => x.Value.Tick + x.Value.Effect.Life <= tick).Select(x => x.Key).ToArray())
                cached.Effects.Remove(effect);
        foreach (var key in frames.Where(x => tick - x.Value.Tick > 180).Select(x => x.Key).ToArray())
        {
            renderer?.RemoveSource(key);
            frames.Remove(key);
        }
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
        // Retired: no capture feeds frames anymore, so there is nothing to seed late joiners with.
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
        frames.Clear(); craftGenerations.Clear();
    }

    private sealed class CachedFrame
    {
        public long Sequence;
        public long Tick;
        public List<FleetVisualData> Units = new();
        public readonly Dictionary<(BattleEffectKind Kind, int Id, long Generation), (long Tick, BattleEffectData Effect)> Effects = new();
    }
}
