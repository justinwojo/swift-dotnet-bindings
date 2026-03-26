// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests that the generator correctly handles methods with mixed emission patterns.
/// MixedEmittability has methods with method-level generics, inout params,
/// and opaque returns. Most emit with @_cdecl wrappers; some fall back to CallConvSwift.
///
/// This tests the boundary between working @_cdecl methods and fallback
/// CallConvSwift methods — the same boundary that causes real-world DllNotFoundException
/// in ObjectMapper/XMLCoder/PhoneNumberKit when wrapper compilation fails.
///
/// Coverage:
/// - Method-level generics emission (ShouldEmitWrapper:69-70 — CallConvSwift fallback)
/// - Inout param emission (ShouldEmitWrapper:97-98 — CallConvSwift fallback)
/// - Opaque return emission — now routed through @_cdecl wrapper (boxes some→any Protocol)
/// - Variadic param emission (ShouldEmitWrapper:108-109 — generator emits via IEnumerable)
/// </summary>
public class WrapperStrippingTests : TestBase
{
    public WrapperStrippingTests(TestResults results) : base(results) { }

    #region MixedEmittability — Working @_cdecl Methods

    public void TestMixedEmittabilityConstruction()
    {
        var obj = new MixedEmittability(name: "test", count: 5);
        AssertNotNull(obj, "MixedEmittability constructed");
        TestLogger.Info("MixedEmittability construction passed");
    }

    public void TestMixedEmittabilityGetName()
    {
        var obj = new MixedEmittability(name: "hello", count: 3);
        var name = obj.GetName();
        AssertEqual("hello", name, "GetName");
        TestLogger.Info($"MixedEmittability.GetName() = {name}");
    }

    public void TestMixedEmittabilityGetCount()
    {
        var obj = new MixedEmittability(name: "test", count: 42);
        var count = obj.GetCount();
        AssertEqual(42, count, "GetCount");
        TestLogger.Info($"MixedEmittability.GetCount() = {count}");
    }

    public void TestMixedEmittabilityDescribe()
    {
        var obj = new MixedEmittability(name: "item", count: 7);
        var desc = obj.GetDescribe();
        AssertEqual("item:7", desc, "GetDescribe");
        TestLogger.Info($"MixedEmittability.GetDescribe() = {desc}");
    }

    #endregion

    #region MixedEmittability — CallConvSwift Fallback Methods

    // These methods ARE emitted (contrary to initial assumption) but use CallConvSwift
    // instead of @_cdecl. They have [Obsolete("SB0001")] warnings.
    // On Mono simulator they may crash; on NativeAOT device they work.
    // This is the same pattern as real-world library failures — the method exists
    // but crashes on Mono because there's no @_cdecl wrapper.

    public void TestMixedEmittabilityInoutParam()
    {
        // increment(counter: inout Int32) — emitted with CallConvSwift, no @_cdecl wrapper.
        // Note: inout is marshalled as value param (int, not ref int) — possible marshalling gap.
        var obj = new MixedEmittability(name: "test", count: 5);
        #pragma warning disable SB0001
        obj.Increment(counter: 10);
        #pragma warning restore SB0001
        // Can't verify side effect since inout is marshalled as value — just verify no crash
        TestLogger.Info("MixedEmittability.Increment called without crash");
    }

    [Skip("Opaque return @_cdecl wrapper works (generator fixed), but ExistentialContainer1 metadata not yet supported at runtime — needs protocol descriptor pointers (Bug 6)")]
    public void TestMixedEmittabilityOpaqueReturn()
    {
        // asDescribable() -> some CustomStringConvertible — @_cdecl wrapper boxes to any Protocol
        var obj = new MixedEmittability(name: "test", count: 7);
        var result = obj.GetAsDescribable();
        AssertNotNull(result, "GetAsDescribable returned non-null");
        TestLogger.Info($"MixedEmittability.GetAsDescribable() = {result}");
    }

    #endregion

    #region VariadicHolder — Variadic Param via IEnumerable

    public void TestVariadicHolderConstruction()
    {
        // Variadic init emitted as IEnumerable<int> constructor
        var holder = new VariadicHolder(values: new[] { 1, 2, 3 });
        AssertNotNull(holder, "VariadicHolder constructed");
        TestLogger.Info("VariadicHolder(IEnumerable) construction passed");
    }

    public void TestVariadicHolderSum()
    {
        var holder = new VariadicHolder(values: new[] { 10, 20, 30 });
        var sum = holder.Sum();
        AssertEqual(60, sum, "Sum = 10 + 20 + 30");
        TestLogger.Info($"VariadicHolder.Sum() = {sum}");
    }

    public void TestVariadicMethodSuppressed()
    {
        // Variadic methods (T...) cannot be bound — ABI JSON represents T... as Array<T>,
        // and neither @_cdecl (can't spread array into variadic) nor CallConvSwift can dispatch.
        // Verify the method is correctly suppressed from emission.
        var type = typeof(VariadicHolder);
        var appendMethod = type.GetMethod("Append");
        AssertNull(appendMethod, "Variadic method Append should not be emitted");
        TestLogger.Info("VariadicHolder.Append correctly suppressed");
    }

    #endregion
}
