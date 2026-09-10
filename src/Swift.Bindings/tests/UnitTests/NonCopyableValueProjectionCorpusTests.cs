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
/// Corpus invariant for the two <c>~Copyable</c> projections. Which one a non-copyable struct takes
/// is decided by whether it carries a reference-bearing stored field, not by <c>~Copyable</c> itself:
///
/// <list type="bullet">
/// <item>no reference-bearing field → a by-value C# struct with NO payload. There is no handle to
/// mark consumed, <c>Dispose()</c> is a no-op even though Swift runs a <c>deinit</c>, and plain C#
/// assignment duplicates a value Swift permits exactly one owner of. All three are unsound and all
/// three COMPILE, so the type is refused at emission — <see cref="SkipReason.NonCopyableValueProjection"/>.</item>
/// <item>a reference-bearing field → a class carrying a real payload handle, which is what the
/// consumed-ownership machinery is written against. That flavor is fully supported.</item>
/// </list>
///
/// The <c>FrozenPlainToken</c> / <c>FrozenLabeledToken</c> pair in
/// <c>BindingTests/Sources/SwiftBindingsTestLib/Lifetime/FrozenNoncopyableProjection.swift</c> is
/// otherwise identical across that boundary, so a refusal that widens or narrows moves exactly one of
/// them and lands here. The refused half has no runtime test by construction — nothing is emitted to
/// call — which is why the assertion lives at this layer; the admitted half is exercised at runtime by
/// <c>FrozenNoncopyableProjectionTests</c>.
/// </summary>
public class NonCopyableValueProjectionCorpusTests
{
    private const string RefusedType = "FrozenPlainToken";
    private const string AdmittedType = "FrozenLabeledToken";

    /// <summary>
    /// The emitted skip comment carries the generator's own short description for the reason, so the
    /// expectation is derived from that map rather than restating the prose — a reworded reason moves
    /// both sides together, while a reason that stops being emitted still fails.
    /// </summary>
    private static string RefusalReason =>
        WorkaroundRecommendations.GetDescription(SkipReason.NonCopyableValueProjection)
        ?? throw new InvalidOperationException(
            "SkipReason.NonCopyableValueProjection has no description; the emitted marker would be unattributable.");

    // `// Unsupported: type 'Name' — <reason> (<detail>)` — capture the quoted subject so the refusal
    // can be attributed to a type rather than merely counted.
    private static readonly Regex TypeSkipComment =
        new(@"^\s*//\s*Unsupported:\s*type\s+'(?<type>[^']+)'\s*[—-]\s*(?<reason>.+)$", RegexOptions.Compiled);

    [SkippableFact]
    [Trait("Category", GeneratedBindingsOutputRequirement.TraitCategory)]
    public void NonCopyableValueProjection_RefusesThePayloadFreeFlavorOnly()
    {
        var corpus = LoadCorpus(out _, out var outputDir);
        GeneratedBindingsOutputRequirement.SkipUnlessAvailable(corpus.Count > 0,
            $"Generated bindings corpus not found under {outputDir}");

        var refusedTypes = new List<string>();
        foreach (var file in corpus)
        {
            foreach (var line in File.ReadAllLines(file))
            {
                var match = TypeSkipComment.Match(line);
                if (match.Success && match.Groups["reason"].Value.Contains(RefusalReason, StringComparison.Ordinal))
                    refusedTypes.Add(match.Groups["type"].Value);
            }
        }

        // Positive control: the payload-free flavor is refused, with the projection reason attached.
        Assert.Contains(RefusedType, refusedTypes);

        // Negative control on the same axis: the reference-bearing flavor differs only by carrying a
        // payload, and must NOT be swept up. Without this, a refusal that widened to every ~Copyable
        // struct would still satisfy the assertion above.
        Assert.DoesNotContain(AdmittedType, refusedTypes);
    }

    [SkippableFact]
    [Trait("Category", GeneratedBindingsOutputRequirement.TraitCategory)]
    public void RefusedNonCopyableType_ContributesNoPublicSurface()
    {
        // A type-level refusal is only sound if it also prunes everything that would have mentioned
        // the type — a surviving member would take an argument of a type that was never emitted.
        // Asserted as "no public declaration names it", which is independent of how the dependent
        // pruning is worded at each site.
        var corpus = LoadCorpus(out var repoRoot, out var outputDir);
        GeneratedBindingsOutputRequirement.SkipUnlessAvailable(corpus.Count > 0,
            $"Generated bindings corpus not found under {outputDir}");

        var leaks = new List<string>();
        foreach (var file in corpus)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("public ", StringComparison.Ordinal) &&
                    trimmed.Contains(RefusedType, StringComparison.Ordinal))
                {
                    leaks.Add($"{Path.GetRelativePath(repoRoot, file)}:{i + 1}: {trimmed}");
                }
            }
        }

        Assert.True(leaks.Count == 0,
            $"A refused ~Copyable type must not reach the public surface, but {leaks.Count} declaration(s) name it:{Environment.NewLine}" +
            string.Join(Environment.NewLine, leaks));
    }

    [SkippableFact]
    [Trait("Category", GeneratedBindingsOutputRequirement.TraitCategory)]
    public void AdmittedNonCopyableType_KeepsItsConsumedOwnershipMachinery()
    {
        // The other side of the boundary, and the guard against the previous two tests passing because
        // BOTH flavors stopped emitting: the payload-carrying flavor must still project as a class with
        // a real handle, an "already consumed" preflight, and a MarkConsumed on the consuming path.
        var corpus = LoadCorpus(out var repoRoot, out var outputDir);
        GeneratedBindingsOutputRequirement.SkipUnlessAvailable(corpus.Count > 0,
            $"Generated bindings corpus not found under {outputDir}");

        // Scoped to the file that DECLARES the type, so the ownership markers cannot be satisfied by
        // some unrelated type elsewhere in the corpus.
        var declaring = corpus.FirstOrDefault(f =>
            File.ReadAllText(f).Contains($"class {AdmittedType} ", StringComparison.Ordinal));
        Assert.True(declaring is not null,
            $"'{AdmittedType}' must still project as a class; no file under {outputDir} declares it.");

        var text = File.ReadAllText(declaring!);
        var where = Path.GetRelativePath(repoRoot, declaring!);

        Assert.True(text.Contains($"SwiftSafeHandle<{AdmittedType}>", StringComparison.Ordinal),
            $"{where}: the admitted flavor must carry a real payload handle.");
        Assert.True(text.Contains("IsConsumed", StringComparison.Ordinal),
            $"{where}: the admitted flavor must keep its already-consumed preflight.");
        Assert.True(text.Contains("MarkConsumed", StringComparison.Ordinal),
            $"{where}: the consuming path must mark the payload consumed.");
    }

    /// <summary>
    /// The compiled-probe evidence for the shapes this wave REFUSES rather than fixes, which the
    /// runtime suite cannot cover by construction — nothing is emitted to call.
    ///
    /// <para>The fixture declares three of them next to the working lane:
    /// <c>inspectOptionalFrozenToken</c> and <c>inspectOptionalTrackedResource</c> take
    /// <c>Optional&lt;~Copyable&gt;</c> (itself <c>~Copyable</c>, but spelled <c>Swift.Optional</c>,
    /// whose own record is copyable — the detection gap), and <c>NoncopyableToken</c> is a
    /// <c>~Copyable</c> enum, which no C# enum projection can express move-only. Each must appear
    /// in the generated output under a skip marker and never as an emitted member or type.</para>
    ///
    /// <para>Without this, the Optional and enum halves of the refusal could stop firing and every
    /// other test here would stay green: the corpus tests above read only the two frozen struct
    /// flavors, and a shape that binds instead of being refused fails at RUNTIME with a value-witness
    /// trap, which nothing in this repo's compile lane observes.</para>
    /// </summary>
    [SkippableTheory]
    [Trait("Category", GeneratedBindingsOutputRequirement.TraitCategory)]
    // member name, the emitted C# name it would take if the refusal stopped firing
    [InlineData("inspectOptionalFrozenToken", "InspectOptionalFrozenToken")]
    [InlineData("inspectOptionalTrackedResource", "InspectOptionalTrackedResource")]
    [InlineData("describeNoncopyableToken", "DescribeNoncopyableToken")]
    public void NonCopyableThroughAGenericSlot_IsRefusedWithAMarker_NotBound(
        string swiftName, string projectedName)
    {
        var corpus = LoadCorpus(out _, out var outputDir);
        GeneratedBindingsOutputRequirement.SkipUnlessAvailable(corpus.Count > 0,
            $"Generated bindings corpus not found under {outputDir}");

        var markers = new List<string>();
        var bindings = new List<string>();
        foreach (var file in corpus)
        {
            foreach (var line in File.ReadAllLines(file))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    if (trimmed.Contains("Unsupported:", StringComparison.Ordinal) &&
                        trimmed.Contains($"'{swiftName}'", StringComparison.Ordinal))
                        markers.Add(trimmed);
                }
                else if (trimmed.StartsWith("public ", StringComparison.Ordinal) &&
                         trimmed.Contains($" {projectedName}(", StringComparison.Ordinal))
                {
                    bindings.Add(trimmed);
                }
            }
        }

        Assert.True(markers.Count > 0,
            $"'{swiftName}' must be refused with a skip marker in the generated output under {outputDir}.");
        Assert.True(bindings.Count == 0,
            $"'{swiftName}' must not also be bound, but found:{Environment.NewLine}" +
            string.Join(Environment.NewLine, bindings));
    }

    /// <summary>
    /// The <c>~Copyable</c> ENUM half of the same evidence, at the type level: the enum itself is
    /// refused, so no C# enum or class for it reaches the surface. Separate from the member probe
    /// above because a member refusal and a type refusal are decided by different authorities
    /// (<c>MemberValidationPipeline</c>/<c>MemberGateEvaluator</c> versus <c>TypeSkipConditions</c>),
    /// and only one of them firing would still satisfy the other's assertion.
    /// </summary>
    [SkippableFact]
    [Trait("Category", GeneratedBindingsOutputRequirement.TraitCategory)]
    public void NonCopyableEnum_IsRefusedAtTheTypeLevel()
    {
        const string enumName = "NoncopyableToken";

        var corpus = LoadCorpus(out var repoRoot, out var outputDir);
        GeneratedBindingsOutputRequirement.SkipUnlessAvailable(corpus.Count > 0,
            $"Generated bindings corpus not found under {outputDir}");

        var refusedTypes = new List<string>();
        var declarations = new List<string>();
        foreach (var file in corpus)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                var match = TypeSkipComment.Match(lines[i]);
                if (match.Success)
                {
                    refusedTypes.Add(match.Groups["type"].Value);
                    continue;
                }
                if (trimmed.StartsWith("public ", StringComparison.Ordinal) &&
                    (trimmed.Contains($"enum {enumName}", StringComparison.Ordinal) ||
                     trimmed.Contains($"class {enumName} ", StringComparison.Ordinal)))
                {
                    declarations.Add($"{Path.GetRelativePath(repoRoot, file)}:{i + 1}: {trimmed}");
                }
            }
        }

        Assert.Contains(enumName, refusedTypes);
        Assert.True(declarations.Count == 0,
            $"A refused ~Copyable enum must not reach the public surface, but found:{Environment.NewLine}" +
            string.Join(Environment.NewLine, declarations));
    }

    /// <summary>
    /// The route half of the ownership story: a <c>consuming</c> <c>~Copyable</c> argument is only
    /// bindable where something downstream can MOVE it, and the emitted pairing that does so is the
    /// wrapper's <c>.move()</c> — which empties the caller's buffer — with the C# side's
    /// <c>MarkConsumed()</c>, which disarms the caller's own destroy. Emit one without the other
    /// and the value is destroyed twice (Swift's <c>deinit</c> in the callee, the handle's
    /// value-witness destroy in C#) or not at all; both compilers accept either emission, so only
    /// an assertion over the generated text catches it.
    ///
    /// <para>Whether a member reaches a move-capable route is decided by its WHOLE signature, which
    /// is why the fixture puts a parameter with nothing to do with ownership — a nested frozen
    /// struct — beside the token. Members shaped like this were refused outright while that
    /// parameter denied them a wrapper; they now reach one, so what has to hold is the pairing
    /// rather than the refusal. The refusal itself stays covered against a synthesised
    /// wrapper-less route in <c>CalleeArgumentOwnershipTests</c>.</para>
    ///
    /// <para>The fixture declares the two initializers side by side — same nested parameter, same
    /// token type, differing only in <c>consuming</c> versus <c>borrowing</c> — so an emission that
    /// widens or narrows moves exactly one of them: both must bind, and exactly the consuming one
    /// may mark its payload consumed.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Category", GeneratedBindingsOutputRequirement.TraitCategory)]
    public void ConsumedNonCopyable_OnAMoveCapableRoute_MarksItsPayloadConsumed_LeavingItsBorrowingSiblingUntouched()
    {
        const string hostType = "NestedFrozenTokenHost";

        var corpus = LoadCorpus(out var repoRoot, out var outputDir);
        GeneratedBindingsOutputRequirement.SkipUnlessAvailable(corpus.Count > 0,
            $"Generated bindings corpus not found under {outputDir}");

        var declaring = corpus.FirstOrDefault(f =>
            File.ReadAllText(f).Contains($"class {hostType} ", StringComparison.Ordinal));
        Assert.True(declaring is not null,
            $"'{hostType}' must still be emitted as a type; no file under {outputDir} declares it.");

        var where = Path.GetRelativePath(repoRoot, declaring!);
        var reason = WorkaroundRecommendations.GetDescription(SkipReason.NonCopyableWithoutMoveCapableRoute)
            ?? throw new InvalidOperationException(
                "SkipReason.NonCopyableWithoutMoveCapableRoute has no description; the emitted marker would be unattributable.");

        var text = File.ReadAllText(declaring!);
        var lines = File.ReadAllLines(declaring!).Select(l => l.TrimStart()).ToList();

        var markers = lines
            .Where(l => l.StartsWith("//", StringComparison.Ordinal)
                        && l.Contains("Unsupported:", StringComparison.Ordinal)
                        && l.Contains(reason, StringComparison.Ordinal))
            .ToList();
        Assert.True(markers.Count == 0,
            $"{where}: '{hostType}' reaches a move-capable route, so no member may carry a route " +
            $"refusal; found {markers.Count}:{Environment.NewLine}{string.Join(Environment.NewLine, markers)}");

        var constructors = lines
            .Where(l => l.StartsWith($"public {hostType}(", StringComparison.Ordinal))
            .ToList();
        Assert.True(constructors.Count == 2,
            $"{where}: both the consuming and the borrowing initializer must bind, found " +
            $"{constructors.Count} public constructor(s):" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, constructors)}");
        Assert.Contains(constructors, c => c.Contains(AdmittedType, StringComparison.Ordinal)
                                           && !c.Contains("borrowedToken", StringComparison.Ordinal));
        Assert.Contains(constructors, c => c.Contains("borrowedToken", StringComparison.Ordinal));

        // The hand-over is the pairing, so it is counted rather than merely found: exactly the
        // consuming initializer disarms the caller's destroy. A second MarkConsumed would mean the
        // borrowing sibling was swept up with it and its caller's value silently emptied.
        var consumedMarks = CountOccurrences(text, ".MarkConsumed()");
        Assert.True(consumedMarks == 1,
            $"{where}: exactly the consuming initializer may mark its payload consumed, found " +
            $"{consumedMarks} MarkConsumed() call(s).");

        // The other half of the double-destroy: a value-witness copy of a ~Copyable payload is
        // `__swift_cannot_copy_noncopyable_type`, an unconditional trap both compilers accept.
        Assert.DoesNotContain("OwnedArgument.BeginValueTransfer", text);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
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
}
