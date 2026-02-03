// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Foundation;
using Swift;
using Swift.Lottie;
using Swift.Runtime;
using UIKit;

namespace LottieTestApp;

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
        Memory,
        Perf,
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
            Category.Memory => "[MEM]",
            Category.Perf => "[PERF]",
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
    public static void Memory(string message) => Log(Category.Memory, message);
    public static void Perf(string message) => Log(Category.Perf, message);
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
        if (libraryName == "Lottie" || libraryName == "SwiftBindings")
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
    private readonly TestResults _results = new();

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
        var screenHeight = screenBounds.Height;

        var safeTop = 60.0;
        var contentWidth = screenWidth - 40;
        var titleHeight = 30.0;
        var buttonHeight = 40.0;
        var spacing = 8.0;
        var resultLabelHeight = 400.0;

        var currentY = safeTop;

        // Title
        var label = new UILabel
        {
            Text = "Lottie Binding Validation",
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

        TestLogger.Info("=== LOTTIE BINDING VALIDATION SUITE ===");
        TestLogger.Info($"Starting test run at {DateTime.Now:HH:mm:ss}");

        var results = new TestResults();

        try
        {
            // Test 1: Type metadata access
            await RunTestAsync("Type Metadata", TestTypeMetadataAsync, results);
            await Task.Delay(100);

            // Test 2: LottieConfiguration
            await RunTestAsync("LottieConfiguration", TestLottieConfigurationAsync, results);
            await Task.Delay(100);

            // Test 3: LottieColor
            await RunTestAsync("LottieColor", TestLottieColorAsync, results);
            await Task.Delay(100);

            // Test 4: LottieAnimation from bundled JSON
            await RunTestAsync("LottieAnimation JSON", TestLottieAnimationFromBundledJsonAsync, results);
            await Task.Delay(100);

            // Test 5: LottieVector types
            await RunTestAsync("LottieVector", TestLottieVectorAsync, results);
            await Task.Delay(100);

            // Test 6: Enum types
            await RunTestAsync("Enum Types", TestEnumTypesAsync, results);
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

    private Task TestTypeMetadataAsync(TestResults results)
    {
        TestLogger.Info("Testing type metadata access...");

        try
        {
            // Test LottieConfiguration metadata
            var configMetadata = SwiftObjectHelper<LottieConfiguration>.GetTypeMetadata();
            TestLogger.Info($"LottieConfiguration metadata size: {configMetadata.Size}");

            if (configMetadata.Size > 0)
            {
                results.Pass("LottieConfiguration metadata access");
            }
            else
            {
                results.Fail("LottieConfiguration metadata access", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            results.Fail("LottieConfiguration metadata access", ex.Message);
        }

        try
        {
            // Test LottieColor metadata
            var colorMetadata = SwiftObjectHelper<LottieColor>.GetTypeMetadata();
            TestLogger.Info($"LottieColor metadata size: {colorMetadata.Size}");

            if (colorMetadata.Size > 0)
            {
                results.Pass("LottieColor metadata access");
            }
            else
            {
                results.Fail("LottieColor metadata access", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            results.Fail("LottieColor metadata access", ex.Message);
        }

        return Task.CompletedTask;
    }

    private Task TestLottieConfigurationAsync(TestResults results)
    {
        TestLogger.Info("Testing LottieConfiguration...");

        try
        {
            // Get the default shared configuration
            var config = LottieConfiguration.Shared;
            TestLogger.Info($"LottieConfiguration.Shared: {config}");

            if (config != null && config.Payload != null)
            {
                results.Pass("LottieConfiguration.Shared access");
            }
            else
            {
                results.Fail("LottieConfiguration.Shared access", "Configuration is null");
            }
        }
        catch (Exception ex)
        {
            results.Fail("LottieConfiguration.Shared access", ex.Message);
        }

        return Task.CompletedTask;
    }

    private Task TestLottieColorAsync(TestResults results)
    {
        TestLogger.Info("Testing LottieColor...");

        try
        {
            // Create a LottieColor
            var color = new LottieColor(1.0, 0.5, 0.25, 1.0, ColorFormatDenominator.One);
            TestLogger.Info($"Created LottieColor with r=1.0, g=0.5, b=0.25, a=1.0");

            // Verify the color values
            var r = color.R;
            var g = color.G;
            var b = color.B;
            var a = color.A;

            TestLogger.Info($"Retrieved values: r={r}, g={g}, b={b}, a={a}");

            if (Math.Abs(r - 1.0) < 0.001 && Math.Abs(g - 0.5) < 0.001 &&
                Math.Abs(b - 0.25) < 0.001 && Math.Abs(a - 1.0) < 0.001)
            {
                results.Pass("LottieColor creation and property access");
            }
            else
            {
                results.Fail("LottieColor creation", "Color values don't match");
            }
        }
        catch (Exception ex)
        {
            results.Fail("LottieColor creation", ex.Message);
        }

        return Task.CompletedTask;
    }

    private Task TestLottieAnimationFromBundledJsonAsync(TestResults results)
    {
        TestLogger.Info("Testing LottieAnimation from bundled JSON...");

        try
        {
            var jsonPath = NSBundle.MainBundle.PathForResource("test-animation", "json");
            if (string.IsNullOrEmpty(jsonPath))
            {
                results.Fail("LottieAnimation JSON load", "Bundled test-animation.json not found");
                return Task.CompletedTask;
            }

            using var data = NSData.FromFile(jsonPath);
            if (data == null)
            {
                results.Fail("LottieAnimation JSON load", "Failed to read JSON file into NSData");
                return Task.CompletedTask;
            }

            var animation = LottieAnimation.From(data, DecodingStrategy.DictionaryBased);
            TestLogger.Info($"Animation loaded: duration={animation.Duration}, framerate={animation.Framerate}, start={animation.StartFrame}, end={animation.EndFrame}");

            if (animation.Framerate > 0 &&
                animation.EndFrame >= animation.StartFrame &&
                animation.Duration >= 0)
            {
                results.Pass("LottieAnimation from bundled JSON");
            }
            else
            {
                results.Fail("LottieAnimation from bundled JSON", "Animation properties are invalid");
            }
        }
        catch (Exception ex)
        {
            results.Fail("LottieAnimation from bundled JSON", ex.Message);
        }

        return Task.CompletedTask;
    }

    private Task TestLottieVectorAsync(TestResults results)
    {
        TestLogger.Info("Testing LottieVector types...");

        try
        {
            // Test LottieVector1D
            var vec1d = new LottieVector1D(42.0);
            var value = vec1d.Value;
            TestLogger.Info($"LottieVector1D: value={value}");

            if (Math.Abs(value - 42.0) < 0.001)
            {
                results.Pass("LottieVector1D creation and access");
            }
            else
            {
                results.Fail("LottieVector1D creation", $"Expected 42.0, got {value}");
            }
        }
        catch (Exception ex)
        {
            results.Fail("LottieVector1D creation", ex.Message);
        }

        try
        {
            // Test LottieVector3D
            var vec3d = new LottieVector3D(1.0, 2.0, 3.0);
            var x = vec3d.X;
            var y = vec3d.Y;
            var z = vec3d.Z;
            TestLogger.Info($"LottieVector3D: x={x}, y={y}, z={z}");

            if (Math.Abs(x - 1.0) < 0.001 && Math.Abs(y - 2.0) < 0.001 && Math.Abs(z - 3.0) < 0.001)
            {
                results.Pass("LottieVector3D creation and access");
            }
            else
            {
                results.Fail("LottieVector3D creation", "Values don't match");
            }
        }
        catch (Exception ex)
        {
            results.Fail("LottieVector3D creation", ex.Message);
        }

        return Task.CompletedTask;
    }

    private Task TestEnumTypesAsync(TestResults results)
    {
        TestLogger.Info("Testing enum types...");

        try
        {
            // Test LottieLoopMode
            var loopMode = LottieLoopMode.Loop;
            TestLogger.Info($"LottieLoopMode.Loop: {loopMode}");
            results.Pass("LottieLoopMode enum");
        }
        catch (Exception ex)
        {
            results.Fail("LottieLoopMode enum", ex.Message);
        }

        try
        {
            // Test LottieBackgroundBehavior
            var behavior = LottieBackgroundBehavior.Pause;
            TestLogger.Info($"LottieBackgroundBehavior.Pause: {behavior}");
            results.Pass("LottieBackgroundBehavior enum");
        }
        catch (Exception ex)
        {
            results.Fail("LottieBackgroundBehavior enum", ex.Message);
        }

        return Task.CompletedTask;
    }

    #endregion
}

#endregion
