// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Patterns;

/// <summary>
/// Tests for ConfigLoader — a class with static factory methods (Create) that return
/// optional results, exercising nullable return marshalling and blittable/string property access.
/// </summary>
public class StaticFactoryTests : TestBase
{
    public StaticFactoryTests(TestResults results) : base(results) { }

    #region Tier 1 — Factory + Blittable Property

    [TestTier(TestTier.Tier1)]
    public void TestCreateWithValidNameVersion()
    {
        var loader = ConfigLoader.Create("AppConfig");
        AssertNotNull(loader, "Create with valid name returns non-null");
        var version = loader!.Version;
        AssertTrue(version >= 0, "Version is non-negative");
        TestLogger.Info($"ConfigLoader.Create(\"AppConfig\").Version = {version}");
    }

    #endregion

    #region Tier 2 — Factory Overloads + String Properties + Null Returns

    [TestTier(TestTier.Tier2)]
    public void TestCreateWithValidNameProperty()
    {
        var loader = ConfigLoader.Create("TestConfig");
        AssertNotNull(loader, "Create with valid name returns non-null");
        AssertEqual("TestConfig", loader!.Name, "Name property matches");
        TestLogger.Info($"ConfigLoader.Name = \"{loader.Name}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestCreateWithEmptyNameReturnsNull()
    {
        var loader = ConfigLoader.Create("");
        AssertNull(loader, "Create with empty name returns null");
        TestLogger.Info("ConfigLoader.Create(\"\") = null");
    }

    [TestTier(TestTier.Tier2)]
    public void TestCreateWithNameAndVersion()
    {
        var loader = ConfigLoader.Create("VersionedConfig", 2);
        AssertNotNull(loader, "Create with name + version returns non-null");
        AssertEqual("VersionedConfig", loader!.Name, "Name property matches");
        AssertEqual(2, loader.Version, "Version property matches");
        TestLogger.Info($"ConfigLoader.Create(\"VersionedConfig\", 2): Name={loader.Name}, Version={loader.Version}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestCreateWithNegativeVersionReturnsNull()
    {
        var loader = ConfigLoader.Create("Config", -1);
        AssertNull(loader, "Create with version -1 returns null");
        TestLogger.Info("ConfigLoader.Create(\"Config\", -1) = null");
    }

    [TestTier(TestTier.Tier2)]
    public void TestCreateWithZeroVersionReturnsNull()
    {
        // Swift guard is version > 0, so 0 is invalid
        var loader = ConfigLoader.Create("Config", 0);
        AssertNull(loader, "Create with version 0 returns null (guard: version > 0)");
        TestLogger.Info("ConfigLoader.Create(\"Config\", 0) = null");
    }

    [TestTier(TestTier.Tier2)]
    public void TestGetDescribe()
    {
        var loader = ConfigLoader.Create("DescribeTest");
        AssertNotNull(loader, "Create for describe test returns non-null");
        var desc = loader!.GetDescribe();
        AssertNotNull(desc, "GetDescribe returns non-null");
        AssertTrue(desc.Length > 0, "GetDescribe returns non-empty string");
        TestLogger.Info($"ConfigLoader.GetDescribe() = \"{desc}\"");
    }

    #endregion
}
