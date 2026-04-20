// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public static partial class ClosureEmitter
{
    /// <summary>
    /// Emits an [UnmanagedCallersOnly] "start" callback function for an async+throwing closure.
    /// This function is called synchronously by Swift and spawns Task.Run to execute the async work.
    /// When the async work completes, it calls the appropriate Swift callback (success or error).
    ///
    /// Per-arity (Session B): the Start thunk widens between (contextPtr, continuationBoxPtr)
    /// and (successFuncPtr, errorFuncPtr) with one ABI-typed slot per closure arg
    /// (primitive scalar, or IntPtr for String/class). Args are marshalled synchronously
    /// BEFORE Task.Run — Swift-owned pointers die the moment this thunk returns.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="methodName">The name of the method containing the closure parameter.</param>
    /// <param name="parameterName">The name of the closure parameter.</param>
    /// <param name="closureTypeSpec">The closure type specification (must be async+throwing).</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <param name="mangledName">The mangled name of the method (for callback disambiguation).</param>
    public static void EmitAsyncThrowingClosureCallback(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string mangledName)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName) + "_Start";
        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;

        // Return ABI type — projected when different from public (e.g., Data → byte[]).
        var returnAbiType = hasReturn
            ? closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true)
            : null;
        if (hasReturn)
        {
            var projection = new TypeProjectionFactory().Project(closureTypeSpec.ReturnType,
                new ProjectionContext { TypeDatabase = closureHandler.TypeDatabase, IsParameter = true });
            if (projection?.PInvokeType != null && projection.PInvokeType != projection.PublicType)
                returnAbiType = projection.PInvokeType;
        }

        // Per-arity arg info — ABI type (int/long/IntPtr), public type (int/string/Class),
        // raw param name a0/a1/…, managed var name a0Val/a1Val/….
        var args = closureTypeSpec.EachArgument().ToList();
        var argAbiTypes = args.Select(a => closureHandler.GetAsyncThrowingArgCSharpAbiType(a)).ToList();
        var argPublicTypes = args.Select(a => closureHandler.GetAsyncThrowingArgPublicCSharpType(a)).ToList();

        var isThrowing = closureTypeSpec.Throws;

        // State type: AsyncThrowingClosureState / AsyncThrowingClosureStateVoid
        // for the throwing baseline, or AsyncClosureState (primitive-return only)
        // for the Session C non-throwing baseline. Non-throwing has no void variant
        // yet — the generic skip path still rejects `async -> Void` closures.
        string stateType;
        if (isThrowing)
        {
            stateType = hasReturn
                ? args.Count == 0
                    ? $"AsyncThrowingClosureState<{returnAbiType}>"
                    : $"AsyncThrowingClosureState<{string.Join(", ", argPublicTypes)}, {returnAbiType}>"
                : args.Count == 0
                    ? "AsyncThrowingClosureStateVoid"
                    : $"AsyncThrowingClosureStateVoid<{string.Join(", ", argPublicTypes)}>";
        }
        else
        {
            stateType = args.Count == 0
                ? $"AsyncClosureState<{returnAbiType}>"
                : $"AsyncClosureState<{string.Join(", ", argPublicTypes)}, {returnAbiType}>";
        }

        // Check if return type is Data (special handling for byte arrays)
        var isDataReturn = hasReturn &&
            closureTypeSpec.ReturnType is NamedTypeSpec namedType &&
            (namedType.Name == "Foundation.Data" || namedType.Name == "Swift.Foundation.Data");
        if (isDataReturn && args.Count > 0)
        {
            throw new NotSupportedException(
                "Async-throwing closures with Data return and arguments are not supported; "
                + "widen DataAsyncClosureHelper first.");
        }

        // Build the Start thunk's param list: (ctx, box, a0_raw, a1_raw, …, successFP, errorFP).
        var paramLines = new List<string>
        {
            "IntPtr contextPtr,          // GCHandle to " + stateType,
            "IntPtr continuationBoxPtr,  // Swift's ContinuationBox pointer"
        };
        for (int i = 0; i < args.Count; i++)
            paramLines.Add($"{argAbiTypes[i]} a{i},                 // raw ABI value for closure arg {i}");
        paramLines.Add("IntPtr successFuncPtr,      // Function pointer for success callback");
        paramLines.Add("IntPtr errorFuncPtr)        // Function pointer for error callback");
        var paramList = string.Join("\n    ", paramLines);

        // Sync arg-marshal statements: produce a0Val/a1Val/… from a0/a1/…
        // before Task.Run. Indentation matches the generated C# method body.
        var marshalBlock = "";
        if (args.Count > 0)
        {
            var marshalLines = new List<string>();
            for (int i = 0; i < args.Count; i++)
            {
                var stmts = closureHandler.GetAsyncThrowingArgSyncMarshalStatements(args[i], $"a{i}", $"a{i}Val");
                foreach (var line in stmts.Split('\n'))
                    marshalLines.Add("    " + line);
            }
            marshalBlock = string.Join("\n", marshalLines) + "\n";
        }

        // The helper call: AsyncClosureHelper.RunAsync[<A0Public,…>,TResult](handle, state, contBox, a0Val, a1Val, …, success, error)
        // — extra generic args are inferred from the `state` argument, so no explicit generics needed.
        var argValList = string.Concat(Enumerable.Range(0, args.Count).Select(i => $", a{i}Val"));
        string helperName;
        if (!isThrowing)
            helperName = "RunAsyncNonThrowing";
        else
            helperName = hasReturn ? "RunAsync" : "RunVoidAsync";

        csWriter.WriteLines($$"""
            /// <summary>
            /// [UnmanagedCallersOnly] start function for async+throwing closure parameter '{{parameterName}}'.
            /// Called synchronously by Swift, marshals args, spawns Task.Run to execute the async delegate.
            /// </summary>
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
            private static unsafe void {{callbackName}}(
                {{paramList}}
            {
                var handle = GCHandle.FromIntPtr(contextPtr);
                if (handle.Target is not {{stateType}} state)
                    return;

            {{marshalBlock}}    // Convert function pointers to delegates while we're in the unsafe context.
                // These delegates can then be called from the async code without unsafe blocks.
            """);

        if (isDataReturn)
        {
            // Data return type — same shape as Session A, no args.
            csWriter.WriteLines($$"""
                    var successAction = new Action<IntPtr, IntPtr, nint>((box, dataPtr, len) =>
                    {
                        var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nint, void>)successFuncPtr;
                        fp(box, dataPtr, len);
                    });
                    var errorAction = new Action<IntPtr, IntPtr>((box, errPtr) =>
                    {
                        var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)errorFuncPtr;
                        fp(box, errPtr);
                    });

                    Swift.Foundation.DataAsyncClosureHelper.RunDataAsync(handle, state, continuationBoxPtr, successAction, errorAction);
                }
                """);
        }
        else if (hasReturn && !isThrowing)
        {
            // Session C non-throwing: success-only helper, no error channel.
            // errorFuncPtr param stays in the Start signature (uniform ABI with
            // the throwing variant per §3.6(a)) but is intentionally unused —
            // the Swift adapter passes a sentinel pointer for it.
            csWriter.WriteLines($$"""
                    _ = errorFuncPtr; // unused on the non-throwing path — FailFast handles exceptions
                    var successAction = new Action<IntPtr, IntPtr>((box, resultPtr) =>
                    {
                        var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)successFuncPtr;
                        fp(box, resultPtr);
                    });

                    AsyncClosureHelper.{{helperName}}(handle, state, continuationBoxPtr{{argValList}}, successAction);
                }
                """);
        }
        else if (hasReturn)
        {
            csWriter.WriteLines($$"""
                    var successAction = new Action<IntPtr, IntPtr>((box, resultPtr) =>
                    {
                        var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)successFuncPtr;
                        fp(box, resultPtr);
                    });
                    var errorAction = new Action<IntPtr, IntPtr>((box, errPtr) =>
                    {
                        var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)errorFuncPtr;
                        fp(box, errPtr);
                    });

                    AsyncClosureHelper.{{helperName}}(handle, state, continuationBoxPtr{{argValList}}, successAction, errorAction);
                }
                """);
        }
        else
        {
            csWriter.WriteLines($$"""
                    var successAction = new Action<IntPtr>((box) =>
                    {
                        var fp = (delegate* unmanaged[Cdecl]<IntPtr, void>)successFuncPtr;
                        fp(box);
                    });
                    var errorAction = new Action<IntPtr, IntPtr>((box, errPtr) =>
                    {
                        var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)errorFuncPtr;
                        fp(box, errPtr);
                    });

                    AsyncClosureHelper.{{helperName}}(handle, state, continuationBoxPtr{{argValList}}, successAction, errorAction);
                }
                """);
        }
    }

    /// <summary>
    /// Emits the static field that holds the function pointer for an async+throwing closure's start callback.
    /// The function pointer type is per-arity — args widen the middle of the signature.
    /// </summary>
    public static void EmitAsyncThrowingClosureCallbackPointer(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string mangledName)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName) + "_Start";
        var funcPtrType = closureHandler.GetAsyncThrowingStartFunctionPointerType(closureTypeSpec);
        csWriter.WriteLine($"private static unsafe readonly {funcPtrType} s_{callbackName} = &{callbackName};");
    }

    /// <summary>
    /// Emits code to set up marshalling for an async+throwing closure parameter.
    /// Creates the per-arity AsyncThrowingClosureState and allocates a GCHandle.
    /// </summary>
    public static void EmitAsyncThrowingClosureMarshallingSetup(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string mangledName)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName) + "_Start";
        var handleVar = $"{parameterName}Handle";
        var stateVar = $"{parameterName}State";

        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;

        // Return ABI type (projected if different from public — e.g. Data → byte[]).
        var returnPublicType = hasReturn
            ? closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true)
            : null;
        var returnAbiType = returnPublicType;
        ITypeProjection? returnProjection = null;
        if (hasReturn)
        {
            returnProjection = new TypeProjectionFactory().Project(closureTypeSpec.ReturnType,
                new ProjectionContext { TypeDatabase = closureHandler.TypeDatabase, IsParameter = true });
            if (returnProjection?.PInvokeType != null && returnProjection.PInvokeType != returnProjection.PublicType)
                returnAbiType = returnProjection.PInvokeType;
        }

        var args = closureTypeSpec.EachArgument().ToList();
        var argPublicTypes = args.Select(a => closureHandler.GetAsyncThrowingArgPublicCSharpType(a)).ToList();
        var isThrowing = closureTypeSpec.Throws;

        string stateType;
        if (isThrowing)
        {
            stateType = hasReturn
                ? args.Count == 0
                    ? $"AsyncThrowingClosureState<{returnAbiType}>"
                    : $"AsyncThrowingClosureState<{string.Join(", ", argPublicTypes)}, {returnAbiType}>"
                : args.Count == 0
                    ? "AsyncThrowingClosureStateVoid"
                    : $"AsyncThrowingClosureStateVoid<{string.Join(", ", argPublicTypes)}>";
        }
        else
        {
            stateType = args.Count == 0
                ? $"AsyncClosureState<{returnAbiType}>"
                : $"AsyncClosureState<{string.Join(", ", argPublicTypes)}, {returnAbiType}>";
        }

        // If the public delegate return type differs from the ABI type (Data case),
        // wrap the user delegate with ContinueWith to materialise an ABI-shaped value.
        string asyncFuncExpr = parameterName;
        if (hasReturn && returnAbiType != null)
        {
            var paramConversion = (returnProjection != null && returnProjection.PublicType != returnAbiType)
                ? returnProjection.GetParameterElementConversion("r")
                : null;
            if (paramConversion != null)
            {
                if (args.Count > 0)
                    throw new NotSupportedException(
                        "Async-throwing closures with both arg-bearing signatures and projected "
                        + "return types (e.g. Data) are not supported yet.");
                asyncFuncExpr = $"() => {parameterName}().ContinueWith(t => {{ var r = t.GetAwaiter().GetResult(); return {paramConversion}; }}, TaskContinuationOptions.ExecuteSynchronously)";
            }
        }

        csWriter.WriteLines($$"""
            var {{stateVar}} = new {{stateType}} { AsyncFunc = {{asyncFuncExpr}} };
            var {{handleVar}} = GCHandle.Alloc({{stateVar}});
            var {{parameterName}}ContextPtr = GCHandle.ToIntPtr({{handleVar}});
            """);
    }

}
