// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ObjCInterop;

/// <summary>
/// Reverse-dispatch vtable lockstep for a dropped @objc-existential requirement.
///
/// <c>ObjCShapeCollector</c> declares two hazard requirements — <c>absorb(shapes:)</c> and
/// <c>shapes</c>, both carrying <c>[any ObjCClassBoundShape]</c> (an @objc protocol existential in a
/// container position) — FIRST, then a supported <c>shapeCount: Int32</c> getter LAST. An @objc
/// existential marshals as a single bare ObjC object pointer; routing a container of one through the
/// ExistentialContainer1 carrier is a buffer over-read, so both hazard members must be dropped
/// fail-closed from the C# interface AND the reverse-dispatch vtable in lockstep.
///
/// This C# conformer implements ONLY <c>ShapeCount</c> — the hazard members are absent from the emitted
/// <c>IObjCShapeCollector</c> (their absence is the fail-closed assertion; referencing <c>Absorb</c> or
/// <c>Shapes</c> would fail to compile). Swift dispatches <c>shapeCount</c> back into this conformer: if
/// the vtable had desynced from the interface (dropped one hazard side but not the other), the supported
/// slot would shift and this read would return garbage or SIGSEGV on the NativeAOT device leg. A correct
/// lockstep drop keeps <c>shapeCount</c> at slot 0 and round-trips the value.
/// </summary>
public class ObjCExistentialReverseVtableLockstepTests : TestBase
{
    public ObjCExistentialReverseVtableLockstepTests(TestResults results) : base(results) { }

    public void TestSupportedSlotRoundTripsWithHazardMembersDropped()
    {
        var collector = new CountingCollector(7);
        AssertEqual(7, TestLibFunctions.ReadShapeCollectorCount(collector),
            "reverse dispatch reaches the supported ShapeCount slot; hazard @objc-existential members " +
            "dropped in interface/vtable lockstep (no slot shift, no over-read)");
    }

    public void TestSupportedSlotRoundTripsForDistinctValue()
    {
        var collector = new CountingCollector(1234);
        AssertEqual(1234, TestLibFunctions.ReadShapeCollectorCount(collector),
            "a distinct value confirms the reverse dispatch reads THIS conformer's slot, not a fixed offset");
    }

    /// <summary>
    /// C# conformer of <c>ObjCShapeCollector</c>. Implements only the supported <c>ShapeCount</c> slot;
    /// the dropped hazard members are not part of the emitted interface.
    /// </summary>
    private sealed class CountingCollector : IObjCShapeCollector
    {
        public int ShapeCount { get; }
        public CountingCollector(int shapeCount) => ShapeCount = shapeCount;
    }
}
