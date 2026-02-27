// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift.SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for pointer marshalling: IntPtr param/return, opaque/raw pointer.
/// Note: Generic pointer containers (PointerContainer&lt;T&gt;) are known-unsupported
/// due to ISwiftObject constraint on IntPtr (CS0315). Tracked as a focused follow-up.
/// </summary>
public class PointerMarshallingTests : TestBase
{
    public PointerMarshallingTests(TestResults results) : base(results) { }

    #region Tier 1 — Smoke Tests

    [TestTier(TestTier.Tier1)]
    public void TestGetStaticBuffer()
    {
        var ptr = SwiftBindingsTestLib.GetStaticBuffer();
        AssertTrue(ptr != IntPtr.Zero, "GetStaticBuffer returns non-null pointer");
        TestLogger.Info($"GetStaticBuffer() = 0x{ptr:X}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestReadPointerValue()
    {
        var ptr = SwiftBindingsTestLib.GetStaticBuffer();
        var value = SwiftBindingsTestLib.ReadPointerValue(ptr);
        AssertEqual(42, value, "First buffer element is 42");
        TestLogger.Info($"ReadPointerValue(staticBuffer) = {value}");
    }

    #endregion

    #region Tier 2 — Functional Tests

    [TestTier(TestTier.Tier2)]
    public void TestWritePointerValue()
    {
        // Allocate a mutable buffer and write to it
        var ptr = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(ptr, 0);
            SwiftBindingsTestLib.WritePointerValue(ptr, 99);
            var readBack = Marshal.ReadInt32(ptr);
            AssertEqual(99, readBack, "Write then read back");
            TestLogger.Info("WritePointerValue round-trip passed");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [TestTier(TestTier.Tier2)]
    public void TestIncrementPointer()
    {
        var ptr = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(ptr, 10);
            SwiftBindingsTestLib.IncrementPointer(ptr, 5);
            var value = Marshal.ReadInt32(ptr);
            AssertEqual(15, value, "IncrementPointer(10, 5) = 15");
            TestLogger.Info($"IncrementPointer: 10 + 5 = {value}");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [TestTier(TestTier.Tier2)]
    public void TestFillBuffer()
    {
        var count = 4;
        var ptr = Marshal.AllocHGlobal(sizeof(int) * count);
        try
        {
            SwiftBindingsTestLib.FillBuffer(ptr, count, 7);
            for (int i = 0; i < count; i++)
            {
                var value = Marshal.ReadInt32(ptr + i * sizeof(int));
                AssertEqual(7, value, $"FillBuffer[{i}]");
            }
            TestLogger.Info("FillBuffer passed");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [TestTier(TestTier.Tier2)]
    public void TestOpaquePointerIsValid()
    {
        var ptr = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            var valid = SwiftBindingsTestLib.OpaquePointerIsValid(ptr);
            AssertTrue(valid, "OpaquePointer is valid");
            TestLogger.Info("OpaquePointerIsValid passed");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [TestTier(TestTier.Tier2)]
    public void TestRawPointerToInt32()
    {
        var ptr = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(ptr, 123);
            var value = SwiftBindingsTestLib.RawPointerToInt32(ptr);
            AssertEqual(123, value, "RawPointerToInt32");
            TestLogger.Info($"RawPointerToInt32 = {value}");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [TestTier(TestTier.Tier2)]
    public void TestStoreInt32()
    {
        var ptr = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            SwiftBindingsTestLib.StoreInt32(ptr, 456);
            var value = Marshal.ReadInt32(ptr);
            AssertEqual(456, value, "StoreInt32 round-trip");
            TestLogger.Info($"StoreInt32: wrote 456, read {value}");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [TestTier(TestTier.Tier2)]
    public void TestOptionalOpaquePointerWithNull()
    {
        var result = SwiftBindingsTestLib.OptionalOpaquePointer(null);
        AssertFalse(result, "Optional null OpaquePointer is not valid");
        TestLogger.Info("OptionalOpaquePointer(null) = false");
    }

    #endregion
}
