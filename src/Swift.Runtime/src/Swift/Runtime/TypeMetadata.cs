// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Swift.Runtime;

/// <summary>
/// Flags used to describe types
/// </summary>
[Flags]
public enum TypeMetadataFlags
{
    None = 0,
    /// <summary>
    /// The metadata is not an actual type
    /// </summary>
    IsNonType = 0x400,
    /// <summary>
    /// The metadata doesn't live on the heap
    /// </summary>
    IsNonHeap = 0x200,
    /// <summary>
    /// The type is private to the runtime
    /// </summary>
    IsRuntimePrivate = 0x100,
}

/// <summary>
/// The type represented by the metadata
/// </summary>
public enum TypeMetadataKind
{
    /// <summary>
    /// None - errror
    /// </summary>
    None = 0,
    /// <summary>
    /// The metadata represents a struct
    /// </summary>
    Struct = 0 | TypeMetadataFlags.IsNonHeap,
    /// <summary>
    /// The metadata represents an enum
    /// </summary>
    Enum = 1 | TypeMetadataFlags.IsNonHeap,
    /// <summary>
    /// The metadata represents an optional type
    /// </summary>
    Optional = 2 | TypeMetadataFlags.IsNonHeap,
    /// <summary>
    /// The metadata represents an non-swift class
    /// </summary>
    ForeignClass = 3 | TypeMetadataFlags.IsNonHeap,
    /// <summary>
    /// The metadata represents a foreign reference type
    /// </summary>
    ForeignReferenceType = 4 | TypeMetadataFlags.IsNonHeap,
    /// <summary>
    /// The metadata represents an opaque type
    /// </summary>
    Opaque = 0 | TypeMetadataFlags.IsRuntimePrivate | TypeMetadataFlags.IsNonHeap,
    /// <summary>
    /// The metadata represents a tuple
    /// </summary>
    Tuple = 1 | TypeMetadataFlags.IsRuntimePrivate | TypeMetadataFlags.IsNonHeap,
    /// <summary>
    /// The metadata represents a closure/function
    /// </summary>
    Function = 2 | TypeMetadataFlags.IsRuntimePrivate | TypeMetadataFlags.IsNonHeap,
    /// <summary>
    /// The metadata represents a protocol
    /// </summary>
    Existential = 3 | TypeMetadataFlags.IsRuntimePrivate | TypeMetadataFlags.IsNonHeap,
    /// <summary>
    /// The metadata represents a type of a TypeMetadata type
    /// </summary>
    Metatype = 4 | TypeMetadataFlags.IsRuntimePrivate | TypeMetadataFlags.IsNonHeap,
    /// <summary>
    /// The metadata represents an Objective C wrapper
    /// </summary>
    ObjCClassWrapper = 5 | TypeMetadataFlags.IsRuntimePrivate | TypeMetadataFlags.IsNonHeap,
    /// <summary>
    /// The metadata represents a type of an existential container
    /// </summary>
    ExistentialMetatype = 6 | TypeMetadataFlags.IsRuntimePrivate | TypeMetadataFlags.IsNonHeap,
    /// <summary>
    /// The metadata represents an extended existential type
    /// </summary>
    ExtendedExistential = 7 | TypeMetadataFlags.IsRuntimePrivate | TypeMetadataFlags.IsNonHeap,
    /// <summary>
    /// The metadata represents the type Builtin.FixedArray
    /// </summary>
    FixedArray = 8 | TypeMetadataFlags.IsRuntimePrivate | TypeMetadataFlags.IsNonHeap,
    /// <summary>
    /// The metadata represents a heap local variable
    /// </summary>
    HeapLocalVariable = 0 | TypeMetadataFlags.IsNonType,
    /// <summary>
    /// The metadata represents a generic heap local variable
    /// </summary>
    HeapGenericLocalVariable = 0 | TypeMetadataFlags.IsNonType | TypeMetadataFlags.IsRuntimePrivate,
    /// <summary>
    /// The metadata represents an error
    /// </summary>
    ErrorObject = 1 | TypeMetadataFlags.IsNonType | TypeMetadataFlags.IsRuntimePrivate,
    /// <summary>
    /// The metadata represents a heap-allocated task
    /// </summary>
    Task = 2 | TypeMetadataFlags.IsNonType | TypeMetadataFlags.IsRuntimePrivate,
    /// <summary>
    /// The metadata represents a non-task async job
    /// </summary>
    Job = 3 | TypeMetadataFlags.IsNonType | TypeMetadataFlags.IsRuntimePrivate,
    // Swift source code says that for fixed values, this will never exceed 0x7ff,
    // but all class types will be 0x800 and above
    /// <summary>
    /// The metadata represents a class
    /// </summary>
    Class = 0x800
}

/// <summary>
/// Represents the possible values for a TypeMetadataRequest
/// </summary>
[Flags]
public enum TypeMetadataRequest
{
    Complete = 0,
    NonTransitiveComplete = 1,
    LayoutComplete = 0x3f,
    Abstract = 0xff,
    IsNotBlocking = 0x100,
}


/// <summary>
/// Represents the type metadata for a Swift type
/// </summary>
public readonly struct TypeMetadata : IEquatable<TypeMetadata>
{
    private readonly IntPtr handle;
    public IntPtr Handle => handle;

    static TypeMetadata()
    {
        cache = new TypeMetadataCache(KnownMetadata());
    }

    /// <summary>
    /// An empty/invalid TypeMetadata object
    /// </summary>
    public readonly static TypeMetadata Zero = default(TypeMetadata);

    /// <summary>
    /// Construct a TypeMetadata object
    /// </summary>
    /// <param name="handle">The handle for the type</param>
    TypeMetadata(IntPtr handle)
    {
        this.handle = handle;
    }

    /// <summary>
    /// Creates a TypeMetadata from a raw handle pointer.
    /// Used by generated bindings that obtain metadata via @_cdecl wrappers returning IntPtr.
    /// </summary>
    public static TypeMetadata FromHandle(IntPtr handle) => new TypeMetadata(handle);

    /// <summary>
    /// Returns true if and only if the TypeMetadata is valid.
    /// </summary>
    public bool IsValid => handle != IntPtr.Zero;

    /// <summary>
    /// Throws a SwiftRuntimeException if the TypeMetadata is invalid
    /// </summary>
    /// <exception cref="SwiftRuntimeException"></exception>
    void ThrowOnInvalid()
    {
        if (!IsValid)
            throw new SwiftRuntimeException("TypeMetadata is invalid.");
    }

    // This comes from the Swift ABI documentation - https://github.com/swiftlang/swift/blob/23e3f5f5de2ed046f3183264589be1f9a54f7e1e/include/swift/ABI/MetadataValues.h#L117
    const long kMaxDiscriminator = 0x7ff;

    /// <summary>
    /// Returns the kind of this TypeMetadata
    /// </summary>
    public TypeMetadataKind Kind
    {
        get
        {
            ThrowOnInvalid();
            long val = ReadPointerSizedInt(handle);
            if (val == 0)
                return TypeMetadataKind.None;
            if (val > kMaxDiscriminator)
                return TypeMetadataKind.Class;
            return (TypeMetadataKind)val;
        }
    }

    /// <summary>
    /// Returns a pointer to the value witness table for the given type
    /// </summary>
    public unsafe ValueWitnessTable* ValueWitnessTable => IsValid ? (ValueWitnessTable*)(*((IntPtr*)handle - 1)) : throw new NullReferenceException("TypeMetadata is null");

    /// <summary>
    /// Returns the size of the Swift type in bytes
    /// </summary>
    public unsafe nuint Size => this.ValueWitnessTable->Size;

    /// <summary>
    /// Returns the stride of the Swift type in bytes
    /// </summary>
    public unsafe nuint Stride => this.ValueWitnessTable->Stride;

    /// <summary>
    /// Returns the alignment of the Swift type
    /// </summary>
    public unsafe int Alignment => this.ValueWitnessTable->Alignment;

    /// <summary>
    /// Reads a pointer sized integer from the location supplied
    /// </summary>
    /// <param name="p">a pointer to memory</param>
    /// <returns></returns>
    unsafe static nint ReadPointerSizedInt(IntPtr p)
    {
        // Check for debug only. This calling code should always do the null
        // checking.
#if DEBUG
        if (p == IntPtr.Zero)
            throw new ArgumentOutOfRangeException(nameof(p));
#endif
        return *((nint*)p);
    }

    /// <summary>
    /// Returns true if other is the same as this
    /// </summary>
    /// <param name="other">a TypeMetadata object to compare</param>
    /// <returns>true if the other is the same, false otherwise</returns>
    public bool Equals(TypeMetadata other)
    {
        return other.handle == handle;
    }

    /// <summary>
    /// Returns true if and only if o is a TypeMetadata object and is equal to this
    /// </summary>
    /// <param name="o">an object to compare</param>
    /// <returns>true if the other is the same, false otherwise</returns>
    public override bool Equals(object? o)
    {
        if (o is TypeMetadata tm)
            return tm.handle == this.handle;
        return false;
    }

    /// <summary>
    /// Returns a hashcode for this TypeMetadata object
    /// </summary>
    /// <returns>A hashcode for this TypeMetadata object</returns>
    public override int GetHashCode()
    {
        return handle.GetHashCode();
    }

    static readonly TypeMetadataCache cache;
    /// <summary>
    /// Gets the type metadata cache for the runtime.
    /// </summary>
    public static ITypeMetadataCache Cache => cache;

    /// <summary>
    /// Attempt to get the Swift type metadata for the given object instance
    /// </summary>
    /// <typeparam name="T">The type of the object</typeparam>
    /// <param name="result">The result of looked up type metadata</param>
    /// <returns>true on success false otherwise</returns>
    public static bool TryGetTypeMetadata<T>([NotNullWhen(true)] out TypeMetadata? result)
    {
        if (cache.TryGet(typeof(T), out result))
            return true;
        return TryGetTypeMetadataUncached<T>(out result);
    }

    /// <summary>
    /// Attempt to get the Swift type metadata for the given type
    /// </summary>
    /// <typeparam name="T">The type of the object</typeparam>
    /// <returns>The result of the looked up type metadata on success</returns>
    /// <exception cref="SwiftRuntimeException">Throws when lookup fails</exception>
    public static TypeMetadata GetTypeMetadataOrThrow<T>()
    {
        if (TryGetTypeMetadata<T>(out var result))
            return result.Value;
        throw new SwiftRuntimeException($"Unable to get type metadata for type {typeof(T).Name}");
    }

    /// <summary>
    /// Registers Swift type metadata for a C# type in the metadata cache.
    /// Used by generated module initializers to register metadata for simple enum types
    /// that cannot implement ISwiftObject (C# enum limitation). Without registration,
    /// SwiftOptional&lt;T&gt; would get the wrong Optional layout (tag-byte vs extra-inhabitant).
    /// </summary>
    /// <param name="type">The C# type to register metadata for.</param>
    /// <param name="metadata">The Swift type metadata obtained via P/Invoke.</param>
    public static void RegisterMetadata(Type type, TypeMetadata metadata)
    {
        if (metadata.IsValid)
            cache.GetOrAdd(type, _ => metadata);
    }

    /// <summary>
    /// Attempt to get the Swift type metadata but without accessing the cache
    /// </summary>
    /// <typeparam name="T">The type of the object</typeparam>
    /// <param name="result">The result of looked up type metadata</param>
    /// <returns>true on success false otherwise</returns>
    /// <exception cref="NotImplementedException">Throws if unable to look up the ISwiftObject.GetTypeMetadata method.</exception>
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Tuple metadata path only; non-tuple paths are AOT-safe")]
    [UnconditionalSuppressMessage("Trimming", "IL2087",
        Justification = "typeof(T) satisfies DynamicallyAccessedMembers at runtime; types preserved via TrimmerRoots.xml")]
    [UnconditionalSuppressMessage("Trimming", "IL2059",
        Justification = "RunClassConstructor is a NativeAOT fallback in try-catch; type is always an ISwiftObject whose static constructor is preserved")]
    static bool TryGetTypeMetadataUncached<T>([NotNullWhen(true)] out TypeMetadata? result)
    {
        var type = typeof(T);
        if (typeof(ISwiftObject).IsAssignableFrom(type))
        {
            // Invoke GetTypeMetadata directly on the concrete type via reflection,
            // bypassing SwiftObjectHelper<T> generic instantiation. Static virtual
            // dispatch in generic contexts crashes Mono JIT (jit-info.c:918).
            // On NativeAOT, reflection works because methods are preserved via
            // DynamicallyAccessedMembers annotations on InvokeGetTypeMetadata.
            var candidate = SwiftObjectReflectionHelper.InvokeGetTypeMetadata(type);

            // GetTypeMetadata can return an IntPtr.Zero
            if (candidate.IsValid)
            {
                result = candidate;
                return true;
            }

            // NativeAOT fallback: reflection may fail for explicit interface implementations
            // on generic type instantiations (e.g., SwiftOptional<SwiftString>). Trigger
            // type initialization, which runs static field initializers that call
            // SwiftObjectHelper<T>.GetTypeMetadata() → DirectDispatchGetTypeMetadata(),
            // populating both the metadata cache and NewFromPayload factory.
            if (SwiftRuntimeInfo.IsNativeAotRuntime)
            {
                try
                {
                    RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                    if (cache.TryGet(type, out result))
                        return true;
                }
                catch
                {
                    // Type initialization may fail; fall through to other lookups.
                }
            }
        }

        // NB - all further methods here should finish by putting the type into the cache

        // Handle tuple types (ValueTuple<T1, T2, ...>)
        // Note: Tuple metadata lookup uses reflection internally, but this is intentional
        // for the generic runtime path. Generated bindings use inline code instead.
        if (IsValueTupleType(type))
        {
            if (TryGetTupleTypeMetadata(type, out var tupleMetadata))
            {
                cache.GetOrAdd(type, _ => tupleMetadata);
                result = tupleMetadata;
                return true;
            }
        }

        // Handle closure/delegate types
        // Closures are represented as Function metadata kind in Swift
        // For now, return a basic metadata that indicates this is a closure type
        if (typeof(Delegate).IsAssignableFrom(type))
        {
            // Swift closures have a fixed size of 2 machine words (function pointer + context)
            // We don't have actual Swift metadata for arbitrary C# delegate types,
            // but we can indicate this is a function type
            // Full implementation would require runtime construction of function metadata
            // using swift_getFunctionTypeMetadata
            result = null;
            return false;
        }

        // Handle existential container types (ExistentialContainer0 through ExistentialContainer8)
        // Direct CallConvSwift P/Invoke to swift_getExistentialTypeMetadata is preferred,
        // with fallback to SwiftBindingsRuntime @_cdecl wrapper.
        if (typeof(IExistentialContainer).IsAssignableFrom(type))
        {
            var numProtocols = GetProtocolCountFromExistentialType(type);

            // Try direct CallConvSwift P/Invoke first, then fall back to @_cdecl wrapper
            if (TryGetExistentialTypeMetadataViaWrapper(numProtocols, out var existentialMetadata))
            {
                cache.GetOrAdd(type, _ => existentialMetadata);
                result = existentialMetadata;
                return true;
            }

            // Wrapper unavailable or returned zero.
            throw new SwiftRuntimeException(
                $"Failed to get existential metadata for {type.Name} ({numProtocols} protocol(s)). " +
                "Ensure libSwiftBindingsRuntime.dylib is included in your application bundle.");
        }

        // Handle CoreGraphics struct types (CGPoint, CGRect, CGSize).
        // These are Clang-imported types whose metadata descriptors are local symbols
        // (not exported from any system library). SwiftBindingsRuntime provides @_cdecl
        // wrappers that return the metadata via P/Invoke.
        if (type == typeof(CGPoint) || type == typeof(CGRect) || type == typeof(CGSize))
        {
            if (TryGetCoreGraphicsMetadata(type, out var cgMetadata))
            {
                cache.GetOrAdd(type, _ => cgMetadata.Value);
                result = cgMetadata;
                return true;
            }
        }

        // Simple C# enums: metadata is registered by the generated module initializer
        // via TypeMetadata.RegisterMetadata() + P/Invoke to the @_cdecl metadata wrapper.
        // If the enum was generated with the new pipeline, its metadata is already in the cache
        // (registered during module initialization). No fallback to underlying type metadata —
        // that produces wrong Optional<T> layout (tag-byte vs extra-inhabitant encoding).

        result = null;
        return false;
    }

    /// <summary>
    /// Attempts to get type metadata for CoreGraphics struct types via SwiftBindingsRuntime.
    /// </summary>
    static bool TryGetCoreGraphicsMetadata(Type type, [NotNullWhen(true)] out TypeMetadata? result)
    {
        try
        {
            IntPtr metadataPtr;
            if (type == typeof(CGPoint))
                metadataPtr = CoreGraphicsNativeMethods.CGPoint_GetMetadata();
            else if (type == typeof(CGRect))
                metadataPtr = CoreGraphicsNativeMethods.CGRect_GetMetadata();
            else if (type == typeof(CGSize))
                metadataPtr = CoreGraphicsNativeMethods.CGSize_GetMetadata();
            else
            {
                result = null;
                return false;
            }

            if (metadataPtr != IntPtr.Zero)
            {
                result = FromHandle(metadataPtr);
                return true;
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }

        result = null;
        return false;
    }

    /// <summary>
    /// P/Invoke declarations for CoreGraphics type metadata accessors
    /// in SwiftBindingsRuntime.
    /// </summary>
    static class CoreGraphicsNativeMethods
    {
        private const string LibraryName = "SwiftBindingsRuntime";

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
                   EntryPoint = "SBW_CGPoint_GetMetadata")]
        public static extern IntPtr CGPoint_GetMetadata();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
                   EntryPoint = "SBW_CGRect_GetMetadata")]
        public static extern IntPtr CGRect_GetMetadata();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
                   EntryPoint = "SBW_CGSize_GetMetadata")]
        public static extern IntPtr CGSize_GetMetadata();
    }

    /// <summary>
    /// Gets the protocol count from an existential container type name.
    /// </summary>
    /// <param name="type">The existential container type.</param>
    /// <returns>The number of protocols (0-8).</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "IExistentialContainer types are value types with default constructors")]
    private static int GetProtocolCountFromExistentialType(Type type)
    {
        // ExistentialContainer0 -> 0, ExistentialContainer1 -> 1, etc.
        var name = type.Name;
        const string prefix = "ExistentialContainer";
        if (name.StartsWith(prefix) &&
            int.TryParse(name.AsSpan(prefix.Length), out var count))
            return count;

        // Wrapper types (AnyError, etc.): read Count from default instance
        if (type.IsValueType && typeof(IExistentialContainer).IsAssignableFrom(type))
        {
            var instance = (IExistentialContainer)Activator.CreateInstance(type)!;
            return instance.Count;
        }

        return 0;
    }

    /// <summary>
    /// Gets the type metadata for an existential type with the given number of protocol constraints.
    /// </summary>
    /// <param name="numProtocols">The number of protocols (0 for 'Any').</param>
    /// <returns>The existential type metadata.</returns>
    public static TypeMetadata GetExistentialTypeMetadata(int numProtocols)
    {
        if (TryGetExistentialTypeMetadataViaWrapper(numProtocols, out var result))
            return result;

        throw new SwiftRuntimeException(
            $"Failed to get existential metadata for {numProtocols} protocol(s). " +
            "Ensure libSwiftBindingsRuntime.dylib is included in your application bundle.");
    }

    /// <summary>
    /// Direct CallConvSwift P/Invoke to the Swift runtime's existential metadata function.
    /// Proven safe on both Mono and NativeAOT (NativeAOT investigation, March 2026).
    /// </summary>
    private static class SwiftCoreNativeMethods
    {
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvSwift)])]
        [DllImport("libswiftCore", EntryPoint = "swift_getExistentialTypeMetadata")]
        public static extern IntPtr GetExistentialTypeMetadata(
            nint request, IntPtr superclass, nint numProtocols, IntPtr protocols);
    }

    /// <summary>
    /// P/Invoke declarations for the SwiftBindingsRuntime library (legacy fallback).
    /// </summary>
    private static class RuntimeNativeMethods
    {
        private const string LibraryName = "SwiftBindingsRuntime";

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
                   EntryPoint = "SwiftBindings_GetExistentialTypeMetadata")]
        public static extern IntPtr GetExistentialTypeMetadata(nint numProtocols);
    }

    /// <summary>
    /// Gets existential type metadata. For N=0, prefers the direct CallConvSwift P/Invoke.
    /// For N > 0, uses the SwiftBindingsRuntime @_cdecl wrapper which constructs metadata
    /// via Swift's type system (avoiding the complex ProtocolDescriptorRef format).
    /// </summary>
    private static bool TryGetExistentialTypeMetadataViaWrapper(int numProtocols, out TypeMetadata result)
    {
        result = Zero;

        // For N=0, try the direct CallConvSwift P/Invoke first (no protocol descriptors needed).
        if (numProtocols == 0)
        {
            try
            {
                var handle = SwiftCoreNativeMethods.GetExistentialTypeMetadata(
                    0, IntPtr.Zero, 0, IntPtr.Zero);
                if (handle != IntPtr.Zero)
                {
                    result = new TypeMetadata(handle);
                    return true;
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        // For all N (including N > 0), use the @_cdecl wrapper in SwiftBindingsRuntime.
        // The Swift wrapper uses marker protocols to construct existential metadata
        // with the correct number of witness table slots via Swift's type system.
        try
        {
            var handle = RuntimeNativeMethods.GetExistentialTypeMetadata((nint)numProtocols);
            if (handle != IntPtr.Zero)
            {
                result = new TypeMetadata(handle);
                return true;
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }

        return false;
    }

    /// <summary>
    /// Determines whether the specified type is a ValueTuple type.
    /// </summary>
    /// <param name="type">The type to check.</param>
    /// <returns><c>true</c> if the type is a ValueTuple; otherwise, <c>false</c>.</returns>
    public static bool IsValueTupleType(Type type)
    {
        if (!type.IsGenericType)
            return false;

        var genericDef = type.GetGenericTypeDefinition();
        return genericDef == typeof(ValueTuple<>) ||
               genericDef == typeof(ValueTuple<,>) ||
               genericDef == typeof(ValueTuple<,,>) ||
               genericDef == typeof(ValueTuple<,,,>) ||
               genericDef == typeof(ValueTuple<,,,,>) ||
               genericDef == typeof(ValueTuple<,,,,,>) ||
               genericDef == typeof(ValueTuple<,,,,,,>);
    }

    /// <summary>
    /// Gets the element types from a ValueTuple type.
    /// </summary>
    /// <param name="tupleType">The ValueTuple type.</param>
    /// <returns>An array of element types.</returns>
    public static Type[] GetTupleElementTypes(Type tupleType)
    {
        if (!IsValueTupleType(tupleType))
            throw new ArgumentException("Type is not a ValueTuple", nameof(tupleType));

        return tupleType.GetGenericArguments();
    }

    /// <summary>
    /// Attempts to get tuple type metadata for a C# ValueTuple type.
    /// </summary>
    /// <param name="tupleType">The ValueTuple type.</param>
    /// <param name="result">The resulting tuple metadata.</param>
    /// <returns><c>true</c> if metadata was successfully retrieved; otherwise, <c>false</c>.</returns>
    [RequiresDynamicCode("Tuple metadata lookup uses MakeGenericMethod")]
    private static unsafe bool TryGetTupleTypeMetadata(Type tupleType, out TypeMetadata result)
    {
        result = Zero;

        var elementTypes = GetTupleElementTypes(tupleType);
        var elementCount = elementTypes.Length;

        if (elementCount == 0 || elementCount > 7)
            return false;

        // Get metadata for each element type
        var elementMetadata = new TypeMetadata[elementCount];
        for (int i = 0; i < elementCount; i++)
        {
            // Try to get metadata for each element type using reflection to call the generic method
            var tryGetMethod = typeof(TypeMetadata).GetMethod(nameof(TryGetTypeMetadata), BindingFlags.Public | BindingFlags.Static)!;
            var genericMethod = tryGetMethod.MakeGenericMethod(elementTypes[i]);

            var args = new object?[] { null };
            var success = (bool)genericMethod.Invoke(null, args)!;
            if (!success)
                return false;

            elementMetadata[i] = ((TypeMetadata?)args[0])!.Value;
        }

        // Allocate array of element metadata pointers
        var elementsArray = stackalloc IntPtr[elementCount];
        for (int i = 0; i < elementCount; i++)
        {
            elementsArray[i] = elementMetadata[i].Handle;
        }

        // Call Swift runtime to get tuple metadata
        // flags is just the number of elements for basic tuples
        result = swift_getTupleTypeMetadata(
            TypeMetadataRequest.Complete,
            (nuint)elementCount,
            elementsArray,
            IntPtr.Zero, // no labels
            IntPtr.Zero  // let Swift compute the value witness table
        );

        return result.IsValid;
    }

    /// <summary>
    /// Gets the type metadata for a Swift tuple type.
    /// </summary>
    /// <param name="request">The metadata request type.</param>
    /// <param name="flags">Flags encoding the number of elements.</param>
    /// <param name="elements">Pointer to array of element metadata.</param>
    /// <param name="labels">Optional space-separated element labels (can be null).</param>
    /// <param name="proposedWitnesses">Optional proposed value witness table (can be null).</param>
    /// <returns>The tuple type metadata.</returns>
    [DllImport(KnownLibraries.SwiftCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe TypeMetadata swift_getTupleTypeMetadata(
        TypeMetadataRequest request,
        nuint flags,
        IntPtr* elements,
        IntPtr labels,
        IntPtr proposedWitnesses);

    /// <summary>
    /// Gets tuple metadata for a ValueTuple type, throwing on failure.
    /// </summary>
    /// <typeparam name="T">A ValueTuple type.</typeparam>
    /// <returns>The tuple type metadata.</returns>
    /// <exception cref="SwiftRuntimeException">Thrown if tuple metadata cannot be retrieved.</exception>
    public static TypeMetadata GetTupleTypeMetadataOrThrow<T>()
    {
        if (!IsValueTupleType(typeof(T)))
            throw new ArgumentException($"Type {typeof(T).Name} is not a ValueTuple type");

        if (TryGetTypeMetadata<T>(out var result))
            return result.Value;

        throw new SwiftRuntimeException($"Unable to get tuple type metadata for {typeof(T).Name}");
    }

    /// <summary>
    /// Returns an enumeration of known Type and TypeMetadata objects
    /// </summary>
    /// <returns>An enumeration of known Type and TypeMetadata objects</returns>
    /// <exception cref="SwiftRuntimeException">Throws if unable to load a library containing known types</exception>
    static IEnumerable<(Type, TypeMetadata)> KnownMetadata()
    {
        var libraryHandle = NativeLibrary.Load(KnownLibraries.SwiftCore);
        if (libraryHandle == IntPtr.Zero)
            throw new SwiftRuntimeException($"Unable to load library {KnownLibraries.SwiftCore}");
        // types from libSwiftCore
        yield return (typeof(bool), MetadataFromNativeLibrary(libraryHandle, "$sSbN"));
        yield return (typeof(nint), MetadataFromNativeLibrary(libraryHandle, "$sSiN"));
        yield return (typeof(nuint), MetadataFromNativeLibrary(libraryHandle, "$sSuN"));
        yield return (typeof(float), MetadataFromNativeLibrary(libraryHandle, "$sSfN"));
        yield return (typeof(double), MetadataFromNativeLibrary(libraryHandle, "$sSdN"));
        yield return (typeof(sbyte), MetadataFromNativeLibrary(libraryHandle, "$ss4Int8VN"));
        yield return (typeof(byte), MetadataFromNativeLibrary(libraryHandle, "$ss5UInt8VN"));
        yield return (typeof(short), MetadataFromNativeLibrary(libraryHandle, "$ss5Int16VN"));
        yield return (typeof(ushort), MetadataFromNativeLibrary(libraryHandle, "$ss6UInt16VN"));
        yield return (typeof(int), MetadataFromNativeLibrary(libraryHandle, "$ss5Int32VN"));
        yield return (typeof(uint), MetadataFromNativeLibrary(libraryHandle, "$ss6UInt32VN"));
        yield return (typeof(long), MetadataFromNativeLibrary(libraryHandle, "$ss5Int64VN"));
        yield return (typeof(ulong), MetadataFromNativeLibrary(libraryHandle, "$ss6UInt64VN"));
        yield return (typeof(void), MetadataFromNativeLibrary(libraryHandle, "$sytN"));

        // SwiftString metadata — $sSSN is Swift.String's metadata pointer in libswiftCore.
        // Pre-populating avoids the runtime fallback path in SwiftString.GetTypeMetadata().
        yield return (typeof(Swift.SwiftString), MetadataFromNativeLibrary(libraryHandle, "$sSSN"));
    }

    /// <summary>
    /// Loads type metadata from a NativeLibrary using the supplied symbol
    /// </summary>
    /// <param name="handle">handle to a library loaded by NativeLibrary</param>
    /// <param name="symbolName">Swift symbol for a type metadata object</param>
    /// <param name="libraryName">The library to load from. Defaults to libswiftCore.dylib</param>
    /// <returns>A type metadata object for the symbol</returns>
    /// <exception cref="SwiftRuntimeException">Throws on failure to load symbol</exception>
    static TypeMetadata MetadataFromNativeLibrary(IntPtr handle, string symbolName, string libraryName = KnownLibraries.SwiftCore)
    {
        if (NativeLibrary.TryGetExport(handle, symbolName, out var entryPoint))
        {
            return new TypeMetadata(entryPoint);
        }
        throw new SwiftRuntimeException($"Unable to find symbol {symbolName} in library {libraryName}");
    }


    /// <summary>
    /// Implicit conversion from TypeMetadata to void*
    /// </summary>
    public static unsafe implicit operator void*(TypeMetadata value)
    {
        return (void*)value.Handle;
    }

    /// <summary>
    /// Creates a TupleTypeMetadata accessor for this metadata.
    /// Only valid if this metadata represents a Tuple type (Kind == TypeMetadataKind.Tuple).
    /// </summary>
    /// <returns>A TupleTypeMetadata for accessing element offsets.</returns>
    /// <exception cref="InvalidOperationException">Thrown if this is not tuple metadata.</exception>
    public unsafe TupleTypeMetadata* AsTupleMetadata()
    {
        if (Kind != TypeMetadataKind.Tuple)
            throw new InvalidOperationException($"Cannot access tuple metadata for non-tuple type (kind: {Kind})");
        return (TupleTypeMetadata*)Handle;
    }

    /// <summary>
    /// Gets tuple type metadata from element types at runtime.
    /// </summary>
    /// <param name="elementMetadata">Array of element type metadata.</param>
    /// <returns>The tuple type metadata.</returns>
    public static unsafe TypeMetadata GetTupleTypeMetadataFromElements(params TypeMetadata[] elementMetadata)
    {
        if (elementMetadata == null || elementMetadata.Length == 0)
            throw new ArgumentException("At least one element metadata is required", nameof(elementMetadata));

        if (elementMetadata.Length > 7)
            throw new ArgumentException("Maximum 7 tuple elements supported", nameof(elementMetadata));

        var elementsArray = stackalloc IntPtr[elementMetadata.Length];
        for (int i = 0; i < elementMetadata.Length; i++)
        {
            elementsArray[i] = elementMetadata[i].Handle;
        }

        return swift_getTupleTypeMetadata(
            TypeMetadataRequest.Complete,
            (nuint)elementMetadata.Length,
            elementsArray,
            IntPtr.Zero,
            IntPtr.Zero
        );
    }
}

/// <summary>
/// Represents Swift's tuple type metadata layout.
/// Swift tuple metadata has a specific layout with element types and offsets
/// stored in an element vector following the base metadata.
/// </summary>
/// <remarks>
/// Swift tuple metadata layout (from Swift ABI):
/// - Offset -1: Value witness table pointer
/// - Offset 0: Kind (TypeMetadataKind.Tuple = 0x301)
/// - Offset 1: Number of elements
/// - Offset 2: Labels string pointer (space-separated, null if no labels)
/// - Offset 3+: Element vector (pairs of: element type metadata, element offset)
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct TupleTypeMetadata
{
    /// <summary>
    /// The metadata kind. For tuples, this is TypeMetadataKind.Tuple (0x301).
    /// </summary>
    public nuint Kind;

    /// <summary>
    /// The number of elements in the tuple.
    /// </summary>
    public nuint NumElements;

    /// <summary>
    /// Pointer to a space-separated string of element labels.
    /// Null if no labels are present. Empty string for unlabeled elements.
    /// </summary>
    public IntPtr Labels;

    // Element vector follows immediately after this struct.
    // Each element is a pair of (TypeMetadata*, nuint offset).

    /// <summary>
    /// Gets the byte offset of a tuple element at the specified index.
    /// </summary>
    /// <param name="index">The zero-based index of the element.</param>
    /// <returns>The byte offset of the element within the tuple's memory layout.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if index is out of range.</exception>
    public nuint GetElementOffset(int index)
    {
        if (index < 0 || (nuint)index >= NumElements)
            throw new ArgumentOutOfRangeException(nameof(index), $"Index {index} is out of range. Tuple has {NumElements} elements.");

        // Element vector starts immediately after this struct
        // Each element has: IntPtr typeMetadata, nuint offset
        // So the offset for element i is at: elementVector + (i * 2 + 1) * sizeof(IntPtr)
        fixed (TupleTypeMetadata* self = &this)
        {
            // Skip past the struct fields to get to element vector
            IntPtr* elementVector = (IntPtr*)((byte*)self + sizeof(TupleTypeMetadata));
            // Element i: [type at i*2, offset at i*2+1]
            return (nuint)elementVector[index * 2 + 1];
        }
    }

    /// <summary>
    /// Gets the type metadata pointer for a tuple element at the specified index.
    /// </summary>
    /// <param name="index">The zero-based index of the element.</param>
    /// <returns>The type metadata handle for the element.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if index is out of range.</exception>
    public IntPtr GetElementTypeHandle(int index)
    {
        if (index < 0 || (nuint)index >= NumElements)
            throw new ArgumentOutOfRangeException(nameof(index), $"Index {index} is out of range. Tuple has {NumElements} elements.");

        fixed (TupleTypeMetadata* self = &this)
        {
            IntPtr* elementVector = (IntPtr*)((byte*)self + sizeof(TupleTypeMetadata));
            // Element i: [type at i*2, offset at i*2+1]
            return elementVector[index * 2];
        }
    }

    /// <summary>
    /// Gets the value witness table for this tuple type.
    /// </summary>
    public ValueWitnessTable* ValueWitnessTable
    {
        get
        {
            fixed (TupleTypeMetadata* self = &this)
            {
                // VWT is at offset -1 (one pointer before the metadata)
                return (ValueWitnessTable*)(*((IntPtr*)self - 1));
            }
        }
    }

    /// <summary>
    /// Gets the total size of the tuple type in bytes.
    /// </summary>
    public nuint Size => ValueWitnessTable->Size;
}
