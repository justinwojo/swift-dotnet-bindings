// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests Foundation URLRequest runtime type marshalling.
/// Exercises HTTP header management (SetValue/AddValue/Value),
/// property access (HTTPMethod, TimeoutInterval), and construction paths.
/// </summary>
[Skip("URL/URLRequest P/Invokes use non-blittable types (SwiftString, SafeHandle) in CallConvSwift. Fix plan in src/docs/skip-reduction-plan.md § URL/URLRequest")]
public class URLRequestTests : TestBase
{
    public URLRequestTests(TestResults results) : base(results) { }

    #region Construction

    public void TestFromURL()
    {
        using var url = Swift.URL.FromString("https://example.com")!;
        using var request = Swift.URLRequest.FromURL(url);
        AssertNotNull(request, "URLRequest.FromURL returns non-null");
        TestLogger.Info("URLRequest.FromURL construction passed");
    }

    public void TestFromString()
    {
        using var request = Swift.URLRequest.FromString("https://example.com");
        AssertNotNull(request, "URLRequest.FromString returns non-null");
        TestLogger.Info("URLRequest.FromString construction passed");
    }

    public void TestFromStringInvalid()
    {
        var request = Swift.URLRequest.FromString("not a valid url %%%");
        AssertNull(request, "URLRequest.FromString returns null for invalid URL");
        TestLogger.Info("URLRequest.FromString invalid URL returns null");
    }

    public void TestURLProperty()
    {
        using var request = Swift.URLRequest.FromString("https://example.com/path")!;
        var url = request.URL;
        AssertNotNull(url, "URLRequest.URL is non-null");
        AssertEqual("https://example.com/path", url!.AbsoluteString, "URL matches construction URL");
        TestLogger.Info($"URLRequest.URL = {url.AbsoluteString}");
    }

    #endregion

    #region HTTPMethod Property

    public void TestDefaultHTTPMethod()
    {
        using var request = Swift.URLRequest.FromString("https://example.com")!;
        var method = request.HTTPMethod;
        AssertEqual("GET", method, "Default HTTP method is GET");
        TestLogger.Info($"Default HTTPMethod = {method}");
    }

    public void TestSetHTTPMethod()
    {
        using var request = Swift.URLRequest.FromString("https://example.com")!;
        request.HTTPMethod = "POST";
        AssertEqual("POST", request.HTTPMethod, "HTTPMethod set to POST");
        TestLogger.Info($"HTTPMethod after set = {request.HTTPMethod}");
    }

    #endregion

    #region TimeoutInterval Property

    public void TestDefaultTimeoutInterval()
    {
        using var request = Swift.URLRequest.FromString("https://example.com")!;
        var timeout = request.TimeoutInterval;
        AssertTrue(timeout > 0, "Default timeout is positive");
        TestLogger.Info($"Default TimeoutInterval = {timeout}");
    }

    public void TestSetTimeoutInterval()
    {
        using var request = Swift.URLRequest.FromString("https://example.com")!;
        request.TimeoutInterval = 30.0;
        AssertApproxEqual(30.0, request.TimeoutInterval, 0.001, "TimeoutInterval set to 30");
        TestLogger.Info($"TimeoutInterval after set = {request.TimeoutInterval}");
    }

    #endregion

    #region HTTP Header Management

    public void TestSetValueForHTTPHeaderField()
    {
        using var request = Swift.URLRequest.FromString("https://example.com")!;
        request.SetValue("Bearer token123", "Authorization");
        var value = request.Value("Authorization");
        AssertEqual("Bearer token123", value, "Authorization header set correctly");
        TestLogger.Info($"Authorization = {value}");
    }

    public void TestSetValueMultipleHeaders()
    {
        using var request = Swift.URLRequest.FromString("https://example.com")!;
        request.SetValue("application/json", "Content-Type");
        request.SetValue("Bearer abc", "Authorization");
        AssertEqual("application/json", request.Value("Content-Type"), "Content-Type header");
        AssertEqual("Bearer abc", request.Value("Authorization"), "Authorization header");
        TestLogger.Info("Multiple headers set correctly");
    }

    public void TestSetValueReplacesExisting()
    {
        using var request = Swift.URLRequest.FromString("https://example.com")!;
        request.SetValue("text/plain", "Content-Type");
        request.SetValue("application/json", "Content-Type");
        AssertEqual("application/json", request.Value("Content-Type"), "Content-Type replaced");
        TestLogger.Info("SetValue replaces existing header");
    }

    public void TestSetValueNullRemovesHeader()
    {
        using var request = Swift.URLRequest.FromString("https://example.com")!;
        request.SetValue("Bearer token", "Authorization");
        AssertNotNull(request.Value("Authorization"), "Header exists before removal");
        request.SetValue(null, "Authorization");
        AssertNull(request.Value("Authorization"), "Header removed after SetValue(null)");
        TestLogger.Info("SetValue(null) removes header");
    }

    public void TestValueForNonExistentHeader()
    {
        using var request = Swift.URLRequest.FromString("https://example.com")!;
        var value = request.Value("X-NonExistent");
        AssertNull(value, "Non-existent header returns null");
        TestLogger.Info("Non-existent header returns null");
    }

    public void TestAddValueAppendsToExisting()
    {
        using var request = Swift.URLRequest.FromString("https://example.com")!;
        request.SetValue("gzip", "Accept-Encoding");
        request.AddValue("deflate", "Accept-Encoding");
        var value = request.Value("Accept-Encoding");
        AssertNotNull(value, "Accept-Encoding has value after AddValue");
        // AddValue appends comma-separated
        AssertTrue(value!.Contains("gzip"), "Contains original value");
        AssertTrue(value!.Contains("deflate"), "Contains appended value");
        TestLogger.Info($"Accept-Encoding after AddValue = {value}");
    }

    public void TestAddValueNewHeader()
    {
        using var request = Swift.URLRequest.FromString("https://example.com")!;
        request.AddValue("custom-value", "X-Custom");
        AssertEqual("custom-value", request.Value("X-Custom"), "AddValue creates new header");
        TestLogger.Info("AddValue creates new header when none exists");
    }

    #endregion

    #region ToString

    public void TestToString()
    {
        using var request = Swift.URLRequest.FromString("https://example.com/test")!;
        var str = request.ToString();
        AssertEqual("https://example.com/test", str, "ToString returns URL string");
        TestLogger.Info($"URLRequest.ToString() = {str}");
    }

    #endregion
}
