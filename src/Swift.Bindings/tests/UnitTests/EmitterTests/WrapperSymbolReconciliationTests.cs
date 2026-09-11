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
/// Reconciles the wrapper symbols a generated binding <i>calls</i> against the wrapper symbols the
/// generated Swift <i>defines</i>, and pins a handful of fixture accessors to the symbol the emitter
/// projects for them.
///
/// <para>
/// Why this needs its own layer. The wrapper-symbol registry is first-registration-wins: the second
/// member to claim a symbol is refused the claim and simply does not emit its Swift wrapper body.
/// The in-band contract gate that would catch that asks only whether the symbol is <i>registered</i>,
/// and after a collision it is — by the winner. So the loser's P/Invoke is emitted, it resolves, it
/// links, and it calls the <i>winner's</i> wrapper. Nothing reds: the symbol exists, so the
/// dangling-entry-point gate is satisfied, and the binding compiles and runs against the wrong
/// member.
/// </para>
///
/// <para>
/// Accessor symbols are where that is reachable rather than theoretical. They are spelled
/// <c>SBW_{Get|Set}_{module}_{type}_{property}</c> with no hash — methods, constructors and
/// subscripts all carry one — and a nested type's dots are flattened to underscores, so the
/// projection is not injective: a nested <c>Outer.Inner</c> and a top-level <c>Outer_Inner</c> with
/// the same property name land on one symbol.
/// </para>
///
/// <para>
/// The expected symbols are derived from <see cref="PropertyWrapperEmitter.GetAccessorSymbolName"/>
/// rather than restated, so a deliberate change to the scheme moves the expectation with the emitter
/// while a member that stops being wrapped still fails.
/// </para>
/// </summary>
public class WrapperSymbolReconciliationTests
{
    private const string HarnessModule = "SwiftBindingsTestLib";

    #region Adversarial negative controls (pure, no corpus)

    /// <summary>
    /// The detector's reason for existing: an expected symbol that the Swift side never defined is
    /// named. Without this direction a member that lost its wrapper reads as a clean pass.
    /// </summary>
    [Fact]
    public void ExpectedSymbolWithNoSwiftDefinition_IsReported()
    {
        var swift = """
            @_cdecl("SBW_Get_Demo_Holder_present")
            public func SBW_Get_Demo_Holder_present(_ self_: UnsafeRawPointer) -> Int32 { 0 }
            """;

        var defined = WrapperSymbolReconciliation.ParseDefinitions(swift);
        var expected = new[] { "SBW_Get_Demo_Holder_present", "SBW_Get_Demo_Holder_absent" };

        var missing = WrapperSymbolReconciliation.FindUndefined(expected, defined);

        Assert.Equal(new[] { "SBW_Get_Demo_Holder_absent" }, missing);
    }

    /// <summary>Positive control for the same detector: a complete definition set reports nothing.</summary>
    [Fact]
    public void ExpectedSymbolsAllDefined_ReportNothing()
    {
        var swift = """
            @_cdecl("SBW_Get_Demo_Holder_present")
            public func SBW_Get_Demo_Holder_present(_ self_: UnsafeRawPointer) -> Int32 { 0 }

            @_silgen_name("SBSW_Demo_Holder_describe_1A2B3C4D")
            public func SBSW_Demo_Holder_describe(_ self_: UnsafeRawPointer) { }
            """;

        var defined = WrapperSymbolReconciliation.ParseDefinitions(swift);

        Assert.Empty(WrapperSymbolReconciliation.FindUndefined(
            new[] { "SBW_Get_Demo_Holder_present", "SBSW_Demo_Holder_describe_1A2B3C4D" }, defined));
    }

    /// <summary>
    /// The other direction, re-derived here so the two halves of the reconciliation live together: a
    /// P/Invoke whose <c>EntryPoint</c> names a symbol the wrapper source does not define.
    /// </summary>
    [Fact]
    public void ReferencedSymbolWithNoSwiftDefinition_IsReported()
    {
        var csharp = """
            [global::System.Runtime.InteropServices.LibraryImport("Demo", EntryPoint = "SBW_Get_Demo_Holder_present")]
            [global::System.Runtime.InteropServices.LibraryImport("Demo", EntryPoint = "SBW_Get_Demo_Holder_vanished")]
            """;

        var referenced = WrapperSymbolReconciliation.ParseReferences(csharp);
        var defined = new HashSet<string>(new[] { "SBW_Get_Demo_Holder_present" }, StringComparer.Ordinal);

        Assert.Equal(new[] { "SBW_Get_Demo_Holder_vanished" },
            WrapperSymbolReconciliation.FindUndefined(referenced, defined));
    }

    /// <summary>
    /// The collision hazard itself, exercised through the emitter's own projection. A nested type and
    /// a top-level type whose name already contains the separator are distinct members that the
    /// unhashed accessor scheme maps onto one symbol; first registration wins, the loser emits no
    /// wrapper body, and its P/Invoke is left pointing at the winner's.
    /// </summary>
    [Fact]
    public void NestedTypeAndUnderscoredSibling_ProjectOntoOneAccessorSymbol()
    {
        var collisions = WrapperSymbolReconciliation.FindAccessorProjectionCollisions(
            new[]
            {
                ("Outer.Inner", "tag", true),
                ("Outer_Inner", "tag", true),
            },
            "Demo");

        var collision = Assert.Single(collisions);
        Assert.Equal(
            PropertyWrapperEmitter.GetAccessorSymbolName("Demo", "Outer.Inner", "tag", isGetter: true),
            collision.Symbol);
        Assert.Equal(new[] { "Outer.Inner.tag get", "Outer_Inner.tag get" }, collision.Members);
    }

    /// <summary>
    /// Positive control for the collision detector: the axes that legitimately share a prefix — the
    /// getter and setter of one property, and two properties on one type — must not be reported, or
    /// the detector would flag every accessor pair and mean nothing.
    /// </summary>
    [Fact]
    public void DistinctAccessors_DoNotCollide()
    {
        Assert.Empty(WrapperSymbolReconciliation.FindAccessorProjectionCollisions(
            new[]
            {
                ("Outer.Inner", "tag", true),
                ("Outer.Inner", "tag", false),
                ("Outer.Inner", "other", true),
                ("Sibling", "tag", true),
            },
            "Demo"));
    }

    #endregion

    #region Corpus reconciliation

    /// <summary>
    /// The fixture members pinned to a wrapper route. Each row is (Swift type name as the emitter
    /// spells it, property name, getter). <c>WrapperCohesionBase</c> is the wave's own cohesion
    /// fixture — a stored value property and a stored class-reference property, both accessor pairs;
    /// the two nested rows are what exercise the dot flattening on real generated output.
    /// </summary>
    public static TheoryData<string, string, bool> PinnedAccessors => new()
    {
        { "WrapperCohesionBase", "lastSeenChildId", true },
        { "WrapperCohesionBase", "lastSeenChildId", false },
        { "WrapperCohesionBase", "stashedSibling", true },
        { "WrapperCohesionBase", "stashedSibling", false },
        { "BugReproBindTarget.ScenePath", "segments", true },
        { "Codec.Alignment", "rawValue", true },
    };

    /// <summary>
    /// A pinned member's wrapper symbol has to exist on both sides: defined by the generated Swift
    /// and called by the generated C#. Defined-but-uncalled is the registry collision's signature
    /// (the member fell back to the direct route); called-but-undefined is a dangling entry point.
    /// </summary>
    [SkippableTheory]
    [Trait("Category", GeneratedBindingsOutputRequirement.TraitCategory)]
    [MemberData(nameof(PinnedAccessors))]
    public void PinnedFixtureAccessor_KeepsItsWrapperSymbol(string typeName, string propertyName, bool isGetter)
    {
        var corpus = LoadCorpus();
        GeneratedBindingsOutputRequirement.SkipUnlessAvailable(corpus is not null,
            $"Generated bindings corpus not found under {OutputDirectory()}");

        var symbol = PropertyWrapperEmitter.GetAccessorSymbolName(HarnessModule, typeName, propertyName, isGetter);
        var accessor = isGetter ? "getter" : "setter";

        Assert.True(corpus!.Definitions.Contains(symbol),
            $"The generated Swift defines no wrapper for {typeName}.{propertyName} ({accessor}); " +
            $"expected `{symbol}`. Either the member stopped being wrapper-eligible — it is back on a " +
            "direct CallConvSwift P/Invoke carrying the reserved self register — or it lost the " +
            "symbol claim to another member and emitted no body of its own.");

        Assert.True(corpus.References.Contains(symbol),
            $"The generated C# calls no wrapper for {typeName}.{propertyName} ({accessor}); " +
            $"expected an EntryPoint of `{symbol}`. The Swift side defines the wrapper and nothing " +
            "calls it, so the managed projection of this member is reaching something else.");
    }

    /// <summary>
    /// The two corpus assertions above read whole-corpus sets, so neither can say WHICH member calls
    /// a symbol: if two pinned members projected to one symbol, the winner would define it, both
    /// bindings would reference it, and "defined once, referenced" would hold while one member called
    /// the other's wrapper. What closes that gap is upstream of the corpus — the pinned members must
    /// project to DISTINCT symbols in the first place, which is the same question the shipped
    /// collision detector answers, asked here of the real pinned set rather than a hand-built one.
    /// </summary>
    [Fact]
    public void PinnedFixtureAccessors_ProjectToDistinctSymbols()
    {
        var collisions = WrapperSymbolReconciliation.FindAccessorProjectionCollisions(
            PinnedAccessors.Select(row => ((string)row[0], (string)row[1], (bool)row[2])),
            HarnessModule);

        Assert.Empty(collisions);
    }

    /// <summary>
    /// Every pinned symbol is defined exactly once. A collision cannot show up as two definitions —
    /// the registry refuses the second claim, so the Swift only ever contains one — which is why the
    /// count is pinned here and the per-member call-side assertion is pinned above: together they say
    /// the one definition that exists belongs to the member that calls it.
    /// </summary>
    [SkippableFact]
    [Trait("Category", GeneratedBindingsOutputRequirement.TraitCategory)]
    public void PinnedFixtureAccessorSymbols_AreDefinedExactlyOnce()
    {
        var corpus = LoadCorpus();
        GeneratedBindingsOutputRequirement.SkipUnlessAvailable(corpus is not null,
            $"Generated bindings corpus not found under {OutputDirectory()}");

        foreach (var row in PinnedAccessors)
        {
            var typeName = (string)row[0];
            var propertyName = (string)row[1];
            var isGetter = (bool)row[2];
            var symbol = PropertyWrapperEmitter.GetAccessorSymbolName(HarnessModule, typeName, propertyName, isGetter);

            Assert.Equal(1, corpus!.DefinitionCounts.TryGetValue(symbol, out var count) ? count : 0);
        }
    }

    /// <summary>
    /// Whole-corpus reconciliation, the cheap direction: no generated P/Invoke may name a wrapper
    /// symbol the generated Swift does not define. The build gates this too; re-deriving it here
    /// means a regression is visible from a unit-test run rather than only from a full regeneration.
    /// </summary>
    [SkippableFact]
    [Trait("Category", GeneratedBindingsOutputRequirement.TraitCategory)]
    public void EveryWrapperEntryPoint_HasASwiftDefinition()
    {
        var corpus = LoadCorpus();
        GeneratedBindingsOutputRequirement.SkipUnlessAvailable(corpus is not null,
            $"Generated bindings corpus not found under {OutputDirectory()}");

        var dangling = WrapperSymbolReconciliation.FindUndefined(corpus!.References, corpus.Definitions);

        Assert.True(dangling.Count == 0,
            $"{dangling.Count} wrapper entry point(s) have no Swift definition: " +
            string.Join(", ", dangling.Take(10)));
    }

    #endregion

    #region Corpus loading

    private sealed class Corpus
    {
        public required HashSet<string> Definitions { get; init; }
        public required HashSet<string> References { get; init; }
        public required Dictionary<string, int> DefinitionCounts { get; init; }
    }

    private static string OutputDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SwiftBindings.sln")))
            dir = dir.Parent;
        return dir == null ? string.Empty : Path.Combine(dir.FullName, "BindingTests", "output");
    }

    /// <summary>
    /// Reads the generated module's C# (across the file-per-type split) and every generated Swift
    /// source that can carry a wrapper definition — the module's own wrapper and bridge files plus
    /// the dependency module's, since a P/Invoke in one module may target a symbol defined by the
    /// other. Returns null when the corpus has not been generated.
    /// </summary>
    private static Corpus? LoadCorpus()
    {
        var outputDir = OutputDirectory();
        if (string.IsNullOrEmpty(outputDir) || !Directory.Exists(outputDir))
            return null;
        if (!SplitModuleSource.Exists(outputDir, HarnessModule))
            return null;

        var swiftFiles = Directory.EnumerateFiles(outputDir, "*.swift", SearchOption.TopDirectoryOnly).ToList();
        var depSwift = Path.Combine(outputDir, "dep-swift");
        if (Directory.Exists(depSwift))
            swiftFiles.AddRange(Directory.EnumerateFiles(depSwift, "*.swift", SearchOption.TopDirectoryOnly));
        if (swiftFiles.Count == 0)
            return null;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in swiftFiles.OrderBy(f => f, StringComparer.Ordinal))
        {
            foreach (var symbol in WrapperSymbolReconciliation.EnumerateDefinitions(File.ReadAllText(file)))
                counts[symbol] = counts.TryGetValue(symbol, out var n) ? n + 1 : 1;
        }

        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(outputDir, "*.cs", SearchOption.TopDirectoryOnly))
            references.UnionWith(WrapperSymbolReconciliation.ParseReferences(File.ReadAllText(file)));

        return new Corpus
        {
            Definitions = new HashSet<string>(counts.Keys, StringComparer.Ordinal),
            References = references,
            DefinitionCounts = counts,
        };
    }

    #endregion
}

/// <summary>
/// Pure reconciliation over wrapper symbol text: what the Swift defines, what the C# calls, and which
/// distinct members the unhashed accessor scheme projects onto one symbol. Kept free of file access
/// so the detectors can be driven with hand-built adversarial input.
/// </summary>
internal static class WrapperSymbolReconciliation
{
    /// <summary>Matches the definition side — both wrapper attribute spellings, both symbol prefixes.</summary>
    private static readonly Regex DefinitionPattern = new(
        @"@_(?:cdecl|silgen_name)\s*\(\s*""((?:SBW_|SBSW_)[A-Za-z0-9_]+)""",
        RegexOptions.Compiled);

    /// <summary>Matches the call side: a P/Invoke entry point naming a wrapper symbol.</summary>
    private static readonly Regex ReferencePattern = new(
        @"EntryPoint\s*=\s*""((?:SBW_|SBSW_)[A-Za-z0-9_]+)""",
        RegexOptions.Compiled);

    public static IEnumerable<string> EnumerateDefinitions(string swiftSource)
    {
        foreach (Match m in DefinitionPattern.Matches(swiftSource))
            yield return m.Groups[1].Value;
    }

    public static HashSet<string> ParseDefinitions(string swiftSource)
        => new(EnumerateDefinitions(swiftSource), StringComparer.Ordinal);

    public static HashSet<string> ParseReferences(string csharpSource)
        => new(ReferencePattern.Matches(csharpSource).Select(m => m.Groups[1].Value), StringComparer.Ordinal);

    /// <summary>Expected symbols with no definition, ordinal-sorted for a stable failure message.</summary>
    public static IReadOnlyList<string> FindUndefined(IEnumerable<string> expected, ISet<string> defined)
        => expected.Where(s => !defined.Contains(s)).Distinct(StringComparer.Ordinal)
                   .OrderBy(s => s, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Groups accessors by the symbol the emitter would project for them and returns the groups with
    /// more than one distinct member — the shapes where first-registration-wins silently demotes a
    /// member to the direct route.
    /// </summary>
    public static IReadOnlyList<(string Symbol, IReadOnlyList<string> Members)> FindAccessorProjectionCollisions(
        IEnumerable<(string TypeName, string PropertyName, bool IsGetter)> accessors,
        string moduleName)
    {
        var bySymbol = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var (typeName, propertyName, isGetter) in accessors)
        {
            var symbol = PropertyWrapperEmitter.GetAccessorSymbolName(moduleName, typeName, propertyName, isGetter);
            if (!bySymbol.TryGetValue(symbol, out var members))
                bySymbol[symbol] = members = new SortedSet<string>(StringComparer.Ordinal);
            members.Add($"{typeName}.{propertyName} {(isGetter ? "get" : "set")}");
        }

        return bySymbol.Where(kv => kv.Value.Count > 1)
                       .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                       .Select(kv => (kv.Key, (IReadOnlyList<string>)kv.Value.ToList()))
                       .ToList();
    }
}
