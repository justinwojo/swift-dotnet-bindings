// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The lowering receipt a wrapper compile collects (<c>--collect-swift-lowering-evidence</c>): read from
/// the IR of the same post-processed wrapper the link compiled, with the same frontend flags; written
/// whole, only for a wrapper that shipped and whose every slice was observed; and stale as soon as any
/// of its inputs (compiler, SDK, flags, wrapper, imported interfaces, contract) changes.
/// </summary>
public sealed class SwiftWrapperLoweringEvidenceTests : IDisposable
{
    private const string Artifact = "Swift.Runtime";
    private const string Compiler = "Apple Swift version 6.2.4 (swiftlang-6.2.4.1.4 clang-1700.6.4.2)";

    // Two of the runtime sidecar's forward targets: one the canned IR declares, one it never mentions.
    private const string Declared = "$sSayxSicig";
    private const string Absent = "$sSDyq_Sgxcig";

    private static readonly string DeclaredIr = $"declare swiftcc void @\"{Declared}\"(ptr noalias sret(%swift.opaque) captures(none), i64, ptr, ptr) #0";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"swle_{Guid.NewGuid():N}");

    public SwiftWrapperLoweringEvidenceTests()
    {
        Directory.CreateDirectory(_dir);
        File.Copy(
            Path.Combine(SwiftCallPlanTests.RepoRoot, "build", "baselines", "swift-call-plans", "Swift.Runtime.swift-call-plans.json"),
            Path.Combine(_dir, "Swift.Runtime.swift-call-plans.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static string? Sdk(string sdk) => $"{sdk} 26.2 (23C53)";

    private SwiftWrapperLoweringEvidence Collection(string platform = "ios") =>
        SwiftWrapperLoweringEvidence.ForOutput(_dir, Artifact, platform, Compiler, NullLogger.Instance, Sdk)
        ?? throw new InvalidOperationException("no collection");

    private static IReadOnlyList<SwiftIrModule> Ir(params string[] lines) =>
        SwiftIrModule.ParseModules(new StringReader(string.Join("\n", lines)));

    private static readonly IReadOnlyList<SwiftEvidenceSource> SomeSources =
        [new(SwiftEvidenceSource.WrapperRole, "Swift.RuntimeSwiftBindings", "sha256:aa", SwiftWrapperLoweringEvidence.WrapperFlags("arm64-apple-ios16.0-simulator"))];

    // ---- the IR reader, now shipped ----

    [Fact]
    public void ParseModules_KeepsEachModuleOfAMultiFileCompile_AndADisagreementIsAConflict()
    {
        var modules = Ir(
            "; ModuleID = 'A.swift'",
            "declare swiftcc i64 @f(i64)",
            "; ModuleID = 'B.swift'",
            "declare swiftcc i32 @f(i32)",
            "define swiftcc void @g() {");

        Assert.Equal(2, modules.Count);
        Assert.Equal("i64 (i64)", modules[0].Declarations["f"].Signature);
        Assert.True(modules[1].Functions.ContainsKey("g"));
        var signatures = SwiftLoweringEvidenceCollector.Signatures(["f", "g"], modules);
        Assert.StartsWith("conflict: ", signatures["f"]);
        Assert.Equal("swiftcc void ()", signatures["g"]);
    }

    [Fact]
    public void ParseModules_OneModule_MatchesParse()
    {
        const string ir = "; ModuleID = 'A.swift'\ndefine swiftcc i64 @f(i64 %0) {\ndeclare ccc void @h()";
        var one = Assert.Single(SwiftIrModule.ParseModules(new StringReader(ir)));
        var parsed = SwiftIrModule.Parse(ir);
        Assert.Equal(parsed.Functions.Keys, one.Functions.Keys);
        Assert.Equal(parsed.Declarations.Keys, one.Declarations.Keys);
    }

    // ---- compiled triple -> evidence slice ----

    [Theory]
    [InlineData("ios", "arm64-apple-ios16.0", "arm64-apple-ios15.0")]
    [InlineData("ios", "arm64-apple-ios16.0-simulator", "arm64-apple-ios15.0-simulator")]
    [InlineData("ios", "x86_64-apple-ios15.0-simulator", "x86_64-apple-ios15.0-simulator")]
    [InlineData("tvos", "arm64-apple-tvos17.0", "arm64-apple-tvos15.0")]
    [InlineData("tvos", "x86_64-apple-tvos15.0-simulator", "x86_64-apple-tvos15.0-simulator")]
    [InlineData("maccatalyst", "arm64-apple-ios15.0-macabi", "arm64-apple-ios15.0-macabi")]
    [InlineData("macos", "arm64-apple-macos14.0", "arm64-apple-macosx12.0")]
    [InlineData("macos", "x86_64-apple-macosx12.0", "x86_64-apple-macosx12.0")]
    public void EvidenceSliceOf_KeysACompileByArchitectureOsAndEnvironment(string platform, string compiled, string slice) =>
        Assert.Equal(slice, SwiftArtifactScope.EvidenceSliceOf(platform, compiled));

    [Theory]
    [InlineData("ios", "arm64-apple-tvos15.0")]          // another OS
    [InlineData("ios", "x86_64-apple-ios15.0")]          // no Intel device
    [InlineData("tvos", "arm64-apple-tvos15.0-macabi")]  // another environment
    [InlineData("macos", "arm64-apple-ios15.0-macabi")]
    [InlineData("ios", "arm64-unknown-linux")]
    public void EvidenceSliceOf_IsNull_ForASliceThePlatformDoesNotClaim(string platform, string compiled) =>
        Assert.Null(SwiftArtifactScope.EvidenceSliceOf(platform, compiled));

    // ---- what a pass observes ----

    [Fact]
    public void Observe_RecordsTheShownTarget_AndAReasonForEveryOther()
    {
        var collection = Collection();
        var pass = collection.BeginPass(null);

        var slice = pass.Observe("arm64-apple-ios16.0-simulator", Ir(DeclaredIr), SomeSources);

        Assert.NotNull(slice);
        Assert.Equal("swiftcc void (ptr noalias sret, i64, ptr, ptr)", slice!.Symbols[Declared]);
        Assert.Equal(collection.Targets.Count - 1, slice.Unobserved!.Count);
        Assert.Equal(SwiftWrapperLoweringEvidence.NotInWrapperReason, slice.Unobserved[Absent]);
        Assert.Equal("iphonesimulator 26.2 (23C53)", slice.Sdk);
        Assert.Equal("arm64-apple-ios15.0-simulator", Assert.Single(pass.Slices).Triple);
    }

    [Fact]
    public void Observe_ATargetThePostProcessorStripped_IsUnobservedForThatReason()
    {
        var pass = Collection().BeginPass(null);

        var slice = pass.Observe("arm64-apple-ios16.0", Ir(DeclaredIr), SomeSources, new HashSet<string> { Absent });

        Assert.Equal(SwiftWrapperLoweringEvidence.StrippedReason, slice!.Unobserved![Absent]);
    }

    [Fact]
    public void Observe_ASliceThePlatformDoesNotClaim_IsAFailure()
    {
        var collection = Collection("tvos");
        var pass = collection.BeginPass(null);

        Assert.Null(pass.Observe("arm64-apple-ios16.0", Ir(DeclaredIr), SomeSources));

        Assert.Contains("claims no evidence slice", Assert.Single(pass.Failures));
        Assert.Null(collection.Receipt([], out var why));
        Assert.Contains("arm64-apple-ios16.0", why);
    }

    [Fact]
    public void Observe_AnUnidentifiedSdk_IsAFailure()
    {
        var collection = SwiftWrapperLoweringEvidence.ForOutput(_dir, Artifact, "ios", Compiler, NullLogger.Instance, _ => null)!;
        var pass = collection.BeginPass(null);

        Assert.Null(pass.Observe("arm64-apple-ios16.0", Ir(DeclaredIr), SomeSources));

        Assert.Contains("could not be identified", Assert.Single(pass.Failures));
    }

    // ---- the receipt is the shipped wrapper's ----

    [Fact]
    public void Receipt_CoversTheFirstPassAndEveryMergedExtraPass()
    {
        var collection = Collection();
        var primary = collection.BeginPass("arm64");
        primary.Observe("arm64-apple-ios16.0", Ir(DeclaredIr), SomeSources);
        primary.Observe("arm64-apple-ios16.0-simulator", Ir(DeclaredIr), SomeSources);
        collection.BeginPass("x86_64").Observe("x86_64-apple-ios16.0-simulator", Ir(DeclaredIr), SomeSources);

        var receipt = Assert.Single(collection.Receipt([], out var why)!.Receipts);

        Assert.Null(why);
        Assert.Equal(Artifact, receipt.Artifact);
        Assert.Equal(Compiler, receipt.Compiler);
        Assert.Equal(SwiftLoweringContract.Current, receipt.Contract);
        Assert.Equal(SwiftArtifactScope.PlatformSlices("ios").Order(StringComparer.Ordinal), receipt.Slices.Keys);
    }

    [Fact]
    public void Receipt_LeavesOutAPassThatWasNotMerged_EvenWhenItFailed()
    {
        var collection = Collection();
        collection.BeginPass("arm64").Observe("arm64-apple-ios16.0-simulator", Ir(DeclaredIr), SomeSources);
        collection.BeginPass("x86_64").Fail("x86_64-apple-ios16.0-simulator", "swiftc -emit-ir exited 1");

        var receipt = Assert.Single(collection.Receipt(["x86_64"], out _)!.Receipts);

        Assert.Equal(new[] { "arm64-apple-ios15.0-simulator" }, receipt.Slices.Keys);
    }

    [Fact]
    public void Receipt_IsNotWritten_WhenAnyShippedSliceWasNotObserved()
    {
        var collection = Collection();
        var primary = collection.BeginPass(null);
        primary.Observe("arm64-apple-ios16.0", Ir(DeclaredIr), SomeSources);
        primary.Fail("arm64-apple-ios16.0-simulator", SwiftWrapperCompiler.NoWrapperSourceReason);

        Assert.Null(collection.Receipt([], out var why));
        Assert.Contains(SwiftWrapperCompiler.NoWrapperSourceReason, why);
    }

    [Fact]
    public void Receipt_ASliceTwoPassesBuilt_KeepsTheFirstPasssEvidence()
    {
        var collection = Collection();
        collection.BeginPass("arm64").Observe("arm64-apple-ios16.0", Ir(DeclaredIr), SomeSources);
        collection.BeginPass("x86_64").Observe("arm64-apple-ios16.0", Ir("declare swiftcc i64 @\"" + Declared + "\"(i64)"), SomeSources);

        var receipt = Assert.Single(collection.Receipt([], out _)!.Receipts);

        Assert.Equal("swiftcc void (ptr noalias sret, i64, ptr, ptr)", receipt.Slices["arm64-apple-ios15.0"].Symbols[Declared]);
    }

    [Fact]
    public void Receipt_IsNull_WhenNoPassRan()
    {
        Assert.Null(Collection().Receipt([], out var why));
        Assert.Contains("no wrapper compile pass ran", why);
    }

    // ---- collection lifecycle ----

    [Fact]
    public void Begin_RemovesThePreviousReceipt_BeforeAnythingIsCompiled()
    {
        var path = Path.Combine(_dir, "receipt.json");
        File.WriteAllText(path, "an earlier wrapper's receipt");

        var collection = SwiftWrapperLoweringEvidence.Begin(path, _dir, Artifact, "ios", NullLogger.Instance, () => Compiler, Sdk);

        Assert.NotNull(collection);
        Assert.False(File.Exists(path));
        Assert.Equal(Compiler, collection!.Compiler);
    }

    [Theory]
    [InlineData("Apple Swift version 6.3.1 (swiftlang-6.3.1.1.2 clang-1800.0.1.1)")]
    [InlineData("Swift version 6.2.4 (swift-6.2.4-RELEASE)")]
    [InlineData(null)]
    public void Begin_CollectsNothing_UnderACompilerWhoseReceiptCouldAuthorizeNothing(string? compiler)
    {
        var path = Path.Combine(_dir, "receipt.json");
        File.WriteAllText(path, "an earlier wrapper's receipt");

        Assert.Null(SwiftWrapperLoweringEvidence.Begin(path, _dir, Artifact, "ios", NullLogger.Instance, () => compiler, Sdk));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Begin_CollectsNothing_ForAModuleWithNoPlanSidecar()
    {
        Assert.Null(SwiftWrapperLoweringEvidence.Begin(
            Path.Combine(_dir, "receipt.json"), _dir, "SomeOtherModule", "ios", NullLogger.Instance, () => Compiler, Sdk));
        Assert.Null(SwiftWrapperLoweringEvidence.ForOutput(_dir, Artifact, "watchos", Compiler, NullLogger.Instance, Sdk));
    }

    [Fact]
    public void Commit_WritesTheWholeReceipt_AndLeavesNoTemporaryFile()
    {
        var path = Path.Combine(_dir, "obj", "receipt.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var collection = Collection();
        collection.BeginPass(null).Observe("arm64-apple-ios16.0-simulator", Ir(DeclaredIr), SomeSources);

        Assert.True(collection.Commit(path, [], NullLogger.Instance));

        var written = SwiftLoweringEvidence.Load(path);
        Assert.Equal(collection.Receipt([], out _)!.ToJson(), written.ToJson());
        Assert.Equal(new[] { "receipt.json" }, Directory.GetFiles(Path.GetDirectoryName(path)!).Select(f => Path.GetFileName(f)));
    }

    [Fact]
    public void Commit_WritesNothing_WhenTheReceiptCannotBeBuilt()
    {
        var path = Path.Combine(_dir, "receipt.json");
        var collection = Collection();
        collection.BeginPass(null).Fail("arm64-apple-ios16.0-simulator", "swiftc -emit-ir exited 1");

        Assert.False(collection.Commit(path, [], NullLogger.Instance));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Commit_AFailedWrite_LeavesNoReceiptAndNoPartialFile()
    {
        var collection = Collection();
        collection.BeginPass(null).Observe("arm64-apple-ios16.0-simulator", Ir(DeclaredIr), SomeSources);
        // The receipt's path is a directory, so the rename onto it fails after the temporary is written.
        var path = Path.Combine(_dir, "receipt.json");
        Directory.CreateDirectory(path);

        Assert.False(collection.Commit(path, [], NullLogger.Instance));
        Assert.Empty(Directory.GetFiles(_dir, "receipt.json*"));
    }

    // ---- invalidation: every input the receipt records ----

    public static IEnumerable<object[]> Inputs => new[]
    {
        "compiler", "sdk", "flags", "wrapper", "peer", "dependency", "contract",
    }.Select(i => new object[] { i });

    /// <summary>
    /// A receipt collected from real files, judged by an expectation built the way a consumer build
    /// would build it from its own inputs: it authorizes when nothing changed, and any one changed
    /// input makes it stale.
    /// </summary>
    [Theory]
    [MemberData(nameof(Inputs))]
    public void AChangedInput_MakesTheReceiptStale(string input)
    {
        const string compiled = "arm64-apple-ios16.0-simulator";
        const string slice = "arm64-apple-ios15.0-simulator";
        var wrapper = Path.Combine(_dir, "Swift.Runtime.Wrapper.swift");
        File.WriteAllText(wrapper, "public func f() {}\n");
        var boundSearch = Path.Combine(_dir, "slice");
        var dependencySearch = Path.Combine(_dir, "deps");
        WriteFramework(boundSearch, Artifact, "public func f()");
        var peerInterface = WriteFramework(boundSearch, "Peer", "public struct P {}");
        var dependencyInterface = WriteFramework(dependencySearch, "Dep", "public struct D {}");

        IReadOnlyList<SwiftEvidenceSource> SourcesNow(string triple) =>
        [
            new(SwiftEvidenceSource.WrapperRole, "Swift.RuntimeSwiftBindings", SwiftEvidenceSource.DigestOf([wrapper]),
                SwiftWrapperLoweringEvidence.WrapperFlags(triple)),
            .. SwiftWrapperLoweringEvidence.InterfaceSources(Artifact, boundSearch, [dependencySearch]),
        ];

        var collection = Collection();
        collection.BeginPass(null).Observe(compiled, Ir(DeclaredIr), SourcesNow(compiled));
        var receipt = collection.Receipt([], out _)!;
        Assert.Equal(4, receipt.Receipts.Single().Slices[slice].Sources.Length);

        var scope = new SwiftArtifactScope(Artifact, [slice]);
        SwiftEvidenceExpectation Expected(string compilerNow, string sdkNow, SwiftLoweringContract contractNow, string tripleNow) =>
            new(scope, compilerNow, contractNow,
                new Dictionary<string, string> { [slice] = sdkNow }.ToImmutableSortedDictionary(StringComparer.Ordinal),
                new Dictionary<string, ImmutableArray<SwiftEvidenceSource>> { [slice] = SourcesNow(tripleNow).ToImmutableArray() }
                    .ToImmutableSortedDictionary(StringComparer.Ordinal));

        var sdk = Sdk("iphonesimulator")!;
        var unchanged = SwiftLoweringAuthorization.Authorize(Expected(Compiler, sdk, SwiftLoweringContract.Current, compiled), receipt);
        Assert.True(unchanged.Authorizes, unchanged.Refusal);
        Assert.True(unchanged.Judge(Declared, "void (ptr noalias sret, i64, ptr, ptr)").Executable);

        var current = SwiftLoweringContract.Current;
        var expected = input switch
        {
            "compiler" => Expected("Apple Swift version 6.2.5 (swiftlang-6.2.5.1.1 clang-1700.6.5.1)", sdk, current, compiled),
            "sdk" => Expected(Compiler, "iphonesimulator 26.2 (23C54)", current, compiled),
            "flags" => Expected(Compiler, sdk, current, "arm64-apple-ios17.0-simulator"),
            "contract" => Expected(Compiler, sdk, current with { CollectorVersion = current.CollectorVersion + 1 }, compiled),
            _ => null,
        };
        if (input == "wrapper")
            File.AppendAllText(wrapper, "public func g() {}\n");
        if (input == "peer")
            File.AppendAllText(peerInterface, "public struct Q {}\n");
        if (input == "dependency")
            File.AppendAllText(dependencyInterface, "public struct E {}\n");
        expected ??= Expected(Compiler, sdk, current, compiled);

        var judgement = SwiftLoweringAuthorization.Authorize(expected, receipt);
        Assert.False(judgement.Authorizes, $"a changed {input} still authorizes");
        Assert.False(judgement.Judge(Declared, "void (ptr noalias sret, i64, ptr, ptr)").Executable);
    }

    [Fact]
    public void InterfaceSources_DigestWhatSwiftcImports_TheFirstFrameworkOfEachName()
    {
        var bound = Path.Combine(_dir, "slice");
        var first = Path.Combine(_dir, "deps-a");
        var second = Path.Combine(_dir, "deps-b");
        WriteFramework(bound, Artifact, "public func f()");
        // A peer framework beside the bound one is searched before the dependency paths, as swiftc's -F order does.
        WriteFramework(bound, "Peer", "public struct P {}");
        WriteFramework(first, "Peer", "public struct ShadowedPeer {}");
        WriteFramework(first, "Dep", "public struct D {}");
        WriteFramework(second, "Dep", "public struct Shadowed {}");
        // A framework binary is not what swiftc imports, so it does not change the interface digest.
        var before = SwiftWrapperLoweringEvidence.InterfaceSources(Artifact, bound, [first, second]);
        File.WriteAllText(Path.Combine(first, "Dep.framework", "Dep"), "a rebuilt binary");
        var after = SwiftWrapperLoweringEvidence.InterfaceSources(Artifact, bound, [first, second]);

        Assert.Equal(new[] { Artifact, "Peer", "Dep" }, before.Select(s => s.Name));
        Assert.All(before, s => Assert.Equal(SwiftEvidenceSource.InterfaceRole, s.Role));
        Assert.All(before, s => Assert.Empty(s.Flags));
        Assert.Equal(before, after);
        Assert.Equal(SwiftWrapperLoweringEvidence.InterfaceSources("Peer", bound, null)[0], before[1]);
        Assert.Equal(SwiftWrapperLoweringEvidence.InterfaceSources("Dep", first, null)[0], before[2]);
    }

    private static string WriteFramework(string searchPath, string name, string declaration)
    {
        var modules = Path.Combine(searchPath, name + ".framework", "Modules", name + ".swiftmodule");
        Directory.CreateDirectory(modules);
        var path = Path.Combine(modules, "arm64-apple-ios-simulator.swiftinterface");
        File.WriteAllText(path, $"// swift-module-flags: -module-name {name}\n{declaration}\n");
        File.WriteAllText(Path.Combine(searchPath, name + ".framework", name), "binary");
        return path;
    }

    // ---- the IR pass inside the wrapper compile ----

    /// <summary>
    /// Answers the wrapper compile as a successful toolchain would, and the IR pass with canned IR
    /// written where it asks (or a failure).
    /// </summary>
    private sealed class IrRunner : ICommandRunner
    {
        private readonly MockCommandRunner _inner = new() { SynthesizeMachOOutputs = true };
        private readonly string _ir;
        private readonly int _irExit;

        public IrRunner(string ir, int irExit = 0)
        {
            _ir = ir;
            _irExit = irExit;
            _inner.SetResponse("--show-sdk-path", 0, "/sdk/path");
        }

        public List<string> Swiftc { get; } = new();

        public (int ExitCode, string StdOut, string StdErr) Run(string command, string arguments, int timeoutMs = 30000)
        {
            if (arguments.StartsWith("swiftc ", StringComparison.Ordinal))
                Swiftc.Add(arguments);
            if (!arguments.Contains("-emit-ir", StringComparison.Ordinal))
                return _inner.Run(command, arguments, timeoutMs);
            if (_irExit != 0)
                return (_irExit, "", "wrapper.swift:1:1: error: something the link did not hit");
            var output = Regex.Match(arguments, "-o \"([^\"]+)\"");
            if (!output.Success)
                return (0, _ir, "");
            File.WriteAllText(output.Groups[1].Value, _ir);
            return (0, "", "");
        }
    }

    private string WriteWrapperSources()
    {
        var framework = Path.Combine(_dir, "source-fw");
        Directory.CreateDirectory(framework);
        File.WriteAllText(Path.Combine(framework, "Info.plist"),
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<plist version=\"1.0\"><dict><key>MinimumOSVersion</key><string>16.0</string></dict></plist>\n");
        var dylib = Path.Combine(framework, "TestLib");
        File.WriteAllText(dylib, "");
        File.WriteAllText(Path.Combine(_dir, "Swift.TestLib.swift"), """
            import Foundation
            @_silgen_name("good_func")
            public func good_func(_self: UnsafeMutableRawPointer) {
                let x = 1
            }
            """);
        return dylib;
    }

    [Fact]
    public void Compile_EmitsTheIrOfTheLinkedWrapper_WithTheLinkCompilesFrontendFlags()
    {
        var dylib = WriteWrapperSources();
        var runner = new IrRunner("; ModuleID = 'Swift.TestLib.swift'\n" + DeclaredIr + "\n");
        var collection = Collection();

        var result = SwiftWrapperCompiler.Compile(_dir, "TestLib", "/fw/search", dylib, NullLogger.Instance, runner,
            lowering: collection.BeginPass(null));

        Assert.NotNull(result);
        var link = Assert.Single(runner.Swiftc, a => a.Contains("-emit-library", StringComparison.Ordinal));
        var ir = Assert.Single(runner.Swiftc, a => a.Contains("-emit-ir", StringComparison.Ordinal));
        Assert.True(runner.Swiftc.IndexOf(ir) > runner.Swiftc.IndexOf(link), "the IR pass runs only after the link");
        // Everything that decides how swiftc reads and lowers the wrapper is the link's own.
        const string moduleName = "-module-name TestLibSwiftBindings ";
        var frontend = link["swiftc -emit-library ".Length..(link.IndexOf(moduleName, StringComparison.Ordinal) + moduleName.Length)];
        Assert.StartsWith("swiftc -emit-ir -parse-as-library " + frontend, ir);
        Assert.Contains("-target arm64-apple-ios16.0-simulator ", frontend);
        Assert.Contains("-sdk \"/sdk/path\" ", frontend);
        Assert.DoesNotContain("-Xlinker", ir);
        // The same post-processed source the link compiled.
        var linkedFile = Regex.Match(link, "\"([^\"]+\\.swift)\"").Groups[1].Value;
        Assert.EndsWith($"\"{linkedFile}\"", ir);

        var receipt = Assert.Single(collection.Receipt([], out var why)!.Receipts);
        Assert.Null(why);
        var slice = receipt.Slices["arm64-apple-ios15.0-simulator"];
        Assert.Equal("swiftcc void (ptr noalias sret, i64, ptr, ptr)", slice.Symbols[Declared]);
        Assert.Equal(SwiftWrapperLoweringEvidence.NotInWrapperReason, slice.Unobserved![Absent]);
        var source = Assert.Single(slice.Sources);
        Assert.Equal((SwiftEvidenceSource.WrapperRole, "TestLibSwiftBindings"), (source.Role, source.Name));
        Assert.Equal(SwiftWrapperLoweringEvidence.WrapperFlags("arm64-apple-ios16.0-simulator").ToArray(), source.Flags.ToArray());
        Assert.StartsWith("sha256:", source.Digest);
    }

    [Fact]
    public void Compile_AFailedIrPass_StillShipsTheWrapper_ButLeavesNoReceipt()
    {
        var dylib = WriteWrapperSources();
        var collection = Collection();

        var result = SwiftWrapperCompiler.Compile(_dir, "TestLib", "/fw/search", dylib, NullLogger.Instance, new IrRunner("", irExit: 1),
            lowering: collection.BeginPass(null));

        Assert.NotNull(result);
        Assert.True(Directory.Exists(result!.XCFrameworkPath));
        Assert.Null(collection.Receipt([], out var why));
        Assert.Contains("swiftc -emit-ir exited 1: wrapper.swift:1:1: error: something the link did not hit", why);
    }

    [Fact]
    public void Compile_IrThatShowsNoModule_LeavesNoReceipt()
    {
        var dylib = WriteWrapperSources();
        var collection = Collection();

        SwiftWrapperCompiler.Compile(_dir, "TestLib", "/fw/search", dylib, NullLogger.Instance, new IrRunner(""),
            lowering: collection.BeginPass(null));

        Assert.Null(collection.Receipt([], out var why));
        Assert.Contains("wrote no IR", why);
    }

    [Fact]
    public void Compile_WithoutCollection_RunsNoIrPass()
    {
        var dylib = WriteWrapperSources();
        var runner = new IrRunner(DeclaredIr);

        Assert.NotNull(SwiftWrapperCompiler.Compile(_dir, "TestLib", "/fw/search", dylib, NullLogger.Instance, runner));

        Assert.Contains(runner.Swiftc, a => a.Contains("-emit-library", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Swiftc, a => a.Contains("-emit-ir", StringComparison.Ordinal));
    }
}
