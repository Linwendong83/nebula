using NebulaModel.DataStructures;
using NebulaModel.Networking.Serialization;
using NebulaModel.Utils;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.NebulaModel;

[TestClass]
public class RestorationPersistenceTest
{
    [TestMethod]
    public void EmptyMechaSnapshotDoesNotBecomeADeadReturningPlayer()
    {
        var writer = new NetDataWriter();
        new MechaData().Serialize(writer);
        var restored = new MechaData();
        restored.Deserialize(new NetDataReader(writer));
        TestAssert.IsNull(restored.ReactorStorage);
        TestAssert.IsNull(restored.FightData);
    }

    [TestMethod]
    public void PlayerRevisionNineRoundTripsDeathAndRevisionEightRemainsReadable()
    {
        var player = new PlayerData(7, 102, "test")
        {
            Life = new PlayerLifeData
            {
                IsAlive = false,
                DeathCount = 4,
                RespawnMode = 3,
                RespawnStage = 1,
                RespawnTick = 42,
                Revision = 99,
                TransactionId = Guid.NewGuid().ToString("N"),
                RedeployItemsDropped = true
            }
        };
        var writer = new NetDataWriter(); player.Serialize(writer);
        var data = writer.CopyData();
        var restored = new PlayerData(); restored.Deserialize(new NetDataReader(data));
        TestAssert.IsFalse(restored.Life.IsAlive);
        TestAssert.AreEqual(4, restored.Life.DeathCount);
        TestAssert.IsTrue(restored.Life.RedeployItemsDropped);
        var lifeWriter = new NetDataWriter(); player.Life.Serialize(lifeWriter);
        var legacyBytes = data.Take(data.Length - lifeWriter.Length - sizeof(int)).ToArray();
        var legacy = new PlayerData(); legacy.Import(new NetDataReader(legacyBytes), 8);
        TestAssert.IsTrue(legacy.Life.IsAlive);
        TestAssert.AreEqual("test", legacy.Username);
        TestAssert.IsNull(legacy.Mecha.ReactorStorage);
    }

    [TestMethod]
    public void TornFinalDropRecordIsRecoveredWithoutDiscardingCommittedDrops()
    {
        var root = Path.Combine(Path.GetTempPath(), "nebula-restoration-tests");
        var directory = Path.GetFullPath(Path.Combine(root, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "drops.log");
            BinaryRecordLog.Append(path, [1, 2, 3]);
            using (var stream = new FileStream(path, FileMode.Append))
            using (var writer = new BinaryWriter(stream)) { writer.Write(100); writer.Write((byte)1); }
            var records = BinaryRecordLog.Read(path);
            TestAssert.HasCount(1, records);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, records[0]);
            BinaryRecordLog.Append(path, [4, 5]);
            TestAssert.HasCount(2, BinaryRecordLog.Read(path));
            TestAssert.IsTrue(File.Exists(path + ".interrupted"));
        }
        finally
        {
            if (directory.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void AtomicReplacementPreservesThePreviousValidFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nebula-restoration-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "wallet");
        AtomicFile.Write(path, [1, 2]);
        AtomicFile.Write(path, [3, 4]);
        CollectionAssert.AreEqual(new byte[] { 3, 4 }, File.ReadAllBytes(path));
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, File.ReadAllBytes(path + ".bak"));
        File.Delete(path); File.Delete(path + ".bak"); Directory.Delete(directory);
    }
}
