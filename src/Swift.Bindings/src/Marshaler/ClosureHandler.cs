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

    public ClosureHandler(ITypeDatabase typeDatabase)
    {
        _typeDatabase = typeDatabase;
        _tupleHandler = new TupleHandler(typeDatabase);
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
    /// Both must be synchronous, non-throwing, and have concrete (non-generic) argument/return types.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns><c>true</c> if the closure is supported; otherwise, <c>false</c>.</returns>
    public bool IsSupportedClosure(ClosureTypeSpec closureTypeSpec)
    {
        // Exclude async closures for now
        if (closureTypeSpec.IsAsync)
            return false;

        // Exclude throwing closures for now
        if (closureTypeSpec.Throws)
            return false;

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
        if (!closureTypeSpec.ReturnType.IsEmptyTuple && !IsSupportedClosureParameterType(closureTypeSpec.ReturnType))
            return false;

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

        // Tuples are supported if they meet TupleHandler's criteria
        if (typeSpec is TupleTypeSpec tuple && !tuple.IsEmptyTuple)
            return _tupleHandler.IsSupportedTuple(tuple);

        // Named types should be resolvable in the type database
        if (typeSpec is NamedTypeSpec namedType)
        {
            // Generic parameters in closures not supported yet
            if (namedType.ContainsGenericParameters)
            {
                // Exception: some known generic types like pointers are OK
                if (!IsKnownSupportedGenericType(namedType))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks if a generic type is known to be supported.
    /// </summary>
    private static bool IsKnownSupportedGenericType(NamedTypeSpec namedType)
    {
        // Swift pointer types map to IntPtr
        return namedType.Name == "Swift.UnsafePointer" ||
               namedType.Name == "Swift.UnsafeMutablePointer" ||
               namedType.Name == "Swift.UnsafeRawPointer" ||
               namedType.Name == "Swift.UnsafeMutableRawPointer" ||
               namedType.Name == "Swift.OpaquePointer";
    }

    /// <summary>
    /// Translates a Swift closure type to a C# delegate type string for wrapper methods.
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
    /// Translates a TypeSpec to its C# equivalent for delegate type parameters.
    /// </summary>
    private string TranslateTypeSpecToCSharp(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec namedType)
        {
            // Handle pointer types
            if (IsPointerType(namedType))
                return "IntPtr";

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
    /// Translates a TypeSpec to its P/Invoke equivalent type.
    /// For UnmanagedCallersOnly compatibility, bool is mapped to byte.
    /// </summary>
    private string TranslateTypeSpecToPInvokeType(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec namedType)
        {
            // Handle pointer types - all map to void* or IntPtr
            if (IsPointerType(namedType))
                return "void*";

            // Swift.Bool must be mapped to byte for UnmanagedCallersOnly
            // (bool is non-blittable in .NET)
            if (namedType.Name == "Swift.Bool")
                return "byte";

            var typeRecord = _typeDatabase.GetTypeRecordOrAnyType(namedType);

            // For P/Invoke, non-frozen types need special handling
            if ((typeRecord.Flags & TypeRecordFlags.Frozen) == 0)
                return "void*";

            return typeRecord.CSharpTypeName.FullyQualifiedName;
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
    /// </summary>
    /// <param name="methodName">The name of the method containing the closure.</param>
    /// <param name="parameterName">The name of the closure parameter.</param>
    /// <returns>The callback function name.</returns>
    public static string GetCallbackFunctionName(string methodName, string parameterName)
    {
        return $"{methodName}_{parameterName}_Callback";
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
