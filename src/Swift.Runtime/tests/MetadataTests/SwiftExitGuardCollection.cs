// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace Swift.Runtime.Tests;

/// <summary>
/// xunit collection definition that serializes test classes sharing process-global
/// state: <see cref="SwiftExitGuard"/>'s exit flag and/or <see cref="SwiftObjectRegistry"/>'s
/// handle→proxy map. Both are single-process globals — if two test classes run in
/// parallel, one can observe or mutate the other's state (e.g., interleaved
/// SetProcessExitingForTest true/false calls make <c>OnEveryProtocolDeinitCore</c>
/// short-circuit a code path a test expected to exercise, and concurrent
/// Register/Unregister calls break count-based assertions in
/// <c>SwiftObjectRegistryTests</c>).
///
/// Apply <c>[Collection(SwiftExitGuardCollection.Name)]</c> to any test class
/// that calls <c>SetProcessExitingForTest</c> or mutates the registry; xunit
/// will then run all such classes serially within the test process. Tests that
/// touch the exit flag should ALSO wrap mutations in
/// <see cref="SwiftExitGuardTestScope"/> as a belt-and-suspenders Monitor lock —
/// rare flakes were observed under full-suite runs where the collection alone
/// didn't serialize reliably.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SwiftExitGuardCollection
{
    public const string Name = "SwiftExitGuard process-global flag";
}
