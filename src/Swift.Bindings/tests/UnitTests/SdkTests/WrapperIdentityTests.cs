// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SwiftBindings.TransitionGate;
using Xunit;
using static BindingsGeneration.Tests.SwiftTransitionManifestTests;

namespace BindingsGeneration.Tests;

/// <summary>
/// The wrapper identity (<see cref="SwiftWrapperIdentityFormat"/>): what generation stamps on the
/// managed binding and its native wrapper, and the check that refuses a wrapper of another copy or
/// build (<see cref="SwiftWrapperIdentityCheck"/>, run by SWIFTBIND132 and SWIFTBIND133).
/// </summary>
public sealed class WrapperIdentityTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("swift-wrapper-identity-").FullName;

    public void Dispose() => Directory.Delete(tempDir, recursive: true);

    internal static SwiftWrapperIdentity Identity(string copy, string sdk = "26.2", string source = "struct S {}") =>
        new("Kit", "Kit", copy, sdk, "0.21.0", SwiftWrapperIdentityFormat.SourceDigest(source));

    // ---- the spelling ---------------------------------------------------------------------------

    [Fact]
    public void Spelling_RoundTrips_InItsFixedFieldOrder()
    {
        var identity = Identity("net10.0-ios26.0");
        Assert.StartsWith("schema=1;module=Kit;library=Kit;copy=net10.0-ios26.0;sdk=26.2;generator=0.21.0;source=sha256:", identity.Value);
        Assert.Equal(identity, SwiftWrapperIdentityFormat.Parse(identity.Value, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("schema=2;module=Kit;library=Kit;copy=c;sdk=1;generator=g;source=s")]
    [InlineData("schema=1;library=Kit;module=Kit;copy=c;sdk=1;generator=g;source=s")]
    [InlineData("schema=1;module=Kit;library=Kit;copy=c;sdk=1;generator=g")]
    [InlineData("schema=1;module=Kit;library=Kit;copy=c;sdk=;generator=g;source=s")]
    [InlineData("schema=1;module=Kit;library=Kit;copy=c;sdk=1;generator=g;source=s;extra=x")]
    public void Spelling_RefusesAnythingElse(string? value)
    {
        Assert.Null(SwiftWrapperIdentityFormat.Parse(value, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("a;b")]
    [InlineData("a=b")]
    [InlineData("a\"b")]
    [InlineData("a\\b")]
    [InlineData("a\nb")]
    [InlineData(" ")]
    public void Spelling_RefusesAFieldItCouldNotReadBack(string sdk) =>
        Assert.Throws<ArgumentException>(() => Identity("net10.0-ios26.0", sdk).Value);

    [Fact]
    public void NativeSymbol_IsAnAssemblerIdentifier() =>
        Assert.Equal("SBWI_Kit_Core_2", SwiftWrapperIdentityFormat.NativeSymbol("Kit.Core-2"));

    // ---- what generation writes -----------------------------------------------------------------

    [Fact]
    public void Emission_WritesOneValueToBothFiles_AndRemovesThemWithoutAContext()
    {
        var saved = WrapperIdentityEmission.Current;
        try
        {
            WrapperIdentityEmission.Current = new WrapperIdentityContext("net10.0-ios27.2", "27.2", "0.21.0");
            var attribute = WrapperIdentityEmission.Write(tempDir, "Acme.Kit", "Kit", "AcmeKitSwiftBindings", "public func f() {}");
            var expected = new SwiftWrapperIdentity("Kit", "AcmeKitSwiftBindings", "net10.0-ios27.2", "27.2", "0.21.0",
                SwiftWrapperIdentityFormat.SourceDigest("public func f() {}")).Value;
            Assert.NotNull(attribute);
            Assert.Contains($"\"{SwiftWrapperIdentityFormat.AttributeKey}\", \"{expected}\"", attribute);
            Assert.Equal(attribute, File.ReadAllText(Path.Combine(tempDir, "Acme.Kit.WrapperIdentity.g.cs")));
            Assert.Contains($".asciz \"{expected}\"", File.ReadAllText(Path.Combine(tempDir, "Acme.Kit.WrapperIdentity.s")));

            // Another wrapper source is another identity.
            var changed = WrapperIdentityEmission.Write(tempDir, "Acme.Kit", "Kit", "AcmeKitSwiftBindings", "public func g() {}");
            Assert.NotEqual(attribute, changed);

            // A module with no wrapper source, or a run that cannot name its copy, keeps no stale identity.
            Assert.Null(WrapperIdentityEmission.Write(tempDir, "Acme.Kit", "Kit", "AcmeKitSwiftBindings", ""));
            Assert.Empty(Directory.GetFiles(tempDir, "Acme.Kit.WrapperIdentity.*"));
            WrapperIdentityEmission.Write(tempDir, "Acme.Kit", "Kit", "AcmeKitSwiftBindings", "public func f() {}");
            WrapperIdentityEmission.Current = null;
            Assert.Null(WrapperIdentityEmission.Write(tempDir, "Acme.Kit", "Kit", "AcmeKitSwiftBindings", "public func f() {}"));
            Assert.Empty(Directory.GetFiles(tempDir, "Acme.Kit.WrapperIdentity.*"));
        }
        finally
        {
            WrapperIdentityEmission.Current = saved;
        }
    }

    [Theory]
    [InlineData(0, "26.2\n", "26.2")]
    [InlineData(0, "", null)]
    [InlineData(0, "xcrun: error: SDK \"iphoneos\" cannot be located", null)]
    [InlineData(1, "26.2", null)]
    public void Context_RecordsTheCopyAndTheSdkTheSurfaceIsGeneratedFrom(int exit, string stdout, string? sdk)
    {
        var platform = PlatformInfoFactory.Create(ApplePlatform.iOS, null, "net10.0-ios26.0");
        var runner = new Runner(exit, stdout);
        var context = BindingsGeneratorCommand.ResolveWrapperIdentityContext(platform, runner, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.Equal("--sdk iphoneos --show-sdk-version", runner.Arguments);
        if (sdk is null)
            Assert.Null(context);
        else
        {
            Assert.Equal("net10.0-ios26.0", context!.Copy);
            Assert.Equal(sdk, context.Sdk);
            Assert.DoesNotContain("+", context.Generator);
        }
    }

    private sealed class Runner(int exit, string stdout) : ICommandRunner
    {
        public string? Arguments { get; private set; }

        public (int ExitCode, string StdOut, string StdErr) Run(string command, string arguments, int timeoutMs = 30000)
        {
            Arguments = arguments;
            return (exit, stdout, "");
        }
    }

    [Fact]
    public void ThunkCompiler_TakesTheIdentityFile_ForEveryArchitecture_ButNotAsAThunk()
    {
        File.WriteAllText(Path.Combine(tempDir, "Acme.Kit.WrapperIdentity.s"), "");
        File.WriteAllText(Path.Combine(tempDir, "Acme.Kit.Thunks.arm64.s"), "");
        Assert.Equal(["Acme.Kit.WrapperIdentity.s"], NativeThunkCompiler.CollectIdentityFiles(tempDir).Select(Path.GetFileName));
        Assert.DoesNotContain(NativeThunkCompiler.CollectAssemblyFiles(tempDir, "arm64"), f => f.EndsWith(".WrapperIdentity.s", StringComparison.Ordinal));
        Assert.DoesNotContain(NativeThunkCompiler.CollectAssemblyFiles(tempDir, "x86_64"), f => f.EndsWith(".WrapperIdentity.s", StringComparison.Ordinal));
        Assert.True(NativeThunkCompiler.IsIdentityObject(Path.Combine(tempDir, "arm64", "Acme.Kit.WrapperIdentity.o")));
        Assert.False(NativeThunkCompiler.IsIdentityObject(Path.Combine(tempDir, "arm64", "Acme.Kit.Thunks.arm64.o")));
    }

    [Theory]
    [InlineData("arm64")]
    [InlineData("x86_64")]
    public void NativeStamp_LinksAsAnExportHoldingTheValue_AndReferencesNothing(string architecture)
    {
        var identity = Identity("net10.0-ios27.2");
        var source = Path.Combine(tempDir, "Kit.WrapperIdentity.s");
        File.WriteAllText(source, SwiftWrapperIdentityFormat.NativeAssembly(identity.Module, identity.Value));
        var obj = Path.Combine(tempDir, "Kit.WrapperIdentity.o");
        Run("xcrun", "clang", "-arch", architecture, "-c", source, "-o", obj);
        // The thunk weak-link gate assembles every emitted .s and fails on a strong undefined symbol.
        Assert.Equal("", Run("xcrun", "nm", "-u", obj).Trim());

        // Dead stripping keeps it though nothing references it.
        var dylib = Path.Combine(tempDir, "Kit");
        Run("xcrun", "clang", "-arch", architecture, "-dynamiclib", "-Wl,-dead_strip", obj, "-o", dylib);
        var slice = Assert.Single(MachOSymbols.Read(File.ReadAllBytes(dylib)));
        Assert.Equal(identity.Value, slice.CString(SwiftWrapperIdentityFormat.NativeSymbol("Kit")));
    }

    // ---- the check ------------------------------------------------------------------------------

    [Fact]
    public void Check_AcceptsTheWrapperGeneratedWithTheAssembly_AndRefusesAnyOther()
    {
        var own = Identity("net10.0-ios27.2", "27.2");
        var assembly = new PackagedAssembly("lib/net10.0-ios27.2/Kit.dll", StampedAssembly(own), [AppContext.BaseDirectory], "net10.0-ios27.2");
        ConsumerTransitionResult CheckWith(params PackagedNative[] natives) =>
            ConsumerTransitionCheck.Check(assembly, natives, requireNativeImages: false, Path.Combine(tempDir, "scratch"));

        var accepted = CheckWith(Native("own", own));
        Assert.Equal(ConsumerTransitionVerdict.PrimitiveOnly, accepted.Verdict);
        Assert.Empty(accepted.Problems);

        // NEG-C: the older copy's wrapper under the newer copy's assembly.
        var swapped = CheckWith(Native("older", Identity("net10.0-ios26.0", "26.2")));
        Assert.Equal(ConsumerTransitionVerdict.Refused, swapped.Verdict);
        Assert.Equal(ConsumerTransitionRemedy.WrapperMismatch, swapped.Remedy);
        var problem = Assert.Single(swapped.Problems);
        Assert.Contains("is not the wrapper Kit was generated with: it was generated as copy net10.0-ios26.0 from the 26.2 SDK", problem);
        Assert.Contains("the assembly as copy net10.0-ios27.2 from the 27.2 SDK", problem);

        // A wrapper of another build of the same copy (its source differs).
        Assert.Equal(ConsumerTransitionVerdict.Refused, CheckWith(Native("rebuilt", Identity("net10.0-ios27.2", "27.2", "struct T {}"))).Verdict);

        // A wrapper that carries no identity (a generator before identities, or a hand-built one).
        var unstamped = CheckWith(new PackagedNative("Kit", "native/Kit", Dylib(tempDir, "unstamped", "int kit_a(void){return 1;}")));
        Assert.Equal(ConsumerTransitionVerdict.Refused, unstamped.Verdict);
        Assert.Contains("does not export SBWI_Kit", Assert.Single(unstamped.Problems));

        // Every slice is held to it: one good slice does not pass a bad one.
        Assert.Equal(ConsumerTransitionVerdict.Refused, CheckWith(Native("own", own), Native("older", Identity("net10.0-ios26.0"))).Verdict);

        // No image of the wrapper to check: the app's load is what fails then.
        Assert.Equal(ConsumerTransitionVerdict.PrimitiveOnly, CheckWith().Verdict);
    }

    [Fact]
    public void Check_AnAssemblyWithoutAnIdentity_IsNotHeldToOne()
    {
        var assembly = new PackagedAssembly("lib/net10.0-ios26.0/Kit.dll", Compile("Kit", PrimitiveOnly), [AppContext.BaseDirectory]);
        var result = ConsumerTransitionCheck.Check(assembly, [Native("older", Identity("net10.0-ios26.0"))], requireNativeImages: false, Path.Combine(tempDir, "scratch"));
        Assert.Equal(ConsumerTransitionVerdict.PrimitiveOnly, result.Verdict);
    }

    [Fact]
    public void Read_RefusesAnIdentityItCannotRead_AndTwoForOneModule()
    {
        var one = Identity("net10.0-ios26.0");
        var two = Identity("net10.0-ios27.2");
        var duplicated = SwiftWrapperIdentityCheck.Read(Compile("Kit", Attribute(one.Value) + Attribute(two.Value) + PrimitiveOnly));
        Assert.Contains(duplicated.Problems, p => p.Contains("more than one wrapper identity for Kit", StringComparison.Ordinal));
        var unreadable = SwiftWrapperIdentityCheck.Read(Compile("Kit", Attribute("schema=9") + PrimitiveOnly));
        Assert.Contains(unreadable.Problems, p => p.Contains("has schema 9", StringComparison.Ordinal) || p.Contains("does not have the fields", StringComparison.Ordinal));
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static string Attribute(string value) =>
        SwiftWrapperIdentityFormat.AttributeSource(value) + "\n";

    /// <summary>A primitive-only binding assembly that declares <paramref name="identity"/>.</summary>
    internal static byte[] StampedAssembly(SwiftWrapperIdentity identity, string name = "Kit") =>
        Compile(name, Attribute(identity.Value) + PrimitiveOnly);

    private PackagedNative Native(string name, SwiftWrapperIdentity identity) =>
        new("Kit", $"native/{name}/Kit.framework/Kit", StampedDylib(tempDir, name, identity));

    /// <summary>An arm64 wrapper binary exporting <paramref name="identity"/> and one C function.</summary>
    internal static byte[] StampedDylib(string directory, string name, SwiftWrapperIdentity identity)
    {
        var source = Path.Combine(directory, name + ".WrapperIdentity.s");
        File.WriteAllText(source, SwiftWrapperIdentityFormat.NativeAssembly(identity.Module, identity.Value));
        var c = Path.Combine(directory, name + ".c");
        File.WriteAllText(c, "int kit_a(void){return 1;}");
        var output = Path.Combine(directory, name + ".dylib");
        Run("xcrun", "clang", "-arch", "arm64", "-dynamiclib", c, source, "-o", output);
        return File.ReadAllBytes(output);
    }

    /// <summary>An arm64 dylib exporting the C functions of <paramref name="source"/>.</summary>
    internal static byte[] Dylib(string directory, string name, string source)
    {
        var c = Path.Combine(directory, name + ".c");
        File.WriteAllText(c, source);
        var output = Path.Combine(directory, name + ".dylib");
        Run("xcrun", "clang", "-arch", "arm64", "-dynamiclib", c, "-o", output);
        return File.ReadAllBytes(output);
    }

    private static string Run(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{file} {string.Join(" ", arguments)}: {error}");
        return output.GetAwaiter().GetResult();
    }
}
