#region

using System;
using System.Threading;
using HarmonyLib;
using NebulaModel.Authority;
using NebulaModel.Logger;
using NebulaWorld;

#endregion

namespace NebulaPatcher.Patches.Authority;

/// <summary>
/// Installs the authority frame boundary at the point A01 proved quiescent.
/// </summary>
/// <remarks>
/// <para>
/// The previous frame's unsynchronised tail can still be running at ProcessFrame entry. Wait until
/// only the main thread remains at the frame barrier, without signalling it: workers then stay
/// parked throughout capture/apply. Vanilla releases them after this prefix returns.
/// </para>
/// <para>
/// There is exactly one frame barrier per frame, so there is exactly one quiescent instant. The same
/// hook therefore serves both halves of DESIGN 6: it captures the frame that just completed (whose
/// workers are now parked) and then admits the messages that will be applied to the frame about to
/// run. A post-frame hook would run while workers may still be inside the unsynchronised tail tasks,
/// so no second hook is installed.
/// </para>
/// <para>
/// The patch does nothing unless the session is a live authority world, so a legacy room and
/// single-player are unaffected. It never runs game rules: it drains the queue the socket thread
/// filled and calls the capture seam A06 fills.
/// </para>
/// </remarks>
[HarmonyPatch(typeof(ThreadManager))]
public class AuthorityFrame_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(ThreadManager.ProcessFrame))]
    public static void ProcessFrame_Prefix(ThreadManager __instance)
    {
        var session = Multiplayer.Session;
        if (session is null || !Multiplayer.IsActive) return;

        var runtime = session.AuthorityRuntime;
        if (runtime is null || !runtime.Identity.IsActive) return;
        if (!__instance.initialized || __instance.frameBarrier == null) return;

        // Entry is before SignalAndWait, not after it. Signalling here would release workers to
        // mutate pools concurrently with replica installation. Wait for their arrivals instead.
        var spins = 0;
        while (__instance.frameBarrier.remainingParticipantCount > 1)
        {
            Thread.SpinWait(32);
            if (++spins % 32 == 0) Thread.Yield();
        }

        var hostTick = GameMain.gameTick;
        try
        {
            // Every worker has arrived and cannot leave until vanilla signals the main participant.
            runtime.OnFrameComplete(hostTick);

            // Now admit the messages that arrived while the previous frame ran. Both queues were
            // filled by the socket thread, which never touches the world itself.
            runtime.OnFrameBoundary(hostTick);

            // A22: the standing subscription set follows where the client actually is. The context
            // is the client's own local view — the same observation the standing-set policy is
            // defined over — and a change of view is what issues the subscribe/unsubscribe delta.
            // The host re-checks every request against its registry, so this only plans delivery.
            if (!session.IsServer && GameMain.data != null)
            {
                runtime.UpdateStandingSubscriptions(
                    GameMain.localPlanet != null ? GameMain.localPlanet.id : 0,
                    isInSector: GameMain.localPlanet == null, supportsPool: HasReplicaAdapter);
            }
        }
        catch (Exception e)
        {
            Log.Error("[authority] frame boundary failed; the frame continues unchanged", e);
        }
    }

    // These are the sources registered by MultiplayerSession. Construction/resource pools still
    // use their native protocols; subscribing them without an adapter strands the client forever.
    private static bool HasReplicaAdapter(PoolKind kind) => kind == PoolKind.Entity ||
        kind == PoolKind.GroundEnemy || kind == PoolKind.GroundCraft ||
        kind == PoolKind.SpaceEnemy || kind == PoolKind.SpaceCraft;
}
