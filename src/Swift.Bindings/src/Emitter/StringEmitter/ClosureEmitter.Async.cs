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
    /// Per-arity: the Start thunk widens between (contextPtr, continuationBoxPtr)
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

        // String-return async throwing closures route through StringAsyncClosureHelper
        // and must keep the ABI type as "string" (not SwiftString). Projection override
        // is skipped for this case so the generated AsyncThrowingClosureState binds to
        // the user's Task<string> without a ContinueWith shim.
        var isStringReturn = hasReturn &&
            closureTypeSpec.ReturnType is NamedTypeSpec namedReturnForStringCheck &&
            namedReturnForStringCheck.Name == "Swift.String";

        // Return ABI type — projected when different from public (e.g., Data → byte[]).
        var returnAbiType = hasReturn
            ? closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true)
            : null;
        if (hasReturn && !isStringReturn)
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
        // for the non-throwing baseline. Non-throwing has no void variant
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

        // Fail closed: non-throwing async closures only carry blittable-primitive returns
        // (enforced upstream by ClosureHandler.IsBaselineAsyncNonThrowingClosure). String/Data
        // returns route exclusively through the throwing continuation helpers — RunStringAsync /
        // RunDataAsync bind an AsyncThrowingClosureState, not the non-throwing AsyncClosureState
        // this path would build. If that upstream gate is ever widened without teaching this
        // emitter a non-throwing String/Data shape, refuse at generation time rather than emit a
        // state-type mismatch (non-compiling) or wire an error channel a non-throwing closure
        // does not have.
        if ((isDataReturn || isStringReturn) && !isThrowing)
        {
            throw new NotSupportedException(
                "Non-throwing async closures with String or Data returns are not supported; "
                + "the String/Data continuation helpers require a throwing continuation state.");
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
        // before Task.Run. Written into the guarded body at the try-body indent (Stage 3),
        // so they carry no literal indentation of their own.
        var marshalLines = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            var stmts = closureHandler.GetAsyncThrowingArgSyncMarshalStatements(args[i], $"a{i}", $"a{i}Val");
            foreach (var line in stmts.Split('\n'))
                marshalLines.Add(line);
        }

        // The helper call: AsyncClosureHelper.RunAsync[<A0Public,…>,TResult](handle, state, contBox, a0Val, a1Val, …, success, error)
        // — extra generic args are inferred from the `state` argument, so no explicit generics needed.
        var argValList = string.Concat(Enumerable.Range(0, args.Count).Select(i => $", a{i}Val"));
        string helperName;
        if (!isThrowing)
            helperName = "RunAsyncNonThrowing";
        else
            helperName = hasReturn ? "RunAsync" : "RunVoidAsync";

        // Finding 37 — mechanical resume-once. Every exit from this Start thunk resumes the Swift
        // continuation box exactly once: the success/error completion delegates each claim a shared
        // resume guard, and the two synchronous failure paths (the should-never-happen context-type
        // mismatch and any arg-marshalling exception) resume the box with an error — for throwing
        // closures the ResumeBoxError policy via AsyncClosureHelper.ReportError, for non-throwing
        // closures a loud FailFast (they have no Swift error channel) — instead of returning
        // silently and leaving the Swift task awaiting forever.
        var contextDesc = $"{methodName}.{parameterName}";
        var mismatchMsg = $"[SwiftBindings] async closure context for '{contextDesc}' did not contain the "
            + "expected state type; resuming the Swift continuation with an error instead of dropping it.";
        string targetMismatchBody;
        string ucoCatchBody;
        if (isThrowing)
        {
            targetMismatchBody = "global::Swift.Runtime.AsyncClosureHelper.ReportError("
                + $"new global::System.InvalidOperationException(\"{mismatchMsg}\"), continuationBoxPtr, errorAction);";
            ucoCatchBody = "global::Swift.Runtime.AsyncClosureHelper.ReportError(__uco_ex, continuationBoxPtr, errorAction);";
        }
        else
        {
            targetMismatchBody = "global::Swift.Runtime.AsyncClosureHelper.FailFastNonThrowing("
                + $"new global::System.InvalidOperationException(\"{mismatchMsg}\"));";
            ucoCatchBody = "global::Swift.Runtime.AsyncClosureHelper.FailFastNonThrowing(__uco_ex);";
        }

        // Stage 1 — signature + shared preamble: the single resume guard that every resume path
        // (success, error, and the synchronous failure paths below) claims. The context handle is
        // resolved INSIDE the guarded body (Stage 3), not here: GCHandle.FromIntPtr throws on a
        // zero/corrupt contextPtr, and resolving it before the try would let that escape the
        // [UnmanagedCallersOnly] boundary with the box never resumed. The guard and the success/
        // error delegates must be constructed before the try because the catch resumes through
        // errorAction; their allocation is not wrapped (a resume requires the delegate that resumes).
        csWriter.WriteLines($$"""
            /// <summary>
            /// [UnmanagedCallersOnly] start function for async+throwing closure parameter '{{parameterName}}'.
            /// Called synchronously by Swift, marshals args, spawns Task.Run to execute the async delegate.
            /// Every exit — bad context handle, context mismatch, marshalling fault, or async
            /// completion — resumes the Swift continuation box exactly once via __resumeGuard,
            /// never silently (Finding 37).
            /// </summary>
            [global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
            private static unsafe void {{callbackName}}(
                {{paramList}}
            {
                var __resumeGuard = new global::Swift.Runtime.AsyncResumeGuard();
            """);

        // Stage 2 — branch-specific success/error callback delegates. Each delegate claims the
        // shared guard before invoking its Swift @_cdecl symbol, so a success and an error (or a
        // racing duplicate) can never both consume the same continuation box. Declared before the
        // guarded body so the ResumeBoxError catch below can reach errorAction.
        if (isDataReturn || isStringReturn)
        {
            // Data / String success callback carries (box, bytesPtr, length) of pinned UTF-8 bytes.
            // Data is arity-0; String supports full arity (0–MaxAsyncThrowingClosureArity).
            csWriter.WriteLines($$"""
                    var successAction = new Action<IntPtr, IntPtr, nint>((box, bytesPtr, len) =>
                    {
                        if (!__resumeGuard.TryClaim()) return;
                        var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nint, void>)successFuncPtr;
                        fp(box, bytesPtr, len);
                    });
                    var errorAction = new Action<IntPtr, IntPtr>((box, errPtr) =>
                    {
                        if (!__resumeGuard.TryClaim()) return;
                        var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)errorFuncPtr;
                        fp(box, errPtr);
                    });
                """);
        }
        else if (hasReturn && !isThrowing)
        {
            // Non-throwing: success-only helper, no error channel. errorFuncPtr stays in the ABI
            // signature (uniform with the throwing variant) but is unused — failures FailFast.
            csWriter.WriteLines($$"""
                    _ = errorFuncPtr; // unused on the non-throwing path — failures FailFast
                    var successAction = new Action<IntPtr, IntPtr>((box, resultPtr) =>
                    {
                        if (!__resumeGuard.TryClaim()) return;
                        var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)successFuncPtr;
                        fp(box, resultPtr);
                    });
                """);
        }
        else if (hasReturn)
        {
            csWriter.WriteLines($$"""
                    var successAction = new Action<IntPtr, IntPtr>((box, resultPtr) =>
                    {
                        if (!__resumeGuard.TryClaim()) return;
                        var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)successFuncPtr;
                        fp(box, resultPtr);
                    });
                    var errorAction = new Action<IntPtr, IntPtr>((box, errPtr) =>
                    {
                        if (!__resumeGuard.TryClaim()) return;
                        var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)errorFuncPtr;
                        fp(box, errPtr);
                    });
                """);
        }
        else
        {
            csWriter.WriteLines($$"""
                    var successAction = new Action<IntPtr>((box) =>
                    {
                        if (!__resumeGuard.TryClaim()) return;
                        var fp = (delegate* unmanaged[Cdecl]<IntPtr, void>)successFuncPtr;
                        fp(box);
                    });
                    var errorAction = new Action<IntPtr, IntPtr>((box, errPtr) =>
                    {
                        if (!__resumeGuard.TryClaim()) return;
                        var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)errorFuncPtr;
                        fp(box, errPtr);
                    });
                """);
        }

        // Stage 3 — open the guarded body via the shared UCO envelope (Finding 38): resolve the
        // context handle (GCHandle.FromIntPtr throws on a zero/corrupt contextPtr — caught by the
        // envelope and resumed with an error / FailFast, never an escape past the UCO boundary),
        // verify it carries the expected state (resuming with an error if not, never returning
        // silently), then marshal the args. csWriter.Indent moves to the method-body level so the
        // envelope's try/catch and the method's closing brace nest correctly.
        csWriter.Indent++;
        UcoGuardEmitter.EmitOpen(csWriter);
        csWriter.WriteLine("var handle = GCHandle.FromIntPtr(contextPtr);");
        csWriter.WriteLine($"if (handle.Target is not {stateType} state)");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine(targetMismatchBody);
        csWriter.WriteLine("return;");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        foreach (var line in marshalLines)
            csWriter.WriteLine(line);

        // Stage 4 — branch-specific helper invocation (spawns Task.Run and returns synchronously).
        if (isDataReturn)
        {
            // DataAsyncClosureHelper lives in the SwiftBindings.Apple supplement (unlike its
            // Swift.Runtime String sibling below). The Data-return async path is name-gated,
            // bypassing the projection path that records the supplement reference — record here
            // so the generated csproj carries the SwiftBindings.Apple PackageReference.
            AppleSupplementReferences.Record("Foundation.Data", "ClosureEmitter.Async:DataAsyncClosureHelper");
            csWriter.WriteLine("Swift.Foundation.DataAsyncClosureHelper.RunDataAsync(handle, state, continuationBoxPtr, successAction, errorAction);");
        }
        else if (isStringReturn)
            csWriter.WriteLine($"Swift.Runtime.StringAsyncClosureHelper.RunStringAsync(handle, state, continuationBoxPtr{argValList}, successAction, errorAction);");
        else if (hasReturn && !isThrowing)
            csWriter.WriteLine($"AsyncClosureHelper.{helperName}(handle, state, continuationBoxPtr{argValList}, successAction);");
        else
            csWriter.WriteLine($"AsyncClosureHelper.{helperName}(handle, state, continuationBoxPtr{argValList}, successAction, errorAction);");

        // Stage 5 — close the guarded body through the ResumeBoxError policy: a synchronous escape
        // resumes the box once with an error (throwing closures) or FailFasts (non-throwing). The
        // resume statements are supplied here (ucoCatchBody); the try/catch structure is the shared
        // envelope's. Then close the generated method.
        UcoGuardEmitter.EmitClose(csWriter, UcoGuardEmitter.UcoFaultPolicy.ResumeBoxError,
            resumeErrorBody: new[] { ucoCatchBody });
        csWriter.Indent--;
        csWriter.WriteLine("}");
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

        // String-return async-throwing closures keep the ABI type as "string" so the
        // emitted AsyncThrowingClosureState<string> binds to the caller's Task<string>
        // directly — StringAsyncClosureHelper handles UTF-8 marshalling on the success
        // path, so no ContinueWith wrapper is needed.
        var isStringReturnSetup = hasReturn &&
            closureTypeSpec.ReturnType is NamedTypeSpec namedReturnForStringCheckSetup &&
            namedReturnForStringCheckSetup.Name == "Swift.String";

        // Return ABI type (projected if different from public — e.g. Data → byte[]).
        var returnPublicType = hasReturn
            ? closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true)
            : null;
        var returnAbiType = returnPublicType;
        ITypeProjection? returnProjection = null;
        if (hasReturn && !isStringReturnSetup)
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
            string? paramConversion = null;
            if (returnProjection != null && returnProjection.PublicType != returnAbiType)
            {
                // Existential async RETURN: the awaited delegate result is handed to Swift at +1
                // (owned) — the ContinueWith lambda writes it into the ABI-shaped return slot and Swift
                // adopts it. Mint an independent +1 rather than borrow the proxy's R0, mirroring the
                // synchronous closure return (F2 / BuildCallbackReturnStatement). No-op for the
                // non-existential projected-return case (e.g. Data).
                paramConversion = returnProjection is ExistentialProjection existAsyncRet
                    ? existAsyncRet.GetOwnedParameterElementConversion("r")
                    : returnProjection.GetParameterElementConversion("r");
            }
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
