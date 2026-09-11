// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Runtime coverage for a closure-backed value provider: the block is supplied at
/// construction and Swift calls it to obtain the value it then works with. Callback delivery
/// and VALUE delivery are separate questions — a bridge can dispatch to the managed block
/// correctly and still drop what the block hands back, which a consumer sees as "the callback
/// ran but nothing changed". Every test here pairs a fired-counter with a Swift-side
/// observation of the returned value so the two are distinguishable.
/// </summary>
public class ValueProviderDeliveryTests : TestBase
{
    public ValueProviderDeliveryTests(TestResults results) : base(results) { }

    private static SwiftArray<ProviderColor> Palette(int count)
    {
        var colors = new ProviderColor[count];
        for (var i = 0; i < count; i++)
            colors[i] = new ProviderColor(i + 1, i + 2, i + 3, i + 4);
        return new SwiftArray<ProviderColor>(colors);
    }

    /// <summary>
    /// The block fires AND the array it returns reaches Swift with its element count intact.
    /// A dropped return value would leave the counter at 1 and the count at 0.
    /// </summary>
    public void TestProviderBlockReturnValueReachesSwift()
    {
        var fired = 0;
        var provider = new DynamicGradientProvider(frame => { fired++; return Palette(3); });
        var count = provider.ColorCount(0.5);
        AssertEqual(1, fired, "Provider block fired exactly once");
        AssertEqual(3, count, "Swift counted the elements the block returned");
    }

    /// <summary>The frame Swift passes in reaches the block unchanged.</summary>
    public void TestProviderBlockReceivesFrameArgument()
    {
        var observed = double.NaN;
        var provider = new DynamicGradientProvider(frame => { observed = frame; return Palette(1); });
        provider.ColorCount(0.25);
        AssertApproxEqual(0.25, observed, 0.0001, "Block observes the frame Swift passed");
    }

    /// <summary>
    /// Element CONTENT, not just count: Swift reads the first colour's four components back
    /// out of the returned array.
    /// </summary>
    public void TestProviderReturnedElementContentsReachSwift()
    {
        var provider = new DynamicGradientProvider(_ => new SwiftArray<ProviderColor>(
            new[] { new ProviderColor(1, 2, 3, 4) }));
        // Swift computes r*1000 + g*100 + b*10 + a over the first element.
        AssertApproxEqual(1234, provider.FirstColorChecksum(0), 0.0001,
            "Swift reads the component values the block returned");
    }

    /// <summary>An empty return is delivered as empty, not as a dropped value.</summary>
    public void TestProviderEmptyReturnIsDeliveredAsEmpty()
    {
        var provider = new DynamicGradientProvider(_ => new SwiftArray<ProviderColor>());
        AssertEqual(0, provider.ColorCount(0), "Empty array round-trips as empty");
        AssertApproxEqual(-1, provider.FirstColorChecksum(0), 0.0001,
            "Swift takes its own no-first-element branch");
    }

    /// <summary>The array comes back out to C#, closing the round trip end to end.</summary>
    public void TestProviderArrayRoundTripsBackToManagedCaller()
    {
        var provider = new DynamicGradientProvider(_ => Palette(2));
        var colors = provider.Colors(0);
        AssertEqual(2, colors.Count, "Returned array round-trips back out to C#");
        AssertApproxEqual(1, colors[0].R, 0.0001, "First element survives the round trip");
        AssertApproxEqual(5, colors[1].A, 0.0001, "Second element survives the round trip");
    }

    /// <summary>The block is re-entered per call rather than its first result being cached.</summary>
    public void TestProviderBlockIsCalledPerRequest()
    {
        var fired = 0;
        var provider = new DynamicGradientProvider(_ => { fired++; return Palette(fired); });
        AssertEqual(1, provider.ColorCount(0), "First call returns one element");
        AssertEqual(2, provider.ColorCount(1), "Second call re-enters the block");
        AssertEqual(2, fired, "Block fired once per request");
    }

    /// <summary>The optional second block — supplied — delivers a primitive array.</summary>
    public void TestOptionalSecondProviderBlockDeliversValues()
    {
        var provider = new DynamicGradientProvider(
            _ => Palette(1),
            _ => new SwiftArray<double>(new[] { 0.25, 0.5, 1.0 }));
        AssertEqual(3, provider.LocationCount(0), "Swift counted the primitive array's elements");
        AssertApproxEqual(1.75, provider.LocationSum(0), 0.0001, "Swift summed the returned values");
    }

    /// <summary>Omitting the optional block leaves Swift on its own nil branch.</summary>
    public void TestOmittedOptionalBlockLeavesSwiftOnNilBranch()
    {
        var provider = new DynamicGradientProvider(_ => Palette(1));
        AssertEqual(-1, provider.LocationCount(0), "Omitted block is nil on the Swift side");
        AssertApproxEqual(-1, provider.LocationSum(0), 0.0001, "Swift takes its nil branch");
    }

    /// <summary>
    /// The provider installed after construction, the way a renderer attaches one to an
    /// already-built animation — the block must still deliver through the second hop.
    /// </summary>
    public void TestProviderInstalledAfterConstructionStillDelivers()
    {
        var provider = new DynamicGradientProvider(_ => new SwiftArray<ProviderColor>(
            new[] { new ProviderColor(9, 8, 7, 6) }));
        var target = new DynamicGradientTarget();
        target.SetProvider(provider);
        AssertApproxEqual(9876, target.RenderedChecksum(0), 0.0001,
            "Value reaches Swift through the installed provider");
    }

    /// <summary>
    /// The provider outlives the statement that built it: driving it on a later call proves
    /// the escaping block was retained rather than collected after construction returned.
    /// </summary>
    public void TestProviderBlockSurvivesConstructionScope()
    {
        var target = new DynamicGradientTarget();
        target.SetProvider(new DynamicGradientProvider(_ => new SwiftArray<ProviderColor>(
            new[] { new ProviderColor(1, 1, 1, 1) })));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        AssertApproxEqual(1111, target.RenderedChecksum(0), 0.0001,
            "Block still delivers after its construction scope is gone");
    }
}
