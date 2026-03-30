// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace Swift.Runtime.InteropServices;

#nullable enable

/// <summary>
/// Registry of NewFromPayload factory delegates, populated from constrained code paths
/// (SwiftObjectHelper&lt;T&gt;, MarshalFromSwiftObject&lt;T&gt;) and consumed from unconstrained
/// code paths (MarshalFromSwift&lt;T&gt;). This eliminates the need for reflection on NativeAOT
/// when the type has been previously accessed through any constrained API.
/// </summary>
internal static class NewFromPayloadDispatcher
{
    private static readonly ConcurrentDictionary<Type, Func<IntPtr, object>> _factories = new();

    /// <summary>
    /// Registers a factory delegate for a type. Called from constrained code paths on NativeAOT.
    /// Safe to call multiple times — subsequent calls are no-ops.
    /// </summary>
    internal static void Register(Type type, Func<IntPtr, object> factory)
    {
        _factories.TryAdd(type, factory);
    }

    /// <summary>
    /// Attempts to create an object using a previously registered factory.
    /// Returns null if no factory is registered for the given type.
    /// </summary>
    internal static object? TryCreate(Type type, IntPtr handle)
    {
        if (_factories.TryGetValue(type, out var factory))
            return factory(handle);
        return null;
    }
}

/// <summary>
/// Registry of GetProtocolConformanceDescriptor factory delegates, populated from constrained
/// code paths (ProtocolConformanceDescriptorHelper) and consumed from unconstrained code paths
/// (ProtocolConformanceDescriptor.TryGet). Keyed by (Type, ProtocolType) pairs.
/// </summary>
internal static class ConformanceDispatcher
{
    private static readonly ConcurrentDictionary<(Type, Type), Func<ProtocolConformanceDescriptor>> _factories = new();

    /// <summary>
    /// Registers a conformance factory. Called from constrained code paths on NativeAOT.
    /// </summary>
    internal static void Register(Type type, Type protocolType, Func<ProtocolConformanceDescriptor> factory)
    {
        _factories.TryAdd((type, protocolType), factory);
    }

    /// <summary>
    /// Attempts to get a conformance descriptor using a previously registered factory.
    /// Returns null if no factory is registered.
    /// </summary>
    internal static ProtocolConformanceDescriptor? TryGet(Type type, Type protocolType)
    {
        if (_factories.TryGetValue((type, protocolType), out var factory))
            return factory();
        return null;
    }
}

/// <summary>
/// Registry of pre-computed protocol witness tables, populated by generated [ModuleInitializer]
/// code at assembly load time. This eliminates the need for reflection-based
/// ProtocolWitnessTable.GetOrThrow on NativeAOT for SwiftDictionary/SwiftSet operations
/// where the type parameter lacks an ISwiftObject constraint (e.g., TKey in SwiftDictionary).
/// Keyed by (Type, ProtocolType) pairs, maps to the witness table handle (IntPtr).
/// </summary>
internal static class WitnessTableDispatcher
{
    private static readonly ConcurrentDictionary<(Type, Type), ProtocolWitnessTable> _tables = new();

    /// <summary>
    /// Registers a pre-computed witness table for a (type, protocol) pair.
    /// Called from generated [ModuleInitializer] code on NativeAOT.
    /// Safe to call multiple times — subsequent calls are no-ops.
    /// </summary>
    internal static void Register(Type type, Type protocolType, ProtocolWitnessTable witnessTable)
    {
        _tables.TryAdd((type, protocolType), witnessTable);
    }

    /// <summary>
    /// Attempts to get a pre-registered witness table for the given (type, protocol) pair.
    /// Returns false if no table is registered.
    /// </summary>
    internal static bool TryGet(Type type, Type protocolType, out ProtocolWitnessTable witnessTable)
    {
        return _tables.TryGetValue((type, protocolType), out witnessTable);
    }
}

/// <summary>
/// Represents a class for marshaling data to and from Swift
/// </summary>
public static class SwiftMarshal
{
    /// <summary>
    /// Pre-registers a NewFromPayload factory for a type so NativeAOT can create instances
    /// without reflection. Called by generated [ModuleInitializer] code at assembly load time.
    /// </summary>
    /// <typeparam name="T">The ISwiftObject type to register.</typeparam>
    public static void RegisterSwiftObjectFactory<T>() where T : ISwiftObject
    {
        NewFromPayloadDispatcher.Register(typeof(T), handle => (object)T.NewFromPayload(handle));
    }

    /// <summary>
    /// Pre-registers a protocol conformance factory for a (type, protocol) pair so NativeAOT
    /// can resolve conformances without reflection. Called by generated [ModuleInitializer] code.
    /// </summary>
    /// <typeparam name="TType">The ISwiftObject type.</typeparam>
    /// <typeparam name="TProtocol">The protocol interface type.</typeparam>
    public static void RegisterConformanceFactory<TType, TProtocol>()
        where TType : ISwiftObject
        where TProtocol : class
    {
        ConformanceDispatcher.Register(typeof(TType), typeof(TProtocol),
            () => TType.GetProtocolConformanceDescriptor<TProtocol>());
    }

    /// <summary>
    /// Pre-registers a protocol witness table for a (type, protocol) pair so
    /// SwiftDictionary/SwiftSet can resolve witness tables without reflection on NativeAOT.
    /// Called by generated [ModuleInitializer] code at assembly load time.
    /// The witness table is computed eagerly at registration time using the direct dispatch path.
    /// </summary>
    /// <typeparam name="TType">The ISwiftObject type (e.g., a struct conforming to Hashable).</typeparam>
    /// <typeparam name="TProtocol">The protocol interface type (e.g., ISwiftHashable).</typeparam>
    public static void RegisterWitnessTable<TType, TProtocol>()
        where TType : ISwiftObject
        where TProtocol : class
    {
        // GetOrThrowDirect uses static virtual dispatch (NativeAOT-only).
        // On Mono (JIT or AOT/simulator), witness tables are resolved via reflection
        // at call time — no pre-registration needed.
        if (!SwiftRuntimeInfo.IsNativeAotRuntime)
            return;
        var witnessTable = ProtocolWitnessTable.GetOrThrowDirect<TType, TProtocol>();
        WitnessTableDispatcher.Register(typeof(TType), typeof(TProtocol), witnessTable);
    }

    /// <summary>
    /// Marshals a value to a Swift destination
    /// </summary>
    /// <typeparam name="T">The type of the value being marshaled</typeparam>
    /// <param name="value">The value to marshal</param>
    /// <param name="swiftDestSpan">the destination for marshaling</param>
    /// <returns>the number of bytes written to the destination</returns>
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Tuple marshalling path only; non-tuple paths are AOT-safe")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Tuple marshalling path only; non-tuple paths are trim-safe")]
    [UnconditionalSuppressMessage("Trimming", "IL2087", Justification = "Tuple marshalling path only; non-tuple paths are trim-safe")]
    [UnconditionalSuppressMessage("Trimming", "IL2091", Justification = "Tuple marshalling path only; non-tuple paths are trim-safe")]
    public static int MarshalToSwift<T>(T value, ref Span<byte> swiftDestSpan)
    {
        if (value is ISwiftObject swiftValue)
        {
            return swiftValue.MarshalToSwift(ref swiftDestSpan);
        }

        var type = typeof(T);
        if ((type.IsPrimitive || typeof(nint).IsAssignableFrom(type) || typeof(nuint).IsAssignableFrom(type)) && !typeof(char).IsAssignableFrom(type))
        {
            unsafe
            {
                int size = Unsafe.SizeOf<T>();
                if (size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    MarshalPrimitiveToSwift(value, swiftDest);
                    return size;
                }
            }
        }

        // Handle tuple types (ValueTuple<T1, T2, ...>)
        // Note: Tuple marshalling uses reflection internally, but this is intentional
        // for the generic runtime path. Generated bindings use inline code instead.
        if (TypeMetadata.IsValueTupleType(type))
        {
            return MarshalTupleToSwift(value, type, ref swiftDestSpan);
        }

        // Handle delegate types (closures)
        if (typeof(Delegate).IsAssignableFrom(type))
        {
            unsafe
            {
                if (value is Delegate delegateValue)
                {
                    // For now, only support @convention(c) closures which are just function pointers
                    // Escaping closures require a thunk which is generated by the emitter
                    var closureData = SwiftClosureMarshaller.CreateConventionCClosure(delegateValue);

                    // Write the closure data (function pointer + context) to the destination
                    int closureSize = sizeof(SwiftClosureData);
                    if (closureSize > swiftDestSpan.Length)
                    {
                        throw new ArgumentException($"Span size does not match closure size, Expected: {closureSize}, Actual: {swiftDestSpan.Length}");
                    }
                    fixed (void* swiftDest = swiftDestSpan)
                    {
                        *(SwiftClosureData*)swiftDest = closureData;
                        return closureSize;
                    }
                }
            }
        }

        // Handle existential containers (Swift protocol types like 'any Protocol')
        if (typeof(IExistentialContainer).IsAssignableFrom(type))
        {
            if (value is IExistentialContainer container)
            {
                int containerSize = container.SizeOf;
                if (containerSize > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match container size, Expected: {containerSize}, Actual: {swiftDestSpan.Length}");
                }
                unsafe
                {
                    fixed (void* swiftDest = swiftDestSpan)
                    {
                        container.CopyTo((IntPtr)swiftDest);
                        return containerSize;
                    }
                }
            }
        }

        // Handle blittable value types: C# enums (simple enums) and frozen structs
        // (CGPoint, CGRect, CGSize, etc.). These have no managed references and can be
        // written directly as raw bytes. Primitives are already handled above.
        if (type.IsValueType && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            unsafe
            {
                int size = Unsafe.SizeOf<T>();
                // Simple enum size mismatch: C# enum : int is 4 bytes, but Swift simple enums
                // use the minimum bytes needed for the discriminator (1 byte for ≤256 cases,
                // 2 for ≤65536, 4 for larger). When the caller provides a Swift-sized span
                // (e.g., SwiftArray ElementSize), narrow the C# int to the Swift width
                // instead of throwing or overwriting adjacent memory.
                if (size > swiftDestSpan.Length && type.IsEnum &&
                    !typeof(ISwiftObject).IsAssignableFrom(type) && swiftDestSpan.Length >= 1)
                {
                    int enumValue = Convert.ToInt32(value);
                    fixed (void* swiftDest = swiftDestSpan)
                    {
                        switch (swiftDestSpan.Length)
                        {
                            case 1:
                                ((byte*)swiftDest)[0] = (byte)enumValue;
                                break;
                            case 2:
                                *(short*)swiftDest = (short)enumValue;
                                break;
                            default:
                                *(int*)swiftDest = enumValue;
                                break;
                        }
                        return swiftDestSpan.Length;
                    }
                }
                if (size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    Unsafe.Write(swiftDest, value);
                    return size;
                }
            }
        }

        throw new NotSupportedException($"Cannot marshal type {type} to Swift");
    }

    /// <summary>
    /// Marshals a primitive value to a Swift destination
    /// </summary>
    /// <typeparam name="T">the type of the primitive</typeparam>
    /// <param name="value">The value to marshal</param>
    /// <param name="swiftDest">where in memory to marshal it</param>
    /// <returns>the resulting pointer for passing to a Swift method.</returns>
    /// <exception cref="NotSupportedException"></exception>
    static unsafe void MarshalPrimitiveToSwift<T>(T value, void* swiftDest)
    {
        if (value is bool boolValue)
        {
            *((byte*)swiftDest) = (byte)(boolValue ? 1 : 0);
        }
        else if (value is byte byteValue)
        {
            *((byte*)swiftDest) = byteValue;
        }
        else if (value is sbyte sbyteValue)
        {
            *((sbyte*)swiftDest) = sbyteValue;
        }
        else if (value is short shortValue)
        {
            *((short*)swiftDest) = shortValue;
        }
        else if (value is ushort ushortValue)
        {
            *((ushort*)swiftDest) = ushortValue;
        }
        else if (value is int intValue)
        {
            *((int*)swiftDest) = intValue;
        }
        else if (value is uint uintValue)
        {
            *((uint*)swiftDest) = uintValue;
        }
        else if (value is long longValue)
        {
            *((long*)swiftDest) = longValue;
        }
        else if (value is ulong ulongValue)
        {
            *((ulong*)swiftDest) = ulongValue;
        }
        else if (value is float floatValue)
        {
            *((float*)swiftDest) = floatValue;
        }
        else if (value is double doubleValue)
        {
            *((double*)swiftDest) = doubleValue;
        }
        else if (value is nint nintValue)
        {
            *((nint*)swiftDest) = nintValue;
        }
        else if (value is nuint nuintValue)
        {
            *((nuint*)swiftDest) = nuintValue;
        }
        else
        {
            throw new NotSupportedException($"Cannot marshal type {typeof(T)} to Swift");
        }
    }

    /// <summary>
    /// Marshals an ISwiftObject value from a Swift source.
    /// NativeAOT-safe: uses direct static virtual dispatch instead of reflection.
    /// Generated bindings should prefer this overload when T is known to implement ISwiftObject.
    /// </summary>
    /// <typeparam name="T">The ISwiftObject type</typeparam>
    /// <param name="swiftSource">Memory to read from</param>
    /// <returns>The C# object created by marshaling</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2087",
        Justification = "typeof(T) satisfies DynamicallyAccessedMembers at runtime; types preserved via TrimmerRoots.xml")]
    public static T MarshalFromSwiftObject<T>(IntPtr swiftSource) where T : ISwiftObject
    {
        if (SwiftRuntimeInfo.IsNativeAotRuntime)
        {
            // Register factory so unconstrained callers (MarshalFromSwift<T>) can use it later.
            NewFromPayloadDispatcher.Register(typeof(T), handle => (object)T.NewFromPayload(handle));
            return (T)DirectNewFromPayload<T>(swiftSource);
        }
        return (T)SwiftObjectReflectionHelper.InvokeNewFromPayload(typeof(T), swiftSource);
    }

    /// <summary>
    /// Direct static virtual dispatch for NewFromPayload — NativeAOT only.
    /// Separate method so Mono JIT never compiles this.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ISwiftObject DirectNewFromPayload<T>(IntPtr swiftSource) where T : ISwiftObject
    {
        return T.NewFromPayload(swiftSource);
    }

    /// <summary>
    /// Marshals a value from a Swift source.
    /// </summary>
    /// <typeparam name="T">The type of the expected value</typeparam>
    /// <param name="swiftSource">Memory to read from</param>
    /// <returns>The C# type created by marshaling</returns>
    /// <exception cref="NotSupportedException"></exception>
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Tuple marshalling path only; non-tuple paths are AOT-safe")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Tuple marshalling path only; non-tuple paths are trim-safe")]
    [UnconditionalSuppressMessage("Trimming", "IL2091", Justification = "Tuple marshalling path only; non-tuple paths are trim-safe")]
    [UnconditionalSuppressMessage("Trimming", "IL2087",
        Justification = "typeof(T) satisfies DynamicallyAccessedMembers at runtime; types preserved via TrimmerRoots.xml")]
    [UnconditionalSuppressMessage("Trimming", "IL2059",
        Justification = "RunClassConstructor is a NativeAOT fallback in try-catch; type is always an ISwiftObject whose static constructor is preserved")]
    public static T MarshalFromSwift<T>(IntPtr swiftSource)
    {
        if (typeof(ISwiftObject).IsAssignableFrom(typeof(T)))
        {
            // Try factory cache first (populated by constrained code paths on NativeAOT).
            // This avoids reflection entirely for types that have been accessed through
            // SwiftObjectHelper<T> or MarshalFromSwiftObject<T>.
            var cached = NewFromPayloadDispatcher.TryCreate(typeof(T), swiftSource);
            if (cached != null)
                return (T)cached;

            // NativeAOT fallback: trigger type initialization to populate factory cache.
            // Reflection on explicit interface implementations of generic types may fail
            // on NativeAOT. RunClassConstructor triggers static init which calls
            // SwiftObjectHelper<T>.GetTypeMetadata() → DirectDispatchGetTypeMetadata(),
            // registering the NewFromPayload factory.
            if (SwiftRuntimeInfo.IsNativeAotRuntime)
            {
                try
                {
                    RuntimeHelpers.RunClassConstructor(typeof(T).TypeHandle);
                    cached = NewFromPayloadDispatcher.TryCreate(typeof(T), swiftSource);
                    if (cached != null)
                        return (T)cached;
                }
                catch { }
            }

            // Fallback: reflection. Works on Mono JIT always; works on NativeAOT only
            // for types preserved via TrimmerRoots.xml (Swift.Runtime types).
            return (T)SwiftObjectReflectionHelper.InvokeNewFromPayload(typeof(T), swiftSource);
        }
        var type = typeof(T);
        if (type.IsPrimitive)
        {
            unsafe
            {
                return MarshalPrimitiveFromSwift<T>(swiftSource);
            }
        }

        // Handle tuple types (ValueTuple<T1, T2, ...>)
        // Note: Tuple marshalling uses reflection internally, but this is intentional
        // for the generic runtime path. Generated bindings use inline code instead.
        if (TypeMetadata.IsValueTupleType(type))
        {
            return MarshalTupleFromSwift<T>(swiftSource);
        }

        // Handle existential container types (blittable structs with fixed layout)
        if (typeof(IExistentialContainer).IsAssignableFrom(type))
        {
            unsafe { return Unsafe.Read<T>((void*)swiftSource); }
        }

        // Handle delegate types (closures) - Phase 3 support
        if (typeof(Delegate).IsAssignableFrom(typeof(T)))
        {
            // Read the Swift closure data (function pointer + context)
            unsafe
            {
                var closureData = *(SwiftClosureData*)swiftSource;

                // Receiving Swift closures as C# delegates requires generated invoker code
                // because we need to know the exact signature to call the Swift function
                // with the proper calling convention (context in register).
                // The generated bindings should create SwiftEscapingClosure<TDelegate> wrappers
                // with proper invoker delegates.
                throw new NotSupportedException(
                    $"Receiving Swift closures as C# delegates requires generated invoker code. " +
                    $"The closure data is at address 0x{swiftSource:X}, " +
                    $"function pointer: 0x{closureData.FunctionPointer:X}, " +
                    $"context: 0x{closureData.Context:X}. Type: {typeof(T)}");
            }
        }

        // Existential containers cannot be directly marshalled from Swift to C# as a generic delegate
        // because the concrete type is not known at compile time. The generated bindings should
        // handle existential types with explicit container types.

#if IOS || TVOS || MACCATALYST || MACOS
        // Defense-in-depth: if T is an NSObject subclass (ObjC-bridged type like UIImage, NSImage),
        // read the object pointer from the Swift memory and wrap with GetNSObject<T>.
        // The generated bindings should emit GetNSObject directly, but this catches edge cases
        // where the TypeDatabase didn't recognize the type as ObjC-bridged.
        if (typeof(Foundation.NSObject).IsAssignableFrom(type))
        {
            var objPtr = Marshal.ReadIntPtr(swiftSource);
            return (T)(object)ObjCRuntime.Runtime.GetNSObject(objPtr)!;
        }
#endif

        // Simple enum fast path: Swift simple enums use the minimum bytes for the
        // discriminator (1 for ≤256 cases, 2 for ≤65536, etc.), but C# enum : int is
        // always 4 bytes. Read only the Swift-sized bytes to avoid overreading.
        // With metadata: use exact Size. Without metadata: default to 1 byte (covers
        // enums with ≤256 cases; enums with 256+ cases require metadata registration).
        if (type.IsEnum && !typeof(ISwiftObject).IsAssignableFrom(type) &&
            !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            int csharpSize = Unsafe.SizeOf<T>();
            int swiftSize = TypeMetadata.TryGetTypeMetadata<T>(out var enumMeta)
                ? (int)enumMeta.Value.Size
                : 1; // Default to 1 byte for unregistered simple enums (≤256 cases)
            if (swiftSize < csharpSize)
            {
                unsafe
                {
                    int caseValue = swiftSize switch
                    {
                        1 => ((byte*)swiftSource)[0],
                        2 => *(short*)swiftSource,
                        _ => *(int*)swiftSource,
                    };
                    return (T)Enum.ToObject(typeof(T), caseValue);
                }
            }
            // If Swift size >= C# size, fall through to the blittable path below
        }

        // Blittable value types: frozen structs (CGPoint, CGRect, CGSize).
        // Read directly from native memory. Complex enums implement ISwiftObject
        // and are handled above. Simple enums are handled above.
        // Gate: must be unmanaged (no managed references) to avoid invalid managed pointers.
        if (type.IsValueType && RuntimeHelpers.IsReferenceOrContainsReferences<T>() == false)
        {
            unsafe { return Unsafe.Read<T>((void*)swiftSource); }
        }

        throw new NotSupportedException($"Cannot marshal type {type} from Swift");
    }

    /// <summary>
    /// Reads a Swift Optional value from a raw memory pointer and returns as C# nullable.
    /// The pointer must point to a Swift Optional&lt;T&gt; layout (value bytes + tag byte).
    /// Used by generated closure callbacks that receive heap-allocated Optional values.
    /// Uses direct memory reads to avoid SwiftOptional metadata resolution, which crashes
    /// in Mono JIT UnmanagedCallersOnly context.
    /// </summary>
    /// <typeparam name="T">The value type (primitive or enum) wrapped in Optional.</typeparam>
    /// <param name="ptr">Pointer to the heap-allocated Swift Optional&lt;T&gt; memory.</param>
    /// <returns>The value as C# nullable, or null if the Optional is .none.</returns>
    public static unsafe T? MarshalOptionalFromSwift<T>(IntPtr ptr) where T : struct
    {
        // Swift Optional<T> layout depends on the type:
        // - Primitives (Int32, Int64, Double, etc.): [value bytes] [1 byte tag], tag 0=Some, 1=None
        // - Bool: extra inhabitant encoding — 1 byte total, value > 1 means None
        // - Simple enums: may use extra inhabitants depending on the number of cases

        if (typeof(T) == typeof(bool))
        {
            // Optional<Bool> uses extra inhabitant: 0=false, 1=true, 2=None
            byte rawByte = *(byte*)ptr;
            if (rawByte > 1)
                return null;
            return (T)(object)(rawByte == 1);
        }

        // For primitives (Int32, Double, etc.): tag byte is appended after the value
        int tagOffset = GetPrimitiveTagOffset<T>();
        if (tagOffset > 0)
        {
            byte tag = *((byte*)ptr + tagOffset);
            if (tag != 0)
                return null;
            return Unsafe.ReadUnaligned<T>(ref *(byte*)ptr);
        }

        // For enums and other types: use SwiftOptional metadata path
        // This may not work in all Mono JIT contexts — skip those tests if needed
        using var opt = MarshalFromSwift<SwiftOptional<T>>(ptr);
        return opt.Case == SwiftOptionalCases.Some ? opt.Some : null;
    }

    /// <summary>
    /// Returns the tag byte offset for known blittable primitive types in Swift Optional layout.
    /// Returns -1 for unknown types (enums, structs, etc.).
    /// </summary>
    private static int GetPrimitiveTagOffset<T>() where T : struct
    {
        if (typeof(T) == typeof(byte) || typeof(T) == typeof(sbyte))
            return 1;
        if (typeof(T) == typeof(short) || typeof(T) == typeof(ushort))
            return 2;
        if (typeof(T) == typeof(int) || typeof(T) == typeof(uint) || typeof(T) == typeof(float))
            return 4;
        if (typeof(T) == typeof(long) || typeof(T) == typeof(ulong) || typeof(T) == typeof(double))
            return 8;
        if (typeof(T) == typeof(nint) || typeof(T) == typeof(nuint))
            return IntPtr.Size;
        return -1;
    }

    /// <summary>
    /// Marshals a primitive value from a Swift source
    /// </summary>
    /// <typeparam name="T">The type of the value to marshal</typeparam>
    /// <param name="swiftSource">Memory to read from</param>
    /// <returns>The marshaled type</returns>
    /// <exception cref="NotSupportedException"></exception>
    public static unsafe T MarshalPrimitiveFromSwift<T>(IntPtr swiftSource)
    {
        if (typeof(T) == typeof(bool))
        {
            return (T)(object)(((*(byte*)swiftSource) & 1) != 0);
        }
        else if (typeof(T) == typeof(byte))
        {
            return (T)(object)(*(byte*)swiftSource);
        }
        else if (typeof(T) == typeof(sbyte))
        {
            return (T)(object)(*(sbyte*)swiftSource);
        }
        else if (typeof(T) == typeof(short))
        {
            return (T)(object)(*(short*)swiftSource);
        }
        else if (typeof(T) == typeof(ushort))
        {
            return (T)(object)(*(ushort*)swiftSource);
        }
        else if (typeof(T) == typeof(int))
        {
            return (T)(object)(*(int*)swiftSource);
        }
        else if (typeof(T) == typeof(uint))
        {
            return (T)(object)(*(uint*)swiftSource);
        }
        else if (typeof(T) == typeof(long))
        {
            return (T)(object)(*(long*)swiftSource);
        }
        else if (typeof(T) == typeof(ulong))
        {
            return (T)(object)(*(ulong*)swiftSource);
        }
        else if (typeof(T) == typeof(float))
        {
            return (T)(object)(*(float*)swiftSource);
        }
        else if (typeof(T) == typeof(double))
        {
            return (T)(object)(*(double*)swiftSource);
        }
        else if (typeof(T) == typeof(nint))
        {
            return (T)(object)(*(nint*)swiftSource);
        }
        else if (typeof(T) == typeof(nuint))
        {
            return (T)(object)(*(nuint*)swiftSource);
        }
        else
        {
            throw new NotSupportedException($"Cannot marshal type {typeof(T)} from Swift");
        }
    }

    /// <summary>
    /// Marshals a C# ValueTuple to Swift memory.
    /// Uses direct unsafe memory access for primitive types to avoid reflection overhead.
    /// </summary>
    /// <typeparam name="T">The ValueTuple type.</typeparam>
    /// <param name="value">The tuple value.</param>
    /// <param name="tupleType">The tuple type.</param>
    /// <param name="swiftDestSpan">The destination span.</param>
    /// <returns>The number of bytes written.</returns>
    [RequiresDynamicCode("Tuple marshalling uses reflection for non-primitive element types")]
    [RequiresUnreferencedCode("Tuple marshalling requires access to ValueTuple fields")]
    private static unsafe int MarshalTupleToSwift<T>(T value, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] Type tupleType, ref Span<byte> swiftDestSpan)
    {
        var elementTypes = TypeMetadata.GetTupleElementTypes(tupleType);
        var elementCount = elementTypes.Length;

        // Get tuple metadata to determine layout
        if (!TypeMetadata.TryGetTypeMetadata<T>(out var tupleMetadata))
            throw new NotSupportedException($"Cannot get tuple metadata for {tupleType.Name}");

        var tupleSize = (int)tupleMetadata.Value.Size;
        if (tupleSize > swiftDestSpan.Length)
            throw new ArgumentException($"Span size does not match tuple size, Expected: {tupleSize}, Actual: {swiftDestSpan.Length}");

        // Get field values using ValueTuple's Item1, Item2, etc. fields
        var fields = GetTupleFields(tupleType);

        fixed (byte* destPtr = swiftDestSpan)
        {
            // Calculate offsets and marshal each element
            int currentOffset = 0;
            for (int i = 0; i < elementCount; i++)
            {
                var elementType = elementTypes[i];
                var elementValue = fields[i].GetValue(value);

                // Get element metadata to determine alignment
                var elementMetadata = GetTypeMetadataForType(elementType);
                var elementAlignment = elementMetadata.Alignment;
                var elementSize = (int)elementMetadata.Size;

                // Align the offset
                currentOffset = AlignOffset(currentOffset, elementAlignment);

                // Marshal the element directly using unsafe pointers
                MarshalElementToSwiftUnsafe(elementValue, elementType, destPtr + currentOffset);

                currentOffset += elementSize;
            }
        }

        return tupleSize;
    }

    /// <summary>
    /// Marshals a Swift tuple to a C# ValueTuple.
    /// Uses direct unsafe memory access for primitive types to avoid reflection overhead.
    /// </summary>
    /// <typeparam name="T">The ValueTuple type.</typeparam>
    /// <param name="swiftSource">The Swift memory source.</param>
    /// <returns>The marshalled ValueTuple.</returns>
    [RequiresDynamicCode("Tuple marshalling uses reflection for non-primitive element types")]
    [RequiresUnreferencedCode("Tuple marshalling requires access to ValueTuple constructors")]
    private static unsafe T MarshalTupleFromSwift<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields)] T>(IntPtr swiftSource)
    {
        var tupleType = typeof(T);
        var elementTypes = TypeMetadata.GetTupleElementTypes(tupleType);
        var elementCount = elementTypes.Length;

        // Get element values
        var elementValues = new object?[elementCount];
        int currentOffset = 0;

        for (int i = 0; i < elementCount; i++)
        {
            var elementType = elementTypes[i];

            // Get element metadata to determine alignment and size
            var elementMetadata = GetTypeMetadataForType(elementType);
            var elementAlignment = elementMetadata.Alignment;
            var elementSize = (int)elementMetadata.Size;

            // Align the offset
            currentOffset = AlignOffset(currentOffset, elementAlignment);

            // Marshal the element from Swift
            var elementPtr = IntPtr.Add(swiftSource, currentOffset);
            elementValues[i] = MarshalElementFromSwiftUnsafe(elementPtr, elementType);

            currentOffset += elementSize;
        }

        // Create the ValueTuple using the constructor
        return CreateValueTuple<T>(tupleType, elementValues);
    }

    /// <summary>
    /// Gets the fields of a ValueTuple in order (Item1, Item2, etc.).
    /// </summary>
    [RequiresUnreferencedCode("ValueTuple field access")]
    private static FieldInfo[] GetTupleFields([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] Type tupleType)
    {
        var elementCount = tupleType.GetGenericArguments().Length;
        var fields = new FieldInfo[elementCount];

        for (int i = 0; i < elementCount; i++)
        {
            var fieldName = $"Item{i + 1}";
            fields[i] = tupleType.GetField(fieldName)
                ?? throw new InvalidOperationException($"Could not find field {fieldName} on {tupleType.Name}");
        }

        return fields;
    }

    /// <summary>
    /// Gets TypeMetadata for a runtime Type.
    /// </summary>
    [RequiresDynamicCode("Type metadata lookup uses reflection")]
    private static TypeMetadata GetTypeMetadataForType(Type type)
    {
        // Use reflection to call the generic TryGetTypeMetadata<T>
        var tryGetMethod = typeof(TypeMetadata).GetMethod(nameof(TypeMetadata.TryGetTypeMetadata), BindingFlags.Public | BindingFlags.Static)!;
        var genericMethod = tryGetMethod.MakeGenericMethod(type);

        var args = new object?[] { null };
        var success = (bool)genericMethod.Invoke(null, args)!;

        if (!success)
            throw new NotSupportedException($"Cannot get type metadata for {type.Name}");

        return ((TypeMetadata?)args[0])!.Value;
    }

    /// <summary>
    /// Aligns an offset to the given alignment.
    /// </summary>
    private static int AlignOffset(int offset, int alignment)
    {
        var remainder = offset % alignment;
        return remainder == 0 ? offset : offset + (alignment - remainder);
    }

    /// <summary>
    /// Marshals a single element value to Swift memory using direct pointer access.
    /// </summary>
    [RequiresDynamicCode("Non-primitive element marshalling uses reflection")]
    private static unsafe void MarshalElementToSwiftUnsafe(object? value, Type elementType, byte* dest)
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value), "Tuple element cannot be null");

        // Handle primitives directly without reflection
        if (elementType == typeof(bool))
        {
            *dest = (byte)((bool)value ? 1 : 0);
        }
        else if (elementType == typeof(byte))
        {
            *dest = (byte)value;
        }
        else if (elementType == typeof(sbyte))
        {
            *(sbyte*)dest = (sbyte)value;
        }
        else if (elementType == typeof(short))
        {
            *(short*)dest = (short)value;
        }
        else if (elementType == typeof(ushort))
        {
            *(ushort*)dest = (ushort)value;
        }
        else if (elementType == typeof(int))
        {
            *(int*)dest = (int)value;
        }
        else if (elementType == typeof(uint))
        {
            *(uint*)dest = (uint)value;
        }
        else if (elementType == typeof(long))
        {
            *(long*)dest = (long)value;
        }
        else if (elementType == typeof(ulong))
        {
            *(ulong*)dest = (ulong)value;
        }
        else if (elementType == typeof(float))
        {
            *(float*)dest = (float)value;
        }
        else if (elementType == typeof(double))
        {
            *(double*)dest = (double)value;
        }
        else if (elementType == typeof(nint))
        {
            *(nint*)dest = (nint)value;
        }
        else if (elementType == typeof(nuint))
        {
            *(nuint*)dest = (nuint)value;
        }
        else if (typeof(ISwiftObject).IsAssignableFrom(elementType))
        {
            // For ISwiftObject types, use MarshalToSwift through the interface
            var swiftObject = (ISwiftObject)value;
            var metadata = GetTypeMetadataForType(elementType);
            var span = new Span<byte>(dest, (int)metadata.Size);
            swiftObject.MarshalToSwift(ref span);
        }
        else
        {
            throw new NotSupportedException($"Cannot marshal tuple element type {elementType.Name} to Swift");
        }
    }

    /// <summary>
    /// Marshals a single element from Swift memory using direct pointer access.
    /// </summary>
    [RequiresDynamicCode("Non-primitive element marshalling uses reflection")]
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "elementType comes from ValueTuple generic args which are preserved for tuple marshalling")]
    private static unsafe object? MarshalElementFromSwiftUnsafe(IntPtr source, Type elementType)
    {
        // Handle primitives directly without reflection
        if (elementType == typeof(bool))
        {
            return ((*(byte*)source) & 1) != 0;
        }
        else if (elementType == typeof(byte))
        {
            return *(byte*)source;
        }
        else if (elementType == typeof(sbyte))
        {
            return *(sbyte*)source;
        }
        else if (elementType == typeof(short))
        {
            return *(short*)source;
        }
        else if (elementType == typeof(ushort))
        {
            return *(ushort*)source;
        }
        else if (elementType == typeof(int))
        {
            return *(int*)source;
        }
        else if (elementType == typeof(uint))
        {
            return *(uint*)source;
        }
        else if (elementType == typeof(long))
        {
            return *(long*)source;
        }
        else if (elementType == typeof(ulong))
        {
            return *(ulong*)source;
        }
        else if (elementType == typeof(float))
        {
            return *(float*)source;
        }
        else if (elementType == typeof(double))
        {
            return *(double*)source;
        }
        else if (elementType == typeof(nint))
        {
            return *(nint*)source;
        }
        else if (elementType == typeof(nuint))
        {
            return *(nuint*)source;
        }
        else if (typeof(ISwiftObject).IsAssignableFrom(elementType))
        {
            // Try factory cache first (NativeAOT-safe, no reflection).
            var cached = NewFromPayloadDispatcher.TryCreate(elementType, source);
            if (cached != null)
                return cached;

            // Fallback: reflection (works on Mono; NativeAOT only for preserved types).
            return SwiftObjectReflectionHelper.InvokeNewFromPayload(elementType, source);
        }
        else
        {
            throw new NotSupportedException($"Cannot marshal tuple element type {elementType.Name} from Swift");
        }
    }

    /// <summary>
    /// Creates a ValueTuple from an array of element values.
    /// </summary>
    [RequiresUnreferencedCode("ValueTuple constructor access")]
    private static T CreateValueTuple<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(Type tupleType, object?[] values)
    {
        // Use the ValueTuple constructor directly
        var constructor = tupleType.GetConstructor(tupleType.GetGenericArguments())
            ?? throw new InvalidOperationException($"Could not find constructor for {tupleType.Name}");

        return (T)constructor.Invoke(values);
    }

    // NOTE: These helpers free Swift-allocated buffers with NativeMemory.Free (C free()).
    // Swift's UnsafeMutablePointer.allocate() uses swift_slowAlloc → malloc on Apple platforms,
    // so free() is the correct deallocator. Generated per-library code historically used
    // SBW_Free (which calls ptr.deallocate() → swift_slowDealloc → free()), but the shared
    // runtime can't reference a per-library P/Invoke. Both paths resolve to free().
    // This assumption holds for all supported targets (iOS/macOS ARM64).

    /// <summary>
    /// Reads a UTF-8 string from a Swift Utf8Slice stored at the given result pointer.
    /// The Utf8Slice's buffer is freed after reading. This replaces the inline 9-line
    /// decode-and-free pattern in generated bindings.
    /// </summary>
    /// <param name="resultPtr">Pointer to a Utf8Slice struct in native memory.</param>
    /// <returns>The decoded string, or <see cref="string.Empty"/> if the slice is empty.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static unsafe string ReadUtf8Slice(IntPtr resultPtr)
    {
        var slice = *(Utf8Slice*)resultPtr;
        if (slice.Len == 0) return string.Empty;
        try
        {
            return Marshal.PtrToStringUTF8(slice.Ptr, (int)slice.Len) ?? string.Empty;
        }
        finally
        {
            NativeMemory.Free((void*)slice.Ptr);
        }
    }

    /// <summary>
    /// Reads a UTF-8 string from a Utf8Slice struct value. The slice's buffer is freed after
    /// reading. This overload is for property getters where the accessor returns a Utf8Slice
    /// by value (not via result pointer).
    /// </summary>
    /// <param name="slice">The Utf8Slice containing a pointer to UTF-8 bytes and their length.</param>
    /// <returns>The decoded string, or <see cref="string.Empty"/> if the slice is empty.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static unsafe string ReadUtf8Slice(Utf8Slice slice)
    {
        if (slice.Len == 0) return string.Empty;
        try
        {
            return Marshal.PtrToStringUTF8(slice.Ptr, (int)slice.Len) ?? string.Empty;
        }
        finally
        {
            NativeMemory.Free((void*)slice.Ptr);
        }
    }

    /// <summary>
    /// Reads a Swift error description from a C string pointer and frees it.
    /// Returns "Unknown Swift error" if the pointer is null or the string is null.
    /// This replaces the inline error description extraction pattern in generated bindings.
    /// </summary>
    /// <param name="descPtr">Pointer to a null-terminated UTF-8 error description string,
    /// allocated by Swift (via SBW_GetErrorDescription). Freed after reading.</param>
    /// <returns>The error description string.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static unsafe string ReadErrorDescription(IntPtr descPtr)
    {
        if (descPtr == IntPtr.Zero)
            return "Unknown Swift error";
        try
        {
            return Marshal.PtrToStringUTF8(descPtr) ?? "Unknown Swift error";
        }
        finally
        {
            NativeMemory.Free((void*)descPtr);
        }
    }

    /// <summary>
    /// Handles an untyped Swift error by extracting the description message, releasing the error,
    /// and throwing a <see cref="SwiftException"/>. Used by generated bindings to replace inline
    /// error handling blocks.
    /// </summary>
    /// <param name="errorPtr">The Swift error pointer (from SwiftError.Value or @_cdecl out parameter).</param>
    /// <param name="descPtr">The error description pointer (from SBW_GetErrorDescription).</param>
    /// <param name="releaseError">Action to release the Swift error reference (SBW_ReleaseError).</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void ThrowSwiftError(IntPtr errorPtr, IntPtr descPtr, Action<IntPtr> releaseError)
    {
        try
        {
            var message = ReadErrorDescription(descPtr);
            throw new SwiftException(message);
        }
        finally
        {
            releaseError(errorPtr);
        }
    }
}
