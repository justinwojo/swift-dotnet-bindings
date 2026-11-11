// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace Swift.Runtime.Tests;

/// <summary>
/// xunit collection definition that serializes test classes sharing
/// <see cref="SwiftObjectRegistry"/>'s process-global handle→proxy map. Concurrent
/// Register/Unregister calls from parallel classes break count-based assertions in
/// <c>SwiftObjectRegistryTests</c>.
///
/// The simulated exit flag (<see cref="SwiftExitGuardTestScope"/>) no longer needs this
/// collection. It is thread-scoped, so it cannot reach another test or the GC finalizer
/// thread. A collection could never have contained it anyway: the finalizer thread finalizes
/// objects from every test, whatever collection that test belongs to.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SwiftExitGuardCollection
{
    public const string Name = "SwiftExitGuard process-global flag";
}
