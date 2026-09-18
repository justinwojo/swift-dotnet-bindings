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
    /// The one shape with no C# spelling: a pinned STATIC method. C# has no static extension members,
    /// so <c>coarse</c> is expected to stay unbound — asserted on each generated extension class,
    /// since an extension method is a static member of another class entirely and so would be
    /// invisible to a lookup on <c>PinRange</c> itself.
    /// </summary>
    public void TestPinnedStaticMethodRemainsUnbound()
    {
        // The four CSM extension classes are named outright rather than swept out of the assembly.
        // A GetTypes() sweep trips the trimmer, and worse, it would report "absent" for a member
        // that trimming had merely removed — a negative that passes for the wrong reason. Naming
        // each class roots it, so absence here means the generator did not emit it.
        void AssertNoCoarse(
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] System.Type cls) =>
            AssertNull(cls.GetMethod("Coarse", BindingFlags.Public | BindingFlags.Static),
                $"a pinned static method has no C# static-extension spelling ({cls.Name})");

        AssertNoCoarse(typeof(PinRangeSwiftBindingsTestLib_PinCoarseSwiftBindingsTestLib_PinCoarseCsmExtensions));
        AssertNoCoarse(typeof(PinRangeSwiftBindingsTestLib_PinCoarseSwiftBindingsTestLib_PinFineCsmExtensions));
        AssertNoCoarse(typeof(PinRangeSwiftBindingsTestLib_PinFineSwiftBindingsTestLib_PinCoarseCsmExtensions));
        AssertNoCoarse(typeof(PinRangeSwiftBindingsTestLib_PinFineSwiftBindingsTestLib_PinFineCsmExtensions));

        AssertNull(
            typeof(PinRange<PinFine, PinCoarse>).GetMethod(
                "Coarse", BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static),
            "nor is it on the closed generic type itself");
    }
}
