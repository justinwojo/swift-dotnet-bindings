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
// BOTH FLOWS, BOTH PLATFORMS, PLUS A RE-COPY AND A UNIVERSAL BUILD
//   Each platform runs `dotnet build` and `dotnet publish`. They enter signing through different
//   targets, and a step anchored correctly for one is not automatically correct for the other, so
//   the gate exercises both rather than inferring the second from the first. A third flow forces
//   the workload to copy the frameworks again over the bundle it already rewrote — the state a
//   package update or a lost obj/ produces — and a universal (two-RID) macOS leg covers the
//   per-RID build-and-merge orchestration, where the step fires once per inner build and again on
//   the merged bundle.
//
// STATIC CHECKS, THE SUBMITTED PACKAGE, THEN A LAUNCH
//   Layout, load path, dependency closure and signature are asserted from the bundle on disk. Each
//   publish flow's installer package — the artifact a Mac app is actually submitted as — is then
//   expanded and its payload app put through the same layout assertions, so the archive step gets
//   observed rather than assumed. The shipped bundle is finally launched with a smoke argument and
//   made to dlopen every embedded framework through the rewritten layout, which is the one check
//   that asks dyld rather than approximating it.
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

    // The second framework the fixture embeds, straight from the repo's own xcframework: the runtime
    // framework is binary + Info.plist only, while SBApple also carries a Modules/ directory — the
    // shape a consumer's Apple-framework binding embeds, and the entry a repeated workload copy
    // trips over once the bundle has been rewritten.
    const string MacAnatomyDirectoryEntryFramework = AppleSupplementModuleName;

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
                     // A universal app is built as one bundle per RID and then merged; the step
                     // fires in each inner build and again on the merged bundle, and the reset
                     // target reads workload-private item metadata in both. That orchestration is
                     // its own code path, so it gets its own leg rather than being inferred from
                     // the single-RID ones. macOS only: Catalyst's universal merge is the same
                     // workload machinery, and the x64 Catalyst build would double this leg's cost.
                     ("macOS universal", "net10.0-macos", MacAnatomyUniversalRids),
                 })
        {
            var universal = rid == MacAnatomyUniversalRids;
            var appDir = scratch / ("mac-anatomy-" + tfm + (universal ? "-universal" : ""));
            if (Directory.Exists(appDir)) appDir.DeleteDirectory();
            appDir.CreateDirectory();

            WriteMacAnatomyConsumerApp(appDir, tfm, rid, AppleSupplementXcframeworkDir);
            File.WriteAllText(appDir / "NuGet.config", MixedPackNuGetConfig(nupkgDir, fixtureNupkgDir: null));

            // The universal leg is about the merge orchestration, which publish exercises in full;
            // the build flow adds a second compile of both slices for no new observation.
            if (!universal) RunMacAnatomyFlow(platform, appDir, rid, publish: false);
            RunMacAnatomyFlow(platform, appDir, rid, publish: true);

            // Next flow: the workload copies frameworks with ditto behind a per-framework stamp, so
            // the flows above never copy a second time — and ditto cannot copy a directory onto
            // the directory links a rewritten bundle carries at its root. A package update, a lost
            // obj/, or a publish whose inputs moved all make the stamp stale, and then the copy runs
            // into the rewritten bundle. Wiping the stamps reproduces that exactly, on top of the
            // deepened bundle the previous flows left in bin/.
            ForceMacFrameworkRecopy(appDir);
            RunMacAnatomyFlow(platform, appDir, rid, publish: true, recopy: true);
        }
    }

    const string MacAnatomyUniversalRids = "osx-arm64;osx-x64";

    // Removes the workload's copy stamps for embedded directories so its next copy step runs again
    // over the bundle already in bin/, which is the state a consumer reaches when a framework's
    // source changes under an existing build tree.
    static void ForceMacFrameworkRecopy(AbsolutePath appDir)
    {
        var stampDirs = Directory.GetDirectories(appDir / "obj", "copieddirectories", SearchOption.AllDirectories);
        Assert.True(stampDirs.Length > 0,
            $"no copieddirectories stamp folder under {appDir / "obj"} — the workload's framework-copy step " +
            "no longer stamps where this gate expects, so the re-copy flow cannot be forced.");
        foreach (var d in stampDirs) ((AbsolutePath)d).DeleteDirectory();
    }

    // One flow (build or publish) for one platform: produce the .app, then assert on every framework
    // it embedded.
    void RunMacAnatomyFlow(string platform, AbsolutePath appDir, string rid, bool publish, bool recopy = false)
    {
        var flow = (publish ? "publish" : "build") + (recopy ? " after a forced framework re-copy" : "");
        Log.Information("--- {Platform} / dotnet {Flow} ({Rid}) ---", platform, flow, rid);

        var project = appDir / $"{MacAnatomyAppName}.csproj";
        // A universal app names its RIDs in the project (RuntimeIdentifiers) and is built without
        // -r, which is what makes the workload build every slice and merge them.
        var universal = rid == MacAnatomyUniversalRids;
        if (publish)
        {
            DotNetPublish(s => s
                .SetProject(project)
                .SetConfiguration("Release")
                .When(_ => !universal, x => x.SetRuntime(rid))
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet));
        }
        else
        {
            DotNetBuild(s => s
                .SetProjectFile(project)
                .SetConfiguration("Release")
                .When(_ => !universal, x => x.SetRuntime(rid))
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

        // A universal build leaves one bundle per RID beside the merged one, and only the merged
        // bundle — the shortest path, directly under the TFM directory — is signed and shipped; the
        // per-RID intermediates are left unsigned by the workload with or without the anatomy step.
        // The shipped bundle is the one Apple sees, so it is the one asserted and launched.
        var shipped = (AbsolutePath)appBundles[0];
        var asserted = universal ? new List<string> { shipped } : appBundles;
        foreach (var appBundle in asserted)
            AssertMacAppFrameworkAnatomy((AbsolutePath)appBundle, platform, flow, failures);

        // What a Mac app is submitted as is an installer package, not the .app in bin/. Publish
        // builds one, and it is assembled from the app bundle after signing, so a step that
        // flattened links or dropped the version directory on the way in would leave the bundle in
        // bin/ correct and the submitted payload wrong.
        if (publish)
            AssertMacInstallerPackage(appDir, platform, flow, failures);

        // Layout, load path, dependencies, and signature are all static facts. Launching the app
        // and having it dlopen every embedded framework through the rewritten layout is the one
        // check that asks dyld the question directly, so it runs after the static assertions pass
        // and only on a bundle that is signed.
        if (failures.Count == 0)
            AssertMacAppLaunches(shipped, platform, flow, failures);

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
            "install_name still resolves inside the rewritten bundle, every framework it links is embedded beside it " +
            "or OS-resident, its signature verifies, {Package}and the app launches and loads each embedded framework " +
            "through the rewritten layout.", platform, flow,
            publish ? "the same holds for the app inside the installer package this publish produced, " : string.Empty);
    }

    // The built app, run with its smoke argument: it dlopens every framework under
    // Contents/Frameworks by the path dyld resolves through the root link, constructs a Swift
    // string through the runtime framework, and reports how many frameworks it loaded. An exit
    // code or a count that disagrees with the bundle is a layout dyld could not follow, a
    // signature the loader refused, or a dependency it could not find — the failures the static
    // checks approximate and this one observes.
    static void AssertMacAppLaunches(AbsolutePath appBundle, string platform, string flow, List<string> failures)
    {
        var executable = appBundle / "Contents" / "MacOS" / MacAnatomyAppName;
        if (!File.Exists(executable))
        {
            failures.Add($"{platform} {flow}: {appBundle.Name} has no Contents/MacOS/{MacAnatomyAppName} to launch.");
            return;
        }

        var embedded = Directory.GetDirectories(appBundle / "Contents" / "Frameworks", "*.framework").Length;
        var run = ProcessTasks.StartProcess(
            executable,
            MacAnatomySmokeArgument,
            timeout: 120_000,
            logOutput: false);
        run.WaitForExit();
        var output = string.Join("\n", run.Output.Select(o => o.Text));

        var expected = $"{MacAnatomySmokeOk}{embedded}";
        if (run.ExitCode != 0 || !output.Contains(expected, StringComparison.Ordinal))
            failures.Add(
                $"{platform} {flow}: launching {appBundle.Name} {MacAnatomySmokeArgument} exited {run.ExitCode} without " +
                $"reporting '{expected}' — the app could not load every embedded framework through the rewritten " +
                $"layout. Output:\n{output}");
    }

    const string MacAnatomySmokeArgument = "--anatomy-smoke";
    const string MacAnatomySmokeOk = "anatomy-smoke-ok: frameworks loaded = ";

    // The installer package the publish flow produced, expanded with its payload restored to disk,
    // and the app inside it put through the same layout assertions as the bundle in bin/. The
    // package is what carries the app to Apple; asserting the bundle alone would leave the archive
    // step — which has to preserve symbolic links and the version directory — unobserved.
    void AssertMacInstallerPackage(AbsolutePath appDir, string platform, string flow, List<string> failures)
    {
        var packages = Directory
            .GetFiles(appDir / "bin", "*.pkg", SearchOption.AllDirectories)
            .OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal)
            .ToList();
        if (packages.Count == 0)
        {
            failures.Add(
                $"{platform} {flow}: no .pkg under {appDir / "bin"} — the workload builds one for a Mac publish by " +
                "default, so this leg would stop observing the artifact that is actually submitted.");
            return;
        }

        var expanded = appDir / ("pkg-expanded-" + flow.Replace(' ', '-'));
        if (Directory.Exists(expanded)) expanded.DeleteDirectory();

        // --expand-full restores the payload itself (symbolic links and all), which --expand does not.
        var expand = ProcessTasks.StartProcess(
            "pkgutil", $"--expand-full \"{packages[0]}\" \"{expanded}\"", logOutput: false);
        expand.WaitForExit();
        if (expand.ExitCode != 0)
        {
            failures.Add(
                $"{platform} {flow}: pkgutil could not expand {Path.GetFileName(packages[0])} (exit {expand.ExitCode}): " +
                string.Join("\n", expand.Output.Select(o => o.Text)));
            return;
        }

        var payloadApps = Directory
            .GetDirectories(expanded, $"{MacAnatomyAppName}.app", SearchOption.AllDirectories)
            .OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal)
            .ToList();
        if (payloadApps.Count == 0)
        {
            failures.Add(
                $"{platform} {flow}: {Path.GetFileName(packages[0])} carries no {MacAnatomyAppName}.app payload.");
            return;
        }

        AssertMacAppFrameworkAnatomy((AbsolutePath)payloadApps[0], platform, flow + " installer package", failures);
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

        // Positive control for the directory-entry case: the fixture embeds SBApple, whose bundle
        // carries a Modules/ directory. A rewritten bundle's root Modules is a link to a directory,
        // which is the entry a second workload copy cannot write onto; if SBApple stopped embedding,
        // the re-copy flow would pass without ever putting that entry in ditto's way.
        if (!frameworks.Any(f => Path.GetFileName(f) == MacAnatomyDirectoryEntryFramework + ".framework"))
            failures.Add(
                $"{platform} {flow}: {MacAnatomyDirectoryEntryFramework}.framework did not embed, so no embedded " +
                "framework carries a directory entry (Modules/) and the re-copy flow observes nothing.");
        else if (!Directory.Exists(Path.Combine(frameworksDir, MacAnatomyDirectoryEntryFramework + ".framework", "Modules")))
            failures.Add(
                $"{platform} {flow}: {MacAnatomyDirectoryEntryFramework}.framework/Modules does not resolve to a " +
                "directory — the fixture's directory-entry positive control is gone.");

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
            {
                AssertMacFrameworkLoadPath(fw, name, executable, where, failures);
                AssertMacFrameworkDependencies(frameworksDir, versionedExecutable, where, failures);
            }

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

    // Every dylib the framework binary names as a load-time dependency has to be something dyld
    // will find in the shipped app: another framework embedded beside it (reached through the root
    // link the rewrite created), or an OS-resident library. Two shapes are defects in a Mac App
    // Store bundle whatever the layout: a Swift standard-library dylib anywhere other than
    // /usr/lib/swift (a stable-ABI app links the OS copy, and a bundled one is a loose dylib), and a
    // relative @rpath/@loader_path/@executable_path dependency with nothing under Contents/Frameworks
    // to satisfy it. Absolute OS paths are taken on trust — on current macOS they live in the dyld
    // shared cache rather than on disk, so their existence cannot be checked from the file system.
    static void AssertMacFrameworkDependencies(AbsolutePath frameworksDir, string binary, string where, List<string> failures)
    {
        var dependencies = MachOReader.ReadLinkedDylibs(binary);
        if (dependencies is null)
        {
            failures.Add($"{where}: could not read the dependency load commands of {binary}.");
            return;
        }

        foreach (var dep in dependencies)
        {
            var leaf = dep.Substring(dep.LastIndexOf('/') + 1);
            if (leaf.StartsWith("libswift", StringComparison.Ordinal) && !dep.StartsWith("/usr/lib/swift/", StringComparison.Ordinal))
            {
                failures.Add(
                    $"{where} links the Swift standard library from '{dep}' rather than /usr/lib/swift — a bundled " +
                    "Swift runtime is a loose dylib in the app and not what a stable-ABI app should link.");
                continue;
            }

            if (dep.StartsWith("/", StringComparison.Ordinal))
                continue;

            // @rpath/Foo.framework/Foo, @loader_path/../Bar.framework/Bar, @executable_path/../Frameworks/…:
            // everything after the first path component is resolved against Contents/Frameworks, the
            // only place the app carries frameworks and the directory the workload puts on the rpath.
            var slash = dep.IndexOf('/');
            var relative = slash < 0 ? dep : dep.Substring(slash + 1);
            if (relative.StartsWith("../Frameworks/", StringComparison.Ordinal)) relative = relative.Substring("../Frameworks/".Length);
            if (relative.StartsWith("Frameworks/", StringComparison.Ordinal)) relative = relative.Substring("Frameworks/".Length);

            var resolved = Path.Combine(frameworksDir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(resolved))
                failures.Add(
                    $"{where} depends on '{dep}', which does not resolve to a file under Contents/Frameworks " +
                    $"({resolved}) — dyld would fail to load the framework in the shipped app.");
        }
    }

    // A minimal app whose only package reference is the runtime, so the anatomy step reaches it the
    // way a consumer's does: through the package's own buildTransitive targets. It also embeds the
    // repo's SBApple xcframework directly, for a framework with a directory entry (see
    // MacAnatomyDirectoryEntryFramework).
    static void WriteMacAnatomyConsumerApp(AbsolutePath appDir, string tfm, string rid, AbsolutePath directoryEntryXcframework)
    {
        Assert.True(Directory.Exists(directoryEntryXcframework),
            $"{directoryEntryXcframework} is missing — the Mac anatomy fixture embeds it as its directory-entry positive control.");
        var isCatalyst = tfm.Contains("maccatalyst", StringComparison.Ordinal);

        // A macOS Release build otherwise insists on trimming; this gate inspects bundle structure,
        // and trimming only slows that down.
        var linkMode = isCatalyst ? "" : "\n    <LinkMode>None</LinkMode>";

        // A single RID is passed on the command line; a universal app names its RIDs here instead.
        var runtimeIdentifiers = rid == MacAnatomyUniversalRids
            ? $"<RuntimeIdentifiers>{rid}</RuntimeIdentifiers>"
            : $"<RuntimeIdentifier>{rid}</RuntimeIdentifier>";

        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{tfm}</TargetFramework>
                {runtimeIdentifiers}
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

              <!-- The package reference under test, plus a framework carrying a Modules/ directory. -->
              <ItemGroup>
                <PackageReference Include="SwiftBindings.Runtime" Version="{AppStoreHygieneVersion}" />
                <NativeReference Include="{directoryEntryXcframework}" Kind="Framework" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(appDir / $"{MacAnatomyAppName}.csproj", csproj);

        // A real app, so the workload assembles and signs a bundle, that touches the runtime
        // assembly so the framework is genuinely pulled in. Launched by the gate with the smoke
        // argument, it never brings up UI: it dlopens every embedded framework by the path dyld
        // resolves through the rewritten layout, constructs a Swift string through the runtime
        // framework, and reports the count for the gate to compare against the bundle.
        var smoke = $$"""
              // The smoke entry the gate launches. Loading by the root-link path is what dyld does
              // for an @rpath/<Name>.framework/<Name> install_name, so a link that does not resolve,
              // a signature the loader refuses, or a dependency it cannot find all surface here.
              static int Smoke()
              {
                  var frameworks = System.IO.Path.Combine(NSBundle.MainBundle.BundlePath, "Contents", "Frameworks");
                  var loaded = 0;
                  foreach (var dir in System.IO.Directory.GetDirectories(frameworks, "*.framework"))
                  {
                      var name = System.IO.Path.GetFileNameWithoutExtension(dir);
                      try
                      {
                          System.Runtime.InteropServices.NativeLibrary.Load(System.IO.Path.Combine(dir, name));
                          loaded++;
                      }
                      catch (Exception e)
                      {
                          Console.Error.WriteLine($"anatomy-smoke: failed to load {name}: {e.Message}");
                          return 2;
                      }
                  }
                  var probe = new Swift.SwiftString("anatomy");
                  GC.KeepAlive(probe);
                  Console.WriteLine("{{MacAnatomySmokeOk}}" + loaded);
                  return 0;
              }
          """;

        var program = isCatalyst
            ? $$"""
              // Copyright (c) 2026 Justin Wojciechowski.
              // Licensed under the MIT License.
              using Foundation;
              using UIKit;

              namespace MacAnatomyApp;

              public static class Application
              {
                  static int Main(string[] args)
                  {
                      if (args.Length > 0 && args[0] == "{{MacAnatomySmokeArgument}}") return Smoke();
                      GC.KeepAlive(typeof(Swift.SwiftString));
                      UIApplication.Main(args, null, typeof(AppDelegate));
                      return 0;
                  }

              {{smoke}}
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
            : $$"""
              // Copyright (c) 2026 Justin Wojciechowski.
              // Licensed under the MIT License.
              using AppKit;
              using Foundation;

              namespace MacAnatomyApp;

              public static class Application
              {
                  static int Main(string[] args)
                  {
                      if (args.Length > 0 && args[0] == "{{MacAnatomySmokeArgument}}") return Smoke();
                      GC.KeepAlive(typeof(Swift.SwiftString));
                      NSApplication.Init();
                      NSApplication.Main(args);
                      return 0;
                  }

              {{smoke}}
              }
              """;
        File.WriteAllText(appDir / "Program.cs", program);
    }
}
