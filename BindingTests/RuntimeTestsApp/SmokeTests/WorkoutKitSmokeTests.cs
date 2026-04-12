// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if WORKOUTKIT_SMOKE
using System;
using System.Runtime.Versioning;
using RuntimeTestsApp.Infrastructure;
using WorkoutKit;

namespace RuntimeTestsApp.SmokeTests;

/// <summary>
/// Smoke test for the Apple-framework direct-mode pipeline on WorkoutKit.
/// Consumes the externally-built <c>WorkoutKit.Swift.iOS.dll</c> +
/// <c>WorkoutKitSwiftBindings.xcframework</c> from the gitignored in-tree snapshot at
/// <c>BindingTests/obj/WorkoutKitSnapshot/</c> and exercises metadata-only assertions.
///
/// Gated by <c>WORKOUTKIT_SMOKE</c>. Regenerate with
/// <c>nuke regenerate-apple-snapshot --framework WorkoutKit</c>.
///
/// <b>Deliberately excluded:</b> Anything requiring HealthKit authorization or an
/// active workout session. This smoke test is strictly metadata-only.
/// </summary>
public class WorkoutKitSmokeTests : TestBase
{
    public WorkoutKitSmokeTests(TestResults results) : base(results) { }

    /// <summary>
    /// Verifies that the <c>CustomWorkout</c> type loads successfully from the
    /// generated WorkoutKit binding — proves the end-to-end pipeline (generator →
    /// emitter → wrapper → dylib) is alive for WorkoutKit.
    /// </summary>
    [SupportedOSPlatform("ios17.0")]
    public void TestCustomWorkoutTypeLoads()
    {
        try
        {
            var type = typeof(WorkoutKit.CustomWorkout);
            TestLogger.Info($"typeof(WorkoutKit.CustomWorkout) = {type.FullName}");
            AssertTrue(type is not null,
                "WorkoutKit.CustomWorkout type must be loadable from the generated binding.");
            AssertTrue(type.FullName!.Contains("CustomWorkout"),
                "WorkoutKit.CustomWorkout full name must contain 'CustomWorkout'.");
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// Verifies that the <c>WorkoutPlan</c> type loads successfully, exercising a
    /// second top-level WorkoutKit type to confirm the binding covers the core
    /// workout planning surface.
    /// </summary>
    [SupportedOSPlatform("ios17.0")]
    public void TestWorkoutPlanTypeLoads()
    {
        try
        {
            var type = typeof(WorkoutKit.WorkoutPlan);
            TestLogger.Info($"typeof(WorkoutKit.WorkoutPlan) = {type.FullName}");
            AssertTrue(type is not null,
                "WorkoutKit.WorkoutPlan type must be loadable from the generated binding.");
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    private static void LogExceptionChain(Exception ex)
    {
        var inner = ex;
        var depth = 0;
        while (inner != null)
        {
            TestLogger.Info($"  [ex{depth}] {inner.GetType().FullName}: {inner.Message}");
            if (inner.StackTrace != null)
                TestLogger.Info($"  [ex{depth}] stack: {inner.StackTrace}");
            inner = inner.InnerException;
            depth++;
        }
    }
}

#endif
