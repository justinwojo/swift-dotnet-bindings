// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Discriminated union encoding the marshalled type of a P/Invoke parameter.
/// Replaces the previous string-encoded markers (e.g. "Existential:Container:Public",
/// "SimpleEnum:int:MyEnum") with type-safe variants that support pattern matching.
/// </summary>
public abstract record MarshalledType
{
    // Prevent external subclassing
    private MarshalledType() { }

    // --- Prefixed variants (carry structured data) ---

    /// <summary>Existential protocol type with container and public interface types.</summary>
    public sealed record Existential(string ContainerType, string PublicType) : MarshalledType;

    /// <summary>Existential protocol type marshalled via ref (pointer) for @_cdecl wrappers.</summary>
    public sealed record CdeclExistential(string ContainerType, string PublicType) : MarshalledType;

    /// <summary>Simple C# enum backed by an underlying integer type.</summary>
    public sealed record SimpleEnum(string UnderlyingType, string EnumTypeName) : MarshalledType;

    /// <summary>ObjC bridged type (UIImage, etc.) marshalled via IntPtr + Handle.</summary>
    public sealed record ObjCBridged(string CSharpTypeName) : MarshalledType;

    /// <summary>Cdecl closure function pointer parameter.</summary>
    public sealed record CdeclClosureFuncPtr(string CallbackName, string SourceCsName) : MarshalledType;

    /// <summary>Cdecl closure context parameter.</summary>
    public sealed record CdeclClosureContext(string SourceCsName) : MarshalledType;

    /// <summary>Async+throwing closure context pointer.</summary>
    public sealed record AsyncThrowingContext(string ParamName) : MarshalledType;

    /// <summary>Async+throwing closure start function pointer.</summary>
    public sealed record AsyncThrowingStartFunc(string CallbackName) : MarshalledType;

    /// <summary>Native-remapped frozen type (e.g. SwiftData).</summary>
    public sealed record NativeRemappedFrozen(string SwiftWrapperType) : MarshalledType;

    /// <summary>Frozen struct with memory management requiring a .Buffer wrapper.</summary>
    public sealed record FrozenBuffer(string TypeName) : MarshalledType;

    /// <summary>Custom frozen struct passed as IntPtr (UnsafeRawPointer) in @_cdecl wrappers.
    /// Carries the public C# type name for wrapper signature, while P/Invoke uses IntPtr.</summary>
    public sealed record CdeclFrozenStruct(string CSharpTypeName) : MarshalledType;

    /// <summary>@convention(c) function pointer with full delegate* type string.</summary>
    public sealed record ConventionCFuncPtr(string FuncPtrType) : MarshalledType;

    /// <summary>SwiftSelf with typed generic parameter (e.g. SwiftSelf&lt;MyStruct&gt;).</summary>
    public sealed record SwiftSelfTyped(string InnerType) : MarshalledType;

    // --- Singleton variants (no data) ---

    /// <summary>Async callback void* parameter.</summary>
    public sealed record AsyncCallbackType : MarshalledType { public static readonly AsyncCallbackType Instance = new(); }

    /// <summary>Async error callback void* parameter.</summary>
    public sealed record AsyncErrorCallbackType : MarshalledType { public static readonly AsyncErrorCallbackType Instance = new(); }

    /// <summary>Async context void* parameter.</summary>
    public sealed record AsyncContextType : MarshalledType { public static readonly AsyncContextType Instance = new(); }

    /// <summary>Async task IntPtr parameter.</summary>
    public sealed record AsyncTaskType : MarshalledType { public static readonly AsyncTaskType Instance = new(); }

    /// <summary>Non-frozen struct/class as IntPtr (async path).</summary>
    public sealed record NonFrozenIntPtrType : MarshalledType { public static readonly NonFrozenIntPtrType Instance = new(); }

    /// <summary>Enum SafeHandle (complex enum, non-async).</summary>
    public sealed record EnumSafeHandleType : MarshalledType { public static readonly EnumSafeHandleType Instance = new(); }

    /// <summary>Native-remapped non-frozen type (URL as SafeHandle).</summary>
    public sealed record NativeRemappedNonFrozenType : MarshalledType { public static readonly NativeRemappedNonFrozenType Instance = new(); }

    /// <summary>Non-frozen struct/class as SafeHandle (sync path).</summary>
    public sealed record NonFrozenSafeHandleType : MarshalledType { public static readonly NonFrozenSafeHandleType Instance = new(); }

    /// <summary>Legacy SwiftClosureData parameter.</summary>
    public sealed record SwiftClosureLegacyType : MarshalledType { public static readonly SwiftClosureLegacyType Instance = new(); }

    /// <summary>Boolean with [MarshalAs(UnmanagedType.U1)] for P/Invoke.</summary>
    public sealed record BoolType : MarshalledType { public static readonly BoolType Instance = new(); }

    /// <summary>Untyped SwiftSelf parameter.</summary>
    public sealed record SwiftSelfUntypedType : MarshalledType { public static readonly SwiftSelfUntypedType Instance = new(); }

    // --- Catch-all ---

    /// <summary>Any C# type that needs no special marshalling treatment beyond its type name.</summary>
    public sealed record Simple(string CSharpType) : MarshalledType;

    // --- Convenience static factory methods for singletons ---

    public static readonly MarshalledType AsyncCallback = AsyncCallbackType.Instance;
    public static readonly MarshalledType AsyncErrorCallback = AsyncErrorCallbackType.Instance;
    public static readonly MarshalledType AsyncContext = AsyncContextType.Instance;
    public static readonly MarshalledType AsyncTask = AsyncTaskType.Instance;
    public static readonly MarshalledType NonFrozenIntPtr = NonFrozenIntPtrType.Instance;
    public static readonly MarshalledType EnumSafeHandle = EnumSafeHandleType.Instance;
    public static readonly MarshalledType NativeRemappedNonFrozen = NativeRemappedNonFrozenType.Instance;
    public static readonly MarshalledType NonFrozenSafeHandle = NonFrozenSafeHandleType.Instance;
    public static readonly MarshalledType SwiftClosureLegacy = SwiftClosureLegacyType.Instance;
    public static readonly MarshalledType Bool = BoolType.Instance;
    public static readonly MarshalledType SwiftSelfUntyped = SwiftSelfUntypedType.Instance;

    /// <summary>
    /// Returns whether this type contains the AnyType placeholder, indicating
    /// the type could not be fully resolved.
    /// </summary>
    public bool ContainsAnyTypePlaceholder()
    {
        var anyTypeName = TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        return this is Simple(var csharpType) && csharpType.Contains(anyTypeName);
    }

    /// <summary>
    /// Gets the primary C# type name string for use in wrapper/public API contexts.
    /// For Simple types this is the inner type name; for structured types this returns
    /// the public-facing type name (e.g., enum name for SimpleEnum, interface type for Existential).
    /// </summary>
    public string PublicTypeName => this switch
    {
        Existential(_, var publicType) => publicType,
        CdeclExistential(_, var publicType) => publicType,
        SimpleEnum(_, var enumTypeName) => enumTypeName,
        ObjCBridged(var csTypeName) => csTypeName,
        NativeRemappedFrozen(var swiftWrapperType) => swiftWrapperType,
        FrozenBuffer(var typeName) => typeName + ".Buffer",
        CdeclFrozenStruct(var csharpTypeName) => csharpTypeName,
        ConventionCFuncPtr(var funcPtrType) => funcPtrType,
        SwiftSelfTyped(var innerType) => $"SwiftSelf<{innerType}>",
        BoolType => "bool",
        SwiftSelfUntypedType => "SwiftSelf",
        NonFrozenSafeHandleType => "SafeHandle",
        SwiftClosureLegacyType => "SwiftClosureData",
        NonFrozenIntPtrType => "IntPtr",
        EnumSafeHandleType => "IntPtr",
        AsyncCallbackType => "void*",
        AsyncErrorCallbackType => "void*",
        AsyncContextType => "void*",
        AsyncTaskType => "IntPtr",
        NativeRemappedNonFrozenType => "SafeHandle",
        CdeclClosureFuncPtr => "IntPtr",
        CdeclClosureContext => "IntPtr",
        AsyncThrowingContext => "IntPtr",
        AsyncThrowingStartFunc => "delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>",
        Simple(var csharpType) => csharpType,
        _ => "unknown"
    };
}
