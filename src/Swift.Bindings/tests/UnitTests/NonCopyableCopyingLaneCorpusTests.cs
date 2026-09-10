// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// End-state evidence for the lanes that could route a <c>~Copyable</c> value through a value-witness
/// copy. Each lane ends in one of two dispositions and this sweep pins which one it got, read out of
/// the generated corpus rather than out of the emitter — the marker a consumer sees is the contract.
///
/// <list type="bullet">
/// <item><b>Refuse.</b> The closure argument, closure result and async-parameter lanes have no
/// ownership route: the invoke thunk heap-materialises each argument, the callback marshals the
/// produced value back, and an async parameter must be staged into a buffer that outlives the
/// suspension point while the caller keeps its own value. All three duplicate, all three compile,
/// and the copy witness for a non-copyable type is an unconditional trap, so the member is refused
/// at emission with a report row and a skip marker.</item>
/// <item><b>Take.</b> The async RETURN carrier does have one: the harness owns the allocation
/// outright and the C# side is the only consumer of it, so the value is taken rather than copied and
/// the member keeps its binding.</item>
/// </list>
///
/// <para>
/// Every member here is paired with a structurally identical copyable sibling — the fixture declares
/// <c>CopyableResourceToken</c> as <c>TrackedResource</c>'s twin down to the single stored field.
/// The pairing is what makes the sweep adversarial: a gate that had widened to refuse closures or
/// async members generally would still satisfy every refusal assertion below while quietly costing
/// the twins their bindings, and the twin assertions are what catch it.
/// </para>
/// </summary>
public class NonCopyableCopyingLaneCorpusTests
{
    /// <summary>
    /// Derived from the generator's own description map rather than restated, so a reworded reason
    /// moves both sides together while a reason that stops being emitted still fails.
    /// </summary>
    private static string LaneRefusalReason =>
        WorkaroundRecommendations.GetDescription(SkipReason.NonCopyableThroughCopyingLane)
        ?? throw new InvalidOperationException(
            "SkipReason.NonCopyableThroughCopyingLane has no description; the emitted marker would be unattributable.");

    // `// Unsupported: method 'name' — <reason> (<detail>)`
    private static readonly Regex MemberSkipComment =
        new(@"^\s*//\s*Unsupported:\s*\w+\s+'(?<member>[^']+)'\s*[—-]\s*(?<reason>.+)$", RegexOptions.Compiled);

    /// <summary>
    /// The refused half, one case per lane the register audited. <c>inspectThroughClosure</c> and its
    /// escaping/throwing/two-argument variants are the closure ARGUMENT lane;
    /// <c>produceThroughClosure</c> is the closure RESULT lane, the one with no ownership specifier
    /// in the Swift spelling to give it away; the two async members are the parameter staging lane,
    /// borrowing and consuming.
    /// </summary>
    [SkippableTheory]
    [Trait("Category", GeneratedBindingsOutputRequirement.TraitCategory)]
    [InlineData("inspectThroughClosure", "InspectThroughClosure")]
    [InlineData("inspectThroughEscapingClosure", "InspectThroughEscapingClosure")]
    [InlineData("inspectThroughThrowingClosure", "InspectThroughThrowingClosure")]
    [InlineData("inspectThroughPairClosure", "InspectThroughPairClosure")]
    [InlineData("produceThroughClosure", "ProduceThroughClosure")]
    [InlineData("awaitTrackedResourcePeek", "AwaitTrackedResourcePeekAsync")]
    [InlineData("consumeTrackedResourceAsync", "ConsumeTrackedResourceAsync")]
    public void CopyingLane_NonCopyableMember_IsRefusedWithTheLaneReason(string swiftName, string projectedName)
    {
        var corpus = LoadCorpus(out _, out var outputDir);
        GeneratedBindingsOutputRequirement.SkipUnlessAvailable(corpus.Count > 0,
            $"Generated bindings corpus not found under {outputDir}");

        var reasons = SkipReasonsFor(corpus, swiftName);
        var bindings = PublicDeclarationsOf(corpus, projectedName);

        // The refusal carries the lane's own reason, not merely "unsupported": a member refused under
        // the closure-support reason would emit a throwing tombstone stub instead of a skip row, which
        // tells a consumer "not yet bridgeable" about a permanent soundness limit and leaves the
        // report with no row naming the cause or a workaround.
        Assert.Contains(reasons, r => r.Contains(LaneRefusalReason, StringComparison.Ordinal));

        // And it is a refusal, not a warning: nothing callable survives.
        Assert.True(bindings.Count == 0,
            $"'{swiftName}' is refused but still has a public binding:{Environment.NewLine}" +
            string.Join(Environment.NewLine, bindings));
    }

    /// <summary>
    /// The copyable half of each of those pairs, which must be untouched. A refusal that keyed on the
    /// closure or the suspension point rather than on reachability of a non-copyable value lands here.
    /// </summary>
    [SkippableTheory]
    [Trait("Category", GeneratedBindingsOutputRequirement.TraitCategory)]
    [InlineData("inspectThroughCopyableClosure", "InspectThroughCopyableClosure")]
    [InlineData("inspectThroughEscapingCopyableClosure", "InspectThroughEscapingCopyableClosure")]
    [InlineData("inspectThroughThrowingCopyableClosure", "InspectThroughThrowingCopyableClosure")]
    [InlineData("inspectThroughCopyablePairClosure", "InspectThroughCopyablePairClosure")]
    [InlineData("produceCopyableThroughClosure", "ProduceCopyableThroughClosure")]
    [InlineData("awaitCopyableResourcePeek", "AwaitCopyableResourcePeekAsync")]
    [InlineData("makeCopyableResourceAsync", "MakeCopyableResourceAsync")]
    public void CopyingLane_CopyableSibling_KeepsBinding(string swiftName, string projectedName)
    {
        var corpus = LoadCorpus(out _, out var outputDir);
        GeneratedBindingsOutputRequirement.SkipUnlessAvailable(corpus.Count > 0,
            $"Generated bindings corpus not found under {outputDir}");

        Assert.True(PublicDeclarationsOf(corpus, projectedName).Count > 0,
            $"'{swiftName}' is copyable and must keep binding as '{projectedName}'.");
        Assert.DoesNotContain(SkipReasonsFor(corpus, swiftName),
            r => r.Contains(LaneRefusalReason, StringComparison.Ordinal));
    }

    /// <summary>
    /// The take, asserted as the ownership difference rather than as generated text. The async return
    /// carrier is allocated by the harness for this one consumer, so the <c>~Copyable</c> member moves
    /// the value out of it and leaves no second destroy behind; the copyable twin, whose caller-side
    /// contract is unchanged, still copies out and destroys the carrier. Both then free the
    /// allocation. Asserting both halves keeps the take narrow: widening it to every return type would
    /// turn the twin's copy into a use-after-move and fails here.
    /// </summary>
    [SkippableFact]
    [Trait("Category", GeneratedBindingsOutputRequirement.TraitCategory)]
    public void AsyncReturnCarrier_TakesTheNonCopyableResult_AndStillCopiesTheCopyableOne()
    {
        var corpus = LoadCorpus(out _, out var outputDir);
        GeneratedBindingsOutputRequirement.SkipUnlessAvailable(corpus.Count > 0,
            $"Generated bindings corpus not found under {outputDir}");

        // The member binds at all — the take is what made it bindable; before it the Swift wrapper
        // repeated the result into the carrier, which does not satisfy `Copyable` and cost the member
        // its emission.
        Assert.True(PublicDeclarationsOf(corpus, "MakeTrackedResourceAsync").Count > 0,
            "The ~Copyable async return lane takes rather than refuses, so the member must bind.");

        var nonCopyable = CompletionCallbackBody(corpus, "makeTrackedResourceAsyncOnComplete");
        var copyable = CompletionCallbackBody(corpus, "makeCopyableResourceAsyncOnComplete");

        Assert.Contains("InitializeWithTake", nonCopyable, StringComparison.Ordinal);
        Assert.DoesNotContain("InitializeWithCopy", nonCopyable, StringComparison.Ordinal);
        // A take leaves the carrier uninitialised; destroying it afterwards would run the deinit on a
        // moved-from value — the second deinit this whole lane exists to prevent.
        Assert.DoesNotContain("Destroy((void*)resultPtr", nonCopyable, StringComparison.Ordinal);

        Assert.Contains("InitializeWithCopy", copyable, StringComparison.Ordinal);
        Assert.DoesNotContain("InitializeWithTake", copyable, StringComparison.Ordinal);
        Assert.Contains("Destroy((void*)resultPtr", copyable, StringComparison.Ordinal);
    }

    #region Corpus helpers

    private static List<string> SkipReasonsFor(IEnumerable<string> corpus, string swiftName)
    {
        var reasons = new List<string>();
        foreach (var file in corpus)
        {
            foreach (var line in File.ReadAllLines(file))
            {
                var match = MemberSkipComment.Match(line);
                if (match.Success && match.Groups["member"].Value == swiftName)
                    reasons.Add(match.Groups["reason"].Value);
            }
        }
        return reasons;
    }

    private static List<string> PublicDeclarationsOf(IEnumerable<string> corpus, string projectedName)
    {
        var declarations = new List<string>();
        foreach (var file in corpus)
        {
            foreach (var line in File.ReadAllLines(file))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("public ", StringComparison.Ordinal) &&
                    trimmed.Contains($" {projectedName}(", StringComparison.Ordinal))
                    declarations.Add(trimmed);
            }
        }
        return declarations;
    }

    /// <summary>
    /// The body of one generated <c>[UnmanagedCallersOnly]</c> completion callback. The name is
    /// suffixed with a per-member hash in the output, so the lookup is by prefix, and the body runs
    /// to the first closing brace at the declaration's own indentation.
    /// </summary>
    private static string CompletionCallbackBody(IEnumerable<string> corpus, string namePrefix)
    {
        foreach (var file in corpus)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains($"void {namePrefix}", StringComparison.Ordinal))
                    continue;

                var indent = new string(' ', lines[i].Length - lines[i].TrimStart().Length);
                var closing = indent + "}";
                var body = new List<string>();
                for (int j = i; j < lines.Length; j++)
                {
                    body.Add(lines[j]);
                    if (j > i && lines[j] == closing)
                        return string.Join(Environment.NewLine, body);
                }
            }
        }

        Assert.Fail($"No generated completion callback named '{namePrefix}*' in the corpus.");
        return string.Empty;
    }

    private static List<string> LoadCorpus(out string repoRoot, out string outputDir)
    {
        repoRoot = LocateRepoRoot();
        outputDir = Path.Combine(repoRoot, "BindingTests", "output");
        return Directory.Exists(outputDir)
            ? Directory.EnumerateFiles(outputDir, "*.cs", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.Ordinal).ToList()
            : new List<string>();
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SwiftBindings.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    #endregion
}
