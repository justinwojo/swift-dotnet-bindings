// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public static partial class ClosureEmitter
{
    /// <summary>
    /// Emits an [UnmanagedCallersOnly] callback function for a throwing closure.
    /// The callback invokes the C# delegate (which returns SwiftResult) and marshals the error
    /// via the SwiftError* out parameter if the result is a failure.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="methodName">The name of the method containing the closure parameter.</param>
    /// <param name="parameterName">The name of the closure parameter.</param>
    /// <param name="closureTypeSpec">The closure type specification (must have Throws=true).</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <param name="mangledName">The mangled name of the method (for callback disambiguation).</param>
    /// <param name="useCdecl">When true, emit CallConvCdecl with IntPtr context instead of CallConvSwift with SwiftSelf.</param>
    public static void EmitThrowingClosureCallback(
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

        // Build parameter list: arguments..., SwiftError* errorOut, context
        var parameters = new List<string>();
        var argTypes = new List<TypeSpec>();
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            // UnsafeRawBufferPointer / UnsafeMutableRawBufferPointer: split into (ptr, len)
            // pair to match the Swift @convention(c) decomposition. Mirrors the expansion in
            // EmitEscapingClosureCallback and BuildThrowingClosureCallbackFunctionPointerType.
            if (useCdecl && MarshallingHelpers.IsAnyUnsafeRawBufferPointer(arg))
            {
                parameters.Add($"void* arg{argIndex}");
                parameters.Add($"nint arg{argIndex}_len");
            }
            else
            {
                var paramType = GetCallbackParameterType(arg, closureHandler, useCdecl);
                parameters.Add($"{paramType} arg{argIndex}");
            }
            argTypes.Add(arg);
            argIndex++;
        }
        // Error out parameter before context
        parameters.Add("SwiftError* errorOut");
        // Cdecl: context is a plain IntPtr. Swift: context via SwiftSelf register.
        parameters.Add(useCdecl ? "IntPtr contextPtr" : "SwiftSelf context");

        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        var returnType = hasReturn
            ? GetEscapingClosureCallbackReturnType(closureTypeSpec, closureHandler)
            : "void";

        var parametersString = string.Join(", ", parameters);

        // Build argument list for invoking the delegate
        var invokeArgs = new List<string>();
        for (int i = 0; i < argIndex; i++)
        {
            var argExpr = GetInvokeArgExpression(argTypes[i], i, closureHandler, useCdecl);
            invokeArgs.Add(argExpr);
        }
        var invokeArgsString = string.Join(", ", invokeArgs);

        // Determine the success type for the SwiftResult
        var successType = hasReturn ? closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true) : "Swift.SwiftVoid";
        var returnIsBool = hasReturn && MarshallingHelpers.IsBoolType(closureTypeSpec.ReturnType);

        var callConvType = useCdecl ? "typeof(global::System.Runtime.CompilerServices.CallConvCdecl)" : "typeof(global::System.Runtime.CompilerServices.CallConvSwift)";
        var contextExtraction = useCdecl ? "contextPtr" : "new IntPtr(context.Value)";

        // Callback never frees the GCHandle — the calling method's finally block handles cleanup.
        // Escaping closures may fire multiple times, so freeing in the callback would crash.
        var indent = "            ";

        csWriter.WriteLines($$"""
            [UnmanagedCallersOnly(CallConvs = new[] { {{callConvType}} })]
            private static unsafe {{returnType}} {{callbackName}}({{parametersString}})
            {
                var del = SwiftClosureMarshaller.GetDelegateFromContext<{{delegateType}}>({{contextExtraction}});
                var swiftResult = del({{invokeArgsString}});

                if (swiftResult.IsFailure)
                {
                    // Set the error out parameter
                    *errorOut = swiftResult.Failure;
            """);

        if (hasReturn)
        {
            if (returnIsBool)
            {
                csWriter.WriteLine($"{indent}    return 0; // Return default value on error");
            }
            else
            {
                csWriter.WriteLine($"{indent}    return default; // Return default value on error");
            }
        }
        else
        {
            // Void-return: explicit early return so we don't fall through
            // to the success block and clobber *errorOut with default.
            csWriter.WriteLine($"{indent}    return;");
        }

        csWriter.WriteLines($$"""
            {{indent}}}

            {{indent}}// Success case - no error
            {{indent}}*errorOut = default;
            """);

        if (hasReturn)
        {
            // Use the shared return conversion logic — same cases as EmitEscapingClosureCallback
            var successReturn = BuildCallbackReturnStatement(
                closureTypeSpec.ReturnType,
                "swiftResult.Success",
                closureHandler,
                returnType);
            csWriter.WriteLine($"{indent}{successReturn}");
        }

        csWriter.WriteLine("        }");
    }

    /// <summary>
    /// Emits the static field that holds the function pointer for a throwing closure callback.
    /// </summary>
    /// <param name="useCdecl">When true, emit Cdecl function pointer type with IntPtr context.</param>
    public static void EmitThrowingClosureCallbackPointer(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string mangledName,
        bool useCdecl = false)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName);
        var funcPtrType = BuildThrowingClosureCallbackFunctionPointerType(closureTypeSpec, closureHandler, useCdecl);

        // Add context parameter to the function pointer type
        var funcPtrTypeWithContext = useCdecl
            ? AddCdeclContextToFunctionPointerType(funcPtrType)
            : AddContextToFunctionPointerType(funcPtrType);

        csWriter.WriteLine($"private static unsafe readonly {funcPtrTypeWithContext} s_{callbackName} = &{callbackName};");
    }

    /// <summary>
    /// Emits code to convert a SwiftClosureData return value into a C# delegate for a throwing closure.
    /// The invoker calls the Swift function pointer and captures any error, wrapping the result
    /// in SwiftResult&lt;T, SwiftError&gt;.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="closureTypeSpec">The closure type specification (must have Throws=true).</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <param name="resultVariableName">The name of the variable holding the SwiftClosureData result.</param>
    public static void EmitThrowingClosureReturnMarshalling(
        CSharpWriter csWriter,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string resultVariableName = "result",
        string? invokeThunkPInvokeName = null)
    {
        var delegateType = closureHandler.GetCSharpDelegateType(closureTypeSpec);
        var funcPtrType = closureHandler.GetPInvokeFunctionPointerTypeWithError(closureTypeSpec);
        var funcPtrTypeWithContext = AddContextToFunctionPointerType(funcPtrType);

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

        // Build argument list for invoking the Swift function
        var invokeArgs = new List<string>();
        argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            var argExpr = GetSwiftInvokeArgExpression(arg, argIndex, closureHandler);
            invokeArgs.Add(argExpr);
            argIndex++;
        }
        // Add error out parameter
        invokeArgs.Add("&_error");
        // Add context (SwiftSelf) as last argument
        invokeArgs.Add("_swiftSelf");
        var invokeArgsString = string.Join(", ", invokeArgs);

        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        var returnIsBool = hasReturn && MarshallingHelpers.IsBoolType(closureTypeSpec.ReturnType);
        var successType = hasReturn ? closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true) : "Swift.SwiftVoid";
        var resultType = $"Swift.SwiftResult<{successType}, SwiftError>";

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
                    SwiftError _error = default;
            """);

        if (hasReturn)
        {
            csWriter.WriteLines($$"""
                        var _rawResult = _fp({{invokeArgsString}});

                        // Check for error
                        if (_error.Value != null)
                        {
                            return {{resultType}}.FromFailure(_error);
                        }

                """);

            if (returnIsBool)
            {
                csWriter.WriteLine($"                return {resultType}.FromSuccess(_rawResult != 0);");
            }
            else if (closureHandler.NeedsWellKnownProtocolWrapping(closureTypeSpec.ReturnType, out var wrapThrowingReturn))
            {
                // Owned return: the throwing closure's success payload is handed back to C# at +1.
                csWriter.WriteLine($"                return {resultType}.FromSuccess(new {wrapThrowingReturn}(_rawResult{ExistentialHandler.WellKnownOwnedTransferArg(wrapThrowingReturn)}));");
            }
            else if (closureHandler.NeedsProxyWrapping(closureTypeSpec.ReturnType, out var throwingProxy))
            {
                // Owned return: the throwing closure's success payload is handed back at +1; the proxy
                // (a real EC1-EC8 proxy here, never bare-`any`) adopts and releases it on Dispose/finalize.
                csWriter.WriteLine($"                return {resultType}.FromSuccess(new {throwingProxy}(_rawResult, ownsContainer: true));");
            }
            else if (closureHandler.IsExistentialParam(closureTypeSpec.ReturnType))
            {
                csWriter.WriteLine($"                return {resultType}.FromSuccess((object)_rawResult);");
            }
            else
            {
                csWriter.WriteLine($"                return {resultType}.FromSuccess(_rawResult);");
            }
        }
        else
        {
            csWriter.WriteLines($$"""
                        _fp({{invokeArgsString}});

                        // Check for error
                        if (_error.Value != null)
                        {
                            return {{resultType}}.FromFailure(_error);
                        }

                        return {{resultType}}.FromSuccess(Swift.SwiftVoid.Value);
                """);
        }

        csWriter.WriteLines("""
                }
            };

            return _invoker;
            """);
    }
}
