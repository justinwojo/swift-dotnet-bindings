// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.BindingTests.AppStoreHygiene.cs — opt-in App Store TN2435-hygiene gate (issue #42)
//
// Proves the root-cause fix for the App Store rejections in issue #42: the native Swift/.NET
// interop runtime must ship — and embed into a consumer app — as a SIGNED DYNAMIC FRAMEWORK
// inside an xcframework, never as a loose libSwiftBindingsRuntime.dylib in the app's Frameworks/.
// Apple TN2435 forbids loose, non-system .dylib files in an iOS app bundle, which is what drew
// ITMS-90426 / ITMS-90429 / ITMS-90171 once the reporter got past the earlier errors. Because our
// runtime links the OS-resident stable Swift ABI (/usr/lib/swift) and the consumer app targets
// iOS 15, a compliant app embeds ZERO back-deployment libswift*.dylib and therefore needs NO
// top-level SwiftSupport/ folder at all — which is why we deleted the SwiftSupport injector (and
// the .xcarchive bloat it caused: 138 MB → 185 MB, per the reporter).
//
// WHAT THIS GATE DOES
//   1. Packs SwiftBindings.Runtime at a throwaway version into a local feed (so the framework
//      xcframework + buildTransitive NativeReference are exercised through a REAL package, exactly
//      as a consumer gets them — buildTransitive does not flow across a ProjectReference).
//   2. Structural nupkg check (no device / signing needed): the runtime nupkg preserves the whole
//      SwiftBindingsRuntime.xcframework tree (per-slice .framework binary + Info.plist), the device
//      slice is arm64 and the simulator slice is arm64+x86_64, and it ships NO loose
//      libSwiftBindingsRuntime.dylib and NO SwiftSupport injector script.
//   3. Writes a tiny consumer app that takes ONE PackageReference on SwiftBindings.Runtime and
//      publishes a device App Store IPA (BuildIpa, ios-arm64), then asserts TN2435 hygiene on the
//      produced IPA + app bundle:
//        (a) the runtime is embedded as Frameworks/SwiftBindingsRuntime.framework — a real framework
//            bundle whose binary carries install_name @rpath/SwiftBindingsRuntime.framework/… and a
//            valid code signature (codesign --verify --strict);
//        (b) there is NO loose Frameworks/libSwiftBindingsRuntime.dylib (the exact rejected shape);
//        (c) the app embeds ZERO libswift*.dylib anywhere (it links the OS stable ABI), so no
//            SwiftSupport/ folder is required — and indeed the IPA has none at its root;
//        (d) no .DS_Store / __MACOSX litter, Payload/<app>.app intact, app signature verifies.
//
// WHY A BARE RUNTIME REFERENCE IS A FAITHFUL FIXTURE
//   The runtime framework non-weak-links /usr/lib/swift (libswiftCore, libswiftDispatch,
//   libswift_Concurrency, …). So a consumer that merely references the Runtime and builds a device
//   IPA exercises the full Swift-linking path the .NET iOS workload takes for any real Swift
//   binding — the framework embed + sign, the @rpath wiring, and the stable-ABI link that keeps
//   libswift OUT of the bundle — without the cost of generating + packing a whole Swift binding.
//   If this fixture ever embeds a libswift*.dylib (assertion (c)), the premise that the runtime
//   links the stable ABI is wrong and the fix needs revisiting — the gate is that proof.
//
// OPT-IN BY DESIGN. Never part of the default `nuke binding-tests` run or `--compile-only`. It packs
// a feed and publishes a device IPA (minutes, not seconds) and needs a code-signing identity on the
// host — but NO connected device (the IPA is built and inspected on the build host). Run it before a
// release and after changes to the runtime framework packaging, the buildTransitive NativeReference,
// or native packaging policy. Example:
//   nuke binding-tests --appstore-hygiene

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    [Parameter("Opt-in: through a Runtime-referencing consumer, build a device App Store IPA and assert TN2435 hygiene — the native runtime embeds as a signed SwiftBindingsRuntime.framework (not a loose dylib), the app embeds zero libswift*.dylib, no SwiftSupport/ folder is present, and the app signature verifies. Also asserts the runtime nupkg preserves the framework xcframework. Builds + inspects on the host; needs a code-signing identity but no connected device. Never part of the default run or --compile-only.")]
    readonly bool AppstoreHygiene;

    // Throwaway version with its own suffix so this leg's NuGet-cache clears never collide with
    // --mixed-pack / --mixed-direct.
    const string AppStoreHygieneVersion = "0.0.0-appstorehygiene";

    const string AppStoreHygieneAppName = "AppStoreHygieneApp";
    const string AppStoreHygieneBundleId = "com.swiftbindings.appstorehygiene";

    // Device RID — BuildIpa is only honored on a device publish, and the App Store artifact under
    // test is the iphoneos (device) IPA.
    const string AppStoreHygieneIosRid = "ios-arm64";

    // The codesigning identity this gate signs the device IPA with (Justin's wildcard dev identity,
    // matching RuntimeTestsApp / the mixed legs). Hoisted to one constant so the consumer csproj's
    // <CodesignKey> and the up-front "can this host sign?" tri-state check (Finding 61) reference the
    // same string and cannot drift — a host missing this identity is an honest skip, not a failure.
    const string AppStoreHygieneCodesignKey = "Apple Development: Justin Wojciechowski (KBKS29A36Q)";

    AbsolutePath AppStoreHygieneScratch => RootDirectory / "artifacts" / "appstore-hygiene";

    // Entry point invoked from the BindingTests dispatch when --appstore-hygiene is set.
    void RunAppStoreHygieneLeg()
    {
        Log.Information("=================================================");
        Log.Information(" BindingTests — App Store TN2435-hygiene gate");
        Log.Information("=================================================");

        var scratch = AppStoreHygieneScratch;
        if (Directory.Exists(scratch)) scratch.DeleteDirectory();
        var nupkgDir = scratch / "packages";
        var appDir = scratch / "consumer";
        nupkgDir.CreateDirectory();
        appDir.CreateDirectory();

        using var scope = new VersionScope(AppStoreHygieneVersion, RootDirectory);

        BuildAppStoreHygieneFeed(nupkgDir);

        // Cheap structural proof first (no device / signing): the nupkg ships the framework
        // xcframework with the right slices and no loose dylib / injector script.
        AssertRuntimeNupkgPackaging(nupkgDir);

        // Tri-state (Finding 61): the structural nupkg checks above need no signing and have run. The
        // device-IPA leg requires this host to sign with AppStoreHygieneCodesignKey. If it can't, report
        // an honest SKIP (non-failing) instead of throwing deep inside the publish — "this host cannot
        // sign" is neither a PASS (the IPA hygiene was never proven) nor a hygiene FAIL.
        if (!HostCanSignAppStoreHygiene())
        {
            Log.Warning(
                "--appstore-hygiene: SKIP the device-IPA leg — codesigning identity '{Key}' is not present on " +
                "this host (checked via `security find-identity -v -p codesigning`). The structural runtime-nupkg " +
                "checks PASSED; the device-IPA hygiene assertions require a signing identity, so they are skipped, " +
                "not passed. Run on a host with the identity (or before a release) for the full gate.",
                AppStoreHygieneCodesignKey);
            return;
        }

        WriteAppStoreHygieneConsumerApp(appDir);
        File.WriteAllText(appDir / "NuGet.config", MixedPackNuGetConfig(nupkgDir, fixtureNupkgDir: null));

        RunAppStoreHygieneIpaLeg(appDir, scratch);
    }

    // Tri-state input (Finding 61): does this host have the codesigning identity this gate signs the
    // device IPA with? `security find-identity -v -p codesigning` lists only *valid* codesigning
    // identities; if ours is absent the device publish cannot sign, which is an honest skip rather
    // than a defect. Mirrors how lipo/ditto are invoked directly (security is a system tool on PATH).
    static bool HostCanSignAppStoreHygiene()
    {
        var proc = ProcessTasks.StartProcess(
                "security",
                ArgumentEscaper.Join(new[] { "find-identity", "-v", "-p", "codesigning" }),
                logOutput: false)
            .AssertWaitForExit();
        if (proc.ExitCode != 0) return false;
        return proc.Output.Select(o => o.Text)
            .Any(line => line.Contains(AppStoreHygieneCodesignKey, StringComparison.Ordinal));
    }

    // Packs SwiftBindings.Runtime at the throwaway version into the local feed and clears any stale
    // same-version cache entry so the freshly-built package (framework xcframework + the
    // NativeReference targets) is the one restored. Only the Runtime is needed: the consumer takes a
    // single PackageReference on it and the buildTransitive target flows from there.
    void BuildAppStoreHygieneFeed(AbsolutePath nupkgDir)
    {
        Log.Information("=== appstore-hygiene: building local feed at {Version} ===", AppStoreHygieneVersion);

        DotNetPack(s => s
            .SetProject(SourceDir / "Swift.Runtime" / "src" / "Swift.Runtime.csproj")
            .SetConfiguration("Release")
            .SetOutputDirectory(nupkgDir)
            .EnableNoLogo()
            .SetVerbosity(DotNetVerbosity.quiet));

        ProcessTasks.StartProcess("dotnet", "nuget locals http-cache --clear", logOutput: false)
            .AssertWaitForExit();
        var nugetCacheDir = (AbsolutePath)(Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"));
        var pkgDir = nugetCacheDir / "swiftbindings.runtime" / AppStoreHygieneVersion;
        if (Directory.Exists(pkgDir)) pkgDir.DeleteDirectory();
    }

    // Structural assertions on the runtime nupkg itself — the durable regression net for slice
    // selection and pack-path preservation (Codex review). Needs no device or signing identity, so
    // it runs even on a host that can't publish a device IPA.
    void AssertRuntimeNupkgPackaging(AbsolutePath nupkgDir)
    {
        Log.Information("=== appstore-hygiene: inspecting runtime nupkg packaging ===");
        var failures = new List<string>();

        var nupkg = Directory.GetFiles(nupkgDir, "SwiftBindings.Runtime.*.nupkg")
            .OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new Exception($"--appstore-hygiene: no SwiftBindings.Runtime nupkg found under {nupkgDir}.");
        Log.Information("    nupkg: {Path}", nupkg);

        const string xcfRoot = "native/SwiftBindingsRuntime.xcframework";
        const string deviceBinary = xcfRoot + "/ios-arm64/SwiftBindingsRuntime.framework/SwiftBindingsRuntime";
        const string simBinary = xcfRoot + "/ios-arm64_x86_64-simulator/SwiftBindingsRuntime.framework/SwiftBindingsRuntime";

        var extract = AppStoreHygieneScratch / "nupkg-extract";
        if (Directory.Exists(extract)) extract.DeleteDirectory();
        extract.CreateDirectory();

        using (var zip = ZipFile.OpenRead(nupkg))
        {
            // nupkg entries use forward slashes regardless of host OS.
            var entries = zip.Entries.Select(e => e.FullName).ToList();

            // The whole framework xcframework tree is preserved (slice .framework binaries + plist).
            foreach (var required in new[] { xcfRoot + "/Info.plist", deviceBinary, simBinary })
                if (!entries.Contains(required))
                    failures.Add($"runtime nupkg is missing required xcframework entry: {required}");

            // buildTransitive targets present; SwiftSupport injector + loose dylib are GONE.
            if (!entries.Any(e => e.StartsWith("buildTransitive/", StringComparison.Ordinal)
                    && e.EndsWith("SwiftBindings.Runtime.targets", StringComparison.Ordinal)))
                failures.Add("runtime nupkg is missing buildTransitive/SwiftBindings.Runtime.targets.");
            foreach (var e in entries)
            {
                if (e.EndsWith("libSwiftBindingsRuntime.dylib", StringComparison.Ordinal))
                    failures.Add($"runtime nupkg ships a loose runtime dylib (TN2435 violation): {e}");
                if (e.EndsWith("add-swiftsupport-folder.sh", StringComparison.Ordinal))
                    failures.Add($"runtime nupkg still ships the removed SwiftSupport injector script: {e}");
            }

            // Extract the two slice binaries to confirm their architectures.
            ExtractZipEntry(zip, deviceBinary, extract / "device");
            ExtractZipEntry(zip, simBinary, extract / "sim");
        }

        // The slice binaries were just extracted above, so lipo MUST be able to read them.
        // A null here means lipo failed on a present file (corrupt / not a Mach-O) — that is a
        // defect, not a pass, so treat null as a failure rather than silently no-op'ing.
        var deviceArchs = LipoArchsOrNull(extract / "device");
        if (deviceArchs is null || !deviceArchs.Contains("arm64"))
            failures.Add($"runtime nupkg device slice did not report arm64 via lipo (got: {(deviceArchs is null ? "<lipo failed to read slice>" : string.Join(",", deviceArchs))}).");

        var simArchs = LipoArchsOrNull(extract / "sim");
        if (simArchs is null || !(simArchs.Contains("arm64") && simArchs.Contains("x86_64")))
            failures.Add($"runtime nupkg simulator slice is not fat arm64+x86_64 (got: {(simArchs is null ? "<lipo failed to read slice>" : string.Join(",", simArchs))}).");

        if (failures.Count > 0)
        {
            Log.Error("--appstore-hygiene nupkg check FAILED — {Count} defect(s):", failures.Count);
            foreach (var f in failures) Log.Error("  {Detail}", f);
            Assert.Fail($"--appstore-hygiene: {failures.Count} runtime-nupkg packaging defect(s) — see log.");
        }
        Log.Information("--appstore-hygiene nupkg OK — framework xcframework preserved (device arm64, sim arm64+x86_64), no loose dylib, no injector script.");
    }

    static void ExtractZipEntry(ZipArchive zip, string entryName, AbsolutePath destFile)
    {
        var entry = zip.GetEntry(entryName)
            ?? throw new Exception($"--appstore-hygiene: zip entry not found for extraction: {entryName}");
        destFile.Parent.CreateDirectory();
        entry.ExtractToFile(destFile, overwrite: true);
    }

    // `lipo -archs` of a Mach-O, or null if the tool/path failed. Returns the arch tokens.
    // Named distinctly from the asserting LipoArchs(AbsolutePath) in Build.X64PackGate.cs: this
    // variant takes a string and degrades to null on failure so the gate keeps collecting defects
    // rather than throwing mid-collection.
    static string[]? LipoArchsOrNull(string machoPath)
    {
        if (!File.Exists(machoPath)) return null;
        var proc = ProcessTasks.StartProcess("lipo", ArgumentEscaper.Join(new[] { "-archs", machoPath }), logOutput: false)
            .AssertWaitForExit();
        if (proc.ExitCode != 0) return null;
        return proc.Output.Select(o => o.Text).FirstOrDefault()?.Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    // Publishes a device IPA (BuildIpa) so the workload embeds the runtime framework + creates the
    // App Store .ipa, then asserts TN2435 hygiene on the produced artifact.
    void RunAppStoreHygieneIpaLeg(AbsolutePath appDir, AbsolutePath scratch)
    {
        Log.Information("=== appstore-hygiene: publishing device IPA (ios-arm64) ===");
        DotNetPublish(s => s
            .SetProject(appDir / $"{AppStoreHygieneAppName}.csproj")
            .SetConfiguration("Release")
            .SetRuntime(AppStoreHygieneIosRid)
            .EnableNoLogo()
            .SetVerbosity(DotNetVerbosity.quiet));

        // Positive embed stamp (Finding 61): _StampSwiftRuntimeEmbed (SwiftBindings.Runtime.targets)
        // fires AfterTargets the workload's framework-embed step (_CopyDirectoriesToBundle) and writes
        // this sentinel into the app's obj/. Its absence means that embed target did NOT run — e.g. a
        // future .NET workload renamed/removed it — so the runtime framework embed is no longer proven
        // by a successful publish alone. Assert it positively here, before inferring anything from the
        // produced .ipa.
        var stamps = Directory.GetFiles(appDir / "obj", "swiftbindings-runtime-embed.stamp", SearchOption.AllDirectories);
        if (stamps.Length == 0)
            throw new Exception(
                $"--appstore-hygiene: the runtime embed sentinel (swiftbindings-runtime-embed.stamp) was not produced " +
                $"under {appDir / "obj"}. _StampSwiftRuntimeEmbed (SwiftBindings.Runtime.targets) anchors AfterTargets " +
                "the workload's framework-embed target (_CopyDirectoriesToBundle); its absence means that target did " +
                "not run — most likely renamed or removed by the .NET Apple workload — so the runtime framework embed " +
                "is no longer proven. Re-anchor the stamp to the current embed target.");
        Log.Information("    embed stamp present: {Path}", stamps[0]);

        // Locate the produced .ipa. The exact intermediate layout varies, so search the bin tree
        // for any *.ipa under the device RID. Deterministic selection: shortest path then ordinal.
        var ipas = Directory
            .GetFiles(appDir / "bin", "*.ipa", SearchOption.AllDirectories)
            .Where(p => p.Contains(AppStoreHygieneIosRid, StringComparison.Ordinal))
            .OrderBy(p => p.Length)
            .ThenBy(p => p, StringComparer.Ordinal)
            .ToList();
        var ipa = ipas.FirstOrDefault()
            ?? throw new Exception(
                $"--appstore-hygiene: no .ipa produced under {appDir / "bin"} for rid {AppStoreHygieneIosRid}. " +
                "A device publish should set BuildIpa=true and create an IPA — check the publish log above.");
        if (ipas.Count > 1)
            Log.Warning("--appstore-hygiene: {Count} .ipa files found; selected {Path}", ipas.Count, ipa);
        Log.Information("    IPA: {Path}", ipa);

        AssertAppStoreHygieneIpa((AbsolutePath)ipa, scratch);
    }

    // The consumer: a minimal net10.0-ios app that takes ONE PackageReference on
    // SwiftBindings.Runtime and forces an IPA on the device publish. No NativeAOT — a plain Mono
    // device publish still runs CreateIpa and embeds the runtime framework (the embed shape is
    // identical to the AOT path and far cheaper to produce here).
    static void WriteAppStoreHygieneConsumerApp(AbsolutePath appDir)
    {
        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0-ios</TargetFramework>
                <RuntimeIdentifier>{AppStoreHygieneIosRid}</RuntimeIdentifier>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <ApplicationId>{AppStoreHygieneBundleId}</ApplicationId>
                <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
                <ApplicationVersion>1</ApplicationVersion>
                <SupportedOSPlatformVersion>15.0</SupportedOSPlatformVersion>
                <!-- Mirror RuntimeTestsApp's trim-warning suppression: an iOS Release publish
                     trims, and Swift.Runtime's reflection-heavy interop surfaces the IL2xxx
                     family (IL2104 is the assembly-level rollup). This gate inspects packaging
                     structure, not trim correctness (RuntimeTestsApp covers that), so suppress the
                     same set the shipped apps do rather than fail the publish. -->
                <NoWarn>$(NoWarn);CA1416;CA1422;IL2065;IL2075;IL2087;IL2091;IL2026;IL2104</NoWarn>

                <!-- Force the App Store IPA on this device publish so the workload's CreateIpa runs
                     and the runtime framework is embedded + signed into the app bundle. -->
                <BuildIpa>true</BuildIpa>

                <!-- Justin's wildcard dev identity (matches RuntimeTestsApp / the mixed legs). One
                     constant shared with the up-front tri-state skip check so the two can't drift. -->
                <CodesignKey>{AppStoreHygieneCodesignKey}</CodesignKey>
                <CodesignProvision>Wildcard Dev</CodesignProvision>
                <TeamIdentifierPrefix>TL2K6QUQEH</TeamIdentifierPrefix>
              </PropertyGroup>

              <!-- The single reference under test: the Runtime package's buildTransitive targets
                   (with the framework NativeReference) flow into this app from here. -->
              <ItemGroup>
                <PackageReference Include="SwiftBindings.Runtime" Version="{AppStoreHygieneVersion}" />
              </ItemGroup>

              <ItemGroup>
                <None Include="Info.plist" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(appDir / $"{AppStoreHygieneAppName}.csproj", csproj);

        // Minimal UIKit app — it is never launched (the gate only inspects the IPA structure), but
        // it must be a valid iOS app for the workload to package one. Reference a
        // SwiftBindings.Runtime type so the managed assembly is genuinely linked (keeping the
        // fixture honest), which is what pulls the runtime framework into the app bundle.
        var program = $$"""
            // Copyright (c) 2026 Justin Wojciechowski.
            // Licensed under the MIT License.
            using Foundation;
            using UIKit;

            namespace AppStoreHygieneApp;

            public static class Application
            {
                static void Main(string[] args)
                {
                    // Touch the Runtime assembly so it is not trimmed away entirely.
                    GC.KeepAlive(typeof(Swift.SwiftString));
                    UIApplication.Main(args, null, typeof(AppDelegate));
                }
            }

            public class AppDelegate : UIApplicationDelegate
            {
                public override UIWindow? Window { get; set; }

                public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
                {
                    Window = new UIWindow(UIScreen.MainScreen.Bounds);
                    Window.RootViewController = new UIViewController();
                    Window.MakeKeyAndVisible();
                    return true;
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

    // TN2435-hygiene assertions on the produced device IPA + its app bundle.
    void AssertAppStoreHygieneIpa(AbsolutePath ipa, AbsolutePath scratch)
    {
        var failures = new List<string>();

        using (var zip = ZipFile.OpenRead(ipa))
        {
            var entries = zip.Entries.Select(e => e.FullName).ToList();

            // No Finder/zip litter anywhere in the IPA — these draw ITMS-90430.
            foreach (var n in entries)
            {
                if (Path.GetFileName(n) == ".DS_Store")
                    failures.Add($"stray .DS_Store in the IPA: {n}");
                if (n.StartsWith("__MACOSX", StringComparison.Ordinal))
                    failures.Add($"stray __MACOSX in the IPA: {n}");
            }

            // Payload/ is intact (the app bundle is still there).
            if (!entries.Any(n => n.StartsWith("Payload/", StringComparison.Ordinal) && n.Contains(".app/", StringComparison.Ordinal)))
                failures.Add("Payload/<app>.app is missing — the IPA did not package the app bundle.");

            // A compliant app embeds zero back-deployment libswift and needs NO SwiftSupport/ folder.
            // Its presence here would mean we regressed to embedding loose Swift dylibs.
            if (entries.Any(n => n.StartsWith("SwiftSupport/", StringComparison.Ordinal)))
                failures.Add(
                    "IPA has a top-level SwiftSupport/ folder — a stable-ABI app embedding zero libswift " +
                    "needs none; its presence means libswift*.dylib were embedded (regression to the " +
                    "back-deployment shape that bloated the archive in issue #42).");
        }

        // Extract with ditto (preserves symlinks + code signatures) for the framework + signing checks.
        var extract = scratch / "ipa-extract";
        if (Directory.Exists(extract)) extract.DeleteDirectory();
        extract.CreateDirectory();
        ProcessTasks.StartProcess("ditto", ArgumentEscaper.Join(new[] { "-x", "-k", ipa.ToString(), extract.ToString() }), logOutput: false)
            .AssertWaitForExit()
            .AssertZeroExitCode();

        var appBundle = Directory.GetDirectories(extract / "Payload", "*.app").FirstOrDefault();
        if (appBundle is null)
        {
            failures.Add("no .app under Payload/ after extraction.");
            ReportAppStoreHygiene(failures, $"IPA {ipa.Name}");
            return;
        }

        var frameworksDir = Path.Combine(appBundle, "Frameworks");

        // (a) The runtime is embedded as a real framework BUNDLE with the @rpath install_name.
        var runtimeFwBinary = Path.Combine(frameworksDir, "SwiftBindingsRuntime.framework", "SwiftBindingsRuntime");
        if (!File.Exists(runtimeFwBinary))
        {
            failures.Add(
                "Frameworks/SwiftBindingsRuntime.framework/SwiftBindingsRuntime is missing — the runtime was " +
                "not embedded as a framework. The buildTransitive NativeReference Kind=\"Framework\" did not fire.");
        }
        else
        {
            const string expectedInstallName = "@rpath/SwiftBindingsRuntime.framework/SwiftBindingsRuntime";
            var installName = MachOReader.ReadInstallName(runtimeFwBinary);
            if (installName is null)
                failures.Add(
                    "could not read an LC_ID_DYLIB install_name from the embedded runtime framework binary " +
                    $"({Path.GetFileName(runtimeFwBinary)}) — a present-but-unreadable Mach-O is a defect, not a pass.");
            else if (installName != expectedInstallName)
                failures.Add(
                    $"embedded runtime framework has install_name '{installName}', expected '{expectedInstallName}' " +
                    "— @rpath resolution into the app's Frameworks/ depends on this.");

            // The framework bundle must carry a valid signature after the workload re-signed it.
            AssertCodesignVerifies(
                (AbsolutePath)Path.Combine(frameworksDir, "SwiftBindingsRuntime.framework"),
                "the embedded runtime framework", failures);
        }

        // (b) No loose runtime dylib — the exact TN2435-rejected shape from issue #42.
        if (File.Exists(Path.Combine(frameworksDir, "libSwiftBindingsRuntime.dylib")))
            failures.Add(
                "Frameworks/libSwiftBindingsRuntime.dylib is present — a loose, non-framework Swift dylib that " +
                "App Store review rejects (TN2435 / ITMS-90426/90429). The runtime must embed only as a framework.");

        // (c) Zero embedded libswift*.dylib anywhere in the app — the stable-ABI claim. If this fails,
        //     the runtime is back-deploying Swift instead of linking the OS copies, and the whole
        //     "no SwiftSupport needed" premise is wrong.
        foreach (var f in Directory.GetFiles(appBundle, "libswift*.dylib", SearchOption.AllDirectories))
            failures.Add(
                $"app embeds a back-deployment Swift dylib: {Path.GetRelativePath(appBundle, f)} — a stable-ABI app " +
                "(iOS 15, /usr/lib/swift) must embed none. This would require a SwiftSupport/ folder and bloats the bundle.");

        // (d) The app signature still verifies — proves packaging did not corrupt the bundle.
        AssertCodesignVerifies((AbsolutePath)appBundle, "the app bundle", failures);

        ReportAppStoreHygiene(failures, $"IPA {ipa.Name}");
    }

    // codesign --verify --strict on a bundle/binary, collecting a failure with the codesign output.
    static void AssertCodesignVerifies(AbsolutePath path, string what, List<string> failures)
    {
        var verify = ProcessTasks.StartProcess(
                XcRun.FindTool("codesign"),
                ArgumentEscaper.Join(new[] { "--verify", "--strict", path.ToString() }),
                logOutput: false)
            .AssertWaitForExit();
        if (verify.ExitCode != 0)
            failures.Add(
                $"codesign --verify --strict failed (exit {verify.ExitCode}) on {what} ({path.Name}):\n" +
                string.Join("\n", verify.Output.Select(o => o.Text)));
    }

    // Common pass/fail reporting: fail the gate with all collected defects, or log the OK line.
    static void ReportAppStoreHygiene(List<string> failures, string artifact)
    {
        if (failures.Count > 0)
        {
            Log.Error("--appstore-hygiene gate FAILED — {Count} defect(s) in {Artifact}:", failures.Count, artifact);
            foreach (var f in failures) Log.Error("  {Detail}", f);
            Assert.Fail($"--appstore-hygiene: {failures.Count} defect(s) in {artifact} — see log.");
        }

        Log.Information(
            "--appstore-hygiene gate OK — {Artifact}: runtime embedded as a signed SwiftBindingsRuntime.framework " +
            "(@rpath install_name), no loose dylib, zero embedded libswift*.dylib, no SwiftSupport/ folder, app signature verifies.",
            artifact);
    }
}
