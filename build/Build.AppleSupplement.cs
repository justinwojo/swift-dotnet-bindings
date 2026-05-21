// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.AppleSupplement.cs — SwiftBindingsAppleSupplement.xcframework producer
//
// Builds the host Swift framework that carries the @_cdecl trampolines used by
// SwiftBindings.Apple consumers (KVO observe extensions, AttributedString
// attribute getters/setters, …). The framework ships as a single multi-slice
// xcframework inside the SwiftBindings.Apple NuGet package so consumers don't
// need to compile any Swift themselves; SwiftFrameworkResolver routes
// `[LibraryImport("SwiftBindingsAppleSupplement")]` to the framework via its
// `@rpath/{name}.framework/{name}` search path.
//
// Sources live next to the managed Apple supplement code at
// src/Swift.Bindings.Apple/Shims/*.swift. The xcframework output lands at
// src/Swift.Bindings.Apple/native/SwiftBindingsAppleSupplement.xcframework/
// (gitignored, rebuilt by `nuke build-apple-supplement-xcframework`) and is
// then packed by Swift.Bindings.Apple.csproj.

using System.Collections.Generic;
using System.IO;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

partial class Build
{
    const string AppleSupplementModuleName = "SwiftBindingsAppleSupplement";

    AbsolutePath AppleSupplementDir => SourceDir / "Swift.Bindings.Apple";
    AbsolutePath AppleSupplementShimsDir => AppleSupplementDir / "Shims";
    AbsolutePath AppleSupplementXcframeworkDir =>
        AppleSupplementDir / "native" / $"{AppleSupplementModuleName}.xcframework";
    AbsolutePath AppleSupplementBuildScratchDir =>
        AppleSupplementDir / "native" / ".scratch";

    // All four Apple platforms ship inside one xcframework. macOS and MacCatalyst
    // have no device slice; iOS and tvOS get both simulator and device.
    static readonly ApplePlatform[] s_appleSupplementPlatforms = new[]
    {
        ApplePlatform.IOS,
        ApplePlatform.MacOS,
        ApplePlatform.MacCatalyst,
        ApplePlatform.TvOS,
    };

    Target BuildAppleSupplementXcframework => _ => _
        .Description($"Build {AppleSupplementModuleName}.xcframework from src/Swift.Bindings.Apple/Shims/*.swift across all Apple platforms.")
        .Executes(() => RunBuildAppleSupplementXcframework());

    void RunBuildAppleSupplementXcframework()
    {
        var sourceFiles = CollectAppleSupplementSources();
        if (sourceFiles.Count == 0)
            throw new System.InvalidOperationException(
                $"No Swift sources found under {AppleSupplementShimsDir}. " +
                "The Apple supplement requires at least one *.swift shim to produce a non-empty framework.");

        Log.Information("=== Building {Module}.xcframework ===", AppleSupplementModuleName);
        Log.Information("Shims: {Count} file(s) under {Dir}", sourceFiles.Count, AppleSupplementShimsDir);

        // Wipe scratch + final xcframework so an old slice cannot survive a platform-list shrink.
        if (Directory.Exists(AppleSupplementBuildScratchDir))
            AppleSupplementBuildScratchDir.DeleteDirectory();
        AppleSupplementBuildScratchDir.CreateDirectory();
        if (Directory.Exists(AppleSupplementXcframeworkDir))
            AppleSupplementXcframeworkDir.DeleteDirectory();

        var frameworkSlices = new List<string>();
        foreach (var platform in s_appleSupplementPlatforms)
        {
            frameworkSlices.Add(BuildAppleSupplementSlice(platform, deviceSlice: false, sourceFiles));
            if (platform.HasDeviceSlice)
                frameworkSlices.Add(BuildAppleSupplementSlice(platform, deviceSlice: true, sourceFiles));
        }

        Log.Information("--- Combining {Count} slice(s) into {Xcframework} ---",
            frameworkSlices.Count, AppleSupplementXcframeworkDir);

        var settings = new CreateXcframeworkSettings()
            .SetOutputPath(AppleSupplementXcframeworkDir);
        foreach (var slice in frameworkSlices)
            settings.AddFrameworkPath(slice);
        XcodeBuild.ExecuteCreateXcframework(settings);

        // Scratch directory is no longer needed once xcodebuild has copied the
        // framework slices into the final xcframework bundle.
        if (Directory.Exists(AppleSupplementBuildScratchDir))
            AppleSupplementBuildScratchDir.DeleteDirectory();

        Log.Information("=== {Module}.xcframework built at {Path} ===",
            AppleSupplementModuleName, AppleSupplementXcframeworkDir);
    }

    string BuildAppleSupplementSlice(ApplePlatform platform, bool deviceSlice, IReadOnlyList<string> sourceFiles)
    {
        string sdkName, target, sliceId, moduleSuffix, plistPlatform;
        if (deviceSlice)
        {
            sdkName = platform.DeviceSdkName!;
            target = platform.DeviceTarget!;
            sliceId = platform.DeviceSliceId!;
            moduleSuffix = platform.DeviceModuleSuffix!;
            plistPlatform = platform.DevicePlistPlatform!;
        }
        else
        {
            sdkName = platform.SimulatorSdkName;
            target = platform.SimulatorTarget;
            sliceId = platform.SimulatorSliceId;
            moduleSuffix = platform.SimulatorModuleSuffix;
            plistPlatform = platform.SimulatorPlistPlatform;
        }

        var sdkPath = XcRun.GetSdkPath(sdkName);
        var sliceDir = AppleSupplementBuildScratchDir / sliceId;
        var frameworkDir = sliceDir / $"{AppleSupplementModuleName}.framework";
        var moduleDir = frameworkDir / "Modules" / $"{AppleSupplementModuleName}.swiftmodule";
        sliceDir.CreateDirectory();
        moduleDir.CreateDirectory();

        Log.Information("--- Compiling {Module} slice {Slice} (target {Target}) ---",
            AppleSupplementModuleName, sliceId, target);

        // Library-evolution + module interface so the framework is ABI-stable across
        // Swift toolchain versions, mirroring how BindingTests' fixture xcframework is
        // built. This is what lets consumers built against an older SwiftBindings.Apple
        // nupkg continue resolving symbols against a framework copy compiled with a
        // newer Swift.
        var settings = new SwiftCompilerSettings()
            .SetTarget(target)
            .SetSdk(sdkPath)
            .SetEmitModule()
            .SetEmitLibrary()
            .SetEnableLibraryEvolution()
            .SetEmitModuleInterface()
            .SetModuleName(AppleSupplementModuleName)
            .SetInstallName($"@rpath/{AppleSupplementModuleName}.framework/{AppleSupplementModuleName}")
            .SetOutputPath(frameworkDir / AppleSupplementModuleName)
            .SetModulePath(moduleDir / $"{moduleSuffix}.swiftmodule")
            .SetModuleInterfacePath(moduleDir / $"{moduleSuffix}.swiftinterface")
            .AddSourceFiles(sourceFiles);

        SwiftCompiler.Execute(settings);

        // Mirror the public swiftinterface to .private.swiftinterface so consumers
        // that resolve via the private path (which Apple's tooling sometimes
        // prefers) still find the module — same trick the BindingTests pipeline uses.
        File.Copy(
            moduleDir / $"{moduleSuffix}.swiftinterface",
            moduleDir / $"{moduleSuffix}.private.swiftinterface",
            overwrite: true);

        PlistGenerator.WriteFrameworkPlist(
            frameworkDir / "Info.plist",
            $"com.swiftbindings.{AppleSupplementModuleName}",
            AppleSupplementModuleName,
            AppleSupplementModuleName,
            platform.MinOsVersion,
            plistPlatform);

        return frameworkDir;
    }

    IReadOnlyList<string> CollectAppleSupplementSources()
    {
        if (!Directory.Exists(AppleSupplementShimsDir))
            return System.Array.Empty<string>();
        return Directory.GetFiles(AppleSupplementShimsDir, "*.swift", SearchOption.AllDirectories);
    }
}
