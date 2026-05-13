// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if SWIFTUI_BRIDGE

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.SwiftUIBridge;

/// <summary>
/// Tests for universal modifiers, lifecycle callbacks, view modifier chains,
/// generic views, and class param updates — cross-cutting patterns that
/// affect all bridge views but had zero test coverage.
/// </summary>
public class BridgeModifierAndLifecycleTests : TestBase
{
    public BridgeModifierAndLifecycleTests(TestResults results) : base(results) { }

    // --- Universal Modifiers (using EnumParamView as test vehicle) ---

    public void TestSetFrame_WithBothDimensions()
    {
        var handle = BridgeNativeMethods.EnumParamView_Create(0);
        AssertTrue(handle != IntPtr.Zero, "SetFrame: handle != 0");

        // Set frame with both width and height
        BridgeNativeMethods.EnumParamView_SetFrame(handle, 1, 200.0, 1, 100.0);
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

        BridgeNativeMethods.EnumParamView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("SetFrame (both dimensions): passed");
    }

    public void TestSetFrame_WidthOnly()
    {
        var handle = BridgeNativeMethods.EnumParamView_Create(0);

        // Set only width (hasHeight=0)
        BridgeNativeMethods.EnumParamView_SetFrame(handle, 1, 150.0, 0, 0.0);
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

        BridgeNativeMethods.EnumParamView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("SetFrame (width only): passed");
    }

    public void TestSetFrame_NilReset()
    {
        var handle = BridgeNativeMethods.EnumParamView_Create(0);

        // Set frame, then reset to nil
        BridgeNativeMethods.EnumParamView_SetFrame(handle, 1, 200.0, 1, 100.0);
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));
        BridgeNativeMethods.EnumParamView_SetFrame(handle, 0, 0.0, 0, 0.0);
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

        BridgeNativeMethods.EnumParamView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("SetFrame (nil reset): passed");
    }

    public void TestSetPadding()
    {
        var handle = BridgeNativeMethods.EnumParamView_Create(0);

        BridgeNativeMethods.EnumParamView_SetPadding(handle, 1, 16.0);
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

        BridgeNativeMethods.EnumParamView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("SetPadding: passed");
    }

    public void TestSetPadding_NilReset()
    {
        var handle = BridgeNativeMethods.EnumParamView_Create(0);

        BridgeNativeMethods.EnumParamView_SetPadding(handle, 1, 16.0);
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));
        BridgeNativeMethods.EnumParamView_SetPadding(handle, 0, 0.0);
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

        BridgeNativeMethods.EnumParamView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("SetPadding (nil reset): passed");
    }

    public void TestSetBackground()
    {
        var handle = BridgeNativeMethods.EnumParamView_Create(0);

        // Set background to red (r=1, g=0, b=0, a=1)
        BridgeNativeMethods.EnumParamView_SetBackground(handle, 1, 1.0, 0.0, 0.0, 1.0);
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

        BridgeNativeMethods.EnumParamView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("SetBackground: passed");
    }

    public void TestSetBackground_NilReset()
    {
        var handle = BridgeNativeMethods.EnumParamView_Create(0);

        BridgeNativeMethods.EnumParamView_SetBackground(handle, 1, 1.0, 0.0, 0.0, 1.0);
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));
        // Clear background (hasValue=0)
        BridgeNativeMethods.EnumParamView_SetBackground(handle, 0, 0.0, 0.0, 0.0, 0.0);
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

        BridgeNativeMethods.EnumParamView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("SetBackground (nil reset): passed");
    }

    public void TestSetForegroundColor()
    {
        var handle = BridgeNativeMethods.EnumParamView_Create(0);

        // Set foreground to blue (r=0, g=0, b=1, a=1)
        BridgeNativeMethods.EnumParamView_SetForegroundColor(handle, 1, 0.0, 0.0, 1.0, 1.0);
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

        BridgeNativeMethods.EnumParamView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("SetForegroundColor: passed");
    }

    public void TestSetCornerRadius()
    {
        var handle = BridgeNativeMethods.EnumParamView_Create(0);

        BridgeNativeMethods.EnumParamView_SetCornerRadius(handle, 1, 8.0);
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

        BridgeNativeMethods.EnumParamView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("SetCornerRadius: passed");
    }

    public void TestSetOpacity()
    {
        var handle = BridgeNativeMethods.EnumParamView_Create(0);

        BridgeNativeMethods.EnumParamView_SetOpacity(handle, 1, 0.5);
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

        BridgeNativeMethods.EnumParamView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("SetOpacity: passed");
    }

    public void TestSetFontSize()
    {
        var handle = BridgeNativeMethods.EnumParamView_Create(0);

        BridgeNativeMethods.EnumParamView_SetFont(handle, 1, 24.0);
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

        BridgeNativeMethods.EnumParamView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("SetFontSize: passed");
    }

    public void TestAllModifiers_Combined()
    {
        var handle = BridgeNativeMethods.EnumParamView_Create(1);
        AssertTrue(handle != IntPtr.Zero, "Combined modifiers: handle != 0");

        // Apply all modifiers on a single view to verify no interaction crashes
        BridgeNativeMethods.EnumParamView_SetFrame(handle, 1, 300.0, 1, 200.0);
        BridgeNativeMethods.EnumParamView_SetPadding(handle, 1, 16.0);
        BridgeNativeMethods.EnumParamView_SetBackground(handle, 1, 0.2, 0.4, 0.6, 1.0);
        BridgeNativeMethods.EnumParamView_SetForegroundColor(handle, 1, 1.0, 1.0, 1.0, 1.0);
        BridgeNativeMethods.EnumParamView_SetCornerRadius(handle, 1, 12.0);
        BridgeNativeMethods.EnumParamView_SetOpacity(handle, 1, 0.8);
        BridgeNativeMethods.EnumParamView_SetFont(handle, 1, 18.0);
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

        // Verify view is still usable
        var vcPtr = BridgeNativeMethods.EnumParamView_GetViewController(handle);
        AssertTrue(vcPtr != IntPtr.Zero, "Combined modifiers: GetVC != 0");

        BridgeNativeMethods.EnumParamView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("All modifiers combined: passed");
    }

    // --- Lifecycle Callbacks ---

    public unsafe void TestLifecycle_SetCallbacks()
    {
        LifecycleCallbackState.Reset();

        var titleBytes = Encoding.UTF8.GetBytes("lifecycle");
        fixed (byte* titlePtr = titleBytes)
        {
            var handle = BridgeNativeMethods.LifecycleTestView_Create((IntPtr)titlePtr, titleBytes.Length);
            AssertTrue(handle != IntPtr.Zero, "LifecycleTestView handle != 0");

            // Set lifecycle callbacks
            delegate* unmanaged[Cdecl]<IntPtr, void> onAppearPtr = &LifecycleCallbackState.OnAppearCallback;
            delegate* unmanaged[Cdecl]<IntPtr, void> onDisappearPtr = &LifecycleCallbackState.OnDisappearCallback;
            BridgeNativeMethods.LifecycleTestView_SetLifecycle(
                handle,
                (IntPtr)onAppearPtr, IntPtr.Zero,
                (IntPtr)onDisappearPtr, IntPtr.Zero);

            // Fire callbacks via Swift test helper
            var appearResult = BridgeTestHelpers.LifecycleTestView_FireOnAppear(handle);
            AssertEqual(1, appearResult, "LifecycleTestView onAppear fired");
            AssertEqual(1, LifecycleCallbackState.AppearCount, "LifecycleTestView appear callback count");

            var disappearResult = BridgeTestHelpers.LifecycleTestView_FireOnDisappear(handle);
            AssertEqual(1, disappearResult, "LifecycleTestView onDisappear fired");
            AssertEqual(1, LifecycleCallbackState.DisappearCount, "LifecycleTestView disappear callback count");

            BridgeNativeMethods.LifecycleTestView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        }
        TestLogger.Info("LifecycleTestView set/fire callbacks: passed");
    }

    public unsafe void TestLifecycle_MultipleFiresAccumulate()
    {
        LifecycleCallbackState.Reset();

        var titleBytes = Encoding.UTF8.GetBytes("multi");
        fixed (byte* titlePtr = titleBytes)
        {
            var handle = BridgeNativeMethods.LifecycleTestView_Create((IntPtr)titlePtr, titleBytes.Length);

            delegate* unmanaged[Cdecl]<IntPtr, void> onAppearPtr = &LifecycleCallbackState.OnAppearCallback;
            delegate* unmanaged[Cdecl]<IntPtr, void> onDisappearPtr = &LifecycleCallbackState.OnDisappearCallback;
            BridgeNativeMethods.LifecycleTestView_SetLifecycle(
                handle,
                (IntPtr)onAppearPtr, IntPtr.Zero,
                (IntPtr)onDisappearPtr, IntPtr.Zero);

            // Fire multiple times
            BridgeTestHelpers.LifecycleTestView_FireOnAppear(handle);
            BridgeTestHelpers.LifecycleTestView_FireOnAppear(handle);
            BridgeTestHelpers.LifecycleTestView_FireOnAppear(handle);
            AssertEqual(3, LifecycleCallbackState.AppearCount, "LifecycleTestView 3x appear");

            BridgeNativeMethods.LifecycleTestView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        }
        TestLogger.Info("LifecycleTestView multiple fires: passed");
    }

    public unsafe void TestLifecycle_NoCallbacksBeforeSet()
    {
        var titleBytes = Encoding.UTF8.GetBytes("noset");
        fixed (byte* titlePtr = titleBytes)
        {
            var handle = BridgeNativeMethods.LifecycleTestView_Create((IntPtr)titlePtr, titleBytes.Length);

            // Fire without setting callbacks — should return 0 (no callback registered)
            var result = BridgeTestHelpers.LifecycleTestView_FireOnAppear(handle);
            AssertEqual(0, result, "LifecycleTestView no callback → returns 0");

            BridgeNativeMethods.LifecycleTestView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        }
        TestLogger.Info("LifecycleTestView no callbacks before set: passed");
    }

    public unsafe void TestLifecycle_OnExistingView()
    {
        // Verify lifecycle works on EnumParamView (universal, not just LifecycleTestView)
        LifecycleCallbackState.Reset();

        var handle = BridgeNativeMethods.EnumParamView_Create(0);
        AssertTrue(handle != IntPtr.Zero, "EnumParamView lifecycle: handle != 0");

        delegate* unmanaged[Cdecl]<IntPtr, void> onAppearPtr = &LifecycleCallbackState.OnAppearCallback;
        delegate* unmanaged[Cdecl]<IntPtr, void> onDisappearPtr = &LifecycleCallbackState.OnDisappearCallback;
        BridgeNativeMethods.EnumParamView_SetLifecycle(
            handle,
            (IntPtr)onAppearPtr, IntPtr.Zero,
            (IntPtr)onDisappearPtr, IntPtr.Zero);

        // Lifecycle is set but we can't fire it on EnumParamView without a test helper.
        // Verify SetLifecycle doesn't crash and view is still functional.
        var vcPtr = BridgeNativeMethods.EnumParamView_GetViewController(handle);
        AssertTrue(vcPtr != IntPtr.Zero, "EnumParamView lifecycle: GetVC != 0");
        var style = BridgeTestHelpers.EnumParamView_GetStyle(handle);
        AssertEqual(0, style, "EnumParamView lifecycle: style preserved");

        BridgeNativeMethods.EnumParamView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("Lifecycle on existing view (EnumParamView): passed");
    }

    // --- View Modifier Chains (ModifiableView) ---

    public unsafe void TestModifiableView_SetHighlighted()
    {
        var titleBytes = Encoding.UTF8.GetBytes("highlight");
        fixed (byte* titlePtr = titleBytes)
        {
            var handle = BridgeNativeMethods.ModifiableView_Create((IntPtr)titlePtr, titleBytes.Length);
            AssertTrue(handle != IntPtr.Zero, "ModifiableView handle != 0");

            // Initially not highlighted
            var highlighted = BridgeTestHelpers.ModifiableView_GetHighlighted(handle);
            AssertEqual(0, highlighted, "ModifiableView initially not highlighted");

            // Set highlighted
            BridgeNativeMethods.ModifiableView_SetHighlighted(handle, 1);
            Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));
            highlighted = BridgeTestHelpers.ModifiableView_GetHighlighted(handle);
            AssertEqual(1, highlighted, "ModifiableView highlighted after set");

            // Unset highlighted
            BridgeNativeMethods.ModifiableView_SetHighlighted(handle, 0);
            Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));
            highlighted = BridgeTestHelpers.ModifiableView_GetHighlighted(handle);
            AssertEqual(0, highlighted, "ModifiableView unhighlighted after clear");

            BridgeNativeMethods.ModifiableView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        }
        TestLogger.Info("ModifiableView SetHighlighted: passed");
    }

    public unsafe void TestModifiableView_SetOpacity()
    {
        var titleBytes = Encoding.UTF8.GetBytes("opacity");
        fixed (byte* titlePtr = titleBytes)
        {
            var handle = BridgeNativeMethods.ModifiableView_Create((IntPtr)titlePtr, titleBytes.Length);

            // Set opacity (custom modifier, stored as mod_opacity on state)
            BridgeNativeMethods.ModifiableView_SetOpacity(handle, 1, 0.5);
            Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

            // Verify view still usable
            var vcPtr = BridgeNativeMethods.ModifiableView_GetViewController(handle);
            AssertTrue(vcPtr != IntPtr.Zero, "ModifiableView opacity: GetVC != 0");

            BridgeNativeMethods.ModifiableView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        }
        TestLogger.Info("ModifiableView SetOpacity: passed");
    }

    public unsafe void TestModifiableView_SetEnabled()
    {
        var titleBytes = Encoding.UTF8.GetBytes("enabled");
        fixed (byte* titlePtr = titleBytes)
        {
            var handle = BridgeNativeMethods.ModifiableView_Create((IntPtr)titlePtr, titleBytes.Length);

            // Initially nil
            var enabled = BridgeTestHelpers.ModifiableView_GetModEnabled(handle);
            AssertEqual(-1, enabled, "ModifiableView initially mod_enabled=nil");

            // Set enabled to true
            BridgeNativeMethods.ModifiableView_SetEnabled(handle, 1, 1);
            Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));
            enabled = BridgeTestHelpers.ModifiableView_GetModEnabled(handle);
            AssertEqual(1, enabled, "ModifiableView mod_enabled=true");

            // Set enabled to false
            BridgeNativeMethods.ModifiableView_SetEnabled(handle, 1, 0);
            Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));
            enabled = BridgeTestHelpers.ModifiableView_GetModEnabled(handle);
            AssertEqual(0, enabled, "ModifiableView mod_enabled=false");

            // Clear to nil
            BridgeNativeMethods.ModifiableView_SetEnabled(handle, 0, 0);
            Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));
            enabled = BridgeTestHelpers.ModifiableView_GetModEnabled(handle);
            AssertEqual(-1, enabled, "ModifiableView mod_enabled back to nil");

            BridgeNativeMethods.ModifiableView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        }
        TestLogger.Info("ModifiableView SetEnabled: passed");
    }

    public unsafe void TestModifiableView_AllModifiersCombined()
    {
        var titleBytes = Encoding.UTF8.GetBytes("combined");
        fixed (byte* titlePtr = titleBytes)
        {
            var handle = BridgeNativeMethods.ModifiableView_Create((IntPtr)titlePtr, titleBytes.Length);

            // Apply all three custom modifiers
            BridgeNativeMethods.ModifiableView_SetHighlighted(handle, 1);
            BridgeNativeMethods.ModifiableView_SetOpacity(handle, 1, 0.5);
            BridgeNativeMethods.ModifiableView_SetEnabled(handle, 1, 1);
            Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

            // Verify state persists across calls
            var highlighted = BridgeTestHelpers.ModifiableView_GetHighlighted(handle);
            AssertEqual(1, highlighted, "ModifiableView combined: highlighted");
            var enabled = BridgeTestHelpers.ModifiableView_GetModEnabled(handle);
            AssertEqual(1, enabled, "ModifiableView combined: enabled");

            BridgeNativeMethods.ModifiableView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        }
        TestLogger.Info("ModifiableView all modifiers combined: passed");
    }

    // --- Generic Views ---

    public unsafe void TestGenericPlaceholderView()
    {
        var titleBytes = Encoding.UTF8.GetBytes("generic test");
        fixed (byte* titlePtr = titleBytes)
        {
            var handle = BridgeNativeMethods.GenericPlaceholderView_Create((IntPtr)titlePtr, titleBytes.Length);
            AssertTrue(handle != IntPtr.Zero, "GenericPlaceholderView handle != 0");

            var titleLen = BridgeTestHelpers.GenericPlaceholderView_GetTitleLength(handle);
            AssertEqual(12, titleLen, "GenericPlaceholderView title length");

            var vcPtr = BridgeNativeMethods.GenericPlaceholderView_GetViewController(handle);
            AssertTrue(vcPtr != IntPtr.Zero, "GenericPlaceholderView GetVC != 0");

            BridgeNativeMethods.GenericPlaceholderView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        }
        TestLogger.Info("GenericPlaceholderView: passed");
    }

    public void TestPlaceholderOnlyView()
    {
        // Zero C# params — bridge synthesizes the @ViewBuilder closure
        var handle = BridgeNativeMethods.PlaceholderOnlyView_Create();
        AssertTrue(handle != IntPtr.Zero, "PlaceholderOnlyView handle != 0");

        var alive = BridgeTestHelpers.PlaceholderOnlyView_IsAlive(handle);
        AssertEqual(1, alive, "PlaceholderOnlyView is alive");

        var vcPtr = BridgeNativeMethods.PlaceholderOnlyView_GetViewController(handle);
        AssertTrue(vcPtr != IntPtr.Zero, "PlaceholderOnlyView GetVC != 0");

        BridgeNativeMethods.PlaceholderOnlyView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("PlaceholderOnlyView: passed");
    }

    // --- ClassParamView UpdateModel ---

    public void TestClassParamView_UpdateModel()
    {
        var modelPtr = BridgeTestHelpers.CreateSimpleModel(10);
        var handle = BridgeNativeMethods.ClassParamView_Create(modelPtr);
        AssertTrue(handle != IntPtr.Zero, "ClassParamView UpdateModel: handle != 0");

        var value = BridgeTestHelpers.ClassParamView_GetModelValue(handle);
        AssertEqual(10, value, "ClassParamView initial model value");

        // Create new model and update
        var newModelPtr = BridgeTestHelpers.CreateSimpleModel(99);
        BridgeNativeMethods.ClassParamView_UpdateModel(handle, newModelPtr);
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

        value = BridgeTestHelpers.ClassParamView_GetModelValue(handle);
        AssertEqual(99, value, "ClassParamView model value after update");

        BridgeNativeMethods.ClassParamView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        BridgeTestHelpers.FreeSimpleModel(modelPtr);
        BridgeTestHelpers.FreeSimpleModel(newModelPtr);
        TestLogger.Info("ClassParamView UpdateModel: passed");
    }
}

#endif
