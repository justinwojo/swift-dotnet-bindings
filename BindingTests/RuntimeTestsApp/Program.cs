// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Swift.ActivityKit;
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

    /// <summary>
    /// When true, skip the test suite and instead start one Live Activity from the
    /// .NET ActivityKit facade and leave it on screen (no End). Used by the visual
    /// lock-screen pixel proof: launch with --persist-activity, then look at the
    /// device's lock screen to confirm the .NET-driven activity renders via the
    /// embedded SwiftUI widget. Enabled via the --persist-activity CLI arg.
    /// </summary>
    internal static bool PersistActivity { get; private set; }

    /// <summary>
    /// When set, these test classes are excluded from the run (used by resume-on-crash orchestration).
    /// </summary>
    internal static HashSet<string> ExcludeClasses { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

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
            else if (effectiveArgs[i] == "--persist-activity")
            {
                PersistActivity = true;
            }
            else if (effectiveArgs[i] == "--lifetime")
            {
                TestRunFlags.Lifetime = true;
            }
            else if (effectiveArgs[i] == "--class" && i + 1 < effectiveArgs.Length)
            {
                ClassFilter = effectiveArgs[i + 1];
                i++;
            }
            else if (effectiveArgs[i] == "--exclude-classes" && i + 1 < effectiveArgs.Length)
            {
                foreach (var name in effectiveArgs[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    ExcludeClasses.Add(name.Trim());
                i++;
            }
            else if (effectiveArgs[i] == "--run-token" && i + 1 < effectiveArgs.Length)
            {
                TestRunFlags.RunToken = effectiveArgs[i + 1];
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
    private UITextView? _resultTextView;

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
        var bottomMargin = 20.0;

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

        // Result text view — fill remaining screen height, built-in scrolling
        var textViewHeight = screenHeight - currentY - bottomMargin;
        _resultTextView = new UITextView
        {
            Frame = new CoreGraphics.CGRect(20, currentY, contentWidth, textViewHeight),
            BackgroundColor = UIColor.FromRGB(30, 30, 30),
            TextColor = UIColor.FromRGB(220, 220, 220),
            Font = UIFont.FromName("Menlo", 9) ?? UIFont.SystemFontOfSize(9),
            Editable = false,
            Text = "Ready to run tests...",
            TextContainerInset = new UIEdgeInsets(8, 4, 8, 4),
            Layer = { CornerRadius = 8 }
        };
        _resultTextView.ClipsToBounds = true;
        View.AddSubview(_resultTextView);

        // Visual pixel proof: when launched with --persist-activity, skip the test
        // suite and instead start one Live Activity from the .NET facade and leave
        // it running so it renders on the lock screen via the embedded widget.
        // StartPersistentActivityAsync is [SupportedOSPlatform("ios16.2")] +
        // [UnsupportedOSPlatform("maccatalyst")]; gate the call with CanUseLiveActivities,
        // whose *Guard attributes tell CA1416 the call is statically safe when it is true.
        // On an unsupported OS the visual-proof mode falls back to the normal test run.
        if (Application.PersistActivity && CanUseLiveActivities)
            _ = StartPersistentActivityAsync();
        else
            _ = RunTestsAsync();
    }

    // The ActivityKit content API needs iOS 16.2+ and is sliced out of macabi. Inline
    // `!OperatingSystem.IsMacCatalyst()` is not reliably recognized by CA1416's flow
    // analysis (Mac Catalyst inherits the iOS version, so the version check alone narrows
    // it to 16.2 rather than excluding it); the *Guard attributes state the contract
    // explicitly so the analyzer trusts the call site that tests this property.
    [SupportedOSPlatformGuard("ios16.2")]
    [UnsupportedOSPlatformGuard("maccatalyst")]
    private static bool CanUseLiveActivities =>
        OperatingSystem.IsIOSVersionAtLeast(16, 2) && !OperatingSystem.IsMacCatalyst();

    /// <summary>
    /// Waits for the app to reach the foreground-active state, then starts a single
    /// Live Activity through the .NET ActivityKit facade and leaves it on screen.
    /// ActivityKit's request() throws unless the host app is foreground-active, so we
    /// poll for that first (launch transitions through Inactive). The activity is
    /// intentionally never ended — the system keeps rendering it on the lock screen.
    /// </summary>
    // Mirror the LiveActivity facade's platform contract ([SupportedOSPlatform("ios16.2")] +
    // [UnsupportedOSPlatform("maccatalyst")]) onto this method so CA1416 is satisfied at the
    // LiveActivity.* call sites below. The single caller (ViewDidLoad) guards entry with a
    // positive OperatingSystem check the analyzer recognizes.
    [SupportedOSPlatform("ios16.2")]
    [UnsupportedOSPlatform("maccatalyst")]
    private async Task StartPersistentActivityAsync()
    {
        if (!await AppleSupplement.ActivityKitReadiness.WaitForForegroundActiveAsync(TimeSpan.FromSeconds(10)))
        {
            UpdateResultLabel("App never reached foreground-active; cannot start a Live Activity.");
            return;
        }

        try
        {
            if (!LiveActivity.AreActivitiesEnabled)
            {
                UpdateResultLabel("Live Activities are disabled for this app (Settings).");
                return;
            }

            var activity = LiveActivity.Request(
                name: "delivery",
                attributesJson: """{"title":"Order #42"}""",
                contentStateJson: """{"status":"Out for delivery","eta":"12 min"}""");

            UpdateResultLabel(
                $"Live Activity started from .NET (active={activity.IsActive}).\n" +
                "Lock the device and look at the lock screen.");

            // Visibly update the card a few seconds later so the on-screen state
            // change is observable, then leave it running (never End).
            await Task.Delay(6000);
            activity.Update("""{"status":"Arriving now","eta":"1 min"}""");
            UpdateResultLabel(
                "Live Activity updated to 'Arriving now'.\n" +
                "It remains on the lock screen until ended.");
        }
        catch (Exception ex)
        {
            UpdateResultLabel($"Failed to start Live Activity: {ex.Message}");
        }
    }

    private void UpdateResultLabel(string text)
    {
        InvokeOnMainThread(() =>
        {
            _resultTextView!.Text = text;
            // Auto-scroll to bottom to follow live output
            var bottom = _resultTextView.ContentSize.Height - _resultTextView.Bounds.Height;
            if (bottom > 0)
                _resultTextView.SetContentOffset(new CoreGraphics.CGPoint(0, bottom), false);
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
        if (Application.ExcludeClasses.Count > 0)
            TestLogger.Info($"Excluding {Application.ExcludeClasses.Count} classes: {string.Join(", ", Application.ExcludeClasses)}");
        TestLogger.Info($"Started at {DateTime.Now:HH:mm:ss}");

        var results = new TestResults();

        // Initialize JSONL output for crash-safe structured results
        var jsonlPath = GetJsonlOutputPath();
        TestLogger.Info($"JSONL output: {jsonlPath}");
        results.InitializeJsonl(jsonlPath, TestRunFlags.RunToken);

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
                    results.FinalizeJsonl();
                    Console.WriteLine("RESULTS FLUSHED");
                    Console.WriteLine("TEST FAILURE: No test class matches filter");
                    UpdateResultLabel(TestLogger.GetFullLog());
                    return;
                }

                allClasses = filtered;
            }

            // Apply --exclude-classes filter (resume-on-crash orchestration)
            if (Application.ExcludeClasses.Count > 0)
            {
                var before = allClasses.Count;
                allClasses = allClasses
                    .Where(c => !Application.ExcludeClasses.Contains(c.Name))
                    .ToList();
                TestLogger.Info($"Excluded {before - allClasses.Count} classes, {allClasses.Count} remaining");
            }

            var flakeDetect = Application.FlakeDetect;
            if (flakeDetect)
            {
                TestLogger.Info("Flake detection ENABLED: each test runs 3x");
            }

            foreach (var descriptor in allClasses)
            {
                results.BeginClass(descriptor.Name);
                await RunTestClassAsync(descriptor, results, platform, flakeDetect);
                // Yield to the iOS run loop between test classes to reset the watchdog
                // timer. Required on device (NativeAOT) to prevent SIGKILL; harmless on simulator.
                NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(0.001));
            }
            results.EndClass();
        }
        catch (Exception ex)
        {
            TestLogger.Exception(ex, "Test suite failed");
            results.Fail("Test Suite", ex.Message);
        }

        // Finalize JSONL output (writes done record + flushes)
        results.FinalizeJsonl();

        // Summary
        TestLogger.Info("");
        TestLogger.Info("=== TEST SUMMARY ===");
        TestLogger.Info(results.ToString());
        TestLogger.Info($"Total duration: {TestLogger.Elapsed.TotalSeconds:F1}s");

        // Signal that JSONL is fully written before test markers
        Console.WriteLine("RESULTS FLUSHED");

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

    /// <summary>
    /// Returns the JSONL output path: Documents directory on iOS, working directory on macOS.
    /// </summary>
    private static string GetJsonlOutputPath()
    {
        // On iOS, write to the app's Documents directory (survives app termination, accessible via simctl)
        var documentsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(documentsDir) && Directory.Exists(documentsDir))
            return Path.Combine(documentsDir, "test-results.jsonl");

        // Fallback: working directory (macOS)
        return Path.Combine(Directory.GetCurrentDirectory(), "test-results.jsonl");
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
