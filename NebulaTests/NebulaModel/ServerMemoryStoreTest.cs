using System;
using System.IO;
using NebulaModel;
using NebulaModel.Networking;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.NebulaModel;

[TestClass]
public class ServerMemoryStoreTest
{
    [TestMethod]
    public void ServerAddressAcceptsDnsIpAndExplicitPort()
    {
        TestAssert.IsTrue(ServerAddress.TryParse("example.com", 8469, out var dns));
        TestAssert.AreEqual("example.com", dns.Host);
        TestAssert.AreEqual(8469, dns.Port);
        TestAssert.IsTrue(ServerAddress.TryParse("wss://example.com:80", 8469, out var tls));
        TestAssert.AreEqual("wss", tls.Protocol);
        TestAssert.AreEqual(80, tls.Port);
        TestAssert.IsTrue(ServerAddress.TryParse("[::1]:9000", 8469, out var ipv6));
        TestAssert.AreEqual("::1", ipv6.Host);
        TestAssert.AreEqual(9000, ipv6.Port);
        TestAssert.IsTrue(ServerAddress.TryParse("127.0.0.1:8469", 9000, out _));
        TestAssert.IsFalse(ServerAddress.TryParse("example.com:not-a-port", 8469, out _));
        TestAssert.IsFalse(ServerAddress.TryParse("ftp://example.com", 8469, out _));
        TestAssert.IsFalse(ServerAddress.TryParse("example.com:70000", 8469, out _));
    }

    [TestMethod]
    public void RecordsAndPersonalGoalsPersistByStableIdentity()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nebula-server-memory-test-" + Guid.NewGuid().ToString("N"));
        var serverPath = Path.Combine(folder, "servers.json");
        var goalPath = Path.Combine(folder, "goals.json");
        try
        {
            var store = new ServerMemoryStore(serverPath, goalPath);
            var first = store.SaveServer(null, "Home", "example.com:8469", "secret");
            var second = store.SaveServer(null, "Work", "[::1]:9000", "ephemeral");
            var id = first.Id;
            store.SaveProfile(id, "world-one", "player-one", 2, new[] { 101, 101, 202 });
            store.SaveProfile(id, "world-two", "player-one", 3, Array.Empty<int>());
            store.SaveServer(id, "Renamed", "new.example.com", "updated");
            TestAssert.IsTrue(store.UpdatePassword(second.Id, "saved"));

            store = new ServerMemoryStore(serverPath, goalPath);
            TestAssert.HasCount(2, store.Servers);
            TestAssert.AreEqual(id, store.FindServer(id).Id);
            TestAssert.AreEqual("Renamed", store.FindServer(id).Name);
            TestAssert.AreEqual("saved", store.FindServer(second.Id).Password);
            TestAssert.AreEqual("updated", store.FindServer(id).Password);
            store.SaveServer(id, "Renamed", "new.example.com", "");
            TestAssert.AreEqual("", store.FindServer(id).Password);
            TestAssert.HasCount(2, store.FindProfile(id, "world-one", "player-one").IgnoredGoalIds);
            TestAssert.AreEqual(3, store.FindProfile(id, "world-two", "player-one").Level);
            TestAssert.IsNull(store.FindProfile(id, "world-one", "player-two"));

            store.DeleteServer(id);
            store = new ServerMemoryStore(serverPath, goalPath);
            TestAssert.IsNull(store.FindServer(id));
            TestAssert.IsNull(store.FindProfile(id, "world-one", "player-one"));
            TestAssert.IsNotNull(store.FindServer(second.Id));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [TestMethod]
    public void CorruptServerDocumentFallsBackToBackup()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nebula-server-backup-test-" + Guid.NewGuid().ToString("N"));
        var serverPath = Path.Combine(folder, "servers.json");
        try
        {
            var store = new ServerMemoryStore(serverPath, Path.Combine(folder, "goals.json"));
            var server = store.SaveServer(null, "Original", "example.com", "first");
            store.SaveServer(server.Id, "Changed", "example.com", "second");
            File.WriteAllText(serverPath, "invalid json");

            store = new ServerMemoryStore(serverPath, Path.Combine(folder, "goals.json"));
            TestAssert.AreEqual("Original", store.FindServer(server.Id).Name);
            TestAssert.AreEqual("first", store.FindServer(server.Id).Password);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
