// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Classifies how a method can be dispatched through the witness table.
/// </summary>
public enum MethodDispatchKind
{
    /// <summary>Method cannot be dispatched (unsupported return type, async, etc.).</summary>
    NotDispatchable,
    /// <summary>Method returns blittable or String types (existing dispatch path).</summary>
    BlittableOrString,
    /// <summary>Method returns a protocol existential (new dispatch path with ARC-safe typed pointer).</summary>
    ExistentialReturn,
    /// <summary>Throwing method with blittable/String/void return (error out-parameter pattern).</summary>
    ThrowingBlittableOrString,
    /// <summary>Method returns a Swift class (ARC via Unmanaged.passRetained). Handles throwing internally.</summary>
    ClassReturn,
    /// <summary>Method returns a non-frozen struct or frozen+RefFields struct (indirect result buffer). Handles throwing internally.</summary>
    StructReturn,
    /// <summary>Method returns a bound generic collection (Array, Dictionary, Set). Uses heap-allocated pointer pattern like ExistentialReturn.</summary>
    BoundGenericReturn
}

/// <summary>
/// Pairs a <see cref="MethodDispatchKind"/> with an optional human-readable reason
/// explaining why the method is not dispatchable (null when dispatchable).
/// </summary>
public readonly record struct DispatchClassification(MethodDispatchKind Kind, string? Reason);

/// <summary>
/// Generates Swift @_silgen_name accessor functions that reconstruct existential containers
/// and dispatch through the protocol witness table. These accessors enable C# code to call
/// protocol members on Swift-backed existential containers via P/Invoke.
///
/// Phase A scope: blittable property getters, non-mutating methods returning blittable types,
/// non-mutating void methods with blittable parameters.
/// Phase B scope: String property getters/setters, String method params/returns,
/// blittable property setters.
/// Phase C scope: methods returning protocol existentials (throwing/non-throwing).
/// </summary>
public class WitnessDispatchEmitter
{
    private readonly ITypeDatabase _typeDatabase;
    private readonly ILogger _logger;
    private readonly string _moduleName;
    private readonly ModuleEmissionContext _emissionContext;

    /// <summary>
    /// Set of C# type names that are blittable and can be safely marshalled via Unsafe.Read/Write.
    /// </summary>
    private static readonly HashSet<string> BlittablePrimitiveTypes = new()
    {
        "bool", "System.Boolean",
        "sbyte", "System.SByte",
        "byte", "System.Byte",
        "short", "System.Int16",
        "ushort", "System.UInt16",
        "int", "System.Int32",
        "uint", "System.UInt32",
        "long", "System.Int64",
        "ulong", "System.UInt64",
        "nint", "System.IntPtr",
        "nuint", "System.UIntPtr",
        "float", "System.Single",
        "double", "System.Double",
    };

    /// <summary>
    /// Set of Swift type names that are known blittable primitives.
    /// Used as a fast path before falling back to TypeDatabase lookups.
    /// </summary>
    private static readonly HashSet<string> BlittableSwiftTypes = new()
    {
        "Swift.Int", "Swift.UInt",
        "Swift.Int8", "Swift.UInt8",
        "Swift.Int16", "Swift.UInt16",
        "Swift.Int32", "Swift.UInt32",
        "Swift.Int64", "Swift.UInt64",
        "Swift.Float", "Swift.Double",
        "Swift.Bool",
    };

    /// <summary>
    /// Maps Swift type names to C# type names for resolving types without the type database.
    /// </summary>
    private static readonly Dictionary<string, string> SwiftToCSharpPrimitiveMap = new()
    {
        ["Swift.Int"] = "nint", ["Swift.UInt"] = "nuint",
        ["Swift.Int8"] = "sbyte", ["Swift.UInt8"] = "byte",
        ["Swift.Int16"] = "short", ["Swift.UInt16"] = "ushort",
        ["Swift.Int32"] = "int", ["Swift.UInt32"] = "uint",
        ["Swift.Int64"] = "long", ["Swift.UInt64"] = "ulong",
        ["Swift.Float"] = "float", ["Swift.Double"] = "double",
        ["Swift.Bool"] = "bool",
    };

    /// <summary>
    /// Maps C# type names to Swift type names for use in generated Swift code.
    /// </summary>
    private static readonly Dictionary<string, string> CSharpToSwiftTypeMap = new()
    {
        ["bool"] = "Bool", ["System.Boolean"] = "Bool",
        ["sbyte"] = "Int8", ["System.SByte"] = "Int8",
        ["byte"] = "UInt8", ["System.Byte"] = "UInt8",
        ["short"] = "Int16", ["System.Int16"] = "Int16",
        ["ushort"] = "UInt16", ["System.UInt16"] = "UInt16",
        ["int"] = "Int32", ["System.Int32"] = "Int32",
        ["uint"] = "UInt32", ["System.UInt32"] = "UInt32",
        ["long"] = "Int64", ["System.Int64"] = "Int64",
        ["ulong"] = "UInt64", ["System.UInt64"] = "UInt64",
        ["nint"] = "Int", ["System.IntPtr"] = "Int",
        ["nuint"] = "UInt", ["System.UIntPtr"] = "UInt",
        ["float"] = "Float", ["System.Single"] = "Float",
        ["double"] = "Double", ["System.Double"] = "Double",
    };

    public WitnessDispatchEmitter(ITypeDatabase typeDatabase, ILogger logger, string moduleName, ModuleEmissionContext? ctx = null)
    {
        _typeDatabase = typeDatabase;
        _logger = logger;
        _moduleName = moduleName;
        _emissionContext = ctx ?? ModuleEmissionContext.Default;
    }

    /// <summary>
    /// Emits all witness dispatch accessor functions for a protocol.
    /// These are Swift functions that reconstruct the existential and dispatch through the witness table.
    /// </summary>
    public void EmitWitnessDispatchFunctions(SwiftWriter writer, ProtocolDecl protocolDecl)
    {
        var protocolName = protocolDecl.Name;
        var moduleQualifiedName = protocolDecl.SwiftTypeName.ModuleQualifiedName;

        // Track method indices for overload disambiguation (matching ProtocolProxyEmitter pattern)
        int methodIndex = 0;
        var methodIndices = new Dictionary<string, int>();

        bool anyEmitted = false;

        // Property getters (skip static properties - not part of witness table)
        var emittedPropertyNames = new HashSet<string>();
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;
            if (!emittedPropertyNames.Add(property.Name + "_get"))
                continue;
            var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
            if (hasGetter)
            {
                // Property getter dispatch: blittable/string use the blittable accessor,
                // class/struct types use ClassReturn/StructReturn accessor paths
                bool isBlittableOrString = IsTypeBlittable(property.SwiftTypeSpec) || IsStringType(property.SwiftTypeSpec);
                if (isBlittableOrString)
                {
                    if (!anyEmitted)
                    {
                        writer.WriteLine($"// Witness dispatch accessors for {protocolName}");
                        anyEmitted = true;
                    }
                    if (NeedsUtf8Slice(protocolDecl))
                    {
                        Utf8SliceEmitter.EmitIfNeeded(writer, _emissionContext);
                    }
                    EmitPropertyGetterAccessor(writer, property, protocolDecl, moduleQualifiedName);
                }
                else if (IsPropertyClassReturn(property))
                {
                    if (!anyEmitted)
                    {
                        writer.WriteLine($"// Witness dispatch accessors for {protocolName}");
                        anyEmitted = true;
                    }
                    EmitClassReturnPropertyGetterAccessor(writer, property, protocolDecl, moduleQualifiedName);
                }
                else if (IsPropertyStructReturn(property))
                {
                    if (!anyEmitted)
                    {
                        writer.WriteLine($"// Witness dispatch accessors for {protocolName}");
                        anyEmitted = true;
                    }
                    EmitStructReturnPropertyGetterAccessor(writer, property, protocolDecl, moduleQualifiedName);
                }
                else if (IsPropertyCollectionReturn(property))
                {
                    if (!anyEmitted)
                    {
                        writer.WriteLine($"// Witness dispatch accessors for {protocolName}");
                        anyEmitted = true;
                    }
                    EmitCollectionReturnPropertyGetterAccessor(writer, property, protocolDecl, moduleQualifiedName);
                }
            }
        }

        // Property setters (skip static properties - not part of witness table)
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;
            if (!emittedPropertyNames.Add(property.Name + "_set"))
                continue;
            var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
            // Property setter dispatch: only blittable/string (no class/struct setter dispatch yet)
            bool isSetterBlittableOrString = IsTypeBlittable(property.SwiftTypeSpec) || IsStringType(property.SwiftTypeSpec);
            if (hasSetter && isSetterBlittableOrString)
            {
                if (!anyEmitted)
                {
                    writer.WriteLine($"// Witness dispatch accessors for {protocolName}");
                    anyEmitted = true;
                }
                if (NeedsUtf8Slice(protocolDecl))
                {
                    Utf8SliceEmitter.EmitIfNeeded(writer, _emissionContext);
                }
                EmitPropertySetterAccessor(writer, property, protocolDecl, moduleQualifiedName);
            }
        }

        // Methods
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;

            var methodKey = GetMethodKey(method);
            if (methodIndices.ContainsKey(methodKey))
                continue;

            var idx = methodIndex++;
            methodIndices[methodKey] = idx;

            var kind = ClassifyMethodDispatch(method);
            if (kind == MethodDispatchKind.BlittableOrString)
            {
                if (!anyEmitted)
                {
                    writer.WriteLine($"// Witness dispatch accessors for {protocolName}");
                    anyEmitted = true;
                }
                if (NeedsUtf8Slice(protocolDecl))
                {
                    Utf8SliceEmitter.EmitIfNeeded(writer, _emissionContext);
                }
                EmitMethodAccessor(writer, method, protocolDecl, moduleQualifiedName, idx);
            }
            else if (kind == MethodDispatchKind.ThrowingBlittableOrString)
            {
                if (!anyEmitted)
                {
                    writer.WriteLine($"// Witness dispatch accessors for {protocolName}");
                    anyEmitted = true;
                }
                if (NeedsUtf8Slice(protocolDecl))
                {
                    Utf8SliceEmitter.EmitIfNeeded(writer, _emissionContext);
                }
                ErrorDescriptionEmitter.EmitIfNeeded(writer, _moduleName, _emissionContext);
                Utf8SliceEmitter.EmitFreeIfNeeded(writer, _moduleName, _emissionContext);
                EmitThrowingMethodAccessor(writer, method, protocolDecl, moduleQualifiedName, idx);
            }
            else if (kind == MethodDispatchKind.ExistentialReturn)
            {
                if (!anyEmitted)
                {
                    writer.WriteLine($"// Witness dispatch accessors for {protocolName}");
                    anyEmitted = true;
                }
                if (NeedsUtf8Slice(protocolDecl))
                {
                    Utf8SliceEmitter.EmitIfNeeded(writer, _emissionContext);
                }
                if (method.Throws)
                {
                    ErrorDescriptionEmitter.EmitIfNeeded(writer, _moduleName, _emissionContext);
                    Utf8SliceEmitter.EmitFreeIfNeeded(writer, _moduleName, _emissionContext);
                }
                EmitExistentialMethodAccessor(writer, method, protocolDecl, moduleQualifiedName, idx);
            }
            else if (kind == MethodDispatchKind.ClassReturn)
            {
                if (!anyEmitted)
                {
                    writer.WriteLine($"// Witness dispatch accessors for {protocolName}");
                    anyEmitted = true;
                }
                if (NeedsUtf8Slice(protocolDecl))
                {
                    Utf8SliceEmitter.EmitIfNeeded(writer, _emissionContext);
                }
                if (method.Throws)
                {
                    ErrorDescriptionEmitter.EmitIfNeeded(writer, _moduleName, _emissionContext);
                    Utf8SliceEmitter.EmitFreeIfNeeded(writer, _moduleName, _emissionContext);
                }
                EmitClassReturnMethodAccessor(writer, method, protocolDecl, moduleQualifiedName, idx);
            }
            else if (kind == MethodDispatchKind.StructReturn)
            {
                if (!anyEmitted)
                {
                    writer.WriteLine($"// Witness dispatch accessors for {protocolName}");
                    anyEmitted = true;
                }
                if (NeedsUtf8Slice(protocolDecl))
                {
                    Utf8SliceEmitter.EmitIfNeeded(writer, _emissionContext);
                }
                if (method.Throws)
                {
                    ErrorDescriptionEmitter.EmitIfNeeded(writer, _moduleName, _emissionContext);
                    Utf8SliceEmitter.EmitFreeIfNeeded(writer, _moduleName, _emissionContext);
                }
                EmitStructReturnMethodAccessor(writer, method, protocolDecl, moduleQualifiedName, idx);
            }
            else if (kind == MethodDispatchKind.BoundGenericReturn)
            {
                if (!anyEmitted)
                {
                    writer.WriteLine($"// Witness dispatch accessors for {protocolName}");
                    anyEmitted = true;
                }
                if (NeedsUtf8Slice(protocolDecl))
                {
                    Utf8SliceEmitter.EmitIfNeeded(writer, _emissionContext);
                }
                if (method.Throws)
                {
                    ErrorDescriptionEmitter.EmitIfNeeded(writer, _moduleName, _emissionContext);
                    Utf8SliceEmitter.EmitFreeIfNeeded(writer, _moduleName, _emissionContext);
                }
                EmitCollectionReturnMethodAccessor(writer, method, protocolDecl, moduleQualifiedName, idx);
            }
        }

        if (anyEmitted)
            writer.WriteLine();
    }

    /// <summary>
    /// Determines if a property getter can be dispatched via witness table.
    /// A getter is dispatchable if its return type is blittable or String.
    /// </summary>
    public bool IsPropertyGetterDispatchable(PropertyDecl property)
    {
        return IsTypeDispatchable(property.SwiftTypeSpec);
    }

    /// <summary>
    /// Determines if a property setter can be dispatched via witness table.
    /// A setter is dispatchable if its type is blittable or String.
    /// </summary>
    public bool IsPropertySetterDispatchable(PropertyDecl property)
    {
        return IsTypeDispatchable(property.SwiftTypeSpec);
    }

    /// <summary>
    /// Classifies how a method can be dispatched through the witness table.
    /// Returns <see cref="MethodDispatchKind.BlittableOrString"/> for methods with all blittable/String types,
    /// <see cref="MethodDispatchKind.ExistentialReturn"/> for methods returning protocol existentials
    /// (including throwing methods), or <see cref="MethodDispatchKind.NotDispatchable"/> otherwise.
    /// </summary>
    public MethodDispatchKind ClassifyMethodDispatch(MethodDecl method)
    {
        // Async methods are never dispatchable (require Swift concurrency runtime)
        if (method.IsAsync)
            return MethodDispatchKind.NotDispatchable;

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;

        // Check if return type is an existential that can be dispatched
        if (hasReturn && IsExistentialDispatchable(returnType!))
        {
            // Throwing + optional existential conflict: IntPtr.Zero is used as error sentinel,
            // which collides with the .none sentinel for optionals. Block this combination.
            if (method.Throws && MarshallingHelpers.IsSwiftOptional(returnType!))
                return MethodDispatchKind.NotDispatchable;

            // Existential return path: allows throwing (uses error out-parameter)
            // All params must still be blittable/String
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (!IsTypeDispatchable(param.SwiftTypeSpec))
                    return MethodDispatchKind.NotDispatchable;
            }
            return MethodDispatchKind.ExistentialReturn;
        }

        // Check if return type is a bound generic collection (Array, Dictionary, Set)
        // Must be before class/struct checks because Array could match IsIndirectStructType
        if (hasReturn && IsBoundGenericReturnDispatchable(returnType!))
        {
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (!IsTypeDispatchable(param.SwiftTypeSpec))
                    return MethodDispatchKind.NotDispatchable;
            }
            return MethodDispatchKind.BoundGenericReturn;
        }

        // Check if return type is a concrete class (ARC via Unmanaged.passRetained)
        // Handles throwing internally (same as ExistentialReturn)
        if (hasReturn && IsClassReturn(returnType!))
        {
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (!IsTypeDispatchable(param.SwiftTypeSpec))
                    return MethodDispatchKind.NotDispatchable;
            }
            return MethodDispatchKind.ClassReturn;
        }

        // Check if return type is a non-frozen struct (indirect result buffer)
        // Handles throwing internally (same as ClassReturn)
        if (hasReturn && IsStructReturn(returnType!))
        {
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (!IsTypeDispatchable(param.SwiftTypeSpec))
                    return MethodDispatchKind.NotDispatchable;
            }
            return MethodDispatchKind.StructReturn;
        }

        // Throwing methods with blittable/String/void return use error out-parameter pattern
        if (method.Throws)
        {
            if (hasReturn && !IsTypeDispatchable(returnType!))
                return MethodDispatchKind.NotDispatchable;
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (!IsTypeDispatchable(param.SwiftTypeSpec))
                    return MethodDispatchKind.NotDispatchable;
            }
            return MethodDispatchKind.ThrowingBlittableOrString;
        }

        // Check return type is blittable/String
        if (hasReturn && !IsTypeDispatchable(returnType!))
            return MethodDispatchKind.NotDispatchable;

        // Check all parameters
        foreach (var param in method.CSSignature.Skip(1))
        {
            if (!IsTypeDispatchable(param.SwiftTypeSpec))
                return MethodDispatchKind.NotDispatchable;
        }

        return MethodDispatchKind.BlittableOrString;
    }

    /// <summary>
    /// Classifies method dispatch with a human-readable reason when not dispatchable.
    /// Returns <see cref="DispatchClassification"/> with Kind and optional Reason string.
    /// </summary>
    public DispatchClassification ClassifyMethodDispatchWithReason(MethodDecl method)
    {
        if (method.IsAsync)
            return new DispatchClassification(MethodDispatchKind.NotDispatchable, "async methods require Swift concurrency runtime");

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;

        // Check return type dispatchability first for non-blittable return reason
        if (hasReturn && IsExistentialDispatchable(returnType!))
        {
            if (method.Throws && MarshallingHelpers.IsSwiftOptional(returnType!))
                return new DispatchClassification(MethodDispatchKind.NotDispatchable, "throwing methods with optional existential return are not supported");

            foreach (var param in method.CSSignature.Skip(1))
            {
                if (!IsTypeDispatchable(param.SwiftTypeSpec))
                    return new DispatchClassification(MethodDispatchKind.NotDispatchable,
                        $"parameter '{param.Name}' has non-dispatchable type '{param.SwiftTypeSpec}'");
            }
            return new DispatchClassification(MethodDispatchKind.ExistentialReturn, null);
        }

        if (hasReturn && IsBoundGenericReturnDispatchable(returnType!))
        {
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (!IsTypeDispatchable(param.SwiftTypeSpec))
                    return new DispatchClassification(MethodDispatchKind.NotDispatchable,
                        $"parameter '{param.Name}' has non-dispatchable type '{param.SwiftTypeSpec}'");
            }
            return new DispatchClassification(MethodDispatchKind.BoundGenericReturn, null);
        }

        if (hasReturn && IsClassReturn(returnType!))
        {
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (!IsTypeDispatchable(param.SwiftTypeSpec))
                    return new DispatchClassification(MethodDispatchKind.NotDispatchable,
                        $"parameter '{param.Name}' has non-dispatchable type '{param.SwiftTypeSpec}'");
            }
            return new DispatchClassification(MethodDispatchKind.ClassReturn, null);
        }

        if (hasReturn && IsStructReturn(returnType!))
        {
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (!IsTypeDispatchable(param.SwiftTypeSpec))
                    return new DispatchClassification(MethodDispatchKind.NotDispatchable,
                        $"parameter '{param.Name}' has non-dispatchable type '{param.SwiftTypeSpec}'");
            }
            return new DispatchClassification(MethodDispatchKind.StructReturn, null);
        }

        if (method.Throws)
        {
            if (hasReturn && !IsTypeDispatchable(returnType!))
                return new DispatchClassification(MethodDispatchKind.NotDispatchable,
                    $"return type '{returnType}' is not dispatchable");
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (!IsTypeDispatchable(param.SwiftTypeSpec))
                    return new DispatchClassification(MethodDispatchKind.NotDispatchable,
                        $"parameter '{param.Name}' has non-dispatchable type '{param.SwiftTypeSpec}'");
            }
            return new DispatchClassification(MethodDispatchKind.ThrowingBlittableOrString, null);
        }

        if (hasReturn && !IsTypeDispatchable(returnType!))
            return new DispatchClassification(MethodDispatchKind.NotDispatchable,
                $"return type '{returnType}' is not dispatchable");

        foreach (var param in method.CSSignature.Skip(1))
        {
            if (!IsTypeDispatchable(param.SwiftTypeSpec))
                return new DispatchClassification(MethodDispatchKind.NotDispatchable,
                    $"parameter '{param.Name}' has non-dispatchable type '{param.SwiftTypeSpec}'");
        }

        return new DispatchClassification(MethodDispatchKind.BlittableOrString, null);
    }

    /// <summary>
    /// Returns a human-readable reason why a property type is not dispatchable via witness table.
    /// Returns null if the property is dispatchable.
    /// </summary>
    public string? GetPropertyNonDispatchReason(PropertyDecl property)
    {
        if (IsTypeDispatchable(property.SwiftTypeSpec)
            || IsPropertyClassReturn(property)
            || IsPropertyStructReturn(property)
            || IsPropertyCollectionReturn(property))
            return null;

        return $"property type '{property.SwiftTypeSpec}' is not dispatchable via witness table";
    }

    /// <summary>
    /// Determines if a method can be dispatched via witness table (backward-compat wrapper).
    /// Returns true if the method is dispatchable via any dispatch kind.
    /// </summary>
    public bool IsMethodDispatchable(MethodDecl method)
    {
        return ClassifyMethodDispatch(method) != MethodDispatchKind.NotDispatchable;
    }

    /// <summary>
    /// Checks if a return type is a protocol existential that can be dispatched
    /// through the witness table using a typed pointer allocation pattern.
    /// Reuses <see cref="ProtocolExtensionEmitter.IsSupportedExistentialReturn"/> for validation,
    /// then adds additional gates: must not be a well-known protocol type, and must have
    /// a valid proxy class name.
    /// </summary>
    public bool IsExistentialDispatchable(TypeSpec returnType)
    {
        var existentialHandler = new ExistentialHandler(_typeDatabase);

        // Check for Optional<any Protocol> — unwrap and validate the inner existential
        // Must apply the same safety gates as IsSupportedExistentialReturn (via IsSupportedExistentialCore)
        if (existentialHandler.IsOptionalExistential(returnType))
        {
            var innerProtocolList = existentialHandler.UnwrapOptionalExistential(returnType);
            if (innerProtocolList == null)
                return false;

            // IsSupportedExistential checks (witness table count limit)
            if (!existentialHandler.IsSupportedExistential(innerProtocolList))
                return false;

            // Well-known types (e.g., "any Error" → AnyError) use different wrappers, not proxy classes
            if (existentialHandler.TryGetWellKnownProtocolType(innerProtocolList, out _))
                return false;

            // Zero-protocol "Any" has no proxy class
            if (existentialHandler.IsAnyType(innerProtocolList))
                return false;

            // All protocols must have TypeRecords in the database
            if (!existentialHandler.AllProtocolsHaveTypeRecords(innerProtocolList))
                return false;

            // Block unresolved/unknown protocols and generic protocol existentials
            var publicType = existentialHandler.GetPublicExistentialType(innerProtocolList);
            if (publicType == "object" ||
                publicType == TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName)
                return false;

            // ObjC filtering guard: if filtering drops protocols, ExistentialContainer size mismatches
            var filteredCount = innerProtocolList.Protocols.Keys
                .Count(p => !TypeDatabaseExtensions.IsObjCModuleType(p));
            if (filteredCount != innerProtocolList.Protocols.Count)
                return false;

            // Must have a valid proxy class name (filters ObjC-only protocols)
            if (!existentialHandler.TryGetFilteredProxyClassName(innerProtocolList, out _))
                return false;

            // Reject protocols with flags that prevent proxy emission (PAT, Self, InheritedRequirementsOnly)
            if (ProtocolExtensionEmitter.HasBlockingProtocolFlagsForReturn(innerProtocolList, _typeDatabase))
                return false;

            return true;
        }

        // Delegate to the existing comprehensive existential validation
        if (!ProtocolExtensionEmitter.IsSupportedExistentialReturn(returnType, _typeDatabase))
            return false;

        // IsSupportedExistentialReturn allows well-known types (e.g., Swift.Error → AnyError)
        // and zero-protocol "Any" → ExistentialContainer0. These use different C# wrappers,
        // not proxy classes, so they can't use the existential dispatch pattern.
        var protocolList = existentialHandler.ToProtocolListTypeSpec(returnType);
        if (protocolList == null)
            return false;

        // Reject well-known types (e.g., "any Error" → AnyError)
        if (existentialHandler.TryGetWellKnownProtocolType(protocolList, out _))
            return false;

        // Reject zero-protocol "Any" (no proxy class)
        if (existentialHandler.IsAnyType(protocolList))
            return false;

        // Must have a valid proxy class name
        if (!existentialHandler.TryGetFilteredProxyClassName(protocolList, out _))
            return false;

        return true;
    }

    /// <summary>
    /// Checks if a TypeSpec represents a Swift class (TypeRecordKind.Class) in the type database.
    /// Rejects generic types (ContainsGenericParameters) and ObjC module types.
    /// Does NOT check IsTypeBlittable/IsStringType — use for raw type identification only.
    /// </summary>
    public bool IsSwiftClassType(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;
        if (namedType.ContainsGenericParameters)
            return false;
        if (TypeDatabaseExtensions.IsObjCModuleType(namedType))
            return false;

        try
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
                return typeRecord.Kind == TypeRecordKind.Class
                    && typeRecord.NativeTypeName == null; // Exclude native-remapped (e.g., Foundation.URL → NSUrl)
        }
        catch (ArgumentException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Checks if a TypeSpec represents a struct that requires indirect dispatch
    /// (non-frozen struct or frozen struct with RequiresMemoryManagement).
    /// Does NOT check IsTypeBlittable/IsStringType — use for raw type identification only.
    /// </summary>
    public bool IsIndirectStructType(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;
        if (namedType.ContainsGenericParameters)
            return false;

        try
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
            {
                if (typeRecord.Kind != TypeRecordKind.Struct)
                    return false;
                if (typeRecord.NativeTypeName != null)
                    return false; // Exclude native-remapped (e.g., Foundation.Data → NSData)
                bool isFrozen = typeRecord.Flags.HasFlag(TypeRecordFlags.Frozen);
                bool hasRefFields = typeRecord.Flags.HasFlag(TypeRecordFlags.RequiresMemoryManagement);
                // Frozen value-type structs not supported (would be blittable)
                if (isFrozen && !hasRefFields)
                    return false;
                // Non-frozen OR frozen+RefFields → indirect result buffer
                return true;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Checks if a return type is a Swift class (TypeRecordKind.Class) that can be
    /// dispatched through the witness table using Unmanaged.passRetained.
    /// Rejects generic types (ContainsGenericParameters) and ObjC module types.
    /// </summary>
    public bool IsClassReturn(TypeSpec returnType)
    {
        // Already handled by blittable/String dispatch — use explicit checks to avoid circular dependency
        if (IsTypeBlittable(returnType) || IsStringType(returnType))
            return false;

        return IsSwiftClassType(returnType);
    }

    /// <summary>
    /// Checks if a return type is a struct that requires indirect result buffer
    /// (non-frozen struct or frozen struct with RequiresMemoryManagement).
    /// Matches ExtensionMarshallingHelper.ClassifyReturnType logic for NonFrozenStruct.
    /// </summary>
    public bool IsStructReturn(TypeSpec returnType)
    {
        // Already handled by blittable/String dispatch — use explicit checks to avoid circular dependency
        if (IsTypeBlittable(returnType) || IsStringType(returnType))
            return false;

        return IsIndirectStructType(returnType);
    }

    /// <summary>
    /// Checks if a struct return type is a frozen struct with reference-type fields (ClassWithBufferStruct).
    /// For this subtype, NewFromPayload copies to a new buffer, so the original buffer must be freed on success.
    /// </summary>
    public bool IsFrozenStructWithRefFields(TypeSpec returnType)
    {
        if (returnType is not NamedTypeSpec namedType)
            return false;
        try
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
            {
                return MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord);
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
        return false;
    }

    /// <summary>
    /// Checks if a property getter returns a Swift class (dispatchable via ClassReturn pattern).
    /// </summary>
    public bool IsPropertyClassReturn(PropertyDecl property)
    {
        return IsClassReturn(property.SwiftTypeSpec);
    }

    /// <summary>
    /// Checks if a property getter returns a struct requiring indirect result buffer.
    /// </summary>
    public bool IsPropertyStructReturn(PropertyDecl property)
    {
        return IsStructReturn(property.SwiftTypeSpec);
    }

    /// <summary>
    /// Checks if a TypeSpec represents a collection type (Array, Dictionary, or Set).
    /// </summary>
    public static bool IsCollectionType(TypeSpec? typeSpec)
    {
        return MarshallingHelpers.IsSwiftArray(typeSpec) ||
               MarshallingHelpers.IsSwiftDictionary(typeSpec) ||
               MarshallingHelpers.IsSwiftSet(typeSpec);
    }

    /// <summary>
    /// Checks if a property getter returns a collection type that can be dispatched.
    /// </summary>
    public bool IsPropertyCollectionReturn(PropertyDecl property)
    {
        return IsCollectionType(property.SwiftTypeSpec) && IsBoundGenericReturnDispatchable(property.SwiftTypeSpec);
    }

    /// <summary>
    /// Validates that a collection return type can be dispatched:
    /// - Outer type is Array, Dictionary, or Set
    /// - Element types resolve in TypeDatabase (not AnyType)
    /// - For Dictionary: both key AND value must resolve
    /// </summary>
    public bool IsBoundGenericReturnDispatchable(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;

        if (!IsCollectionType(typeSpec))
            return false;

        var genericParams = namedType.GenericParameters;
        if (genericParams.Count == 0)
            return false;

        // Validate each element type resolves (not AnyType)
        foreach (var elemType in genericParams)
        {
            if (!IsElementTypeResolvable(elemType))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Gets the Swift collection type string for heap allocation in witness dispatch.
    /// E.g., Swift.Array&lt;Swift.String&gt; → "[String]", Swift.Dictionary → "[K: V]", Swift.Set → "Set&lt;T&gt;".
    /// </summary>
    public string? GetSwiftCollectionTypeString(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return null;

        string MapElement(TypeSpec elemType)
        {
            if (elemType is NamedTypeSpec namedElem)
            {
                // Known Swift primitives (Swift.Int, Swift.Bool, etc.) — strip module prefix
                if (SwiftToCSharpPrimitiveMap.ContainsKey(namedElem.Name))
                    return namedElem.NameWithoutModule;
                // Swift.String — strip module prefix
                if (IsStringType(elemType))
                    return namedElem.NameWithoutModule;
                // Keep module-qualified for user types
                return namedElem.Name;
            }
            return "Any";
        }

        if (MarshallingHelpers.IsSwiftArray(typeSpec))
        {
            var elem = MapElement(namedType.GenericParameters[0]);
            return $"[{elem}]";
        }
        if (MarshallingHelpers.IsSwiftDictionary(typeSpec))
        {
            var key = MapElement(namedType.GenericParameters[0]);
            var value = MapElement(namedType.GenericParameters[1]);
            return $"[{key}: {value}]";
        }
        if (MarshallingHelpers.IsSwiftSet(typeSpec))
        {
            var elem = MapElement(namedType.GenericParameters[0]);
            return $"Set<{elem}>";
        }
        return null;
    }

    /// <summary>
    /// Gets the module-qualified Swift type name for a concrete TypeSpec.
    /// Used for struct return's assumingMemoryBound(to:) and class return's type cast.
    /// Returns null if the type cannot be resolved.
    /// </summary>
    public string? GetSwiftConcreteTypeName(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return null;
        // The Swift name is the module-qualified name from the TypeSpec itself
        return namedType.Name;
    }

    /// <summary>
    /// Gets the C# type name for a concrete class/struct return, suitable for
    /// SwiftMarshal.MarshalFromSwift&lt;T&gt;() calls.
    /// Returns null if the type cannot be resolved.
    /// </summary>
    public string? GetConcreteReturnCSharpType(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return null;
        try
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
                return typeRecord.CSharpTypeName.FullyQualifiedName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        return null;
    }

    /// <summary>
    /// Checks if a TypeSpec represents Swift.String.
    /// Used by ProtocolProxyEmitter to branch on String-specific marshalling.
    /// </summary>
    public static bool IsStringType(TypeSpec? typeSpec)
    {
        return typeSpec is NamedTypeSpec namedType && namedType.Name == "Swift.String";
    }

    /// <summary>
    /// Checks if a type can be dispatched through witness accessors.
    /// This includes blittable primitives, Swift.String (via UTF-8 bridge),
    /// Swift classes (via Unmanaged pointer), and indirect structs (non-frozen or frozen+RefFields).
    /// </summary>
    public bool IsTypeDispatchable(TypeSpec? typeSpec)
    {
        return IsTypeBlittable(typeSpec) || IsStringType(typeSpec)
            || IsSwiftClassType(typeSpec) || IsIndirectStructType(typeSpec);
    }

    /// <summary>
    /// Checks if a TypeSpec represents a String dispatch type.
    /// Public for ProtocolProxyEmitter to branch on String vs blittable marshalling.
    /// </summary>
    public static bool IsStringDispatchType(TypeSpec? typeSpec)
    {
        return IsStringType(typeSpec);
    }

    /// <summary>
    /// Gets the @_silgen_name symbol for an accessor function.
    /// Format: SBW_{Protocol}_{kind}_{name}_{index}
    /// </summary>
    public static string GetAccessorSymbol(string protocolName, string kind, string memberName, int index)
    {
        return $"SBW_{protocolName}_{kind}_{memberName}_{index}";
    }

    /// <summary>
    /// Gets the @_silgen_name symbol for a free function.
    /// Format: SBW_{Protocol}_free_{kind}_{name}_{index}
    /// </summary>
    public static string GetFreeSymbol(string protocolName, string kind, string memberName, int index)
    {
        return $"SBW_{protocolName}_free_{kind}_{memberName}_{index}";
    }

    /// <summary>
    /// Checks if a C# type name represents a blittable primitive.
    /// </summary>
    public static bool IsBlittablePrimitive(string csharpTypeName)
    {
        return BlittablePrimitiveTypes.Contains(csharpTypeName);
    }

    /// <summary>
    /// Returns the canonical blittable C# type name for a TypeSpec.
    /// Uses the Swift-name fast-path first, then falls back to the type database.
    /// This must be used for MarshalFromSwift/MarshalToSwift type parameters
    /// to ensure the marshal type matches the dispatch gate decision.
    /// Returns null if the type is not blittable.
    /// </summary>
    public string? GetBlittableCSharpType(TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        // Fast path: map known Swift primitives directly
        if (typeSpec is NamedTypeSpec namedType && SwiftToCSharpPrimitiveMap.TryGetValue(namedType.Name, out var csharpType))
            return csharpType;

        // Slow path: fall back to type database
        try
        {
            var record = _typeDatabase.GetTypeRecordOrAnyType(typeSpec);
            var fqn = record.CSharpTypeName.FullyQualifiedName;
            return IsBlittablePrimitive(fqn) ? fqn : null;
        }
        catch
        {
            return null;
        }
    }

    #region Private Helpers

    /// <summary>
    /// Checks whether a protocol has any dispatchable members that use String types,
    /// which requires the SBW_Utf8Slice struct to be emitted.
    /// </summary>
    private bool NeedsUtf8Slice(ProtocolDecl protocolDecl)
    {
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;
            if (IsStringType(property.SwiftTypeSpec))
                return true;
        }
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;
            // Skip async methods entirely, but for throwing methods check if they
            // are ExistentialReturn dispatchable (those can have String params)
            if (method.IsAsync)
                continue;
            var kind = ClassifyMethodDispatch(method);
            if (kind == MethodDispatchKind.NotDispatchable)
                continue;
            var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            if (returnType != null && !returnType.IsEmptyTuple && IsStringType(returnType))
                return true;
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (IsStringType(param.SwiftTypeSpec))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks if an element type (used inside a collection) can be resolved
    /// in the type database to a concrete type (not AnyType).
    /// </summary>
    private bool IsElementTypeResolvable(TypeSpec elemType)
    {
        if (elemType is not NamedTypeSpec namedElem)
            return false;

        // Known Swift primitive types are always resolvable
        if (SwiftToCSharpPrimitiveMap.ContainsKey(namedElem.Name))
            return true;

        // Swift.String is always resolvable (not in primitive map since it's not blittable)
        if (IsStringType(elemType))
            return true;

        // Check type database
        try
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedElem.Name);
            if (_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
                return typeRecord != TypeDatabaseExtensions.AnyType;
        }
        catch (ArgumentException)
        {
            return false;
        }
        return false;
    }

    private bool IsTypeBlittable(TypeSpec? typeSpec)
    {
        if (typeSpec == null) return false;

        // Fast path: check Swift type name directly against known primitives
        if (typeSpec is NamedTypeSpec namedType && BlittableSwiftTypes.Contains(namedType.Name))
            return true;

        // Slow path: fall back to type database
        try
        {
            var record = _typeDatabase.GetTypeRecordOrAnyType(typeSpec);
            return IsBlittablePrimitive(record.CSharpTypeName.FullyQualifiedName);
        }
        catch
        {
            return false;
        }
    }

    private string GetCSharpTypeName(TypeSpec? typeSpec)
    {
        if (typeSpec == null) return "object";

        // Fast path: map known Swift primitives directly
        if (typeSpec is NamedTypeSpec namedType && SwiftToCSharpPrimitiveMap.TryGetValue(namedType.Name, out var csharpType))
            return csharpType;

        try
        {
            var record = _typeDatabase.GetTypeRecordOrAnyType(typeSpec);
            return record.CSharpTypeName.FullyQualifiedName;
        }
        catch
        {
            return "object";
        }
    }

    private static string GetSwiftPrimitiveType(string csharpTypeName)
    {
        return CSharpToSwiftTypeMap.TryGetValue(csharpTypeName, out var swiftType)
            ? swiftType
            : "Any";
    }

    private void EmitPropertyGetterAccessor(SwiftWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, string moduleQualifiedName)
    {
        var protocolName = protocolDecl.Name;
        var accessorSymbol = GetAccessorSymbol(protocolName, "get", property.Name, 0);
        var freeSymbol = GetFreeSymbol(protocolName, "get", property.Name, 0);

        if (IsStringType(property.SwiftTypeSpec))
        {
            // String getter: convert Swift String to UTF-8 bytes via SBW_Utf8Slice
            writer.WriteLines($$"""
                @_silgen_name("{{accessorSymbol}}")
                public func {{accessorSymbol}}(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
                    let existential = containerPtr.load(as: (any {{moduleQualifiedName}}).self)
                    let result: String = existential.{{property.Name}}
                    let utf8 = Array(result.utf8)
                    let bufferPtr = UnsafeMutablePointer<UInt8>.allocate(capacity: max(utf8.count, 1))
                    if !utf8.isEmpty {
                        utf8.withUnsafeBufferPointer { src in
                            bufferPtr.initialize(from: src.baseAddress!, count: src.count)
                        }
                    }
                    let slicePtr = UnsafeMutablePointer<SBW_Utf8Slice>.allocate(capacity: 1)
                    slicePtr.initialize(to: SBW_Utf8Slice(ptr: bufferPtr, len: utf8.count))
                    return UnsafeMutableRawPointer(slicePtr)
                }

                @_silgen_name("{{freeSymbol}}")
                public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                    let slicePtr = ptr.assumingMemoryBound(to: SBW_Utf8Slice.self)
                    slicePtr.pointee.ptr.deallocate()
                    slicePtr.deinitialize(count: 1)
                    slicePtr.deallocate()
                }

                """);
        }
        else
        {
            // Blittable getter: direct pointer allocation
            var csharpReturnType = GetCSharpTypeName(property.SwiftTypeSpec);
            var swiftReturnType = GetSwiftPrimitiveType(csharpReturnType);

            writer.WriteLines($$"""
                @_silgen_name("{{accessorSymbol}}")
                public func {{accessorSymbol}}(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
                    let existential = containerPtr.load(as: (any {{moduleQualifiedName}}).self)
                    let result = existential.{{property.Name}}
                    let ptr = UnsafeMutablePointer<{{swiftReturnType}}>.allocate(capacity: 1)
                    ptr.initialize(to: result)
                    return UnsafeMutableRawPointer(ptr)
                }

                @_silgen_name("{{freeSymbol}}")
                public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                    ptr.assumingMemoryBound(to: {{swiftReturnType}}.self).deinitialize(count: 1)
                    ptr.deallocate()
                }

                """);
        }
    }

    private void EmitPropertySetterAccessor(SwiftWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, string moduleQualifiedName)
    {
        var protocolName = protocolDecl.Name;
        var accessorSymbol = GetAccessorSymbol(protocolName, "set", property.Name, 0);

        if (IsStringType(property.SwiftTypeSpec))
        {
            // String setter: decode SBW_Utf8Slice → String, then assign via typed pointee
            writer.WriteLines($$"""
                @_silgen_name("{{accessorSymbol}}")
                public func {{accessorSymbol}}(_ containerPtr: UnsafeMutableRawPointer, _ valuePtr: UnsafeRawPointer) {
                    let typedPtr = containerPtr.assumingMemoryBound(to: (any {{moduleQualifiedName}}).self)
                    var existential = typedPtr.pointee
                    let slice = valuePtr.load(as: SBW_Utf8Slice.self)
                    let str: String
                    if slice.len > 0 {
                        str = String(unsafeUninitializedCapacity: slice.len) { buf in
                            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
                            return slice.len
                        }
                    } else {
                        str = ""
                    }
                    existential.{{property.Name}} = str
                    typedPtr.pointee = existential
                }

                """);
        }
        else
        {
            // Blittable setter: typed pointee assignment
            var csharpType = GetCSharpTypeName(property.SwiftTypeSpec);
            var swiftType = GetSwiftPrimitiveType(csharpType);

            writer.WriteLines($$"""
                @_silgen_name("{{accessorSymbol}}")
                public func {{accessorSymbol}}(_ containerPtr: UnsafeMutableRawPointer, _ valuePtr: UnsafeRawPointer) {
                    let typedPtr = containerPtr.assumingMemoryBound(to: (any {{moduleQualifiedName}}).self)
                    var existential = typedPtr.pointee
                    existential.{{property.Name}} = valuePtr.load(as: {{swiftType}}.self)
                    typedPtr.pointee = existential
                }

                """);
        }
    }

    private void EmitMethodAccessor(SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl, string moduleQualifiedName, int index)
    {
        var protocolName = protocolDecl.Name;
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var isStringReturn = hasReturn && IsStringType(returnType!);

        var accessorSymbol = GetAccessorSymbol(protocolName, "method", method.Name, index);

        // Build Swift parameter list: containerPtr + one UnsafeRawPointer per param
        var swiftParams = new List<string> { "_ containerPtr: UnsafeRawPointer" };
        for (int i = 0; i < method.CSSignature.Count - 1; i++)
        {
            swiftParams.Add($"_ arg{i}Ptr: UnsafeRawPointer");
        }
        var swiftParamsString = string.Join(", ", swiftParams);

        // Build Swift return type
        var swiftReturnDecl = hasReturn ? " -> UnsafeMutableRawPointer" : "";

        writer.WriteLine($"@_silgen_name(\"{accessorSymbol}\")");
        writer.WriteLine($"public func {accessorSymbol}({swiftParamsString}){swiftReturnDecl} {{");
        writer.Indent++;

        // Load existential — use var for methods that may be mutating in the future
        writer.WriteLine($"let existential = containerPtr.load(as: (any {moduleQualifiedName}).self)");

        // Unmarshal parameters
        var callArgs = new List<string>();
        int argIdx = 0;
        foreach (var param in method.CSSignature.Skip(1))
        {
            EmitParameterUnmarshal(writer, param, argIdx);
            callArgs.Add($"arg{argIdx}");
            argIdx++;
        }

        // Build labeled args
        var labeledArgs = BuildLabeledArgs(method, callArgs);
        var callArgsString = string.Join(", ", labeledArgs);

        if (hasReturn)
        {
            if (isStringReturn)
            {
                // String return: convert to UTF-8 bytes via SBW_Utf8Slice
                writer.WriteLine($"let result: String = existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
                writer.WriteLine("let utf8 = Array(result.utf8)");
                writer.WriteLine("let bufferPtr = UnsafeMutablePointer<UInt8>.allocate(capacity: max(utf8.count, 1))");
                writer.WriteLine("if !utf8.isEmpty {");
                writer.Indent++;
                writer.WriteLine("utf8.withUnsafeBufferPointer { src in");
                writer.Indent++;
                writer.WriteLine("bufferPtr.initialize(from: src.baseAddress!, count: src.count)");
                writer.Indent--;
                writer.WriteLine("}");
                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine("let slicePtr = UnsafeMutablePointer<SBW_Utf8Slice>.allocate(capacity: 1)");
                writer.WriteLine("slicePtr.initialize(to: SBW_Utf8Slice(ptr: bufferPtr, len: utf8.count))");
                writer.WriteLine("return UnsafeMutableRawPointer(slicePtr)");
            }
            else
            {
                // Blittable return: direct pointer allocation
                var csharpReturnType = GetCSharpTypeName(returnType!);
                var swiftReturnType = GetSwiftPrimitiveType(csharpReturnType);
                writer.WriteLine($"let result = existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
                writer.WriteLine($"let ptr = UnsafeMutablePointer<{swiftReturnType}>.allocate(capacity: 1)");
                writer.WriteLine("ptr.initialize(to: result)");
                writer.WriteLine("return UnsafeMutableRawPointer(ptr)");
            }
        }
        else
        {
            writer.WriteLine($"existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();

        // Emit free function only for methods with return values
        if (hasReturn)
        {
            var freeSymbol = GetFreeSymbol(protocolName, "method", method.Name, index);

            if (isStringReturn)
            {
                // String return: free SBW_Utf8Slice + buffer
                writer.WriteLines($$"""
                    @_silgen_name("{{freeSymbol}}")
                    public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                        let slicePtr = ptr.assumingMemoryBound(to: SBW_Utf8Slice.self)
                        slicePtr.pointee.ptr.deallocate()
                        slicePtr.deinitialize(count: 1)
                        slicePtr.deallocate()
                    }

                    """);
            }
            else
            {
                // Blittable return: simple dealloc
                var csharpReturnType = GetCSharpTypeName(returnType!);
                var swiftReturnType = GetSwiftPrimitiveType(csharpReturnType);

                writer.WriteLines($$"""
                    @_silgen_name("{{freeSymbol}}")
                    public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                        ptr.assumingMemoryBound(to: {{swiftReturnType}}.self).deinitialize(count: 1)
                        ptr.deallocate()
                    }

                    """);
            }
        }
    }

    /// <summary>
    /// Emits a throwing witness dispatch accessor for blittable/String/void return types.
    /// Uses do/catch with error out-parameter pattern:
    /// - Value-returning: returns UnsafeMutableRawPointer? (nil = error), with free function
    /// - Void: returns Void with errorOut param, no free function
    /// </summary>
    private void EmitThrowingMethodAccessor(SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl, string moduleQualifiedName, int index)
    {
        var protocolName = protocolDecl.Name;
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var isStringReturn = hasReturn && IsStringType(returnType!);

        var accessorSymbol = GetAccessorSymbol(protocolName, "method", method.Name, index);

        // Build Swift parameter list: containerPtr + one UnsafeRawPointer per param + errorOut
        var swiftParams = new List<string> { "_ containerPtr: UnsafeRawPointer" };
        for (int i = 0; i < method.CSSignature.Count - 1; i++)
        {
            swiftParams.Add($"_ arg{i}Ptr: UnsafeRawPointer");
        }
        swiftParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeRawPointer?>");
        var swiftParamsString = string.Join(", ", swiftParams);

        // Return type: UnsafeMutableRawPointer? for value-returning (nil = error), Void for void
        var swiftReturnDecl = hasReturn ? " -> UnsafeMutableRawPointer?" : "";

        writer.WriteLine($"@_silgen_name(\"{accessorSymbol}\")");
        writer.WriteLine($"public func {accessorSymbol}({swiftParamsString}){swiftReturnDecl} {{");
        writer.Indent++;

        writer.WriteLine($"let existential = containerPtr.load(as: (any {moduleQualifiedName}).self)");

        // Unmarshal parameters
        var callArgs = new List<string>();
        int argIdx = 0;
        foreach (var param in method.CSSignature.Skip(1))
        {
            EmitParameterUnmarshal(writer, param, argIdx);
            callArgs.Add($"arg{argIdx}");
            argIdx++;
        }

        // Build labeled args
        var labeledArgs = BuildLabeledArgs(method, callArgs);
        var callArgsString = string.Join(", ", labeledArgs);

        // do/catch with error out-parameter
        writer.WriteLine("do {");
        writer.Indent++;

        if (hasReturn)
        {
            if (isStringReturn)
            {
                // String return: convert to UTF-8 bytes via SBW_Utf8Slice inside do block
                writer.WriteLine($"let result: String = try existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
                writer.WriteLine("let utf8 = Array(result.utf8)");
                writer.WriteLine("let bufferPtr = UnsafeMutablePointer<UInt8>.allocate(capacity: max(utf8.count, 1))");
                writer.WriteLine("if !utf8.isEmpty {");
                writer.Indent++;
                writer.WriteLine("utf8.withUnsafeBufferPointer { src in");
                writer.Indent++;
                writer.WriteLine("bufferPtr.initialize(from: src.baseAddress!, count: src.count)");
                writer.Indent--;
                writer.WriteLine("}");
                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine("let slicePtr = UnsafeMutablePointer<SBW_Utf8Slice>.allocate(capacity: 1)");
                writer.WriteLine("slicePtr.initialize(to: SBW_Utf8Slice(ptr: bufferPtr, len: utf8.count))");
                writer.WriteLine("return UnsafeMutableRawPointer(slicePtr)");
            }
            else
            {
                // Blittable return: direct pointer allocation
                var csharpReturnType = GetCSharpTypeName(returnType!);
                var swiftReturnType = GetSwiftPrimitiveType(csharpReturnType);
                writer.WriteLine($"let result = try existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
                writer.WriteLine($"let ptr = UnsafeMutablePointer<{swiftReturnType}>.allocate(capacity: 1)");
                writer.WriteLine("ptr.initialize(to: result)");
                writer.WriteLine("return UnsafeMutableRawPointer(ptr)");
            }
        }
        else
        {
            // Void return
            writer.WriteLine($"try existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
        }

        writer.Indent--;
        writer.WriteLine("} catch {");
        writer.Indent++;
        writer.WriteLine("errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()");
        if (hasReturn)
            writer.WriteLine("return nil");
        writer.Indent--;
        writer.WriteLine("}");

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();

        // Emit free function only for methods with return values
        if (hasReturn)
        {
            var freeSymbol = GetFreeSymbol(protocolName, "method", method.Name, index);

            if (isStringReturn)
            {
                writer.WriteLines($$"""
                    @_silgen_name("{{freeSymbol}}")
                    public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                        let slicePtr = ptr.assumingMemoryBound(to: SBW_Utf8Slice.self)
                        slicePtr.pointee.ptr.deallocate()
                        slicePtr.deinitialize(count: 1)
                        slicePtr.deallocate()
                    }

                    """);
            }
            else
            {
                var csharpReturnType = GetCSharpTypeName(returnType!);
                var swiftReturnType = GetSwiftPrimitiveType(csharpReturnType);

                writer.WriteLines($$"""
                    @_silgen_name("{{freeSymbol}}")
                    public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                        ptr.assumingMemoryBound(to: {{swiftReturnType}}.self).deinitialize(count: 1)
                        ptr.deallocate()
                    }

                    """);
            }
        }
    }

    /// <summary>
    /// Gets the Swift module-qualified existential type string for a return type.
    /// E.g., for a ProtocolListTypeSpec with "SmartCardIO.Card", returns "SmartCardIO.Card".
    /// Used in typed pointer declarations: <c>UnsafeMutablePointer&lt;any SmartCardIO.Card&gt;</c>.
    /// </summary>
    private string? GetSwiftExistentialTypeName(TypeSpec returnType)
    {
        var existentialHandler = new ExistentialHandler(_typeDatabase);

        // Handle Optional<any Protocol> — unwrap to get the inner protocol list
        ProtocolListTypeSpec? protocolList;
        if (existentialHandler.IsOptionalExistential(returnType))
            protocolList = existentialHandler.UnwrapOptionalExistential(returnType);
        else
            protocolList = existentialHandler.ToProtocolListTypeSpec(returnType);

        if (protocolList == null)
            return null;

        // Build module-qualified protocol names for the existential type
        var protocols = protocolList.Protocols.Keys
            .Where(p => !TypeDatabaseExtensions.IsObjCModuleType(p))
            .OrderBy(p => p.NameWithoutModule, StringComparer.Ordinal)
            .ToList();
        if (protocols.Count == 0)
            return null;

        if (protocols.Count == 1)
            return protocols[0].Name; // e.g., "SmartCardIO.Card"

        // Multi-protocol composition: "ProtocolA & ProtocolB"
        return string.Join(" & ", protocols.Select(p => p.Name));
    }

    private void EmitExistentialMethodAccessor(SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl, string moduleQualifiedName, int index)
    {
        var protocolName = protocolDecl.Name;
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var swiftExistentialType = GetSwiftExistentialTypeName(returnType!);
        if (swiftExistentialType == null)
            return; // Should not happen — IsExistentialDispatchable already validated

        // Detect optional existential return
        var existentialHandler = new ExistentialHandler(_typeDatabase);
        bool isOptionalReturn = existentialHandler.IsOptionalExistential(returnType!);

        var accessorSymbol = GetAccessorSymbol(protocolName, "method", method.Name, index);
        var freeSymbol = GetFreeSymbol(protocolName, "method", method.Name, index);

        // Build Swift parameter list: containerPtr + one UnsafeRawPointer per param
        // + errorOut if throwing
        var swiftParams = new List<string> { "_ containerPtr: UnsafeRawPointer" };
        for (int i = 0; i < method.CSSignature.Count - 1; i++)
        {
            swiftParams.Add($"_ arg{i}Ptr: UnsafeRawPointer");
        }
        if (method.Throws)
        {
            swiftParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeRawPointer?>");
        }
        var swiftParamsString = string.Join(", ", swiftParams);

        // Return type: UnsafeMutableRawPointer? for optional (nil = .none) and for throwing (nil = error)
        var swiftReturnDecl = (method.Throws || isOptionalReturn)
            ? " -> UnsafeMutableRawPointer?"
            : " -> UnsafeMutableRawPointer";

        writer.WriteLine($"@_silgen_name(\"{accessorSymbol}\")");
        writer.WriteLine($"public func {accessorSymbol}({swiftParamsString}){swiftReturnDecl} {{");
        writer.Indent++;

        // Load existential from container
        writer.WriteLine($"let existential = containerPtr.load(as: (any {moduleQualifiedName}).self)");

        // Unmarshal parameters
        var callArgs = new List<string>();
        int argIdx = 0;
        foreach (var param in method.CSSignature.Skip(1))
        {
            EmitParameterUnmarshal(writer, param, argIdx);
            callArgs.Add($"arg{argIdx}");
            argIdx++;
        }

        // Build labeled args
        var labeledArgs = BuildLabeledArgs(method, callArgs);
        var callArgsString = string.Join(", ", labeledArgs);

        var tryPrefix = method.Throws ? "try " : "";

        if (method.Throws)
        {
            // Throwing pattern: do/catch with error out-parameter
            // Note: throwing + optional is gated out in ClassifyMethodDispatch
            writer.WriteLine("do {");
            writer.Indent++;
            writer.WriteLine($"let result: any {swiftExistentialType} = {tryPrefix}existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
            writer.WriteLine($"let ptr = UnsafeMutablePointer<any {swiftExistentialType}>.allocate(capacity: 1)");
            writer.WriteLine("ptr.initialize(to: result)");
            writer.WriteLine("return UnsafeMutableRawPointer(ptr)");
            writer.Indent--;
            writer.WriteLine("} catch {");
            writer.Indent++;
            writer.WriteLine("errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()");
            writer.WriteLine("return nil");
            writer.Indent--;
            writer.WriteLine("}");
        }
        else if (isOptionalReturn)
        {
            // Optional existential pattern: if let unwrap, nil = .none
            writer.WriteLine($"let result: (any {swiftExistentialType})? = existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
            writer.WriteLine("if let unwrapped = result {");
            writer.Indent++;
            writer.WriteLine($"let ptr = UnsafeMutablePointer<any {swiftExistentialType}>.allocate(capacity: 1)");
            writer.WriteLine("ptr.initialize(to: unwrapped)");
            writer.WriteLine("return UnsafeMutableRawPointer(ptr)");
            writer.Indent--;
            writer.WriteLine("}");
            writer.WriteLine("return nil");
        }
        else
        {
            // Non-throwing, non-optional pattern: direct allocation
            writer.WriteLine($"let result: any {swiftExistentialType} = existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
            writer.WriteLine($"let ptr = UnsafeMutablePointer<any {swiftExistentialType}>.allocate(capacity: 1)");
            writer.WriteLine("ptr.initialize(to: result)");
            writer.WriteLine("return UnsafeMutableRawPointer(ptr)");
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();

        // Emit free function — typed deinitialize for ARC-safe cleanup
        // For optional: only called when result is non-nil
        writer.WriteLines($$"""
            @_silgen_name("{{freeSymbol}}")
            public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                ptr.assumingMemoryBound(to: (any {{swiftExistentialType}}).self).deinitialize(count: 1)
                ptr.deallocate()
            }

            """);
    }

    /// <summary>
    /// Emits a witness dispatch accessor for methods returning a Swift class.
    /// Non-throwing: returns UnsafeMutableRawPointer via Unmanaged.passRetained.
    /// Throwing: do/catch with errorOut, returns nil on error.
    /// No free function — C# SafeHandle handles ARC release.
    /// </summary>
    private void EmitClassReturnMethodAccessor(SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl, string moduleQualifiedName, int index)
    {
        var protocolName = protocolDecl.Name;
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var swiftConcreteType = GetSwiftConcreteTypeName(returnType!);
        if (swiftConcreteType == null)
            return;

        var accessorSymbol = GetAccessorSymbol(protocolName, "method", method.Name, index);

        // Build Swift parameter list
        var swiftParams = new List<string> { "_ containerPtr: UnsafeRawPointer" };
        for (int i = 0; i < method.CSSignature.Count - 1; i++)
        {
            swiftParams.Add($"_ arg{i}Ptr: UnsafeRawPointer");
        }
        if (method.Throws)
        {
            swiftParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeRawPointer?>");
        }
        var swiftParamsString = string.Join(", ", swiftParams);

        var swiftReturnDecl = method.Throws
            ? " -> UnsafeMutableRawPointer?"
            : " -> UnsafeMutableRawPointer";

        writer.WriteLine($"@_silgen_name(\"{accessorSymbol}\")");
        writer.WriteLine($"public func {accessorSymbol}({swiftParamsString}){swiftReturnDecl} {{");
        writer.Indent++;

        writer.WriteLine($"let existential = containerPtr.load(as: (any {moduleQualifiedName}).self)");

        // Unmarshal parameters
        var callArgs = new List<string>();
        int argIdx = 0;
        foreach (var param in method.CSSignature.Skip(1))
        {
            EmitParameterUnmarshal(writer, param, argIdx);
            callArgs.Add($"arg{argIdx}");
            argIdx++;
        }

        // Build labeled args
        var labeledArgs = BuildLabeledArgs(method, callArgs);
        var callArgsString = string.Join(", ", labeledArgs);

        var tryPrefix = method.Throws ? "try " : "";

        if (method.Throws)
        {
            writer.WriteLine("do {");
            writer.Indent++;
            writer.WriteLine($"let result = {tryPrefix}existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
            writer.WriteLine("return Unmanaged.passRetained(result as AnyObject).toOpaque()");
            writer.Indent--;
            writer.WriteLine("} catch {");
            writer.Indent++;
            writer.WriteLine("errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()");
            writer.WriteLine("return nil");
            writer.Indent--;
            writer.WriteLine("}");
        }
        else
        {
            writer.WriteLine($"let result = existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
            writer.WriteLine("return Unmanaged.passRetained(result as AnyObject).toOpaque()");
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
        // No free function — SafeHandle handles ARC release
    }

    /// <summary>
    /// Emits a witness dispatch accessor for methods returning a non-frozen struct.
    /// Caller provides resultBuf; Swift writes into it via assumingMemoryBound(to:).initialize(to:).
    /// Throwing: do/catch with errorOut, void return.
    /// No free function — SafeHandle owns the buffer.
    /// </summary>
    private void EmitStructReturnMethodAccessor(SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl, string moduleQualifiedName, int index)
    {
        var protocolName = protocolDecl.Name;
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var swiftConcreteType = GetSwiftConcreteTypeName(returnType!);
        if (swiftConcreteType == null)
            return;

        var accessorSymbol = GetAccessorSymbol(protocolName, "method", method.Name, index);

        // Build Swift parameter list: containerPtr + resultBuf + per-param + errorOut
        var swiftParams = new List<string> { "_ containerPtr: UnsafeRawPointer", "_ resultBuf: UnsafeMutableRawPointer" };
        for (int i = 0; i < method.CSSignature.Count - 1; i++)
        {
            swiftParams.Add($"_ arg{i}Ptr: UnsafeRawPointer");
        }
        if (method.Throws)
        {
            swiftParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeRawPointer?>");
        }
        var swiftParamsString = string.Join(", ", swiftParams);

        // Struct return always returns void (result written into buffer)
        writer.WriteLine($"@_silgen_name(\"{accessorSymbol}\")");
        writer.WriteLine($"public func {accessorSymbol}({swiftParamsString}) {{");
        writer.Indent++;

        writer.WriteLine($"let existential = containerPtr.load(as: (any {moduleQualifiedName}).self)");

        // Unmarshal parameters
        var callArgs = new List<string>();
        int argIdx = 0;
        foreach (var param in method.CSSignature.Skip(1))
        {
            EmitParameterUnmarshal(writer, param, argIdx);
            callArgs.Add($"arg{argIdx}");
            argIdx++;
        }

        // Build labeled args
        var labeledArgs = BuildLabeledArgs(method, callArgs);
        var callArgsString = string.Join(", ", labeledArgs);

        var tryPrefix = method.Throws ? "try " : "";

        if (method.Throws)
        {
            writer.WriteLine("do {");
            writer.Indent++;
            writer.WriteLine($"let result = {tryPrefix}existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
            writer.WriteLine($"resultBuf.assumingMemoryBound(to: {swiftConcreteType}.self).initialize(to: result)");
            writer.Indent--;
            writer.WriteLine("} catch {");
            writer.Indent++;
            writer.WriteLine("errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()");
            writer.Indent--;
            writer.WriteLine("}");
        }
        else
        {
            writer.WriteLine($"let result = existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
            writer.WriteLine($"resultBuf.assumingMemoryBound(to: {swiftConcreteType}.self).initialize(to: result)");
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
        // No free function — SafeHandle owns the buffer
    }

    /// <summary>
    /// Emits a property getter accessor for class return types.
    /// Returns UnsafeMutableRawPointer via Unmanaged.passRetained.
    /// </summary>
    private void EmitClassReturnPropertyGetterAccessor(SwiftWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, string moduleQualifiedName)
    {
        var protocolName = protocolDecl.Name;
        var accessorSymbol = GetAccessorSymbol(protocolName, "get", property.Name, 0);

        writer.WriteLines($$"""
            @_silgen_name("{{accessorSymbol}}")
            public func {{accessorSymbol}}(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
                let existential = containerPtr.load(as: (any {{moduleQualifiedName}}).self)
                let result = existential.{{property.Name}}
                return Unmanaged.passRetained(result as AnyObject).toOpaque()
            }

            """);
        // No free function — SafeHandle handles ARC release
    }

    /// <summary>
    /// Emits a property getter accessor for struct return types.
    /// Caller provides resultBuf; Swift writes into it.
    /// </summary>
    private void EmitStructReturnPropertyGetterAccessor(SwiftWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, string moduleQualifiedName)
    {
        var protocolName = protocolDecl.Name;
        var swiftConcreteType = GetSwiftConcreteTypeName(property.SwiftTypeSpec);
        if (swiftConcreteType == null)
            return;

        var accessorSymbol = GetAccessorSymbol(protocolName, "get", property.Name, 0);

        writer.WriteLines($$"""
            @_silgen_name("{{accessorSymbol}}")
            public func {{accessorSymbol}}(_ containerPtr: UnsafeRawPointer, _ resultBuf: UnsafeMutableRawPointer) {
                let existential = containerPtr.load(as: (any {{moduleQualifiedName}}).self)
                let result = existential.{{property.Name}}
                resultBuf.assumingMemoryBound(to: {{swiftConcreteType}}.self).initialize(to: result)
            }

            """);
        // No free function — SafeHandle owns the buffer
    }

    /// <summary>
    /// Emits a property getter accessor for collection return types (Array, Dictionary, Set).
    /// Uses heap-allocated pointer pattern: allocate → initialize → return UnsafeMutableRawPointer.
    /// Also emits a free function for typed deinitialize + deallocate.
    /// </summary>
    private void EmitCollectionReturnPropertyGetterAccessor(SwiftWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, string moduleQualifiedName)
    {
        var protocolName = protocolDecl.Name;
        var swiftCollectionType = GetSwiftCollectionTypeString(property.SwiftTypeSpec);
        if (swiftCollectionType == null)
            return;

        var accessorSymbol = GetAccessorSymbol(protocolName, "get", property.Name, 0);
        var freeSymbol = GetFreeSymbol(protocolName, "get", property.Name, 0);

        writer.WriteLines($$"""
            @_silgen_name("{{accessorSymbol}}")
            public func {{accessorSymbol}}(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
                let existential = containerPtr.load(as: (any {{moduleQualifiedName}}).self)
                let result = existential.{{property.Name}}
                let ptr = UnsafeMutablePointer<{{swiftCollectionType}}>.allocate(capacity: 1)
                ptr.initialize(to: result)
                return UnsafeMutableRawPointer(ptr)
            }

            @_silgen_name("{{freeSymbol}}")
            public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                ptr.assumingMemoryBound(to: {{swiftCollectionType}}.self).deinitialize(count: 1)
                ptr.deallocate()
            }

            """);
    }

    /// <summary>
    /// Emits a witness dispatch accessor for methods returning a collection type.
    /// Non-throwing: allocate → initialize → return UnsafeMutableRawPointer.
    /// Throwing: do/catch with errorOut, returns nil on error.
    /// Also emits a free function for typed deinitialize + deallocate.
    /// </summary>
    private void EmitCollectionReturnMethodAccessor(SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl, string moduleQualifiedName, int index)
    {
        var protocolName = protocolDecl.Name;
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var swiftCollectionType = GetSwiftCollectionTypeString(returnType!);
        if (swiftCollectionType == null)
            return;

        var accessorSymbol = GetAccessorSymbol(protocolName, "method", method.Name, index);
        var freeSymbol = GetFreeSymbol(protocolName, "method", method.Name, index);

        // Build Swift parameter list: containerPtr + one UnsafeRawPointer per param
        // + errorOut if throwing
        var swiftParams = new List<string> { "_ containerPtr: UnsafeRawPointer" };
        for (int i = 0; i < method.CSSignature.Count - 1; i++)
        {
            swiftParams.Add($"_ arg{i}Ptr: UnsafeRawPointer");
        }
        if (method.Throws)
        {
            swiftParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeRawPointer?>");
        }
        var swiftParamsString = string.Join(", ", swiftParams);

        // Return type: UnsafeMutableRawPointer (nullable if throwing — nil means error)
        var swiftReturnDecl = method.Throws
            ? " -> UnsafeMutableRawPointer?"
            : " -> UnsafeMutableRawPointer";

        writer.WriteLine($"@_silgen_name(\"{accessorSymbol}\")");
        writer.WriteLine($"public func {accessorSymbol}({swiftParamsString}){swiftReturnDecl} {{");
        writer.Indent++;

        // Load existential from container
        writer.WriteLine($"let existential = containerPtr.load(as: (any {moduleQualifiedName}).self)");

        // Unmarshal parameters
        var callArgs = new List<string>();
        int argIdx = 0;
        foreach (var param in method.CSSignature.Skip(1))
        {
            EmitParameterUnmarshal(writer, param, argIdx);
            callArgs.Add($"arg{argIdx}");
            argIdx++;
        }

        // Build labeled args
        var labeledArgs = BuildLabeledArgs(method, callArgs);
        var callArgsString = string.Join(", ", labeledArgs);

        var tryPrefix = method.Throws ? "try " : "";

        if (method.Throws)
        {
            // Throwing pattern: do/catch with error out-parameter
            writer.WriteLine("do {");
            writer.Indent++;
            writer.WriteLine($"let result: {swiftCollectionType} = {tryPrefix}existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
            writer.WriteLine($"let ptr = UnsafeMutablePointer<{swiftCollectionType}>.allocate(capacity: 1)");
            writer.WriteLine("ptr.initialize(to: result)");
            writer.WriteLine("return UnsafeMutableRawPointer(ptr)");
            writer.Indent--;
            writer.WriteLine("} catch {");
            writer.Indent++;
            writer.WriteLine("errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()");
            writer.WriteLine("return nil");
            writer.Indent--;
            writer.WriteLine("}");
        }
        else
        {
            // Non-throwing pattern: direct allocation
            writer.WriteLine($"let result: {swiftCollectionType} = existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
            writer.WriteLine($"let ptr = UnsafeMutablePointer<{swiftCollectionType}>.allocate(capacity: 1)");
            writer.WriteLine("ptr.initialize(to: result)");
            writer.WriteLine("return UnsafeMutableRawPointer(ptr)");
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();

        // Emit free function — typed deinitialize for memory-safe cleanup
        writer.WriteLines($$"""
            @_silgen_name("{{freeSymbol}}")
            public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                ptr.assumingMemoryBound(to: {{swiftCollectionType}}.self).deinitialize(count: 1)
                ptr.deallocate()
            }

            """);
    }

    /// <summary>
    /// Emits parameter unmarshalling for a single argument (shared by all accessor types).
    /// Supports String (UTF-8 decode), class (Unmanaged.fromOpaque), struct (assumingMemoryBound),
    /// and blittable (direct load).
    /// </summary>
    private void EmitParameterUnmarshal(SwiftWriter writer, ArgumentDecl param, int argIdx)
    {
        if (IsStringType(param.SwiftTypeSpec))
        {
            // String parameter: decode SBW_Utf8Slice → Swift String
            writer.WriteLine($"let arg{argIdx}Slice = arg{argIdx}Ptr.load(as: SBW_Utf8Slice.self)");
            writer.WriteLine($"let arg{argIdx}: String");
            writer.WriteLine($"if arg{argIdx}Slice.len > 0 {{");
            writer.Indent++;
            writer.WriteLine($"arg{argIdx} = String(unsafeUninitializedCapacity: arg{argIdx}Slice.len) {{ buf in");
            writer.Indent++;
            writer.WriteLine($"UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: arg{argIdx}Slice.ptr, byteCount: arg{argIdx}Slice.len)");
            writer.WriteLine($"return arg{argIdx}Slice.len");
            writer.Indent--;
            writer.WriteLine("}");
            writer.Indent--;
            writer.WriteLine("} else {");
            writer.Indent++;
            writer.WriteLine($"arg{argIdx} = \"\"");
            writer.Indent--;
            writer.WriteLine("}");
        }
        else if (IsSwiftClassType(param.SwiftTypeSpec))
        {
            // Class parameter: load raw pointer, then Unmanaged<T>.fromOpaque().takeUnretainedValue()
            var swiftTypeName = GetSwiftConcreteTypeName(param.SwiftTypeSpec);
            writer.WriteLine($"let rawPtr{argIdx} = arg{argIdx}Ptr.load(as: UnsafeMutableRawPointer.self)");
            writer.WriteLine($"let arg{argIdx} = Unmanaged<{swiftTypeName}>.fromOpaque(rawPtr{argIdx}).takeUnretainedValue()");
        }
        else if (IsIndirectStructType(param.SwiftTypeSpec))
        {
            // Struct parameter: load raw pointer, then assumingMemoryBound(to:).pointee
            var swiftTypeName = GetSwiftConcreteTypeName(param.SwiftTypeSpec);
            writer.WriteLine($"let rawPtr{argIdx} = arg{argIdx}Ptr.load(as: UnsafeMutableRawPointer.self)");
            writer.WriteLine($"let arg{argIdx} = rawPtr{argIdx}.assumingMemoryBound(to: {swiftTypeName}.self).pointee");
        }
        else
        {
            // Blittable parameter: direct load
            var csharpType = GetCSharpTypeName(param.SwiftTypeSpec);
            var swiftType = GetSwiftPrimitiveType(csharpType);
            writer.WriteLine($"let arg{argIdx} = arg{argIdx}Ptr.load(as: {swiftType}.self)");
        }
    }

    /// <summary>
    /// Builds labeled Swift argument list from method signature and call args.
    /// </summary>
    private static List<string> BuildLabeledArgs(MethodDecl method, List<string> callArgs)
    {
        var labeledArgs = new List<string>();
        int argIdx = 0;
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var param = method.CSSignature[i];
            var label = GetSwiftParameterLabel(param);
            var argRef = callArgs[argIdx];
            labeledArgs.Add(label == "_" ? argRef : $"{label}: {argRef}");
            argIdx++;
        }
        return labeledArgs;
    }

    /// <summary>
    /// Gets the Swift parameter label for a method argument.
    /// Mirrors EveryProtocolEmitter.GetSwiftParameterLabel logic.
    /// </summary>
    private static string GetSwiftParameterLabel(ArgumentDecl param)
    {
        if (string.IsNullOrEmpty(param.Name) || param.Name == "_" || IsGeneratedArgName(param.Name))
            return "_";

        // Strip C# keyword prefix
        if (param.Name.Length > 1 && param.Name[0] == '_')
        {
            var possibleKeyword = param.Name.Substring(1);
            if (CSharpKeywords.Contains(possibleKeyword))
                return possibleKeyword;
        }

        return param.Name;
    }

    private static bool IsGeneratedArgName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !name.StartsWith("arg"))
            return false;
        return name.Length > 3 && name.Substring(3).All(char.IsDigit);
    }

    private static readonly HashSet<string> CSharpKeywords = new()
    {
        "for", "in", "is", "as", "if", "else", "do", "while", "return",
        "break", "continue", "switch", "case", "default", "try", "catch",
        "throw", "new", "this", "base", "null", "true", "false", "class",
        "struct", "enum", "interface", "public", "private", "protected",
        "internal", "static", "readonly", "const", "override", "virtual",
        "abstract", "sealed", "async", "await", "var", "object", "string",
        "int", "long", "float", "double", "bool", "void", "ref", "out",
        "params", "event", "delegate", "operator", "implicit", "explicit",
        "where", "get", "set", "value", "partial", "using", "namespace"
    };

    private static string GetMethodKey(MethodDecl method)
    {
        return method.Name + "(" + string.Join(",", method.CSSignature.Skip(1).Select(p => p.SwiftTypeSpec?.ToString() ?? "")) + ")";
    }

    #endregion
}
