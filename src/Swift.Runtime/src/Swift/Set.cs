// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

namespace Swift;

/// <summary>
/// Represents a Swift set payload.
///
/// The following diagram illustrates the hierarchy of a Swift set type.
/// The actual implementation may differ:
///
///    struct Set
///    +-----------------------------------------------------------------------+
///    |   struct Variant                                                     |
///    |   +--------------------------------------------------------------+   |
///    |   |   struct Object                                              |   |
///    |   |   +------------------------------------------------------+   |   |
///    |   |   | var rawValue: IntPtr                                 |   |   |
///    |   |   +------------------------------------------------------+   |   |
///    |   +--------------------------------------------------------------+   |
///    +-----------------------------------------------------------------------+
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 8)]
public struct Variant
{
    public IntPtr rawValue;
}

/// <summary>
/// Represents a Swift set.
/// </summary>
/// <typeparam name="Element">The element type contained in the set.</typeparam>
public class SwiftSet<Element> : IDisposable, ISwiftObject
{
    static nuint _payloadSize = SwiftObjectHelper<SwiftSet<Element>>.GetTypeMetadata().Size;
    static nuint _elementSize = ElementTypeMetadata.Size;

    // Swift set is a value type and doesn't contain an IntPtr payload
    private Variant variant;
    public unsafe void Dispose()
    {
        if (variant.rawValue != IntPtr.Zero)
        {
            Arc.Release(*(IntPtr*)variant.rawValue);
            variant.rawValue = IntPtr.Zero;
            GC.SuppressFinalize(this);
        }
    }

    unsafe ~SwiftSet()
    {
        Arc.Release(*(IntPtr*)variant.rawValue);
        variant.rawValue = IntPtr.Zero;
    }
    public static nuint PayloadSize => _payloadSize;
    public Variant Payload => variant;
    public static nuint ElementSize => _elementSize;
    public static ProtocolWitnessTable IHashableWitnessTable
    {
        get
        {
            ProtocolWitnessTable.TryGet<Element, IHashable>(out var witnessTable);
            return witnessTable.HasValue ? witnessTable.Value : ProtocolWitnessTable.Zero;
        }
    }
    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return TypeMetadata.Cache.GetOrAdd(typeof(SwiftSet<Element>), _ => SwiftSetPInvokes.PInvoke_getMetadata(TypeMetadataRequest.Complete, ElementTypeMetadata));
    }

    static TypeMetadata ElementTypeMetadata
    {
        get => TypeMetadata.GetTypeMetadataOrThrow<Element>();
    }

    static ISwiftObject ISwiftObject.NewFromPayload(SwiftHandle handle)
    {
        return new SwiftSet<Element>(handle);
    }

    IntPtr ISwiftObject.MarshalToSwift(IntPtr swiftDest)
    {
        var metadata = SwiftObjectHelper<SwiftSet<Element>>.GetTypeMetadata();
        unsafe
        {
            fixed (void* _payloadPtr = &variant)
            {
                metadata.ValueWitnessTable->InitializeWithCopy((void*)swiftDest, (void*)_payloadPtr, metadata);
            }
        }
        return swiftDest;
    }

    /// <summary>
    /// Gets the protocol conformance descriptor for the given type.
    /// </summary>
    /// <typeparam name="TProtocol"></typeparam>
    /// <returns></returns>
    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
    {
        return ProtocolConformanceDescriptor.Zero;
    }

    /// <summary>
    /// Constructs a new SwiftSet from the given handle.
    /// </summary>
    unsafe SwiftSet(SwiftHandle handle)
    {
        this.variant = *(Variant*)(handle);
    }

    /// <summary>
    /// Constructs a new empty SwiftSet.
    /// </summary>
    public SwiftSet()
    {
        variant = SwiftSetPInvokes.Init(ElementTypeMetadata, IHashableWitnessTable);
    }

    /// <summary>
    /// Gets the number of elements in the set.
    /// </summary>
    public unsafe int Count
    {
        get
        {
            return (int)SwiftSetPInvokes.Count(variant, ElementTypeMetadata, IHashableWitnessTable);
        }
    }
}

internal static class SwiftSetPInvokes
{
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sShMa")]
    public static extern TypeMetadata PInvoke_getMetadata(TypeMetadataRequest request, TypeMetadata typeMetadata);

    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sS2hyxGycfC")]
    public static extern Variant Init(TypeMetadata typeMetadata, ProtocolWitnessTable witnessTable);

    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSh5countSivg")]
    public static extern nint Count(Variant handle, TypeMetadata elementMetadata, ProtocolWitnessTable witnessTable);
}
