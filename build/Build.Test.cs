// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.Test.cs — Unit, analyzer, and runtime library tests.
// End-to-end BindingTests compile + runtime gates are handled by the
// consolidated BindingTests target (see Build.BindingTests.cs).

using System;
using System.Linq;
using System.Xml.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    string DotNetHostPath => ToolPathResolver.GetPathExecutable("dotnet");

    Target UnitTests => _ => _
        .DependsOn(Compile)
        .After(ValidateAppleTypesManifest)
        .Executes(() =>
        {
            var resultsDir = SourceDir / "Swift.Bindings" / "tests" / "UnitTests" / "TestResults";
            DotNetTest(s => s
                .SetProjectFile(SourceDir / "Swift.Bindings" / "tests" / "UnitTests")
                .EnableNoBuild()
                .SetConfiguration("Debug")
                .SetResultsDirectory(resultsDir)
                .AddLoggers("trx;LogFileName=unit-tests.trx")
                .SetProcessAdditionalArguments($"-- RunConfiguration.DotNetHostPath=\"{DotNetHostPath}\""));

            // DotNetTest above already throws on any FAILED test. This floor adds the other half of
            // the Finding 28 ratchet at the unit layer: catch a silent DROP in passing tests — a
            // [Fact]/[Theory] deleted or renamed, or a whole file dropped from compilation — on an
            // otherwise-green run, which a failure-only gate cannot see.
            EnforceUnitTestPassFloor(resultsDir / "unit-tests.trx");
        });

    Target AnalyzerTests => _ => _
        .DependsOn(Compile)
        .After(UnitTests)
        .Executes(() =>
        {
            DotNetTest(s => s
                .SetProjectFile(SourceDir / "Swift.Analyzers.Tests")
                .EnableNoBuild()
                .SetConfiguration("Debug")
                .AddLoggers("trx;LogFileName=analyzer-tests.trx")
                .SetProcessAdditionalArguments($"-- RunConfiguration.DotNetHostPath=\"{DotNetHostPath}\""));
        });

    Target RuntimeUnitTests => _ => _
        .DependsOn(Compile)
        .After(AnalyzerTests)
        .Executes(() =>
        {
            DotNetTest(s => s
                .SetProjectFile(SourceDir / "Swift.Runtime" / "tests")
                .EnableNoBuild()
                .SetConfiguration("Debug")
                .AddLoggers("trx;LogFileName=runtime-lib-tests.trx")
                .SetProcessAdditionalArguments($"-- RunConfiguration.DotNetHostPath=\"{DotNetHostPath}\""));
        });

    // ============================================================
    // Test — full test suite matching run-tests.sh EXACTLY
    // ============================================================

    Target Test => _ => _
        .DependsOn(UnitTests, RuntimeUnitTests, AnalyzerTests)
        .After(ValidateAppleTypesManifest)
        .ProceedAfterFailure()
        .Executes(() =>
        {
            // Unit tests, analyzer tests, and runtime unit tests are run via DependsOn.
            // BindingTests regression + simulator/device/macOS/catalyst/tvOS runtime
            // gates are handled by the consolidated BindingTests target (and by the
            // separate CI job). No need to duplicate that work here.
            Log.Information("All test suites complete.");
        });

    /// <summary>
    /// Enforces (and auto-raises) the <c>Swift.Bindings.Unit.Tests</c> pass-count floor stored in
    /// <c>validation-baseline.json</c>'s <c>unit_tests</c> block. Mirrors the runtime baseline's
    /// raise-only auto-update: a passing count below the floor throws (a test silently vanished); a
    /// higher count ratchets the floor up; an absent floor seeds itself from the first run.
    /// </summary>
    void EnforceUnitTestPassFloor(AbsolutePath trxPath)
    {
        if (!trxPath.FileExists())
        {
            Log.Warning("Unit-test trx not found at {Path}; skipping pass-count floor.", trxPath);
            return;
        }

        var passed = ParseTrxPassedCount(trxPath);
        if (passed < 0)
        {
            Log.Warning("Could not parse passed count from {Path}; skipping pass-count floor.", trxPath);
            return;
        }

        var baseline = ValidationBaseline.Load(BaselinePath);
        var floor = baseline.UnitTests?.SwiftBindingsUnitPassFloor ?? 0;

        Log.Information("");
        Log.Information("=== UNIT TEST PASS FLOOR (Swift.Bindings.Unit.Tests) ===");
        Log.Information("  Floor:   {Floor}", floor);
        Log.Information("  Current: {Passed}", passed);

        if (passed < floor)
            throw new Exception(
                $"Unit test regression: Swift.Bindings.Unit.Tests passing count dropped from {floor} " +
                $"to {passed} (-{floor - passed}). A [Fact]/[Theory] was removed or stopped being discovered.");

        if (passed > floor)
        {
            var updated = baseline with
            {
                UnitTests = new ValidationBaseline.UnitTestsBaseline { SwiftBindingsUnitPassFloor = passed }
            };
            updated.Save(BaselinePath);
            Log.Information("Unit test pass floor {Action}: {Floor} -> {Passed}",
                floor == 0 ? "seeded" : "auto-raised", floor, passed);
        }
        else
        {
            Log.Information("Unit test pass floor holds at {Floor}.", floor);
        }
    }

    /// <summary>Reads the <c>passed</c> counter from a VSTest <c>.trx</c> file (-1 if unparseable).</summary>
    static int ParseTrxPassedCount(AbsolutePath trxPath)
    {
        try
        {
            var doc = XDocument.Load(trxPath);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            var counters = doc.Descendants(ns + "Counters").FirstOrDefault();
            var passedAttr = counters?.Attribute("passed")?.Value;
            return int.TryParse(passedAttr, out var p) ? p : -1;
        }
        catch
        {
            return -1;
        }
    }
}
