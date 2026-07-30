// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Gate-hygiene invariant: every BindingTests skip
/// that blames Mono "Issue 1" / the <c>!ji-&gt;async</c> assertion MUST have at least one
/// CallConvSwift P/Invoke on its path. Issue 1 fires only during a signal-handler unwind
/// through a CallConvSwift frame (see <c>feedback_mono_jit_blame</c>); a crash on a pure
/// CallConvCdecl path is OUR bug, never upstream, and must not be masked by a skip.
///
/// The invariant is enforced per-MEMBER, not per-type or per-library (a "lib-wide grep" would
/// pass trivially because the test library always contains <em>some</em> CallConvSwift symbol).
/// Each Issue-1 skip must NAME the specific Swift mangled entry-point symbol (<c>$s…</c>) that is
/// its CallConvSwift call, and that symbol must:
///   (a) actually be emitted as a CallConvSwift P/Invoke in the generated bindings, AND
///   (b) belong to a Swift type the skipped test method actually references in its body.
/// A false skip on a pure-Cdecl path cannot satisfy (a); a skip that names an unrelated
/// CallConvSwift symbol to game the gate cannot satisfy (b).
/// </summary>
public class Issue1SkipAttributionTests
{
    // "Issue 1" or the bare "!ji->async" assertion text (literal arrow form used in attributes).
    private static readonly Regex Issue1Marker =
        new(@"issue\s*1|ji-?>async|ji-&gt;async", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A Swift mangled symbol, e.g. $s20SwiftBindingsTestLib21OptionalGenericHolderVMa.
    private static readonly Regex SwiftSymbol = new(@"\$s[0-9A-Za-z_]+", RegexOptions.Compiled);

    private static readonly Regex EntryPoint =
        new("EntryPoint\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled);

    // The generated test library's module name — too generic to prove path-specificity on its own.
    private const string ModuleName = "SwiftBindingsTestLib";

    [SkippableFact]
    [Trait("Category", GeneratedBindingsOutputRequirement.TraitCategory)]
    public void EveryIssue1Skip_NamesACallConvSwiftEntryPoint_OnItsOwnPath()
    {
        var repoRoot = LocateRepoRoot();
        var runtimeTestsDir = Path.Combine(repoRoot, "BindingTests", "RuntimeTestsApp");
        var outputDir = Path.Combine(repoRoot, "BindingTests", "output");
        var preludePath = Path.Combine(outputDir, $"{ModuleName}.cs");

        Assert.True(Directory.Exists(runtimeTestsDir), $"RuntimeTestsApp not found at {runtimeTestsDir}");
        // The module is emitted file-per-top-level-type: scan the prelude plus every
        // {module}.Types.*.cs so a P/Invoke that moved into a type file still counts.
        var generatedBindingsExist = SplitModuleSource.Exists(outputDir, ModuleName);
        GeneratedBindingsOutputRequirement.SkipUnlessAvailable(generatedBindingsExist,
            $"Generated bindings not found at {preludePath}");

        HashSet<string> callConvSwiftSymbols = ExtractCallConvSwiftEntryPoints(SplitModuleSource.ReadAll(outputDir, ModuleName));

        // Sanity: the extractor must find the library's known CallConvSwift surface, otherwise a
        // parser drift would silently make every check vacuously pass.
        Assert.True(callConvSwiftSymbols.Count > 0,
            "No CallConvSwift entry points were extracted from the generated bindings — the extractor is broken.");

        var violations = new List<string>();
        var issue1SkipCount = 0;

        foreach (var file in Directory.EnumerateFiles(runtimeTestsDir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            var rel = Path.GetRelativePath(repoRoot, file);

            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("[Skip", System.StringComparison.Ordinal))
                    continue;

                // Accumulate the (possibly multi-line) attribute up to its closing ")]".
                var attr = new StringBuilder();
                int attrEnd = i;
                while (attrEnd < lines.Length)
                {
                    attr.Append(lines[attrEnd]).Append('\n');
                    if (lines[attrEnd].Contains(")]")) break;
                    attrEnd++;
                }

                var attrText = attr.ToString();
                if (!Issue1Marker.IsMatch(attrText))
                {
                    i = attrEnd; // skip past this attribute
                    continue;
                }

                issue1SkipCount++;

                // The documenting comment directly above the attribute may carry the symbol too.
                var context = attrText;
                if (i > 0 && lines[i - 1].TrimStart().StartsWith("//", System.StringComparison.Ordinal))
                    context = lines[i - 1] + "\n" + attrText;

                var body = CollectMethodBody(lines, attrEnd + 1);

                var named = SwiftSymbol.Matches(context).Select(m => m.Value).Distinct().ToList();
                if (named.Count == 0)
                {
                    violations.Add($"{rel}:{i + 1} — Issue-1 skip names no CallConvSwift Swift entry-point symbol ($s…). " +
                        "An Issue-1 skip must document the specific CallConvSwift call on its path (in the reason or a comment directly above), or be removed — a crash on a pure-Cdecl path is our bug.");
                    i = attrEnd;
                    continue;
                }

                // A named symbol justifies the skip iff it is emitted CallConvSwift AND its Swift
                // type is referenced by this very test method (path-specific, not lib-wide).
                var justified = named.FirstOrDefault(sym =>
                    callConvSwiftSymbols.Contains(sym) && SymbolTypeReferencedInBody(sym, body));

                if (justified is null)
                {
                    var emittedSwift = named.Where(callConvSwiftSymbols.Contains).ToList();
                    string detail = emittedSwift.Count == 0
                        ? $"none of the named symbol(s) [{string.Join(", ", named)}] are emitted as a CallConvSwift P/Invoke in the generated bindings (path is pure-Cdecl — our bug, or the symbol is wrong)"
                        : $"named CallConvSwift symbol(s) [{string.Join(", ", emittedSwift)}] are not on this test's path (their Swift type is never referenced in the method body — looks like an unrelated symbol)";
                    violations.Add($"{rel}:{i + 1} — {detail}.");
                }

                i = attrEnd;
            }
        }

        // There should be at least one genuine Issue-1 skip; if the count drops to zero the
        // marker/regex likely drifted (or all were removed — update this guard intentionally).
        Assert.True(issue1SkipCount > 0,
            "No Issue-1 skips were found in RuntimeTestsApp. If this is intentional, relax this guard; " +
            "otherwise the Issue-1 marker regex has drifted and the gate is now vacuous.");

        Assert.True(violations.Count == 0,
            "Issue-1 skip attribution invariant violated — each must name a CallConvSwift entry point on its own path:\n  " +
            string.Join("\n  ", violations));
    }

    /// <summary>
    /// Collects CallConvSwift <c>EntryPoint</c> symbols from the generated bindings. The generator
    /// emits, in order: <c>[UnmanagedCallConv(... CallConvSwift)]</c> then
    /// <c>[LibraryImport(... EntryPoint = "$s…")]</c> then the <c>partial</c> declaration.
    ///
    /// The attribute names are matched from the open paren rather than the opening bracket, so
    /// the match holds whether or not the emitted attribute is namespace-qualified — which it
    /// is, since the generated code shares a namespace with the bound module's own types.
    /// Anchoring on the bracket would make this silently extract nothing, and every check
    /// downstream would pass vacuously.
    /// </summary>
    private static HashSet<string> ExtractCallConvSwiftEntryPoints(string generated)
    {
        var result = new HashSet<string>(System.StringComparer.Ordinal);
        bool pendingSwift = false;

        foreach (var raw in generated.Split('\n'))
        {
            if (raw.Contains("UnmanagedCallConv(", System.StringComparison.Ordinal))
            {
                pendingSwift = raw.Contains("CallConvSwift", System.StringComparison.Ordinal);
                continue;
            }

            var m = EntryPoint.Match(raw);
            if (m.Success && pendingSwift)
                result.Add(m.Groups[1].Value);

            // End of the attribute block resets the pending convention.
            if (raw.Contains("partial", System.StringComparison.Ordinal) ||
                raw.Contains("extern", System.StringComparison.Ordinal))
                pendingSwift = false;
        }

        return result;
    }

    /// <summary>
    /// True if the Swift type embedded in <paramref name="symbol"/> (a length-prefixed mangled name)
    /// is referenced anywhere in <paramref name="body"/>. The module name is excluded as too generic.
    /// </summary>
    private static bool SymbolTypeReferencedInBody(string symbol, string body)
    {
        foreach (var id in ExtractSwiftIdentifiers(symbol))
        {
            if (id.Length < 4 || id == ModuleName) continue;
            if (body.Contains(id, System.StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Parses the length-prefixed identifiers out of a Swift mangled symbol (e.g.
    /// <c>$s20SwiftBindingsTestLib30OptionalThrowingModifierHolderC9validator…</c> →
    /// ["SwiftBindingsTestLib", "OptionalThrowingModifierHolder", "validator"]).
    /// </summary>
    private static List<string> ExtractSwiftIdentifiers(string symbol)
    {
        var ids = new List<string>();
        int i = symbol.StartsWith("$s", System.StringComparison.Ordinal) ? 2 : 0;
        while (i < symbol.Length)
        {
            if (!char.IsDigit(symbol[i])) { i++; continue; }
            int n = 0;
            while (i < symbol.Length && char.IsDigit(symbol[i])) { n = n * 10 + (symbol[i] - '0'); i++; }
            if (n <= 0 || i + n > symbol.Length) break;
            ids.Add(symbol.Substring(i, n));
            i += n;
        }
        return ids;
    }

    /// <summary>Returns the source text of the method whose signature begins at or after
    /// <paramref name="startIndex"/>, by brace-matching from its opening brace.</summary>
    private static string CollectMethodBody(string[] lines, int startIndex)
    {
        var sb = new StringBuilder();
        int depth = 0;
        bool started = false;

        for (int i = startIndex; i < lines.Length; i++)
        {
            var line = lines[i];
            // Stop if we reach the next member's attribute before any body brace (defensive).
            if (!started && line.TrimStart().StartsWith("[Skip", System.StringComparison.Ordinal))
                break;

            sb.Append(line).Append('\n');
            foreach (var c in line)
            {
                if (c == '{') { depth++; started = true; }
                else if (c == '}') { depth--; }
            }
            if (started && depth <= 0) break;
        }

        return sb.ToString();
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SwiftBindings.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
