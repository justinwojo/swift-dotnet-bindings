// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for nested enum types: Codec.Format, Codec.Encoding, Codec.CompressionLevel,
/// SHA2Variant, HashAlgorithm, and associated free functions.
/// </summary>
public class NestedEnumTests : TestBase
{
    public NestedEnumTests(TestResults results) : base(results) { }

    #region Tier 1 — Blittable Enum Values

    public void TestCodecFormatValues()
    {
        AssertEqual(Codec.FormatType.Json, (Codec.FormatType)0, "Json is 0");
        AssertEqual(Codec.FormatType.Xml, (Codec.FormatType)1, "Xml is 1");
        AssertEqual(Codec.FormatType.Binary, (Codec.FormatType)2, "Binary is 2");
        TestLogger.Info("Codec.Format enum values passed");
    }

    public void TestCodecFormatDistinct()
    {
        AssertTrue(Codec.FormatType.Json != Codec.FormatType.Xml, "Json != Xml");
        AssertTrue(Codec.FormatType.Xml != Codec.FormatType.Binary, "Xml != Binary");
        AssertTrue(Codec.FormatType.Json != Codec.FormatType.Binary, "Json != Binary");
        TestLogger.Info("Codec.Format distinct values passed");
    }

    public void TestSHA2VariantValues()
    {
        AssertEqual(SHA2Variant.Sha224, (SHA2Variant)0, "Sha224 is 0");
        AssertEqual(SHA2Variant.Sha256, (SHA2Variant)1, "Sha256 is 1");
        AssertEqual(SHA2Variant.Sha384, (SHA2Variant)2, "Sha384 is 2");
        AssertEqual(SHA2Variant.Sha512, (SHA2Variant)3, "Sha512 is 3");
        TestLogger.Info("SHA2Variant enum values passed");
    }

    public void TestSHA2VariantRoundTrip()
    {
        AssertEqual(SHA2Variant.Sha256, (SHA2Variant)(int)SHA2Variant.Sha256, "Sha256 round-trip");
        AssertEqual(SHA2Variant.Sha512, (SHA2Variant)(int)SHA2Variant.Sha512, "Sha512 round-trip");
        TestLogger.Info("SHA2Variant round-trip passed");
    }

    #endregion

    #region Tier 2 — Codec.Encoding

    public void TestCodecEncodingFromRawValueUtf8()
    {
        var encoding = Codec.EncodingType.FromRawValue("utf-8");
        AssertNotNull(encoding, "Encoding utf-8 not null");
        AssertEqual(Codec.EncodingType.CaseTag.Utf8, encoding!.Tag, "utf-8 tag");
        TestLogger.Info("Codec.EncodingType.FromRawValue(utf-8) passed");
    }

    public void TestCodecEncodingFromRawValueAscii()
    {
        var encoding = Codec.EncodingType.FromRawValue("ascii");
        AssertNotNull(encoding, "Encoding ascii not null");
        AssertEqual(Codec.EncodingType.CaseTag.Ascii, encoding!.Tag, "ascii tag");
        TestLogger.Info("Codec.EncodingType.FromRawValue(ascii) passed");
    }

    public void TestCodecEncodingFromRawValueLatin1()
    {
        var encoding = Codec.EncodingType.FromRawValue("latin-1");
        AssertNotNull(encoding, "Encoding latin-1 not null");
        AssertEqual(Codec.EncodingType.CaseTag.Latin1, encoding!.Tag, "latin-1 tag");
        TestLogger.Info("Codec.EncodingType.FromRawValue(latin-1) passed");
    }

    public void TestCodecEncodingFromRawValueInvalid()
    {
        var encoding = Codec.EncodingType.FromRawValue("bogus");
        AssertNull(encoding, "Invalid encoding is null");
        TestLogger.Info("Codec.EncodingType.FromRawValue(bogus) = null");
    }

    public void TestCodecEncodingRawValueRoundTrip()
    {
        var encoding = Codec.EncodingType.FromRawValue("utf-8");
        AssertNotNull(encoding, "Encoding not null");
        var raw = encoding!.RawValue.ToString();
        AssertEqual("utf-8", raw, "utf-8 raw value round-trip");
        TestLogger.Info("Codec.Encoding raw value round-trip passed");
    }

    #endregion

    #region Tier 3 — Codec Construction and Properties

    public void TestCodecConstructionJson()
    {
        var encoding = Codec.EncodingType.FromRawValue("utf-8");
        AssertNotNull(encoding, "Encoding not null");
        var codec = new Codec(Codec.FormatType.Json, encoding!);
        AssertEqual(Codec.FormatType.Json, codec.Format, "Codec format is Json");
        TestLogger.Info("Codec construction with Json format passed");
    }

    public void TestCodecConstructionXml()
    {
        var encoding = Codec.EncodingType.FromRawValue("ascii");
        AssertNotNull(encoding, "Encoding not null");
        var codec = new Codec(Codec.FormatType.Xml, encoding!);
        AssertEqual(Codec.FormatType.Xml, codec.Format, "Codec format is Xml");
        TestLogger.Info("Codec construction with Xml format passed");
    }

    public void TestCodecEncodingValueProperty()
    {
        var encoding = Codec.EncodingType.FromRawValue("utf-8");
        AssertNotNull(encoding, "Encoding not null");
        var codec = new Codec(Codec.FormatType.Binary, encoding!);
        var encodingBack = codec.Encoding;
        AssertEqual(Codec.EncodingType.CaseTag.Utf8, encodingBack.Tag, "EncodingValue tag is Utf8");
        TestLogger.Info("Codec.EncodingValue property passed");
    }

    public void TestCodecGetDescribe()
    {
        var encoding = Codec.EncodingType.FromRawValue("utf-8");
        AssertNotNull(encoding, "Encoding not null");
        var codec = new Codec(Codec.FormatType.Json, encoding!);
        var desc = codec.GetDescribe();
        AssertTrue(desc.Contains("utf-8"), "Describe contains encoding raw value");
        TestLogger.Info($"Codec.GetDescribe() = \"{desc}\"");
    }

    #endregion

    #region Tier 2 — Codec.CompressionLevel

    public void TestCompressionLevelNone()
    {
        var level = Codec.CompressionLevel.None;
        AssertEqual(Codec.CompressionLevel.CaseTag.None, level.Tag, "None tag");
        TestLogger.Info("Codec.CompressionLevel.None passed");
    }

    public void TestCompressionLevelFast()
    {
        var level = Codec.CompressionLevel.Fast;
        AssertEqual(Codec.CompressionLevel.CaseTag.Fast, level.Tag, "Fast tag");
        TestLogger.Info("Codec.CompressionLevel.Fast passed");
    }

    public void TestCompressionLevelBest()
    {
        var level = Codec.CompressionLevel.Best;
        AssertEqual(Codec.CompressionLevel.CaseTag.Best, level.Tag, "Best tag");
        TestLogger.Info("Codec.CompressionLevel.Best passed");
    }

    public void TestCompressionLevelCustom()
    {
        var level = Codec.CompressionLevel.Custom(7);
        AssertEqual(Codec.CompressionLevel.CaseTag.Custom, level.Tag, "Custom tag");
        AssertTrue(level.TryGetCustom(out var value), "TryGetCustom succeeds");
        AssertEqual(7, value, "Custom value is 7");
        TestLogger.Info($"Codec.CompressionLevel.Custom(7) value = {value}");
    }

    public void TestCompressionLevelTryGetCustomOnNonCustom()
    {
        var level = Codec.CompressionLevel.None;
        AssertFalse(level.TryGetCustom(out _), "TryGetCustom on None returns false");
        TestLogger.Info("TryGetCustom on None correctly returns false");
    }

    public void TestCompressionLevelAllCasesDistinct()
    {
        var none = Codec.CompressionLevel.None;
        var fast = Codec.CompressionLevel.Fast;
        var best = Codec.CompressionLevel.Best;
        var custom = Codec.CompressionLevel.Custom(5);

        AssertTrue(none.Tag != fast.Tag, "None != Fast");
        AssertTrue(none.Tag != best.Tag, "None != Best");
        AssertTrue(none.Tag != custom.Tag, "None != Custom");
        AssertTrue(fast.Tag != best.Tag, "Fast != Best");
        AssertTrue(fast.Tag != custom.Tag, "Fast != Custom");
        AssertTrue(best.Tag != custom.Tag, "Best != Custom");
        TestLogger.Info("CompressionLevel all cases distinct");
    }

    #endregion

    #region Tier 2 — HashAlgorithm

    public void TestHashAlgorithmMd5()
    {
        var algo = HashAlgorithm.Md5;
        AssertEqual(HashAlgorithm.CaseTag.Md5, algo.Tag, "Md5 tag");
        TestLogger.Info("HashAlgorithm.Md5 passed");
    }

    public void TestHashAlgorithmSha1()
    {
        var algo = HashAlgorithm.Sha1;
        AssertEqual(HashAlgorithm.CaseTag.Sha1, algo.Tag, "Sha1 tag");
        TestLogger.Info("HashAlgorithm.Sha1 passed");
    }

    public void TestHashAlgorithmSha2()
    {
        var algo = HashAlgorithm.Sha2(SHA2Variant.Sha256);
        AssertEqual(HashAlgorithm.CaseTag.Sha2, algo.Tag, "Sha2 tag");
        AssertTrue(algo.TryGetSha2(out var variant), "TryGetSha2 succeeds");
        AssertEqual(SHA2Variant.Sha256, variant, "Sha2 variant is Sha256");
        TestLogger.Info("HashAlgorithm.Sha2(Sha256) passed");
    }

    public void TestHashAlgorithmSha2AllVariants()
    {
        var variants = new[] { SHA2Variant.Sha224, SHA2Variant.Sha256, SHA2Variant.Sha384, SHA2Variant.Sha512 };
        foreach (var v in variants)
        {
            var algo = HashAlgorithm.Sha2(v);
            AssertEqual(HashAlgorithm.CaseTag.Sha2, algo.Tag, $"Sha2({v}) tag");
            AssertTrue(algo.TryGetSha2(out var extracted), $"TryGetSha2({v}) succeeds");
            AssertEqual(v, extracted, $"Sha2 variant round-trip for {v}");
        }
        TestLogger.Info("HashAlgorithm.Sha2 all variants passed");
    }

    public void TestHashAlgorithmCustom()
    {
        var algo = HashAlgorithm.Custom(42);
        AssertEqual(HashAlgorithm.CaseTag.Custom, algo.Tag, "Custom tag");
        AssertTrue(algo.TryGetCustom(out var value), "TryGetCustom succeeds");
        AssertEqual(42, value, "Custom value is 42");
        TestLogger.Info($"HashAlgorithm.Custom(42) value = {value}");
    }

    public void TestHashAlgorithmTryGetOnWrongCase()
    {
        var md5 = HashAlgorithm.Md5;
        AssertFalse(md5.TryGetSha2(out _), "TryGetSha2 on Md5 returns false");
        AssertFalse(md5.TryGetCustom(out _), "TryGetCustom on Md5 returns false");
        TestLogger.Info("HashAlgorithm TryGet on wrong case correctly returns false");
    }

    #endregion

    #region Tier 2 — Free Functions

    public void TestCreateHashAlgorithm()
    {
        var algo = TestLibFunctions.CreateHashAlgorithm(SHA2Variant.Sha512);
        AssertEqual(HashAlgorithm.CaseTag.Sha2, algo.Tag, "CreateHashAlgorithm returns Sha2");
        AssertTrue(algo.TryGetSha2(out var variant), "TryGetSha2 succeeds");
        AssertEqual(SHA2Variant.Sha512, variant, "Created with Sha512 variant");
        TestLogger.Info("CreateHashAlgorithm(Sha512) passed");
    }

    public void TestDescribeAlgorithmMd5()
    {
        var algo = HashAlgorithm.Md5;
        var desc = TestLibFunctions.DescribeAlgorithm(algo);
        AssertTrue(desc.Length > 0, "DescribeAlgorithm should not be empty");
        TestLogger.Info($"DescribeAlgorithm(Md5) = \"{desc}\"");
    }

    public void TestDescribeAlgorithmSha2()
    {
        var algo = HashAlgorithm.Sha2(SHA2Variant.Sha256);
        var desc = TestLibFunctions.DescribeAlgorithm(algo);
        AssertTrue(desc.Length > 0, "DescribeAlgorithm should not be empty");
        TestLogger.Info($"DescribeAlgorithm(Sha2(Sha256)) = \"{desc}\"");
    }

    public void TestDescribeAlgorithmCustom()
    {
        var algo = HashAlgorithm.Custom(99);
        var desc = TestLibFunctions.DescribeAlgorithm(algo);
        AssertTrue(desc.Length > 0, "DescribeAlgorithm should not be empty");
        TestLogger.Info($"DescribeAlgorithm(Custom(99)) = \"{desc}\"");
    }

    #endregion

    #region Pass 2 — L2: Nested Enum-with-AVs (PlaybackMode)

    public void TestPlaybackModePlaying()
    {
        using var mode = PlaybackMode.Playing(2.0);
        AssertEqual(PlaybackMode.CaseTag.Playing, mode.Tag, "Playing tag");
        var desc = TestLibFunctions.DescribePlaybackMode(mode);
        AssertEqual("Playing at 2.0x", desc, "Describe playing");
        TestLogger.Info($"PlaybackMode.Playing: {desc}");
    }

    public void TestPlaybackModeStopped()
    {
        var mode = PlaybackMode.Stopped;
        AssertEqual(PlaybackMode.CaseTag.Stopped, mode.Tag, "Stopped tag");
        var desc = TestLibFunctions.DescribePlaybackMode(mode);
        AssertEqual("Stopped", desc, "Describe stopped");
        TestLogger.Info($"PlaybackMode.Stopped: {desc}");
    }

    #endregion

    #region Pass 2 — L6: Nested Enum with String RawValue + CaseIterable

    public void TestCodecAlignmentCases()
    {
        AssertEqual(Codec.Alignment.CaseTag.Left, Codec.Alignment.Left.Tag, "Left tag");
        AssertEqual(Codec.Alignment.CaseTag.Center, Codec.Alignment.Center.Tag, "Center tag");
        AssertEqual(Codec.Alignment.CaseTag.Right, Codec.Alignment.Right.Tag, "Right tag");
        TestLogger.Info("Codec.Alignment case tags passed");
    }

    public void TestCodecAlignmentRawValues()
    {
        AssertEqual("left", Codec.Alignment.Left.RawValue.ToString(), "Left raw value");
        AssertEqual("center", Codec.Alignment.Center.RawValue.ToString(), "Center raw value");
        AssertEqual("right", Codec.Alignment.Right.RawValue.ToString(), "Right raw value");
        TestLogger.Info("Codec.Alignment raw values passed");
    }

    #endregion

    #region Nested Class Rename — Generic Parameter (image-loader Animator pattern)

    public void TestNestedClassRename_TypeExists()
    {
        // The nested class Animator is renamed to AnimatorType to avoid collision
        // with the animator property (PascalCase → Animator).
        using var animator = new ImageTransitionTest.AnimatorType(true);
        AssertTrue(animator != null, "AnimatorType should be constructible");
        TestLogger.Info("Nested class rename: type exists as AnimatorType");
    }

    public void TestNestedClassRename_PropertyAccess()
    {
        // The property name stays as Animator (PascalCase of "animator"),
        // the TYPE is renamed to AnimatorType.
        using var animator = new ImageTransitionTest.AnimatorType(true);
        using var transition = new ImageTransitionTest(animator);
        var result = transition.Animator;
        AssertTrue(result != null, "Animator property should return non-null");
        TestLogger.Info("Nested class rename: property access works");
    }

    public void TestNestedClassRename_MethodOnRenamed()
    {
        // Verify the renamed nested class methods work at runtime —
        // this exercises SwiftClassHandle<AnimatorType> end-to-end.
        using var animator = new ImageTransitionTest.AnimatorType(true);
        var status = animator.GetStatus();
        AssertEqual("active", status, "Active animator status");
        TestLogger.Info("Nested class rename: method dispatch works");
    }

    public void TestNestedClassRename_FullRoundTrip()
    {
        // Full round-trip: create nested class, pass to parent, read back via property.
        using var animator = new ImageTransitionTest.AnimatorType(false);
        using var transition = new ImageTransitionTest(animator);
        var description = transition.GetDescribe();
        AssertEqual("Transition with inactive animator", description, "Full round-trip describe");
        TestLogger.Info("Nested class rename: full round-trip passed");
    }

    #endregion
}
