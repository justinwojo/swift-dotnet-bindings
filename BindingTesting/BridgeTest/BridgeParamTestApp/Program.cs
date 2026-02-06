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

namespace BridgeParamTestApp;

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

#region P/Invoke Declarations — Bridge Functions

internal static class NativeMethods
{
    private const string BridgeLib = "BridgeParamTestLibBridge";

    // --- EnumParamView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_EnumParamView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr EnumParamView_Create(int style);

    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_EnumParamView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr EnumParamView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_EnumParamView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void EnumParamView_Free(IntPtr handle);

    // --- ClassParamView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_ClassParamView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr ClassParamView_Create(IntPtr modelPtr);

    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_ClassParamView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr ClassParamView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_ClassParamView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void ClassParamView_Free(IntPtr handle);

    // --- TypedClosureView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_TypedClosureView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr TypedClosureView_Create(IntPtr onValueCallback, IntPtr onValueUserData);

    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_TypedClosureView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr TypedClosureView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_TypedClosureView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void TypedClosureView_Free(IntPtr handle);

    // --- MultiArgClosureView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_MultiArgClosureView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr MultiArgClosureView_Create(IntPtr onEventCallback, IntPtr onEventUserData);

    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_MultiArgClosureView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr MultiArgClosureView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_MultiArgClosureView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void MultiArgClosureView_Free(IntPtr handle);

    // --- MixedParamView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_MixedParamView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr MixedParamView_Create(int style, IntPtr onActionCallback, IntPtr onActionUserData, int count);

    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_MixedParamView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr MixedParamView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_MixedParamView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void MixedParamView_Free(IntPtr handle);

    // --- OptionalEnumView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_OptionalEnumView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr OptionalEnumView_Create(int styleHasValue, int styleValue);

    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_OptionalEnumView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr OptionalEnumView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_OptionalEnumView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void OptionalEnumView_Free(IntPtr handle);

    // --- OptionalClassView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_OptionalClassView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr OptionalClassView_Create(IntPtr modelPtr);

    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_OptionalClassView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr OptionalClassView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_BridgeParamTestLib_OptionalClassView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void OptionalClassView_Free(IntPtr handle);
}

#endregion

#region P/Invoke Declarations — Test Helpers

internal static class TestHelpers
{
    private const string BridgeLib = "BridgeParamTestLibBridge";

    // SimpleModel helpers
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_CreateSimpleModel")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr CreateSimpleModel(int value);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_FreeSimpleModel")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void FreeSimpleModel(IntPtr ptr);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_GetSimpleModelDeinitCount")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int GetSimpleModelDeinitCount();

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_ResetSimpleModelDeinitCount")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void ResetSimpleModelDeinitCount();

    // EnumParamView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_EnumParamView_GetStyle")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int EnumParamView_GetStyle(IntPtr handle);

    // ClassParamView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_ClassParamView_GetModelValue")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int ClassParamView_GetModelValue(IntPtr handle);

    // TypedClosureView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_TypedClosureView_InvokeClosure")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int TypedClosureView_InvokeClosure(IntPtr handle, int value);

    // MultiArgClosureView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_MultiArgClosureView_InvokeClosure")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int MultiArgClosureView_InvokeClosure(IntPtr handle, int val, int flag);

    // MixedParamView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_MixedParamView_GetStyle")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int MixedParamView_GetStyle(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_MixedParamView_FireAction")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int MixedParamView_FireAction(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_MixedParamView_GetCount")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int MixedParamView_GetCount(IntPtr handle);

    // OptionalEnumView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_OptionalEnumView_HasValue")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int OptionalEnumView_HasValue(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_OptionalEnumView_GetStyle")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int OptionalEnumView_GetStyle(IntPtr handle);

    // OptionalClassView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_OptionalClassView_HasValue")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int OptionalClassView_HasValue(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_OptionalClassView_GetModelValue")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int OptionalClassView_GetModelValue(IntPtr handle);
}

#endregion

#region Managed Session Wrapper

public class BridgeSession : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;
    private readonly string _name;
    private readonly Action<IntPtr> _freeAction;

    public BridgeSession(IntPtr handle, string name, Action<IntPtr> freeAction)
    {
        _handle = handle;
        _name = name;
        _freeAction = freeAction;
    }

    public IntPtr Handle => !_disposed
        ? _handle
        : throw new ObjectDisposedException(_name);

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _freeAction(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

#endregion

#region Callback State

internal static class TypedClosureState
{
    internal static volatile int LastArgValue;
    internal static volatile bool LastReturnedTrue;
    internal static volatile int CallCount;

    internal static void Reset()
    {
        LastArgValue = 0;
        LastReturnedTrue = false;
        CallCount = 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static int OnValueCallback(int value, IntPtr userData)
    {
        LastArgValue = value;
        Interlocked.Increment(ref CallCount);
        // Return true (1) if value is positive
        var result = value > 0;
        LastReturnedTrue = result;
        return result ? 1 : 0;
    }
}

internal static class MultiArgClosureState
{
    internal static volatile int LastVal;
    internal static volatile bool LastFlag;
    internal static volatile int CallCount;

    internal static void Reset()
    {
        LastVal = 0;
        LastFlag = false;
        CallCount = 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static void OnEventCallback(int val, int flag, IntPtr userData)
    {
        LastVal = val;
        LastFlag = flag != 0;
        Interlocked.Increment(ref CallCount);
    }
}

internal static class MixedActionState
{
    internal static volatile int CallCount;

    internal static void Reset()
    {
        CallCount = 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static void OnActionCallback(IntPtr userData)
    {
        Interlocked.Increment(ref CallCount);
    }
}

#endregion

#region Framework Diagnostics

internal static class FrameworkDiagnostics
{
    private static readonly string[] RequiredFrameworks = { "BridgeParamTestLib", "BridgeParamTestLibBridge" };

    internal static bool ValidateAll()
    {
        TestLogger.Info("--- Framework Diagnostics ---");
        var allOk = true;

        foreach (var name in RequiredFrameworks)
        {
            var path = $"@rpath/{name}.framework/{name}";
            if (NativeLibrary.TryLoad(path, out var handle))
            {
                TestLogger.Success($"Framework loaded: {name}");
                NativeLibrary.Free(handle);
            }
            else
            {
                TestLogger.Error($"Framework MISSING: {name} (tried {path})");
                allOk = false;
            }
        }

        if (!allOk)
        {
            TestLogger.Error("Required frameworks missing — cannot run tests.");
        }

        return allOk;
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
        if (libraryName is "BridgeParamTestLibBridge" or "BridgeParamTestLib")
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

        var screenWidth = screenBounds.Width;
        var contentWidth = screenWidth - 40;
        var safeTop = 60.0;
        var titleHeight = 30.0;
        var buttonHeight = 40.0;
        var spacing = 8.0;
        var resultLabelHeight = 500.0;

        var currentY = safeTop;

        // Title
        var label = new UILabel
        {
            Text = "Bridge Param Type Validation",
            TextAlignment = UITextAlignment.Center,
            Font = UIFont.BoldSystemFontOfSize(16),
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
            Font = UIFont.FromName("Menlo", 9) ?? UIFont.SystemFontOfSize(9),
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

        TestLogger.Info("=== BRIDGE PARAM TYPE VALIDATION SUITE ===");
        TestLogger.Info($"Starting test run at {DateTime.Now:HH:mm:ss}");

        // Fail-fast: verify all required native frameworks are loadable
        if (!FrameworkDiagnostics.ValidateAll())
        {
            Console.WriteLine("TEST FAILURE: Required frameworks missing");
            TestLogger.Error("=== VALIDATION FAILED (framework diagnostics) ===");
            UpdateResultLabel(TestLogger.GetFullLog());
            return;
        }

        var results = new TestResults();

        try
        {
            // --- BoundEnum Tests ---
            TestLogger.Info("");
            TestLogger.Info("--- BoundEnum (EnumParamView) ---");
            await RunTestAsync("EnumParam_Create", TestEnumParam_Create, results);
            await RunTestAsync("EnumParam_ValueRoundTrip", TestEnumParam_ValueRoundTrip, results);
            await RunTestAsync("EnumParam_GetVC", TestEnumParam_GetVC, results);
            await Task.Delay(100);

            // --- BoundType Tests ---
            TestLogger.Info("");
            TestLogger.Info("--- BoundType (ClassParamView) ---");
            await RunTestAsync("ClassParam_Create", TestClassParam_Create, results);
            await RunTestAsync("ClassParam_ValueRoundTrip", TestClassParam_ValueRoundTrip, results);
            await RunTestAsync("ClassParam_Lifetime", TestClassParam_Lifetime, results);
            await Task.Delay(100);

            // --- TypedClosure Tests ---
            TestLogger.Info("");
            TestLogger.Info("--- TypedClosure (TypedClosureView) ---");
            await RunTestAsync("TypedClosure_Create", TestTypedClosure_Create, results);
            await RunTestAsync("TypedClosure_RoundTrip", TestTypedClosure_RoundTrip, results);
            await Task.Delay(100);

            // --- MultiArgClosure Tests ---
            TestLogger.Info("");
            TestLogger.Info("--- MultiArgClosure (MultiArgClosureView) ---");
            await RunTestAsync("MultiArgClosure_Create", TestMultiArgClosure_Create, results);
            await RunTestAsync("MultiArgClosure_RoundTrip", TestMultiArgClosure_RoundTrip, results);
            await Task.Delay(100);

            // --- MixedParam Tests ---
            TestLogger.Info("");
            TestLogger.Info("--- MixedParam (MixedParamView) ---");
            await RunTestAsync("MixedParam_Create", TestMixedParam_Create, results);
            await RunTestAsync("MixedParam_ValuesRoundTrip", TestMixedParam_ValuesRoundTrip, results);
            await RunTestAsync("MixedParam_CallbackRoundTrip", TestMixedParam_CallbackRoundTrip, results);
            await Task.Delay(100);

            // --- OptionalEnum Tests ---
            TestLogger.Info("");
            TestLogger.Info("--- OptionalEnum (OptionalEnumView) ---");
            await RunTestAsync("OptionalEnum_WithValue", TestOptionalEnum_WithValue, results);
            await RunTestAsync("OptionalEnum_Nil", TestOptionalEnum_Nil, results);
            await Task.Delay(100);

            // --- OptionalClass Tests ---
            TestLogger.Info("");
            TestLogger.Info("--- OptionalClass (OptionalClassView) ---");
            await RunTestAsync("OptionalClass_WithValue", TestOptionalClass_WithValue, results);
            await RunTestAsync("OptionalClass_Nil", TestOptionalClass_Nil, results);
            await Task.Delay(100);

            // --- Cleanup ---
            TestLogger.Info("");
            TestLogger.Info("--- Cleanup ---");
            await RunTestAsync("Cleanup", TestCleanup, results);
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

    // Session handles (created during tests, freed in Cleanup)
    private BridgeSession? _enumSession;
    private BridgeSession? _classSession;
    private IntPtr _classModelPtr;
    private BridgeSession? _typedClosureSession;
    private BridgeSession? _multiArgClosureSession;
    private BridgeSession? _mixedSession;
    private BridgeSession? _optEnumWithValueSession;
    private BridgeSession? _optEnumNilSession;
    private BridgeSession? _optClassWithValueSession;
    private IntPtr _optClassModelPtr;
    private BridgeSession? _optClassNilSession;

    #endregion

    #region Test Implementations — BoundEnum

    private Task TestEnumParam_Create(TestResults results)
    {
        TestLogger.Info("Creating EnumParamView session with style=1 (warning)...");

        var handle = NativeMethods.EnumParamView_Create(1); // AlertStyle.warning
        if (handle != IntPtr.Zero)
        {
            _enumSession = new BridgeSession(handle, "EnumParamView", NativeMethods.EnumParamView_Free);
            results.Pass("EnumParam_Create (handle != 0)");
        }
        else
        {
            results.Fail("EnumParam_Create", "handle is IntPtr.Zero");
        }
        return Task.CompletedTask;
    }

    private Task TestEnumParam_ValueRoundTrip(TestResults results)
    {
        if (_enumSession == null) { results.Fail("EnumParam_ValueRoundTrip", "no session"); return Task.CompletedTask; }

        var style = TestHelpers.EnumParamView_GetStyle(_enumSession.Handle);
        TestLogger.Info($"GetStyle returned: {style}");

        if (style == 1)
            results.Pass("EnumParam_ValueRoundTrip (style == 1)");
        else
            results.Fail("EnumParam_ValueRoundTrip", $"expected 1, got {style}");

        return Task.CompletedTask;
    }

    private Task TestEnumParam_GetVC(TestResults results)
    {
        if (_enumSession == null) { results.Fail("EnumParam_GetVC", "no session"); return Task.CompletedTask; }

        var vcPtr = NativeMethods.EnumParamView_GetViewController(_enumSession.Handle);
        TestLogger.Info($"GetViewController returned: 0x{vcPtr:X}");

        if (vcPtr != IntPtr.Zero)
            results.Pass("EnumParam_GetVC (pointer != 0)");
        else
            results.Fail("EnumParam_GetVC", "pointer is IntPtr.Zero");

        return Task.CompletedTask;
    }

    #endregion

    #region Test Implementations — BoundType

    private Task TestClassParam_Create(TestResults results)
    {
        TestLogger.Info("Creating SimpleModel(value=99) and ClassParamView session...");

        _classModelPtr = TestHelpers.CreateSimpleModel(99);
        if (_classModelPtr == IntPtr.Zero)
        {
            results.Fail("ClassParam_Create", "CreateSimpleModel returned null");
            return Task.CompletedTask;
        }

        var handle = NativeMethods.ClassParamView_Create(_classModelPtr);
        if (handle != IntPtr.Zero)
        {
            _classSession = new BridgeSession(handle, "ClassParamView", NativeMethods.ClassParamView_Free);
            results.Pass("ClassParam_Create (handle != 0)");
        }
        else
        {
            results.Fail("ClassParam_Create", "handle is IntPtr.Zero");
        }
        return Task.CompletedTask;
    }

    private Task TestClassParam_ValueRoundTrip(TestResults results)
    {
        if (_classSession == null) { results.Fail("ClassParam_ValueRoundTrip", "no session"); return Task.CompletedTask; }

        var value = TestHelpers.ClassParamView_GetModelValue(_classSession.Handle);
        TestLogger.Info($"GetModelValue returned: {value}");

        if (value == 99)
            results.Pass("ClassParam_ValueRoundTrip (value == 99)");
        else
            results.Fail("ClassParam_ValueRoundTrip", $"expected 99, got {value}");

        return Task.CompletedTask;
    }

    private Task TestClassParam_Lifetime(TestResults results)
    {
        TestLogger.Info("Testing SimpleModel lifetime — session must retain model...");

        TestHelpers.ResetSimpleModelDeinitCount();

        // Create a fresh model for lifetime testing (+1 retain from passRetained)
        var modelPtr = TestHelpers.CreateSimpleModel(42);
        TestLogger.Info($"After create: deinitCount={TestHelpers.GetSimpleModelDeinitCount()}");

        // Create session — session stores strong ref via `let model: SimpleModel`
        var sessionHandle = NativeMethods.ClassParamView_Create(modelPtr);
        if (sessionHandle == IntPtr.Zero)
        {
            results.Fail("ClassParam_Lifetime", "session creation failed");
            TestHelpers.FreeSimpleModel(modelPtr);
            return Task.CompletedTask;
        }

        // Free original model pointer first (release the +1 from CreateSimpleModel).
        // If the session correctly retains the model, it should still be alive.
        TestHelpers.FreeSimpleModel(modelPtr);
        var afterModelFree = TestHelpers.GetSimpleModelDeinitCount();
        TestLogger.Info($"After model free (session alive): deinitCount={afterModelFree}");

        if (afterModelFree == 0)
            results.Pass("ClassParam_Lifetime: model alive while session holds it");
        else
            results.Fail("ClassParam_Lifetime", $"model deallocated while session alive (deinitCount={afterModelFree})");

        // Now free session — session releases its strong ref, model should dealloc
        NativeMethods.ClassParamView_Free(sessionHandle);
        var afterSessionFree = TestHelpers.GetSimpleModelDeinitCount();
        TestLogger.Info($"After session free: deinitCount={afterSessionFree}");

        if (afterSessionFree == 1)
            results.Pass("ClassParam_Lifetime: model deallocated after session free");
        else
            results.Fail("ClassParam_Lifetime", $"expected deinitCount=1, got {afterSessionFree}");

        return Task.CompletedTask;
    }

    #endregion

    #region Test Implementations — TypedClosure

    private unsafe Task TestTypedClosure_Create(TestResults results)
    {
        TestLogger.Info("Creating TypedClosureView session with (Int32) -> Bool closure...");

        TypedClosureState.Reset();

        delegate* unmanaged[Cdecl]<int, IntPtr, int> callbackPtr = &TypedClosureState.OnValueCallback;
        var handle = NativeMethods.TypedClosureView_Create((IntPtr)callbackPtr, IntPtr.Zero);

        if (handle != IntPtr.Zero)
        {
            _typedClosureSession = new BridgeSession(handle, "TypedClosureView", NativeMethods.TypedClosureView_Free);
            results.Pass("TypedClosure_Create (handle != 0)");
        }
        else
        {
            results.Fail("TypedClosure_Create", "handle is IntPtr.Zero");
        }
        return Task.CompletedTask;
    }

    private Task TestTypedClosure_RoundTrip(TestResults results)
    {
        if (_typedClosureSession == null) { results.Fail("TypedClosure_RoundTrip", "no session"); return Task.CompletedTask; }

        TypedClosureState.Reset();

        // Invoke with positive value — should return true (1)
        var result = TestHelpers.TypedClosureView_InvokeClosure(_typedClosureSession.Handle, 42);
        TestLogger.Info($"InvokeClosure(42) returned: {result}, C# callback count: {TypedClosureState.CallCount}");

        if (TypedClosureState.CallCount != 1)
        {
            results.Fail("TypedClosure_RoundTrip", $"callback not fired (count={TypedClosureState.CallCount})");
            return Task.CompletedTask;
        }

        if (TypedClosureState.LastArgValue != 42)
        {
            results.Fail("TypedClosure_RoundTrip", $"arg mismatch: expected 42, got {TypedClosureState.LastArgValue}");
            return Task.CompletedTask;
        }

        if (result == 1) // true
            results.Pass("TypedClosure_RoundTrip (42 -> true -> 1)");
        else
            results.Fail("TypedClosure_RoundTrip", $"expected 1, got {result}");

        return Task.CompletedTask;
    }

    #endregion

    #region Test Implementations — MultiArgClosure

    private unsafe Task TestMultiArgClosure_Create(TestResults results)
    {
        TestLogger.Info("Creating MultiArgClosureView session with (Int32, Bool) -> Void closure...");

        MultiArgClosureState.Reset();

        delegate* unmanaged[Cdecl]<int, int, IntPtr, void> callbackPtr = &MultiArgClosureState.OnEventCallback;
        var handle = NativeMethods.MultiArgClosureView_Create((IntPtr)callbackPtr, IntPtr.Zero);

        if (handle != IntPtr.Zero)
        {
            _multiArgClosureSession = new BridgeSession(handle, "MultiArgClosureView", NativeMethods.MultiArgClosureView_Free);
            results.Pass("MultiArgClosure_Create (handle != 0)");
        }
        else
        {
            results.Fail("MultiArgClosure_Create", "handle is IntPtr.Zero");
        }
        return Task.CompletedTask;
    }

    private Task TestMultiArgClosure_RoundTrip(TestResults results)
    {
        if (_multiArgClosureSession == null) { results.Fail("MultiArgClosure_RoundTrip", "no session"); return Task.CompletedTask; }

        MultiArgClosureState.Reset();

        var result = TestHelpers.MultiArgClosureView_InvokeClosure(_multiArgClosureSession.Handle, 7, 1); // 1 = true
        TestLogger.Info($"InvokeClosure(7, true) returned: {result}, C# callback count: {MultiArgClosureState.CallCount}");

        if (MultiArgClosureState.CallCount != 1)
        {
            results.Fail("MultiArgClosure_RoundTrip", $"callback not fired (count={MultiArgClosureState.CallCount})");
            return Task.CompletedTask;
        }

        if (MultiArgClosureState.LastVal != 7)
        {
            results.Fail("MultiArgClosure_RoundTrip", $"val mismatch: expected 7, got {MultiArgClosureState.LastVal}");
            return Task.CompletedTask;
        }

        if (!MultiArgClosureState.LastFlag)
        {
            results.Fail("MultiArgClosure_RoundTrip", "flag mismatch: expected true, got false");
            return Task.CompletedTask;
        }

        if (result == 1)
            results.Pass("MultiArgClosure_RoundTrip (7, true -> callback fired)");
        else
            results.Fail("MultiArgClosure_RoundTrip", $"expected 1, got {result}");

        return Task.CompletedTask;
    }

    #endregion

    #region Test Implementations — MixedParam

    private unsafe Task TestMixedParam_Create(TestResults results)
    {
        TestLogger.Info("Creating MixedParamView session with style=1, count=42...");

        MixedActionState.Reset();

        delegate* unmanaged[Cdecl]<IntPtr, void> callbackPtr = &MixedActionState.OnActionCallback;
        var handle = NativeMethods.MixedParamView_Create(1, (IntPtr)callbackPtr, IntPtr.Zero, 42);

        if (handle != IntPtr.Zero)
        {
            _mixedSession = new BridgeSession(handle, "MixedParamView", NativeMethods.MixedParamView_Free);
            results.Pass("MixedParam_Create (handle != 0)");
        }
        else
        {
            results.Fail("MixedParam_Create", "handle is IntPtr.Zero");
        }
        return Task.CompletedTask;
    }

    private Task TestMixedParam_ValuesRoundTrip(TestResults results)
    {
        if (_mixedSession == null) { results.Fail("MixedParam_ValuesRoundTrip", "no session"); return Task.CompletedTask; }

        var style = TestHelpers.MixedParamView_GetStyle(_mixedSession.Handle);
        TestLogger.Info($"GetStyle returned: {style}");

        if (style == 1)
            results.Pass("MixedParam_ValuesRoundTrip: style == 1");
        else
            results.Fail("MixedParam_ValuesRoundTrip", $"style: expected 1, got {style}");

        var count = TestHelpers.MixedParamView_GetCount(_mixedSession.Handle);
        TestLogger.Info($"GetCount returned: {count}");

        if (count == 42)
            results.Pass("MixedParam_ValuesRoundTrip: count == 42");
        else
            results.Fail("MixedParam_ValuesRoundTrip", $"count: expected 42, got {count}");

        return Task.CompletedTask;
    }

    private async Task TestMixedParam_CallbackRoundTrip(TestResults results)
    {
        if (_mixedSession == null) { results.Fail("MixedParam_CallbackRoundTrip", "no session"); return; }

        var beforeCount = MixedActionState.CallCount;
        TestLogger.Info($"Before FireAction: callbackCount={beforeCount}");

        // FireAction invokes rootView.onAction() which goes through
        // the generated wrapper: DispatchQueue.main.async { cb_onAction?(ud_onAction) }
        TestHelpers.MixedParamView_FireAction(_mixedSession.Handle);

        // The generated bridge dispatches onAction asynchronously on main queue,
        // so give it a moment to arrive.
        await Task.Delay(500);

        var afterCount = MixedActionState.CallCount;
        TestLogger.Info($"After FireAction: callbackCount={afterCount}");

        if (afterCount == beforeCount + 1)
            results.Pass("MixedParam_CallbackRoundTrip: onAction fired");
        else
            results.Fail("MixedParam_CallbackRoundTrip", $"expected count {beforeCount + 1}, got {afterCount}");
    }

    #endregion

    #region Test Implementations — OptionalEnum

    private Task TestOptionalEnum_WithValue(TestResults results)
    {
        TestLogger.Info("Creating OptionalEnumView with style=2 (error)...");

        var handle = NativeMethods.OptionalEnumView_Create(1, 2); // hasValue=1, value=2
        if (handle == IntPtr.Zero)
        {
            results.Fail("OptionalEnum_WithValue", "handle is IntPtr.Zero");
            return Task.CompletedTask;
        }

        _optEnumWithValueSession = new BridgeSession(handle, "OptionalEnumView_WithValue", NativeMethods.OptionalEnumView_Free);
        results.Pass("OptionalEnum_WithValue: created");

        var hasValue = TestHelpers.OptionalEnumView_HasValue(handle);
        TestLogger.Info($"HasValue returned: {hasValue}");
        if (hasValue == 1)
            results.Pass("OptionalEnum_WithValue: hasValue == 1");
        else
            results.Fail("OptionalEnum_WithValue", $"expected hasValue=1, got {hasValue}");

        var style = TestHelpers.OptionalEnumView_GetStyle(handle);
        TestLogger.Info($"GetStyle returned: {style}");
        if (style == 2)
            results.Pass("OptionalEnum_WithValue: style == 2");
        else
            results.Fail("OptionalEnum_WithValue", $"expected style=2, got {style}");

        return Task.CompletedTask;
    }

    private Task TestOptionalEnum_Nil(TestResults results)
    {
        TestLogger.Info("Creating OptionalEnumView with nil...");

        var handle = NativeMethods.OptionalEnumView_Create(0, 0); // hasValue=0, value=0 (ignored)
        if (handle == IntPtr.Zero)
        {
            results.Fail("OptionalEnum_Nil", "handle is IntPtr.Zero");
            return Task.CompletedTask;
        }

        _optEnumNilSession = new BridgeSession(handle, "OptionalEnumView_Nil", NativeMethods.OptionalEnumView_Free);
        results.Pass("OptionalEnum_Nil: created");

        var hasValue = TestHelpers.OptionalEnumView_HasValue(handle);
        TestLogger.Info($"HasValue returned: {hasValue}");
        if (hasValue == 0)
            results.Pass("OptionalEnum_Nil: hasValue == 0 (nil)");
        else
            results.Fail("OptionalEnum_Nil", $"expected hasValue=0, got {hasValue}");

        return Task.CompletedTask;
    }

    #endregion

    #region Test Implementations — OptionalClass

    private Task TestOptionalClass_WithValue(TestResults results)
    {
        TestLogger.Info("Creating OptionalClassView with model(value=77)...");

        _optClassModelPtr = TestHelpers.CreateSimpleModel(77);
        var handle = NativeMethods.OptionalClassView_Create(_optClassModelPtr);
        if (handle == IntPtr.Zero)
        {
            results.Fail("OptionalClass_WithValue", "handle is IntPtr.Zero");
            return Task.CompletedTask;
        }

        _optClassWithValueSession = new BridgeSession(handle, "OptionalClassView_WithValue", NativeMethods.OptionalClassView_Free);
        results.Pass("OptionalClass_WithValue: created");

        var hasValue = TestHelpers.OptionalClassView_HasValue(handle);
        TestLogger.Info($"HasValue returned: {hasValue}");
        if (hasValue == 1)
            results.Pass("OptionalClass_WithValue: hasValue == 1");
        else
            results.Fail("OptionalClass_WithValue", $"expected hasValue=1, got {hasValue}");

        var modelValue = TestHelpers.OptionalClassView_GetModelValue(handle);
        TestLogger.Info($"GetModelValue returned: {modelValue}");
        if (modelValue == 77)
            results.Pass("OptionalClass_WithValue: modelValue == 77");
        else
            results.Fail("OptionalClass_WithValue", $"expected 77, got {modelValue}");

        return Task.CompletedTask;
    }

    private Task TestOptionalClass_Nil(TestResults results)
    {
        TestLogger.Info("Creating OptionalClassView with nil...");

        var handle = NativeMethods.OptionalClassView_Create(IntPtr.Zero);
        if (handle == IntPtr.Zero)
        {
            results.Fail("OptionalClass_Nil", "handle is IntPtr.Zero");
            return Task.CompletedTask;
        }

        _optClassNilSession = new BridgeSession(handle, "OptionalClassView_Nil", NativeMethods.OptionalClassView_Free);
        results.Pass("OptionalClass_Nil: created");

        var hasValue = TestHelpers.OptionalClassView_HasValue(handle);
        TestLogger.Info($"HasValue returned: {hasValue}");
        if (hasValue == 0)
            results.Pass("OptionalClass_Nil: hasValue == 0 (nil)");
        else
            results.Fail("OptionalClass_Nil", $"expected hasValue=0, got {hasValue}");

        return Task.CompletedTask;
    }

    #endregion

    #region Test Implementations — Cleanup

    private Task TestCleanup(TestResults results)
    {
        var sessions = new[]
        {
            _enumSession, _classSession, _typedClosureSession,
            _multiArgClosureSession, _mixedSession,
            _optEnumWithValueSession, _optEnumNilSession,
            _optClassWithValueSession, _optClassNilSession
        };

        int disposed = 0;
        int odeFired = 0;

        foreach (var session in sessions)
        {
            if (session == null) continue;

            session.Dispose();
            disposed++;

            try
            {
                _ = session.Handle;
                results.Fail("Cleanup", $"Expected ObjectDisposedException for {session}");
            }
            catch (ObjectDisposedException)
            {
                odeFired++;
            }
        }

        // Free model pointers
        if (_classModelPtr != IntPtr.Zero)
        {
            TestHelpers.FreeSimpleModel(_classModelPtr);
            _classModelPtr = IntPtr.Zero;
        }
        if (_optClassModelPtr != IntPtr.Zero)
        {
            TestHelpers.FreeSimpleModel(_optClassModelPtr);
            _optClassModelPtr = IntPtr.Zero;
        }

        TestLogger.Info($"Disposed {disposed} sessions, {odeFired} threw ObjectDisposedException");
        results.Pass($"Cleanup: {disposed} sessions disposed, {odeFired} ODE fired");

        return Task.CompletedTask;
    }

    #endregion
}

#endregion
