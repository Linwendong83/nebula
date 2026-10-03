#region

using System;
using System.Collections.Generic;
using HarmonyLib;
using NebulaModel.Authority;
using NebulaModel.Logger;

#endregion

namespace NebulaPatcher.Patches.Authority;

/// <summary>
/// Verifies that a combat/construction method transformation really matched the IL it expects
/// (TASKS.md A05).
/// </summary>
/// <remarks>
/// <para>
/// A method transformation has a failure mode that resolving a method name cannot catch: the pattern
/// silently matches nothing, the patch reports success, and the method keeps running the vanilla
/// rule. A01 also established that the game's IL changes between patch versions, so "it matched when
/// we wrote it" is not evidence. The A05 acceptance therefore requires an exact match count, and the
/// new mode must not load when a required transformation did not apply.
/// </para>
/// <para>
/// The result is recorded rather than thrown. Patch installation happens once at startup, before any
/// room exists, so throwing there would break legacy rooms for a transformation the new mode is not
/// using yet. The record is instead checked when the mode is entered
/// (<see cref="AuthorityRuleGuard.RegisterVerifier"/>), which is where DESIGN 1.8 wants the refusal:
/// the new mode stops, single-player is untouched.
/// </para>
/// </remarks>
public static class AuthorityTranspilerGuard
{
    /// <summary>One required transformation and how many matches it must find.</summary>
    public readonly struct Requirement
    {
        public Requirement(string label, string hook, int expectedMatches, bool exact)
        {
            Label = label;
            Hook = hook;
            ExpectedMatches = expectedMatches;
            Exact = exact;
        }

        /// <summary>Transformation identity, unique per patched method.</summary>
        public string Label { get; }

        /// <summary>The A01 hook this transformation is required by, for the failure message.</summary>
        public string Hook { get; }

        /// <summary>Matches required when <see cref="Exact"/> is true, otherwise the minimum.</summary>
        public int ExpectedMatches { get; }

        /// <summary>True when any count other than <see cref="ExpectedMatches"/> is a failure.</summary>
        public bool Exact { get; }
    }

    private static readonly object gate = new();
    private static readonly Dictionary<string, int> observed = [];

    /// <summary>
    /// Transformations the new mode requires. A missing one means a shared rule would run unguarded,
    /// so entering the mode is refused.
    /// </summary>
    /// <remarks>
    /// Only the combat and construction transformations belong here. The DFG, hive and factory
    /// transformations are later cards' territory (A12/A13, W00) and are deliberately not claimed by
    /// this list, so this card cannot fail the mode for work it did not do.
    /// </remarks>
    public static readonly IReadOnlyList<Requirement> Required =
    [
        // The vanilla damage entries test "player id == 1" once. The rewrite replaces that single
        // constant, so anything other than exactly one occurrence means the game changed shape.
        new("SkillSystem.DamageObject.playerId", AuthorityHookLabels.SkillSystemDamageObject, 1, exact: true),
        new("SkillSystem.DamageGroundObjectByLocalCaster.playerId",
            AuthorityHookLabels.SkillSystemDamageGroundObjectByLocalCaster, 1, exact: true),
        new("SkillSystem.DamageGroundObjectByRemoteCaster.playerId",
            AuthorityHookLabels.SkillSystemDamageGroundObjectByRemoteCaster, 1, exact: true),
        // The item-required report is inserted at every material hand-off, so the count is a minimum:
        // at least one insertion must have happened for the transformation to have done anything.
        new("ConstructionModuleComponent.PlaceItems.itemRequired",
            "ConstructionModuleComponent.PlaceItems", 1, exact: false)
    ];

    /// <summary>
    /// Counts the matches of one pattern and records the result.
    /// </summary>
    /// <param name="label">Transformation identity.</param>
    /// <param name="instructions">The method body, already materialised.</param>
    /// <param name="matches">The instruction pattern to count.</param>
    /// <returns>How many times the pattern occurs.</returns>
    public static int CountMatches(string label, IReadOnlyList<CodeInstruction> instructions,
        params CodeMatch[] matches)
    {
        if (instructions == null) throw new ArgumentNullException(nameof(instructions));
        if (matches == null || matches.Length == 0)
        {
            throw new ArgumentException("At least one match is required.", nameof(matches));
        }

        var count = 0;
        if (instructions.Count >= matches.Length)
        {
            var matcher = new CodeMatcher(instructions);
            while (true)
            {
                // MatchForward(false, ...) searches forward from the current position and leaves the
                // matcher on the first instruction of the match. Advancing past the whole match is
                // what keeps overlapping windows from being counted twice.
                matcher.MatchForward(false, matches);
                if (matcher.IsInvalid) break;
                count++;
                matcher.Advance(matches.Length);
            }
        }

        Record(label, count);
        return count;
    }

    /// <summary>
    /// Counts the matches and reports whether the transformation may proceed.
    /// </summary>
    /// <remarks>
    /// The caller materialises the method body once and passes the same list on to its own matcher,
    /// so the verification and the rewrite cannot disagree about what the method contains.
    /// </remarks>
    public static bool VerifyCount(string label, IReadOnlyList<CodeInstruction> instructions, int expected,
        bool exact, params CodeMatch[] matches)
    {
        var actual = CountMatches(label, instructions, matches);
        if (exact ? actual == expected : actual >= expected) return true;

        Log.Error($"[authority] transpiler {label}: expected {(exact ? "exactly " : "at least ")}{expected} " +
                  $"match(es) in the installed game assembly but found {actual}; the transformation did not apply");
        return false;
    }

    /// <summary>How many matches one transformation actually found, or -1 when it never ran.</summary>
    public static int ObservedMatches(string label)
    {
        lock (gate)
        {
            return observed.TryGetValue(label ?? string.Empty, out var count) ? count : -1;
        }
    }

    /// <summary>Records one transformation's observed match count.</summary>
    public static void Record(string label, int matchCount)
    {
        lock (gate)
        {
            observed[label ?? string.Empty] = matchCount;
        }
    }

    /// <summary>Forgets every record. Used by tests.</summary>
    public static void Reset()
    {
        lock (gate)
        {
            observed.Clear();
        }
    }

    /// <summary>
    /// Reports every required transformation that did not apply with its expected match count.
    /// </summary>
    /// <remarks>
    /// Registered with <see cref="AuthorityRuleGuard"/> so the mode refuses to load when a
    /// transformation silently no-opped.
    /// </remarks>
    public static IReadOnlyList<string> VerifyRequired()
    {
        var failures = new List<string>();
        foreach (var requirement in Required)
        {
            var actual = ObservedMatches(requirement.Label);
            if (actual >= 0 && (requirement.Exact ? actual == requirement.ExpectedMatches
                    : actual >= requirement.ExpectedMatches))
            {
                continue;
            }

            failures.Add(actual < 0
                ? $"transpiler {requirement.Label} never ran (required by {requirement.Hook})"
                : $"transpiler {requirement.Label} matched {actual} of " +
                  $"{(requirement.Exact ? "exactly " : "at least ")}{requirement.ExpectedMatches} " +
                  $"(required by {requirement.Hook})");
        }
        return failures;
    }
}
