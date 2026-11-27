// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Swift;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// An <c>AnyObject</c> crosses as the class reference alone. The factory resolves the C# value to
/// that reference, or refuses a value that does not stand for a class instance, so a mistake
/// surfaces as an exception before the call instead of as a pointer Swift misreads.
/// </summary>
public class ObjectReferenceTests
{
    private static ExistentialContainer1 Borrow(object value)
        => ExistentialContainerFactory.GetOrCreateObjectReference(value, out _, out _);

    [Fact]
    public void PlainManagedObjects_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => Borrow(new object()));
        Assert.Throws<ArgumentException>(() => Borrow("text"));
        Assert.Throws<ArgumentException>(() => ExistentialContainerFactory.CreateOwnedObjectReference(new object()));
    }

    [Fact]
    public void Null_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => Borrow(null!));
        Assert.Throws<ArgumentNullException>(() => ExistentialContainerFactory.CreateOwnedObjectReference(null!));
    }

    [Fact]
    public void SwiftValueTypes_AreRejected()
    {
        // A Swift struct wrapper is an ISwiftObject too, but its handle is a value buffer, not an object.
        var error = Assert.Throws<ArgumentException>(
            () => Borrow(new SwiftIntMock(4)));
        Assert.Contains("value type", error.Message);
    }

    [Fact]
    public void ContainerHoldingAValue_IsRejected()
    {
        // An opaque container stands for an object only when its metadata is a class's.
        var container = ExistentialContainerFactory.Create<SwiftIntMock, ISwiftHashable>(new SwiftIntMock(5));
        Assert.Throws<ArgumentException>(() => Borrow(container));
    }

    [Fact]
    public void ClassBoundCarrier_LendsItsReference()
    {
        var reference = (IntPtr)0x1000;
        var carrier = new SwiftExistential<ExistentialContainer1>(new ExistentialContainer1 { Payload0 = reference }, classBound: true);
        try
        {
            var container = ExistentialContainerFactory.GetOrCreateObjectReference(carrier, out var owns, out var keepAlive);
            Assert.Equal(reference, container.Payload0);
            Assert.False(owns);
            Assert.Same(carrier, keepAlive);
        }
        finally
        {
            // The address is not a real object, so the carrier must never release it.
            GC.SuppressFinalize(carrier);
        }
    }

    [Fact]
    public void DisposedCarrier_IsRejected()
    {
        // Releasing a null reference is a no-op, so the carrier can be disposed without an object.
        var carrier = new SwiftExistential<ExistentialContainer1>(default, classBound: true);
        carrier.Dispose();
        Assert.Throws<ObjectDisposedException>(() => Borrow(carrier));
    }

    [Fact]
    public void CarrierWithoutAReference_IsRejected()
    {
        // A live carrier made without a reference would hand Swift a null where it reads an object.
        var carrier = new SwiftExistential<ExistentialContainer1>(default, classBound: true);
        try
        {
            Assert.Throws<ObjectDisposedException>(() => Borrow(carrier));
            Assert.Throws<ObjectDisposedException>(() => ExistentialContainerFactory.CreateOwnedObjectReference(carrier));
        }
        finally
        {
            carrier.Dispose();
        }
    }

    [Fact]
    public void ReadObjectReference_ReadsOnlyTheFirstWord()
    {
        var cell = Marshal.AllocHGlobal(2 * IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(cell, (IntPtr)0x2000);
            Marshal.WriteIntPtr(cell, IntPtr.Size, (IntPtr)0x3000);
            var container = ExistentialContainerFactory.ReadObjectReference(cell);
            Assert.Equal((IntPtr)0x2000, container.Payload0);
            Assert.Equal(IntPtr.Zero, container.Payload1);
        }
        finally
        {
            Marshal.FreeHGlobal(cell);
        }
    }

    [Fact]
    public void ReadObjectReference_RejectsANullCell()
        => Assert.Throws<ArgumentNullException>(() => ExistentialContainerFactory.ReadObjectReference(IntPtr.Zero));
}
