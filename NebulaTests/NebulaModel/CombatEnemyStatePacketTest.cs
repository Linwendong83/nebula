using NebulaModel.Networking.Serialization;
using NebulaModel.Packets.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.NebulaModel;

[TestClass]
public class CombatEnemyStatePacketTest
{
    [TestMethod]
    public void SnapshotRequestPreservesScopeAndExpectedGeneration()
    {
        var processor = new NebulaNetPacketProcessor();
        CombatEnemyStateRequestPacket received = null!;
        processor.SubscribeReusable<CombatEnemyStateRequestPacket>(packet => received = packet);
        var sent = new CombatEnemyStateRequestPacket
        {
            AstroId = 101,
            EnemyId = 7,
            ExpectedGeneration = 123,
            RequestId = 42,
            IncludeSnapshot = true
        };
        var writer = new NetDataWriter();
        processor.Write(writer, sent);
        processor.ReadAllPackets(new NetDataReader(writer));

        TestAssert.AreEqual(sent.AstroId, received.AstroId);
        TestAssert.AreEqual(sent.EnemyId, received.EnemyId);
        TestAssert.AreEqual(sent.ExpectedGeneration, received.ExpectedGeneration);
        TestAssert.AreEqual(sent.RequestId, received.RequestId);
        TestAssert.IsTrue(received.IncludeSnapshot);
    }

    [TestMethod]
    public void StateResponsePreservesGenerationAndSnapshotPayload()
    {
        var processor = new NebulaNetPacketProcessor();
        CombatEnemyStateResponsePacket received = null!;
        processor.SubscribeReusable<CombatEnemyStateResponsePacket>(packet => received = packet);
        var sent = new CombatEnemyStateResponsePacket
        {
            AstroId = 0,
            EnemyId = 17,
            ExpectedGeneration = 101,
            Generation = 102,
            RequestId = 29,
            Alive = true,
            OriginAstroId = 1000002,
            ProtoId = 8113,
            ModelIndex = 443,
            Owner = 2,
            Port = 3,
            Dynamic = true,
            HasCombatStat = true,
            Hp = 41,
            HpMax = 100,
            HpRecover = 2,
            Snapshot = [1, 2, 3, 4]
        };
        var writer = new NetDataWriter();
        processor.Write(writer, sent);
        processor.ReadAllPackets(new NetDataReader(writer));

        TestAssert.AreEqual(sent.EnemyId, received.EnemyId);
        TestAssert.AreEqual(sent.ExpectedGeneration, received.ExpectedGeneration);
        TestAssert.AreEqual(sent.Generation, received.Generation);
        TestAssert.AreEqual(sent.RequestId, received.RequestId);
        TestAssert.AreEqual(sent.OriginAstroId, received.OriginAstroId);
        TestAssert.AreEqual(sent.Owner, received.Owner);
        TestAssert.AreEqual(sent.Port, received.Port);
        TestAssert.AreEqual(sent.Hp, received.Hp);
        CollectionAssert.AreEqual(sent.Snapshot, received.Snapshot);
    }
}
