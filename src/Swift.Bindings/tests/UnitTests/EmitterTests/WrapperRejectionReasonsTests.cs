// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Text.RegularExpressions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The give-up reason a rejected member carries is written into consumer-facing text — the
/// SB0001 attribute message and the report row — so a token with no sentence behind it reaches
/// a consumer as an internal identifier they cannot act on.
/// </summary>
public class WrapperRejectionReasonsTests
{
    [Fact]
    public void EveryRejectTokenInTheEmitters_HasASentence()
    {
        // Scans the eligibility emitters' own source rather than a hand-kept list: a new
        // Reject("…") site is exactly the change that would otherwise ship an unexplained token,
        // and it is added in the emitter, not here. An identifier-shaped token has to be in the
        // table; a site that already rejects with prose (one carries a SWIFTBIND code and a
        // sentence) is self-describing and passes through Describe untouched.
        var sourceRoot = FindEmitterSourceRoot();
        var tokens = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, @"Reject\(\s*""([^""]+)""\s*\)"))
                tokens.Add(match.Groups[1].Value);

            // The highest-traffic sources are not literal Reject sites: the shared guards return a
            // bare token that each emitter then forwards as Reject(memberReason). Scanning only the
            // literal form would let a new shared token ship with no sentence behind it.
            foreach (var producer in SharedReasonProducers)
            {
                foreach (var token in ReturnedStringLiterals(text, producer))
                    tokens.Add(token);
            }
        }

        Assert.NotEmpty(tokens);
        var unexplained = tokens
            .Where(t => !t.Contains(' ') && !WrapperRejectionReasons.KnownReasons.Contains(t))
            .ToList();
        Assert.True(
            unexplained.Count == 0,
            "Wrapper give-up tokens with no sentence in WrapperRejectionReasons: "
            + string.Join(", ", unexplained));

        // Whichever arm a site takes, what a consumer reads is prose rather than the raw token.
        foreach (var token in tokens)
        {
            var described = WrapperRejectionReasons.Describe(token);
            Assert.Contains(' ', described);
            if (!token.Contains(' '))
                Assert.NotEqual(token, described);
        }
    }

    [Fact]
    public void Describe_KnownToken_ReadsAsASentenceFragmentNotAnIdentifier()
    {
        // The text is spliced into "No @_cdecl wrapper or native thunk available (…)", so it has
        // to read as prose there — an echoed token would be worse than saying nothing. The
        // sentences carry no trailing period because the caller composes them.
        foreach (var token in WrapperRejectionReasons.KnownReasons)
        {
            var described = WrapperRejectionReasons.Describe(token);
            Assert.False(string.IsNullOrWhiteSpace(described), $"'{token}' describes to nothing");
            Assert.NotEqual(token, described);
            Assert.Contains(' ', described);
            Assert.False(described.EndsWith('.'), $"'{token}' describes with a trailing period");
        }
    }

    [Fact]
    public void Describe_NullOrBlank_StillYieldsASentence()
    {
        // Never returns null or empty, so no caller can splice a hole into a message. Callers that
        // have no reason at all omit the clause themselves rather than relying on this.
        foreach (var blank in new string?[] { null, "", "   " })
        {
            var described = WrapperRejectionReasons.Describe(blank);
            Assert.False(string.IsNullOrWhiteSpace(described));
            Assert.Contains("unspecified", described, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Describe_UnknownToken_HumanizesRatherThanEchoingTheIdentifier()
    {
        // A token added without a table entry still has to read as English in the attribute
        // message; the completeness test above is what keeps this arm from being the norm.
        var described = WrapperRejectionReasons.Describe("some_new_guard_token");
        Assert.DoesNotContain("_", described);
        Assert.Contains("some new guard token", described, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Describe_AlreadyASentence_PassesThroughUnchanged()
    {
        // One emitter rejects with prose rather than a token. Humanizing it would mangle its
        // punctuation, and looking it up would fall through to the same mangling.
        const string prose = "property type is not supported by the wrapper emitter";
        Assert.Equal(prose, WrapperRejectionReasons.Describe(prose));
    }

    /// <summary>
    /// Methods that hand a give-up token back to an emitter instead of rejecting with a literal.
    /// </summary>
    private static readonly string[] SharedReasonProducers =
    {
        "GetMemberRejectionReason",
        "GetUnsupportedTypeSignatureReason",
    };

    [Fact]
    public void SharedReasonProducers_AreStillFoundByTheScanner()
    {
        // Guards the scan above: if either producer is renamed or moved, the extraction silently
        // finds nothing and the completeness test passes for the wrong reason.
        var sourceRoot = FindEmitterSourceRoot();
        foreach (var producer in SharedReasonProducers)
        {
            var found = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                .SelectMany(f => ReturnedStringLiterals(File.ReadAllText(f), producer))
                .ToList();
            Assert.True(found.Count > 0, $"No returned reason tokens found in '{producer}'");
        }
    }

    /// <summary>
    /// The string literals returned from the body of <paramref name="methodName"/>. Takes the text
    /// from the declaration to the first line that closes a member at type-member indentation, which
    /// is where a single method body ends in this codebase's formatting.
    /// </summary>
    private static IEnumerable<string> ReturnedStringLiterals(string source, string methodName)
    {
        var declaration = Regex.Match(source, @$"\b(?:string\??|var)\s+{Regex.Escape(methodName)}\s*\(");
        if (!declaration.Success)
            yield break;

        var end = source.IndexOf("\n    }", declaration.Index, StringComparison.Ordinal);
        var body = end < 0 ? source[declaration.Index..] : source[declaration.Index..end];

        foreach (Match match in Regex.Matches(body, @"return\s+""([^""]+)""\s*;"))
            yield return match.Groups[1].Value;
    }

    private static string FindEmitterSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Swift.Bindings", "src", "Emitter");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate src/Swift.Bindings/src/Emitter from " + AppContext.BaseDirectory);
    }
}
