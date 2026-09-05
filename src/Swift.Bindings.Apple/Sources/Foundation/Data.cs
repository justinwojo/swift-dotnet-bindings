// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Swift.Runtime;

namespace Swift.Foundation;

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
    internal static extern TypeMetadata PInvoke_getMetadata();

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new Data(handle);
    }

    /// <inheritdoc/>
    static PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics
        => PayloadConstructionSemantics.Inline;

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
    internal static unsafe extern Data PInvoke_InitWithBytes(UnsafeRawPointer pointer, nint count);

    /// <summary>Returns the number of bytes held by this Data.</summary>
    public readonly nint Count => PInvoke_GetCount(this);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation4DataV5countSivg")]
    internal static unsafe extern nint PInvoke_GetCount(Data data);

    /// <summary>Copies the bytes of this Data into the supplied destination buffer.</summary>
    /// <param name="buffer">Destination buffer; must be at least <paramref name="count"/> bytes wide.</param>
    /// <param name="count">Number of bytes to copy (typically <see cref="Count"/>).</param>
    public unsafe void CopyBytes(UnsafeMutablePointer<byte> buffer, nint count)
    {
        PInvoke_CopyBytes(buffer, count, this);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation4DataV9copyBytes2to5countySpys5UInt8VG_SitF")]
    internal static unsafe extern void PInvoke_CopyBytes(UnsafeMutablePointer<byte> buffer, nint count, Data data);

    /// <summary>
    /// Creates a Swift.Data from a byte array.
    /// </summary>
    /// <param name="bytes">The byte array to convert.</param>
    /// <returns>A Swift.Data representation of the byte array.</returns>
    /// <exception cref="ArgumentNullException">Thrown if bytes is null.</exception>
    public static unsafe Data FromByteArray(byte[] bytes)
    {
        if (bytes == null) throw new ArgumentNullException(nameof(bytes));
        if (bytes.Length == 0) return new Data(new UnsafeRawPointer(null), 0);
        fixed (byte* ptr = bytes)
        {
            return new Data(new UnsafeRawPointer(ptr), bytes.Length);
        }
    }

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

#if IOS || TVOS || MACCATALYST || MACOS
    /// <summary>
    /// Converts this Swift.Data to a .NET iOS global::Foundation.NSData.
    /// </summary>
    /// <returns>An NSData representation of this Data.</returns>
    public global::Foundation.NSData ToNSData()
    {
        return global::Foundation.NSData.FromArray(ToByteArray());
    }

    /// <summary>
    /// Creates a Swift.Data from a .NET iOS global::Foundation.NSData.
    /// </summary>
    /// <param name="nsData">The NSData to convert.</param>
    /// <returns>A Swift.Data representation of the NSData.</returns>
    /// <exception cref="ArgumentNullException">Thrown if nsData is null.</exception>
    public static unsafe Data FromNSData(global::Foundation.NSData nsData)
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
    /// Implicitly converts a global::Foundation.NSData to a Swift.Data.
    /// </summary>
    /// <param name="nsData">The NSData to convert.</param>
    public static implicit operator Data(global::Foundation.NSData nsData) => FromNSData(nsData);

    /// <summary>
    /// Implicitly converts a Swift.Data to a global::Foundation.NSData.
    /// </summary>
    /// <param name="data">The Swift.Data to convert.</param>
    public static implicit operator global::Foundation.NSData(Data data) => data.ToNSData();
#endif

    /// <summary>
    /// No-op, deliberately: a <see cref="Data"/> value is a <b>non-owning view</b> of a Swift
    /// <c>Foundation.Data</c>, so there is nothing for it to release.
    /// </summary>
    /// <remarks>
    /// <para><b>The ownership model this type declares.</b> <see cref="Data"/> declares
    /// <see cref="PayloadConstructionSemantics.Inline"/>, and its <c>NewFromPayload</c> is a bitwise
    /// read (<c>this = *(Data*)handle</c>) of the two words Swift wrote — the flags word and the
    /// out-of-line <c>__DataStorage</c> reference for any payload past the inline threshold. That
    /// read takes no retain, so the value it produces <em>aliases</em> a payload that belongs to
    /// whatever produced it: the indirect-result buffer the marshal seam allocated, the enclosing
    /// Swift value a borrowed slot read it out of, or the <c>+1</c> a Swift initializer returned by
    /// value. A copy of a <see cref="Data"/> is another alias of that same payload, not a second
    /// owner — the struct is freely copied by every C# assignment and argument pass, and it has no
    /// retain to make copies independent with.</para>
    /// <para>So releasing belongs to the producer, at the seam that knows how the value was
    /// produced — and only a seam that <em>consumes</em> the value can release it. An indirect
    /// result whose body applies the projection (a method or global function returning
    /// <c>byte[]</c>) has copied the bytes out before its <c>finally</c> runs, so it releases the
    /// payload through <c>SwiftMarshal.ReleaseIndirectResultValue</c>. An <b>accessor</b> seam does
    /// not: a property emits a private getter that hands the raw <see cref="Data"/> back and a
    /// public property that projects it afterwards, so the value outlives the getter's cleanup and
    /// that cleanup frees only the buffer's storage — releasing the payload there would hand the
    /// property a freed allocation. Making this method destructive instead — value-witness
    /// <c>Destroy</c>ing <see cref="_object"/> — would release payloads this value only borrows
    /// (the borrowed-slot and embedded-buffer reads above), and would fire once per copy. It is
    /// not a matter of adding a null guard: the type cannot tell an owning value from an alias,
    /// because nothing in the two words says which it is.</para>
    /// <para>The consequence to be aware of: a <see cref="Data"/> that reaches managed code
    /// <em>without</em> passing a consuming seam — the accessor-getter shape above, a value
    /// returned by value from a Swift initializer such as <see cref="FromByteArray"/>, or read out
    /// of a borrowed carrier and kept — holds a payload nothing will release. Giving those a home
    /// needs a decision on what a
    /// <see cref="Data"/> copy means (an owning handle with a real <see cref="IDisposable"/>
    /// contract, or a projection that never surfaces the raw value), not a destructive
    /// <c>Dispose</c> bolted onto the alias model.</para>
    /// </remarks>
    public void Dispose() { }
}
