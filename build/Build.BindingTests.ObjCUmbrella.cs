// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

// W-1 — the pure-ObjC clang-umbrella BindingTests fixture. This is the first authored
// `module.modulemap` fixture in the harness: a synthetic Objective-C framework
// (`Sources/ObjCUmbrella/`) built with clang, generated through the generator's `--objc`
// pipeline (bgen ApiDefinition.cs), and consumed by RuntimeTestsApp via a single
// ProjectReference to the emitted binding csproj. It is the durable regression gate for the
// generator behaviors a real pure-ObjC clang-umbrella library (e.g. MapLibre) depends on —
// duplicate-selector flattening, static-inline exclusion, protocol-typed collection round-trip,
// optional-delegate reverse dispatch, and the ML-1 property-vs-method disambiguation (commit
// 3e5a0a5e). It rides the same lifecycle as the Swift fixture: BuildXcframework builds its
// xcframework alongside SwiftBindingsTestLib's, and RegenerateBindings runs `--objc` right
// after the Swift generate. Both are iOS-only (the fixture targets the iOS RuntimeTestsApp /
// CompileCheck; other platforms simply skip it and the ProjectReference's Exists() gate drops).
partial class Build
{
    const string ObjCUmbrellaModule = "ObjCUmbrella";

    // Source lives OUTSIDE Sources/SwiftBindingsTestLib so GetMainSourceFiles' *.swift glob
    // never sweeps it into the Swift target (which would misclassify the framework as Mixed).
    AbsolutePath BtObjCSourceDir => BindingTestsDir / "Sources" / ObjCUmbrellaModule;
    AbsolutePath BtObjCIncludeDir => BtObjCSourceDir / "include";

    // The ObjC fixture xcframework is built into .build (which BuildXcframework wipes at the
    // START of its run, before this method is called, so it survives) and its `--objc` output
    // lands in a SIBLING of output/ (output-objc/), because the ObjC pipeline emits FIXED
    // filenames (ApiDefinition.cs / StructsAndEnums.cs) that would otherwise collide with the
    // Swift path's output/ and because RegenerateBindings wipes output/ wholesale.
    AbsolutePath BtObjCXcframeworkDir => BtBuildDir / $"{ObjCUmbrellaModule}.xcframework";
    AbsolutePath BtObjCOutputDir => BindingTestsDir / "output-objc";
    AbsolutePath BtObjCCsproj => BtObjCOutputDir / $"{ObjCUmbrellaModule}.ObjC.iOS.csproj";

    // Only iOS carries the ObjC fixture — it targets the iOS RuntimeTestsApp/CompileCheck, and
    // the runtime gate is sim-only by design (device is NativeAOT via --device). Other platforms
    // skip it entirely.
    static bool ObjCUmbrellaAppliesTo(ApplePlatform platform) => platform.Name == "ios";

    /// <summary>
    /// Builds the pure-ObjC ObjCUmbrella.xcframework (simulator slice always; device slice when
    /// includeDevice) with clang. Mirrors the Swift RunBuildXcframework recipe: compile the .m to
    /// an object, link a dynamic framework binary, assemble Headers/ + Modules/module.modulemap +
    /// Info.plist, then combine the slices via xcodebuild -create-xcframework.
    /// </summary>
    void BuildObjCUmbrellaXcframework(ApplePlatform platform, bool includeDevice)
    {
        if (!ObjCUmbrellaAppliesTo(platform))
            return;

        Log.Information("=== Building {Module} (pure-ObjC fixture) ===", ObjCUmbrellaModule);

        var simSliceDir = BtBuildDir / platform.SimulatorSliceId / ObjCUmbrellaModule;
        var simFwDir = BuildObjCUmbrellaSlice(
            simSliceDir, platform.SimulatorTarget, platform.SimulatorSdkName,
            platform.MinOsVersion, platform.SimulatorPlistPlatform);

        var settings = new CreateXcframeworkSettings()
            .AddFrameworkPath(simFwDir)
            .SetOutputPath(BtObjCXcframeworkDir);

        if (includeDevice && platform.HasDeviceSlice)
        {
            var devSliceDir = BtBuildDir / platform.DeviceSliceId! / ObjCUmbrellaModule;
            var devFwDir = BuildObjCUmbrellaSlice(
                devSliceDir, platform.DeviceTarget!, platform.DeviceSdkName!,
                platform.MinOsVersion, platform.DevicePlistPlatform!);
            settings.AddFrameworkPath(devFwDir);
        }

        if (Directory.Exists(BtObjCXcframeworkDir))
            BtObjCXcframeworkDir.DeleteDirectory();
        XcodeBuild.ExecuteCreateXcframework(settings);
        Log.Information("ObjC fixture xcframework: {Dir}", BtObjCXcframeworkDir);
    }

    /// <summary>
    /// Compiles + links one framework slice for the ObjC fixture and returns its .framework dir.
    /// </summary>
    AbsolutePath BuildObjCUmbrellaSlice(
        AbsolutePath sliceDir, string target, string sdkName, string minOs, string plistPlatform)
    {
        var sdkPath = XcRun.GetSdkPath(sdkName);
        var frameworkDir = sliceDir / $"{ObjCUmbrellaModule}.framework";
        var headersDir = frameworkDir / "Headers";
        var modulesDir = frameworkDir / "Modules";

        if (Directory.Exists(sliceDir))
            sliceDir.DeleteDirectory();
        headersDir.CreateDirectory();
        modulesDir.CreateDirectory();

        var objectFile = sliceDir / $"{ObjCUmbrellaModule}.o";
        var binaryPath = frameworkDir / ObjCUmbrellaModule;

        // Compile the .m against the fixture's own include/ (resolves #import "ObjCUmbrella.h").
        RunObjCUmbrellaTool("clang", new[]
        {
            "-x", "objective-c", "-c", "-target", target,
            "-isysroot", sdkPath.ToString(), "-fobjc-arc",
            "-I", BtObjCIncludeDir.ToString(),
            (BtObjCSourceDir / $"{ObjCUmbrellaModule}.m").ToString(),
            "-o", objectFile.ToString(),
        });

        // Link a dynamic framework binary. Foundation is linked for the NSString / NSArray literals
        // and UIKit for Shape 7's UIApplicationState reference; the install_name is the framework
        // @rpath so the app loader resolves it.
        RunObjCUmbrellaTool("clang", new[]
        {
            "-dynamiclib", "-target", target, "-isysroot", sdkPath.ToString(),
            "-framework", "Foundation", "-framework", "UIKit",
            "-install_name", $"@rpath/{ObjCUmbrellaModule}.framework/{ObjCUmbrellaModule}",
            objectFile.ToString(), "-o", binaryPath.ToString(),
        });

        // Assemble the framework payload: umbrella header + modulemap (the non-Swift header is
        // what flips framework-type detection to pure ObjC) + Info.plist.
        File.Copy(BtObjCIncludeDir / $"{ObjCUmbrellaModule}.h", headersDir / $"{ObjCUmbrellaModule}.h", overwrite: true);
        File.WriteAllText(modulesDir / "module.modulemap",
            $"framework module {ObjCUmbrellaModule} {{\n" +
            $"    umbrella header \"{ObjCUmbrellaModule}.h\"\n" +
            "    export *\n" +
            "    module * { export * }\n" +
            "}\n");
        PlistGenerator.WriteFrameworkPlist(
            frameworkDir / "Info.plist",
            bundleId: $"com.swiftbindings.{ObjCUmbrellaModule.ToLowerInvariant()}",
            bundleName: ObjCUmbrellaModule,
            executableName: ObjCUmbrellaModule,
            minOs: minOs,
            plistPlatform: plistPlatform);

        return frameworkDir;
    }

    /// <summary>
    /// Regenerates the ObjC fixture bindings via the generator's `--objc` pipeline into
    /// output-objc/ (fixed filenames: ApiDefinition.cs / StructsAndEnums.cs + the emitted
    /// {Module}.ObjC.iOS.csproj bgen binding project). Called from RunRegenerateBindings after
    /// the Swift generate. Always fail-closed: a pure-ObjC generate has no "unsupported feature"
    /// degradation path, so any non-zero exit is a real regression.
    /// </summary>
    void RegenerateObjCUmbrellaBindings(ApplePlatform platform)
    {
        if (!ObjCUmbrellaAppliesTo(platform))
            return;

        Log.Information("=== Regenerating {Module} bindings (--objc) ===", ObjCUmbrellaModule);

        if (Directory.Exists(BtObjCOutputDir))
            BtObjCOutputDir.DeleteDirectory();
        BtObjCOutputDir.CreateDirectory();

        var genArgs = new List<string>
        {
            $"\"{GeneratorDll}\"",
            "--objc",
            $"--xcframework \"{BtObjCXcframeworkDir}\"",
            $"-o \"{BtObjCOutputDir}\"",
        };

        var proc = ProcessTasks.StartProcess(
            "dotnet", string.Join(" ", genArgs),
            workingDirectory: BindingTestsDir,
            logOutput: false);
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            foreach (var line in proc.Output)
                Log.Information("[objc-generator] {Text}", line.Text);
            throw new Exception($"ObjC fixture generator exited with code {proc.ExitCode}");
        }

        Log.Information("ObjC fixture bindings: {Csproj}", BtObjCCsproj);
    }

    static void RunObjCUmbrellaTool(string tool, IReadOnlyList<string> args)
    {
        ProcessTasks.StartProcess(XcRun.FindTool(tool), ArgumentEscaper.Join(args), logOutput: false)
            .AssertWaitForExit()
            .AssertZeroExitCode();
    }
}
