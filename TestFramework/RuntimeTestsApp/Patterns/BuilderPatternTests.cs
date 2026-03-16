// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Patterns;

/// <summary>
/// Tests for RequestBuilder — a fluent builder pattern with string/int properties
/// and chaining methods (WithMethod, WithTimeout, WithRetryCount).
/// </summary>
public class BuilderPatternTests : TestBase
{
    public BuilderPatternTests(TestResults results) : base(results) { }

    #region Tier 1 — Construction + Default Blittable Properties

    [TestTier(TestTier.Tier1)]
    public void TestRequestBuilderDefaultTimeout()
    {
        var builder = new RequestBuilder("https://example.com");
        AssertEqual(30, builder.Timeout, "Default Timeout is 30");
        TestLogger.Info($"RequestBuilder default Timeout = {builder.Timeout}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestRequestBuilderDefaultRetryCount()
    {
        var builder = new RequestBuilder("https://example.com");
        AssertEqual(0, builder.RetryCount, "Default RetryCount is 0");
        TestLogger.Info($"RequestBuilder default RetryCount = {builder.RetryCount}");
    }

    #endregion

    #region Tier 2 — Chaining + String Properties

    [TestTier(TestTier.Tier2)]
    public void TestRequestBuilderUrlProperty()
    {
        var builder = new RequestBuilder("https://example.com/api");
        AssertEqual("https://example.com/api", builder.Url, "Url getter returns constructor value");
        TestLogger.Info($"RequestBuilder.Url = \"{builder.Url}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestRequestBuilderUrlSetter()
    {
        var builder = new RequestBuilder("https://example.com");
        builder.Url = "https://updated.com";
        AssertEqual("https://updated.com", builder.Url, "Url setter updates value");
        TestLogger.Info($"RequestBuilder.Url after set = \"{builder.Url}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestRequestBuilderMethodProperty()
    {
        var builder = new RequestBuilder("https://example.com");
        // Default method — read the initial value
        var defaultMethod = builder.Method;
        AssertNotNull(defaultMethod, "Default Method is not null");
        TestLogger.Info($"RequestBuilder default Method = \"{defaultMethod}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestRequestBuilderMethodSetter()
    {
        var builder = new RequestBuilder("https://example.com");
        builder.Method = "POST";
        AssertEqual("POST", builder.Method, "Method setter updates value");
        TestLogger.Info($"RequestBuilder.Method after set = \"{builder.Method}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestWithMethodChaining()
    {
        var builder = new RequestBuilder("https://example.com");
        var result = builder.WithMethod("PUT");
        AssertNotNull(result, "WithMethod returns non-null builder");
        AssertEqual("PUT", result.Method, "WithMethod sets Method to PUT");
        TestLogger.Info($"WithMethod(\"PUT\").Method = \"{result.Method}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestWithTimeoutChaining()
    {
        var builder = new RequestBuilder("https://example.com");
        var result = builder.WithTimeout(60);
        AssertNotNull(result, "WithTimeout returns non-null builder");
        AssertEqual(60, result.Timeout, "WithTimeout sets Timeout to 60");
        TestLogger.Info($"WithTimeout(60).Timeout = {result.Timeout}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestWithRetryCountChaining()
    {
        var builder = new RequestBuilder("https://example.com");
        var result = builder.WithRetryCount(3);
        AssertNotNull(result, "WithRetryCount returns non-null builder");
        AssertEqual(3, result.RetryCount, "WithRetryCount sets RetryCount to 3");
        TestLogger.Info($"WithRetryCount(3).RetryCount = {result.RetryCount}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestFluentChaining()
    {
        var builder = new RequestBuilder("https://api.example.com")
            .WithMethod("DELETE")
            .WithTimeout(120)
            .WithRetryCount(5);

        AssertEqual("DELETE", builder.Method, "Chained Method is DELETE");
        AssertEqual(120, builder.Timeout, "Chained Timeout is 120");
        AssertEqual(5, builder.RetryCount, "Chained RetryCount is 5");
        TestLogger.Info("Fluent chaining: Method/Timeout/RetryCount all set correctly");
    }

    [TestTier(TestTier.Tier2)]
    public void TestWithMethodReturnsSameInstance()
    {
        var builder = new RequestBuilder("https://example.com");
        var returned = builder.WithMethod("PATCH");
        // Builder pattern returns Self — the returned handle should be the same Swift object
        AssertEqual(builder.Timeout, returned.Timeout, "Same instance: Timeout matches");
        AssertEqual("PATCH", builder.Method, "Original builder also sees the mutation");
        TestLogger.Info("WithMethod returns same builder instance");
    }

    [TestTier(TestTier.Tier2)]
    public void TestGetDescribe()
    {
        var builder = new RequestBuilder("https://example.com")
            .WithMethod("POST")
            .WithTimeout(45);

        var desc = builder.GetDescribe();
        AssertTrue(desc.Contains("POST"), "GetDescribe contains method");
        AssertTrue(desc.Contains("45"), "GetDescribe contains timeout");
        TestLogger.Info($"RequestBuilder.GetDescribe() = \"{desc}\"");
    }

    #endregion
}
