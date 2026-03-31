// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Mac;

/// <summary>
/// macOS console-based test runner. Same reflection-based discovery as the iOS
/// RuntimeTestsApp but without UIKit/Foundation — runs directly on macOS.
/// </summary>
public class Program
{
    static TestPlatform Platform = TestPlatform.Simulator;
    static bool FlakeDetect;
    static string? ClassFilter;

    static async Task<int> Main(string[] args)
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
            else if (args[i] == "--class" && i + 1 < args.Length)
            {
                ClassFilter = args[i + 1];
                i++;
            }
        }

        // Register resolver for bundled frameworks BEFORE any Swift types are accessed.
        SwiftFrameworkResolver.RegisterForAssembly(Assembly.GetExecutingAssembly());

        return await RunTestsAsync();
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

        // Initialize JSONL output for crash-safe structured results
        var jsonlPath = Path.Combine(Directory.GetCurrentDirectory(), "test-results.jsonl");
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
