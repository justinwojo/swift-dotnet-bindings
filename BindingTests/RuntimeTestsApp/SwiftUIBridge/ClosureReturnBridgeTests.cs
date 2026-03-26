// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if SWIFTUI_BRIDGE

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.SwiftUIBridge;

/// <summary>
/// Tests for SwiftUI bridge views with non-primitive closure return types.
/// StringReturnClosureView: (Int32) -> String callback, verifies UTF-8 string round-trip.
/// ClassReturnClosureView: (Int32) -> SimpleModel callback, verifies Arc retention semantics.
/// </summary>
public class BridgeClosureReturnTests : TestBase
{
    public BridgeClosureReturnTests(TestResults results) : base(results) { }

    // --- StringReturnClosureView ---

    public unsafe void TestStringReturnClosureView_CreateAndFree()
    {
        StringReturnCallbackState.Reset();

        delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr, IntPtr> callbackPtr = &StringReturnCallbackState.TransformerCallback;
        var handle = BridgeNativeMethods.StringReturnClosureView_Create((IntPtr)callbackPtr, IntPtr.Zero);
        AssertTrue(handle != IntPtr.Zero, "StringReturnClosureView handle != 0");

        var vcPtr = BridgeNativeMethods.StringReturnClosureView_GetViewController(handle);
        AssertTrue(vcPtr != IntPtr.Zero, "StringReturnClosureView GetVC != 0");

        BridgeNativeMethods.StringReturnClosureView_Free(handle);
        TestLogger.Info("StringReturnClosureView create/free: passed");
    }

    public unsafe void TestStringReturnClosureView_InvokeTransformer()
    {
        StringReturnCallbackState.Reset();

        delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr, IntPtr> callbackPtr = &StringReturnCallbackState.TransformerCallback;
        var handle = BridgeNativeMethods.StringReturnClosureView_Create((IntPtr)callbackPtr, IntPtr.Zero);
        AssertTrue(handle != IntPtr.Zero, "StringReturnClosureView invoke: handle != 0");

        // Invoke transformer(42) from Swift — our callback returns "value_42" (8 bytes)
        var resultLen = BridgeTestHelpers.StringReturnClosureView_InvokeTransformer(handle, 42);
        AssertEqual(1, StringReturnCallbackState.CallCount, "StringReturnClosureView callback fired");
        AssertEqual(42, StringReturnCallbackState.LastArg, "StringReturnClosureView arg round-trip");
        AssertEqual(8, resultLen, "StringReturnClosureView result string length (value_42)");

        BridgeNativeMethods.StringReturnClosureView_Free(handle);
        TestLogger.Info("StringReturnClosureView invoke transformer: passed");
    }

    public unsafe void TestStringReturnClosureView_MultipleInvocations()
    {
        StringReturnCallbackState.Reset();

        delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr, IntPtr> callbackPtr = &StringReturnCallbackState.TransformerCallback;
        var handle = BridgeNativeMethods.StringReturnClosureView_Create((IntPtr)callbackPtr, IntPtr.Zero);

        // Invoke multiple times with different args
        var len1 = BridgeTestHelpers.StringReturnClosureView_InvokeTransformer(handle, 1);
        AssertEqual(7, len1, "StringReturnClosureView len(value_1)");

        var len2 = BridgeTestHelpers.StringReturnClosureView_InvokeTransformer(handle, 100);
        AssertEqual(9, len2, "StringReturnClosureView len(value_100)");

        var len3 = BridgeTestHelpers.StringReturnClosureView_InvokeTransformer(handle, 0);
        AssertEqual(7, len3, "StringReturnClosureView len(value_0)");

        AssertEqual(3, StringReturnCallbackState.CallCount, "StringReturnClosureView 3 invocations");
        AssertEqual(0, StringReturnCallbackState.LastArg, "StringReturnClosureView last arg = 0");

        BridgeNativeMethods.StringReturnClosureView_Free(handle);
        TestLogger.Info("StringReturnClosureView multiple invocations: passed");
    }

    // --- ClassReturnClosureView ---

    public unsafe void TestClassReturnClosureView_CreateAndFree()
    {
        ClassReturnCallbackState.Reset();

        delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr> callbackPtr = &ClassReturnCallbackState.FactoryCallback;
        var handle = BridgeNativeMethods.ClassReturnClosureView_Create((IntPtr)callbackPtr, IntPtr.Zero);
        AssertTrue(handle != IntPtr.Zero, "ClassReturnClosureView handle != 0");

        var vcPtr = BridgeNativeMethods.ClassReturnClosureView_GetViewController(handle);
        AssertTrue(vcPtr != IntPtr.Zero, "ClassReturnClosureView GetVC != 0");

        BridgeNativeMethods.ClassReturnClosureView_Free(handle);
        TestLogger.Info("ClassReturnClosureView create/free: passed");
    }

    public unsafe void TestClassReturnClosureView_InvokeFactory()
    {
        ClassReturnCallbackState.Reset();

        delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr> callbackPtr = &ClassReturnCallbackState.FactoryCallback;
        var handle = BridgeNativeMethods.ClassReturnClosureView_Create((IntPtr)callbackPtr, IntPtr.Zero);
        AssertTrue(handle != IntPtr.Zero, "ClassReturnClosureView invoke: handle != 0");

        // Invoke factory(5) from Swift — our callback returns SimpleModel(50)
        var modelValue = BridgeTestHelpers.ClassReturnClosureView_InvokeFactory(handle, 5);
        AssertEqual(1, ClassReturnCallbackState.CallCount, "ClassReturnClosureView callback fired");
        AssertEqual(5, ClassReturnCallbackState.LastArg, "ClassReturnClosureView arg round-trip");
        AssertEqual(50, modelValue, "ClassReturnClosureView model value (5*10)");

        BridgeNativeMethods.ClassReturnClosureView_Free(handle);
        TestLogger.Info("ClassReturnClosureView invoke factory: passed");
    }

    public unsafe void TestClassReturnClosureView_MultipleInvocations()
    {
        ClassReturnCallbackState.Reset();

        delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr> callbackPtr = &ClassReturnCallbackState.FactoryCallback;
        var handle = BridgeNativeMethods.ClassReturnClosureView_Create((IntPtr)callbackPtr, IntPtr.Zero);

        // Each invocation creates a new SimpleModel via the callback
        var val1 = BridgeTestHelpers.ClassReturnClosureView_InvokeFactory(handle, 1);
        AssertEqual(10, val1, "ClassReturnClosureView factory(1) → model(10)");

        var val2 = BridgeTestHelpers.ClassReturnClosureView_InvokeFactory(handle, 7);
        AssertEqual(70, val2, "ClassReturnClosureView factory(7) → model(70)");

        AssertEqual(2, ClassReturnCallbackState.CallCount, "ClassReturnClosureView 2 invocations");

        BridgeNativeMethods.ClassReturnClosureView_Free(handle);
        TestLogger.Info("ClassReturnClosureView multiple invocations: passed");
    }

    public unsafe void TestClassReturnClosureView_ArcRetention()
    {
        ClassReturnCallbackState.Reset();
        BridgeTestHelpers.ResetSimpleModelDeinitCount();

        delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr> callbackPtr = &ClassReturnCallbackState.FactoryCallback;
        var handle = BridgeNativeMethods.ClassReturnClosureView_Create((IntPtr)callbackPtr, IntPtr.Zero);

        // Invoke factory — creates SimpleModel with Arc.Retain, Swift takes ownership via takeRetainedValue
        BridgeTestHelpers.ClassReturnClosureView_InvokeFactory(handle, 3);
        AssertEqual(1, ClassReturnCallbackState.CallCount, "ArcRetention: callback fired");

        // The model created by factory is now owned by Swift (via takeRetainedValue).
        // When the view processes it, Swift releases it. Since there's no other retain,
        // it should be deallocated. The exact timing depends on autorelease pools.

        BridgeNativeMethods.ClassReturnClosureView_Free(handle);
        TestLogger.Info("ClassReturnClosureView Arc retention: passed");
    }
}

#endif
