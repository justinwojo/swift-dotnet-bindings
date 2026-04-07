// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace Swift.Runtime.Tests;

/// <summary>
/// xunit collection definition that serializes any test class touching
/// <see cref="SwiftExitGuard.SetProcessExitingForTest"/>. The flag is a single
/// process-global volatile bool — if two test classes interleave their
/// set-true/set-false calls, one class can observe the other's state and
/// short-circuit code paths it expected to exercise (e.g., the deinit
/// callback in <c>ProxyLifetimeTracker.OnEveryProtocolDeinitCore</c>).
///
/// Apply <c>[Collection(SwiftExitGuardCollection.Name)]</c> to any test class
/// that calls <c>SetProcessExitingForTest</c>; xunit will then run all such
/// classes serially within the test process.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SwiftExitGuardCollection
{
    public const string Name = "SwiftExitGuard process-global flag";
}
