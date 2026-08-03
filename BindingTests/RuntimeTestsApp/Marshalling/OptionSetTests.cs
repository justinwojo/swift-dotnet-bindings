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
        var opt = ImageRequest.OptionsInfo.DisableCache;
        AssertTrue(opt.RawValue != 0, "DisableCache RawValue should be non-zero");
        TestLogger.Info($"ImageRequest.OptionsInfo.DisableCache.RawValue = {opt.RawValue}");
    }

    public void TestImageRequestOptionsReturnCachedRawValue()
    {
        var opt = ImageRequest.OptionsInfo.ReturnCached;
        AssertTrue(opt.RawValue != 0, "ReturnCached RawValue should be non-zero");
        TestLogger.Info($"ImageRequest.OptionsInfo.ReturnCached.RawValue = {opt.RawValue}");
    }

    public void TestImageRequestOptionsLowPriorityRawValue()
    {
        var opt = ImageRequest.OptionsInfo.LowPriority;
        AssertTrue(opt.RawValue != 0, "LowPriority RawValue should be non-zero");
        TestLogger.Info($"ImageRequest.OptionsInfo.LowPriority.RawValue = {opt.RawValue}");
    }

    public void TestImageRequestOptionsDistinct()
    {
        var disable = ImageRequest.OptionsInfo.DisableCache.RawValue;
        var cached = ImageRequest.OptionsInfo.ReturnCached.RawValue;
        var low = ImageRequest.OptionsInfo.LowPriority.RawValue;

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
        var options = ImageRequest.OptionsInfo.DisableCache;
        var request = new ImageRequest(options);
        AssertEqual(options.RawValue, request.Options.RawValue, "OptionsValue RawValue matches");
        TestLogger.Info($"ImageRequest construction: OptionsValue.RawValue = {request.Options.RawValue}");
    }

    public void TestImageRequestOptionsValueGetSet()
    {
        var request = new ImageRequest(ImageRequest.OptionsInfo.DisableCache);
        AssertEqual(ImageRequest.OptionsInfo.DisableCache.RawValue, request.Options.RawValue, "Initial options");

        request.Options = ImageRequest.OptionsInfo.LowPriority;
        AssertEqual(ImageRequest.OptionsInfo.LowPriority.RawValue, request.Options.RawValue, "Updated options");
        TestLogger.Info("ImageRequest.OptionsValue get/set passed");
    }

    public void TestImageRequestOptionsEquality()
    {
        var a = ImageRequest.OptionsInfo.DisableCache;
        var b = ImageRequest.OptionsInfo.DisableCache;
        AssertTrue(a == b, "DisableCache == DisableCache");

        var c = ImageRequest.OptionsInfo.LowPriority;
        AssertTrue(a != c, "DisableCache != LowPriority");
        TestLogger.Info("ImageRequest.Options equality passed");
    }

    #endregion

    #region Tier 3 — Synthesized OptionSet bitwise surface

    // Swift's OptionSet operators come from stdlib protocol extensions and carry no ABI symbols,
    // so they are synthesized in C# over RawValue/init(rawValue:). These tests prove the results
    // are option sets Swift itself agrees with — each combination is handed back to a Swift
    // function that reads it through `contains`, not merely compared bit-for-bit in C#.

    public void TestTextStyleUnionOperatorRoundTripsThroughSwift()
    {
        var combined = TextStyle.Bold | TextStyle.Italic;
        var desc = TestLibFunctions.DescribeTextStyle(combined);
        AssertTrue(desc.Contains("bold"), "Union description contains 'bold'");
        AssertTrue(desc.Contains("italic"), "Union description contains 'italic'");
        AssertTrue(!desc.Contains("underline"), "Union description omits 'underline'");
        TestLogger.Info($"DescribeTextStyle(Bold | Italic) = \"{desc}\"");
    }

    public void TestTextStyleIntersectionOperatorRoundTripsThroughSwift()
    {
        var left = TextStyle.Bold | TextStyle.Italic;
        var right = TextStyle.Italic | TextStyle.Underline;
        var desc = TestLibFunctions.DescribeTextStyle(left & right);
        AssertEqual("italic", desc, "Intersection of {bold,italic} and {italic,underline} is {italic}");
        TestLogger.Info($"DescribeTextStyle((Bold|Italic) & (Italic|Underline)) = \"{desc}\"");
    }

    public void TestTextStyleSymmetricDifferenceOperatorRoundTripsThroughSwift()
    {
        var left = TextStyle.Bold | TextStyle.Italic;
        var right = TextStyle.Italic | TextStyle.Underline;
        var desc = TestLibFunctions.DescribeTextStyle(left ^ right);
        AssertTrue(desc.Contains("bold"), "Symmetric difference keeps 'bold'");
        AssertTrue(desc.Contains("underline"), "Symmetric difference keeps 'underline'");
        AssertTrue(!desc.Contains("italic"), "Symmetric difference drops the shared 'italic'");
        TestLogger.Info($"DescribeTextStyle((Bold|Italic) ^ (Italic|Underline)) = \"{desc}\"");
    }

    public void TestTextStyleComplementOperatorRoundTripsThroughSwift()
    {
        // ~Bold clears bold and leaves every other declared option set.
        var desc = TestLibFunctions.DescribeTextStyle(~TextStyle.Bold);
        AssertTrue(!desc.Contains("bold"), "Complement of Bold drops 'bold'");
        AssertTrue(desc.Contains("italic"), "Complement of Bold keeps 'italic'");
        AssertTrue(desc.Contains("underline"), "Complement of Bold keeps 'underline'");
        AssertTrue(desc.Contains("strikethrough"), "Complement of Bold keeps 'strikethrough'");
        TestLogger.Info($"DescribeTextStyle(~Bold) = \"{desc}\"");
    }

    public void TestTextStyleContainsMembership()
    {
        var combined = TextStyle.Bold | TextStyle.Italic;
        AssertTrue(combined.Contains(TextStyle.Bold), "Bold|Italic contains Bold");
        AssertTrue(combined.Contains(TextStyle.Italic), "Bold|Italic contains Italic");
        AssertTrue(!combined.Contains(TextStyle.Underline), "Bold|Italic does not contain Underline");
        AssertTrue(combined.Contains(combined), "A set contains itself");
        TestLogger.Info("TextStyle.Contains membership verified");
    }

    public void TestAccessFlagsUnionRoundTripsThroughSwift()
    {
        // AccessFlags is @frozen with a UInt8 raw value, so it projects as a C# value type —
        // the other arm of the synthesis (no null guards, narrow raw type).
        var desc = TestLibFunctions.DescribeAccessFlags(AccessFlags.Read | AccessFlags.Write);
        AssertTrue(desc.Contains("read"), "Union description contains 'read'");
        AssertTrue(desc.Contains("write"), "Union description contains 'write'");
        AssertTrue(!desc.Contains("execute"), "Union description omits 'execute'");
        TestLogger.Info($"DescribeAccessFlags(Read | Write) = \"{desc}\"");
    }

    public void TestAccessFlagsComplementWrapsWithinNarrowRawType()
    {
        // ~Read on a UInt8 raw value overflows int arithmetic back into a byte; the synthesized
        // cast is unchecked, so this must produce a value rather than throw.
        var complement = ~AccessFlags.Read;
        AssertTrue(!complement.Contains(AccessFlags.Read), "Complement of Read drops Read");
        AssertTrue(complement.Contains(AccessFlags.Write), "Complement of Read keeps Write");
        var desc = TestLibFunctions.DescribeAccessFlags(complement);
        AssertTrue(!desc.Contains("read"), "Swift agrees the complement excludes 'read'");
        AssertTrue(desc.Contains("write"), "Swift agrees the complement includes 'write'");
        TestLogger.Info($"DescribeAccessFlags(~Read) = \"{desc}\" (raw {complement.RawValue})");
    }

    public void TestAccessFlagsIntersectionAndContains()
    {
        var all = AccessFlags.Read | AccessFlags.Write | AccessFlags.Execute;
        var readWrite = all & ~AccessFlags.Execute;
        AssertTrue(readWrite.Contains(AccessFlags.Read), "Read remains after clearing Execute");
        AssertTrue(readWrite.Contains(AccessFlags.Write), "Write remains after clearing Execute");
        AssertTrue(!readWrite.Contains(AccessFlags.Execute), "Execute cleared");
        AssertEqual("read, write", TestLibFunctions.DescribeAccessFlags(readWrite), "Swift reads back {read, write}");
        TestLogger.Info("AccessFlags intersection + Contains verified");
    }

    public void TestPermissionMaskUnionOverPlatformWidthRawValue()
    {
        // PermissionMask's raw value is Swift `Int`, so the emitted property and the initializer
        // parameter have different C# types. The combined mask has to survive the round trip into
        // Swift as an option set, not merely as the right bits.
        var mask = PermissionMask.ReadData | PermissionMask.Share;
        AssertTrue(mask.Contains(PermissionMask.ReadData), "Union keeps ReadData");
        AssertTrue(mask.Contains(PermissionMask.Share), "Union keeps Share");
        AssertTrue(!mask.Contains(PermissionMask.WriteData), "Union omits WriteData");
        var desc = TestLibFunctions.DescribePermissionMask(mask);
        AssertEqual("readData, share", desc, "Swift reads back {readData, share}");
        TestLogger.Info($"DescribePermissionMask(ReadData | Share) = \"{desc}\"");
    }

    public void TestPermissionMaskComplementAndIntersection()
    {
        var all = PermissionMask.ReadData | PermissionMask.WriteData | PermissionMask.Share;
        var withoutShare = all & ~PermissionMask.Share;
        AssertTrue(withoutShare.Contains(PermissionMask.ReadData), "ReadData remains after clearing Share");
        AssertTrue(!withoutShare.Contains(PermissionMask.Share), "Share cleared");
        AssertEqual("readData, writeData", TestLibFunctions.DescribePermissionMask(withoutShare),
            "Swift reads back {readData, writeData}");
        TestLogger.Info("PermissionMask complement + intersection verified");
    }

    #endregion
}
