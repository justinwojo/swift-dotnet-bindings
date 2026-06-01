// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.X64ThunkGate.cs — x86_64 (SysV) thunk-backend gate
//
// Durable, opt-in gate proving the x86_64 thunk backend's cdecl -> swiftcc
// bridge is ABI-correct under Rosetta. It builds the committed Fixture.swift
// (build/x64-thunk-gate/) for x86_64-apple-macos, runs the generator, assembles
// and links the emitted x86_64 thunks into the wrapper dylib, then publishes a
// self-contained osx-x64 driver that P/Invokes the `thunk_*` symbols directly
// and asserts every round-trip under `arch -x86_64`.
//
// Scope: the THUNK ABI, not the full runtime. The driver uses manual cdecl
// [DllImport] decls (no Swift.Runtime). The full idiomatic generated-bindings
// path (TypeMetadata/SwiftObjectHelper/ARC) is a separate, later-session
// x86_64 runtime concern and is intentionally not exercised here.
//
// Not part of `nuke test`/`nuke binding-tests`: needs the macOS SDK + Rosetta
// and runs ~30-60s. Run explicitly: `nuke X64ThunkGate`.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    const string X64GateModule = "FixtureLib";
    const string X64GateWrapperModule = "FixtureLibSwiftBindings";
    const string X64GateTarget = "x86_64-apple-macos13.0";
    const string X64GateModuleSuffix = "x86_64-apple-macos";
    const string X64GateMinOs = "13.0";
    const string X64GatePlistPlatform = "MacOSX";
    const string X64GateDriverExe = "X64ThunkGateDriver";
    const int X64GateExpectedThunkCount = 6;

    // PInvoke_<token>_<hash> method tokens that must resolve to a `thunk_*`
    // EntryPoint in the generated FixtureLib.cs. Names are PascalCased to match
    // the const identifiers Program.cs references via ThunkSymbols.
    static readonly string[] X64GateExpectedThunks =
        { "Init", "AddAndGet", "Snapshot", "CheckedAdd", "Origin", "MakeMixed" };

    AbsolutePath X64ThunkGateScratch => RootDirectory / "artifacts" / "x64-thunk-gate";
    AbsolutePath X64ThunkGateDir => RootDirectory / "build" / "x64-thunk-gate";

    Target X64ThunkGate => _ => _
        .DependsOn(Compile)
        .OnlyWhenStatic(() => OperatingSystem.IsMacOS())
        // Pure ordering edge for Nuke --strict's sink-total-order requirement.
        // BehaviorTier and the X64*Gate chain are otherwise co-equal sinks; this
        // edge linearizes the chain after BehaviorTier. X64PackGate / X64SimGate
        // continue the chain with their own .After() edges.
        .After(BehaviorTier)
        .Executes(() =>
        {
            var scratch = X64ThunkGateScratch;
            if (Directory.Exists(scratch)) scratch.DeleteDirectory();
            scratch.CreateDirectory();

            // Force a Debug rebuild of the generator so the gate exercises the
            // current generator source. The gate invokes the Debug dll directly;
            // EnsureGeneratorBuilt only builds when the dll is *missing*, which
            // would let a stale binary silently certify old output.
            Log.Information("=== X64ThunkGate: rebuilding generator (Debug) ===");
            DotNetBuild(s => s
                .SetProjectFile(GeneratorProject)
                .SetConfiguration("Debug")
                .SetVerbosity(DotNetVerbosity.quiet));

            var sdkPath = XcRun.GetSdkPath("macosx");

            // 1. Build the x86_64 fixture framework + xcframework (generator input).
            var frameworkDir = scratch / $"{X64GateModule}.framework";
            CompileModuleSlice(
                X64GateModule, X64GateTarget, sdkPath,
                X64GateModuleSuffix, X64GateMinOs, X64GatePlistPlatform,
                frameworkDir, new[] { (X64ThunkGateDir / "Fixture.swift").ToString() },
                frameworkSearchPaths: null);

            var xcframework = scratch / $"{X64GateModule}.xcframework";
            XcodeBuild.ExecuteCreateXcframework(new CreateXcframeworkSettings()
                .AddFrameworkPath(frameworkDir)
                .SetOutputPath(xcframework));

            // 2. Run the generator (--platform macos emits x86_64 thunks alongside arm64).
            //    Emit-only: the gate assembles and links the thunks itself, so skip the
            //    generator's internal thunk/wrapper compilation. That internal compile is
            //    x86_64 build-wiring (per-RID slice selection / RID routing), a separate
            //    later-session concern — it would otherwise assemble the always-emitted
            //    arm64 thunks against the x86_64 slice and fail. Skipping it keeps a zero
            //    exit meaningful as a gate on emission.
            var genOut = scratch / "out";
            genOut.CreateDirectory();
            var genArgs = new[]
            {
                $"\"{GeneratorDll}\"",
                $"--xcframework \"{xcframework}\"",
                $"-o \"{genOut}\"",
                "--platform macos",
                "--skip-wrapper-compilation",
                "--skip-thunk-compilation",
            };
            var gen = ProcessTasks.StartProcess("dotnet", string.Join(" ", genArgs), logOutput: true);
            gen.WaitForExit();
            if (gen.ExitCode != 0)
                Assert.Fail($"X64ThunkGate: generator exited {gen.ExitCode}.");

            // 3. Assert the x86_64 thunk assembly emitted with the expected thunk count.
            var asmFile = genOut / $"{X64GateModule}.x86_64.s";
            if (!File.Exists(asmFile))
                Assert.Fail($"X64ThunkGate: expected x86_64 thunk assembly at {asmFile}.");
            var emittedThunks = Regex
                .Matches(File.ReadAllText(asmFile), @"^\s*\.globl\s+_(thunk_\w+)", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value).ToList();
            if (emittedThunks.Count != X64GateExpectedThunkCount)
                Assert.Fail(
                    $"X64ThunkGate: expected {X64GateExpectedThunkCount} thunks in {asmFile.Name}, " +
                    $"found {emittedThunks.Count}: {string.Join(", ", emittedThunks)}.");

            // 4. Assemble the thunks for x86_64.
            var thunkObj = genOut / "thunks.o";
            XcRunTool($"clang -c \"{asmFile}\" -o \"{thunkObj}\" -target {X64GateTarget}");

            // 5. Link the wrapper dylib (Wrapper.swift + thunks.o) against FixtureLib.
            //    install_name + @loader_path rpath let the wrapper resolve both itself
            //    and FixtureLib.framework when placed next to the published driver.
            var wrapperSwift = genOut / $"{X64GateModule}.Wrapper.swift";
            if (!File.Exists(wrapperSwift))
                Assert.Fail($"X64ThunkGate: expected wrapper source at {wrapperSwift}.");
            var runtimeDir = scratch / "runtime";
            runtimeDir.CreateDirectory();
            var wrapperDylib = runtimeDir / $"lib{X64GateWrapperModule}.dylib";
            SwiftCompiler.Execute(new SwiftCompilerSettings()
                .SetEmitLibrary()
                .SetTarget(X64GateTarget)
                .SetSdk(sdkPath)
                .SetModuleName(X64GateWrapperModule)
                .AddFrameworkSearchPath(scratch) // parent dir of FixtureLib.framework
                .AddExtraArgument("-framework").AddExtraArgument(X64GateModule)
                .SetInstallName($"@rpath/lib{X64GateWrapperModule}.dylib")
                .AddExtraArgument("-Xlinker").AddExtraArgument("-rpath")
                .AddExtraArgument("-Xlinker").AddExtraArgument("@loader_path")
                .SetOutputPath(wrapperDylib)
                .AddSourceFile(wrapperSwift.ToString())
                .AddSourceFile(thunkObj.ToString()));

            // 6. Assert every emitted thunk symbol is actually exported by the dylib.
            var nm = ProcessTasks.StartProcess("nm", $"\"{wrapperDylib}\"", logOutput: false);
            nm.WaitForExit();
            var nmText = string.Join("\n", nm.Output.Select(o => o.Text));
            var missingSymbols = emittedThunks.Where(t => !nmText.Contains(t, StringComparison.Ordinal)).ToList();
            if (missingSymbols.Any())
                Assert.Fail(
                    $"X64ThunkGate: thunk symbol(s) absent from {wrapperDylib.Name}: " +
                    $"{string.Join(", ", missingSymbols)}.");

            // 7. Derive method -> thunk EntryPoint from the generated FixtureLib.cs so the
            //    driver never hard-codes FNV hashes. Pair each `EntryPoint = "thunk_..."`
            //    with the PInvoke_<token>_<hash> partial decl on the following line(s).
            var thunkConsts = DeriveThunkSymbols(genOut / $"{X64GateModule}.cs");
            var missingConsts = X64GateExpectedThunks.Where(e => !thunkConsts.ContainsKey(e)).ToList();
            if (missingConsts.Any())
                Assert.Fail(
                    $"X64ThunkGate: could not derive thunk EntryPoint(s) for " +
                    $"{string.Join(", ", missingConsts)} from FixtureLib.cs.");

            // 8. Stage the driver in scratch (committed source stays clean), inject the
            //    derived ThunkSymbols, and publish self-contained osx-x64.
            var driverSrc = scratch / "Driver";
            driverSrc.CreateDirectory();
            File.Copy(X64ThunkGateDir / "Driver" / "Driver.csproj", driverSrc / "Driver.csproj", overwrite: true);
            File.Copy(X64ThunkGateDir / "Driver" / "Program.cs", driverSrc / "Program.cs", overwrite: true);
            File.WriteAllText(driverSrc / "ThunkSymbols.g.cs", BuildThunkSymbolsSource(thunkConsts));

            var publishDir = scratch / "publish";
            DotNetPublish(s => s
                .SetProject(driverSrc / "Driver.csproj")
                .SetConfiguration("Release")
                .SetRuntime("osx-x64")
                .SetSelfContained(true)
                .SetOutput(publishDir)
                .SetVerbosity(DotNetVerbosity.quiet));

            // 9. Place the wrapper dylib + FixtureLib.framework next to the driver so
            //    dyld resolves @rpath/@loader_path at launch.
            File.Copy(wrapperDylib, publishDir / $"lib{X64GateWrapperModule}.dylib", overwrite: true);
            var fwTarget = publishDir / $"{X64GateModule}.framework";
            if (Directory.Exists(fwTarget)) fwTarget.DeleteDirectory();
            frameworkDir.Copy(fwTarget);

            // 10. Run the driver under Rosetta and assert it round-trips every thunk.
            var driverExe = publishDir / X64GateDriverExe;
            if (!File.Exists(driverExe))
                Assert.Fail($"X64ThunkGate: published driver not found at {driverExe}.");

            Log.Information("=== X64ThunkGate: running driver under arch -x86_64 ===");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "arch",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = publishDir,
            };
            psi.ArgumentList.Add("-x86_64");
            psi.ArgumentList.Add(driverExe);
            using var driverProc = System.Diagnostics.Process.Start(psi)
                ?? throw new Exception($"Failed to launch driver at {driverExe}.");
            var driverStdout = driverProc.StandardOutput.ReadToEnd();
            var driverStderr = driverProc.StandardError.ReadToEnd();
            driverProc.WaitForExit();

            Log.Information("X64ThunkGate driver output:\n{Output}", driverStdout);
            if (driverProc.ExitCode != 0)
                Assert.Fail(
                    $"X64ThunkGate: driver exited {driverProc.ExitCode}.\n" +
                    $"stdout:\n{driverStdout}\nstderr:\n{driverStderr}");

            Log.Information("=== X64ThunkGate: PASS — x86_64 thunk ABI verified under Rosetta ===");
        });

    // Maps PascalCased method token -> `thunk_*` EntryPoint, parsed from FixtureLib.cs.
    static Dictionary<string, string> DeriveThunkSymbols(AbsolutePath generatedCs)
    {
        if (!File.Exists(generatedCs))
            throw new FileNotFoundException($"X64ThunkGate: generated bindings not found at {generatedCs}.");

        var lines = File.ReadAllLines(generatedCs);
        var entryRe = new Regex("EntryPoint = \"(thunk_[A-Za-z0-9_]+)\"");
        var pinvokeRe = new Regex(@"PInvoke_([A-Za-z0-9]+)_[0-9A-Fa-f]{6,8}\s*\(");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int i = 0; i < lines.Length; i++)
        {
            var em = entryRe.Match(lines[i]);
            if (!em.Success) continue;
            var symbol = em.Groups[1].Value;

            // The partial decl carrying PInvoke_<token>_<hash> follows the attribute.
            for (int j = i + 1; j < Math.Min(i + 4, lines.Length); j++)
            {
                var pm = pinvokeRe.Match(lines[j]);
                if (!pm.Success) continue;
                var token = pm.Groups[1].Value;
                var pascal = char.ToUpperInvariant(token[0]) + token.Substring(1);
                map[pascal] = symbol;
                break;
            }
        }

        return map;
    }

    static string BuildThunkSymbolsSource(IReadOnlyDictionary<string, string> thunkConsts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// Derived by `nuke X64ThunkGate` from the generated FixtureLib.cs. Do not edit.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("internal static class ThunkSymbols");
        sb.AppendLine("{");
        foreach (var name in X64GateExpectedThunks)
            sb.AppendLine($"    public const string {name} = \"{thunkConsts[name]}\";");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
