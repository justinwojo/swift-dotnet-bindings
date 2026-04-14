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
using System.Text.RegularExpressions;
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

    // Opt-in to the CryptoKit Apple-framework smoke tests. Off by default for the
    // same reason as StoreKit: the direct-mode pipeline regenerates an in-tree
    // snapshot against the active Xcode SDK and runs a hermetic, metadata-only
    // assertion chain that has no business in the default validation gate.
    [Parameter("Opt in to the CryptoKit smoke tests (regenerates BindingTests/obj/CryptoKitSnapshot/ in-tree)")]
    readonly bool EnableCryptoKitSmoke;

    // Opt-in to the WeatherKit Apple-framework smoke tests. Off by default for
    // the same reasons as StoreKit/CryptoKit. WeatherKit's smoke assertions are
    // strictly metadata-only — no WeatherService calls, no entitlement usage —
    // so the test can run in any environment where the framework dylib is
    // reachable by dyld.
    [Parameter("Opt in to the WeatherKit smoke tests (regenerates BindingTests/obj/WeatherKitSnapshot/ in-tree)")]
    readonly bool EnableWeatherKitSmoke;

    // Opt-in to the TipKit Apple-framework smoke tests. Off by default for the
    // same reasons as StoreKit/CryptoKit/WeatherKit. TipKit's smoke fixture is
    // a synthetic Swift `Tip`-conforming type guarded by `#if TIPKIT_SMOKE`,
    // exercised through an `any Tip` parameter — the real-framework pin of
    // fix #7 (PAT fallback to `object` at parameter position).
    [Parameter("Opt in to the TipKit smoke tests (regenerates BindingTests/obj/TipKitSnapshot/ in-tree)")]
    readonly bool EnableTipKitSmoke;

    // Opt-in to the MusicKit Apple-framework smoke tests. Off by default for
    // the same reasons as the other Apple-framework smokes. MusicKit's smoke
    // assertions are strictly metadata-only — no MusicCatalog network calls,
    // no MusicAuthorization.request() sheet — so the test can run in any
    // environment where the framework dylib is reachable by dyld. Pins the
    // property / per-case `@available` propagation path (fix #2) on a real
    // Tier-A framework via reflection-only assertions.
    [Parameter("Opt in to the MusicKit smoke tests (regenerates BindingTests/obj/MusicKitSnapshot/ in-tree)")]
    readonly bool EnableMusicKitSmoke;

    // Opt-in to the WorkoutKit Apple-framework smoke tests. Off by default.
    // WorkoutKit is iOS 17.0+, metadata-only assertions (no HealthKit authorization).
    [Parameter("Opt in to the WorkoutKit smoke tests (regenerates BindingTests/obj/WorkoutKitSnapshot/ in-tree)")]
    readonly bool EnableWorkoutKitSmoke;

    // Opt-in to the RoomPlan Apple-framework smoke tests. Off by default.
    // RoomPlan is iOS 17.0+, metadata-only assertions (no LiDAR/ARSession).
    [Parameter("Opt in to the RoomPlan smoke tests (regenerates BindingTests/obj/RoomPlanSnapshot/ in-tree)")]
    readonly bool EnableRoomPlanSmoke;

    // Opt-in to the ProximityReader Apple-framework smoke tests. Off by default.
    // ProximityReader is iOS 17.4+, metadata-only assertions (no NFC hardware).
    // Known bug: MobileDocumentReaderError.errorDescription is skipped.
    [Parameter("Opt in to the ProximityReader smoke tests (regenerates BindingTests/obj/ProximityReaderSnapshot/ in-tree)")]
    readonly bool EnableProximityReaderSmoke;

    // Opt-in to the LiveCommunicationKit Apple-framework smoke tests. Off by default.
    // LiveCommunicationKit requires iOS 26.0+ (SupportedOSPlatformVersion=26.0).
    // Metadata-only assertions (no VoIP/CallKit session).
    [Parameter("Opt in to the LiveCommunicationKit smoke tests (regenerates BindingTests/obj/LiveCommunicationKitSnapshot/ in-tree)")]
    readonly bool EnableLiveCommunicationKitSmoke;

    /// <summary>
    /// A single opt-in smoke flag. <see cref="FlagName"/> is the user-visible
    /// CLI option (used in error messages and log lines); <see cref="Define"/>
    /// is the Swift / C# conditional-compilation symbol threaded through
    /// <c>swiftc -D</c>, <c>swift-frontend -D</c>, and the generated bindings'
    /// <c>#if</c> gates. Every new <c>Enable&lt;Framework&gt;Smoke</c>
    /// parameter MUST be registered in <see cref="GetActiveSmokeFlags"/> so
    /// the build-infra plumbing (SkipBuild rejection, `-D` threading through
    /// <c>CompileModuleSlice</c>, `.smoke-flags` staleness stamping) picks it
    /// up automatically.
    /// </summary>
    readonly record struct SmokeFlag(string FlagName, string Define);

    /// <summary>
    /// Collects the smoke flags the caller enabled on this invocation. This is
    /// the single registration point for every <c>Enable&lt;Framework&gt;Smoke</c>
    /// parameter — add one line here when a new smoke test lands and the rest
    /// of the build-infra plumbing picks it up for free.
    /// </summary>
    IReadOnlyList<SmokeFlag> GetActiveSmokeFlags()
    {
        var flags = new List<SmokeFlag>();
        if (EnableStoreKitSmoke)
            flags.Add(new SmokeFlag("--enable-storekit-smoke", "STOREKIT_SMOKE"));
        if (EnableCryptoKitSmoke)
            flags.Add(new SmokeFlag("--enable-cryptokit-smoke", "CRYPTOKIT_SMOKE"));
        if (EnableWeatherKitSmoke)
            flags.Add(new SmokeFlag("--enable-weatherkit-smoke", "WEATHERKIT_SMOKE"));
        if (EnableTipKitSmoke)
            flags.Add(new SmokeFlag("--enable-tipkit-smoke", "TIPKIT_SMOKE"));
        if (EnableMusicKitSmoke)
            flags.Add(new SmokeFlag("--enable-musickit-smoke", "MUSICKIT_SMOKE"));
        if (EnableWorkoutKitSmoke)
            flags.Add(new SmokeFlag("--enable-workoutkit-smoke", "WORKOUTKIT_SMOKE"));
        if (EnableRoomPlanSmoke)
            flags.Add(new SmokeFlag("--enable-roomplan-smoke", "ROOMPLAN_SMOKE"));
        if (EnableProximityReaderSmoke)
            flags.Add(new SmokeFlag("--enable-proximityreader-smoke", "PROXIMITYREADER_SMOKE"));
        if (EnableLiveCommunicationKitSmoke)
            flags.Add(new SmokeFlag("--enable-livecommunicationkit-smoke", "LIVECOMMUNICATIONKIT_SMOKE"));
        // Sort by Define at the source so every downstream consumer — log
        // messages, `-D` compiler args, the `.smoke-flags` sidecar — observes
        // the same stable order. Without this the log could print
        // `STOREKIT_SMOKE, CRYPTOKIT_SMOKE` while the sidecar stores
        // `CRYPTOKIT_SMOKE\nSTOREKIT_SMOKE`, which makes "flag set drifted"
        // error messages needlessly confusing.
        return flags.OrderBy(f => f.Define, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Rejects <c>--skip-build</c> in combination with any active
    /// <c>Enable&lt;Framework&gt;Smoke</c> flag. Per-framework snapshots are
    /// regenerated and consumed as part of the app build; honoring
    /// <c>--skip-build</c> with smoke enabled would run the previous session's
    /// app bundle (pinned to whatever Swift.Runtime version built it) against
    /// the current in-tree Swift.Runtime — the stale-AOT footgun originally
    /// documented in <c>src/docs/0.8.0-storekit2-exploration.md</c>. Every new
    /// smoke flag registered in <see cref="GetActiveSmokeFlags"/> is covered
    /// automatically — callers do not need to update this guard.
    /// </summary>
    void RejectSkipBuildWithActiveSmokeFlags()
    {
        if (!SkipBuild)
            return;

        var active = GetActiveSmokeFlags();
        if (active.Count == 0)
            return;

        var names = string.Join(", ", active.Select(f => f.FlagName));
        throw new Exception(
            $"--skip-build and {names} are mutually incompatible: " +
            "per-framework smoke snapshots are regenerated and consumed as part of the app " +
            "build, so skipping the build would leave the previous app bundle pinned to " +
            "whatever Swift.Runtime version built it. That is the stale-AOT footgun " +
            "documented in src/docs/0.8.0-storekit2-exploration.md. Drop --skip-build or " +
            "drop the smoke flag and rerun.");
    }

    // ------------------------------------------------------------------
    // Smoke-flag staleness sidecar
    //
    // AssertBindingsNotStale only compares Swift source mtimes against the
    // generated .cs output, so a `--skip-regen` run after the Enable*Smoke
    // flag set has changed would happily reuse a Swift xcframework that was
    // compiled with a different set of `-D FOO_SMOKE` defines — and the smoke
    // fixture would be missing from (or stuck in) the dylib. To close that
    // hole, every regen stamps the active flag set into this sidecar, and
    // AssertBindingsNotStale rejects `--skip-regen` when the current set
    // doesn't match. Loud failure, same principle as the snapshot freshness
    // fingerprint above.
    // ------------------------------------------------------------------
    const string SmokeFlagsSidecarName = ".smoke-flags";

    // Sibling of .smoke-flags — stamps the generator --platform used for the
    // last regen, so AssertBindingsNotStale can reject --skip-regen across
    // platform boundaries (e.g. running `nuke runtime-tests-tvos-simulator
    // --skip-regen` after a previous iOS regen would otherwise silently reuse
    // iOS-flavored bindings).
    const string TargetPlatformSidecarName = ".target-platform";

    static string FormatSmokeFlagsForSidecar(IReadOnlyList<SmokeFlag> flags)
    {
        // Sort by Define so the stamp is insensitive to registration order in
        // GetActiveSmokeFlags — renaming or reordering entries shouldn't force
        // a regen as long as the enabled set is the same.
        return string.Join(
            "\n",
            flags.Select(f => f.Define).OrderBy(d => d, StringComparer.Ordinal));
    }

    /// <summary>
    /// Writes the active smoke-flag set to a sidecar file alongside the
    /// generated bindings. Called by every regen path (iOS, device, macOS) as
    /// the last step after bindings have been successfully regenerated. The
    /// sidecar is later consulted by <see cref="AssertBindingsNotStale"/> to
    /// detect flag-set drift under <c>--skip-regen</c>.
    /// </summary>
    void StampSmokeFlagsSidecar(AbsolutePath outputDir)
    {
        var sidecar = outputDir / SmokeFlagsSidecarName;
        outputDir.CreateDirectory();
        File.WriteAllText(sidecar, FormatSmokeFlagsForSidecar(GetActiveSmokeFlags()));
    }

    /// <summary>
    /// Writes the generator --platform used for the last regen into a sidecar
    /// alongside the generated bindings. Same loud-failure invariant as
    /// <see cref="StampSmokeFlagsSidecar"/> — just a different axis (platform
    /// instead of smoke-flag set).
    /// </summary>
    void StampTargetPlatformSidecar(AbsolutePath outputDir, ApplePlatform platform)
    {
        var sidecar = outputDir / TargetPlatformSidecarName;
        outputDir.CreateDirectory();
        File.WriteAllText(sidecar, platform.Name);
    }

    // --skip-build implies --skip-regen (matches run-runtime-tests.sh line 56-59).
    bool EffectiveSkipRegen => SkipRegen || SkipBuild;

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
    // StoreKit predates the generalized Apple-snapshot path and keeps its
    // original directory name ("StoreKit2Snapshot") because RuntimeTestsApp.csproj
    // hardcodes it. New frameworks use the canonical <Framework>Snapshot layout
    // produced by the shared RegenerateAppleFrameworkSnapshot helper below.
    AbsolutePath StoreKitSnapshotDir => BindingTestsDir / "obj" / "StoreKit2Snapshot";
    AbsolutePath StoreKitSnapshotAbiJson => StoreKitSnapshotDir / "StoreKit.abi.json";
    AbsolutePath StoreKitSnapshotCsproj => StoreKitSnapshotDir / "StoreKit.Swift.iOS.csproj";
    AbsolutePath StoreKitSnapshotProjectRefTargets =>
        StoreKitSnapshotDir / "StoreKit.Swift.iOS.ProjectReference.targets";

    // CryptoKit snapshot — canonical <Framework>Snapshot layout produced by the
    // generalized RegenerateAppleFrameworkSnapshot helper. No legacy path quirks
    // like StoreKit2Snapshot.
    AbsolutePath CryptoKitSnapshotDir => BindingTestsDir / "obj" / "CryptoKitSnapshot";
    AbsolutePath CryptoKitSnapshotCsproj => CryptoKitSnapshotDir / "CryptoKit.Swift.iOS.csproj";
    AbsolutePath CryptoKitSnapshotProjectRefTargets =>
        CryptoKitSnapshotDir / "CryptoKit.Swift.iOS.ProjectReference.targets";

    // WeatherKit snapshot — same canonical <Framework>Snapshot layout as CryptoKit.
    AbsolutePath WeatherKitSnapshotDir => BindingTestsDir / "obj" / "WeatherKitSnapshot";
    AbsolutePath WeatherKitSnapshotCsproj => WeatherKitSnapshotDir / "WeatherKit.Swift.iOS.csproj";
    AbsolutePath WeatherKitSnapshotProjectRefTargets =>
        WeatherKitSnapshotDir / "WeatherKit.Swift.iOS.ProjectReference.targets";

    // TipKit snapshot — same canonical <Framework>Snapshot layout as CryptoKit/WeatherKit.
    AbsolutePath TipKitSnapshotDir => BindingTestsDir / "obj" / "TipKitSnapshot";
    AbsolutePath TipKitSnapshotCsproj => TipKitSnapshotDir / "TipKit.Swift.iOS.csproj";
    AbsolutePath TipKitSnapshotProjectRefTargets =>
        TipKitSnapshotDir / "TipKit.Swift.iOS.ProjectReference.targets";

    // MusicKit snapshot — same canonical <Framework>Snapshot layout as CryptoKit/WeatherKit/TipKit.
    AbsolutePath MusicKitSnapshotDir => BindingTestsDir / "obj" / "MusicKitSnapshot";
    AbsolutePath MusicKitSnapshotCsproj => MusicKitSnapshotDir / "MusicKit.Swift.iOS.csproj";
    AbsolutePath MusicKitSnapshotProjectRefTargets =>
        MusicKitSnapshotDir / "MusicKit.Swift.iOS.ProjectReference.targets";

    // WorkoutKit snapshot — iOS 17.0+.
    AbsolutePath WorkoutKitSnapshotDir => BindingTestsDir / "obj" / "WorkoutKitSnapshot";
    AbsolutePath WorkoutKitSnapshotCsproj => WorkoutKitSnapshotDir / "WorkoutKit.Swift.iOS.csproj";
    AbsolutePath WorkoutKitSnapshotProjectRefTargets =>
        WorkoutKitSnapshotDir / "WorkoutKit.Swift.iOS.ProjectReference.targets";

    // RoomPlan snapshot — iOS 17.0+.
    AbsolutePath RoomPlanSnapshotDir => BindingTestsDir / "obj" / "RoomPlanSnapshot";
    AbsolutePath RoomPlanSnapshotCsproj => RoomPlanSnapshotDir / "RoomPlan.Swift.iOS.csproj";
    AbsolutePath RoomPlanSnapshotProjectRefTargets =>
        RoomPlanSnapshotDir / "RoomPlan.Swift.iOS.ProjectReference.targets";

    // ProximityReader snapshot — iOS 17.4+.
    AbsolutePath ProximityReaderSnapshotDir => BindingTestsDir / "obj" / "ProximityReaderSnapshot";
    AbsolutePath ProximityReaderSnapshotCsproj => ProximityReaderSnapshotDir / "ProximityReader.Swift.iOS.csproj";
    AbsolutePath ProximityReaderSnapshotProjectRefTargets =>
        ProximityReaderSnapshotDir / "ProximityReader.Swift.iOS.ProjectReference.targets";

    // LiveCommunicationKit snapshot — iOS 26.0+ (SupportedOSPlatformVersion=26.0).
    AbsolutePath LiveCommunicationKitSnapshotDir => BindingTestsDir / "obj" / "LiveCommunicationKitSnapshot";
    AbsolutePath LiveCommunicationKitSnapshotCsproj => LiveCommunicationKitSnapshotDir / "LiveCommunicationKit.Swift.iOS.csproj";
    AbsolutePath LiveCommunicationKitSnapshotProjectRefTargets =>
        LiveCommunicationKitSnapshotDir / "LiveCommunicationKit.Swift.iOS.ProjectReference.targets";

    // CryptoKit macOS snapshot — same framework as iOS, different platform target.
    // Snapshot dir uses the "-macOS" suffix to coexist with the iOS snapshot.
    AbsolutePath CryptoKitMacOSSnapshotDir => BindingTestsDir / "obj" / "CryptoKitSnapshot-macOS";

    // WeatherKit macOS snapshot — same layout as CryptoKit macOS.
    AbsolutePath WeatherKitMacOSSnapshotDir => BindingTestsDir / "obj" / "WeatherKitSnapshot-macOS";

    const string AppleSnapshotSwiftRuntimeVersion = "0.0.0-dev";
    // Name of the fingerprint stamp file written at the end of every successful
    // snapshot regeneration. Read during the freshness check in
    // IsAppleFrameworkSnapshotFresh to detect generator / tooling drift that a
    // plain mtime comparison would miss.
    const string AppleSnapshotFingerprintStampName = ".snapshot-fingerprint";

    // ============================================================
    // Apple-framework snapshot regeneration (generalized)
    // ============================================================

    [Parameter("Apple framework name for regen-apple-snapshot (e.g. CryptoKit)")]
    readonly string? Framework;

    /// <summary>
    /// Exposes <see cref="RegenerateAppleFrameworkSnapshot"/> as a manually
    /// invokable nuke target so contributors can rebuild an in-tree Apple
    /// framework snapshot on demand. Requires <c>--framework &lt;name&gt;</c>;
    /// the snapshot directory is derived as
    /// <c>BindingTests/obj/&lt;Framework&gt;Snapshot/</c>.
    /// </summary>
    // Matches the canonical Apple framework-name shape (e.g. StoreKit, CryptoKit,
    // WeatherKit). Deliberately narrow: the name is interpolated into filesystem
    // paths AND into command-line arguments for xcrun / the generator, so any
    // whitespace, path separator, or shell-significant character could escape
    // the snapshot dir or corrupt the tool invocation. Framework names in the
    // Apple SDK are all PascalCase identifiers, so this is not a real restriction.
    static readonly Regex AppleFrameworkNameRegex =
        new("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled);

    static void ValidateAppleFrameworkName(string frameworkName)
    {
        if (!AppleFrameworkNameRegex.IsMatch(frameworkName))
            throw new Exception(
                $"Invalid Apple framework name '{frameworkName}'. Expected a PascalCase " +
                "identifier (letters / digits / underscore, leading letter) — e.g. " +
                "CryptoKit, WeatherKit, StoreKit. This restriction exists because the " +
                "name is interpolated directly into filesystem paths and command-line " +
                "arguments for swift-api-digester and the generator.");
    }

    Target RegenerateAppleSnapshot => _ => _
        .After(SmokeTest, RuntimeTestsCatalyst)
        .Description("Regenerate an in-tree Apple framework snapshot under BindingTests/obj/<Framework>Snapshot/. Requires --framework <name>.")
        .Executes(() =>
        {
            if (string.IsNullOrWhiteSpace(Framework))
                throw new Exception(
                    "nuke regen-apple-snapshot requires --framework <name>. " +
                    "Example: nuke regen-apple-snapshot --framework CryptoKit");
            ValidateAppleFrameworkName(Framework);

            var snapshotDir = BindingTestsDir / "obj" / $"{Framework}Snapshot";
            RegenerateAppleFrameworkSnapshot(Framework, snapshotDir, force: true);
        });

    /// <summary>
    /// Exposes <see cref="RegenerateStoreKit2Snapshot"/> as a manually invokable
    /// nuke target. StoreKit keeps its own top-level target (instead of asking
    /// contributors to remember the <c>--framework StoreKit</c> + custom dir
    /// combo) because <c>RuntimeTestsApp.csproj</c> hardcodes the
    /// <c>StoreKit2Snapshot/</c> path and the runtime test pipeline calls
    /// <see cref="RegenerateStoreKit2Snapshot"/> directly during
    /// <c>--enable-storekit-smoke</c> runs.
    /// </summary>
    Target RegenerateStoreKitSnapshot => _ => _
        .After(RegenerateAppleSnapshot, RuntimeTestsCatalyst)
        .Description("Regenerate the in-tree StoreKit 2 snapshot (BindingTests/obj/StoreKit2Snapshot/) from the active Xcode SDK.")
        .Executes(() => RegenerateStoreKit2Snapshot(force: true));

    /// <summary>
    /// Thin wrapper around <see cref="RegenerateAppleFrameworkSnapshot"/> that
    /// preserves StoreKit's non-canonical snapshot directory name
    /// (<c>StoreKit2Snapshot</c>, hardcoded in <c>RuntimeTestsApp.csproj</c>)
    /// while routing through the shared generalized path. All other frameworks
    /// should call <see cref="RegenerateAppleFrameworkSnapshot"/> directly or
    /// go through the <c>regen-apple-snapshot</c> nuke target.
    /// </summary>
    void RegenerateStoreKit2Snapshot(bool force)
    {
        RegenerateAppleFrameworkSnapshot("StoreKit", StoreKitSnapshotDir, force);
    }

    /// <summary>
    /// Regenerates an in-tree snapshot of the public ABI of a Swift-first
    /// Apple framework. Used by the per-framework smoke tests to produce a
    /// fresh set of C# bindings + Swift wrapper against the currently-selected
    /// Xcode SDK. Pipeline: (1) resolve the iphonesimulator SDK via xcrun,
    /// (2) locate the framework's swiftinterface and tbd under the SDK,
    /// (3) incremental freshness gate against the tooling fingerprint,
    /// (4) wipe the output dir, (5) dump ABI JSON via swift-api-digester,
    /// (6) invoke the generator in manual mode, (7) drop a snapshot-local
    /// Directory.Build.targets to disable TreatWarningsAsErrors for the
    /// Apple-SDK full-surface binding, (8) verify required outputs exist,
    /// (9) stamp the fingerprint file.
    /// </summary>
    /// <param name="frameworkName">
    /// Apple framework to regenerate (e.g. <c>StoreKit</c>, <c>CryptoKit</c>).
    /// Drives the digester module, the generator <c>-l @rpath</c> arg, the
    /// canonical swiftinterface/tbd lookup paths under the iPhoneSimulator
    /// SDK, and the expected generator output filenames.
    /// </param>
    /// <param name="snapshotDir">
    /// Output directory for the ABI JSON and all generator artifacts. Must be
    /// under <c>BindingTests/obj/</c> so the top-level <c>[Oo]bj/</c> gitignore
    /// rule keeps it out of the repo. The StoreKit wrapper above passes a
    /// non-canonical path (<c>StoreKit2Snapshot</c>) for backwards compatibility
    /// with <c>RuntimeTestsApp.csproj</c>; new frameworks use the canonical
    /// <c>&lt;Framework&gt;Snapshot</c> layout from the
    /// <see cref="RegenerateAppleSnapshot"/> target.
    /// </param>
    /// <param name="force">
    /// When <c>true</c>, always regenerate regardless of output staleness.
    /// When <c>false</c>, skip regeneration if every output file exists, the
    /// stamped fingerprint matches the current (generator + runtime + tooling)
    /// fingerprint, and every output file is at least as new as every Xcode
    /// SDK input file. This makes repeat smoke-enabled runs effectively free.
    /// </param>
    /// <remarks>
    /// Failure modes are loud: missing Xcode install throws pointing at
    /// <c>xcode-select --switch</c>; missing swiftinterface/tbd throws with the
    /// resolved SDK root; non-zero digester or generator exit throws. This is
    /// NOT the same permissive "generator may exit non-zero for unsupported
    /// features" path used by <see cref="RunRegenerateBindings"/> — Apple-SDK
    /// targets must always generate cleanly for the smoke tests to mean
    /// anything.
    /// </remarks>
    void RegenerateAppleFrameworkSnapshot(string frameworkName, AbsolutePath snapshotDir, bool force,
        ApplePlatform? platform = null)
    {
        platform ??= ApplePlatform.IOS;
        ValidateAppleFrameworkName(frameworkName);
        Log.Information("--- Regenerating {Framework} snapshot ({Platform}) ---", frameworkName, platform.Name);
        Log.Information("    Output: {Dir}", snapshotDir);

        var platformSuffix = platform.PackageSuffix; // "iOS", "macOS", etc.
        var abiJsonPath = snapshotDir / $"{frameworkName}.abi.json";
        var csprojPath = snapshotDir / $"{frameworkName}.Swift.{platformSuffix}.csproj";
        var projRefTargetsPath = snapshotDir / $"{frameworkName}.Swift.{platformSuffix}.ProjectReference.targets";
        var csFilePath = snapshotDir / $"{frameworkName}.cs";
        var wrapperSwiftPath = snapshotDir / $"{frameworkName}.Wrapper.swift";
        var fingerprintStampPath = snapshotDir / AppleSnapshotFingerprintStampName;

        // The backslash is the System.CommandLine escape for the response-file
        // prefix `@`. Without it, System.CommandLine interprets `@rpath/...`
        // as a path to a response file (and emits "Error reading response
        // file"). The backslash is stripped by the parser before the value
        // reaches BindingsGeneratorCommand, so IsSystemFrameworkTarget still
        // sees a plain "@rpath/<Framework>.framework/<Framework>" and enables
        // the csproj + wrapper compilation branch. Documented by the
        // generator's -l help text.
        var libraryNameArg = $@"\@rpath/{frameworkName}.framework/{frameworkName}";

        // Step A: resolve the SDK root via xcrun. We shell out instead of
        // hardcoding /Applications/Xcode.app because a contributor may have
        // xcode-select pointed at a non-default Xcode install.
        var sdkName = platform.SimulatorSdkName; // "iphonesimulator", "macosx", etc.
        var sdkPath = RunXcrunCapture($"--sdk {sdkName} --show-sdk-path");
        if (string.IsNullOrWhiteSpace(sdkPath))
        {
            throw new Exception(
                $"xcrun --sdk {sdkName} --show-sdk-path returned an empty path. " +
                "Is Xcode installed and selected via `xcode-select --switch`?");
        }

        var swiftinterfacePath = (AbsolutePath)sdkPath /
            "System" / "Library" / "Frameworks" / $"{frameworkName}.framework" /
            "Modules" / $"{frameworkName}.swiftmodule" /
            $"{platform.SimulatorModuleSuffix}.swiftinterface";
        var tbdPath = (AbsolutePath)sdkPath /
            "System" / "Library" / "Frameworks" / $"{frameworkName}.framework" / $"{frameworkName}.tbd";

        if (!File.Exists(swiftinterfacePath))
        {
            throw new Exception(
                $"{frameworkName} swiftinterface not found at {swiftinterfacePath}. " +
                $"SDK root was {sdkPath} (from xcrun --sdk {sdkName}). Check your Xcode install.");
        }
        if (!File.Exists(tbdPath))
        {
            throw new Exception(
                $"{frameworkName}.tbd not found at {tbdPath}. SDK root was {sdkPath}.");
        }

        // Directory.Build.targets is part of the snapshot contract (it turns off
        // TreatWarningsAsErrors for the Apple-SDK full-surface build) and is
        // written deterministically by this method on every regen. Include it in
        // the freshness gate so a hand-deletion invalidates the cache; otherwise
        // the next run would silently compile the snapshot against the repo-wide
        // warnings-as-errors rules and fail in a confusing way far from the
        // snapshot code.
        var directoryBuildTargetsPath = snapshotDir / "Directory.Build.targets";
        var requiredOutputs = new (string Label, AbsolutePath Path)[]
        {
            ("C# bindings", csFilePath),
            ("generated csproj", csprojPath),
            ("ProjectReference.targets", projRefTargetsPath),
            ("Swift wrapper source", wrapperSwiftPath),
            ("Directory.Build.targets", directoryBuildTargetsPath),
        };

        // Step B: incremental skip check. Both the ABI JSON (digester output)
        // and the generator outputs must exist AND be at least as new as the
        // Xcode SDK inputs. If any input is newer than any output, regenerate
        // everything — we don't bother trying to skip just one of the two
        // phases because the cost of a full regen is ~10s and the logic of
        // partial staleness is far more error-prone than the savings.
        if (!force && IsAppleFrameworkSnapshotFresh(
                requiredOutputs.Select(r => r.Path).Append(abiJsonPath).ToArray(),
                fingerprintStampPath,
                swiftinterfacePath, tbdPath))
        {
            Log.Information("    Snapshot is up to date relative to Xcode SDK inputs — skipping regeneration");
            return;
        }

        // Ensure a clean output directory. We deliberately wipe and recreate
        // rather than merging into existing output: the generator writes a
        // mix of .cs, .swift, .csproj, .targets, and .xcframework artifacts,
        // and a stale file from a previous generator version (e.g. a file the
        // generator no longer emits) would silently survive an in-place regen.
        if (Directory.Exists(snapshotDir))
            snapshotDir.DeleteDirectory();
        snapshotDir.CreateDirectory();

        // Step C: dump the framework ABI via swift-api-digester. The
        // -dump-sdk command emits a JSON description of the module's public
        // API surface that the generator consumes as its -a input in manual
        // mode.
        Log.Information("    Dumping {Framework} ABI via swift-api-digester ({Platform})", frameworkName, platform.Name);
        var digesterTarget = platform.SimulatorTarget; // e.g. "arm64-apple-ios15.0-simulator" or "arm64-apple-macos12.0"
        var digesterArgs = string.Join(" ", new[]
        {
            "swift-api-digester",
            "-dump-sdk",
            "-module", frameworkName,
            "-target", digesterTarget,
            "-sdk", $"\"{sdkPath}\"",
            "-o", $"\"{abiJsonPath}\"",
        });
        var digesterProc = ProcessTasks.StartProcess(
            "xcrun", digesterArgs,
            workingDirectory: snapshotDir,
            logOutput: true);
        digesterProc.WaitForExit();
        if (digesterProc.ExitCode != 0)
        {
            throw new Exception(
                $"swift-api-digester exited with code {digesterProc.ExitCode}. " +
                $"Arguments: {digesterArgs}");
        }
        if (!File.Exists(abiJsonPath))
        {
            throw new Exception(
                $"swift-api-digester exited successfully but did not produce {abiJsonPath}.");
        }

        var abiJsonSize = new FileInfo(abiJsonPath).Length;
        Log.Information("    ABI JSON: {Path} ({Size:N0} bytes)", abiJsonPath, abiJsonSize);

        // Step D: run the generator in manual mode against the digester dump.
        // The generator is shared with BindingTests, so EnsureGeneratorBuilt()
        // is a no-op when it's already compiled.
        EnsureGeneratorBuilt();

        Log.Information("    Running generator (manual mode, -a/-d/-t/-s)");
        var genArgsList = new List<string>
        {
            $"\"{GeneratorDll}\"",
            $"-a \"{abiJsonPath}\"",
            $"-d \"{tbdPath}\"",
            $"-t \"{tbdPath}\"",
            $"-s \"{swiftinterfacePath}\"",
            $"-l \"{libraryNameArg}\"",
            $"--platform {platform.Name}",
        };
        // macOS has no simulator/device distinction — omit --platform-target.
        // iOS/tvOS must specify "simulator" so the generator resolves the
        // correct xcframework slice and emits the right RID.
        if (platform.HasSimulatorPlistVariant)
            genArgsList.Add("--platform-target simulator");
        genArgsList.Add($"--swift-runtime-version {AppleSnapshotSwiftRuntimeVersion}");
        genArgsList.Add($"-o \"{snapshotDir}\"");
        var genArgs = string.Join(" ", genArgsList);
        var genProc = ProcessTasks.StartProcess(
            "dotnet", genArgs,
            workingDirectory: snapshotDir,
            logOutput: true);
        genProc.WaitForExit();
        if (genProc.ExitCode != 0)
        {
            throw new Exception(
                $"Generator exited with code {genProc.ExitCode} regenerating the {frameworkName} snapshot. " +
                $"Unlike BindingTests, Apple-framework targets must always generate cleanly — " +
                $"investigate the generator output above.");
        }

        // Step E (prep): drop a snapshot-local Directory.Build.targets that
        // disables TreatWarningsAsErrors for the snapshot csproj. The repo-wide
        // Directory.Build.props (/Directory.Build.props, root) turns warnings
        // into errors, which is the right default for first-party source but
        // the wrong default for a reflection of an Apple framework's full
        // public API surface. Binding every public entry point legitimately
        // surfaces CA1416 (platform availability), CA1422 (obsoleted APIs),
        // CS0436 (type collisions with Microsoft.iOS ObjC bindings), CS8604
        // (nullable-annotation noise in async completion bridges), and a long
        // tail of similar codes. None of those block the smoke tests — the
        // tests only dereference runtime-safe entry points — but under
        // warnings-as-errors they all become hard failures and turn the
        // snapshot build into a whack-a-mole session of NoWarn codes. Keeping
        // this OFF is the right scope: the snapshot is test scaffolding, not
        // a shipping package, so "any compile error ≠ warning" is the right
        // gate.
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
        File.WriteAllText(snapshotDir / "Directory.Build.targets",
            """
            <Project>
              <PropertyGroup>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <MSBuildTreatWarningsAsErrors>false</MSBuildTreatWarningsAsErrors>
              </PropertyGroup>
            </Project>
            """);

        // Step E: verify the key output files exist. These are the files that
        // RuntimeTestsApp.csproj will reference, so if the generator silently
        // produced a partial output (e.g. dropped the .ProjectReference.targets
        // file because of a regression) we want to know now, not at app-build
        // time with a confusing MSBuild error.
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
        File.WriteAllText(fingerprintStampPath, ComputeAppleSnapshotFingerprint());

        Log.Information("    {Framework} snapshot regenerated: {Dir}", frameworkName, snapshotDir);
    }

    /// <summary>
    /// Returns <c>true</c> when every required snapshot output file exists,
    /// the recorded snapshot fingerprint matches the current (generator +
    /// runtime + tooling) fingerprint, AND every output file is at least as
    /// new as every Xcode SDK input file. Any missing output, fingerprint
    /// drift, or newer input means "regenerate."
    /// </summary>
    /// <remarks>
    /// The mtime check by itself is not sufficient. A generator edit, a tweak
    /// to one of the snapshot constants, or a change to the inline
    /// <c>Directory.Build.targets</c> template would all leave the snapshot
    /// files older than the SDK inputs — and the freshness check would
    /// happily reuse the stale generated bindings against the new generator.
    /// The fingerprint stamp closes that hole by hashing the generator +
    /// runtime source tree (via <see cref="ComputeSourceFingerprint"/>) AND
    /// the snapshot tooling source itself (<c>Build.RuntimeTests.cs</c>), so
    /// any of those changes invalidates the snapshot.
    /// </remarks>
    bool IsAppleFrameworkSnapshotFresh(
        IReadOnlyList<AbsolutePath> requiredOutputs,
        AbsolutePath fingerprintStampPath,
        AbsolutePath swiftinterfacePath,
        AbsolutePath tbdPath)
    {
        foreach (var outputPath in requiredOutputs)
        {
            if (!File.Exists(outputPath))
                return false;
        }

        // Fingerprint gate: the stamp must exist AND match the current
        // generator/runtime/tooling fingerprint. A missing stamp indicates a
        // partially-completed previous regen; a mismatched stamp indicates a
        // generator or tooling change since the snapshot was last written.
        if (!File.Exists(fingerprintStampPath))
            return false;
        var recordedFingerprint = File.ReadAllText(fingerprintStampPath).Trim();
        var currentFingerprint = ComputeAppleSnapshotFingerprint();
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
    /// The latter covers the shared snapshot tooling: the digester args, the
    /// generator CLI flags, the inline <c>Directory.Build.targets</c>
    /// template, and the snapshot path constants — anything that, if changed,
    /// should force a regen of every in-tree Apple framework snapshot.
    /// </summary>
    string ComputeAppleSnapshotFingerprint()
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

            RejectSkipBuildWithActiveSmokeFlags();

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
                AssertBindingsNotStale(expectedPlatform: ApplePlatform.IOS);
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
                if (EnableCryptoKitSmoke)
                    RegenerateAppleFrameworkSnapshot("CryptoKit", CryptoKitSnapshotDir, force: false);
                if (EnableWeatherKitSmoke)
                    RegenerateAppleFrameworkSnapshot("WeatherKit", WeatherKitSnapshotDir, force: false);
                if (EnableTipKitSmoke)
                    RegenerateAppleFrameworkSnapshot("TipKit", TipKitSnapshotDir, force: false);
                if (EnableMusicKitSmoke)
                    RegenerateAppleFrameworkSnapshot("MusicKit", MusicKitSnapshotDir, force: false);
                if (EnableWorkoutKitSmoke)
                    RegenerateAppleFrameworkSnapshot("WorkoutKit", WorkoutKitSnapshotDir, force: false);
                if (EnableRoomPlanSmoke)
                    RegenerateAppleFrameworkSnapshot("RoomPlan", RoomPlanSnapshotDir, force: false);
                if (EnableProximityReaderSmoke)
                    RegenerateAppleFrameworkSnapshot("ProximityReader", ProximityReaderSnapshotDir, force: false);
                if (EnableLiveCommunicationKitSmoke)
                    RegenerateAppleFrameworkSnapshot("LiveCommunicationKit", LiveCommunicationKitSnapshotDir, force: false);

                Log.Information("--- Building RuntimeTestsApp ---");
                if (EnableStoreKitSmoke)
                    Log.Information("    StoreKit 2 smoke tests: ENABLED (--enable-storekit-smoke)");
                if (EnableCryptoKitSmoke)
                    Log.Information("    CryptoKit smoke tests: ENABLED (--enable-cryptokit-smoke)");
                if (EnableWeatherKitSmoke)
                    Log.Information("    WeatherKit smoke tests: ENABLED (--enable-weatherkit-smoke)");
                if (EnableTipKitSmoke)
                    Log.Information("    TipKit smoke tests: ENABLED (--enable-tipkit-smoke)");
                if (EnableMusicKitSmoke)
                    Log.Information("    MusicKit smoke tests: ENABLED (--enable-musickit-smoke)");
                if (EnableWorkoutKitSmoke)
                    Log.Information("    WorkoutKit smoke tests: ENABLED (--enable-workoutkit-smoke)");
                if (EnableRoomPlanSmoke)
                    Log.Information("    RoomPlan smoke tests: ENABLED (--enable-roomplan-smoke)");
                if (EnableProximityReaderSmoke)
                    Log.Information("    ProximityReader smoke tests: ENABLED (--enable-proximityreader-smoke)");
                if (EnableLiveCommunicationKitSmoke)
                    Log.Information("    LiveCommunicationKit smoke tests: ENABLED (--enable-livecommunicationkit-smoke)");
                DotNetBuild(s =>
                {
                    var built = s
                        .SetProjectFile(BindingTestsDir / "RuntimeTestsApp")
                        .SetConfiguration("Debug")
                        .SetVerbosity(DotNetVerbosity.quiet);
                    // Any smoke flag needs SwiftBindingsRepoRoot so the snapshot csproj
                    // resolves SwiftBindings.Runtime via the in-tree ProjectReference
                    // fallback instead of the [0.0.0-dev] sentinel PackageReference.
                    if (EnableStoreKitSmoke || EnableCryptoKitSmoke || EnableWeatherKitSmoke || EnableTipKitSmoke || EnableMusicKitSmoke || EnableWorkoutKitSmoke || EnableRoomPlanSmoke || EnableProximityReaderSmoke || EnableLiveCommunicationKitSmoke)
                        built = built.SetProperty("SwiftBindingsRepoRoot", RootDirectory.ToString());
                    if (EnableCryptoKitSmoke)
                        built = built.SetProperty("EnableCryptoKitSmoke", "true");
                    if (EnableWeatherKitSmoke)
                        built = built.SetProperty("EnableWeatherKitSmoke", "true");
                    if (EnableTipKitSmoke)
                        built = built.SetProperty("EnableTipKitSmoke", "true");
                    if (EnableMusicKitSmoke)
                        built = built.SetProperty("EnableMusicKitSmoke", "true");
                    if (EnableWorkoutKitSmoke)
                        built = built.SetProperty("EnableWorkoutKitSmoke", "true");
                    if (EnableRoomPlanSmoke)
                        built = built.SetProperty("EnableRoomPlanSmoke", "true");
                    if (EnableProximityReaderSmoke)
                        built = built.SetProperty("EnableProximityReaderSmoke", "true");
                    if (EnableLiveCommunicationKitSmoke)
                        built = built.SetProperty("EnableLiveCommunicationKitSmoke", "true");
                    // SwiftBindingsRepoRoot above is what lets the generator-emitted
                    // <Framework>.Swift.iOS.csproj resolve SwiftBindings.Runtime via the
                    // in-tree ProjectReference fallback (see the snapshot csproj's
                    // Condition="'$(SwiftBindingsRepoRoot)' != ''" ProjectReference line).
                    // Without it, the csproj falls through to the `[0.0.0-dev]` sentinel
                    // PackageReference which has no matching NuGet and fails with NU1102.
                    // IncludeSwiftBindingsRuntimeNative=false is not forced here as a
                    // global property — the snapshot ProjectReferences in RuntimeTestsApp.csproj
                    // set it via <AdditionalProperties> so it cascades through to the
                    // transitive Swift.Runtime ProjectReference without polluting the
                    // non-smoke build's global property bag.
                    if (EnableStoreKitSmoke)
                        built = built.SetProperty("EnableStoreKitSmoke", "true");
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

            RejectSkipBuildWithActiveSmokeFlags();

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
                AssertBindingsNotStale(expectedPlatform: ApplePlatform.IOS);
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

            RejectSkipBuildWithActiveSmokeFlags();

            // macOS only supports CryptoKit and WeatherKit smokes — reject everything else.
            var unsupported = GetActiveSmokeFlags()
                .Where(f => f.Define is not ("CRYPTOKIT_SMOKE" or "WEATHERKIT_SMOKE"))
                .ToList();
            if (unsupported.Count > 0)
            {
                var names = string.Join(", ", unsupported.Select(f => f.FlagName));
                throw new Exception(
                    $"{names}: smoke flags are not supported by runtime-tests-macos. " +
                    "Only --enable-cryptokit-smoke and --enable-weatherkit-smoke are wired for macOS. " +
                    "Drop the flag and rerun, or use runtime-tests-simulator.");
            }

            var platform = ApplePlatform.MacOS;

            if (!EffectiveSkipRegen)
            {
                RunBuildXcframework(platformOverride: platform);
                RunRegenerateMacOSBindings();
                RunBuildAsyncWrapper(platformOverride: platform);
            }
            else
            {
                AssertBindingsNotStale(expectedPlatform: platform);
            }

            if (!SkipBuild)
            {
                // Regenerate Apple-framework macOS snapshots before building.
                // Same pattern as iOS (RuntimeTestsSimulator) — the regen is
                // gated on the same Enable*Smoke CLI parameters and uses the
                // macOS-specific snapshot directories that coexist alongside
                // the iOS snapshots under BindingTests/obj/.
                if (EnableCryptoKitSmoke)
                    RegenerateAppleFrameworkSnapshot("CryptoKit", CryptoKitMacOSSnapshotDir, force: false, ApplePlatform.MacOS);
                if (EnableWeatherKitSmoke)
                    RegenerateAppleFrameworkSnapshot("WeatherKit", WeatherKitMacOSSnapshotDir, force: false, ApplePlatform.MacOS);

                Log.Information("--- Building RuntimeTestsApp.Mac ---");
                if (EnableCryptoKitSmoke)
                    Log.Information("    CryptoKit smoke tests: ENABLED (--enable-cryptokit-smoke)");
                if (EnableWeatherKitSmoke)
                    Log.Information("    WeatherKit smoke tests: ENABLED (--enable-weatherkit-smoke)");

                // Clean previous app bundle to avoid codesign "unsealed contents"
                // errors from previously injected dylibs.
                var macBuildDir = BindingTestsDir / "RuntimeTestsApp.Mac" / "bin" / "Debug" /
                    $"{DotNetTfm}-macos" / "osx-arm64";
                var appBundle = macBuildDir / "RuntimeTestsApp.Mac.app";
                if (Directory.Exists(appBundle))
                {
                    appBundle.DeleteDirectory();
                    Log.Information("Cleaned previous app bundle.");
                }

                DotNetBuild(s =>
                {
                    var built = s
                        .SetProjectFile(BindingTestsDir / "RuntimeTestsApp.Mac")
                        .SetConfiguration("Debug")
                        .SetVerbosity(DotNetVerbosity.quiet);
                    if (EnableCryptoKitSmoke || EnableWeatherKitSmoke)
                        built = built.SetProperty("SwiftBindingsRepoRoot", RootDirectory.ToString());
                    if (EnableCryptoKitSmoke)
                        built = built.SetProperty("EnableCryptoKitSmoke", "true");
                    if (EnableWeatherKitSmoke)
                        built = built.SetProperty("EnableWeatherKitSmoke", "true");
                    return built;
                });

                if (!Directory.Exists(appBundle))
                    throw new Exception($"Build failed - macOS app bundle not found at {appBundle}");

                // Inject native libs that NativeReference doesn't cover.
                var monoBundle = appBundle / "Contents" / "MonoBundle";
                InjectMacOSNativeLibraries(monoBundle);

                // Re-sign the .app bundle after dylib injection. The build
                // produces a linker-signed binary, but injecting dylibs into
                // MonoBundle/Frameworks invalidates the sealed-resource hash.
                // macOS kills unsigned/invalid bundles with SIGKILL on Apple
                // Silicon. Bottom-up signing: dylibs → frameworks → exe → bundle.
                CodesignMacOSApp(appBundle);

                Log.Information("Build successful.");
            }

            // Run natively on macOS via the .app bundle's native executable
            RunOnMacOS();
        });

    // ============================================================
    // RuntimeTestsCatalyst — Mac Catalyst runner
    //
    // Mirror of RuntimeTestsMacOS but targets net10.0-maccatalyst. Catalyst
    // apps produce macOS .app bundles and run directly on the host — same
    // deployment mechanism as macOS. The xcframework uses the
    // ios-arm64-maccatalyst slice (macOS SDK, -macabi target triple).
    //
    // No smoke wiring: Catalyst shares the same test matrix as macOS and
    // the primary goal is verifying the Catalyst binding/runtime path works.
    // ============================================================

    Target RuntimeTestsCatalyst => _ => _
        .After(Clean, RuntimeTestsMacOS, BindingTestsStrict, SmokeTest)
        .Executes(() =>
        {
            Log.Information("=========================================");
            Log.Information(" BindingTests Runtime Tests (Mac Catalyst)");
            Log.Information("=========================================");

            // Catalyst has no smoke wiring — reject any active smoke flags.
            var activeSmoke = GetActiveSmokeFlags();
            if (activeSmoke.Count > 0)
            {
                var names = string.Join(", ", activeSmoke.Select(f => f.FlagName));
                throw new Exception(
                    $"{names}: smoke flags are not supported by runtime-tests-catalyst. " +
                    "Per-framework smoke wiring is not implemented for Catalyst. " +
                    "Drop the flag and rerun, or use runtime-tests-simulator.");
            }
            RejectSkipBuildWithActiveSmokeFlags();

            var platform = ApplePlatform.MacCatalyst;

            if (!EffectiveSkipRegen)
            {
                RunBuildXcframework(platformOverride: platform);
                RunRegenerateMacOSBindings(platformOverride: platform);
                RunBuildAsyncWrapper(platformOverride: platform);
            }
            else
            {
                AssertBindingsNotStale(expectedPlatform: platform);
            }

            if (!SkipBuild)
            {
                Log.Information("--- Building RuntimeTestsApp.MacCatalyst ---");

                // Clean previous app bundle to avoid codesign "unsealed contents"
                // errors from previously injected dylibs.
                var catalystBuildDir = BindingTestsDir / "RuntimeTestsApp.MacCatalyst" / "bin" / "Debug" /
                    $"{DotNetTfm}-maccatalyst" / "maccatalyst-arm64";
                var appBundle = catalystBuildDir / "RuntimeTestsApp.MacCatalyst.app";
                if (Directory.Exists(appBundle))
                {
                    appBundle.DeleteDirectory();
                    Log.Information("Cleaned previous app bundle.");
                }

                DotNetBuild(s => s
                    .SetProjectFile(BindingTestsDir / "RuntimeTestsApp.MacCatalyst")
                    .SetConfiguration("Debug")
                    .SetVerbosity(DotNetVerbosity.quiet));

                if (!Directory.Exists(appBundle))
                    throw new Exception($"Build failed - Catalyst app bundle not found at {appBundle}");

                // Inject native libs that NativeReference doesn't cover.
                var monoBundle = appBundle / "Contents" / "MonoBundle";
                InjectCatalystNativeLibraries(monoBundle);

                // Re-sign the .app bundle after dylib injection.
                CodesignMacOSApp(appBundle, exeNameOverride: "RuntimeTestsApp.MacCatalyst");

                Log.Information("Build successful.");
            }

            // Run natively on macOS via the .app bundle's native executable
            RunOnCatalyst();
        });

    // ============================================================
    // RuntimeTestsTvOSSimulator — NO DependsOn, manages pipeline internally
    //
    // Mirror of RuntimeTestsSimulator but targets the tvOS simulator. Shares
    // the same binding output directory, xcframework, and runtime-test sources
    // as the iOS target; the iOS and tvOS regen paths clobber each other's
    // xcframeworks in BindingTests/.build/ by design — each target rebuilds
    // from scratch. We intentionally do NOT share state across targets, and
    // smoke flags are rejected up front because there is no tvOS snapshot
    // wiring.
    //
    // Per the design doc: no tvOS device runner (NativeAOT), no per-framework
    // smoke gating. The tvOS csproj excludes SmokeTests/ at the Compile-item
    // level for the same reason.
    // ============================================================

    Target RuntimeTestsTvOSSimulator => _ => _
        .After(Clean, RuntimeTestsMacOS, RuntimeTestsCatalyst, BindingTestsStrict, RegenerateStoreKitSnapshot)
        .Executes(() =>
        {
            Log.Information("=========================================");
            Log.Information(" BindingTests Runtime Tests (tvOS Simulator)");
            Log.Information("=========================================");
            Log.Information("Skip regeneration: {SkipRegen}", EffectiveSkipRegen);
            Log.Information("Skip build: {SkipBuild}", SkipBuild);
            Log.Information("Timeout: {Timeout}s", Timeout);
            if (!string.IsNullOrEmpty(ClassFilter))
                Log.Information("Class filter: {ClassFilter}", ClassFilter);
            if (FlakeDetect)
                Log.Information("Flake detection: enabled");

            // tvOS has no smoke wiring today — any active smoke flag is a
            // configuration error, not something to quietly ignore.
            var activeSmoke = GetActiveSmokeFlags();
            if (activeSmoke.Count > 0)
            {
                var names = string.Join(", ", activeSmoke.Select(f => f.FlagName));
                throw new Exception(
                    $"{names}: smoke flags are not supported by runtime-tests-tvos-simulator. " +
                    "Per-framework smoke wiring lives on the iOS simulator runner only. " +
                    "Drop the flag and rerun, or use runtime-tests-simulator.");
            }
            RejectSkipBuildWithActiveSmokeFlags();

            var platform = ApplePlatform.TvOS;

            // Step 1: Conditionally run binding pipeline (tvOS-flavored).
            // RunRegenerateBindings needs the platform so the generator emits
            // tvOS-correct availability attributes and filters out bindings whose
            // Swift types don't exist on tvOS (e.g. AuthenticationServices.
            // ASAuthorizationPublicKeyCredentialParameters is iOS/macOS only).
            //
            // We deliberately skip RunCompileCheck() here: CompileCheck targets
            // net10.0-ios, so invoking it on tvOS-regenerated output is a TFM
            // mismatch. The dotnet build of RuntimeTestsApp.tvOS below is the
            // real compile gate for the tvOS output.
            if (!EffectiveSkipRegen)
            {
                RunBuildXcframework(platformOverride: platform);
                RunRegenerateBindings(strict: false, platformOverride: platform);
                RunBuildAsyncWrapper(platformOverride: platform);
                RunBuildBridge(platformOverride: platform);
            }
            else
            {
                AssertBindingsNotStale(expectedPlatform: platform);
            }

            // Step 2: Build RuntimeTestsApp.tvOS (unless --skip-build)
            if (!SkipBuild)
            {
                Log.Information("--- Building RuntimeTestsApp.tvOS ---");
                DotNetBuild(s => s
                    .SetProjectFile(BindingTestsDir / "RuntimeTestsApp.tvOS")
                    .SetConfiguration("Debug")
                    .SetVerbosity(DotNetVerbosity.quiet));

                var appFrameworks = BindingTestsDir / "RuntimeTestsApp.tvOS" / "bin" / "Debug" /
                    $"{DotNetTfm}-tvos" / "tvossimulator-arm64" / "RuntimeTestsApp.tvOS.app" / "Frameworks";

                if (!Directory.Exists(BindingTestsDir / "RuntimeTestsApp.tvOS" / "bin" / "Debug" /
                    $"{DotNetTfm}-tvos" / "tvossimulator-arm64" / "RuntimeTestsApp.tvOS.app"))
                    throw new Exception("Build failed - tvOS app bundle not found");

                Log.Information("Build successful.");

                // Inject all 4 native artifacts into app bundle Frameworks/
                InjectRuntimeDylib(appFrameworks, nativeSubdir: "tvossimulator");
                InjectAsyncWrapper(appFrameworks, platformOverride: platform);
                InjectDependencyFramework(appFrameworks, platformOverride: platform);
                InjectDependencyWrapper(appFrameworks, platformOverride: platform);
            }
            else
            {
                Log.Information("--- Steps 1-2: Skipped (--skip-build) ---");
            }

            // Step 3: Install + run on tvOS simulator
            RunOnTvOSSimulator();
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
                args.ToArray(), TimeSpan.FromSeconds(Timeout),
                appName: "RuntimeTestsApp");
            lastResult = result;

            // Show output
            Log.Information("");
            Log.Information("=== APP OUTPUT ===");
            Log.Information(result.Output);

            // Crash diagnostics
            HandleCrashDiagnostics(result, device.Udid, crashLogsBefore, appName: "RuntimeTestsApp");

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
    // Shared Helpers: tvOS Simulator Execution
    //
    // Thin mirror of RunOnSimulator. The tvOS runner has no interactive UI
    // and no resume-on-crash loop — the test surface is smaller (no smoke
    // tests, UIKit-only regressions already excluded at compile time), and
    // every runtime crash on tvOS is just as "our bug" as on iOS, so the
    // simpler one-shot flow keeps the new target from becoming a second
    // place where crash-recovery plumbing has to evolve.
    // ============================================================

    void RunOnTvOSSimulator()
    {
        Log.Information("--- Running on tvOS Simulator ---");
        Log.Information("tvOS runner is single-shot by design: no resume-on-crash. " +
            "A crashing test class will prevent later classes from running — fix the " +
            "crash, don't add a retry loop.");

        SimCtl.SimDevice device;
        if (!string.IsNullOrEmpty(DeviceUdid))
        {
            // Loud-fail on family mismatch: a caller passing an iOS UDID here
            // would otherwise hit a confusing install-time error. Validate
            // against simctl's own device family listing before touching install.
            var tvDevices = SimCtl.ListDevices(SimCtl.TvOSAppleTVFamily.RuntimeFilter);
            if (!tvDevices.Any(d => string.Equals(d.Udid, DeviceUdid, StringComparison.OrdinalIgnoreCase)))
            {
                throw new Exception(
                    $"--device-udid {DeviceUdid} is not a tvOS simulator. " +
                    $"runtime-tests-tvos-simulator only accepts tvOS devices; " +
                    $"drop the flag or pass a tvOS UDID from `xcrun simctl list devices tvOS`.");
            }
            device = new SimCtl.SimDevice(DeviceUdid, "pre-booted", "Booted", true, "");
        }
        else
        {
            device = SimCtl.EnsureBootedDevice(SimCtl.TvOSAppleTVFamily);
        }
        Log.Information("Using simulator: {Name} ({Udid})", device.Name, device.Udid);

        var appPath = BindingTestsDir / "RuntimeTestsApp.tvOS" / "bin" / "Debug" /
            $"{DotNetTfm}-tvos" / "tvossimulator-arm64" / "RuntimeTestsApp.tvOS.app";

        var crashLogsBefore = SimCtl.CountCrashLogs("RuntimeTestsApp.tvOS");

        SimCtl.Install(device.Udid, appPath);

        var args = new List<string> { "--platform", "simulator" };
        if (FlakeDetect) args.AddRange(["--flake-detect"]);
        if (!string.IsNullOrEmpty(ClassFilter)) args.AddRange(["--class", ClassFilter]);

        Log.Information("Launching app (timeout: {Timeout}s)...", Timeout);
        var result = SimCtl.Launch(
            device.Udid, RuntimeTestsBundleId,
            args.ToArray(), TimeSpan.FromSeconds(Timeout),
            appName: "RuntimeTestsApp.tvOS");

        Log.Information("");
        Log.Information("=== APP OUTPUT ===");
        Log.Information(result.Output);

        HandleCrashDiagnostics(result, device.Udid, crashLogsBefore, appName: "RuntimeTestsApp.tvOS");

        // Try to retrieve JSONL results from sandbox
        JsonlTestResults? jsonlResults = null;
        var jsonlContent = SimCtl.CopyResultsFromSandbox(device.Udid, RuntimeTestsBundleId);
        if (jsonlContent != null)
        {
            jsonlResults = JsonlTestResults.Parse(jsonlContent);
            Log.Information("JSONL results: {Summary}", jsonlResults.ToString());
            File.WriteAllText("/tmp/runtime-tests-tvos.jsonl", jsonlContent);
        }
        else
        {
            Log.Debug("JSONL retrieval failed");
        }

        ReportRuntimeTestResult(result, "tvOS Simulator", jsonlResults);
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

        // macOS uses --platform simulator (Mono JIT mode, same as simulator).
        // Pass --results-path so JSONL is written outside the .app bundle
        // (writing inside would invalidate the code signature seal).
        var macResultsDir = (AbsolutePath)Path.GetTempPath() / "swift-bindings-macos-results";
        Directory.CreateDirectory(macResultsDir);
        var launchArgs = $"--platform simulator --results-path \"{macResultsDir}\"";
        if (FlakeDetect) launchArgs += " --flake-detect";
        if (!string.IsNullOrEmpty(ClassFilter)) launchArgs += $" --class {ClassFilter}";

        Log.Information("Launching RuntimeTestsApp.Mac (timeout: {Timeout}s)...", Timeout);

        var output = new ConcurrentQueue<string>();
        using var process = new Process();
        // net10.0-macos produces a .app bundle — launch the native executable
        // directly instead of `dotnet run`.
        var macExe = BindingTestsDir / "RuntimeTestsApp.Mac" / "bin" / "Debug" /
            $"{DotNetTfm}-macos" / "osx-arm64" / "RuntimeTestsApp.Mac.app" /
            "Contents" / "MacOS" / "RuntimeTestsApp.Mac";
        process.StartInfo = new ProcessStartInfo
        {
            FileName = macExe,
            Arguments = launchArgs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
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

        // JSONL is written to --results-path (temp dir outside the .app bundle).
        JsonlTestResults? jsonlResults = null;
        var macJsonlPath = macResultsDir / "test-results.jsonl";
        if (File.Exists(macJsonlPath))
        {
            jsonlResults = JsonlTestResults.ParseFile(macJsonlPath);
            Log.Information("JSONL results: {Summary}", jsonlResults.ToString());
        }

        ReportRuntimeTestResult(result, "macOS", jsonlResults);
    }

    void RunOnCatalyst()
    {
        Log.Information("--- Running on Mac Catalyst ---");

        // Catalyst uses --platform simulator (Mono JIT mode, same as macOS).
        // Pass --results-path so JSONL is written outside the .app bundle.
        var catalystResultsDir = (AbsolutePath)Path.GetTempPath() / "swift-bindings-catalyst-results";
        Directory.CreateDirectory(catalystResultsDir);
        // Remove stale JSONL from previous runs so a crash doesn't report old results.
        var staleJsonl = catalystResultsDir / "test-results.jsonl";
        if (File.Exists(staleJsonl)) File.Delete(staleJsonl);
        var launchArgs = $"--platform simulator --results-path \"{catalystResultsDir}\"";
        if (FlakeDetect) launchArgs += " --flake-detect";
        if (!string.IsNullOrEmpty(ClassFilter)) launchArgs += $" --class {ClassFilter}";

        Log.Information("Launching RuntimeTestsApp.MacCatalyst (timeout: {Timeout}s)...", Timeout);

        var output = new ConcurrentQueue<string>();
        using var process = new Process();
        // net10.0-maccatalyst produces a .app bundle — launch the native executable
        // directly instead of `dotnet run`.
        var catalystExe = BindingTestsDir / "RuntimeTestsApp.MacCatalyst" / "bin" / "Debug" /
            $"{DotNetTfm}-maccatalyst" / "maccatalyst-arm64" / "RuntimeTestsApp.MacCatalyst.app" /
            "Contents" / "MacOS" / "RuntimeTestsApp.MacCatalyst";
        process.StartInfo = new ProcessStartInfo
        {
            FileName = catalystExe,
            Arguments = launchArgs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
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

        // JSONL is written to --results-path (temp dir outside the .app bundle).
        JsonlTestResults? jsonlResults = null;
        var catalystJsonlPath = catalystResultsDir / "test-results.jsonl";
        if (File.Exists(catalystJsonlPath))
        {
            jsonlResults = JsonlTestResults.ParseFile(catalystJsonlPath);
            Log.Information("JSONL results: {Summary}", jsonlResults.ToString());
        }

        ReportRuntimeTestResult(result, "Mac Catalyst", jsonlResults);
    }

    // ============================================================
    // Crash Diagnostics
    // ============================================================

    // appName identifies which app's crash logs to look at. CountCrashLogs and
    // FindLatestCrashLog glob `{appName}*.ips`, which is a prefix match — so
    // "RuntimeTestsApp" cross-contaminates with "RuntimeTestsApp.tvOS". Callers
    // MUST pass the exact basename of the app they launched.
    void HandleCrashDiagnostics(LaunchResult result, string simulatorUdid, int crashLogsBefore, string appName)
    {
        if (result.Result is not (TestResult.Crash or TestResult.LaunchFailure or TestResult.Timeout))
            return;

        // Check crash log count delta
        var crashLogsAfter = SimCtl.CountCrashLogs(appName);
        if (crashLogsAfter > crashLogsBefore)
        {
            var crashLog = SimCtl.FindLatestCrashLog(appName);
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
        var deviceLog = SimCtl.ReadLog(simulatorUdid, TimeSpan.FromMinutes(3), appName);
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
                Log.Warning("=== DEVICE LOG (last 3 min, {AppName}) ===", appName);
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

    void AssertBindingsNotStale(AbsolutePath? outputDirOverride = null, ApplePlatform? expectedPlatform = null)
    {
        var outputDir = outputDirOverride ?? BtOutputDir;
        var bindingsFile = outputDir / $"{ModuleName}.cs";

        if (!File.Exists(bindingsFile))
            throw new InvalidOperationException(
                $"Bindings not found at {bindingsFile}. Run without --skip-regen first.");

        // Reject --skip-regen when the set of Enable<Framework>Smoke flags
        // has changed since the last regeneration. The Swift xcframework was
        // compiled with a specific set of `-D FOO_SMOKE` defines, and reusing
        // those artifacts against a different flag set would either miss the
        // smoke fixture entirely (flag added since last regen) or carry it
        // when the caller no longer wants it (flag removed). Same loud-failure
        // principle as the snapshot freshness fingerprint: the user rerun
        // without `--skip-regen` so the full pipeline sees the new flag set.
        var sidecar = outputDir / SmokeFlagsSidecarName;
        var currentFlags = FormatSmokeFlagsForSidecar(GetActiveSmokeFlags());
        var stampedFlags = File.Exists(sidecar) ? File.ReadAllText(sidecar) : string.Empty;
        if (stampedFlags != currentFlags)
        {
            throw new InvalidOperationException(
                "The smoke flag set has changed since bindings were last regenerated; " +
                $"rerun without --skip-regen. Stamped={FormatSidecarForMessage(stampedFlags)}, " +
                $"current={FormatSidecarForMessage(currentFlags)}. Sidecar: {sidecar}.");
        }

        // Same loud-failure invariant for --platform: if the caller is asking
        // for tvOS bindings but the last regen stamped "ios", the generated
        // output is platform-wrong and --skip-regen must be rejected. Missing
        // sidecar is treated as "ios" for back-compat with bindings produced
        // before this sidecar existed.
        if (expectedPlatform != null)
        {
            var platformSidecar = outputDir / TargetPlatformSidecarName;
            var stampedPlatform = File.Exists(platformSidecar)
                ? File.ReadAllText(platformSidecar).Trim()
                : "ios";
            if (stampedPlatform != expectedPlatform.Name)
            {
                throw new InvalidOperationException(
                    "The target platform has changed since bindings were last regenerated; " +
                    $"rerun without --skip-regen. Stamped={stampedPlatform}, " +
                    $"current={expectedPlatform.Name}. Sidecar: {platformSidecar}.");
            }
        }

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

    static string FormatSidecarForMessage(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "(none)";
        return raw.Replace("\n", ",");
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
    /// Defaults to the iOS simulator runtime; pass "tvossimulator" for tvOS.
    /// </summary>
    void InjectRuntimeDylib(AbsolutePath appFrameworks, string nativeSubdir = "iossimulator")
    {
        var runtimeDylib = RootDirectory / "src" / "Swift.Runtime" / "native" / nativeSubdir /
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
    void InjectAsyncWrapper(AbsolutePath appFrameworks, ApplePlatform? platformOverride = null)
    {
        var platform = platformOverride ?? ResolvedPlatform;
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
    void InjectDependencyFramework(AbsolutePath appFrameworks, ApplePlatform? platformOverride = null)
    {
        var platform = platformOverride ?? ResolvedPlatform;
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
    void InjectDependencyWrapper(AbsolutePath appFrameworks, ApplePlatform? platformOverride = null)
    {
        var platform = platformOverride ?? ResolvedPlatform;
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
    // macOS Binding Generation
    // ============================================================

    /// <summary>
    /// Generates macOS bindings. Uses the shared output dir (BtOutputDir) but
    /// skips --async-library because the generator's async wrapper separation
    /// doesn't work with --platform macos. Async wrappers are compiled from the
    /// inline Swift wrapper files by RunBuildAsyncWrapper.
    /// Also generates dependency module bindings (unlike the original version).
    /// </summary>
    void RunRegenerateMacOSBindings(ApplePlatform? platformOverride = null)
    {
        var platform = platformOverride ?? ApplePlatform.MacOS;
        Log.Information("=== Generating {Platform} bindings for {Module} ===", platform.Name, ModuleName);

        EnsureGeneratorBuilt();

        if (Directory.Exists(BtOutputDir))
            ((AbsolutePath)BtOutputDir).DeleteDirectory();
        BtOutputDir.CreateDirectory();

        var genArgs = new List<string>
        {
            $"\"{GeneratorDll}\"",
            $"--xcframework \"{BtXcframeworkDir}\"",
            $"--platform {platform.Name}",
            $"-o \"{BtOutputDir}\"",
        };

        // Note: --async-library, --framework-dependency, and --symbolgraph are
        // intentionally not passed for macOS/Catalyst. All three cause the
        // generator to produce no C# output when combined with desktop-class
        // platforms (generator limitation). The wrapper compilation (which needs
        // dep search paths) is handled separately by RunBuildAsyncWrapper.
        //
        // Without --async-library, the generator uses the default wrapper
        // library name "{Module}SwiftBindings" instead of the WrapperModule
        // constant ("SwiftBindings"). We post-process the generated C# to
        // fix this so DllImport/LibraryImport names match the compiled wrapper.
        //
        // Without --framework-dependency, cross-module APIs (e.g. functions
        // accepting dependency module types) are emitted as unsupported
        // placeholders. This is acceptable — desktop cross-module coverage is
        // not a priority and the runner doesn't include CrossModule/ tests.

        var genProcess = ProcessTasks.StartProcess(
            "dotnet", string.Join(" ", genArgs),
            workingDirectory: BindingTestsDir,
            logOutput: false);
        genProcess.WaitForExit();
        var exitCode = genProcess.ExitCode;

        File.WriteAllText(BtOutputDir / "generator-exit-code", exitCode.ToString());

        if (exitCode != 0)
            Log.Warning("{Platform} binding generation exited with code {ExitCode} (non-fatal)", platform.Name, exitCode);

        // Fix wrapper library name: without --async-library, the generator
        // defaults to "{Module}SwiftBindings" but RunBuildAsyncWrapper compiles
        // the wrapper as WrapperModule ("SwiftBindings").
        var defaultWrapperName = $"{ModuleName}{WrapperModule}";
        foreach (var csFile in Directory.GetFiles(BtOutputDir, "*.cs"))
        {
            var content = File.ReadAllText(csFile);
            if (content.Contains(defaultWrapperName))
            {
                content = content.Replace(
                    $"\"{defaultWrapperName}\"",
                    $"\"{WrapperModule}\"");
                File.WriteAllText(csFile, content);
            }
        }

        // Generate dependency module bindings
        if (Directory.Exists(BtDepXcframeworkDir))
        {
            Log.Information("=== Generating dependency bindings for {Module} ===", DepModuleName);
            var depOutputDir = BtOutputDir / "dep";
            depOutputDir.CreateDirectory();

            var depArgs = new List<string>
            {
                $"\"{GeneratorDll}\"",
                $"--xcframework \"{BtDepXcframeworkDir}\"",
                $"--platform {platform.Name}",
                $"-o \"{depOutputDir}\"",
            };

            var depProcess = ProcessTasks.StartProcess(
                "dotnet", string.Join(" ", depArgs),
                workingDirectory: BindingTestsDir,
                logOutput: false);
            depProcess.WaitForExit();

            if (depProcess.ExitCode != 0)
                Log.Warning("Dependency binding generation exited with code {ExitCode} (non-fatal)", depProcess.ExitCode);

            // Consolidate dependency CS files to root output dir
            foreach (var csFile in Directory.GetFiles(depOutputDir, "*.cs"))
            {
                var dest = BtOutputDir / Path.GetFileName(csFile);
                File.Copy(csFile, dest, overwrite: true);
            }

            // Consolidate dependency Swift wrappers to dep-swift/ for RunBuildAsyncWrapper
            var depSwiftDir = BtOutputDir / "dep-swift";
            depSwiftDir.CreateDirectory();
            foreach (var swiftFile in Directory.GetFiles(depOutputDir, "*.swift"))
            {
                var dest = depSwiftDir / Path.GetFileName(swiftFile);
                File.Copy(swiftFile, dest, overwrite: true);
            }

            // Consolidate dependency wrapper xcframework
            foreach (var dir in Directory.GetDirectories(depOutputDir, "*.xcframework"))
            {
                var destDir = BtOutputDir / Path.GetFileName(dir);
                if (Directory.Exists(destDir))
                    ((AbsolutePath)destDir).DeleteDirectory();
                Directory.Move(dir, destDir);
            }
        }

        var csCount = Directory.GetFiles(BtOutputDir, "*.cs", SearchOption.AllDirectories).Length;
        var swiftCount = Directory.GetFiles(BtOutputDir, "*.swift", SearchOption.AllDirectories).Length;
        Log.Information("Generated ({Platform}): {CsCount} C# files, {SwiftCount} Swift wrapper files", platform.Name, csCount, swiftCount);

        StampSmokeFlagsSidecar(BtOutputDir);
        StampTargetPlatformSidecar(BtOutputDir, platform);
    }

    // ============================================================
    // Native Artifact Injection (macOS)
    // ============================================================

    /// <summary>
    /// Injects native libraries into the macOS app output.
    /// With net10.0-macos, NativeReference in the csproj handles xcframeworks
    /// (SwiftBindingsTestLib, dependency, async wrapper). This function injects
    /// the runtime dylib and dependency wrapper which don't have NativeReference.
    /// </summary>
    void InjectMacOSNativeLibraries(AbsolutePath outputBin)
    {
        InjectRuntimeDylib(outputBin, nativeSubdir: "macos");
        InjectDependencyWrapper(outputBin, platformOverride: ApplePlatform.MacOS);
    }

    /// <summary>
    /// Injects native libraries into the Mac Catalyst app output.
    /// Same pattern as macOS: NativeReference handles xcframeworks, this injects
    /// the runtime dylib and dependency wrapper.
    /// </summary>
    void InjectCatalystNativeLibraries(AbsolutePath outputBin)
    {
        InjectRuntimeDylib(outputBin, nativeSubdir: "maccatalyst");
        InjectDependencyWrapper(outputBin, platformOverride: ApplePlatform.MacCatalyst);
    }

    // ============================================================
    // macOS Code Signing
    // ============================================================

    /// <summary>
    /// Re-signs the macOS .app bundle after native library injection.
    /// net10.0-macos builds produce a linker-signed binary, but injecting
    /// dylibs post-build invalidates the sealed-resource hashes. macOS on
    /// Apple Silicon kills binaries with invalid signatures (SIGKILL / exit 137).
    /// Signs bottom-up: dylibs → frameworks → main exe → bundle.
    /// </summary>
    void CodesignMacOSApp(AbsolutePath appBundle, string? exeNameOverride = null)
    {
        Log.Information("Re-signing .app bundle after native library injection...");

        // Sign all dylibs in MonoBundle
        var monoBundle = appBundle / "Contents" / "MonoBundle";
        foreach (var dylib in Directory.GetFiles(monoBundle, "*.dylib"))
        {
            ProcessTasks.StartProcess("codesign", $"--force -s - \"{dylib}\"")
                .AssertZeroExitCode();
        }

        // Sign frameworks injected into MonoBundle (e.g. dependency wrapper
        // framework placed there by InjectDependencyWrapper).
        foreach (var fw in Directory.GetDirectories(monoBundle, "*.framework"))
        {
            ProcessTasks.StartProcess("codesign", $"--force -s - \"{fw}\"")
                .AssertZeroExitCode();
        }

        // Sign frameworks under Contents/Frameworks (NativeReference items)
        var frameworks = appBundle / "Contents" / "Frameworks";
        if (Directory.Exists(frameworks))
        {
            foreach (var fw in Directory.GetDirectories(frameworks, "*.framework"))
            {
                ProcessTasks.StartProcess("codesign", $"--force -s - \"{fw}\"")
                    .AssertZeroExitCode();
            }
        }

        // Sign the main executable
        var exeName = exeNameOverride ?? "RuntimeTestsApp.Mac";
        var mainExe = appBundle / "Contents" / "MacOS" / exeName;
        ProcessTasks.StartProcess("codesign", $"--force -s - \"{mainExe}\"")
            .AssertZeroExitCode();

        // Sign the bundle itself
        ProcessTasks.StartProcess("codesign", $"--force -s - \"{appBundle}\"")
            .AssertZeroExitCode();

        Log.Information(".app bundle re-signed successfully.");
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
