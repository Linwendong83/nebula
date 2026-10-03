#region

using System;
using System.Collections.Generic;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// Which seat a host-tracked player occupies (TASKS.md A09).
/// </summary>
/// <remarks>
/// <para>
/// The host and every remote client run the same rule path; only the virtual server seat is
/// special. A dedicated headless server has no mecha, no combat presence and no construction
/// module, so it must never become a combat or drone-task owner. That is the executable form of
/// "虚拟服务器角色不参与维修/战斗".
/// </para>
/// <para>
/// The role never changes what the ledger does: the ledger is role-agnostic on purpose, so a
/// remote can never be handed an "unlimited" branch the host does not also take. Callers check
/// <see cref="HostPlayerState.CanOwnCombat"/> / <see cref="HostPlayerState.CanOwnDroneTask"/>
/// before submitting work for the player; the ledger itself only sees the persistent owner.
/// </para>
/// </remarks>
public enum HostPlayerRole : byte
{
    Unknown = 0,

    /// <summary>The hosting player itself: plays and hosts.</summary>
    LocalHost = 1,

    /// <summary>A connected client player. Same rules as <see cref="LocalHost"/>.</summary>
    Remote = 2,

    /// <summary>
    /// A headless dedicated server with no mecha. Never a combat or repair owner.
    /// </summary>
    HeadlessDedicated = 3
}

/// <summary>
/// The host-accepted pose of one player, in primitives only.
/// </summary>
/// <remarks>
/// This is the host's accepted position, not any client's interpolated render pose. Combat hit
/// tests (A11) use this pose; the display layer keeps its own buffered copy (DESIGN 10).
/// </remarks>
public readonly struct HostPlayerPose : IEquatable<HostPlayerPose>
{
    public HostPlayerPose(float x, float y, float z, float velocity)
    {
        X = x;
        Y = y;
        Z = z;
        Velocity = velocity;
    }

    public float X { get; }

    public float Y { get; }

    public float Z { get; }

    /// <summary>Scalar speed the host accepted, used for the >20 repair gate.</summary>
    public float Velocity { get; }

    public static HostPlayerPose Zero => new(0, 0, 0, 0);

    public bool Equals(HostPlayerPose other) =>
        X == other.X && Y == other.Y && Z == other.Z && Velocity == other.Velocity;

    public override bool Equals(object obj) => obj is HostPlayerPose other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = X.GetHashCode();
            hash = (hash * 397) ^ Y.GetHashCode();
            hash = (hash * 397) ^ Z.GetHashCode();
            hash = (hash * 397) ^ Velocity.GetHashCode();
            return hash;
        }
    }

    public static bool operator ==(HostPlayerPose left, HostPlayerPose right) => left.Equals(right);

    public static bool operator !=(HostPlayerPose left, HostPlayerPose right) => !left.Equals(right);

    public override string ToString() => "(" + X + "," + Y + "," + Z + ")v=" + Velocity;
}

/// <summary>
/// One player's host-side presence and capability record (pure model for A09).
/// </summary>
/// <remarks>
/// <para>
/// Identity follows DESIGN 4.1: the session <c>PlayerId</c> comes from the connection and is
/// reusable, while <see cref="PersistentId"/> is the durable player identity used for saves and
/// for the resource ledger. The ledger keys balances by <see cref="PersistentId"/>, which is why
/// a reconnect keeps its balance: the session seat changes, the persistent owner does not.
/// </para>
/// <para>
/// Combat-adjacent detail (weapon cooldowns, shield, fleet loadout) belongs to A11 and
/// construction scoring detail belongs to A16. This record carries only what A09 promises: who
/// the player is, where the host believes they are, whether they are alive, and the
/// construction switches and drone capacity the candidate filter (A16) needs. Adapters fill the
/// presence fields; until they do the record is fail-closed (no planet, no drones, both switches
/// off), so no task service can mistake an uninitialized entry for an eligible owner.
/// </para>
/// </remarks>
public sealed class HostPlayerState
{
    internal HostPlayerState(string persistentId, ushort sessionPlayerId, HostPlayerRole role,
        ConnectionEpoch connection)
    {
        PersistentId = persistentId;
        SessionPlayerId = sessionPlayerId;
        Role = role;
        Connection = connection;
        IsOnline = true;
        IsAlive = true;
        Pose = HostPlayerPose.Zero;
    }

    /// <summary>Durable player identity. Never reused across players. Ledger balances key by this.</summary>
    public string PersistentId { get; }

    /// <summary>Session seat from the connection. Reusable after a disconnect (VALIDATION L09).</summary>
    public ushort SessionPlayerId { get; internal set; }

    public HostPlayerRole Role { get; internal set; }

    /// <summary>Connection epoch the session assigned to this presence.</summary>
    public ConnectionEpoch Connection { get; internal set; }

    /// <summary>False after a disconnect; the persistent entry is retained so a reconnect rebinds it.</summary>
    public bool IsOnline { get; internal set; }

    public bool IsAlive { get; internal set; }

    /// <summary>Host-accepted planet, or 0 when unknown / not on a planet.</summary>
    public int PlanetId { get; internal set; }

    /// <summary>Host-accepted star, or 0 when unknown.</summary>
    public int StarId { get; internal set; }

    public HostPlayerPose Pose { get; internal set; }

    /// <summary>Construction repair switch, as the host last accepted it. Default off.</summary>
    public bool RepairEnabled { get; internal set; }

    /// <summary>Construction build switch, as the host last accepted it. Default off.</summary>
    public bool BuildEnabled { get; internal set; }

    /// <summary>Total drone slots the host believes this player has. Default 0.</summary>
    public int DroneTotal { get; internal set; }

    /// <summary>Per-player revision, incremented on every mutation of this record.</summary>
    public long Revision { get; internal set; }

    /// <summary>True for the virtual server seat, which owns no combat or drone work.</summary>
    public bool IsVirtualServer => Role == HostPlayerRole.HeadlessDedicated;

    /// <summary>Whether this presence may own combat work (checked by A11 before submitting).</summary>
    public bool CanOwnCombat => !IsVirtualServer && IsOnline && IsAlive;

    /// <summary>Whether this presence may own drone tasks (checked by A16/A17 before dispatch).</summary>
    public bool CanOwnDroneTask => !IsVirtualServer && IsOnline && IsAlive;

    public override string ToString() =>
        "persistent=" + PersistentId + "|session=" + SessionPlayerId + "|role=" + Role +
        "|online=" + IsOnline + "|alive=" + IsAlive + "|planet=" + PlanetId;
}

/// <summary>
/// The host's player membership table (TASKS.md A09, pure model).
/// </summary>
/// <remarks>
/// <para>
/// Not thread-safe by construction, like <see cref="CommandWindow"/>: only the frame boundary
/// touches it. The socket thread never registers presence directly; processors enqueue, the frame
/// applies.
/// </para>
/// <para>
/// A disconnect marks the persistent entry offline but keeps it: the next registration with the
/// same <c>PersistentId</c> rebinds the same record under a new session seat and connection
/// epoch, with presence fields preserved until the adapter refreshes them. Ledger balances live
/// under the persistent id, so they survive the rebind untouched. Session-seat reuse by a
/// different persistent id retires the previous holder first, so no command or task can leak
/// across players.
/// </para>
/// </remarks>
public sealed class HostPlayerRegistry
{
    private readonly Dictionary<ushort, HostPlayerState> bySession = new();
    private readonly Dictionary<string, HostPlayerState> byPersistent =
        new(StringComparer.Ordinal);

    private long reconnectsTotal;

    /// <summary>Persistent entries retained, online or offline.</summary>
    public int Count => byPersistent.Count;

    /// <summary>Entries currently online.</summary>
    public int OnlineCount
    {
        get
        {
            var online = 0;
            foreach (var state in byPersistent.Values)
            {
                if (state.IsOnline) online++;
            }
            return online;
        }
    }

    /// <summary>How many registrations rebound an existing persistent id.</summary>
    public long ReconnectsTotal => reconnectsTotal;

    /// <summary>
    /// Registers a presence, or rebinds the existing persistent entry to a new seat.
    /// </summary>
    /// <exception cref="ArgumentException">When the identity is unusable.</exception>
    public HostPlayerState RegisterOrUpdate(string persistentId, ushort sessionPlayerId,
        HostPlayerRole role, ConnectionEpoch connection)
    {
        if (string.IsNullOrEmpty(persistentId))
            throw new ArgumentException("A host player needs a persistent id.", nameof(persistentId));
        if (sessionPlayerId == 0)
            throw new ArgumentException("A host player needs a session player id.", nameof(sessionPlayerId));
        if (role == HostPlayerRole.Unknown)
            throw new ArgumentException("A host player needs a role.", nameof(role));
        if (!connection.IsValid)
            throw new ArgumentException("A host player needs a connection epoch.", nameof(connection));

        // A session seat reused by a different player retires the previous holder first. Without
        // this, VALIDATION L09 (session id reuse) would leave two persistent ids behind one seat.
        if (bySession.TryGetValue(sessionPlayerId, out var seatHolder) &&
            !string.Equals(seatHolder.PersistentId, persistentId, StringComparison.Ordinal))
        {
            RetireSeat(seatHolder);
        }

        if (byPersistent.TryGetValue(persistentId, out var existing))
        {
            var isReconnect = existing.IsOnline &&
                (existing.SessionPlayerId != sessionPlayerId || !existing.Connection.Equals(connection));
            if (isReconnect)
            {
                // The old seat is gone; the new registration owns the record now.
                if (existing.SessionPlayerId != 0 && existing.SessionPlayerId != sessionPlayerId)
                    bySession.Remove(existing.SessionPlayerId);
                System.Threading.Interlocked.Increment(ref reconnectsTotal);
            }
            else if (existing.IsOnline && existing.SessionPlayerId != 0 &&
                     existing.SessionPlayerId != sessionPlayerId)
            {
                bySession.Remove(existing.SessionPlayerId);
            }

            existing.SessionPlayerId = sessionPlayerId;
            existing.Role = role;
            existing.Connection = connection;
            existing.IsOnline = true;
            existing.Revision++;
            bySession[sessionPlayerId] = existing;
            return existing;
        }

        var state = new HostPlayerState(persistentId, sessionPlayerId, role, connection);
        byPersistent[persistentId] = state;
        bySession[sessionPlayerId] = state;
        return state;
    }

    /// <summary>
    /// Refreshes only the location facts, preserving switches, drone capacity and pose.
    /// </summary>
    /// <remarks>
    /// The join/movement adapters know where a player is but not their construction configuration
    /// or an authoritative pose, so a location refresh must not zero the fields the construction
    /// adapter owns (A16/A17) nor fabricate the host-accepted pose combat uses (A11). Returns
    /// false when the seat is unknown or offline, or the planet is unknown.
    /// </remarks>
    public bool UpdatePresenceLocation(ushort sessionPlayerId, int planetId, int starId, bool isAlive)
    {
        if (!bySession.TryGetValue(sessionPlayerId, out var state) || !state.IsOnline) return false;
        if (planetId <= 0 || starId < 0) return false;
        if (state.PlanetId == planetId && state.StarId == starId && state.IsAlive == isAlive)
        {
            return true;
        }
        state.PlanetId = planetId;
        state.StarId = starId;
        state.IsAlive = isAlive;
        state.Revision++;
        return true;
    }

    /// <summary>
    /// Refreshes presence. Returns false when the seat is unknown or offline.
    /// </summary>
    public bool UpdatePresence(ushort sessionPlayerId, int planetId, int starId,
        HostPlayerPose pose, bool isAlive, bool repairEnabled, bool buildEnabled, int droneTotal)
    {
        if (!bySession.TryGetValue(sessionPlayerId, out var state) || !state.IsOnline) return false;
        if (planetId < 0 || starId < 0 || droneTotal < 0) return false;
        state.PlanetId = planetId;
        state.StarId = starId;
        state.Pose = pose;
        state.IsAlive = isAlive;
        state.RepairEnabled = repairEnabled;
        state.BuildEnabled = buildEnabled;
        state.DroneTotal = droneTotal;
        state.Revision++;
        return true;
    }

    /// <summary>
    /// Marks one seat offline. The persistent entry and its ledger balances survive.
    /// </summary>
    /// <remarks>
    /// This is what <c>AuthoritySession.ForgetPlayerConnection</c> calls: the connection's dedup
    /// window and queued commands are dropped there, while here only presence goes offline. A
    /// reconnect with the same persistent id rebinds the retained record.
    /// </remarks>
    public bool MarkOfflineBySession(ushort sessionPlayerId)
    {
        if (!bySession.TryGetValue(sessionPlayerId, out var state)) return false;
        RetireSeat(state);
        return true;
    }

    public bool TryGetBySession(ushort sessionPlayerId, out HostPlayerState state)
    {
        if (bySession.TryGetValue(sessionPlayerId, out state)) return state.IsOnline;
        state = null;
        return false;
    }

    public bool TryGetByPersistent(string persistentId, out HostPlayerState state)
    {
        if (persistentId != null && byPersistent.TryGetValue(persistentId, out state)) return true;
        state = null;
        return false;
    }

    /// <summary>Drops everything. Used when the world or session ends.</summary>
    public void Clear()
    {
        bySession.Clear();
        byPersistent.Clear();
        reconnectsTotal = 0;
    }

    /// <summary>
    /// Restores one saved player as an offline persistent entry (TASKS.md A21, host-only).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The durable identity, presence facts and drone capacity survive the reload; the session
    /// seat and connection epoch do not — a restored entry is offline, so
    /// <see cref="HostPlayerState.CanOwnCombat"/> and <see cref="HostPlayerState.CanOwnDroneTask"/>
    /// are false until the player actually rejoins and the adapters refresh presence.
    /// </para>
    /// <para>
    /// A persistent id that already has a live entry is skipped: the running world's facts must
    /// not be overwritten by the file. That also makes re-applying a sidecar harmless.
    /// </para>
    /// </remarks>
    public bool RestoreSavedPlayer(string persistentId, HostPlayerRole role, int planetId, int starId,
        bool isAlive, bool repairEnabled, bool buildEnabled, int droneTotal)
    {
        if (string.IsNullOrEmpty(persistentId) || role == HostPlayerRole.Unknown) return false;
        if (byPersistent.TryGetValue(persistentId, out _)) return false;
        if (planetId < 0 || starId < 0 || droneTotal < 0) return false;
        var state = new HostPlayerState(persistentId, sessionPlayerId: 0, role, connection: default)
        {
            PlanetId = planetId,
            StarId = starId,
            IsAlive = isAlive,
            RepairEnabled = repairEnabled,
            BuildEnabled = buildEnabled,
            DroneTotal = droneTotal
        };
        // Construction-time invariants hold a seated record online; a restored entry starts
        // offline until RegisterOrUpdate seats it.
        state.IsOnline = false;
        byPersistent[persistentId] = state;
        return true;
    }

    /// <summary>
    /// Captures every persistent entry into the save-sidecar record form (TASKS.md A21).
    /// </summary>
    public List<AuthoritySavePlayer> CapturePlayersForSave()
    {
        var players = new List<AuthoritySavePlayer>(byPersistent.Count);
        foreach (var state in byPersistent.Values)
        {
            players.Add(new AuthoritySavePlayer
            {
                PersistentId = state.PersistentId,
                Role = state.Role,
                PlanetId = state.PlanetId,
                StarId = state.StarId,
                IsAlive = state.IsAlive,
                RepairEnabled = state.RepairEnabled,
                BuildEnabled = state.BuildEnabled,
                DroneTotal = state.DroneTotal
            });
        }
        return players;
    }

    private void RetireSeat(HostPlayerState state)
    {
        if (state.SessionPlayerId != 0) bySession.Remove(state.SessionPlayerId);
        state.SessionPlayerId = 0;
        state.Connection = default;
        state.IsOnline = false;
        state.Revision++;
    }
}
