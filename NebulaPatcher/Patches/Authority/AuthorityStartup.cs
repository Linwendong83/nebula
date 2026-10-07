using System;
using NebulaModel.Authority;
using NebulaModel.Logger;

namespace NebulaPatcher.Patches.Authority;

/// <summary>Validates world-write guards before multiplayer can start.</summary>
public static class AuthorityStartup
{
    public static void Validate(Func<bool> verifyLoadOnce)
    {
        if (verifyLoadOnce == null) throw new ArgumentNullException(nameof(verifyLoadOnce));
        if (!verifyLoadOnce())
        {
            throw new InvalidOperationException("Multiplayer world-write guards are unavailable: " +
                                                AuthorityRuleGuard.LoadFailure);
        }
        Log.Info("[authority] multiplayer world-write guards verified");
    }

    /// <summary>A supported game build cannot override a failed guard verification.</summary>
    public static bool CanStartMultiplayer(bool supportedGameBuild) =>
        supportedGameBuild && AuthorityRuleGuard.IsLoadVerified;
}
