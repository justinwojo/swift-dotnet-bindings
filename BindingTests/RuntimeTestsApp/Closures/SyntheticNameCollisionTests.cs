// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// P1-22 (C1): end-to-end coverage for the MethodClosureBridge synthetic-name guard. The @_cdecl
/// wrapper hardcodes synthetic Swift identifiers (`self_`, `selfObj`, per-closure `cdecl`/`_box_{N}`);
/// a user parameter spelled the same name used to produce broken Swift at generator exit 0.
///
/// Each fixture method puts a user param on one reserved synthetic. Reaching this test at all means
/// the generated Swift compiled (the compile gate would have failed on an "invalid redeclaration").
/// Asserting the round-tripped value proves the guard renamed the synthetic CONSISTENTLY across both
/// emission paths — if EmitSwiftWrapper and EmitSwiftMultiClosureWithPointerWrapping disagreed on the
/// renamed name, the body would reference an undefined identifier (compile failure) or wire the wrong
/// value through.
/// </summary>
public class SyntheticNameCollisionTests : TestBase
{
    public SyntheticNameCollisionTests(TestResults results) : base(results) { }

    public void TestSelfParamCollision_RoundTrips()
    {
        using var host = new SyntheticNameCollisionHost();
        int? captured = null;
        host.RunSelfCollision(7, result => { if (result.IsSuccess) captured = result.Success; });
        AssertEqual(8, captured, "user param `self_` round-trips despite colliding with synthetic self pointer");
        TestLogger.Info("P1-22 self_ collision round-trip passed");
    }

    public void TestSelfObjParamCollision_RoundTrips()
    {
        using var host = new SyntheticNameCollisionHost();
        int? captured = null;
        host.RunSelfObjCollision(10, result => { if (result.IsSuccess) captured = result.Success; });
        AssertEqual(12, captured, "user param `selfObj` round-trips despite colliding with synthetic self-reconstruction local");
        TestLogger.Info("P1-22 selfObj collision round-trip passed");
    }

    public void TestCdeclParamCollision_RoundTrips()
    {
        using var host = new SyntheticNameCollisionHost();
        int? captured = null;
        host.RunCdeclCollision(20, result => { if (result.IsSuccess) captured = result.Success; });
        AssertEqual(23, captured, "user param `cdecl` round-trips despite colliding with synthetic func-ptr local");
        TestLogger.Info("P1-22 cdecl collision round-trip passed");
    }

    public void TestBoxParamCollision_RoundTrips()
    {
        using var host = new SyntheticNameCollisionHost();
        int? captured = null;
        host.RunBoxCollision(30, result => { if (result.IsSuccess) captured = result.Success; });
        AssertEqual(34, captured, "user param `_box_0` round-trips despite colliding with synthetic escaping-box local");
        TestLogger.Info("P1-22 _box_0 collision round-trip passed");
    }

    public void TestAdapterParamCollision_RoundTrips()
    {
        // `__adapter0` is the per-closure adapter local emitted by the pointer-wrapping path
        // (EmitSwiftMultiClosureWithPointerWrapping), distinct from the `_box_0` box. Reaching
        // this assertion proves the guard renamed the synthetic so the `let __adapter0 = …`
        // binding and the call-site that references it agree — a disagreement would be a Swift
        // "use of unresolved identifier" at compile time (symbol stripped → EntryPointNotFound).
        using var host = new SyntheticNameCollisionHost();
        int? captured = null;
        host.RunAdapterCollision(40, result => { if (result.IsSuccess) captured = result.Success; });
        AssertEqual(45, captured, "user param `__adapter0` round-trips despite colliding with synthetic adapter local");
        TestLogger.Info("P1-22 __adapter0 collision round-trip passed");
    }
}
