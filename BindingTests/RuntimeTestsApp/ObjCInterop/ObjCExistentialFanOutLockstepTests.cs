// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ObjCInterop;

/// <summary>
/// Reverse-dispatch FAN-OUT lockstep for a dropped @objc-existential requirement shared across two
/// protocols.
///
/// <c>ObjCShapeSinkA</c> and <c>ObjCShapeSinkB</c> both declare the IDENTICAL hazard method
/// <c>absorb(shapes: [any ObjCClassBoundShape])</c> (an @objc protocol existential in a container
/// parameter position), so they form one same-signature owner/peer fan-out group. The owner emits a
/// single EveryProtocol witness body that fans a nil-check branch out across every sibling emitting a
/// per-protocol vtable func field. Each branch reads <c>siblingVtable.func_absorb_{idx}</c>, which
/// exists only for a layout-included slot — but the nested @objc existential is dropped fail-closed
/// from the layout. The fan-out branch filter must consult the same vtable-layout membership oracle as
/// the struct walk; a divergent predicate that kept the dropped sibling would emit a branch over a
/// <c>func_absorb_{idx}</c> member the struct never declared, failing wrapper compilation for the whole
/// package. That the binding compiles at all, plus these round-trips, is the fail-closed assertion.
///
/// Each conformer implements ONLY its supported scalar getter — the hazard <c>Absorb</c> is absent from
/// the emitted interfaces (referencing it would fail to compile). Swift reverse-dispatches the supported
/// slot back into the conformer; a slot shift from a lockstep failure would return garbage / SIGSEGV.
/// </summary>
public class ObjCExistentialFanOutLockstepTests : TestBase
{
    public ObjCExistentialFanOutLockstepTests(TestResults results) : base(results) { }

    public void TestSinkASupportedSlotRoundTrips()
    {
        var sink = new SinkA(7);
        AssertEqual(7, TestLibFunctions.ReadSinkCountA(sink),
            "reverse dispatch reaches ObjCShapeSinkA's supported SinkCountA slot; the shared hazard " +
            "@objc-existential method is dropped from the fan-out branch list in lockstep with the layout");
    }

    public void TestSinkBSupportedSlotRoundTrips()
    {
        var sink = new SinkB(1234);
        AssertEqual(1234, TestLibFunctions.ReadSinkCountB(sink),
            "reverse dispatch reaches ObjCShapeSinkB's supported SinkCountB slot; the co-grouped sibling's " +
            "dropped hazard method does not shift or corrupt this slot");
    }

    private sealed class SinkA : IObjCShapeSinkA
    {
        public int SinkCountA { get; }
        public SinkA(int count) => SinkCountA = count;
    }

    private sealed class SinkB : IObjCShapeSinkB
    {
        public int SinkCountB { get; }
        public SinkB(int count) => SinkCountB = count;
    }
}
