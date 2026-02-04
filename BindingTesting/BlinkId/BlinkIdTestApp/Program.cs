// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Foundation;
using Swift;
using Swift.BlinkID;
using Swift.Runtime;
using UIKit;

namespace BlinkIdTestApp;

#region Logging Infrastructure

/// <summary>
/// Structured logging for test output with categories and timestamps.
/// </summary>
public static class TestLogger
{
    private static readonly object _lock = new();
    private static readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private static readonly StringBuilder _fullLog = new();

    public enum Category
    {
        Info,
        Success,
        Warning,
        Error,
        Test
    }

    public static void Log(Category category, string message)
    {
        var timestamp = _stopwatch.Elapsed.TotalSeconds;
        var prefix = category switch
        {
            Category.Success => "[PASS]",
            Category.Warning => "[WARN]",
            Category.Error => "[FAIL]",
            Category.Test => "[TEST]",
            _ => "[INFO]"
        };

        var line = $"[{timestamp:F3}s] {prefix} {message}";

        lock (_lock)
        {
            Console.WriteLine(line);
            _fullLog.AppendLine(line);
        }
    }

    public static void Info(string message) => Log(Category.Info, message);
    public static void Success(string message) => Log(Category.Success, message);
    public static void Warning(string message) => Log(Category.Warning, message);
    public static void Error(string message) => Log(Category.Error, message);
    public static void Test(string message) => Log(Category.Test, message);

    public static void Exception(Exception ex, string context = "")
    {
        var prefix = string.IsNullOrEmpty(context) ? "" : $"{context}: ";
        Error($"{prefix}{ex.GetType().Name}: {ex.Message}");
        if (ex.InnerException != null)
        {
            Error($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
    }

    public static string GetFullLog()
    {
        lock (_lock)
        {
            return _fullLog.ToString();
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _fullLog.Clear();
        }
    }
}

#endregion

#region Test Results

/// <summary>
/// Tracks test results for summary reporting.
/// </summary>
public class TestResults
{
    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public int Warnings { get; private set; }
    public List<string> FailedTests { get; } = new();

    public void Pass(string testName)
    {
        Passed++;
        TestLogger.Success(testName);
    }

    public void Fail(string testName, string reason = "")
    {
        Failed++;
        var msg = string.IsNullOrEmpty(reason) ? testName : $"{testName}: {reason}";
        FailedTests.Add(msg);
        TestLogger.Error(msg);
    }

    public void Warn(string message)
    {
        Warnings++;
        TestLogger.Warning(message);
    }

    public bool AllPassed => Failed == 0;

    public override string ToString()
    {
        var status = AllPassed ? "ALL TESTS PASSED" : "SOME TESTS FAILED";
        return $"{status}: {Passed} passed, {Failed} failed, {Warnings} warnings";
    }
}

#endregion

#region Application Entry Point

public class Application
{
    static void Main(string[] args)
    {
        // Register resolver for bundled frameworks BEFORE any Swift types are accessed
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), ResolveBundledFramework);

        UIApplication.Main(args, null, typeof(AppDelegate));
    }

    static IntPtr ResolveBundledFramework(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == "BlinkID" || libraryName == "SwiftBindings")
        {
            var frameworkPath = $"@rpath/{libraryName}.framework/{libraryName}";
            if (NativeLibrary.TryLoad(frameworkPath, out var handle))
            {
                TestLogger.Info($"Resolved {libraryName} -> {frameworkPath}");
                return handle;
            }
            TestLogger.Warning($"Failed to resolve {libraryName} at {frameworkPath}");
        }
        return IntPtr.Zero;
    }
}

#endregion

#region App Delegate

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

#endregion

#region Main View Controller

public class MainViewController : UIViewController
{
    private UILabel? _resultLabel;
    private UIScrollView? _scrollView;

    public override bool PrefersStatusBarHidden() => false;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();

        var screenBounds = UIScreen.MainScreen.Bounds;
        View!.Frame = screenBounds;
        View.BackgroundColor = UIColor.White;
        View.ClipsToBounds = false;

        EdgesForExtendedLayout = UIRectEdge.All;
        ExtendedLayoutIncludesOpaqueBars = true;

        var screenWidth = screenBounds.Width;
        var contentWidth = screenWidth - 40;
        var safeTop = 60.0;
        var titleHeight = 30.0;
        var buttonHeight = 40.0;
        var spacing = 8.0;
        var resultLabelHeight = 400.0;

        var currentY = safeTop;

        // Title
        var label = new UILabel
        {
            Text = "BlinkID Binding Validation",
            TextAlignment = UITextAlignment.Center,
            Font = UIFont.BoldSystemFontOfSize(18),
            Frame = new CoreGraphics.CGRect(20, currentY, contentWidth, titleHeight)
        };
        View.AddSubview(label);
        currentY += titleHeight + spacing;

        // Run All Tests button
        var runAllButton = UIButton.FromType(UIButtonType.System);
        runAllButton.Frame = new CoreGraphics.CGRect(20, currentY, contentWidth, buttonHeight);
        runAllButton.SetTitle("Run All Tests", UIControlState.Normal);
        runAllButton.BackgroundColor = UIColor.SystemBlue;
        runAllButton.SetTitleColor(UIColor.White, UIControlState.Normal);
        runAllButton.Layer.CornerRadius = 8;
        runAllButton.TouchUpInside += RunAllTests;
        View.AddSubview(runAllButton);
        currentY += buttonHeight + spacing;

        // Result label with scroll
        _scrollView = new UIScrollView
        {
            Frame = new CoreGraphics.CGRect(20, currentY, contentWidth, resultLabelHeight),
            BackgroundColor = UIColor.FromRGB(245, 245, 245),
            Layer = { CornerRadius = 8 }
        };

        _resultLabel = new UILabel
        {
            Text = "Running tests...",
            TextAlignment = UITextAlignment.Left,
            Lines = 0,
            Font = UIFont.FromName("Menlo", 10) ?? UIFont.SystemFontOfSize(10),
            Frame = new CoreGraphics.CGRect(8, 8, contentWidth - 16, resultLabelHeight - 16)
        };
        _scrollView.AddSubview(_resultLabel);
        View.AddSubview(_scrollView);

        // Run tests automatically on startup
        RunAllTests(null, EventArgs.Empty);
    }

    private void UpdateResultLabel(string text)
    {
        InvokeOnMainThread(() =>
        {
            _resultLabel!.Text = text;
            _resultLabel.SizeToFit();
            _resultLabel.Frame = new CoreGraphics.CGRect(
                8, 8,
                _scrollView!.Frame.Width - 16,
                Math.Max(_resultLabel.Frame.Height, _scrollView.Frame.Height - 16)
            );
            _scrollView.ContentSize = new CoreGraphics.CGSize(
                _scrollView.Frame.Width - 16,
                _resultLabel.Frame.Height + 16
            );
        });
    }

    #region Test Runner

    private async void RunAllTests(object? sender, EventArgs e)
    {
        TestLogger.Clear();

        TestLogger.Info("=== BLINKID BINDING VALIDATION SUITE ===");
        TestLogger.Info($"Starting test run at {DateTime.Now:HH:mm:ss}");

        var results = new TestResults();

        try
        {
            // Test 1: Type metadata access
            await RunTestAsync("Type Metadata", TestTypeMetadata, results);
            await Task.Delay(100);

            // Test 2: Enum case construction
            await RunTestAsync("Enum Cases", TestEnumCases, results);
            await Task.Delay(100);

            // Test 3: Enum raw values
            await RunTestAsync("Enum Raw Values", TestEnumRawValues, results);
            await Task.Delay(100);

            // Test 4: Enum FromRawValue factory
            await RunTestAsync("Enum FromRawValue", TestEnumFromRawValue, results);
            await Task.Delay(100);

            // Test 5: Static property access
            await RunTestAsync("Static Properties", TestStaticProperties, results);
            await Task.Delay(100);

            // Test 6: Additional type metadata
            await RunTestAsync("Extended Metadata", TestExtendedMetadata, results);
            await Task.Delay(100);
        }
        catch (Exception ex)
        {
            TestLogger.Exception(ex, "Test suite failed");
            results.Fail("Test Suite", ex.Message);
        }

        // Summary
        TestLogger.Info("=== TEST SUMMARY ===");
        TestLogger.Info(results.ToString());

        if (results.AllPassed)
        {
            Console.WriteLine("TEST SUCCESS");
            TestLogger.Success("=== VALIDATION PASSED ===");
        }
        else
        {
            Console.WriteLine($"TEST FAILURE: {results.Failed} tests failed");
            TestLogger.Error("=== VALIDATION FAILED ===");
            foreach (var failed in results.FailedTests)
            {
                TestLogger.Error($"  - {failed}");
            }
        }

        UpdateResultLabel(TestLogger.GetFullLog());
    }

    private async Task RunTestAsync(string name, Func<TestResults, Task> test, TestResults results)
    {
        TestLogger.Test($"--- {name} ---");
        try
        {
            await test(results);
        }
        catch (Exception ex)
        {
            TestLogger.Exception(ex, name);
            results.Fail(name, ex.Message);
        }
    }

    #endregion

    #region Test Implementations

    private Task TestTypeMetadata(TestResults results)
    {
        TestLogger.Info("Testing type metadata access...");

        // RequestTimeout
        try
        {
            var metadata = SwiftObjectHelper<RequestTimeout>.GetTypeMetadata();
            TestLogger.Info($"RequestTimeout metadata size: {metadata.Size}");
            if (metadata.Size > 0)
                results.Pass("RequestTimeout metadata");
            else
                results.Fail("RequestTimeout metadata", "Size is 0");
        }
        catch (Exception ex) { results.Fail("RequestTimeout metadata", ex.Message); }

        // DetectionStatus enum
        try
        {
            var metadata = SwiftObjectHelper<DetectionStatus>.GetTypeMetadata();
            TestLogger.Info($"DetectionStatus metadata size: {metadata.Size}");
            if (metadata.Size > 0)
                results.Pass("DetectionStatus metadata");
            else
                results.Fail("DetectionStatus metadata", "Size is 0");
        }
        catch (Exception ex) { results.Fail("DetectionStatus metadata", ex.Message); }

        // Country enum
        try
        {
            var metadata = SwiftObjectHelper<Country>.GetTypeMetadata();
            TestLogger.Info($"Country metadata size: {metadata.Size}");
            if (metadata.Size > 0)
                results.Pass("Country metadata");
            else
                results.Fail("Country metadata", "Size is 0");
        }
        catch (Exception ex) { results.Fail("Country metadata", ex.Message); }

        return Task.CompletedTask;
    }

    private Task TestEnumCases(TestResults results)
    {
        TestLogger.Info("Testing enum case construction...");

        // DetectionStatus cases
        try
        {
            var success = DetectionStatus.Success;
            var failed = DetectionStatus.Failed;
            TestLogger.Info($"DetectionStatus.Success: tag={success.Tag}");
            TestLogger.Info($"DetectionStatus.Failed: tag={failed.Tag}");

            if (success.Tag != failed.Tag)
                results.Pass("DetectionStatus case construction");
            else
                results.Fail("DetectionStatus cases", "Success and Failed have same tag");
        }
        catch (Exception ex) { results.Fail("DetectionStatus cases", ex.Message); }

        // ImageAnalysisDetectionStatus cases
        try
        {
            var notAvail = ImageAnalysisDetectionStatus.NotAvailable;
            var detected = ImageAnalysisDetectionStatus.Detected;
            var notDetected = ImageAnalysisDetectionStatus.NotDetected;
            TestLogger.Info($"ImageAnalysisDetectionStatus: NotAvailable={notAvail.Tag}, Detected={detected.Tag}, NotDetected={notDetected.Tag}");

            // All three should have different tags
            if (notAvail.Tag != detected.Tag && detected.Tag != notDetected.Tag)
                results.Pass("ImageAnalysisDetectionStatus case construction");
            else
                results.Fail("ImageAnalysisDetectionStatus cases", "Duplicate tags");
        }
        catch (Exception ex) { results.Fail("ImageAnalysisDetectionStatus cases", ex.Message); }

        // DocumentOrientation cases
        try
        {
            var horizontal = DocumentOrientation.Horizontal;
            var vertical = DocumentOrientation.Vertical;
            TestLogger.Info($"DocumentOrientation: Horizontal={horizontal.Tag}, Vertical={vertical.Tag}");

            if (horizontal.Tag != vertical.Tag)
                results.Pass("DocumentOrientation case construction");
            else
                results.Fail("DocumentOrientation cases", "Same tag for different cases");
        }
        catch (Exception ex) { results.Fail("DocumentOrientation cases", ex.Message); }

        // DocumentRotation cases
        try
        {
            var zero = DocumentRotation.Zero;
            var cw90 = DocumentRotation.Clockwise90;
            var ccw90 = DocumentRotation.CounterClockwise90;
            var upside = DocumentRotation.UpsideDown;
            TestLogger.Info($"DocumentRotation: Zero={zero.Tag}, CW90={cw90.Tag}, CCW90={ccw90.Tag}, UpsideDown={upside.Tag}");

            if (zero.Tag != cw90.Tag && cw90.Tag != ccw90.Tag && ccw90.Tag != upside.Tag)
                results.Pass("DocumentRotation case construction");
            else
                results.Fail("DocumentRotation cases", "Duplicate tags");
        }
        catch (Exception ex) { results.Fail("DocumentRotation cases", ex.Message); }

        return Task.CompletedTask;
    }

    private Task TestEnumRawValues(TestResults results)
    {
        TestLogger.Info("Testing enum raw value access...");

        // DocumentOrientation has IntPtr raw values
        try
        {
            var horizontal = DocumentOrientation.Horizontal;
            var rawValue = horizontal.RawValue;
            TestLogger.Info($"DocumentOrientation.Horizontal raw value: {rawValue}");
            results.Pass("DocumentOrientation raw value access");
        }
        catch (Exception ex) { results.Fail("DocumentOrientation raw value", ex.Message); }

        // DocumentRotation has IntPtr raw values
        try
        {
            var zero = DocumentRotation.Zero;
            var cw90 = DocumentRotation.Clockwise90;
            var rawZero = zero.RawValue;
            var rawCw90 = cw90.RawValue;
            TestLogger.Info($"DocumentRotation raw values: Zero={rawZero}, CW90={rawCw90}");

            if (rawZero != rawCw90)
                results.Pass("DocumentRotation raw values differ");
            else
                results.Fail("DocumentRotation raw values", "Same raw value for different cases");
        }
        catch (Exception ex) { results.Fail("DocumentRotation raw values", ex.Message); }

        // Country has SwiftString raw values
        try
        {
            var none = Country.None;
            var rawValue = none.RawValue;
            TestLogger.Info($"Country.None raw value: {rawValue}");
            results.Pass("Country raw value access");
        }
        catch (Exception ex) { results.Fail("Country raw value", ex.Message); }

        // DocumentType has SwiftString raw values
        try
        {
            var none = DocumentType.None;
            var rawValue = none.RawValue;
            TestLogger.Info($"DocumentType.None raw value: {rawValue}");
            results.Pass("DocumentType raw value access");
        }
        catch (Exception ex) { results.Fail("DocumentType raw value", ex.Message); }

        return Task.CompletedTask;
    }

    private Task TestEnumFromRawValue(TestResults results)
    {
        TestLogger.Info("Testing enum FromRawValue factory methods...");

        // DocumentOrientation from raw value
        try
        {
            var horizontal = DocumentOrientation.Horizontal;
            var rawValue = horizontal.RawValue;
            var roundTripped = DocumentOrientation.FromRawValue((long)rawValue);
            TestLogger.Info($"DocumentOrientation round-trip: rawValue={rawValue}");

            if (roundTripped != null)
                results.Pass("DocumentOrientation FromRawValue round-trip");
            else
                results.Fail("DocumentOrientation FromRawValue", "Returned null");
        }
        catch (Exception ex) { results.Fail("DocumentOrientation FromRawValue", ex.Message); }

        // DocumentRotation from raw value
        try
        {
            var cw90 = DocumentRotation.Clockwise90;
            var cw90Raw = cw90.RawValue;
            var roundTripped = DocumentRotation.FromRawValue((long)cw90Raw);
            TestLogger.Info($"DocumentRotation round-trip: rawValue={cw90Raw}");

            if (roundTripped != null)
                results.Pass("DocumentRotation FromRawValue round-trip");
            else
                results.Fail("DocumentRotation FromRawValue", "Returned null");
        }
        catch (Exception ex) { results.Fail("DocumentRotation FromRawValue", ex.Message); }

        return Task.CompletedTask;
    }

    private Task TestStaticProperties(TestResults results)
    {
        TestLogger.Info("Testing static property access...");

        // RequestTimeout.Default
        try
        {
            var defaultTimeout = RequestTimeout.Default;
            TestLogger.Info($"RequestTimeout.Default payload handle: {defaultTimeout.Payload.DangerousGetHandle()}");

            if (!defaultTimeout.Payload.IsInvalid)
                results.Pass("RequestTimeout.Default access");
            else
                results.Fail("RequestTimeout.Default", "Payload is invalid");
        }
        catch (Exception ex) { results.Fail("RequestTimeout.Default", ex.Message); }

        return Task.CompletedTask;
    }

    private Task TestExtendedMetadata(TestResults results)
    {
        TestLogger.Info("Testing extended type metadata...");

        // DocumentImageColorStatus
        try
        {
            var metadata = SwiftObjectHelper<DocumentImageColorStatus>.GetTypeMetadata();
            TestLogger.Info($"DocumentImageColorStatus metadata size: {metadata.Size}");
            if (metadata.Size > 0)
                results.Pass("DocumentImageColorStatus metadata");
            else
                results.Fail("DocumentImageColorStatus metadata", "Size is 0");
        }
        catch (Exception ex) { results.Fail("DocumentImageColorStatus metadata", ex.Message); }

        // Region
        try
        {
            var metadata = SwiftObjectHelper<Region>.GetTypeMetadata();
            TestLogger.Info($"Region metadata size: {metadata.Size}");
            if (metadata.Size > 0)
                results.Pass("Region metadata");
            else
                results.Fail("Region metadata", "Size is 0");
        }
        catch (Exception ex) { results.Fail("Region metadata", ex.Message); }

        // Point
        try
        {
            var metadata = SwiftObjectHelper<Point>.GetTypeMetadata();
            TestLogger.Info($"Point metadata size: {metadata.Size}");
            if (metadata.Size > 0)
                results.Pass("Point metadata");
            else
                results.Fail("Point metadata", "Size is 0");
        }
        catch (Exception ex) { results.Fail("Point metadata", ex.Message); }

        // Quadrilateral
        try
        {
            var metadata = SwiftObjectHelper<Quadrilateral>.GetTypeMetadata();
            TestLogger.Info($"Quadrilateral metadata size: {metadata.Size}");
            if (metadata.Size > 0)
                results.Pass("Quadrilateral metadata");
            else
                results.Fail("Quadrilateral metadata", "Size is 0");
        }
        catch (Exception ex) { results.Fail("Quadrilateral metadata", ex.Message); }

        return Task.CompletedTask;
    }

    #endregion
}

#endregion
