// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.BindingTests.AppStoreHygiene.Mac.cs — macOS + Mac Catalyst legs of the App Store hygiene gate.
//
// WHAT THIS COVERS THAT THE IOS LEG CANNOT
//   Apple asks for two different framework layouts. On iOS and tvOS a framework must be SHALLOW —
//   the binary and Info.plist sit at the bundle root — and that is the shape our xcframeworks ship
//   in on every slice. On macOS and Mac Catalyst the Mac App Store instead requires the VERSIONED
//   layout: the payload lives under Versions/A/, Versions/Current is a symbolic link to it, and the
//   bundle root holds only symbolic links into Versions/Current. A shallow framework embedded in a
//   .app is rejected at upload as a malformed framework, and nothing in a build, a launch, or a
//   runtime test notices — the app links and runs perfectly either way.
//
//   So the iOS leg's assertions and these are opposites by design, and both are correct. The iOS
//   leg proves the bundle stays flat and free of loose dylibs; these legs prove that on the two Mac
//   platforms the frameworks the app ended up embedding were given the versioned layout, with real
//   relative symbolic links, and that the signature over that layout verifies.
//
// WHY IT IS ASSERTED ON A BUILT APP RATHER THAN ON THE PACKAGE
//   The versioned layout cannot travel inside a .nupkg: zip archives carry neither symbolic links
//   nor the distinction between a link and a copy, so any structural claim made about the package
//   would say nothing about the app. The links have to be created on the consumer's Mac, inside the
//   app bundle, after the workload has finished assembling it and before it is signed — and that is
//   exactly where these legs look.
//
// BOTH FLOWS, BOTH PLATFORMS
//   Each platform runs `dotnet build` and `dotnet publish`. They enter signing through different
//   targets, and a step anchored correctly for one is not automatically correct for the other, so
//   the gate exercises both rather than inferring the second from the first.
//
// NO SIGNING IDENTITY NEEDED
//   macOS and Mac Catalyst app bundles are signed ad-hoc by default, so `codesign --verify --strict`
//   is meaningful here on any Mac. These legs therefore run even on a host that has to skip the
//   device-IPA leg for want of a distribution identity.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    const string MacAnatomyAppName = "MacAnatomyApp";
    const string MacAnatomyBundleId = "com.swiftbindings.macanatomy";

    // The host's own architecture: these legs build and inspect on this Mac, never on a device.
    static string MacAnatomyHostArch =>
        RuntimeInformation.OSArchitecture == Architecture.X64 ? "x64" : "arm64";

    // Runs the macOS and Mac Catalyst framework-anatomy legs against the feed the hygiene gate
    // already packed. Called before the device-IPA leg's signing-identity tri-state, because these
    // legs need no identity and should not be lost to a host that cannot sign for device.
    void RunMacFrameworkAnatomyLegs(AbsolutePath scratch, AbsolutePath nupkgDir)
    {
        Log.Information("=== appstore-hygiene: Mac framework anatomy (macOS + Mac Catalyst) ===");

        foreach (var (platform, tfm, rid) in new[]
                 {
                     ("macOS", "net10.0-macos", $"osx-{MacAnatomyHostArch}"),
                     ("Mac Catalyst", "net10.0-maccatalyst", $"maccatalyst-{MacAnatomyHostArch}"),
                 })
        {
            var appDir = scratch / ("mac-anatomy-" + tfm);
            if (Directory.Exists(appDir)) appDir.DeleteDirectory();
            appDir.CreateDirectory();

            WriteMacAnatomyConsumerApp(appDir, tfm, rid);
            File.WriteAllText(appDir / "NuGet.config", MixedPackNuGetConfig(nupkgDir, fixtureNupkgDir: null));

            RunMacAnatomyFlow(platform, appDir, rid, publish: false);
            RunMacAnatomyFlow(platform, appDir, rid, publish: true);
        }
    }

    // One flow (build or publish) for one platform: produce the .app, then assert on every framework
    // it embedded.
    void RunMacAnatomyFlow(string platform, AbsolutePath appDir, string rid, bool publish)
    {
        var flow = publish ? "publish" : "build";
        Log.Information("--- {Platform} / dotnet {Flow} ({Rid}) ---", platform, flow, rid);

        var project = appDir / $"{MacAnatomyAppName}.csproj";
        if (publish)
        {
            DotNetPublish(s => s
                .SetProject(project)
                .SetConfiguration("Release")
                .SetRuntime(rid)
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet));
        }
        else
        {
            DotNetBuild(s => s
                .SetProjectFile(project)
                .SetConfiguration("Release")
                .SetRuntime(rid)
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet));
        }

        var failures = new List<string>();

        // Positive sentinel, the sibling of the runtime embed stamp: the anatomy step writes it into
        // the app's obj/ when it runs. Asserting it here means a future workload change that moves
        // the step's anchor target shows up as a named failure rather than as an app that quietly
        // ships the wrong layout.
        var stamps = Directory.GetFiles(appDir / "obj", "swiftbindings-mac-anatomy.stamp", SearchOption.AllDirectories);
        if (stamps.Length == 0)
            failures.Add(
                $"the Mac framework-anatomy sentinel (swiftbindings-mac-anatomy.stamp) was not produced under " +
                $"{appDir / "obj"} for the {platform} {flow}. The step anchors between the workload's app-bundle " +
                "post-processing and its codesigning targets; its absence means that anchor no longer binds.");

        var appBundles = Directory
            .GetDirectories(appDir / "bin", $"{MacAnatomyAppName}.app", SearchOption.AllDirectories)
            .OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal)
            .ToList();
        if (appBundles.Count == 0)
        {
            failures.Add($"no {MacAnatomyAppName}.app produced under {appDir / "bin"} for the {platform} {flow}.");
            ReportAppStoreHygiene(failures, $"{platform} {flow}");
            return;
        }

        foreach (var appBundle in appBundles)
            AssertMacAppFrameworkAnatomy((AbsolutePath)appBundle, platform, flow, failures);

        if (failures.Count > 0)
        {
            Log.Error("--appstore-hygiene Mac anatomy FAILED — {Count} defect(s) in the {Platform} {Flow}:",
                failures.Count, platform, flow);
            foreach (var f in failures) Log.Error("  {Detail}", f);
            Assert.Fail($"--appstore-hygiene: {failures.Count} Mac framework-anatomy defect(s) in the {platform} {flow} — see log.");
        }

        Log.Information(
            "--appstore-hygiene Mac anatomy OK — {Platform} {Flow}: every embedded framework carries the versioned " +
            "layout (Versions/Current naming the version directory that is there, every root entry a link to exactly " +
            "its counterpart under Current, the binary under Versions/A, Info.plist resolving through Current), its " +
            "install_name still resolves inside the rewritten bundle, and its signature verifies.", platform, flow);
    }

    // The version directory the step writes. Apple allows any name here, but pinning it is what
    // lets the assertions below compare link targets exactly rather than merely "relative".
    const string ExpectedVersionDirectory = "A";

    // The layout assertions, per embedded framework, plus the app's own signature.
    void AssertMacAppFrameworkAnatomy(AbsolutePath appBundle, string platform, string flow, List<string> failures)
    {
        var frameworksDir = appBundle / "Contents" / "Frameworks";
        if (!Directory.Exists(frameworksDir))
        {
            // The fixture takes a PackageReference on the runtime, whose framework must embed. An
            // empty Frameworks/ would mean the fixture stopped exercising the thing under test.
            failures.Add(
                $"{platform} {flow}: {appBundle.Name} has no Contents/Frameworks — the runtime framework did not " +
                "embed, so this leg would pass without observing anything.");
            return;
        }

        var frameworks = Directory.GetDirectories(frameworksDir, "*.framework");
        if (frameworks.Length == 0)
        {
            failures.Add($"{platform} {flow}: {appBundle.Name}/Contents/Frameworks contains no .framework bundle.");
            return;
        }

        foreach (var fw in frameworks)
        {
            var name = Path.GetFileName(fw);
            var where = $"{platform} {flow}: {name}";

            // Versions/Current must be a link, and it must name the version directory the payload
            // is actually in. A copy of the payload, or a link left over from a version that is no
            // longer there, both pass a casual file-exists check and still fail upload validation.
            var current = Path.Combine(fw, "Versions", "Current");
            if (!Directory.Exists(Path.Combine(fw, "Versions")))
            {
                failures.Add(
                    $"{where} has no Versions/ directory — it is still in the shallow (iOS) layout, which the Mac " +
                    "App Store rejects as a malformed framework.");
                continue;
            }
            var currentTarget = new FileInfo(current).LinkTarget;
            if (currentTarget is null)
                failures.Add($"{where}: Versions/Current is not a symbolic link (it is a real directory or file).");
            else if (currentTarget != ExpectedVersionDirectory)
                failures.Add(
                    $"{where}: Versions/Current points at '{currentTarget}', expected the relative name " +
                    $"'{ExpectedVersionDirectory}' of the version directory beside it.");
            else if (!Directory.Exists(Path.Combine(fw, "Versions", ExpectedVersionDirectory)))
                failures.Add($"{where}: Versions/Current names '{ExpectedVersionDirectory}', but no such version directory exists.");

            // The bundle root holds Versions/ and symbolic links, and every one of those links
            // stands for the entry of the same name under Versions/Current — a link that reaches
            // past Current, or to something else, is the layout being wrong rather than done.
            foreach (var entry in Directory.GetFileSystemEntries(fw))
            {
                var entryName = Path.GetFileName(entry);
                if (entryName == "Versions") continue;

                var target = new FileInfo(entry).LinkTarget;
                if (target is null)
                {
                    failures.Add(
                        $"{where}: '{entryName}' at the bundle root is a real file or directory. In the versioned " +
                        "layout the root carries only Versions/ and symbolic links into Versions/Current.");
                    continue;
                }

                var expected = $"Versions/Current/{entryName}";
                if (target != expected)
                    failures.Add($"{where}: root link '{entryName}' points at '{target}', expected '{expected}'.");
                else if (!File.Exists(entry) && !Directory.Exists(entry))
                    failures.Add($"{where}: root link '{entryName}' does not resolve — nothing of that name is under Versions/Current.");
            }

            // The executable is named by the bundle, not guessed from whichever link happens to
            // resolve to a file, so a bundle that lost its executable link is a failure here.
            var executable = Path.GetFileNameWithoutExtension(name);
            var rootExecutable = Path.Combine(fw, executable);
            var versionedExecutable = Path.Combine(fw, "Versions", ExpectedVersionDirectory, executable);
            if (new FileInfo(rootExecutable).LinkTarget is null || !File.Exists(rootExecutable))
                failures.Add(
                    $"{where}: the bundle root has no symbolic link '{executable}' resolving to the binary — " +
                    $"@rpath/{name}/{executable} has nothing to follow.");
            else if (!File.Exists(versionedExecutable))
                failures.Add($"{where}: the binary is not at Versions/{ExpectedVersionDirectory}/{executable}.");
            else
                AssertMacFrameworkLoadPath(fw, name, executable, where, failures);

            // The plist has to be reachable at the path a validator reads it from, through the links.
            if (!File.Exists(Path.Combine(fw, "Resources", "Info.plist")))
                failures.Add(
                    $"{where}: Resources/Info.plist does not resolve (it must live at " +
                    $"Versions/{ExpectedVersionDirectory}/Resources/Info.plist and be reachable through the " +
                    "Resources and Versions/Current links).");

            // A signature made over the shallow layout does not describe this one; the rewrite has to
            // precede signing for this to hold.
            AssertCodesignVerifies((AbsolutePath)fw, $"{platform} {flow}: the embedded {name}", failures);
        }

        AssertCodesignVerifies(appBundle, $"{platform} {flow}: the app bundle", failures);
    }

    // The binary moved, so the path its own install_name advertises has to still lead to it. The
    // frameworks are built in the shallow shape and keep an install_name of
    // @rpath/<Name>.framework/<Name>, which after the rewrite reaches the binary through the root
    // link; a framework carrying Apple's versioned convention
    // (@rpath/<Name>.framework/Versions/A/<Name>) reaches it directly. Rather than pick a side,
    // this resolves whatever path the binary names and asserts it lands on a real file inside the
    // bundle — which is the property dyld depends on either way.
    void AssertMacFrameworkLoadPath(string fw, string bundleName, string executable, string where, List<string> failures)
    {
        var binary = Path.Combine(fw, "Versions", ExpectedVersionDirectory, executable);
        var installName = MachOReader.ReadInstallName(binary);
        if (string.IsNullOrEmpty(installName))
        {
            failures.Add($"{where}: could not read an LC_ID_DYLIB install_name from {binary}.");
            return;
        }

        var prefix = $"@rpath/{bundleName}/";
        if (!installName.StartsWith(prefix, StringComparison.Ordinal))
        {
            failures.Add(
                $"{where}: install_name is '{installName}', which does not begin with '{prefix}' — the embedded " +
                "copy is not what @rpath resolution inside the app would reach.");
            return;
        }

        var advertised = Path.Combine(fw, installName.Substring(prefix.Length).Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(advertised))
            failures.Add(
                $"{where}: install_name '{installName}' does not resolve inside the rewritten bundle — the binary " +
                "moved under Versions/ and the path it advertises no longer leads to it.");
    }

    // A minimal app whose only reference is the runtime package, so the anatomy step reaches it the
    // way a consumer's does: through the package's own buildTransitive targets.
    static void WriteMacAnatomyConsumerApp(AbsolutePath appDir, string tfm, string rid)
    {
        var isCatalyst = tfm.Contains("maccatalyst", StringComparison.Ordinal);

        // A macOS Release build otherwise insists on trimming; this gate inspects bundle structure,
        // and trimming only slows that down.
        var linkMode = isCatalyst ? "" : "\n    <LinkMode>None</LinkMode>";

        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{tfm}</TargetFramework>
                <RuntimeIdentifier>{rid}</RuntimeIdentifier>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <ApplicationId>{MacAnatomyBundleId}</ApplicationId>
                <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
                <ApplicationVersion>1</ApplicationVersion>{linkMode}
                <!-- Same suppression set the shipped apps carry: the runtime's reflection-heavy
                     interop surface raises the IL2xxx family, which is not what this gate observes. -->
                <NoWarn>$(NoWarn);CA1416;CA1422;IL2065;IL2075;IL2087;IL2091;IL2026;IL2104</NoWarn>
              </PropertyGroup>

              <!-- The single reference under test. -->
              <ItemGroup>
                <PackageReference Include="SwiftBindings.Runtime" Version="{AppStoreHygieneVersion}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(appDir / $"{MacAnatomyAppName}.csproj", csproj);

        // Never launched — the gate inspects the produced bundle — but it has to be a real app for
        // the workload to assemble and sign one, and it has to touch the runtime assembly so the
        // framework is genuinely pulled in.
        var program = isCatalyst
            ? """
              // Copyright (c) 2026 Justin Wojciechowski.
              // Licensed under the MIT License.
              using Foundation;
              using UIKit;

              namespace MacAnatomyApp;

              public static class Application
              {
                  static void Main(string[] args)
                  {
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
              """
            : """
              // Copyright (c) 2026 Justin Wojciechowski.
              // Licensed under the MIT License.
              using AppKit;

              namespace MacAnatomyApp;

              public static class Application
              {
                  static void Main(string[] args)
                  {
                      GC.KeepAlive(typeof(Swift.SwiftString));
                      NSApplication.Init();
                      NSApplication.Main(args);
                  }
              }
              """;
        File.WriteAllText(appDir / "Program.cs", program);
    }
}
