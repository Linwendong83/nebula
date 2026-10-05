#region

using NebulaModel.Authority;
using NebulaModel.Logger;

#endregion

namespace NebulaWorld.Authority;

/// <summary>
/// Connects the A05 rule guard to the live session (DESIGN 6 and 11).
/// </summary>
/// <remarks>
/// <para>
/// The guard itself is a pure policy in <see cref="AuthorityRuleGuard"/> so that the packet
/// processors, the world managers and the Harmony patches can all reach one decision point without
/// any of them depending on each other. This class is the only place that knows where the session
/// state lives, which is what keeps the model free of a session reference.
/// </para>
/// <para>
/// Nothing here turns the new mode on. The probe reports whatever the session already is, and while
/// that is <see cref="AuthorityMode.Legacy"/> every decision the guard makes is the vanilla one, so
/// single-player and existing rooms are unaffected.
/// </para>
/// </remarks>
public static class AuthorityGuardWiring
{
    private static bool installed;

    /// <summary>
    /// Registers the session probe and the refusal log sink, once.
    /// </summary>
    public static void Install()
    {
        if (installed) return;
        installed = true;
        AuthorityRuleGuard.Probe = ReadSession;
        AuthorityRuleGuard.RefusalSink = Log.Warn;
        AuthorityRuleGuard.EnsureInstalled();
    }

    /// <summary>Forgets the installation. Used by tests.</summary>
    public static void Reset()
    {
        installed = false;
        AuthorityRuleGuard.Probe = null;
        AuthorityRuleGuard.RefusalSink = null;
    }

    /// <summary>
    /// Reads the guard's view of the session: negotiated mode, whether a world epoch exists, which
    /// side this is, and whether the frame boundary currently has a replica apply open.
    /// </summary>
    /// <remarks>
    /// A missing or disposed session reads as legacy, which is the safe direction: the guard then
    /// allows the vanilla path rather than refusing work in a room that has no authority mode.
    /// </remarks>
    private static AuthorityGuardSession ReadSession()
    {
        var session = Multiplayer.Session;
        var identity = session?.Authority;
        if (identity is null)
        {
            return AuthorityGuardSession.Legacy;
        }

        var applyActive = !identity.IsHost &&
                          (session.AuthorityRuntime?.ApplyContext?.IsActiveOnCurrentThread ?? false);
        return new AuthorityGuardSession(identity.Mode, identity.IsActive, identity.IsHost, applyActive);
    }
}
