// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.BindingTests.SwiftSupport.cs — opt-in App Store SwiftSupport-folder gate (issue #42)
//
// Closes the one gap nothing else covers: a .NET-for-iOS DEVICE IPA built through our binding
// must carry a compliant top-level SwiftSupport/iphoneos folder, or App Store Connect rejects
// the upload with ITMS-90426. The fix is an MSBuild target in the Runtime package's
// buildTransitive targets (AfterTargets="CreateIpa") that post-processes the finished .ipa via
// build/add-swiftsupport-folder.sh. Because that target leans on workload-version-specific
// behavior ($(IpaPackagePath), the CreateIpa target name), a workload bump that breaks the hook
// must fail OUR CI here — not a user's submission.
//
// WHAT THIS GATE DOES
//   1. Packs SwiftBindings.Runtime at a throwaway version into a local feed (so the
//      buildTransitive target + script are exercised through a REAL package, exactly as a
//      consumer gets them — buildTransitive does not flow across a ProjectReference).
//   2. Writes a tiny consumer app that takes ONE PackageReference on SwiftBindings.Runtime and
//      sets <BuildIpa>true</BuildIpa>, then publishes it for ios-arm64 (device).
//   3. Asserts the produced .ipa has SwiftSupport/iphoneos that is non-empty, contains only
//      Apple-signed libswift*.dylib entries, no .DS_Store / __MACOSX, and a Payload/ whose app
//      signature still verifies.
//
// WHY A BARE RUNTIME REFERENCE IS A FAITHFUL FIXTURE
//   The SwiftBindings.Runtime package bundles libSwiftBindingsRuntime.dylib into the app's
//   Frameworks/, and that dylib non-weak-links /usr/lib/swift/libswiftCore, libswiftDispatch,
//   libswift_Concurrency, libswiftFoundation, libswiftCoreGraphics (plus weak ones). So a
//   consumer that merely references the Runtime and builds a device IPA already requires a
//   populated SwiftSupport folder — the same condition any real Swift binding triggers. This
//   exercises the full target → script path including the weak/non-weak split, the
//   dependency-closure walk, and the dynamic toolchain-dir discovery, without the cost of
//   generating + packing a whole Swift binding.
//
// OPT-IN BY DESIGN. Never part of the default `nuke binding-tests` run or `--compile-only`. It
// packs a feed and publishes a device IPA (minutes, not seconds) and needs a code-signing
// identity on the host — but NO connected device (the IPA is built and inspected on the build
// host). Run it before a release and after changes to the SwiftSupport target/script, native
// packaging policy, or anything touching the workload's IPA-creation hook. Example:
//   nuke binding-tests --swiftsupport

using System;
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
    [Parameter("Opt-in: build a device IPA through a Swift-binding consumer and assert a compliant App Store SwiftSupport/iphoneos folder is injected (issue #42). Builds + inspects on the host; needs a code-signing identity but no connected device. Never part of the default run or --compile-only.")]
    readonly bool Swiftsupport;

    // Throwaway version with its own suffix so this leg's NuGet-cache clears never collide with
    // --mixed-pack / --mixed-direct.
    const string SwiftSupportVersion = "0.0.0-swiftsupport";

    const string SwiftSupportAppName = "SwiftSupportApp";
    const string SwiftSupportBundleId = "com.swiftbindings.swiftsupport";

    // Device RID — BuildIpa is only honored on a device publish, and the SwiftSupport folder is
    // an iphoneos (device) artifact.
    const string SwiftSupportIosRid = "ios-arm64";
    const string SwiftSupportPlatformDir = "iphoneos";

    AbsolutePath SwiftSupportScratch => RootDirectory / "artifacts" / "swiftsupport";

    // Entry point invoked from the BindingTests dispatch when --swiftsupport is set.
    void RunSwiftSupportLeg()
    {
        Log.Information("=================================================");
        Log.Information(" BindingTests — App Store SwiftSupport folder gate");
        Log.Information("=================================================");

        var scratch = SwiftSupportScratch;
        if (Directory.Exists(scratch)) scratch.DeleteDirectory();
        var nupkgDir = scratch / "packages";
        var appDir = scratch / "consumer";
        nupkgDir.CreateDirectory();
        appDir.CreateDirectory();

        using var scope = new VersionScope(SwiftSupportVersion, RootDirectory);

        BuildSwiftSupportFeed(nupkgDir);

        WriteSwiftSupportConsumerApp(appDir);
        File.WriteAllText(appDir / "NuGet.config", MixedPackNuGetConfig(nupkgDir, fixtureNupkgDir: null));

        Log.Information("=== swiftsupport: publishing device IPA (ios-arm64) ===");
        DotNetPublish(s => s
            .SetProject(appDir / $"{SwiftSupportAppName}.csproj")
            .SetConfiguration("Release")
            .SetRuntime(SwiftSupportIosRid)
            .EnableNoLogo()
            .SetVerbosity(DotNetVerbosity.quiet));

        // Locate the produced .ipa. The exact intermediate layout varies, so search the bin tree
        // for any *.ipa under the device RID. Deterministic selection: shortest path then ordinal.
        var ipas = Directory
            .GetFiles(appDir / "bin", "*.ipa", SearchOption.AllDirectories)
            .Where(p => p.Contains(SwiftSupportIosRid, StringComparison.Ordinal))
            .OrderBy(p => p.Length)
            .ThenBy(p => p, StringComparer.Ordinal)
            .ToList();
        var ipa = ipas.FirstOrDefault()
            ?? throw new Exception(
                $"--swiftsupport: no .ipa produced under {appDir / "bin"} for rid {SwiftSupportIosRid}. " +
                "A device publish should set BuildIpa=true and create an IPA — check the publish log above.");
        if (ipas.Count > 1)
            Log.Warning("--swiftsupport: {Count} .ipa files found; selected {Path}", ipas.Count, ipa);
        Log.Information("    IPA: {Path}", ipa);

        AssertSwiftSupportIpa((AbsolutePath)ipa, SwiftSupportPlatformDir, scratch);
    }

    // Packs SwiftBindings.Runtime at the throwaway version into the local feed and clears any
    // stale same-version cache entry so the freshly-built package (with the new SwiftSupport
    // target + script) is the one restored. Only the Runtime is needed: the consumer takes a
    // single PackageReference on it and the buildTransitive target flows from there.
    void BuildSwiftSupportFeed(AbsolutePath nupkgDir)
    {
        Log.Information("=== swiftsupport: building local feed at {Version} ===", SwiftSupportVersion);

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
        var pkgDir = nugetCacheDir / "swiftbindings.runtime" / SwiftSupportVersion;
        if (Directory.Exists(pkgDir)) pkgDir.DeleteDirectory();
    }

    // The consumer: a minimal net10.0-ios app that takes ONE PackageReference on
    // SwiftBindings.Runtime and forces an IPA on the device publish. No NativeAOT — a plain
    // Mono device publish still runs CreateIpa and bundles libSwiftBindingsRuntime.dylib (the
    // SwiftSupport behavior is identical to the AOT path and far cheaper to produce here).
    static void WriteSwiftSupportConsumerApp(AbsolutePath appDir)
    {
        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0-ios</TargetFramework>
                <RuntimeIdentifier>{SwiftSupportIosRid}</RuntimeIdentifier>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <ApplicationId>{SwiftSupportBundleId}</ApplicationId>
                <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
                <ApplicationVersion>1</ApplicationVersion>
                <SupportedOSPlatformVersion>15.0</SupportedOSPlatformVersion>
                <!-- Mirror RuntimeTestsApp's trim-warning suppression: an iOS Release publish
                     trims, and Swift.Runtime's reflection-heavy interop surfaces the IL2xxx
                     family (IL2104 is the assembly-level rollup). This gate inspects IPA
                     packaging structure, not trim correctness (RuntimeTestsApp covers that),
                     so suppress the same set the shipped apps do rather than fail the publish. -->
                <NoWarn>$(NoWarn);CA1416;CA1422;IL2065;IL2075;IL2087;IL2091;IL2026;IL2104</NoWarn>

                <!-- Force the App Store IPA on this device publish so the workload's CreateIpa
                     (and our AfterTargets hook) runs. -->
                <BuildIpa>true</BuildIpa>

                <!-- Justin's wildcard dev identity (matches RuntimeTestsApp / the mixed legs). -->
                <CodesignKey>Apple Development: Justin Wojciechowski (KBKS29A36Q)</CodesignKey>
                <CodesignProvision>Wildcard Dev</CodesignProvision>
                <TeamIdentifierPrefix>TL2K6QUQEH</TeamIdentifierPrefix>
              </PropertyGroup>

              <!-- The single reference under test: the Runtime package's buildTransitive targets
                   (with the new SwiftSupport injector) flow into this app from here. -->
              <ItemGroup>
                <PackageReference Include="SwiftBindings.Runtime" Version="{SwiftSupportVersion}" />
              </ItemGroup>

              <ItemGroup>
                <None Include="Info.plist" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(appDir / $"{SwiftSupportAppName}.csproj", csproj);

        // Minimal UIKit app — it is never launched (the gate only inspects the IPA structure),
        // but it must be a valid iOS app for the workload to package one. Reference a
        // SwiftBindings.Runtime type so the managed assembly is genuinely linked (keeping the
        // fixture honest), even though the bundled native dylib is what drives the SwiftSupport
        // requirement regardless.
        var program = $$"""
            // Copyright (c) 2026 Justin Wojciechowski.
            // Licensed under the MIT License.
            using Foundation;
            using UIKit;

            namespace SwiftSupportApp;

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

    // Structural + signing assertions on the produced IPA. Enumerates zip entries for the cheap
    // structural checks, then ditto-extracts (preserving signatures) for the codesign checks.
    void AssertSwiftSupportIpa(AbsolutePath ipa, string platformDir, AbsolutePath scratch)
    {
        var prefix = $"SwiftSupport/{platformDir}/";
        var failures = new System.Collections.Generic.List<string>();

        using (var zip = ZipFile.OpenRead(ipa))
        {
            var entries = zip.Entries.Select(e => e.FullName).ToList();

            // (a) the folder exists and is non-empty.
            var swiftSupportFiles = entries
                .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && !n.EndsWith("/", StringComparison.Ordinal))
                .ToList();
            if (swiftSupportFiles.Count == 0)
                failures.Add(
                    $"SwiftSupport/{platformDir}/ is missing or empty — the injector did not run or found no embeddable " +
                    "Swift dylibs. (Is libSwiftBindingsRuntime.dylib bundled in the app's Frameworks/? Did CreateIpa fire?)");

            // (b) every entry under it is a libswift*.dylib.
            foreach (var f in swiftSupportFiles)
            {
                var name = Path.GetFileName(f);
                if (!(name.StartsWith("libswift", StringComparison.Ordinal) && name.EndsWith(".dylib", StringComparison.Ordinal)))
                    failures.Add($"unexpected non-libswift entry in SwiftSupport/{platformDir}/: {f}");
            }

            // (c) no Finder/zip litter — these are exactly the things that drew ITMS-90430.
            foreach (var n in entries)
            {
                if (Path.GetFileName(n) == ".DS_Store")
                    failures.Add($"stray .DS_Store in the IPA: {n}");
                if (n.StartsWith("__MACOSX", StringComparison.Ordinal))
                    failures.Add($"stray __MACOSX in the IPA: {n}");
            }

            // (d) Payload/ is intact (the app bundle is still there).
            if (!entries.Any(n => n.StartsWith("Payload/", StringComparison.Ordinal) && n.Contains(".app/", StringComparison.Ordinal)))
                failures.Add("Payload/<app>.app is missing — the re-zip did not preserve the app bundle.");
        }

        // Extract with ditto (preserves symlinks + code signatures) for the signing checks.
        var extract = scratch / "ipa-extract";
        if (Directory.Exists(extract)) extract.DeleteDirectory();
        extract.CreateDirectory();
        ProcessTasks.StartProcess("ditto", ArgumentEscaper.Join(new[] { "-x", "-k", ipa.ToString(), extract.ToString() }), logOutput: false)
            .AssertWaitForExit()
            .AssertZeroExitCode();

        // (e) each SwiftSupport dylib keeps Apple's signature (we ditto-copied, never re-signed).
        var swiftSupportDir = extract / "SwiftSupport" / platformDir;
        if (Directory.Exists(swiftSupportDir))
        {
            foreach (var dylib in Directory.GetFiles(swiftSupportDir, "*.dylib"))
            {
                var auth = CodesignDisplay(dylib);
                // Require the Apple PLATFORM-binary leaf authority ("Software Signing"), not merely
                // a substring "Apple": a dylib re-signed with an "Apple Development: …" identity
                // also contains "Apple" (and even "Apple Root CA"), yet is NOT the preserved Apple
                // toolchain signature App Store validation expects. ditto must have copied it verbatim.
                if (!auth.Contains("Authority=Software Signing", StringComparison.Ordinal))
                    failures.Add(
                        $"SwiftSupport dylib lacks the Apple platform-binary signature (expected 'Authority=Software Signing'; " +
                        $"a re-sign or unsigned copy would fail this): {Path.GetFileName(dylib)} — codesign authorities:\n{auth}");
            }
        }

        // (f) the app signature still verifies — proves the re-zip did not corrupt Payload/.
        var appBundle = Directory.GetDirectories(extract / "Payload", "*.app").FirstOrDefault();
        if (appBundle is null)
        {
            failures.Add("no .app under Payload/ after extraction.");
        }
        else
        {
            var verify = ProcessTasks.StartProcess(
                    XcRun.FindTool("codesign"),
                    ArgumentEscaper.Join(new[] { "--verify", "--strict", appBundle }),
                    logOutput: false)
                .AssertWaitForExit();
            if (verify.ExitCode != 0)
                failures.Add(
                    $"app signature failed codesign --verify after SwiftSupport injection (exit {verify.ExitCode}) — the re-zip " +
                    $"may have corrupted Payload/:\n{string.Join("\n", verify.Output.Select(o => o.Text))}");
        }

        // (g) completeness: SwiftSupport must contain a back-deployment copy of EVERY Swift runtime
        //     dylib in the closure rooted at the app's own Mach-Os — the dylibs the app references
        //     directly (via /usr/lib/swift) AND everything those toolchain copies transitively pull
        //     in (via @rpath). This independently re-derives the set the injector should have copied
        //     (seeded from the app, NOT just from what the injector chose to copy) so it catches both
        //     a missed direct ref and a missed transitive dep — a non-empty-but-incomplete folder
        //     fails here even though (a)/(b) pass. Deps with no toolchain copy are OS-resident or
        //     embedded and correctly excluded; this mirrors the injector's find_copy gate.
        var swiftLibDir = ToolchainSwiftLibDir();
        if (Directory.Exists(swiftSupportDir) && appBundle is not null && swiftLibDir is not null)
        {
            var present = Directory.GetFiles(swiftSupportDir, "*.dylib")
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.Ordinal);

            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            var queue = new System.Collections.Generic.Queue<string>();
            foreach (var dep in AppMachOs(appBundle).SelectMany(SwiftDylibDeps))
                if (seen.Add(dep)) queue.Enqueue(dep);

            while (queue.Count > 0)
            {
                var dep = queue.Dequeue();
                var copy = ToolchainCopyPath(swiftLibDir, platformDir, dep);
                if (copy is null) continue; // OS-resident/embedded — correctly not in SwiftSupport
                if (!present.Contains(dep))
                    failures.Add(
                        $"SwiftSupport is incomplete: {dep} is in the app's Swift-runtime closure and HAS an Apple " +
                        $"back-deployment copy in the toolchain, but is absent from SwiftSupport/{platformDir}/ — the injector " +
                        "under-copied (direct ref or transitive @rpath dep). App Store Connect would reject this (ITMS-90426).");
                foreach (var d2 in SwiftDylibDeps(copy)) // expand the toolchain copy's own @rpath deps
                    if (seen.Add(d2)) queue.Enqueue(d2);
            }
        }

        if (failures.Count > 0)
        {
            Log.Error("--swiftsupport gate FAILED — {Count} defect(s) in {Ipa}:", failures.Count, ipa.Name);
            foreach (var f in failures) Log.Error("  {Detail}", f);
            Assert.Fail($"--swiftsupport: {failures.Count} defect(s) in the produced IPA — see log.");
        }

        var count = Directory.Exists(swiftSupportDir) ? Directory.GetFiles(swiftSupportDir, "*.dylib").Length : 0;
        Log.Information(
            "--swiftsupport gate OK — SwiftSupport/{Platform}/ has {Count} Apple-signed libswift*.dylib, no .DS_Store/__MACOSX, Payload signature verifies.",
            platformDir, count);
    }

    // codesign -dvvv prints the signature display (authorities, etc.) to stderr. ProcessTasks
    // aggregates stdout AND stderr into .Output, so the authority lines are captured here. Does
    // NOT assert a zero exit (an unsigned input returns non-zero, which we report as a failure via
    // the missing-authority text).
    static string CodesignDisplay(string path)
    {
        var proc = ProcessTasks.StartProcess(
                XcRun.FindTool("codesign"),
                ArgumentEscaper.Join(new[] { "-dvvv", path }),
                logOutput: false)
            .AssertWaitForExit();
        return string.Join("\n", proc.Output.Select(o => o.Text));
    }

    // Basenames of the Swift runtime dylibs a Mach-O depends on, parsed from `otool -l`. Matches
    // BOTH install-name forms: /usr/lib/swift/libswift*.dylib (OS-runtime refs) and
    // @rpath/libswift*.dylib (how the toolchain back-deployment copies reference each other) —
    // the @rpath form is exactly what a naive closure walk misses.
    static System.Collections.Generic.IEnumerable<string> SwiftDylibDeps(string machoPath)
    {
        var proc = ProcessTasks.StartProcess("otool", ArgumentEscaper.Join(new[] { "-l", machoPath }), logOutput: false)
            .AssertWaitForExit();
        var deps = new System.Collections.Generic.List<string>();
        bool inLoadDylib = false;
        foreach (var line in proc.Output.Select(o => o.Text))
        {
            var t = line.Trim();
            if (t.StartsWith("cmd ", StringComparison.Ordinal))
                inLoadDylib = t is "cmd LC_LOAD_DYLIB" or "cmd LC_LOAD_WEAK_DYLIB";
            else if (inLoadDylib && t.StartsWith("name ", StringComparison.Ordinal))
            {
                // "name <path> (offset N)"
                var path = t.Substring(5).Split(' ')[0];
                if ((path.StartsWith("/usr/lib/swift/libswift", StringComparison.Ordinal) ||
                     path.StartsWith("@rpath/libswift", StringComparison.Ordinal)) &&
                    path.EndsWith(".dylib", StringComparison.Ordinal))
                    deps.Add(Path.GetFileName(path));
                inLoadDylib = false;
            }
        }
        return deps.Distinct(StringComparer.Ordinal);
    }

    // The active Xcode toolchain's usr/lib (parent of the swift-*/<platform>/ back-deployment dirs),
    // resolved ONCE via xcode-select -p. Null if unresolved. Callers cache this rather than spawning
    // a process per dylib.
    static string? ToolchainSwiftLibDir()
    {
        var devProc = ProcessTasks.StartProcess("xcode-select", "-p", logOutput: false).AssertWaitForExit();
        var devDir = devProc.Output.Select(o => o.Text).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(devDir)) return null;
        var swiftLib = Path.Combine(devDir, "Toolchains", "XcodeDefault.xctoolchain", "usr", "lib");
        return Directory.Exists(swiftLib) ? swiftLib : null;
    }

    // Path of the Apple-signed back-deployment copy of `basename` for the platform, or null if the
    // toolchain has none (mirrors the script's find_copy: first matching swift-*/<platform>/<basename>).
    static string? ToolchainCopyPath(string swiftLibDir, string platformDir, string basename)
    {
        foreach (var d in Directory.GetDirectories(swiftLibDir, "swift-*"))
        {
            var p = Path.Combine(d, platformDir, basename);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // The app bundle's own Mach-Os that can pull in the Swift runtime: the main executable plus
    // every file under Frameworks/ and PlugIns/*.appex/ (SwiftDylibDeps tolerates non-Mach-O inputs,
    // so over-inclusion is harmless). Mirrors the injector's `machos` collection.
    static System.Collections.Generic.IEnumerable<string> AppMachOs(string appBundle)
    {
        var exeProc = ProcessTasks.StartProcess(
                "/usr/libexec/PlistBuddy",
                ArgumentEscaper.Join(new[] { "-c", "Print :CFBundleExecutable", Path.Combine(appBundle, "Info.plist") }),
                logOutput: false)
            .AssertWaitForExit();
        var exe = exeProc.Output.Select(o => o.Text).FirstOrDefault()?.Trim();
        if (!string.IsNullOrEmpty(exe))
        {
            var exePath = Path.Combine(appBundle, exe);
            if (File.Exists(exePath)) yield return exePath;
        }
        foreach (var sub in new[] { "Frameworks", "PlugIns" })
        {
            var dir = Path.Combine(appBundle, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                yield return f;
        }
    }
}
