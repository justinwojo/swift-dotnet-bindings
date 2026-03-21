// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for Swift OptionSet types projected as structs with int RawValue:
/// TextStyle (Bold, Italic, Underline, Strikethrough), ImageRequest.Options
/// (DisableCache, ReturnCached, LowPriority), and associated free functions.
/// </summary>
public class OptionSetTests : TestBase
{
    public OptionSetTests(TestResults results) : base(results) { }

    #region Tier 1 — TextStyle Blittable RawValue

    public void TestTextStyleBoldRawValue()
    {
        var style = TextStyle.Bold;
        AssertTrue(style.RawValue != 0, "Bold RawValue should be non-zero");
        TestLogger.Info($"TextStyle.Bold.RawValue = {style.RawValue}");
    }

    public void TestTextStyleItalicRawValue()
    {
        var style = TextStyle.Italic;
        AssertTrue(style.RawValue != 0, "Italic RawValue should be non-zero");
        TestLogger.Info($"TextStyle.Italic.RawValue = {style.RawValue}");
    }

    public void TestTextStyleUnderlineRawValue()
    {
        var style = TextStyle.Underline;
        AssertTrue(style.RawValue != 0, "Underline RawValue should be non-zero");
        TestLogger.Info($"TextStyle.Underline.RawValue = {style.RawValue}");
    }

    public void TestTextStyleStrikethroughRawValue()
    {
        var style = TextStyle.Strikethrough;
        AssertTrue(style.RawValue != 0, "Strikethrough RawValue should be non-zero");
        TestLogger.Info($"TextStyle.Strikethrough.RawValue = {style.RawValue}");
    }

    public void TestTextStyleStaticInstancesDistinct()
    {
        var bold = TextStyle.Bold.RawValue;
        var italic = TextStyle.Italic.RawValue;
        var underline = TextStyle.Underline.RawValue;
        var strikethrough = TextStyle.Strikethrough.RawValue;

        AssertTrue(bold != italic, "Bold != Italic");
        AssertTrue(bold != underline, "Bold != Underline");
        AssertTrue(bold != strikethrough, "Bold != Strikethrough");
        AssertTrue(italic != underline, "Italic != Underline");
        AssertTrue(italic != strikethrough, "Italic != Strikethrough");
        AssertTrue(underline != strikethrough, "Underline != Strikethrough");
        TestLogger.Info("TextStyle static instances all have distinct RawValues");
    }

    #endregion

    #region Tier 1 — ImageRequest.Options Blittable RawValue

    public void TestImageRequestOptionsDisableCacheRawValue()
    {
        var opt = ImageRequest.OptionsType.DisableCache;
        AssertTrue(opt.RawValue != 0, "DisableCache RawValue should be non-zero");
        TestLogger.Info($"ImageRequest.OptionsType.DisableCache.RawValue = {opt.RawValue}");
    }

    public void TestImageRequestOptionsReturnCachedRawValue()
    {
        var opt = ImageRequest.OptionsType.ReturnCached;
        AssertTrue(opt.RawValue != 0, "ReturnCached RawValue should be non-zero");
        TestLogger.Info($"ImageRequest.OptionsType.ReturnCached.RawValue = {opt.RawValue}");
    }

    public void TestImageRequestOptionsLowPriorityRawValue()
    {
        var opt = ImageRequest.OptionsType.LowPriority;
        AssertTrue(opt.RawValue != 0, "LowPriority RawValue should be non-zero");
        TestLogger.Info($"ImageRequest.OptionsType.LowPriority.RawValue = {opt.RawValue}");
    }

    public void TestImageRequestOptionsDistinct()
    {
        var disable = ImageRequest.OptionsType.DisableCache.RawValue;
        var cached = ImageRequest.OptionsType.ReturnCached.RawValue;
        var low = ImageRequest.OptionsType.LowPriority.RawValue;

        AssertTrue(disable != cached, "DisableCache != ReturnCached");
        AssertTrue(disable != low, "DisableCache != LowPriority");
        AssertTrue(cached != low, "ReturnCached != LowPriority");
        TestLogger.Info("ImageRequest.Options static instances all distinct");
    }

    #endregion

    #region Tier 2 — TextStyle Equality and Free Function

    public void TestTextStyleEqualitySame()
    {
        var a = TextStyle.Bold;
        var b = TextStyle.Bold;
        AssertTrue(a == b, "Bold == Bold");
        AssertFalse(a != b, "Bold != Bold should be false");
        TestLogger.Info("TextStyle equality (same) passed");
    }

    public void TestTextStyleInequalityDifferent()
    {
        var bold = TextStyle.Bold;
        var italic = TextStyle.Italic;
        AssertTrue(bold != italic, "Bold != Italic");
        AssertFalse(bold == italic, "Bold == Italic should be false");
        TestLogger.Info("TextStyle inequality (different) passed");
    }

    public void TestTextStyleCustomRawValue()
    {
        var style = new TextStyle(0);
        AssertEqual(0, style.RawValue, "Custom TextStyle with RawValue 0");
        TestLogger.Info("TextStyle custom construction with RawValue 0 passed");
    }

    public void TestDescribeTextStyleBold()
    {
        var desc = TestLibFunctions.DescribeTextStyle(TextStyle.Bold);
        AssertEqual("bold", desc, "DescribeTextStyle(Bold) is 'bold'");
        TestLogger.Info($"DescribeTextStyle(Bold) = \"{desc}\"");
    }

    public void TestDescribeTextStyleItalic()
    {
        var desc = TestLibFunctions.DescribeTextStyle(TextStyle.Italic);
        AssertEqual("italic", desc, "DescribeTextStyle(Italic) is 'italic'");
        TestLogger.Info($"DescribeTextStyle(Italic) = \"{desc}\"");
    }

    public void TestCombinedFlagRawValueRoundTrip()
    {
        // OR-combine Bold (1) + Italic (2) = 3
        var boldRaw = TextStyle.Bold.RawValue;
        var italicRaw = TextStyle.Italic.RawValue;
        var combined = new TextStyle(boldRaw | italicRaw);
        AssertEqual(boldRaw | italicRaw, combined.RawValue, "Combined raw value round-trips");
        TestLogger.Info($"Bold|Italic RawValue = {combined.RawValue}");
    }

    public void TestDescribeCombinedFlags()
    {
        // Bold (1) | Italic (2) = 3
        var combined = new TextStyle(TextStyle.Bold.RawValue | TextStyle.Italic.RawValue);
        var desc = TestLibFunctions.DescribeTextStyle(combined);
        AssertTrue(desc.Contains("bold"), "Combined description contains 'bold'");
        AssertTrue(desc.Contains("italic"), "Combined description contains 'italic'");
        TestLogger.Info($"DescribeTextStyle(Bold|Italic) = \"{desc}\"");
    }

    #endregion

    #region Tier 2 — ImageRequest Construction and Property

    public void TestImageRequestConstruction()
    {
        var options = ImageRequest.OptionsType.DisableCache;
        var request = new ImageRequest(options);
        AssertEqual(options.RawValue, request.Options.RawValue, "OptionsValue RawValue matches");
        TestLogger.Info($"ImageRequest construction: OptionsValue.RawValue = {request.Options.RawValue}");
    }

    public void TestImageRequestOptionsValueGetSet()
    {
        var request = new ImageRequest(ImageRequest.OptionsType.DisableCache);
        AssertEqual(ImageRequest.OptionsType.DisableCache.RawValue, request.Options.RawValue, "Initial options");

        request.Options = ImageRequest.OptionsType.LowPriority;
        AssertEqual(ImageRequest.OptionsType.LowPriority.RawValue, request.Options.RawValue, "Updated options");
        TestLogger.Info("ImageRequest.OptionsValue get/set passed");
    }

    public void TestImageRequestOptionsEquality()
    {
        var a = ImageRequest.OptionsType.DisableCache;
        var b = ImageRequest.OptionsType.DisableCache;
        AssertTrue(a == b, "DisableCache == DisableCache");

        var c = ImageRequest.OptionsType.LowPriority;
        AssertTrue(a != c, "DisableCache != LowPriority");
        TestLogger.Info("ImageRequest.Options equality passed");
    }

    #endregion
}
