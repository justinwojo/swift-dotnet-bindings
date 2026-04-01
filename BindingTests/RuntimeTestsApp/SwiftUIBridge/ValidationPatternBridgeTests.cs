// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if SWIFTUI_BRIDGE

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.SwiftUIBridge;

/// <summary>
/// Runtime tests for SwiftUI bridge views replicating third-party validation library patterns.
/// Verifies that bridge parameter gates from Sessions 2-3 work end-to-end on the iOS Simulator:
///   NoParamBlurView       → AlertToast.BlurView (zero-param init)
///   PlayerStyleView       → YouTubePlayerKit.YouTubePlayerView (class + string)
///   FormatActionView      → RichTextKit.ActionButton (non-raw-value enum / BoundStruct)
///   FormatMenuView        → RichTextKit.Menu (closure with BoundStruct arg)
///   RichToolbarView       → RichTextKit toolbar views (dual string params)
/// </summary>
public class ValidationPatternBridgeTests : TestBase
{
    public ValidationPatternBridgeTests(TestResults results) : base(results) { }

    // ────────────────────────────────────────────────────────────────
    // NoParamBlurView — AlertToast.BlurView pattern (zero params)
    // ────────────────────────────────────────────────────────────────

    public void TestNoParamBlurView_CreateAndGetVC()
    {
        var handle = BridgeNativeMethods.NoParamBlurView_Create();
        AssertTrue(handle != IntPtr.Zero, "NoParamBlurView handle != 0");

        var vcPtr = BridgeNativeMethods.NoParamBlurView_GetViewController(handle);
        AssertTrue(vcPtr != IntPtr.Zero, "NoParamBlurView GetVC != 0");

        BridgeNativeMethods.NoParamBlurView_Free(handle);
        TestLogger.Info("NoParamBlurView: create/getVC/free cycle passed");
    }

    public void TestNoParamBlurView_FreeInvalidatesHandle()
    {
        var handle = BridgeNativeMethods.NoParamBlurView_Create();
        AssertTrue(handle != IntPtr.Zero, "handle valid before free");

        BridgeNativeMethods.NoParamBlurView_Free(handle);

        // After free, GetVC should return null (handle removed from liveHandles)
        var vcPtr = BridgeNativeMethods.NoParamBlurView_GetViewController(handle);
        AssertTrue(vcPtr == IntPtr.Zero, "GetVC returns 0 after free");
    }

    // ────────────────────────────────────────────────────────────────
    // PlayerStyleView — YouTubePlayerKit pattern (class + string)
    // ────────────────────────────────────────────────────────────────

    public unsafe void TestPlayerStyleView_CreateWithClassAndString()
    {
        var modelPtr = BridgeTestHelpers.CreateSimpleModel(42);
        AssertTrue(modelPtr != IntPtr.Zero, "CreateSimpleModel != 0");

        var titleBytes = Encoding.UTF8.GetBytes("My Video");
        fixed (byte* titlePtr = titleBytes)
        {
            var handle = BridgeNativeMethods.PlayerStyleView_Create(modelPtr, (IntPtr)titlePtr, titleBytes.Length);
            AssertTrue(handle != IntPtr.Zero, "PlayerStyleView handle != 0");

            var vcPtr = BridgeNativeMethods.PlayerStyleView_GetViewController(handle);
            AssertTrue(vcPtr != IntPtr.Zero, "PlayerStyleView GetVC != 0");

            var playerValue = BridgeTestHelpers.PlayerStyleView_GetPlayerValue(handle);
            AssertEqual(42, playerValue, "PlayerStyleView model value round-trip");

            BridgeNativeMethods.PlayerStyleView_Free(handle);
        }
        BridgeTestHelpers.FreeSimpleModel(modelPtr);
        TestLogger.Info("PlayerStyleView: class + string create/read/free passed");
    }

    // ────────────────────────────────────────────────────────────────
    // FormatActionView — RichTextKit ActionButton pattern (BoundStruct)
    // ────────────────────────────────────────────────────────────────

    public void TestFormatActionView_CompletedOutcome()
    {
        var outcomePtr = BridgeTestHelpers.CreateTransformOutcome_Completed(42);
        AssertTrue(outcomePtr != IntPtr.Zero, "CreateTransformOutcome_Completed != 0");

        var handle = BridgeNativeMethods.FormatActionView_Create(outcomePtr);
        AssertTrue(handle != IntPtr.Zero, "FormatActionView handle != 0");

        var vcPtr = BridgeNativeMethods.FormatActionView_GetViewController(handle);
        AssertTrue(vcPtr != IntPtr.Zero, "FormatActionView GetVC != 0");

        var value = BridgeTestHelpers.FormatActionView_GetOutcomeValue(handle);
        AssertEqual(42, value, "FormatActionView outcome value round-trip");

        var isCompleted = BridgeTestHelpers.FormatActionView_IsCompleted(handle);
        AssertEqual(1, isCompleted, "FormatActionView is .completed");

        BridgeNativeMethods.FormatActionView_Free(handle);
        BridgeTestHelpers.FreeTransformOutcome(outcomePtr);
        TestLogger.Info("FormatActionView: BoundStruct completed round-trip passed");
    }

    public void TestFormatActionView_FailedOutcome()
    {
        var outcomePtr = BridgeTestHelpers.CreateTransformOutcome_Failed(-1);
        var handle = BridgeNativeMethods.FormatActionView_Create(outcomePtr);
        AssertTrue(handle != IntPtr.Zero, "FormatActionView handle != 0");

        var value = BridgeTestHelpers.FormatActionView_GetOutcomeValue(handle);
        AssertEqual(-1, value, "FormatActionView error code round-trip");

        var isCompleted = BridgeTestHelpers.FormatActionView_IsCompleted(handle);
        AssertEqual(0, isCompleted, "FormatActionView is .failed");

        BridgeNativeMethods.FormatActionView_Free(handle);
        BridgeTestHelpers.FreeTransformOutcome(outcomePtr);
        TestLogger.Info("FormatActionView: BoundStruct failed round-trip passed");
    }

    // ────────────────────────────────────────────────────────────────
    // FormatMenuView — RichTextKit Menu pattern (closure with BoundStruct)
    // ────────────────────────────────────────────────────────────────

    public unsafe void TestFormatMenuView_ClosureFiresWithBoundStruct()
    {
        FormatMenuCallbackState.Reset();

        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> callbackPtr = &FormatMenuCallbackState.OnFormatCallback;
        var handle = BridgeNativeMethods.FormatMenuView_Create((IntPtr)callbackPtr, IntPtr.Zero);
        AssertTrue(handle != IntPtr.Zero, "FormatMenuView handle != 0");

        var vcPtr = BridgeNativeMethods.FormatMenuView_GetViewController(handle);
        AssertTrue(vcPtr != IntPtr.Zero, "FormatMenuView GetVC != 0");

        // Invoke the closure from Swift side with .completed(result: 99)
        var invokeResult = BridgeTestHelpers.FormatMenuView_InvokeOnFormat(handle, 1, 99);
        AssertEqual(1, invokeResult, "FormatMenuView invoke succeeded");
        AssertEqual(1, FormatMenuCallbackState.CallCount, "FormatMenuView callback fired once");
        AssertTrue(FormatMenuCallbackState.LastOutcomePtr != IntPtr.Zero, "FormatMenuView outcome ptr != 0");

        // Free the heap-allocated BoundStruct received from the closure callback
        BridgeTestHelpers.FreeTransformOutcome(FormatMenuCallbackState.LastOutcomePtr);

        BridgeNativeMethods.FormatMenuView_Free(handle);
        TestLogger.Info("FormatMenuView: BoundStruct closure fire passed");
    }

    // ────────────────────────────────────────────────────────────────
    // RichToolbarView — RichTextKit toolbar pattern (dual string)
    // ────────────────────────────────────────────────────────────────

    public unsafe void TestRichToolbarView_DualStringParams()
    {
        var titleBytes = Encoding.UTF8.GetBytes("Bold");
        var subtitleBytes = Encoding.UTF8.GetBytes("Toggle bold formatting");

        fixed (byte* titlePtr = titleBytes)
        fixed (byte* subtitlePtr = subtitleBytes)
        {
            var handle = BridgeNativeMethods.RichToolbarView_Create(
                (IntPtr)titlePtr, titleBytes.Length,
                (IntPtr)subtitlePtr, subtitleBytes.Length);
            AssertTrue(handle != IntPtr.Zero, "RichToolbarView handle != 0");

            var vcPtr = BridgeNativeMethods.RichToolbarView_GetViewController(handle);
            AssertTrue(vcPtr != IntPtr.Zero, "RichToolbarView GetVC != 0");

            var titleLen = BridgeTestHelpers.RichToolbarView_GetTitleLength(handle);
            AssertEqual(titleBytes.Length, titleLen, "RichToolbarView title length round-trip");

            var subtitleLen = BridgeTestHelpers.RichToolbarView_GetSubtitleLength(handle);
            AssertEqual(subtitleBytes.Length, subtitleLen, "RichToolbarView subtitle length round-trip");

            BridgeNativeMethods.RichToolbarView_Free(handle);
        }
        TestLogger.Info("RichToolbarView: dual string create/read/free passed");
    }

    public unsafe void TestRichToolbarView_EmptyStrings()
    {
        var emptyBytes = Array.Empty<byte>();
        fixed (byte* emptyPtr = emptyBytes)
        {
            var handle = BridgeNativeMethods.RichToolbarView_Create(
                (IntPtr)emptyPtr, 0, (IntPtr)emptyPtr, 0);
            AssertTrue(handle != IntPtr.Zero, "RichToolbarView handle != 0 with empty strings");

            var titleLen = BridgeTestHelpers.RichToolbarView_GetTitleLength(handle);
            AssertEqual(0, titleLen, "RichToolbarView empty title length");

            var subtitleLen = BridgeTestHelpers.RichToolbarView_GetSubtitleLength(handle);
            AssertEqual(0, subtitleLen, "RichToolbarView empty subtitle length");

            BridgeNativeMethods.RichToolbarView_Free(handle);
        }
        TestLogger.Info("RichToolbarView: empty strings passed");
    }

    // ────────────────────────────────────────────────────────────────
    // BindingToggleView — Session 2 gate: Binding<Bool>
    // Tests $state.isOn Binding projection at runtime.
    // ────────────────────────────────────────────────────────────────

    public void TestBindingToggleView_CreateWithInitialValue()
    {
        // Create with isOn = true (Int32 1)
        var handle = BridgeNativeMethods.BindingToggleView_Create(1);
        AssertTrue(handle != IntPtr.Zero, "BindingToggleView handle != 0");

        var vcPtr = BridgeNativeMethods.BindingToggleView_GetViewController(handle);
        AssertTrue(vcPtr != IntPtr.Zero, "BindingToggleView GetVC != 0");

        var isOn = BridgeTestHelpers.BindingToggleView_GetIsOn(handle);
        AssertEqual(1, isOn, "BindingToggleView initial isOn == true");

        BridgeNativeMethods.BindingToggleView_Free(handle);
        TestLogger.Info("BindingToggleView: create with initial value passed");
    }

    public void TestBindingToggleView_CreateWithFalse()
    {
        // Create with isOn = false (Int32 0)
        var handle = BridgeNativeMethods.BindingToggleView_Create(0);
        AssertTrue(handle != IntPtr.Zero, "BindingToggleView handle != 0 (false)");

        var isOn = BridgeTestHelpers.BindingToggleView_GetIsOn(handle);
        AssertEqual(0, isOn, "BindingToggleView initial isOn == false");

        BridgeNativeMethods.BindingToggleView_Free(handle);
        TestLogger.Info("BindingToggleView: create with false passed");
    }

    public void TestBindingToggleView_UpdateToggle()
    {
        // Create with isOn = false
        var handle = BridgeNativeMethods.BindingToggleView_Create(0);
        AssertTrue(handle != IntPtr.Zero, "BindingToggleView handle != 0");

        var isOnBefore = BridgeTestHelpers.BindingToggleView_GetIsOn(handle);
        AssertEqual(0, isOnBefore, "BindingToggleView starts false");

        // Toggle to true via Update
        BridgeNativeMethods.BindingToggleView_UpdateIsOn(handle, 1);
        var isOnAfter = BridgeTestHelpers.BindingToggleView_GetIsOn(handle);
        AssertEqual(1, isOnAfter, "BindingToggleView toggled to true");

        // Toggle back to false
        BridgeNativeMethods.BindingToggleView_UpdateIsOn(handle, 0);
        var isOnFinal = BridgeTestHelpers.BindingToggleView_GetIsOn(handle);
        AssertEqual(0, isOnFinal, "BindingToggleView toggled back to false");

        BridgeNativeMethods.BindingToggleView_Free(handle);
        TestLogger.Info("BindingToggleView: update toggle round-trip passed");
    }

    // ────────────────────────────────────────────────────────────────
    // NumberListView — Session 2 gate: Array<Int>
    // Tests pointer+count ABI and UnsafeBufferPointer.map reconstruction.
    // ────────────────────────────────────────────────────────────────

    public unsafe void TestNumberListView_CreateWithArray()
    {
        var numbers = new int[] { 10, 20, 30, 40, 50 };
        fixed (int* ptr = numbers)
        {
            var handle = BridgeNativeMethods.NumberListView_Create((IntPtr)ptr, numbers.Length);
            AssertTrue(handle != IntPtr.Zero, "NumberListView handle != 0");

            var vcPtr = BridgeNativeMethods.NumberListView_GetViewController(handle);
            AssertTrue(vcPtr != IntPtr.Zero, "NumberListView GetVC != 0");

            var count = BridgeTestHelpers.NumberListView_GetCount(handle);
            AssertEqual(5, count, "NumberListView count == 5");

            var elem0 = BridgeTestHelpers.NumberListView_GetElement(handle, 0);
            AssertEqual(10, elem0, "NumberListView element[0] == 10");

            var elem2 = BridgeTestHelpers.NumberListView_GetElement(handle, 2);
            AssertEqual(30, elem2, "NumberListView element[2] == 30");

            var elem4 = BridgeTestHelpers.NumberListView_GetElement(handle, 4);
            AssertEqual(50, elem4, "NumberListView element[4] == 50");

            BridgeNativeMethods.NumberListView_Free(handle);
        }
        TestLogger.Info("NumberListView: array create/read/free passed");
    }

    public unsafe void TestNumberListView_EmptyArray()
    {
        // Pass null pointer with count 0 for empty array
        var handle = BridgeNativeMethods.NumberListView_Create(IntPtr.Zero, 0);
        AssertTrue(handle != IntPtr.Zero, "NumberListView handle != 0 (empty)");

        var count = BridgeTestHelpers.NumberListView_GetCount(handle);
        AssertEqual(0, count, "NumberListView empty count == 0");

        BridgeNativeMethods.NumberListView_Free(handle);
        TestLogger.Info("NumberListView: empty array passed");
    }

    // ────────────────────────────────────────────────────────────────
    // SymbolIconView — Session 2 gate: SwiftUI.Image
    // Tests Image(systemName:) construction from bridged String.
    // ────────────────────────────────────────────────────────────────

    public unsafe void TestSymbolIconView_CreateWithSFSymbol()
    {
        var symbolBytes = Encoding.UTF8.GetBytes("star.fill");
        fixed (byte* symbolPtr = symbolBytes)
        {
            var handle = BridgeNativeMethods.SymbolIconView_Create((IntPtr)symbolPtr, symbolBytes.Length);
            AssertTrue(handle != IntPtr.Zero, "SymbolIconView handle != 0");

            var vcPtr = BridgeNativeMethods.SymbolIconView_GetViewController(handle);
            AssertTrue(vcPtr != IntPtr.Zero, "SymbolIconView GetVC != 0");

            var iconLen = BridgeTestHelpers.SymbolIconView_GetIconLength(handle);
            AssertEqual(symbolBytes.Length, iconLen, "SymbolIconView icon length round-trip");

            BridgeNativeMethods.SymbolIconView_Free(handle);
        }
        TestLogger.Info("SymbolIconView: SF Symbol create/read/free passed");
    }

    public unsafe void TestSymbolIconView_DifferentSymbol()
    {
        var symbolBytes = Encoding.UTF8.GetBytes("heart.circle.fill");
        fixed (byte* symbolPtr = symbolBytes)
        {
            var handle = BridgeNativeMethods.SymbolIconView_Create((IntPtr)symbolPtr, symbolBytes.Length);
            AssertTrue(handle != IntPtr.Zero, "SymbolIconView handle != 0 (heart)");

            var iconLen = BridgeTestHelpers.SymbolIconView_GetIconLength(handle);
            AssertEqual(symbolBytes.Length, iconLen, "SymbolIconView heart symbol length");

            BridgeNativeMethods.SymbolIconView_Free(handle);
        }
        TestLogger.Info("SymbolIconView: different symbol passed");
    }
}

#endif
