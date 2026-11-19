// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

public class BlastRadiusGoldenTests
{
    static string Golden(string name, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "..",
            "BindingTests", "BlastRadius.Baseline", "measurements", name)));

    static string Diff(params string[] body) =>
        "--- baseline.strings-swift.txt\n+++ consumer.strings-swift.txt\n" + string.Join("\n", body) + "\n";

    // What the .NET 10 + Xcode 27.2 lane measures: the .NET 10 macOS runtime autolinks
    // libswiftCompatibility56, so both executables carry its swift_task_* strings as context.
    static readonly string Net10Xcode27 = Diff("@@ -1,5 +1,7 @@", " Swift.Runtime", " Swift.Runtime.dll",
        "+SwiftBindings.Apple", "+SwiftBindings.Apple.dll", " swift_task_enterThreadLocalContext",
        " swift_task_escalate", " swift_task_exitThreadLocalContext");

    // What the .NET 11 + Xcode 27.2 lane measures: its runtime is built for macOS 14, no compat strings.
    static readonly string Net11Xcode27 = Diff("@@ -1,2 +1,4 @@", " Swift.Runtime", " Swift.Runtime.dll",
        "+SwiftBindings.Apple", "+SwiftBindings.Apple.dll");

    [Theory]
    [InlineData(null, "strings-swift.diff")]
    [InlineData("26.2", "strings-swift.diff")]
    [InlineData("27.2", BlastRadiusGolden.Xcode27StringsGolden)]
    public void StringsGolden_IsChosenByThePlatformVersion(string? version, string expected)
    {
        Assert.Equal(expected, BlastRadiusGolden.GoldenName("strings-swift.diff", version));
        Assert.Equal("nm.diff", BlastRadiusGolden.GoldenName("nm.diff", version));
        Assert.Equal("otool-L.diff", BlastRadiusGolden.GoldenName("otool-L.diff", version));
    }

    [Fact]
    public void BothXcode27Pairings_MatchTheSharedGolden()
    {
        var golden = Golden(BlastRadiusGolden.Xcode27StringsGolden);
        Assert.True(BlastRadiusGolden.Matches(golden, Net10Xcode27));
        Assert.True(BlastRadiusGolden.Matches(golden, Net11Xcode27));
    }

    [Fact]
    public void Xcode26Measurement_MatchesItsGolden_AndNotTheXcode27One()
    {
        var measured = Diff("@@ -1,4 +1,5 @@", " Swift.Runtime", "+SwiftBindings.Apple",
            " swift_task_enterThreadLocalContext", " swift_task_escalate", " swift_task_exitThreadLocalContext");
        Assert.True(BlastRadiusGolden.Matches(Golden("strings-swift.diff"), measured));
        Assert.False(BlastRadiusGolden.Matches(Golden(BlastRadiusGolden.Xcode27StringsGolden), measured));
    }

    [Theory]
    // The supplement drags in one more Swift string.
    [InlineData("+SwiftBindings.Apple", "+SwiftBindings.Apple.dll", "+swift_retain")]
    // It stops adding its assembly name.
    [InlineData("+SwiftBindings.Apple", null, null)]
    // It removes a string the baseline carries.
    [InlineData("-Swift.Runtime.dll", "+SwiftBindings.Apple", "+SwiftBindings.Apple.dll")]
    public void AnyChangeToWhatTheSupplementAddsOrRemoves_Fails(string a, string? b, string? c)
    {
        var lines = new System.Collections.Generic.List<string> { "@@ -1,2 +1,4 @@", " Swift.Runtime" };
        foreach (var l in new[] { a, b, c })
            if (l != null) lines.Add(l);
        Assert.False(BlastRadiusGolden.Matches(Golden(BlastRadiusGolden.Xcode27StringsGolden), Diff(lines.ToArray())));
    }

    [Fact]
    public void AnAddedLineTurningIntoContext_Fails()
    {
        // The baseline app now carries SwiftBindings.Apple too: the supplement no longer adds it.
        var measured = Diff("@@ -1,3 +1,4 @@", " Swift.Runtime", " Swift.Runtime.dll", " SwiftBindings.Apple",
            "+SwiftBindings.Apple.dll");
        Assert.False(BlastRadiusGolden.Matches(Golden(BlastRadiusGolden.Xcode27StringsGolden), measured));
    }

    [Fact]
    public void Headers_AreNotChanges_EvenWhenARemovedLineLooksLikeOne()
    {
        var diff = "--- /a/baseline.nm.txt\t2026-04-17 21:32:46\n+++ /b/consumer.nm.txt\t2026-11-19 01:00:00\n" +
                   "@@ -1,2 +1,1 @@\n--- x\n kept\n\\ No newline at end of file\n";
        Assert.Equal(new[] { "--- x" }, BlastRadiusGolden.Changes(diff));
        Assert.Empty(BlastRadiusGolden.Changes(""));
    }

    [Fact]
    public void CommittedGoldens_StillMeasureTheSupplement()
    {
        // A golden with no changes would pass a measurement that also has none: pin what each holds.
        Assert.Equal(new[] { "-BlastRadius.Baseline:", "+BlastRadius.Consumer:" },
            BlastRadiusGolden.Changes(Golden("otool-L.diff")));
        Assert.Empty(BlastRadiusGolden.Changes(Golden("nm.diff")));
        Assert.Equal(new[] { "+SwiftBindings.Apple" }, BlastRadiusGolden.Changes(Golden("strings-swift.diff")));
        Assert.Equal(new[] { "+SwiftBindings.Apple", "+SwiftBindings.Apple.dll" },
            BlastRadiusGolden.Changes(Golden(BlastRadiusGolden.Xcode27StringsGolden)));
    }
}
