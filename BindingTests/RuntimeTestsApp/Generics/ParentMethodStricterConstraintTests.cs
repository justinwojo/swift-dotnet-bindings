// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime regression for the parent-CSM METHOD-level stricter-constraint filter
/// (<c>ConcreteSpecializationEngine.ParentTupleSatisfiesMethodConstraints</c>, F20 Phase C).
/// See <c>BindingTests/Sources/SwiftBindingsTestLib/Generics/ParentMethodStricterConstraint.swift</c>.
///
/// Companion to <see cref="PatBagConformerMismatchTests"/>: that suite covers the TYPE-level
/// intersection filter (a conformer rejected from the whole specialization); this one covers the
/// sibling METHOD-level path — a conformer that IS admitted at the type level but lacks a protocol
/// required only by one method's <c>where</c> clause, so just that method is dropped.
///
/// <para><c>SlotOnlyItem</c> conforms to the parent constraint <c>RefinableSlot</c> but NOT to
/// <c>RefinableMark</c>, the protocol added by <c>refinedLabel() where Item: RefinableMark</c>. The
/// engine must drop <c>refinedLabel()</c> from <c>RefinableBag&lt;SlotOnlyItem&gt;</c> while keeping
/// the parent-only <c>bump(by:)</c>/<c>read()</c>. Without the per-method filter the emitter would
/// write <c>RefinableBag&lt;SlotOnlyItem&gt;().refinedLabel()</c> against a clause SlotOnlyItem
/// cannot satisfy → a hard Swift wrapper compile error, so the mere presence of these tests proves
/// the filter fired.</para>
///
/// File-level verification (established by the design doc + generated surface, not asserted here):
/// <list type="bullet">
///   <item>Generated C# exposes <c>RefinedLabel</c> on the <c>RefinableBag&lt;FullyRefinedItem&gt;</c>
///   specialization but NOT on <c>RefinableBag&lt;SlotOnlyItem&gt;</c> (both keep <c>Bump</c>/<c>Read</c>).</item>
///   <item><c>binding-emission-report.json</c> records a method-where rejection for
///   <c>SlotOnlyItem</c> against <c>RefinableMark</c>.</item>
/// </list>
/// </summary>
public class ParentMethodStricterConstraintTests : TestBase
{
    public ParentMethodStricterConstraintTests(TestResults results) : base(results) { }

    public void TestFullyRefined_BumpReadAndRefinedLabel_RoundTrip()
    {
        // The conformer that satisfies BOTH the parent constraint and the method's stricter
        // RefinableMark clause: bump/read accumulate AND refinedLabel() survives the filter.
        using var bag = Functions.MakeFullyRefinedBag();
        AssertEqual(0, (int)bag.Read(), "FullyRefined bag starts at 0");
        AssertEqual("refined#0", bag.RefinedLabel(), "RefinedLabel reads the initial counter");
        bag.Bump(5);
        AssertEqual(5, (int)bag.Read(), "FullyRefined bag is 5 after Bump(5)");
        AssertEqual("refined#5", bag.RefinedLabel(), "RefinedLabel reflects the bumped counter");
        bag.Bump(3);
        AssertEqual(8, (int)bag.Read(), "FullyRefined bag accumulates to 8 after Bump(3)");
        AssertEqual("refined#8", bag.RefinedLabel(), "RefinedLabel reflects the accumulated counter");
    }

    public void TestSlotOnly_BumpAndRead_RoundTrip()
    {
        // The conformer that satisfies ONLY the parent constraint. RefinedLabel() is absent from
        // this specialization's surface (the method-level filter dropped it — it does not even
        // compile here), so we can only exercise the parent-only bump/read. The drop is what makes
        // the Swift wrapper compile; this test exists to prove the surviving members still work.
        using var bag = Functions.MakeSlotOnlyBag();
        AssertEqual(0, (int)bag.Read(), "SlotOnly bag starts at 0");
        bag.Bump(11);
        AssertEqual(11, (int)bag.Read(), "SlotOnly bag is 11 after Bump(11)");
        bag.Bump(2);
        AssertEqual(13, (int)bag.Read(), "SlotOnly bag accumulates to 13 after Bump(2)");
    }

    public void TestBothSpecializations_HaveIndependentStorage()
    {
        // The two per-conformer specializations must not share backing storage even though they
        // descend from the same parent CSM struct — confirms each specialization carries its own
        // state through the CSM-emitted mutating setters.
        using var refined = Functions.MakeFullyRefinedBag();
        using var slotOnly = Functions.MakeSlotOnlyBag();
        refined.Bump(17);
        slotOnly.Bump(29);
        AssertEqual(17, (int)refined.Read(), "FullyRefined bag retains its own state (17)");
        AssertEqual(29, (int)slotOnly.Read(), "SlotOnly bag retains its own state (29)");
        AssertEqual("refined#17", refined.RefinedLabel(), "FullyRefined RefinedLabel reflects its own state");
    }
}
