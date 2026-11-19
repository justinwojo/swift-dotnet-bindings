// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/// <summary>
/// The comparison behind <c>nuke validate-blast-radius</c>. The gate measures what pulling
/// SwiftBindings.Apple into an app adds to or removes from the executable, as unified diffs of
/// the baseline app's and the consumer app's <c>otool -L</c>, <c>nm -gU</c> and Swift strings.
/// A diff's added and removed lines are that blast radius. Its context lines are what both
/// executables already carry, which the host's own runtime decides: the macOS runtime libraries
/// of the pinned .NET 10 workloads autolink <c>libswiftCompatibility56</c>, so their executables
/// hold its <c>swift_task_*</c> hook strings, and those of the .NET 11 workload do not. So the gate compares the
/// changed lines, in order, and nothing else.
/// </summary>
public static class BlastRadiusGolden
{
    public static readonly IReadOnlyList<string> Gates = new[] { "otool-L.diff", "nm.diff", "strings-swift.diff" };

    /// <summary>
    /// The strings golden of the Xcode 27 workloads, .NET 10 and .NET 11 alike: their macOS host
    /// also records the managed assembly filenames, so the supplement adds
    /// <c>SwiftBindings.Apple.dll</c> as well as <c>SwiftBindings.Apple</c>.
    /// </summary>
    public const string Xcode27StringsGolden = "strings-swift.xcode27.diff";

    /// <summary>The committed golden a measured diff is compared with on a lane whose Apple
    /// platform version is <paramref name="applePlatformVersion"/> (null outside a profile).</summary>
    public static string GoldenName(string gate, string? applePlatformVersion)
    {
        if (gate != "strings-swift.diff" || applePlatformVersion is null)
            return gate;
        var major = int.Parse(applePlatformVersion.Split('.')[0], CultureInfo.InvariantCulture);
        return major >= 27 ? Xcode27StringsGolden : gate;
    }

    /// <summary>
    /// The added and removed lines of a single-file unified diff, in order, each with its
    /// <c>+</c>/<c>-</c> marker. The <c>---</c>/<c>+++</c> file headers, the hunk headers (whose
    /// counts include the context) and the context lines are dropped. Any other line before the
    /// first hunk, such as <c>diff</c>'s "Binary files ... differ", is kept as a change, so a
    /// difference the diff could not show as hunks never compares equal to an empty golden.
    /// </summary>
    public static IReadOnlyList<string> Changes(string unifiedDiff)
    {
        var changes = new List<string>();
        var inHunk = false;
        foreach (var raw in unifiedDiff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunk = true;
                continue;
            }
            if (inHunk ? line.Length > 0 && (line[0] == '+' || line[0] == '-')
                       : line.Length > 0 && !line.StartsWith("--- ", StringComparison.Ordinal) &&
                         !line.StartsWith("+++ ", StringComparison.Ordinal))
                changes.Add(line);
        }
        return changes;
    }

    /// <summary>True when the measured diff adds and removes exactly what the golden does.</summary>
    public static bool Matches(string golden, string measured) =>
        Changes(golden).SequenceEqual(Changes(measured), StringComparer.Ordinal);
}
