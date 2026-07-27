// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.BindingTests.MixedDirect.cs — opt-in iOS mixed-framework SDK-direct consume→run leg
//
// Closes the runtime gap for consumption PATH (b): a mixed (ObjC + Swift) binding where the
// consumer app's OWN csproj imports `Sdk="SwiftBindings.Sdk"` and declares <SwiftFramework> —
// so the app IS the binding project (no PackageReference, no ProjectReference). The SDK
// generates the binding, compiles the Swift wrapper, builds the ObjC companion, and — via the
// new _ReferenceMixedObjCCompanion target — injects the companion's managed assembly as a
// <Reference> into the app's OWN compile so `new Module.ObjCType()` resolves. This leg builds
// that app for the iOS Simulator, runs it, and asserts the ObjC greeting round-trips AND the
// class registers exactly once.
//
// WHY IT EXISTS / WHAT IT ADDS over the sibling legs:
//   • --mixed-pack covers PATH (a): one packed nupkg consumed via one PackageReference (the
//     companion arrives EMBEDDED in lib/ via NuGet's auto-reference). It already proves the
//     native single-registration story on the iOS loader (sim + device).
//   • The macOS PackGate proves the nupkg STRUCTURE on the host but cannot exercise an iOS
//     runtime.
//   • Neither runs an SDK-DIRECT consumer. PATH (b) is the only mode where the companion is
//     surfaced to a DIFFERENT assembly's compile through _ReferenceMixedObjCCompanion (path a
//     gets it from the package lib/; path c from {PackageId}.ProjectReference.targets). This
//     leg is that path's runtime truth-teller.
//
// SIM-ONLY BY DESIGN. The native single-registration question (a static source's ObjC class
// force-loaded into the wrapper, the source archive dropped so it is never linked twice) is
// keyed on native linkage, not consumer path, and is already device-proven by --mixed-pack.
// The NEW surface here — injecting the companion managed <Reference> into the app's compile and
// copying it to the app output — is a compile/copy-local concern fully observed on the Mono-JIT
// simulator. So this leg runs sim only and does not NativeAOT-publish for a device.
//
// OPT-IN. Never part of the default `nuke binding-tests` run or `--compile-only`. It packs the
// Runtime/SDK/Apple feed at a throwaway version, builds a 2-slice iOS mixed xcframework, then
// builds + deploys a fresh SDK-direct consumer — minutes, not seconds — and needs a booted
// simulator. Run it before a release and after changes to native packaging policy, the ObjC
// companion build/reference path, calling conventions, or struct/P-Invoke marshalling.

using System;
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
    [Parameter("Opt-in: build a mixed (ObjC+Swift) binding in SDK-direct mode (the app's own csproj imports SwiftBindings.Sdk + <SwiftFramework>) and run it on the iOS Simulator. Sim-only; never part of the default run or --compile-only.")]
    readonly bool MixedDirect;

    // Distinct throwaway versions (own suffix) so this leg's NuGet-cache clears never collide
    // with --mixed-pack's. The Apple supplement major must be an integer (the generator rejects a
    // leading 0), so pin it to the live Apple train with a -mixeddirect suffix.
    const string MixedDirectVersion = "0.0.0-mixeddirect";
    const string MixedDirectAppleVersion = "26.2.0-mixeddirect";

    // STATIC source (the issue #40 condition): the wrapper force-loads the ObjC archive and is the
    // sole carrier, and the companion's own source NativeReference is dropped (Gap 2). Distinct
    // module/class names from --mixed-pack so neither leg's symbols can satisfy the other's checks.
    const string MixedDirectModule = "SbMixedDirect";
    const string MixedDirectProbeClass = "SbMixedDirectProbe";

    const string MixedDirectBundleId = "com.swiftbindings.mixeddirect";
    const string MixedDirectAppName = "MixedDirectApp";

    const string MixedDirectSimRid = "iossimulator-arm64";

    AbsolutePath MixedDirectScratch => RootDirectory / "artifacts" / "mixed-direct";

    // Entry point invoked from the BindingTests target dispatch when --mixed-direct is set.
    void RunMixedDirectLeg()
    {
        Log.Information("====================================================");
        Log.Information(" BindingTests — mixed-framework SDK-direct consume→run");
        Log.Information("   (consumption path b: the app IS the binding)");
        Log.Information("====================================================");

        // Hard-fail guard parity with --mixed-pack: the SDK ships SwiftInterfaceParser; if it's
        // missing the SDK-direct build would fail to generate the binding. Run `nuke compile` on a
        // Darwin host with the Swift toolchain first.
        var stagedBinary = SwiftInterfaceParserStagingDir / "SwiftInterfaceParser";
        if (!File.Exists(stagedBinary))
            throw new InvalidOperationException(
                $"--mixed-direct: expected SwiftInterfaceParser binary at '{stagedBinary}' but it is missing. " +
                "Run `nuke compile` on a macOS host with the Swift toolchain installed before exercising this leg.");

        var scratch = MixedDirectScratch;
        if (Directory.Exists(scratch)) scratch.DeleteDirectory();
        var nupkgDir = scratch / "packages";
        var appDir = scratch / "consumer-sim";
        nupkgDir.CreateDirectory();
        appDir.CreateDirectory();

        using var scope = new VersionScope(MixedDirectVersion, RootDirectory, MixedDirectAppleVersion);

        BuildMixedDirectFeed(nupkgDir, scope);

        Log.Information("=== mixed-direct: building 2-slice (device + simulator) iOS mixed xcframework ===");
        // Reuse the shared 2-slice STATIC iOS mixed xcframework builder (Build.BindingTests.MixedPack.cs):
        // the sim build links the simulator slice, and a 2-slice xcframework is the real-world shape
        // (a sim-only xcframework would not exercise the SDK's slice selection).
        var xcfw = BuildMixedPackIosXcframework(scratch / "build", MixedDirectModule, MixedDirectProbeClass);

        WriteMixedDirectConsumerApp(appDir, MixedDirectModule, MixedDirectProbeClass, xcfw);
        // No fixture package in SDK-direct mode: the app resolves SwiftBindings.Sdk + the transitive
        // Runtime/Apple packages from the local throwaway feed (fixtureNupkgDir: null).
        File.WriteAllText(appDir / "NuGet.config", MixedPackNuGetConfig(nupkgDir, fixtureNupkgDir: null));

        Log.Information("=== mixed-direct: building SDK-direct consumer for the iOS Simulator (Mono JIT) ===");
        DotNetBuild(s => s
            .SetProjectFile(appDir / $"{MixedDirectAppName}.csproj")
            .SetConfiguration("Debug")
            .EnableNoLogo()
            .SetVerbosity(DotNetVerbosity.quiet));

        // Prove the Gap-2 source drop STRUCTURALLY, not just by the runtime warning. Duplicate ObjC
        // registration is a non-fatal loader warning — the app still runs and prints TEST SUCCESS —
        // so a bake/flag regression that re-linked the static source could pass the launch grep if
        // the warning were ever missed or reworded. Asserting on the generated companion csproj
        // catches that regression at its source.
        AssertMixedDirectSourceDropStructural(appDir);

        // Locate the produced .app bundle (sim build → bin/Debug/.../iossimulator-arm64/...).
        // Deterministic selection: shortest path then ordinal so the choice never depends on
        // filesystem enumeration order (mirrors RunMixedPackConsumer).
        var candidates = Directory
            .GetDirectories(appDir / "bin", $"{MixedDirectAppName}.app", SearchOption.AllDirectories)
            .Where(d => d.Contains("Debug", StringComparison.Ordinal) && d.Contains(MixedDirectSimRid, StringComparison.Ordinal))
            .OrderBy(d => d.Length)
            .ThenBy(d => d, StringComparer.Ordinal)
            .ToList();
        var appPath = candidates.FirstOrDefault()
            ?? throw new Exception($"--mixed-direct: {MixedDirectAppName}.app bundle not found after build");
        if (candidates.Count > 1)
            Log.Warning("--mixed-direct: {Count} matching {App}.app bundles (config=Debug, rid={Rid}); selected {Path}",
                candidates.Count, MixedDirectAppName, MixedDirectSimRid, appPath);
        Log.Information("    app bundle: {Path}", appPath);

        Log.Information("=== mixed-direct: deploying + launching consumer on the simulator ===");
        var sim = !string.IsNullOrEmpty(DeviceUdid)
            ? new SimCtl.SimDevice(DeviceUdid, "pre-booted", "Booted", true, "")
            : SimCtl.EnsureBootedDevice();
        Log.Information("    simulator: {Name} ({Udid})", sim.Name, sim.Udid);
        var result = LaunchUntilAppRuns(
            () =>
            {
                SimCtl.Install(sim.Udid, appPath);
                return SimCtl.Launch(
                    sim.Udid, MixedDirectBundleId, Array.Empty<string>(),
                    TimeSpan.FromSeconds(Timeout), appName: MixedDirectAppName);
            },
            "--mixed-direct");

        Log.Information("");
        Log.Information("=== CONSUMER OUTPUT (SDK-direct, simulator) ===");
        Log.Information(result.Output);

        AssertMixedDirectConsumerResult(result);
    }

    // Builds the throwaway-version Runtime + SDK + Apple feed the SDK-direct app restores from.
    // Identical in shape to --mixed-pack's feed (publish generator into SDK tools → pack the three
    // core packages → clear the SwiftBindings.* NuGet cache so a stale same-version entry from a
    // prior run can't shadow these), minus the fixture (there is no fixture package in path b).
    void BuildMixedDirectFeed(AbsolutePath nupkgDir, VersionScope scope)
    {
        Log.Information("=== mixed-direct: building local feed at {Version} ===", MixedDirectVersion);

        Log.Information("  [1/3] Publishing generator into SDK tools");
        DotNetPublish(s => scope.Apply(s
            .SetProject(SourceDir / "Swift.Bindings" / "src" / "Swift.Bindings.csproj")
            .SetConfiguration("Release")
            .SetOutput(SourceDir / "Swift.Bindings.Sdk" / "tools" / DotNetTfm / "any")
            .EnableNoLogo()
            .SetVerbosity(DotNetVerbosity.quiet)));

        Log.Information("  [2/3] Packing Runtime + Sdk + Apple");
        foreach (var csproj in new[]
        {
            SourceDir / "Swift.Runtime" / "src" / "Swift.Runtime.csproj",
            SourceDir / "Swift.Bindings.Sdk" / "Swift.Bindings.Sdk.csproj",
            SourceDir / "Swift.Bindings.Apple" / "Swift.Bindings.Apple.csproj",
        })
        {
            DotNetPack(s => scope.Apply(s
                .SetProject(csproj)
                .SetConfiguration("Release")
                .SetOutputDirectory(nupkgDir)
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet)));
        }

        Log.Information("  [3/3] Clearing NuGet cache for throwaway-version packages");
        ProcessTasks.StartProcess("dotnet", "nuget locals http-cache --clear", logOutput: false)
            .AssertWaitForExit();
        var nugetCacheDir = (AbsolutePath)(Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"));
        foreach (var (pkg, ver) in new[]
        {
            ("swiftbindings.runtime", MixedDirectVersion),
            ("swiftbindings.sdk", MixedDirectVersion),
            ("swiftbindings.apple", MixedDirectAppleVersion),
        })
        {
            var pkgDir = nugetCacheDir / pkg / ver;
            if (Directory.Exists(pkgDir)) pkgDir.DeleteDirectory();
        }
    }

    // The SDK-direct consumer: an iOS Exe app whose OWN csproj imports SwiftBindings.Sdk and
    // declares the mixed <SwiftFramework>. The SDK runs generate → compile wrapper → build the ObjC
    // companion → _ReferenceMixedObjCCompanion (inject the companion assembly as a <Reference> into
    // THIS project's compile). IsPackable=false: SDK-direct apps are run, not packed. Sim shape
    // (MtouchLink=None, no trimming) mirrors the --mixed-pack sim consumer.
    static void WriteMixedDirectConsumerApp(
        AbsolutePath appDir, string module, string probeClass, AbsolutePath xcfwPath)
    {
        var csproj = $"""
            <Project Sdk="SwiftBindings.Sdk/{MixedDirectVersion}">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0-ios</TargetFramework>
                <RuntimeIdentifier>{MixedDirectSimRid}</RuntimeIdentifier>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <!-- SDK-direct apps are run, not packed. IsPackable=false also keeps the SDK's
                     pack-time mixed guards (which assume a Library binding being packed) inert. -->
                <IsPackable>false</IsPackable>
                <ApplicationId>{MixedDirectBundleId}</ApplicationId>
                <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
                <ApplicationVersion>1</ApplicationVersion>
                <SupportedOSPlatformVersion>15.0</SupportedOSPlatformVersion>
                <!-- Simulator (Mono JIT): no trimming. -->
                <MtouchLink>None</MtouchLink>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <NoWarn>$(NoWarn);CA1416;CA1422;CS0649;CS0114;CS8604</NoWarn>
              </PropertyGroup>

              <!-- NOTE: unlike the mixed-pack PackageReference consumer (a plain Microsoft.NET.Sdk
                   app that must add DisableRuntimeMarshallingAttribute itself), this app imports
                   SwiftBindings.Sdk, which already emits that assembly attribute (Sdk.props). Adding
                   it here too would be a duplicate (CS0579). -->
              <ItemGroup>
                <None Include="Info.plist" />
              </ItemGroup>

              <!-- The mixed framework. The SDK makes THIS project the binding: it generates the
                   C# surface, compiles the wrapper (sole carrier of the static ObjC class), builds
                   the ObjC companion, and references the companion into this compile. -->
              <ItemGroup>
                <SwiftFramework Include="{xcfwPath}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(appDir / $"{MixedDirectAppName}.csproj", csproj);

        // Minimal UIKit app: bring up a window, then exercise the ObjC type and print the markers
        // the launch harness scrapes. Print RESULTS FLUSHED before the TEST marker so the launcher
        // returns promptly. Same probe shape as the --mixed-pack consumer.
        var program = $$"""
            // Copyright (c) 2026 Justin Wojciechowski.
            // Licensed under the MIT License.
            using CoreFoundation;
            using Foundation;
            using UIKit;

            namespace MixedDirectApp;

            public static class Application
            {
                static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
            }

            public class AppDelegate : UIApplicationDelegate
            {
                public override UIWindow? Window { get; set; }

                public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
                {
                    Window = new UIWindow(UIScreen.MainScreen.Bounds);
                    Window.RootViewController = new UIViewController();
                    Window.MakeKeyAndVisible();

                    // Defer the probe so launch completes first, then run it on the main queue
                    // (object init + objc_msgSend through the SDK-direct binding).
                    DispatchQueue.MainQueue.DispatchAsync(RunProbe);
                    return true;
                }

                static void RunProbe()
                {
                    try
                    {
                        var probe = new global::{{module}}.{{probeClass}}();
                        Console.WriteLine("OBJC_GREETING:" + probe.Greeting());
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
            """;
        File.WriteAllText(appDir / "Program.cs", program);

        File.WriteAllText(appDir / "Info.plist",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\">\n<dict>\n" +
            "    <key>UILaunchScreen</key>\n    <dict/>\n" +
            "</dict>\n</plist>\n");
    }

    // Build-time structural proof that the static source's NativeReference was dropped from the
    // generated ObjC companion csproj (Gap 2). The companion is emitted on this machine where the
    // source linkage is known, so the drop is BAKED into the csproj text: for a static source with a
    // wrapper present (this leg's condition) the companion must NOT emit an active source
    // <NativeReference> (which would re-link the archive and duplicate-register every ObjC class).
    // This complements — and is strictly stronger than — the runtime "implemented in both" grep,
    // because duplicate registration is only a non-fatal warning.
    void AssertMixedDirectSourceDropStructural(AbsolutePath appDir)
    {
        var objDir = appDir / "obj";
        if (!Directory.Exists(objDir))
            Assert.Fail($"--mixed-direct STRUCTURAL: expected generated intermediate at '{objDir}' but it is missing — the SDK did not generate the binding.");

        // The generated ObjC companion is the only project under obj/ that carries the Gap-2 source
        // decision: either an active source <NativeReference> (kept) or the drop marker (dropped).
        // Match on those signals so the check is independent of the exact companion package-id name.
        var companions = Directory
            .GetFiles(objDir, "*.csproj", SearchOption.AllDirectories)
            .Select(f => (path: f, text: File.ReadAllText(f)))
            .Where(x => x.text.Contains("Source NativeReference dropped (Gap 2)", StringComparison.Ordinal)
                        || x.text.Contains("<NativeReference Include=", StringComparison.Ordinal))
            .ToList();

        if (companions.Count == 0)
            Assert.Fail(
                $"--mixed-direct STRUCTURAL: no generated ObjC companion csproj carrying the Gap-2 decision was found under '{objDir}'. " +
                "The SDK did not emit the companion (so the source-drop guarantee is unverified), or the bake markers changed without this check being updated.");

        foreach (var (path, text) in companions)
        {
            if (text.Contains("<NativeReference Include=", StringComparison.Ordinal))
                Assert.Fail(
                    $"--mixed-direct STRUCTURAL: generated companion csproj '{path}' still emits an active source <NativeReference> for a static source whose wrapper carries the archive. " +
                    "The Gap-2 drop did NOT happen, so the static archive links twice and every ObjC class duplicate-registers (issue #40). " +
                    "This is the build-time cause; duplicate registration is only a non-fatal runtime warning, so this assert catches a bake/flag regression the launch grep can miss.");
            if (!text.Contains("Source NativeReference dropped (Gap 2)", StringComparison.Ordinal))
                Assert.Fail(
                    $"--mixed-direct STRUCTURAL: generated companion csproj '{path}' neither emits a source NativeReference nor carries the Gap-2 drop marker — cannot confirm the drop was intentional.");
        }

        Log.Information("--mixed-direct structural OK — generated ObjC companion dropped its source NativeReference (Gap 2), so the static archive is linked exactly once ({Count} companion csproj checked)", companions.Count);
    }

    // Asserts the SDK-direct consumer (a) registered the ObjC class exactly once — the loader's
    // "Class X is implemented in both …" warning is the LOAD-TIME Gap 2 symptom, checked FIRST so a
    // regression reports the precise cause — (b) round-tripped the ObjC greeting through the
    // companion <Reference> the SDK injected into this app's own compile, and (c) ran to completion.
    void AssertMixedDirectConsumerResult(LaunchResult result)
    {
        // Launcher never started the app: report THAT, not a binding verdict. Every assertion below
        // is about what the app printed, and this run has no app output to reason from.
        if (LaunchDiagnostics.LauncherNeverStartedApp(result))
            Assert.Fail(
                $"--mixed-direct: the app was deployed but the launcher never started it (retried " +
                $"{LaunchInfraMaxAttempts}×), so nothing evaluated the ObjC type — this is a deploy/launch failure, " +
                $"NOT a binding result.\nlauncher output:\n{result.Output}");

        if (result.Output.Contains("implemented in both", StringComparison.OrdinalIgnoreCase))
            Assert.Fail(
                "--mixed-direct: the loader reported a duplicate ObjC class registration (Gap 2 regression) — the " +
                $"static source archive was linked into the SDK-direct app in ADDITION to the force-loading wrapper.\noutput:\n{result.Output}");

        var expected = $"OBJC_GREETING:{PackGateMixedObjCGreeting}";
        if (!result.Output.Contains(expected, StringComparison.Ordinal))
            Assert.Fail(
                $"--mixed-direct: expected '{expected}' in output — the ObjC type was not usable. The SDK-direct app's " +
                "compile did not see the companion managed assembly (_ReferenceMixedObjCCompanion did not inject the " +
                $"<Reference>) or it was not copied to the app output.\noutput:\n{result.Output}");

        if (result.Result != TestResult.Success)
            Assert.Fail(
                $"--mixed-direct: consumer did not report TEST SUCCESS (result={result.Result}). The greeting may have " +
                $"printed but the app did not complete cleanly.\noutput:\n{result.Output}");

        Log.Information("--mixed-direct consumer-run OK — ObjC type usable in SDK-direct mode (companion referenced into the app's own compile), class registered once");
    }
}
