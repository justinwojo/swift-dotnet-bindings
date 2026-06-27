// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.StdlibConformances.cs
//
// Nuke target wrapping the generator's `--regen-stdlib-conformances` mode. Dumps the Swift
// standard library's conformance graph via swift-api-digester, then has the generator
// verify/prune the committed `stdlib-conformances.json` fact table against that ground truth.
//
// The table is the ConformanceOracle's curated stdlib slice — a deliberately minimal "ONE
// input" — so this target never widens it to the full live conformance set; it only prunes a
// curated entry the live stdlib no longer declares (catching a hand-curation error or a
// cross-Xcode drift). Run it after a toolchain bump, or whenever the comment in the table says
// to regenerate rather than hand-edit.

using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

partial class Build
{
    AbsolutePath StdlibConformancesPath =>
        RootDirectory / "src" / "Swift.Bindings" / "src" / "Data" / "stdlib-conformances.json";

    // Named RegenStdlibConformances (not Regenerate…) so Nuke's kebab-casing yields the
    // `nuke regen-stdlib-conformances` command the stdlib-conformances.json comment advertises.
    //
    // The .After(...) edges are ordering-only (they never force those gates to run when this
    // target is invoked standalone); they exist to give Nuke `--strict` a total order. This is
    // a manual-maintenance sink in the same family as SeedParityBaseline / SeedSkipSurfaceBaseline:
    // without an edge between the co-equal sinks, strict mode rejects the plan ("Incomplete target
    // definition order"). Mirror SeedParityBaseline's anchor set and peel this one after it so all
    // three maintenance sinks are totally ordered. The release gate runs `binding-tests --strict`,
    // so this must hold.
    Target RegenStdlibConformances => _ => _
        .After(BindingTests, BehaviorTier, ValidateBlastRadius, X64SimGate, SeedSkipSurfaceBaseline, SeedParityBaseline)
        .Description("Dump the Swift stdlib via swift-api-digester and prune stale entries from " +
                     "stdlib-conformances.json (write-back). Pass nothing else — sim SDK + arm64 triple.")
        .Executes(() =>
        {
            // This generator mode consumes a swift-api-digester ABI dump + the conformance table,
            // never a `.swiftinterface`, so it does not need the SwiftInterfaceParser host binary.
            EnsureGeneratorBuilt(ensureSwiftInterfaceParser: false);

            // Any SDK carries the Swift module; the iOS-simulator slice is always present on a
            // dev host. The conformance graph is platform-invariant for the curated value types.
            var sdkName = "iphonesimulator";
            var target = "arm64-apple-ios15.0-simulator";
            var sdkPath = XcRun.GetSdkPath(sdkName);

            var dumpPath = TemporaryDirectory / "swift-stdlib-abi.json";
            if (System.IO.File.Exists(dumpPath))
                System.IO.File.Delete(dumpPath);

            Log.Information("=== Dumping Swift stdlib conformance graph via swift-api-digester ===");
            Log.Information("SDK: {Sdk} ({Path}); target: {Target}", sdkName, sdkPath, target);

            var digesterArgs = string.Join(" ", new[]
            {
                "swift-api-digester",
                "-dump-sdk",
                "-module", "Swift",
                "-target", target,
                "-sdk", $"\"{sdkPath}\"",
                "-o", $"\"{dumpPath}\"",
            });
            var digesterProc = ProcessTasks.StartProcess(
                "xcrun", digesterArgs,
                workingDirectory: RootDirectory,
                logOutput: true);
            digesterProc.WaitForExit();
            if (digesterProc.ExitCode != 0)
                Assert.Fail($"swift-api-digester exited with code {digesterProc.ExitCode}. Arguments: {digesterArgs}");
            if (!System.IO.File.Exists(dumpPath))
                Assert.Fail($"swift-api-digester exited 0 but did not produce {dumpPath}.");

            Log.Information("Dump: {Path} ({Size:N0} bytes)", dumpPath, new System.IO.FileInfo(dumpPath).Length);
            Log.Information("=== Verifying/pruning {Table} ===", StdlibConformancesPath);

            var args = string.Join(" ", new[]
            {
                $"\"{GeneratorDll}\"",
                "--regen-stdlib-conformances",
                $"--stdlib-dump \"{dumpPath}\"",
                $"--stdlib-conformances \"{StdlibConformancesPath}\"",
                "--stdlib-conformances-write-back",
            });
            var process = ProcessTasks.StartProcess(
                "dotnet", args,
                workingDirectory: RootDirectory,
                logOutput: true);
            process.WaitForExit();

            if (process.ExitCode != 0)
                Assert.Fail($"stdlib-conformances regen failed (exit code {process.ExitCode}).");

            Log.Information("stdlib-conformances regen complete.");
        });
}
