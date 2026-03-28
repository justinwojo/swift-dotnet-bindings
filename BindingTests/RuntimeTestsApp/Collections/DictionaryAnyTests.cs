// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collections;

/// <summary>
/// Tests for [String: Any] dictionary projection via ExistentialContainer0.Box/Unbox.
/// Exercises the ConfigStore class and countAnyDictEntries free function.
/// </summary>
public class DictionaryAnyTests : TestBase
{
    public DictionaryAnyTests(TestResults results) : base(results) { }

    #region Tier 1 — Construction and Count

    public void TestConfigStoreConstruction()
    {
        var store = new ConfigStore();
        AssertNotNull(store, "ConfigStore created");
        AssertEqual(0, store.GetCount(), "Empty ConfigStore has count 0");
        TestLogger.Info("ConfigStore construction verified");
    }

    public void TestConfigStoreSetConfigCount()
    {
        var store = new ConfigStore();
        var config = new Dictionary<string, object>
        {
            { "name", "test" },
            { "count", 42L },
            { "enabled", true },
        };

        store.SetConfig(config);
        AssertEqual(3, store.GetCount(), "ConfigStore count is 3 after SetConfig");
        TestLogger.Info($"ConfigStore.GetCount() = {store.GetCount()}");
    }

    #endregion

    #region Tier 2 — Value Type Round-Trips

    public void TestConfigStoreStringRoundTrip()
    {
        var store = new ConfigStore();
        var config = new Dictionary<string, object>
        {
            { "name", "hello world" },
        };

        store.SetConfig(config);
        var result = store.GetString("name");
        AssertEqual("hello world", result, "String value survives round-trip");
        TestLogger.Info($"String round-trip: \"{result}\"");
    }

    public void TestConfigStoreIntRoundTrip()
    {
        var store = new ConfigStore();
        var config = new Dictionary<string, object>
        {
            { "count", 42L }, // Swift Int = 64-bit, use long
        };

        store.SetConfig(config);
        var result = store.GetInt("count");
        AssertEqual(42L, result, "Int value survives round-trip");
        TestLogger.Info($"Int round-trip: {result}");
    }

    public void TestConfigStoreDoubleRoundTrip()
    {
        var store = new ConfigStore();
        var config = new Dictionary<string, object>
        {
            { "pi", 3.14159 },
        };

        store.SetConfig(config);
        var result = store.GetDouble("pi");
        AssertTrue(Math.Abs(result - 3.14159) < 0.0001, "Double value survives round-trip");
        TestLogger.Info($"Double round-trip: {result}");
    }

    public void TestConfigStoreBoolRoundTrip()
    {
        var store = new ConfigStore();
        var config = new Dictionary<string, object>
        {
            { "enabled", true },
        };

        store.SetConfig(config);
        var result = store.GetBool("enabled");
        AssertTrue(result, "Bool value survives round-trip");
        TestLogger.Info($"Bool round-trip: {result}");
    }

    #endregion

    #region Tier 3 — Mixed Types and Free Function

    public void TestConfigStoreMixedTypes()
    {
        var store = new ConfigStore();
        var config = new Dictionary<string, object>
        {
            { "name", "test-app" },
            { "retries", 3L },
            { "timeout", 30.5 },
            { "debug", false },
        };

        store.SetConfig(config);
        AssertEqual(4, store.GetCount(), "Mixed config has 4 entries");
        AssertEqual("test-app", store.GetString("name"), "String entry correct");
        AssertEqual(3L, store.GetInt("retries"), "Int entry correct");
        AssertTrue(Math.Abs(store.GetDouble("timeout") - 30.5) < 0.0001, "Double entry correct");
        AssertFalse(store.GetBool("debug"), "Bool entry correct");
        TestLogger.Info("Mixed type round-trip passed");
    }

    public void TestCountAnyDictEntries()
    {
        var dict = new Dictionary<string, object>
        {
            { "a", "alpha" },
            { "b", 2L },
            { "c", true },
        };

        var count = TestLibFunctions.CountAnyDictEntries(dict);
        AssertEqual(3, count, "Free function counts 3 entries");
        TestLogger.Info($"countAnyDictEntries = {count}");
    }

    #endregion
}
