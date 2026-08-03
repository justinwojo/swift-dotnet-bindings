// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.Versioning;
using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.Types;

/// <summary>
/// Tests for the Swift.Runtime SwiftUI value-type factories:
/// <c>SwiftUI.Color.Create</c> and <c>SwiftUI.Font.System</c>, which sit on the
/// <c>SBW_SwiftUI_Color_Create</c> / <c>SBW_SwiftUI_Font_System</c> cdecl shims.
/// </summary>
/// <remarks>
/// Both factories are <c>[UnsupportedOSPlatform("maccatalyst")]</c> because the SwiftUI
/// shims are compiled out of the macabi runtime slice. The attribute is mirrored onto this
/// consumer so CA1416 is satisfied; the Catalyst contract itself is asserted at runtime by
/// <see cref="TestCatalystFactories_ThrowPlatformNotSupported"/>, so this class runs — and
/// means something — on every platform leg including Catalyst.
/// </remarks>
[UnsupportedOSPlatform("maccatalyst")]
public class SwiftUIValueConstructionTests : TestBase
{
    public SwiftUIValueConstructionTests(TestResults results) : base(results) { }

    private static bool IsCatalyst => OperatingSystem.IsMacCatalyst();

    public void TestColorCreate_ReturnsLivePayload()
    {
        if (IsCatalyst)
        {
            TestLogger.Info("Skipped body on Mac Catalyst; the throw contract is asserted separately");
            return;
        }

        using var color = global::SwiftUI.Color.Create(0.25, 0.5, 0.75, 1.0);
        AssertNotNull(color, "Color.Create should return a non-null instance");
        AssertFalse(color.Payload.IsInvalid, "Color payload should be a live buffer");
    }

    public void TestColorCreate_DefaultOpacity()
    {
        if (IsCatalyst)
        {
            TestLogger.Info("Skipped body on Mac Catalyst; the throw contract is asserted separately");
            return;
        }

        using var color = global::SwiftUI.Color.Create(1.0, 0.0, 0.0);
        AssertFalse(color.Payload.IsInvalid, "Three-argument Color.Create should still produce a live buffer");
    }

    public void TestColorCreate_NonFiniteComponentThrows()
    {
        if (IsCatalyst)
        {
            TestLogger.Info("Skipped body on Mac Catalyst; the throw contract is asserted separately");
            return;
        }

        AssertThrows<ArgumentOutOfRangeException>(
            () => global::SwiftUI.Color.Create(double.NaN, 0.0, 0.0, 1.0),
            "NaN component should be rejected");
    }

    public void TestFontSystem_EveryWeightAndDesign()
    {
        if (IsCatalyst)
        {
            TestLogger.Info("Skipped body on Mac Catalyst; the throw contract is asserted separately");
            return;
        }

        // The numeric enum values are the ABI contract with the Swift shim's switch, so
        // walking the whole declared range proves both sides agree on every code.
        foreach (global::SwiftUI.Font.Weight weight in new[]
        {
            global::SwiftUI.Font.Weight.UltraLight, global::SwiftUI.Font.Weight.Thin,
            global::SwiftUI.Font.Weight.Light, global::SwiftUI.Font.Weight.Regular,
            global::SwiftUI.Font.Weight.Medium, global::SwiftUI.Font.Weight.Semibold,
            global::SwiftUI.Font.Weight.Bold, global::SwiftUI.Font.Weight.Heavy,
            global::SwiftUI.Font.Weight.Black,
        })
        {
            foreach (global::SwiftUI.Font.Design design in new[]
            {
                global::SwiftUI.Font.Design.Default, global::SwiftUI.Font.Design.Serif,
                global::SwiftUI.Font.Design.Rounded, global::SwiftUI.Font.Design.Monospaced,
            })
            {
                using var font = global::SwiftUI.Font.System(15.0, weight, design);
                AssertFalse(font.Payload.IsInvalid, $"Font.System({weight}, {design}) should produce a live buffer");
            }
        }
    }

    public void TestFontSystem_InvalidSizeThrows()
    {
        if (IsCatalyst)
        {
            TestLogger.Info("Skipped body on Mac Catalyst; the throw contract is asserted separately");
            return;
        }

        AssertThrows<ArgumentOutOfRangeException>(
            () => global::SwiftUI.Font.System(0.0),
            "Zero point size should be rejected");
    }

    public void TestFontSystem_UndeclaredWeightThrows()
    {
        if (IsCatalyst)
        {
            TestLogger.Info("Skipped body on Mac Catalyst; the throw contract is asserted separately");
            return;
        }

        AssertThrows<ArgumentOutOfRangeException>(
            () => global::SwiftUI.Font.System(15.0, (global::SwiftUI.Font.Weight)99),
            "Out-of-contract weight code should be rejected before it reaches the shim");
    }

    /// <summary>
    /// The Catalyst half of the contract: the shims are sliced out of macabi, so both
    /// factories must fail with a diagnosable <see cref="PlatformNotSupportedException"/>
    /// rather than an <see cref="EntryPointNotFoundException"/> from the loader.
    /// </summary>
    public void TestCatalystFactories_ThrowPlatformNotSupported()
    {
        if (!IsCatalyst)
        {
            TestLogger.Info("Not Mac Catalyst; the platform-throw contract is asserted on the Catalyst leg");
            return;
        }

        AssertThrows<PlatformNotSupportedException>(
            () => global::SwiftUI.Color.Create(0.1, 0.2, 0.3, 0.4),
            "Color.Create should throw PlatformNotSupportedException on Mac Catalyst");
        AssertThrows<PlatformNotSupportedException>(
            () => global::SwiftUI.Font.System(15.0),
            "Font.System should throw PlatformNotSupportedException on Mac Catalyst");
    }
}
