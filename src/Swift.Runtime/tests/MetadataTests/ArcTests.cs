// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using Swift.Runtime;
using Xunit;

namespace Swift.Runtime.Tests;

public class ArcTests
{
    [Fact]
    public void RetainMultiple_EmptySpan_DoesNothing()
    {
        Arc.RetainMultiple(ReadOnlySpan<IntPtr>.Empty);
    }

    [Fact]
    public void ReleaseMultiple_EmptySpan_DoesNothing()
    {
        Arc.ReleaseMultiple(ReadOnlySpan<IntPtr>.Empty);
    }

    [Fact]
    public void RetainMultiple_NullPointer_ThrowsArgumentException()
    {
        var pointers = new IntPtr[] { IntPtr.Zero };
        Assert.Throws<ArgumentException>(() => Arc.RetainMultiple(pointers));
    }

    [Fact]
    public void ReleaseMultiple_NullPointer_ThrowsArgumentException()
    {
        var pointers = new IntPtr[] { IntPtr.Zero };
        Assert.Throws<ArgumentException>(() => Arc.ReleaseMultiple(pointers));
    }

    [Fact]
    public void RetainMultiple_ValidThenNull_ReportsCorrectIndex()
    {
        // Exercises the original crash path: a valid (non-null) pointer followed by null.
        // Pre-validation must catch the null at index 1 before calling swift_retain on
        // the valid pointer. If pre-validation regresses, swift_retain on allocated (but
        // non-Swift) memory will crash the process — that crash IS the regression signal.
        var allocated = Marshal.AllocHGlobal(64);
        try
        {
            var pointers = new IntPtr[] { allocated, IntPtr.Zero };
            var ex = Assert.Throws<ArgumentException>(() => Arc.RetainMultiple(pointers));
            Assert.Contains("index 1", ex.Message);
        }
        finally
        {
            Marshal.FreeHGlobal(allocated);
        }
    }

    [Fact]
    public void ReleaseMultiple_ValidThenNull_ReportsCorrectIndex()
    {
        // Same pattern as RetainMultiple: valid pointer at index 0, null at index 1.
        // Pre-validation must reject before any native swift_isDeallocating/swift_release call.
        var allocated = Marshal.AllocHGlobal(64);
        try
        {
            var pointers = new IntPtr[] { allocated, IntPtr.Zero };
            var ex = Assert.Throws<ArgumentException>(() => Arc.ReleaseMultiple(pointers));
            Assert.Contains("index 1", ex.Message);
        }
        finally
        {
            Marshal.FreeHGlobal(allocated);
        }
    }
}
