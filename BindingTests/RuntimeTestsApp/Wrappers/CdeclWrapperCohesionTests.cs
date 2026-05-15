// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning

namespace RuntimeTestsApp.Wrappers;

/// <summary>
/// Maximum-case round-trips for the `@_cdecl` wrapper cohesion gates that broke
/// downstream in 0.11.0. Two symptoms covered here:
///
///   * <see cref="WrapperCohesionBase"/> exposes an instance method that takes
///     and returns an <c>Optional&lt;WrapperCohesionBase&gt;</c>. Two subclasses
///     inherit the method, so the wrapper-emit path runs the same Optional-class
///     param/return shape three times. Without the wrapper rendering it as
///     <c>UnsafeMutableRawPointer?</c>, swiftc would reject the wrapper file with
///     "type is not representable in Objective-C" and the BindingTests Swift
///     compile step would fail before this test even runs.
///
///   * <see cref="WrapperCohesionBuildable"/> ships a default Bool-bearing
///     extension method that <c>ProtocolExtensionEmitter</c> synthesises onto
///     the conforming class, after which <c>MethodHandler</c> would also emit a
///     wrapper for the same method. Reaching the runtime check proves the
///     cross-emitter dedup gate runs and the symbol was emitted exactly once.
/// </summary>
public class CdeclWrapperCohesionTests : TestBase
{
    public CdeclWrapperCohesionTests(TestResults results) : base(results) { }

    public void TestSubclassAttachNonNilCarriesSibling()
    {
        using var left = new WrapperCohesionLeft(nodeId: 11);
        using var right = new WrapperCohesionRight(nodeId: 22);
        var attached = left.Attach(right);
        AssertTrue(attached, "Attach returns true when given a non-nil sibling");
        AssertEqual(22, left.LastSeenChildId, "Subclass observed sibling's nodeId");
    }

    public void TestSubclassAttachNilReturnsFalse()
    {
        using var right = new WrapperCohesionRight(nodeId: 7);
        var attached = right.Attach(null);
        AssertTrue(!attached, "Attach returns false when given a nil sibling");
        AssertEqual(-1, right.LastSeenChildId, "lastSeenChildId stays at sentinel on nil attach");
    }

    public void TestSubclassDetachReturnsNilThenStashed()
    {
        using var left = new WrapperCohesionLeft(nodeId: 1);
        AssertNull(left.Detach(), "Detach returns nil when nothing stashed");

        using var sibling = new WrapperCohesionRight(nodeId: 99);
        left.Stash(sibling);
        var recovered = left.Detach();
        AssertNotNull(recovered, "Detach returns the stashed sibling");
        AssertEqual(99, recovered!.NodeId, "Recovered sibling carries the original nodeId");
        AssertNull(left.Detach(), "Detach clears the slot after returning the stashed value");
    }

    public void TestProtocolExtensionStepDispatches()
    {
        using var builder = new WrapperCohesionBuilder();
        AssertEqual(0, builder.StepCounter, "stepCounter starts at zero");

        AssertTrue(builder.Step(true), "Step echoes its argument");
        AssertEqual(1, builder.StepCounter, "Step increments stepCounter when enabled");

        AssertTrue(!builder.Step(false), "Step echoes its argument (false)");
        AssertEqual(1, builder.StepCounter, "Step leaves stepCounter unchanged when disabled");
    }

    public void TestProtocolExtensionStepInt32OverloadDispatches()
    {
        // Same external label, distinct parameter type — proves both @_cdecl
        // wrappers survived dedup. A labels-only source key would have collapsed
        // step(Bool) and step(Int32) to the same identity and silently dropped
        // one of the two wrappers; we'd land here with a missing symbol at
        // P/Invoke time.
        using var builder = new WrapperCohesionBuilder();
        var first = builder.Step(3);
        AssertEqual(3, first, "Step(Int32) returns the running total");
        AssertEqual(3, builder.StrideCounter, "strideCounter mirrors the running total");

        var second = builder.Step(4);
        AssertEqual(7, second, "Step(Int32) accumulates");
        AssertEqual(7, builder.StrideCounter, "strideCounter accumulates");
    }

    public void TestProtocolExtensionRemainingDoubleNilAndSome()
    {
        using var holder = new WrapperCohesionRemainingHolder();

        var nilResult = holder.RemainingTime(stateDuration: null);
        AssertEqual(-1.0, nilResult, "Nil Optional<Double> yields the sentinel return");
        AssertEqual(-1.0, holder.ObservedDouble, "Nil case stamps the sentinel into observedDouble");

        var someResult = holder.RemainingTime(stateDuration: 12.5);
        AssertEqual(25.0, someResult, "Some(12.5) round-trips through the @_cdecl wrapper");
        AssertEqual(12.5, holder.ObservedDouble, "Some case records the decoded Double payload");
    }

    public void TestProtocolExtensionRemainingInt32NilAndSome()
    {
        using var holder = new WrapperCohesionRemainingHolder();

        var nilResult = holder.RemainingCount(null);
        AssertEqual(-1, nilResult, "Nil Optional<Int32> yields the sentinel return");
        AssertEqual(-1, holder.ObservedInt32, "Nil case stamps the sentinel into observedInt32");

        var someResult = holder.RemainingCount(7);
        AssertEqual(21, someResult, "Some(7) round-trips through the @_cdecl wrapper");
        AssertEqual(7, holder.ObservedInt32, "Some case records the decoded Int32 payload");
    }

    public void TestProtocolExtensionRemainingBoolNilAndSome()
    {
        using var holder = new WrapperCohesionRemainingHolder();

        var nilResult = holder.RemainingFlag(null);
        AssertEqual(-1, nilResult, "Nil Optional<Bool> yields the sentinel return");
        AssertEqual(-1, holder.ObservedBoolByte, "Nil case stamps the sentinel into observedBoolByte");

        var trueResult = holder.RemainingFlag(true);
        AssertEqual(1, trueResult, "Some(true) round-trips through the pointer-typed fallback");
        AssertEqual(1, holder.ObservedBoolByte, "Some(true) records 1 into observedBoolByte");

        var falseResult = holder.RemainingFlag(false);
        AssertEqual(0, falseResult, "Some(false) round-trips through the pointer-typed fallback");
        AssertEqual(0, holder.ObservedBoolByte, "Some(false) records 0 into observedBoolByte");
    }
}
