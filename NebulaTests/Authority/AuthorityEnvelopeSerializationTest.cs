using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaModel.Networking;
using NebulaModel.Networking.Serialization;
using NebulaModel.Packets.Authority;
using NebulaModel.Packets.Session;
using NebulaWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A03: the envelope DTOs survive the real network serializer, and the session starts in legacy.
/// </summary>
/// <remarks>
/// The gate tests drive the model directly. These tests cover the seam the model cannot see: that
/// the DTO field order the serializer derives from the properties round-trips, so a wire field is
/// not silently dropped when the packet crosses a real socket.
/// </remarks>
[TestClass]
public class AuthorityEnvelopeSerializationTest
{
    private static readonly AuthorityEpoch Epoch = new(0xDEADBEEFCAFEBABE, 0x0123456789ABCDEF);

    private static void RoundTrip<T>(T sent, out T received) where T : class, new()
    {
        var processor = new NebulaNetPacketProcessor();
        T captured = null!;
        processor.SubscribeReusable<T>(packet => captured = packet);
        var writer = new NetDataWriter();
        processor.Write(writer, sent);
        processor.ReadAllPackets(new NetDataReader(writer));
        received = captured;
    }

    [TestMethod]
    public void ACommandEnvelopeRoundTripsEveryHeaderFieldAndItsPayload()
    {
        var target = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 4);
        var header = new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.Command, Epoch,
            new ConnectionEpoch(9), sequence: 42, hostTick: 1234, claimedPlayerId: 3, payloadLength: 5);
        var sent = AuthorityCommandPacket.Create(header, target, category: 2, payload: [1, 2, 3, 4, 5]);

        RoundTrip(sent, out var received);
        TestAssert.IsNotNull(received);
        TestAssert.AreEqual((byte)AuthoritySchema.V1, received.Schema);
        TestAssert.AreEqual(Epoch.High, received.EpochHigh);
        TestAssert.AreEqual(Epoch.Low, received.EpochLow);
        TestAssert.AreEqual(9UL, received.ConnectionEpoch);
        TestAssert.AreEqual(42L, received.Sequence);
        TestAssert.AreEqual(1234L, received.HostTick);
        TestAssert.AreEqual((ushort)3, received.ClaimedPlayerId);
        TestAssert.AreEqual((byte)PoolKind.GroundEnemy, received.TargetKind);
        TestAssert.AreEqual(101, received.TargetScope);
        TestAssert.AreEqual(7, received.TargetNativeId);
        TestAssert.AreEqual(4L, received.TargetGeneration);
        TestAssert.AreEqual((byte)2, received.Category);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, received.Payload);
        TestAssert.AreEqual(AuthorityFamily.Command, received.Family);
        TestAssert.AreEqual(5, received.DeclaredPayloadLength);
    }

    [TestMethod]
    public void ACommandTargetReassemblesIntoTheOriginalKey()
    {
        var target = ObjectKey.Create(Epoch, PoolKind.SpaceCraft, 1000007, 3, 8);
        var header = new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.Command, Epoch,
            new ConnectionEpoch(1), 1, 1, 1, 0);
        var sent = AuthorityCommandPacket.Create(header, target, 0, []);
        RoundTrip(sent, out var received);
        TestAssert.IsTrue(received.TryGetTargetKey(out var key));
        TestAssert.AreEqual(target, key);
    }

    [TestMethod]
    public void ASnapshotBeginRoundTripsItsScopeAndBaseline()
    {
        var scope = new ScopeKey(PoolKind.GroundEnemy, 204899);
        var header = new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.SnapshotBegin, Epoch,
            default, 3, 77, 0, 0);
        var sent = AuthoritySnapshotBeginPacket.Create(header, scope, baselineId: 12, subscriptionEpoch: 2,
            cutoffSequence: 500, totalBytes: 4096, chunkCount: 4, snapshotHash: 0xABCDEF);

        RoundTrip(sent, out var received);
        TestAssert.IsTrue(received.TryGetScopeKey(out var readScope));
        TestAssert.AreEqual(scope, readScope);
        TestAssert.AreEqual(12L, received.BaselineId);
        TestAssert.AreEqual(2L, received.SubscriptionEpoch);
        TestAssert.AreEqual(500L, received.CutoffSequence);
        TestAssert.AreEqual(4096L, received.TotalBytes);
        TestAssert.AreEqual(4, received.ChunkCount);
        TestAssert.AreEqual(0xABCDEFUL, received.SnapshotHash);
    }

    [TestMethod]
    public void ASnapshotChunkRoundTripsItsBytesAndDeclaresItsLength()
    {
        var header = new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.SnapshotChunk, Epoch,
            default, 4, 78, 0, 0);
        var data = new byte[1024];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)i;
        var sent = AuthoritySnapshotChunkPacket.Create(header, new ScopeKey(PoolKind.GroundEnemy, 101), baselineId: 12, subscriptionEpoch: 1, chunkIndex: 3, data);

        RoundTrip(sent, out var received);
        TestAssert.AreEqual(12L, received.BaselineId);
        TestAssert.AreEqual(3, received.ChunkIndex);
        CollectionAssert.AreEqual(data, received.Data);
        TestAssert.AreEqual(1024, received.DeclaredPayloadLength);
    }

    [TestMethod]
    public void AWorldStateEnvelopeRoundTripsItsBlobAndScope()
    {
        var scope = new ScopeKey(PoolKind.GroundEnemy, 101);
        var header = new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.WorldState, Epoch,
            default, 5, 79, 0, 0);
        var sent = AuthorityWorldStatePacket.Create(header, scope, declaredBaselineId: 12, recordCount: 1,
            data: [9, 8, 7]);

        RoundTrip(sent, out var received);
        TestAssert.IsTrue(received.TryGetScopeKey(out var readScope));
        TestAssert.AreEqual(scope, readScope);
        TestAssert.AreEqual(12L, received.DeclaredBaselineId);
        TestAssert.AreEqual(1, received.RecordCount);
        CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, received.Data);
        TestAssert.IsTrue(received.AreRecordsConsistent());
    }

    [TestMethod]
    public void ACommandResultAndAckRoundTrip()
    {
        var header = new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.CommandResult, Epoch,
            new ConnectionEpoch(2), 6, 80, 0, 0);
        var outcome = new CommandOutcome(CommandResultCode.Applied, appliedHostTick: 81, transactionId: 900,
            resourceRevision: 44);
        RoundTrip(AuthorityCommandResultPacket.Create(header, outcome), out var result);
        TestAssert.AreEqual((byte)CommandResultCode.Applied, result.ResultCode);
        TestAssert.AreEqual(81L, result.AppliedHostTick);
        TestAssert.AreEqual(900L, result.TransactionId);
        TestAssert.AreEqual(44L, result.ResourceRevision);

        var ackHeader = new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.SnapshotAck, Epoch,
            new ConnectionEpoch(2), 7, 82, 0, 0);
        RoundTrip(AuthoritySnapshotAckPacket.Create(ackHeader, baselineId: 12, lastAppliedSequence: 99,
            accepted: true), out var ack);
        TestAssert.AreEqual(12L, ack.BaselineId);
        TestAssert.AreEqual(99L, ack.LastAppliedSequence);
        TestAssert.IsTrue(ack.Accepted);
    }

    [TestMethod]
    public void TheSessionStartsWithLegacyAndFollowsTheNegotiatedMode()
    {
        // This default is the reason the new protocol cannot affect an existing room: until A04
        // begins a real authority world, the gate refuses every authority packet.
        var session = new AuthoritySessionState();
        TestAssert.AreEqual(AuthorityMode.Legacy, session.Mode);
        TestAssert.IsFalse(session.IsActive);

        session.OnPeerNegotiated(AuthorityMode.HostAuthority);
        TestAssert.AreEqual(AuthorityMode.HostAuthority, session.Mode);
        TestAssert.IsFalse(session.IsActive, "An authority mode without a world epoch is not an active session.");

        session.BeginAuthorityWorld(Epoch, isHost: true);
        session.SetConnection(new ConnectionEpoch(4), 2);
        TestAssert.IsTrue(session.IsActive);
        TestAssert.AreEqual(Epoch, session.Context.Epoch);
        TestAssert.AreEqual((ushort)2, session.Context.ConnectedPlayerId);

        // A legacy peer must not be able to leave the session half-negotiated.
        session.OnPeerNegotiated(AuthorityMode.Legacy);
        TestAssert.AreEqual(AuthorityMode.Legacy, session.Mode);
        TestAssert.IsFalse(session.IsActive);
    }

    [TestMethod]
    public void AnInvalidWorldEpochIsRefused()
    {
        var session = new AuthoritySessionState();
        session.BeginAuthorityWorld(default, isHost: true);
        TestAssert.IsFalse(session.IsActive, "A zero epoch is not a world identity.");
    }

    [TestMethod]
    public void ResettingReturnsToLegacyAndClearsTheConnection()
    {
        var session = new AuthoritySessionState();
        session.OnPeerNegotiated(AuthorityMode.HostAuthority);
        session.BeginAuthorityWorld(Epoch, isHost: false);
        session.SetConnection(new ConnectionEpoch(4), 2);

        session.Reset();
        TestAssert.AreEqual(AuthorityMode.Legacy, session.Mode);
        TestAssert.AreEqual(AuthoritySchema.None, session.Schema);
        TestAssert.IsFalse(session.Context.Epoch.IsValid);
        TestAssert.IsFalse(session.Context.Connection.IsValid);
        TestAssert.AreEqual((ushort)0, session.Context.ConnectedPlayerId);
    }

    [TestMethod]
    public void AHostChecksTheClaimedIdAgainstTheConnectionItArrivedOn()
    {
        // DESIGN 4.1: on the host each connection has its own player id, so the gate must compare
        // against the connection's id rather than one session-wide value. Otherwise a client could
        // claim another player's seat and have the command attributed to them.
        // A real authority room is hosted by an installation configured for authority mode; the
        // session schema follows that configuration rather than being invented per session.
        var session = new AuthoritySessionState();
        session.OnPeerNegotiated(AuthorityMode.HostAuthority);
        session.BeginAuthorityWorld(Epoch, isHost: true);
        session.SetConnection(new ConnectionEpoch(4), 1);

        var contextForPlayer2 = session.ContextForConnection(2);
        TestAssert.AreEqual((ushort)2, contextForPlayer2.ConnectedPlayerId);
        TestAssert.IsTrue(contextForPlayer2.IsHost);
        TestAssert.AreEqual(AuthoritySchema.V1, contextForPlayer2.Schema);

        var header = new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.Command, Epoch,
            new ConnectionEpoch(4), 1, 1, claimedPlayerId: 2, payloadLength: 0);
        TestAssert.AreEqual(AuthorityRejectCode.None,
            AuthorityEnvelopeGate.Validate(contextForPlayer2, AuthorityDirection.ClientToServer, header).Code,
            "A command claiming the connection's own id is accepted.");

        var headerForPlayer3 = new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.Command, Epoch,
            new ConnectionEpoch(4), 1, 1, claimedPlayerId: 3, payloadLength: 0);
        TestAssert.AreEqual(AuthorityRejectCode.ForgedPlayer,
            AuthorityEnvelopeGate.Validate(contextForPlayer2, AuthorityDirection.ClientToServer, headerForPlayer3)
                .Code,
            "The same packet claiming another seat must be refused.");

        // A client has only its own identity, so the per-connection form is the session form there.
        var client = new AuthoritySessionState();
        client.OnPeerNegotiated(AuthorityMode.HostAuthority);
        client.BeginAuthorityWorld(Epoch, isHost: false);
        client.SetConnection(new ConnectionEpoch(4), 7);
        TestAssert.AreEqual((ushort)7, client.ContextForConnection(2).ConnectedPlayerId);
    }

    [TestMethod]
    public void TheHandshakeResponseCarriesTheConfirmedMode()
    {
        // The client asserts the host agreed to the same mode, so a host that answers differently is
        // caught at the handshake instead of surfacing as two rule sets in one room later. The host
        // sets the field explicitly from the declaration it actually accepted, and an unstated mode
        // stays zero, which the client treats as a protocol error rather than as legacy.
        var sent = new HandshakeResponse();
        TestAssert.AreEqual((byte)AuthorityMode.None, sent.AuthorityMode);

        sent.AuthorityMode = (byte)AuthorityMode.HostAuthority;
        TestAssert.AreEqual((byte)AuthorityMode.HostAuthority, sent.AuthorityMode);
    }

    [TestMethod]
    public void EveryFamilyDeclaresADirectionAndAPayloadCeiling()
    {
        // A family with no direction would be undecidable at the gate; the enumerations must cover
        // the same set so no family can be added to one and forgotten in the other.
        foreach (AuthorityFamily family in System.Enum.GetValues(typeof(AuthorityFamily)))
        {
            if (family == AuthorityFamily.None) continue;
            TestAssert.IsTrue(AuthorityFamilyDirection.TryGetDirection(family, out _),
                "Family " + family + " has no direction.");
        }
        TestAssert.IsFalse(AuthorityFamilyDirection.TryGetDirection(AuthorityFamily.None, out _));

        TestAssert.AreEqual(AuthorityLimits.CommandPayloadMaxBytes,
            AuthorityLimits.PayloadMaxBytes(AuthorityFamily.Command));
        TestAssert.AreEqual(AuthorityLimits.ChunkMaxBytes,
            AuthorityLimits.PayloadMaxBytes(AuthorityFamily.SnapshotChunk));
    }
}
