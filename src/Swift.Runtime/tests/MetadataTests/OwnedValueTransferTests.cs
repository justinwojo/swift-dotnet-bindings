// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
using System;
using System.Runtime.InteropServices;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using Xunit;

namespace BindingsGeneration.Tests;

public unsafe class OwnedValueTransferTests
{
    // Each test owns its counters through source storage; no shared test-order dependency.
    private struct Counters { public int Copies; public int Destroys; }

    [UnmanagedCallersOnly]
    private static void* Copy(void* destination, void* source, TypeMetadata metadata)
    {
        var counters = *(Counters**)source;
        counters->Copies++;
        System.Buffer.MemoryCopy(source, destination, (long)metadata.Stride, (long)metadata.Stride);
        return destination;
    }

    [UnmanagedCallersOnly]
    private static void Destroy(void* value, TypeMetadata metadata) => (*(Counters**)value)->Destroys++;

    [UnmanagedCallersOnly]
    private static void* CopyEmpty(void* destination, void* source, TypeMetadata metadata) => destination;

    [UnmanagedCallersOnly]
    private static void DestroyEmpty(void* value, TypeMetadata metadata) { }

    private sealed class Fixture : IDisposable
    {
        private readonly IntPtr* _metadata;
        private readonly ValueWitnessTable* _witnesses;
        public readonly Counters* Counts = (Counters*)NativeMemory.AllocZeroed((nuint)sizeof(Counters));
        public readonly CountingHandle Handle;
        public TypeMetadata Metadata => TypeMetadata.FromHandle((IntPtr)(_metadata + 1));

        public Fixture(bool empty = false)
        {
            _witnesses = (ValueWitnessTable*)NativeMemory.AllocZeroed(512);
            _witnesses->InitializeWithCopy = empty ? &CopyEmpty : &Copy;
            _witnesses->Destroy = empty ? &DestroyEmpty : &Destroy;
            _witnesses->Size = empty ? 0u : (nuint)sizeof(IntPtr);
            // Copy writes the full stride: allocating only Size is insufficient.
            _witnesses->Stride = empty ? 0u : 32u;
            _witnesses->Flags = empty ? 0 : ValueWitnessFlags.IsNonPOD;
            _metadata = (IntPtr*)NativeMemory.AllocZeroed((nuint)(2 * sizeof(IntPtr)));
            _metadata[0] = (IntPtr)_witnesses;
            _metadata[1] = (IntPtr)0x200;
            var source = (Counters**)NativeMemory.AllocZeroed(32);
            *source = Counts;
            Handle = new CountingHandle((IntPtr)source);
        }

        public void Dispose()
        {
            Handle.Dispose();
            NativeMemory.Free(_metadata);
            NativeMemory.Free(_witnesses);
            NativeMemory.Free(Counts);
        }
    }

    private sealed class CountingHandle : SafeHandle
    {
        public int Releases { get; private set; }
        public CountingHandle(IntPtr source) : base(IntPtr.Zero, true) => SetHandle(source);
        public override bool IsInvalid => false;
        protected override bool ReleaseHandle() { Releases++; NativeMemory.Free((void*)handle); return true; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValueCopy_RollsBackOnlyBeforeCompletion_AndPinsCaller(bool complete)
    {
        using var fixture = new Fixture();
        var transfer = new OwnedArgument.ValueTransfer(fixture.Metadata, fixture.Handle);
        Assert.Equal(1, fixture.Counts->Copies);
        fixture.Handle.Dispose();
        Assert.Equal(0, fixture.Handle.Releases);
        if (complete) { transfer.Complete(); transfer.Complete(); }
        transfer.Dispose();
        transfer.Dispose();
        Assert.Equal(complete ? 0 : 1, fixture.Counts->Destroys);
        Assert.Equal(1, fixture.Handle.Releases);
    }

    [Fact]
    public void LaterAcquisitionFailure_RollsBackEachEarlierValueCopy()
    {
        using var first = new Fixture();
        using var second = new Fixture();
        using var later = new Fixture();
        later.Handle.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
        {
            using var a = new OwnedArgument.ValueTransfer(first.Metadata, first.Handle);
            using var b = new OwnedArgument.ValueTransfer(second.Metadata, second.Handle);
            using var c = new OwnedArgument.ValueTransfer(later.Metadata, later.Handle);
        });
        Assert.Equal(1, first.Counts->Copies);
        Assert.Equal(1, first.Counts->Destroys);
        Assert.Equal(1, second.Counts->Copies);
        Assert.Equal(1, second.Counts->Destroys);
        Assert.Equal(0, later.Counts->Copies);
    }

    [Fact]
    public void NullHandle_RejectsBeforeAcquisition()
        => Assert.Throws<ArgumentNullException>(() => new OwnedArgument.ValueTransfer(default, null!));

    [Fact]
    public void InvalidMetadata_WithLivePayload_RejectsWithoutCopyingOrLeakingPin()
    {
        using var fixture = new Fixture();
        var error = Assert.Throws<ArgumentException>(() =>
            new OwnedArgument.ValueTransfer(default, fixture.Handle));
        Assert.Equal("metadata", error.ParamName);
        Assert.Equal(0, fixture.Counts->Copies);
        Assert.Equal(0, fixture.Counts->Destroys);
        fixture.Handle.Dispose();
        Assert.Equal(1, fixture.Handle.Releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyPod_HasValidStorageAndBalancedPin(bool complete)
    {
        using var fixture = new Fixture(empty: true);
        var transfer = new OwnedArgument.ValueTransfer(fixture.Metadata, fixture.Handle);
        if (complete) transfer.Complete();
        transfer.Dispose();
        fixture.Handle.Dispose();
        Assert.Equal(1, fixture.Handle.Releases);
    }
}
