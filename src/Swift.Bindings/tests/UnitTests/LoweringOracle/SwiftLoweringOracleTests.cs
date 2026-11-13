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
    /// A dynamic Self in a result, bare or optional, in a class member (instance, class, getter,
    /// subscript, throwing, generic class) or a protocol member (requirement or extension, bound to
    /// classes or not): every one is predicted, and each prediction is what swiftc gives.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(Triples))]
    public void Probe_DynamicSelfResultsMatchSwiftc(string triple)
    {
        var report = Report(triple).Probe;
        static bool SelfResult(SwiftOracleEntry e) =>
            e.Facts.Target.DeclaringType is { } type
            && (type.EndsWith("SelfReturning", StringComparison.Ordinal) || type.Contains(".SelfOptional", StringComparison.Ordinal))
            && e.Facts.Result.Type.Spelling is var result
            && (result.Contains("Self", StringComparison.Ordinal) || result.Contains("τ_0_0", StringComparison.Ordinal));
        var entries = report.Entries.Where(SelfResult).ToList();
        var notMatched = entries.Where(e => e.Outcome != SwiftOracleOutcome.Match).ToList();
        Assert.True(notMatched.Count == 0, string.Join("\n", notMatched));
        // Two bare-Self class members, eight class Self? callees and five protocol ones.
        Assert.True(entries.Count >= 15, $"{entries.Count} dynamic-Self callees: " + string.Join("\n", entries));
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

        // A one-module corpus (the packaged-consumer fixture) names no dependency.
        var dependency = manifest.TryGetProperty("dependencyModule", out var dependencyModule)
            ? SwiftLoweringOracle.Compile(dependencyModule.GetString()!, Strings("dependency"), target, Path.Combine(scratch, "dependency"))
            : null;
        var main = SwiftLoweringOracle.Compile(manifest.GetProperty("mainModule").GetString()!, Strings("main"), target, Path.Combine(scratch, "main"),
            dependency is null ? null : new[] { "-I", Path.Combine(scratch, "dependency") });
        var reports = dependency is null
            ? new[] { SwiftLoweringOracle.Compare(main, triple) }
            : new[]
            {
                SwiftLoweringOracle.Compare(dependency, triple),
                SwiftLoweringOracle.Compare(main, triple, new[] { dependency }),
            };

        var text = new System.Text.StringBuilder();
        foreach (var report in reports)
            text.AppendLine().Append(report.Describe());
        AppendReport(reportDirectory, triple, text.ToString());
        _output.WriteLine(text.ToString());

        foreach (var report in reports)
        {
            Assert.True(!report.With(SwiftOracleOutcome.Mismatch).Any() && !report.LayoutMismatches.Any() && !report.CarrierMismatches.Any(),
                report.Describe());
            Assert.True(report.Count(SwiftOracleOutcome.Match) > 0, report.Summary);
        }

        // The lowering receipt of each artifact the manifest names, read from the modules' definitions,
        // the generated wrappers (which define the wrapper targets and declare the library symbols they
        // call) and the probe that calls the hand-written tables' targets.
        if (manifest.TryGetProperty("evidence", out var evidenceProperty))
        {
            var wrappers = manifest.GetProperty("wrappers");
            // The wrappers import the modules' public interfaces, as they do a shipped framework's.
            var includes = new List<string> { "-I", SwiftLoweringOracle.InterfaceDirectory(Path.Combine(scratch, "main")) };
            if (dependency is not null)
                includes.InsertRange(0, new[] { "-I", SwiftLoweringOracle.InterfaceDirectory(Path.Combine(scratch, "dependency")) });
            var modules = new List<SwiftIrModule> { main.Ir };
            var sources = new List<SwiftEvidenceSource>
            {
                Source(SwiftEvidenceSource.DefinitionRole, main.Name, Strings("main"), SwiftLoweringOracle.DefinitionFlags),
            };
            if (dependency is not null)
            {
                modules.Insert(0, dependency.Ir);
                sources.Add(Source(SwiftEvidenceSource.DefinitionRole, dependency.Name, Strings("dependency"), SwiftLoweringOracle.DefinitionFlags));
            }
            foreach (var wrapper in wrappers.EnumerateObject().OrderBy(w => w.Name, StringComparer.Ordinal))
            {
                modules.Add(SwiftLoweringOracle.CompileIr(wrapper.Name, new[] { wrapper.Value.GetString()! }, target,
                    Path.Combine(scratch, "wrapper-" + wrapper.Name), includes));
                sources.Add(Source(SwiftEvidenceSource.WrapperRole, wrapper.Name, new[] { wrapper.Value.GetString()! }, SwiftLoweringOracle.IrFlags));
            }
            var probe = manifest.GetProperty("probe").GetString()!;
            modules.Add(SwiftLoweringOracle.CompileIr(ProbeModule, new[] { probe }, target, Path.Combine(scratch, "probe")));
            sources.Add(Source(SwiftEvidenceSource.ProbeRole, ProbeModule, new[] { probe }, SwiftLoweringOracle.IrFlags));

            var findings = RecordArtifacts(evidenceProperty.GetString()!, triple, target, Strings("artifacts"), modules, sources, reportDirectory);
            Assert.True(findings.Count == 0, string.Join("\n", findings));
        }
    }

    /// <summary>Every slice a hand-written table may claim.</summary>
    public static IEnumerable<object[]> EverySlice => SwiftOracleTarget.Every.Select(t => new object[] { t.Triple });

    /// <summary>
    /// The lowering receipts of the hand-written tables (the runtime's and the Apple supplement's), on
    /// each slice its table claims: their targets are standard-library and Foundation functions, which
    /// the probe calls the way a client does. Runs only from the <c>swift-lowering-oracle</c> build
    /// target; a target the probe does not show fails.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(EverySlice))]
    public void HandWrittenTables_RecordEvidence(string triple)
    {
        var manifestPath = Environment.GetEnvironmentVariable(CorpusManifestVariable);
        Skip.If(string.IsNullOrEmpty(manifestPath), $"{CorpusManifestVariable} is not set; the swift-lowering-oracle target runs this");
        Skip.IfNot(SwiftLoweringOracle.IsAvailable, "xcrun and an iOS simulator SDK are required");
        var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath!)).RootElement;
        Skip.IfNot(manifest.TryGetProperty("handWritten", out var handWritten), "the manifest names no hand-written table");
        var claimed = handWritten.EnumerateArray()
            .Select(t => (Table: SwiftHandWrittenTransitionTable.Load(t.GetProperty("table").GetString()!), Plans: t.GetProperty("plans").GetString()!))
            .Where(t => t.Table.Slices.Contains(triple))
            .Select(t => t.Plans)
            .ToArray();
        Skip.If(claimed.Length == 0, $"no hand-written table claims {triple}");
        var target = SwiftOracleTarget.ByTriple(triple);
        var scratch = Path.Combine(manifest.GetProperty("scratch").GetString()!, "hand-written", triple);
        var probe = manifest.GetProperty("probe").GetString()!;
        var modules = new List<SwiftIrModule> { SwiftLoweringOracle.CompileIr(ProbeModule, new[] { probe }, target, scratch) };
        var sources = new List<SwiftEvidenceSource> { Source(SwiftEvidenceSource.ProbeRole, ProbeModule, new[] { probe }, SwiftLoweringOracle.IrFlags) };
        var findings = RecordArtifacts(manifest.GetProperty("evidence").GetString()!, triple, target, claimed, modules, sources,
            manifest.GetProperty("reports").GetString()!);
        Assert.True(findings.Count == 0, string.Join("\n", findings));
    }

    private const string ProbeModule = "HandWrittenTargets";

    private static SwiftEvidenceSource Source(string role, string name, IEnumerable<string> files, System.Collections.Immutable.ImmutableArray<string> flags) =>
        new(role, name, SwiftEvidenceSource.DigestOf(files), flags);

    /// <summary>
    /// Records, for each plan sidecar in <paramref name="planFiles"/>, its artifact's receipt on
    /// <paramref name="triple"/> under this toolchain, read from <paramref name="modules"/>; appends one
    /// line per artifact to the triple's report, and returns every target not shown or shown in conflict.
    /// </summary>
    private List<string> RecordArtifacts(
        string evidencePath, string triple, SwiftOracleTarget target, IEnumerable<string> planFiles,
        IReadOnlyList<SwiftIrModule> modules, IReadOnlyList<SwiftEvidenceSource> sources, string reportDirectory)
    {
        var sdk = SwiftToolchainProbe.SdkIdentity(target.Sdk) ?? throw new InvalidOperationException($"the {target.Sdk} SDK could not be identified");
        var findings = new List<string>();
        var report = new System.Text.StringBuilder();
        foreach (var planFile in planFiles)
        {
            var artifact = SwiftCallPlanSet.Read(planFile).Module;
            var targets = SwiftLoweringEvidenceCollector.Targets(new[] { planFile });
            var symbols = SwiftLoweringEvidenceCollector.Signatures(targets, modules);
            SwiftLoweringEvidenceCollector.Record(evidencePath, artifact, SwiftLoweringOracle.ToolchainVersion, triple,
                SwiftLoweringEvidenceCollector.Slice(sdk, sources, targets, symbols));
            var unseen = targets.Where(t => !symbols.ContainsKey(t)).ToList();
            var conflicts = symbols.Where(s => s.Value.StartsWith("conflict: ", StringComparison.Ordinal)).Select(s => $"{s.Key}: {s.Value}").ToList();
            var line = $"evidence: {artifact}: {symbols.Count} of {targets.Count} plan target(s) shown by swiftc, {conflicts.Count} conflict(s)";
            report.Append($"\n{line}\n").Append(string.Concat(unseen.Select(u => $"  unseen: {u}\n"))).Append(string.Concat(conflicts.Select(c => $"  {c}\n")));
            _output.WriteLine(line);
            findings.AddRange(unseen.Select(u => $"{artifact} on {triple}: unseen: {u}"));
            findings.AddRange(conflicts.Select(c => $"{artifact} on {triple}: {c}"));
        }
        AppendReport(reportDirectory, triple, report.ToString());
        return findings;
    }

    /// <summary>
    /// Appends <paramref name="text"/> to the triple's report, which the corpus and the hand-written
    /// tables both write to (the build target clears the reports before a run).
    /// </summary>
    private static void AppendReport(string reportDirectory, string triple, string text)
    {
        Directory.CreateDirectory(reportDirectory);
        var reportPath = Path.Combine(reportDirectory, triple + ".txt");
        if (!File.Exists(reportPath))
            text = $"toolchain: {SwiftLoweringOracle.ToolchainVersion}\ntarget: {triple}\n" + text;
        File.AppendAllText(reportPath, text);
    }

    /// <summary>
    /// The hand-written tables' targets probe calls every target of the committed plan products, so
    /// the oracle has evidence for each: a target added to a table without a call fails here.
    /// </summary>
    [SkippableFact]
    public void HandWrittenTargetsProbe_DeclaresEveryProductTarget()
    {
        Skip.IfNot(SwiftLoweringOracle.IsAvailable, "xcrun and an iOS simulator SDK are required");
        var products = Directory.GetFiles(Path.Combine(RepoRoot, "build", "baselines", "swift-call-plans"), "*.swift-call-plans.json");
        var targets = SwiftLoweringEvidenceCollector.Targets(products);
        var scratch = Path.Combine(Path.GetTempPath(), "swift-lowering-oracle", Guid.NewGuid().ToString("N"));
        try
        {
            var probe = SwiftLoweringOracle.CompileIr("HandWrittenTargets",
                new[] { Path.Combine(RepoRoot, "src", "Swift.Bindings", "tests", "UnitTests", "LoweringOracle", "Evidence", "HandWrittenTargets.swift") },
                SwiftOracleTarget.SimulatorArm64, scratch);
            var shown = SwiftLoweringEvidenceCollector.Signatures(targets, new[] { probe });
            Assert.NotEmpty(targets);
            Assert.Equal(targets, shown.Keys);
            Assert.All(shown.Values, v => Assert.StartsWith("swiftcc ", v));
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
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
