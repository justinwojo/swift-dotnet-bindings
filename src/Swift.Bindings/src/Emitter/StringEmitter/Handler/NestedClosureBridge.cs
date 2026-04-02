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
/// boundary, then reconstruct it as Action/Func in C#.
/// </para>
/// <para>
/// Supports multiple inner closures per outer. Multiple outer closures are gated
/// (requires single-wrapper architecture not yet implemented).
/// </para>
/// </summary>
public static class NestedClosureBridge
{
    /// <summary>
    /// Describes a single inner closure within an outer closure parameter.
    /// </summary>
    private record InnerClosureInfo(ClosureTypeSpec Spec, List<TypeSpec> Args, int OuterArgIndex);

    /// <summary>
    /// Describes a single outer closure parameter and its inner closures.
    /// </summary>
    private record NestedClosureInfo(
        ClosureTypeSpec OuterSpec, ArgumentDecl Arg, List<TypeSpec> OuterArgs,
        List<InnerClosureInfo> InnerClosures, List<TypeSpec> OuterNonClosureArgs,
        string CallbackBaseName, string ParamName, int Index);
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

        // Find all closure parameters with nested closures
        var closureArgs = new List<ArgumentDecl>();
        foreach (var arg in method.CSSignature.Skip(1))
        {
            var cts = closureHandler.GetClosureTypeSpec(arg);
            if (cts != null)
                closureArgs.Add(arg);
        }

        if (closureArgs.Count == 0) return false;

        // Multiple outer closures require a single Swift wrapper with ALL funcPtr/context pairs,
        // but current architecture emits one wrapper per outer closure with mismatched P/Invoke ABI.
        // Re-gate until single-wrapper multi-outer architecture is implemented.
        if (closureArgs.Count > 1) return false;

        // Validate each outer closure
        foreach (var closureArg in closureArgs)
        {
            var outerClosureSpec = closureHandler.GetClosureTypeSpec(closureArg)!;

            // Outer closure must have void return and not be async
            if (!outerClosureSpec.ReturnType.IsEmptyTuple) return false;
            if (outerClosureSpec.IsAsync) return false;

            // Must have at least one inner closure
            bool hasInnerClosure = false;

            foreach (var outerArg in outerClosureSpec.EachArgument())
            {
                if (outerArg is ClosureTypeSpec innerCts)
                {
                    hasInnerClosure = true;

                    // Inner closure: void or primitive return only; not async
                    if (!innerCts.ReturnType.IsEmptyTuple)
                    {
                        if (innerCts.ReturnType is not NamedTypeSpec innerRetNamed)
                            return false;
                        if (!MarshallingHelpers.IsSwiftPrimitive(innerRetNamed.Name))
                            return false;
                    }
                    if (innerCts.IsAsync) return false;

                    // Inner closure args must all be cdecl-compatible
                    foreach (var innerArg in innerCts.EachArgument())
                    {
                        if (!ClosureEmitter.IsCdeclCompatibleType(innerArg, closureHandler))
                            return false;
                    }
                }
                else
                {
                    // Non-closure outer args must be cdecl-compatible
                    if (!ClosureEmitter.IsCdeclCompatibleType(outerArg, closureHandler))
                        return false;
                }
            }

            if (!hasInnerClosure) return false;
        }

        // Non-closure method params: each must be passable or have a default value
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (closureArgs.Contains(arg)) continue;
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

        // Collect all nested closure infos
        var nestedClosures = new List<NestedClosureInfo>();
        var mangledHash = EmitterUtility.DeterministicHash8(method.MangledName);
        int closureIndex = 0;

        foreach (var arg in method.CSSignature.Skip(1))
        {
            var cts = env.ClosureHandler.GetClosureTypeSpec(arg);
            if (cts == null) continue;

            var outerArgs = cts.EachArgument().ToList();
            var outerNonClosureArgs = new List<TypeSpec>();
            var innerClosures = new List<InnerClosureInfo>();
            int outerArgIdx = 0;

            foreach (var outerArg in outerArgs)
            {
                if (outerArg is ClosureTypeSpec innerCts)
                {
                    innerClosures.Add(new InnerClosureInfo(innerCts, innerCts.EachArgument().ToList(), outerArgIdx));
                }
                else
                {
                    outerNonClosureArgs.Add(outerArg);
                }
                outerArgIdx++;
            }

            if (innerClosures.Count == 0) continue;

            var paramName = NameProvider.GetCSharpParameterName(arg);
            var baseName = nestedClosures.Count == 0 ? $"NCB_{mangledHash}" : $"NCB_{mangledHash}_{closureIndex}";

            nestedClosures.Add(new NestedClosureInfo(
                cts, arg, outerArgs, innerClosures, outerNonClosureArgs,
                baseName, paramName, closureIndex));

            closureIndex++;
        }

        if (nestedClosures.Count == 0)
            return false;

        // If multiple outer closures, re-index with suffixes
        if (nestedClosures.Count > 1)
        {
            var reindexed = new List<NestedClosureInfo>();
            for (int i = 0; i < nestedClosures.Count; i++)
            {
                var nc = nestedClosures[i];
                reindexed.Add(nc with { CallbackBaseName = $"NCB_{mangledHash}_{i}", Index = i });
            }
            nestedClosures = reindexed;
        }

        var asyncLibName = env.TypeDatabase.AsyncLibraryName ?? "SwiftBindings";

        // Determine which non-closure method params to pass through
        var closureArgSet = nestedClosures.Select(nc => nc.Arg).ToHashSet();
        var passableNonClosureParams = new List<(ArgumentDecl arg, string csName, string csType, MethodClosureBridge.ParamAbiCategory category)>();
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (closureArgSet.Contains(arg)) continue;
            if (arg.HasDefaultArg) continue;

            var csName = NameProvider.GetCSharpParameterName(arg);
            var (csType, category) = GetNonClosureParamCSharpType(arg, env);
            passableNonClosureParams.Add((arg, csName, csType, category));
        }

        // For single outer closure, use the legacy single-closure code path for backward compatibility
        // For multiple, we emit each independently
        foreach (var nc in nestedClosures)
        {
            // For single inner closure, use direct indices; for multiple, use indexed names
            EmitSwiftWrapper(swiftWriter, method, env, parentDecl, nc.Arg, nc.OuterSpec,
                nc.OuterArgs, nc.InnerClosures, passableNonClosureParams, nc.CallbackBaseName);
        }

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

            foreach (var nc in nestedClosures)
            {
                EmitCallback(helperCsWriter, nc.OuterArgs, nc.InnerClosures, nc.CallbackBaseName, env);
                EmitFunctionPointerField(helperCsWriter, nc.OuterArgs, nc.InnerClosures, nc.CallbackBaseName, env);
            }
            EmitPInvoke(helperCsWriter, method, asyncLibName, nestedClosures, passableNonClosureParams, env);

            helperCsWriter.Flush();
            env.PInvokeHelperContext.RawCodeBlocks.Add(helperWriter.ToString());
        }
        else
        {
            foreach (var nc in nestedClosures)
            {
                EmitCallback(csWriter, nc.OuterArgs, nc.InnerClosures, nc.CallbackBaseName, env);
                EmitFunctionPointerField(csWriter, nc.OuterArgs, nc.InnerClosures, nc.CallbackBaseName, env);
            }
            EmitPInvoke(csWriter, method, asyncLibName, nestedClosures, passableNonClosureParams, env);
        }

        // Public method always in the class body
        EmitPublicMethod(csWriter, method, nestedClosures, passableNonClosureParams,
            env, parentDecl, helperClassName);

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
        List<InnerClosureInfo> innerClosures,
        List<(ArgumentDecl arg, string csName, string csType, MethodClosureBridge.ParamAbiCategory category)> passableNonClosureParams,
        string callbackBaseName)
    {
        bool isInstance = method.MethodType != MethodType.Static && parentDecl != null;
        var typeName = parentDecl?.SwiftTypeName?.ModuleQualifiedName ?? parentDecl?.Name ?? "";
        bool multiInner = innerClosures.Count > 1;

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
        var cdeclParamTypes = new List<string>();
        var innerIndices = innerClosures.Select(ic => ic.OuterArgIndex).ToHashSet();
        for (int i = 0; i < outerArgs.Count; i++)
        {
            if (innerIndices.Contains(i))
            {
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

        // Build inner trampolines — one per inner closure
        for (int j = 0; j < innerClosures.Count; j++)
        {
            var ic = innerClosures[j];
            var innerArgs = ic.Args;
            var innerClosureSpec = ic.Spec;
            var suffix = multiInner ? $"{j}" : "";

            var innerCdeclParamTypes = new List<string>();
            for (int i = 0; i < innerArgs.Count; i++)
            {
                innerCdeclParamTypes.Add(GetSwiftCdeclParamType(innerArgs[i], env));
            }
            innerCdeclParamTypes.Add("UnsafeMutableRawPointer"); // closure box (non-optional)

            bool innerReturnsValue = !innerClosureSpec.ReturnType.IsEmptyTuple;
            var innerReturnCdeclType = innerReturnsValue
                ? GetSwiftCdeclParamType(innerClosureSpec.ReturnType, env)
                : "Void";
            var innerTrampolineType = $"@convention(c) ({string.Join(", ", innerCdeclParamTypes)}) -> {innerReturnCdeclType}";

            // Build the inner closure's Swift type string
            var innerClosureSwiftArgTypes = new List<string>();
            foreach (var innerArg in innerArgs)
            {
                innerClosureSwiftArgTypes.Add(ExistentialBypassEmitter.RenderSwiftTypeSpec(innerArg));
            }
            var innerReturnSwiftType = innerReturnsValue
                ? ExistentialBypassEmitter.RenderSwiftTypeSpec(innerClosureSpec.ReturnType)
                : "Void";
            var innerClosureSwiftType = innerClosureSwiftArgTypes.Count switch
            {
                0 => $"() -> {innerReturnSwiftType}",
                1 => $"({innerClosureSwiftArgTypes[0]}) -> {innerReturnSwiftType}",
                _ => $"({string.Join(", ", innerClosureSwiftArgTypes)}) -> {innerReturnSwiftType}"
            };

            // Store for use in adapter closure below
            innerClosures[j] = ic with { };  // no mutation needed, just for reference

            // Build return type
            var returnSpec = method.CSSignature[0].SwiftTypeSpec;
            bool returnsValue = !returnSpec.IsEmptyTuple;
            var swiftReturnType = returnsValue ? $" -> {ExistentialBypassEmitter.RenderSwiftTypeSpecForReturnType(returnSpec)}" : "";

            if (j == 0)
            {
                // Emit the wrapper header only once
                swiftWriter.WriteLine($"extension {typeName} {{");
                swiftWriter.WriteLine($"@_silgen_name(\"{silgenName}\")");
                var funcKeyword = isInstance ? "public func" : "public static func";
                swiftWriter.WriteLine($"{funcKeyword} _sb_{method.Name}(");
                swiftWriter.WriteLine(string.Join(",\n", swiftParams));
                swiftWriter.WriteLine($"){swiftReturnType} {{");
            }

            // Define the inner trampoline as a local @convention(c) function
            var innerTrampolineParams = new List<string>();
            for (int i = 0; i < innerArgs.Count; i++)
            {
                innerTrampolineParams.Add($"_ __ip{i}: {GetSwiftCdeclParamType(innerArgs[i], env)}");
            }
            innerTrampolineParams.Add($"_ __closureBox{suffix}: UnsafeMutableRawPointer");

            var trampolineName = multiInner ? $"innerTrampoline{j}" : "innerTrampoline";
            swiftWriter.WriteLine($"    let {trampolineName}: {innerTrampolineType} = {{ {string.Join(", ", innerTrampolineParams.Select(p => p.Split(' ')[1].TrimEnd(':')))} in");

            // Inside the trampoline: unbox the inner closure and invoke it.
            // Uses takeUnretainedValue (no retain change) — the passRetained(+1) in the adapter
            // is intentionally NOT balanced here, creating a bounded leak (one AnyObject per invocation).
            // This is safe for multi-call inner closures. A future optimization can use takeRetainedValue
            // for verified single-use completion handlers, or a ref-counted wrapper for the general case.
            swiftWriter.WriteLine($"        let innerClosure = Unmanaged<AnyObject>.fromOpaque(__closureBox{suffix}).takeUnretainedValue() as! {innerClosureSwiftType}");

            // Build invocation args
            var innerInvocationArgs = new List<string>();
            for (int i = 0; i < innerArgs.Count; i++)
            {
                innerInvocationArgs.Add(GetSwiftTrampolineArgConversion(innerArgs[i], $"__ip{i}", env));
            }

            if (innerReturnsValue)
            {
                swiftWriter.WriteLine($"        let __innerResult = innerClosure({string.Join(", ", innerInvocationArgs)})");
                swiftWriter.WriteLine($"        return {GetSwiftOuterArgConversion(innerClosureSpec.ReturnType, "__innerResult", env)}");
            }
            else
            {
                swiftWriter.WriteLine($"        innerClosure({string.Join(", ", innerInvocationArgs)})");
            }

            swiftWriter.WriteLine("    }");
            swiftWriter.WriteLine();
        }

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

        var methodReturnSpec = method.CSSignature[0].SwiftTypeSpec;
        var returnPrefix = !methodReturnSpec.IsEmptyTuple ? "return " : "";
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
            // Check if this arg index is an inner closure
            var innerMatch = innerClosures.FindIndex(ic => ic.OuterArgIndex == i);
            if (innerMatch >= 0)
            {
                var suffix = multiInner ? $"{innerMatch}" : "";
                var trampolineName = multiInner ? $"innerTrampoline{innerMatch}" : "innerTrampoline";
                swiftWriter.WriteLine($"        let __innerBox{suffix} = Unmanaged.passRetained(__op{i} as AnyObject).toOpaque()");
                swiftWriter.WriteLine($"        let __innerFuncPtr{suffix} = unsafeBitCast({trampolineName}, to: UnsafeMutableRawPointer?.self)");
                cdeclCallArgs.Add($"__innerFuncPtr{suffix}");
                cdeclCallArgs.Add($"__innerBox{suffix}");
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
        List<InnerClosureInfo> innerClosures,
        string callbackBaseName,
        MethodEnvironment env)
    {
        bool multiInner = innerClosures.Count > 1;
        var innerIndices = innerClosures.Select(ic => ic.OuterArgIndex).ToHashSet();

        // Build callback params: outer non-closure args + inner funcPtr/context pairs + outerCtx
        var paramParts = new List<string>();
        for (int i = 0; i < outerArgs.Count; i++)
        {
            if (innerIndices.Contains(i))
            {
                var innerIdx = innerClosures.FindIndex(ic => ic.OuterArgIndex == i);
                var suffix = multiInner ? $"{innerIdx}" : "";
                paramParts.Add($"IntPtr innerFuncPtr{suffix}");
                paramParts.Add($"IntPtr innerContext{suffix}");
            }
            else
            {
                paramParts.Add($"{GetCallbackParamType(outerArgs[i], env)} arg{i}");
            }
        }
        paramParts.Add("IntPtr outerContext");

        csWriter.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        csWriter.WriteLine($"private static unsafe void {callbackBaseName}({string.Join(", ", paramParts)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        csWriter.WriteLine("var handle = GCHandle.FromIntPtr(outerContext);");

        // Build the outer delegate type
        var outerDelegateTypeArgs = new List<string>();
        for (int i = 0; i < outerArgs.Count; i++)
        {
            if (innerIndices.Contains(i))
            {
                var ic = innerClosures.First(c => c.OuterArgIndex == i);
                outerDelegateTypeArgs.Add(BuildInnerDelegateType(ic.Args, ic.Spec.ReturnType, env));
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
            if (innerIndices.Contains(i))
            {
                var innerIdx = innerClosures.FindIndex(ic => ic.OuterArgIndex == i);
                var ic = innerClosures[innerIdx];
                var innerArgs = ic.Args;
                var innerClosureSpec = ic.Spec;
                var suffix = multiInner ? $"{innerIdx}" : "";
                bool innerReturnsValue = !innerClosureSpec.ReturnType.IsEmptyTuple;

                // Build inner Action/Func from funcPtr + context
                var innerDelegateType = BuildInnerDelegateType(innerArgs, innerClosureSpec.ReturnType, env);
                var innerDelegateParams = new List<string>();
                for (int j = 0; j < innerArgs.Count; j++)
                {
                    var csType = GetCSharpTypeForInnerArg(innerArgs[j], env);
                    innerDelegateParams.Add($"{csType} __ia{j}");
                }

                // Build the inner delegate* type for the cast
                var innerFuncPtrTypes = new List<string>();
                for (int j = 0; j < innerArgs.Count; j++)
                {
                    innerFuncPtrTypes.Add(GetCallbackParamType(innerArgs[j], env));
                }
                innerFuncPtrTypes.Add("IntPtr"); // closure box
                var innerReturnCdeclType = innerReturnsValue
                    ? GetCallbackParamType(innerClosureSpec.ReturnType, env)
                    : "void";
                innerFuncPtrTypes.Add(innerReturnCdeclType);
                var innerDelegatePtrType = $"delegate* unmanaged[Cdecl]<{string.Join(", ", innerFuncPtrTypes)}>";

                var actionName = multiInner ? $"__innerAction{innerIdx}" : "__innerAction";
                csWriter.WriteLine($"{innerDelegateType} {actionName} = ({string.Join(", ", innerDelegateParams)}) =>");
                csWriter.WriteLine("{");
                csWriter.Indent++;

                var innerCallArgs = new List<string>();
                for (int j = 0; j < innerArgs.Count; j++)
                {
                    innerCallArgs.Add(GetInnerArgMarshalToCdecl(innerArgs[j], $"__ia{j}", env));
                }
                innerCallArgs.Add($"innerContext{suffix}");

                if (innerReturnsValue)
                {
                    var innerReturnCSharpType = GetCSharpTypeForOuterArg(innerClosureSpec.ReturnType, env);
                    csWriter.WriteLine($"var __innerRet = (({innerDelegatePtrType})innerFuncPtr{suffix})({string.Join(", ", innerCallArgs)});");
                    if (innerClosureSpec.ReturnType is NamedTypeSpec innerRetNamed && innerRetNamed.Name == "Swift.Bool")
                        csWriter.WriteLine($"return __innerRet != 0;");
                    else
                        csWriter.WriteLine($"return ({innerReturnCSharpType})__innerRet;");
                }
                else
                {
                    csWriter.WriteLine($"(({innerDelegatePtrType})innerFuncPtr{suffix})({string.Join(", ", innerCallArgs)});");
                }

                csWriter.Indent--;
                csWriter.WriteLine("};");

                invokeArgs.Add(actionName);
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
        List<InnerClosureInfo> innerClosures,
        string callbackBaseName,
        MethodEnvironment env)
    {
        var innerIndices = innerClosures.Select(ic => ic.OuterArgIndex).ToHashSet();

        // delegate* unmanaged[Cdecl]<outerArgTypes..., innerFuncPtr, innerCtx, ..., outerCtx, void>
        var delegateParts = new List<string>();
        for (int i = 0; i < outerArgs.Count; i++)
        {
            if (innerIndices.Contains(i))
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
        List<NestedClosureInfo> nestedClosures,
        List<(ArgumentDecl arg, string csName, string csType, MethodClosureBridge.ParamAbiCategory category)> passableNonClosureParams,
        MethodEnvironment env)
    {
        // Use the first (or only) nested closure's callback base name for the P/Invoke
        var callbackBaseName = nestedClosures[0].CallbackBaseName;

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

        // Each outer closure → funcPtr + context pair
        foreach (var nc in nestedClosures)
        {
            var csName = NameProvider.StripVerbatimPrefix(nc.ParamName);
            pinvokeParams.Add($"IntPtr {csName}FuncPtr");
            pinvokeParams.Add($"IntPtr {csName}Context");
        }

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
        List<NestedClosureInfo> nestedClosures,
        List<(ArgumentDecl arg, string csName, string csType, MethodClosureBridge.ParamAbiCategory category)> passableNonClosureParams,
        MethodEnvironment env,
        TypeDecl? parentDecl,
        string helperClassName)
    {
        var callbackBaseName = nestedClosures[0].CallbackBaseName;
        var pInvokeName = $"PInvoke_{callbackBaseName}";

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

        // Add delegate params for each outer closure
        foreach (var nc in nestedClosures)
        {
            var innerIndices = nc.InnerClosures.Select(ic => ic.OuterArgIndex).ToHashSet();
            var outerDelegateTypeArgs = new List<string>();
            for (int i = 0; i < nc.OuterArgs.Count; i++)
            {
                if (innerIndices.Contains(i))
                {
                    var ic = nc.InnerClosures.First(c => c.OuterArgIndex == i);
                    outerDelegateTypeArgs.Add(BuildInnerDelegateType(ic.Args, ic.Spec.ReturnType, env));
                }
                else
                {
                    outerDelegateTypeArgs.Add(GetCSharpTypeForOuterArg(nc.OuterArgs[i], env));
                }
            }
            var delegateType = outerDelegateTypeArgs.Count > 0
                ? $"Action<{string.Join(", ", outerDelegateTypeArgs)}>"
                : "Action";
            publicParams.Add($"{delegateType} {nc.ParamName}");
        }

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

        // When in a generic type, callback pointer and P/Invoke live in the helper class
        var helperPrefix = string.IsNullOrEmpty(helperClassName) ? "" : $"{helperClassName}.";

        // Allocate GCHandle for each outer closure delegate
        foreach (var nc in nestedClosures)
        {
            csWriter.WriteLine($"var __gcHandle_{nc.Index} = GCHandle.Alloc({nc.ParamName});");
        }

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

        // Each outer closure: funcPtr + context pair
        foreach (var nc in nestedClosures)
        {
            callArgs.Add($"{helperPrefix}s_{nc.CallbackBaseName}");
            callArgs.Add($"GCHandle.ToIntPtr(__gcHandle_{nc.Index})");
        }

        // SwiftSelf — instance methods only
        if (!isStatic)
        {
            bool isObjCRooted = method.ParentDecl is ClassDecl cd && cd.IsObjCRooted;
            var selfExpr = isObjCRooted
                ? "new SwiftSelf((void*)Handle)"
                : "new SwiftSelf((void*)Payload.DangerousGetHandle())";
            callArgs.Add(selfExpr);
        }

        if (returnsClass)
        {
            csWriter.WriteLine($"var __result = {helperPrefix}{pInvokeName}({string.Join(", ", callArgs)});");
            csWriter.WriteLine($"return ({returnType})SwiftMarshal.MarshalFromSwift<{returnType}>(__result);");
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
            // Optional<ref> → nil check: IntPtr.Zero → null, else SwiftMarshal
            if (named.Name == "Swift.Optional" && named.ContainsGenericParameters &&
                named.GenericParameters.Count == 1 && env.ClosureHandler.IsReferenceType(named.GenericParameters[0]))
            {
                var innerCsType = GetCSharpTypeForOuterArg(named.GenericParameters[0], env);
                csWriter.WriteLine($"var {marshaledName} = {rawName} == IntPtr.Zero ? null : Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{innerCsType}>(new IntPtr((void*){rawName}));");
                return;
            }

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
            // Optional<ref> → nil check: null → IntPtr.Zero, else handle
            if (named.Name == "Swift.Optional" && named.ContainsGenericParameters &&
                named.GenericParameters.Count == 1 && env.ClosureHandler.IsReferenceType(named.GenericParameters[0]))
            {
                var inner = named.GenericParameters[0];
                if (env.ClosureHandler.IsObjCBridgedClass(inner))
                    return $"{varName} == null ? IntPtr.Zero : {varName}.Handle";
                return $"{varName} == null ? IntPtr.Zero : {varName}.Payload.DangerousGetHandle()";
            }

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
            // Optional<ref> → nullable reference type
            if (named.Name == "Swift.Optional" && named.ContainsGenericParameters &&
                named.GenericParameters.Count == 1 && env.ClosureHandler.IsReferenceType(named.GenericParameters[0]))
            {
                var innerCsType = GetCSharpTypeForOuterArg(named.GenericParameters[0], env);
                return $"{innerCsType}?";
            }

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
    private static string BuildInnerDelegateType(List<TypeSpec> innerArgs, TypeSpec innerReturnType, MethodEnvironment env)
    {
        bool innerReturnsValue = !innerReturnType.IsEmptyTuple;
        var typeArgs = new List<string>();
        foreach (var arg in innerArgs)
        {
            typeArgs.Add(GetCSharpTypeForInnerArg(arg, env));
        }

        if (innerReturnsValue)
        {
            var returnCsType = GetCSharpTypeForOuterArg(innerReturnType, env);
            typeArgs.Add(returnCsType);
            return $"Func<{string.Join(", ", typeArgs)}>";
        }

        if (typeArgs.Count == 0)
            return "Action";

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
            // Optional<ref> → IntPtr (IntPtr.Zero = null)
            if (named.Name == "Swift.Optional" && named.ContainsGenericParameters &&
                named.GenericParameters.Count == 1 && env.ClosureHandler.IsReferenceType(named.GenericParameters[0]))
                return "IntPtr";

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
    /// Delegates to the canonical implementation in SwiftBuilder.
    /// </summary>
    private static string GetSwiftCdeclParamType(TypeSpec argType, MethodEnvironment env)
        => SwiftBuilder.GetSwiftCdeclParamType(argType, env.ClosureHandler);

    /// <summary>
    /// Gets the Swift expression to convert a trampoline cdecl arg back to the original type.
    /// Used inside the inner trampoline to reconstruct inner closure args.
    /// </summary>
    private static string GetSwiftTrampolineArgConversion(TypeSpec argType, string paramName, MethodEnvironment env)
    {
        if (argType is NamedTypeSpec named)
        {
            // Optional<ref> → nil check + Unmanaged.fromOpaque
            if (named.Name == "Swift.Optional" && named.ContainsGenericParameters &&
                named.GenericParameters.Count == 1 && env.ClosureHandler.IsReferenceType(named.GenericParameters[0]))
            {
                var innerSpec = named.GenericParameters[0];
                var innerTypeStr = ExistentialBypassEmitter.RenderSwiftTypeSpec(innerSpec);
                // ObjC-bridged structs (e.g., NSZone, IndexPath) need AnyObject bridge for Unmanaged
                if (innerSpec is NamedTypeSpec innerNamed &&
                    !env.ClosureHandler.IsClassType(innerNamed) &&
                    env.ClosureHandler.IsObjCBridgedClass(innerNamed))
                {
                    return $"{paramName} != nil ? (Unmanaged<AnyObject>.fromOpaque({paramName}!).takeUnretainedValue() as! {innerTypeStr}) : nil";
                }
                return $"{paramName} != nil ? Unmanaged<{innerTypeStr}>.fromOpaque({paramName}!).takeUnretainedValue() : nil";
            }

            if (named.Name == "Swift.Bool")
                return $"({paramName} != 0)";

            if (MarshallingHelpers.IsSwiftPrimitive(named.Name))
                return paramName;

            // Simple enum: construct from underlying integer.
            var enumInfo = env.ClosureHandler.GetSimpleEnumInfo(argType);
            if (enumInfo != null)
            {
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(argType);
                if (enumInfo.Value.hasRawValue)
                {
                    // Wrap in explicit raw type cast: the callback scalar (e.g., Int64)
                    // may differ from the enum's actual rawValue type (e.g., Int).
                    var rawCast = enumInfo.Value.swiftRawType != null &&
                                  enumInfo.Value.swiftRawType != enumInfo.Value.swiftScalar
                        ? $"{enumInfo.Value.swiftRawType}({paramName})"
                        : paramName;
                    return $"{swiftType}(rawValue: {rawCast})!";
                }
                return $"{{ var __raw = {paramName}; return withUnsafeMutablePointer(to: &__raw) {{ UnsafeMutableRawPointer($0).load(as: {swiftType}.self) }} }}()";
            }

            // ObjC / classes: Unmanaged.fromOpaque().takeUnretainedValue()
            // ObjC-bridged structs need AnyObject bridge for Unmanaged (T: AnyObject constraint)
            var typeStr = ExistentialBypassEmitter.RenderSwiftTypeSpec(argType);
            if (!env.ClosureHandler.IsClassType(named) &&
                env.ClosureHandler.IsObjCBridgedClass(named))
            {
                return $"(Unmanaged<AnyObject>.fromOpaque({paramName}).takeUnretainedValue() as! {typeStr})";
            }
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
            // Optional<ref> → nil check + passUnretained
            if (named.Name == "Swift.Optional" && named.ContainsGenericParameters &&
                named.GenericParameters.Count == 1 && env.ClosureHandler.IsReferenceType(named.GenericParameters[0]))
                return $"{paramName} != nil ? Unmanaged.passUnretained({paramName}!).toOpaque() : nil";

            if (named.Name == "Swift.Bool")
                return $"({paramName} ? 1 : 0)";

            if (MarshallingHelpers.IsSwiftPrimitive(named.Name))
                return paramName;

            // Simple enum: convert to underlying integer.
            var enumInfo = env.ClosureHandler.GetSimpleEnumInfo(argType);
            if (enumInfo != null)
            {
                if (enumInfo.Value.hasRawValue)
                    // Wrap in explicit swiftScalar cast: .rawValue may return a
                    // different Swift type (e.g., Int) than the callback's scalar
                    // type (e.g., Int64). Swift treats Int and Int64 as distinct types.
                    return $"{enumInfo.Value.swiftScalar}({paramName}.rawValue)";
                var enumSwiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(argType);
                return $"{{ var __s: {enumInfo.Value.swiftScalar} = 0; var __e = {paramName}; withUnsafeMutablePointer(to: &__s) {{ dst in withUnsafePointer(to: &__e) {{ src in UnsafeMutableRawPointer(dst).copyMemory(from: UnsafeRawPointer(src), byteCount: MemoryLayout<{enumSwiftType}>.size) }} }}; return __s }}()";
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
        if (SwiftBuilder.IsAutoGeneratedArgName(name))
            return "";
        if (name.StartsWith("_"))
            return $"{name.Substring(1)}: ";
        return $"{name}: ";
    }
}
