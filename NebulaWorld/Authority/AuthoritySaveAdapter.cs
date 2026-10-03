#region

using NebulaModel.Authority;
using NebulaModel.Logger;

#endregion

namespace NebulaWorld.Authority;

/// <summary>
/// Bridges the host authority session and the versioned save sidecar (TASKS.md A21, DESIGN 11).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Capture"/> runs at save time on the host only: it walks the session's ledger,
/// task ledger and player registry into the bounded DTO the codec writes. Everything captured is
/// host fact — a client never captures, and a client-side session produces nothing.
/// </para>
/// <para>
/// <see cref="ApplyRestore"/> runs once per loaded world, after <c>BeginAuthorityWorld</c> minted
/// the new epoch and before any player is seated. It applies the pure restore rules (balances
/// re-keyed under the new epoch, budgets restored all-idle, saved tasks reclaimed once) and leaves
/// the world untouched when the sidecar fails validation: an unusable sidecar yields an empty
/// ledger plus a loud error, never a half-truth.
/// </para>
/// </remarks>
public static class AuthoritySaveAdapter
{
    /// <summary>File extension of the sidecar that sits next to a <c>.server</c> save.</summary>
    public const string FileExtension = ".server.authority";

    /// <summary>
    /// Captures the host facts of one live host authority world, or null when this session is not
    /// one (a client, a legacy room, or a host whose world has not begun).
    /// </summary>
    public static AuthoritySaveState Capture(AuthoritySession session, string worldId)
    {
        if (session == null || !session.IsHostAuthority) return null;
        var state = new AuthoritySaveState
        {
            Schema = AuthoritySidecarSchema.Current,
            WorldId = worldId,
            SavedEpoch = session.Identity.Epoch,
            SavedHostTick = session.LastFrameTick
        };
        state.Players.AddRange(session.HostPlayers.CapturePlayersForSave());
        state.Accounts.AddRange(session.HostLedger.CaptureAccountsForSave());
        state.Budgets.AddRange(session.HostConstructionLedger.CaptureBudgetsForSave());
        state.Tasks.AddRange(session.HostConstructionLedger.CaptureLiveTasksForSave());
        return state;
    }

    /// <summary>
    /// Applies a decoded sidecar to a freshly begun host world. Returns false and leaves the
    /// world's model state empty when the restore was refused.
    /// </summary>
    public static bool ApplyRestore(AuthoritySession session, AuthoritySaveState state,
        out AuthoritySaveRestoreReport report)
    {
        report = null;
        if (session == null || !session.IsHostAuthority) return false;
        report = AuthoritySaveRestore.Apply(state, session.Identity.Epoch, session.HostLedger,
            session.HostConstructionLedger, session.HostPlayers);
        if (!report.Succeeded)
        {
            Log.Error("[authority] refusing sidecar restore: " + report.Error);
            return false;
        }
        Log.Info("[authority] sidecar restored under epoch " + session.Identity.Epoch + ": " + report);
        return true;
    }
}
