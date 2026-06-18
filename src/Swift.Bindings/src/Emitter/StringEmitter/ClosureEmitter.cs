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
    /// Finding 53: project the public existential type for <paramref name="typeSpec"/>, or — when
    /// the resolver can't (it returns <c>null</c> or the bare <c>object</c> fallback) — record a
    /// SWIFTBIND026 object-degradation and return <c>"object"</c>. Replaces the silent
    /// <c>?? "object"</c> fallbacks at closure parameter/return positions so a member that ends up
    /// typed as bare <c>object</c> is observable rather than invisible.
    /// </summary>
    private static string ResolveClosureExistentialOrDegrade(ClosureHandler closureHandler, TypeSpec typeSpec)
    {
        var publicType = closureHandler.GetPublicExistentialType(typeSpec);
        if (!string.IsNullOrEmpty(publicType) && publicType != "object")
            return publicType;

        ReportCollector.RecordObjectDegradation(typeSpec?.ToString() ?? "<unknown>");
        return "object";
    }

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
        bool useCdecl = false,
        bool useBoxedContext = false)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName);
        var delegateType = closureHandler.GetCSharpDelegateType(closureTypeSpec);

        // Cdecl closures with indirect return: the Swift adapter passes a result buffer as
        // the first parameter. The callback writes the result to this buffer instead of returning it.
        // Swift adapter: cdecl_func(resultBuf, [args...], context) → reads result from buffer.
        // Without this, the callback signature mismatches the @convention(c) type, causing
        // the result buffer pointer to be interpreted as the context → crash in swift_cvw_initWithCopyImpl.
        bool isIndirectReturn = useCdecl
            && closureHandler.RequiresIndirectReturnMarshalling(closureTypeSpec)
            && !closureTypeSpec.Throws;

        // Build parameter list for the callback (arguments + context as last param)
        var parameters = new List<string>();
        var argTypes = new List<TypeSpec>();

        // Indirect return: result buffer is first parameter (matches Swift adapter's @convention(c) layout)
        if (isIndirectReturn)
        {
            parameters.Add("IntPtr resultBuffer");
        }

        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            // UnsafeRawBufferPointer / UnsafeMutableRawBufferPointer: split into (ptr, len) pair
            // to match the Swift @convention(c) decomposition. C# callback reconstructs the
            // 16-byte struct before invoking the user delegate.
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
        // Cdecl: context is a plain IntPtr parameter.
        // Swift: context is passed in the Swift "self" register via SwiftSelf.
        parameters.Add(useCdecl ? "IntPtr contextPtr" : "SwiftSelf context");

        var returnType = isIndirectReturn
            ? "void"
            : GetEscapingClosureCallbackReturnType(closureTypeSpec, closureHandler);

        var parametersString = string.Join(", ", parameters);

        // Build argument list for invoking the delegate
        // Handle type conversions: byte->bool, void*->struct marshalling
        var invokeArgs = new List<string>();
        for (int i = 0; i < argIndex; i++)
        {
            var argExpr = GetInvokeArgExpression(argTypes[i], i, closureHandler, useCdecl);
            invokeArgs.Add(argExpr);
        }
        var invokeArgsString = string.Join(", ", invokeArgs);

        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;

        // Build the return statement using the shared conversion logic
        string returnStatement;
        if (isIndirectReturn && hasReturn)
        {
            returnStatement = BuildCallbackIndirectReturnStatement(
                closureTypeSpec.ReturnType,
                $"del({invokeArgsString})",
                closureHandler);
        }
        else if (!hasReturn)
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

        // Callback never frees the GCHandle — the calling method's finally block handles cleanup
        // for non-escaping closures. Escaping closures may fire multiple times
        // (e.g., callMultipleTimes), so freeing in the callback would crash on the second
        // invocation. Escaping-closure GCHandle leaks are closed via the Swift-side
        // _SBClosureCtx box deinit upcall — for cdecl the Swift wrapper unboxes before
        // calling C# (raw GCHandle ptr arrives here); for the legacy SwiftClosureData
        // path the context slot stores the box pointer itself, so the trampoline calls
        // GetDelegateFromBoxedContext to resolve it via SwiftClosureContext.GetCtx.
        var extractCall = useBoxedContext
            ? $"SwiftClosureMarshaller.GetDelegateFromBoxedContext<{delegateType}>({contextExtraction})"
            : $"SwiftClosureMarshaller.GetDelegateFromContext<{delegateType}>({contextExtraction})";
        // Non-throwing closure: there is no error channel back to Swift, so a managed
        // exception escaping this [UnmanagedCallersOnly] callback would unwind into native
        // Swift and abort the process (SIGABRT). Wrap the body so any unhandled exception
        // becomes a controlled FailFast with the original exception attached. FailFast is
        // [DoesNotReturn], but C#'s end-point-reachability analysis (CS0161) does NOT honor
        // [DoesNotReturn] — only nullable/definite-assignment flow does — so a value-returning
        // callback whose catch ends in the FailFast call still trips "not all code paths
        // return a value". The trailing `throw;` gives the catch a definite terminator; it is
        // unreachable at runtime (FailFast already aborted) and type-agnostic, so it works for
        // both void and value-returning callbacks.
        csWriter.WriteLines($$"""
            [UnmanagedCallersOnly(CallConvs = new[] { {{callConvType}} })]
            private static unsafe {{returnType}} {{callbackName}}({{parametersString}})
            {
                try
                {
                    var del = {{extractCall}};
                    {{returnStatement}}
                }
                catch (global::System.Exception __ex)
                {
                    SwiftClosureMarshaller.FailFastUnhandledClosureException(__ex);
                    throw;
                }
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
                var _inv = new {{invokerClassName}}((nint)_closureWrapper.FunctionPointer, (nint)_closureWrapper.Context, _closureWrapper);
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
        // +0 borrowed existential ARGS whose auto-wrapped proxy must be pinned across the native
        // function-pointer call (design change 4 / mechanism 3) — GC.KeepAlive'd after _fp(...) returns.
        var keepAliveVarsFallback = new List<string>();
        argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            var argExpr = GetSwiftInvokeArgExpression(arg, argIndex, closureHandler, keepAliveVars: keepAliveVarsFallback);
            invokeArgsFallback.Add(argExpr);
            argIndex++;
        }
        // Add context (SwiftSelf) as last argument
        invokeArgsFallback.Add("_swiftSelf");
        var invokeArgsStringFallback = string.Join(", ", invokeArgsFallback);

        // GC.KeepAlive(...) for the borrowed existential args, emitted AFTER the native call returns so
        // a weakly-registered auto-wrapped proxy's R0 cannot be released while Swift is still borrowing.
        var keepAliveSuffixFallback = keepAliveVarsFallback.Count > 0
            ? " " + string.Join(" ", keepAliveVarsFallback.Select(v => $"GC.KeepAlive({v});"))
            : string.Empty;
        // When the closure has a return value AND there are args to keep alive, hoist the native call
        // into a local so KeepAlive lands after it but before the value is consumed by the return shape.
        var preCallFallback = string.Empty;

        // Generate the closure body
        // For well-known protocol returns (ExistentialContainer1 from P/Invoke → AnyError for delegate)
        string invokeExprFallback;
        if (keepAliveVarsFallback.Count > 0 && hasReturn)
        {
            preCallFallback = $"var _invRet = _fp({invokeArgsStringFallback});{keepAliveSuffixFallback} ";
            invokeExprFallback = "_invRet";
        }
        else
        {
            invokeExprFallback = $"_fp({invokeArgsStringFallback})";
        }
        string returnExprFallback;
        if (!hasReturn)
        {
            // Void: the call IS the statement, so KeepAlive simply follows it.
            returnExprFallback = $"{invokeExprFallback};{keepAliveSuffixFallback}";
        }
        else if (returnIsBool)
        {
            returnExprFallback = $"return {invokeExprFallback} != 0;";
        }
        else if (closureHandler.NeedsWellKnownProtocolWrapping(closureTypeSpec.ReturnType, out var wrapReturnType))
        {
            // Owned return: invoking the Swift function pointer hands the existential back to C# at +1,
            // so the well-known wrapper adopts and releases it on Dispose/finalize or the payload leaks.
            returnExprFallback = $"return new {wrapReturnType}({invokeExprFallback}{ExistentialHandler.WellKnownOwnedTransferArg(wrapReturnType)});";
        }
        else if (closureHandler.NeedsProxyWrapping(closureTypeSpec.ReturnType, out var returnProxy))
        {
            // Owned return: invoking the Swift function pointer hands the existential back at +1, so
            // the proxy adopts the container and releases it on Dispose/finalize. This branch is only
            // reached for a real EC1-EC8 proxy (bare-`any` falls to the IsExistentialParam branch
            // below), so the transfer is always owned.
            returnExprFallback = $"return new {returnProxy}({invokeExprFallback}, ownsContainer: true);";
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
                    // Owned tuple element: the +1 existential returned by the Swift closure is adopted here.
                    elems.Add($"new {wrt}({acc}{ExistentialHandler.WellKnownOwnedTransferArg(wrt)})");
                else if (closureHandler.NeedsProxyWrapping(elem, out var prn))
                    // Owned tuple element: the +1 existential returned by the Swift closure is adopted here.
                    elems.Add($"new {prn}({acc}, ownsContainer: true)");
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
        else if (closureHandler.IsClassType(closureTypeSpec.ReturnType) ||
                 closureHandler.IsObjCBridgedClass(closureTypeSpec.ReturnType))
        {
            // Class/ObjC return: function pointer returns void* (opaque pointer).
            // Wrap in SwiftHandle → class constructor. Swift calling convention returns
            // an owned reference, so SwiftClassHandle takes ownership.
            var csReturnType = closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true);
            returnExprFallback = $"return new {csReturnType}(new Swift.Runtime.SwiftHandle((IntPtr){invokeExprFallback}));";
        }
        else
        {
            returnExprFallback = $"return {invokeExprFallback};";
        }

        // Prefix the hoisted "var _invRet = _fp(...); GC.KeepAlive(...);" for the return-value
        // keepAlive case; empty string (no-op) for void and the no-existential-arg case.
        returnExprFallback = preCallFallback + returnExprFallback;

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
                var pt = ResolveClosureExistentialOrDegrade(closureHandler, returnType);
                var qp = closureHandler.GetQualifiedProxyClassName(returnType);
                // A closure return is +1-owned by Swift, so mint an independent reference rather
                // than borrow the proxy's construction +1 (R0). After this callback returns the
                // existential by value, there is no C# statement left to GC.KeepAlive the proxy,
                // so a borrowed (GetOrCreate) container would let a GC finalize the proxy and
                // release R0 before Swift retains the value. CreateOwnedExistential1 mints the +1
                // Swift takes ownership of and is internally keep-alive-safe across the mint.
                return qp != null
                    ? $"return Swift.Runtime.ExistentialContainerFactory.CreateOwnedExistential1<{pt}>({resultExpr}, static __v => new {qp}(__v));"
                    : $"return Swift.Runtime.ExistentialContainerFactory.CreateOwnedExistential1<{pt}>({resultExpr});";
            }
            var ct = closureHandler.GetPInvokeExistentialType(returnType);
            // EC2+ composition return (any P & Q…): ShouldUseGetOrCreate is EC1-only, so a composition
            // existential falls here. Its only conformer is a Swift-vended proxy whose
            // GetExistentialContainer() BORROWS the proxy's stored bytes — returning that borrowed alias
            // at +1 would double-release the proxy's sole construction +1 (R0) once Swift's owned
            // release and the proxy's release both fire. Mint an independent +1 via the always-mint
            // composition sibling of CreateOwnedExistential1 (no boxable composition conformer exists,
            // so there is no donate arm). Same owned-closure-return rationale as the EC1 branch above.
            if (ExistentialHandler.IsOwnedExistentialContainerType(ct) &&
                ct != "Swift.Runtime.ExistentialContainer1")
            {
                var pt = ResolveClosureExistentialOrDegrade(closureHandler, returnType);
                return $"return Swift.Runtime.ExistentialContainerFactory.CreateOwnedCompositionExistential<{pt}, {ct}>({resultExpr});";
            }
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
                        var pt = ResolveClosureExistentialOrDegrade(closureHandler, elem);
                        var qp = closureHandler.GetQualifiedProxyClassName(elem);
                        // Returned tuple element is +1-owned by Swift — mint an independent
                        // reference (see the scalar-return case above for the full rationale).
                        elems.Add(qp != null
                            ? $"Swift.Runtime.ExistentialContainerFactory.CreateOwnedExistential1<{pt}>({acc}, static __v => new {qp}(__v))"
                            : $"Swift.Runtime.ExistentialContainerFactory.CreateOwnedExistential1<{pt}>({acc})");
                    }
                    else
                    {
                        var ct = closureHandler.GetPInvokeExistentialType(elem);
                        // EC2+ composition tuple element: mint an independent +1 (see the scalar-return
                        // case above) — a returned borrowed alias would double-release the proxy's R0.
                        if (ExistentialHandler.IsOwnedExistentialContainerType(ct) &&
                            ct != "Swift.Runtime.ExistentialContainer1")
                        {
                            var pt = ResolveClosureExistentialOrDegrade(closureHandler, elem);
                            elems.Add($"Swift.Runtime.ExistentialContainerFactory.CreateOwnedCompositionExistential<{pt}, {ct}>({acc})");
                        }
                        else
                        {
                            elems.Add($"((Swift.Runtime.ISwiftExistentialConvertible<{ct}>){acc}).GetExistentialContainer()");
                        }
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

        // String returns need special handling: System.String doesn't have Swift TypeMetadata,
        // so the generic MarshalToSwift path fails. Convert to SwiftString first, which has
        // proper Swift metadata, then marshal that.
        if (callbackReturnType == "void*" && WitnessDispatchEmitter.IsStringType(returnType))
        {
            return $"""
                    var _result = {resultExpr};
                            using var _swiftStr = new Swift.SwiftString(_result);
                            var _resultMetadata = Swift.Runtime.SwiftObjectHelper<Swift.SwiftString>.GetTypeMetadata();
                            var _resultBuffer = (void*)NativeMemory.Alloc(_resultMetadata.Size);
                            var _resultSpan = new Span<byte>(_resultBuffer, (int)_resultMetadata.Size);
                            ((Swift.Runtime.ISwiftObject)_swiftStr).MarshalToSwift(ref _resultSpan);
                            return _resultBuffer;
                """;
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
    /// Builds the body statement for a Cdecl closure callback with indirect return.
    /// Instead of returning the result, writes the marshalled value to the caller-provided
    /// result buffer (passed as the first parameter by the Swift @convention(c) adapter).
    /// The Swift adapter then loads the value from the buffer via .move().
    /// </summary>
    /// <param name="returnType">The closure's return TypeSpec.</param>
    /// <param name="resultExpr">The expression that produces the C# result (e.g., "del(args)").</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <returns>One or more lines of C# code that write the result to resultBuffer.</returns>
    internal static string BuildCallbackIndirectReturnStatement(
        TypeSpec returnType,
        string resultExpr,
        ClosureHandler closureHandler)
    {
        // Class type: write retained pointer to buffer
        if (closureHandler.IsClassType(returnType))
        {
            return $$"""
                    var _result = {{resultExpr}};
                            *(IntPtr*)(void*)resultBuffer = _result.Payload.DangerousGetHandle();
                """;
        }

        // ObjC-bridged class (e.g., Foundation.URL → NSURL): write handle to buffer
        if (closureHandler.IsObjCBridgedClass(returnType))
        {
            return $$"""
                    var _result = {{resultExpr}};
                            *(IntPtr*)(void*)resultBuffer = _result.Handle;
                """;
        }

        // String: marshal SwiftString bytes to buffer
        if (WitnessDispatchEmitter.IsStringType(returnType))
        {
            return $$"""
                    var _result = {{resultExpr}};
                            using var _swiftStr = new Swift.SwiftString(_result);
                            var _resultSpan = new Span<byte>((void*)resultBuffer, (int)Swift.Runtime.SwiftObjectHelper<Swift.SwiftString>.GetTypeMetadata().Size);
                            ((Swift.Runtime.ISwiftObject)_swiftStr).MarshalToSwift(ref _resultSpan);
                """;
        }

        // General struct/value type: use SwiftMarshal.MarshalToSwift to write to buffer
        var csharpRetType = closureHandler.TranslateTypeSpecToCSharp(returnType, isReturnType: true);
        return $$"""
                var _result = {{resultExpr}};
                        var _resultMetadata = TypeMetadata.GetTypeMetadataOrThrow<{{csharpRetType}}>();
                        var _resultSpan = new Span<byte>((void*)resultBuffer, (int)_resultMetadata.Size);
                        SwiftMarshal.MarshalToSwift(_result, ref _resultSpan);
            """;
    }

    /// <summary>
    /// Gets the C# type for a closure callback parameter.
    /// Delegates to ClosureHandler.TranslateTypeSpecToPInvokeType for consistency
    /// between the callback signature and function pointer type declaration.
    /// For cdecl callbacks, existential params become void* because the Swift adapter
    /// passes a pointer to a heap-allocated ExistentialContainer{N}; the native
    /// @convention(c) ABI cannot receive Swift existential containers by value.
    /// </summary>
    private static string GetCallbackParameterType(TypeSpec typeSpec, ClosureHandler closureHandler, bool useCdecl = false)
    {
        // Cdecl existential: Swift adapter passes a UnsafeMutableRawPointer (void*) to a
        // heap-allocated ExistentialContainer{N}. Both forms reach here: ProtocolListTypeSpec
        // (multi-proto) and NamedTypeSpec { IsAny = true } (single-proto parser output).
        if (useCdecl && (typeSpec is ProtocolListTypeSpec || typeSpec is NamedTypeSpec { IsAny: true }))
            return "void*";
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
    private static string GetInvokeArgExpression(TypeSpec typeSpec, int argIndex, ClosureHandler closureHandler, bool useCdecl = false)
    {
        // UnsafeRawBufferPointer / UnsafeMutableRawBufferPointer: reconstruct the 16-byte
        // struct from the (ptr, len) pair the Swift @convention(c) callback handed us.
        if (useCdecl && MarshallingHelpers.IsAnyUnsafeRawBufferPointer(typeSpec))
        {
            var bufferType = MarshallingHelpers.IsUnsafeMutableRawBufferPointer(typeSpec)
                ? "global::Swift.UnsafeMutableRawBufferPointer"
                : "global::Swift.UnsafeRawBufferPointer";
            return $"new {bufferType}(arg{argIndex}, arg{argIndex}_len)";
        }

        // Cdecl existential: Swift adapter handed us a void* pointer to a heap-allocated
        // ExistentialContainer{N}. Dereference and wrap with the appropriate proxy/runtime type.
        // Both forms reach here: ProtocolListTypeSpec (multi-proto) and
        // NamedTypeSpec { IsAny = true } (single-proto parser output).
        if (useCdecl && (typeSpec is ProtocolListTypeSpec || typeSpec is NamedTypeSpec { IsAny: true }))
        {
            var containerType = closureHandler.GetPInvokeExistentialType(typeSpec);
            // A class-bound (single AnyObject-/superclass-constrained) existential is a compact
            // 2-word [classRef][witnessTable] heap cell (16 bytes), not the 5-word opaque container
            // (40 bytes); dereferencing the wider type over-reads 24 bytes past the allocation.
            // This is a borrow (the callback owns the cell), so no extra retain.
            var containerAccess = closureHandler.IsClassBoundArity1Existential(typeSpec)
                ? $"global::Swift.Runtime.ClassExistentialContainer1.ReadHeapCell((IntPtr)arg{argIndex})"
                : $"*(global::{containerType}*)arg{argIndex}";
            if (closureHandler.NeedsWellKnownProtocolWrapping(typeSpec, out var cdeclWrapType))
                return $"new {cdeclWrapType}({containerAccess})";
            if (closureHandler.NeedsProxyWrapping(typeSpec, out var cdeclProxyName))
            {
                var qp = closureHandler.GetQualifiedProxyClassName(typeSpec) ?? cdeclProxyName;
                return $"new {qp}({containerAccess})";
            }
            return $"(object)({containerAccess})";
        }

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

            // Optional<String>: TranslateTypeSpecToCSharp projects to "string?" but
            // System.String has no Swift metadata, so MarshalCallbackArg<string>
            // crashes. Marshal as SwiftString and call ToString() — same shape as the
            // top-level String path. Must run before IsOptionalFrozenStructParam since
            // Swift.String is a frozen struct and would otherwise hit that branch.
            if (namedType.ContainsGenericParameters &&
                namedType.Name == "Swift.Optional" &&
                namedType.GenericParameters.Count == 1 &&
                namedType.GenericParameters[0] is NamedTypeSpec optInnerStr &&
                WitnessDispatchEmitter.IsStringType(optInnerStr))
                return $"arg{argIndex} != null ? SwiftMarshal.MarshalCallbackArg<Swift.SwiftString>(new IntPtr(arg{argIndex})).ToString() : null";

            // Optional<Foundation.Data>: same shape as the top-level Foundation.Data
            // path — projected delegate type is "byte[]?" but byte[] has no Swift metadata.
            if (namedType.ContainsGenericParameters &&
                namedType.Name == "Swift.Optional" &&
                namedType.GenericParameters.Count == 1 &&
                namedType.GenericParameters[0] is NamedTypeSpec optInnerData &&
                optInnerData.Name == "Foundation.Data")
                return $"arg{argIndex} != null ? SwiftMarshal.MarshalCallbackArg<Swift.Foundation.Data>(new IntPtr(arg{argIndex})).ToByteArray() : null";

            // Optional<Class>: void* → null check → MarshalCallbackArg or null
            // Callback parameters are borrowed references — use the borrowed marshal path
            // to prevent double-release when the GC collects the wrapper.
            if (IsOptionalReferenceParam(namedType, closureHandler))
            {
                var inner = namedType.GenericParameters[0];
                var innerType = closureHandler.TranslateTypeSpecToCSharp(inner);
                if (closureHandler.IsClassType(inner))
                    // The wrapper is handed to the user's closure body and may be Disposed there.
                    // MarshalBorrowedClassFromSwift takes a real +1 (owning), so Dispose + finalize
                    // both balance it — unlike a blanket-suppress borrowed marshal, whose
                    // SuppressFinalize-only strategy leaves an explicit Dispose double-releasing a +0 handle.
                    return $"arg{argIndex} != null ? SwiftMarshal.MarshalBorrowedClassFromSwift<{innerType}>(new IntPtr(arg{argIndex})) : null";
                else // ObjC-bridged
                    return $"arg{argIndex} != null ? {MarshallingHelpers.FormatObjCBridgeCall(innerType, $"new IntPtr(arg{argIndex})")} : null";
            }

            // Optional<Bool/SimpleEnum>: nil-for-none pointer ABI (null = .none, non-null = pointer to inner value)
            if (IsOptionalNilForNoneParam(namedType, closureHandler))
            {
                var inner = namedType.GenericParameters[0];
                var innerType = closureHandler.TranslateTypeSpecToCSharp(inner);
                if (MarshallingHelpers.IsBoolType(inner))
                    return $"arg{argIndex} != null ? ({innerType}?)(*(byte*)arg{argIndex} != 0) : null";
                // Simple enum: read underlying integer from pointer, cast to enum type
                var enumInfo = closureHandler.GetSimpleEnumInfo(inner);
                var csUnderlying = enumInfo?.csUnderlying ?? "int";
                return $"arg{argIndex} != null ? ({innerType}?)({innerType})(*({csUnderlying}*)arg{argIndex}) : null";
            }

            // Optional<FrozenStruct>: nil-for-none pointer ABI. Swift unwraps and
            // allocates the inner struct via initializeMemory (deallocated after callback).
            // C# reads the borrowed struct value via MarshalCallbackArg — Swift
            // still owns the heap memory, so no Dispose is issued here.
            if (IsOptionalFrozenStructParam(namedType, closureHandler))
            {
                var inner = namedType.GenericParameters[0];
                var innerType = closureHandler.TranslateTypeSpecToCSharp(inner);
                return $"arg{argIndex} != null ? ({innerType}?)SwiftMarshal.MarshalCallbackArg<{innerType}>(new IntPtr(arg{argIndex})) : null";
            }

            // Optional<NumericPrimitive>: full Optional on heap → SwiftMarshal.MarshalOptionalFromSwift<T>
            if (IsOptionalValueParam(namedType, closureHandler))
            {
                var inner = namedType.GenericParameters[0];
                var innerType = closureHandler.TranslateTypeSpecToCSharp(inner);
                return $"SwiftMarshal.MarshalOptionalFromSwift<{innerType}>(new IntPtr(arg{argIndex}))";
            }

            // String parameter: System.String has no Swift metadata, so MarshalFromSwift<string> fails.
            // Marshal as SwiftString (which implements ISwiftObject) and convert to string.
            if (WitnessDispatchEmitter.IsStringType(namedType))
                return $"SwiftMarshal.MarshalCallbackArg<Swift.SwiftString>(new IntPtr(arg{argIndex})).ToString()";

            // Foundation.Data → byte[] projection: TranslateTypeSpecToCSharp projects
            // Foundation.Data to byte[], but byte[] has no Swift metadata, so the default
            // MarshalCallbackArg<byte[]> path crashes with NotSupportedException.
            // Marshal as Swift.Foundation.Data first, then call ToByteArray() — same shape
            // as DataProjection's ReturnPlan.Direct (".ToByteArray()" on the marshalled value).
            if (namedType.Name == "Foundation.Data")
                return $"SwiftMarshal.MarshalCallbackArg<Swift.Foundation.Data>(new IntPtr(arg{argIndex})).ToByteArray()";

            // ObjC-bridged native remap (e.g., Foundation.URLResponse → Foundation.NSUrlResponse).
            // TranslateTypeSpecToCSharp returns the NativeTypeName (NSUrlResponse) when set,
            // but MarshalCallbackArg can't bridge directly. Use FormatObjCBridgeCall
            // which dispatches between GetNSObject / GetINativeObject — same shape as the
            // Optional-of-ObjC-class path above.
            if (closureHandler.HasObjCNativeRemap(namedType, out var nativeRemapType))
                return MarshallingHelpers.FormatObjCBridgeCall(nativeRemapType, $"new IntPtr(arg{argIndex})");

            // Non-frozen struct cdecl param: the Swift adapter VWT-copies the value onto a
            // malloc-compatible heap buffer (UnsafeMutableRawPointer.allocate → swift_slowAlloc
            // → malloc on Darwin) and hands ownership to the C# callback — no Swift-side
            // defer. MarshalFromSwift<T> wraps the buffer in a SwiftSafeHandle whose
            // ReleaseHandle pairs VWT.Destroy + NativeMemory.Free. This makes it safe for the
            // callback to escape the wrapper (store it in a field, return it, etc.) without
            // UAF. The borrowed wrapper that used to live here would dangle the moment the
            // callback returned.
            if (useCdecl && closureHandler.IsNonFrozenStruct(namedType))
            {
                var ownedType = closureHandler.TranslateTypeSpecToCSharp(typeSpec);
                return $"SwiftMarshal.MarshalFromSwift<{ownedType}>(new IntPtr(arg{argIndex}))";
            }

            // Complex enums and ClassWithBufferStruct frozen structs are also heap-allocated
            // by the Swift adapter (Wrapper emits allocate + initializeMemory), and ownership
            // is transferred to the C# callback. `MarshalFromSwift<T>` constructs the
            // ISwiftObject wrapper whose SafeHandle pairs VWT.Destroy + NativeMemory.Free.
            // Without owning-transfer, capture-out of the borrowed wrapper UAFs on the next
            // GC cycle (or sooner under GC stress).
            if (useCdecl && (closureHandler.IsComplexEnum(namedType) ||
                             closureHandler.IsFrozenStructWithRefFields(namedType)))
            {
                var ownedType = closureHandler.TranslateTypeSpecToCSharp(typeSpec);
                return $"SwiftMarshal.MarshalFromSwift<{ownedType}>(new IntPtr(arg{argIndex}))";
            }

            // The callback receives void* but the delegate expects the actual type.
            // Callback parameters are borrowed references. For a class wrapper handed to the user's
            // closure body, route through MarshalBorrowedClassFromSwift so an explicit Dispose in
            // that body is balanced by a real +1; value-type wrappers (SwiftString /
            // Foundation.Data read-and-discard) keep the SuppressFinalize-only borrowed path, which
            // is correct because they are never surfaced to the user for Dispose.
            var delegateType = closureHandler.TranslateTypeSpecToCSharp(typeSpec);
            if (closureHandler.IsClassType(typeSpec))
                return $"SwiftMarshal.MarshalBorrowedClassFromSwift<{delegateType}>(new IntPtr(arg{argIndex}))";
            return $"SwiftMarshal.MarshalCallbackArg<{delegateType}>(new IntPtr(arg{argIndex}))";
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
    /// Checks if a type is Optional&lt;Bool/SimpleEnum&gt; with nil-for-none pointer ABI.
    /// Swift unwraps the optional, passes pointer to inner value (null for .none).
    /// </summary>
    private static bool IsOptionalNilForNoneParam(NamedTypeSpec namedType, ClosureHandler closureHandler)
    {
        return namedType.ContainsGenericParameters &&
               namedType.Name == "Swift.Optional" &&
               namedType.GenericParameters.Count == 1 &&
               namedType.GenericParameters[0] is NamedTypeSpec inner &&
               (MarshallingHelpers.IsBoolType(inner) ||
                closureHandler.IsSimpleEnum(inner));
    }

    /// <summary>
    /// Checks if a type is Optional&lt;FrozenStruct&gt; with nil-for-none pointer ABI.
    /// Swift unwraps the optional, passes pointer to the inner struct value (null for .none).
    /// C# reads the inner struct via MarshalCallbackArg.
    /// Excludes reference types (classes, ObjC-bridged) which use Optional-reference ABI.
    /// </summary>
    private static bool IsOptionalFrozenStructParam(NamedTypeSpec namedType, ClosureHandler closureHandler)
    {
        if (!namedType.ContainsGenericParameters ||
            namedType.Name != "Swift.Optional" ||
            namedType.GenericParameters.Count != 1 ||
            namedType.GenericParameters[0] is not NamedTypeSpec inner)
            return false;
        if (closureHandler.IsClassType(inner) || closureHandler.IsObjCBridgedClass(inner))
            return false;
        // Primitives (Int32, Double, etc.) are frozen structs in stdlib but use the
        // heap-allocated full-Optional path, not nil-for-none pointer ABI.
        // Bool is excluded too — it has its own nil-for-none branch upstream.
        if (MarshallingHelpers.IsSwiftPrimitive(inner.Name) || inner.Name == "Swift.Bool")
            return false;
        if (inner.Name.Contains("Pointer") || inner.Name == "Swift.OpaquePointer")
            return false;
        return closureHandler.IsFrozenStruct(inner);
    }

    /// <summary>
    /// Checks if a type is Optional&lt;NumericPrimitive&gt; with full Optional on heap (tag-byte layout).
    /// Excludes Bool and SimpleEnum which use nil-for-none pointer ABI instead.
    /// </summary>
    private static bool IsOptionalValueParam(NamedTypeSpec namedType, ClosureHandler closureHandler)
    {
        return namedType.ContainsGenericParameters &&
               namedType.Name == "Swift.Optional" &&
               namedType.GenericParameters.Count == 1 &&
               namedType.GenericParameters[0] is NamedTypeSpec inner &&
               CdeclParamMapper.IsBlittablePrimitiveSwiftType(inner.Name);
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
        // Cdecl closures with indirect return: prepend IntPtr (result buffer) and use void return.
        // Must match the @convention(c) type generated by GetSwiftConventionCType which inserts
        // UnsafeMutableRawPointer as the first param and returns Void for indirect return closures.
        bool isIndirectReturn = useCdecl
            && closureHandler.RequiresIndirectReturnMarshalling(closureTypeSpec)
            && !closureTypeSpec.Throws;

        var types = new List<string>();

        if (isIndirectReturn)
        {
            types.Add("IntPtr"); // result buffer — first parameter
        }

        foreach (var arg in closureTypeSpec.EachArgument())
        {
            // UnsafeRawBufferPointer / UnsafeMutableRawBufferPointer split into (void*, nint)
            // — must mirror EmitEscapingClosureCallback's parameter expansion exactly.
            if (useCdecl && MarshallingHelpers.IsAnyUnsafeRawBufferPointer(arg))
            {
                types.Add("void*");
                types.Add("nint");
            }
            else
            {
                types.Add(GetCallbackParameterType(arg, closureHandler, useCdecl));
            }
        }

        types.Add(isIndirectReturn ? "void" : GetEscapingClosureCallbackReturnType(closureTypeSpec, closureHandler));
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
            if (useCdecl && MarshallingHelpers.IsAnyUnsafeRawBufferPointer(arg))
            {
                types.Add("void*");
                types.Add("nint");
            }
            else
            {
                types.Add(GetCallbackParameterType(arg, closureHandler, useCdecl));
            }
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
    /// <param name="useNintCast">When true, casts to nint (safe context, invoke thunk P/Invoke).
    /// When false, casts to void* (unsafe context, fallback lambda with function pointers).</param>
    private static string GetSwiftInvokeArgExpression(TypeSpec typeSpec, int argIndex, ClosureHandler? closureHandler = null, bool useNintCast = false, List<string>? keepAliveVars = null)
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

        // Complex enum: extract payload handle for P/Invoke or function pointer
        if (closureHandler != null && closureHandler.IsComplexEnum(typeSpec))
        {
            var castType = useNintCast ? "nint" : "void*";
            return $"({castType})_arg{argIndex}.Payload.DangerousGetHandle()";
        }

        // Well-known protocol types: unwrap to ExistentialContainer for function pointer
        if (closureHandler != null && closureHandler.NeedsWellKnownProtocolWrapping(typeSpec, out _))
            return $"_arg{argIndex}.GetExistentialContainer()";

        // Known protocol: extract container from interface for function pointer
        if (closureHandler != null && closureHandler.NeedsProxyWrapping(typeSpec, out _))
        {
            if (closureHandler.ShouldUseGetOrCreate(typeSpec))
            {
                var pt = ResolveClosureExistentialOrDegrade(closureHandler, typeSpec);
                var qp = closureHandler.GetQualifiedProxyClassName(typeSpec);
                if (qp != null)
                {
                    // +0 borrowed existential closure ARG (design change 4 / mechanism 3): the
                    // auto-wrapped EC1 aliases the proxy's sole R0, which under B2's weak proxy
                    // registration a GC could release while the Swift function pointer borrows it.
                    // When the caller supplies a keepAliveVars sink, capture the proxy via the keepAlive
                    // GetOrCreate overload so the caller can GC.KeepAlive it after the native call. The
                    // qp == null path roots via the already-convertible/boxable _arg itself (no auto-wrap).
                    if (keepAliveVars != null)
                    {
                        var kaVar = $"_arg{argIndex}__ka";
                        keepAliveVars.Add(kaVar);
                        return $"Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{pt}>(_arg{argIndex}, static __v => new {qp}(__v), out _, out var {kaVar})";
                    }
                    return $"Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{pt}>(_arg{argIndex}, static __v => new {qp}(__v))";
                }
                return $"Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{pt}>(_arg{argIndex})";
            }
            var ct = closureHandler.GetPInvokeExistentialType(typeSpec);
            // +0 borrowed EC2+ composition closure ARG: no auto-wrap exists (a composition interface is
            // only implemented by the Swift-vended proxy), so _arg{argIndex} IS the proxy aliasing its
            // sole R0. Pin it across the native function-pointer call when a sink is supplied — the EC2+
            // analogue of the EC1 GetOrCreate keepAlive above (design change 4 / mechanism 3).
            if (keepAliveVars != null)
                keepAliveVars.Add($"_arg{argIndex}");
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
                            var pt = ResolveClosureExistentialOrDegrade(closureHandler, elem);
                            var qp = closureHandler.GetQualifiedProxyClassName(elem);
                            if (qp != null && keepAliveVars != null)
                            {
                                // +0 borrowed existential tuple-element closure ARG — pin the auto-wrapped
                                // proxy across the native call (design change 4 / mechanism 3). The out var
                                // declared in the nested tuple literal is in scope at the hoisted call site.
                                var kaVar = $"_arg{argIndex}_e{i}__ka";
                                keepAliveVars.Add(kaVar);
                                elements.Add($"Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{pt}>({acc}, static __v => new {qp}(__v), out _, out var {kaVar})");
                            }
                            else
                            {
                                elements.Add(qp != null
                                    ? $"Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{pt}>({acc}, static __v => new {qp}(__v))"
                                    : $"Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{pt}>({acc})");
                            }
                        }
                        else
                        {
                            var ct = closureHandler.GetPInvokeExistentialType(elem);
                            // +0 borrowed EC2+ composition tuple-element closure ARG — pin the proxy
                            // ({acc} is the element accessor, which IS the proxy: no auto-wrap for
                            // compositions) across the native call (design change 4 / mechanism 3).
                            if (keepAliveVars != null)
                                keepAliveVars.Add(acc);
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
