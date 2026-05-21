// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

// Regression coverage for the property/subscript wrapper crash on constrained
// generic classes (see Sources/SwiftBindingsTestLib/Generics/ConstrainedGenericConcreteProperty.swift
// for the bug rationale). Both the property getter and the subscript getter
// pre-fix crashed with SIGSEGV inside the wrapper's `as!`-cast because the
// Swift wrapper signature dropped the PWT pointer the C# side passes.
public class ConstrainedGenericConcretePropertyTests : TestBase
{
    public ConstrainedGenericConcretePropertyTests(TestResults results) : base(results) { }

    public void TestConcreteLabelGetter()
    {
        var box = new CgcpPlainBox<CgcpPlainConformer>(content: new CgcpPlainConformer());
        var label = box.Label.ToString();
        AssertEqual("plain", label, "CgcpPlainBox.Label");
    }

    public void TestConcreteSubscriptGetter()
    {
        var box = new CgcpPlainBox<CgcpPlainConformer>(content: new CgcpPlainConformer());
        var value = box[7].ToString();
        AssertEqual("plain[7]", value, "CgcpPlainBox[7]");
    }

    public void TestConcretePropertySetterRoundTrip()
    {
        // Round-trip the setter half of the wrapper ABI contract. Pre-fix the
        // setter wrapper dropped the PWT pointer the same way the getter did,
        // causing the write to land through a garbage self_ pointer.
        var box = new CgcpPlainBox<CgcpPlainConformer>(content: new CgcpPlainConformer());
        box.Tag = "written";
        AssertEqual("written", box.Tag.ToString(), "CgcpPlainBox.Tag round-trip");
    }

    public void TestConcreteSubscriptSetterRoundTrip()
    {
        // Round-trip the settable subscript on the constrained-generic parent —
        // covers the subscript setter wrapper that pre-fix slid the PWT pointer
        // into self_ on write.
        var box = new CgcpPlainBox<CgcpPlainConformer>(content: new CgcpPlainConformer());
        box["alpha"] = "stored-alpha";
        AssertEqual("stored-alpha", box["alpha"].ToString(), "CgcpPlainBox[\"alpha\"] round-trip");
        AssertEqual("missing[beta]", box["beta"].ToString(), "CgcpPlainBox[\"beta\"] default");
    }
}
