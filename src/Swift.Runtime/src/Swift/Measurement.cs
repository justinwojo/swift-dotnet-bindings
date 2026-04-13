// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Swift.Runtime;

namespace Swift;

/// <summary>
/// C# projection of Foundation.Measurement&lt;UnitType&gt;, a non-frozen generic struct
/// containing a Double value and an ObjC-bridged unit type.
/// Uses VWT-backed storage via SwiftSafeHandle for proper ARC management of the unit reference.
/// </summary>
/// <typeparam name="T">The unit type (must be an NSUnit subclass, e.g., Foundation.NSUnitTemperature).
/// Constrained to class because unit types are ObjC reference types.</typeparam>
public sealed class Measurement<T> : ISwiftObject, ISwiftStruct, IDisposable where T : class
{
    private SwiftSafeHandle<Measurement<T>> _payload = SwiftSafeHandle<Measurement<T>>.Zero;
    private bool _disposed;
    private static TypeMetadata? _cachedMetadata;

    public SwiftSafeHandle<Measurement<T>> Payload => _payload;

    IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();

    /// <summary>The numeric value of the measurement.</summary>
    /// <remarks>
    /// Reads the Double at offset 0 of the VWT-managed payload. This assumes the
    /// Measurement layout starts with the value field, which has been stable since iOS 10.
    /// A fully resilient approach would require per-unit-type Swift property accessors,
    /// which is impractical for a generic C# projection.
    /// </remarks>
    public double Value
    {
        get
        {
            unsafe
            {
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    return *(double*)_payload.DangerousGetHandle();
                }
                finally
                {
                    if (success) _payload.DangerousRelease();
                }
            }
        }
    }

    /// <summary>
    /// The raw handle to the unit object (ObjC NSUnit subclass).
    /// Consumers can use platform-specific APIs to convert to the typed unit.
    /// The handle is valid for the lifetime of this Measurement instance.
    /// </summary>
    public IntPtr UnitHandle
    {
        get
        {
            unsafe
            {
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    return *(IntPtr*)((byte*)_payload.DangerousGetHandle() + sizeof(double));
                }
                finally
                {
                    if (success) _payload.DangerousRelease();
                }
            }
        }
    }

    static TypeMetadata ISwiftObject.GetTypeMetadata()
        => _cachedMetadata ??= InitializeMetadata();

    private static TypeMetadata InitializeMetadata()
    {
        // T is an NSUnit subclass whose C# name matches the ObjC class name
        // (e.g., NSUnitTemperature → "NSUnitTemperature"). The ObjC class pointer
        // IS the Swift type metadata for ObjC-bridged classes.
        var unitClassName = typeof(T).Name;
        var unitClassHandle = ObjCInterop.GetObjCClassHandle(unitClassName);
        var metadata = MeasurementInterop.GetMeasurementMetadata(TypeMetadata.FromHandle(unitClassHandle));
        _cachedMetadata = metadata;
        return metadata;
    }

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        var metadata = _cachedMetadata ??= InitializeMetadata();
        unsafe
        {
            var size = (int)metadata.Size;
            var heapCopy = NativeMemory.Alloc((nuint)size);
            metadata.ValueWitnessTable->InitializeWithCopy(heapCopy, (void*)handle, metadata);
            return new Measurement<T>((IntPtr)heapCopy);
        }
    }

    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        var metadata = _cachedMetadata ??= InitializeMetadata();
        if ((int)metadata.Size > swiftDestSpan.Length)
            throw new ArgumentException($"Span size mismatch: expected {(int)metadata.Size}, got {swiftDestSpan.Length}");
        unsafe
        {
            fixed (void* dest = swiftDestSpan)
            {
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    metadata.ValueWitnessTable->InitializeWithCopy(dest, (void*)_payload.DangerousGetHandle(), metadata);
                    return (int)metadata.Size;
                }
                finally
                {
                    if (success) _payload.DangerousRelease();
                }
            }
        }
    }

    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
        => throw new SwiftRuntimeException($"Protocol conformance not implemented for Measurement<{typeof(T).Name}> and {typeof(TProtocol).Name}");

    internal Measurement(IntPtr handle) => _payload = new SwiftSafeHandle<Measurement<T>>(handle);

    public void Dispose()
    {
        if (!_disposed)
        {
            _payload.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Non-generic helper for Measurement metadata P/Invoke.
/// DllImport cannot be inside a generic type (CS7042), so the metadata accessor
/// is exposed from this non-generic class.
/// </summary>
internal static class MeasurementInterop
{
    // Route through the @_cdecl wrapper in SwiftBindingsRuntime to avoid the
    // Mono JIT !ji->async assertion (upstream Issue 1) that fires when P/Invoking
    // the Foundation metadata accessor $s10Foundation11MeasurementVMa directly.
    [DllImport("SwiftBindingsRuntime", EntryPoint = "SBW_Measurement_GetMetadata",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr PInvoke_GetMeasurementMetadata(IntPtr unitMetadata);

    internal static TypeMetadata GetMeasurementMetadata(TypeMetadata unitMetadata)
        => TypeMetadata.FromHandle(PInvoke_GetMeasurementMetadata(unitMetadata.Handle));
}
