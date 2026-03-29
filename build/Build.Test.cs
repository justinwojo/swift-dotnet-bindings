// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.Test.cs — Full test suite matching run-tests.sh behavior
//
// Suite execution order (matches run-tests.sh):
//   1. Unit Tests (dotnet test UnitTests)
//   2. Runtime Unit Tests (dotnet test Swift.Runtime/tests)
//   3. Analyzer Tests (dotnet test Swift.Analyzers.Tests)
//   4. BindingTests Regression — ONLY IF: macOS + BindingTests/ exists
//   5. BindingTests Runtime Tests — ONLY IF: above + xcrun + iOS Simulator available
//
// All suites run even if earlier ones fail (ProceedAfterFailure).

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    string DotNetHostPath => ToolPathResolver.GetPathExecutable("dotnet");

    Target UnitTests => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(s => s
                .SetProjectFile(SourceDir / "Swift.Bindings" / "tests" / "UnitTests")
                .EnableNoBuild()
                .SetConfiguration("Debug")
                .SetProcessAdditionalArguments($"-- RunConfiguration.DotNetHostPath=\"{DotNetHostPath}\""));
        });

    Target AnalyzerTests => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(s => s
                .SetProjectFile(SourceDir / "Swift.Analyzers.Tests")
                .EnableNoBuild()
                .SetConfiguration("Debug")
                .SetProcessAdditionalArguments($"-- RunConfiguration.DotNetHostPath=\"{DotNetHostPath}\""));
        });

    Target RuntimeUnitTests => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(s => s
                .SetProjectFile(SourceDir / "Swift.Runtime" / "tests")
                .EnableNoBuild()
                .SetConfiguration("Debug")
                .SetProcessAdditionalArguments($"-- RunConfiguration.DotNetHostPath=\"{DotNetHostPath}\""));
        });

    // ============================================================
    // Test — full test suite matching run-tests.sh EXACTLY
    // ============================================================

    Target Test => _ => _
        .DependsOn(UnitTests, RuntimeUnitTests, AnalyzerTests)
        .ProceedAfterFailure()
        .Executes(() =>
        {
            // Suite 4: BindingTests regression (macOS + Xcode only) — run-tests.sh lines 83-88
            if (!EnvironmentInfo.IsOsx)
            {
                Log.Information("Skipping BindingTests (requires macOS with Xcode).");
                return;
            }
            if (!Directory.Exists(BindingTestsDir))
            {
                Log.Information("BindingTests directory not found, skipping.");
                return;
            }

            // Run BindingTests regression suite: strict regen + coverage + baselines
            // Both suites run even if the first fails (matching run-tests.sh behavior
            // where regression and runtime are separate run_suite calls).
            Exception? regressionFailure = null;
            try
            {
                RunBindingTestsRegression();
            }
            catch (Exception ex)
            {
                Log.Error("BindingTests Regression Suite FAILED: {Message}", ex.Message);
                regressionFailure = ex;
            }

            // Suite 5: Conditional simulator runtime tests (run-tests.sh lines 112-139)
            Exception? runtimeFailure = null;
            if (!HasXcrun())
            {
                Log.Information("Skipping BindingTests Runtime Tests (xcrun not available).");
            }
            else if (!HasAvailableSimulator())
            {
                Log.Information("Skipping BindingTests Runtime Tests (no available iPhone simulator found).");
            }
            else
            {
                // Run simulator runtime tests with --skip-regen (bindings already generated
                // by the regression suite above). Matches:
                //   run-tests.sh → run-runtime-tests.sh --skip-regen --timeout 90
                try
                {
                    RunRuntimeTestsOnSimulator();
                }
                catch (Exception ex)
                {
                    Log.Error("BindingTests Runtime Tests FAILED: {Message}", ex.Message);
                    runtimeFailure = ex;
                }
            }

            // Report failures after both suites have had a chance to run
            if (regressionFailure != null && runtimeFailure != null)
                throw new AggregateException("BindingTests suites failed", regressionFailure, runtimeFailure);
            if (regressionFailure != null)
                throw regressionFailure;
            if (runtimeFailure != null)
                throw runtimeFailure;
        });

    // ============================================================
    // BindingTestsRegression — shared method (not a target)
    //
    // Equivalent of: build-and-test.sh --strict + generate-coverage-report.sh + check-baselines.sh
    // ============================================================

    void RunBindingTestsRegression()
    {
        Log.Information("");
        Log.Information("=== BindingTests Regression Suite ===");

        // Step 1: Run full binding pipeline in strict mode
        RunBuildXcframework();
        RunRegenerateBindings(strict: true);
        RunCompileCheck();
        RunBuildAsyncWrapper();
        RunBuildBridge();
        ReportBindingTestResults();

        // Step 2: Generate coverage report (generate-coverage-report.sh)
        RunGenerateCoverageReport();

        // Step 3: Check must_pass degraded count (run-tests.sh lines 94-110)
        CheckMustPassDegraded();

        // Step 4: Check baselines (check-baselines.sh)
        RunCheckBaselines();
    }

    // ============================================================
    // Coverage Report Generation — ports generate-coverage-report.sh
    // ============================================================

    void RunGenerateCoverageReport()
    {
        Log.Information("=== Generating Coverage Report ===");

        // generate-coverage-report.sh is an embedded Python script that parses
        // ABI JSON + binding-report.json to produce coverage-matrix.json.
        // Rather than reimplementing the Python logic in C#, we invoke the script directly.
        var scriptPath = BindingTestsDir / "generate-coverage-report.sh";
        if (!File.Exists(scriptPath))
        {
            Log.Warning("generate-coverage-report.sh not found — skipping coverage report generation.");
            return;
        }

        var process = ProcessTasks.StartProcess(
            "bash", scriptPath,
            workingDirectory: BindingTestsDir);
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new Exception($"Coverage report generation failed (exit code {process.ExitCode})");

        Log.Information("Coverage report generated: {Path}", BindingTestsDir / "output" / "coverage-matrix.json");
    }

    // ============================================================
    // Must-pass degraded check — run-tests.sh lines 94-110
    // ============================================================

    void CheckMustPassDegraded()
    {
        var coverageJson = BindingTestsDir / "output" / "coverage-matrix.json";
        if (!File.Exists(coverageJson))
            return;

        int degraded;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(coverageJson));
            var summary = doc.RootElement.GetProperty("summary");
            var mustPass = summary.GetProperty("must_pass");
            degraded = mustPass.GetProperty("degraded").GetInt32();
        }
        catch (Exception ex)
        {
            Log.Warning("Could not parse coverage-matrix.json: {Message}", ex.Message);
            return;
        }

        if (degraded > 0)
        {
            Log.Error("*** {Degraded} must-pass feature(s) are degraded in BindingTests ***", degraded);
            Log.Error("See {Path} for details.", coverageJson);
            throw new Exception($"{degraded} must-pass feature(s) are degraded in BindingTests");
        }
    }

    // ============================================================
    // Baselines Check — ports check-baselines.sh logic into C#
    // ============================================================

    void RunCheckBaselines()
    {
        Log.Information("=== Checking Baselines ===");

        var baselineFile = BindingTestsDir / "baselines.json";
        var coverageFile = BindingTestsDir / "output" / "coverage-matrix.json";
        var exitCodeFile = BindingTestsDir / "output" / "generator-exit-code";
        var stripCountFile = BindingTestsDir / "output" / "wrapper-stripped-count";

        if (!File.Exists(baselineFile))
        {
            Log.Warning("baselines.json not found — skipping baseline check.");
            return;
        }

        using var baselineDoc = JsonDocument.Parse(File.ReadAllText(baselineFile));
        var baseline = baselineDoc.RootElement;
        bool failed = false;

        // 1. Generator exit code
        var expectedExit = baseline.GetProperty("generator_exit_code").GetInt32();
        if (!File.Exists(exitCodeFile))
        {
            Log.Error("BASELINE FAIL: generator_exit_code file missing");
            failed = true;
        }
        else
        {
            var actualExitStr = File.ReadAllText(exitCodeFile).Trim();
            if (!int.TryParse(actualExitStr, out var actualExit) || actualExit != expectedExit)
            {
                Log.Error("BASELINE FAIL: generator_exit_code: expected={Expected} actual={Actual}",
                    expectedExit, actualExitStr);
                failed = true;
            }
        }

        // 2-4. Coverage metrics (require coverage-matrix.json)
        if (!File.Exists(coverageFile))
        {
            Log.Error("BASELINE FAIL: coverage-matrix.json missing or unreadable");
            failed = true;
        }
        else
        {
            using var coverageDoc = JsonDocument.Parse(File.ReadAllText(coverageFile));
            var summary = coverageDoc.RootElement.GetProperty("summary");
            var mustPass = summary.GetProperty("must_pass");

            // must_pass_degraded
            var expectedDegraded = baseline.GetProperty("must_pass_degraded").GetInt32();
            var actualDegraded = mustPass.GetProperty("degraded").GetInt32();
            if (actualDegraded > expectedDegraded)
            {
                Log.Error("BASELINE FAIL: must_pass_degraded: expected<={Expected} actual={Actual}",
                    expectedDegraded, actualDegraded);
                failed = true;
            }

            // must_pass_compiled_out
            var expectedCo = baseline.GetProperty("must_pass_compiled_out").GetInt32();
            var actualCo = mustPass.GetProperty("compiled_out").GetInt32();
            if (actualCo > expectedCo)
            {
                Log.Error("BASELINE FAIL: must_pass_compiled_out: expected<={Expected} actual={Actual}",
                    expectedCo, actualCo);
                failed = true;
            }

            // known_unsupported_total
            var expectedUnsup = baseline.GetProperty("known_unsupported_total").GetInt32();
            var knownUnsupported = summary.GetProperty("known_unsupported");
            var actualUnsup = knownUnsupported.GetProperty("total").GetInt32();
            if (actualUnsup > expectedUnsup)
            {
                Log.Error("BASELINE FAIL: known_unsupported_total: expected<={Expected} actual={Actual}",
                    expectedUnsup, actualUnsup);
                failed = true;
            }
        }

        // 5. Wrapper stripped count (with +2 tolerance)
        var expectedStrip = baseline.GetProperty("wrapper_stripped_count").GetInt32();
        if (!File.Exists(stripCountFile))
        {
            Log.Warning("BASELINE WARN: wrapper-stripped-count file missing (async wrapper may not have been built)");
        }
        else
        {
            var actualStripStr = File.ReadAllText(stripCountFile).Trim();
            if (int.TryParse(actualStripStr, out var actualStrip))
            {
                const int tolerance = 2;
                var maxStrip = expectedStrip + tolerance;
                if (actualStrip > maxStrip)
                {
                    Log.Error("BASELINE FAIL: wrapper_stripped_count: expected<={MaxStrip} actual={Actual} (baseline={Expected} +{Tolerance} tolerance)",
                        maxStrip, actualStrip, expectedStrip, tolerance);
                    failed = true;
                }
            }
        }

        if (failed)
        {
            Log.Error("");
            Log.Error("Baseline check failed. If these changes are intentional, update baselines.json.");
            throw new Exception("Baseline check failed");
        }

        Log.Information("All baselines OK.");
    }

    // ============================================================
    // Runtime Tests on Simulator (called from Test target)
    //
    // Invokes the simulator runtime tests with skip-regen since
    // bindings were already generated by the regression suite.
    // ============================================================

    void RunRuntimeTestsOnSimulator()
    {
        Log.Information("");
        Log.Information("=== BindingTests Runtime Tests ===");

        // Bindings are already current from the regression suite — assert not stale
        AssertBindingsNotStale();

        // Build RuntimeTestsApp
        Log.Information("--- Building RuntimeTestsApp ---");
        DotNetBuild(s => s
            .SetProjectFile(BindingTestsDir / "RuntimeTestsApp")
            .SetConfiguration("Debug")
            .SetVerbosity(DotNetVerbosity.quiet));

        var appBundle = BindingTestsDir / "RuntimeTestsApp" / "bin" / "Debug" /
            "net10.0-ios" / "iossimulator-arm64" / "RuntimeTestsApp.app";

        if (!Directory.Exists(appBundle))
            throw new Exception("Build failed - app bundle not found");

        Log.Information("Build successful.");

        // Inject native artifacts
        var appFrameworks = appBundle / "Frameworks";
        InjectRuntimeDylib(appFrameworks);
        InjectAsyncWrapper(appFrameworks);
        InjectDependencyFramework(appFrameworks);
        InjectDependencyWrapper(appFrameworks);

        // Run on simulator (timeout 90s, matching run-tests.sh)
        RunOnSimulator();
    }

    // ============================================================
    // Environment Detection Helpers
    // ============================================================

    static bool HasXcrun()
    {
        try
        {
            var process = ProcessTasks.StartProcess(
                "which", "xcrun",
                logOutput: false, logInvocation: false);
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    static bool HasAvailableSimulator()
    {
        try
        {
            var process = ProcessTasks.StartProcess(
                "xcrun", "simctl list devices available -j",
                logOutput: false, logInvocation: false);
            process.WaitForExit();
            if (process.ExitCode != 0) return false;

            var json = string.Join("", process.Output.Where(o => o.Type == OutputType.Std).Select(o => o.Text));
            using var doc = JsonDocument.Parse(json);
            var devices = doc.RootElement.GetProperty("devices");

            foreach (var runtime in devices.EnumerateObject())
            {
                if (!runtime.Name.Contains("iOS") && !runtime.Name.Contains("iphone", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var device in runtime.Value.EnumerateArray())
                {
                    if (device.TryGetProperty("isAvailable", out var avail) && avail.GetBoolean() &&
                        device.TryGetProperty("name", out var name) && name.GetString()!.Contains("iPhone"))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
