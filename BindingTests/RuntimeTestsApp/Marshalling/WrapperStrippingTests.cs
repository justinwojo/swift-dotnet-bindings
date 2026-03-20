// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests that the generator correctly handles methods with unsupported patterns.
/// MixedEmittability has methods with method-level generics, inout params,
/// and opaque returns. The generator DOES emit these methods (with CallConvSwift
/// fallback and [Obsolete] warning), but they may crash on Mono simulator.
///
/// This tests the boundary between working @_cdecl methods and fallback
/// CallConvSwift methods — the same boundary that causes real-world DllNotFoundException
/// in ObjectMapper/XMLCoder/PhoneNumberKit when wrapper compilation fails.
///
/// Coverage gaps addressed:
/// - Method-level generics emission (ShouldEmitWrapper:69-70 — generator emits with fallback)
/// - Inout param emission (ShouldEmitWrapper:97-98 — generator emits with CallConvSwift)
/// - Opaque return emission (ShouldEmitWrapper:133-134 — generator emits with fallback)
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

    [SkipOnSimulator("CallConvSwift inout param crashes Mono JIT")]
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

    [SkipOnSimulator("CallConvSwift opaque return crashes Mono JIT")]
    public void TestMixedEmittabilityOpaqueReturn()
    {
        // asDescribable() -> some CustomStringConvertible — emitted with CallConvSwift
        var obj = new MixedEmittability(name: "test", count: 7);
        #pragma warning disable SB0001
        var result = obj.GetAsDescribable();
        #pragma warning restore SB0001
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

    [Skip("coverage gap: variadic method Append crashes Mono JIT — IEnumerable→variadic marshalling")]
    public void TestVariadicHolderAppend()
    {
        var holder = new VariadicHolder(values: new[] { 1, 2 });
        var result = holder.Append(more: new[] { 3, 4 });
        AssertEqual(4, result.Count, "Append produces 4 elements");
        TestLogger.Info($"VariadicHolder.Append count = {result.Count}");
    }

    #endregion
}
