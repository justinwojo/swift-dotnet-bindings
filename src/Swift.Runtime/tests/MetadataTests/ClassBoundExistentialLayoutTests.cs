// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A class instance boxed into the widened existential container must also read correctly as a
/// class-bound existential, which Swift lays out as <c>[reference][witness table]</c>: the witness
/// table has to sit in the word right after the reference, not only in the dedicated witness word.
/// A value payload keeps its inline words untouched.
/// </summary>
public class ClassBoundExistentialLayoutTests
{
    [Fact]
    public void Create_ClassPayload_PlacesWitnessTableAfterTheReference()
    {
        var value = new NSObjectReferenceMock((IntPtr)0x1230);
        var container = ExistentialContainerFactory.Create<NSObjectReferenceMock, ISwiftHashable>(value);

        Assert.Equal((IntPtr)0x1230, container.Payload0);
        Assert.NotEqual(IntPtr.Zero, container[0]);
        Assert.Equal(container[0], container.Payload1);
        Assert.Equal(IntPtr.Zero, container.Payload2);
    }

    [Fact]
    public void Create_ClassPayload_ReadsBackAsClassBoundCarrier()
    {
        var value = new NSObjectReferenceMock((IntPtr)0x4560);
        var container = ExistentialContainerFactory.Create<NSObjectReferenceMock, ISwiftHashable>(value);

        var carrier = ClassExistentialContainer1.FromExistentialContainer1(container);
        Assert.Equal((IntPtr)0x4560, carrier.ClassRef);
        Assert.Equal(container[0], carrier.WitnessTable0);
    }

    [Fact]
    public void Create_ValuePayload_LeavesInlineWordsToTheValue()
    {
        var value = new SwiftIntMock(42);
        var container = ExistentialContainerFactory.Create<SwiftIntMock, ISwiftHashable>(value);

        Assert.Equal((IntPtr)42, container.Payload0);
        Assert.Equal(IntPtr.Zero, container.Payload1);
        Assert.NotEqual(IntPtr.Zero, container[0]);
    }
}

/// <summary>
/// An Objective-C class payload: NSObject metadata and its Hashable conformance from the
/// ObjectiveC overlay. The reference word is never dereferenced by the factory, so a sentinel
/// value stands in for a real instance.
/// </summary>
sealed class NSObjectReferenceMock : ISwiftObject
{
    private readonly IntPtr _reference;

    public NSObjectReferenceMock(IntPtr reference) => _reference = reference;

    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
    {
        if (typeof(TProtocol) != typeof(ISwiftHashable))
            throw new SwiftRuntimeException("Protocol conformance not found");
        return ProtocolConformanceDescriptor.LoadFromSymbol(
            "/usr/lib/swift/libswiftObjectiveC.dylib", "$sSo8NSObjectCSH10ObjectiveCMc");
    }

    static TypeMetadata ISwiftObject.GetTypeMetadata() => ObjCInterop.GetTypeMetadata("NSObject");

    static PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics
        => PayloadConstructionSemantics.Adopt;

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr payload) => throw new NotSupportedException();

    unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        fixed (void* swiftDest = swiftDestSpan)
        {
            *(IntPtr*)swiftDest = _reference;
            return IntPtr.Size;
        }
    }

    public void Dispose() { }
}
