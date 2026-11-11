// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// The native inputs every BindingTests app lane loads at run time, located by xcframework slice.
/// </summary>
/// <remarks>
/// Each regeneration rebuilds these xcframeworks for one lane only: a device regen leaves the
/// wrappers with just <c>ios-arm64</c>, a macOS regen with just <c>macos-arm64</c>, and so on.
/// The app projects and the post-build injection skip an input whose slice is absent rather than
/// fail (a NativeReference to an xcframework without the RID's slice is a hard MSBuild error in
/// other contexts), so a <c>--skip-regen</c> run after another lane's regen used to build an app
/// without <c>SwiftBindings.framework</c> and fail hundreds of tests with DllNotFoundException.
/// Checking the slices before the app build turns that into one actionable error.
/// </remarks>
public static class LaneNativeSlices
{
    /// <summary>
    /// The directories that must exist for <paramref name="sliceId"/>: the test library and its
    /// dependency (under <c>.build/</c>) and both generated wrapper frameworks (under <c>output/</c>).
    /// </summary>
    public static IReadOnlyList<string> Required(string bindingTestsDir, string sliceId,
        string module, string dependencyModule, string wrapperModule)
    {
        if (string.IsNullOrWhiteSpace(sliceId))
            throw new ArgumentException("A lane must name its xcframework slice.", nameof(sliceId));
        var build = Path.Combine(bindingTestsDir, ".build");
        var output = Path.Combine(bindingTestsDir, "output");
        var dependencyWrapper = dependencyModule + wrapperModule;
        return new[]
        {
            Path.Combine(build, $"{module}.xcframework", sliceId, $"{module}.framework"),
            Path.Combine(build, $"{dependencyModule}.xcframework", sliceId, $"{dependencyModule}.framework"),
            Path.Combine(output, $"{wrapperModule}.xcframework", sliceId, $"{wrapperModule}.framework"),
            Path.Combine(output, $"{dependencyWrapper}.xcframework", sliceId, $"{dependencyWrapper}.framework"),
        };
    }

    public static IReadOnlyList<string> Missing(IEnumerable<string> required) =>
        required.Where(path => !Directory.Exists(path)).ToList();

    /// <summary>The error for a lane whose native inputs lack its slice; null when none is missing.</summary>
    public static string? Failure(string lane, string sliceId, IReadOnlyList<string> missing, bool reusedBindings)
    {
        if (missing.Count == 0)
            return null;
        var list = string.Join(", ", missing);
        return reusedBindings
            ? $"{lane}: the regenerated native inputs have no '{sliceId}' slice ({list}). The last " +
              "regeneration targeted a different lane, for example a device regen before a simulator " +
              "--skip-regen run, and the app would be built without these frameworks. Rerun this lane " +
              "without --skip-regen to regenerate them."
            : $"{lane}: regeneration did not produce the '{sliceId}' slice ({list}). The app would be " +
              "built without these frameworks; inspect the xcframework and wrapper build steps above.";
    }
}
