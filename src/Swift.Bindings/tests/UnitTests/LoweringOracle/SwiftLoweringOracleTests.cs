// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace BindingsGeneration.Tests;

/// <summary>
/// The swiftc lowering oracle over the probe modules: every callee's predicted physical signature
/// against the IR swiftc emits, on the device and both simulator architectures.
/// </summary>
public class SwiftLoweringOracleTests
{
    private readonly ITestOutputHelper _output;

    public SwiftLoweringOracleTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Triples => SwiftOracleTarget.All.Select(t => new object[] { t.Triple });

    // The probe declares a few hundred callees; a run that predicts far fewer has stopped reading them.
    private const int ProbeMatchFloor = 500;
    private const int ProbeLayoutFloor = 20;
    private const int FixtureMatchFloor = 40;

    [SkippableTheory]
    [MemberData(nameof(Triples))]
    public void Probe_EveryPredictionMatchesSwiftc(string triple)
    {
        var report = Report(triple).Probe;
        _output.WriteLine(report.Describe(includeMatches: true));
        Assert.Empty(report.With(SwiftOracleOutcome.Mismatch));
        Assert.Empty(report.With(SwiftOracleOutcome.NotDefined));
        Assert.True(report.Count(SwiftOracleOutcome.Match) >= ProbeMatchFloor, report.Summary);
    }

    [Fact]
    public void IrLayout_ReadsPackedStructs_AndRejectsAFormItDoesNotModel()
    {
        var ir = SwiftIrModule.Parse("%T1M1PV = type <{ %Ts5Int32V, [4 x i8], i64 }>\n%Ts5Int32V = type <{ i32 }>\n%T1M1UV = type { i32, i64 }\n");

        Assert.Equal(16, ir.LayoutOf("%T1M1PV")!.Size);
        Assert.Null(ir.LayoutOf("%T1M1XV"));
        Assert.Throws<FormatException>(() => ir.LayoutOf("%T1M1UV"));
    }

    [SkippableTheory]
    [MemberData(nameof(Triples))]
    public void Probe_EveryFrozenStructLayoutMatchesSwiftc(string triple)
    {
        var report = Report(triple).Probe;
        // A struct storing an @_alignment type has no layout, which the descriptor cannot give.
        var refused = report.LayoutMismatches.Where(l => l.TypeName == "LoweringProbe.CarrierWithAlignedObjCEnum").ToList();
        Assert.Single(refused);
        Assert.Contains("declares an alignment the ABI descriptor does not record", refused[0].Mismatch, StringComparison.Ordinal);
        Assert.Empty(report.LayoutMismatches.Except(refused));
        Assert.True(report.Layouts.Length >= ProbeLayoutFloor, report.Summary);
    }

    /// <summary>
    /// The carriers the type databases do not list (so the facts builder refuses their callees)
    /// still lower, through the resolver, to the pieces swiftc passes them in.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(Triples))]
    public void Probe_CarriersWithoutTypeDatabaseEntriesLowerAsSwiftcPassesThem(string triple)
    {
        var report = Report(triple).Probe;
        Assert.Empty(report.CarrierMismatches);
        var checkedCarriers = report.Carriers.Select(c => FunctionName(c.Symbol)).ToHashSet(StringComparer.Ordinal);
        foreach (var name in new[] { "carrierCharacter", "carrierSubstring", "carrierContiguousArray", "carrierUnicodeScalar", "carrierUnmanaged", "carrierCGVector", "carrierRange" })
            Assert.Contains(name, checkedCarriers);
    }

    /// <summary>
    /// Every refusal is one the predictor owes: a callee that is not a synchronous swiftcc call, a
    /// fact the inputs do not record, or a shape it does not model. A new refusal fails here until it
    /// is either fixed or added with its reason.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(Triples))]
    public void Probe_EveryRefusalIsExpectedWithItsReason(string triple)
    {
        const string AlignmentNotRecorded = "declares an alignment the ABI descriptor does not record";
        var report = Report(triple).Probe;
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["refusalAsync"] = "async:",
            ["refusalAsyncThrows"] = "async:",
            ["asyncMethod"] = "async:",
            ["asyncProperty"] = "async:",
            ["refusalTypedThrows"] = "typed throws",
            ["refusalPack"] = "parameter packs",
            ["objcRequirement"] = "Objective-C message",
            ["objcProperty"] = "Objective-C message",
            // Two address-only results come back as two indirect results, which is not modeled.
            ["genericPairResult"] = "address-only",
            ["unownedExecutor"] = "not in the carrier table",
            ["carrierCharacter"] = "facts are incomplete",
            ["carrierSubstring"] = "facts are incomplete",
            ["carrierContiguousArray"] = "facts are incomplete",
            ["carrierUnicodeScalar"] = "facts are incomplete",
            ["carrierUnmanaged"] = "facts are incomplete",
            ["carrierCGVector"] = "facts are incomplete",
            ["carrierRange"] = "facts are incomplete",
            // A type nested in P0TypeHost and named Type parses as P0TypeHost.Type, like the
            // metatype, so neither is resolved where the spelling names both.
            ["kind"] = "names both a metatype and a nested type",
            ["metatype"] = "names both a metatype and a nested type",
            ["== infix"] = "names both a metatype and a nested type",
            ["carrierWithAlignedObjCEnum"] = AlignmentNotRecorded,
        };
        var unexpected = new List<string>();
        foreach (var entry in report.With(SwiftOracleOutcome.Refused))
        {
            if (entry.Facts.Target.Kind == SwiftCallTargetKind.MetadataAccessor)
                continue;
            // Every member of an @_alignment type, and of a type storing one, whatever its name.
            if (entry.Symbol.Contains("AlignedObjCEnum", StringComparison.Ordinal) && entry.Refusal!.Contains(AlignmentNotRecorded, StringComparison.Ordinal))
                continue;
            var name = FunctionName(entry.Symbol);
            if (!expected.TryGetValue(name, out var reason) || !entry.Refusal!.Contains(reason, StringComparison.Ordinal))
                unexpected.Add(entry.ToString());
        }
        Assert.True(unexpected.Count == 0, "unexpected refusals:\n" + string.Join("\n", unexpected));
        var refused = report.With(SwiftOracleOutcome.Refused).Select(e => FunctionName(e.Symbol)).ToHashSet(StringComparer.Ordinal);
        var neverRefused = expected.Keys.Where(name => !refused.Contains(name)).ToList();
        Assert.True(neverRefused.Count == 0, "expected refusals that did not happen: " + string.Join(", ", neverRefused));
    }

    /// <summary>
    /// Each refused effect is one swiftc really lowers differently: async is swifttailcc with a
    /// swiftasync context, typed throws adds an indirect error result after the error register, and
    /// a pack is passed as a pack address with a count.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(Triples))]
    public void Probe_RefusedEffectsAreLoweredDifferentlyBySwiftc(string triple)
    {
        var report = Report(triple).Probe;
        SwiftIrFunction Actual(string name) =>
            report.With(SwiftOracleOutcome.Refused).Single(e => FunctionName(e.Symbol) == name).Actual
                ?? throw new InvalidOperationException($"swiftc defined no {name}");

        foreach (var name in new[] { "refusalAsync", "refusalAsyncThrows", "asyncMethod", "asyncProperty" })
        {
            var actual = Actual(name);
            Assert.Equal("swifttailcc", actual.CallingConvention);
            Assert.Contains("ptr swiftasync", actual.Signature, StringComparison.Ordinal);
        }
        Assert.EndsWith("ptr noalias swifterror, ptr)", Actual("refusalTypedThrows").Signature, StringComparison.Ordinal);
        Assert.Contains("ptr noalias, i64, ptr", Actual("refusalPack").Signature, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [MemberData(nameof(Triples))]
    public void SelfAndParameterPassingFixture_EveryPredictionMatchesSwiftc(string triple)
    {
        var report = Report(triple).Fixture;
        _output.WriteLine(report.Describe(includeMatches: true));
        Assert.Empty(report.With(SwiftOracleOutcome.Mismatch));
        Assert.Empty(report.LayoutMismatches);
        var refused = report.With(SwiftOracleOutcome.Refused).Where(e => e.Facts.Target.Kind != SwiftCallTargetKind.MetadataAccessor).ToList();
        Assert.True(refused.Count == 0, string.Join("\n", refused));
        Assert.True(report.Count(SwiftOracleOutcome.Match) >= FixtureMatchFloor, report.Summary);
    }

    /// <summary>
    /// The self convention the classifier decides is the one swiftc's signature shows: an address
    /// self is a <c>noalias swiftself</c>, a reference, metatype or inout self a plain
    /// <c>swiftself</c>, and a lowered self or none no self register (a throwing callee with no self
    /// keeps an unused one).
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(Triples))]
    public void SelfConventionsMatchSwiftc(string triple)
    {
        var (probe, fixture) = Report(triple);
        var seen = new HashSet<SwiftSelfConvention>();
        foreach (var entry in probe.With(SwiftOracleOutcome.Match).Concat(fixture.With(SwiftOracleOutcome.Match)))
        {
            var convention = entry.Self!.Convention!.Value;
            seen.Add(convention);
            var signature = entry.Actual!.Signature;
            var placement = signature.Contains("ptr noalias swiftself", StringComparison.Ordinal) ? "address"
                : signature.Contains("ptr swiftself", StringComparison.Ordinal) ? "pointer"
                : "none";
            var expected = convention switch
            {
                SwiftSelfConvention.IndirectBorrowed or SwiftSelfConvention.IndirectOwned => "address",
                SwiftSelfConvention.Reference or SwiftSelfConvention.Metatype or SwiftSelfConvention.Inout => "pointer",
                _ => entry.Facts.Effects.Throws ? "pointer" : "none",
            };
            Assert.True(expected == placement, $"{entry.Symbol}: {convention} but swiftc places self as {placement}: {signature}");
        }
        Assert.Equal(Enum.GetValues<SwiftSelfConvention>().ToHashSet(), seen);
    }

    /// <summary>The environment variable naming the corpus manifest the <c>swift-lowering-oracle</c> target writes.</summary>
    public const string CorpusManifestVariable = "SWIFT_LOWERING_ORACLE_CORPUS";

    /// <summary>
    /// The BindingTests corpus (its dependency module, then the main module), compiled by swiftc and
    /// compared callee by callee. Runs only from the <c>swift-lowering-oracle</c> build target, which
    /// names the sources and where the reports go; refusals are reported, mismatches fail.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(Triples))]
    public void Corpus_EveryPredictionMatchesSwiftc(string triple)
    {
        var manifestPath = Environment.GetEnvironmentVariable(CorpusManifestVariable);
        Skip.If(string.IsNullOrEmpty(manifestPath), $"{CorpusManifestVariable} is not set; the swift-lowering-oracle target runs this");
        Skip.IfNot(SwiftLoweringOracle.IsAvailable, "xcrun and an iOS simulator SDK are required");
        var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath!)).RootElement;
        string[] Strings(string property) => manifest.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToArray();
        var reportDirectory = manifest.GetProperty("reports").GetString()!;
        var target = SwiftOracleTarget.ByTriple(triple);
        var scratch = Path.Combine(manifest.GetProperty("scratch").GetString()!, triple);

        var dependency = SwiftLoweringOracle.Compile(manifest.GetProperty("dependencyModule").GetString()!, Strings("dependency"), target, Path.Combine(scratch, "dependency"));
        var main = SwiftLoweringOracle.Compile(manifest.GetProperty("mainModule").GetString()!, Strings("main"), target, Path.Combine(scratch, "main"),
            new[] { "-I", Path.Combine(scratch, "dependency") });
        var reports = new[]
        {
            SwiftLoweringOracle.Compare(dependency, triple),
            SwiftLoweringOracle.Compare(main, triple, new[] { dependency }),
        };

        Directory.CreateDirectory(reportDirectory);
        var text = new System.Text.StringBuilder($"toolchain: {SwiftLoweringOracle.ToolchainVersion}\ntarget: {triple}\n");
        foreach (var report in reports)
            text.AppendLine().Append(report.Describe());
        File.WriteAllText(Path.Combine(reportDirectory, triple + ".txt"), text.ToString());
        _output.WriteLine(text.ToString());

        foreach (var report in reports)
        {
            Assert.True(!report.With(SwiftOracleOutcome.Mismatch).Any() && !report.LayoutMismatches.Any() && !report.CarrierMismatches.Any(),
                report.Describe());
            Assert.True(report.Count(SwiftOracleOutcome.Match) > 0, report.Summary);
        }
    }

    private static (SwiftOracleReport Probe, SwiftOracleReport Fixture) Report(string triple)
    {
        Skip.IfNot(SwiftLoweringOracle.IsAvailable, "xcrun and an iOS simulator SDK are required");
        return ProbeReports.Get(triple);
    }

    /// <summary>The function name a symbol's demangling ends in, e.g. <c>carrierCharacter</c>.</summary>
    private static string FunctionName(string symbol)
    {
        var demangled = SwiftLoweringOracle.Demangle(symbol);
        var open = demangled.IndexOfAny(new[] { '(', '<' });
        var head = open < 0 ? demangled : demangled[..open];
        head = head.Split(" : ")[0];
        foreach (var accessor in new[] { ".getter", ".setter", ".modify", ".read" })
            if (head.EndsWith(accessor, StringComparison.Ordinal))
                head = head[..^accessor.Length];
        return head[(head.LastIndexOf('.') + 1)..].Trim();
    }

    /// <summary>Compiles each probe module once per target and keeps the reports for the run.</summary>
    internal static class ProbeReports
    {
        private static readonly ConcurrentDictionary<string, Lazy<(SwiftOracleReport Probe, SwiftOracleReport Fixture)>> _reports = new();

        public static (SwiftOracleReport Probe, SwiftOracleReport Fixture) Get(string triple) =>
            _reports.GetOrAdd(triple, t => new Lazy<(SwiftOracleReport, SwiftOracleReport)>(() => Build(t))).Value;

        private static (SwiftOracleReport, SwiftOracleReport) Build(string triple)
        {
            var target = SwiftOracleTarget.ByTriple(triple);
            var root = RepoRoot;
            var scratch = Path.Combine(Path.GetTempPath(), "swift-lowering-oracle", Guid.NewGuid().ToString("N"), triple);
            try
            {
                var probeSources = Directory.GetFiles(
                    Path.Combine(root, "src", "Swift.Bindings", "tests", "UnitTests", "LoweringOracle", "Probe"), "*.swift")
                    .OrderBy(f => f, StringComparer.Ordinal);
                var probe = SwiftLoweringOracle.Compile("LoweringProbe", probeSources, target, Path.Combine(scratch, "probe"));
                var fixture = SwiftLoweringOracle.Compile(
                    "SelfAndParameterPassing",
                    new[] { Path.Combine(root, "BindingTests", "Sources", "SwiftBindingsTestLib", "Parameters", "SelfAndParameterPassing.swift") },
                    target, Path.Combine(scratch, "fixture"));
                Console.WriteLine($"swift lowering oracle {triple}: {SwiftLoweringOracle.ToolchainVersion}");
                return (SwiftLoweringOracle.Compare(probe, triple), SwiftLoweringOracle.Compare(fixture, triple));
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(scratch)!, recursive: true); } catch (IOException) { }
            }
        }
    }

    internal static string RepoRoot { get; } = LocateRepoRoot();

    private static string LocateRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BindingTests", "RuntimeTestsApp", "RuntimeTestsApp.csproj")))
                return directory.FullName;
        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
