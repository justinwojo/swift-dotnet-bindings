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
    internal static extern void EnumParamView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- ClassParamView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ClassParamView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr ClassParamView_Create(IntPtr modelPtr);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ClassParamView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr ClassParamView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ClassParamView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void ClassParamView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- TypedClosureView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_TypedClosureView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr TypedClosureView_Create(IntPtr onValueCallback, IntPtr onValueUserData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_TypedClosureView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr TypedClosureView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_TypedClosureView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void TypedClosureView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- MultiArgClosureView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MultiArgClosureView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr MultiArgClosureView_Create(IntPtr onEventCallback, IntPtr onEventUserData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MultiArgClosureView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr MultiArgClosureView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MultiArgClosureView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void MultiArgClosureView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- MixedParamView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MixedParamView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr MixedParamView_Create(int style, IntPtr onActionCallback, IntPtr onActionUserData, int count);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MixedParamView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr MixedParamView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MixedParamView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void MixedParamView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- OptionalEnumView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalEnumView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr OptionalEnumView_Create(int styleHasValue, int styleValue);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalEnumView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr OptionalEnumView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalEnumView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void OptionalEnumView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- OptionalClassView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalClassView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr OptionalClassView_Create(IntPtr modelPtr);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalClassView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr OptionalClassView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalClassView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void OptionalClassView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- StringClosureView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_StringClosureView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr StringClosureView_Create(IntPtr onResultCallback, IntPtr onResultUserData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_StringClosureView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr StringClosureView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_StringClosureView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void StringClosureView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- ClassClosureView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ClassClosureView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr ClassClosureView_Create(IntPtr onModelCallback, IntPtr onModelUserData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ClassClosureView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr ClassClosureView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ClassClosureView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void ClassClosureView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- OptionalStringView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalStringView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr OptionalStringView_Create(IntPtr titlePtr, nint titleLen);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalStringView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr OptionalStringView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalStringView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void OptionalStringView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- OptionalClosureView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalClosureView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr OptionalClosureView_Create(IntPtr callbackCallback, IntPtr callbackUserData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalClosureView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr OptionalClosureView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_OptionalClosureView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void OptionalClosureView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- MixedStringView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MixedStringView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr MixedStringView_Create(IntPtr titlePtr, nint titleLen, IntPtr onResultCallback, IntPtr onResultUserData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MixedStringView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr MixedStringView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_MixedStringView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void MixedStringView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- UpdatableCounterView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_UpdatableCounterView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr UpdatableCounterView_Create(int count, IntPtr labelPtr, nint labelLen);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_UpdatableCounterView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr UpdatableCounterView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_UpdatableCounterView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void UpdatableCounterView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

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
    internal static extern void UpdatableMixedView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_UpdatableMixedView_UpdateTitle")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void UpdatableMixedView_UpdateTitle(IntPtr handle, IntPtr newValuePtr, nint newValueLen);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_UpdatableMixedView_UpdateIsEnabled")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void UpdatableMixedView_UpdateIsEnabled(IntPtr handle, int newValue);

    // --- Update functions for existing views ---
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
    internal static extern void AsyncServiceView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

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
    internal static extern void DeepChainView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

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
    internal static extern void MixedAsyncView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- Universal Modifiers (using EnumParamView as test vehicle) ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_EnumParamView_SetFrame")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void EnumParamView_SetFrame(IntPtr handle, int hasWidth, double width, int hasHeight, double height);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_EnumParamView_SetPadding")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void EnumParamView_SetPadding(IntPtr handle, int hasValue, double value);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_EnumParamView_SetBackground")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void EnumParamView_SetBackground(IntPtr handle, int hasValue, double r, double g, double b, double a);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_EnumParamView_SetForegroundColor")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void EnumParamView_SetForegroundColor(IntPtr handle, int hasValue, double r, double g, double b, double a);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_EnumParamView_SetCornerRadius")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void EnumParamView_SetCornerRadius(IntPtr handle, int hasValue, double value);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_EnumParamView_SetOpacity")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void EnumParamView_SetOpacity(IntPtr handle, int hasValue, double value);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_EnumParamView_SetFont")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void EnumParamView_SetFont(IntPtr handle, int hasValue, double size);

    // --- Lifecycle (using EnumParamView as test vehicle) ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_EnumParamView_SetLifecycle")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void EnumParamView_SetLifecycle(IntPtr handle, IntPtr onAppearCb, IntPtr onAppearUd, IntPtr onDisappearCb, IntPtr onDisappearUd);

    // --- LifecycleTestView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_LifecycleTestView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr LifecycleTestView_Create(IntPtr titlePtr, nint titleLen);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_LifecycleTestView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr LifecycleTestView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_LifecycleTestView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void LifecycleTestView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_LifecycleTestView_SetLifecycle")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void LifecycleTestView_SetLifecycle(IntPtr handle, IntPtr onAppearCb, IntPtr onAppearUd, IntPtr onDisappearCb, IntPtr onDisappearUd);

    // --- StringReturnClosureView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_StringReturnClosureView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr StringReturnClosureView_Create(IntPtr transformerCallback, IntPtr transformerUserData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_StringReturnClosureView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr StringReturnClosureView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_StringReturnClosureView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void StringReturnClosureView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- ClassReturnClosureView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ClassReturnClosureView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr ClassReturnClosureView_Create(IntPtr factoryCallback, IntPtr factoryUserData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ClassReturnClosureView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr ClassReturnClosureView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ClassReturnClosureView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void ClassReturnClosureView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- ModifiableView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ModifiableView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr ModifiableView_Create(IntPtr titlePtr, nint titleLen);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ModifiableView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr ModifiableView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ModifiableView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void ModifiableView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ModifiableView_SetHighlighted")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void ModifiableView_SetHighlighted(IntPtr handle, int enabled);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ModifiableView_SetOpacity")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void ModifiableView_SetOpacity(IntPtr handle, int hasValue, double value);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ModifiableView_SetEnabled")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void ModifiableView_SetEnabled(IntPtr handle, int hasValue, int value);

    // --- GenericPlaceholderView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_GenericPlaceholderView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr GenericPlaceholderView_Create(IntPtr titlePtr, nint titleLen);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_GenericPlaceholderView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr GenericPlaceholderView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_GenericPlaceholderView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void GenericPlaceholderView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- PlaceholderOnlyView ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_PlaceholderOnlyView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr PlaceholderOnlyView_Create();

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_PlaceholderOnlyView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr PlaceholderOnlyView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_PlaceholderOnlyView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void PlaceholderOnlyView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- ClassParamView UpdateModel ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ClassParamView_UpdateModel")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void ClassParamView_UpdateModel(IntPtr handle, IntPtr newValue);

    // --- NoParamBlurView (AlertToast BlurView pattern) ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_NoParamBlurView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr NoParamBlurView_Create();

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_NoParamBlurView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr NoParamBlurView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_NoParamBlurView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void NoParamBlurView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- PlayerStyleView (YouTubePlayerKit pattern) ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_PlayerStyleView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr PlayerStyleView_Create(IntPtr playerPtr, IntPtr titlePtr, int titleLen);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_PlayerStyleView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr PlayerStyleView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_PlayerStyleView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void PlayerStyleView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- FormatActionView (RichTextKit ActionButton pattern — BoundStruct enum) ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_FormatActionView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr FormatActionView_Create(IntPtr actionPtr);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_FormatActionView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr FormatActionView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_FormatActionView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void FormatActionView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- FormatMenuView (RichTextKit Menu pattern — closure with BoundStruct) ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_FormatMenuView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr FormatMenuView_Create(IntPtr onFormatCallback, IntPtr onFormatUserData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_FormatMenuView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr FormatMenuView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_FormatMenuView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void FormatMenuView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- RichToolbarView (RichTextKit toolbar pattern — dual string) ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_RichToolbarView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr RichToolbarView_Create(IntPtr titlePtr, int titleLen, IntPtr subtitlePtr, int subtitleLen);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_RichToolbarView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr RichToolbarView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_RichToolbarView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void RichToolbarView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- BindingToggleView (Binding<Bool> gate) ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_BindingToggleView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr BindingToggleView_Create(int isOn);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_BindingToggleView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr BindingToggleView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_BindingToggleView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void BindingToggleView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_BindingToggleView_UpdateIsOn")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void BindingToggleView_UpdateIsOn(IntPtr handle, int newValue);

    // --- NumberListView (Array<Int> gate) ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_NumberListView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr NumberListView_Create(IntPtr numbersPtr, nint numbersCount);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_NumberListView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr NumberListView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_NumberListView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void NumberListView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- SymbolIconView (SwiftUI.Image gate) ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_SymbolIconView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr SymbolIconView_Create(IntPtr iconPtr, nint iconLen);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_SymbolIconView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr SymbolIconView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_SymbolIconView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void SymbolIconView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- ResultWithStructView (Result<BoundType, BoundStruct> closure gate) ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ResultWithStructView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr ResultWithStructView_Create(
        IntPtr completionSuccessCallback, IntPtr completionSuccessUserData,
        IntPtr completionErrorCallback, IntPtr completionErrorUserData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ResultWithStructView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr ResultWithStructView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ResultWithStructView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void ResultWithStructView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);

    // --- ResultCompletionView (Result<T,E> closure gate) ---
    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ResultCompletionView_Create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr ResultCompletionView_Create(
        IntPtr completionSuccessCallback, IntPtr completionSuccessUserData,
        IntPtr completionErrorCallback, IntPtr completionErrorUserData);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ResultCompletionView_GetViewController")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr ResultCompletionView_GetViewController(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_SwiftBindingsTestLib_ResultCompletionView_Free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void ResultCompletionView_Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn);
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

    // UpdatableCounterView (two-way state binding)
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_UpdatableCounterView_GetCount")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int UpdatableCounterView_GetCount(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_UpdatableCounterView_GetLabelLength")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int UpdatableCounterView_GetLabelLength(IntPtr handle);

    // UpdatableMixedView (two-way state binding with mixed param types)
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_UpdatableMixedView_GetTitleLength")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int UpdatableMixedView_GetTitleLength(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_UpdatableMixedView_GetIsEnabled")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int UpdatableMixedView_GetIsEnabled(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_UpdatableMixedView_FireOnTap")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int UpdatableMixedView_FireOnTap(IntPtr handle);

    // GenericPlaceholderView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_GenericPlaceholderView_GetTitleLength")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int GenericPlaceholderView_GetTitleLength(IntPtr handle);

    // PlaceholderOnlyView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_PlaceholderOnlyView_IsAlive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int PlaceholderOnlyView_IsAlive(IntPtr handle);

    // StringReturnClosureView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_StringReturnClosureView_InvokeTransformer")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int StringReturnClosureView_InvokeTransformer(IntPtr handle, int value);

    // ClassReturnClosureView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_ClassReturnClosureView_InvokeFactory")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int ClassReturnClosureView_InvokeFactory(IntPtr handle, int value);

    // ModifiableView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_ModifiableView_GetHighlighted")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int ModifiableView_GetHighlighted(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_ModifiableView_GetModEnabled")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int ModifiableView_GetModEnabled(IntPtr handle);

    // LifecycleTestView
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_LifecycleTestView_FireOnAppear")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int LifecycleTestView_FireOnAppear(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_LifecycleTestView_FireOnDisappear")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int LifecycleTestView_FireOnDisappear(IntPtr handle);

    // TransformOutcome helpers (BoundStruct creation)
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_CreateTransformOutcome_Completed")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr CreateTransformOutcome_Completed(int result);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_CreateTransformOutcome_Failed")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern IntPtr CreateTransformOutcome_Failed(int errorCode);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_FreeTransformOutcome")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern void FreeTransformOutcome(IntPtr ptr);

    // FormatActionView (BoundStruct param verification)
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_FormatActionView_GetOutcomeValue")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int FormatActionView_GetOutcomeValue(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_FormatActionView_IsCompleted")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int FormatActionView_IsCompleted(IntPtr handle);

    // FormatMenuView (closure with BoundStruct arg)
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_FormatMenuView_InvokeOnFormat")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int FormatMenuView_InvokeOnFormat(IntPtr handle, int isCompleted, int value);

    // PlayerStyleView (class + string)
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_PlayerStyleView_GetPlayerValue")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int PlayerStyleView_GetPlayerValue(IntPtr handle);

    // RichToolbarView (dual string)
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_RichToolbarView_GetTitleLength")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int RichToolbarView_GetTitleLength(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_RichToolbarView_GetSubtitleLength")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int RichToolbarView_GetSubtitleLength(IntPtr handle);

    // BindingToggleView (Binding<Bool> gate)
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_BindingToggleView_GetIsOn")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int BindingToggleView_GetIsOn(IntPtr handle);

    // NumberListView (Array<Int> gate)
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_NumberListView_GetCount")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int NumberListView_GetCount(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_NumberListView_GetElement")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int NumberListView_GetElement(IntPtr handle, int index);

    // SymbolIconView (SwiftUI.Image gate)
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_SymbolIconView_GetIconLength")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int SymbolIconView_GetIconLength(IntPtr handle);

    // ResultWithStructView (Result<BoundType, BoundStruct> closure gate)
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_ResultWithStructView_InvokeSuccess")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int ResultWithStructView_InvokeSuccess(IntPtr handle, int value);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_ResultWithStructView_InvokeError")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int ResultWithStructView_InvokeError(IntPtr handle, int value);

    // ResultCompletionView (Result<T,E> closure gate)
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_ResultCompletionView_InvokeSuccess")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int ResultCompletionView_InvokeSuccess(IntPtr handle, int value);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_ResultCompletionView_InvokeError")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int ResultCompletionView_InvokeError(IntPtr handle, int errorCode);

    // --- Bridge test helpers ---

    // UrlResultView: Result<URL, ScanError> closure — ObjC-bridgeable success branch
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_UrlResultView_InvokeSuccess")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int UrlResultView_InvokeSuccess(IntPtr handle, int value);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_UrlResultView_InvokeError")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int UrlResultView_InvokeError(IntPtr handle, int code);

    // UrlClosureView (review): typed (URL)->Void closure — ObjC-bridgeable struct arg
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_UrlClosureView_InvokeOnPick")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int UrlClosureView_InvokeOnPick(IntPtr handle, int value);

    // FrozenRefClosureView: @frozen struct w/ ref field as closure arg
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_FrozenRefClosureView_InvokeOnEvent")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int FrozenRefClosureView_InvokeOnEvent(IntPtr handle, int value);

    // UrlParamView: ObjC-bridgeable struct (URL) param
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_UrlParamView_GetTargetLength")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int UrlParamView_GetTargetLength(IntPtr handle);

    // OptionalUrlParamView: Optional<ObjC-bridgeable struct> param
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_OptionalUrlParamView_GetTargetLength")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int OptionalUrlParamView_GetTargetLength(IntPtr handle);

    // ArrayEnumView: [BoundEnum] param
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_ArrayEnumView_GetCount")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int ArrayEnumView_GetCount(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_ArrayEnumView_GetElement")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int ArrayEnumView_GetElement(IntPtr handle, int index);

    // HandleParamView: init params colliding with generated locals
    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_HandleParamView_GetHandle")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int HandleParamView_GetHandle(IntPtr handle);

    [DllImport(BridgeLib, EntryPoint = "SBW_TEST_HandleParamView_GetSession")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static extern int HandleParamView_GetSession(IntPtr handle);
}

#endregion

#endif
