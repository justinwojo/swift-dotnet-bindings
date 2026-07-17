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
        csWriter.WriteLines($$"""
            [global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { {{callConvType}} })]
            private static unsafe {{returnType}} {{callbackName}}({{parametersString}})
            {
                var del = {{extractCall}};
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

        // Build argument list for invoking the Swift function. A by-value struct arg cannot be
        // passed as a bare C# value into the func-ptr's struct-pointer/void* slot (CS1503) — it
        // needs the same metadata + buffer + MarshalToSwift prologue the non-throwing struct-param
        // closure paths emit. This mirrors the invoke-thunk path's three-list structure
        // (ClosureEmitter.InvokeThunk.cs) so the two stay consistent:
        //   • Frozen value struct → stackalloc + MarshalToSwift (no heap, no cleanup).
        //   • Non-frozen struct   → heap buffer + value-witness InitializeWithCopy.
        //   • String / Foundation.Data → project to string/byte[], which carry NO Swift
        //     TypeMetadata, so the generic GetTypeMetadataOrThrow<string>()/<byte[]>() path
        //     throws at runtime. Convert to the metadata-bearing runtime value (SwiftString /
        //     Foundation.Data) first, then marshal its inline Swift representation into a heap
        //     buffer via the value-witness retaining copy (MarshalToSwift).
        // Heap buffers (non-frozen + String/Data) are declared null BEFORE the try, allocated
        // INSIDE it, and Destroy+Free'd in a null-guarded finally so an alloc/InitializeWithCopy
        // failure on a later arg never leaks an earlier arg's buffer (each buffer carries its own
        // +1 from the retaining copy and must always be released). AllocZeroed makes a Destroy on
        // a partially-initialised buffer safe (zeroed reference fields release as nil no-ops).
        // Every other arg kind (class, ObjC-bridged, enum, bool, protocol, tuple) is handled by
        // GetSwiftInvokeArgExpression, which already renders the correct func-ptr-slot expression.
        var invokeArgs = new List<string>();
        var heapPreTry = new List<string>();     // declarations visible to both try and finally
        var heapInTry = new List<string>();      // allocations + init (inside try, before the call)
        var heapCleanup = new List<string>();    // null-guarded Destroy+Free (finally)
        var frozenPrologue = new List<string>(); // frozen stackalloc setup (no cleanup)
        // +0 borrowed existential ARGS whose auto-wrapped proxy must be pinned across the native call
        // (design change 4 / mechanism 3) — GC.KeepAlive'd immediately after _fp(...) returns.
        var keepAliveVarsThrowing = new List<string>();
        argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (IsInvokeThunkStructArg(arg, closureHandler))
            {
                int i = argIndex;
                if (IsMetadataRemappedValueStructArg(arg))
                {
                    var (conv, helperType) = GetMetadataRemappedConversion(arg, i);
                    heapPreTry.Add($"using var _arg{i}Swift = {conv};");
                    heapPreTry.Add($"byte* _arg{i}Buffer = null;");
                    heapPreTry.Add($"var _arg{i}Metadata = Swift.Runtime.SwiftObjectHelper<{helperType}>.GetTypeMetadata();");
                    heapInTry.Add($"_arg{i}Buffer = (byte*)NativeMemory.AllocZeroed((nuint)_arg{i}Metadata.Size, (nuint)_arg{i}Metadata.Stride);");
                    heapInTry.Add($"var _arg{i}Span = new Span<byte>(_arg{i}Buffer, (int)_arg{i}Metadata.Size);");
                    heapInTry.Add($"((Swift.Runtime.ISwiftObject)_arg{i}Swift).MarshalToSwift(ref _arg{i}Span);");
                    heapCleanup.Add($"if (_arg{i}Buffer != null) {{ _arg{i}Metadata.ValueWitnessTable->Destroy((void*)_arg{i}Buffer, _arg{i}Metadata); NativeMemory.Free(_arg{i}Buffer); }}");
                    invokeArgs.Add($"_arg{i}Buffer");
                }
                else if (closureHandler.IsNonFrozenStruct(arg))
                {
                    var csharpType = closureHandler.TranslateTypeSpecToCSharp(arg);
                    heapPreTry.Add($"byte* _arg{i}Buffer = null;");
                    heapPreTry.Add($"var _arg{i}Metadata = TypeMetadata.GetTypeMetadataOrThrow<{csharpType}>();");
                    heapInTry.Add($"_arg{i}Buffer = (byte*)NativeMemory.AllocZeroed((nuint)_arg{i}Metadata.Size, (nuint)_arg{i}Metadata.Stride);");
                    heapInTry.Add($"_arg{i}Metadata.ValueWitnessTable->InitializeWithCopy((void*)_arg{i}Buffer, (void*)_arg{i}.Payload.DangerousGetHandle(), _arg{i}Metadata);");
                    heapCleanup.Add($"if (_arg{i}Buffer != null) {{ _arg{i}Metadata.ValueWitnessTable->Destroy((void*)_arg{i}Buffer, _arg{i}Metadata); NativeMemory.Free(_arg{i}Buffer); }}");
                    invokeArgs.Add($"_arg{i}Buffer");
                }
                else
                {
                    var csharpType = closureHandler.TranslateTypeSpecToCSharp(arg);
                    frozenPrologue.Add($"var _arg{i}Metadata = TypeMetadata.GetTypeMetadataOrThrow<{csharpType}>();");
                    frozenPrologue.Add($"byte* _arg{i}Buffer = stackalloc byte[(int)_arg{i}Metadata.Size];");
                    frozenPrologue.Add($"var _arg{i}Span = new Span<byte>(_arg{i}Buffer, (int)_arg{i}Metadata.Size);");
                    frozenPrologue.Add($"SwiftMarshal.MarshalToSwift(_arg{i}, ref _arg{i}Span);");
                    invokeArgs.Add($"_arg{i}Buffer");
                }
            }
            else
            {
                invokeArgs.Add(GetSwiftInvokeArgExpression(arg, argIndex, closureHandler, keepAliveVars: keepAliveVarsThrowing));
            }
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

        // GC.KeepAlive(...) for the borrowed existential args, emitted right after the _fp(...) call so
        // a weakly-registered auto-wrapped proxy's R0 cannot be released while Swift is still borrowing.
        // Empty when no existential arg needs pinning. The out vars are declared at the call statement,
        // so this following statement is in scope (inside the heap-cleanup try when one is present).
        var keepAliveLineThrowing = keepAliveVarsThrowing.Count > 0
            ? string.Join(" ", keepAliveVarsThrowing.Select(v => $"GC.KeepAlive({v});"))
            : string.Empty;

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

        // Marshal by-value struct args into Swift buffers. Heap buffers (non-frozen + String/Data)
        // declare null pointers before a try, allocate and initialise inside it, and Destroy+Free
        // in a null-guarded finally (the call's early returns run that finally). Frozen stackalloc
        // args carry no cleanup and only need their prologue ahead of the call.
        foreach (var preTryLine in heapPreTry)
        {
            csWriter.WriteLine(preTryLine);
        }
        var hasHeapCleanup = heapCleanup.Count > 0;
        if (hasHeapCleanup)
        {
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            foreach (var inTryLine in heapInTry)
            {
                csWriter.WriteLine(inTryLine);
            }
        }
        foreach (var frozenLine in frozenPrologue)
        {
            csWriter.WriteLine(frozenLine);
        }

        if (hasReturn)
        {
            csWriter.WriteLines($$"""
                        var _rawResult = _fp({{invokeArgsString}});
                        {{keepAliveLineThrowing}}

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
                // PRODUCE: a suppressed proxy throws → member-body checkpoint restubs the whole member.
                closureHandler.ThrowIfProxyReferenceSuppressed(closureTypeSpec.ReturnType);
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
                        {{keepAliveLineThrowing}}

                        // Check for error
                        if (_error.Value != null)
                        {
                            return {{resultType}}.FromFailure(_error);
                        }

                        return {{resultType}}.FromSuccess(Swift.SwiftVoid.Value);
                """);
        }

        // Close the try and free heap struct buffers after the call — runs on both the
        // error-return and success-return paths, and on any exception thrown in between.
        if (hasHeapCleanup)
        {
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally");
            csWriter.WriteLine("{");
            foreach (var cleanupLine in heapCleanup)
            {
                csWriter.WriteLine(cleanupLine);
            }
            csWriter.WriteLine("}");
        }

        csWriter.WriteLines("""
                }
            };

            return _invoker;
            """);
    }
}
