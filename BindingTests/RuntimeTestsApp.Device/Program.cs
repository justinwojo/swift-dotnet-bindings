// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
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
    internal static TestPlatform Platform { get; private set; } = TestPlatform.Device;
    internal static bool FlakeDetect { get; private set; }
    internal static string? ClassFilter { get; private set; }

    static void Main(string[] args)
    {
        var effectiveArgs = args.Length > 0 ? args : GetProcessInfoArgs();
        for (int i = 0; i < effectiveArgs.Length; i++)
        {
            if (effectiveArgs[i] == "--platform" && i + 1 < effectiveArgs.Length)
            {
                Platform = effectiveArgs[i + 1].ToLowerInvariant() switch
                {
                    "simulator" => TestPlatform.Simulator,
                    _ => TestPlatform.Device
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

        SwiftFrameworkResolver.RegisterForAssembly(Assembly.GetExecutingAssembly());

        UIApplication.Main(args, null, typeof(AppDelegate));
    }

    static string[] GetProcessInfoArgs()
    {
        var allArgs = NSProcessInfo.ProcessInfo.Arguments;
        if (allArgs.Length <= 1)
            return Array.Empty<string>();
        return allArgs.Skip(1).ToArray();
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

        _scrollView = new UIScrollView
        {
            Frame = new CoreGraphics.CGRect(20, 60, screenBounds.Width - 40, screenBounds.Height - 80),
            BackgroundColor = UIColor.FromRGB(245, 245, 245),
        };

        _resultLabel = new UILabel
        {
            Text = "Running tests...",
            TextAlignment = UITextAlignment.Left,
            Lines = 0,
            Font = UIFont.FromName("Menlo", 9) ?? UIFont.SystemFontOfSize(9),
            Frame = new CoreGraphics.CGRect(8, 8, screenBounds.Width - 56, screenBounds.Height - 96)
        };
        _scrollView.AddSubview(_resultLabel);
        View.AddSubview(_scrollView);

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
            try
            {
                SwiftBindingsTestLib.Functions.InitializeConcurrency();
                TestLogger.Info("Swift concurrency initialized");
            }
            catch (Exception ex)
            {
                TestLogger.Warning($"Failed to initialize Swift concurrency: {ex.Message}");
            }

            // Diagnostic: log assembly info
            var asm = Assembly.GetExecutingAssembly();
            TestLogger.Info($"Assembly: {asm.FullName}");

            Type[] allTypes;
            try
            {
                allTypes = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException rtle)
            {
                TestLogger.Warning($"ReflectionTypeLoadException: {rtle.LoaderExceptions.Length} loader errors");
                foreach (var le in rtle.LoaderExceptions.Where(e => e != null).Take(10))
                    TestLogger.Warning($"  Loader: {le!.Message}");
                allTypes = rtle.Types.Where(t => t != null).ToArray()!;
            }

            TestLogger.Info($"Total types in assembly: {allTypes.Length}");

            var testBaseType = typeof(TestBase);
            TestLogger.Info($"TestBase type: {testBaseType.FullName} from {testBaseType.Assembly.FullName}");

            var allClasses = allTypes
                .Where(t => t.IsSubclassOf(testBaseType) && !t.IsAbstract)
                .ToList();

            TestLogger.Info($"Discovered {allClasses.Count} test classes");

            if (allClasses.Count == 0)
            {
                // Extra diagnostics
                var candidates = allTypes.Where(t => t.BaseType != null && t.BaseType.Name == "TestBase").ToList();
                TestLogger.Warning($"Types with BaseType.Name=='TestBase': {candidates.Count}");
                foreach (var c in candidates.Take(5))
                    TestLogger.Warning($"  {c.FullName} base={c.BaseType?.FullName} asm={c.BaseType?.Assembly.FullName}");
            }

            // Apply --class filter if specified
            if (Application.ClassFilter != null)
            {
                allClasses = allClasses
                    .Where(t => t.Name.Equals(Application.ClassFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var testClasses = allClasses.OrderBy(t => t.Name).ToList();

            var flakeDetect = Application.FlakeDetect;

            foreach (var testClass in testClasses)
            {
                await RunTestClassAsync(testClass, results, platform, flakeDetect);
            }
        }
        catch (Exception ex)
        {
            TestLogger.Exception(ex, "Test suite failed");
            results.Fail("Test Suite", ex.Message);
        }

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
                TestLogger.Error($"  - {failed}");
        }

        UpdateResultLabel(TestLogger.GetFullLog());
    }

    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "Test runner")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Test runner")]
    private async Task RunTestClassAsync(Type testClassType, TestResults results, TestPlatform platform, bool flakeDetect = false)
    {
        TestLogger.Info("");
        TestLogger.Info($"=== {testClassType.Name} ===");

        try
        {
            var testClass = (TestBase)Activator.CreateInstance(testClassType, results)!;
            await testClass.RunAllTestsAsync(platform, flakeDetect);
        }
        catch (Exception ex)
        {
            TestLogger.Exception(ex, testClassType.Name);
            results.Fail(testClassType.Name, ex.Message);
        }
    }
}

#endregion
