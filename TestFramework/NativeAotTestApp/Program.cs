// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

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
using Swift.SwiftBindingsTestLib;
using UIKit;

namespace NativeAotTestApp;

#region Test Dispatch

/// <summary>
/// Dispatches test execution based on --test-id argument.
/// Each test runs as a separate app launch (one test per process) to isolate fatal crashes.
/// </summary>
public static class TestDispatcher
{
    /// <summary>
    /// Runs a single test by ID and prints PASS/FAIL/CRASH markers.
    /// </summary>
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

                // Blocker 2: Baseline (always in main project)
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

                // NativeAOT-specific
                case "n1-moduleinit":
                    N1_ModuleInit();
                    break;
                case "n2-resolve-no-inject":
                    N2_ResolveNoInject();
                    break;
                case "n2-resolve-with-inject":
                    N2_ResolveWithInject();
                    break;
                case "n3-trimming":
                    N3_Trimming();
                    break;

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

    // -----------------------------------------------------------------------
    // Blocker 1: Mono JIT assertion crash — CallConvSwift P/Invoke
    // Under NativeAOT there is no JIT, so these should all pass.
    // -----------------------------------------------------------------------

    /// <summary>
    /// b1-string-create: Raw CallConvSwift P/Invoke to libswiftCore string constructor.
    /// On Mono this triggers jit-info.c:918 assertion. On NativeAOT it should work.
    /// </summary>
    static unsafe void B1_StringCreate()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("NativeAOT");
        fixed (byte* ptr = utf8)
        {
            var buffer = SwiftString.PInvoke_Create(ptr, utf8.Length, 1);
            // If we get here without crashing, the CallConvSwift P/Invoke worked
            Console.WriteLine("PASS: b1-string-create");
        }
    }

    /// <summary>
    /// b1-string-length: Raw CallConvSwift P/Invoke to libswiftCore count getter.
    /// </summary>
    static unsafe void B1_StringLength()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("Hello");
        fixed (byte* ptr = utf8)
        {
            var buffer = SwiftString.PInvoke_Create(ptr, utf8.Length, 1);
            long length = SwiftString.PInvoke_GetLength(buffer);
            if (length == 5)
            {
                Console.WriteLine("PASS: b1-string-length");
            }
            else
            {
                Console.WriteLine($"FAIL: b1-string-length: Expected length 5, got {length}");
            }
        }
    }

    /// <summary>
    /// b1-string-wrapper: SwiftString via Cdecl wrapper path (baseline — works on Mono too).
    /// </summary>
    static void B1_StringWrapper()
    {
        using var str = new SwiftString("Hello NativeAOT");
        var result = str.ToString();
        if (result == "Hello NativeAOT")
        {
            Console.WriteLine("PASS: b1-string-wrapper");
        }
        else
        {
            Console.WriteLine($"FAIL: b1-string-wrapper: Expected 'Hello NativeAOT', got '{result}'");
        }
    }

    /// <summary>
    /// b1-existential: swift_getExistentialTypeMetadata via CallConvSwift.
    /// On Mono this crashes with jit-info.c:918. On NativeAOT it should work.
    /// </summary>
    static void B1_ExistentialMetadata()
    {
        // TypeMetadata.GetExistentialTypeMetadata(0) uses the direct CallConvSwift path
        // when the wrapper is not available, or the wrapper path if deployed.
        // Under NativeAOT, both paths should work since there's no JIT.
        var metadata = TypeMetadata.GetExistentialTypeMetadata(0);
        if (metadata.IsValid)
        {
            Console.WriteLine("PASS: b1-existential");
        }
        else
        {
            Console.WriteLine("FAIL: b1-existential: Metadata is not valid");
        }
    }

    /// <summary>
    /// b1-generated-binding: End-to-end call to a generated binding method.
    /// Uses StaticMethods.GetStoredValue() which is a simple blittable call.
    /// </summary>
    static void B1_GeneratedBinding()
    {
        // GetStoredValue returns a static Int32 — purely blittable, no string marshalling
        int value = StaticMethods.GetStoredValue();
        // We don't care about the specific value, just that the call succeeded
        Console.WriteLine($"PASS: b1-generated-binding (value={value})");
    }

    // -----------------------------------------------------------------------
    // Blocker 1: Investigative — indirect CallConvSwift function pointers
    // -----------------------------------------------------------------------

    /// <summary>
    /// b1-vwt-destroy: .Dispose() on a struct with String fields.
    /// VWT Destroy uses an indirect CallConvSwift function pointer.
    /// On Mono this crashes (jit-info.c:918 via VWT dispatch).
    /// </summary>
    static void B1_VwtDestroy()
    {
        // AsyncThrowingWorker has a String name field — its VWT Destroy calls through
        // an indirect CallConvSwift function pointer to release the string.
        var worker = new AsyncThrowingWorker("test-destroy");
        // Explicit dispose triggers VWT Destroy
        worker.Dispose();
        Console.WriteLine("PASS: b1-vwt-destroy");
    }

    /// <summary>
    /// b1-vwt-initcopy: VWT InitializeWithCopy via MarshalToSwift path.
    /// Same indirect function pointer pattern as Destroy.
    /// </summary>
    static void B1_VwtInitCopy()
    {
        var worker = new AsyncThrowingWorker("test-copy");
        // MarshalToSwift internally calls VWT InitializeWithCopy
        var swiftObj = (ISwiftObject)worker;
        var metadata = SwiftObjectHelper<AsyncThrowingWorker>.GetTypeMetadata();
        Span<byte> buffer = stackalloc byte[(int)metadata.Size];
        swiftObj.MarshalToSwift(ref buffer);
        Console.WriteLine("PASS: b1-vwt-initcopy");
        worker.Dispose();
    }

    // -----------------------------------------------------------------------
    // Blocker 2: Non-blittable baseline (manual IntPtr path)
    // -----------------------------------------------------------------------

    /// <summary>
    /// b2-intptr-manual: Existing _optbuf wrapper pattern — should always work.
    /// </summary>
    static void B2_IntPtrManual()
    {
        // The wrapper path uses IntPtr + manual marshalling to avoid non-blittable
        // CallConvSwift signatures. This should work on both Mono and NativeAOT.
        using var str = new SwiftString("optional-test");
        var result = str.ToString();
        if (result == "optional-test")
        {
            Console.WriteLine("PASS: b2-intptr-manual");
        }
        else
        {
            Console.WriteLine($"FAIL: b2-intptr-manual: Expected 'optional-test', got '{result}'");
        }
    }

    // -----------------------------------------------------------------------
    // Blocker 3: SafeHandle not preserved across async P/Invoke
    // -----------------------------------------------------------------------

    /// <summary>
    /// b3-async-safehandle: Call async instance method where self is SwiftSafeHandle.
    /// On Mono, the SafeHandle may be GC-collected during async suspension.
    /// </summary>
    static void B3_AsyncSafeHandle()
    {
        // AsyncThrowingWorker.GetThrowingMethodAsync is an async instance method
        // that takes a SafeHandle-backed self through the suspension point.
        var worker = new AsyncThrowingWorker("async-handle-test");

        // Use a synchronous wait — this is a test app, not production code.
        // The key question is whether the SafeHandle survives the async round-trip.
        var task = worker.GetThrowingMethodAsync(shouldThrow: false);

        // Block on the main thread with a timeout
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

    /// <summary>
    /// b3-async-static: Call async static method — no SafeHandle, should always pass.
    /// </summary>
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

    /// <summary>
    /// b3-async-wrapper: Async via existing IntPtr conversion wrapper — baseline.
    /// Uses the same async pattern but through the wrapper library (Cdecl).
    /// </summary>
    static void B3_AsyncWrapper()
    {
        // AsyncStringWorker.GetStaticStringAsync goes through the wrapper library
        var task = AsyncStringWorker.GetStaticStringAsync();

        if (task.Wait(TimeSpan.FromSeconds(10)))
        {
            string result = task.Result;
            if (!string.IsNullOrEmpty(result))
            {
                Console.WriteLine($"PASS: b3-async-wrapper (result={result})");
            }
            else
            {
                Console.WriteLine("FAIL: b3-async-wrapper: Empty result");
            }
        }
        else
        {
            Console.WriteLine("FAIL: b3-async-wrapper: Timed out after 10s");
        }
    }

    // -----------------------------------------------------------------------
    // NativeAOT-specific tests
    // -----------------------------------------------------------------------

    /// <summary>
    /// n1-moduleinit: Verify [ModuleInitializer] + SetDllImportResolver fires.
    /// </summary>
    static void N1_ModuleInit()
    {
        // If we can load a generated type, the module initializer ran successfully.
        // The [ModuleInitializer] sets up SetDllImportResolver for the assembly.
        // Under NativeAOT, module initializers should still work.
        try
        {
            // Access a type that requires DllImport resolution
            var metadata = StaticMethods.PInvoke_getMetadata();
            if (metadata.IsValid)
            {
                Console.WriteLine("PASS: n1-moduleinit");
            }
            else
            {
                Console.WriteLine("FAIL: n1-moduleinit: Metadata not valid after module init");
            }
        }
        catch (DllNotFoundException ex)
        {
            Console.WriteLine($"FAIL: n1-moduleinit: DllNotFoundException — resolver did not fire: {ex.Message}");
        }
    }

    /// <summary>
    /// n2-resolve-no-inject: Test NativeLibrary.Load for @rpath framework resolution
    /// WITHOUT manual dylib injection. Tests whether NativeAOT embeds frameworks correctly.
    /// </summary>
    static void N2_ResolveNoInject()
    {
        // Try to load SwiftBindingsTestLib without manual dylib injection.
        // This tests whether the NativeReference xcframework is properly embedded.
        if (NativeLibrary.TryLoad("@rpath/SwiftBindingsTestLib.framework/SwiftBindingsTestLib", out var handle))
        {
            Console.WriteLine("PASS: n2-resolve-no-inject");
            NativeLibrary.Free(handle);
        }
        else
        {
            // Also try the direct name (DllImport resolver may handle it)
            if (NativeLibrary.TryLoad("SwiftBindingsTestLib", out handle))
            {
                Console.WriteLine("PASS: n2-resolve-no-inject (via direct name)");
                NativeLibrary.Free(handle);
            }
            else
            {
                Console.WriteLine("FAIL: n2-resolve-no-inject: Could not load SwiftBindingsTestLib");
            }
        }
    }

    /// <summary>
    /// n2-resolve-with-inject: Same as no-inject but with manual dylib copy.
    /// Tests that the runtime resolver correctly finds injected frameworks.
    /// </summary>
    static void N2_ResolveWithInject()
    {
        // When dylib is injected into Frameworks/, @rpath resolution should work
        if (NativeLibrary.TryLoad("@rpath/libSwiftBindingsRuntime.dylib", out var handle))
        {
            Console.WriteLine("PASS: n2-resolve-with-inject (runtime dylib)");
            NativeLibrary.Free(handle);
        }
        else
        {
            Console.WriteLine("FAIL: n2-resolve-with-inject: Could not load libSwiftBindingsRuntime.dylib");
        }

        // Also verify the test library
        if (NativeLibrary.TryLoad("@rpath/SwiftBindingsTestLib.framework/SwiftBindingsTestLib", out handle))
        {
            Console.WriteLine("PASS: n2-resolve-with-inject (test lib)");
            NativeLibrary.Free(handle);
        }
    }

    /// <summary>
    /// n3-trimming: Verify core types survive NativeAOT trimming.
    /// SwiftString, SwiftArray, TypeMetadata use reflection — trimmer may remove them.
    /// </summary>
    static void N3_Trimming()
    {
        var errors = new List<string>();

        // SwiftString: constructor + ToString round-trip
        try
        {
            using var str = new SwiftString("trim-test");
            if (str.ToString() != "trim-test")
                errors.Add("SwiftString round-trip failed");
        }
        catch (Exception ex)
        {
            errors.Add($"SwiftString: {ex.GetType().Name}: {ex.Message}");
        }

        // SwiftArray<int>: basic construction
        try
        {
            using var arr = new SwiftArray<int>(new[] { 1, 2, 3 });
            if (arr.Count != 3)
                errors.Add($"SwiftArray<int>.Count: expected 3, got {arr.Count}");
        }
        catch (Exception ex)
        {
            errors.Add($"SwiftArray<int>: {ex.GetType().Name}: {ex.Message}");
        }

        // TypeMetadata: cache + GetTypeMetadataOrThrow
        try
        {
            var metadata = SwiftObjectHelper<SwiftString>.GetTypeMetadata();
            if (!metadata.IsValid)
                errors.Add("TypeMetadata for SwiftString is not valid");
        }
        catch (Exception ex)
        {
            errors.Add($"TypeMetadata: {ex.GetType().Name}: {ex.Message}");
        }

        // SwiftMarshal.MarshalFromSwift<int>: reflection-based generic instantiation
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
        catch (Exception ex)
        {
            errors.Add($"MarshalFromSwift: {ex.GetType().Name}: {ex.Message}");
        }

        if (errors.Count == 0)
        {
            Console.WriteLine("PASS: n3-trimming");
        }
        else
        {
            Console.WriteLine($"FAIL: n3-trimming: {string.Join("; ", errors)}");
        }
    }
}

#endregion

#region Application Entry Point

public class Application
{
    static void Main(string[] args)
    {
        // Parse --test-id from NSProcessInfo (iOS doesn't pass args to Main reliably)
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

        // Register resolver for bundled frameworks BEFORE any Swift types are accessed.
        try
        {
            NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), ResolveBundledFramework);
        }
        catch (InvalidOperationException)
        {
            // A resolver is already registered (from generated bindings ModuleInitializer).
        }

        if (testId != null)
        {
            // Single-test mode: run one test, print result, exit
            TestDispatcher.RunTest(testId);
        }
        else
        {
            Console.WriteLine("FAIL: No --test-id specified. Usage: --test-id <test-name>");
        }

        // Exit cleanly without starting UIKit (not needed for headless tests)
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
