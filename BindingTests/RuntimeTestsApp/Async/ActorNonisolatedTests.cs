// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Tests for <c>nonisolated</c> members on Swift actor types.
///
/// Actor-isolated members still require async dispatch (tracked as SB0001 fallback),
/// but <c>nonisolated</c> methods and properties opt out of the actor's isolation and
/// are safe to reach through a synchronous <c>@_cdecl</c> wrapper from any context.
/// </summary>
public class ActorNonisolatedTests : TestBase
{
    public ActorNonisolatedTests(TestResults results) : base(results) { }

    public void TestCounter_Description_Nonisolated()
    {
        var counter = SwiftBindingsTestLib.Functions.CreateCounter();
        var description = counter.GetDescription();
        AssertEqual("Counter actor", description, "nonisolated description() should round-trip");
        counter.Dispose();
    }

    public void TestCounter_TypeName_Nonisolated()
    {
        var counter = SwiftBindingsTestLib.Functions.CreateCounter();
        var name = counter.TypeName;
        AssertEqual("Counter", name, "nonisolated typeName property should round-trip");
        counter.Dispose();
    }

    public void TestCounter_MultipleNonisolatedCalls()
    {
        var counter = SwiftBindingsTestLib.Functions.CreateCounter();
        for (int i = 0; i < 5; i++)
        {
            AssertEqual("Counter", counter.TypeName, "typeName stable across calls");
            AssertEqual("Counter actor", counter.GetDescription(), "description stable across calls");
        }
        counter.Dispose();
    }
}
