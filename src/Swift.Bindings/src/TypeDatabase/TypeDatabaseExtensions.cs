// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

// TODO: TypeDatabase should hold only nominal types (represented by NamedTypeSpec). Specifically tuples, closures etc. should not reside inside TypeDatabase.
// Functions taking TypeSpec should be moved into another class which will handle construction of complex types using nominal types.

public static class TypeDatabaseExtensions
{
    public readonly record struct AnyTypeFallbackInfo(string Reason, string SwiftType);

    /// <summary>
    /// Determines whether the specified Swift type has been processed.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <returns>True if the type has been processed; otherwise, false.</returns>
    public static bool IsTypeProcessed(this ITypeDatabase typeDatabase, TypeSpec typeSpec)
    {
        return typeSpec switch
        {
            NamedTypeSpec namedTypeSpec => typeDatabase.IsTypeProcessed(namedTypeSpec),
            TupleTypeSpec { IsEmptyTuple: true } => true,
            ProtocolListTypeSpec => true, // Existential types are handled via ExistentialContainer
            _ => false
        };
    }

    /// <summary>
    /// Determines whether the specified Swift type has been processed.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <returns>True if the type has been processed; otherwise, false.</returns>
    public static bool IsTypeProcessed(this ITypeDatabase typeDatabase, NamedTypeSpec typeSpec)
    {
        // Generic type parameters are handled as AnyType (considered "processed")
        if (TypeSpecHelpers.IsGenericTypeParameter(typeSpec.Name))
        {
            return true;
        }

        // Existential types (any X) are handled separately, not processed as regular types
        if (IsExistentialTypeName(typeSpec))
        {
            return true;
        }

        // Pointer types are always mapped to IntPtr
        if (IsPointerType(typeSpec))
        {
            return true;
        }

        var typeName = SwiftTypeName.FromTypeSpec(typeSpec);
        if (typeDatabase.IsTypeProcessed(typeName))
            return true;

        // ObjC root class types get synthetic ObjCBridged records (DB-first to allow explicit overrides)
        return IsObjCModuleType(typeSpec);
    }

    /// <summary>
    /// Gets the type record for the specified Swift type or the Any type if the type is not found.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <returns>The type record.</returns>
    public static TypeRecord GetTypeRecordOrAnyType(this ITypeDatabase typeDatabase, TypeSpec typeSpec)
    {
        return typeSpec switch
        {
            NamedTypeSpec namedTypeSpec => typeDatabase.GetTypeRecordOrAnyType(namedTypeSpec),
            TupleTypeSpec { IsEmptyTuple: true } => VoidType,
            ProtocolListTypeSpec protocolList => GetExistentialTypeRecord(protocolList),
            _ => AnyType
        };
    }

    /// <summary>
    /// Gets the type record for the specified Swift type or the Any type if the type is not found.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <returns>The type record.</returns>
    public static TypeRecord GetTypeRecordOrAnyType(this ITypeDatabase typeDatabase, NamedTypeSpec typeSpec)
    {
        // Generic type parameters (τ_0_0, T, Element, etc.) should return AnyType
        // since their concrete types aren't known at binding generation time
        if (TypeSpecHelpers.IsGenericTypeParameter(typeSpec.Name))
        {
            return AnyType;
        }

        // Existential types (any X) return AnyType
        if (IsExistentialTypeName(typeSpec))
        {
            return AnyType;
        }

        // Pointer types are always mapped to IntPtr
        if (IsPointerType(typeSpec))
        {
            return IntPtrType;
        }

        // ObjC types are handled in the SwiftTypeName overload (DB-first, synthetic second)
        var typeName = SwiftTypeName.FromTypeSpec(typeSpec);
        return typeDatabase.GetTypeRecordOrAnyType(typeName);
    }

    /// <summary>
    /// Gets the type record for the specified Swift type or throws an exception if the type is not found.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <returns>The type record.</returns>
    public static TypeRecord GetTypeRecordOrThrow(this ITypeDatabase typeDatabase, TypeSpec typeSpec)
    {
        return typeSpec switch
        {
            NamedTypeSpec namedTypeSpec => typeDatabase.GetTypeRecordOrThrow(namedTypeSpec),
            TupleTypeSpec { IsEmptyTuple: true } => VoidType,
            _ => throw new ArgumentException($"Attempted to read TypeRecord of unsupported type spec: {typeSpec}")
        };
    }

    /// <summary>
    /// Tries to get the type record for the specified Swift type.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <param name="record">The type record.</param>
    /// <returns>True if the type record was found; otherwise, false.</returns>
    public static bool TryGetTypeRecord(this ITypeDatabase typeDatabase, TypeSpec typeSpec, [NotNullWhen(returnValue: true)] out TypeRecord? record)
    {
        record = null;
        return typeSpec switch
        {
            NamedTypeSpec namedTypeSpec => typeDatabase.TryGetTypeRecord(namedTypeSpec, out record),
            TupleTypeSpec { IsEmptyTuple: true } => false,
            _ => false
        };
    }

    /// <summary>
    /// Tries to get the type record for the specified Swift type.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <param name="record">The type record.</param>
    /// <returns>True if the type record was found; otherwise, false.</returns>
    public static bool TryGetTypeRecord(this ITypeDatabase typeDatabase, NamedTypeSpec typeSpec, [NotNullWhen(returnValue: true)] out TypeRecord? record)
    {
        // Generic type parameters return AnyType
        if (TypeSpecHelpers.IsGenericTypeParameter(typeSpec.Name))
        {
            record = AnyType;
            return true;
        }

        // Existential types (any X) return AnyType
        if (IsExistentialTypeName(typeSpec))
        {
            record = AnyType;
            return true;
        }

        // Pointer types are always mapped to IntPtr
        if (IsPointerType(typeSpec))
        {
            record = IntPtrType;
            return true;
        }

        var typeName = SwiftTypeName.FromTypeSpec(typeSpec);
        if (typeDatabase.TryGetTypeRecord(typeName, out record))
            return true;

        // ObjC root class types get synthetic ObjCBridged records (DB-first to allow explicit overrides)
        if (IsObjCModuleType(typeSpec))
        {
            record = CreateObjCBridgedTypeRecord(typeName);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the type record for the specified Swift type or throws an exception if the type is not found.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <returns>The type record.</returns>
    public static TypeRecord GetTypeRecordOrThrow(this ITypeDatabase typeDatabase, NamedTypeSpec typeSpec)
    {
        // Generic type parameters return AnyType (they can't be resolved to concrete types)
        if (TypeSpecHelpers.IsGenericTypeParameter(typeSpec.Name))
        {
            return AnyType;
        }

        // Existential types (any X) return AnyType
        if (IsExistentialTypeName(typeSpec))
        {
            return AnyType;
        }

        // Pointer types are always mapped to IntPtr
        if (IsPointerType(typeSpec))
        {
            return IntPtrType;
        }

        // ObjC types are handled in the SwiftTypeName overload (DB-first, synthetic second)
        var typeName = SwiftTypeName.FromTypeSpec(typeSpec);
        return typeDatabase.GetTypeRecordOrThrow(typeName);
    }

    /// <summary>
    /// Gets the type record for the specified Swift type or throws an exception if the type is not found.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="swiftTypeName">The Swift type name.</param>
    /// <returns>The type record.</returns>
    public static TypeRecord GetTypeRecordOrThrow(this ITypeDatabase typeDatabase, SwiftTypeName swiftTypeName)
    {
        if (typeDatabase.TryGetTypeRecord(swiftTypeName, out var record))
            return record;

        // ObjC root class types not in the database get synthetic ObjCBridged records.
        // Only NSObject/NSProxy are safe — other ObjectiveC module types (Selector, ObjCBool)
        // are structs. TypeSpecParser remaps ObjectiveC.X → Foundation.X, so check both modules.
        if (IsObjCRootClassSwiftType(swiftTypeName))
            return CreateObjCBridgedTypeRecord(swiftTypeName);

        throw new Exception($"Type {swiftTypeName.ModuleQualifiedName} not found in database.");
    }

    /// <summary>
    /// Gets the type record for the specified Swift type or the Any type if the type is not found.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="swiftTypeName">The Swift type name.</param>
    /// <returns>The type record.</returns>
    public static TypeRecord GetTypeRecordOrAnyType(this ITypeDatabase typeDatabase, SwiftTypeName swiftTypeName)
    {
        if (typeDatabase.TryGetTypeRecord(swiftTypeName, out var record))
            return record;

        // ObjC root class types not in the database get synthetic ObjCBridged records.
        // Only NSObject/NSProxy are safe — other ObjectiveC module types (Selector, ObjCBool)
        // are structs. TypeSpecParser remaps ObjectiveC.X → Foundation.X, so check both modules.
        if (IsObjCRootClassSwiftType(swiftTypeName))
            return CreateObjCBridgedTypeRecord(swiftTypeName);

        return AnyType;
    }

    /// <summary>
    /// Tries to describe why a type would degrade to AnyType when resolving type records.
    /// Generic type parameters are excluded because they are expected to resolve through generic constraints.
    /// </summary>
    public static bool TryGetAnyTypeFallbackInfo(this ITypeDatabase typeDatabase, TypeSpec typeSpec, [NotNullWhen(true)] out AnyTypeFallbackInfo? fallbackInfo)
    {
        switch (typeSpec)
        {
            case NamedTypeSpec namedTypeSpec:
                return typeDatabase.TryGetAnyTypeFallbackInfo(namedTypeSpec, out fallbackInfo);
            default:
                fallbackInfo = null;
                return false;
        }
    }

    /// <summary>
    /// Tries to describe why a named type would degrade to AnyType when resolving type records.
    /// </summary>
    public static bool TryGetAnyTypeFallbackInfo(this ITypeDatabase typeDatabase, NamedTypeSpec typeSpec, [NotNullWhen(true)] out AnyTypeFallbackInfo? fallbackInfo)
    {
        // Generic type parameters (T, τ_0_0, Element, etc.) are expected and should not be marked as unsupported.
        if (TypeSpecHelpers.IsGenericTypeParameter(typeSpec.Name))
        {
            fallbackInfo = null;
            return false;
        }

        if (IsExistentialTypeName(typeSpec))
        {
            fallbackInfo = new AnyTypeFallbackInfo(
                "Existential type fallback",
                typeSpec.ToString());
            return true;
        }

        // Pointer types are fully handled (mapped to IntPtr), not a fallback
        if (IsPointerType(typeSpec))
        {
            fallbackInfo = null;
            return false;
        }

        // ObjC framework types are handled via synthetic ObjCBridged records, not a fallback
        if (IsObjCModuleType(typeSpec))
        {
            fallbackInfo = null;
            return false;
        }

        var typeName = SwiftTypeName.FromTypeSpec(typeSpec);
        if (typeDatabase.TryGetTypeRecord(typeName, out _))
        {
            fallbackInfo = null;
            return false;
        }

        fallbackInfo = new AnyTypeFallbackInfo(
            "Type is missing from the type database",
            typeName.ModuleQualifiedName);
        return true;
    }

    /// <summary>
    /// Gets the type record for Swift pointer types, mapped to System.IntPtr.
    /// Covers OpaquePointer, UnsafePointer, UnsafeMutablePointer, UnsafeRawPointer,
    /// UnsafeMutableRawPointer, and Builtin.RawPointer.
    /// </summary>
    public static TypeRecord IntPtrType { get; } = new TypeRecord
    {
        CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "IntPtr"),
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.OpaquePointer"),
        MetadataAccessor = string.Empty,
        Flags = TypeRecordFlags.Frozen,
        Kind = TypeRecordKind.Struct,
    };

    /// <summary>
    /// Gets the type record for the Any type.
    /// </summary>
    /// <returns>The type record for the Any type.</returns>
    public static TypeRecord AnyType { get; } = new TypeRecord
    {
        CSharpTypeName = CSharpTypeName.AnyType,
        SwiftTypeName = SwiftTypeName.AnyType,
        MetadataAccessor = string.Empty,
        Flags = TypeRecordFlags.None,
        Kind = TypeRecordKind.Protocol,
    };

    /// <summary>
    /// Gets the type record for the Void type.
    /// </summary>
    /// <returns>The type record for the Void type.</returns>
    public static TypeRecord VoidType { get; } = new TypeRecord
    {
        CSharpTypeName = CSharpTypeName.VoidType,
        SwiftTypeName = SwiftTypeName.VoidType,
        MetadataAccessor = string.Empty,
        Flags = TypeRecordFlags.Frozen,
        Kind = TypeRecordKind.Struct,
    };

    /// <summary>
    /// Gets the type record for an existential type (protocol or protocol composition).
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>The type record for the existential type.</returns>
    private static TypeRecord GetExistentialTypeRecord(ProtocolListTypeSpec protocolList)
    {
        var protocolCount = protocolList.Protocols.Count;
        var protocolNames = protocolList.Protocols.Count == 0
            ? "Any"
            : string.Join(" & ", protocolList.Protocols.Keys.Select(p => p.NameWithoutModule));

        return new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", $"ExistentialContainer{protocolCount}"),
            // Use AnyType for existential types since they don't have a standard module-qualified name
            SwiftTypeName = SwiftTypeName.AnyType,
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.Frozen, // Existential containers have fixed layout
            Kind = TypeRecordKind.Existential,
        };
    }

    /// <summary>
    /// The ObjectiveC module namespace mapping. Only the ObjectiveC module is safe for
    /// automatic ObjCBridged fallback because every type in it is an NSObject subclass.
    /// Other Apple framework modules (Foundation, UIKit, etc.) also contain value types
    /// and enums that would be misclassified as classes — those types must be registered
    /// in the type database XML explicitly.
    /// </summary>
    private const string ObjCModuleName = "ObjectiveC";
    private const string ObjCModuleCSharpNamespace = "Foundation";

    /// <summary>
    /// Creates a synthetic ObjCBridged TypeRecord for a type from the ObjectiveC module.
    /// The resulting record triggers the existing ObjCBridged marshalling pipeline
    /// (IntPtr in P/Invoke, Handle extraction in wrappers).
    /// </summary>
    private static TypeRecord CreateObjCBridgedTypeRecord(SwiftTypeName swiftTypeName)
    {
        return new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName(ObjCModuleCSharpNamespace, swiftTypeName.Name),
            SwiftTypeName = swiftTypeName,
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        };
    }

    /// <summary>
    /// Determines whether the specified NamedTypeSpec represents a known ObjC root class.
    /// Only NSObject and NSProxy are safe for synthetic ObjCBridged records — other
    /// ObjectiveC module types (Selector, ObjCBool) are structs.
    /// TypeSpecParser.cs remaps "ObjectiveC.X" → "Foundation.X", so we check both modules.
    /// </summary>
    private static bool IsObjCModuleType(NamedTypeSpec typeSpec)
    {
        if (!typeSpec.HasModule())
            return false;
        return (typeSpec.Module == ObjCModuleName || typeSpec.Module == "Foundation")
            && IsKnownObjCRootClass(typeSpec.NameWithoutModule);
    }

    /// <summary>
    /// Determines whether the specified SwiftTypeName represents a known ObjC root class.
    /// Mirrors <see cref="IsObjCModuleType"/> but for the SwiftTypeName path.
    /// </summary>
    private static bool IsObjCRootClassSwiftType(SwiftTypeName swiftTypeName)
    {
        return (swiftTypeName.Module == ObjCModuleName || swiftTypeName.Module == "Foundation")
            && IsKnownObjCRootClass(swiftTypeName.Name);
    }

    /// <summary>
    /// Returns true if the given unqualified type name is a known Objective-C root class.
    /// The ObjectiveC Swift module only defines NSObject and NSProxy as root classes;
    /// these get remapped to Foundation.NSObject and Foundation.NSProxy by TypeSpecParser.
    /// Other ObjectiveC module types (Selector, ObjCBool, NSZone) are value types.
    /// </summary>
    private static bool IsKnownObjCRootClass(string name)
    {
        return name is "NSObject" or "NSProxy";
    }

    /// <summary>
    /// Determines whether the specified NamedTypeSpec represents a Swift pointer type
    /// that should be mapped to System.IntPtr.
    /// </summary>
    private static bool IsPointerType(NamedTypeSpec typeSpec)
    {
        return typeSpec.Name is "Swift.OpaquePointer" or "Swift.UnsafePointer"
            or "Swift.UnsafeMutablePointer" or "Swift.UnsafeRawPointer"
            or "Swift.UnsafeMutableRawPointer" or "Builtin.RawPointer";
    }

    /// <summary>
    /// Determines whether the specified NamedTypeSpec represents an existential type.
    /// Existential types come through as NamedTypeSpec with names like "any" or "any SomeProtocol"
    /// when parsing tuple elements or enum associated values containing existential types.
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <returns><c>true</c> if this is an existential type name; otherwise, <c>false</c>.</returns>
    private static bool IsExistentialTypeName(NamedTypeSpec typeSpec)
    {
        // Check if the TypeSpec has the IsAny flag set (set by TypeSpecParser when "any" prefix is parsed)
        // This is the primary way existential types are detected (e.g., "any Swift.Encoder" -> IsAny=true, Name="Swift.Encoder")
        if (typeSpec.IsAny)
        {
            return true;
        }

        // Check for existential type patterns:
        // - "any" alone
        // - "any SomeProtocol" or "any Module.Protocol"
        if (typeSpec.Name == "any" || typeSpec.Name.StartsWith("any "))
        {
            return true;
        }

        // Don't classify generic type parameters as existential types.
        // Generic parameters (τ_0_0, T, Element, etc.) are unbound type parameters
        // that should be handled by the generic type system, not as existentials.
        if (TypeSpecHelpers.IsGenericTypeParameter(typeSpec.Name))
        {
            return false;
        }

        // Check if this is a type name without a module qualifier (no dot)
        // These are typically special types or parsing artifacts that should be treated as existential
        // Exclude known single-word types that are valid (Swift.Any, Swift.AnyObject are already prefixed)
        if (!typeSpec.HasModule() && typeSpec.Name != "Swift.Any" && typeSpec.Name != "Swift.AnyObject")
        {
            return true;
        }

        return false;
    }
}
