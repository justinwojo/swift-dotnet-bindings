// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Operators;

/// <summary>
/// Tests for struct equality operators: Tag struct with Key/Value string properties
/// and overloaded == / != operators.
/// </summary>
public class StructEqualityTests : TestBase
{
    public StructEqualityTests(TestResults results) : base(results) { }

    #region Tier 2 — Construction and Property Access

    [TestTier(TestTier.Tier2)]
    public void TestTagConstruction()
    {
        var tag = new Tag("env", "production");
        var key = tag.Key.ToString();
        var value = tag.Value.ToString();
        AssertEqual("env", key, "Tag.Key");
        AssertEqual("production", value, "Tag.Value");
        TestLogger.Info($"Tag: Key=\"{key}\", Value=\"{value}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestTagKeyProperty()
    {
        var tag = new Tag("version", "1.0");
        AssertEqual("version", tag.Key.ToString(), "Tag.Key getter");
        TestLogger.Info("Tag.Key property access passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestTagValueProperty()
    {
        var tag = new Tag("version", "1.0");
        AssertEqual("1.0", tag.Value.ToString(), "Tag.Value getter");
        TestLogger.Info("Tag.Value property access passed");
    }

    #endregion

    #region Tier 2 — Equality Operators

    [TestTier(TestTier.Tier2)]
    public void TestTagEqualitySameValues()
    {
        var a = new Tag("env", "prod");
        var b = new Tag("env", "prod");
        AssertTrue(a == b, "Tags with same key+value are equal");
        AssertFalse(a != b, "Tags with same key+value are not unequal");
        TestLogger.Info("Tag equality (same key+value) passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestTagInequalityDifferentKey()
    {
        var a = new Tag("env", "prod");
        var b = new Tag("stage", "prod");
        AssertTrue(a != b, "Tags with different keys are unequal");
        AssertFalse(a == b, "Tags with different keys are not equal");
        TestLogger.Info("Tag inequality (different key) passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestTagInequalityDifferentValue()
    {
        var a = new Tag("env", "prod");
        var b = new Tag("env", "dev");
        AssertTrue(a != b, "Tags with different values are unequal");
        AssertFalse(a == b, "Tags with different values are not equal");
        TestLogger.Info("Tag inequality (different value) passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestTagInequalityBothDifferent()
    {
        var a = new Tag("env", "prod");
        var b = new Tag("region", "us-east");
        AssertTrue(a != b, "Tags with both key+value different are unequal");
        AssertFalse(a == b, "Tags with both key+value different are not equal");
        TestLogger.Info("Tag inequality (both different) passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestTagEqualsMethod()
    {
        var a = new Tag("key", "value");
        var b = new Tag("key", "value");
        AssertTrue(a.Equals(b), "Tag.Equals with same key+value");
        TestLogger.Info("Tag.Equals method passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestTagEqualsMethodInequality()
    {
        var a = new Tag("key", "value1");
        var b = new Tag("key", "value2");
        AssertFalse(a.Equals(b), "Tag.Equals with different values");
        TestLogger.Info("Tag.Equals method inequality passed");
    }

    #endregion
}
