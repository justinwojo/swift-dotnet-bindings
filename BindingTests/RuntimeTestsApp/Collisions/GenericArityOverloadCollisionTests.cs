// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// A class declares a method-generic method and a non-generic namesake whose parameters project to the
/// same C# overload key (<c>Transform(RefBox)</c>): the generic takes <c>RefBox?</c> and the non-generic
/// takes <c>RefBox</c>, and a nullable reference annotation does not distinguish C# overloads, so both
/// erase to <c>Transform(RefBox)</c>. Their RAW Swift parameter types differ (<c>RefBox?</c> vs
/// <c>RefBox</c>), so the conformance validator matches the non-generic witness (the generic is skipped)
/// and the class is emitted as a DIRECT <c>: IRefBoxArityTransform</c>. They are legal, distinct C#
/// overloads (<c>int Transform(RefBox)</c> vs <c>TTag? Transform&lt;TTag&gt;(RefBox?)</c>): method-level
/// generic arity is part of overload identity, so the secondary-dedup projected key must encode it. An
/// arity-blind key groups the two as colliding overloads and renames BOTH off the bare slot (neither
/// carries a label, so both escalate to the type rung); because the class is declared
/// <c>: IRefBoxArityTransform</c> (whose requirement is the bare non-generic shape), that leaves the
/// interface requirement unimplemented → CS0535 at binding-compile time. A real event-monitor type
/// broke this way.
///
/// This is primarily a generation-time guard — a regression fails the binding COMPILE (CS0535). The
/// runtime assertions additionally confirm the non-generic body keeps the bare slot, on the concrete
/// receiver and through the interface: with the arity marker the bare slot dispatches to the non-generic
/// (value+50); a regression that re-blinds the projected key would bind the bare slot to the generic body.
/// </summary>
public class GenericArityOverloadCollisionTests : TestBase
{
    public GenericArityOverloadCollisionTests(TestResults results) : base(results) { }

    public void TestNonGenericKeepsBareSlot()
    {
        using var t = new RefBoxArityTransformer();
        using var box = new RefBox(5);
        // The non-generic transform owns the bare `Transform(RefBox)` slot; the method-generic sibling
        // (declared first) must not steal it. value+50 proves the non-generic Swift body ran — a regression
        // that re-blinds the projected key would bind `Transform(RefBox)` to the generic body instead.
        AssertEqual(55, t.Transform(box),
            "Transform(RefBox) -> non-generic transform value+50 (method-generic sibling did not steal the bare slot)");
    }

    public void TestProtocolRequirementSatisfiedByBareSlot()
    {
        using var t = new RefBoxArityTransformer();
        using var box = new RefBox(5);
        // The exact CS0535 surface: the bare `Transform(RefBox)` is what satisfies the
        // IRefBoxArityTransform requirement. Dispatching through the interface must reach the non-generic body.
        IRefBoxArityTransform iface = t;
        AssertEqual(55, iface.Transform(box),
            "IRefBoxArityTransform.Transform -> non-generic concrete value+50 (interface requirement satisfied by the bare slot)");
    }
}
