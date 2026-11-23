// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.BindingTests.StaticDependencyForceLoad.cs — opt-in runtime gate for ForceLoad on a
// library-type SwiftFrameworkDependency.
//
// A library-type xcframework (each slice a bare .a) is linked into the wrapper by path, so the
// linker keeps only the archive members that resolve a symbol something already references. A
// library whose components register themselves from static initializers loses every component
// nothing else references, and later reports them as not registered. ForceLoad="true" on the
// dependency links the archive in full.
//
// The fixture is a static Swift framework that reads a registry living in a static C/C++/ObjC
// library-type xcframework. The registry's four components (two C __attribute__((constructor))
// functions, one C++ global constructor and one Objective-C class's +load) each sit in their own
// archive member and are referenced by nothing. The leg builds the same SDK-direct app twice,
// without and with ForceLoad, for the iOS Simulator and/or a phone (NativeAOT, or Mono full-AOT
// with --mono-aot), and runs both: without it the wrapper's registry must be empty, with it all
// four components must have registered by the time managed code reads it. Every app is built and
// checked structurally before any is deployed, so a run with no phone connected still proves the
// device builds and every check up to install.
//
// The primary also declares a SwiftUI view, so the binding emits a SwiftUI bridge: a second image
// that uses the primary, and so the archive. The archive's code must still live in one image only.
// Each registration also counts itself in the process environment, which every image shares, and
// the app loads the bridge before reading the counts: each component must have registered exactly
// once, the runtime must not report an Objective-C class implemented in two images, and of the
// app's images (executable, wrapper, bridge) at most one, the wrapper, may define any global
// symbol of the archive or of the static primary.
//
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    [Parameter("Opt-in: build a binding whose static library-type dependency registers components from static initializers, without and with ForceLoad, and run both on the iOS Simulator (--sim, the default) and/or a phone (--device: NativeAOT, or Mono full-AOT with --mono-aot). Never part of the default run or --compile-only.")]
    readonly bool StaticDependencyForceLoad;

    const string ForceLoadVersion = "0.0.0-forceload";
    const string ForceLoadModule = "SbForceLoad";
    const string ForceLoadRegistry = "SbFlRegistry";
    const string ForceLoadAppName = "ForceLoadApp";
    const string ForceLoadSimRid = "iossimulator-arm64";
    static readonly string[] ForceLoadComponents = { "alpha", "beta", "gamma", "delta" };
    const string ForceLoadObjCClass = "SbFlDelta";

    string ForceLoadLaneVersion => MixedPackageLaneVersion(ForceLoadVersion, RuntimeLane);
    string ForceLoadLaneAppleVersion => MixedPackageAppleVersion("forceload", RuntimeLane);
    AbsolutePath ForceLoadScratch => RootDirectory / "artifacts" / ("force-load" + MixedPackageLaneSuffix(RuntimeLane));

    // Where an app of the leg runs: the iOS Simulator, or a phone under NativeAOT (or the lane's
    // CoreCLR engine on .NET 11) or Mono full-AOT.
    sealed record ForceLoadPlatform(bool OnDevice, bool MonoAot)
    {
        public string Rid => OnDevice ? "ios-arm64" : ForceLoadSimRid;
        public string SliceId => OnDevice ? ApplePlatform.IOS.DeviceSliceId! : ApplePlatform.IOS.SimulatorSliceId;
        public string Name => OnDevice ? MonoAot ? "device-monoaot" : "device" : "simulator";
    }

    sealed record ForceLoadBuild(
        ForceLoadPlatform Platform, bool ForceLoad, AbsolutePath AppDir, AbsolutePath AppPath, string BundleId,
        long WrapperBytes, long BridgeBytes, bool AppCarriesArchive, IReadOnlyList<string> Failures)
    {
        public string Label => $"{Platform.Name}/{(ForceLoad ? "forced" : "lazy")}";
    }

    sealed record ForceLoadRun(ForceLoadBuild Build, string? Registered, string? Registrations, IReadOnlyList<string> Failures);

    // Builds and structurally checks every app first, then deploys and runs them, so a run that cannot
    // deploy (no phone connected) still reports every build and every structural check.
    void RunStaticDependencyForceLoadLeg(bool sim, bool device, bool monoAot)
    {
        Log.Information("====================================================");
        Log.Information(" BindingTests — static library dependency ForceLoad");
        Log.Information("   sim: {Sim}   device: {Device}{MonoAot}", sim, device, monoAot ? " (Mono full-AOT)" : "");
        Log.Information("====================================================");

        // Stages SwiftInterfaceParser, which the SDK package ships and the binding build runs; a
        // fresh profile workspace has not built it yet.
        EnsureGeneratorBuilt();
        var stagedBinary = SwiftInterfaceParserStagingDir / "SwiftInterfaceParser";
        if (!File.Exists(stagedBinary))
            throw new InvalidOperationException(
                $"--static-dependency-force-load: SwiftInterfaceParser is missing at '{stagedBinary}'.");

        var scratch = ForceLoadScratch;
        if (Directory.Exists(scratch)) scratch.DeleteDirectory();
        var nupkgDir = scratch / "packages";
        nupkgDir.CreateDirectory();

        using var scope = new VersionScope(ForceLoadLaneVersion, RootDirectory, ForceLoadLaneAppleVersion);
        BuildSdkDirectFeed(nupkgDir, scope, "force-load", ForceLoadLaneVersion, ForceLoadLaneAppleVersion);

        var registry = BuildForceLoadRegistryXcframework(scratch / "build" / "registry");
        var primary = BuildForceLoadPrimaryXcframework(scratch / "build" / "primary", registry);

        var platforms = new List<ForceLoadPlatform>();
        if (sim) platforms.Add(new ForceLoadPlatform(OnDevice: false, MonoAot: false));
        if (device) platforms.Add(new ForceLoadPlatform(OnDevice: true, MonoAot: monoAot));

        var builds = new List<ForceLoadBuild>();
        foreach (var platform in platforms)
            foreach (var forceLoad in new[] { false, true })
                builds.Add(BuildForceLoadConsumer(scratch, nupkgDir, primary, registry, platform, forceLoad));
        foreach (var build in builds)
        {
            Log.Information("force-load {Label}: wrapper={Wrapper} bytes bridge={Bridge} bytes app-executable-carries-archive={Carries}; structural check {Verdict}",
                build.Label, build.WrapperBytes, build.BridgeBytes, build.AppCarriesArchive, build.Failures.Count == 0 ? "passed" : "FAILED");
            foreach (var failure in build.Failures)
                Log.Error("force-load {Label}: {Failure}", build.Label, failure);
        }

        var runs = builds.Select(RunForceLoadConsumer).ToList();

        var receipt = string.Join(Environment.NewLine, runs.Select(r =>
            $"{r.Build.Label}: registered=[{r.Registered ?? "<not run>"}] registrations=[{r.Registrations ?? "<not run>"}] " +
            $"wrapper={r.Build.WrapperBytes} bytes bridge={r.Build.BridgeBytes} bytes app-executable-carries-archive={r.Build.AppCarriesArchive} " +
            $"structural={(r.Build.Failures.Count == 0 ? "ok" : "FAILED")}"));
        File.WriteAllText(scratch / "receipt.txt", receipt + Environment.NewLine);
        Log.Information("--static-dependency-force-load receipt:{NewLine}{Receipt}", Environment.NewLine, receipt);

        var failures = new List<string>();
        foreach (var run in runs)
            failures.AddRange(run.Build.Failures.Concat(run.Failures).Select(f => $"{run.Build.Label}: {f}"));
        var expected = string.Join(",", ForceLoadComponents.OrderBy(c => c, StringComparer.Ordinal));
        foreach (var run in runs.Where(r => r.Registered is not null))
        {
            if (!run.Build.ForceLoad && run.Registered != "")
                failures.Add($"{run.Build.Label}: without ForceLoad the wrapper's registry holds [{run.Registered}]; the fixture no longer reproduces a dropped self-registering member, so the ForceLoad result proves nothing.");
            if (run.Build.ForceLoad && run.Registered != expected)
                failures.Add($"{run.Build.Label}: with ForceLoad the wrapper's registry holds [{run.Registered}], expected [{expected}]: the dependency's archive was not linked in full.");
        }
        if (failures.Count > 0)
            Assert.Fail("--static-dependency-force-load:" + Environment.NewLine + string.Join(Environment.NewLine, failures));

        // Scratch apps are large; keep only the receipt and the fixture.
        foreach (var build in builds)
        {
            if (Directory.Exists(build.AppDir / "bin")) (build.AppDir / "bin").DeleteDirectory();
            if (Directory.Exists(build.AppDir / "obj")) (build.AppDir / "obj").DeleteDirectory();
        }
        if (Directory.Exists(nupkgDir)) nupkgDir.DeleteDirectory();
        Log.Information("--static-dependency-force-load OK — lazily linked wrapper registered nothing, the force-loaded one registered every component once, and the bridge carries none of the archive");
    }

    ForceLoadBuild BuildForceLoadConsumer(
        AbsolutePath scratch, AbsolutePath nupkgDir, AbsolutePath primary, AbsolutePath registry, ForceLoadPlatform platform, bool forceLoad)
    {
        var label = $"{platform.Name}/{(forceLoad ? "forced" : "lazy")}";
        var appDir = scratch / platform.Name / (forceLoad ? "forced" : "lazy");
        appDir.CreateDirectory();
        var bundleId = $"com.swiftbindings.forceload.{(forceLoad ? "forced" : "lazy")}";
        WriteForceLoadConsumerApp(appDir, primary, registry, forceLoad, bundleId, RuntimeLane, ForceLoadLaneVersion, platform);
        File.WriteAllText(appDir / "NuGet.config", MixedPackNuGetConfig(nupkgDir, fixtureNupkgDir: null));

        var appPath = BuildPackageConsumer(appDir, ForceLoadAppName, platform.OnDevice, "--static-dependency-force-load", label,
            platform.MonoAot, rid: platform.Rid);

        var failures = new List<string>();
        var wrapperName = $"{ForceLoadModule}SwiftBindings";
        var wrapperBinary = appPath / "Frameworks" / $"{wrapperName}.framework" / wrapperName;
        if (!File.Exists(wrapperBinary))
            Assert.Fail($"--static-dependency-force-load: the {label} app embeds no wrapper at {wrapperBinary}.");
        var bridgeName = $"{ForceLoadModule}Bridge";
        var bridgeBinary = appPath / "Frameworks" / $"{bridgeName}.framework" / bridgeName;
        if (!File.Exists(bridgeBinary))
            Assert.Fail($"--static-dependency-force-load: the {label} app embeds no SwiftUI bridge at {bridgeBinary}; the fixture's view no longer produces a bridge, so nothing tests a second image.");

        // The app executable references nothing in the archive (the wrapper does), so it should carry
        // none of it; recorded, since the SDK leaves the app's own NativeReference unchanged.
        var executable = appPath / ForceLoadAppName;
        var appCarriesArchive = File.Exists(executable)
            && DefinedSymbols(executable).Any(s => s.StartsWith("_sbfl_", StringComparison.Ordinal));

        failures.AddRange(ForceLoadSingleCarrierFailures(
            registry, primary, platform.SliceId, forceLoad,
            new[] { ("app executable", executable), ("wrapper", wrapperBinary), ("bridge", bridgeBinary) },
            wrapperName));
        if (ForceLoadBundleShapeProblem(appPath, platform) is { } shape)
            failures.Add(shape);

        return new ForceLoadBuild(platform, forceLoad, appDir, appPath, bundleId,
            new FileInfo(wrapperBinary).Length, new FileInfo(bridgeBinary).Length, appCarriesArchive, failures);
    }

    // A .NET 10 device app must be the runtime its lane names before it is ever deployed: a Mono
    // full-AOT bundle ships its managed assemblies, a NativeAOT one compiles them into the executable.
    string? ForceLoadBundleShapeProblem(AbsolutePath appPath, ForceLoadPlatform platform)
    {
        if (!platform.OnDevice || RuntimeLane.IsNet11)
            return null;
        var managed = File.Exists(appPath / $"{ForceLoadAppName}.dll");
        return managed == platform.MonoAot
            ? null
            : platform.MonoAot
                ? $"the Mono full-AOT bundle {appPath} carries no {ForceLoadAppName}.dll, so it was not built for Mono."
                : $"the NativeAOT bundle {appPath} carries {ForceLoadAppName}.dll, so it was not published NativeAOT.";
    }

    ForceLoadRun RunForceLoadConsumer(ForceLoadBuild build)
    {
        var failures = new List<string>();
        LaunchResult result;
        try
        {
            result = LaunchPackageConsumer(build.AppPath, ForceLoadAppName, build.BundleId, build.Platform.OnDevice,
                "--static-dependency-force-load", build.Label, []);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ProcessException)
        {
            failures.Add($"could not deploy or launch the app, so nothing ran: {ex.Message}");
            return new ForceLoadRun(build, null, null, failures);
        }
        Log.Information("=== CONSUMER OUTPUT ({Label}) ==={NewLine}{Output}", build.Label, Environment.NewLine, result.Output);
        if (LaunchDiagnostics.LauncherNeverStartedApp(result))
        {
            failures.Add($"the app was deployed but never started; no registry was read.\n{result.Output}");
            return new ForceLoadRun(build, null, null, failures);
        }
        if (RuntimeLane.IsNet11)
        {
            try { AssertLiveRuntimeLane(result, build.Platform.OnDevice); }
            catch (InvalidOperationException ex) { failures.Add(ex.Message); }
        }
        else if (ForceLoadNet10FlavorProblem(result.Output, build.Platform) is { } flavor)
            failures.Add(flavor);
        if (result.Result != TestResult.Success)
        {
            failures.Add($"the app did not report TEST SUCCESS (result={result.Result}).\n{result.Output}");
            return new ForceLoadRun(build, null, null, failures);
        }

        var (registered, registrations) = ParseForceLoadOutput(result.Output, build.Label, build.ForceLoad, failures);
        return new ForceLoadRun(build, registered, registrations, failures);
    }

    // The .NET 10 engine the app reported: Mono everywhere but a NativeAOT device.
    static string? ForceLoadNet10FlavorProblem(string output, ForceLoadPlatform platform)
    {
        var banner = DeviceRuntimeFlavorGate.ExtractBanner(output);
        if (banner is null)
            return "the app printed no runtime-flavor banner, so the engine it ran on is unproven.";
        var nativeAotLane = platform.OnDevice && !platform.MonoAot;
        var mono = banner.Contains("IsMonoRuntime=True", StringComparison.Ordinal);
        var nativeAot = banner.Contains("IsNativeAotRuntime=True", StringComparison.Ordinal);
        return nativeAot == nativeAotLane && mono == !nativeAotLane
            ? null
            : $"the app reported '{banner}', expected {(nativeAotLane ? "NativeAOT" : "Mono")}.";
    }

    // Reads the app's REGISTERED and REGISTRATIONS lines and checks the runtime half of the one-copy
    // rule (the app loads the bridge before it counts, and a failed load fails the app): no Objective-C
    // class was reported implemented in two images, and each component registered once with ForceLoad
    // and never without it.
    static (string Registered, string Registrations) ParseForceLoadOutput(
        string output, string label, bool forceLoad, List<string> failures)
    {
        var lines = output.Split('\n').Select(l => l.Trim()).ToList();
        string Bracketed(string marker)
        {
            var line = lines.FirstOrDefault(l => l.Contains(marker, StringComparison.Ordinal))
                ?? throw new Exception($"--static-dependency-force-load: the {label} app printed no {marker} line.\n{output}");
            var start = line.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            return line[start..line.IndexOf(']', start)];
        }
        static string Sorted(string list) =>
            string.Join(",", list.Split(',', StringSplitOptions.RemoveEmptyEntries).OrderBy(c => c, StringComparer.Ordinal));

        var registered = Sorted(Bracketed("REGISTERED:["));
        var registrations = Sorted(Bracketed("REGISTRATIONS:["));
        if (output.Contains("implemented in both", StringComparison.OrdinalIgnoreCase))
            failures.Add($"the Objective-C runtime reported a class implemented in two images: the archive's {ForceLoadObjCClass} was linked into more than one image.");
        var counts = registrations.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Split('='))
            .ToDictionary(e => e[0], e => int.Parse(e[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
        foreach (var component in ForceLoadComponents)
        {
            var count = counts.TryGetValue(component, out var n) ? n : 0;
            var want = forceLoad ? 1 : 0;
            if (count != want)
                failures.Add($"component '{component}' registered {count} time(s) across the process, expected {want}" +
                    (count > 1 ? ": its static initializer ran in more than one image, each into its own copy of the registry." : "."));
        }
        return (registered, registrations);
    }

    // The structural half of the one-copy rule. Every global symbol the dependency archive or the
    // static primary defines may be defined by at most one of the app's images, the bridge defines
    // none, and the bridge loads the wrapper it resolves them through. With ForceLoad the wrapper
    // defines every global of the archive.
    static IEnumerable<string> ForceLoadSingleCarrierFailures(
        AbsolutePath registry, AbsolutePath primary, string sliceId, bool forceLoad,
        IReadOnlyList<(string Name, AbsolutePath Binary)> images, string wrapperName)
    {
        var archive = registry / sliceId / $"lib{ForceLoadRegistry}.a";
        var primaryArchive = primary / sliceId / $"{ForceLoadModule}.framework" / ForceLoadModule;
        var archiveSymbols = DefinedSymbols(archive).Where(s => s.StartsWith('_')).ToHashSet(StringComparer.Ordinal);
        var primarySymbols = DefinedSymbols(primaryArchive).Where(s => s.StartsWith('_')).ToHashSet(StringComparer.Ordinal);
        if (!archiveSymbols.Contains($"_OBJC_CLASS_$_{ForceLoadObjCClass}") || !archiveSymbols.Contains("_sbfl_names"))
            yield return $"the registry archive {archive} defines no {ForceLoadObjCClass} class or sbfl_names; the fixture changed under the check.";
        if (primarySymbols.Count == 0)
            yield return $"the static primary {primaryArchive} defines no global symbol; the fixture changed under the check.";

        var defined = images.ToDictionary(i => i.Name, i => DefinedSymbols(i.Binary).ToHashSet(StringComparer.Ordinal));
        foreach (var (origin, symbols) in new[] { ("dependency archive", archiveSymbols), ("static primary", primarySymbols) })
        {
            foreach (var symbol in symbols.OrderBy(s => s, StringComparer.Ordinal))
            {
                var carriers = images.Where(i => defined[i.Name].Contains(symbol)).Select(i => i.Name).ToList();
                if (carriers.Count > 1)
                    yield return $"{origin} symbol {symbol} is defined in {carriers.Count} images ({string.Join(", ", carriers)}).";
                else if (carriers.Contains("bridge"))
                    yield return $"{origin} symbol {symbol} is defined in the bridge, which must resolve it through the wrapper.";
            }
        }
        if (forceLoad)
        {
            var missing = archiveSymbols.Where(s => !defined["wrapper"].Contains(s)).OrderBy(s => s, StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
                yield return $"with ForceLoad the wrapper does not define {string.Join(", ", missing)} from the archive.";
        }
        var bridge = images.Single(i => i.Name == "bridge").Binary;
        if (!LinkedDylibs(bridge).Any(d => d.Contains($"/{wrapperName}.framework/{wrapperName}", StringComparison.Ordinal)))
            yield return $"the bridge {bridge} does not load the wrapper {wrapperName}, so it cannot be resolving the static code through it.";
    }

    static IEnumerable<string> LinkedDylibs(AbsolutePath binary)
    {
        var process = ProcessTasks.StartProcess(XcRun.FindTool("otool"), ArgumentEscaper.Join(new[] { "-L", binary.ToString() }), logOutput: false);
        process.AssertWaitForExit();
        return process.Output.Where(o => o.Type == OutputType.Std).Skip(1).Select(o => o.Text.Trim().Split(' ')[0]);
    }

    static IEnumerable<string> DefinedSymbols(AbsolutePath binary)
    {
        var process = ProcessTasks.StartProcess(XcRun.FindTool("nm"), ArgumentEscaper.Join(new[] { "-gUj", binary.ToString() }), logOutput: false);
        process.AssertWaitForExit();
        return process.Output.Where(o => o.Type == OutputType.Std).Select(o => o.Text.Trim());
    }

    // The registry: one archive member holds the registry, and each component sits in its own member
    // that only a static initializer reaches. Two are C constructors, one a C++ global object and one
    // an Objective-C class's +load. Each registration also counts itself in the process environment,
    // which every image shares, so a component whose initializer runs in two images counts twice.
    AbsolutePath BuildForceLoadRegistryXcframework(AbsolutePath root)
    {
        if (Directory.Exists(root)) root.DeleteDirectory();
        var src = root / "src";
        var headers = root / "Headers";
        src.CreateDirectory();
        headers.CreateDirectory();
        File.WriteAllText(headers / $"{ForceLoadRegistry}.h", """
            #ifndef SBFL_REGISTRY_H
            #define SBFL_REGISTRY_H
            #ifdef __cplusplus
            extern "C" {
            #endif
            void sbfl_register(const char *name);
            const char *sbfl_names(void);
            #ifdef __cplusplus
            }
            #endif
            #endif
            """);
        File.WriteAllText(headers / "module.modulemap",
            $"module {ForceLoadRegistry} {{\n    header \"{ForceLoadRegistry}.h\"\n    link framework \"Foundation\"\n    export *\n}}\n");
        var sources = new List<AbsolutePath>();
        void Source(string name, string body)
        {
            File.WriteAllText(src / name, $"#include \"{ForceLoadRegistry}.h\"\n" + body);
            sources.Add(src / name);
        }
        Source("registry.c", """
            #include <stdio.h>
            #include <stdlib.h>
            #include <string.h>
            static char names[256];
            static int count;
            void sbfl_register(const char *name) {
                if (count > 0) strlcat(names, ",", sizeof names);
                strlcat(names, name, sizeof names);
                count++;
                char key[64], value[16];
                snprintf(key, sizeof key, "SBFL_REG_%s", name);
                const char *previous = getenv(key);
                snprintf(value, sizeof value, "%d", (previous ? atoi(previous) : 0) + 1);
                setenv(key, value, 1);
            }
            const char *sbfl_names(void) { return names; }
            """);
        Source("component_alpha.c", "__attribute__((constructor)) static void sbfl_alpha_init(void) { sbfl_register(\"alpha\"); }\n");
        Source("component_beta.c", "__attribute__((constructor)) static void sbfl_beta_init(void) { sbfl_register(\"beta\"); }\n");
        Source("component_gamma.cpp", "namespace { struct Gamma { Gamma() { sbfl_register(\"gamma\"); } } sbfl_gamma; }\n");
        Source("component_delta.m", $"#import <Foundation/Foundation.h>\n@interface {ForceLoadObjCClass} : NSObject\n@end\n@implementation {ForceLoadObjCClass}\n+ (void)load {{ sbfl_register(\"delta\"); }}\n@end\n");

        var ios = ApplePlatform.IOS;
        var args = new List<string> { "-create-xcframework" };
        foreach (var (slice, triple, sdkName) in new[]
        {
            (ios.DeviceSliceId!, ios.DeviceTarget!, ios.DeviceSdkName!),
            (ios.SimulatorSliceId, ios.SimulatorTarget, ios.SimulatorSdkName),
        })
        {
            var sliceDir = root / "slices" / slice;
            sliceDir.CreateDirectory();
            var sdkPath = XcRun.GetSdkPath(sdkName).ToString();
            var objects = new List<string>();
            foreach (var source in sources)
            {
                var obj = sliceDir / (Path.GetFileNameWithoutExtension(source) + ".o");
                var language = Path.GetExtension(source) switch { ".cpp" => "c++", ".m" => "objective-c", _ => "c" };
                RunPackGateTool("clang", new[]
                {
                    "-x", language, "-c", "-target", triple, "-isysroot", sdkPath,
                    "-I", headers.ToString(), source.ToString(), "-o", obj.ToString(),
                });
                objects.Add(obj);
            }
            var archive = sliceDir / $"lib{ForceLoadRegistry}.a";
            RunPackGateTool("ar", new[] { "rcs", archive.ToString() }.Concat(objects).ToList());
            args.AddRange(new[] { "-library", archive.ToString(), "-headers", headers.ToString() });
        }
        var xcframework = root / $"{ForceLoadRegistry}_library.xcframework";
        args.AddRange(new[] { "-output", xcframework.ToString() });
        RunPackGateTool("xcodebuild", args);
        Log.Information("  built library-type registry xcframework: {Path}", xcframework);
        return xcframework;
    }

    // The primary: a static Swift framework whose only use of the registry is reading it. The import
    // is implementation-only, so the module's interface does not name the C module. Its SwiftUI view
    // makes the binding emit a SwiftUI bridge, a second image that uses the primary.
    AbsolutePath BuildForceLoadPrimaryXcframework(AbsolutePath root, AbsolutePath registry)
    {
        if (Directory.Exists(root)) root.DeleteDirectory();
        root.CreateDirectory();
        var libSwift = root / "Lib.swift";
        File.WriteAllText(libSwift, $$"""
            @_implementationOnly import {{ForceLoadRegistry}}
            import SwiftUI

            public enum ForceLoadProbe {
                public static func registeredNames() -> String { String(cString: sbfl_names()) }
            }

            public struct ForceLoadProbeView: View {
                public init() {}
                public var body: some View { Text(ForceLoadProbe.registeredNames()) }
            }
            """);

        var ios = ApplePlatform.IOS;
        var frameworks = new List<AbsolutePath>();
        foreach (var (slice, triple, moduleSuffix, sdkName, plistPlatform) in new[]
        {
            (ios.DeviceSliceId!, ios.DeviceTarget!, ios.DeviceModuleSuffix!, ios.DeviceSdkName!, ios.DevicePlistPlatform!),
            (ios.SimulatorSliceId, ios.SimulatorTarget, ios.SimulatorModuleSuffix, ios.SimulatorSdkName, ios.SimulatorPlistPlatform),
        })
        {
            var sliceDir = root / slice;
            var frameworkDir = sliceDir / $"{ForceLoadModule}.framework";
            var modDir = frameworkDir / "Modules" / $"{ForceLoadModule}.swiftmodule";
            modDir.CreateDirectory();
            var sdkPath = XcRun.GetSdkPath(sdkName);
            var registrySlice = Directory.GetDirectories(registry)
                .Single(d => Path.GetFileName(d) == slice);
            var libO = sliceDir / "lib.o";
            var swiftInterfacePath = modDir / $"{moduleSuffix}.swiftinterface";
            SwiftCompiler.Execute(new SwiftCompilerSettings()
                .SetTarget(triple)
                .SetSdk(sdkPath)
                .SetModuleName(ForceLoadModule)
                .SetEmitModule()
                .SetEnableLibraryEvolution()
                .SetEmitModuleInterface()
                .SetOutputPath(libO)
                .SetModulePath(modDir / $"{moduleSuffix}.swiftmodule")
                .SetModuleInterfacePath(swiftInterfacePath)
                .AddExtraArgument("-parse-as-library")
                .AddExtraArgument("-emit-object")
                .AddExtraArgument("-I").AddExtraArgument(Path.Combine(registrySlice, "Headers"))
                .AddSourceFile(libSwift));
            RunPackGateTool("ar", new[] { "rcs", (frameworkDir / ForceLoadModule).ToString(), libO.ToString() });
            File.Copy(swiftInterfacePath, modDir / $"{moduleSuffix}.private.swiftinterface", overwrite: true);
            SwiftFrontend.Execute(new SwiftFrontendSettings()
                .SetSwiftInterfacePath(swiftInterfacePath)
                .SetTarget(triple)
                .SetModuleName(ForceLoadModule)
                .SetSdk(sdkPath)
                .SetAbiDescriptorPath(modDir / $"{moduleSuffix}.abi.json"));
            PlistGenerator.WriteFrameworkPlist(
                frameworkDir / "Info.plist",
                bundleId: $"com.swiftbindings.forceload.{ForceLoadModule}",
                bundleName: ForceLoadModule,
                executableName: ForceLoadModule,
                minOs: ios.MinOsVersion,
                plistPlatform: plistPlatform);
            frameworks.Add(frameworkDir);
        }

        var xcframework = root / $"{ForceLoadModule}.xcframework";
        var settings = new CreateXcframeworkSettings().SetOutputPath(xcframework);
        foreach (var framework in frameworks)
            settings = settings.AddFrameworkPath(framework);
        XcodeBuild.ExecuteCreateXcframework(settings);
        Log.Information("  built static Swift primary xcframework: {Path}", xcframework);
        return xcframework;
    }

    static void WriteForceLoadConsumerApp(
        AbsolutePath appDir, AbsolutePath primary, AbsolutePath registry, bool forceLoad,
        string bundleId, RuntimeTestLane lane, string packageVersion, ForceLoadPlatform platform)
    {
        File.WriteAllText(appDir / $"{ForceLoadAppName}.csproj", $"""
            <Project Sdk="SwiftBindings.Sdk/{packageVersion}">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{lane.Framework}-ios</TargetFramework>
                <RuntimeIdentifier>{platform.Rid}</RuntimeIdentifier>
                {MixedConsumerRuntimeProperties(lane, platform.OnDevice, platform.MonoAot)}
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <IsPackable>false</IsPackable>
                <ApplicationId>{bundleId}</ApplicationId>
                <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
                <ApplicationVersion>1</ApplicationVersion>
                <SupportedOSPlatformVersion>15.0</SupportedOSPlatformVersion>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <NoWarn>$(NoWarn);CA1416;CA1422;CS0649;CS0114;CS8604{(platform.OnDevice ? ";IL2121" : "")}</NoWarn>
              </PropertyGroup>
              <PropertyGroup Condition="'$(RuntimeIdentifier)' == '{ForceLoadSimRid}'">
                <MtouchLink>None</MtouchLink>
              </PropertyGroup>
              <!-- Device code signing (the development identity RuntimeTestsApp uses). -->
              <PropertyGroup Condition="'$(RuntimeIdentifier)' == 'ios-arm64'">
                <CodesignKey>Apple Development: Justin Wojciechowski (2YJ2TQ6Q7Q)</CodesignKey>
                <CodesignProvision>Swift Bindings Dev</CodesignProvision>
                <TeamIdentifierPrefix>HM68F9PH26</TeamIdentifierPrefix>
              </PropertyGroup>
              <ItemGroup>
                <None Include="Info.plist" />
              </ItemGroup>
              <ItemGroup>
                <SwiftFramework Include="{primary}" />
                <SwiftFrameworkDependency Include="{registry}" ForceLoad="{(forceLoad ? "true" : "false")}" />
              </ItemGroup>
            </Project>
            """);

        File.WriteAllText(appDir / "Program.cs", $$"""
            // Copyright (c) 2026 Justin Wojciechowski.
            // Licensed under the MIT License.
            using CoreFoundation;
            using Foundation;
            using UIKit;

            namespace ForceLoadApp;

            public static class Application
            {
                static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
            }

            public class AppDelegate : UIApplicationDelegate
            {
                public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions) => true;

                [System.Diagnostics.CodeAnalysis.DynamicDependency(
                    System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.Interfaces, typeof(SceneDelegate))]
                public override UISceneConfiguration GetConfiguration(UIApplication application,
                    UISceneSession connectingSceneSession, UISceneConnectionOptions options) =>
                    new("Default Configuration", connectingSceneSession.Role)
                    {
                        DelegateClass = new ObjCRuntime.Class(typeof(SceneDelegate))
                    };
            }

            // Blittable signatures only: the SDK applies DisableRuntimeMarshalling to the app.
            static class Native
            {
                [System.Runtime.InteropServices.DllImport("/usr/lib/libSystem.dylib")]
                public static extern IntPtr getenv(IntPtr name);

                public static string? GetEnvironment(string name)
                {
                    var utf8 = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(name);
                    try { return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(getenv(utf8)); }
                    finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(utf8); }
                }
            }

            [Register("SceneDelegate")]
            public class SceneDelegate : UIWindowSceneDelegate
            {
                private static bool _probeStarted;

                public override UIWindow? Window { get; set; }

                public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
                {
                    if (scene is not UIWindowScene windowScene)
                        return;
                    Window = new UIWindow(windowScene);
                    Window.RootViewController = new UIViewController();
                    Window.MakeKeyAndVisible();
                    if (_probeStarted)
                        return;
                    _probeStarted = true;
                    DispatchQueue.MainQueue.DispatchAsync(RunProbe);
                }

                static void RunProbe()
                {
                    try
                    {
                        {{MixedConsumerRuntimeBanner()}}
                        // The registry the wrapper links; its components register before managed code runs.
                        Console.WriteLine("REGISTERED:[" + global::{{ForceLoadModule}}.ForceLoadProbe.GetRegisteredNames() + "]");
                        // Load the SwiftUI bridge too, so a copy of the archive in it would have run its
                        // static initializers before the process-wide counts are read.
                        var bridge = System.IO.Path.Combine(NSBundle.MainBundle.PrivateFrameworksPath!,
                            "{{ForceLoadModule}}Bridge.framework", "{{ForceLoadModule}}Bridge");
                        System.Runtime.InteropServices.NativeLibrary.Load(bridge);
                        var counts = new System.Collections.Generic.List<string>();
                        foreach (var component in new[] { {{string.Join(", ", ForceLoadComponents.Select(c => $"\"{c}\""))}} })
                        {
                            var value = Native.GetEnvironment("SBFL_REG_" + component);
                            if (value != null)
                                counts.Add(component + "=" + value);
                        }
                        Console.WriteLine("REGISTRATIONS:[" + string.Join(",", counts) + "]");
                        Console.WriteLine("RESULTS FLUSHED");
                        Console.WriteLine("TEST SUCCESS");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("RESULTS FLUSHED");
                        Console.WriteLine("TEST FAILURE: " + ex);
                    }
                }
            }
            """);

        File.WriteAllText(appDir / "Info.plist",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\">\n<dict>\n" +
            "    <key>UILaunchScreen</key>\n    <dict/>\n" +
            "    <key>UIApplicationSceneManifest</key>\n" +
            "    <dict>\n" +
            "        <key>UIApplicationSupportsMultipleScenes</key>\n" +
            "        <false/>\n" +
            "        <key>UISceneConfigurations</key>\n" +
            "        <dict>\n" +
            "            <key>UIWindowSceneSessionRoleApplication</key>\n" +
            "            <array>\n" +
            "                <dict>\n" +
            "                    <key>UISceneConfigurationName</key>\n" +
            "                    <string>Default Configuration</string>\n" +
            "                    <key>UISceneDelegateClassName</key>\n" +
            "                    <string>SceneDelegate</string>\n" +
            "                </dict>\n" +
            "            </array>\n" +
            "        </dict>\n" +
            "    </dict>\n" +
            "</dict>\n</plist>\n");
    }
}
