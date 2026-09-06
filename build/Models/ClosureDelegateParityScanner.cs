// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// ClosureDelegateParityScanner — the decision model behind `nuke binding-tests --compile-only`'s
// closure delegate-type parity gate.
//
// A closure parameter is bridged in two halves that must agree on ONE C# delegate type. The public
// method signature declares the type a consumer's lambda is bound to and the type the delegate is
// stored under; the [UnmanagedCallersOnly] trampoline recovers it with
// `SwiftClosureMarshaller.GetDelegateFrom(Boxed)Context<T>`, which is an unchecked cast of the
// GCHandle target. When the two halves were computed by two independent translators they could
// disagree — `Action<SwiftResult<SwiftOptional<ExistentialContainer0>, ExistentialContainer1>>`
// stored, `Action<SwiftResult<SwiftOptional<ExistentialContainer0>, AnyError>>` recovered — and the
// disagreement is invisible to both compilers: the C# compiles, the Swift compiles, and the FIRST
// callback throws InvalidCastException inside the trampoline, where it becomes a
// FailFastUnhandledClosureException and aborts the process.
//
// The generator now derives both halves from one computation, so this scanner is the standing
// assertion that they stay derived from one computation. It reads emitted C# rather than generator
// internals on purpose: the two strings a consumer's process actually depends on are the ones in the
// file, and a future refactor that reintroduces a second translator shows up here no matter which
// layer it lives in.
//
// The check is deliberately weak in one direction — a cast type must appear SOMEWHERE in the same
// file as a public delegate type, not on the specific member it belongs to. Tying a trampoline to
// its member would mean re-deriving the emitter's own thunk-naming scheme inside a gate that exists
// to distrust it. Per-file containment already catches every divergence observed, because a
// translator that disagrees disagrees for the whole shape, not for one member of it.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// One trampoline cast whose delegate type has no matching public delegate type in the same file.
/// </summary>
internal sealed class ClosureDelegateMismatch
{
    /// <summary>File the divergence was found in, as handed to the scanner.</summary>
    public string File { get; init; } = string.Empty;

    /// <summary>The cast's type argument exactly as written in the generated source.</summary>
    public string CastType { get; init; } = string.Empty;

    /// <summary>The normalized form actually compared, kept for diagnostics.</summary>
    public string NormalizedCastType { get; init; } = string.Empty;
}

/// <summary>
/// Result of scanning one or more generated files.
/// </summary>
internal sealed class ClosureDelegateParityVerdict
{
    /// <summary>Every trampoline cast whose type is absent from its file's public delegate surface.</summary>
    public IReadOnlyList<ClosureDelegateMismatch> Mismatches { get; init; } = Array.Empty<ClosureDelegateMismatch>();

    /// <summary>How many trampoline casts were seen in total. Zero means the scanner found nothing to judge.</summary>
    public int CastCount { get; init; }

    /// <summary>How many files carried at least one trampoline cast.</summary>
    public int FilesWithCasts { get; init; }

    public bool Passed => Mismatches.Count == 0;
}

/// <summary>
/// Extracts trampoline delegate casts and public delegate types out of generated C# and reports any
/// cast whose type does not appear on the same file's public surface.
/// </summary>
internal static class ClosureDelegateParityScanner
{
    // `SwiftClosureMarshaller.GetDelegateFromContext<T>(...)` and its boxed sibling — the two
    // unchecked recoveries of a stored user delegate.
    private static readonly Regex CastHead = new(
        @"GetDelegateFrom(?:Boxed)?Context\s*<",
        RegexOptions.Compiled);

    // `Action` / `Func`, qualified or not. A bare `Action` (no type arguments) is a legitimate
    // delegate type in its own right, so it is collected too.
    private static readonly Regex DelegateHead = new(
        @"(?:global::)?(?:System\.)?\b(Action|Func)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Scans one generated file. <paramref name="file"/> is only used to label mismatches.
    /// </summary>
    public static ClosureDelegateParityVerdict ScanFile(string file, string text)
        => Scan(new[] { (file, text) });

    /// <summary>
    /// Scans a set of generated files, judging each file's casts against that same file's public
    /// delegate surface.
    /// </summary>
    public static ClosureDelegateParityVerdict Scan(IEnumerable<(string File, string Text)> files)
    {
        var mismatches = new List<ClosureDelegateMismatch>();
        int castCount = 0;
        int filesWithCasts = 0;

        foreach (var (file, text) in files)
        {
            var casts = ExtractCastTypes(text);
            if (casts.Count == 0)
                continue;

            filesWithCasts++;
            castCount += casts.Count;

            // The public surface is every delegate type in the file EXCEPT the cast type arguments
            // themselves — otherwise every cast trivially matches itself.
            var surface = ExtractDelegateTypes(RemoveCastTypeArguments(text));

            foreach (var cast in casts)
            {
                var normalized = Normalize(cast);
                if (!surface.Contains(normalized))
                {
                    mismatches.Add(new ClosureDelegateMismatch
                    {
                        File = file,
                        CastType = cast,
                        NormalizedCastType = normalized,
                    });
                }
            }
        }

        return new ClosureDelegateParityVerdict
        {
            Mismatches = mismatches,
            CastCount = castCount,
            FilesWithCasts = filesWithCasts,
        };
    }

    /// <summary>
    /// Collapses the spellings the emitter uses interchangeably — `global::`, `System.` and `Swift.`
    /// qualification, whitespace inside type arguments, and a trailing `?` on a nullable delegate
    /// parameter — so a difference in the TYPE is what is compared, not a difference in how it was
    /// spelled at one of the two sites.
    /// </summary>
    public static string Normalize(string type)
    {
        var t = type.Replace("global::", string.Empty);
        t = Regex.Replace(t, @"\bSystem\.", string.Empty);
        t = Regex.Replace(t, @"\bSwift\.", string.Empty);
        t = Regex.Replace(t, @"\s+", string.Empty);
        return t.TrimEnd('?');
    }

    /// <summary>Every `GetDelegateFrom(Boxed)Context&lt;T&gt;` type argument, verbatim.</summary>
    public static IReadOnlyList<string> ExtractCastTypes(string text)
    {
        var result = new List<string>();
        foreach (Match m in CastHead.Matches(text))
        {
            int open = text.IndexOf('<', m.Index);
            if (open < 0)
                continue;
            int end = EndOfGeneric(text, open);
            if (end < 0)
                continue;
            result.Add(text.Substring(open + 1, end - open - 2));
        }
        return result;
    }

    /// <summary>Every `Action`/`Func` type occurrence, normalized.</summary>
    public static ISet<string> ExtractDelegateTypes(string text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in DelegateHead.Matches(text))
        {
            var nameGroup = m.Groups[1];
            int after = m.Index + m.Length;
            if (after < text.Length && text[after] == '<')
            {
                int end = EndOfGeneric(text, after);
                if (end > 0)
                {
                    result.Add(Normalize(text.Substring(nameGroup.Index, end - nameGroup.Index)));
                    continue;
                }
            }
            result.Add(Normalize(nameGroup.Value));
        }
        return result;
    }

    /// <summary>
    /// Returns the text with every cast's type argument spliced out, so the remainder is the file's
    /// public delegate surface.
    /// </summary>
    private static string RemoveCastTypeArguments(string text)
    {
        var sb = new StringBuilder(text.Length);
        int pos = 0;
        foreach (Match m in CastHead.Matches(text))
        {
            if (m.Index < pos)
                continue;
            int open = text.IndexOf('<', m.Index);
            if (open < 0)
                continue;
            int end = EndOfGeneric(text, open);
            if (end < 0)
                continue;
            sb.Append(text, pos, m.Index - pos);
            pos = end;
        }
        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }

    /// <summary>
    /// Given the index of a '&lt;', returns the index just past its matching '&gt;', or -1.
    /// Nested generic arguments are what make a naive scan wrong here.
    /// </summary>
    private static int EndOfGeneric(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '<')
            {
                depth++;
            }
            else if (text[i] == '>')
            {
                depth--;
                if (depth == 0)
                    return i + 1;
            }
        }
        return -1;
    }
}
