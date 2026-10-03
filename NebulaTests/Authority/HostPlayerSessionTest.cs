using NebulaModel.Authority;
using NebulaWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A09: the session owns the registry and ledger on the host side only.
/// </summary>
[TestClass]
public class HostPlayerSessionTest
{
    private static readonly AuthorityEpoch Epoch = new(0xA09BEEF123456789, 0x1122334455667788);
    private static readonly AuthorityEpoch NextEpoch = new(0x9988776655443321, 0x0011223344556677);

    [TestMethod]
    public void AHostWorldOpensARegistryAndLedger()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: true);
        TestAssert.IsNotNull(session.HostPlayers);
        TestAssert.IsNotNull(session.HostLedger);
        TestAssert.AreEqual(Epoch, session.HostLedger.Epoch);
        session.Dispose();
    }

    [TestMethod]
    public void AClientWorldOpensNeither()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: false);
        TestAssert.IsNull(session.HostPlayers);
        TestAssert.IsNull(session.HostLedger);
        TestAssert.IsNotNull(session.WorldReplica);
        session.Dispose();
    }

    [TestMethod]
    public void ForgettingAConnectionTakesPresenceOfflineButKeepsStock()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: true);
        var connection = session.AssignConnectionEpoch(2);
        session.HostPlayers.RegisterOrUpdate("p1", 2, HostPlayerRole.Remote, connection);
        var owner = LedgerOwner.ForPlayer("p1");
        session.HostLedger.SeedLong(owner, LedgerResourceKind.InventoryItem, 9009, 12);

        session.ForgetPlayerConnection(2);
        TestAssert.IsTrue(session.HostPlayers.TryGetByPersistent("p1", out var retained));
        TestAssert.IsFalse(retained.IsOnline);
        TestAssert.IsTrue(session.HostLedger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 9009,
            out var balance, out _));
        TestAssert.AreEqual(12L, balance);
        session.Dispose();
    }

    [TestMethod]
    public void ANewWorldReplacesPresenceAndLedger()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: true);
        session.HostPlayers.RegisterOrUpdate("p1", 2, HostPlayerRole.Remote, session.AssignConnectionEpoch(2));

        session.BeginAuthorityWorld(NextEpoch, isHost: true);
        TestAssert.AreEqual(0, session.HostPlayers.Count);
        TestAssert.AreEqual(NextEpoch, session.HostLedger.Epoch);
        session.Dispose();
    }

    [TestMethod]
    public void ResetClearsHostState()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: true);
        TestAssert.IsNotNull(session.HostPlayers);
        session.Reset();
        TestAssert.IsNull(session.HostPlayers);
        TestAssert.IsNull(session.HostLedger);
        session.Dispose();
    }
}
