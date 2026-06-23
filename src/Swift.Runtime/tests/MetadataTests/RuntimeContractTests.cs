// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit coverage for the load-time runtime-contract handshake (<see cref="RuntimeContract"/>).
/// The gate is a supported window: a binding's contract epoch must fall within
/// <c>[MinimumSupportedGeneratedVersion, Version]</c>, with epoch 0 (the dev/in-tree build) treated
/// as always-compatible. The pure window predicate is exercised with explicit epochs so the logic
/// is verified independently of THIS build's derived <see cref="RuntimeContract.Version"/> (which is
/// the always-compatible epoch 0 in a dev build, where <see cref="RuntimeContract.AssertCompatible"/>
/// can never reach the comparisons).
/// </summary>
public class RuntimeContractTests
{
    // --- Pure window predicate: forward-incompatible direction (binding newer than runtime) ---

    [Theory]
    [InlineData(17, 16, 16)] // one minor ahead
    [InlineData(1000, 16, 16)] // a whole major ahead
    public void IsGeneratedVersionSupported_NewerThanRuntime_NotSupported(int gen, int runtime, int floor)
    {
        Assert.False(RuntimeContract.IsGeneratedVersionSupported(gen, runtime, floor));
    }

    // --- Pure window predicate: too-old direction (binding below the supported floor) ---

    [Theory]
    [InlineData(15, 16, 16)] // the last pre-contract minor (0.15)
    [InlineData(1, 16, 16)] // an ancient epoch-1 binding
    public void IsGeneratedVersionSupported_OlderThanFloor_NotSupported(int gen, int runtime, int floor)
    {
        Assert.False(RuntimeContract.IsGeneratedVersionSupported(gen, runtime, floor));
    }

    // --- Pure window predicate: inside the window (the relaxation's teeth) ---

    [Theory]
    [InlineData(16, 16, 16)] // exact match
    [InlineData(16, 17, 16)] // older-but-supported binding on a newer (additive-minor) runtime
    [InlineData(17, 17, 16)] // current binding on current runtime, floor below both
    public void IsGeneratedVersionSupported_InsideWindow_Supported(int gen, int runtime, int floor)
    {
        Assert.True(RuntimeContract.IsGeneratedVersionSupported(gen, runtime, floor));
    }

    // --- Pure window predicate: dev/in-tree sentinel bypass (epoch 0 on either side) ---

    [Theory]
    [InlineData(0, 16, 16)] // dev binding against a released runtime
    [InlineData(99, 0, 16)] // any binding against a dev runtime
    [InlineData(0, 0, 16)] // dev on dev (the normal ProjectReference inner loop)
    public void IsGeneratedVersionSupported_DevSentinel_AlwaysSupported(int gen, int runtime, int floor)
    {
        Assert.True(RuntimeContract.IsGeneratedVersionSupported(gen, runtime, floor));
    }

    // --- AssertCompatible end-to-end (uses the real derived Version) ---

    [Fact]
    public void AssertCompatible_MatchingVersion_DoesNotThrow()
    {
        // The runtime's own epoch is always inside its own window.
        RuntimeContract.AssertCompatible(RuntimeContract.Version);
    }

    [Fact]
    public void AssertCompatible_DevRuntime_NeverAborts()
    {
        // Under the dev sentinel (Version == 0) every binding bypasses, so an in-tree
        // ProjectReference build of a binding generated against any epoch loads cleanly. This is
        // the inner-loop / BindingTests case; a real abort only happens at a released runtime.
        if (RuntimeContract.Version == 0)
        {
            RuntimeContract.AssertCompatible(2);
            RuntimeContract.AssertCompatible(9999);
        }
    }

    // --- Exception carries the full window so the message is actionable ---

    [Fact]
    public void MismatchException_CarriesGeneratedRuntimeAndFloor()
    {
        var ex = new SwiftRuntimeContractMismatchException(
            generatedAgainstVersion: 15, runtimeVersion: 16, minimumSupportedGeneratedVersion: 16);

        Assert.Equal(15, ex.GeneratedAgainstVersion);
        Assert.Equal(16, ex.RuntimeVersion);
        Assert.Equal(16, ex.MinimumSupportedGeneratedVersion);
        Assert.Contains("epoch 15", ex.Message);
        Assert.Contains("epoch 16", ex.Message);
    }

    // --- Epoch parse: major*1000 + minor, pre-release tolerant, fail-soft to the dev sentinel ---

    [Theory]
    [InlineData("0.0.0-dev", 0)] // the dev sentinel
    [InlineData("0.15.3", 15)]
    [InlineData("0.16.0", 16)]
    [InlineData("0.16.0-preview.1", 16)] // a pre-release suffix on the patch doesn't change the minor
    [InlineData("1.0.0", 1000)] // a real 1.0 is epoch 1000, clear of the dev sentinel
    [InlineData("1.15.0", 1015)] // distinct from 0.15 (epoch 15)
    [InlineData("garbage", 0)] // unparseable degrades to the always-compatible sentinel
    [InlineData("x.8.0", 0)] // non-integer major fails closed to the sentinel
    public void ParseEpoch_MapsVersionToEpoch(string version, int expected)
    {
        Assert.Equal(expected, RuntimeContract.ParseEpoch(version));
    }

    // --- The one hand-maintained value stays consistent with this build's derived ceiling ---

    [Fact]
    public void Floor_DoesNotExceedVersion_AtReleaseBuilds()
    {
        // In a dev build Version is the epoch-0 sentinel and the floor isn't consulted; only a
        // released runtime (non-zero epoch) must be able to support its own bindings.
        if (RuntimeContract.Version != 0)
            Assert.True(RuntimeContract.MinimumSupportedGeneratedVersion <= RuntimeContract.Version,
                "A released runtime must support at least its own epoch; raise nothing above Version.");
    }
}
