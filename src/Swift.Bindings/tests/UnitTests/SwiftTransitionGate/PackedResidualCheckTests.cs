// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SwiftBindings.TransitionGate;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The packed residual-guard check (<see cref="PackedResidualCheck"/>), which <c>nuke pack</c> runs over
/// every <c>lib/&lt;framework&gt;/</c> copy of the runtime and the Apple supplement: an unguarded
/// known-unsafe transition in any packed copy fails it, a guarded one passes, and the sidecar
/// tripwire sees the packed copy, not another build.
/// </summary>
public class PackedResidualCheckTests : IDisposable
{
    private const string Import = """
        using System.Runtime.CompilerServices;
        using System.Runtime.InteropServices;
        using System.Runtime.InteropServices.Swift;
        using Swift.Runtime;
        [assembly: DisableRuntimeMarshalling]
        internal static class Native
        {
            [DllImport("lib", EntryPoint = "f")] [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
            internal static extern nint F(SwiftSelf self);
        }
        """;

    private static readonly string RuntimeDll = Path.Combine(AppContext.BaseDirectory, "Swift.Runtime.dll");
    private readonly string _directory = Directory.CreateTempSubdirectory("packed-residual-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void AnUnguardedTransitionInAnyPackedFramework_Fails_AndEveryCopyIsChecked()
    {
        var guarded = Compile("public static class C { public static unsafe nint Read(nint h) { SwiftTransitionGuard.ThrowIfUnsupported((SwiftTransitionFacts)5, \"C.Read\"); return Native.F(new SwiftSelf((void*)h)); } }");
        var unguarded = Compile("public static class C { public static unsafe nint Read(nint h) => Native.F(new SwiftSelf((void*)h)); }");
        var nupkg = Pack(("lib/net10.0-ios26.0/A.dll", guarded), ("lib/net10.0-macos26.0/A.dll", unguarded), ("lib/net10.0-ios26.0/Other.dll", unguarded));

        var results = PackedResidualCheck.Check(nupkg, "A.dll", _ => new[] { File.ReadAllBytes(RuntimeDll) }, _ => null);

        Assert.Equal(["net10.0-ios26.0", "net10.0-macos26.0"], results.Select(r => r.Framework));
        Assert.True(results[0].Passed, PackedResidualCheck.ToSummary(results[0]));
        Assert.False(results[1].Passed);
        Assert.Contains(results[1].Result.Violations, v => v.Rule == ResidualGuardCheck.UnguardedRule && v.Member == "C::Read(nint)");
    }

    [Fact]
    public void TheSidecarTripwire_ReadsTheDirectoryForThePackedFramework()
    {
        var guarded = Compile("public static class C { public static unsafe nint Read(nint h) { SwiftTransitionGuard.ThrowIfUnsupported((SwiftTransitionFacts)5, \"C.Read\"); return Native.F(new SwiftSelf((void*)h)); } }");
        var nupkg = Pack(("lib/net10.0-tvos26.0/A.dll", guarded));
        var recorded = Directory.CreateDirectory(Path.Combine(_directory, "recorded")).FullName;
        File.WriteAllText(Path.Combine(recorded, "M.swift-transition-guards.json"),
            """{ "transitions": [ { "kind": "ForwardPInvoke", "member": "Native::F", "entryPoint": "f", "facts": 5 } ] }""");
        var empty = Directory.CreateDirectory(Path.Combine(_directory, "empty")).FullName;
        string? asked = null;

        var agreeing = PackedResidualCheck.Check(nupkg, "A.dll", _ => new[] { File.ReadAllBytes(RuntimeDll) }, f => { asked = f; return recorded; });
        var silent = PackedResidualCheck.Check(nupkg, "A.dll", _ => new[] { File.ReadAllBytes(RuntimeDll) }, _ => empty);

        Assert.Equal("net10.0-tvos26.0", asked);
        Assert.True(Assert.Single(agreeing).Passed, PackedResidualCheck.ToSummary(agreeing[0]));
        Assert.False(Assert.Single(silent).Passed);
        Assert.NotEmpty(silent[0].SidecarProblems);
    }

    [Fact]
    public void APackageWithoutTheAssembly_YieldsNothing_SoTheCallerCountsFrameworks()
    {
        var nupkg = Pack(("lib/net10.0-ios26.0/Other.dll", Compile("public static class C { }")), ("ref/net10.0-ios26.0/A.dll", Compile("public static class C { }")));
        Assert.Empty(PackedResidualCheck.Check(nupkg, "A.dll", _ => Array.Empty<byte[]>(), _ => null));
    }

    [Theory]
    [InlineData("net10.0-ios26.0", "net10.0-ios")]
    [InlineData("net10.0-maccatalyst26.0", "net10.0-maccatalyst")]
    [InlineData("net10.0-macos26.0", "net10.0-macos")]
    [InlineData("net10.0-tvos", "net10.0-tvos")]
    [InlineData("net10.0", "net10.0")]
    public void APackageFolder_MapsToTheProjectFramework(string folder, string framework) =>
        Assert.Equal(framework, PackedResidualCheck.ProjectFramework(folder));

    private string Pack(params (string Entry, byte[] Image)[] entries)
    {
        var path = Path.Combine(_directory, $"P{Guid.NewGuid():N}.nupkg");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, image) in entries)
        {
            using var stream = archive.CreateEntry(name).Open();
            stream.Write(image);
        }
        return path;
    }

    private static byte[] Compile(string body)
    {
        var frameworkDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p) && p.StartsWith(frameworkDir, StringComparison.Ordinal))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(RuntimeDll));
        var compilation = CSharpCompilation.Create("A",
            new[] { CSharpSyntaxTree.ParseText(Import + body, new CSharpParseOptions(LanguageVersion.Latest)) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return stream.ToArray();
    }
}
