// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Shared gate for sweep tests that read the generated BindingTests output under
/// <c>BindingTests/output/</c> (produced by <c>nuke binding-tests --compile-only</c>).
///
/// Where the output is absent the test skips rather than fails — but the skip reason carries
/// <see cref="SkipMarker"/> so the unit-test pass-floor gate can tell a designed
/// environment skip from a test that silently vanished, and count it toward the floor.
/// In CI the <c>SWIFT_BINDINGS_REQUIRE_GENERATED_BINDINGTESTS_OUTPUT</c> env gate turns the
/// skip into a hard failure: the enforcement step runs after the compile gate has produced
/// the output, selecting every such test via <see cref="TraitCategory"/>.
///
/// A test using this helper must therefore also carry
/// <c>[Trait("Category", GeneratedBindingsOutputRequirement.TraitCategory)]</c>, or it gets the
/// floor accounting without the CI enforcement.
/// </summary>
public static class GeneratedBindingsOutputRequirement
{
    /// <summary>
    /// Stable token embedded in the skip reason. The unit-test pass-floor gate in
    /// <c>build/Build.Test.cs</c> matches this exact string in the trx — keep the two in lockstep.
    /// </summary>
    public const string SkipMarker = "[generated-bindings-output-missing]";

    /// <summary>xunit trait category selecting all tests behind this gate.</summary>
    public const string TraitCategory = "RequiresGeneratedBindingsOutput";

    /// <summary>
    /// Skips the calling test with the marker-bearing reason when <paramref name="exists"/> is
    /// false — unless the env gate is set, in which case absence is a hard failure.
    /// </summary>
    public static void SkipUnlessAvailable(bool exists, string detail)
    {
        if (Required())
            Assert.True(exists, detail);
        Skip.IfNot(exists, $"{detail} {SkipMarker} — run `nuke binding-tests --compile-only` first.");
    }

    private static bool Required()
        => string.Equals(
            Environment.GetEnvironmentVariable("SWIFT_BINDINGS_REQUIRE_GENERATED_BINDINGTESTS_OUTPUT"),
            "true",
            StringComparison.OrdinalIgnoreCase);
}
