// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.InteropServices;
using Foundation;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;
using UIKit;

namespace RuntimeTestsApp.TvOS;

// Minimal tvOS host. Unlike the iOS host, this runner has no interactive UI:
// it launches UIApplication, runs the full test suite on a background task from
// FinishedLaunching, and writes results/markers to Console.WriteLine. The
// Nuke-side harness captures stdout via `xcrun simctl launch --console`, looks
// for "TEST SUCCESS"/"TEST FAILURE", and reaps the process. The tvOS simulator
// still requires a proper UIApplication lifecycle — we just keep the view
// hierarchy to a bare empty root so focus routing doesn't matter.

public class Application
{
    internal static TestPlatform Platform { get; private set; } = TestPlatform.Simulator;
    internal static bool FlakeDetect { get; private set; }
    internal static string? ClassFilter { get; private set; }
    internal static HashSet<string> ExcludeClasses { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    static void Main(string[] args)
    {
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
        }

        SwiftFrameworkResolver.RegisterForAssembly(Assembly.GetExecutingAssembly());

        UIApplication.Main(effectiveArgs, null, typeof(AppDelegate));
    }

    static string[] GetProcessInfoArgs()
    {
        var allArgs = NSProcessInfo.ProcessInfo.Arguments;
        if (allArgs.Length <= 1)
            return Array.Empty<string>();
        return allArgs.Skip(1).ToArray();
    }
}

public class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        Window = new UIWindow(UIScreen.MainScreen.Bounds);
        Window.RootViewController = new UIViewController();
        Window.MakeKeyAndVisible();

        _ = Task.Run(async () =>
        {
            try
            {
                await RunTestsAsync();
            }
            catch (Exception ex)
            {
                TestLogger.Exception(ex, "Test suite failed");
                Console.WriteLine("RESULTS FLUSHED");
                Console.WriteLine($"TEST FAILURE: {ex.Message}");
            }
        });

        return true;
    }

    static async Task RunTestsAsync()
    {
        var platform = Application.Platform;

        TestLogger.Clear();

        TestLogger.Info("=== RUNTIME TESTS (tvOS Simulator) ===");
        TestLogger.Info($"Platform: {platform}");
        if (Application.ClassFilter != null)
            TestLogger.Info($"Class filter: {Application.ClassFilter}");
        if (Application.ExcludeClasses.Count > 0)
            TestLogger.Info($"Excluding {Application.ExcludeClasses.Count} classes: {string.Join(", ", Application.ExcludeClasses)}");
        TestLogger.Info($"Started at {DateTime.Now:HH:mm:ss}");

        var results = new TestResults();

        var jsonlPath = GetJsonlOutputPath();
        TestLogger.Info($"JSONL output: {jsonlPath}");
        results.InitializeJsonl(jsonlPath);

        try
        {
            InitializeSwiftConcurrency();

            var allClasses = TestRegistry.Classes;

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
                    return;
                }

                allClasses = filtered;
            }

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
                TestLogger.Info("Flake detection ENABLED: each test runs 3x");

            foreach (var descriptor in allClasses)
            {
                results.BeginClass(descriptor.Name);
                await RunTestClassAsync(descriptor, results, platform, flakeDetect);
                NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(0.001));
            }
            results.EndClass();
        }
        catch (Exception ex)
        {
            TestLogger.Exception(ex, "Test suite failed");
            results.Fail("Test Suite", ex.Message);
        }

        results.FinalizeJsonl();

        TestLogger.Info("");
        TestLogger.Info("=== TEST SUMMARY ===");
        TestLogger.Info(results.ToString());
        TestLogger.Info($"Total duration: {TestLogger.Elapsed.TotalSeconds:F1}s");

        Console.WriteLine("RESULTS FLUSHED");
        Console.WriteLine(TestLogger.GetFullLog());

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
    }

    static async Task RunTestClassAsync(TestClassDescriptor descriptor, TestResults results, TestPlatform platform, bool flakeDetect = false)
    {
        TestLogger.Info("");
        TestLogger.Info($"=== {descriptor.Name} ===");

        if (descriptor.SkipReason != null)
        {
            foreach (var m in descriptor.Methods)
                results.Skip($"{descriptor.Name}.{m.Name}", descriptor.SkipReason);
            return;
        }

        if (descriptor.SkipOnSimulator != null && platform == TestPlatform.Simulator)
        {
            foreach (var m in descriptor.Methods)
                results.Skip($"{descriptor.Name}.{m.Name}", $"Simulator: {descriptor.SkipOnSimulator}");
            return;
        }

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

    static string GetJsonlOutputPath()
    {
        var documentsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(documentsDir) && Directory.Exists(documentsDir))
            return Path.Combine(documentsDir, "test-results.jsonl");

        return Path.Combine(Directory.GetCurrentDirectory(), "test-results.jsonl");
    }

    static void InitializeSwiftConcurrency()
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
