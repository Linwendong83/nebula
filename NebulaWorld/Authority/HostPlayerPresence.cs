#region

using NebulaAPI.GameState;
using NebulaModel.Authority;
using NebulaModel.DataStructures;
using NebulaWorld;

#endregion

namespace NebulaWorld.Authority;

/// <summary>
/// A22 production bridge: feeds the host's player registry from the real join/movement events.
/// </summary>
/// <remarks>
/// <para>
/// The A09 registry and the A20 subscription policy were both correct, but nothing in production
/// ever called <c>RegisterOrUpdate</c> / <c>UpdatePresence</c>: the registry stayed empty, so the
/// host refused every planet-scope subscribe and no client ever received a mirror. This bridge is
/// the missing production writer.
/// </para>
/// <para>
/// Fail-closed and host-only: every entry point returns without writing unless the session is a
/// live host authority world. A legacy room or single-player never touches the registry. Identity
/// comes from the connection (the session id), and the persistent id comes from the host-accepted
/// player data, never from a packet claim.
/// </para>
/// </remarks>
public static class HostPlayerPresence
{
    /// <summary>
    /// Registers a joining player and seeds their location from the data the host accepted.
    /// </summary>
    public static bool TryRegister(IPlayerData data, ushort sessionPlayerId)
    {
        var runtime = Multiplayer.Session?.AuthorityRuntime;
        if (runtime?.IsHostAuthority != true) return false;
        var registry = runtime.HostPlayers;
        if (registry == null) return false;
        var persistentId = (data as PlayerData)?.PersistentId;
        if (string.IsNullOrEmpty(persistentId)) return false;

        var connection = runtime.ConnectionEpochFor(sessionPlayerId);
        if (!connection.IsValid) return false;

        registry.RegisterOrUpdate(persistentId, sessionPlayerId, HostPlayerRole.Remote, connection);
        TryUpdateLocation(data, sessionPlayerId);
        return true;
    }

    /// <summary>
    /// Refreshes the location of an already-registered player. No-op when the seat is unknown.
    /// </summary>
    public static bool TryUpdateLocation(IPlayerData data, ushort sessionPlayerId)
    {
        var runtime = Multiplayer.Session?.AuthorityRuntime;
        if (runtime?.IsHostAuthority != true) return false;
        var registry = runtime.HostPlayers;
        if (registry == null || data == null) return false;

        var planetId = data.LocalPlanetId;
        if (planetId <= 0) return false;
        var starId = data.LocalStarId > 0 ? data.LocalStarId : planetId / 100;

        return registry.UpdatePresenceLocation(sessionPlayerId, planetId, starId, isAlive: true);
    }
}
