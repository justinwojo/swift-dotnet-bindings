using Swift;
using Swift.Runtime;

namespace BindingsGeneration.Tests;

struct TypeNotImplementingAnyProtocols { }

struct SwiftIntMock : ISwiftObject
{
    public int Value { get; set; }

    public SwiftIntMock(int value)
    {
        Value = value;
    }

    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
    {
        var dic = new Dictionary<Type, string>
            {
                { typeof(ISwiftHashable), "$sSiSHsMc"}, // protocol conformance descriptor for Swift.Int : Swift.Hashable in Swift
                { typeof(IEquatable<SwiftIntMock>), "$sSiSQsMc"}, // protocol conformance descriptor for Swift.Int : Swift.Equatable in Swift
            };

        if (!dic.ContainsKey(typeof(TProtocol)))
        {
            throw new SwiftRuntimeException("Protocol conformance not found");
        }

        return ProtocolConformanceDescriptor.LoadFromSymbol("/usr/lib/swift/libswiftCore.dylib", dic[typeof(TProtocol)]);
    }

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return TypeMetadata.GetTypeMetadataOrThrow<nint>();
    }

    // Value-type ISwiftObject: the marshal seam short-circuits to Inline before reading this,
    // so the declared value is inert — declared honestly as Inline to satisfy the contract.
    static global::Swift.Runtime.PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics
        => global::Swift.Runtime.PayloadConstructionSemantics.Inline;

    static unsafe ISwiftObject ISwiftObject.NewFromPayload(IntPtr payload)
    {
        return new SwiftIntMock(*(int*)payload);
    }

    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        unsafe
        {
            fixed (void* swiftDest = swiftDestSpan)
            {
                *(int*)swiftDest = Value;
                return 4;
            }
        }
    }

    public void Dispose() { }
}

struct AnyTypeMock : ISwiftObject
{
    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
    {
        return ProtocolConformanceDescriptor.Zero;
    }

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return TypeMetadata.Zero;
    }

    // Value-type ISwiftObject: short-circuits to Inline before this is read (inert, declared honestly).
    static global::Swift.Runtime.PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics
        => global::Swift.Runtime.PayloadConstructionSemantics.Inline;

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr payload)
    {
        throw new NotImplementedException();
    }

    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        throw new NotImplementedException();
    }

    public void Dispose() { }
}
