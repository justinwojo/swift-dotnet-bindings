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

    // Routes through SwiftObjectHelper so RunClassConstructor (the NativeAOT fallback in
    // TypeMetadata.TryGetTypeMetadataUncached / SwiftMarshal.MarshalFromSwiftCore) both
    // populates TypeMetadata.Cache AND registers the NewFromPayload factory. Direct
    // registration without the factory causes SIGSEGV when MarshalFromSwift falls back
    // to reflection on closed generic instantiations under NativeAOT. Mirrors SwiftOptional<T>.
    private static readonly nuint _payloadSize = SwiftObjectHelper<Measurement<T>>.GetTypeMetadata().Size;

    public SwiftSafeHandle<Measurement<T>> Payload => _payload;

    IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();

    /// <summary>The numeric value of the measurement.</summary>
    /// <remarks>
    /// Swift's Foundation.Measurement is declared as <c>{ unit: UnitType; value: Double }</c>,
    /// so with an 8-byte class reference for <c>unit</c>, the Double lives at offset 8.
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
                    return *(double*)((byte*)_payload.DangerousGetHandle() + sizeof(IntPtr));
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
                    return *(IntPtr*)_payload.DangerousGetHandle();
                }
                finally
                {
                    if (success) _payload.DangerousRelease();
                }
            }
        }
    }

    static TypeMetadata ISwiftObject.GetTypeMetadata() => TypeMetadata.Cache.GetOrAdd(typeof(Measurement<T>), _ =>
    {
        // T is an NSUnit subclass whose C# name matches the ObjC class name.
        // Route through swift_getObjCClassMetadata so the Swift runtime returns
        // proper type metadata with a valid VWT — the raw ObjC class pointer is
        // not interchangeable with Swift generic argument metadata on NativeAOT
        // and passing it directly crashes the Measurement metadata accessor.
        var unitClassName = typeof(T).Name;
        var unitMetadata = ObjCInterop.GetTypeMetadata(unitClassName);
        return MeasurementInterop.GetMeasurementMetadata(unitMetadata);
    });

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        var metadata = SwiftObjectHelper<Measurement<T>>.GetTypeMetadata();
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
        var metadata = SwiftObjectHelper<Measurement<T>>.GetTypeMetadata();
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
