#region

using System;
using NebulaModel.Authority;
using NebulaModel.Logger;

#endregion

namespace NebulaPatcher.Patches.Authority;

/// <summary>
/// The one place the authority mode is entered (release: unconditional).
/// </summary>
/// <remarks>
/// <para>
/// Release: the host-authority mode is the only multiplayer mode. There are no launch flags.
/// Enabling is fail-closed — the required hooks (and every registered transpiler verifier) are
/// checked before the mode is adopted, and a failure leaves the error in the log without entering
/// the mode. The session gate (<c>AuthoritySession.BeginAuthorityWorld</c>) re-verifies, so a
/// failed startup can never produce a half-entered authority world. There is no per-room
/// override and no per-scope hot toggle. Single-player still runs vanilla through the guard's
/// no-authority-world path until a multiplayer world begins.
/// </para>
/// <para>
/// Fault injection is driven by <c>AuthorityFaultControl</c> directly through its harness verbs.
/// </para>
/// </remarks>
public static class AuthorityStartup
{
    /// <summary>Enters the authority mode after verification (the tests' seam).</summary>
    public static void Apply(Func<bool> verifyLoadOnce)
    {
        if (verifyLoadOnce == null) throw new System.ArgumentNullException(nameof(verifyLoadOnce));

        if (!verifyLoadOnce())
        {
            Log.Error("[authority] hook verification failed; authority mode not entered: " +
                      AuthorityRuleGuard.LoadFailure);
            return;
        }

        Log.Info("[authority] authority mode enabled (release: unconditional, fail-closed on hook verification)");
    }
}
