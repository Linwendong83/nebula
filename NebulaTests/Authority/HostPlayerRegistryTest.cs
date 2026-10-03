using System;
using NebulaModel.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A09: the host's player membership table.
/// </summary>
/// <remarks>
/// TASKS.md A09 requires distinct seats for the hosting player, remote clients and the headless
/// server, with the virtual server seat never owning combat or repair work; a reconnect keeping
/// its persistent record; and the host and remotes taking the same rule path. The ledger holds
/// the balances; this table only decides who may act.
/// </remarks>
[TestClass]
public class HostPlayerRegistryTest
{
    private static HostPlayerRegistry NewRegistry() => new();

    private static ConnectionEpoch Conn(ulong value) => new(value);

    [TestMethod]
    public void ALocalHostMayOwnCombatAndDrones()
    {
        var registry = NewRegistry();
        var state = registry.RegisterOrUpdate("persistent-host", 1, HostPlayerRole.LocalHost, Conn(7));
        TestAssert.IsTrue(registry.UpdatePresence(1, 101, 10, new HostPlayerPose(1, 2, 3, 5), true, true, true, 8));
        TestAssert.IsTrue(state.CanOwnCombat);
        TestAssert.IsTrue(state.CanOwnDroneTask);
        TestAssert.IsFalse(state.IsVirtualServer);
    }

    [TestMethod]
    public void ARemoteFollowsTheSameRulesAsTheHost()
    {
        var registry = NewRegistry();
        var host = registry.RegisterOrUpdate("persistent-host", 1, HostPlayerRole.LocalHost, Conn(7));
        var remote = registry.RegisterOrUpdate("persistent-remote", 2, HostPlayerRole.Remote, Conn(8));
        registry.UpdatePresence(1, 101, 10, HostPlayerPose.Zero, true, true, true, 8);
        registry.UpdatePresence(2, 102, 10, HostPlayerPose.Zero, true, true, true, 8);

        // Same online/alive/switches, same answer: there is no "remote infinite" branch here.
        TestAssert.AreEqual(host.CanOwnCombat, remote.CanOwnCombat);
        TestAssert.AreEqual(host.CanOwnDroneTask, remote.CanOwnDroneTask);
        TestAssert.IsTrue(remote.CanOwnCombat);
        TestAssert.AreEqual(2, registry.OnlineCount);
    }

    [TestMethod]
    public void AHeadlessServerNeverOwnsCombatOrDrones()
    {
        var registry = NewRegistry();
        var state = registry.RegisterOrUpdate("headless", 1, HostPlayerRole.HeadlessDedicated, Conn(7));
        registry.UpdatePresence(1, 101, 10, HostPlayerPose.Zero, true, true, true, 99);
        TestAssert.IsTrue(state.IsVirtualServer);
        TestAssert.IsFalse(state.CanOwnCombat, "A virtual server has no mecha to fight with.");
        TestAssert.IsFalse(state.CanOwnDroneTask, "A virtual server owns no construction module.");
    }

    [TestMethod]
    public void DeadOrOfflinePlayersOwnNothing()
    {
        var registry = NewRegistry();
        var state = registry.RegisterOrUpdate("p1", 1, HostPlayerRole.Remote, Conn(7));
        registry.UpdatePresence(1, 101, 10, HostPlayerPose.Zero, false, true, true, 8);
        TestAssert.IsFalse(state.CanOwnCombat);
        TestAssert.IsFalse(state.CanOwnDroneTask);

        registry.UpdatePresence(1, 101, 10, HostPlayerPose.Zero, true, true, true, 8);
        TestAssert.IsTrue(state.CanOwnCombat);
        TestAssert.IsTrue(registry.MarkOfflineBySession(1));
        TestAssert.IsFalse(state.CanOwnCombat, "An offline seat owns nothing until it reconnects.");
    }

    [TestMethod]
    public void ReconnectKeepsThePersistentRecord()
    {
        var registry = NewRegistry();
        var first = registry.RegisterOrUpdate("p1", 1, HostPlayerRole.Remote, Conn(7));
        registry.UpdatePresence(1, 101, 10, new HostPlayerPose(1, 2, 3, 4), true, true, false, 6);

        var second = registry.RegisterOrUpdate("p1", 5, HostPlayerRole.Remote, Conn(8));
        TestAssert.AreSame(first, second, "A reconnect rebinds the same persistent record.");
        TestAssert.AreEqual((ushort)5, second.SessionPlayerId);
        TestAssert.AreEqual(Conn(8), second.Connection);
        TestAssert.IsTrue(second.IsOnline);
        TestAssert.AreEqual(1L, registry.ReconnectsTotal);
        // Presence survives the rebind until the adapter refreshes it; nothing is reset to a lie.
        TestAssert.AreEqual(101, second.PlanetId);
        TestAssert.AreEqual(6, second.DroneTotal);
        TestAssert.IsFalse(registry.TryGetBySession(1, out _), "The old seat must not resolve anymore.");
        TestAssert.IsTrue(registry.TryGetBySession(5, out var bySeat));
        TestAssert.AreSame(second, bySeat);
    }

    [TestMethod]
    public void SessionSeatReuseDoesNotPolluteThePreviousPlayer()
    {
        var registry = NewRegistry();
        registry.RegisterOrUpdate("player-a", 1, HostPlayerRole.Remote, Conn(7));
        registry.RegisterOrUpdate("player-b", 1, HostPlayerRole.Remote, Conn(8));

        TestAssert.IsTrue(registry.TryGetByPersistent("player-a", out var old));
        TestAssert.IsFalse(old.IsOnline, "The evicted seat holder goes offline.");
        TestAssert.AreEqual((ushort)0, old.SessionPlayerId);
        TestAssert.IsTrue(registry.TryGetBySession(1, out var current));
        TestAssert.AreEqual("player-b", current.PersistentId);
    }

    [TestMethod]
    public void MarkingOfflineKeepsThePersistentEntry()
    {
        var registry = NewRegistry();
        registry.RegisterOrUpdate("p1", 3, HostPlayerRole.Remote, Conn(7));
        TestAssert.IsTrue(registry.MarkOfflineBySession(3));
        TestAssert.IsFalse(registry.TryGetBySession(3, out _));
        TestAssert.IsTrue(registry.TryGetByPersistent("p1", out var retained));
        TestAssert.IsFalse(retained.IsOnline);
        // The ledger balance under "p1" is untouched by this call; that half is pinned by the
        // ledger test's registry integration case.
    }

    [TestMethod]
    public void UpdatePresenceOnlyForOnlineSeats()
    {
        var registry = NewRegistry();
        TestAssert.IsFalse(registry.UpdatePresence(9, 101, 10, HostPlayerPose.Zero, true, true, true, 1));
        registry.RegisterOrUpdate("p1", 9, HostPlayerRole.Remote, Conn(7));
        registry.MarkOfflineBySession(9);
        TestAssert.IsFalse(registry.UpdatePresence(9, 101, 10, HostPlayerPose.Zero, true, true, true, 1));
        TestAssert.IsFalse(registry.MarkOfflineBySession(9), "A retired seat cannot be retired twice.");
    }

    [TestMethod]
    public void NewRegistrationsAreFailClosed()
    {
        var registry = NewRegistry();
        var state = registry.RegisterOrUpdate("fresh", 4, HostPlayerRole.Remote, Conn(11));
        TestAssert.AreEqual(0, state.PlanetId);
        TestAssert.AreEqual(0, state.DroneTotal);
        TestAssert.IsFalse(state.RepairEnabled, "No dispatch before the adapter confirms the switch.");
        TestAssert.IsFalse(state.BuildEnabled, "No dispatch before the adapter confirms the switch.");
    }

    [TestMethod]
    public void RegisterRejectsAnUnusableIdentity()
    {
        var registry = NewRegistry();
        TestAssert.ThrowsExactly<ArgumentException>(() => registry.RegisterOrUpdate("", 1, HostPlayerRole.Remote, Conn(7)));
        TestAssert.ThrowsExactly<ArgumentException>(() => registry.RegisterOrUpdate("p", 0, HostPlayerRole.Remote, Conn(7)));
        TestAssert.ThrowsExactly<ArgumentException>(() => registry.RegisterOrUpdate("p", 1, HostPlayerRole.Unknown, Conn(7)));
        TestAssert.ThrowsExactly<ArgumentException>(() => registry.RegisterOrUpdate("p", 1, HostPlayerRole.Remote, default));
    }
}
