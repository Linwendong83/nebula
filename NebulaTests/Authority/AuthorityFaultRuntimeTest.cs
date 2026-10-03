#region

using System;
using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaModel.Packets.Authority;
using NebulaWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

#endregion

namespace NebulaTests.Authority;

/// <summary>
/// A22 (part 4): the runtime fault link. When the process armed
/// <see cref="AuthorityFaultControl"/>, the host's replication delivery goes through the same
/// deterministic <see cref="FaultyAuthorityLink"/> the model harness tested, and the frame boundary
/// pumps one delivery cycle per frame — so a delayed packet arrives a whole boundary late and a
/// paused one when the driver ends the pause. Without the flag the replicator talks to the network
/// directly, byte for byte.
/// </summary>
[TestClass]
public class AuthorityFaultRuntimeTest
{
    private static readonly AuthorityEpoch Epoch = new(0x1824182418241824, 0x2482482482482482);
    private static readonly ScopeKey Scope = new(PoolKind.GroundEnemy, 101);

    [TestCleanup]
    public void DisarmTheFaultControl()
    {
        AuthorityFaultControl.Configure(null);
    }

    private sealed class FakeWorld : IHostWorldView
    {
        public readonly List<ObjectKey> Members = [];
        public readonly Dictionary<ObjectKey, byte[]> States = [];

        public void Add(int nativeId, byte[] state)
        {
            var key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, nativeId, 1);
            Members.Add(key);
            if (state != null) States[key] = state;
        }

        public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
        {
            members.AddRange(Members);
            return true;
        }

        public bool TryReadState(ObjectKey key, out byte[] state) => States.TryGetValue(key, out state!);
    }

    private sealed class RecordingSink : IReplicationSink
    {
        public readonly List<AuthorityEnvelopePacket> Sent = [];

        public void Send(ushort subscriberId, AuthorityEnvelopePacket packet) => Sent.Add(packet);
    }

    private static AuthoritySession NewHostSession(FakeWorld world, IReplicationSink sink)
    {
        var identity = new AuthoritySessionState();
        identity.OnPeerNegotiated(AuthorityMode.HostAuthority);
        var session = new AuthoritySession(identity);
        session.BeginAuthorityWorld(Epoch, isHost: true);
        TestAssert.IsTrue(session.RegisterHostReplication(world, sink));
        return session;
    }

    [TestMethod]
    public void AFaultLinkDelaysDeliveryByWholeFrameBoundaries()
    {
        // delay=2: a packet produced at boundary 1 becomes deliverable at boundary 2's pump.
        // (The boundary that produces a packet counts as its first cycle — the pump runs after
        // capture, so delay=1 delivers in the producing boundary itself.)
        var rules = new FaultInjectionRules { DelayPumps = 2 };
        AuthorityFaultControl.Configure(rules);
        var world = new FakeWorld();
        world.Add(1, [80]);
        var downstream = new RecordingSink();
        var link = new FaultyAuthorityLink(rules, downstream);
        var session = NewHostSession(world, link);
        session.SetFaultLink(link);
        session.HostReplicator.Subscribe(1, Scope);

        session.OnFrameComplete(100);
        TestAssert.IsEmpty(downstream.Sent, "A delayed packet does not reach the network this boundary.");

        session.OnFrameComplete(101);
        TestAssert.HasCount(3, downstream.Sent, "Begin, chunk and commit arrive at the next boundary.");
        TestAssert.IsInstanceOfType(downstream.Sent[0], typeof(AuthoritySnapshotBeginPacket));
        TestAssert.IsNotNull(session.FaultLink);
    }

    [TestMethod]
    public void APausedLinkHoldsDeliveryUntilTheDriverEndsThePause()
    {
        var rules = new FaultInjectionRules { Paused = true };
        AuthorityFaultControl.Configure(rules);
        var world = new FakeWorld();
        world.Add(1, [80]);
        var downstream = new RecordingSink();
        var link = new FaultyAuthorityLink(rules, downstream);
        var session = NewHostSession(world, link);
        session.SetFaultLink(link);
        session.HostReplicator.Subscribe(1, Scope);

        session.OnFrameComplete(100);
        session.OnFrameComplete(101);
        session.OnFrameComplete(102);
        TestAssert.IsEmpty(downstream.Sent, "The pause holds everything, across boundaries.");
        TestAssert.IsTrue(link.HeldCount > 0);

        // The driver ends the pause; the next boundary delivers in send order.
        rules.Paused = false;
        session.OnFrameComplete(103);
        TestAssert.HasCount(3, downstream.Sent);
    }

    [TestMethod]
    public void WithoutAFaultLinkDeliveryIsImmediate()
    {
        AuthorityFaultControl.Configure(null);
        var world = new FakeWorld();
        world.Add(1, [80]);
        var downstream = new RecordingSink();
        var session = NewHostSession(world, downstream);
        session.HostReplicator.Subscribe(1, Scope);

        session.OnFrameComplete(100);
        TestAssert.HasCount(3, downstream.Sent, "The un-instrumented path is unchanged.");
        TestAssert.IsNull(session.FaultLink);
    }

    [TestMethod]
    public void AWorldChangeDropsTheLinkAndTheNextWorldRearmsIt()
    {
        AuthorityFaultControl.Configure(new FaultInjectionRules());
        var world = new FakeWorld();
        world.Add(1, [80]);
        var downstream = new RecordingSink();
        var link = new FaultyAuthorityLink(AuthorityFaultControl.Rules, downstream);
        var session = NewHostSession(world, link);
        session.SetFaultLink(link);
        TestAssert.IsNotNull(session.FaultLink);

        session.BeginAuthorityWorld(new AuthorityEpoch(0x99AA99AA99AA99AA, 0xBBCCBBCCBBCCBBCC), isHost: true);
        TestAssert.IsNull(session.FaultLink,
            "The new world's registration owns the link; the old one is gone with its epoch.");
    }
}
