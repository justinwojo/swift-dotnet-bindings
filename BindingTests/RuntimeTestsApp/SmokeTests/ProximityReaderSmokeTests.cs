// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if PROXIMITYREADER_SMOKE
using System;
using System.Runtime.Versioning;
using RuntimeTestsApp.Infrastructure;
using ProximityReader;

namespace RuntimeTestsApp.SmokeTests;

/// <summary>
/// Smoke test for the Apple-framework direct-mode pipeline on ProximityReader.
/// Consumes the externally-built <c>ProximityReader.Swift.iOS.dll</c> +
/// <c>ProximityReaderSwiftBindings.xcframework</c> from the gitignored in-tree snapshot
/// at <c>BindingTests/obj/ProximityReaderSnapshot/</c> and exercises metadata-only
/// assertions.
///
/// Gated by <c>PROXIMITYREADER_SMOKE</c>. Regenerate with
/// <c>nuke regenerate-apple-snapshot --framework ProximityReader</c>.
///
/// <b>Deliberately excluded:</b> <c>MobileDocumentReaderError.errorDescription</c>
/// (known emitter bug — C# emits the P/Invoke but the Swift wrapper never emits
/// the <c>@_cdecl</c>, causing <c>EntryPointNotFoundException</c>). Also excluded:
/// anything requiring NFC hardware or a Tap to Pay session.
/// </summary>
public class ProximityReaderSmokeTests : TestBase
{
    public ProximityReaderSmokeTests(TestResults results) : base(results) { }

    /// <summary>
    /// Verifies that the <c>PaymentCardReadResult</c> type loads successfully from
    /// the generated ProximityReader binding — proves the end-to-end pipeline is
    /// alive for ProximityReader.
    /// </summary>
    [SupportedOSPlatform("ios17.4")]
    public void TestPaymentCardReadResultTypeLoads()
    {
        try
        {
            var type = typeof(ProximityReader.PaymentCardReadResult);
            TestLogger.Info($"typeof(ProximityReader.PaymentCardReadResult) = {type.FullName}");
            AssertTrue(type is not null,
                "ProximityReader.PaymentCardReadResult type must be loadable from the generated binding.");
            AssertTrue(type.FullName!.Contains("PaymentCardReadResult"),
                "ProximityReader.PaymentCardReadResult full name must contain 'PaymentCardReadResult'.");
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// Verifies that the <c>PaymentCardTransactionRequest</c> type loads successfully,
    /// exercising a second core ProximityReader type.
    /// </summary>
    [SupportedOSPlatform("ios17.4")]
    public void TestPaymentCardTransactionRequestTypeLoads()
    {
        try
        {
            var type = typeof(ProximityReader.PaymentCardTransactionRequest);
            TestLogger.Info($"typeof(ProximityReader.PaymentCardTransactionRequest) = {type.FullName}");
            AssertTrue(type is not null,
                "ProximityReader.PaymentCardTransactionRequest type must be loadable from the generated binding.");
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
