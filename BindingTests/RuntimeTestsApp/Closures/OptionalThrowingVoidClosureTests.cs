// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices.Swift;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// End-to-end gate for an *optional* escaping throwing closure returning Void as a
/// method/initializer parameter — `((T) throws -> Void)? = nil`.
/// Before the handler-layer error-mint registration fix, the per-module helper
/// `SBW_CreateError_{module}` went unregistered on the native `_optbuf`/default-parameter/
/// non-optional-setter forwarding paths; the wrapper-symbol contract gate then rejected
/// the throwing-closure callback's P/Invoke, the co-gater stripped the
/// `[UnmanagedCallersOnly]` callback method, and its dangling `s_&lt;cb&gt;` field plus
/// call-site produced CS0103 — failing to compile optional throwing closure parameters like
/// `Session.upload(…requestModifier:)` and `init(htmlProvider:)` (with `HtmlProvider` setter) at the compile
/// gate. The compile gate alone proves the regression is fixed; these runtime tests
/// additionally exercise the value round-trip.
///
/// All the optional/Cdecl-routed members here — `RunWithOptionalModifier`, the
/// `OptionalThrowingModifierHolder` initializer, the optional `OnComplete` setter, and the
/// `RunStoredModifier`/`RunOnComplete` methods — bind through SBW_ `@_cdecl` wrappers with
/// `[UnmanagedCallersOnly(CallConvCdecl)]` callbacks (verify in output/SwiftBindingsTestLib.cs).
/// They have no CallConvSwift frame, so Mono Issue 1 (`!ji-&gt;async`) cannot apply and they run
/// on BOTH simulator and device.
///
/// The ONE remaining simulator skip is <see cref="TestHolder_SetValidator_DelegateThrows_GracefulFault"/>:
/// the NON-optional `Validator` setter is the video-player library `HtmlProvider` bypass site that
/// emits a genuine CallConvSwift P/Invoke (`$s…OptionalThrowingModifierHolderC9validatoryyKcvs`,
/// `SwiftSelf` self, `delegate* unmanaged[Swift]` callback) instead of routing through the
/// `@_cdecl` wrapper — so it is the only path here with a CallConvSwift frame. The durable fix is
/// to funnel that setter through a wrapper; until then it runs under NativeAOT on device only.
/// </summary>
public class OptionalThrowingVoidClosureTests : TestBase
{
    public OptionalThrowingVoidClosureTests(TestResults results) : base(results) { }

    #region Nil / default-parameter paths (no managed callback invoked — run everywhere)

    public void TestRunWithOptionalModifier_Nil_ReturnsFalse()
    {
        // Free function with an optional throwing-void closure param
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
        // Video-player library init shape with both parameters defaulted: the non-optional `validator`
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
        // Initializer with the optional throwing-void closure defaulted to nil (the video-player
        // library `init(htmlProvider:)` _optbuf path). The stored modifier is nil, so RunStoredModifier
        // takes the nil branch without invoking a callback — safe everywhere.
        var holder = new OptionalThrowingModifierHolder();
        var result = holder.RunStoredModifier(7);
        AssertFalse(result, "RunStoredModifier with nil init modifier returns false");
        TestLogger.Info($"OptionalThrowingModifierHolder().RunStoredModifier(7) = {result}");
    }

    #endregion

    #region Closure invocation — success paths (managed callback with SwiftError*)

    public void TestRunWithOptionalModifier_Success()
    {
        // A supplied optional throwing-void closure that cooperatively succeeds. Exercises the
        // throwing-closure callback whose catch block would mint via SBW_CreateError — the
        // exact wrapper symbol the regression left unregistered.
        var result = TestLibFunctions.RunWithOptionalModifier(
            5, _cfg => Swift.SwiftResult<Swift.SwiftVoid, SwiftError>.FromSuccess(Swift.SwiftVoid.Value));
        AssertTrue(result, "RunWithOptionalModifier(success) returns true");
        TestLogger.Info($"RunWithOptionalModifier(success) = {result}");
    }

    public void TestHolder_InitModifier_Success()
    {
        // Video-player library `init(htmlProvider:)` shape: an optional throwing-void closure supplied at
        // construction (the _optbuf forward path), then invoked from Swift via RunStoredModifier.
        var holder = new OptionalThrowingModifierHolder(
            () => Swift.SwiftResult<Swift.SwiftVoid, SwiftError>.FromSuccess(Swift.SwiftVoid.Value),
            _cfg => Swift.SwiftResult<Swift.SwiftVoid, SwiftError>.FromSuccess(Swift.SwiftVoid.Value));
        var result = holder.RunStoredModifier(9);
        AssertTrue(result, "Init-supplied modifier runs without throwing");
        TestLogger.Info($"InitModifier RunStoredModifier(9) = {result}");
    }

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

    #region Closure RETURN direction — throwing getter with by-value struct arg

    // These exercise the OTHER direction of the same struct-arg throwing-closure shape: the
    // `configValidator` gettable property hands C# a delegate backed by a Swift func-ptr, and each
    // C# invocation marshals the by-value `RequestConfig` struct TO Swift through that func-ptr.
    // The throwing-closure RETURN invoker previously emitted a bare `_arg0` struct value into a
    // `void*`/struct-pointer func-ptr slot → CS1503 at the compile gate. The fix routes the struct
    // arg through the same metadata + buffer + MarshalToSwift prologue the non-throwing struct-param
    // closure paths use; compiling these tests proves the CS1503 is gone, and the round-trip below
    // proves the struct argument reaches Swift intact.

    public void TestHolder_ConfigValidator_Success_StructArgRoundTrips()
    {
        // Reading the property returns a `Func<RequestConfig, SwiftResult<SwiftVoid, SwiftError>>`
        // whose body is a Swift closure. Invoking it with a positive timeout records the value into
        // the holder, so `LastObservedTimeout` proves the by-value struct argument round-tripped TO
        // Swift through the throwing-closure RETURN invoker.
        var holder = new OptionalThrowingModifierHolder();
        var validator = holder.ConfigValidator;
        AssertNotNull(validator, "configValidator getter returns a non-null delegate");

        var result = validator(new RequestConfig(42));
        AssertTrue(result.IsSuccess, "Positive timeout does not throw → SwiftResult.IsSuccess");
        AssertFalse(result.IsFailure, "Positive timeout is not the failure case");
        AssertEqual(42, holder.LastObservedTimeout,
            "By-value RequestConfig struct arg round-tripped TO Swift through the throwing-closure return func-ptr");
        TestLogger.Info($"ConfigValidator(42) success; LastObservedTimeout = {holder.LastObservedTimeout}");
    }

    public void TestHolder_ConfigValidator_NonPositive_SurfacesError()
    {
        // Same returned delegate, failure branch: a non-positive timeout makes the Swift closure
        // `throw ConfigError.invalidTimeout`. The throwing-closure return invoker must surface that as
        // a SwiftResult.Failure (via the errorOut pointer), never an unhandled native unwind. The
        // struct arg still had to marshal correctly for Swift to evaluate the `<= 0` guard.
        var holder = new OptionalThrowingModifierHolder();
        var validator = holder.ConfigValidator;

        var result = validator(new RequestConfig(0));
        AssertTrue(result.IsFailure, "Non-positive timeout throws → SwiftResult.IsFailure");
        AssertFalse(result.IsSuccess, "Non-positive timeout is not the success case");
        AssertEqual(0, holder.LastObservedTimeout,
            "Throwing branch never reached the assignment, so LastObservedTimeout is unchanged");
        TestLogger.Info($"ConfigValidator(0) failure surfaced as SwiftResult; LastObservedTimeout = {holder.LastObservedTimeout}");
    }

    #endregion

    #region Closure RETURN direction — NON-throwing getter with by-value struct arg

    // The struct-arg closure RETURN fix routes both throwing and non-throwing closures through the
    // @_cdecl invoke thunk. Before the fix, a non-throwing struct-arg closure return was dispatched
    // to the raw `delegate* unmanaged[Swift]` lambda struct path (an untested latent SIGSEGV —
    // native calls from a display-class method crash Mono JIT with !ji->async). These guard the
    // non-throwing reroute through the safe CallConvCdecl invoker class, covering both the
    // non-frozen (NativeMemory + InitializeWithCopy) and frozen (stackalloc + MarshalToSwift) branches.

    public void TestHolder_ConfigEcho_NonFrozenStructArg_RoundTrips()
    {
        // Non-frozen `RequestConfig` arg through the non-throwing return invoker: the buffer is
        // heap-allocated, InitializeWithCopy'd from the C# class payload, passed to the Cdecl thunk,
        // then Destroy+Free'd. The echoed Int32 proves the struct argument reached Swift intact.
        var holder = new OptionalThrowingModifierHolder();
        var echo = holder.ConfigEcho;
        AssertNotNull(echo, "configEcho getter returns a non-null delegate");

        var result = echo(new RequestConfig(99));
        AssertEqual(99, result,
            "Non-frozen RequestConfig struct arg round-tripped TO Swift through the non-throwing closure-return invoker");
        TestLogger.Info($"ConfigEcho(99) = {result}");
    }

    public void TestHolder_PointEcho_FrozenStructArg_RoundTrips()
    {
        // Frozen `FrozenPoint` arg through the non-throwing return invoker: the buffer is stackalloc'd
        // and MarshalToSwift'd (no heap cleanup). The summed coordinates prove the struct argument
        // reached Swift intact via the frozen marshalling branch.
        var holder = new OptionalThrowingModifierHolder();
        var echo = holder.PointEcho;
        AssertNotNull(echo, "pointEcho getter returns a non-null delegate");

        var result = echo(new FrozenPoint(3.0, 4.0));
        AssertEqual(7.0, result,
            "Frozen FrozenPoint struct arg round-tripped TO Swift through the non-throwing closure-return invoker");
        TestLogger.Info($"PointEcho((3,4)) = {result}");
    }

    #endregion

    #region Closure invocation — graceful fault (managed delegate throws)

    // These cover the direction where the C# delegate *throws* a managed exception instead of
    // cooperatively returning SwiftResult.FromFailure. The throwing-closure callback must catch
    // it at the [UnmanagedCallersOnly] boundary and mint a Swift error via SBW_CreateError_{module}
    // (the symbol the regression stripped) rather than letting it unwind into native Swift
    // (SIGABRT). The Swift `do/catch` then yields the sentinel false.

    // Runs everywhere: RunWithOptionalModifier is a pure-CallConvCdecl SBW_ wrapper with a
    // [UnmanagedCallersOnly(CallConvCdecl)] callback that catches the managed throw and mints
    // a Swift error — no CallConvSwift frame, so no Issue-1 surface.
    public void TestRunWithOptionalModifier_DelegateThrows_GracefulFault()
    {
        // Typed as the primary's delegate: a throw-expression lambda converts to both the primary and
        // its Action convenience sibling, and this test targets the primary's boundary.
        Func<SwiftBindingsTestLib.RequestConfig, Swift.SwiftResult<Swift.SwiftVoid, SwiftError>> thrower =
            _cfg => throw new InvalidOperationException("cs-boom-optmodifier");
        var result = TestLibFunctions.RunWithOptionalModifier(5, thrower);
        AssertFalse(result,
            "Throwing C# delegate must surface as a Swift error → Swift catch → false, never SIGABRT");
        TestLogger.Info($"RunWithOptionalModifier(delegate throws) = {result}");
    }

    // CallConvSwift entry point on this path: $s20SwiftBindingsTestLib30OptionalThrowingModifierHolderC9validatoryyKcvs
    [SkipOnMonoJit("upstream Issue 1 (!ji->async, jit-info.c:918) — the non-optional Validator setter is the CallConvSwift closure-property bypass (PInvoke_validator_Set_*: SwiftSelf self, delegate* unmanaged[Swift] callback), so a managed throw inside the [UnmanagedCallersOnly(CallConvSwift)] callback can unwind through a CallConvSwift frame. Mono-only (Simulator + Catalyst); runs on macOS (CoreCLR) and under NativeAOT on device. CallConvSwift entry: $s20SwiftBindingsTestLib30OptionalThrowingModifierHolderC9validatoryyKcvs")]
    public void TestHolder_SetValidator_DelegateThrows_GracefulFault()
    {
        // Settable NON-OPTIONAL throwing-void closure property — the video-player library `HtmlProvider`
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
