// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Coverage for same-type pins on a multi-parameter generic parent
/// (<c>Generics/MultiParameterSameTypePin.swift</c>). The closed-extension route spelled
/// <c>PinRange&lt;PinCoarse&gt;</c> for a two-parameter type, so the wrapper did not compile.
/// The pinned members now bind on the closed instantiations their <c>where</c> clause admits,
/// re-surfaced as extension methods, and must actually dispatch through to Swift.
/// </summary>
public class MultiParameterSameTypePinTests : TestBase
{
    public MultiParameterSameTypePinTests(TestResults results) : base(results) { }

    public void TestUnconstrainedMembersOfPinnedParentDispatch()
    {
        using var range = Functions.MakeFineCoarsePinRange(41);
        AssertEqual(41, range.Raw, "stored property on PinRange<PinFine, PinCoarse>");
        AssertEqual(42, range.RawPlusOne, "computed property on PinRange<PinFine, PinCoarse>");
    }

    public void TestSingleParameterPinStillClosesTheParent()
    {
        using var single = Functions.MakeCoarsePinSingle(21);
        AssertEqual((nint)42, single.GetDoubled(), "constrained-extension property on PinSingle<PinCoarse>");
    }

    /// <summary>
    /// A property pinned on a two-parameter parent (<c>where Lo: PinFineGranularity, Hi == PinCoarse</c>)
    /// dispatches on the closed instantiation. Reaching Swift is the point: a wrong receiver spelling
    /// would not compile, and a wrong self pointer would not return the Swift-computed value.
    /// </summary>
    public void TestPinnedPropertyOnMultiParameterParentDispatches()
    {
        using var range = Functions.MakeFineCoarsePinRange(41);
        AssertEqual((nint)41000, range.CoarseUnits(), "coarseUnits on PinRange<PinFine, PinCoarse>");
    }

    /// <summary>
    /// A property pinned on EVERY parameter (<c>where Lo == PinFine, Hi == PinCoarse</c>) binds only
    /// on that one instantiation.
    /// </summary>
    public void TestFullyPinnedPropertyDispatches()
    {
        using var range = Functions.MakeFineCoarsePinRange(41);
        AssertEqual((nint)48, range.FinePerCoarse(), "finePerCoarse on PinRange<PinFine, PinCoarse>");
    }

    public void TestPinnedMethodsOnMultiParameterParentDispatch()
    {
        using var range = Functions.MakeFineCoarsePinRange(41);
        AssertEqual((nint)82000, range.CoarseUnitsTwice(), "coarseUnitsTwice on PinRange<PinFine, PinCoarse>");
        AssertEqual((nint)96, range.FinePerCoarseTwice(), "finePerCoarseTwice on PinRange<PinFine, PinCoarse>");
    }

    /// <summary>
    /// A pinned STATIC method returning its own closed parent. This is the shape a static returning a
    /// scalar would not prove: the return type is written <c>PinRange</c> in Swift, meaning the parent
    /// with both parameters still open, so binding it at all requires substituting the pairing into the
    /// return before the ABI is classified. The value comes back through an indirect result pointer.
    ///
    /// Dispatching a further member on the returned range is the assertion that matters — it shows the
    /// result is a live closed parent whose metadata resolves, not merely a struct that marshalled.
    /// </summary>
    public void TestPinnedStaticMethodDispatchesReturningClosedParent()
    {
        using var range = PinRangeSwiftBindingsTestLib_PinFineSwiftBindingsTestLib_PinCoarseCsmExtensions.Coarse(41);
        AssertEqual((nint)41, range.Raw, "coarse(_:) returned a PinRange<PinFine, PinCoarse> carrying its argument");
        AssertEqual((nint)41000, range.CoarseUnits(), "a pinned member dispatches on the returned closed parent");
    }

    /// <summary>
    /// The pin is honoured rather than widened: <c>coarse</c> is declared under
    /// <c>where Lo: PinFineGranularity, Hi == PinCoarse</c>, so it belongs only on the pairings whose
    /// <c>Hi</c> is <c>PinCoarse</c>. Emitting it on all four would mean the substitution had stopped
    /// respecting the <c>where</c> clause. Asserted on the generated extension classes, since an
    /// extension method is a static member of another class entirely and so would be invisible to a
    /// lookup on <c>PinRange</c> itself.
    /// </summary>
    public void TestPinnedStaticMethodIsAbsentWherePinIsUnsatisfiable()
    {
        // The CSM extension classes are named outright rather than swept out of the assembly.
        // A GetTypes() sweep trips the trimmer, and worse, it would report "absent" for a member
        // that trimming had merely removed — a negative that passes for the wrong reason. Naming
        // each class roots it, so absence here means the generator did not emit it.
        static MethodInfo? Coarse(
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] System.Type cls) =>
            cls.GetMethod("Coarse", BindingFlags.Public | BindingFlags.Static);

        AssertNull(Coarse(typeof(PinRangeSwiftBindingsTestLib_PinCoarseSwiftBindingsTestLib_PinFineCsmExtensions)),
            "Hi is not PinCoarse, so the pinned static does not belong on Lo == PinCoarse, Hi == PinFine");
        AssertNull(Coarse(typeof(PinRangeSwiftBindingsTestLib_PinFineSwiftBindingsTestLib_PinFineCsmExtensions)),
            "Hi is not PinCoarse, so the pinned static does not belong on Lo == PinFine, Hi == PinFine");

        AssertNotNull(Coarse(typeof(PinRangeSwiftBindingsTestLib_PinCoarseSwiftBindingsTestLib_PinCoarseCsmExtensions)),
            "and it does belong on the other admitted pairing, Lo == PinCoarse, Hi == PinCoarse");
    }
}
