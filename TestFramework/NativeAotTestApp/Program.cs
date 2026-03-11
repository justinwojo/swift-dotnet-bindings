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
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;
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

                // CrashRisk ports: tests that crash Mono JIT but should pass under NativeAOT
                case "cr-enum-basic":
                    CR_EnumBasic();
                    break;
                case "cr-enum-string":
                    CR_EnumString();
                    break;
                case "cr-enum-shape":
                    CR_EnumShape();
                    break;
                case "cr-enum-nested":
                    CR_EnumNested();
                    break;
                case "cr-array-basic":
                    CR_ArrayBasic();
                    break;
                case "cr-array-advanced":
                    CR_ArrayAdvanced();
                    break;
                case "cr-gc-basic":
                    CR_GCBasic();
                    break;
                case "cr-gc-mutableprops":
                    CR_GCMutableProps();
                    break;
                case "cr-gc-stress":
                    CR_GCStress();
                    break;
                case "cr-existential":
                    CR_Existential();
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

    // -----------------------------------------------------------------------
    // CrashRisk ports: tests that crash under Mono JIT but should pass
    // under NativeAOT. Ported from RuntimeTestsApp CrashRisk classes.
    // -----------------------------------------------------------------------

    /// <summary>
    /// cr-enum-basic: Direction + Color enum construction, method calls, raw values.
    /// Crashes Mono JIT on CreateOrder/Shape P/Invoke.
    /// </summary>
    static void CR_EnumBasic()
    {
        var errors = new List<string>();

        // Direction method call (P/Invoke — the crash point under Mono JIT)
        if (!Functions.IsHorizontal(Direction.East)) errors.Add("East not horizontal");
        if (Functions.IsHorizontal(Direction.North)) errors.Add("North is horizontal");

        // Direction.Opposite() extension method (P/Invoke)
        if (Direction.North.Opposite() != Direction.South) errors.Add("Opposite(North) != South");
        if (Direction.East.Opposite() != Direction.West) errors.Add("Opposite(East) != West");

        // ColorForIndex P/Invoke
        var color0 = Functions.ColorForIndex(0);
        if (color0 != Color.Red) errors.Add($"ColorForIndex(0): expected Red, got {color0}");
        var color1 = Functions.ColorForIndex(1);
        if (color1 != Color.Green) errors.Add($"ColorForIndex(1): expected Green, got {color1}");

        if (errors.Count == 0)
            Console.WriteLine("PASS: cr-enum-basic");
        else
            Console.WriteLine($"FAIL: cr-enum-basic: {string.Join("; ", errors)}");
    }

    /// <summary>
    /// cr-enum-string: StatusCode string raw value enum — construction, raw values, FromRawValue.
    /// </summary>
    static void CR_EnumString()
    {
        var errors = new List<string>();

        // Case tag construction
        if (StatusCode.Ok.Tag != StatusCode.CaseTag.Ok) errors.Add("Ok tag mismatch");
        if (StatusCode.NotFound.Tag != StatusCode.CaseTag.NotFound) errors.Add("NotFound tag mismatch");
        if (StatusCode.Error.Tag != StatusCode.CaseTag.Error) errors.Add("Error tag mismatch");
        if (StatusCode.Timeout.Tag != StatusCode.CaseTag.Timeout) errors.Add("Timeout tag mismatch");

        // Raw values
        if (StatusCode.Ok.RawValue.ToString() != "OK") errors.Add($"Ok raw value: {StatusCode.Ok.RawValue}");
        if (StatusCode.NotFound.RawValue.ToString() != "NOT_FOUND") errors.Add($"NotFound raw value: {StatusCode.NotFound.RawValue}");
        if (StatusCode.Error.RawValue.ToString() != "ERROR") errors.Add($"Error raw value: {StatusCode.Error.RawValue}");
        if (StatusCode.Timeout.RawValue.ToString() != "TIMEOUT") errors.Add($"Timeout raw value: {StatusCode.Timeout.RawValue}");

        // FromRawValue round-trip
        var ok = StatusCode.FromRawValue("OK");
        if (ok == null) errors.Add("FromRawValue(OK) is null");
        else if (ok.RawValue.ToString() != "OK") errors.Add("OK round-trip failed");

        var timeout = StatusCode.FromRawValue("TIMEOUT");
        if (timeout == null) errors.Add("FromRawValue(TIMEOUT) is null");
        else if (timeout.RawValue.ToString() != "TIMEOUT") errors.Add("TIMEOUT round-trip failed");

        // Invalid raw value returns null
        var invalid = StatusCode.FromRawValue("INVALID");
        if (invalid != null) errors.Add("Invalid raw value should be null");

        if (errors.Count == 0)
            Console.WriteLine("PASS: cr-enum-string");
        else
            Console.WriteLine($"FAIL: cr-enum-string: {string.Join("; ", errors)}");
    }

    /// <summary>
    /// cr-enum-shape: Shape associated values + EnumPropertyHolder get/set.
    /// </summary>
    static void CR_EnumShape()
    {
        var errors = new List<string>();

        // Shape case creation with associated values
        var circle = Shape.Circle(5.0);
        if (circle.Tag != Shape.CaseTag.Circle) errors.Add("Circle tag mismatch");

        var rect = Shape.Rectangle((width: 10.0, height: 20.0));
        if (rect.Tag != Shape.CaseTag.Rectangle) errors.Add("Rectangle tag mismatch");

        var point = Shape.Point(new FrozenPoint { X = 1.0, Y = 2.0 });
        if (point.Tag != Shape.CaseTag.Point) errors.Add("Point tag mismatch");

        var empty = Shape.Empty;
        if (empty.Tag != Shape.CaseTag.Empty) errors.Add("Empty tag mismatch");

        // All cases distinct
        if (circle.Tag == rect.Tag) errors.Add("Circle == Rectangle");
        if (circle.Tag == empty.Tag) errors.Add("Circle == Empty");
        if (rect.Tag == point.Tag) errors.Add("Rectangle == Point");

        // EnumPropertyHolder — property get/set
        var holder = new EnumPropertyHolder(Shape.Circle(5.0));
        if (holder.CurrentShape.Tag != Shape.CaseTag.Circle) errors.Add("CurrentShape not Circle");

        holder.CurrentShape = Shape.Rectangle((width: 3.0, height: 4.0));
        if (holder.CurrentShape.Tag != Shape.CaseTag.Rectangle) errors.Add("CurrentShape not Rectangle after set");

        // GetShape method
        var holder2 = new EnumPropertyHolder(Shape.Empty);
        if (holder2.GetShape().Tag != Shape.CaseTag.Empty) errors.Add("GetShape() not Empty");

        // Optional shape — default null
        if (holder2.OptionalShape != null) errors.Add("OptionalShape default not null");

        // Set optional shape
        holder2.OptionalShape = Shape.Circle(3.0);
        if (holder2.OptionalShape == null) errors.Add("OptionalShape null after set");
        else if (holder2.OptionalShape.Tag != Shape.CaseTag.Circle) errors.Add("OptionalShape not Circle");

        // Clear optional shape
        holder2.OptionalShape = null;
        if (holder2.OptionalShape != null) errors.Add("OptionalShape not null after clear");

        if (errors.Count == 0)
            Console.WriteLine("PASS: cr-enum-shape");
        else
            Console.WriteLine($"FAIL: cr-enum-shape: {string.Join("; ", errors)}");
    }

    /// <summary>
    /// cr-enum-nested: OrderContainer, PaymentContainer, NetworkConfig nested enums.
    /// </summary>
    static void CR_EnumNested()
    {
        var errors = new List<string>();

        // OrderContainer creation
        var order = Functions.CreateOrder("ORD-001", "order_pending");
        if (order == null) errors.Add("Order is null");
        else
        {
            var statusRaw = Functions.GetOrderStatusRaw(order);
            if (statusRaw != "order_pending") errors.Add($"Order status: {statusRaw}");
        }

        // OrderContainer.Status FromRawValue
        var allOrderStatuses = new[] { "order_pending", "order_processing", "order_shipped", "order_delivered", "order_cancelled" };
        foreach (var raw in allOrderStatuses)
        {
            var status = OrderContainer.Status.FromRawValue(raw);
            if (status == null) errors.Add($"OrderContainer.Status({raw}) is null");
            else if (status.RawValue.ToString() != raw) errors.Add($"OrderContainer.Status({raw}) round-trip failed");
        }

        // Case name (not raw value) should return null
        if (OrderContainer.Status.FromRawValue("pending") != null) errors.Add("Case name 'pending' should not match");

        // PaymentContainer
        var payment = Functions.CreatePayment("PAY-001", "payment_authorized");
        if (payment == null) errors.Add("Payment is null");
        else
        {
            var statusRaw = Functions.GetPaymentStatusRaw(payment);
            if (statusRaw != "payment_authorized") errors.Add($"Payment status: {statusRaw}");
        }

        var allPaymentStatuses = new[] { "payment_pending", "payment_authorized", "payment_captured", "payment_refunded", "payment_failed" };
        foreach (var raw in allPaymentStatuses)
        {
            var status = PaymentContainer.Status.FromRawValue(raw);
            if (status == null) errors.Add($"PaymentContainer.Status({raw}) is null");
            else if (status.RawValue.ToString() != raw) errors.Add($"PaymentContainer.Status({raw}) round-trip failed");
        }

        // NetworkConfig nested enums
        var httpMethods = new[] { "GET", "POST", "PUT", "DELETE", "PATCH" };
        foreach (var raw in httpMethods)
        {
            var method = NetworkConfig.HttpMethod.FromRawValue(raw);
            if (method == null) errors.Add($"HttpMethod({raw}) is null");
            else if (method.RawValue.ToString() != raw) errors.Add($"HttpMethod({raw}) round-trip failed");
        }

        var contentTypes = new[] { "application/json", "application/xml", "multipart/form-data", "text/plain" };
        foreach (var raw in contentTypes)
        {
            var ct = NetworkConfig.ContentType.FromRawValue(raw);
            if (ct == null) errors.Add($"ContentType({raw}) is null");
            else if (ct.RawValue.ToString() != raw) errors.Add($"ContentType({raw}) round-trip failed");
        }

        if (errors.Count == 0)
            Console.WriteLine("PASS: cr-enum-nested");
        else
            Console.WriteLine($"FAIL: cr-enum-nested: {string.Join("; ", errors)}");
    }

    /// <summary>
    /// cr-array-basic: Array parameter, return, empty, sum, reverse — core SwiftArray P/Invoke.
    /// Crashes Mono JIT on SwiftArray P/Invoke via CallConvSwift.
    /// </summary>
    static void CR_ArrayBasic()
    {
        var errors = new List<string>();

        // Array parameter count
        var count = Functions.ArrayCount(new[] { 10, 20, 30 });
        if (count != 3) errors.Add($"ArrayCount: expected 3, got {count}");

        // Array return
        var created = Functions.CreateIntArray(3, 42);
        if (created.Count != 3) errors.Add($"CreateIntArray count: expected 3, got {created.Count}");
        for (int i = 0; i < created.Count; i++)
        {
            if (created[i] != 42) errors.Add($"CreateIntArray[{i}]: expected 42, got {created[i]}");
        }

        // Empty array
        if (!Functions.IsEmptyArray(Array.Empty<int>())) errors.Add("Empty array not detected");
        if (Functions.ArrayCount(Array.Empty<int>()) != 0) errors.Add("Empty array count != 0");

        // Sum
        var sum = Functions.SumArray(new[] { 1, 2, 3, 4, 5 });
        if (sum != 15) errors.Add($"SumArray: expected 15, got {sum}");

        // Reverse
        var reversed = Functions.ReverseIntArray(new[] { 1, 2, 3 });
        if (reversed.Count != 3) errors.Add($"Reversed count: {reversed.Count}");
        else
        {
            if (reversed[0] != 3) errors.Add($"Reversed[0]: expected 3, got {reversed[0]}");
            if (reversed[1] != 2) errors.Add($"Reversed[1]: expected 2, got {reversed[1]}");
            if (reversed[2] != 1) errors.Add($"Reversed[2]: expected 1, got {reversed[2]}");
        }

        // Single element
        if (Functions.ArrayCount(new[] { 99 }) != 1) errors.Add("Single element count != 1");
        if (Functions.SumArray(new[] { 99 }) != 99) errors.Add("Single element sum != 99");

        if (errors.Count == 0)
            Console.WriteLine("PASS: cr-array-basic");
        else
            Console.WriteLine($"FAIL: cr-array-basic: {string.Join("; ", errors)}");
    }

    /// <summary>
    /// cr-array-advanced: String arrays, class arrays, filter — complex array marshalling.
    /// </summary>
    static void CR_ArrayAdvanced()
    {
        var errors = new List<string>();

        // String array creation
        var strings = Functions.CreateStringArray("hello", "world");
        if (strings.Count != 2) errors.Add($"String array count: {strings.Count}");
        else
        {
            if (strings[0] != "hello") errors.Add($"String[0]: {strings[0]}");
            if (strings[1] != "world") errors.Add($"String[1]: {strings[1]}");
        }

        // Array of classes
        var cat = Functions.CreateAnimal("Cat", "Meow");
        var dog = Functions.CreateAnimal("Dog", "Woof");
        var descriptions = Functions.DescribeAnimals(new[] { cat, dog });
        if (descriptions.Count != 2) errors.Add($"Descriptions count: {descriptions.Count}");
        else
        {
            if (!descriptions[0].Contains("Cat")) errors.Add($"Description[0] missing Cat: {descriptions[0]}");
            if (!descriptions[1].Contains("Dog")) errors.Add($"Description[1] missing Dog: {descriptions[1]}");
        }

        // Filter positive
        var filtered = Functions.FilterPositive(new[] { -2, -1, 0, 1, 2, 3 });
        if (filtered.Count != 3) errors.Add($"FilterPositive count: {filtered.Count}");
        else
        {
            if (filtered[0] != 1) errors.Add($"Filtered[0]: {filtered[0]}");
            if (filtered[1] != 2) errors.Add($"Filtered[1]: {filtered[1]}");
            if (filtered[2] != 3) errors.Add($"Filtered[2]: {filtered[2]}");
        }

        // All negative filtered to empty
        var allNeg = Functions.FilterPositive(new[] { -3, -2, -1 });
        if (allNeg.Count != 0) errors.Add($"All negative count: {allNeg.Count}");

        if (errors.Count == 0)
            Console.WriteLine("PASS: cr-array-advanced");
        else
            Console.WriteLine($"FAIL: cr-array-advanced: {string.Join("; ", errors)}");
    }

    /// <summary>
    /// cr-gc-basic: Animal/UniqueResource create-use-release with ForceGC.
    /// Crashes Mono JIT when ForceGC triggers VWT Destroy via GC finalizer thread.
    /// </summary>
    static void CR_GCBasic()
    {
        var errors = new List<string>();

        // Animal create-use-release
        var animal = Functions.CreateAnimal("Temp", "Woof");
        var name = animal.Name.ToString();
        if (name != "Temp") errors.Add($"Animal name: {name}");
        var speak = animal.GetSpeak();
        if (speak == null) errors.Add("Speak returned null");
        animal = null!;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // UniqueResource via factory
        var resource = Functions.CreateUniqueResource(42);
        if (resource.Id != 42) errors.Add($"UniqueResource.Id: {resource.Id}");
        var inspected = resource.GetInspect();
        if (inspected != 42) errors.Add($"UniqueResource.Inspect: {inspected}");
        resource = null!;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // UniqueResource via constructor
        var resource2 = new UniqueResource(99);
        if (resource2.Id != 99) errors.Add($"Constructor UniqueResource.Id: {resource2.Id}");
        resource2 = null!;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        if (errors.Count == 0)
            Console.WriteLine("PASS: cr-gc-basic");
        else
            Console.WriteLine($"FAIL: cr-gc-basic: {string.Join("; ", errors)}");
    }

    /// <summary>
    /// cr-gc-mutableprops: MutableProps lifecycle, double dispose, access after dispose.
    /// MutableProps constructor uses CallConvSwift → Mono JIT frame tracker corruption.
    /// </summary>
    static void CR_GCMutableProps()
    {
        var errors = new List<string>();

        // Basic lifecycle
        var props = new MutableProps(10, "Test");
        if (props.Value != 10) errors.Add($"MutableProps.Value: {props.Value}");
        if (props.Name.ToString() != "Test") errors.Add($"MutableProps.Name: {props.Name}");

        // Modify and verify
        props.Value = 20;
        if (props.Value != 20) errors.Add($"MutableProps.Value after set: {props.Value}");
        props = null!;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Double dispose safety
        var props2 = new MutableProps(5, "DoubleDispose");
        if (props2.Value != 5) errors.Add("DoubleDispose initial value wrong");
        props2.Dispose();
        props2.Dispose(); // Should not throw

        // Access after dispose throws ObjectDisposedException
        var props3 = new MutableProps(10, "Disposed");
        props3.Dispose();
        try
        {
            _ = props3.Value;
            errors.Add("Value access after dispose did not throw");
        }
        catch (ObjectDisposedException)
        {
            // Expected
        }

        try
        {
            props3.Value = 99;
            errors.Add("Value set after dispose did not throw");
        }
        catch (ObjectDisposedException)
        {
            // Expected
        }

        if (errors.Count == 0)
            Console.WriteLine("PASS: cr-gc-mutableprops");
        else
            Console.WriteLine($"FAIL: cr-gc-mutableprops: {string.Join("; ", errors)}");
    }

    /// <summary>
    /// cr-gc-stress: GC stress scenarios — repeated GC, mass object abandonment,
    /// interleaved create/dispose, GC pressure during property access.
    /// </summary>
    static void CR_GCStress()
    {
        var errors = new List<string>();

        // Object survives repeated GC
        var survivor = Functions.CreateAnimal("Survivor", "Roar");
        for (int i = 0; i < 10; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var sName = survivor.Name.ToString();
            if (sName != "Survivor") errors.Add($"Survivor GC cycle {i}: {sName}");
        }

        // Many objects create and abandon
        for (int i = 0; i < 100; i++)
        {
            var temp = Functions.CreateAnimal($"Temp{i}", "Sound");
            _ = temp.Name.ToString();
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Verify system still healthy after mass abandonment
        var final = Functions.CreateAnimal("Final", "OK");
        if (final.Name.ToString() != "Final") errors.Add("System unhealthy after mass abandonment");

        // Interleaved create/dispose
        var animals = new List<Animal>();
        for (int i = 0; i < 20; i++)
        {
            animals.Add(Functions.CreateAnimal($"Animal{i}", $"Sound{i}"));
            if (i % 5 == 4 && animals.Count > 0)
            {
                animals[0].Dispose();
                animals.RemoveAt(0);
            }
        }
        foreach (var a in animals)
        {
            var n = a.Name.ToString();
            if (string.IsNullOrEmpty(n)) errors.Add("Remaining animal has invalid name");
        }

        // GC pressure during property access
        var pressure = Functions.CreateAnimal("Pressure", "Test");
        for (int i = 0; i < 50; i++)
        {
            _ = new byte[4096]; // garbage
            var pName = pressure.Name.ToString();
            if (pName != "Pressure") errors.Add($"GC pressure cycle {i}: {pName}");
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        if (pressure.Name.ToString() != "Pressure") errors.Add("Pressure failed after GC");

        if (errors.Count == 0)
            Console.WriteLine("PASS: cr-gc-stress");
        else
            Console.WriteLine($"FAIL: cr-gc-stress: {string.Join("; ", errors)}");
    }

    /// <summary>
    /// cr-existential: Protocol proxy callback with existential parameter.
    /// Crashes Mono JIT with SIGSEGV when proxy object passes through CallConvSwift.
    /// </summary>
    static void CR_Existential()
    {
        var errors = new List<string>();

        var impl = new TestExistentialDelegateImpl();
        var proxy = new ExistentialParamDelegateProxy(impl);

        // Swift creates a MutableItem(value: 42), passes it as `any HasValue`
        // to delegate.didReceive(value:). The proxy receiver unmarshals
        // ExistentialContainer1 → HasValueProxy and dispatches to impl.
        Functions.FireExistentialDelegate(proxy, intValue: 42);

        if (!impl.WasCalled) errors.Add("Delegate was not called");
        if (impl.ReceivedValue != 42) errors.Add($"Received value: {impl.ReceivedValue}, expected 42");

        if (errors.Count == 0)
            Console.WriteLine("PASS: cr-existential");
        else
            Console.WriteLine($"FAIL: cr-existential: {string.Join("; ", errors)}");
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

/// <summary>
/// C# implementation of IExistentialParamDelegate for cr-existential test.
/// Records whether the callback was received and what value was passed.
/// </summary>
internal class TestExistentialDelegateImpl : IExistentialParamDelegate
{
    public bool WasCalled { get; private set; }
    public int ReceivedValue { get; private set; }

    public void DidReceive(IHasValue value)
    {
        WasCalled = true;
        ReceivedValue = value.Value;
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
