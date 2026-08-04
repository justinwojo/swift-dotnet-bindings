// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if SWIFTUI_VALUE_PROBE
using System.Runtime.Versioning;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Types;

/// <summary>
/// End-to-end proof of the deliverable: a C#-constructed <c>SwiftUI.Color</c> /
/// <c>SwiftUI.Font</c> passed as an argument to a generated binding whose Swift parameter
/// is typed <c>SwiftUI.Color</c> / <c>SwiftUI.Font</c>.
/// </summary>
/// <remarks>
/// The Swift probe reconstructs the expected value and reports what actually arrived, so
/// these assert VALUE round-trip, not merely that a non-null handle crossed the ABI. The
/// probe's Swift source is behind <c>#if !targetEnvironment(macCatalyst)</c>, so the whole
/// file is gated on the SWIFTUI_VALUE_PROBE constant the csproj defines when the binding
/// for it exists.
/// </remarks>
[UnsupportedOSPlatform("maccatalyst")]
public class SwiftUIValueRoundTripTests : TestBase
{
    public SwiftUIValueRoundTripTests(TestResults results) : base(results) { }

    public void TestColorComponentsSurviveTheAbi()
    {
        using var probe = new SwiftUIValueProbe();
        using var color = global::SwiftUI.Color.Create(0.25, 0.5, 0.75, 0.5);

        AssertApproxEqual(0.25, probe.ColorComponent(color, 0), 0.01, "red component should round-trip");
        AssertApproxEqual(0.5, probe.ColorComponent(color, 1), 0.01, "green component should round-trip");
        AssertApproxEqual(0.75, probe.ColorComponent(color, 2), 0.01, "blue component should round-trip");
        AssertApproxEqual(0.5, probe.ColorComponent(color, 3), 0.01, "alpha component should round-trip");
    }

    public void TestColorDefaultOpacityIsOpaque()
    {
        using var probe = new SwiftUIValueProbe();
        using var color = global::SwiftUI.Color.Create(1.0, 0.0, 0.0);

        AssertApproxEqual(1.0, probe.ColorComponent(color, 0), 0.01, "red component should be fully saturated");
        AssertApproxEqual(1.0, probe.ColorComponent(color, 3), 0.01, "omitted opacity should default to opaque");
    }

    public void TestColorEqualsRebuiltValue()
    {
        using var probe = new SwiftUIValueProbe();
        using var color = global::SwiftUI.Color.Create(0.1, 0.2, 0.3, 0.4);

        AssertTrue(probe.ColorEquals(color, 0.1, 0.2, 0.3, 0.4),
            "constructed Color should equal a Swift-side Color built from the same components");
        AssertFalse(probe.ColorEquals(color, 0.9, 0.2, 0.3, 0.4),
            "a different red component must not compare equal");
    }

    public void TestMultiWordValueSurvivesTheAbi()
    {
        // Color and Font are single words, so they cross correctly even if only the
        // frozen/non-frozen decision is right. Text is four words: passing it by pointer, or
        // by value at the wrong width, hands Swift something other than the string built here.
        using var probe = new SwiftUIValueProbe();
        using var text = global::SwiftUI.Text.Create("value round-trip");

        AssertTrue(probe.TextEquals(text, "value round-trip"),
            "constructed Text should equal a Swift-side Text built from the same string");
    }

    public void TestMultiWordValueContentIsNotIgnored()
    {
        // Guards the oracle: if the probe compared anything other than the value that arrived,
        // a mismatched string would still report equal.
        using var probe = new SwiftUIValueProbe();
        using var text = global::SwiftUI.Text.Create("value round-trip");

        AssertFalse(probe.TextEquals(text, "a different string"),
            "a Text carrying different content must not compare equal");
    }

    public void TestFontCarriesSizeWeightAndDesign()
    {
        using var probe = new SwiftUIValueProbe();
        using var font = global::SwiftUI.Font.System(
            23.0, global::SwiftUI.Font.Weight.Semibold, global::SwiftUI.Font.Design.Rounded);

        AssertTrue(
            probe.FontIsSystem(font, 23.0,
                (int)global::SwiftUI.Font.Weight.Semibold, (int)global::SwiftUI.Font.Design.Rounded),
            "constructed Font should equal a Swift-side system font with the same size/weight/design");
    }

    public void TestFontWeightCodeIsNotIgnored()
    {
        // Guards the mapping itself: if the shim ignored the weight code and always built a
        // regular font, this would compare equal and the test would go red.
        using var probe = new SwiftUIValueProbe();
        using var font = global::SwiftUI.Font.System(23.0, global::SwiftUI.Font.Weight.Black);

        AssertFalse(
            probe.FontIsSystem(font, 23.0,
                (int)global::SwiftUI.Font.Weight.UltraLight, (int)global::SwiftUI.Font.Design.Default),
            "a black-weight font must not equal an ultraLight-weight font of the same size");
    }

    public void TestFontDesignCodeIsNotIgnored()
    {
        using var probe = new SwiftUIValueProbe();
        using var font = global::SwiftUI.Font.System(
            23.0, global::SwiftUI.Font.Weight.Regular, global::SwiftUI.Font.Design.Monospaced);

        AssertFalse(
            probe.FontIsSystem(font, 23.0,
                (int)global::SwiftUI.Font.Weight.Regular, (int)global::SwiftUI.Font.Design.Serif),
            "a monospaced font must not equal a serif font of the same size and weight");
    }

    public void TestEveryWeightAndDesignRoundTrips()
    {
        using var probe = new SwiftUIValueProbe();

        for (int weight = 0; weight <= 8; weight++)
        {
            for (int design = 0; design <= 3; design++)
            {
                using var font = global::SwiftUI.Font.System(
                    18.0, (global::SwiftUI.Font.Weight)weight, (global::SwiftUI.Font.Design)design);

                AssertTrue(probe.FontIsSystem(font, 18.0, weight, design),
                    $"weight code {weight} / design code {design} should map to the same Swift font on both sides");
            }
        }
    }
}
#endif
