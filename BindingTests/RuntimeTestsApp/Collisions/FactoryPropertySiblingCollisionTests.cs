// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// Constructor-factory recovery next to a property that already owns the factory's name. The
/// binding compiling is the gate (a factory emitted against the property is CS0102); at runtime the
/// positional constructor and the name-owning property must both be the members they claim to be.
/// </summary>
public class FactoryPropertySiblingCollisionTests : TestBase
{
    public FactoryPropertySiblingCollisionTests(TestResults results) : base(results) { }

    public void TestPositionalConstructorAndPropertySurvive()
    {
        using var host = new FactoryPropertySiblingHost(5);

        AssertEqual("positional", host.Source, "The positional initializer keeps the plain constructor");
        AssertEqual(5, host.Value);
        AssertEqual(5, host.CreateWithFoo, "The property owns the CreateWithFoo name");
    }
}
