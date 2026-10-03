using System;
using System.Collections.Generic;
using System.Linq;
using NebulaModel.Authority;
using NebulaModel.Networking;
using NebulaModel.Networking.Serialization;
using NebulaModel.Packets.Authority;
using NebulaWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A03: the authority envelope gate and its bounded wire format.
/// </summary>
/// <remarks>
/// TASKS.md A03 requires that a mixed room, a wrong direction, a forged player, an illegal scope, a
/// negative length, an oversized fragment and an unknown schema are all refused predictably. The
/// tests below assert the specific reject code rather than only "false", so a check that starts
/// failing for a different reason cannot masquerade as the right refusal.
/// </remarks>
[TestClass]
public class AuthorityEnvelopeGateTest
{
    private static readonly AuthorityEpoch Epoch = new(0xAABBCCDD00112233, 0x445566778899AABB);
    private static readonly ConnectionEpoch Connection = new(11);

    private static AuthoritySessionContext Host(AuthorityMode mode = AuthorityMode.HostAuthority,
        AuthoritySchema schema = AuthoritySchema.V1, ushort playerId = 5) =>
        new(mode, schema, Epoch, Connection, playerId, true);

    private static AuthorityEnvelopeHeader Header(AuthorityFamily family, int payloadLength = 0,
        AuthoritySchema schema = AuthoritySchema.V1, AuthorityEpoch? epoch = null,
        ConnectionEpoch? connection = null, ushort claimedPlayerId = 5, long sequence = 1) =>
        new(schema, family, epoch ?? Epoch, connection ?? Connection, sequence, hostTick: 100, claimedPlayerId,
            payloadLength);

    [TestMethod]
    public void ALegacySessionRefusesEveryAuthorityPacket()
    {
        // Before A04 sets a real epoch, every build is in this state. If this check regressed, an
        // unfinished authority path could start acting on packets in a normal room.
        var reject = AuthorityEnvelopeGate.Validate(AuthoritySessionContext.Legacy,
            AuthorityDirection.ClientToServer, Header(AuthorityFamily.Command));
        TestAssert.AreEqual(AuthorityRejectCode.NotAuthorityMode, reject.Code);
    }

    [TestMethod]
    public void AWellFormedCommandFromTheBoundPlayerIsAccepted()
    {
        var reject = AuthorityEnvelopeGate.Validate(Host(), AuthorityDirection.ClientToServer,
            Header(AuthorityFamily.Command, payloadLength: 32));
        TestAssert.AreEqual(AuthorityRejectCode.None, reject.Code, reject.ToString());
    }

    [TestMethod]
    public void CommandsOnlyTravelClientToServer()
    {
        // A command arriving on the host as S2C, or a snapshot pushed at the host, must be refused
        // by direction rather than routed and rejected later.
        TestAssert.AreEqual(AuthorityRejectCode.WrongDirection,
            AuthorityEnvelopeGate.Validate(Host(), AuthorityDirection.ServerToClient,
                Header(AuthorityFamily.Command)).Code);

        TestAssert.AreEqual(AuthorityRejectCode.WrongDirection,
            AuthorityEnvelopeGate.Validate(Host(), AuthorityDirection.ClientToServer,
                Header(AuthorityFamily.SnapshotChunk, payloadLength: 16)).Code);

        TestAssert.AreEqual(AuthorityRejectCode.WrongDirection,
            AuthorityEnvelopeGate.Validate(Host(), AuthorityDirection.ClientToServer,
                Header(AuthorityFamily.WorldState)).Code);
    }

    [TestMethod]
    public void SnapshotAcksOnlyTravelClientToServer()
    {
        TestAssert.AreEqual(AuthorityRejectCode.None,
            AuthorityEnvelopeGate.Validate(Host(), AuthorityDirection.ClientToServer,
                Header(AuthorityFamily.SnapshotAck, payloadLength: 16)).Code);
        TestAssert.AreEqual(AuthorityRejectCode.WrongDirection,
            AuthorityEnvelopeGate.Validate(Host(), AuthorityDirection.ServerToClient,
                Header(AuthorityFamily.SnapshotAck)).Code);
    }

    [TestMethod]
    public void APlayerIdThatDoesNotMatchTheConnectionIsAForgedIdentity()
    {
        // DESIGN 4.1: identity comes from the connection, so the field in the packet is only an
        // assertion. A mismatch must be refused, never resolved in the packet's favour.
        var reject = AuthorityEnvelopeGate.Validate(Host(playerId: 5), AuthorityDirection.ClientToServer,
            Header(AuthorityFamily.Command, claimedPlayerId: 6));
        TestAssert.AreEqual(AuthorityRejectCode.ForgedPlayer, reject.Code);

        // The same id on an S2C stream is not a forgery, because the client is not claiming a seat.
        TestAssert.AreEqual(AuthorityRejectCode.None,
            AuthorityEnvelopeGate.Validate(Host(playerId: 5), AuthorityDirection.ServerToClient,
                Header(AuthorityFamily.Welcome, claimedPlayerId: 9)).Code);
    }

    [TestMethod]
    public void AMessageFromAnotherWorldEpochIsRefused()
    {
        var otherWorld = new AuthorityEpoch(1, 2);
        var reject = AuthorityEnvelopeGate.Validate(Host(), AuthorityDirection.ClientToServer,
            Header(AuthorityFamily.Command, epoch: otherWorld));
        TestAssert.AreEqual(AuthorityRejectCode.EpochMismatch, reject.Code);

        // An all-zero epoch is not a real world either, so it cannot authorize anything.
        TestAssert.AreEqual(AuthorityRejectCode.EpochMismatch,
            AuthorityEnvelopeGate.Validate(Host(), AuthorityDirection.ClientToServer,
                Header(AuthorityFamily.Command, epoch: new AuthorityEpoch(0, 0))).Code);
    }

    [TestMethod]
    public void AnUnknownSchemaIsRefused()
    {
        TestAssert.AreEqual(AuthorityRejectCode.SchemaMismatch,
            AuthorityEnvelopeGate.Validate(Host(), AuthorityDirection.ClientToServer,
                Header(AuthorityFamily.Command, schema: AuthoritySchema.None)).Code);
    }

    [TestMethod]
    public void AnUnknownFamilyIsRefused()
    {
        var reject = AuthorityEnvelopeGate.Validate(Host(), AuthorityDirection.ClientToServer,
            Header((AuthorityFamily)200));
        TestAssert.AreEqual(AuthorityRejectCode.UnknownFamily, reject.Code);
    }

    [TestMethod]
    public void ANegativeLengthIsRefusedRatherThanTreatedAsEmpty()
    {
        var reject = AuthorityEnvelopeGate.Validate(Host(), AuthorityDirection.ClientToServer,
            Header(AuthorityFamily.Command, payloadLength: -1));
        TestAssert.AreEqual(AuthorityRejectCode.NegativeLength, reject.Code);
    }

    [TestMethod]
    public void AnOversizedFragmentIsRefusedWithItsOwnCode()
    {
        // A snapshot chunk over the 64 KiB ceiling gets the fragment code, so the bulk family is
        // distinguishable from an ordinary oversized command.
        TestAssert.AreEqual(AuthorityRejectCode.FragmentTooLarge,
            AuthorityEnvelopeGate.Validate(Host(), AuthorityDirection.ServerToClient,
                Header(AuthorityFamily.SnapshotChunk, payloadLength: AuthorityLimits.ChunkMaxBytes + 1)).Code);

        // Exactly at the ceiling is still legal.
        TestAssert.AreEqual(AuthorityRejectCode.None,
            AuthorityEnvelopeGate.Validate(Host(), AuthorityDirection.ServerToClient,
                Header(AuthorityFamily.SnapshotChunk, payloadLength: AuthorityLimits.ChunkMaxBytes)).Code);
    }

    [TestMethod]
    public void AnOversizedCommandPayloadIsRefused()
    {
        TestAssert.AreEqual(AuthorityRejectCode.PayloadTooLarge,
            AuthorityEnvelopeGate.Validate(Host(), AuthorityDirection.ClientToServer,
                Header(AuthorityFamily.Command, payloadLength: AuthorityLimits.CommandPayloadMaxBytes + 1)).Code);
    }

    [TestMethod]
    public void AnIllegalScopeIsRefusedAndALegalOneAccepted()
    {
        var legal = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 3);
        TestAssert.AreEqual(AuthorityRejectCode.None,
            AuthorityEnvelopeGate.ValidateObjectKey(Host(), legal).Code);

        // A ground pool addressed with the sector scope merges every planet into one scope, which is
        // the identity bug the scope rule exists to prevent.
        var illegal = new ObjectKey(Epoch, PoolKind.GroundEnemy, AuthorityScope.Sector, 7, 3);
        TestAssert.AreEqual(AuthorityRejectCode.IllegalScope,
            AuthorityEnvelopeGate.ValidateObjectKey(Host(), illegal).Code);

        // A key from another world is an epoch problem, not a scope one.
        var foreign = new ObjectKey(new AuthorityEpoch(9, 9), PoolKind.GroundEnemy, 101, 7, 3);
        TestAssert.AreEqual(AuthorityRejectCode.EpochMismatch,
            AuthorityEnvelopeGate.ValidateObjectKey(Host(), foreign).Code);
    }

    [TestMethod]
    public void ACommandKeyMustEchoTheConnectionEpoch()
    {
        var key = new CommandKey(Epoch, Connection, 4);
        TestAssert.AreEqual(AuthorityRejectCode.None,
            AuthorityEnvelopeGate.ValidateCommandKey(Host(), key).Code);

        var replayed = new CommandKey(Epoch, new ConnectionEpoch(12), 4);
        TestAssert.AreEqual(AuthorityRejectCode.InvalidCommandKey,
            AuthorityEnvelopeGate.ValidateCommandKey(Host(), replayed).Code);
    }

    [TestMethod]
    public void TheHeaderSurvivesARoundTripAndRejectsATruncatedBuffer()
    {
        var header = Header(AuthorityFamily.WorldState, payloadLength: 4096, sequence: 77);
        var bytes = header.ToBytes();
        TestAssert.HasCount(AuthorityLimits.HeaderBytes, bytes);
        TestAssert.IsTrue(AuthorityEnvelopeHeader.TryRead(bytes, out var read, out _));
        TestAssert.AreEqual(header.Family, read.Family);
        TestAssert.AreEqual(header.Schema, read.Schema);
        TestAssert.AreEqual(header.Epoch, read.Epoch);
        TestAssert.AreEqual(header.Connection, read.Connection);
        TestAssert.AreEqual(header.Sequence, read.Sequence);
        TestAssert.AreEqual(header.HostTick, read.HostTick);
        TestAssert.AreEqual(header.ClaimedPlayerId, read.ClaimedPlayerId);
        TestAssert.AreEqual(header.PayloadLength, read.PayloadLength);

        var truncated = new byte[AuthorityLimits.HeaderBytes - 1];
        TestAssert.IsFalse(AuthorityEnvelopeHeader.TryRead(truncated, out _, out var reject));
        TestAssert.AreEqual(AuthorityRejectCode.MalformedEnvelope, reject.Code);
    }

    [TestMethod]
    public void AnObjectKeySurvivesItsWireEncoding()
    {
        var key = ObjectKey.Create(Epoch, PoolKind.Hive, 1000003, 12, 5);
        var bytes = new byte[AuthorityLimits.ObjectKeyBytes];
        ObjectKeyCodec.WriteTo(bytes, 0, key);
        TestAssert.IsTrue(ObjectKeyCodec.TryRead(bytes, 0, Host(), out var read, out var reject), reject.ToString());
        TestAssert.AreEqual(key, read);
    }

    [TestMethod]
    public void ADecodedKeyWithAnIllegalScopeIsRefusedByTheCodec()
    {
        var bytes = new byte[AuthorityLimits.ObjectKeyBytes];
        ObjectKeyCodec.WriteTo(bytes, 0, ObjectKey.Create(Epoch, PoolKind.SpaceEnemy, 1000001, 3, 1));
        // Rewrite the scope to a planet id, which is illegal for a sector pool.
        Buffer.BlockCopy(BitConverter.GetBytes(101), 0, bytes, AuthorityEpoch.SizeBytes + 1, 4);
        TestAssert.IsFalse(ObjectKeyCodec.TryRead(bytes, 0, Host(), out _, out var reject));
        TestAssert.AreEqual(AuthorityRejectCode.IllegalScope, reject.Code);
    }

    [TestMethod]
    public void ACommandKeySurvivesItsWireEncoding()
    {
        var key = new CommandKey(Epoch, Connection, 9001);
        var bytes = new byte[CommandKeyCodec.SizeBytes];
        CommandKeyCodec.WriteTo(bytes, 0, key);
        TestAssert.IsTrue(CommandKeyCodec.TryRead(bytes, 0, Host(), out var read, out var reject), reject.ToString());
        TestAssert.AreEqual(key, read);
    }
}

/// <summary>
/// A03: the bounded payload reader refuses declared lengths above its ceiling.
/// </summary>
[TestClass]
public class AuthorityPayloadBoundsTest
{
    [TestMethod]
    public void ADeclaredLengthBeyondTheBufferIsRefusedBeforeReading()
    {
        var buffer = new byte[8];
        TestAssert.IsFalse(AuthorityPayloadReader.TryCreate(buffer, 0, 9, out _, out var reject));
        TestAssert.AreEqual(AuthorityRejectCode.MalformedEnvelope, reject.Code);
    }

    [TestMethod]
    public void ANegativeDeclaredLengthIsRefused()
    {
        TestAssert.IsFalse(AuthorityPayloadReader.TryCreate(new byte[8], 0, -1, out _, out var reject));
        TestAssert.AreEqual(AuthorityRejectCode.NegativeLength, reject.Code);
    }

    [TestMethod]
    public void AnArrayLengthAboveTheCeilingIsRefusedWithoutAllocating()
    {
        var writer = new AuthorityPayloadWriter();
        writer.WriteInt(int.MaxValue);
        var payload = writer.ToArray();

        TestAssert.IsTrue(AuthorityPayloadReader.TryCreate(payload, 0, payload.Length, out var reader, out _));
        TestAssert.IsFalse(reader.TryReadBytes(maxBytes: 64, out var value),
            "A peer must not be able to make the reader allocate an arbitrary array.");
        TestAssert.IsNull(value);
    }

    [TestMethod]
    public void AnArrayAtTheCeilingIsRead()
    {
        var writer = new AuthorityPayloadWriter();
        TestAssert.IsTrue(writer.TryWriteBytes(new byte[64], maxBytes: 64));
        var payload = writer.ToArray();
        TestAssert.IsTrue(AuthorityPayloadReader.TryCreate(payload, 0, payload.Length, out var reader, out _));
        TestAssert.IsTrue(reader.TryReadBytes(maxBytes: 64, out var value));
        TestAssert.HasCount(64, value);
        TestAssert.IsTrue(reader.EndOfPayload);
    }

    [TestMethod]
    public void ATruncatedFixedWidthReadFailsInsteadOfOverReading()
    {
        var payload = new byte[3];
        TestAssert.IsTrue(AuthorityPayloadReader.TryCreate(payload, 0, payload.Length, out var reader, out _));
        TestAssert.IsFalse(reader.TryReadInt(out _));
        TestAssert.IsFalse(reader.TryReadLong(out _));
    }

    [TestMethod]
    public void AnUnknownEnumValueIsRefused()
    {
        var writer = new AuthorityPayloadWriter();
        writer.WriteByte(200);
        var payload = writer.ToArray();
        TestAssert.IsTrue(AuthorityPayloadReader.TryCreate(payload, 0, payload.Length, out var reader, out _));
        TestAssert.IsFalse(reader.TryReadEnumByte<AuthorityFamily>(out _),
            "An unknown discriminator must not decode to the default member.");

        var known = new AuthorityPayloadWriter();
        known.WriteByte((byte)AuthorityFamily.Command);
        var knownPayload = known.ToArray();
        TestAssert.IsTrue(AuthorityPayloadReader.TryCreate(knownPayload, 0, knownPayload.Length, out var knownReader,
            out _));
        TestAssert.IsTrue(knownReader.TryReadEnumByte<AuthorityFamily>(out var family));
        TestAssert.AreEqual(AuthorityFamily.Command, family);
    }
}

/// <summary>
/// A03: the handshake refuses a mixed room before a peer is admitted.
/// </summary>
[TestClass]
public class AuthorityNegotiationTest
{
    [TestMethod]
    public void AHostAuthorityClientMatchesAHostAuthorityHost()
    {
        TestAssert.IsTrue(AuthorityNegotiation.IsCompatible(AuthorityMode.HostAuthority, AuthoritySchema.V1,
            AuthorityCapability.All, AuthorityMode.HostAuthority, AuthoritySchema.V1,
            AuthorityCapability.Combat, out var reject), reject.ToString());
    }

    [TestMethod]
    public void LegacyAndAuthorityPeersNeverShareARoom()
    {
        TestAssert.IsFalse(AuthorityNegotiation.IsCompatible(AuthorityMode.HostAuthority, AuthoritySchema.V1,
            AuthorityCapability.All, AuthorityMode.Legacy, AuthoritySchema.None,
            AuthorityCapability.None, out var reject));
        TestAssert.AreEqual(AuthorityRejectCode.NotAuthorityMode, reject.Code);

        TestAssert.IsFalse(AuthorityNegotiation.IsCompatible(AuthorityMode.Legacy, AuthoritySchema.None,
            AuthorityCapability.None, AuthorityMode.HostAuthority, AuthoritySchema.V1,
            AuthorityCapability.None, out reject));
        TestAssert.AreEqual(AuthorityRejectCode.NotAuthorityMode, reject.Code);

        TestAssert.IsFalse(AuthorityNegotiation.IsCompatible(AuthorityMode.Legacy, AuthoritySchema.None,
            AuthorityCapability.None, AuthorityMode.Legacy, AuthoritySchema.None,
            AuthorityCapability.None, out reject),
            "Legacy rooms were removed; two legacy peers must not share a room either.");
        TestAssert.AreEqual(AuthorityRejectCode.NotAuthorityMode, reject.Code);
    }

    [TestMethod]
    public void ADifferentSchemaIsRefused()
    {
        TestAssert.IsFalse(AuthorityNegotiation.IsCompatible(AuthorityMode.HostAuthority, AuthoritySchema.V1,
            AuthorityCapability.All, AuthorityMode.HostAuthority, AuthoritySchema.None,
            AuthorityCapability.None, out var reject));
        TestAssert.AreEqual(AuthorityRejectCode.SchemaMismatch, reject.Code);
    }

    [TestMethod]
    public void AClientMayNotRequireACapabilityTheHostLacks()
    {
        TestAssert.IsFalse(AuthorityNegotiation.IsCompatible(AuthorityMode.HostAuthority, AuthoritySchema.V1,
            AuthorityCapability.Combat, AuthorityMode.HostAuthority, AuthoritySchema.V1,
            AuthorityCapability.Combat | AuthorityCapability.Snapshot, out var reject));
        TestAssert.AreEqual(AuthorityRejectCode.CapabilityMismatch, reject.Code);
    }

    [TestMethod]
    public void AMissingModeIsRefused()
    {
        TestAssert.IsFalse(AuthorityNegotiation.IsCompatible(AuthorityMode.None, AuthoritySchema.V1,
            AuthorityCapability.All, AuthorityMode.HostAuthority, AuthoritySchema.V1,
            AuthorityCapability.None, out var reject));
        TestAssert.AreEqual(AuthorityRejectCode.MalformedEnvelope, reject.Code);
    }

    [TestMethod]
    public void TheDeclarationRoundTripsAndRefusesUnknownValues()
    {
        var encoded = AuthorityHandshake.Encode(AuthorityMode.HostAuthority, AuthoritySchema.V1,
            AuthorityCapability.Combat | AuthorityCapability.Snapshot);
        TestAssert.IsTrue(AuthorityHandshake.TryDecode(encoded, out var mode, out var schema, out var capabilities));
        TestAssert.AreEqual(AuthorityMode.HostAuthority, mode);
        TestAssert.AreEqual(AuthoritySchema.V1, schema);
        TestAssert.AreEqual(AuthorityCapability.Combat | AuthorityCapability.Snapshot, capabilities);

        TestAssert.IsFalse(AuthorityHandshake.TryDecode("2;2;1;0", out _, out _, out _),
            "An unknown declaration prefix must not be accepted.");
        TestAssert.IsFalse(AuthorityHandshake.TryDecode("1;99;1;0", out _, out _, out _),
            "An unknown mode must not be accepted.");
        TestAssert.IsFalse(AuthorityHandshake.TryDecode("1;2;99;0", out _, out _, out _),
            "An unknown schema must not be accepted.");
        TestAssert.IsFalse(AuthorityHandshake.TryDecode("1;2;1;4294967295", out _, out _, out _),
            "A capability bit outside the defined set must not be accepted.");
        TestAssert.IsFalse(AuthorityHandshake.TryDecode("", out _, out _, out _));
        TestAssert.IsFalse(AuthorityHandshake.TryDecode(null, out _, out _, out _));
    }

    [TestMethod]
    public void TheDefaultInstallationIsAuthority()
    {
        // Host-authority is the only multiplayer mode. There is no flag and no legacy switch:
        // every installation declares host-authority, and legacy declarations are refused.
        AuthorityLocalOptions.ResetToDefaults();
        TestAssert.AreEqual(AuthorityMode.HostAuthority, AuthorityLocalOptions.Mode);
        TestAssert.AreEqual(AuthoritySchema.V1, AuthorityLocalOptions.Schema);
        TestAssert.AreEqual(AuthorityHandshake.Encode(AuthorityMode.HostAuthority, AuthoritySchema.V1,
            AuthorityCapability.None), AuthorityLocalOptions.Declaration);
    }
}

/// <summary>
/// A04: the welcome is the one message allowed to precede the world epoch, and only that message.
/// </summary>
/// <remarks>
/// The bootstrap has to exist because a client cannot know the epoch before the host states it. The
/// tests below pin the exception down: it applies only to a welcome traveling server to client, only
/// while the session has no epoch, and never to any other family.
/// </remarks>
[TestClass]
public class AuthorityWelcomeBootstrapTest
{
    private static readonly AuthorityEpoch Epoch = new(0xAABBCCDD00112233, 0x445566778899AABB);
    private static readonly ConnectionEpoch Connection = new(11);

    private static AuthoritySessionContext Negotiated(AuthorityEpoch? epoch = null) =>
        new(AuthorityMode.HostAuthority, AuthoritySchema.V1, epoch ?? default, default, 0, false);

    private static AuthorityEnvelopeHeader WelcomeHeader(AuthorityEpoch? epoch = null,
        AuthoritySchema schema = AuthoritySchema.V1, int payloadLength = 0) =>
        new(schema, AuthorityFamily.Welcome, epoch ?? Epoch, Connection, sequence: 1, hostTick: 10,
            claimedPlayerId: 0, payloadLength);

    [TestMethod]
    public void AWelcomeEstablishesTheEpochOnAClientThatHasNone()
    {
        var reject = AuthorityEnvelopeGate.ValidateBootstrapWelcome(Negotiated(),
            AuthorityDirection.ServerToClient, WelcomeHeader());
        TestAssert.AreEqual(AuthorityRejectCode.None, reject.Code, reject.ToString());
    }

    [TestMethod]
    public void ASecondWelcomeCannotRebindAWorldThatIsAlreadyKnown()
    {
        // Once the client has a world, a welcome is an ordinary message and must match the epoch.
        // Otherwise a second host could silently move a running client to another world.
        var reject = AuthorityEnvelopeGate.ValidateBootstrapWelcome(Negotiated(Epoch),
            AuthorityDirection.ServerToClient, WelcomeHeader(new AuthorityEpoch(5, 5)));
        TestAssert.AreEqual(AuthorityRejectCode.EpochMismatch, reject.Code);
    }

    [TestMethod]
    public void TheBootstrapOnlyAcceptsAWelcome()
    {
        var other = new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.Command, Epoch, Connection, 1,
            10, 0, 0);
        var reject = AuthorityEnvelopeGate.ValidateBootstrapWelcome(Negotiated(),
            AuthorityDirection.ServerToClient, other);
        TestAssert.AreEqual(AuthorityRejectCode.WrongDirection, reject.Code);
    }

    [TestMethod]
    public void TheBootstrapRefusesALegacySessionAndAnInvalidEpoch()
    {
        TestAssert.AreEqual(AuthorityRejectCode.NotAuthorityMode,
            AuthorityEnvelopeGate.ValidateBootstrapWelcome(AuthoritySessionContext.Legacy,
                AuthorityDirection.ServerToClient, WelcomeHeader()).Code);

        TestAssert.AreEqual(AuthorityRejectCode.EpochMismatch,
            AuthorityEnvelopeGate.ValidateBootstrapWelcome(Negotiated(),
                AuthorityDirection.ServerToClient, WelcomeHeader(new AuthorityEpoch(0, 0))).Code);
    }

    [TestMethod]
    public void TheBootstrapStillEnforcesSchemaAndLength()
    {
        TestAssert.AreEqual(AuthorityRejectCode.SchemaMismatch,
            AuthorityEnvelopeGate.ValidateBootstrapWelcome(Negotiated(), AuthorityDirection.ServerToClient,
                WelcomeHeader(schema: AuthoritySchema.None)).Code);
        TestAssert.AreEqual(AuthorityRejectCode.NegativeLength,
            AuthorityEnvelopeGate.ValidateBootstrapWelcome(Negotiated(), AuthorityDirection.ServerToClient,
                WelcomeHeader(payloadLength: -1)).Code);
        TestAssert.AreEqual(AuthorityRejectCode.PayloadTooLarge,
            AuthorityEnvelopeGate.ValidateBootstrapWelcome(Negotiated(), AuthorityDirection.ServerToClient,
                WelcomeHeader(payloadLength: AuthorityLimits.PayloadMaxBytes(AuthorityFamily.Welcome) + 1)).Code);
    }

    [TestMethod]
    public void AClientAdoptsTheWelcomeEpochExactlyOnce()
    {
        var session = new AuthoritySessionState();
        session.OnPeerNegotiated(AuthorityMode.HostAuthority);
        TestAssert.IsFalse(session.IsActive);

        TestAssert.IsTrue(session.TryAdoptWorldEpoch(Epoch), "The first welcome establishes the world.");
        TestAssert.IsTrue(session.IsActive);
        TestAssert.AreEqual(Epoch, session.Context.Epoch);

        TestAssert.IsFalse(session.TryAdoptWorldEpoch(new AuthorityEpoch(9, 9)),
            "A second welcome must not move the client to another world.");
        TestAssert.AreEqual(Epoch, session.Context.Epoch);
    }

    [TestMethod]
    public void ALegacySessionCannotAdoptAWorldEpoch()
    {
        var session = new AuthoritySessionState();
        TestAssert.IsFalse(session.TryAdoptWorldEpoch(Epoch),
            "A welcome must not create authority where the handshake never agreed to it.");
        TestAssert.IsFalse(session.IsActive);
        TestAssert.IsFalse(session.TryAdoptWorldEpoch(default));
    }
}

/// <summary>
/// A03: the world-state batch codec bounds each record and refuses inconsistent batches.
/// </summary>
[TestClass]
public class AuthorityWorldStateCodecTest
{
    private static readonly AuthorityEpoch Epoch = new(0x0102030405060708, 0x090A0B0C0D0E0F10);
    private static readonly AuthoritySessionContext Context =
        new(AuthorityMode.HostAuthority, AuthoritySchema.V1, Epoch, new ConnectionEpoch(3), 1, true);

    private static AuthorityWorldStateRecord Record(int nativeId, long revision, int stateBytes) =>
        new(ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, nativeId, 1), revision, new byte[stateBytes]);

    [TestMethod]
    public void ARecordBatchSurvivesEncodeAndDecode()
    {
        var records = new List<AuthorityWorldStateRecord>
        {
            Record(1, 5, 16),
            Record(2, 9, 32)
        };
        TestAssert.IsTrue(AuthorityWorldStateCodec.TryEncode(records, out var blob));
        TestAssert.IsTrue(AuthorityWorldStateCodec.TryDecode(blob, 0, blob.Length, records.Count, Context,
            out var decoded, out var reject), reject.ToString());
        TestAssert.HasCount(2, decoded);
        TestAssert.AreEqual(records[0].Key, decoded[0].Key);
        TestAssert.AreEqual(5L, decoded[0].Revision);
        TestAssert.HasCount(32, decoded[1].State);
    }

    [TestMethod]
    public void ARecordStateAboveThePerRecordCeilingIsRefusedOnEncode()
    {
        var records = new List<AuthorityWorldStateRecord>
        {
            Record(1, 1, AuthorityLimits.StateRecordMaxBytes + 1)
        };
        TestAssert.IsFalse(AuthorityWorldStateCodec.TryEncode(records, out var blob));
        TestAssert.IsNull(blob);
    }

    [TestMethod]
    public void ARecordCountThatDisagreesWithTheBlobIsRefused()
    {
        var records = new List<AuthorityWorldStateRecord> { Record(1, 1, 8) };
        TestAssert.IsTrue(AuthorityWorldStateCodec.TryEncode(records, out var blob));
        TestAssert.IsFalse(AuthorityWorldStateCodec.TryDecode(blob, 0, blob.Length, declaredRecordCount: 2,
            Context, out _, out var reject));
        TestAssert.AreEqual(AuthorityRejectCode.MalformedEnvelope, reject.Code);
    }

    [TestMethod]
    public void ATrailingByteIsRefusedInsteadOfIgnored()
    {
        var records = new List<AuthorityWorldStateRecord> { Record(1, 1, 8) };
        TestAssert.IsTrue(AuthorityWorldStateCodec.TryEncode(records, out var blob));
        var padded = new byte[blob.Length + 1];
        Buffer.BlockCopy(blob, 0, padded, 0, blob.Length);

        TestAssert.IsFalse(AuthorityWorldStateCodec.TryDecode(padded, 0, padded.Length, records.Count,
            Context, out _, out var reject));
        TestAssert.AreEqual(AuthorityRejectCode.MalformedEnvelope, reject.Code);
    }

    [TestMethod]
    public void ARecordWithAnIllegalScopeIsRefusedOnDecode()
    {
        var illegal = new AuthorityWorldStateRecord(
            new ObjectKey(Epoch, PoolKind.GroundEnemy, AuthorityScope.Sector, 4, 1), 1, new byte[4]);
        TestAssert.IsTrue(AuthorityWorldStateCodec.TryEncode([illegal], out var blob));
        TestAssert.IsFalse(AuthorityWorldStateCodec.TryDecode(blob, 0, blob.Length, 1, Context, out _,
            out var reject));
        TestAssert.AreEqual(AuthorityRejectCode.IllegalScope, reject.Code);
    }

    [TestMethod]
    public void ARecordWithANonPositiveRevisionIsRefused()
    {
        var records = new List<AuthorityWorldStateRecord> { Record(1, 0, 4) };
        TestAssert.IsTrue(AuthorityWorldStateCodec.TryEncode(records, out var blob));
        TestAssert.IsFalse(AuthorityWorldStateCodec.TryDecode(blob, 0, blob.Length, 1, Context, out _,
            out var reject));
        TestAssert.AreEqual(AuthorityRejectCode.MalformedEnvelope, reject.Code);
    }
}
