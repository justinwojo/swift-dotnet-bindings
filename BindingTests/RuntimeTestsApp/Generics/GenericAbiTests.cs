// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Generic ABI tests: Proves that generic @_silgen_name Swift wrappers receive
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
public class GenericAbiTests : TestBase
{
    public GenericAbiTests(TestResults results) : base(results) { }

    #region Basic TypeMetadata Passing

    /// <summary>
    /// Call a generic @_silgen_name function with Int TypeMetadata.
    /// Identity function returns the same pointer — proves the call works.
    /// </summary>
    public void TestGenericIdentityInt()
    {
        var boxPtr = GenericAbiNativeMethods.SBSW_GenericAbi_createIntBox(42);
        try
        {
            var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
            var result = GenericAbiNativeMethods.SBSW_GenericAbi_identity(boxPtr, intMetadata);

            AssertEqual(boxPtr, result, "Identity should return same pointer");
            TestLogger.Info("Generic identity with Int metadata — PASS");
        }
        finally
        {
            GenericAbiNativeMethods.SBSW_GenericAbi_releaseBox(boxPtr);
        }
    }

    /// <summary>
    /// Use TypeMetadata to query sizeof(T). Proves metadata is *correct*,
    /// not just passed. sizeof(Int)==8, sizeof(Bool)==1, sizeof(Int32)==4, sizeof(Double)==8.
    /// </summary>
    public unsafe void TestSizeOfTWithMetadata()
    {
        nint result = 0;
        var boxPtr = GenericAbiNativeMethods.SBSW_GenericAbi_createIntBox(1); // dummy self_

        try
        {
            // Int (nint on arm64 = 8 bytes) — pass metadata TWICE (explicit T.Type + implicit)
            var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
            GenericAbiNativeMethods.SBSW_GenericAbi_sizeOfT(boxPtr, (IntPtr)(&result), intMetadata, intMetadata);
            AssertEqual((nint)8, result, "sizeof(Int) should be 8 on arm64");

            // Bool (1 byte)
            var boolMetadata = TypeMetadata.GetTypeMetadataOrThrow<bool>();
            GenericAbiNativeMethods.SBSW_GenericAbi_sizeOfT(boxPtr, (IntPtr)(&result), boolMetadata, boolMetadata);
            AssertEqual((nint)1, result, "sizeof(Bool) should be 1");

            // Int32 (4 bytes)
            var int32Metadata = TypeMetadata.GetTypeMetadataOrThrow<int>();
            GenericAbiNativeMethods.SBSW_GenericAbi_sizeOfT(boxPtr, (IntPtr)(&result), int32Metadata, int32Metadata);
            AssertEqual((nint)4, result, "sizeof(Int32) should be 4");

            // Double (8 bytes)
            var doubleMetadata = TypeMetadata.GetTypeMetadataOrThrow<double>();
            GenericAbiNativeMethods.SBSW_GenericAbi_sizeOfT(boxPtr, (IntPtr)(&result), doubleMetadata, doubleMetadata);
            AssertEqual((nint)8, result, "sizeof(Double) should be 8");

            TestLogger.Info("sizeOfT with various TypeMetadata — PASS");
        }
        finally
        {
            GenericAbiNativeMethods.SBSW_GenericAbi_releaseBox(boxPtr);
        }
    }

    /// <summary>
    /// Stride of T. stride(Int) == 8 on arm64.
    /// </summary>
    public unsafe void TestStrideOfTWithMetadata()
    {
        nint result = 0;
        var boxPtr = GenericAbiNativeMethods.SBSW_GenericAbi_createIntBox(1);
        try
        {
            var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
            GenericAbiNativeMethods.SBSW_GenericAbi_strideOfT(boxPtr, (IntPtr)(&result), intMetadata, intMetadata);
            AssertEqual((nint)8, result, "stride(Int) should be 8 on arm64");
            TestLogger.Info("strideOfT — PASS");
        }
        finally
        {
            GenericAbiNativeMethods.SBSW_GenericAbi_releaseBox(boxPtr);
        }
    }

    #endregion

    #region Filter Callback — Closure Round-Trip

    /// <summary>
    /// Filter with Int element. C# predicate receives the Int value via pointer.
    /// Proves value-type closure round-trip through generic wrapper.
    /// </summary>
    public unsafe void TestFilterIntElement()
    {
        var boxPtr = GenericAbiNativeMethods.SBSW_GenericAbi_createIntBox(42);
        try
        {
            var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
            byte resultBuf = 0;

            // Predicate: value > 10 → true
            var callbackPtr = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&FilterIntCallback_GreaterThan10;
            GenericAbiNativeMethods.SBSW_GenericAbi_filter(
                boxPtr, callbackPtr, IntPtr.Zero, (IntPtr)(&resultBuf), intMetadata, intMetadata);
            AssertTrue(resultBuf != 0, "42 > 10 should be true");

            // Predicate: value > 100 → false
            var callbackPtr2 = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&FilterIntCallback_GreaterThan100;
            GenericAbiNativeMethods.SBSW_GenericAbi_filter(
                boxPtr, callbackPtr2, IntPtr.Zero, (IntPtr)(&resultBuf), intMetadata, intMetadata);
            AssertTrue(resultBuf == 0, "42 > 100 should be false");

            TestLogger.Info("Filter with Int element — PASS");
        }
        finally
        {
            GenericAbiNativeMethods.SBSW_GenericAbi_releaseBox(boxPtr);
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
    /// Filter with GCHandle context. Passes a threshold value via context.
    /// Proves context pointer round-trips correctly through the generic wrapper.
    /// </summary>
    public unsafe void TestFilterWithContext()
    {
        var boxPtr = GenericAbiNativeMethods.SBSW_GenericAbi_createIntBox(42);
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
                GenericAbiNativeMethods.SBSW_GenericAbi_filter(
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
                GenericAbiNativeMethods.SBSW_GenericAbi_filter(
                    boxPtr, callbackPtr, GCHandle.ToIntPtr(gcHandle2), (IntPtr)(&resultBuf), intMetadata, intMetadata);
                AssertTrue(resultBuf != 0, "42 > 30 threshold should be true");
            }
            finally
            {
                gcHandle2.Free();
            }

            TestLogger.Info("Filter with GCHandle context — PASS");
        }
        finally
        {
            GenericAbiNativeMethods.SBSW_GenericAbi_releaseBox(boxPtr);
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

    #region Map with Two Generic Parameters

    /// <summary>
    /// Map Int→Int (double the value). Proves two metadata params
    /// (Element + Result) are both passed correctly via explicit T.Type params.
    /// </summary>
    public unsafe void TestMapIntToInt()
    {
        var boxPtr = GenericAbiNativeMethods.SBSW_GenericAbi_createIntBox(21);
        try
        {
            var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();

            // Allocate result buffer for Int (8 bytes on arm64)
            nint resultVal = 0;
            var callbackPtr = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&MapIntDoubleCallback;

            // Two metadata params: Element=Int, Result=Int
            GenericAbiNativeMethods.SBSW_GenericAbi_map(
                boxPtr, callbackPtr, IntPtr.Zero, (IntPtr)(&resultVal),
                intMetadata, intMetadata, intMetadata, intMetadata);

            AssertEqual((nint)42, resultVal, "21 * 2 should be 42");
            TestLogger.Info("Map Int→Int — PASS");
        }
        finally
        {
            GenericAbiNativeMethods.SBSW_GenericAbi_releaseBox(boxPtr);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void MapIntDoubleCallback(IntPtr elementPtr, IntPtr resultBuf, IntPtr context)
    {
        var value = *(nint*)elementPtr;
        *(nint*)resultBuf = value * 2;
    }

    /// <summary>
    /// Map Int→Bool (isEven). Proves Element and Result can be different types
    /// with different metadata. This is the critical test for method-level generics.
    /// </summary>
    public unsafe void TestMapIntToBool()
    {
        var boxPtr = GenericAbiNativeMethods.SBSW_GenericAbi_createIntBox(42);
        try
        {
            var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
            var boolMetadata = TypeMetadata.GetTypeMetadataOrThrow<bool>();

            byte resultVal = 0;
            var callbackPtr = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&MapIntIsEvenCallback;

            // Two DIFFERENT metadata params: Element=Int, Result=Bool
            GenericAbiNativeMethods.SBSW_GenericAbi_map(
                boxPtr, callbackPtr, IntPtr.Zero, (IntPtr)(&resultVal),
                intMetadata, boolMetadata, intMetadata, boolMetadata);

            AssertTrue(resultVal != 0, "42 is even → true");
            TestLogger.Info("Map Int→Bool (different metadata) — PASS");
        }
        finally
        {
            GenericAbiNativeMethods.SBSW_GenericAbi_releaseBox(boxPtr);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void MapIntIsEvenCallback(IntPtr elementPtr, IntPtr resultBuf, IntPtr context)
    {
        var value = *(nint*)elementPtr;
        *(byte*)resultBuf = (byte)(value % 2 == 0 ? 1 : 0);
    }

    #endregion

    #region Error Propagation

    /// <summary>
    /// Filter that succeeds (no error). Error out-param should be null.
    /// </summary>
    public unsafe void TestFilterThrowsNoError()
    {
        var boxPtr = GenericAbiNativeMethods.SBSW_GenericAbi_createIntBox(42);
        try
        {
            var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
            var callbackPtr = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte>)&FilterThrowsCallback_NoError;

            byte resultBuf = 0;
            IntPtr errorPtr = IntPtr.Zero;
            GenericAbiNativeMethods.SBSW_GenericAbi_filterThrows(
                boxPtr, callbackPtr, IntPtr.Zero, (IntPtr)(&resultBuf), &errorPtr, intMetadata, intMetadata);

            AssertTrue(resultBuf != 0, "Predicate should return true");
            AssertEqual(IntPtr.Zero, errorPtr, "Error should be null");

            TestLogger.Info("FilterThrows no error — PASS");
        }
        finally
        {
            GenericAbiNativeMethods.SBSW_GenericAbi_releaseBox(boxPtr);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe byte FilterThrowsCallback_NoError(
        IntPtr elementPtr, IntPtr errorOutPtr, IntPtr context)
    {
        return 1;
    }

    /// <summary>
    /// Filter that propagates an error from C# → Swift → C#.
    /// The callback creates an NSError and writes it to the error out-param.
    /// The wrapper passes it through to the caller.
    /// </summary>
    public unsafe void TestFilterThrowsWithError()
    {
        var boxPtr = GenericAbiNativeMethods.SBSW_GenericAbi_createIntBox(42);
        try
        {
            var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
            var callbackPtr = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte>)&FilterThrowsCallback_WithError;

            byte resultBuf = 0;
            IntPtr errorPtr = IntPtr.Zero;
            GenericAbiNativeMethods.SBSW_GenericAbi_filterThrows(
                boxPtr, callbackPtr, IntPtr.Zero, (IntPtr)(&resultBuf), &errorPtr, intMetadata, intMetadata);

            AssertTrue(resultBuf == 0, "Result should be false when error occurs");
            AssertTrue(errorPtr != IntPtr.Zero, "Error pointer should be non-null");

            // Extract error description
            var descPtr = GenericAbiNativeMethods.SBSW_GenericAbi_getErrorDescription(errorPtr);
            try
            {
                var desc = Marshal.PtrToStringUTF8(descPtr) ?? "";
                AssertTrue(desc.Contains("generic abi test error"), $"Error should contain our message, got: {desc}");
                TestLogger.Info($"Error message: '{desc}'");
            }
            finally
            {
                if (descPtr != IntPtr.Zero)
                    NativeMemory.Free((void*)descPtr);
                GenericAbiNativeMethods.SBSW_GenericAbi_releaseError(errorPtr);
            }

            TestLogger.Info("FilterThrows with error propagation — PASS");
        }
        finally
        {
            GenericAbiNativeMethods.SBSW_GenericAbi_releaseBox(boxPtr);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe byte FilterThrowsCallback_WithError(
        IntPtr elementPtr, IntPtr errorOutPtr, IntPtr context)
    {
        var msgBytes = System.Text.Encoding.UTF8.GetBytes("generic abi test error\0");
        fixed (byte* msgPtr = msgBytes)
        {
            var errorObj = GenericAbiNativeMethods.SBSW_GenericAbi_createError((IntPtr)msgPtr);
            *(IntPtr*)errorOutPtr = errorObj;
        }
        return 0;
    }

    #endregion
}

/// <summary>
/// P/Invoke declarations for generic ABI wrappers.
/// All use CallConvSwift to test TypeMetadata passing via explicit T.Type params.
///
/// ABI insight: In Swift, `T.Type` is ABI-equivalent to `TypeMetadata*`.
/// C# passes `TypeMetadata` (wrapping IntPtr) in the same parameter position.
/// All results via out-param buffers to avoid Mono JIT crash on return values.
/// </summary>
internal static partial class GenericAbiNativeMethods
{
    // identity<T>(value, T.Type) → UnsafeMutableRawPointer
    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBSW_GenericAbi_identity")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static partial IntPtr SBSW_GenericAbi_identity(IntPtr value, TypeMetadata tMetadata);

    // sizeOfT<T>(self_, resultBuf, T.Type, /*implicit*/ T_metadata)
    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBSW_GenericAbi_sizeOfT")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static partial void SBSW_GenericAbi_sizeOfT(IntPtr self_, IntPtr resultBuf,
        TypeMetadata explicitType, TypeMetadata implicitMetadata);

    // strideOfT<T>(self_, resultBuf, T.Type, /*implicit*/ T_metadata)
    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBSW_GenericAbi_strideOfT")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static partial void SBSW_GenericAbi_strideOfT(IntPtr self_, IntPtr resultBuf,
        TypeMetadata explicitType, TypeMetadata implicitMetadata);

    // filter<Element>(self_, funcPtr, ctx, resultBuf, Element.Type, /*implicit*/ Element_metadata)
    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBSW_GenericAbi_filter")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static partial void SBSW_GenericAbi_filter(
        IntPtr self_, IntPtr predicateFuncPtr, IntPtr predicateContext,
        IntPtr resultBuf, TypeMetadata explicitElementType, TypeMetadata implicitElementMetadata);

    // map<Element, Result>(self_, funcPtr, ctx, resultBuf, Element.Type, Result.Type,
    //                      /*implicit*/ Element_metadata, Result_metadata)
    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBSW_GenericAbi_map")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static partial void SBSW_GenericAbi_map(
        IntPtr self_, IntPtr transformFuncPtr, IntPtr transformContext, IntPtr resultBuf,
        TypeMetadata explicitElementType, TypeMetadata explicitResultType,
        TypeMetadata implicitElementMetadata, TypeMetadata implicitResultMetadata);

    // filterThrows<Element>(self_, funcPtr, ctx, resultBuf, errorOut, Element.Type,
    //                       /*implicit*/ Element_metadata)
    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBSW_GenericAbi_filterThrows")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static unsafe partial void SBSW_GenericAbi_filterThrows(
        IntPtr self_, IntPtr predicateFuncPtr, IntPtr predicateContext,
        IntPtr resultBuf, IntPtr* errorOut,
        TypeMetadata explicitElementType, TypeMetadata implicitElementMetadata);

    // Helper: Create GenericAbiBox<Int>
    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBSW_GenericAbi_createIntBox")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static partial IntPtr SBSW_GenericAbi_createIntBox(nint value);

    // Helper: Release GenericAbiBox
    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBSW_GenericAbi_releaseBox")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static partial void SBSW_GenericAbi_releaseBox(IntPtr ptr);

    // Error helpers
    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBSW_GenericAbi_createError")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static partial IntPtr SBSW_GenericAbi_createError(IntPtr message);

    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBSW_GenericAbi_getErrorDescription")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static partial IntPtr SBSW_GenericAbi_getErrorDescription(IntPtr errorPtr);

    [LibraryImport("SwiftBindingsTestLib", EntryPoint = "SBSW_GenericAbi_releaseError")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    internal static partial void SBSW_GenericAbi_releaseError(IntPtr errorPtr);
}
