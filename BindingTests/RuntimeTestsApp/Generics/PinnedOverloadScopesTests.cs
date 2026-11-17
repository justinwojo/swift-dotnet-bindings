// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Default-omitting members whose same-named Swift siblings the reduced call can never reach
/// (<c>PinnedOverloadScopes.swift</c>). Each member exists only because the call-pinning replay does
/// not count those siblings as rivals, and each must reach the declaration it names: the Swift
/// bodies record which initializer ran and which defaults they received.
/// </summary>
public class PinnedOverloadScopesTests : TestBase
{
    public PinnedOverloadScopesTests(TestResults results) : base(results) { }

    public void TestBaseShortInitializerRunsBaseDeclaration()
    {
        using var value = new PinnedInitBase((nint)3);
        AssertEqual(1, value.Initializer, "PinnedInitBase(value:) runs PinnedInitBase.init");
        AssertEqual(3, value.Value, "value");
        AssertEqual("standard", value.Label, "the base declaration's default label");
    }

    public void TestOverridingShortInitializerRunsSubclassDeclaration()
    {
        using var value = new PinnedInitDerived((nint)7);
        AssertEqual(2, value.Initializer, "PinnedInitDerived(value:) runs PinnedInitDerived.init");
        AssertEqual(7, value.Value, "value");
        AssertEqual("standard-derived", value.Label, "the subclass declaration's default label");
    }

    public void TestSpecializationOutsidePinnedExtensionUsesPlainInitializer()
    {
        using var endpoint = new PinnedEndpointB((nint)5);
        using var transition =
            PinnedTransitionSwiftBindingsTestLib_PinnedEndpointBCsmExtensions.FromSwiftBindingsTestLibPinnedEndpointB(endpoint);
        AssertEqual("plain:5:40", transition.Summary,
            "PinnedTransition<PinnedEndpointB>(to:) runs the unconstrained initializer with its computed default");
    }
}
