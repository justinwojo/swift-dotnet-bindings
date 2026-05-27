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
        // Pure ordering edge for strict-graph mode. This target is consumed only by Pack
        // (Pack.DependsOn). Pack also runs after PackGate, so the supplement and PackGate
        // are both predecessors of Pack and must be totally ordered. Chaining the supplement
        // after PackGate lets it inherit PackGate's full ancestry, linearizing it into the
        // spine immediately before Pack — one edge, no unordered sibling. PackGate builds its
        // own throwaway-version supplement, so it does not consume this target's output;
        // and this edge does not pull the supplement build into a standalone PackGate run.
        .After(PackGate)
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
        sliceDir.CreateDirectory();

        Log.Information("--- Compiling {Module} slice {Slice} (target {Target}) ---",
            AppleSupplementModuleName, sliceId, target);

        CompileAppleSupplementFramework(
            target, sdkPath, moduleSuffix, plistPlatform, platform.MinOsVersion, frameworkDir, sourceFiles);

        // Device slices stay arm64-only — there is no x86_64 device target. The
        // simulator/host slices (macOS, iOS-sim, tvOS-sim, Mac Catalyst) all have an
        // x86_64 target, so fold one in to make the slice universal. Without this the
        // shipped supplement is arm64-only and x86_64 (Intel/Rosetta) consumers fail to
        // resolve it — the SDK injects an implicit SwiftBindings.Apple reference into
        // every non-ObjC binding, so this gates osx-x64 (and the other x86_64 RIDs).
        if (!deviceSlice)
        {
            var x64Target = target.Replace("arm64", "x86_64");
            var x64Suffix = moduleSuffix.Replace("arm64", "x86_64");
            var x64FrameworkDir = sliceDir / "x86_64" / $"{AppleSupplementModuleName}.framework";
            Log.Information("--- Folding x86_64 into {Slice} (target {Target}) ---", sliceId, x64Target);
            CompileAppleSupplementFramework(
                x64Target, sdkPath, x64Suffix, plistPlatform, platform.MinOsVersion, x64FrameworkDir, sourceFiles);

            // lipo can't write to one of its own inputs — merge to a temp, then replace.
            var fatBin = sliceDir / $"{AppleSupplementModuleName}.fat";
            RunLipoCreate(
                new[] { frameworkDir / AppleSupplementModuleName, x64FrameworkDir / AppleSupplementModuleName },
                fatBin);
            File.Delete(frameworkDir / AppleSupplementModuleName);
            File.Move(fatBin, frameworkDir / AppleSupplementModuleName);

            // Fold the x86_64 swiftmodule artifacts alongside the arm64 ones. Each
            // arch's files are suffix-named (e.g. x86_64-apple-macos.swiftmodule), so
            // they coexist in the single .swiftmodule directory.
            var armModules = frameworkDir / "Modules" / $"{AppleSupplementModuleName}.swiftmodule";
            var x64Modules = x64FrameworkDir / "Modules" / $"{AppleSupplementModuleName}.swiftmodule";
            foreach (var file in Directory.EnumerateFiles(x64Modules))
                File.Copy(file, armModules / Path.GetFileName(file), overwrite: true);
        }

        return frameworkDir;
    }

    void CompileAppleSupplementFramework(
        string target, string sdkPath, string moduleSuffix, string plistPlatform,
        string minOsVersion, AbsolutePath frameworkDir, IReadOnlyList<string> sourceFiles)
    {
        var moduleDir = frameworkDir / "Modules" / $"{AppleSupplementModuleName}.swiftmodule";
        moduleDir.CreateDirectory();

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
            minOsVersion,
            plistPlatform);
    }

    IReadOnlyList<string> CollectAppleSupplementSources()
    {
        if (!Directory.Exists(AppleSupplementShimsDir))
            return System.Array.Empty<string>();
        return Directory.GetFiles(AppleSupplementShimsDir, "*.swift", SearchOption.AllDirectories);
    }
}
