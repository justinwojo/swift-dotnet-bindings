// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
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

    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "Test runner discovers test classes by reflection")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Test runner discovers test classes by reflection")]
    static async Task<int> RunTestsAsync()
    {
        TestLogger.Clear();

        TestLogger.Info("=== RUNTIME TESTS (macOS) ===");
        TestLogger.Info($"Platform: {Platform}");
        if (ClassFilter != null)
            TestLogger.Info($"Class filter: {ClassFilter}");
        TestLogger.Info($"Started at {DateTime.Now:HH:mm:ss}");

        var results = new TestResults();

        try
        {
            // Initialize Swift concurrency runtime
            InitializeSwiftConcurrency();

            // Discover all TestBase subclasses in the assembly
            var allClasses = Assembly.GetExecutingAssembly()
                .GetTypes()
                .Where(t => t.IsSubclassOf(typeof(TestBase)) && !t.IsAbstract)
                .ToList();

            // Apply --class filter if specified
            if (ClassFilter != null)
            {
                var filtered = allClasses
                    .Where(t => t.Name.Equals(ClassFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (filtered.Count == 0)
                {
                    TestLogger.Error($"No test class matches '{ClassFilter}'");
                    TestLogger.Error("Available classes:");
                    foreach (var c in allClasses.OrderBy(t => t.Name))
                        TestLogger.Error($"  - {c.Name}");
                    Console.WriteLine("TEST FAILURE: No test class matches filter");
                    return 1;
                }

                allClasses = filtered;
            }

            // Sort all test classes alphabetically
            var testClasses = allClasses.OrderBy(t => t.Name).ToList();

            var flakeDetect = FlakeDetect;
            if (flakeDetect)
            {
                TestLogger.Info("Flake detection ENABLED: each test runs 3x");
            }

            foreach (var testClass in testClasses)
            {
                await RunTestClassAsync(testClass, results, Platform, flakeDetect);
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

    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "Test runner discovers test classes by reflection")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Test runner discovers test classes by reflection")]
    static async Task RunTestClassAsync(Type testClassType, TestResults results, TestPlatform platform, bool flakeDetect = false)
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
