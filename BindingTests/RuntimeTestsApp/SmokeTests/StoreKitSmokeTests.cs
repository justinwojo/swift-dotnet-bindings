// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if STOREKIT_SMOKE
extern alias StoreKitSwift;

using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.SmokeTests;

/// <summary>
/// Session 5 end-to-end smoke test for the Apple-framework direct-mode pipeline:
/// consumes the externally-built <c>StoreKit.Swift.iOS.dll</c> + <c>StoreKitSwiftBindings.xcframework</c>
/// and calls one trivial, non-throwing, non-async StoreKit 2 accessor to prove the
/// whole chain (<c>SwiftFrameworkResolver</c> → wrapper dylib → system framework via dyld) resolves.
///
/// Gated by the <c>STOREKIT_SMOKE</c> compile symbol, which the csproj sets only when the
/// Session 4 artifacts exist at <c>/tmp/storekit2-session4</c> on an iOS Simulator build.
/// Regenerate them via the reproducer command in <c>src/docs/0.8.0-storekit2-exploration.md</c>
/// (Session 4 section) when re-running this on a fresh machine.
/// </summary>
public class StoreKitSmokeTests : TestBase
{
    public StoreKitSmokeTests(TestResults results) : base(results) { }

    /// <summary>
    /// The minimum viable success signal for the Apple-framework direct-mode pipeline:
    /// a single <c>LibraryImport("StoreKitSwiftBindings")</c> call resolves, the wrapper
    /// dylib pulls <c>/System/Library/Frameworks/StoreKit.framework/StoreKit</c> into
    /// the process as a transitive dependency (dyld resolves it via the absolute path
    /// baked into the wrapper's load commands at link time), the <c>@_cdecl</c> thunk
    /// runs inside the wrapper, calls <c>StoreKit.AppStore.canMakePayments</c> on the
    /// real StoreKit 2 API, and returns a plain <c>bool</c>.
    ///
    /// We specifically picked a primitive-return accessor rather than
    /// <c>AppStore.deviceVerificationID</c> (the originally planned candidate) because
    /// the latter returns <c>SwiftOptional&lt;System.Guid&gt;</c> whose cctor reaches
    /// <c>TypeMetadata.GetTypeMetadataOrThrow&lt;System.Guid&gt;()</c>, and
    /// <c>System.Guid → Foundation.UUID</c> is currently only mapped at generator time
    /// (<c>FoundationDatabase.xml</c>) — there is no runtime <c>RegisterMetadata</c>
    /// call for it. That's an orthogonal Swift.Runtime gap that deserves its own
    /// follow-up session; this smoke test exists solely to verify the resolver chain.
    ///
    /// The value itself is allowed to be either true or false — an iOS Simulator with
    /// no StoreKit configuration legitimately reports <c>false</c>. The assertion is
    /// solely "the call completed without DllNotFoundException /
    /// EntryPointNotFoundException".
    /// </summary>
    public void TestAppStoreCanMakePayments()
    {
        // The Microsoft.iOS ObjC bindings also expose StoreKit.AppStore, so we route
        // through the StoreKitSwift extern alias to pick the Swift-side type.
        try
        {
            bool canMakePayments = StoreKitSwift::StoreKit.AppStore.CanMakePayments;
            TestLogger.Info($"StoreKit.AppStore.CanMakePayments = {canMakePayments}");
            AssertTrue(true, "AppStore.CanMakePayments call completed without DllNotFound/EntryPointNotFound");
        }
        catch (System.Exception ex)
        {
            // Log the full exception chain so we can see what's actually wrong beyond
            // the wrapping TargetInvocationException the reflection invoker produces.
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
            throw;
        }
    }
}

#endif
