// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Provides methods for emitting closure-related code, including callback functions
/// and marshalling setup for Swift closures.
/// </summary>
public static partial class ClosureEmitter
{
    /// <summary>
    /// Emits an [UnmanagedCallersOnly] callback function that can be used as a Swift closure's
    /// function pointer. The callback extracts the delegate from the context and invokes it.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="methodName">The name of the method containing the closure parameter.</param>
    /// <param name="parameterName">The name of the closure parameter.</param>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <param name="mangledName">The mangled name of the method (for callback disambiguation).</param>
    /// <param name="useCdecl">When true, emit CallConvCdecl with IntPtr context instead of CallConvSwift with SwiftSelf.</param>
    public static void EmitEscapingClosureCallback(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string mangledName,
        bool useCdecl = false)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName);
        var delegateType = closureHandler.GetCSharpDelegateType(closureTypeSpec);

        // Build parameter list for the callback (arguments + context as last param)
        var parameters = new List<string>();
        var argTypes = new List<TypeSpec>();
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            var paramType = GetCallbackParameterType(arg, closureHandler);
            parameters.Add($"{paramType} arg{argIndex}");
            argTypes.Add(arg);
            argIndex++;
        }
        // Cdecl: context is a plain IntPtr parameter.
        // Swift: context is passed in the Swift "self" register via SwiftSelf.
        parameters.Add(useCdecl ? "IntPtr contextPtr" : "SwiftSelf context");

        var returnType = GetEscapingClosureCallbackReturnType(closureTypeSpec, closureHandler);

        var parametersString = string.Join(", ", parameters);

        // Build argument list for invoking the delegate
        // Handle type conversions: byte->bool, void*->struct marshalling
        var invokeArgs = new List<string>();
        for (int i = 0; i < argIndex; i++)
        {
            var argExpr = GetInvokeArgExpression(argTypes[i], i, closureHandler);
            invokeArgs.Add(argExpr);
        }
        var invokeArgsString = string.Join(", ", invokeArgs);

        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;

        // Build the return statement using the shared conversion logic
        string returnStatement;
        if (!hasReturn)
        {
            returnStatement = $"del({invokeArgsString});";
        }
        else
        {
            returnStatement = BuildCallbackReturnStatement(
                closureTypeSpec.ReturnType,
                $"del({invokeArgsString})",
                closureHandler,
                returnType);
        }

        var callConvType = useCdecl ? "typeof(global::System.Runtime.CompilerServices.CallConvCdecl)" : "typeof(global::System.Runtime.CompilerServices.CallConvSwift)";
        var contextExtraction = useCdecl ? "contextPtr" : "new IntPtr(context.Value)";

        // Callback never frees the GCHandle — the calling method's finally block handles cleanup.
        // Escaping closures may fire multiple times (e.g., callMultipleTimes), so freeing in the
        // callback would crash on the second invocation.
        csWriter.WriteLines($$"""
            [UnmanagedCallersOnly(CallConvs = new[] { {{callConvType}} })]
            private static unsafe {{returnType}} {{callbackName}}({{parametersString}})
            {
                var del = SwiftClosureMarshaller.GetDelegateFromContext<{{delegateType}}>({{contextExtraction}});
                {{returnStatement}}
            }
            """);
    }

    /// <summary>
    /// Emits the static field that holds the function pointer for the callback.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="methodName">The name of the method containing the closure parameter.</param>
    /// <param name="parameterName">The name of the closure parameter.</param>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <param name="mangledName">The mangled name of the method (for callback disambiguation).</param>
    /// <param name="useCdecl">When true, emit Cdecl function pointer type with IntPtr context.</param>
    public static void EmitClosureCallbackPointer(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string mangledName,
        bool useCdecl = false)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName);
        var funcPtrType = BuildEscapingClosureCallbackFunctionPointerType(closureTypeSpec, closureHandler, useCdecl);

        // Add context parameter to the function pointer type
        var funcPtrTypeWithContext = useCdecl
            ? AddCdeclContextToFunctionPointerType(funcPtrType)
            : AddContextToFunctionPointerType(funcPtrType);

        csWriter.WriteLine($"private static unsafe readonly {funcPtrTypeWithContext} s_{callbackName} = &{callbackName};");
    }

    /// <summary>
    /// Emits code to create closure data from a delegate for escaping closures.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="methodName">The name of the method containing the closure parameter.</param>
    /// <param name="parameterName">The name of the closure parameter.</param>
    /// <param name="closureHandler">The closure handler.</param>
    /// <param name="mangledName">The mangled name of the method (for callback disambiguation).</param>
    public static void EmitEscapingClosureMarshallingSetup(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureHandler closureHandler,
        string mangledName)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName);
        var closureDataVar = $"{parameterName}ClosureData";
        var handleVar = $"{parameterName}Handle";

        csWriter.WriteLines($$"""
            var {{handleVar}} = GCHandle.Alloc({{parameterName}});
            var {{closureDataVar}} = new SwiftClosureData((IntPtr)s_{{callbackName}}, GCHandle.ToIntPtr({{handleVar}}));
            """);
    }

    /// <summary>
    /// Emits code to clean up escaping closure resources in a finally block.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="parameterName">The name of the closure parameter.</param>
    public static void EmitEscapingClosureCleanup(
        CSharpWriter csWriter,
        string parameterName)
    {
        var handleVar = $"{parameterName}Handle";

        csWriter.WriteLine($"if ({handleVar}.IsAllocated) {handleVar}.Free();");
    }

    /// <summary>
    /// Emits code to convert a SwiftClosureData return value into a C# delegate.
    /// When an invoke thunk is available, creates a nested invoker class that calls the
    /// @_cdecl invoke thunk via [LibraryImport] P/Invoke. The delegate is created via
    /// method group (invoker.Invoke) to avoid lambdas — lambdas create display classes,
    /// and Mono JIT crashes when native calls happen from display class methods.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <param name="resultVariableName">The name of the variable holding the SwiftClosureData result.</param>
    /// <param name="invokeThunkEntryPoint">Optional @_cdecl entry point name for the invoke thunk.</param>
    /// <param name="invokeThunkLibrary">The framework library name for the invoke thunk (e.g., "SwiftBindings").
    /// Required when invokeThunkEntryPoint is non-null.</param>
    /// <param name="invokeThunkHelper">The name of the [LibraryImport] P/Invoke method for the thunk.
    /// Required when invokeThunkEntryPoint is non-null.</param>
    public static void EmitClosureReturnMarshalling(
        CSharpWriter csWriter,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string resultVariableName = "result",
        string? invokeThunkEntryPoint = null,
        string? invokeThunkLibrary = null,
        string? invokeThunkHelper = null)
    {
        var delegateType = closureHandler.GetCSharpDelegateType(closureTypeSpec);

        // Build lambda parameter list
        var parameters = new List<string>();
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            parameters.Add($"_arg{argIndex}");
            argIndex++;
        }
        var parametersString = string.Join(", ", parameters);
        var parameterListWithParens = parameters.Count == 1 ? parametersString : $"({parametersString})";

        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        var returnIsBool = hasReturn && MarshallingHelpers.IsBoolType(closureTypeSpec.ReturnType);

        // When an invoke thunk is available, use the pre-emitted invoker class instead of a lambda.
        // Mono JIT crashes with assertion `!ji->async` when ANY native call mechanism is invoked
        // from a lambda/display class method. The invoker class has a regular Invoke method —
        // the delegate is created via method group, eliminating the display class entirely.
        if (invokeThunkEntryPoint != null && invokeThunkHelper != null)
        {
            var invokerClassName = GetInvokerClassName(invokeThunkHelper);

            csWriter.WriteLines($$"""
                // Wrap Swift closure in SwiftEscapingClosure for ARC management
                var _closureWrapper = SwiftEscapingClosure<{{delegateType}}>.FromSwift({{resultVariableName}}.FunctionPointer, {{resultVariableName}}.Context);

                // Use invoker class instead of lambda — Mono JIT crashes with !ji->async when
                // native calls happen from display class methods (lambdas create display classes).
                var _inv = new {{invokerClassName}}((nint)_closureWrapper.FunctionPointer, (nint)_closureWrapper.Context);
                {{delegateType}} _invoker = _inv.Invoke;

                return _invoker;
                """);
            return;
        }

        // Fallback: direct delegate* unmanaged[Swift] invocation (for closures without invoke thunks)
        var funcPtrType = closureHandler.GetPInvokeFunctionPointerType(closureTypeSpec);
        var funcPtrTypeWithContext = AddContextToFunctionPointerType(funcPtrType);

        // Build argument list for invoking the Swift function
        // Need to convert C# types to Swift types (e.g., bool -> byte, AnyError -> EC1)
        var invokeArgsFallback = new List<string>();
        argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            var argExpr = GetSwiftInvokeArgExpression(arg, argIndex, closureHandler);
            invokeArgsFallback.Add(argExpr);
            argIndex++;
        }
        // Add context (SwiftSelf) as last argument
        invokeArgsFallback.Add("_swiftSelf");
        var invokeArgsStringFallback = string.Join(", ", invokeArgsFallback);

        // Generate the closure body
        // For well-known protocol returns (ExistentialContainer1 from P/Invoke → AnyError for delegate)
        string invokeExprFallback = $"_fp({invokeArgsStringFallback})";
        string returnExprFallback;
        if (!hasReturn)
        {
            returnExprFallback = $"{invokeExprFallback};";
        }
        else if (returnIsBool)
        {
            returnExprFallback = $"return {invokeExprFallback} != 0;";
        }
        else if (closureHandler.NeedsWellKnownProtocolWrapping(closureTypeSpec.ReturnType, out var wrapReturnType))
        {
            returnExprFallback = $"return new {wrapReturnType}({invokeExprFallback});";
        }
        else if (closureHandler.NeedsProxyWrapping(closureTypeSpec.ReturnType, out var returnProxy))
        {
            returnExprFallback = $"return new {returnProxy}({invokeExprFallback});";
        }
        else if (closureHandler.IsExistentialParam(closureTypeSpec.ReturnType))
        {
            returnExprFallback = $"return (object){invokeExprFallback};";
        }
        else if (closureTypeSpec.ReturnType is TupleTypeSpec invRetTuple &&
                 invRetTuple.Elements.Any(e => closureHandler.NeedsWellKnownProtocolWrapping(e, out _) ||
                                                closureHandler.NeedsProxyWrapping(e, out _) ||
                                                closureHandler.IsExistentialParam(e) ||
                                                closureHandler.IsSimpleEnum(e)))
        {
            var elems = new List<string>();
            for (int i = 0; i < invRetTuple.Elements.Count; i++)
            {
                var elem = invRetTuple.Elements[i];
                var acc = $"_invResult.Item{i + 1}";
                if (closureHandler.NeedsWellKnownProtocolWrapping(elem, out var wrt))
                    elems.Add($"new {wrt}({acc})");
                else if (closureHandler.NeedsProxyWrapping(elem, out var prn))
                    elems.Add($"new {prn}({acc})");
                else if (closureHandler.IsExistentialParam(elem))
                    elems.Add($"(object){acc}");
                else if (closureHandler.IsSimpleEnum(elem))
                {
                    var enumType = closureHandler.TranslateTypeSpecToCSharp(elem);
                    elems.Add($"({enumType}){acc}");
                }
                else
                    elems.Add(acc);
            }
            returnExprFallback = $"""
                    var _invResult = {invokeExprFallback};
                            return ({string.Join(", ", elems)});
                """;
        }
        else if (closureHandler.IsSimpleEnum(closureTypeSpec.ReturnType))
        {
            // Simple enum return: Swift returns underlying integer, delegate expects C# enum
            var enumCsType = closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true);
            returnExprFallback = $"return ({enumCsType}){invokeExprFallback};";
        }
        else
        {
            returnExprFallback = $"return {invokeExprFallback};";
        }

        csWriter.WriteLines($$"""
            // Wrap Swift closure in SwiftEscapingClosure for ARC management
            var _closureWrapper = SwiftEscapingClosure<{{delegateType}}>.FromSwift({{resultVariableName}}.FunctionPointer, {{resultVariableName}}.Context);

            // Create invoker delegate that captures wrapper (keeps it alive for proper ARC)
            {{delegateType}} _invoker = {{parameterListWithParens}} =>
            {
                unsafe
                {
                    var _fp = ({{funcPtrTypeWithContext}})_closureWrapper.FunctionPointer;
                    var _swiftSelf = new SwiftSelf((void*)_closureWrapper.Context.ToPointer());
                    {{returnExprFallback}}
                }
            };

            return _invoker;
            """);
    }

    /// <summary>
    /// Builds the return statement for a closure callback that marshals the result value
    /// from C# types to P/Invoke types. Shared between escaping and throwing callback emitters
    /// to ensure all return type cases are handled consistently.
    /// </summary>
    /// <param name="returnType">The closure's return TypeSpec.</param>
    /// <param name="resultExpr">The expression that produces the result (e.g., "del(args)" or "swiftResult.Success").</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <param name="callbackReturnType">The callback's declared return type string (e.g., "void*", "byte").</param>
    /// <returns>One or more lines of C# code for the return statement.</returns>
    internal static string BuildCallbackReturnStatement(
        TypeSpec returnType,
        string resultExpr,
        ClosureHandler closureHandler,
        string callbackReturnType)
    {
        if (MarshallingHelpers.IsBoolType(returnType))
            return $"return (byte)({resultExpr} ? 1 : 0);";

        if (closureHandler.NeedsWellKnownProtocolWrapping(returnType, out _))
            return $"return {resultExpr}.GetExistentialContainer();";

        if (closureHandler.NeedsProxyWrapping(returnType, out _))
        {
            if (closureHandler.ShouldUseGetOrCreate(returnType))
            {
                var pt = closureHandler.GetPublicExistentialType(returnType) ?? "object";
                return $"return Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{pt}>({resultExpr});";
            }
            var ct = closureHandler.GetPInvokeExistentialType(returnType);
            return $"return ((Swift.Runtime.ISwiftExistentialConvertible<{ct}>){resultExpr}).GetExistentialContainer();";
        }

        if (closureHandler.IsExistentialParam(returnType))
        {
            var ct = closureHandler.GetPInvokeExistentialType(returnType);
            return $"return ({ct}){resultExpr};";
        }

        if (callbackReturnType == "void*" && returnType is NamedTypeSpec retNamedType && IsPointerType(retNamedType))
            return $"return (void*){resultExpr};";

        if (callbackReturnType == "void*" && closureHandler.IsClassType(returnType))
            return $"return (void*){resultExpr}.Payload.DangerousGetHandle();";

        if (callbackReturnType == "void*" && closureHandler.IsObjCBridgedClass(returnType))
            return $"return (void*){resultExpr}.Handle;";

        if (callbackReturnType == "void*" && IsOptionalReferenceReturn(returnType, closureHandler))
        {
            var isClass = closureHandler.IsClassType(((NamedTypeSpec)returnType).GenericParameters[0]);
            if (isClass)
                return $$"""
                    var _optResult = {{resultExpr}};
                            return _optResult != null ? (void*)_optResult.Payload.DangerousGetHandle() : null;
                    """;
            else
                return $$"""
                    var _optResult = {{resultExpr}};
                            return _optResult != null ? (void*)_optResult.Handle : null;
                    """;
        }

        if (returnType is TupleTypeSpec retTuple &&
            retTuple.Elements.Any(e => closureHandler.NeedsWellKnownProtocolWrapping(e, out _) ||
                                        closureHandler.NeedsProxyWrapping(e, out _) ||
                                        closureHandler.IsExistentialParam(e) ||
                                        closureHandler.IsSimpleEnum(e)))
        {
            var elems = new List<string>();
            for (int i = 0; i < retTuple.Elements.Count; i++)
            {
                var elem = retTuple.Elements[i];
                var acc = $"_tupleResult.Item{i + 1}";
                if (closureHandler.NeedsWellKnownProtocolWrapping(elem, out _))
                    elems.Add($"{acc}.GetExistentialContainer()");
                else if (closureHandler.NeedsProxyWrapping(elem, out _))
                {
                    if (closureHandler.ShouldUseGetOrCreate(elem))
                    {
                        var pt = closureHandler.GetPublicExistentialType(elem) ?? "object";
                        elems.Add($"Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{pt}>({acc})");
                    }
                    else
                    {
                        var ct = closureHandler.GetPInvokeExistentialType(elem);
                        elems.Add($"((Swift.Runtime.ISwiftExistentialConvertible<{ct}>){acc}).GetExistentialContainer()");
                    }
                }
                else if (closureHandler.IsExistentialParam(elem))
                {
                    var ct = closureHandler.GetPInvokeExistentialType(elem);
                    elems.Add($"({ct}){acc}");
                }
                else if (closureHandler.IsSimpleEnum(elem))
                {
                    var underlyingType = closureHandler.GetSimpleEnumInfo(elem)?.csUnderlying ?? "int";
                    elems.Add($"({underlyingType}){acc}");
                }
                else
                    elems.Add(acc);
            }
            return $"""
                    var _tupleResult = {resultExpr};
                            return ({string.Join(", ", elems)});
                """;
        }

        if (closureHandler.IsSimpleEnum(returnType))
        {
            var underlyingType = closureHandler.GetSimpleEnumInfo(returnType)?.csUnderlying ?? "int";
            return $"return ({underlyingType}){resultExpr};";
        }

        if (callbackReturnType == "void*" && !closureHandler.CanUseDirectCallbackReturn(returnType))
        {
            var csharpRetType = closureHandler.TranslateTypeSpecToCSharp(returnType, isReturnType: true);
            return $"""
                    var _result = {resultExpr};
                            var _resultMetadata = TypeMetadata.GetTypeMetadataOrThrow<{csharpRetType}>();
                            var _resultBuffer = (void*)NativeMemory.Alloc(_resultMetadata.Size);
                            var _resultSpan = new Span<byte>(_resultBuffer, (int)_resultMetadata.Size);
                            SwiftMarshal.MarshalToSwift(_result, ref _resultSpan);
                            return _resultBuffer;
                """;
        }

        return $"return {resultExpr};";
    }

    /// <summary>
    /// Gets the C# type for a closure callback parameter.
    /// Delegates to ClosureHandler.TranslateTypeSpecToPInvokeType for consistency
    /// between the callback signature and function pointer type declaration.
    /// </summary>
    private static string GetCallbackParameterType(TypeSpec typeSpec, ClosureHandler closureHandler)
    {
        return closureHandler.TranslateTypeSpecToPInvokeType(typeSpec);
    }

    /// <summary>
    /// Checks if a type is a Swift pointer type.
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
    /// Generates the expression to pass an argument when invoking the delegate.
    /// Handles type conversions:
    /// - byte -> bool for Swift.Bool
    /// - void* -> struct marshalling for complex types
    /// </summary>
    /// <param name="typeSpec">The TypeSpec for the argument.</param>
    /// <param name="argIndex">The argument index.</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <returns>The expression string to use when invoking the delegate.</returns>
    private static string GetInvokeArgExpression(TypeSpec typeSpec, int argIndex, ClosureHandler closureHandler)
    {
        // Bool requires byte->bool conversion
        if (MarshallingHelpers.IsBoolType(typeSpec))
            return $"arg{argIndex} != 0";

        // Simple enum: callback receives underlying integer, delegate expects C# enum → cast
        if (closureHandler.IsSimpleEnum(typeSpec))
        {
            var delegateType = closureHandler.TranslateTypeSpecToCSharp(typeSpec);
            return $"({delegateType})arg{argIndex}";
        }

        // Tuple with existential elements: decompose and convert each element
        if (typeSpec is TupleTypeSpec tupleSpec)
        {
            bool needsConversion = tupleSpec.Elements.Any(e =>
                closureHandler.NeedsWellKnownProtocolWrapping(e, out _) ||
                closureHandler.NeedsProxyWrapping(e, out _) ||
                closureHandler.IsExistentialParam(e) ||
                closureHandler.IsSimpleEnum(e));
            if (needsConversion)
            {
                var elements = new List<string>();
                for (int i = 0; i < tupleSpec.Elements.Count; i++)
                {
                    var elem = tupleSpec.Elements[i];
                    var accessor = $"arg{argIndex}.Item{i + 1}";
                    if (closureHandler.NeedsWellKnownProtocolWrapping(elem, out var wt))
                        elements.Add($"new {wt}({accessor})");
                    else if (closureHandler.NeedsProxyWrapping(elem, out var pn))
                        elements.Add($"new {pn}({accessor})");
                    else if (closureHandler.IsExistentialParam(elem))
                        elements.Add($"(object){accessor}");
                    else if (closureHandler.IsSimpleEnum(elem))
                    {
                        var enumType = closureHandler.TranslateTypeSpecToCSharp(elem);
                        elements.Add($"({enumType}){accessor}");
                    }
                    else
                        elements.Add(accessor);
                }
                return $"({string.Join(", ", elements)})";
            }
        }

        // Check if this parameter needs marshalling from void*
        var callbackType = GetCallbackParameterType(typeSpec, closureHandler);
        if (callbackType == "void*" && typeSpec is NamedTypeSpec namedType)
        {
            if (IsPointerType(namedType))
            {
                // Pointer types (OpaquePointer, UnsafeRawPointer, etc.) are void* in the callback
                // but IntPtr in the delegate — just cast.
                return $"new IntPtr(arg{argIndex})";
            }

            // Optional<Class>: void* → null check → MarshalFromSwift or null
            if (IsOptionalReferenceParam(namedType, closureHandler))
            {
                var inner = namedType.GenericParameters[0];
                var innerType = closureHandler.TranslateTypeSpecToCSharp(inner);
                if (closureHandler.IsClassType(inner))
                    return $"arg{argIndex} != null ? SwiftMarshal.MarshalFromSwift<{innerType}>(new IntPtr(arg{argIndex})) : null";
                else // ObjC-bridged
                    return $"arg{argIndex} != null ? {MarshallingHelpers.FormatObjCBridgeCall(innerType, $"new IntPtr(arg{argIndex})")} : null";
            }

            // The callback receives void* but the delegate expects the actual type.
            // Use SwiftMarshal.MarshalFromSwift to convert.
            var delegateType = closureHandler.TranslateTypeSpecToCSharp(typeSpec);
            return $"SwiftMarshal.MarshalFromSwift<{delegateType}>(new IntPtr(arg{argIndex}))";
        }

        // Well-known protocol wrapping (e.g., any Swift.Error → AnyError)
        if (closureHandler.NeedsWellKnownProtocolWrapping(typeSpec, out var wrapType))
            return $"new {wrapType}(arg{argIndex})";

        // Direct existential params: P/Invoke type is ExistentialContainer (blittable),
        // but delegate type is now the protocol interface. Wrap with proxy constructor.
        if (closureHandler.NeedsProxyWrapping(typeSpec, out var proxyName))
            return $"new {proxyName}(arg{argIndex})";

        // Unknown protocol: box ExistentialContainer to object for delegate
        if (closureHandler.IsExistentialParam(typeSpec))
            return $"(object)arg{argIndex}";

        return $"arg{argIndex}";
    }

    /// <summary>
    /// Checks if a type is Optional&lt;Class/ObjC&gt; with nil-pointer ABI (parameter direction).
    /// </summary>
    private static bool IsOptionalReferenceParam(NamedTypeSpec namedType, ClosureHandler closureHandler)
    {
        return namedType.ContainsGenericParameters &&
               namedType.Name == "Swift.Optional" &&
               namedType.GenericParameters.Count == 1 &&
               closureHandler.IsReferenceType(namedType.GenericParameters[0]);
    }

    /// <summary>
    /// Checks if a return type is Optional&lt;Class/ObjC&gt; with nil-pointer ABI.
    /// </summary>
    private static bool IsOptionalReferenceReturn(TypeSpec typeSpec, ClosureHandler closureHandler)
    {
        return typeSpec is NamedTypeSpec named &&
               named.ContainsGenericParameters &&
               named.Name == "Swift.Optional" &&
               named.GenericParameters.Count == 1 &&
               closureHandler.IsReferenceType(named.GenericParameters[0]);
    }


    /// <summary>
    /// Adds SwiftSelf context parameter to a function pointer type string.
    /// SwiftSelf is used because Swift passes closure context in the "self" register.
    /// </summary>
    private static string AddContextToFunctionPointerType(string funcPtrType)
    {
        // Transform "delegate* unmanaged[Swift]<int, void>" to "delegate* unmanaged[Swift]<int, SwiftSelf, void>"
        // The context is the last parameter before the return type

        int lastAngle = funcPtrType.LastIndexOf('>');
        if (lastAngle == -1)
            return funcPtrType;

        // Use nesting-aware search to skip commas inside generic type arguments
        int lastComma = EmitterUtility.FindLastTopLevelComma(funcPtrType, lastAngle);
        if (lastComma == -1)
        {
            // No parameters, just return type: "delegate* unmanaged[Swift]<void>"
            int openAngle = funcPtrType.IndexOf('<');
            if (openAngle == -1)
                return funcPtrType;

            return funcPtrType.Insert(openAngle + 1, "SwiftSelf, ");
        }

        return funcPtrType.Insert(lastComma + 1, " SwiftSelf,");
    }

    /// <summary>
    /// Gets the return type for escaping/throwing closure callbacks.
    /// Frozen structs and primitive scalars can return by value directly.
    /// </summary>
    private static string GetEscapingClosureCallbackReturnType(
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler)
    {
        if (closureTypeSpec.ReturnType.IsEmptyTuple)
            return "void";

        if (MarshallingHelpers.IsBoolType(closureTypeSpec.ReturnType))
            return "byte";

        // Simple enum returns use their underlying integer type (blittable)
        if (closureHandler.IsSimpleEnum(closureTypeSpec.ReturnType))
        {
            var enumInfo = closureHandler.GetSimpleEnumInfo(closureTypeSpec.ReturnType);
            return enumInfo?.csUnderlying ?? "int";
        }

        if (closureHandler.CanUseDirectCallbackReturn(closureTypeSpec.ReturnType))
            return closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true);

        return GetCallbackParameterType(closureTypeSpec.ReturnType, closureHandler);
    }

    /// <summary>
    /// Builds the function pointer type for an escaping closure callback.
    /// </summary>
    private static string BuildEscapingClosureCallbackFunctionPointerType(
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        bool useCdecl = false)
    {
        var types = new List<string>();
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            types.Add(GetCallbackParameterType(arg, closureHandler));
        }

        types.Add(GetEscapingClosureCallbackReturnType(closureTypeSpec, closureHandler));
        var callConv = useCdecl ? "Cdecl" : "Swift";
        return $"delegate* unmanaged[{callConv}]<{string.Join(", ", types)}>";
    }

    /// <summary>
    /// Builds the function pointer type for a throwing closure callback.
    /// </summary>
    private static string BuildThrowingClosureCallbackFunctionPointerType(
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        bool useCdecl = false)
    {
        var types = new List<string>();
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            types.Add(GetCallbackParameterType(arg, closureHandler));
        }

        types.Add("SwiftError*");
        types.Add(closureTypeSpec.ReturnType.IsEmptyTuple
            ? "void"
            : GetEscapingClosureCallbackReturnType(closureTypeSpec, closureHandler));
        var callConv = useCdecl ? "Cdecl" : "Swift";
        return $"delegate* unmanaged[{callConv}]<{string.Join(", ", types)}>";
    }

    /// <summary>
    /// Adds IntPtr context parameter to a Cdecl function pointer type string.
    /// Used for closure Cdecl wrapper path where context is a plain IntPtr (not SwiftSelf).
    /// </summary>
    internal static string AddCdeclContextToFunctionPointerType(string funcPtrType)
    {
        // Transform "delegate* unmanaged[Cdecl]<int, void>" to "delegate* unmanaged[Cdecl]<int, IntPtr, void>"
        int lastAngle = funcPtrType.LastIndexOf('>');
        if (lastAngle == -1)
            return funcPtrType;

        // Use nesting-aware search to skip commas inside generic type arguments
        int lastComma = EmitterUtility.FindLastTopLevelComma(funcPtrType, lastAngle);
        if (lastComma == -1)
        {
            // No parameters, just return type: "delegate* unmanaged[Cdecl]<void>"
            int openAngle = funcPtrType.IndexOf('<');
            if (openAngle == -1)
                return funcPtrType;

            return funcPtrType.Insert(openAngle + 1, "IntPtr, ");
        }

        return funcPtrType.Insert(lastComma + 1, " IntPtr,");
    }

    /// <summary>
    /// Generates the expression to convert a C# argument to Swift-compatible form when invoking a Swift closure.
    /// </summary>
    private static string GetSwiftInvokeArgExpression(TypeSpec typeSpec, int argIndex, ClosureHandler? closureHandler = null)
    {
        // Bool requires bool -> byte conversion
        if (MarshallingHelpers.IsBoolType(typeSpec))
            return $"(byte)(_arg{argIndex} ? 1 : 0)";

        // Simple enum: delegate passes C# enum, Swift function pointer expects underlying int
        if (closureHandler != null && closureHandler.IsSimpleEnum(typeSpec))
        {
            var underlyingType = closureHandler.GetSimpleEnumInfo(typeSpec)?.csUnderlying ?? "int";
            return $"({underlyingType})_arg{argIndex}";
        }

        // Well-known protocol types: unwrap to ExistentialContainer for function pointer
        if (closureHandler != null && closureHandler.NeedsWellKnownProtocolWrapping(typeSpec, out _))
            return $"_arg{argIndex}.GetExistentialContainer()";

        // Known protocol: extract container from interface for function pointer
        if (closureHandler != null && closureHandler.NeedsProxyWrapping(typeSpec, out _))
        {
            if (closureHandler.ShouldUseGetOrCreate(typeSpec))
            {
                var pt = closureHandler.GetPublicExistentialType(typeSpec) ?? "object";
                return $"Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{pt}>(_arg{argIndex})";
            }
            var ct = closureHandler.GetPInvokeExistentialType(typeSpec);
            return $"((Swift.Runtime.ISwiftExistentialConvertible<{ct}>)_arg{argIndex}).GetExistentialContainer()";
        }

        // Unknown protocol: unbox object to container for function pointer
        if (closureHandler != null && closureHandler.IsExistentialParam(typeSpec))
        {
            var ct = closureHandler.GetPInvokeExistentialType(typeSpec);
            return $"({ct})_arg{argIndex}";
        }

        // Tuple with existential/enum elements: decompose and convert each element to P/Invoke type
        if (typeSpec is TupleTypeSpec invTupleSpec && closureHandler != null)
        {
            bool needsConversion = invTupleSpec.Elements.Any(e =>
                closureHandler.NeedsWellKnownProtocolWrapping(e, out _) ||
                closureHandler.NeedsProxyWrapping(e, out _) ||
                closureHandler.IsExistentialParam(e) ||
                closureHandler.IsSimpleEnum(e));
            if (needsConversion)
            {
                var elements = new List<string>();
                for (int i = 0; i < invTupleSpec.Elements.Count; i++)
                {
                    var elem = invTupleSpec.Elements[i];
                    var acc = $"_arg{argIndex}.Item{i + 1}";
                    if (closureHandler.NeedsWellKnownProtocolWrapping(elem, out _))
                        elements.Add($"{acc}.GetExistentialContainer()");
                    else if (closureHandler.NeedsProxyWrapping(elem, out _))
                    {
                        if (closureHandler.ShouldUseGetOrCreate(elem))
                        {
                            var pt = closureHandler.GetPublicExistentialType(elem) ?? "object";
                            elements.Add($"Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{pt}>({acc})");
                        }
                        else
                        {
                            var ct = closureHandler.GetPInvokeExistentialType(elem);
                            elements.Add($"((Swift.Runtime.ISwiftExistentialConvertible<{ct}>){acc}).GetExistentialContainer()");
                        }
                    }
                    else if (closureHandler.IsExistentialParam(elem))
                    {
                        var ct = closureHandler.GetPInvokeExistentialType(elem);
                        elements.Add($"({ct}){acc}");
                    }
                    else if (closureHandler.IsSimpleEnum(elem))
                    {
                        var underlyingType = closureHandler.GetSimpleEnumInfo(elem)?.csUnderlying ?? "int";
                        elements.Add($"({underlyingType}){acc}");
                    }
                    else
                        elements.Add(acc);
                }
                return $"({string.Join(", ", elements)})";
            }
        }

        // Class types: extract handle as void* for function pointer invocation
        if (closureHandler?.IsClassType(typeSpec) == true)
            return $"(void*)_arg{argIndex}.Payload.DangerousGetHandle()";

        // ObjC bridged class types: extract .Handle as void* for function pointer invocation
        if (closureHandler?.IsObjCBridgedClass(typeSpec) == true)
            return $"(void*)_arg{argIndex}.Handle";

        return $"_arg{argIndex}";
    }

    /// <summary>
    /// Checks if a @convention(c) closure needs a bool bridge for Marshal.GetFunctionPointerForDelegate.
    /// Marshal.GetFunctionPointerForDelegate creates a native thunk based on the managed delegate's
    /// signature, where bool marshals as 4-byte BOOL instead of Swift's 1-byte Bool. The bridge wraps
    /// the user's delegate in a byte-typed delegate that matches Swift's calling convention.
    /// </summary>
    internal static bool NeedsConventionCBoolBridge(ClosureTypeSpec closureTypeSpec)
    {
        if (MarshallingHelpers.IsBoolType(closureTypeSpec.ReturnType))
            return true;
        return closureTypeSpec.EachArgument().Any(a => MarshallingHelpers.IsBoolType(a));
    }

    /// <summary>
    /// Emits a bridge delegate for @convention(c) closures with bool types.
    /// Wraps the user's delegate (which uses C# bool) in a delegate that uses byte for bool,
    /// so Marshal.GetFunctionPointerForDelegate creates a thunk with the correct 1-byte return/param ABI.
    /// </summary>
    internal static void EmitConventionCBoolBridge(
        CSharpWriter csWriter,
        string originalName,
        string bridgeName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler)
    {
        var args = closureTypeSpec.EachArgument().ToList();
        bool hasBoolReturn = MarshallingHelpers.IsBoolType(closureTypeSpec.ReturnType);

        // Build bridge delegate type args and lambda
        var bridgeTypeArgs = new List<string>();
        var lambdaParams = new List<string>();
        var forwardArgs = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            lambdaParams.Add($"_ba{i}");
            if (MarshallingHelpers.IsBoolType(args[i]))
            {
                bridgeTypeArgs.Add("byte");
                forwardArgs.Add($"_ba{i} != 0");
            }
            else
            {
                bridgeTypeArgs.Add(closureHandler.TranslateTypeSpecToCSharp(args[i]));
                forwardArgs.Add($"_ba{i}");
            }
        }

        string callExpr = $"{originalName}({string.Join(", ", forwardArgs)})";
        string lambdaParamsStr = string.Join(", ", lambdaParams);

        if (closureTypeSpec.ReturnType.IsEmptyTuple)
        {
            string delegateType = bridgeTypeArgs.Count > 0
                ? $"Action<{string.Join(", ", bridgeTypeArgs)}>"
                : "Action";
            csWriter.WriteLine($"{delegateType} {bridgeName} = ({lambdaParamsStr}) => {{ {callExpr}; }};");
        }
        else
        {
            if (hasBoolReturn)
                bridgeTypeArgs.Add("byte");
            else
                bridgeTypeArgs.Add(closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true));

            string delegateType = $"Func<{string.Join(", ", bridgeTypeArgs)}>";
            string returnExpr = hasBoolReturn ? $"(byte)({callExpr} ? 1 : 0)" : callExpr;
            csWriter.WriteLine($"{delegateType} {bridgeName} = ({lambdaParamsStr}) => {returnExpr};");
        }
    }
}
