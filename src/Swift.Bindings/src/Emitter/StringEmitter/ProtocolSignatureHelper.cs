// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Mode flags controlling how <see cref="ProtocolSignatureHelper.ProjectTypeToCSharp"/> resolves types.
/// Consolidates the behavioral differences between proxy, interface, and property contexts.
/// </summary>
[Flags]
internal enum TypeResolutionMode
{
    /// <summary>Default: returns PublicType from factory projection. Used by interface signatures.</summary>
    Default = 0,

    /// <summary>Returns MarshalFromSwiftType instead of PublicType (for ABI marshalling in proxy receivers).</summary>
    AbiMarshalling = 1,

    /// <summary>Applies NativeIntOverloadEmitter.NarrowNativeIntType() to the result (property interface context).</summary>
    NarrowNativeInt = 2,

    /// <summary>Includes ExistentialHandler fallback when factory can't resolve an existential (proxy context).</summary>
    ExistentialFallback = 4,

    /// <summary>Include tuple element labels in tuple type output (proxy context).</summary>
    IncludeTupleLabels = 8,
}

/// <summary>
/// Shared signature key generation for protocol member matching.
/// Used by both ProtocolHandler (interface emission) and ProtocolConformanceValidator.
/// </summary>
internal static class ProtocolSignatureHelper
{
    /// <summary>
    /// Creates a unique signature key for a method based on name and parameter types.
    /// </summary>
    public static string GetMethodSignatureKey(MethodDecl methodDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext = null)
    {
        var paramTypes = new List<string>();
        // Skip first element (return type) in CSSignature
        for (int i = 1; i < methodDecl.CSSignature.Count; i++)
        {
            var arg = methodDecl.CSSignature[i];
            try
            {
                // Handle associated type references for protocols
                if (arg.SwiftTypeSpec is AssociatedTypeReferenceSpec assocRef)
                {
                    paramTypes.Add(MapAssociatedTypeToGenericParam(assocRef, protocolContext));
                }
                else
                {
                    var typeRecord = typeDatabase.GetTypeRecordOrAnyType(arg.SwiftTypeSpec);
                    paramTypes.Add(typeRecord.CSharpTypeName.FullyQualifiedName);
                }
            }
            catch
            {
                // For generic type parameters or other unsupported types,
                // use the string representation of the type spec
                paramTypes.Add(arg.SwiftTypeSpec?.ToString() ?? "unknown");
            }
        }
        return $"{methodDecl.Name}({string.Join(",", paramTypes)})";
    }

    /// <summary>
    /// Creates a unique signature key for a subscript based on index parameter types.
    /// </summary>
    public static string GetSubscriptSignatureKey(SubscriptDecl subscriptDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext = null)
    {
        var paramTypes = new List<string>();
        foreach (var param in subscriptDecl.IndexParameters)
        {
            try
            {
                // Handle associated type references for protocols
                if (param.SwiftTypeSpec is AssociatedTypeReferenceSpec assocRef)
                {
                    paramTypes.Add(MapAssociatedTypeToGenericParam(assocRef, protocolContext));
                }
                else if (param.SwiftTypeSpec != null)
                {
                    var typeRecord = typeDatabase.GetTypeRecordOrAnyType(param.SwiftTypeSpec);
                    paramTypes.Add(typeRecord.CSharpTypeName.FullyQualifiedName);
                }
                else
                {
                    paramTypes.Add("unknown");
                }
            }
            catch
            {
                // For generic type parameters or other unsupported types,
                // use the string representation of the type spec
                paramTypes.Add(param.SwiftTypeSpec?.ToString() ?? "unknown");
            }
        }
        return $"subscript[{string.Join(",", paramTypes)}]";
    }

    /// <summary>
    /// Creates a projected C# method signature key for dedup purposes.
    /// Two methods that would produce the same C# interface signature get the same key.
    /// Key format: "MethodName(paramType1,paramType2,...)" — no return type (C# overload identity).
    /// </summary>
    public static string GetProjectedCSharpMethodKey(MethodDecl methodDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext = null)
    {
        // Compute the public method name the same way EmitInterfaceMethod does
        var returnTypeSpec = methodDecl.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        bool hasReturnValue = returnTypeSpec != null && !returnTypeSpec.IsEmptyTuple;
        // Capture hasReturnValue BEFORE async conversion turns void→Task
        var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(methodDecl);
        var methodName = NameProvider.GetPublicMethodName(methodDecl.Name, methodDecl.IsAsync, hasReturnValue: hasReturnValue, isSelfReturning: isSelfReturning,
            parameterCount: methodDecl.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple));

        var paramTypes = new List<string>();
        for (int i = 1; i < methodDecl.CSSignature.Count; i++)
        {
            var arg = methodDecl.CSSignature[i];
            // Debug params (#file, #line, etc.) are stripped from the public signature
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            // Empty tuple () params are stripped from the C# signature (zero-sized Void)
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;
            var projected = ProjectTypeToCSharp(arg.SwiftTypeSpec, typeDatabase, protocolContext, isParameter: true);
            projected = NormalizeParamTypeForOverloadIdentity(projected, arg.SwiftTypeSpec, typeDatabase);
            paramTypes.Add(projected);
        }
        return $"{methodName}({string.Join(",", paramTypes)})";
    }

    /// <summary>
    /// Projects a Swift TypeSpec to the C# type name for protocol contexts.
    /// This is the single consolidated entry point for type resolution across proxy,
    /// interface, and property contexts. Use <see cref="TypeResolutionMode"/> flags
    /// to control context-specific behavior.
    /// </summary>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <param name="typeDatabase">Type database for lookups.</param>
    /// <param name="protocolContext">Protocol context for associated type resolution and Self-requirement detection.</param>
    /// <param name="isParameter">True for parameter types (arrays → IEnumerable), false for return types (arrays → IReadOnlyList).</param>
    /// <param name="genericContext">Explicit generic context override. When null, auto-computed from protocolContext
    /// (ForProtocolSelf when HasSelfRequirement, otherwise Empty).</param>
    /// <param name="mode">Mode flags controlling resolution behavior. Default is interface context.</param>
    public static string ProjectTypeToCSharp(
        TypeSpec typeSpec,
        ITypeDatabase typeDatabase,
        ProtocolDecl? protocolContext = null,
        bool isParameter = false,
        GenericContext? genericContext = null,
        TypeResolutionMode mode = TypeResolutionMode.Default)
    {
        bool forAbiMarshalling = mode.HasFlag(TypeResolutionMode.AbiMarshalling);
        bool narrowNativeInt = mode.HasFlag(TypeResolutionMode.NarrowNativeInt);
        bool existentialFallback = mode.HasFlag(TypeResolutionMode.ExistentialFallback);
        bool includeTupleLabels = mode.HasFlag(TypeResolutionMode.IncludeTupleLabels);

        // Mode for recursive calls: strip AbiMarshalling (nested types always use public type)
        // and NarrowNativeInt (applied once at the top level only).
        var recurMode = mode & ~(TypeResolutionMode.AbiMarshalling | TypeResolutionMode.NarrowNativeInt);

        // Associated type references → generic param (factory doesn't handle these)
        if (typeSpec is AssociatedTypeReferenceSpec assocRef)
            return MaybeNarrow(MapAssociatedTypeToGenericParam(assocRef, protocolContext), narrowNativeInt);

        // Resolve generic context: explicit override, or auto-compute from protocolContext
        var effectiveGenericContext = genericContext
            ?? (protocolContext?.HasSelfRequirement == true
                ? GenericContext.ForProtocolSelf()
                : GenericContext.Empty);

        // Factory-first: handles existentials, closures, tuples, containers (Array, Dict, Optional),
        // string, bool, ObjC bridged, simple enum, native remapped, class, non-frozen, blittable
        var factory = new TypeProjectionFactory();
        var projection = factory.Project(typeSpec, new ProjectionContext
        {
            TypeDatabase = typeDatabase,
            IsParameter = isParameter,
            GenericContext = effectiveGenericContext
        });
        if (projection != null)
        {
            var result = forAbiMarshalling ? projection.MarshalFromSwiftType : projection.PublicType;
            return MaybeNarrow(result, narrowNativeInt);
        }

        // Closure fallback when factory can't fully resolve (e.g., inner types not in TypeDatabase)
        if (typeSpec is ClosureTypeSpec closureType)
        {
            var args = closureType.EachArgument()
                .Select(a => ProjectTypeToCSharp(a, typeDatabase, protocolContext, isParameter: true, genericContext, recurMode))
                .ToList();
            bool hasReturn = !closureType.ReturnType.IsEmptyTuple;

            string closureResult;
            if (!hasReturn)
            {
                closureResult = args.Count == 0 ? "Action" : $"Action<{string.Join(", ", args)}>";
            }
            else
            {
                // Closure return types use isParameter:false (return position) so arrays project
                // as IReadOnlyList<T>, matching ProtocolHandler.GetClosureCSharpType for interface parity.
                var retName = ProjectTypeToCSharp(closureType.ReturnType, typeDatabase, protocolContext, isParameter: false, genericContext, recurMode);
                closureResult = args.Count == 0 ? $"Func<{retName}>" : $"Func<{string.Join(", ", args)}, {retName}>";
            }
            return MaybeNarrow(closureResult, narrowNativeInt);
        }

        // Tuple fallback
        if (typeSpec is TupleTypeSpec tupleType)
        {
            if (tupleType.IsEmptyTuple) return "void";

            var elements = new List<string>();
            foreach (var element in tupleType.Elements)
            {
                var typeName = ProjectTypeToCSharp(element, typeDatabase, protocolContext, isParameter, genericContext, recurMode);
                if (includeTupleLabels && !string.IsNullOrEmpty(element.TypeLabel))
                    elements.Add($"{typeName} {element.TypeLabel}");
                else
                    elements.Add(typeName);
            }
            return MaybeNarrow($"({string.Join(", ", elements)})", narrowNativeInt);
        }

        // Existential fallback: when factory can't resolve but ExistentialHandler can.
        // Only used in proxy context where the factory may not cover all existential patterns.
        if (existentialFallback)
        {
            var existentialHandler = new ExistentialHandler(typeDatabase);
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null)
                {
                    if (existentialHandler.TryGetWellKnownProtocolType(protocolList, out var wellKnownType))
                        return MaybeNarrow(wellKnownType, narrowNativeInt);

                    if (existentialHandler.IsSupportedExistential(protocolList))
                    {
                        var existentialResult = forAbiMarshalling
                            ? existentialHandler.GetCSharpExistentialType(protocolList)
                            : existentialHandler.GetPublicExistentialType(protocolList);
                        return MaybeNarrow(existentialResult, narrowNativeInt);
                    }
                }
            }
        }

        // Bound generic fallback: produce full type name with generic args
        // (e.g., BatchedCollection<Swift.AnyType> for unknown inner types).
        if (typeSpec is NamedTypeSpec boundGeneric && boundGeneric.ContainsGenericParameters)
        {
            var bgh = new BoundGenericsHandler(typeDatabase);
            return MaybeNarrow(bgh.TranslateBoundGenericTypeToCSharp(typeSpec, effectiveGenericContext), narrowNativeInt);
        }

        // Final fallback: raw type record lookup
        var record = typeDatabase.GetTypeRecordOrAnyType(typeSpec);
        return MaybeNarrow(record.CSharpTypeName.FullyQualifiedName, narrowNativeInt);
    }

    /// <summary>
    /// Conditionally applies NativeInt narrowing to a type name.
    /// </summary>
    private static string MaybeNarrow(string typeName, bool narrow)
        => narrow ? NativeIntOverloadEmitter.NarrowNativeIntType(typeName) : typeName;

    /// <summary>
    /// Normalizes a projected C# parameter type for overload identity comparison.
    /// In C#, nullability annotations don't affect overload resolution for reference types —
    /// Optional&lt;Class&gt; and Class resolve to the same overload. This strips the trailing '?'
    /// for reference-like types so that emission dedup correctly detects collisions.
    /// </summary>
    public static string NormalizeParamTypeForOverloadIdentity(string projectedType, TypeSpec swiftTypeSpec, ITypeDatabase typeDatabase)
    {
        if (swiftTypeSpec is NamedTypeSpec optNamed && optNamed.Name == "Swift.Optional" &&
            optNamed.GenericParameters.Count == 1)
        {
            var innerRecord = typeDatabase.GetTypeRecordOrAnyType(optNamed.GenericParameters[0]);
            if (innerRecord.Kind == TypeRecordKind.Class ||
                innerRecord.Kind == TypeRecordKind.Protocol ||
                innerRecord.Kind == TypeRecordKind.Existential ||
                (innerRecord.Kind == TypeRecordKind.Enum && !innerRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum)) ||
                // Non-frozen structs are emitted as C# classes (ClassWithOpaquePayload),
                // making nullable annotation irrelevant for overload resolution.
                (innerRecord.Kind == TypeRecordKind.Struct && !innerRecord.Flags.HasFlag(TypeRecordFlags.Frozen)) ||
                // Frozen structs with reference-type fields are emitted as C# classes (ClassWithBufferStruct),
                // so nullable annotation is also irrelevant for overload resolution.
                MarshallingHelpers.IsFrozenStructProjectedAsClass(innerRecord))
                return projectedType.TrimEnd('?');

            // Swift value types that project to C# reference types (e.g., Swift.String → string).
            // In C#, Optional<String> and String both map to 'string' / 'string?' which are the
            // same CLR type (nullability is annotation-only for reference types).
            if (projectedType.EndsWith("?") && IsCSharpReferenceTypeProjection(projectedType.TrimEnd('?')))
                return projectedType.TrimEnd('?');
        }

        return projectedType;
    }

    /// <summary>
    /// Checks if a projected C# type name is a reference type in the CLR,
    /// where nullability is annotation-only and doesn't affect overload resolution.
    /// </summary>
    private static bool IsCSharpReferenceTypeProjection(string projectedType) =>
        projectedType is "string" or "object";

    /// <summary>
    /// Maps an associated type reference to a C# generic parameter name.
    /// For example, "Self.Element" in a protocol with associated type "Element" becomes "TElement".
    /// </summary>
    internal static string MapAssociatedTypeToGenericParam(AssociatedTypeReferenceSpec assocRef, ProtocolDecl? protocolDecl)
    {
        // Handle Self reference
        if (assocRef.BaseType == "Self" && string.IsNullOrEmpty(assocRef.AssociatedTypeName))
        {
            return "TSelf";
        }

        // Handle associated type reference like "Self.Element"
        if (!string.IsNullOrEmpty(assocRef.AssociatedTypeName))
        {
            // Map "Element" -> "TElement"
            return $"T{assocRef.AssociatedTypeName}";
        }

        // Fallback for generic parameter like τ_0_0
        if (assocRef.BaseType.StartsWith("τ_") || assocRef.BaseType.StartsWith("T"))
        {
            // Already a generic param reference
            return assocRef.BaseType;
        }

        return "object";
    }
}
