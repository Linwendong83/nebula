using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using NebulaPatcher.Patches.Dynamic;
using NebulaWorld;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests;

[TestClass]
[DoNotParallelize]
public class HeadlessTickRateTest
{
    private readonly Harmony harmony = new("nebula.tests.headless-tick-rate");
    private MultiplayerSession? previousSession;
    private GameMain game = null!;
    private static readonly MethodInfo TickRate = AccessTools.Method(typeof(GameMain), "DetermineGameTickRate");
    private static readonly FieldInfo Paused = AccessTools.Field(typeof(GameMain), "_fullscreenPaused");
    private static readonly FieldInfo Unlock = AccessTools.Field(typeof(GameMain), "_fullscreenPausedUnlockOneFrame");
    private static readonly FieldInfo CanPause = AccessTools.Field(typeof(MultiplayerSession), "canPause");

    [TestInitialize]
    public void SetUp()
    {
        previousSession = Multiplayer.Session;
        // No Unity scene is needed: the dedicated prefix must bypass the death branch,
        // even though this instance has no GameData and cannot run the original body.
        game = (GameMain)FormatterServices.GetUninitializedObject(typeof(GameMain));
        Multiplayer.Session = (MultiplayerSession)FormatterServices.GetUninitializedObject(typeof(MultiplayerSession));
        CanPause.SetValue(Multiplayer.Session, true);
        var patches = typeof(NebulaPatcher.NebulaPlugin).Assembly.GetType(
            "NebulaPatcher.Patches.Misc.Dedicated_Server_Patches", true)!;
        harmony.Patch(TickRate,
            prefix: new HarmonyMethod(AccessTools.Method(patches, "DetermineGameTickRate_Prefix")),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(GameMain_Patch), "DetermineGameTickRate_Postfix")));
    }

    [TestCleanup]
    public void TearDown()
    {
        harmony.UnpatchSelf();
        Multiplayer.Session = previousSession;
    }

    private int Frames() => (int)TickRate.Invoke(game, null)!;

    [TestMethod]
    public void DedicatedClockRunsWithoutConsultingTheDeadHost()
    {
        TestAssert.AreEqual(1, Frames());
        TestAssert.AreEqual(1, Frames());
    }

    [TestMethod]
    public void PlayerJoinPauseStopsTheClockUntilSynchronizationCompletes()
    {
        Paused.SetValue(game, true);
        TestAssert.AreEqual(0, Frames());
        TestAssert.AreEqual(0, Frames());
        Paused.SetValue(game, false);
        CanPause.SetValue(Multiplayer.Session, false);
        TestAssert.AreEqual(1, Frames());
    }

    [TestMethod]
    public void UnlockAdvancesExactlyOneFrameWhilePaused()
    {
        Paused.SetValue(game, true);
        Unlock.SetValue(game, true);
        TestAssert.AreEqual(1, Frames());
        TestAssert.IsFalse((bool)Unlock.GetValue(game)!);
        TestAssert.AreEqual(0, Frames());
    }

    [TestMethod]
    public void ExistingMultiplayerNoPausePolicyStillOverridesThePause()
    {
        Paused.SetValue(game, true);
        CanPause.SetValue(Multiplayer.Session, false);
        TestAssert.AreEqual(1, Frames());
    }

    [TestMethod]
    public void UnlockIsConsumedBeforeALaterPause()
    {
        Unlock.SetValue(game, true);
        TestAssert.AreEqual(1, Frames());
        Paused.SetValue(game, true);
        TestAssert.AreEqual(0, Frames());
    }
}
