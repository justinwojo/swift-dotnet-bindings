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
    /// Also returns true for Optional closures (e.g., Optional&lt;() -&gt; Void&gt;).
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns><c>true</c> if the argument's Swift type is a closure or optional closure; otherwise, <c>false</c>.</returns>
    public bool IsClosure(ArgumentDecl argumentDecl) =>
        argumentDecl.SwiftTypeSpec is ClosureTypeSpec ||
        IsOptionalClosure(argumentDecl.SwiftTypeSpec);

    /// <summary>
    /// Determines whether the specified property declaration represents a closure type.
    /// Also returns true for Optional closures (e.g., Optional&lt;() -&gt; Void&gt;).
    /// </summary>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <returns><c>true</c> if the property's Swift type is a closure or optional closure; otherwise, <c>false</c>.</returns>
    public bool IsClosure(PropertyDecl propertyDecl) =>
        propertyDecl.SwiftTypeSpec is ClosureTypeSpec ||
        IsOptionalClosure(propertyDecl.SwiftTypeSpec);

    /// <summary>
    /// Gets the ClosureTypeSpec from an argument declaration.
    /// Also extracts the closure from Optional closures.
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns>The ClosureTypeSpec if the argument is a closure or optional closure; otherwise, null.</returns>
    public ClosureTypeSpec? GetClosureTypeSpec(ArgumentDecl argumentDecl) =>
        GetClosureTypeSpec(argumentDecl.SwiftTypeSpec);

    /// <summary>
    /// Gets the ClosureTypeSpec from a property declaration.
    /// Also extracts the closure from Optional closures.
    /// </summary>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <returns>The ClosureTypeSpec if the property is a closure; otherwise, null.</returns>
    public ClosureTypeSpec? GetClosureTypeSpec(PropertyDecl propertyDecl) =>
        GetClosureTypeSpec(propertyDecl.SwiftTypeSpec);

    /// <summary>
    /// Gets the ClosureTypeSpec from a TypeSpec.
    /// Also extracts the closure from Optional closures.
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <returns>The ClosureTypeSpec if the type is a closure or optional closure; otherwise, null.</returns>
    public ClosureTypeSpec? GetClosureTypeSpec(TypeSpec typeSpec)
    {
        if (typeSpec is ClosureTypeSpec closure)
            return closure;

        // Check for Optional<Closure>
        if (IsOptionalClosure(typeSpec) &&
            typeSpec is NamedTypeSpec namedType &&
            namedType.GenericParameters.Count > 0 &&
            namedType.GenericParameters[0] is ClosureTypeSpec innerClosure)
        {
            return innerClosure;
        }

        return null;
    }

    /// <summary>
    /// Determines whether the specified type is an Optional containing a closure.
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <returns><c>true</c> if the type is Optional&lt;Closure&gt;; otherwise, <c>false</c>.</returns>
    public bool IsOptionalClosure(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;

        if (namedType.Name != "Swift.Optional")
            return false;

        if (namedType.GenericParameters.Count != 1)
            return false;

        return namedType.GenericParameters[0] is ClosureTypeSpec;
    }

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
    /// Determines whether the closure has @MainActor attribute.
    /// @MainActor closures must be called on the main thread/actor.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns><c>true</c> if the closure is @MainActor; otherwise, <c>false</c>.</returns>
    public bool IsMainActor(ClosureTypeSpec closureTypeSpec)
    {
        if (!closureTypeSpec.HasAttributes)
            return false;

        return closureTypeSpec.Attributes.Exists(attr =>
            attr.Name == "MainActor" ||
            attr.Name == "Swift.MainActor" ||
            attr.Name == "_Concurrency.MainActor");
    }

    /// <summary>
    /// Determines whether the closure has @Sendable attribute.
    /// @Sendable closures can be passed across concurrency domains.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns><c>true</c> if the closure is @Sendable; otherwise, <c>false</c>.</returns>
    public bool IsSendable(ClosureTypeSpec closureTypeSpec)
    {
        if (!closureTypeSpec.HasAttributes)
            return false;

        return closureTypeSpec.Attributes.Exists(attr =>
            attr.Name == "Sendable" ||
            attr.Name == "Swift.Sendable" ||
            attr.Name == "_Concurrency.Sendable");
    }

    /// <summary>
    /// Determines whether the closure is a supported type.
    /// Currently supports:
    /// - @convention(c) closures (Phase 1)
    /// - Escaping closures with concrete types (Phase 2)
    /// - Async closures (Phase 3) - mapped to Func&lt;..., Task&gt; or Func&lt;..., Task&lt;T&gt;&gt;
    /// - Throwing closures (Phase 4) - mapped to Func&lt;..., SwiftResult&lt;T, SwiftError&gt;&gt;
    /// - Async+throwing closures (Phase 28) - mapped to Func&lt;..., Task&lt;SwiftResult&lt;T, SwiftError&gt;&gt;&gt;
    ///   via Swift continuation wrapper pattern
    /// All must have concrete (non-generic) argument/return types.
    /// Return types must be primitive/blittable (complex return type marshalling not yet implemented).
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns><c>true</c> if the closure is supported; otherwise, <c>false</c>.</returns>
    public bool IsSupportedClosure(ClosureTypeSpec closureTypeSpec)
    {
        // B13: Async+throwing closures WITH parameters are not supported.
        // AsyncThrowingClosureState<T>.AsyncFunc is Func<Task<T>> (parameterless).
        // A closure like (String) async throws -> String produces Func<SwiftString, Task<SwiftString>>
        // which can't be assigned to Func<Task<SwiftString>>.
        if (closureTypeSpec.IsAsync && closureTypeSpec.Throws && closureTypeSpec.EachArgument().Any())
            return false;

        // Async+throwing closures are now supported via Swift continuation wrapper pattern (Phase 28)
        // The C# side provides a synchronous "start" callback that spawns Task.Run,
        // while Swift uses withCheckedThrowingContinuation to create the actual async closure.
        //
        // Foundation.Data returns are supported via special byte[] marshalling:
        // 1. User provides Func<Task<Swift.Data>>
        // 2. C# awaits the task, calls result.ToByteArray()
        // 3. Pins bytes and calls Swift's success callback with (boxPtr, dataPtr, length)
        // 4. Swift copies the bytes to create a new Data object

        // Plain throwing closures are supported - mapped to SwiftResult<T, SwiftError>
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
            // Pointer returns use direct pointer ABI.
            if (IsPointerType(namedType))
                return false;

            // Bound generic return types require indirect marshalling
            if (namedType.ContainsGenericParameters)
                return true;

            // Struct returns that cannot be returned directly from callbacks
            // (for example non-frozen structs) use indirect return marshalling.
            if (!CanUseDirectCallbackReturn(closureTypeSpec.ReturnType))
            {
                var nonDirectBaseType = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                if (_typeDatabase.TryGetTypeRecord(nonDirectBaseType, out var nonDirectRecord) &&
                    nonDirectRecord.Kind == TypeRecordKind.Struct)
                {
                    return true;
                }
            }

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
    /// Determines whether a closure callback can return this type directly from
    /// an UnmanagedCallersOnly callback method.
    /// </summary>
    /// <remarks>
    /// Direct return is allowed for:
    /// - blittable Swift primitives (Int/Double/etc.)
    /// - frozen structs that do not require memory management.
    /// Other complex types continue to use pointer-based return handling.
    /// </remarks>
    public bool CanUseDirectCallbackReturn(TypeSpec returnTypeSpec)
    {
        if (returnTypeSpec.IsEmptyTuple)
            return false;

        if (returnTypeSpec is not NamedTypeSpec namedType)
            return false;

        if (namedType.ContainsGenericParameters)
            return false;

        // Primitives are returned directly.
        if (GetBlittablePrimitiveType(namedType.Name) != null)
            return true;

        var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
        if (!_typeDatabase.TryGetTypeRecord(baseTypeName, out var typeRecord))
            return false;

        return typeRecord.Kind == TypeRecordKind.Struct &&
               (typeRecord.Flags & TypeRecordFlags.Frozen) != 0 &&
               (typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) == 0;
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
                    // Existential generic parameters (e.g., Optional<any Protocol>) are NOT supported
                    // for closure return types because the emitter can't marshal void* back to the
                    // bound generic type (e.g., SwiftOptional<ExistentialContainer1>)
                    if (_existentialHandler.IsExistential(genericParam))
                        return false;

                    if (!IsSupportedClosureParameterType(genericParam))
                        return false;

                    // B7: Generic parameters that require memory management (e.g., SwiftString inside Optional)
                    // The P/Invoke uses void* for these (line 868) but the C# delegate expects the actual struct.
                    if (genericParam is NamedTypeSpec innerNamed && !innerNamed.ContainsGenericParameters && innerNamed.HasModule())
                    {
                        var innerTypeName = SwiftTypeName.FromModuleQualifiedName(innerNamed.Name);
                        if (_typeDatabase.TryGetTypeRecord(innerTypeName, out var innerRecord) &&
                            MarshallingHelpers.RequiresMemoryManagement(innerRecord))
                        {
                            return false;
                        }
                    }
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
            // B16: C# enums are non-blittable and cannot be used in [UnmanagedCallersOnly] callbacks
            if (!namedType.ContainsGenericParameters && namedType.HasModule())
            {
                var enumSwiftName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                if (_typeDatabase.TryGetTypeRecord(enumSwiftName, out var enumRecord) &&
                    enumRecord.Kind == TypeRecordKind.Enum)
                {
                    return false;
                }
            }
            // Generic type parameters (τ_0_0, τ_0_1, T, etc.) are not supported in closures
            // because their concrete types aren't known at binding generation time
            if (IsGenericTypeParameter(namedType.Name))
                return false;

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
                // Types without a module qualifier are not supported (except pointer types handled above)
                if (!namedType.HasModule())
                    return false;

                // Non-generic named types must be in the type database
                var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                if (!_typeDatabase.TryGetTypeRecord(swiftTypeName, out _))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks if a type name represents a generic type parameter.
    /// Swift generic type parameters appear as τ_0_0, τ_0_1, etc., or as simple names like T, U, Element.
    /// Delegates to the shared TypeSpecHelpers.IsGenericTypeParameter method.
    /// </summary>
    private static bool IsGenericTypeParameter(string typeName) =>
        TypeSpecHelpers.IsGenericTypeParameter(typeName);

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
    /// Throwing closures are mapped to Func&lt;..., SwiftResult&lt;T, SwiftError&gt;&gt;.
    /// Async+throwing closures are mapped to Func&lt;..., Task&lt;SwiftResult&lt;T, SwiftError&gt;&gt;&gt;.
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
        bool isAsync = closureTypeSpec.IsAsync;
        bool throws = closureTypeSpec.Throws;

        // Determine the core return type
        string coreReturnType;
        if (hasReturn)
        {
            coreReturnType = TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true);
        }
        else
        {
            // For void returns, use SwiftVoid as the success type in throwing closures
            coreReturnType = throws ? "Swift.SwiftVoid" : null!;
        }

        // Build the final return type based on async and throws modifiers
        string finalReturnType;
        if (isAsync && throws)
        {
            // Async+throwing closures: error handling is via Swift continuation callback,
            // NOT via SwiftResult return type. User's delegate returns Task<T> and
            // exceptions are caught and forwarded to Swift's error callback.
            finalReturnType = hasReturn ? $"Task<{coreReturnType}>" : "Task";
        }
        else if (throws)
        {
            // Throwing closures (non-async) wrap in SwiftResult<T, SwiftError>
            var resultType = hasReturn
                ? $"Swift.SwiftResult<{coreReturnType}, SwiftError>"
                : $"Swift.SwiftResult<Swift.SwiftVoid, SwiftError>";
            finalReturnType = resultType;
        }
        else if (isAsync)
        {
            // Async only: Task or Task<T>
            finalReturnType = hasReturn ? $"Task<{coreReturnType}>" : "Task";
        }
        else
        {
            // Non-async, non-throwing
            finalReturnType = coreReturnType;
        }

        // Generate delegate type
        if (finalReturnType == null)
        {
            // Non-throwing, non-async void -> Action
            if (argTypes.Count == 0)
                return "Action";
            return $"Action<{string.Join(", ", argTypes)}>";
        }
        else
        {
            // All other cases -> Func
            if (argTypes.Count == 0)
                return $"Func<{finalReturnType}>";
            return $"Func<{string.Join(", ", argTypes)}, {finalReturnType}>";
        }
    }

    /// <summary>
    /// Determines if a closure is a throwing closure.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>True if the closure throws.</returns>
    public bool IsThrowingClosure(ClosureTypeSpec closureTypeSpec)
    {
        return closureTypeSpec.Throws;
    }

    /// <summary>
    /// Determines if a closure is both async and throwing.
    /// Async+throwing closures require special handling via Swift continuation wrappers.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>True if the closure is both async and throws.</returns>
    public bool IsAsyncThrowingClosure(ClosureTypeSpec closureTypeSpec)
    {
        return closureTypeSpec.IsAsync && closureTypeSpec.Throws;
    }

    /// <summary>
    /// Gets the P/Invoke function pointer type for an async+throwing closure's "start" function.
    /// The start function is called synchronously by Swift and spawns the async work via Task.Run.
    /// Signature: (contextPtr, continuationBoxPtr, successCallbackPtr, errorCallbackPtr) -> void
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>The unmanaged function pointer type string for the start function.</returns>
    public string GetAsyncThrowingStartFunctionPointerType(ClosureTypeSpec closureTypeSpec)
    {
        // The start function always has this signature:
        // - IntPtr contextPtr: GCHandle to AsyncThrowingClosureState
        // - IntPtr continuationBoxPtr: Swift's ContinuationBox pointer
        // - IntPtr successCallbackPtr: Function pointer for success callback
        // - IntPtr errorCallbackPtr: Function pointer for error callback
        // - Returns void (spawns Task.Run internally)
        return "delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>";
    }

    /// <summary>
    /// Gets the Swift success callback signature for an async+throwing closure.
    /// The success callback is called by C# when the async work completes successfully.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>The Swift @convention(c) callback signature.</returns>
    public string GetAsyncThrowingSuccessCallbackSwiftSignature(ClosureTypeSpec closureTypeSpec)
    {
        var returnType = closureTypeSpec.ReturnType;
        if (returnType.IsEmptyTuple)
        {
            // Void return: just boxPtr
            return "@convention(c) (UnsafeMutableRawPointer) -> Void";
        }

        // For Data return type (most common case for async data loaders)
        if (returnType is NamedTypeSpec namedType && namedType.Name == "Foundation.Data")
        {
            return "@convention(c) (UnsafeMutableRawPointer, UnsafePointer<UInt8>, Int) -> Void";
        }

        // Generic case - use opaque pointer for result
        return "@convention(c) (UnsafeMutableRawPointer, UnsafeRawPointer) -> Void";
    }

    /// <summary>
    /// Gets the Swift error callback signature for an async+throwing closure.
    /// The error callback is called by C# when the async work throws an exception.
    /// </summary>
    /// <returns>The Swift @convention(c) callback signature.</returns>
    public string GetAsyncThrowingErrorCallbackSwiftSignature()
    {
        // Error callback: (boxPtr, errorMessage) -> Void
        return "@convention(c) (UnsafeMutableRawPointer, UnsafePointer<CChar>) -> Void";
    }

    /// <summary>
    /// Gets the C# delegate type for an optional closure.
    /// Returns the nullable delegate type (e.g., "Func&lt;Task&gt;?").
    /// </summary>
    /// <param name="typeSpec">The type specification (should be Optional&lt;Closure&gt;).</param>
    /// <returns>The nullable C# delegate type name.</returns>
    public string GetCSharpOptionalDelegateType(TypeSpec typeSpec)
    {
        var closureTypeSpec = GetClosureTypeSpec(typeSpec);
        if (closureTypeSpec == null)
            return "object?";

        return GetCSharpDelegateType(closureTypeSpec) + "?";
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
    /// Gets the P/Invoke function pointer type for a throwing closure callback.
    /// The error pointer is passed as an additional parameter (SwiftError*), and the callback
    /// should set the error pointer if the delegate returns a failure result.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>The unmanaged function pointer type string with error parameter.</returns>
    public string GetPInvokeFunctionPointerTypeWithError(ClosureTypeSpec closureTypeSpec)
    {
        var argTypes = new List<string>();
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            argTypes.Add(TranslateTypeSpecToPInvokeType(arg));
        }

        // Add SwiftError* out parameter before SwiftSelf context
        argTypes.Add("SwiftError*");

        bool hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        var returnType = hasReturn ? TranslateTypeSpecToPInvokeType(closureTypeSpec.ReturnType) : "void";

        // Use Swift calling convention
        if (argTypes.Count == 0)
            return $"delegate* unmanaged[Swift]<{returnType}>";

        return $"delegate* unmanaged[Swift]<{string.Join(", ", argTypes)}, {returnType}>";
    }

    /// <summary>
    /// Gets the P/Invoke function pointer type for a throwing closure callback that uses indirect return.
    /// Combines indirect return (void* first) with error handling (SwiftError* before context).
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>The unmanaged function pointer type string with indirect return and error parameter.</returns>
    public string GetPInvokeFunctionPointerTypeWithIndirectReturnAndError(ClosureTypeSpec closureTypeSpec)
    {
        var argTypes = new List<string> { "void*" }; // indirectResult first
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            argTypes.Add(TranslateTypeSpecToPInvokeType(arg));
        }

        // Add SwiftError* out parameter before SwiftSelf context
        argTypes.Add("SwiftError*");

        // Use Swift calling convention, return type is always void with indirect return
        return $"delegate* unmanaged[Swift]<{string.Join(", ", argTypes)}, void>";
    }

    /// <summary>
    /// Translates a TypeSpec to its C# equivalent for delegate type parameters.
    /// When isReturnType is true, existential types keep using ExistentialContainer
    /// because the callback/invoker code paths don't yet handle interface↔container
    /// conversion for return types.
    /// </summary>
    public string TranslateTypeSpecToCSharp(TypeSpec typeSpec, bool isReturnType = false)
    {
        // Handle existential types — use protocol interface for known protocols (params only)
        if (_existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
            if (protocolList != null && _existentialHandler.IsSupportedExistential(protocolList))
            {
                // Use public interface (e.g., IImageProcessing) when all protocols have TypeRecords
                // AND a proxy class exists (TryGetFilteredProxyClassName filters ObjC protocols).
                // Without a proxy class, the callback can't convert ExistentialContainer → interface.
                // Return types are excluded — the callback/invoker don't have conversion logic yet.
                // P1 fix: Also exclude mixed compositions where ObjC filtering drops protocols,
                // because the proxy constructor accepts ExistentialContainer{filteredCount}
                // but P/Invoke passes ExistentialContainer{originalCount}.
                if (!isReturnType && _existentialHandler.AllProtocolsHaveTypeRecords(protocolList))
                {
                    var publicType = _existentialHandler.GetPublicExistentialType(protocolList);
                    var filteredCount = protocolList.Protocols.Keys
                        .Count(p => !TypeDatabaseExtensions.IsObjCModuleType(p));
                    if (publicType != "object" &&
                        filteredCount == protocolList.Protocols.Count &&
                        _existentialHandler.TryGetFilteredProxyClassName(protocolList, out _))
                        return publicType;
                }
                return _existentialHandler.GetCSharpExistentialType(protocolList);
            }
            return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }

        if (typeSpec is NamedTypeSpec namedType)
        {
            // Handle pointer types
            if (IsPointerType(namedType))
                return "IntPtr";

            // Handle Optional<T> -> use C# nullable syntax T? for simple types
            // For complex types (classes, existentials), keep Swift.SwiftOptional<T> to avoid
            // issues with closure invocation marshalling code
            if (namedType.Name == "Swift.Optional" &&
                namedType.GenericParameters.Count == 1)
            {
                var innerTypeSpec = namedType.GenericParameters[0];
                // For Optional<any Protocol>, use container type (not interface) because
                // Optional existentials use void* in P/Invoke → MarshalFromSwift<IProtocol?>
                // would throw NotSupportedException at runtime.
                string innerType;
                if (_existentialHandler.IsExistential(innerTypeSpec))
                {
                    var innerProtocolList = _existentialHandler.ToProtocolListTypeSpec(innerTypeSpec);
                    innerType = innerProtocolList != null && _existentialHandler.IsSupportedExistential(innerProtocolList)
                        ? _existentialHandler.GetCSharpExistentialType(innerProtocolList)
                        : TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
                }
                else
                {
                    innerType = TranslateTypeSpecToCSharp(innerTypeSpec);
                }

                // Use nullable syntax only for primitive/simple types
                // Keep SwiftOptional for complex types that need special marshalling
                if (IsPrimitiveType(innerTypeSpec) ||
                    innerTypeSpec.IsEmptyTuple ||
                    IsPointerType(innerTypeSpec as NamedTypeSpec))
                {
                    return $"{innerType}?";
                }

                // For complex types, use SwiftOptional wrapper
                return $"Swift.SwiftOptional<{innerType}>";
            }

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

        // Pointer types like UnsafeMutablePointer<T> resolve to IntPtr which doesn't support generics
        if (typeRecord == TypeDatabaseExtensions.IntPtrType)
        {
            return typeRecord.CSharpTypeName.FullyQualifiedName;
        }

        // Recursively translate all generic parameters
        var translatedParams = new List<string>();
        foreach (var genericParam in namedType.GenericParameters)
        {
            // Map Swift.Void (empty tuple) to SwiftVoid for generic type arguments (B8)
            // Mirrors the B3 fix in BoundGenericsHandler.TranslateBoundGenericTypeToCSharp
            if (genericParam.IsEmptyTuple)
            {
                translatedParams.Add("Swift.SwiftVoid");
                continue;
            }

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
    /// Supports primitive types, frozen structs, and non-frozen structs that can be marshalled.
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

            // Frozen structs are supported (via marshalling with stackalloc)
            if (IsFrozenStruct(namedType))
                return true;

            // Non-frozen structs are supported (via ISwiftObject marshalling with NativeMemory)
            if (IsNonFrozenStruct(namedType))
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
    /// Checks if a type is a non-frozen struct in the type database.
    /// Non-frozen structs implement ISwiftObject and can be marshalled via
    /// InitializeWithCopy/Destroy when invoking closures.
    /// </summary>
    /// <param name="typeSpec">The type specification to check.</param>
    /// <returns>True if the type is a non-frozen struct.</returns>
    public bool IsNonFrozenStruct(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;

        // Don't treat generic types as non-frozen structs - they need special handling
        if (namedType.ContainsGenericParameters)
            return false;

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
        if (!_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
            return false;

        // Must be a struct and NOT be frozen
        return typeRecord.Kind == TypeRecordKind.Struct &&
               (typeRecord.Flags & TypeRecordFlags.Frozen) == 0;
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
    /// Checks if invoking a closure from C# requires non-frozen struct marshalling for any parameter.
    /// Non-frozen structs require heap allocation via NativeMemory and InitializeWithCopy/Destroy.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>True if any parameter requires non-frozen struct marshalling.</returns>
    public bool RequiresNonFrozenMarshalling(ClosureTypeSpec closureTypeSpec)
    {
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (IsNonFrozenStruct(arg))
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
    private static bool IsPointerType(NamedTypeSpec? namedType)
    {
        if (namedType == null) return false;
        return namedType.Name == "Swift.UnsafePointer" ||
               namedType.Name == "Swift.UnsafeMutablePointer" ||
               namedType.Name == "Swift.UnsafeRawPointer" ||
               namedType.Name == "Swift.UnsafeMutableRawPointer" ||
               namedType.Name == "Swift.OpaquePointer" ||
               namedType.Name == "Builtin.RawPointer";
    }

    /// <summary>
    /// Checks if a type spec is a primitive type (bool, int, float, etc.).
    /// Primitive types can safely use C# nullable syntax (T?).
    /// </summary>
    private bool IsPrimitiveType(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;

        // Check against known primitive type names
        return namedType.Name switch
        {
            "Swift.Bool" => true,
            "Swift.Int" => true,
            "Swift.Int8" => true,
            "Swift.Int16" => true,
            "Swift.Int32" => true,
            "Swift.Int64" => true,
            "Swift.UInt" => true,
            "Swift.UInt8" => true,
            "Swift.UInt16" => true,
            "Swift.UInt32" => true,
            "Swift.UInt64" => true,
            "Swift.Float" => true,
            "Swift.Double" => true,
            "Swift.Float16" => true,
            "Swift.Float32" => true,
            "Swift.Float80" => true,
            _ => false
        };
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

    /// <summary>
    /// Determines whether a direct existential type spec in a closure parameter needs proxy wrapping
    /// when converting from the blittable ExistentialContainer to the protocol interface type.
    /// Only applies to direct existentials (not Optional-wrapped ones).
    /// </summary>
    /// <param name="typeSpec">The type specification of the closure parameter.</param>
    /// <param name="proxyClassName">The proxy class name to use for wrapping, if applicable.</param>
    /// <returns>True if the parameter needs proxy construction; false otherwise.</returns>
    public bool NeedsProxyWrapping(TypeSpec typeSpec, out string proxyClassName)
    {
        proxyClassName = "";
        if (!_existentialHandler.IsExistential(typeSpec)) return false;
        var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
        if (protocolList == null || !_existentialHandler.AllProtocolsHaveTypeRecords(protocolList)) return false;
        var publicType = _existentialHandler.GetPublicExistentialType(protocolList);
        if (publicType == "object") return false;
        // P1 fix: Only wrap when filtered protocol count matches original count.
        // If ObjC filtering drops protocols, the proxy class constructor accepts
        // ExistentialContainer{filteredCount} but P/Invoke passes ExistentialContainer{originalCount}.
        if (!_existentialHandler.TryGetFilteredProxyClassName(protocolList, out proxyClassName))
            return false;
        var filteredCount = protocolList.Protocols.Keys
            .Count(p => !TypeDatabaseExtensions.IsObjCModuleType(p));
        if (filteredCount != protocolList.Protocols.Count)
        {
            proxyClassName = "";
            return false;
        }
        return true;
    }
}
