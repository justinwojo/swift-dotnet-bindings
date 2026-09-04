// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Guards the point at which a runtime-test lane becomes <i>graded</i>. <c>CompareRuntimeBaseline</c>
/// resolves a platform label to a baseline key and, when it cannot, logs "No runtime test baseline
/// for … — skipping comparison" and returns. That is a silent pass: a lane whose label never mapped
/// runs green forever no matter how far its pass count falls. These tests assert the mapping covers
/// every shipping lane, that both committed baseline files actually carry the Mono full-AOT device
/// lane's floor, and — the part that cannot be read off the JSON — that the identity ratchet is live
/// for that lane rather than taking its no-entry inert path.
/// </summary>
public class RuntimeBaselinePlatformKeyTests
{
    private const string MonoAotKey = "device_monoaot";

    [Theory]
    [InlineData("Simulator", "simulator")]
    [InlineData("Device/NativeAOT", "device")]
    [InlineData("Device/MonoAOT", MonoAotKey)]
    [InlineData("macOS", "macos")]
    [InlineData("macOS x64", "macos_x64")]
    [InlineData("Mac Catalyst", "maccatalyst")]
    [InlineData("Mac Catalyst x64", "maccatalyst_x64")]
    [InlineData("tvOS Simulator", "tvos_simulator")]
    public void Resolve_MapsEveryShippingLabelToItsOwnKey(string label, string expectedKey)
        => Assert.Equal(expectedKey, RuntimeBaselinePlatformKey.Resolve(label));

    /// <summary>
    /// The two device lanes must not collapse onto one key: they run the same suite on the same
    /// phone but carry different skip sets, so a shared floor would false-regress one and un-gate
    /// the other.
    /// </summary>
    [Fact]
    public void Resolve_KeepsTheTwoDeviceLanesApart()
    {
        Assert.NotEqual(
            RuntimeBaselinePlatformKey.Resolve("Device/NativeAOT"),
            RuntimeBaselinePlatformKey.Resolve("Device/MonoAOT"));

        // The bare historical "device" label predates the lane naming and still means NativeAOT.
        Assert.Equal("device", RuntimeBaselinePlatformKey.Resolve("device"));
    }

    [Fact]
    public void Resolve_IsCaseInsensitiveSoDisplayCasingCannotUnGateALane()
        => Assert.Equal(MonoAotKey, RuntimeBaselinePlatformKey.Resolve("DEVICE/MONOAOT"));

    [Theory]
    [InlineData("")]
    [InlineData("watchOS")]
    [InlineData("Device/Interpreter")]
    public void Resolve_ReturnsNullForALaneThisRepoDoesNotGrade(string label)
        => Assert.Null(RuntimeBaselinePlatformKey.Resolve(label));

    [Fact]
    public void Resolve_ReturnsNullForNull() => Assert.Null(RuntimeBaselinePlatformKey.Resolve(null));

    /// <summary>
    /// Every label the pipeline reports a run under must have a scalar pass-count floor committed,
    /// otherwise the lookup falls through to the same "skipping comparison" early return as an
    /// unmapped label.
    /// </summary>
    [Fact]
    public void EveryShippingLane_HasAScalarPassCountFloor()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(LocateRepoRoot(), "build", "baselines", "validation-baseline.json")));
        var runtimeTests = doc.RootElement.GetProperty("runtime_tests");

        var missing = new List<string>();
        foreach (var label in RuntimeBaselinePlatformKey.ShippingPlatformLabels)
        {
            var key = RuntimeBaselinePlatformKey.Resolve(label);
            Assert.NotNull(key);
            if (!runtimeTests.TryGetProperty(key!, out var counts) || counts.GetProperty("pass").GetInt32() <= 0)
                missing.Add($"{label} => runtime_tests.{key}");
        }

        Assert.True(missing.Count == 0,
            "Shipping runtime lane(s) with no committed pass-count floor — the comparison silently " +
            "skips for these: " + string.Join(", ", missing));
    }

    /// <summary>
    /// Resolving a key is only half of being graded: <c>CompareRuntimeBaseline</c> then maps that key
    /// to a typed <c>RuntimeTestsBaseline</c> property twice — once to read the floor and once to
    /// write it back on a green improvement — and a missing arm falls through to <c>null</c>, which
    /// is the same silent skip as an unmapped label. That model carries a Nuke dependency so it
    /// cannot be link-compiled here; scan the target's source for the arms instead. Cruder than the
    /// tests above, and it guards the exact way this gate fails open.
    /// </summary>
    [Fact]
    public void EveryBaselineKey_HasBothSwitchArmsInTheComparison()
    {
        var source = File.ReadAllText(
            Path.Combine(LocateRepoRoot(), "build", "Build.RuntimeTests.cs"));

        var underWired = new List<string>();
        foreach (var label in RuntimeBaselinePlatformKey.ShippingPlatformLabels)
        {
            var key = RuntimeBaselinePlatformKey.Resolve(label);
            var arms = System.Text.RegularExpressions.Regex.Matches(source, $"\"{key}\" =>").Count;
            if (arms < 2)
                underWired.Add($"{key} ({arms} arm(s))");
        }

        Assert.True(underWired.Count == 0,
            "Baseline key(s) missing a lookup or auto-update arm in CompareRuntimeBaseline — the " +
            "lane resolves but grades against a null floor: " + string.Join(", ", underWired));
    }

    /// <summary>
    /// The Mono full-AOT device lane is graded by both stores, and their skip cardinality agrees.
    /// The scalar floor and the identity floor are updated by separate code paths, so a drift
    /// between them means one of the two is stale.
    /// </summary>
    [Fact]
    public void DeviceMonoAotLane_ScalarAndIdentityFloorsAgreeOnSkipCount()
    {
        var repoRoot = LocateRepoRoot();
        using var doc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repoRoot, "build", "baselines", "validation-baseline.json")));
        var scalar = doc.RootElement.GetProperty("runtime_tests").GetProperty(MonoAotKey);

        var identity = LoadIdentityBaseline(repoRoot);
        Assert.True(identity.Platforms.ContainsKey(MonoAotKey),
            $"runtime-identity-baseline.json has no '{MonoAotKey}' entry — the identity ratchet is " +
            "inert for the Mono full-AOT device lane.");
        var lane = identity.Platforms[MonoAotKey];

        Assert.Equal(scalar.GetProperty("pass").GetInt32(), lane.PassCount);
        Assert.Equal(scalar.GetProperty("skip").GetInt32(), lane.Skips.Count);
        Assert.Equal(0, scalar.GetProperty("fail").GetInt32());
        Assert.Empty(lane.KnownFails);
    }

    /// <summary>
    /// The identity ratchet returns no regressions at all for a platform it has no entry for, so
    /// "green" proves nothing by itself. Drive the real committed baseline through a drop, a new
    /// skip and a resolved skip to show the Mono full-AOT lane takes the comparing path.
    /// </summary>
    [Fact]
    public void DeviceMonoAotLane_IdentityRatchetActuallyCompares()
    {
        var identity = LoadIdentityBaseline(LocateRepoRoot());
        var lane = identity.Platforms[MonoAotKey];

        // A run that reproduces the baseline exactly is clean.
        var atFloor = SynthesizeRun(lane);
        var (regressions, improvements) = identity.Compare(MonoAotKey, atFloor);
        Assert.Empty(regressions);
        Assert.Empty(improvements);

        // One fewer pass trips the scalar floor inside the identity model.
        var oneShort = atFloor.Where(t => t.Status != "pass" || t.Method != "Pass0").ToList();
        Assert.Contains(identity.Compare(MonoAotKey, oneShort).Regressions,
            r => r.Contains("pass count dropped"));

        // A test that stops passing and starts skipping is a new skip identity.
        var newSkip = atFloor
            .Select(t => t.Method == "Pass1"
                ? new RuntimeIdentityBaseline.TestRecord(t.Class, t.Method, "skip", "flaky on Mono AOT")
                : t)
            .ToList();
        Assert.Contains(identity.Compare(MonoAotKey, newSkip).Regressions,
            r => r.Contains("NEW skip") && r.Contains(MonoAotKey));

        // A baselined skip that starts passing is reported as an improvement, not a regression.
        var first = lane.Skips[0];
        var resolved = atFloor
            .Select(t => t.Class == first.Class && t.Method == first.Method
                ? new RuntimeIdentityBaseline.TestRecord(t.Class, t.Method, "pass", "")
                : t)
            .ToList();
        var resolvedResult = identity.Compare(MonoAotKey, resolved);
        Assert.Empty(resolvedResult.Regressions);
        Assert.Contains(resolvedResult.Improvements,
            i => i.Contains("RESOLVED skip") && i.Contains($"{first.Class}.{first.Method}"));
    }

    /// <summary>
    /// The five runtime-detected Mono skips are the lane's whole reason for having its own floor:
    /// they do not appear on the NativeAOT device lane, and the one NativeAOT-shaped skip does not
    /// appear here. If the two skip sets ever became identical, one shared key would do.
    /// </summary>
    [Fact]
    public void DeviceMonoAotLane_SkipSetDiffersFromTheNativeAotDeviceLane()
    {
        var identity = LoadIdentityBaseline(LocateRepoRoot());
        var mono = identity.Platforms[MonoAotKey].Skips.Select(s => (s.Class, s.Method)).ToHashSet();
        var nativeAot = identity.Platforms["device"].Skips.Select(s => (s.Class, s.Method)).ToHashSet();

        Assert.NotEmpty(mono.Except(nativeAot));
        Assert.NotEmpty(nativeAot.Except(mono));
    }

    private static RuntimeIdentityBaseline LoadIdentityBaseline(string repoRoot)
        => RuntimeIdentityBaseline.Load(
            Path.Combine(repoRoot, "build", "baselines", "runtime-identity-baseline.json"));

    /// <summary>
    /// Builds the minimal run that satisfies a lane's baseline: its exact skip identities plus
    /// <c>PassCount</c> synthetic passes (passes are stored by count, never by name).
    /// </summary>
    private static List<RuntimeIdentityBaseline.TestRecord> SynthesizeRun(
        RuntimeIdentityBaseline.PlatformIdentities lane)
    {
        var records = lane.Skips
            .Select(s => new RuntimeIdentityBaseline.TestRecord(s.Class, s.Method, "skip", s.Reason))
            .ToList();
        for (var i = 0; i < lane.PassCount; i++)
            records.Add(new RuntimeIdentityBaseline.TestRecord("SynthesizedPassing", $"Pass{i}", "pass", ""));
        return records;
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SwiftBindings.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
