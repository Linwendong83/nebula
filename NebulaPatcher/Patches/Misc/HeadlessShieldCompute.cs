#region

using System;
using UnityEngine;

#endregion

namespace NebulaPatcher.Patches.Misc;

// Scope for the optional Unity compatibility backend. The independent native device and CPU
// fallback never call Unity compute APIs; other compute users retain their headless behaviour.
internal static class HeadlessShieldCompute
{
    private const int Unknown = -1;
    private const int Unsupported = 0;
    private const int Supported = 1;

    // Indirect so tests can simulate both a Null device and a real one without a GPU.
    internal static Func<bool> ProbeComputeSupport = DefaultProbe;

    private static int support = Unknown;

    // The shield is recalculated from the main thread only (GameLogic.DefenseGroundSystemGameTick
    // and PlanetFactory.Import), but the scope is thread-static so a stray worker thread can never
    // observe an open scope and start dispatching its own compute work.
    [ThreadStatic] private static int scopeDepth;

    // True when this process has a graphics device able to run compute shaders. Cached: the device
    // cannot change while the game is running.
    internal static bool ComputeAvailable
    {
        get
        {
            if (support == Unknown)
            {
                bool available;
                try
                {
                    available = ProbeComputeSupport();
                }
                catch (Exception)
                {
                    // Report unavailable; RequireComputeSupport rejects startup/recalculation.
                    available = false;
                }
                support = available ? Supported : Unsupported;
            }
            return support == Supported;
        }
    }

    // True while RecalculatePhysicsShape is executing AND the device can actually run compute.
    internal static bool Allowed => ComputeAvailable && scopeDepth > 0;

    internal static void RequireComputeSupport()
    {
        if (!ComputeAvailable)
            throw new InvalidOperationException("The Unity shield backend requires a real Unity compute device. With -nographics use auto, native or cpu.");
    }

    internal static void EnterScope()
    {
        scopeDepth++;
    }

    // Clamped at zero: a mismatched Enter/Exit pair must not leave the gate permanently open.
    internal static void ExitScope()
    {
        if (scopeDepth > 0)
        {
            scopeDepth--;
        }
    }

    internal static void ResetForTests()
    {
        support = Unknown;
        scopeDepth = 0;
        ProbeComputeSupport = DefaultProbe;
    }

    private static bool DefaultProbe()
    {
        return SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null &&
               SystemInfo.supportsComputeShaders;
    }
}
