#region

using System;
using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

#endregion

namespace NebulaTests.Authority;

/// <summary>
/// A22 (part 3): the client's standing subscription set follows where the subscriber actually is.
/// The frame boundary re-plans from the client's own local view and issues only the delta —
/// subscribe what is newly desired, unsubscribe what is left behind. Digest-only observations are
/// not the standing set and are never touched by it.
/// </summary>
/// <remarks>
/// These tests drive the session with a fake observation; the frame patch supplies the real one
/// from <c>GameMain.localPlanet</c>. The host's enforcement of every request is A20's
/// <c>MaySubscribe</c> and stays independent.
/// </remarks>
[TestClass]
public class StandingSubscriptionTest
{
    private static readonly AuthorityEpoch Epoch = new(0x1723172317231723, 0x2372372372372372);
    private static readonly int PlanetA = 101;
    private static readonly int PlanetB = 102;

    private sealed class RecordingControlSink : IScopeControlSink
    {
        public readonly List<(ScopeControlOp Op, ScopeKey Scope, ScopeRecoveryReason Reason)> Sent = [];

        public void Send(ScopeControlOp op, ScopeKey scope, ScopeRecoveryReason reason, bool digestOnly,
            long subscriptionEpoch, long lastAppliedSequence) => Sent.Add((op, scope, reason));
    }

    private static readonly PoolKind[] PlanetPools =
    [
        PoolKind.Entity, PoolKind.Prebuild, PoolKind.GroundEnemy, PoolKind.GroundCraft,
        PoolKind.Vegetable, PoolKind.Vein, PoolKind.Base, PoolKind.DroneTask
    ];

    private sealed class Rig
    {
        public readonly RecordingControlSink Control = new();
        public readonly AuthoritySession Session;

        public Rig(bool isHost = false)
        {
            var identity = new AuthoritySessionState();
            identity.OnPeerNegotiated(AuthorityMode.HostAuthority);
            Session = new AuthoritySession(identity);
            Session.BeginAuthorityWorld(Epoch, isHost);
            Session.ScopeControlSink = Control;
        }

        public void Observe(int planetId, bool inSector) =>
            Session.UpdateStandingSubscriptions(planetId, inSector);

        public int SubscribesFor(ScopeKey scope) => Count(ScopeControlOp.Subscribe, scope);
        public int UnsubscribesFor(ScopeKey scope) => Count(ScopeControlOp.Unsubscribe, scope);

        private int Count(ScopeControlOp op, ScopeKey scope)
        {
            var count = 0;
            foreach (var entry in Control.Sent)
            {
                if (entry.Op == op && entry.Scope.Equals(scope)) count++;
            }
            return count;
        }
    }

    private static ScopeKey PlanetScope(PoolKind kind, int planetId) => new(kind, planetId);

    [TestMethod]
    public void TheFirstObservationSubscribesTheStandingSet()
    {
        var rig = new Rig();
        rig.Observe(PlanetA, inSector: false);

        TestAssert.HasCount(8, rig.Session.StandingScopes, "One scope per planet pool.");
        foreach (var kind in PlanetPools)
        {
            var scope = PlanetScope(kind, PlanetA);
            TestAssert.AreEqual(1, rig.SubscribesFor(scope), $"{kind} was requested once.");
            TestAssert.AreEqual(SubscriptionPhase.Snapshotting,
                rig.Session.WorldReplica.Versions.TryGetScope(scope, out var state)
                    ? state.Phase : SubscriptionPhase.Unsubscribed,
                $"{kind} waits for its baseline.");
        }
        TestAssert.AreEqual(0, rig.SubscribesFor(new ScopeKey(PoolKind.SpaceEnemy, AuthorityScope.Sector)),
            "A landed client does not hold the sector pools.");
    }

    [TestMethod]
    public void AnUnchangedObservationSendsNothing()
    {
        var rig = new Rig();
        rig.Observe(PlanetA, inSector: false);
        var afterFirst = rig.Control.Sent.Count;

        rig.Observe(PlanetA, inSector: false);
        rig.Observe(PlanetA, inSector: false);
        TestAssert.AreEqual(afterFirst, rig.Control.Sent.Count,
            "The same standing set issues no delta; per-frame replanning is free.");
    }

    [TestMethod]
    public void LeavingAPlanetReleasesItsScopesAndLandsTheNewOnes()
    {
        var rig = new Rig();
        rig.Observe(PlanetA, inSector: false);
        rig.Control.Sent.Clear();

        rig.Observe(PlanetB, inSector: false);

        foreach (var kind in PlanetPools)
        {
            TestAssert.AreEqual(1, rig.UnsubscribesFor(PlanetScope(kind, PlanetA)),
                $"{kind} of A was left behind exactly once.");
            TestAssert.AreEqual(1, rig.SubscribesFor(PlanetScope(kind, PlanetB)),
                $"{kind} of B was subscribed exactly once.");
        }
        TestAssert.HasCount(8, rig.Session.StandingScopes);
        TestAssert.AreEqual(SubscriptionPhase.Unsubscribed,
            rig.Session.WorldReplica.Versions.TryGetScope(PlanetScope(PoolKind.GroundEnemy, PlanetA), out var left)
                ? left.Phase : SubscriptionPhase.Unsubscribed);
    }

    [TestMethod]
    public void FlyingInSpaceSwapsPlanetPoolsForSectorPools()
    {
        var rig = new Rig();
        rig.Observe(PlanetB, inSector: false);
        rig.Control.Sent.Clear();

        rig.Observe(planetId: 0, inSector: true);

        foreach (var kind in PlanetPools)
        {
            TestAssert.AreEqual(1, rig.UnsubscribesFor(PlanetScope(kind, PlanetB)),
                "The landed set is released when the subscriber flies.");
        }
        var spaceEnemy = new ScopeKey(PoolKind.SpaceEnemy, AuthorityScope.Sector);
        var spaceCraft = new ScopeKey(PoolKind.SpaceCraft, AuthorityScope.Sector);
        TestAssert.AreEqual(1, rig.SubscribesFor(spaceEnemy));
        TestAssert.AreEqual(1, rig.SubscribesFor(spaceCraft));
        TestAssert.HasCount(2, rig.Session.StandingScopes);
    }

    [TestMethod]
    public void LandingAgainRebasesThePlanetScopes()
    {
        // Space → planet: the sector pools are released and the planet's scopes subscribe fresh.
        // The fresh subscription re-enters Snapshotting with its stream reset, so returning to a
        // previously visited planet is a new baseline conversation (L02's A→B→A) — the old
        // subscription's epoch and history are gone, which is what makes the old tail refusable.
        var rig = new Rig();
        rig.Observe(PlanetA, inSector: false);
        rig.Observe(planetId: 0, inSector: true);
        rig.Control.Sent.Clear();

        rig.Observe(PlanetA, inSector: false);

        var spaceEnemy = new ScopeKey(PoolKind.SpaceEnemy, AuthorityScope.Sector);
        TestAssert.AreEqual(1, rig.UnsubscribesFor(spaceEnemy), "The sector pool is released on landing.");
        TestAssert.AreEqual(1, rig.SubscribesFor(PlanetScope(PoolKind.GroundEnemy, PlanetA)),
            "Returning to A subscribes it again, never silently reuses the old subscription.");
        TestAssert.IsTrue(rig.Session.WorldReplica.Versions.TryGetScope(
            PlanetScope(PoolKind.GroundEnemy, PlanetA), out var state));
        TestAssert.AreEqual(SubscriptionPhase.Snapshotting, state.Phase,
            "The return waits for a fresh baseline.");
        TestAssert.AreEqual(0L, state.LastAppliedSequence, "The stream restarts with the baseline.");
    }

    [TestMethod]
    public void DigestOnlyObservationsAreNotTheStandingSet()
    {
        // A star-map observation held while standing on A: the standing update neither subscribes
        // nor unsubscribes it — observation-driven scopes are managed by their observer.
        var rig = new Rig();
        rig.Observe(PlanetA, inSector: false);
        var observed = PlanetScope(PoolKind.GroundEnemy, PlanetB);
        TestAssert.IsTrue(rig.Session.TrySubscribeScope(observed, digestOnly: true));
        rig.Control.Sent.Clear();

        rig.Observe(PlanetA, inSector: false);
        rig.Observe(PlanetA, inSector: true);
        rig.Observe(PlanetA, inSector: false);

        TestAssert.AreEqual(0, rig.UnsubscribesFor(observed),
            "The standing update never releases an observation it does not own.");
        TestAssert.AreEqual(0, rig.SubscribesFor(observed));
        TestAssert.AreEqual(SubscriptionPhase.Live,
            rig.Session.WorldReplica.Versions.TryGetScope(observed, out var state) ? state.Phase : SubscriptionPhase.Unsubscribed,
            "The observation stays live throughout.");
        TestAssert.IsFalse(rig.Session.StandingScopes.Contains(observed),
            "The observation is not part of the standing set.");
    }

    [TestMethod]
    public void AHostSessionIgnoresStandingUpdates()
    {
        var rig = new Rig(isHost: true);
        rig.Observe(PlanetA, inSector: false);
        rig.Observe(PlanetB, inSector: true);
        TestAssert.IsEmpty(rig.Control.Sent, "A host has no replica and subscribes to nothing.");
        TestAssert.IsEmpty(rig.Session.StandingScopes);
    }

    [TestMethod]
    public void AWorldChangePlansTheStandingSetFromScratch()
    {
        // The new world's subscriptions must be requested again even for the same planet: the old
        // world's scopes and epochs are gone, and suppressing the delta would strand the client
        // with no facts in the new epoch.
        var rig = new Rig();
        rig.Observe(PlanetA, inSector: false);
        var before = rig.Control.Sent.Count;

        rig.Session.BeginAuthorityWorld(new AuthorityEpoch(0x33445566778899AA, 0xBBCCDDEEFF001122), isHost: false);
        rig.Observe(PlanetA, inSector: false);

        TestAssert.IsTrue(rig.Control.Sent.Count > before, "The new world re-requests the standing set.");
        TestAssert.HasCount(8, rig.Session.StandingScopes);
        foreach (var kind in PlanetPools)
        {
            TestAssert.AreEqual(1, rig.SubscribesFor(PlanetScope(kind, PlanetA)) - 1,
                $"Exactly one fresh request for {kind} in the new epoch.");
        }
    }
}
