// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

partial class Build
{
    // --- SwiftInterfaceParser host-tool paths ---

    /// <summary>SPM package root (tools/SwiftInterfaceParser).</summary>
    AbsolutePath SwiftInterfaceParserSourceDir => RootDirectory / "tools" / "SwiftInterfaceParser";

    /// <summary>
    /// Where the compiled binary is staged. Lives under the Sdk's tools/ tree so the existing
    /// <c>tools/**/*</c> pack glob in Swift.Bindings.Sdk.csproj absorbs it without a new
    /// MSBuild incantation, mirroring how the apple-types-manifest tool ships today.
    /// </summary>
    AbsolutePath SwiftInterfaceParserStagingDir =>
        SourceDir / "Swift.Bindings.Sdk" / "tools" / "swift-interface-parser";

    /// <summary>
    /// Compile the SwiftInterfaceParser SPM tool. Darwin-gated so the .NET-only
    /// <see cref="Compile"/> path still works on non-Apple hosts; skipped (with a warning)
    /// when xcrun cannot locate the swift toolchain. The Pack target hard-fails when the
    /// staged binary is missing — there is no fallback producer.
    /// </summary>
    // .After(Clean) is a pure ordering edge: both targets have no other dependencies and
    // are otherwise co-equal roots, which Nuke --strict rejects.
    Target CompileSwiftInterfaceParser => _ => _
        .Description("Build the SwiftInterfaceParser host binary (M2 SwiftSyntax fact producer).")
        .After(Clean)
        .OnlyWhenStatic(() => OperatingSystem.IsMacOS())
        .Executes(RunCompileSwiftInterfaceParser);

    /// <summary>
    /// Ensure the staged SwiftInterfaceParser host binary exists, building it when missing.
    /// The generator REQUIRES this binary to extract interface facts and hard-fails without it
    /// ("Could not locate the SwiftInterfaceParser host binary"). <c>nuke compile</c> builds it
    /// via <see cref="CompileSwiftInterfaceParser"/>, but the binding-tests/runtime regen paths that
    /// run the generator against a <c>.swiftinterface</c>/<c>--xcframework</c> do NOT depend on
    /// <see cref="Compile"/> — so on a clean checkout (e.g. a fresh CI runner) the binary is absent
    /// and the generator run fails. Called from <see cref="EnsureGeneratorBuilt"/> — the shared
    /// precondition of those paths — so each builds the parser on demand when missing. (Generator
    /// modes that consume no <c>.swiftinterface</c> — the stdlib-conformances ABI-dump pass and the
    /// apple-types-manifest probe — opt out via <c>ensureSwiftInterfaceParser: false</c>. The opt-in
    /// SDK-pack legs — <c>--mixed-pack</c>/<c>--mixed-direct</c>/<c>--appstore-hygiene</c> — run the
    /// generator through a packed SDK, not <see cref="EnsureGeneratorBuilt"/>, and keep their own
    /// loud presence assert by design; they are never in the default run or CI.)
    ///
    /// Existence guard, not a source fingerprint: a prior <c>nuke compile</c>/binding-tests run
    /// already staged the binary, so this is a no-op after the first build and the inner loop
    /// stays fast. The rare stale-parser case is handled elsewhere — <c>nuke compile</c> always
    /// rebuilds it, and a schema-incompatible stale binary fails loud at runtime via the
    /// producer↔consumer schema-version handshake (it does not silently mis-map).
    /// </summary>
    void EnsureSwiftInterfaceParserBuilt()
    {
        // Honor an explicit override (CI / tests). If SWIFT_INTERFACE_PARSER_PATH points at a real
        // binary the generator uses it directly, so we must not shadow it with a staged build.
        var fromEnv = Environment.GetEnvironmentVariable("SWIFT_INTERFACE_PARSER_PATH");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
            return;

        if (File.Exists(SwiftInterfaceParserStagingDir / "SwiftInterfaceParser"))
            return;

        // Mirror CompileSwiftInterfaceParser's Darwin gate. The generator is macOS-only, so a
        // non-Darwin binding-tests run can't reach the parser path anyway; leave the loud
        // "binary not found" to the generator rather than throwing here.
        if (!OperatingSystem.IsMacOS())
            return;

        Log.Information("SwiftInterfaceParser host binary missing — building it (required by the generator).");
        RunCompileSwiftInterfaceParser();
    }

    /// <summary>
    /// Build one slice per arch, lipo into a universal2 binary, and stage it. Shared by the
    /// <see cref="CompileSwiftInterfaceParser"/> target and the binding-tests path's
    /// <see cref="EnsureSwiftInterfaceParserBuilt"/> guard so both produce an identical artifact.
    /// </summary>
    void RunCompileSwiftInterfaceParser()
    {
        var stagingDir = SwiftInterfaceParserStagingDir;
        stagingDir.CreateDirectory();

        string swiftPath;
        try
        {
            swiftPath = XcRun.FindTool("swift");
        }
        catch (Exception ex)
        {
            Log.Warning(
                "Skipping CompileSwiftInterfaceParser: could not locate `swift` via xcrun ({Reason}). " +
                "Install Xcode or the Command Line Tools to build the SwiftSyntax fact producer. " +
                "Pack will hard-fail if the binary is missing.",
                ex.Message);
            // Wipe staging on the early-return path so a previously-built single-arch binary
            // from a prior `nuke compile` cannot survive a toolchain regression and quietly
            // ship through Pack — Pack/PackGate's existence check would otherwise pass.
            foreach (var existing in Directory.GetFiles(stagingDir))
                File.Delete(existing);
            return;
        }

        var packageDir = SwiftInterfaceParserSourceDir;

        // Build one slice per arch, then lipo into a universal2 binary so the shipped
        // SDK runs on both Apple Silicon and Intel developer hosts. The SDK packs a
        // single flat binary, so a host-only slice would fail with "Bad CPU type" on
        // the other architecture. macOS deployment target matches
        // tools/SwiftInterfaceParser/Package.swift's .macOS(.v13).
        var triples = new[]
        {
            "arm64-apple-macosx13.0",
            "x86_64-apple-macosx13.0",
        };

        // Resolve each slice's bin dir (.build/<triple>/release) via --show-bin-path so
        // we never hard-code SPM's layout.
        var binDirByTriple = new Dictionary<string, AbsolutePath>();
        foreach (var triple in triples)
        {
            Log.Information("Building SwiftInterfaceParser slice {Triple} (cwd: {Dir})", triple, packageDir);
            var buildProcess = ProcessTasks.StartProcess(
                    swiftPath, $"build -c release --triple {triple}",
                    workingDirectory: packageDir,
                    logOutput: false)
                .AssertWaitForExit();
            if (buildProcess.ExitCode != 0)
            {
                // Combined stdout+stderr: swift build's most useful diagnostics (e.g.
                // sandbox-exec failures, toolchain mismatches) frequently land on stderr,
                // which `StdToText()` would silently drop.
                Log.Error("swift build --triple {Triple} failed (exit {Exit}). Output:\n{Output}",
                    triple, buildProcess.ExitCode, CombinedOutput(buildProcess));
                throw new InvalidOperationException(
                    $"SwiftInterfaceParser build for {triple} failed (exit {buildProcess.ExitCode}).");
            }

            var binPathProcess = ProcessTasks.StartProcess(
                    swiftPath, $"build -c release --triple {triple} --show-bin-path",
                    workingDirectory: packageDir,
                    logOutput: false)
                .AssertWaitForExit()
                .AssertZeroExitCode();
            binDirByTriple[triple] = (AbsolutePath)binPathProcess.Output.StdToText().Trim();
        }

        // Wipe staging so stale slices/dylibs from a previous swift-syntax pin can't
        // shadow a fresh build.
        foreach (var existing in Directory.GetFiles(stagingDir))
            File.Delete(existing);

        // Verify each slice produced the binary, then lipo-merge.
        var perTripleExe = binDirByTriple.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value / "SwiftInterfaceParser");
        foreach (var (triple, exe) in perTripleExe)
        {
            if (!FileExists(exe))
                throw new InvalidOperationException(
                    $"swift build did not produce SwiftInterfaceParser for {triple} at {exe}.");
        }

        var stagedExe = stagingDir / "SwiftInterfaceParser";
        RunLipoCreate(perTripleExe.Values, stagedExe);
        // Preserve executable bit
        ProcessTasks.StartProcess("chmod", $"+x \"{stagedExe}\"", logOutput: false).AssertWaitForExit();

        // Defensive: confirm the staged binary is actually universal2. A single-slice
        // ship looks fine on the build host and only blows up on the other arch — this
        // assertion fails the build instead so we never package a host-only binary.
        AssertUniversal2(stagedExe);

        // Dylibs (swift-syntax 601.0.x links statically today, so the list is empty,
        // but the loop keeps us correct if a future swift-syntax bump goes dynamic).
        // Both triples must produce the same dylib set; an asymmetry is a build bug,
        // not something to silently paper over by shipping only one arch's dylib.
        var dylibsByTriple = binDirByTriple.ToDictionary(
            kvp => kvp.Key,
            kvp => Directory.GetFiles(kvp.Value, "*.dylib")
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray());
        var baselineDylibs = dylibsByTriple.Values.First();
        foreach (var (triple, names) in dylibsByTriple)
        {
            if (!names.SequenceEqual(baselineDylibs, StringComparer.Ordinal))
                throw new InvalidOperationException(
                    $"Dylib set mismatch for {triple}. " +
                    $"Got [{string.Join(", ", names)}], expected [{string.Join(", ", baselineDylibs)}]. " +
                    "Both arch slices must produce the same dylib set.");
        }

        foreach (var dylibName in baselineDylibs)
        {
            var perTripleDylibs = binDirByTriple.Values
                .Select(d => d / dylibName!)
                .ToArray();
            RunLipoCreate(perTripleDylibs, stagingDir / dylibName!);
        }

        Log.Information("Staged universal2 SwiftInterfaceParser → {Dir}", stagingDir);
    }

    static void RunLipoCreate(IEnumerable<AbsolutePath> inputs, AbsolutePath output)
    {
        var args = string.Join(" ",
            new[] { "-create" }
                .Concat(inputs.Select(p => $"\"{p}\""))
                .Concat(new[] { "-output", $"\"{output}\"" }));
        var process = ProcessTasks.StartProcess("lipo", args, logOutput: false)
            .AssertWaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"lipo -create failed (exit {process.ExitCode}). Output:\n{CombinedOutput(process)}");
        }
    }

    /// <summary>
    /// Verify a Mach-O binary is universal2 (arm64 + x86_64). Callable from Pack/PackGate
    /// so the gate that ships the SDK is independent of the build target's own assertion —
    /// even if a stale single-arch binary slipped past CompileSwiftInterfaceParser somehow,
    /// pack still refuses to ship it.
    /// </summary>
    internal static void AssertUniversal2(AbsolutePath binary)
    {
        var process = ProcessTasks.StartProcess("lipo", $"-archs \"{binary}\"", logOutput: false)
            .AssertWaitForExit()
            .AssertZeroExitCode();
        var actual = process.Output.StdToText().Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToArray();
        var expected = new[] { "arm64", "x86_64" };
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Staged {binary} is not universal2. Expected [{string.Join(", ", expected)}], " +
                $"got [{string.Join(", ", actual)}]. Both arch slices must be present.");
        }
    }

    static string CombinedOutput(IProcess process) =>
        string.Join("\n", process.Output.Select(o => o.Text));

    static bool FileExists(AbsolutePath path) => File.Exists(path);
}
