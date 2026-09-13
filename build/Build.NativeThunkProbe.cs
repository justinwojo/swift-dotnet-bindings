// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Opt-in, host-only proof for the generated native-thunk path retained on an opaque internal
// class receiver. All products are built in artifacts/native-thunk-probe; the BindingTests output,
// apps, baselines, and checked-in generated sources are never touched.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

partial class Build
{
    const string NativeThunkProbeModule = "SwiftBindingsTestLib";
    const string NativeThunkProbeWrapper = "SwiftBindings";

    AbsolutePath NativeThunkProbeAssets => RootDirectory / "build" / "native-thunk-probe";
    AbsolutePath NativeThunkProbeScratch => RootDirectory / "artifacts" / "native-thunk-probe";

    Target NativeThunkProbe => _ => _
        .DependsOn(Compile)
        .OnlyWhenStatic(() => OperatingSystem.IsMacOS())
        // Ordering-only edge for Nuke --strict's total-order requirement. The opt-in probe does
        // not consume or trigger the withdrawal invocation fixture.
        .After(WithdrawalInvocationPromotion)
        .Executes(RunNativeThunkProbe);

    void RunNativeThunkProbe()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x86_64",
            var other => throw new PlatformNotSupportedException(
                $"NativeThunkProbe supports arm64 and x86_64 macOS hosts, not {other}."),
        };
        var target = $"{architecture}-apple-macos13.0";
        var moduleSuffix = $"{architecture}-apple-macos";
        var generatedArchitecture = architecture == "arm64" ? "arm64" : "x86_64";

        var scratch = NativeThunkProbeScratch;
        if (Directory.Exists(scratch))
            scratch.DeleteDirectory();
        scratch.CreateDirectory();

        var evidence = scratch / "evidence";
        evidence.CreateDirectory();
        var sdkPath = XcRun.GetSdkPath("macosx");
        var fixtureSource = BindingTestsDir / "Sources" / NativeThunkProbeModule / "Internal" /
            "NativeThunkReach.swift";

        Log.Information("=== NativeThunkProbe: building isolated {Arch} host fixture ===", architecture);

        // Build the one-file fixture with the production module name. CompileModuleSlice gives the
        // generator the same library-evolution interface, ABI JSON, TBD, and Mach-O shape as a real
        // BindingTests slice without mutating BindingTests/.build or BindingTests/output.
        var fixtureFramework = scratch / $"{NativeThunkProbeModule}.framework";
        CompileModuleSlice(
            NativeThunkProbeModule,
            target,
            sdkPath,
            moduleSuffix,
            "13.0",
            "MacOSX",
            fixtureFramework,
            new[] { fixtureSource.ToString() },
            frameworkSearchPaths: null);

        var fixtureXcframework = scratch / $"{NativeThunkProbeModule}.xcframework";
        XcodeBuild.ExecuteCreateXcframework(new CreateXcframeworkSettings()
            .AddFrameworkPath(fixtureFramework)
            .SetOutputPath(fixtureXcframework));

        // Preserve the compiler-facing contract before generating or assembling any thunk. The
        // public interface is also the visibility control: the @usableFromInline parent remains
        // internal to Swift source even though its ABI is available to the binding importer.
        var fixtureModuleDir = fixtureFramework / "Modules" / $"{NativeThunkProbeModule}.swiftmodule";
        var publicInterface = fixtureModuleDir / $"{moduleSuffix}.swiftinterface";
        var tbd = fixtureModuleDir / $"{NativeThunkProbeModule}.tbd";
        File.Copy(fixtureSource, evidence / "NativeThunkReach.swift", overwrite: true);
        File.Copy(publicInterface, evidence / "module.swiftinterface", overwrite: true);
        File.Copy(tbd, evidence / "module.tbd", overwrite: true);

        var silGen = evidence / "fixture.silgen";
        var optimizedSil = evidence / "fixture.optimized.sil";
        var optimizedIr = evidence / "fixture.optimized.ll";
        RunRequiredNativeThunkProbeProcess(
            "xcrun",
            [
                "swiftc", "-parse-as-library", "-module-name", NativeThunkProbeModule,
                "-enable-library-evolution", "-target", target, "-sdk", sdkPath,
                "-emit-silgen", fixtureSource.ToString(), "-o", silGen.ToString(),
            ],
            scratch,
            "SILGen ownership capture");
        RunRequiredNativeThunkProbeProcess(
            "xcrun",
            [
                "swiftc", "-parse-as-library", "-module-name", NativeThunkProbeModule,
                "-enable-library-evolution", "-target", target, "-sdk", sdkPath,
                "-O", "-emit-sil", fixtureSource.ToString(), "-o", optimizedSil.ToString(),
            ],
            scratch,
            "optimized SIL capture");
        RunRequiredNativeThunkProbeProcess(
            "xcrun",
            [
                "swiftc", "-parse-as-library", "-module-name", NativeThunkProbeModule,
                "-enable-library-evolution", "-target", target, "-sdk", sdkPath,
                "-O", "-emit-ir", fixtureSource.ToString(), "-o", optimizedIr.ToString(),
            ],
            scratch,
            "optimized IR capture");

        // Emit only. This target owns assembly/link validation below and intentionally does not
        // build or run any .NET app.
        var generated = scratch / "generated";
        generated.CreateDirectory();
        var generator = RunNativeThunkProbeProcess(
            "dotnet",
            [
                GeneratorDll.ToString(),
                "--xcframework", fixtureXcframework.ToString(),
                "-o", generated.ToString(),
                "--platform", "macos",
                "--async-library", NativeThunkProbeWrapper,
                "--skip-wrapper-compilation",
                "--skip-thunk-compilation",
                "--no-verify-csharp",
            ],
            scratch);
        File.WriteAllText(evidence / "generator.txt", generator.CombinedOutput);
        if (generator.ExitCode != 0)
            throw new Exception(
                $"NativeThunkProbe generator exited {generator.ExitCode}. See {evidence / "generator.txt"}.");

        var generatedCs = Directory.GetFiles(generated, "*.cs")
            .FirstOrDefault(path => File.ReadAllText(path).Contains(
                "class NativeThunkReceiver", StringComparison.Ordinal))
            ?? throw new FileNotFoundException(
                "NativeThunkProbe did not emit NativeThunkReceiver generated C#.");
        var generatedText = File.ReadAllText(generatedCs);
        if (!generatedText.Contains("public virtual int Add", StringComparison.Ordinal))
            throw new Exception(
                "NativeThunkProbe requires the generated concrete NativeThunkReceiver.Add member.");

        var thunkMatches = Regex.Matches(
            generatedText,
            "LibraryImport\\(\"SwiftBindings\", EntryPoint = \"(thunk_SwiftBindingsTestLib_[0-9a-f]{8})\"\\)\\][\\s\\S]{0,320}?PInvoke_add_",
            RegexOptions.CultureInvariant);
        if (thunkMatches.Count != 1)
            throw new Exception(
                $"NativeThunkProbe expected one generated Add native-thunk import, found {thunkMatches.Count}.");
        var thunkSymbol = thunkMatches[0].Groups[1].Value;

        var thunkAssembly = generated / $"{NativeThunkProbeModule}.{generatedArchitecture}.s";
        if (!File.Exists(thunkAssembly))
            throw new FileNotFoundException(
                $"NativeThunkProbe expected generated assembly {thunkAssembly}.");
        var assemblyText = File.ReadAllText(thunkAssembly);
        if (!assemblyText.Contains($"_{thunkSymbol}", StringComparison.Ordinal))
            throw new Exception($"Generated assembly does not define {thunkSymbol}.");

        // Compile both committed sentinels on every host. Only the native architecture is linked
        // and run; the cross-architecture object is a cheap syntax/assembler check.
        var armSentinelObject = scratch / "sentinel-arm64.o";
        var x64SentinelObject = scratch / "sentinel-x86_64.o";
        var armSentinelText = File.ReadAllText(NativeThunkProbeAssets / "sentinel-arm64.S");
        if (armSentinelText.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Any(line => Regex.IsMatch(line, @"\bx18\b", RegexOptions.CultureInvariant)))
            throw new Exception("NativeThunkProbe ARM64 sentinel must never read or write platform register x18.");
        XcRunTool(
            $"clang -c \"{NativeThunkProbeAssets / "sentinel-arm64.S"}\" -o \"{armSentinelObject}\" -target arm64-apple-macos13.0");
        XcRunTool(
            $"clang -c \"{NativeThunkProbeAssets / "sentinel-x86_64.S"}\" -o \"{x64SentinelObject}\" -target x86_64-apple-macos13.0");

        var thunkObject = scratch / "generated-thunk.o";
        var badObject = scratch / "bad-control.o";
        XcRunTool($"clang -c \"{thunkAssembly}\" -o \"{thunkObject}\" -target {target}");
        XcRunTool(
            $"clang -c \"{NativeThunkProbeAssets / "bad-control.S"}\" -o \"{badObject}\" -target {target}");

        // Link the production-emitted thunk and any generated Swift wrapper source into the wrapper
        // image. The sentinel remains in the probe executable and calls the exported thunk strictly
        // by the dlsym function pointer supplied at runtime.
        var wrapperSource = generated / $"{NativeThunkProbeModule}.Wrapper.swift";
        if (!File.Exists(wrapperSource))
            throw new FileNotFoundException(
                $"NativeThunkProbe expected generated wrapper source {wrapperSource}.");
        var wrapperDylib = scratch / $"lib{NativeThunkProbeWrapper}.dylib";
        SwiftCompiler.Execute(new SwiftCompilerSettings()
            .SetEmitLibrary()
            .SetTarget(target)
            .SetSdk(sdkPath)
            .SetModuleName(NativeThunkProbeWrapper)
            .AddFrameworkSearchPath(scratch)
            .AddExtraArgument("-framework").AddExtraArgument(NativeThunkProbeModule)
            .SetInstallName($"@rpath/lib{NativeThunkProbeWrapper}.dylib")
            .SetOutputPath(wrapperDylib)
            .AddSourceFile(wrapperSource.ToString())
            .AddSourceFile(thunkObject.ToString()));

        var sentinelObject = architecture == "arm64" ? armSentinelObject : x64SentinelObject;
        var probeExecutable = scratch / "native-thunk-probe";
        XcRunTool(
            $"clang -Wall -Wextra -Werror \"{NativeThunkProbeAssets / "probe.c"}\" \"{sentinelObject}\" \"{badObject}\" -o \"{probeExecutable}\" -target {target}");

        // Structural evidence first: exact exports, disassembly, ABI JSON, and reproducible hashes.
        var fixtureBinary = fixtureFramework / NativeThunkProbeModule;
        var exports = RunNativeThunkProbeProcess("nm", ["-gU", fixtureBinary, wrapperDylib], scratch);
        File.WriteAllText(evidence / "exports.txt", exports.CombinedOutput);
        if (exports.ExitCode != 0
            || !exports.CombinedOutput.Contains($"_{thunkSymbol}", StringComparison.Ordinal)
            || !exports.CombinedOutput.Contains("_bt_native_thunk_create", StringComparison.Ordinal))
            throw new Exception("NativeThunkProbe export evidence is incomplete.");

        var wrapperDisassembly = RunNativeThunkProbeProcess("otool", ["-tvV", wrapperDylib], scratch);
        var sentinelDisassembly = RunNativeThunkProbeProcess("otool", ["-tvV", probeExecutable], scratch);
        File.WriteAllText(
            evidence / "disassembly.txt",
            wrapperDisassembly.CombinedOutput + Environment.NewLine + sentinelDisassembly.CombinedOutput);
        if (wrapperDisassembly.ExitCode != 0 || sentinelDisassembly.ExitCode != 0)
            throw new Exception("NativeThunkProbe disassembly capture failed.");

        var abiJson = fixtureFramework / "Modules" / $"{NativeThunkProbeModule}.swiftmodule" /
            $"{moduleSuffix}.abi.json";
        File.Copy(abiJson, evidence / "abi.json", overwrite: true);
        File.Copy(generatedCs, evidence / "NativeThunkReceiver.generated.cs", overwrite: true);
        File.Copy(thunkAssembly, evidence / $"generated-thunk-{generatedArchitecture}.s", overwrite: true);

        var swiftVersion = RunRequiredNativeThunkProbeProcess(
            "xcrun", ["swiftc", "--version"], scratch, "Swift version capture");
        var xcodeVersion = RunRequiredNativeThunkProbeProcess(
            "xcodebuild", ["-version"], scratch, "Xcode version capture");
        var dotnetInfo = RunRequiredNativeThunkProbeProcess(
            "dotnet", ["--info"], scratch, ".NET runtime capture");
        File.WriteAllText(
            evidence / "toolchain.txt",
            swiftVersion.CombinedOutput + Environment.NewLine +
            xcodeVersion.CombinedOutput + Environment.NewLine +
            dotnetInfo.CombinedOutput);
        var uuids = RunRequiredNativeThunkProbeProcess(
            "xcrun",
            ["dwarfdump", "--uuid", fixtureBinary, wrapperDylib, probeExecutable],
            scratch,
            "Mach-O UUID capture");
        File.WriteAllText(evidence / "uuids.txt", uuids.CombinedOutput);
        File.WriteAllText(
            evidence / "metadata.txt",
            $"architecture={architecture}{Environment.NewLine}" +
            $"target={target}{Environment.NewLine}" +
            $"sdk={sdkPath}{Environment.NewLine}" +
            $"minimum_os=13.0{Environment.NewLine}" +
            $"thunk={thunkSymbol}{Environment.NewLine}" +
            $"generated_csharp={generatedCs}{Environment.NewLine}" +
            $"generated_assembly={thunkAssembly}{Environment.NewLine}" +
            $"swift_fixture_options=-parse-as-library -module-name {NativeThunkProbeModule} " +
                $"-enable-library-evolution -target {target} -sdk {sdkPath}{Environment.NewLine}" +
            $"thunk_assembly_options=clang -c -target {target}{Environment.NewLine}" +
            $"wrapper_link_options=swiftc -emit-library -target {target} -sdk {sdkPath} " +
                $"-module-name {NativeThunkProbeWrapper} -framework {NativeThunkProbeModule}{Environment.NewLine}");
        WriteNativeThunkProbeHashes(
            evidence / "sha256.txt",
            RootDirectory / "build" / "Build.NativeThunkProbe.cs",
            fixtureSource,
            NativeThunkProbeAssets / "probe.c",
            NativeThunkProbeAssets / "sentinel-arm64.S",
            NativeThunkProbeAssets / "sentinel-x86_64.S",
            NativeThunkProbeAssets / "bad-control.S",
            generatedCs,
            thunkAssembly,
            armSentinelObject,
            x64SentinelObject,
            thunkObject,
            badObject,
            fixtureBinary,
            wrapperDylib,
            probeExecutable);

        // Dynamic proof: good calls preserve every platform callee-saved register covered by the
        // sentinel and retain Swift override dispatch. Then run the bad x20/r13-clobber control
        // under the same sentinel while pretending it must be clean; it must turn red (exit 1).
        var good = RunNativeThunkProbeProcess(
            probeExecutable,
            [fixtureBinary, wrapperDylib, thunkSymbol, "good"],
            scratch);
        File.WriteAllText(
            evidence / "good-run.txt",
            $"exit_code={good.ExitCode}{Environment.NewLine}{good.CombinedOutput}");
        if (good.ExitCode != 0)
            throw new Exception(
                $"NativeThunkProbe good run exited {good.ExitCode}. See {evidence / "good-run.txt"}.");

        var bad = RunNativeThunkProbeProcess(
            probeExecutable,
            [fixtureBinary, wrapperDylib, thunkSymbol, "bad-control"],
            scratch);
        File.WriteAllText(
            evidence / "bad-control-run.txt",
            $"exit_code={bad.ExitCode}{Environment.NewLine}{bad.CombinedOutput}");
        var expectedBadMask = architecture == "arm64" ? "bad-control mask=0x2" : "bad-control mask=0x8";
        if (bad.ExitCode == 0
            || !bad.CombinedOutput.Contains(expectedBadMask, StringComparison.Ordinal))
            throw new Exception(
                $"NativeThunkProbe bad nonvolatile-register control did not fail with {expectedBadMask}; " +
                "sentinel is ineffective or reports the wrong register.");

        Log.Information(
            "=== NativeThunkProbe PASS: {Thunk}; good ABI clean, bad control red; evidence {Evidence} ===",
            thunkSymbol,
            evidence);
    }

    static NativeThunkProbeProcessResult RunNativeThunkProbeProcess(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new Exception($"NativeThunkProbe could not start {executable}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new NativeThunkProbeProcessResult(
            process.ExitCode,
            stdout + (stderr.Length == 0 ? string.Empty : Environment.NewLine + stderr));
    }

    static NativeThunkProbeProcessResult RunRequiredNativeThunkProbeProcess(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        string description)
    {
        var result = RunNativeThunkProbeProcess(executable, arguments, workingDirectory);
        if (result.ExitCode != 0)
            throw new Exception($"NativeThunkProbe {description} exited {result.ExitCode}: {result.CombinedOutput}");
        return result;
    }

    static void WriteNativeThunkProbeHashes(string outputPath, params string[] paths)
    {
        var lines = paths.Select(path =>
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            return $"{hash}  {path}";
        });
        File.WriteAllLines(outputPath, lines);
    }

    readonly record struct NativeThunkProbeProcessResult(int ExitCode, string CombinedOutput);
}
