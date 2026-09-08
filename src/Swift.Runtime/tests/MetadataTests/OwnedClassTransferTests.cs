// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
using System;
using System.Runtime.InteropServices;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

public class OwnedClassTransferTests
{
    // Null ARC is a no-op, so these controls exercise pin/exception state on any host.
    // Native deinit probes separately test the +1 rollback and completed handover.
    private sealed class CountingHandle : SafeHandle
    {
        public int Releases { get; private set; }
        public CountingHandle() : base(IntPtr.Zero, true) { }
        public override bool IsInvalid => false;
        protected override bool ReleaseHandle() { Releases++; return true; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransferPin_SpansCallAndReleasesOnce(bool complete)
    {
        var handle = new CountingHandle();
        var transfer = new OwnedArgument.ClassTransfer(handle);
        handle.Dispose();
        Assert.Equal(0, handle.Releases);
        if (complete) { transfer.Complete(); transfer.Complete(); }
        transfer.Dispose();
        transfer.Dispose();
        Assert.Equal(1, handle.Releases);
    }

    [Fact]
    public void DisposedLaterArgument_UnwindsEarlierTransferPin()
    {
        var first = new CountingHandle();
        var second = new CountingHandle();
        second.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
        {
            using var earlier = new OwnedArgument.ClassTransfer(first);
            first.Dispose();
            using var later = new OwnedArgument.ClassTransfer(second);
        });
        Assert.Equal(1, first.Releases);
        Assert.Equal(1, second.Releases);
    }

    [Fact]
    public void NullHandle_RejectsBeforeAcquisition()
        => Assert.Throws<ArgumentNullException>(() => new OwnedArgument.ClassTransfer(null!));
}
