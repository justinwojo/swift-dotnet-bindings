// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if LIVECOMMUNICATIONKIT_SMOKE
using System;
using System.Runtime.Versioning;
using RuntimeTestsApp.Infrastructure;
using LiveCommunicationKit;

namespace RuntimeTestsApp.SmokeTests;

/// <summary>
/// Smoke test for the Apple-framework direct-mode pipeline on LiveCommunicationKit.
/// Consumes the externally-built <c>LiveCommunicationKit.Swift.iOS.dll</c> +
/// <c>LiveCommunicationKitSwiftBindings.xcframework</c> from the gitignored in-tree
/// snapshot at <c>BindingTests/obj/LiveCommunicationKitSnapshot/</c> and exercises
/// metadata-only assertions.
///
/// Gated by <c>LIVECOMMUNICATIONKIT_SMOKE</c>. Regenerate with
/// <c>nuke regenerate-apple-snapshot --framework LiveCommunicationKit</c>.
///
/// <b>Note:</b> LiveCommunicationKit requires <c>SupportedOSPlatformVersion=26.0</c>
/// in the snapshot csproj. Test methods are annotated with
/// <c>[SupportedOSPlatform("ios26.0")]</c>.
///
/// <b>Deliberately excluded:</b> Anything requiring an active VoIP session or
/// CallKit integration. This smoke test is strictly metadata-only.
/// </summary>
public class LiveCommunicationKitSmokeTests : TestBase
{
    public LiveCommunicationKitSmokeTests(TestResults results) : base(results) { }

    /// <summary>
    /// Verifies that the <c>Conversation</c> type loads successfully from the
    /// generated LiveCommunicationKit binding — proves the end-to-end pipeline is
    /// alive for LiveCommunicationKit.
    /// </summary>
    [SupportedOSPlatform("ios26.0")]
    public void TestConversationTypeLoads()
    {
        try
        {
            var type = typeof(LiveCommunicationKit.Conversation);
            TestLogger.Info($"typeof(LiveCommunicationKit.Conversation) = {type.FullName}");
            AssertTrue(type is not null,
                "LiveCommunicationKit.Conversation type must be loadable from the generated binding.");
            AssertTrue(type.FullName!.Contains("Conversation"),
                "LiveCommunicationKit.Conversation full name must contain 'Conversation'.");
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// Verifies that the <c>StartConversationAction</c> type loads successfully,
    /// exercising a second core LiveCommunicationKit type from the conversation
    /// action surface.
    /// </summary>
    [SupportedOSPlatform("ios26.0")]
    public void TestStartConversationActionTypeLoads()
    {
        try
        {
            var type = typeof(LiveCommunicationKit.StartConversationAction);
            TestLogger.Info($"typeof(LiveCommunicationKit.StartConversationAction) = {type.FullName}");
            AssertTrue(type is not null,
                "LiveCommunicationKit.StartConversationAction type must be loadable from the generated binding.");
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
