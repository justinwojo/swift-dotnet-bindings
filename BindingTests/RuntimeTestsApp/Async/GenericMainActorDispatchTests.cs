// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Bug (d) round-trip: a `@MainActor`-isolated instance method on a GENERIC struct
/// (Factory/FactoryKit's `resolveOnMainActor()` shape). Any instance method on a generic
/// struct routes through <c>GenericDispatchEmitter</c>'s static-dispatch/type-erasure path
/// regardless of whether it references the parent's generic parameter — a different
/// emission path than the non-generic-struct case in <see cref="MainActorTests"/>.
/// Pre-fix, the `@MainActor` annotation reached only the
/// `@_cdecl` entry point, not the type-erasure dispatch shim's protocol requirement +
/// witness the entry point calls through, so Swift 6 rejected the unannotated call from
/// a nonisolated context at compile time. This class exercises the resulting synchronous
/// C# API end-to-end at runtime, mirroring the sync-gate-lift convention in
/// <see cref="MainActorTests"/> (the harness drives tests on the main thread).
/// </summary>
public class GenericMainActorDispatchTests : TestBase
{
    public GenericMainActorDispatchTests(TestResults results) : base(results) { }

    public void TestMainActorGenericContainer_ResolveOnMainActor()
    {
        var container = TestLibFunctions.MakeMainActorGenericContainer(7);
        var result = container.ResolveOnMainActor();
        AssertEqual(1, result, "resolveOnMainActor() on a generic struct's type-erasure dispatch shim must succeed on the main thread");
    }
}
