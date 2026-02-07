// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

namespace Swift;

/// <summary>
/// Represents a Swift dictionary.
/// </summary>
/// <typeparam name="TKey">The key type (must be Hashable in Swift).</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public class SwiftDictionary<TKey, TValue> : ISwiftObject
{
    static nuint _payloadSize = SwiftObjectHelper<SwiftDictionary<TKey, TValue>>.GetTypeMetadata().Size;

    static nuint _keySize = KeyTypeMetadata.Size;

    static nuint _valueSize = ValueTypeMetadata.Size;

    private SwiftSafeHandle<SwiftDictionary<TKey, TValue>> _payload;

    public SwiftSafeHandle<SwiftDictionary<TKey, TValue>> Payload => _payload;

    public unsafe PayloadBuffer<IntPtr> PayloadBuffer => new PayloadBuffer<IntPtr>(_payload);

    private static Dictionary<Type, string> _protocolConformanceSymbols;

    static SwiftDictionary()
    {
        _protocolConformanceSymbols = new Dictionary<Type, string>
        {
            { typeof(ISwiftCollection), "$sSDyq_xGSlsMc" }
        };
    }

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        var witnessTable = ProtocolWitnessTable.GetOrThrow<TKey, ISwiftHashable>();
        return TypeMetadata.Cache.GetOrAdd(typeof(SwiftDictionary<TKey, TValue>), _ =>
            SwiftDictionaryPInvokes.PInvoke_getMetadata(TypeMetadataRequest.Complete, KeyTypeMetadata, ValueTypeMetadata, witnessTable));
    }

    static TypeMetadata KeyTypeMetadata
    {
        get => TypeMetadata.GetTypeMetadataOrThrow<TKey>();
    }

    static TypeMetadata ValueTypeMetadata
    {
        get => TypeMetadata.GetTypeMetadataOrThrow<TValue>();
    }

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new SwiftDictionary<TKey, TValue>(handle);
    }

    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        var metadata = SwiftObjectHelper<SwiftDictionary<TKey, TValue>>.GetTypeMetadata();
        if ((int)metadata.Size > swiftDestSpan.Length)
        {
            throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
        }
        unsafe
        {
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure the payload is valid before making copy
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    metadata.ValueWitnessTable->InitializeWithCopy(swiftDest, (void*)_payload.DangerousGetHandle(), metadata);
                    return (int)metadata.Size;
                }
                finally
                {
                    if (success)
                        _payload.DangerousRelease();
                }
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
            throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type SwiftDictionary and protocol {typeof(TProtocol).Name}, but no conformance was found.");
        }
        return ProtocolConformanceDescriptor.LoadFromSymbol("/usr/lib/swift/libswiftCore.dylib", symbolName);
    }

    /// <summary>
    /// Constructs a new SwiftDictionary from the given handle.
    /// </summary>
    unsafe SwiftDictionary(IntPtr handle)
    {
        IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc((nuint)sizeof(IntPtr));
        *(IntPtr*)bufferPtr = *(IntPtr*)handle;
        _payload = new SwiftSafeHandle<SwiftDictionary<TKey, TValue>>(bufferPtr);
    }

    /// <summary>
    /// Constructs a new empty SwiftDictionary.
    /// </summary>
    public unsafe SwiftDictionary()
    {
        var witnessTable = ProtocolWitnessTable.GetOrThrow<TKey, ISwiftHashable>();
        var result = SwiftDictionaryPInvokes.Init(KeyTypeMetadata, ValueTypeMetadata, witnessTable);

        IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc((nuint)sizeof(IntPtr));
        *(IntPtr*)bufferPtr = result;
        _payload = new SwiftSafeHandle<SwiftDictionary<TKey, TValue>>(bufferPtr);
    }

    /// <summary>
    /// Gets the number of key-value pairs in the dictionary.
    /// </summary>
    public int Count
    {
        get
        {
            using PayloadBuffer<IntPtr> disposable = PayloadBuffer;
            var witnessTable = ProtocolWitnessTable.GetOrThrow<TKey, ISwiftHashable>();
            int result = (int)SwiftDictionaryPInvokes.Count(disposable.Buffer, KeyTypeMetadata, ValueTypeMetadata, witnessTable);
            return result;
        }
    }

    /// <summary>
    /// Gets or sets the value associated with the specified key.
    /// Note: Getting returns default(TValue) if the key is not found.
    /// </summary>
    public unsafe TValue this[TKey key]
    {
        get
        {
            using PayloadBuffer<IntPtr> disposable = PayloadBuffer;
            var witnessTable = ProtocolWitnessTable.GetOrThrow<TKey, ISwiftHashable>();

            // Marshal the key to Swift
            Span<byte> keySpan = stackalloc byte[(int)_keySize];
            SwiftMarshal.MarshalToSwift(key, ref keySpan);
            IntPtr keyPayload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(keySpan));

            // The subscript getter returns Optional<TValue>, which has size = value size + 1 byte for the tag
            // For simplicity, allocate enough space for the optional
            nuint optionalSize = _valueSize + 8; // Extra space for optional metadata/tag
            void* resultPayload = NativeMemory.Alloc(optionalSize);

            SwiftDictionaryPInvokes.Get(
                new SwiftIndirectResult(resultPayload),
                keyPayload,
                disposable.Buffer,
                KeyTypeMetadata,
                ValueTypeMetadata,
                witnessTable);

            // Check if the optional has a value (last byte is the discriminator)
            // In Swift, Optional.none has discriminator 0, Optional.some has discriminator 1 for single-payload enums
            // But for larger types, the layout may differ. This is a simplified approach.
            // For now, return the value directly - caller should check for default value
            return SwiftMarshal.MarshalFromSwift<TValue>((IntPtr)resultPayload);
        }
        set
        {
            var metadata = SwiftObjectHelper<SwiftDictionary<TKey, TValue>>.GetTypeMetadata();
            var witnessTable = ProtocolWitnessTable.GetOrThrow<TKey, ISwiftHashable>();

            bool success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                // Marshal the key
                Span<byte> keySpan = stackalloc byte[(int)_keySize];
                SwiftMarshal.MarshalToSwift(key, ref keySpan);
                IntPtr keyPayload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(keySpan));

                // Marshal the value
                Span<byte> valueSpan = stackalloc byte[(int)_valueSize];
                SwiftMarshal.MarshalToSwift(value, ref valueSpan);
                IntPtr valuePayload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(valueSpan));

                // Allocate space for optional return value (old value if any)
                nuint optionalSize = _valueSize + 8;
                void* resultPayload = NativeMemory.Alloc(optionalSize);

                SwiftDictionaryPInvokes.UpdateValue(
                    new SwiftIndirectResult(resultPayload),
                    valuePayload,
                    keyPayload,
                    KeyTypeMetadata,
                    ValueTypeMetadata,
                    witnessTable,
                    new SwiftSelf((void*)_payload.DangerousGetHandle()));

                NativeMemory.Free(resultPayload);
            }
            finally
            {
                if (success)
                    _payload.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Removes all key-value pairs from the dictionary.
    /// </summary>
    public unsafe void RemoveAll()
    {
        var metadata = SwiftObjectHelper<SwiftDictionary<TKey, TValue>>.GetTypeMetadata();
        var witnessTable = ProtocolWitnessTable.GetOrThrow<TKey, ISwiftHashable>();

        bool success = false;
        _payload.DangerousAddRef(ref success);
        try
        {
            SwiftDictionaryPInvokes.RemoveAll(
                1, // keepingCapacity: true
                KeyTypeMetadata,
                ValueTypeMetadata,
                witnessTable,
                new SwiftSelf((void*)_payload.DangerousGetHandle()));
        }
        finally
        {
            if (success)
                _payload.DangerousRelease();
        }
    }

    /// <summary>
    /// Removes the value for the specified key.
    /// </summary>
    /// <param name="key">The key to remove.</param>
    /// <returns>The removed value, or default if the key was not present.</returns>
    public unsafe TValue RemoveValue(TKey key)
    {
        var metadata = SwiftObjectHelper<SwiftDictionary<TKey, TValue>>.GetTypeMetadata();
        var witnessTable = ProtocolWitnessTable.GetOrThrow<TKey, ISwiftHashable>();

        bool success = false;
        _payload.DangerousAddRef(ref success);
        try
        {
            // Marshal the key
            Span<byte> keySpan = stackalloc byte[(int)_keySize];
            SwiftMarshal.MarshalToSwift(key, ref keySpan);
            IntPtr keyPayload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(keySpan));

            // Allocate space for optional return value
            nuint optionalSize = _valueSize + 8;
            void* resultPayload = NativeMemory.Alloc(optionalSize);

            SwiftDictionaryPInvokes.RemoveValue(
                new SwiftIndirectResult(resultPayload),
                keyPayload,
                KeyTypeMetadata,
                ValueTypeMetadata,
                witnessTable,
                new SwiftSelf((void*)_payload.DangerousGetHandle()));

            return SwiftMarshal.MarshalFromSwift<TValue>((IntPtr)resultPayload);
        }
        finally
        {
            if (success)
                _payload.DangerousRelease();
        }
    }
}

internal static class SwiftDictionaryPInvokes
{
    // Dictionary metadata accessor: $sSDMa
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSDMa")]
    public static extern TypeMetadata PInvoke_getMetadata(
        TypeMetadataRequest request,
        TypeMetadata keyTypeMetadata,
        TypeMetadata valueTypeMetadata,
        ProtocolWitnessTable witnessTable);

    // Dictionary init: $sSDyxq_GycfC (init())
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSDyxq_GycfC")]
    public static extern IntPtr Init(
        TypeMetadata keyTypeMetadata,
        TypeMetadata valueTypeMetadata,
        ProtocolWitnessTable witnessTable);

    // Dictionary count: $sSD5countSivg
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSD5countSivg")]
    public static extern nint Count(
        IntPtr handle,
        TypeMetadata keyTypeMetadata,
        TypeMetadata valueTypeMetadata,
        ProtocolWitnessTable witnessTable);

    // Dictionary subscript getter: $sSDyq_Sgxcig (subscript getter returns Optional<Value>)
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSDyq_Sgxcig")]
    public static extern void Get(
        SwiftIndirectResult result,
        IntPtr key,
        IntPtr handle,
        TypeMetadata keyTypeMetadata,
        TypeMetadata valueTypeMetadata,
        ProtocolWitnessTable witnessTable);

    // Dictionary updateValue(_:forKey:): $sSD11updateValue_6forKeyq_Sgq_n_xtF
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSD11updateValue_6forKeyq_Sgq_n_xtF")]
    public static extern void UpdateValue(
        SwiftIndirectResult result,
        IntPtr value,
        IntPtr key,
        TypeMetadata keyTypeMetadata,
        TypeMetadata valueTypeMetadata,
        ProtocolWitnessTable witnessTable,
        SwiftSelf self);

    // Dictionary removeAll(keepingCapacity:): $sSD9removeAll15keepingCapacityySb_tF
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSD9removeAll15keepingCapacityySb_tF")]
    public static extern void RemoveAll(
        byte keepCapacity,
        TypeMetadata keyTypeMetadata,
        TypeMetadata valueTypeMetadata,
        ProtocolWitnessTable witnessTable,
        SwiftSelf self);

    // Dictionary removeValue(forKey:): $sSD11removeValue6forKeyq_Sgx_tF
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSD11removeValue6forKeyq_Sgx_tF")]
    public static extern void RemoveValue(
        SwiftIndirectResult result,
        IntPtr key,
        TypeMetadata keyTypeMetadata,
        TypeMetadata valueTypeMetadata,
        ProtocolWitnessTable witnessTable,
        SwiftSelf self);
}
