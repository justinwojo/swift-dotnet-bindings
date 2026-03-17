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

    // --- StringClosureView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_StringClosureView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr StringClosureView_Create(IntPtr onResultCallback, IntPtr onResultUserData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_StringClosureView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr StringClosureView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_StringClosureView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void StringClosureView_Free(IntPtr handle);

    // --- ClassClosureView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ClassClosureView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr ClassClosureView_Create(IntPtr onModelCallback, IntPtr onModelUserData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ClassClosureView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr ClassClosureView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ClassClosureView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void ClassClosureView_Free(IntPtr handle);

    // --- OptionalStringView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalStringView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr OptionalStringView_Create(IntPtr titlePtr, nint titleLen);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalStringView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr OptionalStringView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalStringView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void OptionalStringView_Free(IntPtr handle);

    // --- OptionalClosureView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalClosureView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr OptionalClosureView_Create(IntPtr callbackCallback, IntPtr callbackUserData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalClosureView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr OptionalClosureView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalClosureView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void OptionalClosureView_Free(IntPtr handle);

    // --- MixedStringView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MixedStringView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr MixedStringView_Create(IntPtr titlePtr, nint titleLen, IntPtr onResultCallback, IntPtr onResultUserData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MixedStringView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr MixedStringView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MixedStringView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void MixedStringView_Free(IntPtr handle);

    // --- UpdatableCounterView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_UpdatableCounterView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr UpdatableCounterView_Create(int count, IntPtr labelPtr, nint labelLen);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_UpdatableCounterView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr UpdatableCounterView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_UpdatableCounterView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void UpdatableCounterView_Free(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_UpdatableCounterView_UpdateCount")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void UpdatableCounterView_UpdateCount(IntPtr handle, int newValue);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_UpdatableCounterView_UpdateLabel")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void UpdatableCounterView_UpdateLabel(IntPtr handle, IntPtr newValuePtr, nint newValueLen);

    // --- UpdatableMixedView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_UpdatableMixedView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr UpdatableMixedView_Create(IntPtr titlePtr, nint titleLen, int isEnabled, IntPtr onTapCallback, IntPtr onTapUserData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_UpdatableMixedView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr UpdatableMixedView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_UpdatableMixedView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void UpdatableMixedView_Free(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_UpdatableMixedView_UpdateTitle")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void UpdatableMixedView_UpdateTitle(IntPtr handle, IntPtr newValuePtr, nint newValueLen);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_UpdatableMixedView_UpdateIsEnabled")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void UpdatableMixedView_UpdateIsEnabled(IntPtr handle, int newValue);

    // --- Update functions for existing views (Session 4A) ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_EnumParamView_UpdateStyle")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void EnumParamView_UpdateStyle(IntPtr handle, int newValue);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MixedParamView_UpdateStyle")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void MixedParamView_UpdateStyle(IntPtr handle, int newValue);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MixedParamView_UpdateCount")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void MixedParamView_UpdateCount(IntPtr handle, int newValue);

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

    // StringClosureView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_StringClosureView_InvokeClosure")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int StringClosureView_InvokeClosure(IntPtr handle, IntPtr valuePtr, nint valueLen);

    // ClassClosureView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_ClassClosureView_InvokeClosure")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int ClassClosureView_InvokeClosure(IntPtr handle, IntPtr modelPtr);

    // OptionalStringView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_OptionalStringView_HasValue")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int OptionalStringView_HasValue(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_OptionalStringView_GetTitleLength")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int OptionalStringView_GetTitleLength(IntPtr handle);

    // OptionalClosureView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_OptionalClosureView_InvokeClosure")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int OptionalClosureView_InvokeClosure(IntPtr handle, int value);

    // MixedStringView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_MixedStringView_GetTitleLength")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int MixedStringView_GetTitleLength(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_MixedStringView_InvokeClosure")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int MixedStringView_InvokeClosure(IntPtr handle, IntPtr valuePtr, nint valueLen);

    // UpdatableCounterView (Session 4A)
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_UpdatableCounterView_GetCount")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int UpdatableCounterView_GetCount(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_UpdatableCounterView_GetLabelLength")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int UpdatableCounterView_GetLabelLength(IntPtr handle);

    // UpdatableMixedView (Session 4A)
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_UpdatableMixedView_GetTitleLength")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int UpdatableMixedView_GetTitleLength(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_UpdatableMixedView_GetIsEnabled")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int UpdatableMixedView_GetIsEnabled(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_UpdatableMixedView_FireOnTap")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int UpdatableMixedView_FireOnTap(IntPtr handle);
}

#endregion

#endif
