// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collections;

/// <summary>
/// Tests for HeaderMap — a Swift class initialized with an IDictionary&lt;string, string&gt;
/// constructor, exercising dictionary marshalling, count, get, and set operations.
/// </summary>
public class DictionaryConstructorTests : TestBase
{
    public DictionaryConstructorTests(TestResults results) : base(results) { }

    #region Tier 2 — Dictionary Constructor + Operations

    public void TestHeaderMapConstruction()
    {
        var headers = new Dictionary<string, string>
        {
            { "Content-Type", "application/json" },
            { "Authorization", "Bearer token123" },
        };

        var map = new HeaderMap(headers);
        AssertNotNull(map, "HeaderMap created from dictionary");
        TestLogger.Info("HeaderMap constructed from IDictionary<string, string>");
    }

    public void TestHeaderMapGetCount()
    {
        var headers = new Dictionary<string, string>
        {
            { "Accept", "text/html" },
            { "Host", "example.com" },
            { "X-Custom", "value" },
        };

        var map = new HeaderMap(headers);
        AssertEqual(3, map.GetCount(), "HeaderMap count is 3");
        TestLogger.Info($"HeaderMap.GetCount() = {map.GetCount()}");
    }

    public void TestHeaderMapGetExistingKey()
    {
        var headers = new Dictionary<string, string>
        {
            { "Content-Type", "application/json" },
        };

        var map = new HeaderMap(headers);
        var value = map.Get("Content-Type");
        AssertNotNull(value, "Get existing key returns non-null");
        AssertEqual("application/json", value!, "Get returns correct value");
        TestLogger.Info($"HeaderMap.Get(\"Content-Type\") = \"{value}\"");
    }

    public void TestHeaderMapGetMissingKey()
    {
        var headers = new Dictionary<string, string>
        {
            { "Content-Type", "application/json" },
        };

        var map = new HeaderMap(headers);
        var value = map.Get("X-Missing");
        AssertNull(value, "Get missing key returns null");
        TestLogger.Info("HeaderMap.Get(\"X-Missing\") = null");
    }

    public void TestHeaderMapSetAndGet()
    {
        var headers = new Dictionary<string, string>
        {
            { "Content-Type", "text/plain" },
        };

        var map = new HeaderMap(headers);
        AssertEqual(1, map.GetCount(), "Initial count is 1");

        map.Set("X-New-Header", "new-value");
        var value = map.Get("X-New-Header");
        AssertNotNull(value, "Set + Get round-trip returns non-null");
        AssertEqual("new-value", value!, "Set + Get round-trip value");
        TestLogger.Info($"HeaderMap.Set + Get round-trip: \"{value}\"");
    }

    public void TestHeaderMapEmptyDictionary()
    {
        var headers = new Dictionary<string, string>();
        var map = new HeaderMap(headers);
        AssertEqual(0, map.GetCount(), "Empty dictionary creates empty HeaderMap");
        TestLogger.Info("HeaderMap from empty dictionary: count = 0");
    }

    #endregion
}
