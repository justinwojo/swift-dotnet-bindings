// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Swift.Runtime;

namespace Swift.Foundation;

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

    /// <summary>The safe handle wrapping the native Swift storage for this Measurement.</summary>
    public SwiftSafeHandle<Measurement<T>> Payload => _payload;

    IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();

    // Non-reflective borrowed-marshal finalizer suppression (Finding 56a). See ISwiftObject.SuppressPayloadFinalizer.
    void ISwiftObject.SuppressPayloadFinalizer() => global::System.GC.SuppressFinalize(_payload);

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

    // Measurement carries conditional conformances in Foundation
    // (`extension Measurement : Comparable/Equatable/Hashable where UnitType : Dimension`).
    // All WorkoutKit/HealthKit unit types are Dimension subclasses, so these
    // descriptors instantiate against the concrete unit metadata. Comparable is
    // what unblocks SwiftClosedRange<Measurement<…>> (range alerts): its metadata
    // accessor requires the Bound's Comparable witness table.
    private static readonly Dictionary<Type, string> _protocolConformanceSymbols = new()
    {
        { typeof(global::Swift.ISwiftComparable), "$s10Foundation11MeasurementVyxGSLAAMc" },
        { typeof(global::Swift.ISwiftEquatable),  "$s10Foundation11MeasurementVyxGSQAAMc" },
        { typeof(global::Swift.ISwiftHashable),   "$s10Foundation11MeasurementVyxGSHAAMc" },
    };

    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
    {
        if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            throw new SwiftRuntimeException($"Protocol conformance not implemented for Measurement<{typeof(T).Name}> and {typeof(TProtocol).Name}");
        return ProtocolConformanceDescriptor.LoadFromSymbol(KnownLibraries.SwiftFoundation, symbolName);
    }

    static Measurement()
    {
        // SwiftClosedRange<Measurement<T>> (the WorkoutKit range-alert bound shape)
        // resolves the Bound's Comparable witness table through the *unconstrained*
        // ComparableConformanceRegistry — it has no `ISwiftObject` constraint to dispatch
        // the static-virtual GetProtocolConformanceDescriptor, so on NativeAOT it falls to
        // MakeGenericMethod, which is unsupported and throws. Pre-register the conformance
        // here through the constrained static-virtual path (no-op on Mono, where reflection
        // works) so the registry finds the table without reflection. Best-effort: a
        // registration that cannot resolve on a given platform must not brick Measurement
        // construction/reads — the call site then simply falls back to its prior path.
        TryRegisterConformance<global::Swift.ISwiftComparable>();
    }

    private static void TryRegisterConformance<TProtocol>() where TProtocol : class
    {
        try
        {
            global::Swift.Runtime.InteropServices.SwiftMarshal.RegisterWitnessTable<Measurement<T>, TProtocol>();
        }
        catch (SwiftRuntimeException)
        {
            // Descriptor/witness table not resolvable on this platform; leave the table
            // unregistered so the unconstrained registry falls back at the call site.
        }
    }

    internal Measurement(IntPtr handle) => _payload = new SwiftSafeHandle<Measurement<T>>(handle);

    /// <summary>
    /// Constructs a Measurement from a numeric value and a unit instance.
    /// </summary>
    /// <param name="value">The numeric magnitude.</param>
    /// <param name="unit">
    /// The unit. Must be an Objective-C bridged NSUnit subclass (an
    /// <see cref="global::ObjCRuntime.INativeObject"/>), e.g. <c>Foundation.NSUnitLength.Meters</c>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="unit"/> is not an Objective-C bridged type or has a null handle.
    /// </exception>
    public unsafe Measurement(double value, T unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit is not global::ObjCRuntime.INativeObject native)
            throw new ArgumentException(
                $"Measurement unit type '{typeof(T).Name}' must be an Objective-C bridged NSUnit (INativeObject).",
                nameof(unit));
        IntPtr unitHandle = (IntPtr)native.Handle;
        if (unitHandle == IntPtr.Zero)
            throw new ArgumentException("Unit handle is null.", nameof(unit));

        var metadata = SwiftObjectHelper<Measurement<T>>.GetTypeMetadata();
        void* buffer = NativeMemory.Alloc((nuint)metadata.Size);
        bool constructed;
        try
        {
            constructed = MeasurementInterop.InitFromValueUnit(value, unitHandle, (IntPtr)buffer);
        }
        catch
        {
            NativeMemory.Free(buffer);
            throw;
        }
        if (!constructed)
        {
            // INativeObject only proves the handle is an ObjC object, not that its dynamic
            // type is an NSUnit subclass. The Swift shim reports the conditional-cast result:
            // a non-NSUnit handle leaves the buffer uninitialized, so free the raw bytes (no
            // VWT destroy ran) and reject as a managed error rather than an `as!` process trap.
            NativeMemory.Free(buffer);
            throw new ArgumentException(
                $"Measurement unit '{unit.GetType().Name}' is not a Foundation NSUnit subclass.",
                nameof(unit));
        }
        // SwiftSafeHandle takes ownership: on release it runs the VWT destroy
        // (releasing the retained unit reference) and frees this buffer.
        _payload = new SwiftSafeHandle<Measurement<T>>((IntPtr)buffer);
    }

    /// <summary>Releases the native Swift storage backing this Measurement.</summary>
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

    // Constructs Measurement(value:unit:) via the real Swift initializer and writes
    // the struct into resultPtr. unitHandle is the ObjC object handle of the NSUnit.
    // Returns true when the handle's dynamic type is an NSUnit subclass and the struct
    // was written; false (buffer left uninitialized) when the type does not match.
    [DllImport("SwiftBindingsRuntime", EntryPoint = "SBW_Measurement_InitFromValueUnit",
        CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool PInvoke_InitFromValueUnit(double value, IntPtr unitHandle, IntPtr resultPtr);

    internal static bool InitFromValueUnit(double value, IntPtr unitHandle, IntPtr resultPtr)
        => PInvoke_InitFromValueUnit(value, unitHandle, resultPtr);
}
