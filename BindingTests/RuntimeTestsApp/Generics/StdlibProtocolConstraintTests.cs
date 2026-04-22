// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Regression coverage for generic types whose single generic parameter is
/// constrained by a Swift stdlib protocol that was absent from
/// SwiftDatabase.xml (Equatable / Decodable / Encodable). Without the XML
/// entries the metadata-accessor PWT slot could not be resolved and the
/// enclosing type silently tombstoned — same shape as WeatherKit's
/// <c>Forecast&lt;TElement&gt;</c>. These tests assert that the type emits,
/// can be instantiated via a concrete-T factory, and releases cleanly.
/// </summary>
public class StdlibProtocolConstraintTests : TestBase
{
    public StdlibProtocolConstraintTests(TestResults results) : base(results) { }

    public void TestEquatableContainer_ConcreteFactoryRoundTrips()
    {
        using var container = Functions.MakeEquatableContainer(42);
        AssertTrue(container is not null, "Functions.MakeEquatableContainer should return a live instance");
        AssertTrue(
            container!.Payload.DangerousGetHandle() != IntPtr.Zero,
            "EquatableContainer payload must be a non-null Swift handle");
    }

    public void TestCodableContainer_ConcreteFactoryRoundTrips()
    {
        using var container = Functions.MakeCodableContainer(42);
        AssertTrue(container is not null, "Functions.MakeCodableContainer should return a live instance");
        AssertTrue(
            container!.Payload.DangerousGetHandle() != IntPtr.Zero,
            "CodableContainer payload must be a non-null Swift handle");
    }
}
