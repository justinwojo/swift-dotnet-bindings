// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.IO;
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
    /// Compile the SwiftInterfaceParser SPM tool. Darwin-gated: skips on non-Apple hosts
    /// and emits a warning when xcrun cannot locate the swift toolchain (the M2 Session 1
    /// audit flagged that adding swift build unconditionally to <see cref="Compile"/> would
    /// regress the .NET-only Compile target's prerequisites). The Pack target hard-fails if
    /// the binary is missing — see <c>SwiftInterfaceParserBinaryPath</c> consumers there.
    /// </summary>
    Target CompileSwiftInterfaceParser => _ => _
        .Description("Build the SwiftInterfaceParser host binary (M2 SwiftSyntax fact producer).")
        .OnlyWhenStatic(() => OperatingSystem.IsMacOS())
        .Executes(() =>
        {
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
                    "The .NET seam still defaults to the regex producer; this only affects the " +
                    "swift-syntax CLI option in the generator.",
                    ex.Message);
                return;
            }

            var packageDir = SwiftInterfaceParserSourceDir;
            var stagingDir = SwiftInterfaceParserStagingDir;
            stagingDir.CreateDirectory();

            // `swift build -c release` produces .build/release/SwiftInterfaceParser plus any
            // dynamically-linked SwiftSyntax dylibs. We copy the entire release directory into
            // staging so SPM-resolved dylibs travel with the executable. swift-syntax 601.0.x
            // links dynamically by default; the dylibs sit alongside the binary at runtime.
            //
            // Show stdout only on failure to keep `nuke compile` quiet on the happy path —
            // SPM's resolution chatter is noisy and uninteresting when nothing changed.
            Log.Information("Building SwiftInterfaceParser via `swift build -c release` (cwd: {Dir})", packageDir);
            var process = ProcessTasks.StartProcess(
                    swiftPath, "build -c release",
                    workingDirectory: packageDir,
                    logOutput: false)
                .AssertWaitForExit();
            if (process.ExitCode != 0)
            {
                Log.Error("swift build failed (exit {Exit}). Output:\n{Output}",
                    process.ExitCode, process.Output.StdToText());
                throw new InvalidOperationException(
                    $"SwiftInterfaceParser build failed (exit {process.ExitCode}).");
            }

            // SPM places the release binary at .build/<arch>-apple-macosx/release/<exe>. Use
            // `swift build --show-bin-path` to avoid hard-coding the triple.
            var binPathProcess = ProcessTasks.StartProcess(
                    swiftPath, "build -c release --show-bin-path",
                    workingDirectory: packageDir,
                    logOutput: false)
                .AssertWaitForExit()
                .AssertZeroExitCode();
            var binDir = (AbsolutePath)binPathProcess.Output.StdToText().Trim();

            // Wipe the staging dir so stale dylibs from a previous swift-syntax pin can't
            // shadow a fresh build. Then copy executable + every dylib next to it.
            foreach (var existing in Directory.GetFiles(stagingDir))
                File.Delete(existing);

            var executable = binDir / "SwiftInterfaceParser";
            if (!FileExists(executable))
                throw new InvalidOperationException($"swift build did not produce the expected binary at {executable}.");

            var stagedExe = stagingDir / "SwiftInterfaceParser";
            File.Copy(executable, stagedExe, overwrite: true);
            // Preserve executable bit
            ProcessTasks.StartProcess("chmod", $"+x \"{stagedExe}\"", logOutput: false).AssertWaitForExit();

            // Copy every dylib that ships next to the binary (SwiftSyntax linked dynamically).
            foreach (var dylib in Directory.GetFiles(binDir, "*.dylib"))
            {
                var dest = stagingDir / Path.GetFileName(dylib);
                File.Copy(dylib, dest, overwrite: true);
            }

            Log.Information("Staged SwiftInterfaceParser → {Dir}", stagingDir);
        });

    static bool FileExists(AbsolutePath path) => File.Exists(path);
}
