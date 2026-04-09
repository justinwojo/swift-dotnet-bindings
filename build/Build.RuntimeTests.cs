// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.RuntimeTests.cs — simulator/device/macOS test execution
//
// DESIGN DECISION: Skip modes vs target dependencies
//
// Problem: --skip-regen means "don't rebuild bindings, just build app + run" (~17s).
// --skip-build means "don't even rebuild the .NET app, just install + run" (~5s).
// If RuntimeTestsSimulator unconditionally DependsOn(BindingTests), Nuke runs
// the full pipeline before the target body even executes — the skip flags can't work.
//
// Solution: The runtime test targets do NOT depend on BindingTests. Instead:
//   - Default behavior (no skip flags): the target body calls the binding pipeline
//     methods directly, then builds the app, then runs tests.
//   - --skip-regen: skips binding pipeline, just builds app + runs tests.
//   - --skip-build: skips everything, just installs + runs.
//   - Staleness detection: if --skip-regen but Swift sources are newer than bindings,
//     refuse to run (prevents confusing stale-binding failures).
//
// This matches run-runtime-tests.sh which is a self-contained script that
// conditionally calls build-and-test.sh, not a dependency chain.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    [Parameter("Skip all builds, just install + run")] readonly bool SkipBuild;
    [Parameter("Pre-booted simulator or device UDID")] readonly string? DeviceUdid;

    // Opt-in to the StoreKit 2 Apple-framework smoke tests. Off by default so
    // the Apple-framework path never runs silently — the smoke test exercises
    // a code path that is intentionally not part of the default validation gate.
    // When enabled, the StoreKit snapshot under BindingTests/obj/StoreKit2Snapshot/
    // is regenerated (or skipped if fresh) before the app build. See
    // RegenerateStoreKit2Snapshot() below for the pipeline.
    [Parameter("Opt in to the StoreKit 2 smoke tests (regenerates BindingTests/obj/StoreKit2Snapshot/ in-tree)")]
    readonly bool EnableStoreKitSmoke;

    // --skip-build implies --skip-regen (matches run-runtime-tests.sh line 56-59).
    bool EffectiveSkipRegen => SkipRegen || SkipBuild;

    // macOS uses a separate output directory for its bindings
    AbsolutePath BtMacOSOutputDir => BindingTestsDir / "output-macos";

    const string RuntimeTestsBundleId = "com.swiftbindings.runtimetestsapp";

    // ------------------------------------------------------------------
    // StoreKit 2 snapshot — in-tree, first-class path.
    //
    // Lives under BindingTests/obj/StoreKit2Snapshot/ which is git-ignored
    // via the top-level `[Oo]bj/` rule, so nothing gets committed. Regenerated
    // by the `RegenerateStoreKit2Snapshot()` helper below (exposed as the
    // `nuke regenerate-storekit-snapshot` target, and called automatically
    // as a prerequisite of `runtime-tests-simulator --enable-storekit-smoke`).
    //
    // Why in-tree: the previous out-of-repo path consumed the snapshot DLL via
    // a raw `<Reference HintPath=...>` pointing at /tmp/storekit2-session4,
    // which MSBuild cannot invalidate when Swift.Runtime.dll changes —
    // producing the "stale-AOT footgun" that SIGABRTs during
    // xamarin_bridge_initialize. With the snapshot in-tree, RuntimeTestsApp.csproj
    // uses a real ProjectReference + conditional Import of the generator-emitted
    // `.ProjectReference.targets`, and the whole incremental-build graph stays
    // coherent across Swift.Runtime rebuilds.
    // ------------------------------------------------------------------
    AbsolutePath StoreKitSnapshotDir => BindingTestsDir / "obj" / "StoreKit2Snapshot";
    AbsolutePath StoreKitSnapshotAbiJson => StoreKitSnapshotDir / "StoreKit.abi.json";
    AbsolutePath StoreKitSnapshotCsproj => StoreKitSnapshotDir / "StoreKit.Swift.iOS.csproj";
    AbsolutePath StoreKitSnapshotProjectRefTargets =>
        StoreKitSnapshotDir / "StoreKit.Swift.iOS.ProjectReference.targets";
    // Persistent fingerprint of (generator + runtime sources + snapshot tooling)
    // written at the end of every successful regen and compared during the
    // freshness check. Without this stamp, IsStoreKitSnapshotFresh would only
    // notice changes to the Xcode SDK inputs — a generator edit, a constant
    // tweak inside Build.RuntimeTests.cs, or a change to the inline
    // Directory.Build.targets template would all leave the snapshot stale.
    AbsolutePath StoreKitSnapshotFingerprintStamp =>
        StoreKitSnapshotDir / ".snapshot-fingerprint";

    // Swift-side hardcodes for the digester dump. The target triple needs to
    // match what the generator will resolve via --platform-target simulator
    // (arm64-apple-ios-simulator slice under the iphonesimulator SDK). The
    // minimum deployment version (15.0) matches what Session 4 used during
    // the original reproducer and what StoreKit 2's API surface requires.
    const string StoreKitDigesterModule = "StoreKit";
    const string StoreKitDigesterTarget = "arm64-apple-ios15.0-simulator";
    // The backslash is the System.CommandLine escape for the response-file
    // prefix `@`. Without it, System.CommandLine interprets `@rpath/...` as a
    // path to a response file (and emits "Error reading response file"). The
    // backslash is stripped by the parser before the value reaches
    // BindingsGeneratorCommand, so IsSystemFrameworkTarget still sees a plain
    // "@rpath/StoreKit.framework/StoreKit" and enables the csproj + wrapper
    // compilation branch. Documented by the generator's -l help text.
    const string StoreKitSnapshotLibraryNameArg = @"\@rpath/StoreKit.framework/StoreKit";
    const string StoreKitSnapshotSwiftRuntimeVersion = "0.0.0-dev";

    // ============================================================
    // StoreKit 2 snapshot regeneration (in-tree, first-class)
    // ============================================================

    /// <summary>
    /// Exposes <see cref="RegenerateStoreKit2Snapshot"/> as a manually invokable
    /// nuke target so contributors can rebuild the StoreKit 2 snapshot on demand
    /// (e.g. after an Xcode upgrade changes the bundled swiftinterface). The
    /// runtime test targets call the helper directly — they do NOT DependsOn
    /// this target — because the conditional-prerequisite wiring in Step 4 needs
    /// to gate on <c>EnableStoreKitSmoke</c> at runtime, not at target declaration.
    /// </summary>
    Target RegenerateStoreKitSnapshot => _ => _
        .Description("Regenerate the in-tree StoreKit 2 snapshot (BindingTests/obj/StoreKit2Snapshot/) from the active Xcode SDK.")
        .Executes(() => RegenerateStoreKit2Snapshot(force: true));

    /// <summary>
    /// Regenerates the in-tree StoreKit 2 snapshot at
    /// <see cref="StoreKitSnapshotDir"/> by (1) invoking
    /// <c>xcrun swift-api-digester</c> against the active iphonesimulator SDK
    /// to produce a fresh ABI JSON dump, then (2) running the generator in
    /// manual mode (<c>-a / -d / -t / -s / -l</c>) to emit the C# bindings,
    /// Swift wrapper source, and project files. The output includes
    /// <see cref="StoreKitSnapshotProjectRefTargets"/>, which
    /// <c>RuntimeTestsApp.csproj</c> imports (in Step 3) to wire the
    /// generator-emitted NativeReference through the ProjectReference graph.
    /// </summary>
    /// <param name="force">
    /// When <c>true</c>, always regenerate regardless of output staleness.
    /// Used by the on-demand <see cref="RegenerateStoreKitSnapshot"/> target.
    /// When <c>false</c>, skip regeneration if every output file exists and
    /// every one is at least as new as the Xcode SDK input files — this makes
    /// repeat smoke-enabled runs effectively free.
    /// </param>
    /// <remarks>
    /// Failure mode for a missing Xcode install: the initial <c>xcrun --sdk
    /// iphonesimulator --show-sdk-path</c> call returns a non-zero exit code
    /// or an empty path, and the helper throws a loud exception pointing the
    /// user at Xcode installation. The generator invocation itself also
    /// throws on any non-zero exit — this is NOT the same permissive
    /// "generator may exit non-zero for unsupported features" path used by
    /// <c>RunRegenerateBindings</c>, because Apple SDK targets must always
    /// succeed cleanly for the smoke test to have any meaning.
    /// </remarks>
    void RegenerateStoreKit2Snapshot(bool force)
    {
        Log.Information("--- Regenerating StoreKit 2 snapshot ---");
        Log.Information("    Output: {Dir}", StoreKitSnapshotDir);

        // Step A: resolve the iphonesimulator SDK root via xcrun. We shell out
        // instead of hardcoding /Applications/Xcode.app because a contributor
        // may have xcode-select pointed at a non-default Xcode install.
        var sdkPath = RunXcrunCapture("--sdk iphonesimulator --show-sdk-path");
        if (string.IsNullOrWhiteSpace(sdkPath))
        {
            throw new Exception(
                "xcrun --sdk iphonesimulator --show-sdk-path returned an empty path. " +
                "Is Xcode installed and selected via `xcode-select --switch`?");
        }

        var swiftinterfacePath = (AbsolutePath)sdkPath /
            "System" / "Library" / "Frameworks" / "StoreKit.framework" /
            "Modules" / "StoreKit.swiftmodule" /
            "arm64-apple-ios-simulator.swiftinterface";
        var tbdPath = (AbsolutePath)sdkPath /
            "System" / "Library" / "Frameworks" / "StoreKit.framework" / "StoreKit.tbd";

        if (!File.Exists(swiftinterfacePath))
        {
            throw new Exception(
                $"StoreKit swiftinterface not found at {swiftinterfacePath}. " +
                $"SDK root was {sdkPath} (from xcrun). Check your Xcode install.");
        }
        if (!File.Exists(tbdPath))
        {
            throw new Exception(
                $"StoreKit.tbd not found at {tbdPath}. SDK root was {sdkPath}.");
        }

        // Step B: incremental skip check. Both the ABI JSON (digester output)
        // and the generator outputs must exist AND be at least as new as the
        // Xcode SDK inputs. If any input is newer than any output, regenerate
        // everything — we don't bother trying to skip just one of the two
        // phases because the cost of a full regen is ~10s and the logic of
        // partial staleness is far more error-prone than the savings.
        if (!force && IsStoreKitSnapshotFresh(swiftinterfacePath, tbdPath))
        {
            Log.Information("    Snapshot is up to date relative to Xcode SDK inputs — skipping regeneration");
            return;
        }

        // Ensure a clean output directory. We deliberately wipe and recreate
        // rather than merging into existing output: the generator writes a
        // mix of .cs, .swift, .csproj, .targets, and .xcframework artifacts,
        // and a stale file from a previous generator version (e.g. a file the
        // generator no longer emits) would silently survive an in-place regen.
        if (Directory.Exists(StoreKitSnapshotDir))
            ((AbsolutePath)StoreKitSnapshotDir).DeleteDirectory();
        StoreKitSnapshotDir.CreateDirectory();

        // Step C: dump the StoreKit ABI via swift-api-digester. The -dump-sdk
        // command emits a JSON description of the module's public API surface
        // that the generator consumes as its -a input in manual mode. This is
        // the same command Session 4 used for the original reproducer, now
        // owned by this target instead of a one-shot shell script.
        Log.Information("    Dumping StoreKit ABI via swift-api-digester");
        var digesterArgs = string.Join(" ", new[]
        {
            "swift-api-digester",
            "-dump-sdk",
            "-module", StoreKitDigesterModule,
            "-target", StoreKitDigesterTarget,
            "-sdk", $"\"{sdkPath}\"",
            "-o", $"\"{StoreKitSnapshotAbiJson}\"",
        });
        var digesterProc = ProcessTasks.StartProcess(
            "xcrun", digesterArgs,
            workingDirectory: StoreKitSnapshotDir,
            logOutput: true);
        digesterProc.WaitForExit();
        if (digesterProc.ExitCode != 0)
        {
            throw new Exception(
                $"swift-api-digester exited with code {digesterProc.ExitCode}. " +
                $"Arguments: {digesterArgs}");
        }
        if (!File.Exists(StoreKitSnapshotAbiJson))
        {
            throw new Exception(
                $"swift-api-digester exited successfully but did not produce " +
                $"{StoreKitSnapshotAbiJson}.");
        }

        var abiJsonSize = new FileInfo(StoreKitSnapshotAbiJson).Length;
        Log.Information("    ABI JSON: {Path} ({Size:N0} bytes)", StoreKitSnapshotAbiJson, abiJsonSize);

        // Step D: run the generator in manual mode against the digester dump.
        // The generator is shared with BindingTests, so EnsureGeneratorBuilt()
        // is a no-op when it's already compiled.
        EnsureGeneratorBuilt();

        Log.Information("    Running generator (manual mode, -a/-d/-t/-s)");
        var genArgs = string.Join(" ", new[]
        {
            $"\"{GeneratorDll}\"",
            $"-a \"{StoreKitSnapshotAbiJson}\"",
            $"-d \"{tbdPath}\"",
            $"-t \"{tbdPath}\"",
            $"-s \"{swiftinterfacePath}\"",
            $"-l \"{StoreKitSnapshotLibraryNameArg}\"",
            "--platform ios",
            "--platform-target simulator",
            $"--swift-runtime-version {StoreKitSnapshotSwiftRuntimeVersion}",
            $"-o \"{StoreKitSnapshotDir}\"",
        });
        var genProc = ProcessTasks.StartProcess(
            "dotnet", genArgs,
            workingDirectory: StoreKitSnapshotDir,
            logOutput: true);
        genProc.WaitForExit();
        if (genProc.ExitCode != 0)
        {
            throw new Exception(
                $"Generator exited with code {genProc.ExitCode} regenerating the StoreKit snapshot. " +
                $"Unlike BindingTests, Apple-framework targets must always generate cleanly — " +
                $"investigate the generator output above.");
        }

        // Step E (prep): drop a snapshot-local Directory.Build.targets that
        // adds CA1416 to NoWarn after the csproj body runs. The repo root
        // ships <TreatWarningsAsErrors>True</TreatWarningsAsErrors>, so any
        // Apple-SDK API whose availability is gated on a newer min-OS version
        // (e.g. StoreKit.Transaction.RefundRequestStatus, tvOS 17+) converts
        // the expected CA1416 warnings into 235+ hard build errors.
        //
        // Why Directory.Build.targets and NOT Directory.Build.props: the
        // generator-emitted csproj sets an explicit <NoWarn>CS0169;CA1420</NoWarn>
        // in its PropertyGroup. Directory.Build.props is imported BEFORE the
        // project body, so any NoWarn set there is overwritten by the csproj's
        // literal assignment. Directory.Build.targets imports AFTER the body,
        // so the $(NoWarn) expansion picks up the csproj value and the append
        // survives.
        //
        // Why not patch the generator: CA1416 is NOT a generator-wide problem.
        // Non-snapshot consumers want to see the warning, decide whether to
        // raise their deployment target, and make an informed call. This
        // suppression is specific to the snapshot's full-API-surface binding
        // strategy combined with the smoke tests' runtime-safe entry points.
        //
        // Regenerated on every run because the regen step wipes the directory
        // first. Do NOT hand-edit the emitted file.
        // Disable TreatWarningsAsErrors for the snapshot csproj. The repo-wide
        // Directory.Build.props (/Directory.Build.props, root) turns warnings
        // into errors, which is the right default for first-party source but
        // the wrong default for a reflection of Apple's full public StoreKit
        // API surface. Binding every public entry point legitimately surfaces
        // CA1416 (platform availability — Transaction.RefundRequestStatus is
        // tvOS 17+ only), CA1422 (obsoleted APIs — PromotionalOffer is
        // replaced in iOS 26 by JWS), CS0436 (AppStore collides with
        // Microsoft.iOS's StoreKit ObjC binding; consumers resolve via
        // `extern alias StoreKitSwift`), CS8604 (nullable-annotation noise in
        // the async completion bridges), and a long tail of similar codes.
        // None of those block the smoke tests — the tests only dereference
        // runtime-safe entry points — but under warnings-as-errors they all
        // become hard failures and turn the snapshot build into a whack-a-mole
        // session of NoWarn codes. Keeping this OFF is the right scope: the
        // snapshot is test scaffolding, not a shipping package, so "any
        // compile error ≠ warning" is the right gate.
        //
        // Why Directory.Build.targets and NOT Directory.Build.props: the
        // generator-emitted csproj sets explicit PropertyGroup values in its
        // body. Directory.Build.props imports BEFORE the project body, so
        // overrides there are clobbered by the csproj's literal assignments.
        // Directory.Build.targets imports AFTER the body, so assignments here
        // take effect AT the time MSBuild starts invoking targets (post-
        // PropertyGroup evaluation). This is exactly the override window we
        // need for TreatWarningsAsErrors.
        //
        // Regenerated on every run because the regen step wipes the directory
        // first. Do NOT hand-edit the emitted file.
        File.WriteAllText(StoreKitSnapshotDir / "Directory.Build.targets",
            """
            <Project>
              <PropertyGroup>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <MSBuildTreatWarningsAsErrors>false</MSBuildTreatWarningsAsErrors>
              </PropertyGroup>
            </Project>
            """);

        // Step E: verify the key output files exist. These are the files that
        // Step 3's RuntimeTestsApp.csproj rewrite will reference, so if the
        // generator silently produced a partial output (e.g. dropped the
        // .ProjectReference.targets file because of a regression) we want to
        // know now, not at app-build time with a confusing MSBuild error.
        var requiredOutputs = new (string Label, AbsolutePath Path)[]
        {
            ("C# bindings", StoreKitSnapshotDir / "StoreKit.cs"),
            ("generated csproj", StoreKitSnapshotCsproj),
            ("ProjectReference.targets", StoreKitSnapshotProjectRefTargets),
            ("Swift wrapper source", StoreKitSnapshotDir / "StoreKit.Wrapper.swift"),
        };
        foreach (var (label, path) in requiredOutputs)
        {
            if (!File.Exists(path))
            {
                throw new Exception(
                    $"Generator succeeded but {label} is missing: {path}. " +
                    $"This is a generator regression — the snapshot cannot be consumed as-is.");
            }
        }

        // Step F: write the freshness stamp. This is the LAST step so a partial
        // failure (e.g. generator throws mid-emit) leaves no stamp behind and
        // the next run will see the snapshot as stale and re-regenerate.
        File.WriteAllText(StoreKitSnapshotFingerprintStamp, ComputeStoreKitSnapshotFingerprint());

        Log.Information("    StoreKit 2 snapshot regenerated: {Dir}", StoreKitSnapshotDir);
    }

    /// <summary>
    /// Returns <c>true</c> when every required StoreKit snapshot output file
    /// exists, the recorded snapshot fingerprint matches the current
    /// (source + tooling) fingerprint, AND every output file is at least as
    /// new as every Xcode SDK input file. Any missing output, fingerprint
    /// drift, or newer input means "regenerate."
    /// </summary>
    /// <remarks>
    /// The mtime check by itself is not sufficient. A generator edit, a tweak
    /// to one of the snapshot constants in this file, or a change to the
    /// inline <c>Directory.Build.targets</c> template would all leave the
    /// snapshot files older than the SDK inputs — and the freshness check
    /// would happily reuse the stale generated StoreKit bindings against the
    /// new generator. The fingerprint stamp closes that hole by hashing the
    /// generator + runtime source tree (via <see cref="ComputeSourceFingerprint"/>)
    /// AND the snapshot tooling source itself (<c>Build.RuntimeTests.cs</c>),
    /// so any of those changes invalidates the snapshot.
    /// </remarks>
    bool IsStoreKitSnapshotFresh(AbsolutePath swiftinterfacePath, AbsolutePath tbdPath)
    {
        var requiredOutputs = new[]
        {
            StoreKitSnapshotAbiJson,
            StoreKitSnapshotCsproj,
            StoreKitSnapshotProjectRefTargets,
            StoreKitSnapshotDir / "StoreKit.cs",
            StoreKitSnapshotDir / "StoreKit.Wrapper.swift",
        };

        foreach (var outputPath in requiredOutputs)
        {
            if (!File.Exists(outputPath))
                return false;
        }

        // Fingerprint gate: the stamp must exist AND match the current
        // generator/runtime/tooling fingerprint. A missing stamp indicates a
        // partially-completed previous regen; a mismatched stamp indicates a
        // generator or tooling change since the snapshot was last written.
        if (!File.Exists(StoreKitSnapshotFingerprintStamp))
            return false;
        var recordedFingerprint = File.ReadAllText(StoreKitSnapshotFingerprintStamp).Trim();
        var currentFingerprint = ComputeStoreKitSnapshotFingerprint();
        if (recordedFingerprint != currentFingerprint)
            return false;

        var oldestOutputUtc = requiredOutputs.Min(p => File.GetLastWriteTimeUtc(p));
        var newestInputUtc = new[]
        {
            File.GetLastWriteTimeUtc(swiftinterfacePath),
            File.GetLastWriteTimeUtc(tbdPath),
        }.Max();

        return oldestOutputUtc >= newestInputUtc;
    }

    /// <summary>
    /// Combines <see cref="ComputeSourceFingerprint"/> (generator + runtime
    /// source tree) with a SHA256 hash of <c>Build.RuntimeTests.cs</c> itself.
    /// The latter covers the snapshot tooling: the digester args, the generator
    /// CLI flags, the inline <c>Directory.Build.targets</c> template, and the
    /// snapshot path constants — anything that, if changed, should force a
    /// regen of the in-tree StoreKit 2 snapshot.
    /// </summary>
    string ComputeStoreKitSnapshotFingerprint()
    {
        var sourceFingerprint = ComputeSourceFingerprint();
        var toolingFile = RootDirectory / "build" / "Build.RuntimeTests.cs";
        var toolingBytes = File.Exists(toolingFile)
            ? File.ReadAllBytes(toolingFile)
            : Array.Empty<byte>();
        var toolingHash = Convert.ToHexString(SHA256.HashData(toolingBytes)).ToLowerInvariant();
        return $"{sourceFingerprint}.{toolingHash}";
    }

    /// <summary>
    /// Invokes <c>xcrun</c> with <paramref name="args"/> and returns stdout
    /// trimmed of trailing whitespace. Used for SDK path resolution. Throws
    /// with a message suggesting an Xcode install check if xcrun exits
    /// non-zero.
    /// </summary>
    static string RunXcrunCapture(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "xcrun",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)
            ?? throw new Exception($"Failed to start xcrun {args}");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new Exception(
                $"xcrun {args} exited with code {proc.ExitCode}. " +
                $"stderr: {stderr.Trim()}. Is Xcode installed?");
        }
        return stdout.Trim();
    }

    // ============================================================
    // RuntimeTestsSimulator — NO DependsOn, manages pipeline internally
    // ============================================================

    Target RuntimeTestsSimulator => _ => _
        .After(Clean, BindingTestsStrict)
        .Executes(() =>
        {
            Log.Information("=========================================");
            Log.Information(" BindingTests Runtime Tests (Simulator)");
            Log.Information("=========================================");
            Log.Information("Skip regeneration: {SkipRegen}", EffectiveSkipRegen);
            Log.Information("Skip build: {SkipBuild}", SkipBuild);
            Log.Information("Timeout: {Timeout}s", Timeout);
            if (!string.IsNullOrEmpty(ClassFilter))
                Log.Information("Class filter: {ClassFilter}", ClassFilter);
            if (FlakeDetect)
                Log.Information("Flake detection: enabled");

            // Reject --skip-build + --enable-storekit-smoke up front. The
            // snapshot regeneration runs inside the !SkipBuild branch, and the
            // app bundle's copy of StoreKit.Swift.iOS.dll is only refreshed as
            // part of the app build. Silently honoring --skip-build with smoke
            // enabled would run the previous session's (possibly stale) snapshot
            // against the current in-tree Swift.Runtime — the exact stale-AOT
            // footgun we set out to fix. Fail loudly instead so the user can
            // either drop --skip-build (accept the rebuild cost) or drop
            // --enable-storekit-smoke (skip the StoreKit smoke tests for this
            // iteration). This is the ONLY flag combination we refuse — every
            // other combination is either a normal full run or a default
            // non-smoke run, both of which are safe.
            if (SkipBuild && EnableStoreKitSmoke)
            {
                throw new Exception(
                    "--skip-build and --enable-storekit-smoke are mutually incompatible: " +
                    "the StoreKit 2 snapshot is regenerated and consumed as part of the app " +
                    "build, so skipping the build would leave the previous app bundle's " +
                    "StoreKit.Swift.iOS.dll pinned to whatever Swift.Runtime version built it. " +
                    "That is the stale-AOT footgun documented in src/docs/0.8.0-storekit2-exploration.md. " +
                    "Drop one of the two flags and rerun.");
            }

            // Step 1: Conditionally run binding pipeline
            if (!EffectiveSkipRegen)
            {
                RunBuildXcframework();
                RunRegenerateBindings(strict: false);
                RunCompileCheck();
                RunBuildAsyncWrapper();
                RunBuildBridge();
            }
            else
            {
                AssertBindingsNotStale();
            }

            // Step 2: Build RuntimeTestsApp (unless --skip-build)
            if (!SkipBuild)
            {
                // In-tree StoreKit 2 snapshot regeneration. Runs the generator against
                // the active Xcode SDK and drops the output at
                // BindingTests/obj/StoreKit2Snapshot/, which RuntimeTestsApp.csproj
                // references via ProjectReference + conditional Import of
                // StoreKit.Swift.iOS.ProjectReference.targets. Incremental: skips the
                // regen when inputs haven't changed (force: false). Called
                // unconditionally here — NOT as a DependsOn — because we need to gate
                // on EnableStoreKitSmoke at runtime, and DependsOn is a static graph
                // edge that would run on every target invocation.
                if (EnableStoreKitSmoke)
                    RegenerateStoreKit2Snapshot(force: false);

                Log.Information("--- Building RuntimeTestsApp ---");
                if (EnableStoreKitSmoke)
                    Log.Information("    StoreKit 2 smoke tests: ENABLED (--enable-storekit-smoke)");
                DotNetBuild(s =>
                {
                    var built = s
                        .SetProjectFile(BindingTestsDir / "RuntimeTestsApp")
                        .SetConfiguration("Debug")
                        .SetVerbosity(DotNetVerbosity.quiet);
                    if (EnableStoreKitSmoke)
                    {
                        built = built.SetProperty("EnableStoreKitSmoke", "true");
                        // Plumb SwiftBindingsRepoRoot as a global MSBuild property so
                        // the generator-emitted StoreKit.Swift.iOS.csproj resolves its
                        // SwiftBindings.Runtime dependency via the in-tree ProjectReference
                        // fallback (see the snapshot csproj's Condition="'$(SwiftBindingsRepoRoot)' != ''"
                        // ProjectReference line). Without this, the csproj falls through
                        // to the `[0.0.0-dev]` sentinel PackageReference which has no
                        // matching NuGet and fails with NU1102 during the app build.
                        // Scoped to smoke-enabled only: non-smoke builds never consume
                        // the snapshot ProjectReference, so passing this property has
                        // no effect for them but also no downside — we keep it gated to
                        // avoid polluting the default build's property bag.
                        built = built.SetProperty("SwiftBindingsRepoRoot", RootDirectory.ToString());
                        // NOTE: IncludeSwiftBindingsRuntimeNative=false is no longer
                        // forced here as a global property. The snapshot ProjectReference
                        // in RuntimeTestsApp.csproj now sets it via AdditionalProperties,
                        // which becomes the global property bag for the snapshot's build
                        // and cascades through to its transitive Swift.Runtime
                        // ProjectReference — exactly the dedupe path the global -p:
                        // workaround used to fake. The in-csproj form means a raw
                        // `dotnet build RuntimeTestsApp.csproj -p:EnableStoreKitSmoke=true
                        // -p:SwiftBindingsRepoRoot=...` invocation outside of Nuke also
                        // succeeds without needing an extra `-p:IncludeSwiftBindingsRuntimeNative=false`.
                    }
                    return built;
                });

                var appFrameworks = BindingTestsDir / "RuntimeTestsApp" / "bin" / "Debug" /
                    $"{DotNetTfm}-ios" / "iossimulator-arm64" / "RuntimeTestsApp.app" / "Frameworks";

                if (!Directory.Exists(BindingTestsDir / "RuntimeTestsApp" / "bin" / "Debug" /
                    $"{DotNetTfm}-ios" / "iossimulator-arm64" / "RuntimeTestsApp.app"))
                    throw new Exception("Build failed - app bundle not found");

                Log.Information("Build successful.");

                // Inject all 4 native artifacts into app bundle Frameworks/
                InjectRuntimeDylib(appFrameworks);
                InjectAsyncWrapper(appFrameworks);
                InjectDependencyFramework(appFrameworks);
                InjectDependencyWrapper(appFrameworks);
            }
            else
            {
                Log.Information("--- Steps 1-2: Skipped (--skip-build) ---");
            }

            // Step 3: Install + run on simulator
            RunOnSimulator();
        });

    // ============================================================
    // RuntimeTestsDevice — NO DependsOn, manages pipeline internally
    // Device path has its OWN wrapper build step, separate from simulator.
    // ============================================================

    Target RuntimeTestsDevice => _ => _
        .After(Clean, RuntimeTestsSimulator)
        .Executes(() =>
        {
            Log.Information("=========================================");
            Log.Information(" BindingTests Runtime Tests (Device)");
            Log.Information("=========================================");

            // Step 0: Find connected device
            PhysicalDeviceInfo device;
            if (!string.IsNullOrEmpty(DeviceUdid))
            {
                device = new PhysicalDeviceInfo(DeviceUdid, "specified");
                Log.Information("Using specified device: {Udid}", DeviceUdid);
            }
            else
            {
                var found = DeviceCtl.ListDevices().FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "No connected iOS device found. Connect your iPhone and try again, or use --device-udid UDID.");
                device = new PhysicalDeviceInfo(found.Udid, found.Name);
                Log.Information("Device: {Name} ({Udid})", device.Name, device.Udid);
            }

            if (!EffectiveSkipRegen)
            {
                // Device path: build xcframework with device slice
                RunBuildXcframework(includeDeviceOverride: true);
                RunRegenerateBindings(strict: false);
                // Build device-specific wrappers
                RunBuildDeviceWrappers();
                RunBuildBridge(target: "device");
            }
            else
            {
                AssertBindingsNotStale();
                AssertDeviceSliceExists();
            }

            if (!SkipBuild)
            {
                // Publish NativeAOT (takes several minutes)
                // Uses the unified RuntimeTestsApp project with -r ios-arm64 (activates device conditionals)
                Log.Information("--- Publishing RuntimeTestsApp (NativeAOT, ios-arm64) ---");
                Log.Information("This may take several minutes (ILCompiler + code signing)...");
                DotNetPublish(s => s
                    .SetProject(BindingTestsDir / "RuntimeTestsApp")
                    .SetConfiguration("Release")
                    .SetRuntime("ios-arm64")
                    .SetVerbosity(DotNetVerbosity.quiet));
            }

            // Locate app bundle
            var appSearchDir = BindingTestsDir / "RuntimeTestsApp" / "bin";
            var appPath = Directory.GetDirectories(appSearchDir, "RuntimeTestsApp.app",
                    SearchOption.AllDirectories)
                .Where(d => d.Contains("Release") && d.Contains("ios-arm64"))
                .FirstOrDefault()
                ?? throw new Exception("App bundle not found after publish");
            Log.Information("App bundle: {Path}", appPath);

            // Install + run on device
            RunOnDevice(device, appPath);
        });

    // Simple record to avoid depending on DeviceCtl.PhysicalDevice in the target body
    record PhysicalDeviceInfo(string Udid, string Name);

    // ============================================================
    // RuntimeTestsMacOS — NO DependsOn
    // macOS has its own xcframework build and generates macOS-specific bindings.
    // ============================================================

    Target RuntimeTestsMacOS => _ => _
        .After(Clean, RuntimeTestsDevice, BindingTestsStrict)
        .Executes(() =>
        {
            Log.Information("=========================================");
            Log.Information(" BindingTests Runtime Tests (macOS)");
            Log.Information("=========================================");

            if (!EffectiveSkipRegen)
            {
                // Build xcframework for macOS
                RunBuildXcframework(platformOverride: ApplePlatform.MacOS);
                // Generate macOS-specific bindings
                RunRegenerateMacOSBindings();
                // Build async wrappers for macOS
                RunBuildAsyncWrapper(platformOverride: ApplePlatform.MacOS, outputDirOverride: BtMacOSOutputDir);
            }
            else
            {
                AssertBindingsNotStale(BtMacOSOutputDir);
            }

            if (!SkipBuild)
            {
                Log.Information("--- Building RuntimeTestsApp.Mac ---");
                DotNetBuild(s => s
                    .SetProjectFile(BindingTestsDir / "RuntimeTestsApp.Mac")
                    .SetConfiguration("Debug")
                    .SetVerbosity(DotNetVerbosity.quiet));

                var outputBin = BindingTestsDir / "RuntimeTestsApp.Mac" / "bin" / "Debug" /
                    DotNetTfm / "osx-arm64";
                if (!File.Exists(outputBin / "RuntimeTestsApp.Mac"))
                    throw new Exception("Build failed - macOS executable not found");

                Log.Information("Build successful.");

                InjectMacOSNativeLibraries(outputBin);
            }

            // Run natively on macOS (no simulator/device)
            RunOnMacOS();
        });

    // ============================================================
    // Shared Helpers: Simulator Execution
    // ============================================================

    void RunOnSimulator()
    {
        Log.Information("--- Running on iOS Simulator ---");

        var device = !string.IsNullOrEmpty(DeviceUdid)
            ? new SimCtl.SimDevice(DeviceUdid, "pre-booted", "Booted", true, "")
            : SimCtl.EnsureBootedDevice();
        Log.Information("Using simulator: {Name} ({Udid})", device.Name, device.Udid);

        var appPath = BindingTestsDir / "RuntimeTestsApp" / "bin" / "Debug" /
            $"{DotNetTfm}-ios" / "iossimulator-arm64" / "RuntimeTestsApp.app";

        // Load test inventory for crash recovery
        var inventoryPath = BindingTestsDir / "RuntimeTestsApp" / "TestClasses.g.txt";
        var inventory = TestClassInventory.Load(inventoryPath);

        // Compute eligible class set: full inventory or just the filtered class
        var eligibleClasses = inventory.ClassNames.ToHashSet();
        if (!string.IsNullOrEmpty(ClassFilter))
        {
            var match = eligibleClasses.FirstOrDefault(c =>
                c.Equals(ClassFilter, StringComparison.OrdinalIgnoreCase));
            eligibleClasses = match != null ? new HashSet<string> { match } : new HashSet<string>();
        }

        // Resume-on-crash orchestration loop
        const int maxRetries = 5;
        var excludeClasses = new HashSet<string>();
        var aggregated = new JsonlTestResults();
        LaunchResult? lastResult = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (attempt > 0)
                Log.Information("--- Resume-on-crash: attempt {Attempt}/{MaxRetries} (excluding {Count} classes) ---",
                    attempt + 1, maxRetries + 1, excludeClasses.Count);

            var crashLogsBefore = SimCtl.CountCrashLogs("RuntimeTestsApp");

            SimCtl.Install(device.Udid, appPath);

            var args = new List<string> { "--platform", "simulator" };
            if (FlakeDetect) args.AddRange(["--flake-detect"]);
            if (!string.IsNullOrEmpty(ClassFilter)) args.AddRange(["--class", ClassFilter]);
            if (excludeClasses.Count > 0)
                args.AddRange(["--exclude-classes", string.Join(",", excludeClasses)]);

            Log.Information("Launching app (timeout: {Timeout}s)...", Timeout);
            var result = SimCtl.Launch(
                device.Udid, RuntimeTestsBundleId,
                args.ToArray(), TimeSpan.FromSeconds(Timeout));
            lastResult = result;

            // Show output
            Log.Information("");
            Log.Information("=== APP OUTPUT ===");
            Log.Information(result.Output);

            // Crash diagnostics
            HandleCrashDiagnostics(result, device.Udid, crashLogsBefore);

            // Try to retrieve JSONL results from sandbox
            JsonlTestResults? runResults = null;
            var jsonlContent = SimCtl.CopyResultsFromSandbox(device.Udid, RuntimeTestsBundleId);
            if (jsonlContent != null)
            {
                runResults = JsonlTestResults.Parse(jsonlContent);
                Log.Information("JSONL results (run {Run}): {Summary}", attempt + 1, runResults.ToString());

                // Save this run's JSONL to host-side temp file
                var tempPath = $"/tmp/runtime-tests-run-{attempt}.jsonl";
                File.WriteAllText(tempPath, jsonlContent);
            }
            else
            {
                Log.Debug("JSONL retrieval failed for run {Run}", attempt + 1);
            }

            // If app completed normally (success or failure), we're done
            if (result.Result is TestResult.Success or TestResult.Failure)
            {
                if (runResults != null) aggregated.Merge(runResults);
                break;
            }

            // Crash/timeout: attempt recovery
            if (result.Result is TestResult.Crash or TestResult.Timeout or TestResult.LaunchFailure)
            {
                if (runResults == null || runResults.Tests.Count == 0)
                {
                    // JSONL recovery failed — fall back to console output parsing
                    var consoleClasses = JsonlTestResults.ParseClassesFromConsole(result.Output);
                    if (consoleClasses.Count > 0)
                    {
                        Log.Warning("JSONL recovery failed — falling back to console output ({Count} classes found).", consoleClasses.Count);
                        foreach (var cls in consoleClasses)
                            excludeClasses.Add(cls);

                        var remainingAfterConsole = eligibleClasses.Except(excludeClasses).ToList();
                        if (remainingAfterConsole.Count == 0)
                        {
                            Log.Information("All classes either completed or crashed — no more to run.");
                            break;
                        }

                        Log.Information("Remaining classes: {Count}", remainingAfterConsole.Count);

                        if (attempt == maxRetries)
                        {
                            Log.Error("Max retries ({Max}) reached.", maxRetries);
                            break;
                        }

                        continue;
                    }

                    // Neither JSONL nor console output available. Blind-skip the first
                    // remaining class to make progress through the crash-recovery loop.
                    var remainingBlind = eligibleClasses.Except(excludeClasses).OrderBy(c => c).ToList();
                    if (remainingBlind.Count > 0 && attempt < maxRetries)
                    {
                        var suspect = remainingBlind[0];
                        Log.Warning("Blind skip: excluding '{Class}' (first remaining — no output to identify crasher).", suspect);
                        excludeClasses.Add(suspect);
                        continue;
                    }

                    Log.Error("No JSONL results recovered from crashed run — cannot resume.");
                    break;
                }

                // Identify completed and crashing classes
                var crashingClass = runResults.FindCrashingClass();

                // Synthesize CRASHED entries for unfinished methods
                if (crashingClass != null)
                {
                    Log.Warning("Crash detected in class: {Class}", crashingClass);
                    runResults.SynthesizeCrashEntries(crashingClass, inventory);
                    excludeClasses.Add(crashingClass);
                }

                // Add all completed classes to exclude list
                foreach (var cls in runResults.CompletedClasses)
                    excludeClasses.Add(cls);

                aggregated.Merge(runResults);

                // Check if there are remaining classes to run (scoped to eligible set)
                var remaining = eligibleClasses.Except(excludeClasses).ToList();
                if (remaining.Count == 0)
                {
                    Log.Information("All classes either completed or crashed — no more to run.");
                    break;
                }

                Log.Information("Remaining classes: {Count} (completed: {Completed}, crashed: {Crashed})",
                    remaining.Count, runResults.CompletedClasses.Count,
                    crashingClass != null ? 1 : 0);

                if (attempt == maxRetries)
                {
                    Log.Error("Max retries ({Max}) reached. {Remaining} classes not executed.",
                        maxRetries, remaining.Count);
                    break;
                }

                continue;
            }

            // Unknown result — don't retry
            break;
        }

        // Report final aggregated result
        var finalJsonl = aggregated.Tests.Count > 0 ? aggregated : null;
        ReportRuntimeTestResult(lastResult!, "Simulator", finalJsonl);
    }

    // ============================================================
    // Shared Helpers: Device Execution
    // ============================================================

    void RunOnDevice(PhysicalDeviceInfo device, string appPath)
    {
        Log.Information("--- Running on physical device ---");

        // Load test inventory for crash recovery (unified project — same manifest for sim + device)
        var inventoryPath = BindingTestsDir / "RuntimeTestsApp" / "TestClasses.g.txt";
        var inventory = TestClassInventory.Load(inventoryPath);

        // Compute eligible class set: full inventory or just the filtered class
        var eligibleClasses = inventory.ClassNames.ToHashSet();
        if (!string.IsNullOrEmpty(ClassFilter))
        {
            var match = eligibleClasses.FirstOrDefault(c =>
                c.Equals(ClassFilter, StringComparison.OrdinalIgnoreCase));
            eligibleClasses = match != null ? new HashSet<string> { match } : new HashSet<string>();
        }

        // Resume-on-crash orchestration loop
        const int maxRetries = 5;
        var excludeClasses = new HashSet<string>();
        var aggregated = new JsonlTestResults();
        LaunchResult? lastResult = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (attempt > 0)
                Log.Information("--- Resume-on-crash (device): attempt {Attempt}/{MaxRetries} (excluding {Count} classes) ---",
                    attempt + 1, maxRetries + 1, excludeClasses.Count);

            DeviceCtl.Install(device.Udid, appPath);

            var args = new List<string> { "--platform", "device" };
            if (FlakeDetect) args.AddRange(["--flake-detect"]);
            if (!string.IsNullOrEmpty(ClassFilter)) args.AddRange(["--class", ClassFilter]);
            if (excludeClasses.Count > 0)
                args.AddRange(["--exclude-classes", string.Join(",", excludeClasses)]);

            Log.Information("Launching app on device (timeout: {Timeout}s)...", Timeout);
            var result = DeviceCtl.Launch(
                device.Udid, RuntimeTestsBundleId,
                args.ToArray(), TimeSpan.FromSeconds(Timeout));
            lastResult = result;

            Log.Information("");
            Log.Information("=== APP OUTPUT ===");
            Log.Information(result.Output);

            // Try to retrieve JSONL results from device sandbox
            JsonlTestResults? runResults = null;
            var jsonlContent = DeviceCtl.CopyResultsFromSandbox(device.Udid, RuntimeTestsBundleId);
            if (jsonlContent != null)
            {
                runResults = JsonlTestResults.Parse(jsonlContent);
                Log.Information("JSONL results (run {Run}): {Summary}", attempt + 1, runResults.ToString());

                var tempPath = $"/tmp/runtime-tests-device-run-{attempt}.jsonl";
                File.WriteAllText(tempPath, jsonlContent);
            }
            else
            {
                Log.Debug("JSONL retrieval from device failed for run {Run}", attempt + 1);
            }

            // If app completed normally, we're done
            if (result.Result is TestResult.Success or TestResult.Failure)
            {
                if (runResults != null) aggregated.Merge(runResults);
                break;
            }

            // Crash/timeout: attempt recovery
            if (result.Result is TestResult.Crash or TestResult.Timeout or TestResult.LaunchFailure)
            {
                if (runResults == null || runResults.Tests.Count == 0)
                {
                    // JSONL recovery failed — fall back to console output parsing.
                    // Extract class names from [PASS]/[FAIL]/[SKIP] lines to identify
                    // completed classes and the class that was running when the app crashed.
                    var consoleClasses = JsonlTestResults.ParseClassesFromConsole(result.Output);
                    if (consoleClasses.Count > 0)
                    {
                        Log.Warning("JSONL recovery failed — falling back to console output ({Count} classes found).", consoleClasses.Count);
                        foreach (var cls in consoleClasses)
                            excludeClasses.Add(cls);

                        var remainingAfterConsole = eligibleClasses.Except(excludeClasses).ToList();
                        if (remainingAfterConsole.Count == 0)
                        {
                            Log.Information("All classes either completed or crashed — no more to run.");
                            break;
                        }

                        Log.Information("Remaining classes: {Count}", remainingAfterConsole.Count);

                        if (attempt == maxRetries)
                        {
                            Log.Error("Max retries ({Max}) reached on device.", maxRetries);
                            break;
                        }

                        continue;
                    }

                    // Neither JSONL nor console gave us classes. The app likely crashed at
                    // startup before running any tests. Skip the first remaining class
                    // (alphabetically) to make progress — the crash-recovery loop will
                    // keep narrowing down until the crasher is isolated.
                    var remainingBlind = eligibleClasses.Except(excludeClasses).OrderBy(c => c).ToList();
                    if (remainingBlind.Count > 0 && attempt < maxRetries)
                    {
                        var suspect = remainingBlind[0];
                        Log.Warning("Blind skip: excluding '{Class}' (first remaining — no output to identify crasher).", suspect);
                        excludeClasses.Add(suspect);
                        continue;
                    }

                    Log.Error("No JSONL results recovered from crashed device run — cannot resume.");
                    break;
                }

                var crashingClass = runResults.FindCrashingClass();

                if (crashingClass != null)
                {
                    Log.Warning("Crash detected in class: {Class}", crashingClass);
                    runResults.SynthesizeCrashEntries(crashingClass, inventory);
                    excludeClasses.Add(crashingClass);
                }

                foreach (var cls in runResults.CompletedClasses)
                    excludeClasses.Add(cls);

                aggregated.Merge(runResults);

                // Check if there are remaining classes to run (scoped to eligible set)
                var remaining = eligibleClasses.Except(excludeClasses).ToList();
                if (remaining.Count == 0)
                {
                    Log.Information("All classes either completed or crashed — no more to run.");
                    break;
                }

                Log.Information("Remaining classes: {Count}", remaining.Count);

                if (attempt == maxRetries)
                {
                    Log.Error("Max retries ({Max}) reached on device.", maxRetries);
                    break;
                }

                continue;
            }

            break;
        }

        var finalJsonl = aggregated.Tests.Count > 0 ? aggregated : null;
        ReportRuntimeTestResult(lastResult!, "Device/NativeAOT", finalJsonl);
    }

    // ============================================================
    // Shared Helpers: macOS Execution
    // ============================================================

    void RunOnMacOS()
    {
        Log.Information("--- Running on macOS ---");

        // macOS uses --platform simulator (Mono JIT mode, same as simulator)
        var launchArgs = "--platform simulator";
        if (FlakeDetect) launchArgs += " --flake-detect";
        if (!string.IsNullOrEmpty(ClassFilter)) launchArgs += $" --class {ClassFilter}";

        Log.Information("Launching RuntimeTestsApp.Mac (timeout: {Timeout}s)...", Timeout);

        var output = new ConcurrentQueue<string>();
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{BindingTestsDir / "RuntimeTestsApp.Mac"}\" --no-build -c Debug -- {launchArgs}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.Enqueue(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.Enqueue(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var sw = Stopwatch.StartNew();
        var testResult = TestResult.Timeout;
        bool resultsFlushed = false;

        while (sw.Elapsed < TimeSpan.FromSeconds(Timeout))
        {
            if (process.HasExited)
            {
                Thread.Sleep(100);
                var text = string.Join("\n", output);
                resultsFlushed = text.Contains("RESULTS FLUSHED");
                if (text.Contains("TEST SUCCESS")) testResult = TestResult.Success;
                else if (text.Contains("TEST FAILURE")) testResult = TestResult.Failure;
                else testResult = TestResult.LaunchFailure;
                break;
            }

            var currentText = string.Join("\n", output);
            if (currentText.Contains("RESULTS FLUSHED"))
            {
                resultsFlushed = true;
                if (currentText.Contains("TEST SUCCESS")) { testResult = TestResult.Success; break; }
                if (currentText.Contains("TEST FAILURE")) { testResult = TestResult.Failure; break; }
            }

            Thread.Sleep(250);
        }

        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }
        }

        var finalOutput = string.Join("\n", output);
        int? exitCode = null;
        try { if (process.HasExited) exitCode = process.ExitCode; } catch { }

        var result = new LaunchResult(testResult, finalOutput, exitCode, null, resultsFlushed);

        Log.Information("");
        Log.Information("=== APP OUTPUT ===");
        Log.Information(result.Output);

        // macOS: try to read JSONL from working directory (dotnet run uses repo root as cwd)
        JsonlTestResults? jsonlResults = null;
        var macJsonlPath = RootDirectory / "test-results.jsonl";
        if (File.Exists(macJsonlPath))
        {
            jsonlResults = JsonlTestResults.ParseFile(macJsonlPath);
            Log.Information("JSONL results: {Summary}", jsonlResults.ToString());
        }

        ReportRuntimeTestResult(result, "macOS", jsonlResults);
    }

    // ============================================================
    // Crash Diagnostics
    // ============================================================

    void HandleCrashDiagnostics(LaunchResult result, string simulatorUdid, int crashLogsBefore)
    {
        if (result.Result is not (TestResult.Crash or TestResult.LaunchFailure or TestResult.Timeout))
            return;

        // Check crash log count delta
        var crashLogsAfter = SimCtl.CountCrashLogs("RuntimeTestsApp");
        if (crashLogsAfter > crashLogsBefore)
        {
            var crashLog = SimCtl.FindLatestCrashLog("RuntimeTestsApp");
            if (crashLog != null)
            {
                Log.Error("Crash log detected: {Path}", crashLog);
                try
                {
                    var crashContent = File.ReadAllLines(crashLog).Take(30);
                    foreach (var line in crashContent)
                        Log.Error("  {Line}", line);
                }
                catch { }
            }
        }

        // Read device log for crash evidence
        var deviceLog = SimCtl.ReadLog(simulatorUdid, TimeSpan.FromMinutes(3), "RuntimeTestsApp");
        if (!string.IsNullOrEmpty(deviceLog))
        {
            var isMonoJitCrash = SimCtl.IsMonoJitCrash(deviceLog) ||
                SimCtl.IsMonoJitCrash(result.Output);

            if (isMonoJitCrash)
            {
                Log.Error("");
                Log.Error("=== DEVICE LOG (crash evidence) ===");
                var crashLines = deviceLog.Split('\n')
                    .Where(l => l.Contains("crash", StringComparison.OrdinalIgnoreCase) ||
                                l.Contains("assert", StringComparison.OrdinalIgnoreCase) ||
                                l.Contains("abort", StringComparison.OrdinalIgnoreCase) ||
                                l.Contains("exc_bad", StringComparison.OrdinalIgnoreCase) ||
                                l.Contains("jit-info", StringComparison.OrdinalIgnoreCase) ||
                                l.Contains("ReleaseHandle") ||
                                l.Contains("SIGABRT") ||
                                l.Contains("fatal", StringComparison.OrdinalIgnoreCase))
                    .TakeLast(10);
                foreach (var line in crashLines)
                    Log.Error("  {Line}", line);

                var passCount = result.Output.Split('\n')
                    .Count(l => l.Contains("[PASS]"));
                Log.Error("");
                Log.Error("Mono JIT crash detected on simulator ({PassCount} tests passed before crash).", passCount);
                Log.Error("This crash is a regression — diagnose the root cause (see CLAUDE.md).");
            }
            else if (deviceLog.Contains("EXC_BAD_ACCESS") || deviceLog.Contains("SIGABRT"))
            {
                Log.Warning("");
                Log.Warning("=== DEVICE LOG (last 3 min, RuntimeTestsApp) ===");
                var logLines = deviceLog.Split('\n').TakeLast(30);
                foreach (var line in logLines)
                    Log.Warning("  {Line}", line);
            }
        }

        // Extract partial results from output before crash
        var failCount = result.Output.Split('\n')
            .Count(l => l.Contains("[FAIL]") && l.Contains("ms)"));
        var passCountFinal = result.Output.Split('\n')
            .Count(l => l.Contains("[PASS]"));

        if (failCount > 0)
        {
            Log.Error("{FailCount} test(s) failed before crash ({PassCount} passed).", failCount, passCountFinal);
            var failingTests = result.Output.Split('\n')
                .Where(l => l.Contains("[FAIL]") && l.Contains("ms)"));
            foreach (var test in failingTests)
                Log.Error("  {Test}", test.Trim());
        }
    }

    // ============================================================
    // Result Reporting
    // ============================================================

    void ReportRuntimeTestResult(LaunchResult result, string platform, JsonlTestResults? jsonlResults = null)
    {
        // Log JSONL-derived counts if available
        if (jsonlResults != null)
        {
            Log.Information("");
            Log.Information("=== JSONL TEST COUNTS ({Platform}) ===", platform);
            Log.Information("  Pass:  {Pass}", jsonlResults.PassCount);
            Log.Information("  Fail:  {Fail}", jsonlResults.FailCount);
            Log.Information("  Skip:  {Skip}", jsonlResults.SkipCount);
            Log.Information("  Crash: {Crash}", jsonlResults.CrashCount);
            Log.Information("  Done:  {Done}", jsonlResults.Done);

            // Report crashed classes explicitly
            var crashedClasses = jsonlResults.CrashedClasses;
            if (crashedClasses.Count > 0)
            {
                Log.Warning("  Crashed classes ({Count}):", crashedClasses.Count);
                foreach (var cls in crashedClasses)
                {
                    var crashCount = jsonlResults.Tests.Count(t => t.ClassName == cls && t.Status == "crash");
                    Log.Warning("    - {Class} ({Count} methods crashed)", cls, crashCount);
                }
            }
        }

        // If we have aggregated results from crash recovery, adjust the final verdict.
        // A crash that was recovered (all remaining classes ran) is reported as Success/Failure
        // based on actual test results, not the crash status of the last launch.
        var effectiveResult = result.Result;
        if (jsonlResults != null && jsonlResults.CrashCount > 0 &&
            result.Result is TestResult.Crash or TestResult.Timeout or TestResult.LaunchFailure)
        {
            // Crash recovery completed — report based on aggregated fail count
            if (jsonlResults.FailCount > 0)
                effectiveResult = TestResult.Failure;
            else
                effectiveResult = TestResult.Success;
            Log.Information("Crash recovery completed — reporting based on aggregated results.");
        }

        Log.Information("");
        Log.Information("=========================================");
        switch (effectiveResult)
        {
            case TestResult.Success:
                Log.Information(" RUNTIME TESTS PASSED ({Platform})", platform);
                Log.Information("=========================================");
                if (jsonlResults != null)
                    CompareRuntimeBaseline(platform, jsonlResults);
                break;

            case TestResult.Failure:
                Log.Information(" RUNTIME TESTS FAILED ({Platform})", platform);
                Log.Information("=========================================");
                if (jsonlResults != null)
                    CompareRuntimeBaseline(platform, jsonlResults);
                throw new Exception($"Runtime tests failed ({platform})");

            case TestResult.Crash:
                Log.Information(" RUNTIME TESTS CRASHED ({Platform})", platform);
                Log.Information("=========================================");
                throw new Exception($"Runtime tests crashed ({platform})");

            case TestResult.LaunchFailure:
                Log.Information(" RUNTIME TESTS LAUNCH FAILURE ({Platform})", platform);
                Log.Information("=========================================");
                throw new Exception($"Runtime tests launch failure ({platform})");

            case TestResult.Timeout:
                Log.Information(" RUNTIME TESTS TIMEOUT ({Platform})", platform);
                Log.Information("=========================================");
                throw new Exception($"Runtime tests timed out ({platform})");
        }
    }

    /// <summary>
    /// Compares JSONL results against the runtime tests baseline.
    /// Fails if pass count drops, warns + auto-updates if pass count increases.
    /// </summary>
    void CompareRuntimeBaseline(string platform, JsonlTestResults jsonlResults)
    {
        var baseline = ValidationBaseline.Load(BaselinePath);
        var runtimeBaseline = baseline.RuntimeTests;

        // Determine which platform baseline to compare
        var platformKey = platform.ToLowerInvariant() switch
        {
            "simulator" => "simulator",
            "device/nativeaot" or "device" => "device",
            _ => null // macOS — no baseline yet
        };

        if (platformKey == null || runtimeBaseline == null)
        {
            Log.Information("No runtime test baseline for {Platform} — skipping comparison", platform);
            return;
        }

        var baselineCounts = platformKey == "simulator" ? runtimeBaseline.Simulator : runtimeBaseline.Device;
        if (baselineCounts == null)
        {
            Log.Information("No runtime test baseline for {Platform} — skipping comparison", platform);
            return;
        }

        var currentPass = jsonlResults.PassCount;
        var baselinePass = baselineCounts.Pass;

        Log.Information("");
        Log.Information("=== RUNTIME BASELINE COMPARISON ({Platform}) ===", platform);
        Log.Information("  Baseline pass: {Baseline}", baselinePass);
        Log.Information("  Current pass:  {Current}", currentPass);

        if (currentPass < baselinePass)
        {
            var delta = baselinePass - currentPass;
            Log.Error("REGRESSION: {Platform} pass count dropped by {Delta} (baseline={Baseline}, current={Current})",
                platform, delta, baselinePass, currentPass);
            throw new Exception(
                $"Runtime test regression on {platform}: pass count dropped from {baselinePass} to {currentPass} (-{delta})");
        }

        if (currentPass > baselinePass)
        {
            var delta = currentPass - baselinePass;
            Log.Warning("IMPROVEMENT: {Platform} pass count increased by {Delta} (baseline={Baseline}, current={Current})",
                platform, delta, baselinePass, currentPass);

            // Auto-update baseline on unfiltered, no-crash successful runs
            if (string.IsNullOrEmpty(ClassFilter) && jsonlResults.CrashCount == 0)
            {
                var newCounts = new ValidationBaseline.RuntimeTestsPlatformCounts
                {
                    Pass = currentPass,
                    Fail = jsonlResults.FailCount,
                    Skip = jsonlResults.SkipCount,
                    Crash = 0
                };

                var newRuntimeBaseline = platformKey == "simulator"
                    ? runtimeBaseline with { Simulator = newCounts }
                    : runtimeBaseline with { Device = newCounts };

                var newBaseline = baseline with { RuntimeTests = newRuntimeBaseline };
                newBaseline.Save(BaselinePath);
                Log.Information("Baseline auto-updated for {Platform}: pass={Pass}", platform, currentPass);
            }
        }
        else
        {
            Log.Information("Baseline matches: {Platform} pass count = {Pass}", platform, currentPass);
        }
    }


    // ============================================================
    // Staleness Detection
    // ============================================================

    void AssertBindingsNotStale(AbsolutePath? outputDirOverride = null)
    {
        var outputDir = outputDirOverride ?? BtOutputDir;
        var bindingsFile = outputDir / $"{ModuleName}.cs";

        if (!File.Exists(bindingsFile))
            throw new InvalidOperationException(
                $"Bindings not found at {bindingsFile}. Run without --skip-regen first.");

        var bindingsTime = File.GetLastWriteTimeUtc(bindingsFile);
        var swiftSourceDir = BindingTestsDir / "Sources" / "SwiftBindingsTestLib";

        if (!Directory.Exists(swiftSourceDir)) return;

        var newerSource = Directory.GetFiles(swiftSourceDir, "*.swift", SearchOption.AllDirectories)
            .FirstOrDefault(f => File.GetLastWriteTimeUtc(f) > bindingsTime);

        if (newerSource != null)
            throw new InvalidOperationException(
                $"Bindings are stale. Swift source newer than bindings: {newerSource}. " +
                "Run without --skip-regen to regenerate.");

        Log.Information("Staleness check passed: bindings are up to date.");
    }

    void AssertDeviceSliceExists()
    {
        var deviceSliceDir = BtXcframeworkDir / "ios-arm64";
        if (!Directory.Exists(deviceSliceDir))
            throw new InvalidOperationException(
                "Device slice missing from SwiftBindingsTestLib.xcframework. " +
                "Run without --skip-regen first.");
    }

    // ============================================================
    // Native Artifact Injection (Simulator)
    // ============================================================

    /// <summary>
    /// Injects libSwiftBindingsRuntime.dylib into the app bundle Frameworks/ directory.
    /// </summary>
    void InjectRuntimeDylib(AbsolutePath appFrameworks)
    {
        var runtimeDylib = RootDirectory / "src" / "Swift.Runtime" / "native" / "iossimulator" /
            "libSwiftBindingsRuntime.dylib";

        appFrameworks.CreateDirectory();
        if (File.Exists(runtimeDylib))
        {
            File.Copy(runtimeDylib, appFrameworks / "libSwiftBindingsRuntime.dylib", overwrite: true);
            Log.Information("Injected libSwiftBindingsRuntime.dylib into app bundle.");
        }
        else
        {
            Log.Warning("libSwiftBindingsRuntime.dylib not found at {Path}", runtimeDylib);
            Log.Warning("Existential metadata tests will fail.");
        }
    }

    /// <summary>
    /// Injects the SwiftBindings async wrapper framework into the app bundle.
    /// The resolver uses @rpath/SwiftBindings.framework/SwiftBindings.
    /// </summary>
    void InjectAsyncWrapper(AbsolutePath appFrameworks)
    {
        var platform = ResolvedPlatform;
        var wrapperSlice = BtOutputDir / $"{WrapperModule}.xcframework" /
            platform.SimulatorSliceId / $"{WrapperModule}.framework" / WrapperModule;

        if (File.Exists(wrapperSlice))
        {
            var targetDir = appFrameworks / $"{WrapperModule}.framework";
            targetDir.CreateDirectory();
            File.Copy(wrapperSlice, targetDir / WrapperModule, overwrite: true);
            Log.Information("Injected {Module} wrapper dylib into app bundle.", WrapperModule);
        }
        else
        {
            Log.Information("Note: {Module} wrapper dylib not found — wrapper-dependent tests will be skipped.",
                WrapperModule);
        }
    }

    /// <summary>
    /// Injects the SwiftBindingsTestLibDependency framework into the app bundle.
    /// </summary>
    void InjectDependencyFramework(AbsolutePath appFrameworks)
    {
        var platform = ResolvedPlatform;
        var depFwDir = BtDepXcframeworkDir / platform.SimulatorSliceId /
            $"{DepModuleName}.framework";

        if (Directory.Exists(depFwDir))
        {
            var targetDir = appFrameworks / $"{DepModuleName}.framework";
            targetDir.CreateDirectory();
            File.Copy(depFwDir / DepModuleName, targetDir / DepModuleName, overwrite: true);

            // Copy or generate Info.plist
            var plistSource = depFwDir / "Info.plist";
            if (File.Exists(plistSource))
                File.Copy(plistSource, targetDir / "Info.plist", overwrite: true);
            else
                PlistGenerator.WriteFrameworkPlist(
                    targetDir / "Info.plist",
                    $"com.test.{DepModuleName}", DepModuleName, DepModuleName,
                    platform.MinOsVersion, platform.SimulatorPlistPlatform);

            Log.Information("Injected {Module} framework into app bundle.", DepModuleName);
        }
        else
        {
            Log.Information("Note: {Module} framework not found — cross-module tests may fail.", DepModuleName);
        }
    }

    /// <summary>
    /// Injects the dependency wrapper framework into the app bundle.
    /// </summary>
    void InjectDependencyWrapper(AbsolutePath appFrameworks)
    {
        var platform = ResolvedPlatform;
        var depWrapperName = $"{DepModuleName}SwiftBindings";
        var depWrapperDir = BtOutputDir / $"{depWrapperName}.xcframework" /
            platform.SimulatorSliceId / $"{depWrapperName}.framework";

        if (Directory.Exists(depWrapperDir))
        {
            var targetDir = appFrameworks / $"{depWrapperName}.framework";
            targetDir.CreateDirectory();
            File.Copy(depWrapperDir / depWrapperName, targetDir / depWrapperName, overwrite: true);

            // Copy or generate Info.plist
            var plistSource = depWrapperDir / "Info.plist";
            if (File.Exists(plistSource))
                File.Copy(plistSource, targetDir / "Info.plist", overwrite: true);
            else
                PlistGenerator.WriteFrameworkPlist(
                    targetDir / "Info.plist",
                    $"com.test.{depWrapperName}", depWrapperName, depWrapperName,
                    platform.MinOsVersion, platform.SimulatorPlistPlatform);

            Log.Information("Injected {Module} wrapper into app bundle.", depWrapperName);
        }
        else
        {
            Log.Information("Note: {Module} wrapper not found — dependency wrapper tests may fail.", depWrapperName);
        }
    }

    // ============================================================
    // Native Artifact Injection (macOS)
    // ============================================================

    /// <summary>
    /// Injects native libraries into the macOS output directory as flat dylibs.
    /// macOS doesn't use framework bundles — just copies dylibs directly.
    /// </summary>
    void InjectMacOSNativeLibraries(AbsolutePath outputBin)
    {
        var macosPlatform = ApplePlatform.MacOS;

        // 1. SwiftBindingsTestLib dylib from xcframework
        var xcfwSlice = BtXcframeworkDir / macosPlatform.SimulatorSliceId /
            $"{ModuleName}.framework" / ModuleName;
        if (File.Exists(xcfwSlice))
        {
            File.Copy(xcfwSlice, outputBin / $"lib{ModuleName}.dylib", overwrite: true);
            Log.Information("Injected {Module} dylib.", ModuleName);
        }
        else
        {
            Log.Warning("{Module} dylib not found at {Path}", ModuleName, xcfwSlice);
        }

        // 2. SwiftBindings async wrapper dylib
        var asyncSlice = BtMacOSOutputDir / $"{WrapperModule}.xcframework" /
            macosPlatform.SimulatorSliceId / $"{WrapperModule}.framework" / WrapperModule;
        if (File.Exists(asyncSlice))
        {
            File.Copy(asyncSlice, outputBin / $"lib{WrapperModule}.dylib", overwrite: true);
            Log.Information("Injected {Module} async wrapper dylib.", WrapperModule);
        }

        // 3. Runtime dylib
        var runtimeDylib = RootDirectory / "src" / "Swift.Runtime" / "native" / "macos" /
            "libSwiftBindingsRuntime.dylib";
        if (File.Exists(runtimeDylib))
        {
            File.Copy(runtimeDylib, outputBin / "libSwiftBindingsRuntime.dylib", overwrite: true);
            Log.Information("Injected libSwiftBindingsRuntime.dylib.");
        }
        else
        {
            Log.Warning("libSwiftBindingsRuntime.dylib not found at {Path}", runtimeDylib);
        }
    }

    // ============================================================
    // macOS Binding Generation
    // ============================================================

    /// <summary>
    /// Generates macOS-specific bindings. Simpler than RunRegenerateBindings:
    /// no dependency bindings, no strict mode, uses --platform macos.
    /// </summary>
    void RunRegenerateMacOSBindings()
    {
        Log.Information("=== Generating macOS bindings for {Module} ===", ModuleName);

        EnsureGeneratorBuilt();

        if (Directory.Exists(BtMacOSOutputDir))
            BtMacOSOutputDir.DeleteDirectory();
        BtMacOSOutputDir.CreateDirectory();

        var genArgs = new List<string>
        {
            $"\"{GeneratorDll}\"",
            $"--xcframework \"{BtXcframeworkDir}\"",
            "--platform macos",
            $"-o \"{BtMacOSOutputDir}\"",
        };

        var genProcess = ProcessTasks.StartProcess(
            "dotnet", string.Join(" ", genArgs),
            workingDirectory: BindingTestsDir,
            logOutput: false);
        genProcess.WaitForExit();

        if (genProcess.ExitCode != 0)
            Log.Warning("macOS binding generation exited with code {ExitCode} (non-fatal)", genProcess.ExitCode);

        var csCount = Directory.GetFiles(BtMacOSOutputDir, "*.cs", SearchOption.AllDirectories).Length;
        var swiftCount = Directory.GetFiles(BtMacOSOutputDir, "*.swift", SearchOption.AllDirectories).Length;
        Log.Information("Generated (macOS): {CsCount} C# files, {SwiftCount} Swift wrapper files", csCount, swiftCount);
    }

    // ============================================================
    // Device Wrapper Build
    // ============================================================

    /// <summary>
    /// Builds the Swift wrapper and SwiftBindingsRuntime for device (ios-arm64).
    /// Ports build-wrapper-device.sh: strips broken code, compiles for device target,
    /// creates framework structure, also builds SwiftBindingsRuntime device xcframework.
    /// </summary>
    void RunBuildDeviceWrappers()
    {
        var platform = ResolvedPlatform;
        if (!platform.HasDeviceSlice)
        {
            Log.Information("No device slice for {Platform} — skipping device wrappers.", platform.Name);
            return;
        }

        var deviceTarget = platform.DeviceTarget!;
        var deviceSdkName = platform.DeviceSdkName!;
        var deviceSliceId = platform.DeviceSliceId!;
        var devicePlistPlatform = platform.DevicePlistPlatform!;

        var xcfwSliceDir = BtXcframeworkDir / deviceSliceId;
        var depXcfwSliceDir = BtDepXcframeworkDir / deviceSliceId;

        // Verify device slice exists
        if (!Directory.Exists(xcfwSliceDir))
            throw new Exception($"Device slice missing: {xcfwSliceDir}. Run build-xcframework with --include-device.");

        Log.Information("=== Building {Module} wrapper (device) ===", WrapperModule);

        // Collect Swift wrapper files
        var swiftFiles = Directory.GetFiles(BtOutputDir, "*.swift")
            .Where(f => !f.EndsWith(".SwiftUIBridge.swift"))
            .ToList();

        if (swiftFiles.Count == 0)
        {
            Log.Information("No Swift wrapper files found — skipping device wrapper build.");
            return;
        }

        // Post-process: strip known-broken sections
        var cleanedDir = BtOutputDir / ".wrapper-build-device";
        if (Directory.Exists(cleanedDir))
            ((AbsolutePath)cleanedDir).DeleteDirectory();
        cleanedDir.CreateDirectory();

        int totalStripped = 0;
        foreach (var swiftFile in swiftFiles)
        {
            var basename = Path.GetFileName(swiftFile);
            var result = SwiftSourceStripper.StripFile(swiftFile, cleanedDir / basename);
            totalStripped += result.StrippedCount;
        }

        var cleanedFiles = Directory.GetFiles(cleanedDir, "*.swift").ToList();
        if (cleanedFiles.Count == 0)
        {
            Log.Information("No cleaned Swift files to compile for device.");
            return;
        }

        // Compile native ARM64 thunk assembly files (if any)
        var thunkObjects = new List<string>();
        foreach (var asmFile in Directory.GetFiles(BtOutputDir, "*.arm64.s"))
        {
            var objFile = Path.ChangeExtension(asmFile, ".device.o");
            XcRunTool($"clang -c {asmFile} -o {objFile} -target {deviceTarget}");
            thunkObjects.Add(objFile);
        }

        // Create device framework output
        var wrapperXcfDir = BtOutputDir / $"{WrapperModule}.xcframework";
        var outputFwDir = wrapperXcfDir / deviceSliceId / $"{WrapperModule}.framework";
        outputFwDir.CreateDirectory();

        var sdkPath = XcRun.GetSdkPath(deviceSdkName);

        // Compile with error-based retry (same pattern as RunBuildAsyncWrapper)
        const int maxRetries = 3;
        int attempt = 0;

        while (attempt < maxRetries)
        {
            attempt++;
            var allSourceFiles = cleanedFiles.Concat(thunkObjects).ToList();

            var settings = new SwiftCompilerSettings()
                .SetEmitLibrary()
                .SetTarget(deviceTarget)
                .SetSdk(sdkPath)
                .AddFrameworkSearchPath(xcfwSliceDir + "/")
                .SetModuleName(WrapperModule)
                .SetStrictConcurrency("minimal")
                .SetInstallName($"@rpath/{WrapperModule}.framework/{WrapperModule}")
                .SetOutputPath(outputFwDir / WrapperModule)
                .AddSourceFiles(allSourceFiles);

            if (Directory.Exists(depXcfwSliceDir))
                settings.AddFrameworkSearchPath(depXcfwSliceDir + "/");

            var process = SwiftCompiler.Run(settings);
            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                Log.Information("Device wrapper compilation succeeded (after {Attempt} attempt(s)).", attempt);
                break;
            }

            var compileLog = string.Join("\n", process.Output.Select(o => o.Text));

            if (attempt == maxRetries)
            {
                Log.Warning("Device wrapper compilation failed after {Retries} attempts. Continuing without.", maxRetries);
                CleanupWrapperBuild(cleanedDir);
                return;
            }

            Log.Information("Device compilation attempt {Attempt} failed — stripping broken functions...", attempt);
            var errors = string.Join("\n", compileLog.Split('\n').Where(l => l.Contains("error:")).Take(80));
            int strippedN = SwiftSourceStripper.StripErrorFunctions(cleanedDir, errors);

            if (strippedN == 0)
            {
                Log.Warning("No strippable functions found. Device build error may be structural.");
                CleanupWrapperBuild(cleanedDir);
                return;
            }

            totalStripped += strippedN;
            cleanedFiles = Directory.GetFiles(cleanedDir, "*.swift").ToList();
            Log.Information("Retrying device compilation...");
        }

        CleanupWrapperBuild(cleanedDir);

        // Create framework Info.plist
        PlistGenerator.WriteFrameworkPlist(
            outputFwDir / "Info.plist",
            $"com.swiftbindings.{WrapperModule}", WrapperModule, WrapperModule,
            platform.MinOsVersion, devicePlistPlatform);

        // Update xcframework Info.plist to include both simulator and device slices
        WriteDeviceXcframeworkPlist(wrapperXcfDir / "Info.plist", WrapperModule, platform);

        Log.Information("{Module} device wrapper framework built successfully.", WrapperModule);

        // --- Part 2: Build dependency wrapper device xcframework ---
        BuildDependencyWrapperDevice(platform, deviceTarget, deviceSdkName, deviceSliceId, devicePlistPlatform);

        // --- Part 3: Build SwiftBindingsRuntime device xcframework ---
        BuildRuntimeDeviceXcframework(platform);
    }

    /// <summary>
    /// Builds the dependency module's Swift wrapper for device (ios-arm64).
    /// Uses preserved Swift sources from RunRegenerateBindings (dep-swift/ directory).
    /// </summary>
    void BuildDependencyWrapperDevice(ApplePlatform platform, string deviceTarget, string deviceSdkName,
        string deviceSliceId, string devicePlistPlatform)
    {
        var depWrapperName = $"{DepModuleName}SwiftBindings";
        var depSwiftDir = BtOutputDir / "dep-swift";

        if (!Directory.Exists(depSwiftDir))
        {
            Log.Information("No dependency wrapper Swift sources — skipping device dependency wrapper build.");
            return;
        }

        var swiftFiles = Directory.GetFiles(depSwiftDir, "*.swift").ToList();
        if (swiftFiles.Count == 0)
        {
            Log.Information("No dependency wrapper Swift files found.");
            return;
        }

        Log.Information("=== Building {Module} (device) ===", depWrapperName);

        var xcfwSliceDir = BtXcframeworkDir / deviceSliceId;
        var depXcfwSliceDir = BtDepXcframeworkDir / deviceSliceId;
        var sdkPath = XcRun.GetSdkPath(deviceSdkName);

        var depWrapperXcf = BtOutputDir / $"{depWrapperName}.xcframework";
        var outputFwDir = depWrapperXcf / deviceSliceId / $"{depWrapperName}.framework";
        outputFwDir.CreateDirectory();

        // Strip known-broken sections (same pattern as main wrapper)
        var cleanedDir = BtOutputDir / ".dep-wrapper-build-device";
        if (Directory.Exists(cleanedDir))
            ((AbsolutePath)cleanedDir).DeleteDirectory();
        cleanedDir.CreateDirectory();

        foreach (var sf in swiftFiles)
            SwiftSourceStripper.StripFile(sf, cleanedDir / Path.GetFileName(sf));

        var cleanedFiles = Directory.GetFiles(cleanedDir, "*.swift").ToList();
        if (cleanedFiles.Count == 0)
        {
            Log.Information("No cleaned dependency wrapper Swift files to compile.");
            CleanupWrapperBuild(cleanedDir);
            return;
        }

        var settings = new SwiftCompilerSettings()
            .SetEmitLibrary()
            .SetTarget(deviceTarget)
            .SetSdk(sdkPath)
            .AddFrameworkSearchPath(depXcfwSliceDir + "/")
            .SetModuleName(depWrapperName)
            .SetStrictConcurrency("minimal")
            .SetInstallName($"@rpath/{depWrapperName}.framework/{depWrapperName}")
            .SetOutputPath(outputFwDir / depWrapperName)
            .AddSourceFiles(cleanedFiles);

        // Also need main library search path for cross-module references
        if (Directory.Exists(xcfwSliceDir))
            settings.AddFrameworkSearchPath(xcfwSliceDir + "/");

        var process = SwiftCompiler.Run(settings);
        process.WaitForExit();

        CleanupWrapperBuild(cleanedDir);

        if (process.ExitCode != 0)
        {
            Log.Warning("Dependency wrapper device compilation failed. Cross-module tests will be skipped on device.");
            return;
        }

        PlistGenerator.WriteFrameworkPlist(
            outputFwDir / "Info.plist",
            $"com.swiftbindings.{depWrapperName}", depWrapperName, depWrapperName,
            platform.MinOsVersion, devicePlistPlatform);

        WriteDeviceXcframeworkPlist(depWrapperXcf / "Info.plist", depWrapperName, platform);

        Log.Information("{Module} device wrapper built successfully.", depWrapperName);
    }

    void BuildRuntimeDeviceXcframework(ApplePlatform platform)
    {
        Log.Information("=== Building SwiftBindingsRuntime xcframework (device) ===");

        var runtimeDylib = RootDirectory / "src" / "Swift.Runtime" / "native" / "ios" /
            "libSwiftBindingsRuntime.dylib";

        if (!File.Exists(runtimeDylib))
        {
            Log.Warning("Device runtime dylib not found at {Path}. Skipping.", runtimeDylib);
            return;
        }

        var runtimeXcfw = BtBuildDir / "SwiftBindingsRuntime.xcframework";
        var runtimeFwDir = runtimeXcfw / "ios-arm64" / "SwiftBindingsRuntime.framework";
        runtimeFwDir.CreateDirectory();

        File.Copy(runtimeDylib, runtimeFwDir / "SwiftBindingsRuntime", overwrite: true);

        // Fix install_name to use @rpath
        try
        {
            XcRunTool($"install_name_tool -id @rpath/SwiftBindingsRuntime.framework/SwiftBindingsRuntime " +
                $"{runtimeFwDir / "SwiftBindingsRuntime"}");
        }
        catch (Exception ex)
        {
            Log.Warning("install_name_tool failed: {Message}", ex.Message);
        }

        // Code sign
        try
        {
            XcRunTool($"codesign --force --sign - \"{runtimeFwDir / "SwiftBindingsRuntime"}\"");
        }
        catch { /* Best-effort signing */ }

        PlistGenerator.WriteFrameworkPlist(
            runtimeFwDir / "Info.plist",
            "com.swiftbindings.SwiftBindingsRuntime", "SwiftBindingsRuntime", "SwiftBindingsRuntime",
            platform.MinOsVersion, platform.DevicePlistPlatform!);

        // Create xcframework Info.plist with device slice (preserve simulator if exists)
        WriteDeviceXcframeworkPlist(runtimeXcfw / "Info.plist", "SwiftBindingsRuntime", platform);

        Log.Information("SwiftBindingsRuntime device xcframework built successfully.");
    }

    /// <summary>
    /// Writes an xcframework Info.plist that includes both simulator and device slices
    /// when both exist. Used for device wrapper and runtime xcframeworks.
    /// </summary>
    void WriteDeviceXcframeworkPlist(string outputPath, string moduleName, ApplePlatform platform)
    {
        var xcfwDir = Path.GetDirectoryName(outputPath)!;
        var libraries = new List<string>();

        // Add simulator slice if it exists
        if (Directory.Exists(Path.Combine(xcfwDir, platform.SimulatorSliceId)))
        {
            var variantXml = platform.SimulatorPlistVariant != null
                ? $@"
            <key>SupportedPlatformVariant</key>
            <string>{platform.SimulatorPlistVariant}</string>"
                : "";

            libraries.Add($"""
                    <dict>
                        <key>LibraryIdentifier</key>
                        <string>{platform.SimulatorSliceId}</string>
                        <key>LibraryPath</key>
                        <string>{moduleName}.framework</string>
                        <key>SupportedArchitectures</key>
                        <array>
                            <string>arm64</string>
                        </array>
                        <key>SupportedPlatform</key>
                        <string>{platform.SupportedPlatform}</string>{variantXml}
                    </dict>
            """);
        }

        // Add device slice if it exists
        if (platform.HasDeviceSlice && Directory.Exists(Path.Combine(xcfwDir, platform.DeviceSliceId!)))
        {
            libraries.Add($"""
                    <dict>
                        <key>LibraryIdentifier</key>
                        <string>{platform.DeviceSliceId}</string>
                        <key>LibraryPath</key>
                        <string>{moduleName}.framework</string>
                        <key>SupportedArchitectures</key>
                        <array>
                            <string>arm64</string>
                        </array>
                        <key>SupportedPlatform</key>
                        <string>{platform.SupportedPlatform}</string>
                    </dict>
            """);
        }

        var content = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
                "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>AvailableLibraries</key>
                <array>
            {string.Join("\n", libraries)}
                </array>
                <key>CFBundlePackageType</key>
                <string>XFWK</string>
                <key>XCFrameworkFormatVersion</key>
                <string>1.0</string>
            </dict>
            </plist>
            """;
        File.WriteAllText(outputPath, content);
    }
}
