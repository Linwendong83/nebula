using System;
using System.Collections.Generic;
using System.Threading;
using NebulaWorld;

namespace AuthorityBaseline;

/// <summary>
/// Ambient diagnostic state shared by the logger, the hooks and the scenario driver.
/// Read-only with respect to game values: nothing here writes hp, counts, resources or pools.
/// </summary>
internal static class BaselineRuntime
{
    public static string RunId = "unset";
    public static string Role = "unset";
    public static string LogDirectory = ".";
    public static BaselineLog Log;
    public static bool Verbose;
    public static int WriteFailures;
    public static int HookFailures;
    public static readonly List<string> HookReport = new();
    public static readonly List<TrackedTarget> Tracked = new();
    public static readonly List<EnemySweepEntry> EnemySweep = new();

    public static bool MultiplayerActive
    {
        get
        {
            try { return Multiplayer.IsActive && Multiplayer.Session != null; }
            catch (Exception) { return false; }
        }
    }

    public static bool IsServer
    {
        get
        {
            try { return MultiplayerActive && Multiplayer.Session.IsServer; }
            catch (Exception) { return false; }
        }
    }

    public static bool IsClient
    {
        get
        {
            try { return MultiplayerActive && Multiplayer.Session.IsClient; }
            catch (Exception) { return false; }
        }
    }

    public static long GameTick()
    {
        try { return GameMain.gameTick; }
        catch (Exception) { return -1; }
    }

    public static int PlayerCount()
    {
        try
        {
            if (!MultiplayerActive) return -1;
            return IsServer
                ? Multiplayer.Session.Server.Players.Connected.Count
                : Multiplayer.Session.NumPlayers;
        }
        catch (Exception) { return -1; }
    }

    public static TrackedTarget FindTracked(string kind, int planetId, int nativeId)
    {
        lock (Tracked)
        {
            foreach (var target in Tracked)
            {
                if (target.Kind == kind && target.PlanetId == planetId && target.NativeId == nativeId)
                    return target;
            }
        }
        return null;
    }

    public static void Track(TrackedTarget target)
    {
        lock (Tracked)
        {
            if (FindTracked(target.Kind, target.PlanetId, target.NativeId) == null) Tracked.Add(target);
        }
    }

    public static TrackedTarget[] TrackedSnapshot()
    {
        lock (Tracked) return Tracked.ToArray();
    }

    public static void Reset()
    {
        lock (Tracked) Tracked.Clear();
        lock (EnemySweep) EnemySweep.Clear();
    }
}

/// <summary>
/// An object under observation. Kind is "enemy" or "building"; NativeId is the host-side pool id
/// (enemy id / entity id), which is what the wire currently carries. Generation and AuthorityEpoch
/// do not exist as protocol identity yet (A02), so they are recorded as raw pool facts only.
/// </summary>
internal sealed class TrackedTarget
{
    public string Kind;
    public int PlanetId;
    public int NativeId;
    public string Label;

    public bool HasSample;
    public bool LastHasCombatStat;
    public bool LastHasConstructStat;
    public int LastHp = int.MinValue;
    public int LastRepairer = int.MinValue;
    public int LastCombatStatId = -1;
    public int LastConstructStatId = -1;
    public string LastOwner;
    public long LastGeneration = long.MinValue;
}

/// <summary>Per-enemy sweep row used by the "same enemy under sustained attack" scenario.</summary>
internal sealed class EnemySweepEntry
{
    public int PlanetId;
    public int EnemyId;
    public long FirstTick;
    public long LastTick;
    public int Samples;
    public int MinHp = int.MaxValue;
    public int MaxHp = int.MinValue;
    public int LastHp = int.MinValue;
    public int HpIncreases;
    public int ZeroHpSamples;
    public int Deaths;
}
