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

    [TestTier(TestTier.Tier1)]
    public void TestTextStyleBoldRawValue()
    {
        var style = TextStyle.Bold;
        AssertTrue(style.RawValue != 0, "Bold RawValue should be non-zero");
        TestLogger.Info($"TextStyle.Bold.RawValue = {style.RawValue}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestTextStyleItalicRawValue()
    {
        var style = TextStyle.Italic;
        AssertTrue(style.RawValue != 0, "Italic RawValue should be non-zero");
        TestLogger.Info($"TextStyle.Italic.RawValue = {style.RawValue}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestTextStyleUnderlineRawValue()
    {
        var style = TextStyle.Underline;
        AssertTrue(style.RawValue != 0, "Underline RawValue should be non-zero");
        TestLogger.Info($"TextStyle.Underline.RawValue = {style.RawValue}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestTextStyleStrikethroughRawValue()
    {
        var style = TextStyle.Strikethrough;
        AssertTrue(style.RawValue != 0, "Strikethrough RawValue should be non-zero");
        TestLogger.Info($"TextStyle.Strikethrough.RawValue = {style.RawValue}");
    }

    [TestTier(TestTier.Tier1)]
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

    [TestTier(TestTier.Tier1)]
    public void TestImageRequestOptionsDisableCacheRawValue()
    {
        var opt = ImageRequest.Options.DisableCache;
        AssertTrue(opt.RawValue != 0, "DisableCache RawValue should be non-zero");
        TestLogger.Info($"ImageRequest.Options.DisableCache.RawValue = {opt.RawValue}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestImageRequestOptionsReturnCachedRawValue()
    {
        var opt = ImageRequest.Options.ReturnCached;
        AssertTrue(opt.RawValue != 0, "ReturnCached RawValue should be non-zero");
        TestLogger.Info($"ImageRequest.Options.ReturnCached.RawValue = {opt.RawValue}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestImageRequestOptionsLowPriorityRawValue()
    {
        var opt = ImageRequest.Options.LowPriority;
        AssertTrue(opt.RawValue != 0, "LowPriority RawValue should be non-zero");
        TestLogger.Info($"ImageRequest.Options.LowPriority.RawValue = {opt.RawValue}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestImageRequestOptionsDistinct()
    {
        var disable = ImageRequest.Options.DisableCache.RawValue;
        var cached = ImageRequest.Options.ReturnCached.RawValue;
        var low = ImageRequest.Options.LowPriority.RawValue;

        AssertTrue(disable != cached, "DisableCache != ReturnCached");
        AssertTrue(disable != low, "DisableCache != LowPriority");
        AssertTrue(cached != low, "ReturnCached != LowPriority");
        TestLogger.Info("ImageRequest.Options static instances all distinct");
    }

    #endregion

    #region Tier 2 — TextStyle Equality and Free Function

    [TestTier(TestTier.Tier2)]
    public void TestTextStyleEqualitySame()
    {
        var a = TextStyle.Bold;
        var b = TextStyle.Bold;
        AssertTrue(a == b, "Bold == Bold");
        AssertFalse(a != b, "Bold != Bold should be false");
        TestLogger.Info("TextStyle equality (same) passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestTextStyleInequalityDifferent()
    {
        var bold = TextStyle.Bold;
        var italic = TextStyle.Italic;
        AssertTrue(bold != italic, "Bold != Italic");
        AssertFalse(bold == italic, "Bold == Italic should be false");
        TestLogger.Info("TextStyle inequality (different) passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestTextStyleCustomRawValue()
    {
        var style = new TextStyle(0);
        AssertEqual(0, style.RawValue, "Custom TextStyle with RawValue 0");
        TestLogger.Info("TextStyle custom construction with RawValue 0 passed");
    }

    [TestTier(TestTier.Tier3)] // SBW_Free_ entry point not found — string-returning free function
    public void TestDescribeTextStyleBold()
    {
        var desc = TestLibFunctions.DescribeTextStyle(TextStyle.Bold);
        AssertEqual("bold", desc, "DescribeTextStyle(Bold) is 'bold'");
        TestLogger.Info($"DescribeTextStyle(Bold) = \"{desc}\"");
    }

    [TestTier(TestTier.Tier3)]
    public void TestDescribeTextStyleItalic()
    {
        var desc = TestLibFunctions.DescribeTextStyle(TextStyle.Italic);
        AssertEqual("italic", desc, "DescribeTextStyle(Italic) is 'italic'");
        TestLogger.Info($"DescribeTextStyle(Italic) = \"{desc}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestCombinedFlagRawValueRoundTrip()
    {
        // OR-combine Bold (1) + Italic (2) = 3
        var boldRaw = TextStyle.Bold.RawValue;
        var italicRaw = TextStyle.Italic.RawValue;
        var combined = new TextStyle(boldRaw | italicRaw);
        AssertEqual(boldRaw | italicRaw, combined.RawValue, "Combined raw value round-trips");
        TestLogger.Info($"Bold|Italic RawValue = {combined.RawValue}");
    }

    [TestTier(TestTier.Tier3)]
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

    [TestTier(TestTier.Tier2)]
    public void TestImageRequestConstruction()
    {
        var options = ImageRequest.Options.DisableCache;
        var request = new ImageRequest(options);
        AssertEqual(options.RawValue, request.OptionsValue.RawValue, "OptionsValue RawValue matches");
        TestLogger.Info($"ImageRequest construction: OptionsValue.RawValue = {request.OptionsValue.RawValue}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestImageRequestOptionsValueGetSet()
    {
        var request = new ImageRequest(ImageRequest.Options.DisableCache);
        AssertEqual(ImageRequest.Options.DisableCache.RawValue, request.OptionsValue.RawValue, "Initial options");

        request.OptionsValue = ImageRequest.Options.LowPriority;
        AssertEqual(ImageRequest.Options.LowPriority.RawValue, request.OptionsValue.RawValue, "Updated options");
        TestLogger.Info("ImageRequest.OptionsValue get/set passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestImageRequestOptionsEquality()
    {
        var a = ImageRequest.Options.DisableCache;
        var b = ImageRequest.Options.DisableCache;
        AssertTrue(a == b, "DisableCache == DisableCache");

        var c = ImageRequest.Options.LowPriority;
        AssertTrue(a != c, "DisableCache != LowPriority");
        TestLogger.Info("ImageRequest.Options equality passed");
    }

    #endregion
}
