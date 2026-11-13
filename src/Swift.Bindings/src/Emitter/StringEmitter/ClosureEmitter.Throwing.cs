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
    /// <param name="useBoxedContext">When true, the context slot holds an <c>_SBClosureCtx</c> box
    /// pointer (the legacy <c>SwiftClosureData</c> escaping path with no Swift-side unbox), so the
    /// trampoline must resolve it via <c>GetDelegateFromBoxedContext</c> rather than reading a raw
    /// <see cref="System.Runtime.InteropServices.GCHandle"/>. Mirrors the non-throwing
    /// <see cref="EmitEscapingClosureCallback"/> gate exactly.</param>
    public static void EmitThrowingClosureCallback(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string mangledName,
        string moduleName,
        bool useCdecl = false,
        bool useBoxedContext = false)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName);
        RecordSwiftCallback(closureHandler, callbackName, closureTypeSpec, parameterName, mangledName, useCdecl);
        var delegateType = closureHandler.GetCSharpDelegateType(closureTypeSpec);

        // Build the ordinary parameter list; the error and context are placed below, by lane.
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
                // Direct lane: a loadable argument arrives exploded across registers, so declare the
                // words past the first as their own parameters.
                AppendDirectLaneExtraWordParameters(parameters, arg, argIndex, closureHandler, useCdecl);
            }
            argTypes.Add(arg);
            argIndex++;
        }
        if (useCdecl)
        {
            // Cdecl: the @_cdecl adapter's C order — arguments, error out-parameter, IntPtr context.
            parameters.Add(ReverseSwiftCallbackSignature.ErrorParameter);
            parameters.Add("IntPtr contextPtr");
        }
        else
        {
            // Swift: the shared reverse order — error first, context (self register) last.
            parameters = ReverseSwiftCallbackSignature.Parameters(parameters, throws: true);
        }

        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        var returnType = hasReturn
            ? GetEscapingClosureCallbackReturnType(closureTypeSpec, closureHandler)
            : "void";

        var parametersString = string.Join(", ", parameters);

        // Existential-argument proxy suppression (throwing variant): see EmitEscapingClosureCallback.
        // A Swift-vended existential ARGUMENT cannot be marshalled into the user delegate when its
        // protocol proxy was suppressed (EveryProtocol conformance not emitted). A throwing closure
        // has an error channel, so report the failure through *errorOut — the Swift adapter rethrows
        // it on the Swift side — rather than handing Swift a default/garbage result. Local
        // check-and-branch only: never throw across the [UnmanagedCallersOnly] boundary (SIGABRT),
        // and the member-body checkpoint rollback is unsafe for a helper-emitted callback (Hazard D).
        if (argTypes.Any(closureHandler.IsProxyReferenceSuppressed))
        {
            var suppressedCallConv = useCdecl
                ? "typeof(global::System.Runtime.CompilerServices.CallConvCdecl)"
                : "typeof(global::System.Runtime.CompilerServices.CallConvSwift)";
            // Route the cooperative error report through the shared UCO try/catch envelope: the corpus
            // invariant (CatchFreeUcoValidatorTests) requires EVERY [UnmanagedCallersOnly] body to be
            // guarded. The try body reports the suppression through *errorOut (the throwing channel —
            // the Swift adapter rethrows it); the catch is the defensive mirror of the non-suppressed
            // path's catch (any escape while assigning *errorOut is reported through the same channel
            // rather than unwinding into native Swift). Return is at the try/catch-body depth.
            var noopReturn = returnType == "void" ? "" : "\n        return default;";
            csWriter.WriteLines($$"""
                [global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { {{suppressedCallConv}} })]
                private static unsafe {{returnType}} {{callbackName}}({{parametersString}})
                {
                    try
                    {
                        // Protocol proxy unavailable — report through the throwing channel: a required
                        // existential argument's proxy class was suppressed (its EveryProtocol conformance
                        // was not emitted), so the Swift-vended existential cannot be marshalled into the
                        // user delegate. The borrowed argument cell leaks nothing.
                        *errorOut = new SwiftError((void*)SBW_CreateError_{{moduleName}}("Protocol proxy unavailable: an existential argument's EveryProtocol conformance was not emitted.", null));{{noopReturn}}
                    }
                    catch (global::System.Exception ex)
                    {
                        *errorOut = new SwiftError((void*)SBW_CreateError_{{moduleName}}(ex.Message, ex.GetType().FullName));{{noopReturn}}
                    }
                }
                """);
            return;
        }

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
        //
        // Box vs raw context: on the legacy SwiftClosureData escaping path the context slot stores
        // the `_SBClosureCtx` box pointer itself (Swift never unboxes before invoking this
        // trampoline), so we must resolve it via GetDelegateFromBoxedContext. For the cdecl path the
        // Swift wrapper unboxes first and a raw GCHandle ptr arrives, so we read it directly. This
        // gate must stay identical to EmitEscapingClosureCallback — when the setter boxes the
        // context (WrapperEmitter.Marshalling legacyEscaping) but the trampoline reads it raw, the
        // box pointer is misinterpreted as a GCHandle and the cast throws InvalidCastException,
        // which (being outside the try below) escapes the [UnmanagedCallersOnly] boundary and aborts.
        var extractCall = useBoxedContext
            ? $"SwiftClosureMarshaller.GetDelegateFromBoxedContext<{delegateType}>({contextExtraction})"
            : $"SwiftClosureMarshaller.GetDelegateFromContext<{delegateType}>({contextExtraction})";

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
        var argPrologue = BuildDirectLaneWordBufferPrologue(closureTypeSpec, closureHandler, useCdecl);
        csWriter.WriteLines($$"""
            [global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { {{callConvType}} })]
            private static unsafe {{returnType}} {{callbackName}}({{parametersString}})
            {
                var del = {{extractCall}};
                try
                {
                    {{argPrologue}}var swiftResult = del({{invokeArgsString}});
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
                    *errorOut = new SwiftError((void*)SBW_CreateError_{{moduleName}}(ex.Message, ex.GetType().FullName));
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
        var funcPtrTypeWithContext = BuildThrowingClosureCallbackFunctionPointerType(closureTypeSpec, closureHandler, useCdecl);

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
        string? invokeThunkHelper = null,
        string? invokerQualifier = null)
    {
        var delegateType = closureHandler.GetCSharpDelegateType(closureTypeSpec);

        // Route through the pre-emitted CallConvCdecl invoker class. A direct CallConvSwift call
        // through the closure's function pointer crashes the returned throwing closure at runtime
        // (an ABI/reabstraction SIGSEGV). The throwing invoker class's `Invoke` returns
        // SwiftResult<T, SwiftError> (matching the throwing delegate type) and consumes the
        // error-out pointer internally. Mirrors the non-throwing EmitClosureReturnMarshalling path;
        // the invoker class is emitted by EmitClosureReturnInvokeThunkHelper whenever
        // CanUseInvokeThunk holds.
        if (invokeThunkEntryPoint != null && invokeThunkHelper != null)
        {
            var invokerClassName = $"{invokerQualifier}{GetInvokerClassName(invokeThunkHelper)}";
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

        // No invoke thunk: no correct call exists (see EmitClosureReturnMarshalling). Validation
        // refuses every such member, so reaching here is a generator bug.
        throw new InvalidOperationException(
            $"Returned throwing closure '{closureTypeSpec}' has no invoke thunk; the member should have been refused before emission.");
    }
}
