// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Parameters;

/// <summary>
/// End-to-end gate for REMEDIATION-PLAN §6: a small blittable Optional parameter
/// (<c>Int32?</c>) is correctly DECODED when its enclosing method is lowered to a
/// <c>@_cdecl</c> wrapper. In the live generator both methods are claimed by
/// MethodWrapperEmitter (they compile to <c>_sbw_method_&lt;hash&gt;</c> wrappers), which always
/// maps params with <c>omitLabels: false</c>; the wrapper body decodes the optional via the tag
/// byte (<c>let nOpt: Int32? = n.advanced(by: 4).load(as: UInt8.self) == 0 ? n.load(as: Int32.self) : nil</c>)
/// and forwards <c>nOpt</c>. These round-trips therefore pass with no source change — they are
/// the durable runtime gate for the MethodWrapperEmitter decode path, NOT a reachable repro of
/// the §6 fallback-branch defect. That defect lives in the <c>else if (useCdecl)</c> branch of
/// the two FALLBACK emitters (<c>ClosureEmitter.SwiftWrapper</c> /
/// <c>OptionalPointerWrapperEmitter</c>), which only run when MethodWrapperEmitter has NOT
/// claimed the method (<c>!UsesWrapperLibrary</c>) — unreachable by any compilable Swift shape
/// today, hence latent. That latent branch (previously <c>omitLabels: true</c>, which would
/// forward a bare <c>UnsafeRawPointer</c> and strip the wrapper) is hardened and pinned directly
/// by the emitter unit tests in <c>OptionalPointerWrapperTests</c>.
///
/// Two method shapes are covered: a closure-bearing method
/// (<c>addOptionalWithClosure</c>) and a large-optional-bearing method
/// (<c>addOptionalWithLargeOptional</c>, with a large <c>BigPoint?</c>). Each is round-tripped
/// with a non-nil value (proves the optional decoded to the right value, not garbage) and with
/// nil (proves the decoded nil-branch). Neither callback carries a <c>SwiftError*</c>, so both
/// run on simulator (Mono JIT) and device (NativeAOT).
/// </summary>
public class BlittableOptionalCdeclWrapperTests : TestBase
{
    public BlittableOptionalCdeclWrapperTests(TestResults results) : base(results) { }

    #region Closure-bearing method (small Int32? decoded alongside @escaping closure)

    public void TestAddOptionalWithClosure_NonNil_DecodesValue()
    {
        var box = new BlittableOptionalBox(10);
        int callbackCount = 0;
        // 10 + 42 = 52; a mis-decoded Int32? (raw bytes / wrong nil branch) returns a wrong value here.
        var result = box.AddOptionalWithClosure(42, () => callbackCount++);
        AssertEqual(52, result, "AddOptionalWithClosure(42) decodes the Int32? and adds it");
        AssertEqual(1, callbackCount, "closure invoked exactly once");
        TestLogger.Info($"AddOptionalWithClosure(42) = {result}, callbacks = {callbackCount}");
    }

    public void TestAddOptionalWithClosure_Nil_TakesNilBranch()
    {
        var box = new BlittableOptionalBox(10);
        int callbackCount = 0;
        // 10 + (nil ?? -1) = 9; distinguishes a decoded nil from a garbage non-nil.
        var result = box.AddOptionalWithClosure(null, () => callbackCount++);
        AssertEqual(9, result, "AddOptionalWithClosure(nil) takes the nil branch (seed - 1)");
        AssertEqual(1, callbackCount, "closure invoked exactly once on the nil path");
        TestLogger.Info($"AddOptionalWithClosure(nil) = {result}, callbacks = {callbackCount}");
    }

    #endregion

    #region Large-optional-bearing method (small Int32? decoded alongside large BigPoint?)

    public void TestAddOptionalWithLargeOptional_NonNil_DecodesValue()
    {
        var box = new BlittableOptionalBox(10);
        // 10 + 42 + (1+2+3) = 58; the large BigPoint? is widened to a pointer in the method
        // wrapper, where the small Int32? must still be decoded.
        var result = box.AddOptionalWithLargeOptional(42, new BigPoint(1, 2, 3));
        AssertEqual(58, result, "AddOptionalWithLargeOptional(42, BigPoint) decodes both optionals");
        TestLogger.Info($"AddOptionalWithLargeOptional(42, BigPoint(1,2,3)) = {result}");
    }

    public void TestAddOptionalWithLargeOptional_BothNil_TakesNilBranches()
    {
        var box = new BlittableOptionalBox(10);
        // 10 + (nil ?? -1) + (nil ?? 0) = 9.
        var result = box.AddOptionalWithLargeOptional(null, null);
        AssertEqual(9, result, "AddOptionalWithLargeOptional(nil, nil) takes both nil branches");
        TestLogger.Info($"AddOptionalWithLargeOptional(nil, nil) = {result}");
    }

    public void TestAddOptionalWithLargeOptional_SmallNilLargeSet_MixedBranches()
    {
        var box = new BlittableOptionalBox(10);
        // 10 + (nil ?? -1) + (10+20+30) = 69; small nil + large non-nil isolates the
        // small-optional decode from the large-optional widening.
        var result = box.AddOptionalWithLargeOptional(null, new BigPoint(10, 20, 30));
        AssertEqual(69, result, "AddOptionalWithLargeOptional(nil, BigPoint) decodes nil small + set large");
        TestLogger.Info($"AddOptionalWithLargeOptional(nil, BigPoint(10,20,30)) = {result}");
    }

    #endregion
}
