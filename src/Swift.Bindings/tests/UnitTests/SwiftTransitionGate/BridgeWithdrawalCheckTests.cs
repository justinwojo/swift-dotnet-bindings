// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SwiftBindings.TransitionGate;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The pack and consumer check (<see cref="ConsumerTransitionCheck"/>) over a binding whose bridge
/// routing was withdrawn after a relink failure: the withdrawal is accepted only when its record, the
/// compiled assembly and the wrapper's images all show the same complete restoration, and every
/// negative control — an unrecorded gap, an inconsistent record, a missing or late guard, a dangling
/// bridge import, a stale native hash or bridge, a partial restoration, a receipt contradiction — is
/// refused with images required (the pack) and without (an app). The strict reading
/// (<see cref="PackageTransitionEvidence.Check"/> by default) refuses even the coherent withdrawal.
/// </summary>
public class BridgeWithdrawalCheckTests
{
    private const string Library = "FixtureSwiftBindings";
    private const string Bridge = "SBSR_00112233445566778899aabbccddeeff";
    private const string EntryPoint = "$s7Fixture7AccountC7settledSivg";
    private const string Member = "Native::Settled";
    private const string Reason = "the wrapper could not be relinked with its bridges: the relink failed";

    /// <summary>SelfRegister | LoweredSignature: what <c>Native.Settled</c> passes (an untyped self after an ordinary argument).</summary>
    private const ulong Shapes = SwiftTransitionEngines.SelfRegister | SwiftTransitionEngines.LoweredSignature;

    private static readonly string RuntimeDll = Path.Combine(AppContext.BaseDirectory, "Swift.Runtime.dll");

    private static string Native(string? extra = null, string importLibrary = Library) => $$"""
        internal static unsafe class Native
        {
            [DllImport("{{importLibrary}}", EntryPoint = "{{EntryPoint}}")] [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
            internal static extern long Settled(long a, SwiftSelf self);
            {{extra}}
        }
        """;

    private const string Guarded = """
        public sealed class Account
        {
            public long Settled() { SwiftTransitionGuard.ThrowIfUnsupported((SwiftTransitionFacts)5, "Account.Settled"); return Native.Settled(1, default); }
        }
        """;

    /// <summary>The image of the wrapper the receipt describes: no bridge and no manifest hash.</summary>
    private static PackagedNative CleanWrapper
    {
        get
        {
            var path = Path.Combine(SwiftCallPlanTests.RepoRoot, "src", "Swift.Bindings.Apple", "native", "SBApple.xcframework",
                "ios-arm64_x86_64-simulator", "SBApple.framework", "SBApple");
            return new PackagedNative(Library, path, File.ReadAllBytes(path));
        }
    }

    /// <summary>An image that keeps a routing's bridges and manifest hash: what a stale wrapper ships.</summary>
    private static PackagedNative StaleWrapper
    {
        get
        {
            var path = SwiftTransitionManifestTests.RuntimeSlices.First();
            return new PackagedNative(Library, path, File.ReadAllBytes(path));
        }
    }

    /// <summary>
    /// <see cref="StaleWrapper"/> with its manifest hash symbol renamed to Fixture's, in place in the
    /// string table: an image that keeps the bridges and the hash symbol of a routing of Fixture.
    /// </summary>
    private static PackagedNative StaleWrapperOfFixture
    {
        get
        {
            var stale = StaleWrapper;
            var image = (byte[])stale.Image.Clone();
            var held = MachOSymbols.Read(image).SelectMany(s => s.Exports).First(e => e.StartsWith(SwiftTransitionManifestFormat.NativeSymbolPrefix, StringComparison.Ordinal));
            var from = Encoding.ASCII.GetBytes("_" + held + "\0");
            var to = Encoding.ASCII.GetBytes("_" + SwiftTransitionManifestFormat.NativeSymbol("Fixture")).Concat(new byte[from.Length]).Take(from.Length).ToArray();
            var renamed = 0;
            for (var at = image.AsSpan().IndexOf(from); at >= 0; at = image.AsSpan().IndexOf(from))
            {
                to.CopyTo(image, at);
                renamed++;
            }
            Assert.True(renamed > 0, $"{stale.DisplayPath} spells no {held} to rename");
            return stale with { Image = image };
        }
    }

    // ---- the coherent withdrawal ----------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACoherentWithdrawal_IsVerified_WithItsMembersAndEngines(bool requireImages)
    {
        var result = Check(Native() + Guarded, Plans(), [CleanWrapper], requireImages);

        Assert.True(result.Verdict == ConsumerTransitionVerdict.Verified, string.Join("\n", result.Problems));
        Assert.Equal(ConsumerTransitionRemedy.None, result.Remedy);
        var withdrawal = Assert.Single(result.Withdrawals);
        Assert.Equal("RelinkFailed", withdrawal.Cause);
        Assert.Equal(Library, withdrawal.Library);
        var transition = Assert.Single(withdrawal.Transitions);
        Assert.Equal(Member, transition.Member);
        Assert.Equal(["Mono", "the CoreCLR interpreter (arm64)", "the CoreCLR interpreter (x86_64)"], transition.RefusesOn.ToArray());
        Assert.Contains("Native::Settled (refuses on Mono, ", withdrawal.Describe());
    }

    [Fact]
    public void ACoherentWithdrawal_FailsTheStrictCheck_WhichAcceptsNoGap()
    {
        var image = Compile(Native() + Guarded, Plans());
        var scratch = Directory.CreateTempSubdirectory("withdrawal-strict-").FullName;
        try
        {
            var assembly = new PackagedAssembly("Fixture.dll", image, [AppContext.BaseDirectory]);
            var strict = PackageTransitionEvidence.Check([assembly], [CleanWrapper], requireRoutedBridges: false, Path.Combine(scratch, "strict"));
            Assert.Contains(strict.Problems, p => p.Contains("routing", StringComparison.Ordinal) && p.Contains(Reason, StringComparison.Ordinal));

            var accepting = PackageTransitionEvidence.Check([assembly], [CleanWrapper], requireRoutedBridges: false, Path.Combine(scratch, "accepting"), acceptWithdrawals: true);
            Assert.True(accepting.Passed, string.Join("\n", accepting.Problems));

            // Accepting, the withdrawn library's image is required, to show the bridges are gone.
            var imageless = PackageTransitionEvidence.Check([assembly], [], requireRoutedBridges: false, Path.Combine(scratch, "imageless"), acceptWithdrawals: true);
            Assert.Contains(imageless.Problems, p => p.Contains($"no image of {Library}", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void AWithdrawalWithoutTheWrappersImage_IsUnverified_AndRefusedOnlyWhereImagesAreRequired()
    {
        var app = Check(Native() + Guarded, Plans(), [], requireNativeImages: false);
        Assert.Equal(ConsumerTransitionVerdict.Verified, app.Verdict);
        Assert.Equal(Library, Assert.Single(app.UnverifiedLibraries));

        var pack = Check(Native() + Guarded, Plans(), [], requireNativeImages: true);
        Assert.Equal(ConsumerTransitionVerdict.Refused, pack.Verdict);
        Assert.Contains(pack.Problems, p => p.Contains("were withdrawn, but no image", StringComparison.Ordinal));
    }

    // ---- negative controls: each refused by the pack (images required) and an app ---------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnUnrecordedGap_IsRefused(bool requireImages)
    {
        var result = Check(Native() + Guarded, Plans(withdrawal: false), [CleanWrapper], requireImages);
        AssertRefused(result, "kept its direct import", requireImages ? null : ConsumerTransitionRemedy.Regenerate);
        Assert.Empty(result.Withdrawals);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AGapWithAnotherReasonThanTheRecord_IsRefused(bool requireImages)
    {
        var result = Check(Native() + Guarded, Plans(refusal: Reason + "!"), [CleanWrapper], requireImages);
        AssertRefused(result, "kept its direct import", ConsumerTransitionRemedy.WithdrawalIncoherent);
        Assert.Contains(result.Problems, p => p.Contains("does not show that transition executable and restored", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARecordWhoseEnginesDisagreeWithItsShapes_IsRefused(bool requireImages)
    {
        var result = Check(Native() + Guarded, Plans(refusesOn: ["Mono"]), [CleanWrapper], requireImages);
        AssertRefused(result, "but its shapes (SelfRegister, LoweredSignature) are refused on", ConsumerTransitionRemedy.WithdrawalIncoherent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARecordWhoseShapesAreNotTheCompiledImports_IsRefused(bool requireImages)
    {
        var shapes = SwiftTransitionEngines.SelfRegister;
        var result = Check(Native() + Guarded, Plans(shapes: shapes), [CleanWrapper], requireImages);
        AssertRefused(result, "but the compiled import has [SelfRegister, LoweredSignature]", ConsumerTransitionRemedy.WithdrawalIncoherent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARestoredImportWithoutAGuard_IsRefused(bool requireImages)
    {
        const string unguarded = "public sealed class Account { public long Settled() => Native.Settled(1, default); }";
        var result = Check(Native() + unguarded, Plans(), [CleanWrapper], requireImages);
        AssertRefused(result, "residual guard", ConsumerTransitionRemedy.WithdrawalIncoherent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARestoredImportWithALateGuard_IsRefused(bool requireImages)
    {
        const string late = """
            public sealed class Account
            {
                public long Settled() { var n = Native.Settled(1, default); SwiftTransitionGuard.ThrowIfUnsupported((SwiftTransitionFacts)5, "Account.Settled"); return n; }
            }
            """;
        var result = Check(Native() + late, Plans(), [CleanWrapper], requireImages);
        AssertRefused(result, "residual guard", ConsumerTransitionRemedy.WithdrawalIncoherent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADanglingBridgeImport_IsRefused(bool requireImages)
    {
        var twin = $"[DllImport(\"{Library}\", EntryPoint = \"{Bridge}\")] internal static extern long SettledTwin(long a, nint self);";
        var result = Check(Native(twin) + Guarded, Plans(), [CleanWrapper], requireImages);
        AssertRefused(result, $"imports the bridge {Bridge}, which no manifest routes", ConsumerTransitionRemedy.WithdrawalIncoherent);
        Assert.Equal(TransitionEvidenceState.TamperedOrMismatched, result.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AWrapperThatKeepsAnEarlierRoutingsBridgesAndHash_IsRefused_AsAStaleBuild(bool requireImages)
    {
        var result = Check(Native() + Guarded, Plans(), [StaleWrapper], requireImages);
        AssertRefused(result, "which no manifest of Fixture names", ConsumerTransitionRemedy.CleanRebuild);
        Assert.Contains(result.Problems, p => p.Contains("exports SBSR_", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APartialRestoration_IsRefused(bool requireImages)
    {
        // Another member still on its bridge while the record says the module's bridges were withdrawn:
        // the manifest routes it, and the assembly imports its twin.
        var twin = $"[DllImport(\"{Library}\", EntryPoint = \"{Bridge}\")] internal static extern long Other__{Bridge}(long a);";
        var result = Check(Native(twin) + Guarded, Plans(stillBridged: true), [CleanWrapper], requireImages, nativeLibrary: Library);
        AssertRefused(result, "a partial restoration", ConsumerTransitionRemedy.WithdrawalIncoherent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARecordOfATransitionTheAssemblyDoesNotImport_IsRefused(bool requireImages)
    {
        var result = Check(Native() + Guarded, Plans(extraListed: true), [CleanWrapper], requireImages);
        AssertRefused(result, "the assembly declares no such import", ConsumerTransitionRemedy.WithdrawalIncoherent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARestoredImportFromTheModulesOwnLibrary_IsAccepted(bool requireImages)
    {
        // A dynamic framework's restored imports call the module's own library, not the wrapper
        // that hosted the bridges; both images are clean.
        var module = CleanWrapper with { Library = "Fixture" };
        var result = Check(Native(importLibrary: "Fixture") + Guarded, Plans(), [CleanWrapper, module], requireImages);
        Assert.True(result.Verdict == ConsumerTransitionVerdict.Verified, string.Join("\n", result.Problems));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARecordThatNamesACleanDecoy_DoesNotHideAStaleWrapperOfTheModule(bool requireImages)
    {
        // The stale image holds Fixture's manifest hash symbol, which only a wrapper that bridged
        // Fixture exports, whatever library the record names.
        const string decoy = "DecoySwiftBindings";
        var clean = CleanWrapper with { Library = decoy };
        var result = Check(Native() + Guarded, Plans(library: decoy), [clean, StaleWrapperOfFixture], requireImages);
        AssertRefused(result, $"exports {SwiftTransitionManifestFormat.NativeSymbol("Fixture")}, the manifest hash of Fixture's withdrawn routing, "
            + $"but the withdrawal records its bridges as withdrawn from {decoy}", ConsumerTransitionRemedy.CleanRebuild);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnotherLibrarysRoutedImage_IsNotTakenForALeftover(bool requireImages)
    {
        // The runtime's own routed image, offered as an app offers it, beside an import from it and
        // a withdrawn transition whose bridge has the same name as one of the runtime's.
        var runtime = StaleWrapper with { Library = "SwiftBindingsRuntime" };
        var bridge = MachOSymbols.Read(runtime.Image).SelectMany(s => s.Exports).First(SwiftTransitionScanner.IsForwardBridge);
        const string import = """
            [DllImport("SwiftBindingsRuntime", EntryPoint = "SwiftBindings_InitializeConcurrency")]
            internal static extern void InitializeConcurrency();
            """;
        var result = Check(Native(import) + Guarded, Plans(bridge: bridge), [CleanWrapper, runtime], requireImages);
        Assert.True(result.Verdict == ConsumerTransitionVerdict.Verified, string.Join("\n", result.Problems));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARecordThatListsATransitionTwice_IsRefused(bool requireImages)
    {
        var result = Check(Native() + Guarded, Plans(listedTwice: true), [CleanWrapper], requireImages);
        AssertRefused(result, "2 times", ConsumerTransitionRemedy.WithdrawalIncoherent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AWithdrawalOfAReceiptContradiction_IsRefused_AsUnprovable(bool requireImages)
    {
        var result = Check(Native() + Guarded, Plans(cause: "ReceiptMismatch"), [CleanWrapper], requireImages);
        AssertRefused(result, "cannot be shown safe", ConsumerTransitionRemedy.ReceiptMismatch);
    }

    [Fact]
    public void ARecordThatDoesNotRead_IsRefused()
    {
        var plans = JsonNode.Parse(Plans())!;
        plans["bridgeWithdrawal"]!.AsObject().Remove("library");
        var result = Check(Native() + Guarded, plans.ToJsonString(), [CleanWrapper], requireNativeImages: true);
        AssertRefused(result, "does not record its cause, reason, library and transitions", null);
    }

    // ---- the byte-level controls --bridge-consumer's rollback mode runs over a packed binding ------

    [Theory]
    [InlineData(BridgeWithdrawalControl.UnrecordedGap, "kept its direct import", ConsumerTransitionRemedy.Regenerate, true)]
    [InlineData(BridgeWithdrawalControl.UnrecordedGap, "kept its direct import", ConsumerTransitionRemedy.Regenerate, false)]
    [InlineData(BridgeWithdrawalControl.InconsistentRecord, "does not show that transition executable and restored", ConsumerTransitionRemedy.WithdrawalIncoherent, true)]
    [InlineData(BridgeWithdrawalControl.InconsistentRecord, "does not show that transition executable and restored", ConsumerTransitionRemedy.WithdrawalIncoherent, false)]
    [InlineData(BridgeWithdrawalControl.DanglingBridgeImport, "which no manifest routes", ConsumerTransitionRemedy.WithdrawalIncoherent, true)]
    [InlineData(BridgeWithdrawalControl.DanglingBridgeImport, "which no manifest routes", ConsumerTransitionRemedy.WithdrawalIncoherent, false)]
    [InlineData(BridgeWithdrawalControl.MissingGuard, "residual guard", ConsumerTransitionRemedy.WithdrawalIncoherent, true)]
    [InlineData(BridgeWithdrawalControl.MissingGuard, "residual guard", ConsumerTransitionRemedy.WithdrawalIncoherent, false)]
    public void ADoctoredWithdrawal_IsRefused_ForItsOwnReason(BridgeWithdrawalControl control, string expected, ConsumerTransitionRemedy remedy, bool requireImages)
    {
        var image = Compile(Native() + Guarded, Plans());
        var doctored = BridgeWithdrawalControls.Doctor(image, "Fixture.dll", [AppContext.BaseDirectory], control);
        Assert.Equal(image.Length, doctored.Length);
        // A manifest edit is rehashed: the evidence still reads as authentic.
        if (control is BridgeWithdrawalControl.UnrecordedGap or BridgeWithdrawalControl.InconsistentRecord)
            Assert.Equal(TransitionEvidenceState.PresentValid,
                SwiftTransitionManifestReader.Read(doctored, "Fixture.dll", new SwiftTransitionScanOptions { ReferenceDirectories = [AppContext.BaseDirectory] }).State);

        var scratch = Directory.CreateTempSubdirectory("withdrawal-control-").FullName;
        try
        {
            var result = ConsumerTransitionCheck.Check(new PackagedAssembly("Fixture.dll", doctored, [AppContext.BaseDirectory]), [CleanWrapper], requireImages, scratch);
            AssertRefused(result, expected, remedy);
            // The undoctored binding is accepted the same way, so the refusal is the control's.
            Assert.Equal(ConsumerTransitionVerdict.Verified,
                ConsumerTransitionCheck.Check(new PackagedAssembly("Fixture.dll", image, [AppContext.BaseDirectory]), [CleanWrapper], requireImages, Path.Combine(scratch, "positive")).Verdict);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void Doctor_RefusesAnAssemblyWithNoWithdrawal() =>
        Assert.Throws<InvalidOperationException>(() =>
            BridgeWithdrawalControls.Doctor(Compile(Native() + Guarded, Plans(withdrawal: false)), "Fixture.dll", [AppContext.BaseDirectory], BridgeWithdrawalControl.MissingGuard));

    // ---- the engine table -----------------------------------------------------------------------

    [Fact]
    public void TheEngineTable_IsTheRuntimeGuardsTable()
    {
        var known = (ulong)Swift.Runtime.SwiftTransitionGuard.KnownFacts;
        Assert.Equal(SwiftTransitionEngines.Known, known);
        var runtime = new (string Name, Swift.Runtime.SwiftTransitionGuard.Engine Engine, bool Compiles, System.Runtime.InteropServices.Architecture Architecture)[]
        {
            ("NativeAOT", Swift.Runtime.SwiftTransitionGuard.Engine.NativeAot, false, System.Runtime.InteropServices.Architecture.Arm64),
            ("Mono", Swift.Runtime.SwiftTransitionGuard.Engine.Mono, true, System.Runtime.InteropServices.Architecture.Arm64),
            ("CoreCLR (JIT)", Swift.Runtime.SwiftTransitionGuard.Engine.CoreClr, true, System.Runtime.InteropServices.Architecture.Arm64),
            ("the CoreCLR interpreter (arm64)", Swift.Runtime.SwiftTransitionGuard.Engine.CoreClr, false, System.Runtime.InteropServices.Architecture.Arm64),
            ("the CoreCLR interpreter (x86_64)", Swift.Runtime.SwiftTransitionGuard.Engine.CoreClr, false, System.Runtime.InteropServices.Architecture.X64),
        };
        Assert.Equal(runtime.Select(r => r.Name), SwiftTransitionEngines.All.Select(e => e.Name));
        foreach (var (name, engine, compiles, architecture) in runtime)
            Assert.Equal((ulong)Swift.Runtime.SwiftTransitionGuard.UnsupportedFactsFor(engine, compiles, architecture) & known,
                SwiftTransitionEngines.All.Single(e => e.Name == name).Refuses);
        // Mono and NativeAOT refuse the same on either architecture.
        Assert.Equal(Swift.Runtime.SwiftTransitionGuard.UnsupportedFactsFor(Swift.Runtime.SwiftTransitionGuard.Engine.Mono, true, System.Runtime.InteropServices.Architecture.X64),
            Swift.Runtime.SwiftTransitionGuard.UnsupportedFactsFor(Swift.Runtime.SwiftTransitionGuard.Engine.Mono, true, System.Runtime.InteropServices.Architecture.Arm64));
    }

    [Theory]
    [InlineData(0UL, new string[0])]
    [InlineData(SwiftTransitionEngines.LoweredSignature, new[] { "the CoreCLR interpreter (x86_64)" })]
    [InlineData(SwiftTransitionEngines.SelfRegister, new[] { "Mono", "the CoreCLR interpreter (arm64)" })]
    [InlineData(1UL << 40, new[] { "NativeAOT", "Mono", "CoreCLR (JIT)", "the CoreCLR interpreter (arm64)", "the CoreCLR interpreter (x86_64)" })]
    public void Refusing_NamesTheEnginesWhoseGuardRefusesTheShapes(ulong shapes, string[] engines)
    {
        Assert.Equal(engines, SwiftTransitionEngines.Refusing(shapes));
        Assert.Equal(shapes, SwiftTransitionEngines.ShapesOf(SwiftTransitionEngines.ShapeNames(shapes)));
    }

    [Fact]
    public void ShapesOf_RefusesANameItDoesNotKnow() =>
        Assert.Null(SwiftTransitionEngines.ShapesOf(["SelfRegister", "Bogus"]));

    // ---- fixtures -------------------------------------------------------------------------------

    private static void AssertRefused(ConsumerTransitionResult result, string expected, ConsumerTransitionRemedy? remedy)
    {
        Assert.Equal(ConsumerTransitionVerdict.Refused, result.Verdict);
        Assert.True(result.Problems.Any(p => p.Contains(expected, StringComparison.Ordinal)),
            $"no problem names \"{expected}\":\n{string.Join("\n", result.Problems)}");
        if (remedy is { } r)
            Assert.Equal(r, result.Remedy);
    }

    /// <summary>
    /// The plans of a module whose one executable transition, <c>Native::Settled</c>, went back to its
    /// direct import when its bridges were withdrawn, each knob seeding one way the record can be wrong.
    /// </summary>
    internal static string Plans(
        bool withdrawal = true, string refusal = Reason, string cause = "RelinkFailed", ulong shapes = Shapes,
        string[]? refusesOn = null, bool stillBridged = false, bool extraListed = false, string library = Library, bool listedTwice = false, string bridge = Bridge)
    {
        JsonObject Entry(string member, string entryPoint, bool bridged, string? bridgeRefusal) => new()
        {
            ["transition"] = "ForwardPInvoke",
            ["member"] = member,
            ["entryPoint"] = entryPoint,
            ["status"] = "Planned",
            ["symbol"] = bridge,
            ["refusals"] = new JsonArray(),
            ["executable"] = true,
            ["evidence"] = "every slice shows the predicted signature",
            ["bridged"] = bridged,
            ["bridgeRefusal"] = bridgeRefusal,
        };
        JsonObject Listed(string member, string entryPoint) => new()
        {
            ["member"] = member,
            ["entryPoint"] = entryPoint,
            ["symbol"] = bridge,
            ["shapes"] = new JsonArray(SwiftTransitionEngines.ShapeNames(shapes).Select(n => (JsonNode?)n).ToArray()),
            ["refusesOn"] = new JsonArray((refusesOn ?? SwiftTransitionEngines.Refusing(shapes).ToArray()).Select(n => (JsonNode?)n).ToArray()),
        };

        var coverage = new JsonArray(Entry(Member, EntryPoint, bridged: false, refusal));
        if (stillBridged)
        {
            var other = Entry("Native::Other", "$s7Fixture5otherSivg", bridged: true, null);
            other["twin"] = $"Native::Other__{Bridge}";
            coverage.Add((JsonNode)other);
        }
        var plans = new JsonObject
        {
            ["planVersion"] = SwiftCallPlan.Version,
            ["factsVersion"] = SwiftCallFacts.CurrentVersion,
            ["module"] = "Fixture",
            ["coverage"] = coverage,
            ["plans"] = new JsonArray(),
        };
        if (withdrawal)
        {
            var transitions = new JsonArray(Listed(Member, EntryPoint));
            if (extraListed)
                transitions.Add((JsonNode)Listed("Native::Gone", "$s7Fixture4goneSivg"));
            if (listedTwice)
                transitions.Add((JsonNode)Listed(Member, EntryPoint));
            plans["bridgeWithdrawal"] = new JsonObject
            {
                ["cause"] = cause,
                ["reason"] = Reason,
                ["library"] = library,
                ["transitions"] = transitions,
            };
        }
        return plans.ToJsonString();
    }

    private static ConsumerTransitionResult Check(string body, string plans, IReadOnlyList<PackagedNative> natives, bool requireNativeImages,
        string? nativeLibrary = null)
    {
        var scratch = Directory.CreateTempSubdirectory("withdrawal-check-").FullName;
        try
        {
            return ConsumerTransitionCheck.Check(new PackagedAssembly("Fixture.dll", Compile(body, plans, nativeLibrary), [AppContext.BaseDirectory]),
                natives, requireNativeImages, scratch);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// <paramref name="body"/> compiled against the framework and the runtime, embedding the manifest of
    /// <paramref name="plans"/> as a generated module's, with its locator.
    /// </summary>
    internal static byte[] Compile(string body, string plans, string? nativeLibrary = null)
    {
        var manifest = SwiftTransitionManifestFormat.Compose("Fixture", SwiftTransitionManifestFormat.Generated, "test",
            SwiftTransitionManifestTests.Contract, nativeLibrary, plans, null);
        const string resource = "Fixture.swift-transition-manifest.json";
        var locator = SwiftTransitionManifestFormat.AttributeSource([(resource, SwiftTransitionManifestFormat.Hash(manifest))]);
        const string preamble = """
            using System;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Swift;
            using Swift.Runtime;
            [assembly: DisableRuntimeMarshalling]

            """;
        var frameworkDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p) && p.StartsWith(frameworkDir, StringComparison.Ordinal))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(RuntimeDll));
        var options = new CSharpParseOptions(LanguageVersion.Latest);
        var compilation = CSharpCompilation.Create("Fixture",
            [CSharpSyntaxTree.ParseText(preamble + body, options), CSharpSyntaxTree.ParseText(locator, options)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var stream = new MemoryStream();
        var bytes = Encoding.UTF8.GetBytes(manifest);
        var emitted = compilation.Emit(stream, manifestResources: [new ResourceDescription(resource, () => new MemoryStream(bytes), isPublic: true)]);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return stream.ToArray();
    }
}
