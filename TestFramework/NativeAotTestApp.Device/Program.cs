// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// iOS NativeAOT device test app.
// Runs on physical iPhone via `dotnet publish` + `xcrun devicectl`.
// Same test matrix as NativeAotTestApp (simulator) but targets ios-arm64.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using System.Text;
using Foundation;
using Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using SwiftBindingsTestLib;
using UIKit;

namespace NativeAotTestApp.Device;

#region Test Dispatch

public static class TestDispatcher
{
    public static void RunTest(string testId)
    {
        Console.WriteLine($"--- Running test: {testId} ---");

        try
        {
            switch (testId)
            {
                // Blocker 1: JIT assertion (must-pass under NativeAOT)
                case "b1-string-create":
                    B1_StringCreate();
                    break;
                case "b1-string-length":
                    B1_StringLength();
                    break;
                case "b1-string-wrapper":
                    B1_StringWrapper();
                    break;
                case "b1-existential":
                    B1_ExistentialMetadata();
                    break;
                case "b1-generated-binding":
                    B1_GeneratedBinding();
                    break;

                // Blocker 1: Investigative
                case "b1-vwt-destroy":
                    B1_VwtDestroy();
                    break;
                case "b1-vwt-initcopy":
                    B1_VwtInitCopy();
                    break;

                // Blocker 2: Baseline
                case "b2-intptr-manual":
                    B2_IntPtrManual();
                    break;

                // Blocker 3: Async + SafeHandle
                case "b3-async-safehandle":
                    B3_AsyncSafeHandle();
                    break;
                case "b3-async-static":
                    B3_AsyncStatic();
                    break;
                case "b3-async-wrapper":
                    B3_AsyncWrapper();
                    break;

                // cd-dispose: Verify @_cdecl destroy wrappers for all type categories
                case "cd-dispose-class":
                    CD_DisposeClass();
                    break;
                case "cd-dispose-struct-string":
                    CD_DisposeStructString();
                    break;
                case "cd-dispose-struct-nested":
                    CD_DisposeStructNested();
                    break;

                // NativeAOT-specific
                case "n1-moduleinit":
                    N1_ModuleInit();
                    break;
                case "n2-resolve-no-inject":
                    N2_ResolveNoInject();
                    break;
                case "n3-trimming":
                    N3_Trimming();
                    break;

                // Run all tests sequentially (device mode — single launch)
                case "all":
                    RunAllTests();
                    return;

                default:
                    Console.WriteLine($"FAIL: {testId}: Unknown test ID");
                    return;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {testId}: {ex.GetType().Name}: {ex.Message}");
            return;
        }
    }

    static void RunAllTests()
    {
        var testIds = new[]
        {
            "b1-string-create", "b1-string-length", "b1-string-wrapper",
            "b1-existential", "b1-generated-binding",
            "b1-vwt-destroy", "b1-vwt-initcopy",
            "b2-intptr-manual",
            "b3-async-safehandle", "b3-async-static", "b3-async-wrapper",
            "cd-dispose-class", "cd-dispose-struct-string", "cd-dispose-struct-nested",
            "n1-moduleinit", "n2-resolve-no-inject", "n3-trimming",
        };

        Console.WriteLine("=========================================");
        Console.WriteLine(" NativeAOT Device Test Runner");
        Console.WriteLine("=========================================");
        Console.WriteLine();

        foreach (var testId in testIds)
        {
            try
            {
                RunTest(testId);
            }
            catch
            {
                // RunTest handles its own exceptions
            }
        }

        Console.WriteLine();
        Console.WriteLine("=========================================");
        Console.WriteLine(" ALL TESTS COMPLETE");
        Console.WriteLine("=========================================");
    }

    // -----------------------------------------------------------------------
    // Blocker 1: Mono JIT assertion crash — CallConvSwift P/Invoke
    // -----------------------------------------------------------------------

    static unsafe void B1_StringCreate()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("NativeAOT");
        fixed (byte* ptr = utf8)
        {
            var buffer = SwiftString.PInvoke_Create(ptr, utf8.Length, 1);
            Console.WriteLine("PASS: b1-string-create");
        }
    }

    static unsafe void B1_StringLength()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("Hello");
        fixed (byte* ptr = utf8)
        {
            var buffer = SwiftString.PInvoke_Create(ptr, utf8.Length, 1);
            long length = SwiftString.PInvoke_GetLength(buffer);
            if (length == 5)
                Console.WriteLine("PASS: b1-string-length");
            else
                Console.WriteLine($"FAIL: b1-string-length: Expected length 5, got {length}");
        }
    }

    static void B1_StringWrapper()
    {
        using var str = new SwiftString("Hello NativeAOT");
        var result = str.ToString();
        if (result == "Hello NativeAOT")
            Console.WriteLine("PASS: b1-string-wrapper");
        else
            Console.WriteLine($"FAIL: b1-string-wrapper: Expected 'Hello NativeAOT', got '{result}'");
    }

    static void B1_ExistentialMetadata()
    {
        var metadata = TypeMetadata.GetExistentialTypeMetadata(0);
        if (metadata.IsValid)
            Console.WriteLine("PASS: b1-existential");
        else
            Console.WriteLine("FAIL: b1-existential: Metadata is not valid");
    }

    static void B1_GeneratedBinding()
    {
        int value = StaticMethods.GetStoredValue();
        Console.WriteLine($"PASS: b1-generated-binding (value={value})");
    }

    // -----------------------------------------------------------------------
    // Blocker 1: VWT indirect function pointers
    // -----------------------------------------------------------------------

    static void B1_VwtDestroy()
    {
        var worker = new AsyncThrowingWorker("test-destroy");
        worker.Dispose();
        Console.WriteLine("PASS: b1-vwt-destroy");
    }

    static void B1_VwtInitCopy()
    {
        var worker = new AsyncThrowingWorker("test-copy");
        var swiftObj = (ISwiftObject)worker;
        var metadata = SwiftObjectHelper<AsyncThrowingWorker>.GetTypeMetadata();
        Span<byte> buffer = stackalloc byte[(int)metadata.Size];
        swiftObj.MarshalToSwift(ref buffer);
        Console.WriteLine("PASS: b1-vwt-initcopy");
        worker.Dispose();
    }

    // -----------------------------------------------------------------------
    // Blocker 2: Non-blittable baseline
    // -----------------------------------------------------------------------

    static void B2_IntPtrManual()
    {
        using var str = new SwiftString("optional-test");
        var result = str.ToString();
        if (result == "optional-test")
            Console.WriteLine("PASS: b2-intptr-manual");
        else
            Console.WriteLine($"FAIL: b2-intptr-manual: Expected 'optional-test', got '{result}'");
    }

    // -----------------------------------------------------------------------
    // Blocker 3: SafeHandle not preserved across async P/Invoke
    // -----------------------------------------------------------------------

    static void B3_AsyncSafeHandle()
    {
        var worker = new AsyncThrowingWorker("async-handle-test");
        var task = worker.GetThrowingMethodAsync(shouldThrow: false);

        if (task.Wait(TimeSpan.FromSeconds(10)))
        {
            int result = task.Result;
            Console.WriteLine($"PASS: b3-async-safehandle (result={result})");
        }
        else
        {
            Console.WriteLine("FAIL: b3-async-safehandle: Timed out after 10s");
        }

        worker.Dispose();
    }

    static void B3_AsyncStatic()
    {
        var task = AsyncThrowingWorker.GetStaticThrowingAsync(shouldThrow: false);

        if (task.Wait(TimeSpan.FromSeconds(10)))
        {
            string result = task.Result;
            Console.WriteLine($"PASS: b3-async-static (result={result})");
        }
        else
        {
            Console.WriteLine("FAIL: b3-async-static: Timed out after 10s");
        }
    }

    static void B3_AsyncWrapper()
    {
        var task = AsyncStringWorker.GetStaticStringAsync();

        if (task.Wait(TimeSpan.FromSeconds(10)))
        {
            string result = task.Result;
            if (!string.IsNullOrEmpty(result))
                Console.WriteLine($"PASS: b3-async-wrapper (result={result})");
            else
                Console.WriteLine("FAIL: b3-async-wrapper: Empty result");
        }
        else
        {
            Console.WriteLine("FAIL: b3-async-wrapper: Timed out after 10s");
        }
    }

    // -----------------------------------------------------------------------
    // NativeAOT-specific tests
    // -----------------------------------------------------------------------

    static void N1_ModuleInit()
    {
        try
        {
            var metadata = StaticMethods.PInvoke_getMetadata();
            if (metadata.IsValid)
                Console.WriteLine("PASS: n1-moduleinit");
            else
                Console.WriteLine("FAIL: n1-moduleinit: Metadata not valid after module init");
        }
        catch (DllNotFoundException ex)
        {
            Console.WriteLine($"FAIL: n1-moduleinit: DllNotFoundException — resolver did not fire: {ex.Message}");
        }
    }

    static void N2_ResolveNoInject()
    {
        // On device, frameworks are embedded in the app bundle — should load via @rpath
        if (NativeLibrary.TryLoad("@rpath/SwiftBindingsTestLib.framework/SwiftBindingsTestLib", out var handle))
        {
            Console.WriteLine("PASS: n2-resolve-no-inject");
            NativeLibrary.Free(handle);
        }
        else if (NativeLibrary.TryLoad("SwiftBindingsTestLib", out handle))
        {
            Console.WriteLine("PASS: n2-resolve-no-inject (via direct name)");
            NativeLibrary.Free(handle);
        }
        else
        {
            Console.WriteLine("FAIL: n2-resolve-no-inject: Could not load SwiftBindingsTestLib");
        }
    }

    static void N3_Trimming()
    {
        var errors = new List<string>();

        try
        {
            using var str = new SwiftString("trim-test");
            if (str.ToString() != "trim-test")
                errors.Add("SwiftString round-trip failed");
        }
        catch (Exception ex) { errors.Add($"SwiftString: {ex.GetType().Name}: {ex.Message}"); }

        try
        {
            using var arr = new SwiftArray<int>(new[] { 1, 2, 3 });
            if (arr.Count != 3)
                errors.Add($"SwiftArray<int>.Count: expected 3, got {arr.Count}");
        }
        catch (Exception ex) { errors.Add($"SwiftArray<int>: {ex.GetType().Name}: {ex.Message}"); }

        try
        {
            var metadata = SwiftObjectHelper<SwiftString>.GetTypeMetadata();
            if (!metadata.IsValid)
                errors.Add("TypeMetadata for SwiftString is not valid");
        }
        catch (Exception ex) { errors.Add($"TypeMetadata: {ex.GetType().Name}: {ex.Message}"); }

        try
        {
            int testValue = 42;
            unsafe
            {
                var result = SwiftMarshal.MarshalFromSwift<int>(new IntPtr(&testValue));
                if (result != 42)
                    errors.Add($"MarshalFromSwift<int>: expected 42, got {result}");
            }
        }
        catch (Exception ex) { errors.Add($"MarshalFromSwift: {ex.GetType().Name}: {ex.Message}"); }

        if (errors.Count == 0)
            Console.WriteLine("PASS: n3-trimming");
        else
            Console.WriteLine($"FAIL: n3-trimming: {string.Join("; ", errors)}");
    }

    // -----------------------------------------------------------------------
    // cd-dispose: @_cdecl destroy wrapper tests
    // -----------------------------------------------------------------------

    /// <summary>
    /// cd-dispose-class: Dispose() on a Swift class routes through @_cdecl destroy wrapper.
    /// Verifies Issue 2 fix for class types.
    /// </summary>
    static void CD_DisposeClass()
    {
        var animal = new Animal("dispose-test", "woof");
        animal.Dispose();
        Console.WriteLine("PASS: cd-dispose-class");
    }

    /// <summary>
    /// cd-dispose-struct-string: Dispose() on a struct with String field.
    /// Previously crashed on NativeAOT via VWT Destroy (Issue 2).
    /// Now routes through SBW_Destroy_* @_cdecl wrapper.
    /// </summary>
    static void CD_DisposeStructString()
    {
        var worker = new AsyncThrowingWorker("dispose-struct-test");
        worker.Dispose();
        Console.WriteLine("PASS: cd-dispose-struct-string");
    }

    /// <summary>
    /// cd-dispose-struct-nested: Dispose() on a frozen struct with nested Inner struct + String ref field.
    /// NestedOuter is @frozen with NestedOuter.Inner (value) + label (String) — exercises destroy
    /// wrapper for complex struct layouts where deinitialize must release the String reference.
    /// </summary>
    static void CD_DisposeStructNested()
    {
        var inner = new NestedOuter.Inner(42);
        var obj = new NestedOuter(inner, "dispose-nested-test");
        obj.Dispose();
        Console.WriteLine("PASS: cd-dispose-struct-nested");
    }
}

#endregion

#region Application Entry Point

public class Application
{
    static void Main(string[] args)
    {
        var effectiveArgs = args.Length > 0 ? args : GetProcessInfoArgs();
        string? testId = null;

        for (int i = 0; i < effectiveArgs.Length; i++)
        {
            if (effectiveArgs[i] == "--test-id" && i + 1 < effectiveArgs.Length)
            {
                testId = effectiveArgs[i + 1];
                i++;
            }
        }

        // Register resolver for bundled frameworks
        SwiftFrameworkResolver.RegisterForAssembly(Assembly.GetExecutingAssembly());

        if (testId != null)
        {
            TestDispatcher.RunTest(testId);
        }
        else
        {
            // Default: run all tests (device mode — single launch is simpler)
            TestDispatcher.RunTest("all");
        }

        Environment.Exit(0);
    }

    static string[] GetProcessInfoArgs()
    {
        var allArgs = NSProcessInfo.ProcessInfo.Arguments;
        if (allArgs.Length <= 1)
            return Array.Empty<string>();
        return allArgs.Skip(1).ToArray();
    }

    static IntPtr ResolveBundledFramework(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == "SwiftBindingsTestLib" || libraryName == "SwiftBindings"
            || libraryName == "libSwiftBindingsRuntime.dylib")
        {
            string frameworkPath;
            if (libraryName == "libSwiftBindingsRuntime.dylib")
                frameworkPath = $"@rpath/{libraryName}";
            else
                frameworkPath = $"@rpath/{libraryName}.framework/{libraryName}";

            if (NativeLibrary.TryLoad(frameworkPath, out var handle))
            {
                Console.WriteLine($"[Resolver] {libraryName} -> {frameworkPath}");
                return handle;
            }
            Console.WriteLine($"[Resolver] WARN: Failed to resolve {libraryName} at {frameworkPath}");
        }
        return IntPtr.Zero;
    }
}

#endregion
