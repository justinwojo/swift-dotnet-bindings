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
/// when wrapper compilation fails for methods that need @_cdecl wrappers.
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
        // increment(counter: inout Int32) — `counter += count` (count = 5). The inout param now
        // projects as `ref int`, so Swift's mutation is observable to the caller (was a marshalling
        // gap: inout used to be passed by value and the write-back was silently lost).
        var obj = new MixedEmittability(name: "test", count: 5);
        int counter = 10;
        obj.Increment(ref counter);
        AssertEqual(15, counter, "Increment must add count (5) to the inout counter and write it back");
        TestLogger.Info($"MixedEmittability.Increment(ref 10) → {counter}");
    }

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

    /// <summary>
    /// The array-taking sibling of the variadic initializer. A variadic <i>constructor</i> is still
    /// declined — the wrapper bridge's function-value bitCast has no initializer form — so this
    /// overload is how the type gets built at all, and constructing through it pins that the
    /// decline is scoped to the one initializer rather than taking the whole type with it.
    /// </summary>
    public void TestVariadicHolderConstruction()
    {
        using var holder = new VariadicHolder(new[] { 10, 20, 30 });

        var values = holder.Values;
        AssertEqual(3, values.Count, "Values should carry every element the initializer was handed");
        AssertEqual("10,20,30", string.Join(",", values),
            "the stored array should come back in the order the initializer received it");
        TestLogger.Info($"VariadicHolder(list:) → [{string.Join(", ", values)}]");
    }

    /// <summary>
    /// A method reading the constructed value's own storage. <c>sum()</c> reduces the stored array
    /// Swift-side, so a correct total means the payload the initializer wrote is the one Swift
    /// reads back — not a separately-allocated buffer that happens to project the same elements.
    /// </summary>
    public void TestVariadicHolderSum()
    {
        using var holder = new VariadicHolder(new[] { 10, 20, 30 });

        var sum = holder.Sum();
        AssertEqual(60, sum, "Sum should reduce the array the initializer stored");
        TestLogger.Info($"VariadicHolder.Sum() = {sum}");
    }

    /// <summary>
    /// The variadic instance <i>method</i>. It reaches Swift through the wrapper bridge, which
    /// bitCasts the <c>(T...) -&gt; R</c> function value to <c>([T]) -&gt; R</c> and reconstructs the
    /// receiver, so C# hands it an ordinary sequence. Order is the assertion that carries weight:
    /// the result is the receiver's own elements followed by the appended ones, so a swap of the
    /// two operands crossing the seam shows up as a reversal rather than as a length that still
    /// happens to match.
    /// </summary>
    public void TestVariadicMethodRoundTripsThroughTheWrapperBridge()
    {
        using var holder = new VariadicHolder(new[] { 10, 20, 30 });

        var appended = holder.Append(new[] { 40, 50 });
        AssertEqual(5, appended.Count, "Append returns the receiver's elements plus the appended ones");
        AssertEqual("10,20,30,40,50", string.Join(",", appended),
            "the appended elements should follow the receiver's, in order");

        // Empty is the arm where a length-only check would pass on a dropped receiver: appending
        // nothing has to give the receiver back unchanged rather than an empty array.
        var unchanged = holder.Append(new int[0]);
        AssertEqual("10,20,30", string.Join(",", unchanged),
            "appending nothing should leave the receiver's own elements alone");

        TestLogger.Info($"VariadicHolder.Append(40,50) → [{string.Join(", ", appended)}]");
    }

    #endregion
}
