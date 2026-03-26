// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Swift.Runtime;
using Xunit;

namespace Swift.Runtime.Tests;

public class ArcTests
{
    [Fact]
    public void RetainMultiple_EmptySpan_DoesNothing()
    {
        // Should not throw for an empty span
        Arc.RetainMultiple(ReadOnlySpan<IntPtr>.Empty);
    }

    [Fact]
    public void ReleaseMultiple_EmptySpan_DoesNothing()
    {
        // Should not throw for an empty span
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
    public void RetainMultiple_NullAtIndex_ReportsCorrectIndex()
    {
        // First pointer is valid (non-zero), second is null
        var pointers = new IntPtr[] { new IntPtr(0x1234), IntPtr.Zero };
        var ex = Assert.Throws<ArgumentException>(() => Arc.RetainMultiple(pointers));
        Assert.Contains("index 1", ex.Message);
    }

    [Fact]
    public void ReleaseMultiple_NullAtIndex_ReportsCorrectIndex()
    {
        var pointers = new IntPtr[] { new IntPtr(0x1234), IntPtr.Zero };
        var ex = Assert.Throws<ArgumentException>(() => Arc.ReleaseMultiple(pointers));
        Assert.Contains("index 1", ex.Message);
    }
}
