using System.Reflection;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests;

// Covers the compute gate that lets a GPU-equipped dedicated server run the native planetary
// shield shader while every other compute user (DysonSwarm) stays disabled.
[TestClass]
public class HeadlessShieldComputeTest
{
    private static Type Gate => typeof(NebulaPatcher.NebulaPlugin).Assembly.GetType(
        "NebulaPatcher.Patches.Misc.HeadlessShieldCompute", true)!;

    private static Type Patches => typeof(NebulaPatcher.NebulaPlugin).Assembly.GetType(
        "NebulaPatcher.Patches.Misc.Dedicated_Server_Patches", true)!;

    private const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static bool Allowed => (bool)Gate.GetProperty("Allowed", Statics)!.GetValue(null)!;

    private static bool ComputeAvailable => (bool)Gate.GetProperty("ComputeAvailable", Statics)!.GetValue(null)!;

    // ComputeAvailable is cached for the lifetime of the process, so changing the simulated device
    // has to clear that cache first.
    private static void SetProbe(bool available)
    {
        Reset();
        Gate.GetField("ProbeComputeSupport", Statics)!.SetValue(null, available ? () => true : () => false);
    }

    private static void Enter() => Gate.GetMethod("EnterScope", Statics)!.Invoke(null, null);

    private static void Exit() => Gate.GetMethod("ExitScope", Statics)!.Invoke(null, null);

    private static void Reset() => Gate.GetMethod("ResetForTests", Statics)!.Invoke(null, null);

    private static bool InvokePrefix(string name) => (bool)Patches.GetMethod(name, Statics)!.Invoke(null, null)!;

    [TestInitialize]
    public void BeforeEach() => Reset();

    [TestCleanup]
    public void AfterEach() => Reset();

    [TestMethod]
    public void NullGraphicsDeviceNeverAllowsCompute()
    {
        SetProbe(false);
        TestAssert.IsFalse(ComputeAvailable, "A Null graphics device must not report compute support.");
        TestAssert.IsFalse(Allowed);

        // Even inside the shield recalculation window the gate stays shut without a device.
        Enter();
        TestAssert.IsFalse(Allowed, "Without a compute-capable device the gate must stay closed.");
        Exit();
    }

    [TestMethod]
    public void RealGraphicsDeviceAllowsComputeOnlyInsideTheScope()
    {
        SetProbe(true);
        TestAssert.IsTrue(ComputeAvailable);
        TestAssert.IsFalse(Allowed, "The gate must start closed so DysonSwarm keeps its existing behaviour.");

        Enter();
        TestAssert.IsTrue(Allowed, "The shield recalculation window must open the gate.");

        Exit();
        TestAssert.IsFalse(Allowed, "Leaving the shield recalculation must close the gate again.");
    }

    [TestMethod]
    public void NestedScopesStayOpenUntilTheOutermostExit()
    {
        SetProbe(true);
        Enter();
        Enter();
        TestAssert.IsTrue(Allowed);

        Exit();
        TestAssert.IsTrue(Allowed, "An inner exit must not close the gate while an outer scope is open.");

        Exit();
        TestAssert.IsFalse(Allowed);
    }

    [TestMethod]
    public void UnbalancedExitsCannotLeaveTheGateStuckOpen()
    {
        SetProbe(true);
        // A mismatched Enter/Exit pair must not drive the depth negative, which would make a later
        // Exit appear to be the outermost one and close the gate too early -- or, worse, leave the
        // counter so far off that the gate never closes.
        Exit();
        Exit();
        TestAssert.IsFalse(Allowed);

        Enter();
        TestAssert.IsTrue(Allowed);
        Exit();
        TestAssert.IsFalse(Allowed);
    }

    [TestMethod]
    public void ProbeFailureRejectsNativeShieldRequirement()
    {
        Gate.GetField("ProbeComputeSupport", Statics)!.SetValue(null,
            new Func<bool>(() => throw new InvalidOperationException("no device")));
        TestAssert.IsFalse(ComputeAvailable, "A throwing probe must be treated as 'no compute', never crash.");
        TestAssert.IsFalse(Allowed);
        var exception = TestAssert.ThrowsExactly<TargetInvocationException>(() =>
            Gate.GetMethod("RequireComputeSupport", Statics)!.Invoke(null, null));
        TestAssert.IsInstanceOfType<InvalidOperationException>(exception.InnerException);
    }

    [TestMethod]
    public void KernelLookupIsReleasedOnlyInsideTheShieldScope()
    {
        SetProbe(true);
        TestAssert.IsFalse(InvokePrefix("ComputeShaderKernel_Prefix"),
            "FindKernel must stay skipped outside the shield window so DysonSwarm is unaffected.");

        Enter();
        TestAssert.IsTrue(InvokePrefix("ComputeShaderKernel_Prefix"),
            "Unity must be able to resolve the shield kernel while RecalculatePhysicsShape runs.");
        Exit();
    }

    // A Harmony prefix returns FALSE to skip the original and TRUE to run it. Getting this backwards
    // silently disables compute exactly when the gate is open, which shows up as a shield that
    // computes zero coverage on a perfectly good GPU.
    [TestMethod]
    public void DispatchAndBufferAccessFollowTheGate()
    {
        // Without a device: skipped, exactly as the -nographics deployment behaves today.
        SetProbe(false);
        TestAssert.IsFalse(InvokePrefix("ComputeShaderDispatch_Prefix"));
        TestAssert.IsFalse(InvokePrefix("ComputeBuffer_Prefix"));

        // With a device, but outside the shield recalculation: still skipped.
        SetProbe(true);
        TestAssert.IsFalse(InvokePrefix("ComputeShaderDispatch_Prefix"));
        TestAssert.IsFalse(InvokePrefix("ComputeBuffer_Prefix"));

        // Inside the shield recalculation: the original runs, so the native shader can execute.
        Enter();
        TestAssert.IsTrue(InvokePrefix("ComputeShaderDispatch_Prefix"));
        TestAssert.IsTrue(InvokePrefix("ComputeBuffer_Prefix"));
    }

    [TestMethod]
    public void RelayLandingOverrideIsRemoved()
    {
        // The upstream balance hack forced relays to stop landing once 7+ generators were online,
        // overriding the original per-planet raycast test. It must not come back now that the native
        // shield computation produces the real isSpherical/coverage values.
        TestAssert.IsNull(Patches.GetMethod("StopLanding", Statics),
            "StopLanding must stay removed so TestRelayCondition keeps its original behaviour.");
    }

    [TestMethod]
    public void ShieldFinalizerClosesTheGateOnEveryPath()
    {
        // The gate is closed by a Finalizer, which Harmony runs from a finally block. That is what
        // keeps a throwing RecalculatePhysicsShape from leaving compute released for the session.
        var finalizer = Patches.GetMethod("RecalculatePhysicsShape_Finalizer", Statics);
        TestAssert.IsNotNull(finalizer);
        TestAssert.IsTrue(finalizer!.GetCustomAttributes(typeof(HarmonyLib.HarmonyFinalizer), false).Any(),
            "The gate must be closed by a Finalizer, not a Postfix: a Postfix is skipped when the original throws.");
    }

    [TestMethod]
    public void ExternalBackendCanEnterAndExitScopeWithoutUnityGpu()
    {
        SetProbe(false);
        var field = (PlanetATField)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(PlanetATField));
        field.energyMaxTarget = 123;
        object?[] args = [field, false];
        TestAssert.IsTrue((bool)Patches.GetMethod("RecalculatePhysicsShape_Prefix", Statics)!.Invoke(null, args)!);
        TestAssert.IsTrue((bool)args[1]!);
        TestAssert.IsFalse(Allowed, "A native backend scope must not enable Unity's Null compute API.");
        // The runtime finalizer also references Application.Quit, unavailable outside Unity.
        Exit();
        TestAssert.AreEqual(123L, field.energyMaxTarget);
        TestAssert.IsFalse(Allowed);
    }
}
