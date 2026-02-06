// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if SWIFTUI_BRIDGE

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RuntimeTestsApp.SwiftUIBridge;

#region P/Invoke Declarations - Bridge Functions

internal static class BridgeNativeMethods
{
    private const string BridgeLib = "SwiftBindingsTestLibBridge";

    // --- EnumParamView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_EnumParamView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr EnumParamView_Create(int style);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_EnumParamView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr EnumParamView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_EnumParamView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void EnumParamView_Free(IntPtr handle);

    // --- ClassParamView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ClassParamView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr ClassParamView_Create(IntPtr modelPtr);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ClassParamView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr ClassParamView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ClassParamView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void ClassParamView_Free(IntPtr handle);

    // --- TypedClosureView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_TypedClosureView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr TypedClosureView_Create(IntPtr onValueCallback, IntPtr onValueUserData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_TypedClosureView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr TypedClosureView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_TypedClosureView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void TypedClosureView_Free(IntPtr handle);

    // --- MultiArgClosureView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MultiArgClosureView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr MultiArgClosureView_Create(IntPtr onEventCallback, IntPtr onEventUserData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MultiArgClosureView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr MultiArgClosureView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MultiArgClosureView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void MultiArgClosureView_Free(IntPtr handle);

    // --- MixedParamView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MixedParamView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr MixedParamView_Create(int style, IntPtr onActionCallback, IntPtr onActionUserData, int count);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MixedParamView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr MixedParamView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MixedParamView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void MixedParamView_Free(IntPtr handle);

    // --- OptionalEnumView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalEnumView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr OptionalEnumView_Create(int styleHasValue, int styleValue);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalEnumView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr OptionalEnumView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalEnumView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void OptionalEnumView_Free(IntPtr handle);

    // --- OptionalClassView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalClassView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr OptionalClassView_Create(IntPtr modelPtr);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalClassView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr OptionalClassView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalClassView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void OptionalClassView_Free(IntPtr handle);

    // --- AsyncServiceView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_AsyncServiceView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void AsyncServiceView_Create(
        IntPtr keyPtr, nint keyLen,
        IntPtr onReady, IntPtr onError, IntPtr userData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_AsyncServiceView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr AsyncServiceView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_AsyncServiceView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void AsyncServiceView_Free(IntPtr handle);

    // --- DeepChainView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_DeepChainView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void DeepChainView_Create(
        IntPtr keyPtr, nint keyLen, int mode,
        IntPtr onReady, IntPtr onError, IntPtr userData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_DeepChainView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr DeepChainView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_DeepChainView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void DeepChainView_Free(IntPtr handle);

    // --- MixedAsyncView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MixedAsyncView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void MixedAsyncView_Create(
        IntPtr keyPtr, nint keyLen, int count, int enabled,
        IntPtr onReady, IntPtr onError, IntPtr userData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MixedAsyncView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr MixedAsyncView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MixedAsyncView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void MixedAsyncView_Free(IntPtr handle);
}

#endregion

#region P/Invoke Declarations - Test Helpers

internal static class BridgeTestHelpers
{
    private const string BridgeLib = "SwiftBindingsTestLibBridge";

    // SimpleModel helpers
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_CreateSimpleModel")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr CreateSimpleModel(int value);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_FreeSimpleModel")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void FreeSimpleModel(IntPtr ptr);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_GetSimpleModelDeinitCount")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int GetSimpleModelDeinitCount();

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_ResetSimpleModelDeinitCount")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void ResetSimpleModelDeinitCount();

    // EnumParamView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_EnumParamView_GetStyle")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int EnumParamView_GetStyle(IntPtr handle);

    // ClassParamView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_ClassParamView_GetModelValue")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int ClassParamView_GetModelValue(IntPtr handle);

    // TypedClosureView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_TypedClosureView_InvokeClosure")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int TypedClosureView_InvokeClosure(IntPtr handle, int value);

    // MultiArgClosureView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_MultiArgClosureView_InvokeClosure")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int MultiArgClosureView_InvokeClosure(IntPtr handle, int val, int flag);

    // MixedParamView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_MixedParamView_GetStyle")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int MixedParamView_GetStyle(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_MixedParamView_FireAction")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int MixedParamView_FireAction(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_MixedParamView_GetCount")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int MixedParamView_GetCount(IntPtr handle);

    // OptionalEnumView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_OptionalEnumView_HasValue")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int OptionalEnumView_HasValue(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_OptionalEnumView_GetStyle")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int OptionalEnumView_GetStyle(IntPtr handle);

    // OptionalClassView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_OptionalClassView_HasValue")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int OptionalClassView_HasValue(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_OptionalClassView_GetModelValue")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int OptionalClassView_GetModelValue(IntPtr handle);
}

#endregion

#endif
