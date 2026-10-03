#region

#endregion

namespace NebulaWorld.Authority;

/// <summary>
/// Authority-mode policy for the legacy battle-visual facts (TASKS.md A14, pure decision).
/// </summary>
/// <remarks>
/// <para>
/// The old room reports visual facts from every peer: each client captures its own craft
/// lasers/plasmas from its local pools (<c>BattleVisualManager.CapturePlayer</c>) and the
/// server relays world turret/bomber effects captured from its own pools
/// (<c>CaptureWorld</c>). In a host authority world every one of those is a peer asserting
/// what the battle looks like instead of displaying what the host decided, so the mode
/// suppresses the legacy capture and takes its visuals from the host EffectBatch instead.
/// Legacy rooms keep the old path byte for byte.
/// </para>
/// <para>
/// Pure like <see cref="HostCombatPolicy"/>: call sites check the live mode bit and ask here
/// what to do, so tests drive the decision without a game process. The detached renderer
/// pools (<c>BattleVisualRenderer</c>) stay the presentation mechanism in both modes: they
/// decode with damage zeroed and call only renderer update/draw, never a damage, heal,
/// shield-resist or ledger entry.
/// </para>
/// </remarks>
public static class HostEffectPolicy
{
    /// <summary>True when legacy battle-visual capture must not run.</summary>
    public static bool ShouldSuppressLegacyBattleVisual(bool isHostAuthority) => isHostAuthority;

    /// <summary>Why a suppression happened, for logs. Callers branch on the bool, never on this text.</summary>
    public static string SuppressionReason() =>
        "legacy BattleVisual capture asserts peer-reported effects; host EffectBatch is the source in host authority mode (A14)";
}
