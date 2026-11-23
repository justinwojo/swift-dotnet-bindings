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
// The fixture is a static Swift framework that reads a registry living in a static C/C++
// library-type xcframework. The registry's three components (two C __attribute__((constructor))
// functions and one C++ global constructor) each sit in their own archive member and are
// referenced by nothing. The leg builds the same SDK-direct app twice on the iOS Simulator,
// without and with ForceLoad, and runs both: without it the wrapper's registry must be empty,
// with it all three components must have registered by the time managed code reads it. It also
// records both wrapper sizes and whether the app executable carries any of the archive.
//
// OPT-IN, sim-only. It packs a throwaway Runtime/Sdk/Apple feed and builds two apps, so it is
// minutes, not seconds, and needs a booted simulator. Run it after changes to how dependency
// archives are linked (DependencyLinkInputs, the wrapper/bridge/thunk link lines) or to the
// SDK's SwiftFrameworkDependency handling.

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
    [Parameter("Opt-in: build a binding whose static library-type dependency registers components from static initializers, without and with ForceLoad, and run both on the iOS Simulator. Sim-only; never part of the default run or --compile-only.")]
    readonly bool StaticDependencyForceLoad;

    const string ForceLoadVersion = "0.0.0-forceload";
    const string ForceLoadModule = "SbForceLoad";
    const string ForceLoadRegistry = "SbFlRegistry";
    const string ForceLoadAppName = "ForceLoadApp";
    const string ForceLoadSimRid = "iossimulator-arm64";
    static readonly string[] ForceLoadComponents = { "alpha", "beta", "gamma" };

    string ForceLoadLaneVersion => MixedPackageLaneVersion(ForceLoadVersion, RuntimeLane);
    string ForceLoadLaneAppleVersion => MixedPackageAppleVersion("forceload", RuntimeLane);
    AbsolutePath ForceLoadScratch => RootDirectory / "artifacts" / ("force-load" + MixedPackageLaneSuffix(RuntimeLane));

    sealed record ForceLoadRun(bool ForceLoad, string Registered, long WrapperBytes, bool AppCarriesArchive);

    void RunStaticDependencyForceLoadLeg()
    {
        Log.Information("====================================================");
        Log.Information(" BindingTests — static library dependency ForceLoad");
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

        var simUdid = SimulatorUdidFor("--static-dependency-force-load");
        var sim = !string.IsNullOrEmpty(simUdid)
            ? new SimCtl.SimDevice(simUdid, "pre-booted", "Booted", true, "")
            : SimCtl.EnsureBootedDevice();
        Log.Information("    simulator: {Name} ({Udid})", sim.Name, sim.Udid);

        var runs = new List<ForceLoadRun>();
        foreach (var forceLoad in new[] { false, true })
            runs.Add(RunForceLoadConsumer(scratch, nupkgDir, primary, registry, forceLoad, sim.Udid));

        var lazy = runs.Single(r => !r.ForceLoad);
        var full = runs.Single(r => r.ForceLoad);
        var receipt = string.Join(Environment.NewLine, runs.Select(r =>
            $"ForceLoad={r.ForceLoad}: registered=[{r.Registered}] wrapper={r.WrapperBytes} bytes app-executable-carries-archive={r.AppCarriesArchive}"));
        File.WriteAllText(scratch / "receipt.txt", receipt + Environment.NewLine);
        Log.Information("--static-dependency-force-load receipt:{NewLine}{Receipt}", Environment.NewLine, receipt);

        var failures = new List<string>();
        if (lazy.Registered != "")
            failures.Add($"without ForceLoad the wrapper's registry holds [{lazy.Registered}]; the fixture no longer reproduces a dropped self-registering member, so the ForceLoad result below proves nothing.");
        var expected = string.Join(",", ForceLoadComponents.OrderBy(c => c, StringComparer.Ordinal));
        if (full.Registered != expected)
            failures.Add($"with ForceLoad the wrapper's registry holds [{full.Registered}], expected [{expected}]: the dependency's archive was not linked in full.");
        if (failures.Count > 0)
            Assert.Fail("--static-dependency-force-load:" + Environment.NewLine + string.Join(Environment.NewLine, failures));

        // Scratch apps are large; keep only the receipt and the fixture.
        foreach (var r in runs)
        {
            var appDir = scratch / (r.ForceLoad ? "forced" : "lazy");
            if (Directory.Exists(appDir / "bin")) (appDir / "bin").DeleteDirectory();
            if (Directory.Exists(appDir / "obj")) (appDir / "obj").DeleteDirectory();
        }
        if (Directory.Exists(nupkgDir)) nupkgDir.DeleteDirectory();
        Log.Information("--static-dependency-force-load OK — lazily linked wrapper registered nothing, the force-loaded one registered every component");
    }

    ForceLoadRun RunForceLoadConsumer(
        AbsolutePath scratch, AbsolutePath nupkgDir, AbsolutePath primary, AbsolutePath registry, bool forceLoad, string simUdid)
    {
        var label = forceLoad ? "forced" : "lazy";
        var appDir = scratch / label;
        appDir.CreateDirectory();
        var bundleId = $"com.swiftbindings.forceload.{label}";
        WriteForceLoadConsumerApp(appDir, primary, registry, forceLoad, bundleId, RuntimeLane, ForceLoadLaneVersion);
        File.WriteAllText(appDir / "NuGet.config", MixedPackNuGetConfig(nupkgDir, fixtureNupkgDir: null));

        Log.Information("=== force-load: building the {Label} SDK-direct app ({Configuration}) ===", label, BtConfiguration);
        DotNetBuild(s => s
            .SetProjectFile(appDir / $"{ForceLoadAppName}.csproj")
            .SetConfiguration(BtConfiguration)
            .EnableNoLogo()
            .SetVerbosity(DotNetVerbosity.quiet));

        var appPath = Directory
            .GetDirectories(appDir / "bin", $"{ForceLoadAppName}.app", SearchOption.AllDirectories)
            .Where(d => d.Contains(BtConfiguration, StringComparison.Ordinal) && d.Contains(ForceLoadSimRid, StringComparison.Ordinal))
            .OrderBy(d => d.Length).ThenBy(d => d, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new Exception($"--static-dependency-force-load: {ForceLoadAppName}.app not found after the {label} build");

        var wrapperName = $"{ForceLoadModule}SwiftBindings";
        var wrapperBinary = (AbsolutePath)appPath / "Frameworks" / $"{wrapperName}.framework" / wrapperName;
        if (!File.Exists(wrapperBinary))
            Assert.Fail($"--static-dependency-force-load: the {label} app embeds no wrapper at {wrapperBinary}.");
        var wrapperBytes = new FileInfo(wrapperBinary).Length;

        // The app executable references nothing in the archive (the wrapper does), so it should carry
        // none of it; recorded, since the SDK leaves the app's own NativeReference unchanged.
        var executable = (AbsolutePath)appPath / ForceLoadAppName;
        var appCarriesArchive = File.Exists(executable)
            && DefinedSymbols(executable).Any(s => s.StartsWith("_sbfl_", StringComparison.Ordinal));

        var result = LaunchUntilAppRuns(
            () =>
            {
                SimCtl.Install(simUdid, appPath);
                return SimCtl.Launch(simUdid, bundleId, Array.Empty<string>(),
                    TimeSpan.FromSeconds(Timeout), appName: ForceLoadAppName);
            },
            "--static-dependency-force-load");
        Log.Information("=== CONSUMER OUTPUT ({Label}) ==={NewLine}{Output}", label, Environment.NewLine, result.Output);
        if (LaunchDiagnostics.LauncherNeverStartedApp(result))
            Assert.Fail($"--static-dependency-force-load: the {label} app was deployed but never started; no registry was read.\n{result.Output}");
        AssertLiveRuntimeLane(result);
        if (result.Result != TestResult.Success)
            Assert.Fail($"--static-dependency-force-load: the {label} app did not report TEST SUCCESS (result={result.Result}).\n{result.Output}");

        const string marker = "REGISTERED:[";
        var line = result.Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains(marker, StringComparison.Ordinal))
            ?? throw new Exception($"--static-dependency-force-load: the {label} app printed no {marker} line.\n{result.Output}");
        var start = line.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var registered = line[start..line.IndexOf(']', start)];
        var sorted = string.Join(",", registered.Split(',', StringSplitOptions.RemoveEmptyEntries).OrderBy(c => c, StringComparer.Ordinal));

        return new ForceLoadRun(forceLoad, sorted, wrapperBytes, appCarriesArchive);
    }

    static IEnumerable<string> DefinedSymbols(AbsolutePath binary)
    {
        var process = ProcessTasks.StartProcess(XcRun.FindTool("nm"), ArgumentEscaper.Join(new[] { "-gUj", binary.ToString() }), logOutput: false);
        process.AssertWaitForExit();
        return process.Output.Where(o => o.Type == OutputType.Std).Select(o => o.Text.Trim());
    }

    // The registry: one archive member holds the registry, and each component sits in its own member
    // that only a static initializer reaches. Two are C constructors, one a C++ global object.
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
            $"module {ForceLoadRegistry} {{\n    header \"{ForceLoadRegistry}.h\"\n    export *\n}}\n");
        var sources = new List<AbsolutePath>();
        void Source(string name, string body)
        {
            File.WriteAllText(src / name, $"#include \"{ForceLoadRegistry}.h\"\n" + body);
            sources.Add(src / name);
        }
        Source("registry.c", """
            #include <string.h>
            static char names[256];
            static int count;
            void sbfl_register(const char *name) {
                if (count > 0) strlcat(names, ",", sizeof names);
                strlcat(names, name, sizeof names);
                count++;
            }
            const char *sbfl_names(void) { return names; }
            """);
        Source("component_alpha.c", "__attribute__((constructor)) static void sbfl_alpha_init(void) { sbfl_register(\"alpha\"); }\n");
        Source("component_beta.c", "__attribute__((constructor)) static void sbfl_beta_init(void) { sbfl_register(\"beta\"); }\n");
        Source("component_gamma.cpp", "namespace { struct Gamma { Gamma() { sbfl_register(\"gamma\"); } } sbfl_gamma; }\n");

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
                var cpp = source.ToString().EndsWith(".cpp", StringComparison.Ordinal);
                RunPackGateTool("clang", new[]
                {
                    "-x", cpp ? "c++" : "c", "-c", "-target", triple, "-isysroot", sdkPath,
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
    // is implementation-only, so the module's interface does not name the C module.
    AbsolutePath BuildForceLoadPrimaryXcframework(AbsolutePath root, AbsolutePath registry)
    {
        if (Directory.Exists(root)) root.DeleteDirectory();
        root.CreateDirectory();
        var libSwift = root / "Lib.swift";
        File.WriteAllText(libSwift, $$"""
            @_implementationOnly import {{ForceLoadRegistry}}

            public enum ForceLoadProbe {
                public static func registeredNames() -> String { String(cString: sbfl_names()) }
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
        string bundleId, RuntimeTestLane lane, string packageVersion)
    {
        File.WriteAllText(appDir / $"{ForceLoadAppName}.csproj", $"""
            <Project Sdk="SwiftBindings.Sdk/{packageVersion}">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{lane.Framework}-ios</TargetFramework>
                <RuntimeIdentifier>{ForceLoadSimRid}</RuntimeIdentifier>
                {MixedConsumerRuntimeProperties(lane, device: false)}
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <IsPackable>false</IsPackable>
                <ApplicationId>{bundleId}</ApplicationId>
                <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
                <ApplicationVersion>1</ApplicationVersion>
                <SupportedOSPlatformVersion>15.0</SupportedOSPlatformVersion>
                <MtouchLink>None</MtouchLink>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <NoWarn>$(NoWarn);CA1416;CA1422;CS0649;CS0114;CS8604</NoWarn>
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
