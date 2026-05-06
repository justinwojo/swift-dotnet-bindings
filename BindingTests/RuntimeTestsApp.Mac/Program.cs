// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.InteropServices;
using Foundation;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Mac;

/// <summary>
/// macOS console-based test runner. Same reflection-based discovery as the iOS
/// RuntimeTestsApp but without UIKit — runs directly on macOS with an NSRunLoop
/// on the main thread to service GCD dispatch sources for Swift async callbacks.
/// </summary>
public class Program
{
    static TestPlatform Platform = TestPlatform.Simulator;
    static bool FlakeDetect;
    static string? ClassFilter;
    static string? ResultsPath;
    static HashSet<string> ExcludeClasses = new(StringComparer.OrdinalIgnoreCase);

    static int Main(string[] args)
    {
        // Parse arguments directly (no NSProcessInfo needed on macOS)
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--platform" && i + 1 < args.Length)
            {
                Platform = args[i + 1].ToLowerInvariant() switch
                {
                    "device" => TestPlatform.Device,
                    _ => TestPlatform.Simulator
                };
                i++;
            }
            else if (args[i] == "--flake-detect")
            {
                FlakeDetect = true;
            }
            else if (args[i] == "--lifetime")
            {
                TestRunFlags.Lifetime = true;
            }
            else if (args[i] == "--class" && i + 1 < args.Length)
            {
                ClassFilter = args[i + 1];
                i++;
            }
            else if (args[i] == "--exclude-classes" && i + 1 < args.Length)
            {
                foreach (var name in args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    ExcludeClasses.Add(name.Trim());
                i++;
            }
            else if (args[i] == "--results-path" && i + 1 < args.Length)
            {
                ResultsPath = args[i + 1];
                i++;
            }
        }

        // Register resolver for bundled frameworks BEFORE any Swift types are accessed.
        SwiftFrameworkResolver.RegisterForAssembly(Assembly.GetExecutingAssembly());

        // Run tests on a background thread while pumping the main thread's NSRunLoop.
        // Swift async continuations dispatch via GCD, which requires an active run loop
        // on the main thread to service dispatch sources and timers. Without this,
        // Swift Task.sleep and async callbacks never complete, causing 5s timeouts.
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task.Run(async () =>
        {
            try
            {
                completion.SetResult(await RunTestsAsync());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        while (!completion.Task.IsCompleted)
        {
            NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(0.01));
        }

        return completion.Task.GetAwaiter().GetResult();
    }

    static async Task<int> RunTestsAsync()
    {
        TestLogger.Clear();

        TestLogger.Info("=== RUNTIME TESTS (macOS) ===");
        TestLogger.Info($"Platform: {Platform}");
        if (ClassFilter != null)
            TestLogger.Info($"Class filter: {ClassFilter}");
        TestLogger.Info($"Started at {DateTime.Now:HH:mm:ss}");

        var results = new TestResults();

        // Initialize JSONL output for crash-safe structured results.
        // Use --results-path if provided (Nuke passes a path outside the .app bundle
        // so writing JSONL doesn't invalidate the code signature seal). Fall back to
        // CWD for manual invocation.
        var jsonlPath = ResultsPath != null
            ? Path.Combine(ResultsPath, "test-results.jsonl")
            : Path.Combine(Directory.GetCurrentDirectory(), "test-results.jsonl");
        TestLogger.Info($"JSONL output: {jsonlPath}");
        results.InitializeJsonl(jsonlPath);

        try
        {
            // Initialize Swift concurrency runtime
            InitializeSwiftConcurrency();

            // Use compile-time discovered test classes (source generator)
            var allClasses = TestRegistry.Classes;

            // Apply --class filter if specified
            if (ClassFilter != null)
            {
                var filtered = allClasses
                    .Where(c => c.Name.Equals(ClassFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (filtered.Count == 0)
                {
                    TestLogger.Error($"No test class matches '{ClassFilter}'");
                    TestLogger.Error("Available classes:");
                    foreach (var c in allClasses.OrderBy(c => c.Name))
                        TestLogger.Error($"  - {c.Name}");
                    results.FinalizeJsonl();
                    Console.WriteLine("RESULTS FLUSHED");
                    Console.WriteLine("TEST FAILURE: No test class matches filter");
                    return 1;
                }

                allClasses = filtered;
            }

            if (ExcludeClasses.Count > 0)
            {
                var before = allClasses.Count;
                allClasses = allClasses
                    .Where(c => !ExcludeClasses.Contains(c.Name))
                    .ToList();
                TestLogger.Info($"Excluded {before - allClasses.Count} classes, {allClasses.Count} remaining");
            }

            var flakeDetect = FlakeDetect;
            if (flakeDetect)
            {
                TestLogger.Info("Flake detection ENABLED: each test runs 3x");
            }

            foreach (var descriptor in allClasses)
            {
                results.BeginClass(descriptor.Name);
                await RunTestClassAsync(descriptor, results, Platform, flakeDetect);
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
            return 0;
        }
        else
        {
            Console.WriteLine($"TEST FAILURE: {results.Failed} tests failed");
            TestLogger.Error("=== SOME TESTS FAILED ===");
            foreach (var failed in results.FailedTests)
            {
                TestLogger.Error($"  - {failed}");
            }
            return 1;
        }
    }

    static async Task RunTestClassAsync(TestClassDescriptor descriptor, TestResults results, TestPlatform platform, bool flakeDetect = false)
    {
        TestLogger.Info("");
        TestLogger.Info($"=== {descriptor.Name} ===");

        // Check class-level skip BEFORE instantiation
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

    static IntPtr ResolveBundledFramework(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == "SwiftBindingsTestLib" || libraryName == "SwiftBindings"
            || libraryName == "SwiftBindingsTestLibBridge")
        {
            // On macOS, try @rpath first, then direct dylib load
            var frameworkPath = $"@rpath/{libraryName}.framework/{libraryName}";
            if (NativeLibrary.TryLoad(frameworkPath, out var handle))
            {
                TestLogger.Info($"Resolved {libraryName} -> {frameworkPath}");
                return handle;
            }

            // Try direct dylib in current directory
            var dylibPath = $"lib{libraryName}.dylib";
            if (NativeLibrary.TryLoad(dylibPath, out handle))
            {
                TestLogger.Info($"Resolved {libraryName} -> {dylibPath}");
                return handle;
            }

            TestLogger.Warning($"Failed to resolve {libraryName}");
        }
        return IntPtr.Zero;
    }
}
