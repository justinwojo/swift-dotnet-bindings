// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.Test.cs — Unit, analyzer, and runtime library tests.
// End-to-end BindingTests compile + runtime gates are handled by the
// consolidated BindingTests target (see Build.BindingTests.cs).

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
        .After(ValidateAppleTypesManifest)
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

}
