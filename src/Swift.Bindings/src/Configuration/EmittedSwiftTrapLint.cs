// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// A single emitted Swift trap whose message does not carry the <c>[SwiftBindings]</c>
/// breadcrumb. A Swift runtime trap (<c>fatalError</c> / <c>preconditionFailure</c>) is
/// uncatchable and aborts the process with the message as the only attribution, so a
/// binding-emitted trap that omits the breadcrumb produces a crash that looks like the
/// consumer's own — the exact "trap anonymity" hazard this lint exists to surface.
/// </summary>
public readonly record struct UnprefixedTrap(int LineNumber, string Snippet);

/// <summary>
/// Result of scanning one emitted Swift source file for trap-anonymity hazards.
/// </summary>
public readonly record struct TrapLintResult
{
    /// <summary>
    /// Emitted <c>fatalError</c> / <c>preconditionFailure</c> calls with a string-literal
    /// message that does not begin with <c>[SwiftBindings]</c>. Expected to be empty: the
    /// emitter templates carry the prefix at source. A non-empty list signals a newly added
    /// trap that slipped the breadcrumb invariant.
    /// </summary>
    public IReadOnlyList<UnprefixedTrap> UnprefixedTraps { get; init; }

    /// <summary>
    /// Count of emitted force-cast (<c>as!</c>) operators. These are deliberate ABI downcasts;
    /// a failure surfaces as an anonymous Swift cast trap (no binding-authored message). Reported
    /// for visibility only — not rewritten, since the casts are intentional and correct.
    /// </summary>
    public int ForceCastCount { get; init; }
}

/// <summary>
/// Read-only lint over generated Swift wrapper / bridge sources. It does NOT mutate the emitted
/// code — the <c>[SwiftBindings]</c> breadcrumb is applied at the emitter templates themselves; this
/// pass verifies that invariant at the file-write boundary and reports the residual force-cast trap
/// surface. Run from <see cref="StringEmitter"/> as each Swift file is written. Pure scanning logic
/// lives in <see cref="Inspect"/> so it is unit-testable without a generator run.
/// </summary>
public static class EmittedSwiftTrapLint
{
    private static readonly string[] s_trapTokens = { "fatalError(", "preconditionFailure(" };

    // `as!` as a token: a non-identifier char (or start of string) before `as`, then `as!`.
    private static readonly Regex s_forceCast = new(@"(?<![A-Za-z0-9_])as!", RegexOptions.Compiled);

    private const string Breadcrumb = "[SwiftBindings]";

    /// <summary>
    /// Scans <paramref name="swiftSource"/> for emitted traps lacking the breadcrumb and for
    /// force-casts. Line-oriented; the portion of a line at or after a <c>//</c> is treated as a
    /// comment and ignored (a low-precision heuristic that is acceptable for a read-only diagnostic).
    /// </summary>
    public static TrapLintResult Inspect(string swiftSource)
    {
        var unprefixed = new List<UnprefixedTrap>();
        var forceCasts = 0;
        if (string.IsNullOrEmpty(swiftSource))
            return new TrapLintResult { UnprefixedTraps = unprefixed, ForceCastCount = 0 };

        var lines = swiftSource.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            // Drop any trailing line comment so a commented-out trap or `as!` is not counted.
            var line = lines[i];
            var commentIdx = line.IndexOf("//", StringComparison.Ordinal);
            var code = commentIdx >= 0 ? line.Substring(0, commentIdx) : line;
            if (code.Length == 0)
                continue;

            forceCasts += s_forceCast.Matches(code).Count;

            foreach (var token in s_trapTokens)
            {
                var search = 0;
                while (true)
                {
                    var idx = code.IndexOf(token, search, StringComparison.Ordinal);
                    if (idx < 0)
                        break;
                    search = idx + token.Length;

                    // Inspect the first argument: skip whitespace after '(' and require a string
                    // literal. A non-literal first arg (e.g. a variable) cannot carry a prefix and
                    // is not flagged here.
                    var p = idx + token.Length;
                    while (p < code.Length && (code[p] == ' ' || code[p] == '\t'))
                        p++;
                    if (p >= code.Length || code[p] != '"')
                        continue;

                    var messageStart = p + 1;
                    var rest = code.Substring(messageStart);
                    if (!rest.StartsWith(Breadcrumb, StringComparison.Ordinal))
                        unprefixed.Add(new UnprefixedTrap(i + 1, line.Trim()));
                }
            }
        }

        return new TrapLintResult { UnprefixedTraps = unprefixed, ForceCastCount = forceCasts };
    }

    /// <summary>
    /// Inspects an emitted Swift file and logs the result. A residual un-prefixed trap is a
    /// <c>LogWarning</c> (a breadcrumb-invariant regression); the force-cast surface is logged at
    /// information level for visibility. Never throws and never mutates input.
    /// </summary>
    public static void Validate(string swiftSource, string fileLabel, ILogger logger)
    {
        var result = Inspect(swiftSource);

        foreach (var trap in result.UnprefixedTraps)
        {
            logger.LogWarning(
                "SWIFTBIND-TRAP: emitted trap without a [SwiftBindings] breadcrumb in {File}:{Line} — {Snippet}",
                fileLabel, trap.LineNumber, trap.Snippet);
        }

        if (result.ForceCastCount > 0)
        {
            logger.LogInformation(
                "SWIFTBIND-TRAP: {File} emits {Count} force-cast(s) (as!); a cast failure surfaces as an anonymous Swift runtime trap.",
                fileLabel, result.ForceCastCount);
        }
    }
}
