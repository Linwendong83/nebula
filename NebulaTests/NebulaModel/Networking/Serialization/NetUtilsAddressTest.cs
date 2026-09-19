using System;
using NebulaModel.Networking.Serialization;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.NebulaModel.Networking.Serialization;

[TestClass]
public class NetUtilsAddressTest
{
    [TestMethod]
    public void NormalizeHostKeepsNamesAndStripsIpv6Brackets()
    {
        TestAssert.AreEqual("example.com", NetUtils.NormalizeHost("example.com"));
        TestAssert.AreEqual("1.2.3.4", NetUtils.NormalizeHost(" 1.2.3.4 "));
        TestAssert.AreEqual("::1", NetUtils.NormalizeHost("[::1]"));
        TestAssert.AreEqual("::1", NetUtils.NormalizeHost(" ::1 "));
        TestAssert.ThrowsExactly<ArgumentException>(() => NetUtils.NormalizeHost("  "));
        TestAssert.ThrowsExactly<ArgumentException>(() => NetUtils.NormalizeHost(null!));
    }

    [TestMethod]
    public void FormatHostPortBracketsOnlyIpv6Literals()
    {
        TestAssert.AreEqual("example.com:8469", NetUtils.FormatHostPort("example.com", 8469));
        TestAssert.AreEqual("1.2.3.4:8469", NetUtils.FormatHostPort("1.2.3.4", 8469));
        TestAssert.AreEqual("[::1]:8469", NetUtils.FormatHostPort("::1", 8469));
        TestAssert.AreEqual("[fe80::1]:8469", NetUtils.FormatHostPort("fe80::1", 8469));
    }

    [TestMethod]
    public void MakeWebSocketUrlKeepsTheHostNameInsteadOfResolvingIt()
    {
        TestAssert.AreEqual("ws://example.com:8469/socket", NetUtils.MakeWebSocketUrl("ws", "example.com", 8469));
        TestAssert.AreEqual("wss://dsp.example.com:443/socket",
            NetUtils.MakeWebSocketUrl("wss", "dsp.example.com", 443));
        TestAssert.AreEqual("ws://[::1]:8469/socket", NetUtils.MakeWebSocketUrl("ws", "::1", 8469));
        TestAssert.AreEqual("wss://example.com:8469/custom",
            NetUtils.MakeWebSocketUrl("wss", "example.com", 8469, "/custom"));
    }
}
