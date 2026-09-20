using System;
using System.Globalization;
using NebulaModel.Logger;
using UnityEngine;

namespace NebulaPatcher.Patches.Misc;

internal enum ShieldBackendKind { Native, Unity, Cpu }
internal enum ShieldBackendMode { Auto, Native, Unity, Cpu }

internal static class HeadlessShieldBackend
{
    private static readonly object Sync = new();
    private static bool initialized, stopped;
    private static ShieldBackendMode mode;
    private static ShieldBackendKind kind;
    private static ShieldNativeKernel native;
    private static bool reportedNullMaterial;

    internal static ShieldBackendKind Kind { get { lock (Sync) { Initialize(); return kind; } } }
    internal static bool UsesUnity => Kind == ShieldBackendKind.Unity;

    // Unity's Null renderer returns zero for Material.GetFloat, including serialized _K=2.1.
    // This is the verified shader blend parameter, NOT an assumed coverage percentage.
    internal static float ResolveBlend(float materialValue, bool unityComputeAvailable, string overrideValue)
    {
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            if (!float.TryParse(overrideValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                value <= 0 || float.IsInfinity(value) || float.IsNaN(value))
                throw new ArgumentException("NEBULA_SHIELD_BLEND must be finite and positive.");
            return value;
        }
        if (materialValue > 0 && !float.IsInfinity(materialValue)) return materialValue;
        if (!unityComputeAvailable && materialValue == 0) return 2.1f;
        throw new InvalidOperationException("Shield material has no valid _K blend parameter.");
    }

    internal static float GetBlend(PlanetATField field)
    {
        var materialValue = field.displayMaterial.GetFloat("_K");
        var blend = ResolveBlend(materialValue, HeadlessShieldCompute.ComputeAvailable,
            Environment.GetEnvironmentVariable("NEBULA_SHIELD_BLEND"));
        if (!reportedNullMaterial && materialValue == 0 && !HeadlessShieldCompute.ComputeAvailable)
        {
            reportedNullMaterial = true;
            Log.Info($"[shield-backend] Null renderer has no material floats; using verified blend K={blend}");
        }
        return blend;
    }

    internal static ShieldBackendMode ParseMode(string value)
    {
        switch ((value ?? "auto").Trim().ToLowerInvariant())
        {
            case "": case "auto": return ShieldBackendMode.Auto;
            case "native": return ShieldBackendMode.Native;
            case "unity": return ShieldBackendMode.Unity;
            case "cpu": return ShieldBackendMode.Cpu;
            default: throw new ArgumentException("NEBULA_SHIELD_BACKEND must be auto, native, unity or cpu.");
        }
    }

    internal static void Initialize()
    {
        lock (Sync)
        {
            if (stopped) throw new ObjectDisposedException(nameof(HeadlessShieldBackend));
            if (initialized) return;
            mode = ParseMode(Environment.GetEnvironmentVariable("NEBULA_SHIELD_BACKEND"));
            if (mode == ShieldBackendMode.Cpu) kind = ShieldBackendKind.Cpu;
            else if (mode == ShieldBackendMode.Unity)
            {
                HeadlessShieldCompute.RequireComputeSupport();
                kind = ShieldBackendKind.Unity;
            }
            else
            {
                try { native = new ShieldNativeKernel(); kind = ShieldBackendKind.Native; }
                catch (Exception ex)
                {
                    if (mode == ShieldBackendMode.Native) throw;
                    kind = HeadlessShieldCompute.ComputeAvailable ? ShieldBackendKind.Unity : ShieldBackendKind.Cpu;
                    Log.Warn($"[shield-backend] native initialization failed: {ex.Message}; fallback={kind}");
                }
            }
            initialized = true;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            Log.Info($"[shield-backend] selected={kind} mode={mode} adapter={native?.Adapter ?? "n/a"} lifecycle=in-process");
        }
    }

    private static void OnProcessExit(object sender, EventArgs args) => Shutdown();

    internal static void Shutdown()
    {
        lock (Sync)
        {
            stopped = true;
            ReleaseSession();
        }
    }

    internal static void ReleaseSession()
    {
        lock (Sync)
        {
            native?.Dispose();
            native = null;
            initialized = false;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        }
    }

    internal static ComputeBuffer CreatePhysicsBuffer(int count, int stride, ComputeBufferType type) =>
        UsesUnity ? new ComputeBuffer(count, stride, type) : null;

    internal static void Compute(PlanetATField field)
    {
        lock (Sync)
        {
            Initialize();
            var radius = field.planet.realRadius;
            var blend = GetBlend(field);
            ShieldCpuKernel.Validate(field.physicsMeshVertsOriginal, field.generatorMatrix, field.generatorCount,
                radius, 60.8f, 0.05f, blend, field.physicsMeshVerts, field.physicsMeshNorms, field.physicsArgs);
            if (kind == ShieldBackendKind.Native)
            {
                try
                {
                    native.Compute(field.physicsMeshVertsOriginal, field.generatorMatrix, field.generatorCount,
                        radius, 60.8f, 0.05f, blend, field.physicsMeshVerts, field.physicsMeshNorms, field.physicsArgs);
                    return;
                }
                catch (Exception ex)
                {
                    if (mode != ShieldBackendMode.Auto) throw;
                    native.Dispose(); native = null;
                    kind = HeadlessShieldCompute.ComputeAvailable && field.fieldGenerateShader != null ? ShieldBackendKind.Unity : ShieldBackendKind.Cpu;
                    Log.Warn($"[shield-backend] native computation failed: {ex.Message}; fallback={kind}. No retry until restart.");
                }
            }
            if (kind == ShieldBackendKind.Unity)
            {
                try { ComputeUnity(field); return; }
                catch (Exception ex)
                {
                    if (mode != ShieldBackendMode.Auto) throw;
                    kind = ShieldBackendKind.Cpu;
                    Log.Warn($"[shield-backend] Unity computation failed: {ex.Message}; fallback=Cpu. No retry until restart.");
                }
            }
            ShieldCpuKernel.Compute(field.physicsMeshVertsOriginal, field.generatorMatrix, field.generatorCount,
                radius, 60.8f, 0.05f, blend, field.physicsMeshVerts, field.physicsMeshNorms, field.physicsArgs);
        }
    }

    private static void ComputeUnity(PlanetATField field)
    {
        HeadlessShieldCompute.RequireComputeSupport();
        var shader = field.fieldGenerateShader;
        if (shader == null) throw new InvalidOperationException("Unity shield compute shader is missing.");
        var count = field.physicsMeshVertsOriginal.Length;
        // Also handles a transition from the native device, whose Unity buffers were never allocated.
        if (field.physicsMeshBuffer == null) field.physicsMeshBuffer = new ComputeBuffer(count * 2, 12);
        if (field.physicsArgsBuffer == null) field.physicsArgsBuffer = new ComputeBuffer(10, 4);
        Array.Clear(field.physicsArgs, 0, field.physicsArgs.Length);
        field.physicsMeshBuffer.SetData(field.physicsMeshVertsOriginal, 0, 0, count);
        field.physicsArgsBuffer.SetData(field.physicsArgs, 0, 0, field.physicsArgs.Length);
        shader.SetInt("_VertexCount", count);
        shader.SetBuffer(0, "_VertexBuffer", field.physicsMeshBuffer);
        shader.SetBuffer(0, "_ArgsBuffer", field.physicsArgsBuffer);
        shader.SetFloat("_PlanetRadius", field.planet.realRadius);
        shader.SetFloat("_PhysicsScale", 0.05f);
        shader.SetFloat("_FieldAltitude", 60.8f);
        shader.SetFloat("_K", GetBlend(field));
        shader.SetInt("_GeneratorCount", field.generatorCount);
        shader.SetVectorArray("_GeneratorMatrix", field.generatorMatrix);
        shader.Dispatch(0, (count - 1) / 256 + 1, 1, 1);
        field.physicsMeshBuffer.GetData(field.physicsMeshVerts, 0, 0, count);
        field.physicsMeshBuffer.GetData(field.physicsMeshNorms, 0, count, count);
        field.physicsArgsBuffer.GetData(field.physicsArgs, 0, 0, field.physicsArgs.Length);
    }
}
