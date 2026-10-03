#region

using System;
using System.Collections.Generic;
using NebulaModel.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

#endregion

namespace NebulaTests.Authority;

/// <summary>
/// A20: which scopes a subscriber may hold and how deep, and what a broken scope's recovery is.
/// </summary>
/// <remarks>
/// The card's rule is "本地行星/星系、远程星图/UI观察、必要跨星攻击的合法订阅；其它区域只摘要".
/// The policy tests pin the classification table from both sides — the client's own observation and
/// the host's registry facts — and the planner tests pin the mapping from an observed break to the
/// recovery it triggers, including the missing-object-vs-stream-gap split the card names.
/// </remarks>
[TestClass]
public class ScopeSubscriptionTest
{
    private static readonly AuthorityEpoch Epoch = new(0x0A0A0A0A0A0A0A0A, 0x0A0A0A0A0A0A0A0A);

    private static ObjectKey Key(PoolKind kind, int scope, int nativeId, long generation) =>
        ObjectKey.Create(Epoch, kind, scope, nativeId, generation);

    // --- ScopeSubscriptionPolicy.Classify: the client's own observation. ---

    [TestMethod]
    public void TheHomePlanetIsAFullSubscriptionAndAWatchedOneIsDigestOnly()
    {
        var context = new ScopeObservationContext
        {
            CurrentPlanetId = 101,
            ObservedAstroIds = new HashSet<int> { 102 }
        };

        TestAssert.AreEqual(ScopeSubscriptionDecision.Allowed,
            ScopeSubscriptionPolicy.Classify(new ScopeKey(PoolKind.GroundEnemy, 101), context));
        TestAssert.AreEqual(ScopeSubscriptionDecision.DigestOnly,
            ScopeSubscriptionPolicy.Classify(new ScopeKey(PoolKind.GroundEnemy, 102), context));
        TestAssert.AreEqual(ScopeSubscriptionDecision.Refused,
            ScopeSubscriptionPolicy.Classify(new ScopeKey(PoolKind.GroundEnemy, 103), context));
    }

    [TestMethod]
    public void SectorPoolsAreFullInSpaceAndASummaryFromTheStarMap()
    {
        var flying = new ScopeObservationContext { IsInSector = true };
        var watching = new ScopeObservationContext { WantsSectorSummary = true };

        TestAssert.AreEqual(ScopeSubscriptionDecision.Allowed,
            ScopeSubscriptionPolicy.Classify(new ScopeKey(PoolKind.SpaceEnemy, AuthorityScope.Sector), flying));
        TestAssert.AreEqual(ScopeSubscriptionDecision.DigestOnly,
            ScopeSubscriptionPolicy.Classify(new ScopeKey(PoolKind.SpaceCraft, AuthorityScope.Sector), watching));
        TestAssert.AreEqual(ScopeSubscriptionDecision.Refused,
            ScopeSubscriptionPolicy.Classify(new ScopeKey(PoolKind.SpaceEnemy, AuthorityScope.Sector),
                new ScopeObservationContext()));
    }

    [TestMethod]
    public void AHiveIsWatchedByItsAstroSlotAndUnknownKindsAreRefused()
    {
        var hiveSlot = 1000001;
        var flying = new ScopeObservationContext { IsInSector = true };
        var watching = new ScopeObservationContext { ObservedAstroIds = new HashSet<int> { hiveSlot } };

        TestAssert.AreEqual(ScopeSubscriptionDecision.Allowed,
            ScopeSubscriptionPolicy.Classify(new ScopeKey(PoolKind.Hive, hiveSlot), flying));
        TestAssert.AreEqual(ScopeSubscriptionDecision.DigestOnly,
            ScopeSubscriptionPolicy.Classify(new ScopeKey(PoolKind.Hive, hiveSlot), watching));
        TestAssert.AreEqual(ScopeSubscriptionDecision.Refused,
            ScopeSubscriptionPolicy.Classify(new ScopeKey(PoolKind.Unknown, 101), flying));
        TestAssert.AreEqual(ScopeSubscriptionDecision.Refused,
            ScopeSubscriptionPolicy.Classify(default, flying));
    }

    // --- ScopeSubscriptionPolicy.MaySubscribe: the host's registry facts. ---

    [TestMethod]
    public void TheHostServesAPlanetPoolOnlyForThePlanetItAcceptedThePlayerOn()
    {
        TestAssert.AreEqual(ScopeSubscriptionDecision.Allowed,
            ScopeSubscriptionPolicy.MaySubscribe(new ScopeKey(PoolKind.GroundEnemy, 101),
                subscriberOnline: true, subscriberPlanetId: 101));
        TestAssert.AreEqual(ScopeSubscriptionDecision.Refused,
            ScopeSubscriptionPolicy.MaySubscribe(new ScopeKey(PoolKind.GroundEnemy, 102),
                subscriberOnline: true, subscriberPlanetId: 101));
        TestAssert.AreEqual(ScopeSubscriptionDecision.Refused,
            ScopeSubscriptionPolicy.MaySubscribe(new ScopeKey(PoolKind.GroundEnemy, 101),
                subscriberOnline: false, subscriberPlanetId: 101));
        // Global-visibility pools have no per-player planet rule; interest filtering is A23.
        TestAssert.AreEqual(ScopeSubscriptionDecision.Allowed,
            ScopeSubscriptionPolicy.MaySubscribe(new ScopeKey(PoolKind.SpaceEnemy, AuthorityScope.Sector),
                subscriberOnline: true, subscriberPlanetId: 101));
    }

    // --- ScopeSubscriptionPolicy.DesiredScopes: the standing set. ---

    [TestMethod]
    public void TheStandingSetIsWhereTheSubscriberLives()
    {
        var onPlanet = new ScopeObservationContext { CurrentPlanetId = 101 };
        var desired = new List<ScopeKey>();
        ScopeSubscriptionPolicy.DesiredScopes(onPlanet, desired);
        TestAssert.Contains(new ScopeKey(PoolKind.Entity, 101), desired);
        TestAssert.Contains(new ScopeKey(PoolKind.GroundEnemy, 101), desired);
        TestAssert.Contains(new ScopeKey(PoolKind.DroneTask, 101), desired);
        TestAssert.IsFalse(desired.Contains(new ScopeKey(PoolKind.SpaceEnemy, AuthorityScope.Sector)),
            "A grounded player does not stand-subscribe the sector.");

        var inSpace = new ScopeObservationContext { CurrentPlanetId = 0, IsInSector = true };
        ScopeSubscriptionPolicy.DesiredScopes(inSpace, desired);
        TestAssert.Contains(new ScopeKey(PoolKind.SpaceEnemy, AuthorityScope.Sector), desired);
        TestAssert.Contains(new ScopeKey(PoolKind.SpaceCraft, AuthorityScope.Sector), desired);
        TestAssert.IsEmpty(desired.FindAll(scope => scope.Kind == PoolKind.GroundEnemy),
            "A player in space holds no planet pool at full depth.");
    }

    // --- ScopeRecoveryPlanner: the break → recovery mapping. ---

    [TestMethod]
    public void AStreamGapRebuildsOnlyItsOwnScope()
    {
        var scope = new ScopeKey(PoolKind.GroundEnemy, 101);
        var plan = ScopeRecoveryPlanner.Plan(ScopeRecoveryReason.StreamGap, scope);

        TestAssert.HasCount(2, plan.Actions);
        TestAssert.AreEqual(ScopeRecoveryActionKind.SuspendInput, plan.Actions[0].Kind);
        TestAssert.AreEqual(ScopeRecoveryActionKind.RequestScopeBaseline, plan.Actions[1].Kind);
        TestAssert.AreEqual(scope, plan.Actions[1].Scope);
    }

    [TestMethod]
    public void AnUnknownCoreTopologyAsksForTheDependencyBaselineFirst()
    {
        var baseScope = new ScopeKey(PoolKind.Base, 101);
        var plan = ScopeRecoveryPlanner.Plan(ScopeRecoveryReason.UnknownCoreTopology, baseScope);

        TestAssert.HasCount(3, plan.Actions);
        TestAssert.AreEqual(ScopeRecoveryActionKind.RequestDependencyBaseline, plan.Actions[1].Kind);
        TestAssert.AreEqual(new ScopeKey(PoolKind.Entity, 101), plan.Actions[1].Scope,
            "A base is displayed through the factory's entity graph.");
        TestAssert.AreEqual(ScopeRecoveryActionKind.RequestScopeBaseline, plan.Actions[2].Kind);

        var hiveScope = new ScopeKey(PoolKind.Hive, 1000001);
        var hivePlan = ScopeRecoveryPlanner.Plan(ScopeRecoveryReason.UnknownCoreTopology, hiveScope);
        TestAssert.AreEqual(new ScopeKey(PoolKind.SpaceEnemy, AuthorityScope.Sector), hivePlan.Actions[1].Scope,
            "A hive's topology lives with the sector's space-enemy pool.");
    }

    [TestMethod]
    public void EveryReasonEndsInTheBrokenScopesOwnBaselineAndNoneResurrectsByPatch()
    {
        foreach (ScopeRecoveryReason reason in Enum.GetValues(typeof(ScopeRecoveryReason)))
        {
            if (reason == ScopeRecoveryReason.None) continue;
            var scope = new ScopeKey(PoolKind.GroundEnemy, 101);
            var plan = ScopeRecoveryPlanner.Plan(reason, scope);
            TestAssert.IsNotEmpty(plan.Actions, "A named reason must plan something.");
            TestAssert.AreEqual(ScopeRecoveryActionKind.SuspendInput, plan.Actions[0].Kind);
            TestAssert.AreEqual(ScopeRecoveryActionKind.RequestScopeBaseline, plan.Actions[plan.Actions.Length - 1].Kind);
        }
        TestAssert.IsEmpty(ScopeRecoveryPlanner.Plan(ScopeRecoveryReason.None,
            new ScopeKey(PoolKind.GroundEnemy, 101)).Actions);
    }

    // --- ScopeRecoveryPolicy: the legacy enemy state repair channel. ---

    [TestMethod]
    public void TheLegacyEnemyStateRepairChannelRetiresOnlyInHostAuthority()
    {
        // NebulaWorld/Authority/ScopeRecoveryPolicy.cs: the 1-HP query loop, the reflection
        // snapshot repair and its automatic fast reconnect are legacy-room mechanics. In the new
        // mode HP and death are replica facts and recovery is an explicit scope resync (A20).
        TestAssert.IsTrue(NebulaWorld.Authority.ScopeRecoveryPolicy.MustNotQueryEnemyState(isHostAuthority: true));
        TestAssert.IsTrue(NebulaWorld.Authority.ScopeRecoveryPolicy.MustNotAutoReconnectOnSnapshotFailure(isHostAuthority: true));
        TestAssert.IsTrue(NebulaWorld.Authority.ScopeRecoveryPolicy.ShouldRefuseLegacyEnemyStateRequest(isHostAuthority: true));
        TestAssert.IsFalse(NebulaWorld.Authority.ScopeRecoveryPolicy.MustNotQueryEnemyState(isHostAuthority: false));
        TestAssert.IsFalse(NebulaWorld.Authority.ScopeRecoveryPolicy.MustNotAutoReconnectOnSnapshotFailure(isHostAuthority: false));
        TestAssert.IsFalse(NebulaWorld.Authority.ScopeRecoveryPolicy.ShouldRefuseLegacyEnemyStateRequest(isHostAuthority: false));
    }

    // --- ScopeDigestComputer: the comparison itself. ---

    [TestMethod]
    public void TheDigestIsOrderIndependentAndDetectsEveryDifference()
    {
        var a = new List<ScopeDigestMember>
        {
            new(Key(PoolKind.GroundEnemy, 101, 1, 1), 3, [10, 20]),
            new(Key(PoolKind.GroundEnemy, 101, 2, 1), 1, null)
        };
        var b = new List<ScopeDigestMember>
        {
            new(Key(PoolKind.GroundEnemy, 101, 2, 1), 1, null),
            new(Key(PoolKind.GroundEnemy, 101, 1, 1), 3, [10, 20])
        };
        TestAssert.AreEqual(ScopeDigestComputer.Compute(a), ScopeDigestComputer.Compute(b),
            "Dictionary enumeration order must not change the digest.");

        TestAssert.AreNotEqual(ScopeDigestComputer.Compute(a), ScopeDigestComputer.Compute(new List<ScopeDigestMember>
        {
            new(Key(PoolKind.GroundEnemy, 101, 1, 1), 3, [10, 21])
        }), "A changed state byte is a divergence.");

        TestAssert.AreNotEqual(ScopeDigestComputer.Compute(a), ScopeDigestComputer.Compute(new List<ScopeDigestMember>
        {
            new(Key(PoolKind.GroundEnemy, 101, 1, 1), 4, [10, 20])
        }), "A changed revision is a divergence.");

        TestAssert.AreNotEqual(ScopeDigestComputer.Compute(a), ScopeDigestComputer.Compute(new List<ScopeDigestMember>
        {
            new(Key(PoolKind.GroundEnemy, 101, 1, 1), 3, [10, 20]),
            new(Key(PoolKind.GroundEnemy, 101, 2, 1), 1, [1])
        }), "Identity-without-state and identity-with-state are different facts.");

        TestAssert.AreNotEqual(ScopeDigestComputer.Compute(a), ScopeDigestComputer.Compute(new List<ScopeDigestMember>
        {
            new(Key(PoolKind.GroundEnemy, 101, 1, 1), 3, [10, 20])
        }), "A missing member is a divergence — this is what a lost spawn looks like.");

        TestAssert.AreNotEqual(ScopeDigestComputer.Compute(a), ScopeDigestComputer.Compute(new List<ScopeDigestMember>
        {
            new(Key(PoolKind.GroundEnemy, 101, 2, 1), 3, [10, 20]),
            new(Key(PoolKind.GroundEnemy, 101, 1, 1), 1, null)
        }), "A recycled slot is a different key, not the same member.");
    }
}
