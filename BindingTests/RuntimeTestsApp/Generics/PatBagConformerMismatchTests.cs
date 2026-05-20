// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime regression for the CSM multi-constraint intersection filter (see
/// <c>BindingTests/Sources/SwiftBindingsTestLib/Generics/PatBagConformerMismatch.swift</c>).
/// The presence of these tests requires the Swift wrapper to compile, which in
/// turn requires the engine's filter to reject <c>SlotOnlyDouble</c> at the
/// pairing step: without rejection, the emitter would attempt to specialize
/// <c>PermittedBag&lt;SlotOnlyDouble&gt;</c> against the
/// <c>&lt;T: PermittedSlot &amp; Permitted&gt;</c> clause, which Swift rejects
/// because <c>SlotOnlyDouble</c> does not conform to <c>Permitted</c>.
///
/// File-level verification (not asserted here, but established by the design
/// doc and the binding-emission-report.json shape):
/// <list type="bullet">
///   <item>Generated C# contains
///   <c>PermittedBagPermittedStringCsmExtensions</c> and
///   <c>PermittedBagPermittedIntCsmExtensions</c>.</item>
///   <item>Generated C# does NOT contain
///   <c>PermittedBagSlotOnlyDoubleCsmExtensions</c>.</item>
///   <item><c>binding-emission-report.json.csmConformerRejections</c> lists
///   a row with conformer <c>SwiftBindingsTestLib.SlotOnlyDouble</c> and
///   missingConstraint <c>SwiftBindingsTestLib.Permitted</c>.</item>
/// </list>
/// </summary>
public class PatBagConformerMismatchTests : TestBase
{
    public PatBagConformerMismatchTests(TestResults results) : base(results) { }

    public void TestPermittedString_BumpAndRead_RoundTrips()
    {
        using var bag = Functions.MakePermittedBagPermittedString();
        AssertEqual(0, (int)bag.Read(), "PermittedString bag starts at 0");
        bag.Bump(5);
        AssertEqual(5, (int)bag.Read(), "PermittedString bag is 5 after Bump(5)");
        bag.Bump(3);
        AssertEqual(8, (int)bag.Read(), "PermittedString bag accumulates to 8 after Bump(3)");
    }

    public void TestPermittedInt_BumpAndRead_RoundTrips()
    {
        // Second admitted conformer — exercises that each conformer emits its
        // own independent CSM extension class (per-conformer-specialized symbols).
        using var bag = Functions.MakePermittedBagPermittedInt();
        AssertEqual(0, (int)bag.Read(), "PermittedInt bag starts at 0");
        bag.Bump(11);
        AssertEqual(11, (int)bag.Read(), "PermittedInt bag is 11 after Bump(11)");
    }

    public void TestAdmittedConformers_HaveIndependentStorage()
    {
        // Two instances of the same admitted-conformer specialization must
        // not share state — confirms the per-conformer dispatch carries its
        // own backing storage even after writes through the CSM-emitted setters.
        using var a = Functions.MakePermittedBagPermittedString();
        using var b = Functions.MakePermittedBagPermittedString();
        a.Bump(17);
        b.Bump(29);
        AssertEqual(17, (int)a.Read(), "PermittedString bag A retains its own state (17)");
        AssertEqual(29, (int)b.Read(), "PermittedString bag B retains its own state (29)");
    }
}
