#region

using NebulaModel.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

#endregion

namespace NebulaTests.Authority;

/// <summary>
/// A22 production bridge: the host registry must be fed by real join/movement events. The bridge
/// itself is host-only and guarded; this pins the registry mutation it performs, including the
/// rule that a location refresh never clobbers the switches/capacity the construction adapter owns.
/// </summary>
[TestClass]
public class HostPlayerPresenceTest
{
    private static ConnectionEpoch Conn(ulong value) => new(value);

    [TestMethod]
    public void ARegisteredRemoteGetsALocation()
    {
        var registry = new HostPlayerRegistry();
        registry.RegisterOrUpdate("remote-1", 2, HostPlayerRole.Remote, Conn(7));
        TestAssert.IsTrue(registry.UpdatePresenceLocation(2, planetId: 102, starId: 1, isAlive: true));
        TestAssert.IsTrue(registry.TryGetBySession(2, out var state));
        TestAssert.AreEqual(102, state.PlanetId);
        TestAssert.AreEqual(1, state.StarId);
        TestAssert.IsTrue(state.IsAlive);
    }

    [TestMethod]
    public void ALocationRefreshPreservesSwitchesCapacityAndPose()
    {
        var registry = new HostPlayerRegistry();
        registry.RegisterOrUpdate("remote-1", 2, HostPlayerRole.Remote, Conn(7));
        registry.UpdatePresence(2, planetId: 101, starId: 1, pose: new HostPlayerPose(1, 2, 3, 4),
            isAlive: true, repairEnabled: true, buildEnabled: true, droneTotal: 5);

        registry.UpdatePresenceLocation(2, planetId: 102, starId: 1, isAlive: true);
        TestAssert.IsTrue(registry.TryGetBySession(2, out var state));
        TestAssert.AreEqual(102, state.PlanetId, "The moved planet is applied.");
        TestAssert.IsTrue(state.RepairEnabled, "A location refresh must not clear the repair switch.");
        TestAssert.IsTrue(state.BuildEnabled, "A location refresh must not clear the build switch.");
        TestAssert.AreEqual(5, state.DroneTotal, "A location refresh must not drop drone capacity.");
        TestAssert.AreEqual(new HostPlayerPose(1, 2, 3, 4), state.Pose,
            "A location refresh must not overwrite the host-accepted pose.");
    }

    [TestMethod]
    public void ALocationForAnUnknownOrOfflineSeatIsRefused()
    {
        var registry = new HostPlayerRegistry();
        TestAssert.IsFalse(registry.UpdatePresenceLocation(9, 102, 1, true),
            "An unregistered seat has no presence to move.");
        registry.RegisterOrUpdate("remote-1", 2, HostPlayerRole.Remote, Conn(7));
        registry.MarkOfflineBySession(2);
        TestAssert.IsFalse(registry.UpdatePresenceLocation(2, 102, 1, true),
            "An offline persistent entry must not accept a location.");
    }

    [TestMethod]
    public void AnUnknownPlanetIsRefusedSoEligibilityStaysFailClosed()
    {
        var registry = new HostPlayerRegistry();
        registry.RegisterOrUpdate("remote-1", 2, HostPlayerRole.Remote, Conn(7));
        TestAssert.IsFalse(registry.UpdatePresenceLocation(2, planetId: 0, starId: 1, isAlive: true));
        TestAssert.IsFalse(registry.UpdatePresenceLocation(2, planetId: -1, starId: 1, isAlive: true));
        TestAssert.IsTrue(registry.TryGetBySession(2, out var state));
        TestAssert.AreEqual(0, state.PlanetId, "A refused update leaves the record untouched.");
    }

    [TestMethod]
    public void RepeatingTheSameLocationDoesNotBumpTheRevision()
    {
        var registry = new HostPlayerRegistry();
        registry.RegisterOrUpdate("remote-1", 2, HostPlayerRole.Remote, Conn(7));
        registry.UpdatePresenceLocation(2, 102, 1, true);
        TestAssert.IsTrue(registry.TryGetBySession(2, out var state));
        var revision = state.Revision;
        registry.UpdatePresenceLocation(2, 102, 1, true);
        TestAssert.AreEqual(revision, state.Revision, "An unchanged location is not a new revision.");
    }
}
