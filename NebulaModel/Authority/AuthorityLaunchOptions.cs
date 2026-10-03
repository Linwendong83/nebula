#region

using System;
using System.Globalization;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// Fault-spec syntax for the test harness (TASKS.md A22, DESIGN 11).
/// </summary>
/// <remarks>
/// <para>
/// Release: the authority mode is always on. There are no launch flags: the former
/// <c>-nebula-authority</c> / <c>-nebula-authority-faults</c> command-line surface was removed.
/// Fault specs are driven by the test harness verbs, using <see cref="TryParseFaultSpec"/> directly.
/// </para>
/// </remarks>
public sealed class AuthorityLaunchOptions
{

    /// <summary>
    /// Parses a fault spec: semicolon- or comma-separated tokens, each
    /// <c>key</c> or <c>key=value</c>, mapping onto <see cref="FaultInjectionRules"/>.
    /// </summary>
    /// <remarks>
    /// Keys: <c>delay</c>, <c>copies</c>, <c>reorder</c> (counts), <c>pause</c> (holds delivery),
    /// <c>droplifecycle</c>/<c>dropstate</c>/<c>dropdigest</c> (armed-once message loss),
    /// <c>droplifecycle=n</c>/<c>dropstate=n</c>/<c>dropdigest=n</c> (arm n times — the driver can
    /// also re-arm at runtime), <c>dropchunk</c>/<c>dupchunk</c> (chunk index), <c>truncatechunk</c>,
    /// <c>corruptchunk</c>, <c>corrupthash</c>.
    /// </remarks>
    public static bool TryParseFaultSpec(string spec, out FaultInjectionRules rules, out string error)
    {
        rules = new FaultInjectionRules();
        error = null;
        if (string.IsNullOrWhiteSpace(spec))
        {
            error = "empty fault spec";
            return false;
        }

        var tokens = spec.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawToken in tokens)
        {
            var token = rawToken.Trim();
            if (token.Length == 0) continue;
            var separator = token.IndexOf('=');
            var key = separator < 0 ? token.ToLowerInvariant() : token.Substring(0, separator).ToLowerInvariant();
            var valueText = separator < 0 ? null : token.Substring(separator + 1);

            switch (key)
            {
                case "pause":
                    rules.Paused = true;
                    continue;
                case "truncatechunk":
                    rules.TruncateChunk = true;
                    continue;
                case "corruptchunk":
                    rules.CorruptChunkBytes = true;
                    continue;
                case "corrupthash":
                    rules.CorruptCommitHash = true;
                    continue;
                case "droplifecycle" when valueText == null:
                    rules.DropNextLifecycle = true;
                    continue;
                case "dropstate" when valueText == null:
                    rules.DropNextWorldState = true;
                    continue;
                case "dropdigest" when valueText == null:
                    rules.DropNextDigest = true;
                    continue;
            }

            int value;
            if (valueText == null || !int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out value) || value < 0)
            {
                error = "fault key '" + key + "' needs a non-negative integer value";
                rules = null;
                return false;
            }

            switch (key)
            {
                case "delay":
                    rules.DelayPumps = value;
                    break;
                case "copies":
                    rules.ExtraCopies = value;
                    break;
                case "reorder":
                    rules.ReorderDepth = value;
                    break;
                case "droplifecycle":
                    rules.DropNextLifecycle = value > 0;
                    break;
                case "dropstate":
                    rules.DropNextWorldState = value > 0;
                    break;
                case "dropdigest":
                    rules.DropNextDigest = value > 0;
                    break;
                case "dropchunk":
                    rules.DropChunkIndex = value;
                    break;
                case "dupchunk":
                    rules.DuplicateChunkIndex = value;
                    break;
                default:
                    error = "unknown fault key '" + key + "'";
                    rules = null;
                    return false;
            }
        }
        return true;
    }
}
