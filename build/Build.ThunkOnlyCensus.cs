// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.ThunkOnlyCensus.cs — the thunk-only slice census inside `nuke swift-transition-gate`.
//
// A thunk-only binding is one whose members all reach Swift through native assembly thunks: every
// member is a shape NativeThunkEmitter can thunk, so the wrapper's Swift holds only the module
// infrastructure (free, task cancellation, error and metadata accessors) and every member import
// is a C-convention thunk_ symbol linked from hand-written assembly. The compile-check tree never
// has that shape, because SwiftBindingsTestLib always routes some members through @_cdecl wrappers
// and bridges. So the gate builds a first-party thunk-only fixture
// (BindingTests/Sources/ThunkOnlyFixture/) into a simulator slice, generates and compiles it, and
// scans the compiled assembly with every rule, the ledger cross-check and the residual-guard check,
// like the compile-check tree.
//
// The census is fail-closed and runs on every default-input invocation of the gate. It fails when
// the fixture's assembly was not among the scanned reports, when the generation stopped being
// thunk-only (a member routed through a @_cdecl wrapper, or no thunk assembly), when the compiled
// assembly no longer reaches each fixture member through a C-convention thunk import, or when the
// wrapper binary does not export a thunk the assembly imports. Policy, not baseline: no reseed and
// no --permissive arm.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using SwiftBindings.TransitionGate;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    const string ThunkOnlyModule = "ThunkOnlyFixture";
    const string ThunkOnlyWrapperModule = "ThunkOnlyFixtureSwiftBindings";
    const string ThunkOnlyAssemblyName = "ThunkOnlyFixture.Census";

    // The managed members the fixture must reach through a thunk: the class constructor, both
    // vtable-dispatched instance methods and the static method. Matched against the IL member
    // that holds each thunk import (its P/Invoke declaration).
    static readonly string[] ThunkOnlyExpectedMembers = { "_init_", "_add_", "_scaled_", "_origin_" };

    // Wrapper exports every module carries whatever its members route through.
    static readonly string[] ThunkOnlyInfrastructurePrefixes =
    {
        "SBW_Free_", "SBW_CancelTask_", "SBW_UnregisterTask_", "SBW_GetErrorDescription_", "SBW_ReleaseError_", "SBW_GetMetadata_",
    };

    AbsolutePath ThunkOnlySourceDir => BindingTestsDir / "Sources" / ThunkOnlyModule;
    AbsolutePath ThunkOnlyScratch => RootDirectory / "artifacts" / "thunk-only-census";

    sealed record ThunkOnlyArtifact(AbsolutePath Assembly, AbsolutePath OutputDirectory, AbsolutePath WrapperBinary);

    /// <summary>
    /// Builds the thunk-only fixture's simulator slice, generates its binding and compiles the
    /// generated C#, failing closed on any step. Returns the compiled assembly the gate scans.
    /// </summary>
    ThunkOnlyArtifact BuildThunkOnlyCensusArtifact()
    {
        Log.Information("=========================================");
        Log.Information(" Thunk-only slice census: building the fixture");
        Log.Information("=========================================");
        EnsureGeneratorBuilt();

        var scratch = ThunkOnlyScratch;
        if (Directory.Exists(scratch)) scratch.DeleteDirectory();
        scratch.CreateDirectory();

        var ios = ApplePlatform.IOS;
        var buildRoot = scratch / "build";
        var sliceDir = buildRoot / ios.SimulatorSliceId;
        var frameworkDir = sliceDir / $"{ThunkOnlyModule}.framework";
        var sources = Directory.GetFiles(ThunkOnlySourceDir, "*.swift", SearchOption.AllDirectories).ToList();
        if (sources.Count == 0)
            throw new Exception($"Thunk-only census: no Swift sources under {ThunkOnlySourceDir}.");
        CompileModuleSlice(
            ThunkOnlyModule, ios.SimulatorTarget, XcRun.GetSdkPath(ios.SimulatorSdkName),
            ios.SimulatorModuleSuffix, ios.MinOsVersion, ios.SimulatorPlistPlatform,
            frameworkDir, sources, frameworkSearchPaths: new[] { sliceDir.ToString() });
        var xcframework = buildRoot / $"{ThunkOnlyModule}.xcframework";
        XcodeBuild.ExecuteCreateXcframework(new CreateXcframeworkSettings()
            .AddFrameworkPath(frameworkDir)
            .SetOutputPath(xcframework));

        var output = scratch / "output";
        output.CreateDirectory();
        var arguments = string.Join(" ",
            $"\"{GeneratorDll}\"",
            $"--xcframework \"{xcframework}\"",
            $"-o \"{output}\"",
            $"--async-library {ThunkOnlyWrapperModule}",
            $"--target-framework {ios.GetTfm(BtFramework)}");
        var process = ProcessTasks.StartProcess("dotnet", arguments, workingDirectory: BindingTestsDir, logOutput: false);
        process.WaitForExit();
        File.WriteAllLines(scratch / "generator.log", process.Output.Select(o => o.Text));
        if (process.ExitCode != 0)
            throw new Exception($"Thunk-only census: the generator exited {process.ExitCode}; see {scratch / "generator.log"}.");

        var project = scratch / "compile" / $"{ThunkOnlyAssemblyName}.csproj";
        project.Parent.CreateDirectory();
        File.WriteAllText(project, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Library</OutputType>
                <TargetFramework>{BtFramework}-ios</TargetFramework>
                <AssemblyName>{ThunkOnlyAssemblyName}</AssemblyName>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <Nullable>enable</Nullable>
                <NoWarn>CS0169;CS0649;CA1418;CA1420</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <AssemblyAttribute Include="System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="{SourceDir / "Swift.Runtime" / "src" / "Swift.Runtime.csproj"}" />
                <ProjectReference Include="{SourceDir / "Swift.Bindings.Apple" / "Swift.Bindings.Apple.csproj"}" />
              </ItemGroup>
              <ItemGroup>
                <Compile Include="{output / $"{ThunkOnlyModule}.cs"}" />
                <Compile Include="{output / $"{ThunkOnlyModule}.Types.*.cs"}" />
              </ItemGroup>
            </Project>
            """);
        DotNetBuild(s => s
            .SetProjectFile(project)
            .SetConfiguration("Debug")
            .SetOutputDirectory(scratch / "bin")
            .SetVerbosity(DotNetVerbosity.quiet));

        var assembly = scratch / "bin" / $"{ThunkOnlyAssemblyName}.dll";
        if (!File.Exists(assembly))
            throw new Exception($"Thunk-only census: the compile produced no {assembly}.");
        var wrapper = Directory.EnumerateFiles(output, ThunkOnlyWrapperModule, SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(Path.GetDirectoryName(path)) == $"{ThunkOnlyWrapperModule}.framework")
            .Select(path => (AbsolutePath)path)
            .FirstOrDefault()
            ?? throw new Exception($"Thunk-only census: the generation produced no {ThunkOnlyWrapperModule}.framework binary under {output}.");
        return new ThunkOnlyArtifact(assembly, output, wrapper);
    }

    /// <summary>
    /// The census over the gate's reports: the fixture's assembly was scanned, its generation is
    /// still thunk-only and unrouted, and each fixture member reaches Swift through a C-convention
    /// thunk the wrapper exports. Returns the failures; an empty list is a pass.
    /// </summary>
    static List<string> CheckThunkOnlyCensus(ThunkOnlyArtifact artifact, IReadOnlyList<AssemblyTransitionReport> reports)
    {
        var failures = new List<string>();
        var report = reports.SingleOrDefault(r =>
            string.Equals(Path.GetFullPath(r.Path), Path.GetFullPath(artifact.Assembly), StringComparison.Ordinal));
        if (report is null)
        {
            failures.Add($"the thunk-only fixture's assembly {artifact.Assembly} was not scanned");
            return failures;
        }

        // Still thunk-only: assembly thunks generated, and no fixture member reaches Swift through a
        // @_cdecl wrapper. The wrapper's Swift file always holds the module infrastructure (free,
        // task cancellation, error and metadata accessors); only member wrappers end the shape.
        var thunkSources = Directory.GetFiles(artifact.OutputDirectory, "*.s", SearchOption.TopDirectoryOnly);
        if (thunkSources.Length == 0)
            failures.Add("the generation emitted no thunk assembly (*.s)");
        var memberWrappers = report.Sites
            .Where(site => site.Kind == TransitionSiteKind.PInvoke
                           && site.EntryPoint?.StartsWith("SBW_", StringComparison.Ordinal) == true
                           && !ThunkOnlyInfrastructurePrefixes.Any(prefix => site.EntryPoint.StartsWith(prefix, StringComparison.Ordinal)))
            .Select(site => $"{site.Member} -> {site.EntryPoint}")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (memberWrappers.Count > 0)
            failures.Add($"the generation is no longer thunk-only: members reach Swift through @_cdecl wrappers ({string.Join(", ", memberWrappers)})");

        // Nothing to bridge: a thunk takes the C convention, so no member needs a Swift bridge.
        if (report.Inventory.BridgeTwins != 0)
            failures.Add($"the thunk-only assembly holds {report.Inventory.BridgeTwins} bridge twin(s); a module whose members are all thunks has nothing to bridge");

        var thunkSites = report.Sites
            .Where(site => site.Kind == TransitionSiteKind.PInvoke
                           && site.EntryPoint?.StartsWith("thunk_", StringComparison.Ordinal) == true)
            .ToList();
        foreach (var site in thunkSites.Where(site => site.Convention == TransitionConvention.Swift))
            failures.Add($"thunk import {site.Member} -> {site.EntryPoint} uses the Swift convention; a thunk takes the C convention");
        foreach (var expected in ThunkOnlyExpectedMembers)
            if (!thunkSites.Any(site => site.Member.Contains(expected, StringComparison.Ordinal)))
                failures.Add($"no thunk import for the fixture member matching '{expected}'");

        var slices = MachOSymbols.Read(File.ReadAllBytes(artifact.WrapperBinary));
        foreach (var entryPoint in thunkSites.Select(site => site.EntryPoint!).Distinct(StringComparer.Ordinal))
            foreach (var slice in slices.Where(slice => !slice.Exports.Contains(entryPoint)))
                failures.Add($"the wrapper's {slice.Architecture} slice does not export {entryPoint}");

        Log.Information("Thunk-only census: {Thunks} thunk import(s), {Files} thunk assembly file(s), {Swift} Swift P/Invoke(s), {Twins} bridge twin(s); {Verdict}",
            thunkSites.Count, thunkSources.Length, report.Inventory.SwiftPInvokes, report.Inventory.BridgeTwins,
            failures.Count == 0 ? "passed" : "FAILED");
        return failures;
    }
}
