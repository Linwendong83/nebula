using System;
using System.Collections.Generic;
using System.Reflection;
using NebulaModel.Authority;
using NebulaModel.Packets.Authority;
using NebulaWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A04: a replica write is only legal inside an open, scope-checked apply.
/// </summary>
/// <remarks>
/// The invariant under test is DESIGN 6's "ReplicaApplyContext 仅允许适配器写镜像": an idle session
/// allows no write, an apply allows exactly its own scope, and an exception inside an apply leaves
/// the context closed rather than stuck open. The type is pure, so this runs without a game.
/// </remarks>
[TestClass]
public class ReplicaApplyContextTest
{
    private static readonly ScopeKey Planet101 = new(PoolKind.GroundEnemy, 101);
    private static readonly ScopeKey Planet102 = new(PoolKind.GroundEnemy, 102);

    [TestMethod]
    public void AnIdleContextAllowsNoWrite()
    {
        var context = new ReplicaApplyContext();
        TestAssert.IsFalse(context.IsActive);
        TestAssert.IsFalse(context.Allows(new ApplyScope(Planet101)),
            "A write outside an apply must be refused, not accepted because the session is in authority mode.");
    }

    [TestMethod]
    public void AnApplyAllowsOnlyItsOwnScope()
    {
        var context = new ReplicaApplyContext();
        using (context.Enter(new ApplyScope(Planet101)))
        {
            TestAssert.IsTrue(context.IsActive);
            TestAssert.IsTrue(context.Allows(new ApplyScope(Planet101)));
            TestAssert.IsFalse(context.Allows(new ApplyScope(Planet102)),
                "An apply may not write a scope it was not opened for.");
        }
        TestAssert.IsFalse(context.IsActive);
        TestAssert.IsFalse(context.Allows(new ApplyScope(Planet101)), "Closing the apply must close the window.");
    }

    [TestMethod]
    public void AnExceptionInsideAnApplyStillClosesIt()
    {
        // This is the rule that stops a failed adapter from leaving the whole session writable.
        var context = new ReplicaApplyContext();
        try
        {
            context.Run(new ApplyScope(Planet101), () => throw new InvalidOperationException("adapter failed"));
        }
        catch (InvalidOperationException)
        {
            // expected
        }

        TestAssert.IsFalse(context.IsActive, "A failed apply must not leave the context open.");
        TestAssert.IsFalse(context.Allows(new ApplyScope(Planet101)));
    }

    [TestMethod]
    public void NestingKeepsTheInnermostScopeAndRestoresTheOuterOne()
    {
        var context = new ReplicaApplyContext();
        using (context.Enter(new ApplyScope(Planet101)))
        {
            using (context.Enter(new ApplyScope(Planet102)))
            {
                TestAssert.IsTrue(context.Allows(new ApplyScope(Planet102)));
                TestAssert.IsFalse(context.Allows(new ApplyScope(Planet101)),
                    "The innermost apply decides what may be written.");
            }
            TestAssert.IsTrue(context.Allows(new ApplyScope(Planet101)),
                "Leaving the nested apply must restore the outer scope.");
        }
        TestAssert.IsFalse(context.IsActive);
    }

    [TestMethod]
    public void ASecondThreadMayNotOpenAnApplyWhileOneIsRunning()
    {
        // The socket thread must not be able to sneak a write in by opening its own apply while the
        // frame thread is mid-apply. Failing loudly is the point: a silent block would hide the bug.
        var context = new ReplicaApplyContext();
        Exception? caught = null;
        using (context.Enter(new ApplyScope(Planet101)))
        {
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    context.Enter(new ApplyScope(Planet102)).Dispose();
                }
                catch (Exception e)
                {
                    caught = e;
                }
            });
            thread.Start();
            thread.Join();
        }

        TestAssert.IsNotNull(caught, "A cross-thread apply must be refused.");
        TestAssert.IsInstanceOfType<InvalidOperationException>(caught);
    }

    [TestMethod]
    public void TheCurrentScopeIsReportedForDiagnostics()
    {
        var context = new ReplicaApplyContext();
        var scope = new ApplyScope(Planet101, transactionId: 42, streamSequence: 9, hostTick: 1234);
        using (context.Enter(scope))
        {
            TestAssert.AreEqual(Planet101, context.Current.Scope);
            TestAssert.AreEqual(42L, context.Current.TransactionId);
            TestAssert.AreEqual(1234L, context.Current.HostTick);
        }
        TestAssert.IsFalse(context.Current.Scope.IsValid);
    }
}

/// <summary>
/// A04: the session runtime owns identity, queues and the frame boundary together.
/// </summary>
[TestClass]
public class AuthoritySessionTest
{
    private static readonly AuthorityEpoch Epoch = new(0xABCDEF0123456789, 0x0FEDCBA987654321);
    private static readonly ConnectionEpoch Connection = new(5);

    private static AuthoritySession HostSession()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: true);
        return session;
    }

    [TestMethod]
    public void ALegacySessionIsInert()
    {
        // The default state of every build before A25. Nothing may drain, capture or accept.
        var session = new AuthoritySession(new AuthoritySessionState());
        TestAssert.IsFalse(session.Identity.IsActive);
        TestAssert.IsNull(session.Commands);
        TestAssert.IsFalse(session.TryEnqueueHostCommand(new CommandKey(Epoch, Connection, 1),
            Command(1), 3, 1, 10));

        session.OnFrameBoundary(1);
        TestAssert.AreEqual(0L, session.FramesDrained);
    }

    [TestMethod]
    public void BeginningAWorldOnTheHostOpensTheCommandInbox()
    {
        var session = HostSession();
        TestAssert.IsTrue(session.Identity.IsActive);
        TestAssert.IsTrue(session.IsHostAuthority);
        TestAssert.IsNotNull(session.Commands);
    }

    [TestMethod]
    public void AClientHasNoCommandInbox()
    {
        // A client sends intents; it must not be able to accept host work.
        var session = new AuthoritySession(new AuthoritySessionState());
        session.Identity.OnPeerNegotiated(AuthorityMode.HostAuthority);
        session.BeginAuthorityWorld(Epoch, isHost: false);

        TestAssert.IsTrue(session.Identity.IsActive);
        TestAssert.IsFalse(session.IsHostAuthority);
        TestAssert.IsNull(session.Commands);
        TestAssert.IsFalse(session.TryEnqueueHostCommand(new CommandKey(Epoch, Connection, 1), Command(1), 3, 1, 10));
    }

    [TestMethod]
    public void AnInvalidEpochIsRefused()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(default, isHost: true);
        TestAssert.IsFalse(session.Identity.IsActive);
        TestAssert.IsNull(session.Commands);
    }

    [TestMethod]
    public void TheSameEpochIsNotRebegun()
    {
        // A duplicated world-load notification must not mint a second identity or clear a live queue.
        var session = HostSession();
        session.TryEnqueueHostCommand(new CommandKey(Epoch, Connection, 1), Command(1), 3, 1, 10);
        TestAssert.AreEqual(1, session.Commands.Count);

        session.BeginAuthorityWorld(Epoch, isHost: true);
        TestAssert.AreEqual(1, session.Commands.Count, "Re-beginning the same world must keep the inbox.");
    }

    [TestMethod]
    public void ANewWorldClearsThePreviousWorldsQueue()
    {
        var session = HostSession();
        session.TryEnqueueHostCommand(new CommandKey(Epoch, Connection, 1), Command(1), 3, 1, 10);
        TestAssert.AreEqual(1, session.Commands.Count);

        var nextEpoch = new AuthorityEpoch(3, 4);
        session.BeginAuthorityWorld(nextEpoch, isHost: true);
        TestAssert.AreEqual(0, session.Commands.Count, "Nothing from the old world may survive a reload.");
        TestAssert.AreEqual(nextEpoch, session.Identity.Epoch);
    }

    [TestMethod]
    public void ConnectionEpochsAreMonotonicAndPerPlayer()
    {
        // A reconnect must never reuse the previous connection's dedup space.
        var session = HostSession();
        var first = session.AssignConnectionEpoch(3);
        var reconnected = session.AssignConnectionEpoch(3);
        TestAssert.IsTrue(first.IsValid);
        TestAssert.AreNotEqual(first, reconnected, "A reconnect must get a fresh connection epoch.");
        TestAssert.AreEqual(reconnected, session.ConnectionEpochFor(3));

        var other = session.AssignConnectionEpoch(4);
        TestAssert.AreNotEqual(reconnected, other);
        TestAssert.AreEqual(other, session.ConnectionEpochFor(4));
        TestAssert.AreEqual(reconnected, session.ConnectionEpochFor(3), "One player's epoch must not overwrite another's.");
    }

    [TestMethod]
    public void DisconnectingAPlayerRetiresTheirConnectionEpoch()
    {
        // A reconnect must get a fresh dedup space, so the old epoch is dropped with the connection
        // rather than left mapped for a later message to match.
        var session = HostSession();
        session.AssignConnectionEpoch(3);
        session.AssignConnectionEpoch(4);
        var connectionForThree = session.ConnectionEpochFor(3);

        session.TryEnqueueHostCommand(new CommandKey(Epoch, connectionForThree, 1), Command(1), 3, 1, 10);

        session.ForgetPlayerConnection(3);
        TestAssert.IsFalse(session.ConnectionEpochFor(3).IsValid, "The retired epoch must not resolve any more.");
        TestAssert.IsTrue(session.ConnectionEpochFor(4).IsValid, "Another player's epoch must be untouched.");
        TestAssert.AreEqual(0, session.Commands.Count, "A departed player's queued commands must be dropped.");
    }

    [TestMethod]
    public void ResettingReturnsToLegacyAndDropsEverything()
    {
        var session = HostSession();
        session.AssignConnectionEpoch(3);
        session.TryEnqueueHostCommand(new CommandKey(Epoch, Connection, 1), Command(1), 3, 1, 10);

        session.Reset();
        TestAssert.IsFalse(session.Identity.IsActive);
        TestAssert.IsNull(session.Commands);
        TestAssert.IsFalse(session.Identity.Epoch.IsValid);
        TestAssert.IsFalse(session.ConnectionEpochFor(3).IsValid);
    }

    [TestMethod]
    public void AReplicaMessageIsQueuedAndNeverAppliedOnTheSocketThread()
    {
        // The receive path must only store. Applying here would run the replica on the socket thread.
        var session = new AuthoritySession(new AuthoritySessionState());
        session.Identity.OnPeerNegotiated(AuthorityMode.HostAuthority);
        session.BeginAuthorityWorld(Epoch, isHost: false);
        session.SetConnection(Connection, 1);

        var applier = new RecordingApplier();
        session.ReplicaApplier = applier;
        var packet = StatePacket();
        TestAssert.IsTrue(session.TryEnqueueReplicaMessage(packet, new ApplyScope(new ScopeKey(PoolKind.GroundEnemy, 101))));
        TestAssert.AreEqual(1, session.InboundCount);
        TestAssert.AreEqual(0, applier.Applied, "Enqueue must not apply anything on the socket thread.");

        session.OnFrameBoundary(500);
        TestAssert.AreEqual(1, applier.Applied, "The frame boundary applies the queued message.");
        TestAssert.AreEqual(0, session.InboundCount);
        TestAssert.AreEqual(1L, session.InboundApplied);
    }

    [TestMethod]
    public void AFailedReplicaApplyDoesNotLeaveTheSessionWritable()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        session.Identity.OnPeerNegotiated(AuthorityMode.HostAuthority);
        session.BeginAuthorityWorld(Epoch, isHost: false);

        session.ReplicaApplier = new RecordingApplier { ThrowOnApply = true };
        session.TryEnqueueReplicaMessage(StatePacket(), new ApplyScope(new ScopeKey(PoolKind.GroundEnemy, 101)));

        session.OnFrameBoundary(500);

        TestAssert.IsFalse(session.ApplyContext.IsActive, "A failed apply must not leave the context open.");
        TestAssert.AreEqual(1L, session.InboundDropped);
        TestAssert.IsFalse(session.ApplyContext.Allows(new ApplyScope(new ScopeKey(PoolKind.GroundEnemy, 101))));
    }

    [TestMethod]
    public void AMessageWithoutAnApplierIsRefusedRatherThanApplied()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        session.Identity.OnPeerNegotiated(AuthorityMode.HostAuthority);
        session.BeginAuthorityWorld(Epoch, isHost: false);
        session.TryEnqueueReplicaMessage(StatePacket(), new ApplyScope(new ScopeKey(PoolKind.GroundEnemy, 101)));

        session.OnFrameBoundary(500);
        TestAssert.AreEqual(1L, session.InboundDropped);
        TestAssert.AreEqual(0L, session.InboundApplied);
    }

    [TestMethod]
    public void TheReplicaInboxAppliesBackPressure()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        session.Identity.OnPeerNegotiated(AuthorityMode.HostAuthority);
        session.BeginAuthorityWorld(Epoch, isHost: false);

        var scope = new ApplyScope(new ScopeKey(PoolKind.GroundEnemy, 101));
        for (var i = 0; i < AuthorityLimits.CommandQueueMax; i++)
        {
            TestAssert.IsTrue(session.TryEnqueueReplicaMessage(StatePacket(), scope));
        }
        TestAssert.IsFalse(session.TryEnqueueReplicaMessage(StatePacket(), scope),
            "A full replica inbox must refuse rather than grow without bound.");
        TestAssert.AreEqual(1L, session.InboundDropped);
    }

    [TestMethod]
    public void ARepeatedFrameBoundaryIsIdempotent()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        session.Identity.OnPeerNegotiated(AuthorityMode.HostAuthority);
        session.BeginAuthorityWorld(Epoch, isHost: false);
        var applier = new RecordingApplier();
        session.ReplicaApplier = applier;
        session.TryEnqueueReplicaMessage(StatePacket(), new ApplyScope(new ScopeKey(PoolKind.GroundEnemy, 101)));

        session.OnFrameBoundary(500);
        session.OnFrameBoundary(500);
        TestAssert.AreEqual(1, applier.Applied, "A duplicated frame notification must not apply twice.");
    }

    [TestMethod]
    public void TheOtherPlayersMessageKeepsItsOwnConnectionEpoch()
    {
        // The gate now resolves a per-connection epoch, so a session-wide value would accept a
        // replayed sequence from a reconnecting client.
        var session = HostSession();
        var forThree = session.AssignConnectionEpoch(3);
        var forFour = session.AssignConnectionEpoch(4);
        TestAssert.AreEqual(forThree, session.ConnectionEpochFor(3));
        TestAssert.AreEqual(forFour, session.ConnectionEpochFor(4));

        var contextForThree = session.Identity.ContextForConnection(3, forThree);
        var contextForFour = session.Identity.ContextForConnection(4, forFour);
        TestAssert.AreEqual(forThree, contextForThree.Connection);
        TestAssert.AreEqual(forFour, contextForFour.Connection);
        TestAssert.AreNotEqual(contextForThree.Connection, contextForFour.Connection);
    }

    private static AuthorityCommandPacket Command(long sequence) =>
        AuthorityCommandPacket.Create(
            new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.Command, Epoch, Connection, sequence,
                hostTick: 100, claimedPlayerId: 3, payloadLength: 0),
            ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1), category: 1, payload: []);

    private static AuthorityWorldStatePacket StatePacket() =>
        AuthorityWorldStatePacket.Create(
            new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.WorldState, Epoch, Connection, 1,
                hostTick: 100, claimedPlayerId: 0, payloadLength: 0),
            new ScopeKey(PoolKind.GroundEnemy, 101), declaredBaselineId: 1, recordCount: 0, data: [0, 0, 0, 0]);

    private sealed class RecordingApplier : IReplicaMessageApplier
    {
        public int Applied { get; private set; }

        public bool ThrowOnApply { get; set; }

        public bool Apply(in PendingReplicaMessage message)
        {
            if (ThrowOnApply) throw new InvalidOperationException("adapter failed");
            Applied++;
            return true;
        }
    }
}

/// <summary>
/// A04: the world model still has no Unity or game dependency after the runtime was added.
/// </summary>
/// <remarks>
/// A04 puts a session manager next to the model. This test is the executable form of the rule that
/// the model layer stays engine-free, so a later card cannot quietly add a GameMain reference to a
/// type the tests are supposed to be able to drive directly.
/// </remarks>
[TestClass]
public class AuthorityModelPurityTest
{
    [TestMethod]
    public void TheAuthorityModelDoesNotReferenceUnityOrGameTypes()
    {
        var assembly = typeof(AuthorityEpoch).Assembly;
        var violations = new List<string>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.Namespace == null || !type.Namespace.StartsWith("NebulaModel.Authority", StringComparison.Ordinal))
            {
                continue;
            }
            CheckMembers(type, violations);
        }
        TestAssert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    private static void CheckMembers(Type type, List<string> violations)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                   BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var method in type.GetMethods(flags))
        {
            CheckReferences(type, method.ReturnType, violations);
            foreach (var parameter in method.GetParameters()) CheckReferences(type, parameter.ParameterType, violations);
        }
        foreach (var field in type.GetFields(flags)) CheckReferences(type, field.FieldType, violations);
        foreach (var property in type.GetProperties(flags)) CheckReferences(type, property.PropertyType, violations);
    }

    private static void CheckReferences(Type owner, Type referenced, List<string> violations)
    {
        if (referenced == null) return;
        if (referenced.IsGenericType)
        {
            foreach (var argument in referenced.GetGenericArguments()) CheckReferences(owner, argument, violations);
        }
        if (referenced.IsArray) CheckReferences(owner, referenced.GetElementType(), violations);

        var name = referenced.Namespace ?? string.Empty;
        if (name.StartsWith("UnityEngine", StringComparison.Ordinal) ||
            name.StartsWith("Unity", StringComparison.Ordinal) ||
            name.StartsWith("NebulaWorld", StringComparison.Ordinal) ||
            name.StartsWith("NebulaNetwork", StringComparison.Ordinal) ||
            referenced.Name == "GameMain" || referenced.Name == "GameLogic" || referenced.Name == "PlanetFactory")
        {
            violations.Add(owner.FullName + " references " + referenced.FullName);
        }
    }
}
