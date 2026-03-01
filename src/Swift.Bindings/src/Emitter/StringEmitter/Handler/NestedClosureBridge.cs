// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits C# code for methods with nested closures (closure-in-closure parameters).
/// Takes over entire method emission from MethodHandler when eligible.
/// <para>
/// Two-level bridge ABI: the outer closure goes C# → Swift (existing pattern),
/// while the inner closure goes Swift → C# (new). Swift provides the inner closure
/// as a callback argument; we decompose it into funcPtr+context to cross the cdecl
/// boundary, then reconstruct it as Action&lt;T&gt; in C#.
/// </para>
/// <para>
/// Spike scope: single outer closure with exactly one inner closure arg,
/// inner closure must have void return and all-cdecl-compatible args.
/// </para>
/// </summary>
public static class NestedClosureBridge
{
    /// <summary>
    /// Checks if a method is eligible for the NestedClosureBridge pattern.
    /// </summary>
    public static bool IsEligible(MethodDecl method, ClosureHandler closureHandler, ITypeDatabase typeDatabase)
    {
        // Not for protocol extensions, async, constructors, accessors, or throwing
        if (method.IsProtocolExtensionMethod) return false;
        if (method.IsAsync) return false;
        if (method.IsConstructor) return false;
        if (method.IsAccessor) return false;
        if (method.Throws) return false;

        // Find exactly one closure parameter
        ClosureTypeSpec? outerClosureSpec = null;
        ArgumentDecl? closureArg = null;
        int closureCount = 0;

        foreach (var arg in method.CSSignature.Skip(1))
        {
            var cts = closureHandler.GetClosureTypeSpec(arg);
            if (cts != null)
            {
                closureCount++;
                if (closureCount > 1) return false;
                outerClosureSpec = cts;
                closureArg = arg;
            }
        }

        if (outerClosureSpec == null || closureArg == null) return false;

        // Outer closure must have void return and not be async
        if (!outerClosureSpec.ReturnType.IsEmptyTuple) return false;
        if (outerClosureSpec.IsAsync) return false;

        // Find exactly one inner ClosureTypeSpec among outer closure args
        ClosureTypeSpec? innerClosureSpec = null;
        int innerClosureCount = 0;

        foreach (var outerArg in outerClosureSpec.EachArgument())
        {
            if (outerArg is ClosureTypeSpec innerCts)
            {
                innerClosureCount++;
                if (innerClosureCount > 1) return false;
                innerClosureSpec = innerCts;
            }
            else
            {
                // Non-closure outer args must be cdecl-compatible but NOT Optional<ref>
                // (Optional<ref> passes IsCdeclCompatibleType but our trampoline conversions
                // don't handle it — they'd emit invalid Unmanaged<Optional<T>>)
                if (!ClosureEmitter.IsCdeclCompatibleType(outerArg, closureHandler))
                    return false;
                if (IsOptionalReferenceType(outerArg, closureHandler))
                    return false;
            }
        }

        if (innerClosureSpec == null) return false;

        // Inner closure must have void return and not be async
        if (!innerClosureSpec.ReturnType.IsEmptyTuple) return false;
        if (innerClosureSpec.IsAsync) return false;

        // Inner closure args must all be cdecl-compatible (but not Optional<ref>)
        foreach (var innerArg in innerClosureSpec.EachArgument())
        {
            if (!ClosureEmitter.IsCdeclCompatibleType(innerArg, closureHandler))
                return false;
            if (IsOptionalReferenceType(innerArg, closureHandler))
                return false;
        }

        // Non-closure method params: each must be passable or have a default value
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg == closureArg) continue;
            if (arg.HasDefaultArg) continue;

            var category = MethodClosureBridge.ClassifyParam(arg, typeDatabase);
            if (category is not (MethodClosureBridge.ParamAbiCategory.Primitive
                or MethodClosureBridge.ParamAbiCategory.ObjCHandle
                or MethodClosureBridge.ParamAbiCategory.PayloadHandle))
                return false;
        }

        // Method return type: void, DynamicSelf, class, or primitive
        if (method.CSSignature.Count > 0)
        {
            var returnSpec = method.CSSignature[0].SwiftTypeSpec;
            if (!returnSpec.IsEmptyTuple)
            {
                if (!IsReturnTypeSupported(returnSpec, typeDatabase))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Attempts to emit a nested closure bridge for the given method.
    /// Returns true if the method was handled (caller should skip normal emission).
    /// </summary>
    public static bool TryEmit(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        TypeDecl? parentDecl,
        ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        var method = env.MethodDecl;

        if (!IsEligible(method, env.ClosureHandler, env.TypeDatabase))
            return false;

        // Find the outer closure parameter and its inner closure
        ClosureTypeSpec? outerClosureSpec = null;
        ArgumentDecl? closureArg = null;
        foreach (var arg in method.CSSignature.Skip(1))
        {
            var cts = env.ClosureHandler.GetClosureTypeSpec(arg);
            if (cts != null)
            {
                outerClosureSpec = cts;
                closureArg = arg;
                break;
            }
        }

        if (outerClosureSpec == null || closureArg == null)
            return false;

        // Decompose outer closure args into non-closure args + inner closure
        var outerNonClosureArgs = new List<TypeSpec>();
        ClosureTypeSpec? innerClosureSpec = null;
        int innerClosureIndex = -1;
        int outerArgIdx = 0;
        foreach (var outerArg in outerClosureSpec.EachArgument())
        {
            if (outerArg is ClosureTypeSpec cts)
            {
                innerClosureSpec = cts;
                innerClosureIndex = outerArgIdx;
            }
            else
            {
                outerNonClosureArgs.Add(outerArg);
            }
            outerArgIdx++;
        }

        if (innerClosureSpec == null || innerClosureIndex < 0)
            return false;

        var outerArgs = outerClosureSpec.EachArgument().ToList();
        var innerArgs = innerClosureSpec.EachArgument().ToList();

        var asyncLibName = env.TypeDatabase.AsyncLibraryName ?? "SwiftBindings";
        var mangledHash = EmitterUtility.DeterministicHash8(method.MangledName);
        var closureParamName = NameProvider.GetCSharpParameterName(closureArg);
        var callbackBaseName = $"NCB_{mangledHash}";

        // Determine which non-closure method params to pass through
        var passableNonClosureParams = new List<(ArgumentDecl arg, string csName, string csType, MethodClosureBridge.ParamAbiCategory category)>();
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg == closureArg) continue;
            if (arg.HasDefaultArg) continue;

            var csName = NameProvider.GetCSharpParameterName(arg);
            var (csType, category) = GetNonClosureParamCSharpType(arg, env);
            passableNonClosureParams.Add((arg, csName, csType, category));
        }

        // Emit Swift wrapper
        EmitSwiftWrapper(swiftWriter, method, env, parentDecl, closureArg, outerClosureSpec,
            outerArgs, innerClosureSpec, innerArgs, innerClosureIndex,
            passableNonClosureParams, callbackBaseName);

        // Set method flags for wrapper library routing
        method.UsesWrapperLibrary = true;
        method.UsesFreeFunctionWrapper = true;

        // For generic containing types, emit callback + funcPtr + P/Invoke into the
        // PInvokeHelperContext's non-generic helper class to avoid CS7042.
        var helperClassName = "";
        if (env.PInvokeHelperContext != null)
        {
            helperClassName = env.PInvokeHelperContext.HelperClassName;
            var helperWriter = new System.IO.StringWriter();
            var helperCsWriter = new CSharpWriter(helperWriter) { Indent = 0 };

            EmitCallback(helperCsWriter, outerArgs, innerClosureSpec, innerArgs,
                innerClosureIndex, callbackBaseName, env);
            EmitFunctionPointerField(helperCsWriter, outerArgs, innerClosureSpec, innerArgs,
                innerClosureIndex, callbackBaseName, env);
            EmitPInvoke(helperCsWriter, method, asyncLibName, closureArg, passableNonClosureParams,
                callbackBaseName, env);

            helperCsWriter.Flush();
            env.PInvokeHelperContext.RawCodeBlocks.Add(helperWriter.ToString());
        }
        else
        {
            EmitCallback(csWriter, outerArgs, innerClosureSpec, innerArgs,
                innerClosureIndex, callbackBaseName, env);
            EmitFunctionPointerField(csWriter, outerArgs, innerClosureSpec, innerArgs,
                innerClosureIndex, callbackBaseName, env);
            EmitPInvoke(csWriter, method, asyncLibName, closureArg, passableNonClosureParams,
                callbackBaseName, env);
        }

        // Public method always in the class body
        EmitPublicMethod(csWriter, method, outerClosureSpec, closureArg, outerArgs,
            innerClosureSpec, innerArgs, innerClosureIndex, passableNonClosureParams,
            callbackBaseName, closureParamName, env, parentDecl, helperClassName);

        method.WasEmitted = true;
        return true;
    }

    // ─── Swift Wrapper ─────────────────────────────────────────────────

    private static void EmitSwiftWrapper(
        SwiftWriter swiftWriter,
        MethodDecl method,
        MethodEnvironment env,
        TypeDecl? parentDecl,
        ArgumentDecl closureArg,
        ClosureTypeSpec outerClosureSpec,
        List<TypeSpec> outerArgs,
        ClosureTypeSpec innerClosureSpec,
        List<TypeSpec> innerArgs,
        int innerClosureIndex,
        List<(ArgumentDecl arg, string csName, string csType, MethodClosureBridge.ParamAbiCategory category)> passableNonClosureParams,
        string callbackBaseName)
    {
        bool isInstance = method.MethodType != MethodType.Static && parentDecl != null;
        var typeName = parentDecl?.SwiftTypeName?.ModuleQualifiedName ?? parentDecl?.Name ?? "";

        var silgenName = $"SBW_{callbackBaseName}_{method.Name}";

        // Build Swift wrapper params
        var swiftParams = new List<string>();

        // Non-closure passable method params first
        foreach (var (arg, csName, _, category) in passableNonClosureParams)
        {
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
            var paramName = NameProvider.EscapeSwiftKeyword(csName);
            swiftParams.Add($"    _ {paramName}: {swiftType}");
        }

        // Outer closure → funcPtr + context pair
        var closureCsName = NameProvider.StripVerbatimPrefix(NameProvider.GetCSharpParameterName(closureArg));
        swiftParams.Add($"    _ {closureCsName}FuncPtr: UnsafeMutableRawPointer?");
        swiftParams.Add($"    _ {closureCsName}Context: UnsafeMutableRawPointer?");

        // Build @convention(c) type for the outer cdecl callback
        // Outer callback receives: non-closure outer args (category-based) + inner funcPtr + inner context + outer context
        var cdeclParamTypes = new List<string>();
        for (int i = 0; i < outerArgs.Count; i++)
        {
            if (i == innerClosureIndex)
            {
                // Inner closure → funcPtr + context pair
                cdeclParamTypes.Add("UnsafeMutableRawPointer?"); // innerFuncPtr
                cdeclParamTypes.Add("UnsafeMutableRawPointer?"); // innerContext
            }
            else
            {
                cdeclParamTypes.Add(GetSwiftCdeclParamType(outerArgs[i], env));
            }
        }
        cdeclParamTypes.Add("UnsafeMutableRawPointer?"); // outer context
        var cdeclType = $"(@convention(c) ({string.Join(", ", cdeclParamTypes)}) -> Void).self";

        // Build inner trampoline: @convention(c) function for the inner closure
        // Takes inner closure args (cdecl types) + closure box
        var innerCdeclParamTypes = new List<string>();
        for (int i = 0; i < innerArgs.Count; i++)
        {
            innerCdeclParamTypes.Add(GetSwiftCdeclParamType(innerArgs[i], env));
        }
        innerCdeclParamTypes.Add("UnsafeMutableRawPointer"); // closure box (non-optional)
        var innerTrampolineType = $"@convention(c) ({string.Join(", ", innerCdeclParamTypes)}) -> Void";

        // Build the inner closure's Swift type string for unsafeBitCast
        var innerClosureSwiftArgTypes = new List<string>();
        foreach (var innerArg in innerArgs)
        {
            innerClosureSwiftArgTypes.Add(ExistentialBypassEmitter.RenderSwiftTypeSpec(innerArg));
        }
        var innerClosureSwiftType = innerClosureSwiftArgTypes.Count switch
        {
            0 => "() -> Void",
            1 => $"({innerClosureSwiftArgTypes[0]}) -> Void",
            _ => $"({string.Join(", ", innerClosureSwiftArgTypes)}) -> Void"
        };

        // Build return type
        var returnSpec = method.CSSignature[0].SwiftTypeSpec;
        bool returnsValue = !returnSpec.IsEmptyTuple;
        var swiftReturnType = returnsValue ? $" -> {ExistentialBypassEmitter.RenderSwiftTypeSpec(returnSpec)}" : "";

        // Emit the wrapper
        swiftWriter.WriteLine($"extension {typeName} {{");

        // Emit inner trampoline as a nested function (non-capturing → can be cast to @convention(c))
        swiftWriter.WriteLine($"@_silgen_name(\"{silgenName}\")");
        var funcKeyword = isInstance ? "public func" : "public static func";
        swiftWriter.WriteLine($"{funcKeyword} _sb_{method.Name}(");
        swiftWriter.WriteLine(string.Join(",\n", swiftParams));
        swiftWriter.WriteLine($"){swiftReturnType} {{");

        // Define the inner trampoline as a local @convention(c) function
        var innerTrampolineParams = new List<string>();
        for (int i = 0; i < innerArgs.Count; i++)
        {
            innerTrampolineParams.Add($"_ __ip{i}: {GetSwiftCdeclParamType(innerArgs[i], env)}");
        }
        innerTrampolineParams.Add("_ __closureBox: UnsafeMutableRawPointer");

        swiftWriter.WriteLine($"    let innerTrampoline: {innerTrampolineType} = {{ {string.Join(", ", innerTrampolineParams.Select(p => p.Split(' ')[1].TrimEnd(':')))} in");

        // Inside the trampoline: unbox the inner closure and invoke it
        swiftWriter.WriteLine($"        let innerClosure = Unmanaged<AnyObject>.fromOpaque(__closureBox).takeUnretainedValue() as! {innerClosureSwiftType}");

        // Build invocation args — convert cdecl types back to Swift types
        var innerInvocationArgs = new List<string>();
        for (int i = 0; i < innerArgs.Count; i++)
        {
            innerInvocationArgs.Add(GetSwiftTrampolineArgConversion(innerArgs[i], $"__ip{i}", env));
        }
        swiftWriter.WriteLine($"        innerClosure({string.Join(", ", innerInvocationArgs)})");

        swiftWriter.WriteLine("    }");
        swiftWriter.WriteLine();

        // Reconstruct outer cdecl function from pointer
        swiftWriter.WriteLine($"    let cdecl = unsafeBitCast({closureCsName}FuncPtr!, to: {cdeclType})");
        swiftWriter.WriteLine();

        // Build adapter closure that wraps each outer arg
        var callLabel = GetSwiftArgLabel(closureArg);
        var nonClosureCallArgs = new List<string>();
        foreach (var (arg, csName, _, _) in passableNonClosureParams)
        {
            var label = GetSwiftArgLabel(arg);
            var paramName = NameProvider.EscapeSwiftKeyword(csName);
            nonClosureCallArgs.Add($"{label}{paramName}");
        }

        // Build outer closure param declarations
        var outerParamDecls = new List<string>();
        for (int i = 0; i < outerArgs.Count; i++)
        {
            outerParamDecls.Add($"__op{i}");
        }
        var outerParamStr = string.Join(", ", outerParamDecls);

        var returnPrefix = returnsValue ? "return " : "";
        var callTarget = isInstance ? "self" : "Self";

        var prefixStr = nonClosureCallArgs.Count > 0
            ? string.Join(", ", nonClosureCallArgs) + ", "
            : "";

        // Open the method call with trailing closure syntax
        swiftWriter.WriteLine($"    {returnPrefix}{callTarget}.{NameProvider.ParserNameToSwift(method)}({prefixStr}{callLabel}{{ {outerParamStr} in");

        // Inside the adapter closure: convert each outer arg and call cdecl
        var cdeclCallArgs = new List<string>();
        for (int i = 0; i < outerArgs.Count; i++)
        {
            if (i == innerClosureIndex)
            {
                // Box the inner closure and pass trampoline funcPtr + box context
                swiftWriter.WriteLine($"        let __innerBox = Unmanaged.passRetained(__op{i} as AnyObject).toOpaque()");
                swiftWriter.WriteLine($"        let __innerFuncPtr = unsafeBitCast(innerTrampoline, to: UnsafeMutableRawPointer?.self)");
                cdeclCallArgs.Add("__innerFuncPtr");
                cdeclCallArgs.Add("__innerBox");
            }
            else
            {
                cdeclCallArgs.Add(GetSwiftOuterArgConversion(outerArgs[i], $"__op{i}", env));
            }
        }
        cdeclCallArgs.Add($"{closureCsName}Context"); // outer context

        swiftWriter.WriteLine($"        cdecl({string.Join(", ", cdeclCallArgs)})");

        // Close the trailing closure and method call
        swiftWriter.WriteLine("    })");

        swiftWriter.WriteLine("}");
        swiftWriter.WriteLine("}"); // Close extension
        swiftWriter.WriteLine();
    }

    // ─── C# Callback ───────────────────────────────────────────────────

    private static void EmitCallback(
        CSharpWriter csWriter,
        List<TypeSpec> outerArgs,
        ClosureTypeSpec innerClosureSpec,
        List<TypeSpec> innerArgs,
        int innerClosureIndex,
        string callbackBaseName,
        MethodEnvironment env)
    {
        // Build callback params: outer non-closure args (category-based) + inner funcPtr + innerCtx + outerCtx
        var paramParts = new List<string>();
        for (int i = 0; i < outerArgs.Count; i++)
        {
            if (i == innerClosureIndex)
            {
                paramParts.Add("IntPtr innerFuncPtr");
                paramParts.Add("IntPtr innerContext");
            }
            else
            {
                paramParts.Add($"{GetCallbackParamType(outerArgs[i], env)} arg{i}");
            }
        }
        paramParts.Add("IntPtr outerContext");

        csWriter.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]");
        csWriter.WriteLine($"private static unsafe void {callbackBaseName}({string.Join(", ", paramParts)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        csWriter.WriteLine("var handle = GCHandle.FromIntPtr(outerContext);");

        // Build the outer delegate type: Action<outerCSharpArg0, ..., Action<innerCSharpArg0, ...>>
        var outerDelegateTypeArgs = new List<string>();
        for (int i = 0; i < outerArgs.Count; i++)
        {
            if (i == innerClosureIndex)
            {
                outerDelegateTypeArgs.Add(BuildInnerActionType(innerArgs, env));
            }
            else
            {
                outerDelegateTypeArgs.Add(GetCSharpTypeForOuterArg(outerArgs[i], env));
            }
        }
        var outerDelegateType = outerDelegateTypeArgs.Count > 0
            ? $"Action<{string.Join(", ", outerDelegateTypeArgs)}>"
            : "Action";

        csWriter.WriteLine($"var callback = ({outerDelegateType})handle.Target!;");

        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Marshal outer non-closure args
        var invokeArgs = new List<string>();
        for (int i = 0; i < outerArgs.Count; i++)
        {
            if (i == innerClosureIndex)
            {
                // Build inner Action from funcPtr + context
                var innerActionType = BuildInnerActionType(innerArgs, env);
                var innerDelegateParamTypes = new List<string>();
                var innerDelegateParams = new List<string>();
                for (int j = 0; j < innerArgs.Count; j++)
                {
                    var csType = GetCSharpTypeForInnerArg(innerArgs[j], env);
                    innerDelegateParamTypes.Add(csType);
                    innerDelegateParams.Add($"{csType} __ia{j}");
                }

                // Build the inner delegate* type for the cast
                var innerFuncPtrTypes = new List<string>();
                for (int j = 0; j < innerArgs.Count; j++)
                {
                    innerFuncPtrTypes.Add(GetCallbackParamType(innerArgs[j], env));
                }
                innerFuncPtrTypes.Add("IntPtr"); // closure box
                innerFuncPtrTypes.Add("void");   // return
                var innerDelegatePtrType = $"delegate* unmanaged[Cdecl]<{string.Join(", ", innerFuncPtrTypes)}>";

                csWriter.WriteLine($"{innerActionType} __innerAction = ({string.Join(", ", innerDelegateParams)}) =>");
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // Marshal C# types to cdecl types and call the inner funcPtr
                var innerCallArgs = new List<string>();
                for (int j = 0; j < innerArgs.Count; j++)
                {
                    innerCallArgs.Add(GetInnerArgMarshalToCdecl(innerArgs[j], $"__ia{j}", env));
                }
                innerCallArgs.Add("innerContext"); // closure box context

                csWriter.WriteLine($"(({innerDelegatePtrType})innerFuncPtr)({string.Join(", ", innerCallArgs)});");

                csWriter.Indent--;
                csWriter.WriteLine("};");

                invokeArgs.Add("__innerAction");
            }
            else
            {
                // Marshal outer non-closure args from cdecl types to C# types
                var marshaledName = $"__oa{i}";
                EmitOuterArgMarshal(csWriter, outerArgs[i], marshaledName, $"arg{i}", env);
                invokeArgs.Add(marshaledName);
            }
        }

        csWriter.WriteLine($"callback({string.Join(", ", invokeArgs)});");

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("catch { }");

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    // ─── Function Pointer Field ────────────────────────────────────────

    private static void EmitFunctionPointerField(
        CSharpWriter csWriter,
        List<TypeSpec> outerArgs,
        ClosureTypeSpec innerClosureSpec,
        List<TypeSpec> innerArgs,
        int innerClosureIndex,
        string callbackBaseName,
        MethodEnvironment env)
    {
        // delegate* unmanaged[Cdecl]<outerArgTypes..., innerFuncPtr, innerCtx, outerCtx, void>
        var delegateParts = new List<string>();
        for (int i = 0; i < outerArgs.Count; i++)
        {
            if (i == innerClosureIndex)
            {
                delegateParts.Add("IntPtr"); // innerFuncPtr
                delegateParts.Add("IntPtr"); // innerContext
            }
            else
            {
                delegateParts.Add(GetCallbackParamType(outerArgs[i], env));
            }
        }
        delegateParts.Add("IntPtr"); // outerContext
        delegateParts.Add("void");   // return

        csWriter.WriteLine($"internal static readonly unsafe IntPtr s_{callbackBaseName} = " +
            $"(IntPtr)(delegate* unmanaged[Cdecl]<{string.Join(", ", delegateParts)}>)&{callbackBaseName};");
        csWriter.WriteLine();
    }

    // ─── P/Invoke ──────────────────────────────────────────────────────

    private static void EmitPInvoke(
        CSharpWriter csWriter,
        MethodDecl method,
        string asyncLibName,
        ArgumentDecl closureArg,
        List<(ArgumentDecl arg, string csName, string csType, MethodClosureBridge.ParamAbiCategory category)> passableNonClosureParams,
        string callbackBaseName,
        MethodEnvironment env)
    {
        var pinvokeParams = new List<string>();

        // Non-closure passable method params
        foreach (var (arg, csName, csType, category) in passableNonClosureParams)
        {
            switch (category)
            {
                case MethodClosureBridge.ParamAbiCategory.PayloadHandle:
                case MethodClosureBridge.ParamAbiCategory.ObjCHandle:
                    pinvokeParams.Add($"IntPtr {csName}");
                    break;
                case MethodClosureBridge.ParamAbiCategory.Primitive:
                    if (MarshallingHelpers.IsBoolType(arg.SwiftTypeSpec))
                        pinvokeParams.Add($"[MarshalAs(UnmanagedType.U1)] bool {csName}");
                    else
                        pinvokeParams.Add($"{GetPInvokePrimitiveType(arg.SwiftTypeSpec)} {csName}");
                    break;
            }
        }

        // Outer closure → funcPtr + context
        pinvokeParams.Add("IntPtr funcPtr");
        pinvokeParams.Add("IntPtr context");

        // SwiftSelf last — instance methods only
        bool isInstance = method.MethodType != MethodType.Static;
        if (isInstance)
        {
            pinvokeParams.Add("SwiftSelf self_");
        }

        // Return type
        var returnSpec = method.CSSignature[0].SwiftTypeSpec;
        bool returnsClass = !returnSpec.IsEmptyTuple && returnSpec is NamedTypeSpec rn &&
            !MarshallingHelpers.IsSwiftPrimitive(rn.Name);
        string pinvokeReturnType = returnsClass ? "IntPtr" : "void";

        if (!returnSpec.IsEmptyTuple && !returnsClass)
        {
            pinvokeReturnType = GetPInvokePrimitiveType(returnSpec);
        }

        var silgenName = $"SBW_{callbackBaseName}_{method.Name}";
        var pInvokeName = $"PInvoke_{callbackBaseName}";

        PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
        {
            LibraryPath = asyncLibName,
            EntryPoint = silgenName,
            MethodName = pInvokeName,
            ReturnType = pinvokeReturnType,
            ParametersString = string.Join(", ", pinvokeParams),
            Visibility = PInvokeVisibility.Internal
        });
        csWriter.WriteLine();
    }

    // ─── Public Method ─────────────────────────────────────────────────

    private static void EmitPublicMethod(
        CSharpWriter csWriter,
        MethodDecl method,
        ClosureTypeSpec outerClosureSpec,
        ArgumentDecl closureArg,
        List<TypeSpec> outerArgs,
        ClosureTypeSpec innerClosureSpec,
        List<TypeSpec> innerArgs,
        int innerClosureIndex,
        List<(ArgumentDecl arg, string csName, string csType, MethodClosureBridge.ParamAbiCategory category)> passableNonClosureParams,
        string callbackBaseName,
        string closureParamName,
        MethodEnvironment env,
        TypeDecl? parentDecl,
        string helperClassName)
    {
        var pInvokeName = $"PInvoke_{callbackBaseName}";

        // Build the nested delegate type: Action<outerCSharpArg0, ..., Action<innerCSharpArg0, ...>>
        var outerDelegateTypeArgs = new List<string>();
        for (int i = 0; i < outerArgs.Count; i++)
        {
            if (i == innerClosureIndex)
            {
                outerDelegateTypeArgs.Add(BuildInnerActionType(innerArgs, env));
            }
            else
            {
                outerDelegateTypeArgs.Add(GetCSharpTypeForOuterArg(outerArgs[i], env));
            }
        }
        var delegateType = outerDelegateTypeArgs.Count > 0
            ? $"Action<{string.Join(", ", outerDelegateTypeArgs)}>"
            : "Action";

        // Build return type
        var returnSpec = method.CSSignature[0].SwiftTypeSpec;
        string returnType;
        bool returnsClass;
        if (returnSpec.IsEmptyTuple)
        {
            returnType = "void";
            returnsClass = false;
        }
        else
        {
            returnType = GetCSharpReturnType(returnSpec, env);
            returnsClass = returnSpec is NamedTypeSpec rn && !MarshallingHelpers.IsSwiftPrimitive(rn.Name);
        }

        // Build public parameter list
        var publicParams = new List<string>();
        foreach (var (_, csName, csType, _) in passableNonClosureParams)
        {
            publicParams.Add($"{csType} {csName}");
        }
        publicParams.Add($"{delegateType} {closureParamName}");

        // Build method name
        var methodName = NameProvider.GetPublicMethodName(
            method.Name, method.IsAsync,
            hasReturnValue: !returnSpec.IsEmptyTuple,
            env.SiblingPropertyNames,
            isSelfReturning: MethodEnvironment.IsSelfReturningMethod(method),
            parentTypeName: (method.ParentDecl as TypeDecl)?.Name,
            parameterCount: method.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple));

        var isStatic = method.MethodType == MethodType.Static;
        var staticKeyword = isStatic ? "static " : "";

        XmlDocCommentEmitter.EmitMethodDocComment(csWriter, method);

        csWriter.WriteLine($"public {staticKeyword}unsafe {returnType} {methodName}({string.Join(", ", publicParams)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Allocate GCHandle for the outer delegate
        csWriter.WriteLine($"var __gcHandle = GCHandle.Alloc({closureParamName});");

        // When in a generic type, callback pointer and P/Invoke live in the helper class
        var helperPrefix = string.IsNullOrEmpty(helperClassName) ? "" : $"{helperClassName}.";

        // Build P/Invoke call arguments
        var callArgs = new List<string>();

        // Non-closure passable method params
        foreach (var (arg, csName, _, category) in passableNonClosureParams)
        {
            switch (category)
            {
                case MethodClosureBridge.ParamAbiCategory.ObjCHandle:
                    callArgs.Add($"{csName}.Handle");
                    break;
                case MethodClosureBridge.ParamAbiCategory.PayloadHandle:
                    callArgs.Add($"{csName}.Payload.DangerousGetHandle()");
                    break;
                case MethodClosureBridge.ParamAbiCategory.Primitive:
                    callArgs.Add(csName);
                    break;
            }
        }

        // Closure funcPtr + context
        callArgs.Add($"{helperPrefix}s_{callbackBaseName}");
        callArgs.Add("GCHandle.ToIntPtr(__gcHandle)");

        // SwiftSelf — instance methods only
        if (!isStatic)
        {
            callArgs.Add("new SwiftSelf((void*)Payload.DangerousGetHandle())");
        }

        if (returnsClass)
        {
            csWriter.WriteLine($"var __result = {helperPrefix}{pInvokeName}({string.Join(", ", callArgs)});");
            csWriter.WriteLine("var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("*(IntPtr*)classPayload = __result;");
            csWriter.WriteLine($"return ({returnType})SwiftMarshal.MarshalFromSwift<{returnType}>(new IntPtr(classPayload));");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("catch");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("NativeMemory.Free(classPayload);");
            csWriter.WriteLine("throw;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }
        else if (!returnSpec.IsEmptyTuple)
        {
            csWriter.WriteLine($"return {helperPrefix}{pInvokeName}({string.Join(", ", callArgs)});");
        }
        else
        {
            csWriter.WriteLine($"{helperPrefix}{pInvokeName}({string.Join(", ", callArgs)});");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    // ─── Arg Marshalling ───────────────────────────────────────────────

    /// <summary>
    /// Emits C# code to marshal an outer non-closure arg from its cdecl representation
    /// to the C# user-facing type.
    /// </summary>
    private static void EmitOuterArgMarshal(
        CSharpWriter csWriter,
        TypeSpec argType,
        string marshaledName,
        string rawName,
        MethodEnvironment env)
    {
        if (argType is NamedTypeSpec named)
        {
            if (named.Name == "Swift.Bool")
            {
                csWriter.WriteLine($"var {marshaledName} = {rawName} != 0;");
                return;
            }

            if (MarshallingHelpers.IsSwiftPrimitive(named.Name))
            {
                csWriter.WriteLine($"var {marshaledName} = {rawName};");
                return;
            }

            // Simple enum: cast from underlying integer
            var enumInfo = env.ClosureHandler.GetSimpleEnumInfo(argType);
            if (enumInfo != null)
            {
                var csEnumType = GetCSharpTypeForOuterArg(argType, env);
                csWriter.WriteLine($"var {marshaledName} = ({csEnumType}){rawName};");
                return;
            }

            // ObjC class: marshal from IntPtr
            if (env.ClosureHandler.IsObjCBridgedClass(argType))
            {
                var csType = GetCSharpTypeForOuterArg(argType, env);
                csWriter.WriteLine($"var {marshaledName} = Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{csType}>(new IntPtr((void*){rawName}));");
                return;
            }

            // Class: marshal from IntPtr
            if (env.ClosureHandler.IsClassType(argType))
            {
                var csType = GetCSharpTypeForOuterArg(argType, env);
                csWriter.WriteLine($"var {marshaledName} = Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{csType}>(new IntPtr((void*){rawName}));");
                return;
            }
        }

        // Fallback
        csWriter.WriteLine($"var {marshaledName} = {rawName};");
    }

    /// <summary>
    /// Gets the C# expression to marshal an inner arg from C# type to cdecl type
    /// for calling the inner closure's funcPtr.
    /// </summary>
    private static string GetInnerArgMarshalToCdecl(TypeSpec argType, string varName, MethodEnvironment env)
    {
        if (argType is NamedTypeSpec named)
        {
            if (named.Name == "Swift.Bool")
                return $"(byte)({varName} ? 1 : 0)";

            if (MarshallingHelpers.IsSwiftPrimitive(named.Name))
                return varName;

            // Simple enum: cast to underlying integer type
            var enumInfo = env.ClosureHandler.GetSimpleEnumInfo(argType);
            if (enumInfo != null)
                return $"({enumInfo.Value.csUnderlying}){varName}";

            // ObjC class: .Handle
            if (env.ClosureHandler.IsObjCBridgedClass(argType))
                return $"{varName}.Handle";

            // Class: Payload handle
            if (env.ClosureHandler.IsClassType(argType))
                return $"{varName}.Payload.DangerousGetHandle()";
        }

        return varName;
    }

    // ─── Type Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Checks if a type is Optional&lt;ref&gt; — allowed by IsCdeclCompatibleType but not handled
    /// by our trampoline conversion paths (would emit invalid Unmanaged&lt;Optional&lt;T&gt;&gt;).
    /// </summary>
    private static bool IsOptionalReferenceType(TypeSpec typeSpec, ClosureHandler closureHandler)
    {
        if (typeSpec is NamedTypeSpec named &&
            named.Name == "Swift.Optional" &&
            named.ContainsGenericParameters &&
            named.GenericParameters.Count == 1)
        {
            var inner = named.GenericParameters[0];
            return closureHandler.IsReferenceType(inner);
        }
        return false;
    }

    /// <summary>
    /// Checks if the method return type is supported.
    /// </summary>
    private static bool IsReturnTypeSupported(TypeSpec returnSpec, ITypeDatabase typeDatabase)
    {
        if (returnSpec.IsEmptyTuple) return true;
        if (returnSpec.IsDynamicSelf) return true;
        if (returnSpec is not NamedTypeSpec named) return false;

        if (MarshallingHelpers.IsSwiftPrimitive(named.Name)) return true;

        try
        {
            if (typeDatabase.TryGetTypeRecord(
                SwiftTypeName.FromModuleQualifiedName(named.Name), out var record))
            {
                return record.Kind == TypeRecordKind.Class ||
                       MarshallingHelpers.IsObjCBridged(record);
            }
        }
        catch (ArgumentException) { }

        return false;
    }

    /// <summary>
    /// Gets the C# type for an outer non-closure arg.
    /// </summary>
    private static string GetCSharpTypeForOuterArg(TypeSpec argType, MethodEnvironment env)
    {
        if (argType is NamedTypeSpec named)
        {
            if (named.Name == "Swift.Bool") return "bool";
            if (MarshallingHelpers.IsSwiftPrimitive(named.Name))
                return GetCSharpPrimitiveType(named.Name);

            // Simple enum
            var enumInfo = env.ClosureHandler.GetSimpleEnumInfo(argType);
            if (enumInfo != null)
            {
                if (env.TypeDatabase.TryGetTypeRecord(argType, out var enumRecord))
                    return enumRecord.CSharpTypeName.FullyQualifiedName;
            }

            // Class / ObjC
            if (env.TypeDatabase.TryGetTypeRecord(argType, out var record))
                return record.CSharpTypeName.FullyQualifiedName;
        }

        return "IntPtr";
    }

    /// <summary>
    /// Gets the C# type for an inner closure arg.
    /// </summary>
    private static string GetCSharpTypeForInnerArg(TypeSpec argType, MethodEnvironment env)
    {
        // Same as outer for cdecl-compatible types
        return GetCSharpTypeForOuterArg(argType, env);
    }

    /// <summary>
    /// Builds the Action&lt;...&gt; type string for the inner closure.
    /// </summary>
    private static string BuildInnerActionType(List<TypeSpec> innerArgs, MethodEnvironment env)
    {
        if (innerArgs.Count == 0)
            return "Action";

        var typeArgs = new List<string>();
        foreach (var arg in innerArgs)
        {
            typeArgs.Add(GetCSharpTypeForInnerArg(arg, env));
        }
        return $"Action<{string.Join(", ", typeArgs)}>";
    }

    /// <summary>
    /// Gets the callback parameter type for the cdecl callback.
    /// Must match the Swift @convention(c) types from GetSwiftCdeclParamType.
    /// </summary>
    private static string GetCallbackParamType(TypeSpec argType, MethodEnvironment env)
    {
        if (argType is NamedTypeSpec named)
        {
            if (named.Name == "Swift.Bool") return "byte";
            if (MarshallingHelpers.IsSwiftPrimitive(named.Name))
                return GetCSharpPrimitiveType(named.Name);

            // Simple enum: underlying integer type
            var enumInfo = env.ClosureHandler.GetSimpleEnumInfo(argType);
            if (enumInfo != null)
                return enumInfo.Value.csUnderlying;

            // ObjC, classes: IntPtr
            return "IntPtr";
        }

        return "IntPtr";
    }

    /// <summary>
    /// Gets the Swift cdecl-compatible type for a closure argument.
    /// </summary>
    private static string GetSwiftCdeclParamType(TypeSpec argType, MethodEnvironment env)
    {
        if (argType is NamedTypeSpec named)
        {
            if (named.Name == "Swift.Bool") return "UInt8";

            if (MarshallingHelpers.IsSwiftPrimitive(named.Name))
            {
                return named.Name switch
                {
                    "Swift.Int" => "Int",
                    "Swift.UInt" => "UInt",
                    "Swift.Int8" => "Int8",
                    "Swift.UInt8" => "UInt8",
                    "Swift.Int16" => "Int16",
                    "Swift.UInt16" => "UInt16",
                    "Swift.Int32" => "Int32",
                    "Swift.UInt32" => "UInt32",
                    "Swift.Int64" => "Int64",
                    "Swift.UInt64" => "UInt64",
                    "Swift.Float" => "Float",
                    "Swift.Double" => "Double",
                    _ => "UnsafeMutableRawPointer"
                };
            }

            // Simple enum: underlying Swift integer type
            var enumInfo = env.ClosureHandler.GetSimpleEnumInfo(argType);
            if (enumInfo != null)
                return enumInfo.Value.swiftScalar;

            // Classes, ObjC: pointer ABI
            return "UnsafeMutableRawPointer";
        }

        return "UnsafeMutableRawPointer";
    }

    /// <summary>
    /// Gets the Swift expression to convert a trampoline cdecl arg back to the original type.
    /// Used inside the inner trampoline to reconstruct inner closure args.
    /// </summary>
    private static string GetSwiftTrampolineArgConversion(TypeSpec argType, string paramName, MethodEnvironment env)
    {
        if (argType is NamedTypeSpec named)
        {
            if (named.Name == "Swift.Bool")
                return $"({paramName} != 0)";

            if (MarshallingHelpers.IsSwiftPrimitive(named.Name))
                return paramName;

            // Simple enum: unsafeBitCast from underlying type
            var enumInfo = env.ClosureHandler.GetSimpleEnumInfo(argType);
            if (enumInfo != null)
            {
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(argType);
                return $"unsafeBitCast({paramName}, to: {swiftType}.self)";
            }

            // ObjC / classes: Unmanaged.fromOpaque().takeUnretainedValue()
            var typeStr = ExistentialBypassEmitter.RenderSwiftTypeSpec(argType);
            return $"Unmanaged<{typeStr}>.fromOpaque({paramName}).takeUnretainedValue()";
        }

        return paramName;
    }

    /// <summary>
    /// Gets the Swift expression to convert an outer closure arg to its cdecl representation.
    /// </summary>
    private static string GetSwiftOuterArgConversion(TypeSpec argType, string paramName, MethodEnvironment env)
    {
        if (argType is NamedTypeSpec named)
        {
            if (named.Name == "Swift.Bool")
                return $"({paramName} ? 1 : 0)";

            if (MarshallingHelpers.IsSwiftPrimitive(named.Name))
                return paramName;

            // Simple enum: unsafeBitCast to underlying
            var enumInfo = env.ClosureHandler.GetSimpleEnumInfo(argType);
            if (enumInfo != null)
            {
                return $"unsafeBitCast({paramName}, to: {enumInfo.Value.swiftScalar}.self)";
            }

            // ObjC / classes: Unmanaged.passUnretained().toOpaque()
            return $"Unmanaged.passUnretained({paramName}).toOpaque()";
        }

        return paramName;
    }

    /// <summary>
    /// Gets the C# type and ABI category for a non-closure method parameter.
    /// </summary>
    private static (string csType, MethodClosureBridge.ParamAbiCategory category) GetNonClosureParamCSharpType(
        ArgumentDecl arg, MethodEnvironment env)
    {
        var category = MethodClosureBridge.ClassifyParam(arg, env.TypeDatabase);
        var typeSpec = arg.SwiftTypeSpec;

        switch (category)
        {
            case MethodClosureBridge.ParamAbiCategory.Primitive:
                return (GetCSharpPrimitiveType(((NamedTypeSpec)typeSpec).Name), category);

            case MethodClosureBridge.ParamAbiCategory.ObjCHandle:
            case MethodClosureBridge.ParamAbiCategory.PayloadHandle:
                if (env.TypeDatabase.TryGetTypeRecord(typeSpec, out var record))
                    return (record.CSharpTypeName.FullyQualifiedName, category);
                return ("IntPtr", category);

            default:
                return ("IntPtr", MethodClosureBridge.ParamAbiCategory.Unsupported);
        }
    }

    /// <summary>
    /// Gets the C# return type for the method.
    /// </summary>
    private static string GetCSharpReturnType(TypeSpec returnSpec, MethodEnvironment env)
    {
        if (returnSpec is NamedTypeSpec namedRet)
        {
            if (returnSpec.IsDynamicSelf)
            {
                var parentDecl = env.MethodDecl.ParentDecl as TypeDecl;
                if (parentDecl != null)
                {
                    var parentTypeSpec = new NamedTypeSpec(parentDecl.SwiftTypeName.ModuleQualifiedName);
                    if (env.TypeDatabase.TryGetTypeRecord(parentTypeSpec, out var parentRecord))
                    {
                        var baseName = parentRecord.CSharpTypeName.FullyQualifiedName;
                        if (parentDecl.IsGeneric && parentDecl.GenericParameters.Count > 0)
                        {
                            var genericContext = GenericContext.FromType(parentDecl);
                            var csParams = parentDecl.GenericParameters
                                .Select(gp => genericContext.TryResolve(gp.TypeName, out var csName) ? csName : gp.TypeName)
                                .ToList();
                            return $"{baseName}<{string.Join(", ", csParams)}>";
                        }
                        return baseName;
                    }
                }
                return "IntPtr";
            }

            if (MarshallingHelpers.IsSwiftPrimitive(namedRet.Name))
                return GetCSharpPrimitiveType(namedRet.Name);

            if (env.TypeDatabase.TryGetTypeRecord(returnSpec, out var record))
                return record.CSharpTypeName.FullyQualifiedName;
        }

        return "IntPtr";
    }

    /// <summary>
    /// Maps Swift primitive names to C# type names.
    /// </summary>
    private static string GetCSharpPrimitiveType(string swiftName)
    {
        return swiftName switch
        {
            "Swift.Bool" => "bool",
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
            "CoreFoundation.CGFloat" => "NFloat",
            _ => "nint"
        };
    }

    /// <summary>
    /// Gets the P/Invoke type for a Swift primitive.
    /// </summary>
    private static string GetPInvokePrimitiveType(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec named)
        {
            return named.Name switch
            {
                "Swift.Bool" => "bool",
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
                _ => "nint"
            };
        }
        return "nint";
    }

    /// <summary>
    /// Gets the Swift argument label for a parameter.
    /// </summary>
    private static string GetSwiftArgLabel(ArgumentDecl arg)
    {
        var name = arg.Name;
        if (name.StartsWith("arg"))
            return "";
        if (name.StartsWith("_"))
            return $"{name.Substring(1)}: ";
        return $"{name}: ";
    }
}
