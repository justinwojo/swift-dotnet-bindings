// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if ROOMPLAN_SMOKE
using System;
using System.Runtime.Versioning;
using RuntimeTestsApp.Infrastructure;
using RoomPlan;

namespace RuntimeTestsApp.SmokeTests;

/// <summary>
/// Smoke test for the Apple-framework direct-mode pipeline on RoomPlan.
/// Consumes the externally-built <c>RoomPlan.Swift.iOS.dll</c> +
/// <c>RoomPlanSwiftBindings.xcframework</c> from the gitignored in-tree snapshot at
/// <c>BindingTests/obj/RoomPlanSnapshot/</c> and exercises metadata-only assertions.
///
/// Gated by <c>ROOMPLAN_SMOKE</c>. Regenerate with
/// <c>nuke regenerate-apple-snapshot --framework RoomPlan</c>.
///
/// <b>Deliberately excluded:</b> Anything requiring a LiDAR sensor or ARSession.
/// This smoke test is strictly metadata-only.
/// </summary>
public class RoomPlanSmokeTests : TestBase
{
    public RoomPlanSmokeTests(TestResults results) : base(results) { }

    /// <summary>
    /// Verifies that the <c>CapturedRoom</c> type loads successfully from the
    /// generated RoomPlan binding — proves the end-to-end pipeline is alive for
    /// RoomPlan.
    /// </summary>
    [SupportedOSPlatform("ios17.0")]
    public void TestCapturedRoomTypeLoads()
    {
        try
        {
            var type = typeof(RoomPlan.CapturedRoom);
            TestLogger.Info($"typeof(RoomPlan.CapturedRoom) = {type.FullName}");
            AssertTrue(type is not null,
                "RoomPlan.CapturedRoom type must be loadable from the generated binding.");
            AssertTrue(type.FullName!.Contains("CapturedRoom"),
                "RoomPlan.CapturedRoom full name must contain 'CapturedRoom'.");
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// Verifies that the <c>CapturedRoomData</c> type loads successfully, exercising
    /// a second core RoomPlan type.
    /// </summary>
    [SupportedOSPlatform("ios17.0")]
    public void TestCapturedRoomDataTypeLoads()
    {
        try
        {
            var type = typeof(RoomPlan.CapturedRoomData);
            TestLogger.Info($"typeof(RoomPlan.CapturedRoomData) = {type.FullName}");
            AssertTrue(type is not null,
                "RoomPlan.CapturedRoomData type must be loadable from the generated binding.");
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
