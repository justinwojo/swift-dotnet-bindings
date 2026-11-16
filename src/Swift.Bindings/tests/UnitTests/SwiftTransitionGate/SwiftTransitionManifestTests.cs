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
using Microsoft.CodeAnalysis.Emit;
using SwiftBindings.TransitionGate;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The transition manifest a compiled assembly embeds, read back without the build tree: its
/// format, the four evidence states the reader reports, the native hash match, and the package
/// check's reds (a seeded rule violation, a tampered manifest, a missing manifest beside bridged
/// transitions, and a package that must route bridges but routes none). The fixtures are small
/// assemblies compiled here with Roslyn, and the runtime as it is built and committed.
/// </summary>
public class SwiftTransitionManifestTests
{
    internal const string Swift = "[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]";

    private const string Preamble = """
        using System;
        using System.Runtime.CompilerServices;
        using System.Runtime.InteropServices;
        using System.Runtime.InteropServices.Swift;
        [assembly: DisableRuntimeMarshalling]

        """;

    internal static readonly string RuntimeDll = Path.Combine(AppContext.BaseDirectory, "Swift.Runtime.dll");

    private static string RuntimeXcframework => Path.Combine(SwiftCallPlanTests.RepoRoot, "src", "Swift.Runtime", "native", "SwiftBindingsRuntime.xcframework");

    internal static IEnumerable<string> RuntimeSlices =>
        Directory.EnumerateFiles(RuntimeXcframework, "SwiftBindingsRuntime", SearchOption.AllDirectories)
            .Where(p => p.Contains(".framework", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    // ---- the format -----------------------------------------------------------------------------

    [Fact]
    public void Compose_IsDeterministicAndReadsBack()
    {
        var plans = Plans("Acme", ("Acme.C::Prim", "prim"));
        var first = Manifest("Acme", plans);
        Assert.Equal(first, Manifest("Acme", plans));
        Assert.EndsWith("\n", first);
        var read = SwiftTransitionManifestFormat.Parse(Encoding.UTF8.GetBytes(first), "fixture");
        Assert.Equal("Acme", read.Module);
        Assert.Equal(SwiftTransitionManifestFormat.Generated, read.Origin);
        Assert.Null(read.NativeLibrary);
        Assert.Empty(read.Bridges);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(plans), JsonNode.Parse(read.Plans)));
    }

    [Fact]
    public void AttributeValue_RoundTripsAndRejectsMalformedLocators()
    {
        var hash = SwiftTransitionManifestFormat.Hash("x");
        Assert.True(SwiftTransitionManifestFormat.IsHash(hash));
        var value = SwiftTransitionManifestFormat.AttributeValue("A.swift-transition-manifest.json", hash);
        Assert.True(SwiftTransitionManifestFormat.TryParseAttributeValue(value, out var schema, out var resource, out var parsed, out _));
        Assert.Equal((SwiftTransitionManifestFormat.Schema, "A.swift-transition-manifest.json", hash), (schema, resource, parsed));
        Assert.False(SwiftTransitionManifestFormat.TryParseAttributeValue("schema=1;resource=A", out _, out _, out _, out _));
        Assert.False(SwiftTransitionManifestFormat.TryParseAttributeValue(value.Replace(hash[7..], "ABC"), out _, out _, out _, out _));
    }

    [Fact]
    public void NativeSymbol_SpellsTheModuleAsAnIdentifier()
    {
        Assert.Equal("SBTM_Swift_Runtime", SwiftTransitionManifestFormat.NativeSymbol("Swift.Runtime"));
        Assert.Equal("SBTM_My_Lib_2", SwiftTransitionManifestFormat.NativeSymbol("My-Lib.2"));
    }

    [Fact]
    public void Parse_RejectsAManifestThatContradictsItself()
    {
        var manifest = JsonNode.Parse(Manifest("Acme", Plans("Acme", ("Acme.C::Prim", "prim"))))!.AsObject();
        manifest["plans"]!["module"] = "Other";
        var ex = Assert.Throws<InvalidDataException>(() => SwiftTransitionManifestFormat.Parse(Encoding.UTF8.GetBytes(manifest.ToJsonString()), "fixture"));
        Assert.Contains("call plans of Other", ex.Message);

        var native = JsonNode.Parse(Manifest("Acme", Plans("Acme", ("Acme.C::Prim", "prim"))))!.AsObject();
        native["native"] = new JsonObject { ["library"] = "lib", ["symbol"] = "SBTM_Acme", ["bridges"] = new JsonArray("SBSR_1") };
        ex = Assert.Throws<InvalidDataException>(() => SwiftTransitionManifestFormat.Parse(Encoding.UTF8.GetBytes(native.ToJsonString()), "fixture"));
        Assert.Contains("lists the bridges [SBSR_1], but its plans route []", ex.Message);
    }

    [Fact]
    public void Parse_AValueOfTheWrongKind_IsInvalidData()
    {
        var manifest = JsonNode.Parse(Manifest("Acme", Plans("Acme", ("Acme.C::Prim", "prim"))))!.AsObject();
        manifest["generator"] = 123;
        var ex = Assert.Throws<InvalidDataException>(() => SwiftTransitionManifestFormat.Parse(Encoding.UTF8.GetBytes(manifest.ToJsonString()), "fixture"));
        Assert.Contains("fixture does not read as a manifest", ex.Message);
    }

    [Fact]
    public void Compose_RefusesBridgesWithoutALibrary()
    {
        var plans = JsonNode.Parse(Plans("Acme", ("Acme.C::Prim", "prim")))!.AsObject();
        plans["coverage"]![0]!["bridged"] = true;
        plans["coverage"]![0]!["symbol"] = "SBSR_1";
        Assert.Throws<InvalidDataException>(() => SwiftTransitionManifestFormat.Compose(
            "Acme", SwiftTransitionManifestFormat.Generated, "test", Contract, null, plans.ToJsonString(), null));
    }

    [Fact]
    public void WithNativeHash_KeepsTheClosingDirectiveLast()
    {
        var plans = JsonNode.Parse(Plans("Acme", ("Acme.C::Prim", "prim")))!.AsObject();
        plans["coverage"]![0]!["bridged"] = true;
        plans["coverage"]![0]!["symbol"] = "SBSR_1";
        var manifest = Assert.IsType<SwiftTransitionManifestWriter.Manifest>(
            SwiftTransitionManifestWriter.Compose("Acme", "Acme", "AcmeWrapper", plans.ToJsonString(), null));
        Assert.True(manifest.RoutesBridges);
        var assembly = SwiftTransitionManifestWriter.WithNativeHash("_SBSR_1:\n  ret\n.subsections_via_symbols\n", manifest);
        Assert.EndsWith(".subsections_via_symbols\n", assembly);
        Assert.Contains($".asciz \"{manifest.Hash}\"", assembly);
        Assert.Contains(".no_dead_strip _SBTM_Acme", assembly);
        // A module that routes nothing carries no hash in native code.
        var unrouted = SwiftTransitionManifestWriter.Compose("Acme", "Acme", "AcmeWrapper", Plans("Acme", ("Acme.C::Prim", "prim")), null);
        Assert.Equal("x\n", SwiftTransitionManifestWriter.WithNativeHash("x\n", unrouted));
    }

    [Fact]
    public void Writer_WritesAndRemovesTheManifestAndItsAttribute()
    {
        var directory = Directory.CreateTempSubdirectory("swift-transition-manifest-").FullName;
        try
        {
            var manifest = SwiftTransitionManifestWriter.Compose("Acme", "Acme", null, Plans("Acme", ("Acme.C::Prim", "prim")), null);
            Assert.True(SwiftTransitionManifestWriter.Write(directory, "Acme", manifest));
            Assert.False(SwiftTransitionManifestWriter.Write(directory, "Acme", manifest));
            var attribute = File.ReadAllText(Path.Combine(directory, "Acme.SwiftTransitionManifest.g.cs"));
            Assert.Contains($"sha256={manifest!.Hash[7..]}", attribute);
            Assert.Equal(manifest.Text, File.ReadAllText(Path.Combine(directory, "Acme.swift-transition-manifest.json")));
            // A module whose plans record no transition has no manifest.
            Assert.Null(SwiftTransitionManifestWriter.Compose("Acme", "Acme", null, Plans("Acme"), null));
            Assert.True(SwiftTransitionManifestWriter.Write(directory, "Acme", null));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- the reader's four states ---------------------------------------------------------------

    internal const string PrimitiveOnly = $$"""
        public static class C
        {
            [DllImport("lib", EntryPoint = "prim")] {{Swift}}
            public static extern long Prim(long n);
        }
        """;

    [Fact]
    public void Reader_PresentManifest_IsValid()
    {
        var evidence = Read(PrimitiveOnly, Plans("Fixture", ("C::Prim", "prim")));
        Assert.Equal(TransitionEvidenceState.PresentValid, evidence.State);
        Assert.Empty(evidence.Problems);
        var manifest = Assert.Single(evidence.Manifests);
        Assert.Equal("Fixture.swift-transition-manifest.json", manifest.Resource);
        Assert.Equal(SwiftTransitionManifestFormat.Hash(manifest.Bytes), manifest.Hash);
    }

    [Fact]
    public void Reader_NoManifest_PrimitiveOnly()
    {
        var evidence = Read(PrimitiveOnly, plans: null);
        Assert.Equal(TransitionEvidenceState.AbsentPrimitiveOnly, evidence.State);
        Assert.Empty(evidence.Manifests);
        Assert.Equal(1, evidence.Census.Inventory.SwiftPInvokesPrimitiveOnly);
    }

    [Theory]
    [InlineData("""
        public static unsafe class C
        {
            [DllImport("lib", EntryPoint = "self")] [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
            public static extern long WithSelf(long n, SwiftSelf self);
        }
        """, "R1")]
    [InlineData("""
        public static class C
        {
            [DllImport("lib", EntryPoint = "SBSR_0011223344556677")]
            public static extern long Twin(long n);
        }
        """, "imports the bridge SBSR_0011223344556677")]
    [InlineData("""
        public static class C
        {
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
            public static void Callback(long n) { }
        }
        """, "Callback")]
    public void Reader_NoManifest_WithNonPrimitiveTransitions(string body, string why)
    {
        var evidence = Read(body, plans: null);
        Assert.Equal(TransitionEvidenceState.AbsentNonPrimitive, evidence.State);
        Assert.Contains(evidence.Problems, p => p.Contains(why, StringComparison.Ordinal));
    }

    [Fact]
    public void Reader_TamperedManifest_IsReported()
    {
        var text = Manifest("Fixture", Plans("Fixture", ("C::Prim", "prim")));
        var tampered = text.Replace("NotBridged", "NotBridgeD", StringComparison.Ordinal);
        var image = Compile(PrimitiveOnly + Locator("Fixture.swift-transition-manifest.json", SwiftTransitionManifestFormat.Hash(text)),
            ("Fixture.swift-transition-manifest.json", Encoding.UTF8.GetBytes(tampered)));
        var evidence = SwiftTransitionManifestReader.Read(image, "Fixture.dll");
        Assert.Equal(TransitionEvidenceState.TamperedOrMismatched, evidence.State);
        Assert.Contains(evidence.Problems, p => p.Contains("hashes to", StringComparison.Ordinal) && p.Contains("its locator states", StringComparison.Ordinal));
    }

    [Fact]
    public void Reader_LocatorWithoutResource_AndResourceWithoutLocator_AreReported()
    {
        var text = Manifest("Fixture", Plans("Fixture", ("C::Prim", "prim")));
        var missing = Compile(PrimitiveOnly + Locator("Fixture.swift-transition-manifest.json", SwiftTransitionManifestFormat.Hash(text)));
        var evidence = SwiftTransitionManifestReader.Read(missing, "Fixture.dll");
        Assert.Equal(TransitionEvidenceState.TamperedOrMismatched, evidence.State);
        Assert.Contains(evidence.Problems, p => p.Contains("which the assembly does not embed", StringComparison.Ordinal));

        var unlocated = Compile(PrimitiveOnly, ("Fixture.swift-transition-manifest.json", Encoding.UTF8.GetBytes(text)));
        evidence = SwiftTransitionManifestReader.Read(unlocated, "Fixture.dll");
        Assert.Equal(TransitionEvidenceState.TamperedOrMismatched, evidence.State);
        Assert.Contains(evidence.Problems, p => p.Contains("has no locator attribute", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../Fixture.swift-transition-manifest.json")]
    [InlineData("/tmp/Fixture.swift-transition-manifest.json")]
    [InlineData("Fixture.json")]
    public void Reader_ResourceNameThatIsNotAFileName_IsReported(string resource)
    {
        // Readers write a manifest's records out under its resource name, so a path must not pass.
        var text = Manifest("Fixture", Plans("Fixture", ("C::Prim", "prim")));
        var image = Compile(PrimitiveOnly + Locator(resource, SwiftTransitionManifestFormat.Hash(text)), (resource, Encoding.UTF8.GetBytes(text)));
        var evidence = SwiftTransitionManifestReader.Read(image, "Fixture.dll");
        Assert.Equal(TransitionEvidenceState.TamperedOrMismatched, evidence.State);
        Assert.Empty(evidence.Manifests);
        Assert.Contains(evidence.Problems, p => p.Contains("which is not a file name", StringComparison.Ordinal));
    }

    [Fact]
    public void Reader_UnknownSchema_IsReported()
    {
        var text = Manifest("Fixture", Plans("Fixture", ("C::Prim", "prim")));
        var locator = $"\n[assembly: System.Reflection.AssemblyMetadataAttribute(\"{SwiftTransitionManifestFormat.AttributeKey}\", " +
                      $"\"schema=2;resource=Fixture.swift-transition-manifest.json;sha256={SwiftTransitionManifestFormat.Hash(text)[7..]}\")]\n";
        var image = Compile(PrimitiveOnly + locator, ("Fixture.swift-transition-manifest.json", Encoding.UTF8.GetBytes(text)));
        var evidence = SwiftTransitionManifestReader.Read(image, "Fixture.dll");
        Assert.Equal(TransitionEvidenceState.TamperedOrMismatched, evidence.State);
        Assert.Contains(evidence.Problems, p => p.Contains("has schema 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Reader_ManifestThatDoesNotCoverTheAssembly_IsAMismatch()
    {
        // The manifest records no transition at "prim", which the assembly imports with the Swift convention.
        var evidence = Read(PrimitiveOnly, Plans("Fixture", ("C::Other", "other")));
        Assert.Equal(TransitionEvidenceState.TamperedOrMismatched, evidence.State);
        Assert.Contains(evidence.Problems, p => p.Contains("imports prim with the Swift convention, which no manifest covers", StringComparison.Ordinal));
    }

    // ---- the package check ----------------------------------------------------------------------

    [Fact]
    public void Package_ValidPrimitiveOnlyAssembly_Passes()
    {
        var image = Compile(PrimitiveOnly + Locator(Plans("Fixture", ("C::Prim", "prim")), out var resource), resource);
        var result = CheckPackage(image, requireRoutedBridges: false);
        Assert.True(result.Passed, string.Join("\n", result.Problems));
    }

    [Fact]
    public void Package_SeededRuleViolation_IsRed()
    {
        // A valid manifest does not excuse a transition the rules refuse: SwiftSelf in a C-convention import (R2).
        var body = PrimitiveOnly + """
            public static class D
            {
                [DllImport("lib", EntryPoint = "cself")]
                public static extern long CSelf(SwiftSelf self);
            }
            """;
        var image = Compile(body + Locator(Plans("Fixture", ("C::Prim", "prim")), out var resource), resource);
        var result = CheckPackage(image, requireRoutedBridges: false);
        Assert.False(result.Passed);
        Assert.Contains(result.Problems, p => p.Contains("R2 D::CSelf", StringComparison.Ordinal));
    }

    [Fact]
    public void Package_TamperedManifest_IsRed()
    {
        var text = Manifest("Fixture", Plans("Fixture", ("C::Prim", "prim")));
        var image = Compile(PrimitiveOnly + Locator("Fixture.swift-transition-manifest.json", SwiftTransitionManifestFormat.Hash(text + " ")),
            ("Fixture.swift-transition-manifest.json", Encoding.UTF8.GetBytes(text)));
        var result = CheckPackage(image, requireRoutedBridges: false);
        Assert.False(result.Passed);
        Assert.Contains(result.Problems, p => p.Contains("transition evidence is TamperedOrMismatched", StringComparison.Ordinal));
    }

    [Fact]
    public void Package_MissingManifestBesideBridgedTransitions_IsRed()
    {
        var image = Compile("""
            public static class C
            {
                [DllImport("lib", EntryPoint = "SBSR_0011223344556677")]
                public static extern long Twin(long n);
            }
            """);
        var result = CheckPackage(image, requireRoutedBridges: false);
        Assert.False(result.Passed);
        Assert.Contains(result.Problems, p => p.Contains("transition evidence is AbsentNonPrimitive", StringComparison.Ordinal));
    }

    [Fact]
    public void Package_ThatMustRouteBridgesButRoutesNone_IsRed()
    {
        // The vacuity control: a valid manifest that routes nothing, in a package whose bridges are its evidence.
        var image = Compile(PrimitiveOnly + Locator(Plans("Fixture", ("C::Prim", "prim")), out var resource), resource);
        var result = CheckPackage(image, requireRoutedBridges: true);
        Assert.False(result.Passed);
        Assert.Contains(result.Problems, p => p.Contains("routes no transition through a bridge, but this package must", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Contains("holds no native image", StringComparison.Ordinal));
    }

    // ---- the runtime as built and committed -----------------------------------------------------

    [Fact]
    public void Runtime_EmbedsAValidManifestThatRoutesItsBridges()
    {
        var evidence = SwiftTransitionManifestReader.Read(RuntimeDll, new SwiftTransitionScanOptions { ReferenceDirectories = [AppContext.BaseDirectory] });
        Assert.True(evidence.State == TransitionEvidenceState.PresentValid, string.Join("\n", evidence.Problems));
        var manifest = Assert.Single(evidence.Manifests).Manifest;
        Assert.Equal("Swift.Runtime", manifest.Module);
        Assert.Equal(SwiftTransitionManifestFormat.HandWritten, manifest.Origin);
        Assert.Equal("SwiftBindingsRuntime", manifest.NativeLibrary);
        Assert.Equal(4, manifest.Bridges.Length);
        Assert.Equal(4, evidence.Census.Inventory.BridgeTwins);
    }

    [Fact]
    public void Runtime_EveryCommittedSliceHoldsTheManifestHash()
    {
        var evidence = SwiftTransitionManifestReader.Read(RuntimeDll, new SwiftTransitionScanOptions { ReferenceDirectories = [AppContext.BaseDirectory] });
        var hash = Assert.Single(evidence.Manifests).Hash;
        var slices = RuntimeSlices.ToList();
        Assert.Equal(6, slices.Count);
        var architectures = 0;
        foreach (var slice in slices)
        {
            var image = File.ReadAllBytes(slice);
            Assert.Empty(SwiftTransitionManifestReader.CheckNative(evidence, "SwiftBindingsRuntime", image, slice));
            foreach (var arch in MachOSymbols.Read(image))
            {
                architectures++;
                Assert.Equal(hash, arch.CString("SBTM_Swift_Runtime"));
                Assert.Equal(4, arch.Exports.Count(e => e.StartsWith("SBSR_", StringComparison.Ordinal)));
            }
        }
        Assert.Equal(10, architectures);
    }

    [Fact]
    public void Runtime_NativeOfAnotherManifest_IsRed()
    {
        var evidence = SwiftTransitionManifestReader.Read(RuntimeDll, new SwiftTransitionScanOptions { ReferenceDirectories = [AppContext.BaseDirectory] });
        var hash = Assert.Single(evidence.Manifests).Hash;
        var image = File.ReadAllBytes(RuntimeSlices.First());
        var patched = Replace(image, Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(SwiftTransitionManifestFormat.Hash("another manifest")));
        var problems = SwiftTransitionManifestReader.CheckNative(evidence, "SwiftBindingsRuntime", patched, "patched");
        Assert.Contains(problems, p => p.Contains($"SBTM_Swift_Runtime holds {SwiftTransitionManifestFormat.Hash("another manifest")}", StringComparison.Ordinal));
        // A library the manifests do not route through must export no bridge.
        Assert.Contains(SwiftTransitionManifestReader.CheckNative(evidence, "SBApple", image, "other"),
            p => p.Contains("which no manifest of Swift.Runtime routes through SBApple", StringComparison.Ordinal));
    }

    [Fact]
    public void Runtime_TamperedOrHiddenManifest_IsRed()
    {
        var image = File.ReadAllBytes(RuntimeDll);
        var options = new SwiftTransitionScanOptions { ReferenceDirectories = [AppContext.BaseDirectory] };

        var tampered = Replace(image, "\"NotBridged\""u8.ToArray(), "\"NotBridgeD\""u8.ToArray(), all: false);
        Assert.Equal(TransitionEvidenceState.TamperedOrMismatched, SwiftTransitionManifestReader.Read(tampered, "tampered", options).State);

        var hidden = Replace(Replace(image, Encoding.UTF8.GetBytes(SwiftTransitionManifestFormat.AttributeKey), "SwiftBindings.TransitionManifesX"u8.ToArray()),
            Encoding.UTF8.GetBytes(SwiftTransitionManifestFormat.FileSuffix), ".swift-transition-manifesX.json"u8.ToArray());
        var evidence = SwiftTransitionManifestReader.Read(hidden, "hidden", options);
        Assert.Equal(TransitionEvidenceState.AbsentNonPrimitive, evidence.State);
        Assert.Contains(evidence.Problems, p => p.Contains("imports the bridge SBSR_", StringComparison.Ordinal));
    }

    [Fact]
    public void Runtime_PackageCheck_PassesAndARenamedTwinBreaksR3()
    {
        var image = File.ReadAllBytes(RuntimeDll);
        var natives = RuntimeSlices.Select(p => new PackagedNative("SwiftBindingsRuntime", p, File.ReadAllBytes(p))).ToList();
        var scratch = Directory.CreateTempSubdirectory("swift-package-evidence-").FullName;
        try
        {
            var assembly = new PackagedAssembly(RuntimeDll, image, [AppContext.BaseDirectory]);
            var result = PackageTransitionEvidence.Check([assembly], natives, requireRoutedBridges: true, Path.Combine(scratch, "real"));
            Assert.True(result.Passed, string.Join("\n", result.Problems));

            // An image of another library leaves the routed library unchecked, which is red.
            var unrelated = PackageTransitionEvidence.Check([assembly], [natives[0] with { Library = "Other" }], requireRoutedBridges: true, Path.Combine(scratch, "unrelated"));
            Assert.Contains(unrelated.Problems, p => p.Contains("route bridges through SwiftBindingsRuntime, but the package holds no image of it", StringComparison.Ordinal));

            // A twin renamed outside the manifest: the plan it names is withheld, and R3 refuses it.
            var manifest = Assert.Single(result.Evidence).Manifests[0];
            var at = image.AsSpan().IndexOf(manifest.Bytes);
            var twin = Encoding.UTF8.GetBytes(manifest.Manifest.Bridges[0]);
            var renamed = (byte[])twin.Clone();
            renamed[^1] = (byte)(renamed[^1] == (byte)'0' ? '1' : '0');
            var seeded = (byte[])image.Clone();
            for (var i = seeded.AsSpan().IndexOf(twin); i >= 0;)
            {
                if (i < at || i >= at + manifest.Bytes.Length)
                    renamed.CopyTo(seeded, i);
                var next = seeded.AsSpan(i + 1).IndexOf(twin);
                i = next < 0 ? -1 : i + 1 + next;
            }
            var red = PackageTransitionEvidence.Check([assembly with { Image = seeded }], natives, requireRoutedBridges: true, Path.Combine(scratch, "seeded"));
            Assert.False(red.Passed);
            Assert.Contains(red.Problems, p => p.Contains(" R3 ", StringComparison.Ordinal) && p.Contains(Encoding.UTF8.GetString(renamed), StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    // ---- fixtures -------------------------------------------------------------------------------

    internal static (int, int, int) Contract => (SwiftCallPlan.Version, SwiftCallFacts.CurrentVersion, SwiftLoweringContract.Current.CollectorVersion);

    /// <summary>A call-plan document of <paramref name="module"/> recording each (member, entry point) as a primitive-only forward transition.</summary>
    internal static string Plans(string module, params (string Member, string EntryPoint)[] transitions) =>
        new JsonObject
        {
            ["planVersion"] = SwiftCallPlan.Version,
            ["factsVersion"] = SwiftCallFacts.CurrentVersion,
            ["module"] = module,
            ["coverage"] = new JsonArray(transitions.Select(t => (JsonNode?)new JsonObject
            {
                ["transition"] = "ForwardPInvoke",
                ["member"] = t.Member,
                ["entryPoint"] = t.EntryPoint,
                ["status"] = "NotBridged",
                ["reason"] = "primitive-only",
                ["refusals"] = new JsonArray(),
                ["executable"] = false,
            }).ToArray()),
            ["plans"] = new JsonArray(),
        }.ToJsonString();

    internal static string Manifest(string module, string plans) =>
        SwiftTransitionManifestFormat.Compose(module, SwiftTransitionManifestFormat.Generated, "test", Contract, null, plans, null);

    internal static string Locator(string resource, string hash) =>
        "\n" + SwiftTransitionManifestFormat.AttributeSource([(resource, hash)]);

    internal static string Locator(string plans, out (string, byte[]) resource)
    {
        var text = Manifest("Fixture", plans);
        resource = ("Fixture.swift-transition-manifest.json", Encoding.UTF8.GetBytes(text));
        return Locator(resource.Item1, SwiftTransitionManifestFormat.Hash(text));
    }

    private static TransitionEvidence Read(string body, string? plans)
    {
        var image = plans is null ? Compile(body) : Compile(body + Locator(plans, out var resource), resource);
        return SwiftTransitionManifestReader.Read(image, "Fixture.dll");
    }

    private static PackageTransitionEvidenceResult CheckPackage(byte[] image, bool requireRoutedBridges)
    {
        var scratch = Directory.CreateTempSubdirectory("swift-package-evidence-").FullName;
        try
        {
            return PackageTransitionEvidence.Check([new PackagedAssembly("Fixture.dll", image, [])], [], requireRoutedBridges, scratch);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    internal static byte[] Compile(string body, params (string Name, byte[] Bytes)[] resources) => Compile("Fixture", body, resources);

    internal static byte[] Compile(string assemblyName, string body, params (string Name, byte[] Bytes)[] resources)
    {
        var frameworkDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p) && p.StartsWith(frameworkDir, StringComparison.Ordinal))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        // A locator is an assembly attribute, which C# wants ahead of the declarations: its own file.
        var lines = body.Split('\n');
        var locators = string.Join("\n", lines.Where(l => l.Contains("AssemblyMetadataAttribute", StringComparison.Ordinal)));
        var declarations = string.Join("\n", lines.Where(l => !l.Contains("AssemblyMetadataAttribute", StringComparison.Ordinal)));
        var options = new CSharpParseOptions(LanguageVersion.Latest);
        var compilation = CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText(Preamble + declarations, options), CSharpSyntaxTree.ParseText(locators, options)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream,
            manifestResources: resources.Select(r => new ResourceDescription(r.Name, () => new MemoryStream(r.Bytes), isPublic: true)));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return stream.ToArray();
    }

    /// <summary><paramref name="image"/> with <paramref name="from"/> replaced by the same-length <paramref name="to"/> (every occurrence, or the first).</summary>
    internal static byte[] Replace(byte[] image, byte[] from, byte[] to, bool all = true)
    {
        Assert.Equal(from.Length, to.Length);
        var copy = (byte[])image.Clone();
        var found = 0;
        for (var at = copy.AsSpan().IndexOf(from); at >= 0;)
        {
            to.CopyTo(copy, at);
            found++;
            if (!all)
                break;
            var next = copy.AsSpan(at + from.Length).IndexOf(from);
            at = next < 0 ? -1 : at + from.Length + next;
        }
        Assert.True(found > 0, $"the image holds no {Encoding.UTF8.GetString(from)}");
        return copy;
    }
}
