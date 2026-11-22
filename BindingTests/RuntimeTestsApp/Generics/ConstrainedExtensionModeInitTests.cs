// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// <c>MotionFromTo</c>'s <c>where Value == MotionTransform</c> extension re-declares the
/// unconstrained initializers with a defaulted <c>mode:</c>. The <c>MotionTransform</c> factories
/// must call those extension initializers (what Swift resolves the same call to), with the mode's
/// default; the <c>MotionScalar</c> factories call the unconstrained ones. A factory bound to the
/// other declaration reports the wrong route.
/// </summary>
public class ConstrainedExtensionModeInitTests : TestBase
{
    public ConstrainedExtensionModeInitTests(TestResults results) : base(results) { }

    public void TestTransformTo_RunsTheExtensionInit()
    {
        using var to = new MotionTransform(2.0);
        using var action = MotionFromToSwiftBindingsTestLib_MotionTransformCsmExtensions.FromSwiftBindingsTestLibMotionTransform(to);
        AssertEqual("transform.to.local", action.Route, "init(from:to:mode:timing:isAdditive:) ran with mode .default");
        AssertEqual(1, action.TimingRate, "timing defaulted to .linear");
    }

    public void TestTransformToBy_RunsTheExtensionInit()
    {
        using var to = new MotionTransform(2.0);
        using var by = new MotionTransform(0.5);
        using var action = MotionFromToSwiftBindingsTestLib_MotionTransformCsmExtensions.FromSwiftBindingsTestLibMotionTransform(to, by);
        AssertEqual("transform.toBy.local", action.Route, "init(to:by:mode:timing:isAdditive:) ran with mode .default");
    }

    public void TestScalar_RunsTheUnconstrainedInits()
    {
        using var to = new MotionScalar(3.0);
        using var toOnly = MotionFromToSwiftBindingsTestLib_MotionScalarCsmExtensions.FromSwiftBindingsTestLibMotionScalar(to);
        AssertEqual("generic.to", toOnly.Route, "init(from:to:timing:isAdditive:) ran");
        using var by = new MotionScalar(1.0);
        using var toBy = MotionFromToSwiftBindingsTestLib_MotionScalarCsmExtensions.FromSwiftBindingsTestLibMotionScalar(to, by);
        AssertEqual("generic.toBy", toBy.Route, "init(to:by:timing:isAdditive:) ran");
    }
}
