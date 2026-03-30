// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.Test.cs — Unit, analyzer, and runtime library tests.
// BindingTests and simulator runtime tests are handled by dedicated targets
// (BindingTests, BindingTestsStrict, RuntimeTestsSimulator).

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
                .AddLoggers("trx;LogFileName=unit-tests.trx")
                .SetProcessAdditionalArguments($"-- RunConfiguration.DotNetHostPath=\"{DotNetHostPath}\""));
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
        .ProceedAfterFailure()
        .Executes(() =>
        {
            // Unit tests, analyzer tests, and runtime unit tests are run via DependsOn.
            // BindingTests regression and simulator runtime tests are handled by the
            // dedicated BindingTests/BindingTestsStrict/RuntimeTestsSimulator targets
            // (and by the separate CI job). No need to duplicate that work here.
            Log.Information("All test suites complete.");
        });

}
