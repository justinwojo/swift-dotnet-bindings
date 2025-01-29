using Swift.Runtime;

namespace BindingsGeneration.Tests;

struct TypeNotImplementingAnyProtocols { }


struct AnyTypeMock : ISwiftObject
{
    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
    {
        return ProtocolConformanceDescriptor.Zero;
    }

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return TypeMetadata.Zero;
    }

    static ISwiftObject ISwiftObject.NewFromPayload(SwiftHandle payload)
    {
        throw new NotImplementedException();
    }

    nint ISwiftObject.MarshalToSwift(nint swiftDest)
    {
        throw new NotImplementedException();
    }
}
