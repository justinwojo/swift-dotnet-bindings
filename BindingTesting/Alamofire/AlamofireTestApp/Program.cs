// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Foundation;
using Swift;
using Swift.Alamofire;
using Swift.Runtime;
using UIKit;

namespace AlamofireTestApp;

public static class TestLogger
{
    private static readonly object _lock = new();
    private static readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private static readonly StringBuilder _fullLog = new();

    public static void Info(string message) => Log("INFO", message);
    public static void Pass(string message) => Log("PASS", message);
    public static void Fail(string message) => Log("FAIL", message);

    public static void Log(string prefix, string message)
    {
        var timestamp = _stopwatch.Elapsed.TotalSeconds;
        var line = $"[{timestamp:F3}s] [{prefix}] {message}";
        lock (_lock)
        {
            Console.WriteLine(line);
            _fullLog.AppendLine(line);
        }
    }

    public static string GetFullLog()
    {
        lock (_lock) { return _fullLog.ToString(); }
    }

    public static void Clear()
    {
        lock (_lock) { _fullLog.Clear(); }
    }
}

public class TestResults
{
    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public int Skipped { get; private set; }
    public List<string> FailedTests { get; } = new();

    public void Pass(string testName)
    {
        Passed++;
        TestLogger.Pass(testName);
    }

    public void Fail(string testName, string reason)
    {
        Failed++;
        FailedTests.Add($"{testName}: {reason}");
        TestLogger.Fail($"{testName}: {reason}");
    }

    public void Skip(string testName, string reason)
    {
        Skipped++;
        TestLogger.Log("SKIP", $"{testName}: {reason}");
    }

    public bool AllPassed => Failed == 0;
}

public class Application
{
    static void Main(string[] args)
    {
        try
        {
            NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), ResolveBundledFramework);
        }
        catch (InvalidOperationException)
        {
            // DllImportResolver already set (e.g., by generated ModuleInitializer)
        }
        UIApplication.Main(args, null, typeof(AppDelegate));
    }

    static IntPtr ResolveBundledFramework(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName is "Alamofire" or "AlamofireSwiftBindings")
        {
            var frameworkPath = $"@rpath/{libraryName}.framework/{libraryName}";
            if (NativeLibrary.TryLoad(frameworkPath, out var handle))
            {
                TestLogger.Info($"Resolved {libraryName} -> {frameworkPath}");
                return handle;
            }
            TestLogger.Fail($"Failed to resolve {libraryName} at {frameworkPath}");
        }
        return IntPtr.Zero;
    }
}

[Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
    {
        Window = new UIWindow(UIScreen.MainScreen.Bounds);
        Window.BackgroundColor = UIColor.White;
        var vc = new MainViewController();
        vc.ModalPresentationStyle = UIModalPresentationStyle.FullScreen;
        Window.RootViewController = vc;
        Window.MakeKeyAndVisible();
        return true;
    }
}

public class MainViewController : UIViewController
{
    private readonly TestResults _results = new();

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        RunAllTests();
    }

    private void RunAllTests()
    {
        TestLogger.Clear();
        TestLogger.Info("=== ALAMOFIRE BINDING VALIDATION SUITE ===");

        // Phase 1: Safe tests (constructors, static properties, enums)
        // These use static dispatch — no CallConvSwift JIT crash risk.
        TestURLEncodingConstruction();
        TestJSONEncodingConstruction();
        TestHTTPMethodStaticProperties();
        TestHTTPMethodCustomConstruction();
        TestHTTPHeaderConstruction();
        TestHTTPHeadersConstruction();
        TestSerializerConstruction();
        TestConnectionLostRetryPolicy();
        TestEmptyType();

        // Phase 2: Moderate risk (property access on instances — CallConvSwift)
        TestLogger.Info("");
        TestLogger.Info("=== INSTANCE TESTS (may crash — Mono JIT bug) ===");
        TestSessionDefault();

        // Final summary — TEST SUCCESS only after ALL phases complete
        TestLogger.Info("");
        TestLogger.Info("=== FINAL SUMMARY ===");
        var skippedMsg = _results.Skipped > 0 ? $", Skipped: {_results.Skipped}" : "";
        TestLogger.Info($"Passed: {_results.Passed}, Failed: {_results.Failed}{skippedMsg}");

        if (_results.AllPassed)
        {
            Console.WriteLine("TEST SUCCESS");
            TestLogger.Info("=== VALIDATION PASSED ===");
        }
        else
        {
            Console.WriteLine($"TEST FAILURE: {_results.Failed} tests failed");
            TestLogger.Info("=== VALIDATION FAILED ===");
            foreach (var f in _results.FailedTests)
                TestLogger.Info($"  - {f}");
        }
    }

    // --- Test 1: URLEncoding construction ---
    void TestURLEncodingConstruction()
    {
        TestLogger.Info("Test: URLEncoding construction...");
        try
        {
            using var encoding = new URLEncoding();
            TestLogger.Info($"  URLEncoding created: {encoding != null}");

            using var defaultEncoding = URLEncoding.Default;
            TestLogger.Info($"  URLEncoding.Default: {defaultEncoding != null}");

            if (encoding != null && defaultEncoding != null)
                _results.Pass("URLEncoding construction");
            else
                _results.Fail("URLEncoding construction", "Instance was null");
        }
        catch (Exception ex)
        {
            _results.Fail("URLEncoding construction", ex.Message);
        }
    }

    // --- Test 2: JSONEncoding construction ---
    void TestJSONEncodingConstruction()
    {
        TestLogger.Info("Test: JSONEncoding construction...");
        try
        {
            using var defaultEncoding = JSONEncoding.Default;
            TestLogger.Info($"  JSONEncoding.Default: {defaultEncoding != null}");

            if (defaultEncoding != null)
                _results.Pass("JSONEncoding construction");
            else
                _results.Fail("JSONEncoding construction", "Instance was null");
        }
        catch (Exception ex)
        {
            _results.Fail("JSONEncoding construction", ex.Message);
        }
    }

    // --- Test 3: HTTPMethod static properties ---
    void TestHTTPMethodStaticProperties()
    {
        TestLogger.Info("Test: HTTPMethod static properties...");
        int successCount = 0;
        // Each property access is wrapped individually — operator== on HTTPMethod
        // can throw if rhs is null, so avoid != null checks on IEquatable types.
        string[] names = { "Get", "Post", "Put", "Delete", "Patch" };
        Func<HTTPMethod>[] getters = {
            () => HTTPMethod.Get, () => HTTPMethod.Post, () => HTTPMethod.Put,
            () => HTTPMethod.Delete, () => HTTPMethod.Patch
        };
        for (int i = 0; i < names.Length; i++)
        {
            try
            {
                using var method = getters[i]();
                var rawValue = method.RawValue;
                TestLogger.Info($"  HTTPMethod.{names[i]}.RawValue = '{rawValue}'");
                successCount++;
            }
            catch (Exception ex)
            {
                TestLogger.Info($"  HTTPMethod.{names[i]}: FAILED ({ex.Message})");
            }
        }
        if (successCount == names.Length)
            _results.Pass("HTTPMethod static properties");
        else
            _results.Fail("HTTPMethod static properties", $"{names.Length - successCount}/{names.Length} failed");
    }

    // --- Test 4: HTTPMethod custom construction + RawValue ---
    void TestHTTPMethodCustomConstruction()
    {
        TestLogger.Info("Test: HTTPMethod custom construction...");
        try
        {
            using var custom = new HTTPMethod("CUSTOM");
            var rawValue = custom.RawValue;
            TestLogger.Info($"  HTTPMethod('CUSTOM').RawValue = '{rawValue}'");

            if (rawValue == "CUSTOM")
                _results.Pass("HTTPMethod custom construction");
            else
                _results.Fail("HTTPMethod custom construction", $"RawValue='{rawValue}', expected 'CUSTOM'");
        }
        catch (Exception ex)
        {
            _results.Fail("HTTPMethod custom construction", ex.Message);
        }
    }

    // --- Test 5: HTTPHeader construction ---
    void TestHTTPHeaderConstruction()
    {
        TestLogger.Info("Test: HTTPHeader construction...");
        try
        {
            using var header = new HTTPHeader("Content-Type", "application/json");
            var name = header.Name;
            var value = header.Value;
            TestLogger.Info($"  HTTPHeader: {name}={value}");

            if (name == "Content-Type" && value == "application/json")
                _results.Pass("HTTPHeader construction");
            else
                _results.Fail("HTTPHeader construction", $"name='{name}', value='{value}'");
        }
        catch (Exception ex)
        {
            _results.Fail("HTTPHeader construction", ex.Message);
        }
    }

    // --- Test 6: HTTPHeaders construction ---
    void TestHTTPHeadersConstruction()
    {
        TestLogger.Info("Test: HTTPHeaders empty construction...");
        try
        {
            using var headers = new HTTPHeaders();
            TestLogger.Info($"  HTTPHeaders created: {headers != null}");

            if (headers != null)
                _results.Pass("HTTPHeaders empty construction");
            else
                _results.Fail("HTTPHeaders empty construction", "Instance was null");
        }
        catch (Exception ex)
        {
            _results.Fail("HTTPHeaders empty construction", ex.Message);
        }
    }

    // --- Test 7: Serializer construction ---
    void TestSerializerConstruction()
    {
        TestLogger.Info("Test: Serializer construction...");
        try
        {
            using var urlSerializer = new URLResponseSerializer();
            TestLogger.Info($"  URLResponseSerializer created: {urlSerializer != null}");

            using var dataSerializer = new DataResponseSerializer();
            TestLogger.Info($"  DataResponseSerializer created: {dataSerializer != null}");

            if (urlSerializer != null && dataSerializer != null)
                _results.Pass("Serializer construction");
            else
                _results.Fail("Serializer construction", "Some serializers were null");
        }
        catch (Exception ex)
        {
            _results.Fail("Serializer construction", ex.Message);
        }
    }

    // --- Test 8: ConnectionLostRetryPolicy construction ---
    void TestConnectionLostRetryPolicy()
    {
        TestLogger.Info("Test: ConnectionLostRetryPolicy construction...");
        try
        {
            using var policy = new ConnectionLostRetryPolicy();
            TestLogger.Info($"  ConnectionLostRetryPolicy created: {policy != null}");

            using var customPolicy = new ConnectionLostRetryPolicy(retryLimit: 5);
            TestLogger.Info($"  ConnectionLostRetryPolicy(retryLimit: 5) created: {customPolicy != null}");

            if (policy != null && customPolicy != null)
                _results.Pass("ConnectionLostRetryPolicy construction");
            else
                _results.Fail("ConnectionLostRetryPolicy construction", "Instance was null");
        }
        catch (Exception ex)
        {
            _results.Fail("ConnectionLostRetryPolicy construction", ex.Message);
        }
    }

    // --- Test 9: Empty type ---
    void TestEmptyType()
    {
        TestLogger.Info("Test: Empty type...");
        try
        {
            using var empty = Empty.GetEmptyValue();
            TestLogger.Info($"  Empty.GetEmptyValue() created: {empty != null}");

            if (empty != null)
                _results.Pass("Empty type");
            else
                _results.Fail("Empty type", "Instance was null");
        }
        catch (Exception ex)
        {
            _results.Fail("Empty type", ex.Message);
        }
    }

    // --- Test 10: Session.Default (moderate risk — static property on class) ---
    void TestSessionDefault()
    {
        TestLogger.Info("Test: Session.Default...");
        try
        {
            using var session = Session.Default;
            TestLogger.Info($"  Session.Default created: {session != null}");

            if (session != null)
                _results.Pass("Session.Default");
            else
                _results.Fail("Session.Default", "Instance was null");
        }
        catch (Exception ex)
        {
            _results.Fail("Session.Default", ex.Message);
        }
    }
}
