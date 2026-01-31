// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Helper class for handling closure types in Swift bindings.
/// It provides methods to detect closure arguments and translate them to appropriate
/// C# delegate types or function pointers.
/// </summary>
public class ClosureHandler
{
    private readonly ITypeDatabase _typeDatabase;
    private readonly TupleHandler _tupleHandler;
    private readonly ExistentialHandler _existentialHandler;

    public ClosureHandler(ITypeDatabase typeDatabase)
    {
        _typeDatabase = typeDatabase;
        _tupleHandler = new TupleHandler(typeDatabase);
        _existentialHandler = new ExistentialHandler(typeDatabase);
    }

    /// <summary>
    /// Determines whether the specified argument declaration represents a closure type.
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns><c>true</c> if the argument's Swift type is a closure; otherwise, <c>false</c>.</returns>
    public bool IsClosure(ArgumentDecl argumentDecl) =>
        argumentDecl.SwiftTypeSpec is ClosureTypeSpec;

    /// <summary>
    /// Determines whether the specified property declaration represents a closure type.
    /// </summary>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <returns><c>true</c> if the property's Swift type is a closure; otherwise, <c>false</c>.</returns>
    public bool IsClosure(PropertyDecl propertyDecl) =>
        propertyDecl.SwiftTypeSpec is ClosureTypeSpec;

    /// <summary>
    /// Gets the ClosureTypeSpec from an argument declaration.
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns>The ClosureTypeSpec if the argument is a closure; otherwise, null.</returns>
    public ClosureTypeSpec? GetClosureTypeSpec(ArgumentDecl argumentDecl) =>
        argumentDecl.SwiftTypeSpec as ClosureTypeSpec;

    /// <summary>
    /// Gets the ClosureTypeSpec from a property declaration.
    /// </summary>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <returns>The ClosureTypeSpec if the property is a closure; otherwise, null.</returns>
    public ClosureTypeSpec? GetClosureTypeSpec(PropertyDecl propertyDecl) =>
        propertyDecl.SwiftTypeSpec as ClosureTypeSpec;

    /// <summary>
    /// Determines whether the closure has @convention(c) attribute.
    /// @convention(c) closures are simple C function pointers with no context.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns><c>true</c> if the closure is @convention(c); otherwise, <c>false</c>.</returns>
    public bool IsConventionC(ClosureTypeSpec closureTypeSpec)
    {
        if (!closureTypeSpec.HasAttributes)
            return false;

        return closureTypeSpec.Attributes.Exists(attr =>
            attr.Name == "convention" &&
            attr.Parameters.Count > 0 &&
            attr.Parameters[0] == "c");
    }

    /// <summary>
    /// Determines whether the closure is a supported type.
    /// Currently supports:
    /// - @convention(c) closures (Phase 1)
    /// - Escaping closures with concrete types (Phase 2)
    /// - Async closures (Phase 3) - mapped to Func&lt;..., Task&gt; or Func&lt;..., Task&lt;T&gt;&gt;
    /// All must be non-throwing and have concrete (non-generic) argument/return types.
    /// Return types must be primitive/blittable (complex return type marshalling not yet implemented).
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns><c>true</c> if the closure is supported; otherwise, <c>false</c>.</returns>
    public bool IsSupportedClosure(ClosureTypeSpec closureTypeSpec)
    {
        // Exclude throwing closures
        if (closureTypeSpec.Throws)
            return false;

        // Async+throwing closures are not supported (need complex error handling)
        // Plain async closures are supported via Task-based delegates

        // Note: We no longer check for explicit @escaping attribute here.
        // All closures in public Swift APIs are either @convention(c) or @escaping by definition,
        // since non-escaping closures cannot cross API boundaries. The ABI JSON doesn't include
        // these attributes in the printedName field, so we treat all non-async, non-throwing
        // closures as supported (either @convention(c) or implicitly @escaping).

        // Check that all argument types are supported
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (!IsSupportedClosureParameterType(arg))
                return false;
        }

        // Check that return type is supported
        if (!closureTypeSpec.ReturnType.IsEmptyTuple)
        {
            if (!IsSupportedClosureParameterType(closureTypeSpec.ReturnType))
                return false;

            // Closures with return types that require complex marshalling are not yet supported.
            // This includes bound generic types (like Optional<T>, Result<T,E>) and types
            // requiring memory management (like SwiftString). These need native buffer
            // allocation and marshalling which isn't implemented for return values yet.
            if (!IsSupportedClosureReturnType(closureTypeSpec.ReturnType))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Determines if a closure return type requires indirect return marshalling.
    /// Indirect return is needed for non-blittable types that cannot be returned
    /// directly from [UnmanagedCallersOnly] callbacks.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>True if the closure return type requires indirect marshalling.</returns>
    public bool RequiresIndirectReturnMarshalling(ClosureTypeSpec closureTypeSpec)
    {
        if (closureTypeSpec.ReturnType.IsEmptyTuple)
            return false;

        if (closureTypeSpec.ReturnType is NamedTypeSpec namedType)
        {
            // Bound generic return types require indirect marshalling
            if (namedType.ContainsGenericParameters)
                return true;

            // Check if type requires memory management
            var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (_typeDatabase.TryGetTypeRecord(baseTypeName, out var typeRecord))
            {
                if ((typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a return type is supported for closure callbacks.
    /// Now supports bound generic return types via indirect return marshalling.
    /// </summary>
    private bool IsSupportedClosureReturnType(TypeSpec typeSpec)
    {
        // Existential return types supported
        if (_existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
            return protocolList != null && _existentialHandler.IsSupportedExistential(protocolList);
        }

        if (typeSpec is NamedTypeSpec namedType)
        {
            // Pointer types are supported (map to IntPtr)
            if (IsPointerType(namedType))
                return true;

            // Bound generic return types are now supported via indirect return marshalling
            if (namedType.ContainsGenericParameters)
            {
                // Check that the base type is in the database
                var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                if (!_typeDatabase.TryGetTypeRecord(baseTypeName, out _))
                    return false;

                // Recursively check all generic parameters are supported
                foreach (var genericParam in namedType.GenericParameters)
                {
                    // Handle existential generic parameters (e.g., Optional<any Protocol>)
                    if (_existentialHandler.IsExistential(genericParam))
                    {
                        var protocolList = _existentialHandler.ToProtocolListTypeSpec(genericParam);
                        if (protocolList == null || !_existentialHandler.IsSupportedExistential(protocolList))
                            return false;
                        continue; // This parameter is valid
                    }

                    if (!IsSupportedClosureParameterType(genericParam))
                        return false;
                }
                return true;
            }

            // Check if type requires memory management - now supported via indirect return
            var baseType = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (_typeDatabase.TryGetTypeRecord(baseType, out var typeRecord))
            {
                // Types requiring memory management are now supported via indirect return
                // They just need to be in the database
            }

            return true;
        }

        // Tuples as return types - check all elements
        if (typeSpec is TupleTypeSpec tuple)
        {
            foreach (var element in tuple.Elements)
            {
                if (!IsSupportedClosureReturnType(element))
                    return false;
            }
            return true;
        }

        return true;
    }

    /// <summary>
    /// Determines whether the closure requires a thunk (callback) function.
    /// @convention(c) closures don't need thunks - delegates can be passed directly as function pointers.
    /// Escaping closures need thunks to handle the context parameter.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns><c>true</c> if the closure requires a thunk; otherwise, <c>false</c>.</returns>
    public bool RequiresThunk(ClosureTypeSpec closureTypeSpec)
    {
        return !IsConventionC(closureTypeSpec);
    }

    /// <summary>
    /// Checks if a type is supported as a closure parameter or return type.
    /// </summary>
    private bool IsSupportedClosureParameterType(TypeSpec typeSpec)
    {
        // Closures within closures are not supported yet
        if (typeSpec is ClosureTypeSpec)
            return false;

        // Existential types (any Protocol) are supported
        if (_existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
            return protocolList != null && _existentialHandler.IsSupportedExistential(protocolList);
        }

        // Tuples are supported if they meet TupleHandler's criteria
        if (typeSpec is TupleTypeSpec tuple && !tuple.IsEmptyTuple)
            return _tupleHandler.IsSupportedTuple(tuple);

        // Named types should be resolvable in the type database
        if (typeSpec is NamedTypeSpec namedType)
        {
            // Pointer types are always supported
            if (IsPointerType(namedType))
                return true;

            // Generic types require special handling
            if (namedType.ContainsGenericParameters)
            {
                if (!IsSupportedGenericType(namedType))
                    return false;
            }
            else
            {
                // Non-generic named types must be in the type database
                var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                if (!_typeDatabase.TryGetTypeRecord(swiftTypeName, out _))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks if a generic type is supported in closures.
    /// Supports pointer types and bound generic types whose base type is in the type database.
    /// </summary>
    private bool IsSupportedGenericType(NamedTypeSpec namedType)
    {
        // Pointer types always supported - they map to IntPtr
        if (IsPointerType(namedType))
            return true;

        // Check if base type is in type database
        var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
        if (!_typeDatabase.TryGetTypeRecord(baseTypeName, out _))
            return false;

        // Recursively check all generic parameters are supported
        foreach (var genericParam in namedType.GenericParameters)
        {
            // Handle existential generic parameters (e.g., Optional<any Protocol>)
            if (_existentialHandler.IsExistential(genericParam))
            {
                var protocolList = _existentialHandler.ToProtocolListTypeSpec(genericParam);
                if (protocolList == null || !_existentialHandler.IsSupportedExistential(protocolList))
                    return false;
                continue; // This parameter is valid
            }

            if (!IsSupportedClosureParameterType(genericParam))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Translates a Swift closure type to a C# delegate type string for wrapper methods.
    /// Async closures are mapped to Func&lt;..., Task&gt; or Func&lt;..., Task&lt;T&gt;&gt;.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>The C# delegate type name (Action&lt;&gt; or Func&lt;&gt;).</returns>
    public string GetCSharpDelegateType(ClosureTypeSpec closureTypeSpec)
    {
        var argTypes = new List<string>();
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            argTypes.Add(TranslateTypeSpecToCSharp(arg));
        }

        bool hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;

        // Handle async closures - map to Func<..., Task> or Func<..., Task<T>>
        if (closureTypeSpec.IsAsync)
        {
            if (hasReturn)
            {
                var returnType = TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType);
                if (argTypes.Count == 0)
                    return $"Func<Task<{returnType}>>";
                return $"Func<{string.Join(", ", argTypes)}, Task<{returnType}>>";
            }
            else
            {
                if (argTypes.Count == 0)
                    return "Func<Task>";
                return $"Func<{string.Join(", ", argTypes)}, Task>";
            }
        }

        // Non-async closures
        if (hasReturn)
        {
            var returnType = TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType);
            if (argTypes.Count == 0)
                return $"Func<{returnType}>";
            return $"Func<{string.Join(", ", argTypes)}, {returnType}>";
        }
        else
        {
            if (argTypes.Count == 0)
                return "Action";
            return $"Action<{string.Join(", ", argTypes)}>";
        }
    }

    /// <summary>
    /// Determines if a closure is an async closure.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>True if the closure is async.</returns>
    public bool IsAsyncClosure(ClosureTypeSpec closureTypeSpec)
    {
        return closureTypeSpec.IsAsync;
    }

    /// <summary>
    /// Gets the P/Invoke function pointer type for a closure.
    /// Uses Swift calling convention since escaping closures are called with Swift ABI.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>The unmanaged function pointer type string.</returns>
    public string GetPInvokeFunctionPointerType(ClosureTypeSpec closureTypeSpec)
    {
        var argTypes = new List<string>();
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            argTypes.Add(TranslateTypeSpecToPInvokeType(arg));
        }

        bool hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        var returnType = hasReturn ? TranslateTypeSpecToPInvokeType(closureTypeSpec.ReturnType) : "void";

        // Use Swift calling convention for escaping closures (thick closures)
        if (argTypes.Count == 0)
            return $"delegate* unmanaged[Swift]<{returnType}>";

        return $"delegate* unmanaged[Swift]<{string.Join(", ", argTypes)}, {returnType}>";
    }

    /// <summary>
    /// Gets the P/Invoke function pointer type for a closure callback that uses indirect return.
    /// The indirect result pointer is passed as the first parameter (void*), and the callback returns void.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>The unmanaged function pointer type string with indirect return.</returns>
    public string GetPInvokeFunctionPointerTypeWithIndirectReturn(ClosureTypeSpec closureTypeSpec)
    {
        var argTypes = new List<string> { "void*" }; // indirectResult first
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            argTypes.Add(TranslateTypeSpecToPInvokeType(arg));
        }

        // Use Swift calling convention, return type is always void with indirect return
        return $"delegate* unmanaged[Swift]<{string.Join(", ", argTypes)}, void>";
    }

    /// <summary>
    /// Translates a TypeSpec to its C# equivalent for delegate type parameters.
    /// </summary>
    public string TranslateTypeSpecToCSharp(TypeSpec typeSpec)
    {
        // Handle existential types
        if (_existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
            if (protocolList != null && _existentialHandler.IsSupportedExistential(protocolList))
                return _existentialHandler.GetCSharpExistentialType(protocolList);
            return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }

        if (typeSpec is NamedTypeSpec namedType)
        {
            // Handle pointer types
            if (IsPointerType(namedType))
                return "IntPtr";

            // Handle bound generic types (e.g., Result<T, E>, Array<T>)
            if (namedType.ContainsGenericParameters)
                return TranslateBoundGenericToCSharp(namedType);

            var typeRecord = _typeDatabase.GetTypeRecordOrAnyType(namedType);
            return typeRecord.CSharpTypeName.FullyQualifiedName;
        }

        if (typeSpec.IsEmptyTuple)
            return "void";

        // Handle tuple types
        if (typeSpec is TupleTypeSpec tupleType)
            return _tupleHandler.GetCSharpTupleType(tupleType);

        // Fallback for unsupported types
        return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
    }

    /// <summary>
    /// Translates a bound generic NamedTypeSpec to its full C# type name with generic parameters.
    /// </summary>
    private string TranslateBoundGenericToCSharp(NamedTypeSpec namedType)
    {
        var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
        if (!_typeDatabase.TryGetTypeRecord(baseTypeName, out var typeRecord))
        {
            // Fallback if base type not in database
            return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }

        // Recursively translate all generic parameters
        var translatedParams = new List<string>();
        foreach (var genericParam in namedType.GenericParameters)
        {
            // Handle existential generic parameters (e.g., Optional<any Protocol>)
            if (_existentialHandler.IsExistential(genericParam))
            {
                var protocolList = _existentialHandler.ToProtocolListTypeSpec(genericParam);
                if (protocolList != null && _existentialHandler.IsSupportedExistential(protocolList))
                {
                    translatedParams.Add(_existentialHandler.GetCSharpExistentialType(protocolList));
                    continue;
                }
            }
            translatedParams.Add(TranslateTypeSpecToCSharp(genericParam));
        }

        // Build full type name with generics
        return translatedParams.Count > 0
            ? $"{typeRecord.CSharpTypeName.FullyQualifiedName}<{string.Join(", ", translatedParams)}>"
            : typeRecord.CSharpTypeName.FullyQualifiedName;
    }

    /// <summary>
    /// Translates a TypeSpec to its P/Invoke equivalent type.
    /// For UnmanagedCallersOnly compatibility, only blittable types can be used directly.
    /// Non-blittable types (including those requiring memory management) use void*.
    /// </summary>
    public string TranslateTypeSpecToPInvokeType(TypeSpec typeSpec)
    {
        // Handle existential types
        if (_existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
            if (protocolList != null && _existentialHandler.IsSupportedExistential(protocolList))
                return _existentialHandler.GetPInvokeExistentialType(protocolList);
            return "void*";
        }

        if (typeSpec is NamedTypeSpec namedType)
        {
            // Handle pointer types - all map to void* or IntPtr
            if (IsPointerType(namedType))
                return "void*";

            // Check for known blittable primitive types first
            // Only these can be safely passed directly in unmanaged function pointers
            var primitiveType = GetBlittablePrimitiveType(namedType.Name);
            if (primitiveType != null)
                return primitiveType;

            // Bound generic types are passed as opaque pointers in P/Invoke
            if (namedType.ContainsGenericParameters)
                return "void*";

            // All other types (structs, classes, etc.) must be passed as void*
            // and marshalled manually, even if frozen - only primitives are safe
            // to pass directly in unmanaged function pointers
            return "void*";
        }

        if (typeSpec.IsEmptyTuple)
            return "void";

        // Handle tuple types
        if (typeSpec is TupleTypeSpec tupleType)
            return _tupleHandler.GetPInvokeTupleType(tupleType);

        // Fallback
        return "void*";
    }

    /// <summary>
    /// Checks if a closure can be invoked from C# when received from Swift.
    /// Closures with non-primitive parameters cannot be invoked because we can't
    /// easily marshal C# structs to void* pointers in the lambda.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>True if all parameters are primitive types that can be passed directly.</returns>
    public bool CanInvokeFromCSharp(ClosureTypeSpec closureTypeSpec)
    {
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (!IsInvocableParameter(arg))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if a parameter type can be passed when invoking a Swift closure from C#.
    /// Supports primitive types and frozen structs that can be marshalled.
    /// </summary>
    private bool IsInvocableParameter(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec namedType)
        {
            // Pointer types are supported
            if (IsPointerType(namedType))
                return true;

            // Primitive types are supported (direct pass)
            var primitiveType = GetBlittablePrimitiveType(namedType.Name);
            if (primitiveType != null)
                return true;

            // Frozen structs are supported (via marshalling)
            if (IsFrozenStruct(namedType))
                return true;

            return false;
        }

        // Tuples of primitives could be supported but aren't currently
        if (typeSpec is TupleTypeSpec)
            return false;

        // Empty tuples (void) are fine
        if (typeSpec.IsEmptyTuple)
            return true;

        // Other types (closures, existentials, etc.) are not supported
        return false;
    }

    /// <summary>
    /// Checks if a type is a frozen struct in the type database.
    /// Frozen structs can be marshalled via MarshalToSwift when invoking closures.
    /// </summary>
    /// <param name="typeSpec">The type specification to check.</param>
    /// <returns>True if the type is a frozen struct.</returns>
    public bool IsFrozenStruct(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;

        // Don't treat generic types as frozen structs - they need special handling
        if (namedType.ContainsGenericParameters)
            return false;

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
        if (!_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
            return false;

        // Must be a struct and be frozen
        return typeRecord.Kind == TypeRecordKind.Struct &&
               (typeRecord.Flags & TypeRecordFlags.Frozen) != 0;
    }

    /// <summary>
    /// Checks if invoking a closure from C# requires struct marshalling for any parameter.
    /// When true, the invoker lambda needs to marshal struct parameters to void* before calling Swift.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>True if any parameter requires struct marshalling.</returns>
    public bool RequiresStructMarshalling(ClosureTypeSpec closureTypeSpec)
    {
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (arg is NamedTypeSpec namedType &&
                !IsPointerType(namedType) &&
                GetBlittablePrimitiveType(namedType.Name) == null &&
                IsFrozenStruct(namedType))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns the blittable C# type for known Swift primitive types.
    /// Returns null for non-primitive types that should use void*.
    /// </summary>
    private static string? GetBlittablePrimitiveType(string swiftTypeName)
    {
        return swiftTypeName switch
        {
            "Swift.Int" => "nint",
            "Swift.UInt" => "nuint",
            "Swift.Int8" => "sbyte",
            "Swift.UInt8" => "byte",
            "Swift.Int16" => "short",
            "Swift.UInt16" => "ushort",
            "Swift.Int32" => "int",
            "Swift.UInt32" => "uint",
            "Swift.Int64" => "long",
            "Swift.UInt64" => "ulong",
            "Swift.Float" => "float",
            "Swift.Double" => "double",
            // Bool is non-blittable, use byte instead (Swift.Bool is 1 byte)
            "Swift.Bool" => "byte",
            _ => null // Not a primitive - should use void*
        };
    }

    /// <summary>
    /// Checks if a named type is a Swift pointer type.
    /// </summary>
    private static bool IsPointerType(NamedTypeSpec namedType)
    {
        return namedType.Name == "Swift.UnsafePointer" ||
               namedType.Name == "Swift.UnsafeMutablePointer" ||
               namedType.Name == "Swift.UnsafeRawPointer" ||
               namedType.Name == "Swift.UnsafeMutableRawPointer" ||
               namedType.Name == "Swift.OpaquePointer" ||
               namedType.Name == "Builtin.RawPointer";
    }

    /// <summary>
    /// Generates the callback function name for a closure parameter.
    /// Includes a hash of the method's mangled name to disambiguate overloads.
    /// </summary>
    /// <param name="methodName">The name of the method containing the closure.</param>
    /// <param name="parameterName">The name of the closure parameter.</param>
    /// <param name="mangledName">The mangled name of the method (used to create a unique hash).</param>
    /// <returns>The callback function name.</returns>
    public static string GetCallbackFunctionName(string methodName, string parameterName, string mangledName)
    {
        var mangledHash = Math.Abs(mangledName.GetHashCode()).ToString("X8");
        return $"{methodName}_{parameterName}_{mangledHash}_Callback";
    }

    /// <summary>
    /// Generates the closure wrapper field name for storing delegate references.
    /// </summary>
    /// <param name="parameterName">The name of the closure parameter.</param>
    /// <returns>The closure wrapper field name.</returns>
    public static string GetClosureWrapperFieldName(string parameterName)
    {
        return $"_{parameterName}Closure";
    }
}
