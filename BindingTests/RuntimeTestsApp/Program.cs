// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.InteropServices;
using Swift.Runtime;
using Foundation;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using UIKit;

namespace RuntimeTestsApp;

#region Application Entry Point

public class Application
{
    /// <summary>
    /// Target platform: simulator (Mono JIT) or device (NativeAOT).
    /// </summary>
    internal static TestPlatform Platform { get; private set; } = TestPlatform.Simulator;

    /// <summary>
    /// When true, each test runs 3 times and inconsistent results (flaky tests) fail the suite.
    /// Enabled via --flake-detect CLI arg.
    /// </summary>
    internal static bool FlakeDetect { get; private set; }

    /// <summary>
    /// When set, only the test class with this exact name (case-insensitive) is run.
    /// </summary>
    internal static string? ClassFilter { get; private set; }

    static void Main(string[] args)
    {
        // Parse arguments before UI launch.
        // On iOS, Main(string[] args) may not receive simctl launch arguments.
        // Fall back to NSProcessInfo.ProcessInfo.Arguments which always has them.
        var effectiveArgs = args.Length > 0 ? args : GetProcessInfoArgs();
        for (int i = 0; i < effectiveArgs.Length; i++)
        {
            if (effectiveArgs[i] == "--platform" && i + 1 < effectiveArgs.Length)
            {
                Platform = effectiveArgs[i + 1].ToLowerInvariant() switch
                {
                    "device" => TestPlatform.Device,
                    _ => TestPlatform.Simulator
                };
                i++;
            }
            else if (effectiveArgs[i] == "--flake-detect")
            {
                FlakeDetect = true;
            }
            else if (effectiveArgs[i] == "--class" && i + 1 < effectiveArgs.Length)
            {
                ClassFilter = effectiveArgs[i + 1];
                i++;
            }
        }

        // Register resolver for bundled frameworks BEFORE any Swift types are accessed.
        SwiftFrameworkResolver.RegisterForAssembly(Assembly.GetExecutingAssembly());

        UIApplication.Main(args, null, typeof(AppDelegate));
    }

    /// <summary>
    /// Gets arguments from NSProcessInfo (works on iOS when simctl launch passes args).
    /// Skips the first element (executable path).
    /// </summary>
    static string[] GetProcessInfoArgs()
    {
        var allArgs = NSProcessInfo.ProcessInfo.Arguments;
        if (allArgs.Length <= 1)
            return Array.Empty<string>();
        // Skip argv[0] (executable path)
        return allArgs.Skip(1).ToArray();
    }

    static IntPtr ResolveBundledFramework(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == "SwiftBindingsTestLib" || libraryName == "SwiftBindings"
            || libraryName == "SwiftBindingsTestLibBridge")
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

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
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
            Text = $"Runtime Tests ({Application.Platform})",
            TextAlignment = UITextAlignment.Center,
            Font = UIFont.BoldSystemFontOfSize(18),
            Frame = new CoreGraphics.CGRect(20, currentY, contentWidth, titleHeight)
        };
        View.AddSubview(label);
        currentY += titleHeight + spacing;

        // Run Tests button
        var runButton = UIButton.FromType(UIButtonType.System);
        runButton.Frame = new CoreGraphics.CGRect(20, currentY, contentWidth, buttonHeight);
        runButton.SetTitle("Run Tests", UIControlState.Normal);
        runButton.BackgroundColor = UIColor.SystemBlue;
        runButton.SetTitleColor(UIColor.White, UIControlState.Normal);
        runButton.Layer.CornerRadius = 8;
        runButton.TouchUpInside += RunAllTests;
        View.AddSubview(runButton);
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
            Text = "Ready to run tests...",
            TextAlignment = UITextAlignment.Left,
            Lines = 0,
            Font = UIFont.FromName("Menlo", 9) ?? UIFont.SystemFontOfSize(9),
            Frame = new CoreGraphics.CGRect(8, 8, contentWidth - 16, resultLabelHeight - 16)
        };
        _scrollView.AddSubview(_resultLabel);
        View.AddSubview(_scrollView);

        // Auto-run tests on startup
        _ = RunTestsAsync();
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

    private async void RunAllTests(object? sender, EventArgs e)
    {
        await RunTestsAsync();
    }

    private async Task RunTestsAsync()
    {
        var platform = Application.Platform;

        TestLogger.Clear();

        TestLogger.Info("=== RUNTIME TESTS ===");
        TestLogger.Info($"Platform: {platform}");
        if (Application.ClassFilter != null)
            TestLogger.Info($"Class filter: {Application.ClassFilter}");
        TestLogger.Info($"Started at {DateTime.Now:HH:mm:ss}");

        var results = new TestResults();

        try
        {
            // Initialize Swift concurrency runtime
            InitializeSwiftConcurrency();

            // Use compile-time discovered test classes (source generator)
            var allClasses = TestRegistry.Classes;

            // Apply --class filter if specified
            if (Application.ClassFilter != null)
            {
                var filtered = allClasses
                    .Where(c => c.Name.Equals(Application.ClassFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (filtered.Count == 0)
                {
                    TestLogger.Error($"No test class matches '{Application.ClassFilter}'");
                    TestLogger.Error("Available classes:");
                    foreach (var c in allClasses.OrderBy(c => c.Name))
                        TestLogger.Error($"  - {c.Name}");
                    Console.WriteLine("TEST FAILURE: No test class matches filter");
                    UpdateResultLabel(TestLogger.GetFullLog());
                    return;
                }

                allClasses = filtered;
            }

            var flakeDetect = Application.FlakeDetect;
            if (flakeDetect)
            {
                TestLogger.Info("Flake detection ENABLED: each test runs 3x");
            }

            foreach (var descriptor in allClasses)
            {
                await RunTestClassAsync(descriptor, results, platform, flakeDetect);
            }
        }
        catch (Exception ex)
        {
            TestLogger.Exception(ex, "Test suite failed");
            results.Fail("Test Suite", ex.Message);
        }

        // Summary
        TestLogger.Info("");
        TestLogger.Info("=== TEST SUMMARY ===");
        TestLogger.Info(results.ToString());
        TestLogger.Info($"Total duration: {TestLogger.Elapsed.TotalSeconds:F1}s");

        if (results.AllPassed)
        {
            Console.WriteLine("TEST SUCCESS");
            TestLogger.Success("=== ALL TESTS PASSED ===");
        }
        else
        {
            Console.WriteLine($"TEST FAILURE: {results.Failed} tests failed");
            TestLogger.Error("=== SOME TESTS FAILED ===");
            foreach (var failed in results.FailedTests)
            {
                TestLogger.Error($"  - {failed}");
            }
        }

        UpdateResultLabel(TestLogger.GetFullLog());
    }

    // Classes that crash NativeAOT during type loading (before any test runs).
    // Source generator eliminates reflection, but instantiation can still trigger
    // static field initialization that crashes on NativeAOT.
    private static readonly HashSet<string> _nativeAotCrashClasses = new()
    {
        "TupleMarshallingTests",  // SIGSEGV: ValueTuple method signatures not resolvable
    };

    private async Task RunTestClassAsync(TestClassDescriptor descriptor, TestResults results, TestPlatform platform, bool flakeDetect = false)
    {
        TestLogger.Info("");
        TestLogger.Info($"=== {descriptor.Name} ===");

        // NativeAOT: Some classes crash during instantiation (static field initialization).
        if (platform == TestPlatform.Device && _nativeAotCrashClasses.Contains(descriptor.Name))
        {
            foreach (var m in descriptor.Methods)
                results.Skip($"{descriptor.Name}.{m.Name}", "NativeAOT: class loading crashes process");
            return;
        }

        // Check class-level [Skip] BEFORE instantiation — factory call triggers
        // static field initialization which can SIGSEGV on NativeAOT.
        if (descriptor.SkipReason != null)
        {
            foreach (var m in descriptor.Methods)
                results.Skip($"{descriptor.Name}.{m.Name}", descriptor.SkipReason);
            return;
        }

        // Check class-level [SkipOnSimulator] BEFORE instantiation — static field
        // initialization for generic types calls CallConvSwift P/Invokes that crash Mono JIT.
        if (descriptor.SkipOnSimulator != null && platform == TestPlatform.Simulator)
        {
            foreach (var m in descriptor.Methods)
                results.Skip($"{descriptor.Name}.{m.Name}", $"Simulator: {descriptor.SkipOnSimulator}");
            return;
        }

        // Check class-level [SkipOnDevice] BEFORE instantiation
        if (descriptor.SkipOnDevice != null && platform == TestPlatform.Device)
        {
            foreach (var m in descriptor.Methods)
                results.Skip($"{descriptor.Name}.{m.Name}", $"Device: {descriptor.SkipOnDevice}");
            return;
        }

        try
        {
            var testClass = descriptor.Factory(results);
            await testClass.RunAllTestsAsync(descriptor, platform, flakeDetect);
        }
        catch (Exception ex)
        {
            TestLogger.Exception(ex, descriptor.Name);
            results.Fail(descriptor.Name, ex.Message);
        }
    }

    private void InitializeSwiftConcurrency()
    {
        try
        {
            SwiftBindingsTestLib.Functions.InitializeConcurrency();
            TestLogger.Info("Swift concurrency initialized");
        }
        catch (Exception ex)
        {
            TestLogger.Warning($"Failed to initialize Swift concurrency: {ex.Message}");
        }
    }
}

#endregion
