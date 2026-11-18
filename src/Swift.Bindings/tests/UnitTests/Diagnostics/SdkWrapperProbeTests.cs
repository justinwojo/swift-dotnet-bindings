// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The SDK render pass grades its wrapper with a probe compile. The probe must compile exactly the
/// sources a real wrapper compile reads, and none of what it builds may land in the SDK's
/// intermediate directory, which the deferred compile, its fingerprint and the consumer metadata read.
/// </summary>
public class SdkWrapperProbeTests : IDisposable
{
    readonly string _output = Path.Combine(Path.GetTempPath(), "sdk-probe-test-" + Guid.NewGuid().ToString("N"));

    public SdkWrapperProbeTests()
    {
        Directory.CreateDirectory(_output);
        File.WriteAllText(Path.Combine(_output, "Lib.Wrapper.swift"), "// wrapper");
        File.WriteAllText(Path.Combine(_output, "Lib.Extra.swift"), "// extra");
        File.WriteAllText(Path.Combine(_output, "Lib.SwiftUIBridge.swift"), "// bridge compiles separately");
        File.WriteAllText(Path.Combine(_output, "Lib.arm64.s"), "// thunks");
        File.WriteAllText(Path.Combine(_output, "Lib.cs"), "// C#");
    }

    public void Dispose()
    {
        if (Directory.Exists(_output)) Directory.Delete(_output, recursive: true);
    }

    static WrapperRecoveryCompileRequest Request(string dir) => new(
        dir, null, null, null,
        new DepModuleCollisionDetector.SlicedCollisionResult(Array.Empty<string>(), Array.Empty<string>()));

    static WrapperCompileDiagnostics Clean() => new(true, [], [], null, []);

    [Fact]
    public void Run_CompilesACopyOfTheWrapperSourcesInScratch_AndLeavesTheOutputUntouched()
    {
        var before = Directory.GetFileSystemEntries(_output).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        string? compiledIn = null;
        string[] seen = [];

        var result = SdkWrapperProbe.Run(Request(_output), (req, dir) =>
        {
            compiledIn = dir;
            seen = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;
            // What a real compile leaves behind: the wrapper xcframework and its build directory.
            Directory.CreateDirectory(Path.Combine(dir, "LibSwiftBindings.xcframework"));
            Directory.CreateDirectory(Path.Combine(dir, ".wrapper-build"));
            Assert.Equal(_output, req.OutputDirectory);
            return Clean();
        }, NullLogger.Instance);

        Assert.True(result.AllSlicesClean);
        Assert.NotNull(compiledIn);
        Assert.NotEqual(_output, compiledIn);
        Assert.Equal(new[] { "Lib.Extra.swift", "Lib.Wrapper.swift", "Lib.arm64.s" }, seen);
        Assert.False(Directory.Exists(compiledIn), "the probe directory is removed after the compile");
        Assert.Equal(before, Directory.GetFileSystemEntries(_output).OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Run_RemovesTheScratchDirectory_WhenTheCompileThrows()
    {
        string? compiledIn = null;
        Assert.Throws<InvalidOperationException>(() => SdkWrapperProbe.Run(Request(_output), (_, dir) =>
        {
            compiledIn = dir;
            throw new InvalidOperationException("swiftc crashed");
        }, NullLogger.Instance));

        Assert.NotNull(compiledIn);
        Assert.False(Directory.Exists(compiledIn));
    }

    /// <summary>
    /// The probe compiles the platform's primary slice, so it may withdraw members only when the
    /// deferred compile builds that slice too. A device-only wrapper on a platform with a simulator
    /// is the case that must not be probed: the simulator SDK can lack what the device links.
    /// </summary>
    [Theory]
    [InlineData(true, true, "all", true)]
    [InlineData(true, true, "simulator", true)]
    [InlineData(true, true, "device", false)]
    [InlineData(true, false, "all", false)]
    [InlineData(false, false, "all", true)]
    [InlineData(false, false, "device", true)]
    public void DeferredCompileBuildsProbeSlice_ProbesOnlyASliceTheShippedWrapperIsBuiltFrom(
        bool platformHasSimulator, bool resolvedSimulatorSlice, string wrapperArchitectures, bool expected)
    {
        Assert.Equal(expected, BindingsGeneratorCommand.DeferredCompileBuildsProbeSlice(
            platformHasSimulator, resolvedSimulatorSlice, wrapperArchitectures));
    }
}
