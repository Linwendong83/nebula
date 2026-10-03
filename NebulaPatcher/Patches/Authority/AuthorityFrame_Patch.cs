#region

using System;
using HarmonyLib;
using NebulaModel.Logger;
using NebulaWorld;

#endregion

namespace NebulaPatcher.Patches.Authority;

/// <summary>
/// Installs the authority frame boundary at the point A01 proved quiescent.
/// </summary>
/// <remarks>
/// <para>
/// A01 established two facts that decide this hook. First, <c>ThreadManager.ProcessFrame</c> opens
/// with <c>frameBarrier.SignalAndWait</c>, so at its entry every worker is parked and no worker is
/// still inside a task. Second, the tasks after the last barrier (<c>StatisticsPostTick</c>,
/// <c>Scenario</c>, <c>CollectPreferences</c>) are not synchronised, so the end of
/// <c>GameLogic.LogicFrame</c> is <em>not</em> a safe point even though a short run may look idle
/// there. This patch therefore hooks <c>ProcessFrame</c> entry, not <c>LogicFrame</c> exit.
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
    public static void ProcessFrame_Prefix()
    {
        var session = Multiplayer.Session;
        if (session is null || !Multiplayer.IsActive) return;

        var runtime = session.AuthorityRuntime;
        if (runtime is null || !runtime.Identity.IsActive) return;

        var hostTick = GameMain.gameTick;
        try
        {
            // The previous frame's work is provably finished: every worker is parked on the frame
            // barrier that this prefix runs immediately after.
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
                    isInSector: GameMain.localPlanet == null);
            }
        }
        catch (Exception e)
        {
            Log.Error("[authority] frame boundary failed; the frame continues unchanged", e);
        }
    }
}
