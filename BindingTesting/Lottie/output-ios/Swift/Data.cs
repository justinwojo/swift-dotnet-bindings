// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Swift;
using Swift.Runtime;

namespace Swift;

/// <summary>
/// Represents Swift Foundation.DataProtocol in C#.
/// </summary>
public interface ISwiftDataProtocol { }

/// <summary>
/// Represents Swift Foundation.ContiguousBytes in C#.
/// </summary>
public interface ISwiftContiguousBytes { }

/// <summary>
/// Represents Foundation.Data type.
/// https://developer.apple.com/documentation/foundation/data
/// </summary>
public struct Data : ISwiftObject
{
    private long _flags;
    private IntPtr _object;

    private static nuint _payloadSize = SwiftObjectHelper<Data>.GetTypeMetadata().Size;

    private static Dictionary<Type, string> _protocolConformanceSymbols;

    static Data()
    {
        _protocolConformanceSymbols = new Dictionary<Type, string> {
            { typeof(ISwiftDataProtocol), "$s10Foundation4DataVAA0B8ProtocolAAMc" },
            { typeof(ISwiftContiguousBytes), "$s10Foundation4DataVAA15ContiguousBytesAAMc" },
        };
    }

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return TypeMetadata.Cache.GetOrAdd(typeof(Data), _ => PInvoke_getMetadata());
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation4DataVMa")]
    public static extern TypeMetadata PInvoke_getMetadata();

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new Data(handle);
    }

    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        var metadata = SwiftObjectHelper<Data>.GetTypeMetadata();
        if ((int)metadata.Size > swiftDestSpan.Length)
        {
            throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
        }
        unsafe
        {
            fixed (void* _payloadPtr = &this)
            fixed (void* swiftDest = swiftDestSpan)
            {
                metadata.ValueWitnessTable->InitializeWithCopy(swiftDest, _payloadPtr, metadata);
                return (int)metadata.Size;
            }
        }
    }

    /// <summary>
    /// Gets the protocol conformance descriptor for the given type.
    /// </summary>
    /// <typeparam name="TProtocol"></typeparam>
    /// <returns></returns>
    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
        where TProtocol : class
    {
        if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
        {
            throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Data and protocol {typeof(TProtocol).Name}, but no conformance was found.");
        }
        return ProtocolConformanceDescriptor.LoadFromSymbol(KnownLibraries.SwiftFoundation, symbolName);
    }

    /// <summary>
    /// Constructs a new Data from the given handle.
    /// </summary>
    unsafe Data(IntPtr handle)
    {
        this = *(Data*)handle;
    }

    /// <summary>
    /// Constructs a new Data from the given buffer.
    /// </summary>
    public unsafe Data(UnsafeRawPointer pointer, nint count)
    {
        this = PInvoke_InitWithBytes(pointer, count);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation4DataV5bytes5countACSV_SitcfC")]
    public static unsafe extern Data PInvoke_InitWithBytes(UnsafeRawPointer pointer, nint count);

    public readonly nint Count => PInvoke_GetCount(this);


    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation4DataV5countSivg")]
    public static unsafe extern nint PInvoke_GetCount(Data data);

    public unsafe void CopyBytes(UnsafeMutablePointer<byte> buffer, nint count)
    {
        PInvoke_CopyBytes(buffer, count, this);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation4DataV9copyBytes2to5countySpys5UInt8VG_SitF")]
    public static unsafe extern void PInvoke_CopyBytes(UnsafeMutablePointer<byte> buffer, nint count, Data data);

    /// <summary>
    /// Converts this Swift.Data to a byte array.
    /// </summary>
    /// <returns>A byte array containing the data.</returns>
    public unsafe byte[] ToByteArray()
    {
        var count = Count;
        if (count == 0)
            return Array.Empty<byte>();

        var bytes = new byte[count];
        fixed (byte* ptr = bytes)
        {
            CopyBytes(new UnsafeMutablePointer<byte>(ptr), count);
        }
        return bytes;
    }

#if IOS || MACCATALYST || MACOS
    /// <summary>
    /// Converts this Swift.Data to a .NET iOS Foundation.NSData.
    /// </summary>
    /// <returns>An NSData representation of this Data.</returns>
    public Foundation.NSData ToNSData()
    {
        return Foundation.NSData.FromArray(ToByteArray());
    }

    /// <summary>
    /// Creates a Swift.Data from a .NET iOS Foundation.NSData.
    /// </summary>
    /// <param name="nsData">The NSData to convert.</param>
    /// <returns>A Swift.Data representation of the NSData.</returns>
    /// <exception cref="ArgumentNullException">Thrown if nsData is null.</exception>
    public static unsafe Data FromNSData(Foundation.NSData nsData)
    {
        if (nsData == null)
            throw new ArgumentNullException(nameof(nsData));

        var bytes = nsData.ToArray();
        if (bytes.Length == 0)
            return new Data(new UnsafeRawPointer(null), 0);

        fixed (byte* ptr = bytes)
        {
            return new Data(new UnsafeRawPointer(ptr), bytes.Length);
        }
    }

    /// <summary>
    /// Implicitly converts a Foundation.NSData to a Swift.Data.
    /// </summary>
    /// <param name="nsData">The NSData to convert.</param>
    public static implicit operator Data(Foundation.NSData nsData) => FromNSData(nsData);

    /// <summary>
    /// Implicitly converts a Swift.Data to a Foundation.NSData.
    /// </summary>
    /// <param name="data">The Swift.Data to convert.</param>
    public static implicit operator Foundation.NSData(Data data) => data.ToNSData();
#endif

    /// <inheritdoc/>
    public void Dispose() { }
}
