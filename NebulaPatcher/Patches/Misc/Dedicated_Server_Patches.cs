#region

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using NebulaModel;
using NebulaModel.Logger;
using NebulaWorld;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

#endregion

namespace NebulaPatcher.Patches.Misc;

// Dedicated server patches: suppress rendering while retaining native GPU shield computation.
// This part only get patch when Multiplayer.IsDedicated is true
internal class Dedicated_Server_Patches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameMain), nameof(GameMain.Begin))]
    public static void GameMainBegin_Postfix()
    {
        Log.Info($"[headless] GameMain.Begin call #{Interlocked.Increment(ref gameMainBeginCount)} completed");
        // Report the required native compute path separately from rendering suppression.
        Log.Info($"[headless] graphics device={SystemInfo.graphicsDeviceType} " +
                 $"computeShaders={SystemInfo.supportsComputeShaders} " +
                 $"unityShieldCompute={HeadlessShieldCompute.ComputeAvailable} " +
                 $"shieldBackend={HeadlessShieldBackend.Kind} " +
                 $"nativeShieldCompute={HeadlessShieldBackend.Kind != ShieldBackendKind.Cpu}");
        // Server.Start() could not restore the saved player data because the world did not exist yet.
        SaveManager.EnsureServerDataLoaded();
        if (!Multiplayer.IsActive)
        {
            return;
        }
        Log.Info($">> RemoteAccessEnabled: {Config.Options.RemoteAccessEnabled}");
        Log.Info(">> RemoteAccessPassword: " +
                 (string.IsNullOrWhiteSpace(Config.Options.RemoteAccessPassword) ? "None" : "Protected"));
        Log.Info($">> AutoPauseEnabled: {Config.Options.AutoPauseEnabled}");
        if (Config.Options.AutoPauseEnabled)
        {
            GameMain.Pause();
        }

        if (GameMain.mainPlayer != null)
        {
            // Don't let the player of dedicated server to interact with enemies
            GameMain.mainPlayer.isAlive = false;
            // Don't let the player of dedicated server send out construction drones 
            GameMain.mainPlayer.mecha.constructionModule.droneEnabled = false;
        }
    }

    // A dedicated server needs no menu demo. This must stay paired with VFPreload's
    // IsMenuDemoLoaded override: otherwise the demo and server loaders can overlap and
    // call GameMain.Begin twice, causing duplicate-key errors in the achievement UI.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DSPGame), nameof(DSPGame.StartDemoGame))]
    public static bool StartDemoGame_Prefix()
    {
        Log.Info("[headless] Skipping DSPGame.StartDemoGame (main menu demo game)");
        return false;
    }

    // Stop game rendering
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameLogic), nameof(GameLogic.Draw))]
    [HarmonyPatch(typeof(GameLogic), nameof(GameLogic.DrawPost))]
    [HarmonyPatch(typeof(FactoryModel), nameof(FactoryModel.LateUpdate))]
    [HarmonyPatch(typeof(SectorModel), nameof(SectorModel.LateUpdate))]
    public static bool OnDraw_Prefix()
    {
        return false;
    }

    private static int gameMainBeginCount;

    // The dedicated host's dead mecha must not slow or stop the world. Use the vanilla
    // living-player timing rules, including fullscreen pause and one-frame unlock.
    // GameMain_Patch's postfix still applies the multiplayer CanPause policy.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameMain), nameof(GameMain.DetermineGameTickRate))]
    public static bool DetermineGameTickRate_Prefix(bool ____fullscreenPaused,
        ref bool ____fullscreenPausedUnlockOneFrame, ref int __result)
    {
        if (____fullscreenPaused && !____fullscreenPausedUnlockOneFrame)
        {
            __result = 0;
            return false;
        }

        ____fullscreenPausedUnlockOneFrame = false;
        __result = 1;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameMain), nameof(GameMain.LateUpdate))]
    public static bool OnLateUpdate()
    {
        // Because UIRoot._LateUpdate() doesn't run in headless mode, we need this to enable autosave
        UIRoot.instance.uiGame.autoSave._LateUpdate();
        return false;
    }

    // Destroy gameObject so Update() won't execute
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlanetAtmoBlur), nameof(PlanetAtmoBlur.Start))]
    public static bool PlanetAtmoBlur_Start(PlanetAtmoBlur __instance)
    {
        Object.Destroy(__instance.gameObject);
        return false;
    }

    // RenderTexture is not support, so disable all functions using the constructor
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DysonMapCamera), nameof(DysonMapCamera.CheckOrCreateRTex))]
    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.CaptureScreenshot))] // Save won't have a img preview when disable
    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.CaptureScreenShot))]
    [HarmonyPatch(typeof(MechaEditorCamera), nameof(MechaEditorCamera.CheckOrCreateRTex))]
    [HarmonyPatch(typeof(UIDysonOrbitPreview), nameof(UIDysonOrbitPreview.CheckOrCreateRTex))]
    [HarmonyPatch(typeof(UIMechaMaterialBall), nameof(UIMechaMaterialBall._OnCreate))]
    [HarmonyPatch(typeof(UIMechaSaveGroup), nameof(UIMechaSaveGroup._OnCreate))]
    [HarmonyPatch(typeof(UIMilkyWay), nameof(UIMilkyWay.CheckOrCreateRTex))]
    [HarmonyPatch(typeof(UIMinimap3DControl), nameof(UIMinimap3DControl._OnCreate))]
    [HarmonyPatch(typeof(UISplitterWindow), nameof(UISplitterWindow._OnCreate))]
    [HarmonyPatch(typeof(UIStarmap), nameof(UIMilkyWay.CheckOrCreateRTex))]
    [HarmonyPatch(typeof(TranslucentImageSource), nameof(TranslucentImageSource.CreateNewBlurredScreen))]
    private static bool RenderTexture_Prefix()
    {
        return false;
    }

    // Keep the managed lifecycle state used by _OnInit/_OnFree without allocating
    // the advisor's audio visualization buffer on a graphics-less server.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIAdvisorTip), nameof(UIAdvisorTip._OnCreate))]
    public static bool UIAdvisorTipCreate_Prefix(UIAdvisorTip __instance)
    {
        __instance.requests = new List<int>();
        return false;
    }

    // An invisible host must not open advisor UI that depends on the skipped buffer.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIAdvisorTip), nameof(UIAdvisorTip.RequestAdvisorTip))]
    [HarmonyPatch(typeof(UIAdvisorTip), nameof(UIAdvisorTip.RunAdvisorTip))]
    public static bool UIAdvisorTipPlay_Prefix()
    {
        return false;
    }

    // Kernel lookup stays disabled for every other compute user: the planetary shield dispatches
    // kernel 0 directly and keeping these off preserves the existing DysonSwarm behaviour. It is
    // released inside the shield scope so Unity can resolve the kernel for that one shader.
    //
    // NOTE on the return convention: a Harmony prefix returns FALSE to SKIP the original method and
    // TRUE to run it. These helpers therefore return the gate value directly, not its negation.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ComputeShader), nameof(ComputeShader.FindKernel))]
    [HarmonyPatch(typeof(ComputeShader), nameof(ComputeShader.GetKernelThreadGroupSizes))]
    public static bool ComputeShaderKernel_Prefix()
    {
        return HeadlessShieldCompute.Allowed;
    }

    // Dispatch is allowed ONLY while PlanetATField.RecalculatePhysicsShape is running and only when
    // the process has a compute-capable graphics device (see HeadlessShieldCompute). Every other
    // compute user, DysonSwarm in particular, stays blocked exactly as before.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ComputeShader), nameof(ComputeShader.Dispatch))]
    public static bool ComputeShaderDispatch_Prefix()
    {
        return HeadlessShieldCompute.Allowed;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ComputeBuffer), nameof(ComputeBuffer.SetData), typeof(Array))]
    [HarmonyPatch(typeof(ComputeBuffer), nameof(ComputeBuffer.GetData), typeof(Array), typeof(int), typeof(int), typeof(int))]
    public static bool ComputeBuffer_Prefix()
    {
        // Keep Unity compute gated for the compatibility backend and the existing headless swarm.
        // The native in-process backend and CPU fallback do not call these APIs.
        return HeadlessShieldCompute.Allowed;
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(DysonSwarm), nameof(DysonSwarm.GameTick))]
    private static IEnumerable<CodeInstruction> DysonSwarmGameTick_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        // Remove first part of the functions about computeShader
        // (this.computeShader.Set..., this.Dispatch_UpdateVel, this.Dispatch_UpdatePos)
        var codeInstructions = instructions as CodeInstruction[] ?? instructions.ToArray();
        try
        {
            var matcher = new CodeMatcher(codeInstructions)
                .MatchForward(false,
                    new CodeMatch(OpCodes.Call, AccessTools.Method(typeof(DysonSwarm), nameof(DysonSwarm.Dispatch_UpdatePos))));
            var num = matcher.Pos + 1;
            matcher.Start()
                .RemoveInstructions(num);
            return matcher.InstructionEnumeration();
        }
        catch
        {
            Log.Error("DysonSwarmGameTick_Transpiler failed. Mod version not compatible with game version.");
            return codeInstructions;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DysonSwarm), nameof(DysonSwarm.SetSailCapacity))]
    public static void DysonSwarmSetSailCapacity_Prefix(DysonSwarm __instance)
    {
        // Skip the part of computeShader in if (this.swarmBuffer != null)
        // (this.computeShader.SetBuffer(...), this.Dispatch_BlitBuffer())
        if (__instance.swarmBuffer == null)
        {
            return;
        }
        __instance.swarmBuffer.Release();
        __instance.swarmInfoBuffer.Release();
        __instance.swarmBuffer = null;
        __instance.swarmInfoBuffer = null;
    }


    // From user report
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Graphic), nameof(Graphic.DoMeshGeneration))]
    [HarmonyPatch(typeof(Graphic), nameof(Graphic.DoLegacyMeshGeneration))]
    public static bool DoMeshGeneration_Prefix()
    {
        return false;
    }

    // Fixes a UI Object reference not set error during headless load due to there being no UI enabled.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UICommunicatorIndicator), nameof(UICommunicatorIndicator._OnLateUpdate))]
    public static bool UICommunicatorIndicatorOnLateUpdate_Prefix()
    {
        return false;
    }

    // The planetary shield coverage is computed on the GPU by PlanetATField.fieldGenerateShader.
    // The original code:
    //   Dispatch(kernel 0, (vertexCount-1)/256+1, 1, 1) -> GetData -> physicsArgs[0]
    //   coverage        = physicsArgs[0] / (vertexCount * 1000.0)
    //   energyMaxTarget = (long)(1200000000000.0 * coverage + 0.5)
    //   physicsArgs[0] == 0          -> isEmpty = true,  isSpherical = false
    //   physicsArgs[0] >= n * 1000   -> isEmpty = false, isSpherical = true   (full coverage)
    //   otherwise                    -> isEmpty = false, isSpherical = false  (holes in the field)
    //
    // On a host with a real GPU we simply let the original method run: the native shader then
    // produces the same coverage, isEmpty and isSpherical the single-player game does, which is
    // what the relay-landing raycast and the full-coverage achievement depend on.
    //
    // Scope the compatibility Unity path as well; native/CPU kernels never touch Unity GPU APIs.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlanetATField), nameof(PlanetATField.RecalculatePhysicsShape))]
    public static bool RecalculatePhysicsShape_Prefix(PlanetATField __instance, out bool __state)
    {
        __state = false;
        HeadlessShieldCompute.EnterScope();
        __state = true;
        return true;
    }

    // Last logged (isEmpty, isSpherical) pair per planet, so a planet is reported once and then only
    // when its field state actually changes. RecalculatePhysicsShape runs on the main thread only.
    private static readonly Dictionary<int, int> shieldStateByPlanet = [];

    // Closes the compute gate opened by the prefix and reports the coverage the native shader
    // produced. This is a Finalizer rather than a Postfix on purpose: a Postfix is skipped when the
    // original method throws, which would leave the compute gate open for the rest of the session
    // and let DysonSwarm start dispatching again. A Finalizer runs from a finally block, so the gate
    // is closed on every path.
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(PlanetATField), nameof(PlanetATField.RecalculatePhysicsShape))]
    public static void RecalculatePhysicsShape_Finalizer(PlanetATField __instance, bool __state, Exception __exception)
    {
        if (__state) HeadlessShieldCompute.ExitScope();
        if (__exception != null)
        {
            Log.Error("[headless] Shield computation failed after configured fallback; stopping.", __exception);
            Application.Quit(1);
            return; // Harmony still propagates the original exception.
        }
        if (!__state) return;

        if (__instance == null || __instance.generatorCount <= 0)
        {
            return;
        }

        var args = __instance.physicsArgs;
        var vertexCount = __instance.physicsMeshVertsOriginal?.Length ?? 0;
        var planetId = __instance.planet?.id ?? -1;
        if (args == null || args.Length == 0 || vertexCount == 0)
        {
            Log.Error($"[headless] shield planet={planetId} has no valid native physics output; stopping.");
            Application.Quit(1);
            return;
        }

        var covered = args[0];
        var expected = vertexCount * 1000L;
        var coverage = covered / (double)expected;
        var state = (__instance.isEmpty ? 1 : 0) | (__instance.isSpherical ? 2 : 0);

        if (shieldStateByPlanet.TryGetValue(planetId, out var previous) && previous == state)
        {
            return;
        }
        shieldStateByPlanet[planetId] = state;

        Log.Info($"[headless] shield planet={planetId} generators={__instance.generatorCount} " +
                 $"covered={covered}/{expected} coverage={coverage:F4} " +
                 $"energyMaxTarget={__instance.energyMaxTarget} isEmpty={__instance.isEmpty} " +
                 $"isSpherical={__instance.isSpherical}");
    }

    // Removed upstream balance hack:
    //   __result &= !(__instance.energy > 0 && __instance.generatorCount >= 7);
    // It forced relays to stop landing once 7+ generators were online, which overrode the original
    // per-planet raycast test that TestRelayCondition performs against the shield's actual holes.
    // With the native shield computation restored that override is no longer wanted.
}
