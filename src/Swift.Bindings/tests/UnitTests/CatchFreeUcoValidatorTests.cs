// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Corpus invariant (Finding 38): every emitted <c>[UnmanagedCallersOnly]</c> (UCO) callback in the
/// generated bindings is wrapped in a <c>try</c>/<c>catch</c>. A managed exception that unwinds across
/// the native (Swift) call boundary is undefined behaviour — the process aborts with a corrupted,
/// undiagnosable stack — so a catch-free UCO body is a latent cross-boundary-unwind crash. The
/// <see cref="UcoGuardEmitter"/> is the single source of truth for that envelope; this test asserts no
/// emitter forgot to route through it.
///
/// Scoped to the generated <c>BindingTests/output/*.cs</c> corpus (not the runtime's hand-written
/// code), it is the cross-emitter end-to-end check that complements <see cref="UcoGuardEmitterTests"/>
/// (which tests the envelope in isolation): a new callback emitted without a guard compiles fine and
/// passes its own unit tests, but trips here.
/// </summary>
public class CatchFreeUcoValidatorTests
{
    // The emitted attribute is namespace-qualified, since the generated code shares a namespace with
    // the bound module's own types and a Swift type named `UnmanagedCallersOnly` would otherwise
    // capture the reference. Both spellings are accepted so this parser reads the attribute rather
    // than one particular spelling of it.
    private const string InteropNamespacePrefix = "global::System.Runtime.InteropServices.";
    private const string UcoAttributeName = "UnmanagedCallersOnly";

    /// <summary>
    /// True when <paramref name="line"/> IS a UCO attribute, as opposed to a comment mentioning one:
    /// the bracket must open the (trimmed) line, which a <c>//</c> comment never does.
    /// </summary>
    private static bool IsUcoAttributeLine(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith("[", System.StringComparison.Ordinal))
            return false;

        var inner = trimmed.Substring(1).TrimStart();
        if (inner.StartsWith(InteropNamespacePrefix, System.StringComparison.Ordinal))
            inner = inner.Substring(InteropNamespacePrefix.Length);

        return inner.StartsWith(UcoAttributeName, System.StringComparison.Ordinal);
    }

    // A floor well below the real count (~872 at time of writing) but far above zero: if the parser
    // drifts and finds almost nothing, the "zero catch-free" assertion would pass vacuously — this
    // sanity guard makes that drift loud instead.
    private const int MinExpectedUcoMethods = 200;

    [SkippableFact]
    [Trait("Category", GeneratedBindingsOutputRequirement.TraitCategory)]
    public void EveryEmittedUcoCallback_IsWrappedInATryCatch()
    {
        var repoRoot = LocateRepoRoot();
        var outputDir = Path.Combine(repoRoot, "BindingTests", "output");

        var corpus = Directory.Exists(outputDir)
            ? Directory.EnumerateFiles(outputDir, "*.cs", SearchOption.TopDirectoryOnly).ToList()
            : new List<string>();

        GeneratedBindingsOutputRequirement.SkipUnlessAvailable(corpus.Count > 0,
            $"Generated bindings corpus not found under {outputDir}");

        var total = 0;
        var violations = new List<string>();

        foreach (var file in corpus.OrderBy(f => f, System.StringComparer.Ordinal))
        {
            var lines = File.ReadAllLines(file);
            var rel = Path.GetRelativePath(repoRoot, file);

            for (int i = 0; i < lines.Length; i++)
            {
                if (!IsUcoAttributeLine(lines[i]))
                    continue;

                total++;

                // Skip any further attribute lines, then brace-match the method body that follows.
                int sigStart = i + 1;
                while (sigStart < lines.Length && lines[sigStart].TrimStart().StartsWith("[", System.StringComparison.Ordinal))
                    sigStart++;

                var (body, signature) = CollectMethodBody(lines, sigStart);

                // A C# method body containing `catch` necessarily contains its `try` (it would not
                // compile otherwise), so `catch` presence is a faithful proxy for "guarded".
                if (!body.Contains("catch", System.StringComparison.Ordinal))
                {
                    violations.Add($"{rel}:{i + 1} — UCO callback is not wrapped in a try/catch " +
                        $"(a managed exception would unwind across the Swift boundary): {signature}");
                }
            }
        }

        Assert.True(total >= MinExpectedUcoMethods,
            $"Only {total} [UnmanagedCallersOnly] methods were found in the corpus (expected >= {MinExpectedUcoMethods}). " +
            "The corpus parser likely drifted, which would make the catch-free check vacuous.");

        Assert.True(violations.Count == 0,
            $"Catch-free [UnmanagedCallersOnly] callbacks found ({violations.Count}) — every UCO body must " +
            "route through UcoGuardEmitter's try/catch envelope:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// Returns the source text of the method whose signature begins at <paramref name="startIndex"/>,
    /// by brace-matching from its opening brace, plus a one-line signature for diagnostics.
    /// </summary>
    private static (string body, string signature) CollectMethodBody(string[] lines, int startIndex)
    {
        var sb = new StringBuilder();
        var signature = startIndex < lines.Length ? lines[startIndex].Trim() : "<eof>";
        int depth = 0;
        bool started = false;

        for (int i = startIndex; i < lines.Length; i++)
        {
            var line = lines[i];
            sb.Append(line).Append('\n');
            foreach (var c in line)
            {
                if (c == '{') { depth++; started = true; }
                else if (c == '}') { depth--; }
            }
            if (started && depth <= 0) break;
        }

        return (sb.ToString(), signature.Length > 110 ? signature.Substring(0, 110) : signature);
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
