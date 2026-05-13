// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if SWIFTUI_BRIDGE

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.SwiftUIBridge;

/// <summary>
/// Tests for two-way state binding: Update* functions that dynamically change
/// view state after creation via the ObservableObject pattern.
/// </summary>
public class BridgeStateUpdateTests : TestBase
{
    public BridgeStateUpdateTests(TestResults results) : base(results) { }

    // --- UpdatableCounterView: primitive + string updates ---

    public unsafe void TestUpdatableCounterView_CreateAndRead()
    {
        var labelBytes = Encoding.UTF8.GetBytes("Score");
        fixed (byte* labelPtr = labelBytes)
        {
            var handle = BridgeNativeMethods.UpdatableCounterView_Create(10, (IntPtr)labelPtr, labelBytes.Length);
            AssertTrue(handle != IntPtr.Zero, "UpdatableCounterView handle != 0");

            var count = BridgeTestHelpers.UpdatableCounterView_GetCount(handle);
            AssertEqual(10, count, "UpdatableCounterView initial count");

            var labelLen = BridgeTestHelpers.UpdatableCounterView_GetLabelLength(handle);
            AssertEqual(5, labelLen, "UpdatableCounterView initial label length");

            var vcPtr = BridgeNativeMethods.UpdatableCounterView_GetViewController(handle);
            AssertTrue(vcPtr != IntPtr.Zero, "UpdatableCounterView GetVC != 0");

            BridgeNativeMethods.UpdatableCounterView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        }
        TestLogger.Info("UpdatableCounterView create/read: passed");
    }

    public unsafe void TestUpdatableCounterView_UpdateCount()
    {
        var labelBytes = Encoding.UTF8.GetBytes("Score");
        fixed (byte* labelPtr = labelBytes)
        {
            var handle = BridgeNativeMethods.UpdatableCounterView_Create(0, (IntPtr)labelPtr, labelBytes.Length);

            // Update count from 0 → 42
            BridgeNativeMethods.UpdatableCounterView_UpdateCount(handle, 42);
            // Pump run loop for main-thread dispatch
            Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

            var count = BridgeTestHelpers.UpdatableCounterView_GetCount(handle);
            AssertEqual(42, count, "UpdatableCounterView count after update");

            BridgeNativeMethods.UpdatableCounterView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        }
        TestLogger.Info("UpdatableCounterView UpdateCount: passed");
    }

    public unsafe void TestUpdatableCounterView_UpdateLabel()
    {
        var labelBytes = Encoding.UTF8.GetBytes("old");
        fixed (byte* labelPtr = labelBytes)
        {
            var handle = BridgeNativeMethods.UpdatableCounterView_Create(1, (IntPtr)labelPtr, labelBytes.Length);

            var initialLen = BridgeTestHelpers.UpdatableCounterView_GetLabelLength(handle);
            AssertEqual(3, initialLen, "UpdatableCounterView initial label 'old' len");

            // Update label to "new label"
            var newLabelBytes = Encoding.UTF8.GetBytes("new label");
            fixed (byte* newLabelPtr = newLabelBytes)
            {
                BridgeNativeMethods.UpdatableCounterView_UpdateLabel(handle, (IntPtr)newLabelPtr, newLabelBytes.Length);
            }
            Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

            var updatedLen = BridgeTestHelpers.UpdatableCounterView_GetLabelLength(handle);
            AssertEqual(9, updatedLen, "UpdatableCounterView label after update");

            BridgeNativeMethods.UpdatableCounterView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        }
        TestLogger.Info("UpdatableCounterView UpdateLabel: passed");
    }

    // --- UpdatableMixedView: string + bool updates + closure ---

    public unsafe void TestUpdatableMixedView_CreateAndRead()
    {
        MixedActionState.Reset();
        delegate* unmanaged[Cdecl]<IntPtr, void> callbackPtr = &MixedActionState.OnActionCallback;

        var titleBytes = Encoding.UTF8.GetBytes("Hello");
        fixed (byte* titlePtr = titleBytes)
        {
            var handle = BridgeNativeMethods.UpdatableMixedView_Create(
                (IntPtr)titlePtr, titleBytes.Length,
                1, // isEnabled = true
                (IntPtr)callbackPtr, IntPtr.Zero);
            AssertTrue(handle != IntPtr.Zero, "UpdatableMixedView handle != 0");

            var titleLen = BridgeTestHelpers.UpdatableMixedView_GetTitleLength(handle);
            AssertEqual(5, titleLen, "UpdatableMixedView initial title length");

            var isEnabled = BridgeTestHelpers.UpdatableMixedView_GetIsEnabled(handle);
            AssertEqual(1, isEnabled, "UpdatableMixedView initial isEnabled");

            BridgeNativeMethods.UpdatableMixedView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        }
        TestLogger.Info("UpdatableMixedView create/read: passed");
    }

    public unsafe void TestUpdatableMixedView_UpdateTitle()
    {
        MixedActionState.Reset();
        delegate* unmanaged[Cdecl]<IntPtr, void> callbackPtr = &MixedActionState.OnActionCallback;

        var titleBytes = Encoding.UTF8.GetBytes("old");
        fixed (byte* titlePtr = titleBytes)
        {
            var handle = BridgeNativeMethods.UpdatableMixedView_Create(
                (IntPtr)titlePtr, titleBytes.Length,
                1, (IntPtr)callbackPtr, IntPtr.Zero);

            // Update title to "updated title"
            var newTitleBytes = Encoding.UTF8.GetBytes("updated title");
            fixed (byte* newPtr = newTitleBytes)
            {
                BridgeNativeMethods.UpdatableMixedView_UpdateTitle(handle, (IntPtr)newPtr, newTitleBytes.Length);
            }
            Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

            var titleLen = BridgeTestHelpers.UpdatableMixedView_GetTitleLength(handle);
            AssertEqual(13, titleLen, "UpdatableMixedView title after update");

            BridgeNativeMethods.UpdatableMixedView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        }
        TestLogger.Info("UpdatableMixedView UpdateTitle: passed");
    }

    public unsafe void TestUpdatableMixedView_UpdateIsEnabled()
    {
        MixedActionState.Reset();
        delegate* unmanaged[Cdecl]<IntPtr, void> callbackPtr = &MixedActionState.OnActionCallback;

        var titleBytes = Encoding.UTF8.GetBytes("test");
        fixed (byte* titlePtr = titleBytes)
        {
            var handle = BridgeNativeMethods.UpdatableMixedView_Create(
                (IntPtr)titlePtr, titleBytes.Length,
                1, // isEnabled = true
                (IntPtr)callbackPtr, IntPtr.Zero);

            // Update isEnabled from true → false
            BridgeNativeMethods.UpdatableMixedView_UpdateIsEnabled(handle, 0);
            Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

            var isEnabled = BridgeTestHelpers.UpdatableMixedView_GetIsEnabled(handle);
            AssertEqual(0, isEnabled, "UpdatableMixedView isEnabled after update to false");

            // Update isEnabled from false → true
            BridgeNativeMethods.UpdatableMixedView_UpdateIsEnabled(handle, 1);
            Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

            isEnabled = BridgeTestHelpers.UpdatableMixedView_GetIsEnabled(handle);
            AssertEqual(1, isEnabled, "UpdatableMixedView isEnabled after update to true");

            BridgeNativeMethods.UpdatableMixedView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        }
        TestLogger.Info("UpdatableMixedView UpdateIsEnabled: passed");
    }

    public unsafe void TestUpdatableMixedView_ClosureStillWorks()
    {
        MixedActionState.Reset();
        delegate* unmanaged[Cdecl]<IntPtr, void> callbackPtr = &MixedActionState.OnActionCallback;

        var titleBytes = Encoding.UTF8.GetBytes("test");
        fixed (byte* titlePtr = titleBytes)
        {
            var handle = BridgeNativeMethods.UpdatableMixedView_Create(
                (IntPtr)titlePtr, titleBytes.Length,
                1, (IntPtr)callbackPtr, IntPtr.Zero);

            // Update state BEFORE firing closure — verifies closure survives state mutations
            var newTitleBytes = Encoding.UTF8.GetBytes("mutated");
            fixed (byte* newPtr = newTitleBytes)
            {
                BridgeNativeMethods.UpdatableMixedView_UpdateTitle(handle, (IntPtr)newPtr, newTitleBytes.Length);
            }
            BridgeNativeMethods.UpdatableMixedView_UpdateIsEnabled(handle, 0);
            Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

            // Fire the onTap closure via test helper — should still work after state updates
            BridgeTestHelpers.UpdatableMixedView_FireOnTap(handle);
            Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.5));
            AssertTrue(MixedActionState.CallCount >= 1, "UpdatableMixedView onTap callback fired after state update");

            BridgeNativeMethods.UpdatableMixedView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        }
        TestLogger.Info("UpdatableMixedView closure after state update: passed");
    }

    // --- Existing view Update functions (two-way state binding retrofit) ---

    public void TestEnumParamView_UpdateStyle()
    {
        var handle = BridgeNativeMethods.EnumParamView_Create(0); // AlertStyle.info
        AssertTrue(handle != IntPtr.Zero, "EnumParamView handle != 0");

        var style = BridgeTestHelpers.EnumParamView_GetStyle(handle);
        AssertEqual(0, style, "EnumParamView initial style = 0");

        // Update style from info(0) → warning(1)
        BridgeNativeMethods.EnumParamView_UpdateStyle(handle, 1);
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

        style = BridgeTestHelpers.EnumParamView_GetStyle(handle);
        AssertEqual(1, style, "EnumParamView style after update to warning");

        // Update style to error(2)
        BridgeNativeMethods.EnumParamView_UpdateStyle(handle, 2);
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

        style = BridgeTestHelpers.EnumParamView_GetStyle(handle);
        AssertEqual(2, style, "EnumParamView style after update to error");

        BridgeNativeMethods.EnumParamView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("EnumParamView UpdateStyle: passed");
    }

    public unsafe void TestMixedParamView_UpdateStyleAndCount()
    {
        MixedActionState.Reset();
        delegate* unmanaged[Cdecl]<IntPtr, void> callbackPtr = &MixedActionState.OnActionCallback;
        var handle = BridgeNativeMethods.MixedParamView_Create(0, (IntPtr)callbackPtr, IntPtr.Zero, 10);

        var style = BridgeTestHelpers.MixedParamView_GetStyle(handle);
        AssertEqual(0, style, "MixedParamView initial style");
        var count = BridgeTestHelpers.MixedParamView_GetCount(handle);
        AssertEqual(10, count, "MixedParamView initial count");

        // Update both
        BridgeNativeMethods.MixedParamView_UpdateStyle(handle, 2);
        BridgeNativeMethods.MixedParamView_UpdateCount(handle, 99);
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

        style = BridgeTestHelpers.MixedParamView_GetStyle(handle);
        AssertEqual(2, style, "MixedParamView style after update");
        count = BridgeTestHelpers.MixedParamView_GetCount(handle);
        AssertEqual(99, count, "MixedParamView count after update");

        BridgeNativeMethods.MixedParamView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("MixedParamView UpdateStyle+UpdateCount: passed");
    }
}

#endif
