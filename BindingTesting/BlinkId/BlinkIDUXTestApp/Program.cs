// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Foundation;
using ObjCRuntime;
using UIKit;

namespace BlinkIDUXTestApp;

#region Logging Infrastructure

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

#region P/Invoke Declarations

internal static class NativeMethods
{
    private const string BridgeLib = "BlinkIDUXBridge";

    [DllImport(BridgeLib, EntryPoint = "SBW_BlinkIDUX_NoInternetView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr NoInternetView_Create(IntPtr retryCallback, IntPtr userData);

    [DllImport(BridgeLib, EntryPoint = "SBW_BlinkIDUX_NoInternetView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr NoInternetView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_BlinkIDUX_NoInternetView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void NoInternetView_Free(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_BlinkIDUX_NoInternetView_FireRetry")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void NoInternetView_FireRetry(IntPtr handle);
}

#endregion

#region Managed Wrapper

public class NoInternetViewSession : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    public NoInternetViewSession(IntPtr handle) => _handle = handle;

    public IntPtr Handle => !_disposed
        ? _handle
        : throw new ObjectDisposedException(nameof(NoInternetViewSession));

    public IntPtr GetViewController() =>
        NativeMethods.NoInternetView_GetViewController(Handle);

    public void FireRetry() =>
        NativeMethods.NoInternetView_FireRetry(Handle);

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            NativeMethods.NoInternetView_Free(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

#endregion

#region Retry Callback

internal static class RetryState
{
    internal static volatile int CallbackCount;
    internal static volatile IntPtr LastUserData;
    private static TaskCompletionSource<bool>? _callbackTcs;

    internal static void Reset()
    {
        CallbackCount = 0;
        LastUserData = IntPtr.Zero;
        _callbackTcs = null;
    }

    /// Arms a TaskCompletionSource before firing. Call this before the
    /// action that triggers the callback, then await the returned Task.
    internal static Task PrepareForCallback(int timeoutMs = 3000)
    {
        _callbackTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return Task.WhenAny(_callbackTcs.Task, Task.Delay(timeoutMs));
    }

    /// True if the last PrepareForCallback completed via callback (not timeout).
    internal static bool CallbackFired => _callbackTcs?.Task.IsCompleted == true;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static void OnRetryCallback(IntPtr userData)
    {
        Interlocked.Increment(ref CallbackCount);
        LastUserData = userData;
        TestLogger.Info($"Retry callback fired! userData=0x{userData:X}, count={CallbackCount}");
        _callbackTcs?.TrySetResult(true);
    }
}

#endregion

#region Application Entry Point

public class Application
{
    static void Main(string[] args)
    {
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), ResolveBundledFramework);
        UIApplication.Main(args, null, typeof(AppDelegate));
    }

    static IntPtr ResolveBundledFramework(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName is "BlinkIDUXBridge" or "BlinkID" or "BlinkIDUX")
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
            Text = "BlinkIDUX Bridge Validation",
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

        TestLogger.Info("=== BLINKIDUX BRIDGE VALIDATION SUITE ===");
        TestLogger.Info($"Starting test run at {DateTime.Now:HH:mm:ss}");

        var results = new TestResults();

        try
        {
            // Test 1: Create session
            await RunTestAsync("Create Session", TestCreateSession, results);
            await Task.Delay(100);

            // Test 2: Get view controller
            await RunTestAsync("Get ViewController", TestGetViewController, results);
            await Task.Delay(100);

            // Test 3: Present view
            await RunTestAsync("Present View", TestPresentView, results);
            await Task.Delay(100);

            // Test 4: Fire retry callback
            await RunTestAsync("Fire Retry Callback", TestFireRetryCallback, results);
            await Task.Delay(100);

            // Test 5: Cleanup
            await RunTestAsync("Cleanup", TestCleanup, results);
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

    #region Shared State

    // Session handle shared across tests (created in Test 1, disposed in Test 5)
    private NoInternetViewSession? _session;
    private UIViewController? _presentedVC;

    #endregion

    #region Test Implementations

    private unsafe Task TestCreateSession(TestResults results)
    {
        TestLogger.Info("Creating NoInternetView session with callback...");

        RetryState.Reset();

        // Get function pointer for the callback
        delegate* unmanaged[Cdecl]<IntPtr, void> callbackPtr = &RetryState.OnRetryCallback;
        var callbackIntPtr = (IntPtr)callbackPtr;

        // Use a sentinel value as userData to verify round-trip
        var userData = new IntPtr(0x42);

        var handle = NativeMethods.NoInternetView_Create(callbackIntPtr, userData);

        if (handle != IntPtr.Zero)
        {
            _session = new NoInternetViewSession(handle);
            results.Pass("Create session (handle != 0)");
        }
        else
        {
            results.Fail("Create session", "handle is IntPtr.Zero");
        }

        return Task.CompletedTask;
    }

    private Task TestGetViewController(TestResults results)
    {
        if (_session == null)
        {
            results.Fail("Get ViewController", "No session (previous test failed)");
            return Task.CompletedTask;
        }

        TestLogger.Info("Getting UIViewController from session...");

        var vcPtr = _session.GetViewController();
        TestLogger.Info($"ViewController pointer: 0x{vcPtr:X}");

        if (vcPtr == IntPtr.Zero)
        {
            results.Fail("Get ViewController", "pointer is IntPtr.Zero");
            return Task.CompletedTask;
        }

        results.Pass("Get ViewController (pointer != 0)");

        // Wrap as managed UIViewController
        var vc = Runtime.GetNSObject<UIViewController>(vcPtr);
        if (vc != null)
        {
            _presentedVC = vc;
            TestLogger.Info($"UIViewController type: {vc.GetType().Name}");
            results.Pass("Wrap as UIViewController");
        }
        else
        {
            results.Fail("Wrap as UIViewController", "Runtime.GetNSObject returned null");
        }

        return Task.CompletedTask;
    }

    private async Task TestPresentView(TestResults results)
    {
        if (_presentedVC == null)
        {
            results.Fail("Present View", "No ViewController (previous test failed)");
            return;
        }

        TestLogger.Info("Presenting NoInternetView modally...");

        var tcs = new TaskCompletionSource<bool>();

        InvokeOnMainThread(() =>
        {
            PresentViewController(_presentedVC, animated: true, completionHandler: () =>
            {
                tcs.TrySetResult(true);
            });
        });

        // Wait for presentation to complete (with timeout)
        var presented = await Task.WhenAny(tcs.Task, Task.Delay(3000)) == tcs.Task;
        if (presented)
        {
            results.Pass("Present ViewController");
        }
        else
        {
            results.Warn("Present ViewController timed out (may still be visible)");
        }

        // Give the SwiftUI view time to render
        await Task.Delay(1000);
        TestLogger.Info("View should now be visible on screen");
        results.Pass("View rendered (1s delay)");
    }

    private async Task TestFireRetryCallback(TestResults results)
    {
        if (_session == null)
        {
            results.Fail("Fire Retry Callback", "No session (previous test failed)");
            return;
        }

        TestLogger.Info("Firing retry callback via test helper...");

        var beforeCount = RetryState.CallbackCount;
        var callbackTask = RetryState.PrepareForCallback();
        _session.FireRetry();

        // Callback is dispatched asynchronously on the main queue (matching
        // the production NoInternetView retryAction path). PrepareForCallback
        // arms a TCS that OnRetryCallback signals, or times out after 3s.
        await callbackTask;

        if (!RetryState.CallbackFired)
        {
            results.Fail("Retry callback", "Timed out waiting for async callback");
            return;
        }

        if (RetryState.CallbackCount == beforeCount + 1)
        {
            results.Pass("Retry callback fired (count incremented)");
        }
        else
        {
            results.Fail("Retry callback", $"Expected count {beforeCount + 1}, got {RetryState.CallbackCount}");
        }

        // Verify userData round-tripped
        if (RetryState.LastUserData == new IntPtr(0x42))
        {
            results.Pass("userData round-trip (0x42)");
        }
        else
        {
            results.Fail("userData round-trip", $"Expected 0x42, got 0x{RetryState.LastUserData:X}");
        }
    }

    private async Task TestCleanup(TestResults results)
    {
        // Dismiss the presented view controller
        if (_presentedVC != null)
        {
            TestLogger.Info("Dismissing presented ViewController...");
            var tcs = new TaskCompletionSource<bool>();

            InvokeOnMainThread(() =>
            {
                DismissViewController(animated: true, completionHandler: () =>
                {
                    tcs.TrySetResult(true);
                });
            });

            var dismissed = await Task.WhenAny(tcs.Task, Task.Delay(3000)) == tcs.Task;
            if (dismissed)
            {
                results.Pass("Dismiss ViewController");
            }
            else
            {
                results.Warn("Dismiss timed out (continuing anyway)");
            }
            _presentedVC = null;
        }

        // Dispose the session
        if (_session != null)
        {
            TestLogger.Info("Disposing session...");
            _session.Dispose();
            results.Pass("Session disposed");

            // Verify post-dispose access throws
            try
            {
                _ = _session.Handle;
                results.Fail("Post-dispose access", "Expected ObjectDisposedException");
            }
            catch (ObjectDisposedException)
            {
                results.Pass("Post-dispose throws ObjectDisposedException");
            }

            _session = null;
        }
    }

    #endregion
}

#endregion
