// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices.Swift;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// End-to-end gate for REMEDIATION-PLAN §6: an *optional* escaping throwing closure
/// returning Void as a method/initializer parameter — `((T) throws -> Void)? = nil`.
/// Before the handler-layer error-mint registration fix, the per-module helper
/// `SBW_CreateError_{module}` went unregistered on the native `_optbuf`/default-parameter/
/// non-optional-setter forwarding paths; the wrapper-symbol contract gate then rejected
/// the throwing-closure callback's P/Invoke, the co-gater stripped the
/// `[UnmanagedCallersOnly]` callback method, and its dangling `s_&lt;cb&gt;` field plus
/// call-site produced CS0103 — breaking Alamofire (`Session.upload(…requestModifier:)`)
/// and YouTubePlayerKit (`init(htmlProvider:)` + the `HtmlProvider` setter) at the compile
/// gate. The compile gate alone proves the regression is fixed; these runtime tests
/// additionally exercise the value round-trip.
///
/// The closure-invoking tests are <see cref="SkipOnSimulatorAttribute"/> because a callback
/// carrying a `SwiftError*` out-parameter trips the Mono JIT `!ji-&gt;async` assertion
/// (upstream Issue 1) — they run under NativeAOT on device. The nil-path and default-param
/// tests never invoke a managed callback, so they run everywhere.
/// </summary>
public class OptionalThrowingVoidClosureTests : TestBase
{
    public OptionalThrowingVoidClosureTests(TestResults results) : base(results) { }

    #region Nil / default-parameter paths (no managed callback invoked — run everywhere)

    public void TestRunWithOptionalModifier_Nil_ReturnsFalse()
    {
        // Alamofire shape: free function with an optional throwing-void closure param
        // defaulting to nil. Passing null exercises the binding without invoking a callback,
        // so this is the durable runtime witness that the regression's binding compiles and
        // is callable on every platform. Swift returns false for the nil branch.
        var result = TestLibFunctions.RunWithOptionalModifier(
            5, (global::System.Func<RequestConfig, Swift.SwiftResult<Swift.SwiftVoid, SwiftError>>?)null);
        AssertFalse(result, "RunWithOptionalModifier(nil) returns false");
        TestLogger.Info($"RunWithOptionalModifier(nil) = {result}");
    }

    public void TestHolderDefaultCtor_RunValidator_ReturnsTrue()
    {
        // YTPK init shape with both parameters defaulted: the non-optional `validator`
        // resolves to the Swift default closure `{ }` (the default-parameter shim bypass
        // site), and `modifier` defaults to nil. RunValidator invokes the *Swift* default
        // closure — no managed callback — so it round-trips on simulator and device.
        var holder = new OptionalThrowingModifierHolder();
        var result = holder.RunValidator();
        AssertTrue(result, "Default-parameter validator runs without throwing");
        TestLogger.Info($"OptionalThrowingModifierHolder().RunValidator() = {result}");
    }

    public void TestHolderDefaultCtor_RunStoredModifier_NilReturnsFalse()
    {
        // Initializer with the optional throwing-void closure defaulted to nil (the YTPK
        // `init(htmlProvider:)` _optbuf path). The stored modifier is nil, so RunStoredModifier
        // takes the nil branch without invoking a callback — safe everywhere.
        var holder = new OptionalThrowingModifierHolder();
        var result = holder.RunStoredModifier(7);
        AssertFalse(result, "RunStoredModifier with nil init modifier returns false");
        TestLogger.Info($"OptionalThrowingModifierHolder().RunStoredModifier(7) = {result}");
    }

    #endregion

    #region Closure invocation — success paths (managed callback with SwiftError*)

    [SkipOnSimulator("Mono JIT async assertion (upstream Issue 1) — callback with SwiftError* triggers !ji->async assertion; runs under NativeAOT")]
    public void TestRunWithOptionalModifier_Success()
    {
        // Alamofire shape with a supplied closure that cooperatively succeeds. Exercises the
        // throwing-closure callback whose catch block would mint via SBW_CreateError — the
        // exact wrapper symbol the regression left unregistered.
        var result = TestLibFunctions.RunWithOptionalModifier(
            5, _cfg => Swift.SwiftResult<Swift.SwiftVoid, SwiftError>.FromSuccess(Swift.SwiftVoid.Value));
        AssertTrue(result, "RunWithOptionalModifier(success) returns true");
        TestLogger.Info($"RunWithOptionalModifier(success) = {result}");
    }

    [SkipOnSimulator("Mono JIT async assertion (upstream Issue 1) — callback with SwiftError* triggers !ji->async assertion; runs under NativeAOT")]
    public void TestHolder_InitModifier_Success()
    {
        // YTPK `init(htmlProvider:)` shape: an optional throwing-void closure supplied at
        // construction (the _optbuf forward path), then invoked from Swift via RunStoredModifier.
        var holder = new OptionalThrowingModifierHolder(
            () => Swift.SwiftResult<Swift.SwiftVoid, SwiftError>.FromSuccess(Swift.SwiftVoid.Value),
            _cfg => Swift.SwiftResult<Swift.SwiftVoid, SwiftError>.FromSuccess(Swift.SwiftVoid.Value));
        var result = holder.RunStoredModifier(9);
        AssertTrue(result, "Init-supplied modifier runs without throwing");
        TestLogger.Info($"InitModifier RunStoredModifier(9) = {result}");
    }

    [SkipOnSimulator("Mono JIT async assertion (upstream Issue 1) — callback with SwiftError* triggers !ji->async assertion; runs under NativeAOT")]
    public void TestHolder_SetOnComplete_Success()
    {
        // Settable OPTIONAL throwing-void closure property (optional closure-setter branch).
        var holder = new OptionalThrowingModifierHolder();
        holder.OnComplete = () => Swift.SwiftResult<Swift.SwiftVoid, SwiftError>.FromSuccess(Swift.SwiftVoid.Value);
        var result = holder.RunOnComplete();
        AssertTrue(result, "OnComplete setter closure runs without throwing");
        TestLogger.Info($"SetOnComplete RunOnComplete() = {result}");
    }

    #endregion

    #region Closure invocation — graceful fault (managed delegate throws)

    // These cover the direction where the C# delegate *throws* a managed exception instead of
    // cooperatively returning SwiftResult.FromFailure. The throwing-closure callback must catch
    // it at the [UnmanagedCallersOnly] boundary and mint a Swift error via SBW_CreateError_{module}
    // (the symbol the regression stripped) rather than letting it unwind into native Swift
    // (SIGABRT). The Swift `do/catch` then yields the sentinel false.

    [SkipOnSimulator("Mono JIT async assertion (upstream Issue 1) — callback with SwiftError* triggers !ji->async assertion; runs under NativeAOT")]
    public void TestRunWithOptionalModifier_DelegateThrows_GracefulFault()
    {
        var result = TestLibFunctions.RunWithOptionalModifier(
            5, _cfg => throw new InvalidOperationException("cs-boom-optmodifier"));
        AssertFalse(result,
            "Throwing C# delegate must surface as a Swift error → Swift catch → false, never SIGABRT");
        TestLogger.Info($"RunWithOptionalModifier(delegate throws) = {result}");
    }

    [SkipOnSimulator("Mono JIT async assertion (upstream Issue 1) — callback with SwiftError* triggers !ji->async assertion; runs under NativeAOT")]
    public void TestHolder_SetValidator_DelegateThrows_GracefulFault()
    {
        // Settable NON-OPTIONAL throwing-void closure property — the YTPK `HtmlProvider`
        // setter bypass site. A throwing managed delegate must mint a Swift error rather than
        // abort the process; Swift's catch yields the sentinel false.
        var holder = new OptionalThrowingModifierHolder();
        holder.Validator = () => throw new InvalidOperationException("cs-boom-validator");
        var result = holder.RunValidator();
        AssertFalse(result,
            "Throwing validator delegate (non-optional setter) must surface as a Swift error → false, never SIGABRT");
        TestLogger.Info($"SetValidator(delegate throws) RunValidator() = {result}");
    }

    #endregion
}
