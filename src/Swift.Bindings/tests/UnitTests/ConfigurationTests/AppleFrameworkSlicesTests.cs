// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The wrapper slices of an Apple SDK framework a generation run compiles itself when it collects the
/// lowering receipt: the framework is handed over as it sits in one SDK and is found at the same place
/// in each slice's own SDK, so the receipt describes every slice the wrapper ships.
/// </summary>
public sealed class AppleFrameworkSlicesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"afs_{Guid.NewGuid():N}");
    private readonly string _simSdk;
    private readonly string _deviceSdk;
    private readonly string _abi;

    public AppleFrameworkSlicesTests()
    {
        _simSdk = Path.Combine(_dir, "iPhoneSimulator26.2.sdk");
        _deviceSdk = Path.Combine(_dir, "iPhoneOS26.2.sdk");
        // A simulator SDK ships both simulator architectures' interfaces; a device SDK ships arm64e's alone.
        WriteFramework(_simSdk, "AppleKit", "arm64-apple-ios-simulator", "x86_64-apple-ios-simulator");
        WriteFramework(_deviceSdk, "AppleKit", "arm64e-apple-ios");
        _abi = Path.Combine(_dir, "AppleKit.abi.json");
        File.WriteAllText(_abi, "{}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static string WriteFramework(string sdk, string module, params string[] interfaceTriples)
    {
        var framework = Path.Combine(sdk, "System", "Library", "Frameworks", module + ".framework");
        var modules = Path.Combine(framework, "Modules", module + ".swiftmodule");
        Directory.CreateDirectory(modules);
        foreach (var triple in interfaceTriples)
            File.WriteAllText(Path.Combine(modules, triple + ".swiftinterface"), "// interface");
        var tbd = Path.Combine(framework, module + ".tbd");
        File.WriteAllText(tbd, "--- !tapi-tbd");
        return tbd;
    }

    private string Tbd(string sdk) => Path.Combine(sdk, "System", "Library", "Frameworks", "AppleKit.framework", "AppleKit.tbd");

    private static readonly PlatformInfo IOS = PlatformInfoFactory.Create(ApplePlatform.iOS);

    [Fact]
    public void MapIntoSdk_KeepsThePathInsideTheSdk_ThroughItsUnversionedLinkToo()
    {
        Assert.Equal(Tbd(_deviceSdk), AppleFrameworkSlices.MapIntoSdk(Tbd(_simSdk), _simSdk, _deviceSdk));

        var link = Path.Combine(_dir, "iPhoneSimulator.sdk");
        Directory.CreateSymbolicLink(link, "iPhoneSimulator26.2.sdk");
        Assert.Equal(Tbd(_deviceSdk), AppleFrameworkSlices.MapIntoSdk(Tbd(link), _simSdk, _deviceSdk));
        Assert.Equal(Tbd(_deviceSdk), AppleFrameworkSlices.MapIntoSdk(Tbd(_simSdk), link, _deviceSdk));

        Assert.Null(AppleFrameworkSlices.MapIntoSdk(Path.Combine(_dir, "elsewhere", "AppleKit.tbd"), _simSdk, _deviceSdk));
    }

    [Fact]
    public void Resolve_FindsEachSlicesOwnCopy_AndItsInterface()
    {
        var sim = AppleFrameworkSlices.Resolve("AppleKit", _abi, Tbd(_simSdk), _simSdk, IOS.GetSlice(true), _simSdk, out var why);
        Assert.NotNull(sim);
        Assert.Null(why);
        Assert.Equal(Tbd(_simSdk), sim!.TbdPath);
        Assert.Equal(Path.Combine(_simSdk, "System", "Library", "Frameworks"), sim.FrameworkSearchPath);
        Assert.EndsWith("arm64-apple-ios-simulator.swiftinterface", sim.SwiftInterfacePath);
        Assert.True(sim.IsSimulatorSlice);

        var x64 = AppleFrameworkSlices.Resolve("AppleKit", _abi, Tbd(_simSdk), _simSdk, IOS.GetSlice(true).WithArchitecture("x86_64"), _simSdk, out _);
        Assert.EndsWith("x86_64-apple-ios-simulator.swiftinterface", x64!.SwiftInterfacePath);
        Assert.Equal("x86_64", x64.SelectedArchitecture);

        // The device SDK ships arm64e's interface, which swiftc reads for an arm64 compile.
        var device = AppleFrameworkSlices.Resolve("AppleKit", _abi, Tbd(_simSdk), _simSdk, IOS.DeviceSlice, _deviceSdk, out why);
        Assert.NotNull(device);
        Assert.Equal(Tbd(_deviceSdk), device!.DylibPath);
        Assert.EndsWith("arm64e-apple-ios.swiftinterface", device.SwiftInterfacePath);
        Assert.False(device.IsSimulatorSlice);
        Assert.Equal(IOS.DeviceSlice.SliceId, device.LibraryIdentifier);

        // Every slice reads the one descriptor the run bound, so none sees a simulator-only member.
        Assert.Equal(_abi, sim.AbiJsonPath);
        Assert.Equal(_abi, device.AbiJsonPath);
    }

    [Fact]
    public void Resolve_RefusesAFrameworkOutsideTheSdk_OrMissingFromTheSlicesSdk()
    {
        var outside = WriteFramework(Path.Combine(_dir, "local"), "AppleKit", "arm64-apple-ios-simulator");
        Assert.Null(AppleFrameworkSlices.Resolve("AppleKit", _abi, outside, _simSdk, IOS.DeviceSlice, _deviceSdk, out var why));
        Assert.Contains("is not inside the SDK", why);

        var emptySdk = Path.Combine(_dir, "AppleTVOS26.2.sdk");
        Directory.CreateDirectory(emptySdk);
        Assert.Null(AppleFrameworkSlices.Resolve("AppleKit", _abi, Tbd(_simSdk), _simSdk, IOS.DeviceSlice, emptySdk, out why));
        Assert.Contains("has no", why);

        WriteFramework(emptySdk, "AppleKit");
        Assert.Null(AppleFrameworkSlices.Resolve("AppleKit", _abi, Tbd(_simSdk), _simSdk, IOS.DeviceSlice, emptySdk, out why));
        Assert.Contains("module interface", why);
    }

    [Fact]
    public void ResolveAppleFrameworkSlices_ResolvesTheSimulatorAndDeviceSlices_FromEitherGivenSlice()
    {
        string? Root(string sdk) => sdk switch { "iphonesimulator" => _simSdk, "iphoneos" => _deviceSdk, _ => null };

        var fromSim = BindingsGeneratorCommand.ResolveAppleFrameworkSlices(
            "AppleKit", _abi, Tbd(_simSdk), IOS.GetSlice(true), IOS, NullLogger.Instance, out var why, Root);
        Assert.NotNull(fromSim);
        Assert.Null(why);
        Assert.True(fromSim!.Value.Sim.IsSimulatorSlice);
        Assert.False(fromSim.Value.Device!.IsSimulatorSlice);
        Assert.Equal("x86_64", fromSim.Value.SimFor("x86_64")!.SelectedArchitecture);
        Assert.Same(fromSim.Value.Sim, fromSim.Value.SimFor("arm64"));

        // A device-first build hands over the device SDK's copy.
        var fromDevice = BindingsGeneratorCommand.ResolveAppleFrameworkSlices(
            "AppleKit", _abi, Tbd(_deviceSdk), IOS.DeviceSlice, IOS, NullLogger.Instance, out why, Root);
        Assert.Equal(Tbd(_simSdk), fromDevice!.Value.Sim.TbdPath);

        // An SDK that cannot be found resolves nothing.
        Assert.Null(BindingsGeneratorCommand.ResolveAppleFrameworkSlices(
            "AppleKit", _abi, Tbd(_simSdk), IOS.GetSlice(true), IOS, NullLogger.Instance, out why, sdk => sdk == "iphoneos" ? null : Root(sdk)));
        Assert.Contains("SDK path", why);
    }

    /// <summary>
    /// The wrapper ships a device slice whatever simulator architectures were asked for, so one pass
    /// builds it: the pass of its own architecture, or the first pass when no pass has that
    /// architecture, so the receipt describes it either way.
    /// </summary>
    [Theory]
    [InlineData("arm64", true, new[] { "arm64", "x86_64" }, true)]
    [InlineData("x86_64", false, new[] { "arm64", "x86_64" }, false)]
    [InlineData("arm64", false, new[] { "x86_64", "arm64" }, true)]
    [InlineData("x86_64", true, new[] { "x86_64", "arm64" }, false)]
    [InlineData("x86_64", true, new[] { "x86_64" }, true)]
    [InlineData("x86_64", false, new[] { "x86_64" }, false)]
    public void CompilesDeviceSlice_OnItsOwnArchitecturesPass_OrTheFirstWhenNoneHasIt(
        string passArch, bool isFirstPass, string[] passArchs, bool expected) =>
        Assert.Equal(expected, BindingsGeneratorCommand.CompilesDeviceSlice(passArch, isFirstPass, "arm64", passArchs));

    /// <summary>
    /// A generation run collects the receipt only where it compiles the wrapper itself: an xcframework or
    /// an Apple framework whose wrapper is not deferred. Any run that skips the wrapper leaves the
    /// receipt to the --compile-wrapper-only pass, and a direct run of a non-Apple library compiles no
    /// wrapper a receipt could describe.
    /// </summary>
    [Theory]
    [InlineData(false, "@rpath/AppleKit.framework/AppleKit", false, true)]
    [InlineData(false, "/System/Library/Frameworks/AppleKit.framework/AppleKit", false, true)]
    [InlineData(false, "@rpath/AppleKit.framework/AppleKit", true, false)]
    [InlineData(true, null, false, true)]
    [InlineData(true, null, true, false)]
    [InlineData(false, "/Users/me/build/Lib.framework/Lib", false, false)]
    [InlineData(false, null, false, false)]
    public void CollectsLoweringEvidenceInline_OnlyWhereTheRunCompilesTheWrapper(
        bool hasXcframework, string? libraryName, bool skipWrapperCompilation, bool expected) =>
        Assert.Equal(expected, BindingsGeneratorCommand.CollectsLoweringEvidenceInline(hasXcframework, libraryName, skipWrapperCompilation));

    /// <summary>
    /// A standalone --xcframework generation that compiles its wrapper collects the receipt beside the
    /// project and routes on the targets the SDK's wrapper compile does: collection on iOS, Mac
    /// Catalyst, tvOS and every net11.0 target, routing on iOS, Mac Catalyst and tvOS.
    /// </summary>
    [Theory]
    [InlineData(ApplePlatform.iOS, "net10.0", true, true)]
    [InlineData(ApplePlatform.iOS, "net11.0", true, true)]
    [InlineData(ApplePlatform.tvOS, "net10.0", true, true)]
    [InlineData(ApplePlatform.MacCatalyst, "net10.0", true, true)]
    [InlineData(ApplePlatform.macOS, "net11.0", true, false)]
    [InlineData(ApplePlatform.macOS, "net10.0", false, false)]
    public void StandaloneLoweringDefaults_FollowTheSdkWrapperCompile(ApplePlatform platform, string framework, bool collects, bool routes)
    {
        var info = PlatformInfoFactory.Create(platform, null, $"{framework}-{platform.ToString().ToLowerInvariant()}");
        var output = Path.Combine(Path.GetTempPath(), "standalone-defaults");
        var defaults = BindingsGeneratorCommand.StandaloneLoweringDefaults(
            hasXcframework: true, skipWrapperCompilation: false, sdkMode: false, explicitReceipt: null, explicitRoute: false,
            handAuthoredEvidence: false, noRouting: false, skipThunkCompilation: false, info, output);
        Assert.Equal(collects, defaults is not null);
        if (defaults is { } d)
        {
            Assert.Equal(Path.Combine(Path.GetFullPath(output), BindingsGeneratorCommand.StandaloneLoweringReceiptFileName), d.ReceiptPath);
            Assert.Equal(routes, d.Route);
        }
    }

    /// <summary>
    /// The defaults stand aside for every run that is not a standalone wrapper compile, or that says
    /// what to collect itself; --no-swift-bridge-routing keeps the receipt and drops the routing.
    /// </summary>
    [Theory]
    [InlineData(false, false, false, null, false, false, false)] // direct (-a/-d/-t) mode
    [InlineData(true, true, false, null, false, false, false)]   // wrapper deferred to --compile-wrapper-only
    [InlineData(true, false, true, null, false, false, false)]   // the SDK's generation pass
    [InlineData(true, false, false, "/tmp/r.json", false, false, false)] // an explicit receipt
    [InlineData(true, false, false, null, true, false, false)]   // an explicit --route-swift-bridges
    [InlineData(true, false, false, null, false, true, false)]   // hand-authored evidence routes at generation
    [InlineData(true, false, false, null, false, false, true)]   // the standalone run itself
    public void StandaloneLoweringDefaults_ApplyOnlyToAStandaloneWrapperCompile(
        bool hasXcframework, bool skipWrapper, bool sdkMode, string? explicitReceipt, bool explicitRoute, bool handAuthored, bool applies)
    {
        var info = PlatformInfoFactory.Create(ApplePlatform.iOS);
        var defaults = BindingsGeneratorCommand.StandaloneLoweringDefaults(
            hasXcframework, skipWrapper, sdkMode, explicitReceipt, explicitRoute, handAuthored, noRouting: false, skipThunkCompilation: false, info, "/tmp/out");
        Assert.Equal(applies, defaults is not null);
        var optedOut = BindingsGeneratorCommand.StandaloneLoweringDefaults(
            hasXcframework, skipWrapper, sdkMode, explicitReceipt, explicitRoute, handAuthored, noRouting: true, skipThunkCompilation: false, info, "/tmp/out");
        Assert.Equal(applies, optedOut is not null);
        if (optedOut is { } o)
            Assert.False(o.Route);
    }

    /// <summary>
    /// A run that skips the native thunks cannot assemble a bridge, so it collects the receipt and
    /// leaves every import as generated.
    /// </summary>
    [Fact]
    public void StandaloneLoweringDefaults_DoNotRouteWithoutTheNativeThunks()
    {
        var defaults = BindingsGeneratorCommand.StandaloneLoweringDefaults(
            hasXcframework: true, skipWrapperCompilation: false, sdkMode: false, explicitReceipt: null, explicitRoute: false,
            handAuthoredEvidence: false, noRouting: false, skipThunkCompilation: true, PlatformInfoFactory.Create(ApplePlatform.iOS), "/tmp/out");
        Assert.NotNull(defaults);
        Assert.False(defaults!.Value.Route);
    }

    /// <summary>
    /// A standalone run that collects elsewhere, or not at all, removes the default receipt it would
    /// otherwise leave beside a wrapper it no longer describes; a run given hand-authored evidence
    /// never touches it, since that evidence may be the receipt itself under another spelling of its path.
    /// </summary>
    [Theory]
    [InlineData(false, null, true)]
    [InlineData(false, "elsewhere.json", true)]
    [InlineData(false, StandaloneReceipt, false)]
    [InlineData(true, null, false)]
    public void ClearStaleStandaloneReceipt_RemovesOnlyAReceiptThisRunCannotBeReading(bool handAuthored, string? collectName, bool removed)
    {
        var output = Path.Combine(_dir, "out");
        Directory.CreateDirectory(output);
        var receipt = Path.Combine(output, StandaloneReceipt);
        File.WriteAllText(receipt, "{}");
        var collect = collectName == null ? null : Path.Combine(output, collectName);

        var cleared = BindingsGeneratorCommand.ClearStaleStandaloneReceipt(output, collect, handAuthored);

        Assert.Equal(removed, cleared != null);
        Assert.Equal(!removed, File.Exists(receipt));
    }

    private const string StandaloneReceipt = BindingsGeneratorCommand.StandaloneLoweringReceiptFileName;

    /// <summary>
    /// The receipt names its slices by the parsed platform, so every spelling --platform accepts
    /// collects the same slices (the evidence lane throws on a name it does not know).
    /// </summary>
    [Theory]
    [InlineData("ios", "ios")]
    [InlineData("tvos", "tvos")]
    [InlineData("maccatalyst", "maccatalyst")]
    [InlineData("mac-catalyst", "maccatalyst")]
    [InlineData("macos", "macos")]
    public void LoweringPlatform_IsCanonicalForEveryAcceptedSpelling(string spelling, string expected)
    {
        var platform = PlatformInfoFactory.ParsePlatform(spelling);
        Assert.NotNull(platform);
        var lowering = BindingsGeneratorCommand.LoweringPlatform(PlatformInfoFactory.Create(platform!.Value));
        Assert.Equal(expected, lowering);
        Assert.NotEmpty(SwiftArtifactScope.PlatformSlices(lowering));
    }
}
