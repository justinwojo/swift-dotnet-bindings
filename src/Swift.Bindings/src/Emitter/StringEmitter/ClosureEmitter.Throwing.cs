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
        string moduleName,
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

        // Default value returned on BOTH the cooperative-failure and the caught-exception
        // paths. Bool callbacks return the byte 0; void callbacks just return.
        var defaultReturnStmt = !hasReturn
            ? "return;"
            : (returnIsBool ? "return 0;" : "return default;");

        // Success return statement (shared conversion logic — same cases as
        // EmitEscapingClosureCallback). Empty for void: control falls off the end of the
        // try block and the callback returns void.
        var successReturnStmt = hasReturn
            ? BuildCallbackReturnStatement(closureTypeSpec.ReturnType, "swiftResult.Success", closureHandler, returnType)
            : "";

        // The delegate invocation AND the success marshalling run inside try/catch: a
        // managed exception (a non-cooperative throw from the user delegate, or a failure
        // while marshalling the success value) must never unwind into native Swift — that
        // aborts the process (SIGABRT). Convert it into a Swift error in *errorOut; the
        // Swift adapter rethrows it on the Swift side. The cooperative IsFailure path is
        // unchanged.
        csWriter.WriteLines($$"""
            [UnmanagedCallersOnly(CallConvs = new[] { {{callConvType}} })]
            private static unsafe {{returnType}} {{callbackName}}({{parametersString}})
            {
                var del = SwiftClosureMarshaller.GetDelegateFromContext<{{delegateType}}>({{contextExtraction}});
                try
                {
                    var swiftResult = del({{invokeArgsString}});
                    if (swiftResult.IsFailure)
                    {
                        // Cooperative failure: the delegate produced a SwiftError.
                        *errorOut = swiftResult.Failure;
                        {{defaultReturnStmt}}
                    }

                    // Success case - no error.
                    *errorOut = default;
                    {{successReturnStmt}}
                }
                catch (global::System.Exception ex)
                {
                    *errorOut = new SwiftError((void*)SBW_CreateError_{{moduleName}}(ex.Message));
                    {{defaultReturnStmt}}
                }
            }
            """);
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
        string? invokeThunkEntryPoint = null,
        string? invokeThunkLibrary = null,
        string? invokeThunkHelper = null)
    {
        var delegateType = closureHandler.GetCSharpDelegateType(closureTypeSpec);

        // When an invoke thunk is available, route through the pre-emitted CallConvCdecl invoker
        // class instead of the inline `delegate* unmanaged[Swift]` lambda below. The inline lambda
        // makes a CallConvSwift call from a display-class method, which crashes the returned
        // throwing closure at runtime (an ABI/reabstraction SIGSEGV — the cdecl thunk passes the
        // A/B probe 3/3 where the inline path SIGSEGVs 3/3). The throwing invoker class's `Invoke`
        // returns SwiftResult<T, SwiftError> (matching the throwing delegate type) and consumes the
        // error-out pointer internally, so it is a drop-in for the lambda. Mirrors the non-throwing
        // EmitClosureReturnMarshalling path; the invoker class is already emitted by
        // EmitClosureReturnInvokeThunkHelper whenever CanUseInvokeThunk holds.
        if (invokeThunkEntryPoint != null && invokeThunkHelper != null)
        {
            var invokerClassName = GetInvokerClassName(invokeThunkHelper);
            csWriter.WriteLines($$"""
                // Wrap Swift closure in SwiftEscapingClosure for ARC management
                var _closureWrapper = SwiftEscapingClosure<{{delegateType}}>.FromSwift({{resultVariableName}}.FunctionPointer, {{resultVariableName}}.Context);

                // Use invoker class instead of lambda — Mono JIT crashes with !ji->async when
                // native calls happen from display class methods (lambdas create display classes).
                var _inv = new {{invokerClassName}}((nint)_closureWrapper.FunctionPointer, (nint)_closureWrapper.Context, _closureWrapper);
                {{delegateType}} _invoker = _inv.Invoke;

                return _invoker;
                """);
            return;
        }

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
