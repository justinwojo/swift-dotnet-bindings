// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;

namespace RuntimeTestsApp.Spike;

/// <summary>
/// Session 7 ABI Spike: Proves that generic @_silgen_name Swift wrappers receive
/// TypeMetadata correctly from C# via CallConvSwift P/Invoke.
///
/// Key findings:
/// 1. Swift 6 requires generic params in function signatures → use explicit `T.Type`
/// 2. `T.Type` is ABI-equivalent to `TypeMetadata` → C# passes TypeMetadata directly
/// 3. Mono JIT crash on certain return types → use out-param buffers for results
///
/// All results written via out-param pointers to avoid Mono JIT assertion crash
/// (jit-info.c:918) on return values from CallConvSwift P/Invoke.
///
/// Tier 1: All tests (blittable only, no Mono JIT risk via buffer pattern)
/// </summary>
public class GenericAbiSpikeTests : TestBase
{
    public GenericAbiSpikeTests(TestResults results) : base(results) { }

    #region S1: Basic TypeMetadata Passing

    /// <summary>
    /// S1a: Call a generic @_silgen_name function with Int TypeMetadata.
    /// Identity function returns the same pointer — proves the call works.
    /// </summary>
    public void TestGenericIdentityInt()
    {
        var boxPtr = SpikeNativeMethods.SBW_Spike_createIntBox(42);
        try
        {
            var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
            var result = SpikeNativeMethods.SBW_Spike_identity(boxPtr, intMetadata);

            AssertEqual(boxPtr, result, "Identity should return same pointer");
            TestLogger.Info("S1a: Generic identity with Int metadata — PASS");
        }
        finally
        {
            SpikeNativeMethods.SBW_Spike_releaseBox(boxPtr);
        }
    }

    /// <summary>
    /// S1b: Use TypeMetadata to query sizeof(T). Proves metadata is *correct*,
    /// not just passed. sizeof(Int)==8, sizeof(Bool)==1, sizeof(Int32)==4, sizeof(Double)==8.
    /// </summary>
    public unsafe void TestSizeOfTWithMetadata()
    {
        nint result = 0;
        var boxPtr = SpikeNativeMethods.SBW_Spike_createIntBox(1); // dummy self_

        try
        {
            // Int (nint on arm64 = 8 bytes) — pass metadata TWICE (explicit T.Type + implicit)
            var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
            SpikeNativeMethods.SBW_Spike_sizeOfT(boxPtr, (IntPtr)(&result), intMetadata, intMetadata);
            AssertEqual((nint)8, result, "sizeof(Int) should be 8 on arm64");

            // Bool (1 byte)
            var boolMetadata = TypeMetadata.GetTypeMetadataOrThrow<bool>();
            SpikeNativeMethods.SBW_Spike_sizeOfT(boxPtr, (IntPtr)(&result), boolMetadata, boolMetadata);
            AssertEqual((nint)1, result, "sizeof(Bool) should be 1");

            // Int32 (4 bytes)
            var int32Metadata = TypeMetadata.GetTypeMetadataOrThrow<int>();
            SpikeNativeMethods.SBW_Spike_sizeOfT(boxPtr, (IntPtr)(&result), int32Metadata, int32Metadata);
            AssertEqual((nint)4, result, "sizeof(Int32) should be 4");

            // Double (8 bytes)
            var doubleMetadata = TypeMetadata.GetTypeMetadataOrThrow<double>();
            SpikeNativeMethods.SBW_Spike_sizeOfT(boxPtr, (IntPtr)(&result), doubleMetadata, doubleMetadata);
            AssertEqual((nint)8, result, "sizeof(Double) should be 8");

            TestLogger.Info("S1b: sizeOfT with various TypeMetadata — PASS");
        }
        finally
        {
            SpikeNativeMethods.SBW_Spike_releaseBox(boxPtr);
        }
    }

    /// <summary>
    /// S1c: Stride of T. stride(Int) == 8 on arm64.
    /// </summary>
    public unsafe void TestStrideOfTWithMetadata()
    {
        nint result = 0;
        var boxPtr = SpikeNativeMethods.SBW_Spike_createIntBox(1);
        try
        {
            var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
            SpikeNativeMethods.SBW_Spike_strideOfT(boxPtr, (IntPtr)(&result), intMetadata, intMetadata);
            AssertEqual((nint)8, result, "stride(Int) should be 8 on arm64");
            TestLogger.Info("S1c: strideOfT — PASS");
        }
        finally
        {
            SpikeNativeMethods.SBW_Spike_releaseBox(boxPtr);
        }
    }

    #endregion

    #region S2: Filter Callback — Closure Round-Trip

    /// <summary>
    /// S2a: Filter with Int element. C# predicate receives the Int value via pointer.
    /// Proves value-type closure round-trip through generic wrapper.
    /// </summary>
    public unsafe void TestFilterIntElement()
    {
        var boxPtr = SpikeNativeMethods.SBW_Spike_createIntBox(42);
        try
        {
            var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
            byte resultBuf = 0;

            // Predicate: value > 10 → true
            var callbackPtr = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&FilterIntCallback_GreaterThan10;
            SpikeNativeMethods.SBW_Spike_filter(
                boxPtr, callbackPtr, IntPtr.Zero, (IntPtr)(&resultBuf), intMetadata, intMetadata);
            AssertTrue(resultBuf != 0, "42 > 10 should be true");

            // Predicate: value > 100 → false
            var callbackPtr2 = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&FilterIntCallback_GreaterThan100;
            SpikeNativeMethods.SBW_Spike_filter(
                boxPtr, callbackPtr2, IntPtr.Zero, (IntPtr)(&resultBuf), intMetadata, intMetadata);
            AssertTrue(resultBuf == 0, "42 > 100 should be false");

            TestLogger.Info("S2a: Filter with Int element — PASS");
        }
        finally
        {
            SpikeNativeMethods.SBW_Spike_releaseBox(boxPtr);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe byte FilterIntCallback_GreaterThan10(IntPtr elementPtr, IntPtr context)
    {
        var value = *(nint*)elementPtr;
        return (byte)(value > 10 ? 1 : 0);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe byte FilterIntCallback_GreaterThan100(IntPtr elementPtr, IntPtr context)
    {
        var value = *(nint*)elementPtr;
        return (byte)(value > 100 ? 1 : 0);
    }

    /// <summary>
    /// S2b: Filter with GCHandle context. Passes a threshold value via context.
    /// Proves context pointer round-trips correctly through the generic wrapper.
    /// </summary>
    public unsafe void TestFilterWithContext()
    {
        var boxPtr = SpikeNativeMethods.SBW_Spike_createIntBox(42);
        try
        {
            var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
            byte resultBuf = 0;
            var callbackPtr = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&FilterIntCallback_WithContext;

            // Pass threshold=50 via GCHandle context — 42 < 50 → false
            var state = new object[] { (nint)50 };
            var gcHandle = GCHandle.Alloc(state);
            try
            {
                SpikeNativeMethods.SBW_Spike_filter(
                    boxPtr, callbackPtr, GCHandle.ToIntPtr(gcHandle), (IntPtr)(&resultBuf), intMetadata, intMetadata);
                AssertTrue(resultBuf == 0, "42 < 50 threshold should be false");
            }
            finally
            {
                gcHandle.Free();
            }

            // threshold=30 → 42 > 30 → true
            var state2 = new object[] { (nint)30 };
            var gcHandle2 = GCHandle.Alloc(state2);
            try
            {
                SpikeNativeMethods.SBW_Spike_filter(
                    boxPtr, callbackPtr, GCHandle.ToIntPtr(gcHandle2), (IntPtr)(&resultBuf), intMetadata, intMetadata);
                AssertTrue(resultBuf != 0, "42 > 30 threshold should be true");
            }
            finally
            {
                gcHandle2.Free();
            }

            TestLogger.Info("S2b: Filter with GCHandle context — PASS");
        }
        finally
        {
            SpikeNativeMethods.SBW_Spike_releaseBox(boxPtr);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe byte FilterIntCallback_WithContext(IntPtr elementPtr, IntPtr context)
    {
        var value = *(nint*)elementPtr;
        var handle = GCHandle.FromIntPtr(context);
        var state = (object[])handle.Target!;
        var threshold = (nint)state[0];
        return (byte)(value > threshold ? 1 : 0);
    }

    #endregion

    #region S3: Map with Two Generic Parameters

    /// <summary>
    /// S3a: Map Int→Int (double the value). Proves two metadata params
    /// (Element + Result) are both passed correctly via explicit T.Type params.
    /// </summary>
    public unsafe void TestMapIntToInt()
    {
        var boxPtr = SpikeNativeMethods.SBW_Spike_createIntBox(21);
        try
        {
            var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();

            // Allocate result buffer for Int (8 bytes on arm64)
            nint resultVal = 0;
            var callbackPtr = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&MapIntDoubleCallback;

            // Two metadata params: Element=Int, Result=Int
            SpikeNativeMethods.SBW_Spike_map(
                boxPtr, callbackPtr, IntPtr.Zero, (IntPtr)(&resultVal),
                intMetadata, intMetadata, intMetadata, intMetadata);

            AssertEqual((nint)42, resultVal, "21 * 2 should be 42");
            TestLogger.Info("S3a: Map Int→Int — PASS");
        }
        finally
        {
            SpikeNativeMethods.SBW_Spike_releaseBox(boxPtr);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void MapIntDoubleCallback(IntPtr elementPtr, IntPtr resultBuf, IntPtr context)
    {
        var value = *(nint*)elementPtr;
        *(nint*)resultBuf = value * 2;
    }

    /// <summary>
    /// S3b: Map Int→Bool (isEven). Proves Element and Result can be different types
    /// with different metadata. This is the critical test for method-level generics.
    /// </summary>
    public unsafe void TestMapIntToBool()
    {
        var boxPtr = SpikeNativeMethods.SBW_Spike_createIntBox(42);
        try
        {
            var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
            var boolMetadata = TypeMetadata.GetTypeMetadataOrThrow<bool>();

            byte resultVal = 0;
            var callbackPtr = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&MapIntIsEvenCallback;

            // Two DIFFERENT metadata params: Element=Int, Result=Bool
            SpikeNativeMethods.SBW_Spike_map(
                boxPtr, callbackPtr, IntPtr.Zero, (IntPtr)(&resultVal),
                intMetadata, boolMetadata, intMetadata, boolMetadata);

            AssertTrue(resultVal != 0, "42 is even → true");
            TestLogger.Info("S3b: Map Int→Bool (different metadata) — PASS");
        }
        finally
        {
            SpikeNativeMethods.SBW_Spike_releaseBox(boxPtr);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void MapIntIsEvenCallback(IntPtr elementPtr, IntPtr resultBuf, IntPtr context)
    {
        var value = *(nint*)elementPtr;
        *(byte*)resultBuf = (byte)(value % 2 == 0 ? 1 : 0);
    }

    #endregion

    #region S4: Error Propagation

    /// <summary>
    /// S4a: Filter that succeeds (no error). Error out-param should be null.
    /// </summary>
    public unsafe void TestFilterThrowsNoError()
    {
        var boxPtr = SpikeNativeMethods.SBW_Spike_createIntBox(42);
        try
        {
            var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
            var callbackPtr = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte>)&FilterThrowsCallback_NoError;

            byte resultBuf = 0;
            IntPtr errorPtr = IntPtr.Zero;
            SpikeNativeMethods.SBW_Spike_filterThrows(
                boxPtr, callbackPtr, IntPtr.Zero, (IntPtr)(&resultBuf), &errorPtr, intMetadata, intMetadata);

            AssertTrue(resultBuf != 0, "Predicate should return true");
            AssertEqual(IntPtr.Zero, errorPtr, "Error should be null");

            TestLogger.Info("S4a: FilterThrows no error — PASS");
        }
        finally
        {
            SpikeNativeMethods.SBW_Spike_releaseBox(boxPtr);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe byte FilterThrowsCallback_NoError(
        IntPtr elementPtr, IntPtr errorOutPtr, IntPtr context)
    {
        return 1;
    }

    /// <summary>
    /// S4b: Filter that propagates an error from C# → Swift → C#.
    /// The callback creates an NSError via SBW_Spike_createError and writes it
    /// to the error out-param. The wrapper passes it through to the caller.
    /// </summary>
    public unsafe void TestFilterThrowsWithError()
    {
        var boxPtr = SpikeNativeMethods.SBW_Spike_createIntBox(42);
        try
        {
            var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
            var callbackPtr = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte>)&FilterThrowsCallback_WithError;

            byte resultBuf = 0;
            IntPtr errorPtr = IntPtr.Zero;
            SpikeNativeMethods.SBW_Spike_filterThrows(
                boxPtr, callbackPtr, IntPtr.Zero, (IntPtr)(&resultBuf), &errorPtr, intMetadata, intMetadata);

            AssertTrue(resultBuf == 0, "Result should be false when error occurs");
            AssertTrue(errorPtr != IntPtr.Zero, "Error pointer should be non-null");

            // Extract error description
            var descPtr = SpikeNativeMethods.SBW_Spike_getErrorDescription(errorPtr);
            try
            {
                var desc = Marshal.PtrToStringUTF8(descPtr) ?? "";
                AssertTrue(desc.Contains("spike test error"), $"Error should contain our message, got: {desc}");
                TestLogger.Info($"S4b: Error message: '{desc}'");
            }
            finally
            {
                if (descPtr != IntPtr.Zero)
                    NativeMemory.Free((void*)descPtr);
                SpikeNativeMethods.SBW_Spike_releaseError(errorPtr);
            }

            TestLogger.Info("S4b: FilterThrows with error propagation — PASS");
        }
        finally
        {
            SpikeNativeMethods.SBW_Spike_releaseBox(boxPtr);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe byte FilterThrowsCallback_WithError(
        IntPtr elementPtr, IntPtr errorOutPtr, IntPtr context)
    {
        var msgBytes = System.Text.Encoding.UTF8.GetBytes("spike test error\0");
        fixed (byte* msgPtr = msgBytes)
        {
            var errorObj = SpikeNativeMethods.SBW_Spike_createError((IntPtr)msgPtr);
            *(IntPtr*)errorOutPtr = errorObj;
        }
        return 0;
    }

    #endregion
}

/// <summary>
/// P/Invoke declarations for Session 7 ABI Spike wrappers.
/// All use CallConvSwift to test TypeMetadata passing via explicit T.Type params.
///
/// ABI insight: In Swift, `T.Type` is ABI-equivalent to `TypeMetadata*`.
/// C# passes `TypeMetadata` (wrapping IntPtr) in the same parameter position.
/// All results via out-param buffers to avoid Mono JIT crash on return values.
/// </summary>
internal static partial class SpikeNativeMethods
{
    // S1: identity<T>(value, T.Type) → UnsafeMutableRawPointer
    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBW_Spike_identity")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static partial IntPtr SBW_Spike_identity(IntPtr value, TypeMetadata tMetadata);

    // S1: sizeOfT<T>(self_, resultBuf, T.Type, /*implicit*/ T_metadata)
    // Theory: Swift generic @_silgen_name adds IMPLICIT metadata after explicit params
    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBW_Spike_sizeOfT")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static partial void SBW_Spike_sizeOfT(IntPtr self_, IntPtr resultBuf,
        TypeMetadata explicitType, TypeMetadata implicitMetadata);

    // S1: strideOfT<T>(self_, resultBuf, T.Type, /*implicit*/ T_metadata)
    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBW_Spike_strideOfT")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static partial void SBW_Spike_strideOfT(IntPtr self_, IntPtr resultBuf,
        TypeMetadata explicitType, TypeMetadata implicitMetadata);

    // S2: filter<Element>(self_, funcPtr, ctx, resultBuf, Element.Type, /*implicit*/ Element_metadata)
    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBW_Spike_filter")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static partial void SBW_Spike_filter(
        IntPtr self_, IntPtr predicateFuncPtr, IntPtr predicateContext,
        IntPtr resultBuf, TypeMetadata explicitElementType, TypeMetadata implicitElementMetadata);

    // S3: map<Element, Result>(self_, funcPtr, ctx, resultBuf, Element.Type, Result.Type,
    //                          /*implicit*/ Element_metadata, Result_metadata)
    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBW_Spike_map")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static partial void SBW_Spike_map(
        IntPtr self_, IntPtr transformFuncPtr, IntPtr transformContext, IntPtr resultBuf,
        TypeMetadata explicitElementType, TypeMetadata explicitResultType,
        TypeMetadata implicitElementMetadata, TypeMetadata implicitResultMetadata);

    // S4: filterThrows<Element>(self_, funcPtr, ctx, resultBuf, errorOut, Element.Type,
    //                           /*implicit*/ Element_metadata)
    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBW_Spike_filterThrows")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static unsafe partial void SBW_Spike_filterThrows(
        IntPtr self_, IntPtr predicateFuncPtr, IntPtr predicateContext,
        IntPtr resultBuf, IntPtr* errorOut,
        TypeMetadata explicitElementType, TypeMetadata implicitElementMetadata);

    // Helper: Create SpikeBox<Int>
    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBW_Spike_createIntBox")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static partial IntPtr SBW_Spike_createIntBox(nint value);

    // Helper: Release SpikeBox
    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBW_Spike_releaseBox")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static partial void SBW_Spike_releaseBox(IntPtr ptr);

    // Error helpers
    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBW_Spike_createError")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static partial IntPtr SBW_Spike_createError(IntPtr message);

    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBW_Spike_getErrorDescription")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static partial IntPtr SBW_Spike_getErrorDescription(IntPtr errorPtr);

    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBW_Spike_releaseError")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static partial void SBW_Spike_releaseError(IntPtr errorPtr);
}
