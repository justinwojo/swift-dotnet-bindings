// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text;
using SwiftBindings.TransitionGate;
using Xunit;
using static BindingsGeneration.Tests.SwiftTransitionManifestTests;

namespace BindingsGeneration.Tests;

/// <summary>
/// The consumer check's engine (<see cref="ConsumerTransitionCheck"/>): the metadata prefilter that
/// keeps an app's ordinary references unread, and the verdict for each evidence state, including a
/// valid manifest whose rules fail and a routed library whose native image does not match. The
/// fixtures are Roslyn-compiled assemblies and the runtime as built and committed.
/// </summary>
public class ConsumerTransitionCheckTests
{
    private const string UntypedSelf = $$"""
        public static unsafe class Counter
        {
            [DllImport("lib", EntryPoint = "$s4Lib7CounterC5countSivg")] {{SwiftTransitionManifestTests.Swift}}
            private static extern nint CountOf(SwiftSelf self);
        }
        """;

    // ---- the prefilter --------------------------------------------------------------------------

    [Fact]
    public void Prefilter_SkipsAnAssemblyWithNoSwiftSurface()
    {
        var image = Compile("""
            public static class Plain
            {
                [DllImport("libc", EntryPoint = "getpid")]
                public static extern int GetPid();
                public static int Twice(int x) => x * 2;
            }
            """);
        Assert.False(ConsumerTransitionCheck.MayHaveSwiftTransitions(image));
    }

    [Theory]
    [InlineData(SwiftTransitionManifestTests.PrimitiveOnly)]
    [InlineData(UntypedSelf)]
    [InlineData("""
        public static class C
        {
            [DllImport("lib", EntryPoint = "SBSR_0011223344556677")]
            public static extern long Twin(long n);
        }
        """)]
    [InlineData("""
        public static unsafe class C
        {
            public static long Call(delegate* unmanaged[Swift]<long, long> f, long n) => f(n);
        }
        """)]
    public void Prefilter_KeepsEverySwiftSurface(string body) =>
        Assert.True(ConsumerTransitionCheck.MayHaveSwiftTransitions(Compile(body)));

    [Fact]
    public void Prefilter_KeepsTheRuntimeAndAnImageThatDoesNotRead()
    {
        Assert.True(ConsumerTransitionCheck.MayHaveSwiftTransitions(File.ReadAllBytes(RuntimeDll)));
        // An image that does not read stays a candidate, so the check reports why instead of passing it.
        Assert.True(ConsumerTransitionCheck.MayHaveSwiftTransitions(Encoding.ASCII.GetBytes("not an assembly")));
    }

    // ---- verdicts -------------------------------------------------------------------------------

    [Fact]
    public void NoManifest_UntypedSwiftSelf_IsRefused()
    {
        var result = Check(Compile(UntypedSelf));
        Assert.Equal(ConsumerTransitionVerdict.Refused, result.Verdict);
        Assert.Equal(TransitionEvidenceState.AbsentNonPrimitive, result.State);
        var problem = Assert.Single(result.Problems);
        Assert.Contains("Counter::CountOf", problem);
        Assert.Contains("R1", problem);
    }

    [Fact]
    public void NoManifest_PrimitiveOnly_IsAccepted()
    {
        var result = Check(Compile(PrimitiveOnly));
        Assert.Equal(ConsumerTransitionVerdict.PrimitiveOnly, result.Verdict);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void ValidManifest_WhoseRulesHold_IsVerified()
    {
        var result = Check(Compile(PrimitiveOnly + Locator(Plans("Fixture", ("C::Prim", "prim")), out var resource), resource));
        Assert.Equal(ConsumerTransitionVerdict.Verified, result.Verdict);
        Assert.Equal(TransitionEvidenceState.PresentValid, result.State);
        Assert.Empty(result.Problems);
        Assert.Empty(result.UnverifiedLibraries);
    }

    [Fact]
    public void ValidManifest_WhoseRulesFail_IsRefused_AndItsProblemsDropThePath()
    {
        // A valid manifest proves authenticity, not compliance: SwiftSelf in a C-convention import (R2).
        var body = PrimitiveOnly + """
            public static class D
            {
                [DllImport("lib", EntryPoint = "cself")]
                public static extern long CSelf(SwiftSelf self);
            }
            """;
        var result = Check(Compile(body + Locator(Plans("Fixture", ("C::Prim", "prim")), out var resource), resource));
        Assert.Equal(ConsumerTransitionVerdict.Refused, result.Verdict);
        Assert.Equal(TransitionEvidenceState.PresentValid, result.State);
        Assert.Contains(result.Problems, p => p.Contains("R2 D::CSelf", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Problems, p => p.StartsWith("Fixture.dll: ", StringComparison.Ordinal));
    }

    [Fact]
    public void TamperedManifest_IsRefused()
    {
        var text = Manifest("Fixture", Plans("Fixture", ("C::Prim", "prim")));
        var image = Compile(PrimitiveOnly + Locator("Fixture.swift-transition-manifest.json", SwiftTransitionManifestFormat.Hash(text + " ")),
            ("Fixture.swift-transition-manifest.json", Encoding.UTF8.GetBytes(text)));
        var result = Check(image);
        Assert.Equal(ConsumerTransitionVerdict.Refused, result.Verdict);
        Assert.Equal(TransitionEvidenceState.TamperedOrMismatched, result.State);
        Assert.Contains(result.Problems, p => p.Contains("hashes to", StringComparison.Ordinal));
    }

    // ---- the runtime and its native images ------------------------------------------------------

    [Fact]
    public void Runtime_WithItsSlices_IsVerified()
    {
        var natives = RuntimeSlices.Select(p => new PackagedNative("SwiftBindingsRuntime", p, File.ReadAllBytes(p))).ToList();
        var result = Check(File.ReadAllBytes(RuntimeDll), natives, requireNativeImages: true, path: RuntimeDll);
        Assert.True(result.Verdict == ConsumerTransitionVerdict.Verified, string.Join("\n", result.Problems));
        Assert.Empty(result.UnverifiedLibraries);
    }

    [Fact]
    public void Runtime_WithoutItsSlices_IsUnverified_AndRefusedOnlyWhereImagesAreRequired()
    {
        var consumer = Check(File.ReadAllBytes(RuntimeDll), [], requireNativeImages: false, path: RuntimeDll);
        Assert.Equal(ConsumerTransitionVerdict.Verified, consumer.Verdict);
        Assert.Equal("SwiftBindingsRuntime", Assert.Single(consumer.UnverifiedLibraries));

        var package = Check(File.ReadAllBytes(RuntimeDll), [], requireNativeImages: true, path: RuntimeDll);
        Assert.Equal(ConsumerTransitionVerdict.Refused, package.Verdict);
        Assert.Contains(package.Problems, p => p.Contains("no image of SwiftBindingsRuntime was found", StringComparison.Ordinal));
    }

    [Fact]
    public void Runtime_WithASliceOfAnotherManifest_IsRefused()
    {
        // The stale-wrapper shape: a native image that holds the hash of an earlier manifest.
        var hash = SwiftTransitionManifestReader.Read(RuntimeDll, new SwiftTransitionScanOptions { ReferenceDirectories = [AppContext.BaseDirectory] }).Manifests.Single().Hash;
        var slice = RuntimeSlices.First();
        var stale = Replace(File.ReadAllBytes(slice), Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(SwiftTransitionManifestFormat.Hash("an earlier manifest")));
        var result = Check(File.ReadAllBytes(RuntimeDll), [new PackagedNative("SwiftBindingsRuntime", slice, stale)], requireNativeImages: false, path: RuntimeDll);
        Assert.Equal(ConsumerTransitionVerdict.Refused, result.Verdict);
        Assert.Contains(result.Problems, p => p.Contains("SBTM_Swift_Runtime holds", StringComparison.Ordinal));
    }

    [Fact]
    public void NativeReferenceImages_ReadsOnlyTheLibraryANativeReferenceIsNamedAfter()
    {
        var slice = RuntimeSlices.First();
        var framework = slice[..(slice.IndexOf("SwiftBindingsRuntime.framework", StringComparison.Ordinal) + "SwiftBindingsRuntime.framework".Length)];
        Assert.Equal([slice], ConsumerTransitionCheck.NativeReferenceImages(framework, "SwiftBindingsRuntime"));
        var xcframework = Path.Combine(SwiftCallPlanTests.RepoRoot, "src", "Swift.Runtime", "native", "SwiftBindingsRuntime.xcframework");
        Assert.Equal(RuntimeSlices.Count(), ConsumerTransitionCheck.NativeReferenceImages(xcframework + "/", "SwiftBindingsRuntime").Count());
        Assert.Empty(ConsumerTransitionCheck.NativeReferenceImages(xcframework, "Other"));

        var directory = Directory.CreateTempSubdirectory("consumer-native-").FullName;
        try
        {
            var dylib = Path.Combine(directory, "libAcme.dylib");
            File.WriteAllBytes(dylib, [0]);
            Assert.Equal([dylib], ConsumerTransitionCheck.NativeReferenceImages(dylib, "Acme"));
            Assert.Empty(ConsumerTransitionCheck.NativeReferenceImages(dylib, "Other"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- fixtures -------------------------------------------------------------------------------

    private static ConsumerTransitionResult Check(byte[] image) => Check(image, [], requireNativeImages: false, path: "Fixture.dll");

    private static ConsumerTransitionResult Check(byte[] image, System.Collections.Generic.IReadOnlyList<PackagedNative> natives, bool requireNativeImages, string path)
    {
        var scratch = Directory.CreateTempSubdirectory("consumer-transition-").FullName;
        try
        {
            var references = path == "Fixture.dll" ? Array.Empty<string>() : [AppContext.BaseDirectory];
            return ConsumerTransitionCheck.Check(new PackagedAssembly(path, image, references), natives, requireNativeImages, scratch);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }
}
