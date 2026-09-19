using System.Net;
using NebulaClient = NebulaNetwork.Client;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.NebulaNetwork;

[TestClass]
public class ClientAddressTest
{
    [TestMethod]
    public void ClientKeepsTheHostNameAndDoesNotResolveIt()
    {
        // ".invalid" is guaranteed to never resolve (RFC 2606): constructing the client must not
        // attempt a name lookup at all, otherwise this throws a SocketException like it used to.
        var client = new NebulaClient("nebula-client-test.invalid", 8469, "wss");

        TestAssert.AreEqual("nebula-client-test.invalid", client.ServerHost);
        TestAssert.AreEqual(8469, client.ServerPort);
        TestAssert.AreEqual("wss", client.ServerProtocol);
        TestAssert.AreEqual("wss://nebula-client-test.invalid:8469/socket", client.SocketUrl);
        TestAssert.IsNull(client.ServerEndpoint);
    }

    [TestMethod]
    public void ClientDefaultsToWsAndKeepsThePort()
    {
        var client = new NebulaClient("example.com", 9000, "");

        TestAssert.AreEqual("ws", client.ServerProtocol);
        TestAssert.AreEqual("ws://example.com:9000/socket", client.SocketUrl);
    }

    [TestMethod]
    public void ClientAcceptsIpLiteralEndpoints()
    {
        var client = new NebulaClient(new IPEndPoint(IPAddress.Parse("::1"), 8469));

        TestAssert.AreEqual("::1", client.ServerHost);
        TestAssert.AreEqual(8469, client.ServerPort);
        TestAssert.AreEqual("ws", client.ServerProtocol);
        TestAssert.AreEqual("ws://[::1]:8469/socket", client.SocketUrl);
    }
}
