// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Properties;

/// <summary>
/// Tests for struct types with static singleton instances and instance methods.
/// Covers EncodingConfig: static Standard/Compact/Minimal, blittable MaxLength,
/// string FormatName, bool IsWithinLimit, and custom construction.
/// </summary>
public class StaticStructSingletonTests : TestBase
{
    public StaticStructSingletonTests(TestResults results) : base(results) { }

    #region Tier 1 — Blittable Property Access on Static Singletons

    [TestTier(TestTier.Tier1)]
    public void TestStandardMaxLength()
    {
        var config = EncodingConfig.Standard;
        AssertTrue(config.MaxLength > 0, "Standard.MaxLength should be positive");
        TestLogger.Info($"EncodingConfig.Standard.MaxLength = {config.MaxLength}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestCompactMaxLength()
    {
        var config = EncodingConfig.Compact;
        AssertTrue(config.MaxLength > 0, "Compact.MaxLength should be positive");
        TestLogger.Info($"EncodingConfig.Compact.MaxLength = {config.MaxLength}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestMinimalMaxLength()
    {
        var config = EncodingConfig.Minimal;
        AssertTrue(config.MaxLength > 0, "Minimal.MaxLength should be positive");
        TestLogger.Info($"EncodingConfig.Minimal.MaxLength = {config.MaxLength}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestStaticSingletonsHaveDistinctMaxLength()
    {
        var standard = EncodingConfig.Standard;
        var compact = EncodingConfig.Compact;
        var minimal = EncodingConfig.Minimal;

        // All three must have pairwise-distinct MaxLength values
        AssertTrue(standard.MaxLength != compact.MaxLength, "Standard != Compact");
        AssertTrue(standard.MaxLength != minimal.MaxLength, "Standard != Minimal");
        AssertTrue(compact.MaxLength != minimal.MaxLength, "Compact != Minimal");
        TestLogger.Info($"MaxLength — Standard: {standard.MaxLength}, Compact: {compact.MaxLength}, Minimal: {minimal.MaxLength}");
    }

    #endregion

    #region Tier 2 — String Property, Instance Method, Custom Construction

    [TestTier(TestTier.Tier2)]
    public void TestStandardFormatName()
    {
        var config = EncodingConfig.Standard;
        var name = config.FormatName.ToString();
        AssertTrue(name.Length > 0, "Standard.FormatName should not be empty");
        TestLogger.Info($"EncodingConfig.Standard.FormatName = \"{name}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestCompactFormatName()
    {
        var config = EncodingConfig.Compact;
        var name = config.FormatName.ToString();
        AssertTrue(name.Length > 0, "Compact.FormatName should not be empty");
        TestLogger.Info($"EncodingConfig.Compact.FormatName = \"{name}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestMinimalFormatName()
    {
        var config = EncodingConfig.Minimal;
        var name = config.FormatName.ToString();
        AssertTrue(name.Length > 0, "Minimal.FormatName should not be empty");
        TestLogger.Info($"EncodingConfig.Minimal.FormatName = \"{name}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestIsWithinLimitTrue()
    {
        var config = EncodingConfig.Standard;
        var maxLen = config.MaxLength;
        // A length less than MaxLength should be within limit
        AssertTrue(config.IsWithinLimit(maxLen - 1), $"Length {maxLen - 1} should be within limit {maxLen}");
        TestLogger.Info($"IsWithinLimit({maxLen - 1}) on Standard (max={maxLen}) = true");
    }

    [TestTier(TestTier.Tier2)]
    public void TestIsWithinLimitFalse()
    {
        var config = EncodingConfig.Minimal;
        var maxLen = config.MaxLength;
        // A length greater than MaxLength should not be within limit
        AssertFalse(config.IsWithinLimit(maxLen + 1), $"Length {maxLen + 1} should exceed limit {maxLen}");
        TestLogger.Info($"IsWithinLimit({maxLen + 1}) on Minimal (max={maxLen}) = false");
    }

    [TestTier(TestTier.Tier2)]
    public void TestIsWithinLimitBoundary()
    {
        var config = EncodingConfig.Compact;
        var maxLen = config.MaxLength;
        // Exact boundary: MaxLength itself should be within limit (<=)
        AssertTrue(config.IsWithinLimit(maxLen), $"Length {maxLen} should be at limit boundary");
        TestLogger.Info($"IsWithinLimit({maxLen}) on Compact (max={maxLen}) = true (boundary)");
    }

    [TestTier(TestTier.Tier2)]
    public void TestCustomConstruction()
    {
        var config = new EncodingConfig("Custom", 256);
        AssertEqual(256, config.MaxLength, "Custom MaxLength");
        var name = config.FormatName.ToString();
        AssertEqual("Custom", name, "Custom FormatName");
        TestLogger.Info($"Custom EncodingConfig: FormatName=\"{name}\", MaxLength={config.MaxLength}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestCustomConstructionIsWithinLimit()
    {
        var config = new EncodingConfig("Tiny", 10);
        AssertTrue(config.IsWithinLimit(5), "5 within limit of 10");
        AssertTrue(config.IsWithinLimit(10), "10 within limit of 10");
        AssertFalse(config.IsWithinLimit(11), "11 exceeds limit of 10");
        TestLogger.Info("Custom construction IsWithinLimit passed");
    }

    #endregion
}
