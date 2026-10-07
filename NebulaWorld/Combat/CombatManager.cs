#region

using System;
using System.Collections.Generic;
using NebulaAPI.DataStructures;
using NebulaModel.DataStructures;
using UnityEngine;
#pragma warning disable IDE1006 // Naming Styles
#pragma warning disable CA1822 // Mark members as static

#endregion

namespace NebulaWorld.Combat;

public class CombatManager : IDisposable
{
    public readonly ToggleSwitch IsIncomingRequest = new();

    public static int PlayerId { get; private set; }
    public static bool SerializeOverwrite { get; set; }

    public struct PlayerPosition
    {
        public ushort id;
        public int planetId;
        public int starId;
        public Vector3 position;
        public VectorLF3 uPosition;
        public bool isAlive;
        public Mecha mecha;
        public Vector3 skillTargetL;
        public VectorLF3 skillTargetULast;
        public VectorLF3 skillTargetU;
    }

    public PlayerPosition[] Players; // include self
    public HashSet<int> ActivedPlanets;
    public HashSet<int> ActivedStars;
    public HashSet<int> ActivedStarsMechaInSpace; // player in the system and not on a planet
    public Dictionary<int, int> IndexByPlayerId;

    private static CombatManager instance;

    public CombatManager()
    {
        Players = new PlayerPosition[2];
        ActivedPlanets = [];
        ActivedStars = [];
        ActivedStarsMechaInSpace = [];
        IndexByPlayerId = [];
        instance = this;
    }

    public void Dispose()
    {
        PlayerId = 1;
        Players = null;
        ActivedPlanets = null;
        ActivedStars = null;
        ActivedStarsMechaInSpace = null;
        IndexByPlayerId = null;
        instance = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Explicit caster context for one host combat execution (DESIGN 7.1, TASKS.md A11).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Vanilla routes several combat paths through this shared <see cref="PlayerId"/> global (see the
    /// <c>PlayerAction_Combat</c> transpilers). The host states, for exactly one execution, whose rules
    /// are running — and restores the previous value even when the execution throws. Construct in a
    /// <c>using</c>, run the vanilla rule for that player, and the previous id is restored on dispose.
    /// Long-lived state stays in the host simulation keyed by persistent owner, never here.
    /// </para>
    /// </remarks>
    public sealed class CombatPlayerScope : IDisposable
    {
        private readonly int previousPlayerId;
        private bool disposed;

        internal CombatPlayerScope(ushort actingPlayerId)
        {
            previousPlayerId = PlayerId;
            ActingPlayerId = actingPlayerId;
            PlayerId = actingPlayerId;
        }

        /// <summary>The player whose rules run inside this scope.</summary>
        public ushort ActingPlayerId { get; }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            PlayerId = previousPlayerId;
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>Opens an explicit caster scope for one host combat execution.</summary>
    public static CombatPlayerScope CombatAs(ushort playerId) => new CombatPlayerScope(playerId);

    public void GameTick()
    {
        if (!Multiplayer.Session.IsGameLoaded) return;
        var gameTick = GameMain.gameTick;
        ActivedPlanets.Clear();
        ActivedStars.Clear();
        ActivedStarsMechaInSpace.Clear();
        IndexByPlayerId.Clear();
        using (Multiplayer.Session.World.GetRemotePlayersModels(out var remotePlayersModels))
        {
            // Mimic SkillSystem.CollectPlayerStates()
            if (Players.Length != (remotePlayersModels.Count + 1))
            {
                Players = new PlayerPosition[remotePlayersModels.Count + 1];
            }
            PlayerId = Multiplayer.Session.LocalPlayer.Id;
            Players[0].id = Multiplayer.Session.LocalPlayer.Id;
            Players[0].planetId = GameMain.localPlanet?.id ?? -1;
            Players[0].starId = GameMain.localStar?.id ?? -1;
            Players[0].position = GameMain.mainPlayer.position;
            Players[0].uPosition = GameMain.mainPlayer.uPosition;
            Players[0].isAlive = GameMain.mainPlayer.isAlive;
            if (Multiplayer.Session.IsDedicated) Players[0].isAlive = false;
            var mecha = GameMain.mainPlayer.mecha;
            Players[0].mecha = mecha;
            Players[0].skillTargetL = mecha.skillTargetLCenter;
            Players[0].skillTargetULast = Players[0].skillTargetU;
            Players[0].skillTargetU = mecha.skillTargetUCenter;

            ActivedPlanets.Add(Players[0].planetId);
            ActivedStars.Add(Players[0].starId);
            if (Players[0].planetId <= 0 && Players[0].starId > 0)
            {
                ActivedStarsMechaInSpace.Add(Players[0].starId);
            }
            IndexByPlayerId[Players[0].id] = 0;

            var localPlanetId = Players[0].planetId;
            var index = 1;
            foreach (var pair in remotePlayersModels)
            {
                var snapshot = pair.Value.Movement.GetLastPosition();
                var planetData = GameMain.galaxy.PlanetById(snapshot.LocalPlanetId);
                var player = pair.Value.PlayerInstance;
                if (planetData != null) // On planet
                {
                    player.uPosition = planetData.uPosition + (VectorLF3)(planetData.runtimeRotation * player.position);
                    player.uRotation = planetData.runtimeRotation * Maths.SphericalRotation(player.position, 0f);
                }
                else // In space
                {
                    player.uPosition = pair.Value.Movement.absolutePosition;
                }
                ref var ptr = ref Players[index];
                ptr.id = pair.Key;
                ptr.planetId = snapshot.LocalPlanetId;
                ptr.starId = pair.Value.Movement.LocalStarId;
                // If the remote player is on the same planet, player.position is more precise
                // Otherwise it has to use the interpolated received position
                ptr.position = ptr.planetId == localPlanetId ? player.position : snapshot.LocalPlanetPosition.ToVector3();
                ptr.uPosition = player.uPosition;
                ptr.isAlive = player.isAlive;

                mecha = pair.Value.MechaInstance;
                ptr.mecha = mecha;
                ptr.skillTargetL = mecha.skillTargetLCenter;
                ptr.skillTargetULast = ptr.skillTargetU;
                ptr.skillTargetU = mecha.skillTargetUCenter;

                ActivedPlanets.Add(ptr.planetId);
                ActivedStars.Add(ptr.starId);
                if (ptr.planetId <= 0 && ptr.starId > 0)
                {
                    ActivedStarsMechaInSpace.Add(ptr.starId);
                }
                IndexByPlayerId[pair.Key] = index++;

                PlayerLifeManager.TickRemote(pair.Value);
            }
        }
    }

    public void OnFactoryLoadFinished(PlanetFactory factory)
    {
        var cursor = factory.defenseSystem.turrets.cursor;
        var buffer = factory.defenseSystem.turrets.buffer;
        for (var id = 1; id < cursor; id++)
        {
            if (buffer[id].id == id)
            {
                //Remove turretLaserContinuous
                buffer[id].projectileId = 0;
            }
        }
    }

    public void OnAstroFactoryUnload()
    {
        //Remove all projectiles
        GameMain.data.spaceSector.skillSystem.SetForNewGame();
    }
}
