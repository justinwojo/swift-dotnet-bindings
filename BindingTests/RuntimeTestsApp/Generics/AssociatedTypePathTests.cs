// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

public class AssociatedTypePathTests : TestBase
{
    public AssociatedTypePathTests(TestResults results) : base(results) { }

    public void TestChildConstraintUsesChildElement()
    {
        using var host = new AssociatedPathHost();
        using var value = new PathValidRoot();
        AssertEqual(42, host.ReadChild(value), "Child.Element is Int32 although Root.Element is String");
    }

    public void TestRootConstraintKeepsIndependentHealthyOverload()
    {
        using var host = new AssociatedPathHost();
        using var value = new PathInverseRoot();
        AssertEqual(17, host.ReadRoot(value), "Root.Element is Int32 although Child.Element is String");
    }
}
