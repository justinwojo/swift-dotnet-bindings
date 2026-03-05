// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits C# code for regular instance/static methods with closure parameters
/// whose closure argument types include bound generics (e.g., AFDataResponse&lt;Data&gt;).
/// Takes over entire method emission from MethodHandler when eligible.
/// <para>
/// Emits: Swift @_silgen_name wrapper, [UnmanagedCallersOnly] callback, static function
/// pointer field, P/Invoke declaration (LibraryImport, CallConvSwift), and public method
/// with typed Action&lt;&gt;/Func&lt;&gt; parameter.
/// </para>
/// <para>
/// Non-closure params with defaults are omitted from both Swift wrapper and C# API —
/// Swift fills them. Non-closure params that are classes or primitives without defaults
/// are passed through.
/// </para>
/// </summary>
public static class MethodClosureBridge
{
    /// <summary>
    /// Groups per-closure data for multi-closure bridge emission.
    /// </summary>
    private record ClosureInfo(
        ClosureTypeSpec Spec,
        ArgumentDecl Arg,
        List<TypeSpec> ClosureArgs,
        bool ReturnIsVoid,
        string CallbackBaseName,
        string ParamName,
        int Index);

    /// <summary>
    /// Checks if a method is eligible for the MethodClosureBridge pattern.
    /// </summary>
    public static bool IsEligible(MethodDecl method, ClosureHandler closureHandler, ITypeDatabase typeDatabase)
    {
        // Not for protocol extensions, async, constructors, or accessors
        if (method.IsProtocolExtensionMethod) return false;
        if (method.IsAsync) return false;
        if (method.IsConstructor) return false;
        if (method.IsAccessor) return false;
        if (method.Throws) return false;

        // Collect ALL closure parameters — require at least one with bound generic or complex enum args
        var closureArgs = new List<(ClosureTypeSpec spec, ArgumentDecl arg)>();
        bool hasBoundGenericInClosure = false;
        bool hasComplexEnumInClosure = false;

        foreach (var arg in method.CSSignature.Skip(1))
        {
            var cts = closureHandler.GetClosureTypeSpec(arg);
            if (cts != null)
            {
                // Check if closure has async — not supported
                if (cts.IsAsync) return false;

                // Check closure args for bound generic types and complex enums
                foreach (var closureArgType in cts.EachArgument())
                {
                    if (IsBoundGenericClosureArg(closureArgType))
                        hasBoundGenericInClosure = true;

                    if (closureHandler.IsComplexEnum(closureArgType))
                        hasComplexEnumInClosure = true;

                    if (!IsClosureArgSupported(closureArgType, typeDatabase))
                        return false;
                }

                // Check closure return type
                if (!cts.ReturnType.IsEmptyTuple)
                {
                    if (!IsClosureReturnSupported(cts.ReturnType, typeDatabase))
                        return false;
                }

                closureArgs.Add((cts, arg));
            }
        }

        if (closureArgs.Count == 0) return false;

        // Key gate: ONLY activate when at least one closure arg is a bound generic type
        // or a complex enum (D1: complex enums need heap allocation in Swift wrapper)
        if (!hasBoundGenericInClosure && !hasComplexEnumInClosure) return false;

        // Check non-closure params: each must be a class (IntPtr), primitive, or have a default value
        var closureArgSet = new HashSet<ArgumentDecl>(closureArgs.Select(c => c.arg));
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (closureArgSet.Contains(arg)) continue;
            if (!IsNonClosureParamPassable(arg, typeDatabase))
                return false;
        }

        // Check return type: void, class, or primitive
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
    /// Attempts to emit a method closure bridge for the given method.
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

        // Collect all closure parameters
        var closures = new List<ClosureInfo>();
        var closureArgSet = new HashSet<ArgumentDecl>();
        int closureIndex = 0;
        var mangledHash = EmitterUtility.DeterministicHash8(method.MangledName);

        foreach (var arg in method.CSSignature.Skip(1))
        {
            var cts = env.ClosureHandler.GetClosureTypeSpec(arg);
            if (cts != null)
            {
                var cArgs = cts.EachArgument().ToList();
                var retIsVoid = cts.ReturnType.IsEmptyTuple;
                var paramName = NameProvider.GetCSharpParameterName(arg);
                // When multiple closures, use indexed naming; single closure preserves backward compat
                var cbName = $"MCB_{mangledHash}";
                if (closureIndex > 0) cbName += $"_{closureIndex}";

                closures.Add(new ClosureInfo(cts, arg, cArgs, retIsVoid, cbName, paramName, closureIndex));
                closureArgSet.Add(arg);
                closureIndex++;
            }
        }

        if (closures.Count == 0)
            return false;

        var asyncLibName = env.TypeDatabase.AsyncLibraryName ?? "SwiftBindings";

        // Determine which non-closure params to pass through (not defaulted)
        var passableNonClosureParams = new List<(ArgumentDecl arg, string csName, string csType, ParamAbiCategory category)>();
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (closureArgSet.Contains(arg)) continue;
            if (arg.HasDefaultArg) continue; // Omit defaulted params — Swift fills them

            var csName = NameProvider.GetCSharpParameterName(arg);
            var (csType, category) = GetNonClosureParamCSharpType(arg, env);
            passableNonClosureParams.Add((arg, csName, csType, category));
        }

        // Emit Swift wrapper
        EmitSwiftWrapper(swiftWriter, method, env, parentDecl, closures, passableNonClosureParams);

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

            foreach (var ci in closures)
            {
                EmitCallback(helperCsWriter, ci.ClosureArgs, ci.ReturnIsVoid, ci.CallbackBaseName, env);
                EmitFunctionPointerField(helperCsWriter, ci.ClosureArgs, ci.ReturnIsVoid, ci.CallbackBaseName, env);
            }
            EmitPInvoke(helperCsWriter, method, asyncLibName, closures, passableNonClosureParams, env);

            helperCsWriter.Flush();
            env.PInvokeHelperContext.RawCodeBlocks.Add(helperWriter.ToString());
        }
        else
        {
            foreach (var ci in closures)
            {
                EmitCallback(csWriter, ci.ClosureArgs, ci.ReturnIsVoid, ci.CallbackBaseName, env);
                EmitFunctionPointerField(csWriter, ci.ClosureArgs, ci.ReturnIsVoid, ci.CallbackBaseName, env);
            }
            EmitPInvoke(csWriter, method, asyncLibName, closures, passableNonClosureParams, env);
        }

        // Public method always in the class body
        EmitPublicMethod(csWriter, method, closures, passableNonClosureParams, env, parentDecl, helperClassName);

        method.WasEmitted = true;
        return true;
    }

    // ─── Swift Wrapper ─────────────────────────────────────────────────

    private static void EmitSwiftWrapper(
        SwiftWriter swiftWriter,
        MethodDecl method,
        MethodEnvironment env,
        TypeDecl? parentDecl,
        List<ClosureInfo> closures,
        List<(ArgumentDecl arg, string csName, string csType, ParamAbiCategory category)> passableNonClosureParams)
    {
        bool isInstance = method.MethodType != MethodType.Static && parentDecl != null;
        var typeName = parentDecl?.SwiftTypeName?.ModuleQualifiedName ?? parentDecl?.Name ?? "";

        // Use the first closure's callback base name for the silgen symbol (backward compat for single closure)
        var silgenName = $"SBW_{closures[0].CallbackBaseName}_{method.Name}";

        // Build Swift wrapper params
        var swiftParams = new List<string>();

        // Non-closure passable params first
        foreach (var (arg, csName, _, category) in passableNonClosureParams)
        {
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
            var paramName = NameProvider.EscapeSwiftKeyword(csName);
            swiftParams.Add($"    _ {paramName}: {swiftType}");
        }

        // Each closure → funcPtr + context pair
        foreach (var ci in closures)
        {
            var closureCsName = NameProvider.StripVerbatimPrefix(ci.ParamName);
            swiftParams.Add($"    _ {closureCsName}FuncPtr: UnsafeMutableRawPointer?");
            swiftParams.Add($"    _ {closureCsName}Context: UnsafeMutableRawPointer?");
        }

        // Build return type
        var returnSpec = method.CSSignature[0].SwiftTypeSpec;
        bool returnsValue = !returnSpec.IsEmptyTuple;
        var swiftReturnType = returnsValue ? $" -> {ExistentialBypassEmitter.RenderSwiftTypeSpec(returnSpec)}" : "";

        // Emit the wrapper — always inside extension block
        swiftWriter.WriteLine($"extension {typeName} {{");

        swiftWriter.WriteLine($"@_silgen_name(\"{silgenName}\")");
        var funcKeyword = isInstance ? "public func" : "public static func";
        swiftWriter.WriteLine($"{funcKeyword} _sb_{method.Name}(");
        swiftWriter.WriteLine(string.Join(",\n", swiftParams));
        swiftWriter.WriteLine($"){swiftReturnType} {{");

        // Reconstruct cdecl functions from pointers — one per closure
        foreach (var ci in closures)
        {
            var closureCsName = NameProvider.StripVerbatimPrefix(ci.ParamName);
            var cdeclParamTypes = new List<string>();
            for (int i = 0; i < ci.ClosureArgs.Count; i++)
            {
                cdeclParamTypes.Add(GetSwiftCdeclParamType(ci.ClosureArgs[i], env));
            }
            cdeclParamTypes.Add("UnsafeMutableRawPointer?"); // context
            var cdeclReturnType = ci.Spec.ReturnType.IsEmptyTuple ? "Void" : "UInt8";
            var cdeclType = $"(@convention(c) ({string.Join(", ", cdeclParamTypes)}) -> {cdeclReturnType}).self";
            var cdeclVarName = closures.Count > 1 ? $"cdecl{ci.Index}" : "cdecl";
            swiftWriter.WriteLine($"    let {cdeclVarName} = unsafeBitCast({closureCsName}FuncPtr!, to: {cdeclType})");
        }

        // Build original method call arguments in parameter order
        var returnPrefix = returnsValue ? "return " : "";
        var callTarget = isInstance ? "self" : "Self";

        // Collect all method call args in parameter order, interleaving non-closure and closure args
        var methodCallArgs = new List<string>();
        var closureArgSet = new HashSet<ArgumentDecl>(closures.Select(c => c.Arg));
        var closureByArg = closures.ToDictionary(c => c.Arg);
        var passableByArg = passableNonClosureParams.ToDictionary(p => p.arg);

        // Track whether any closure needs withUnsafePointer wrapping or heap allocation
        bool anyClosureNeedsComplexPath = false;
        var perClosureAnalysis = new Dictionary<ClosureInfo, (List<string> paramDecls, List<(int index, string swiftType)> pointerWrapArgs, List<(int index, string conversion)> directArgs, List<(int index, string swiftType)> heapAllocArgs)>();

        foreach (var ci in closures)
        {
            var paramDecls = new List<string>();
            var pointerWrapArgs = new List<(int index, string swiftType)>();
            var directArgs = new List<(int index, string conversion)>();
            var heapAllocArgs = new List<(int index, string swiftType)>();

            for (int i = 0; i < ci.ClosureArgs.Count; i++)
            {
                var argType = ci.ClosureArgs[i];
                var paramName = $"__p{ci.Index}_{i}";
                paramDecls.Add(paramName);

                if (argType is NamedTypeSpec named)
                {
                    if (MarshallingHelpers.IsSwiftPrimitive(named.Name))
                    {
                        if (named.Name == "Swift.Bool")
                            directArgs.Add((i, $"({paramName} ? 1 : 0)"));
                        else
                            directArgs.Add((i, paramName));
                    }
                    else if (IsClassTypeForSwift(named, env.TypeDatabase))
                    {
                        directArgs.Add((i, $"Unmanaged.passUnretained({paramName}).toOpaque()"));
                    }
                    else if (env.ClosureHandler.IsComplexEnum(argType))
                    {
                        // D1: Complex enums use heap allocation — C# takes ownership via SwiftSafeHandle
                        heapAllocArgs.Add((i, ExistentialBypassEmitter.RenderSwiftTypeSpec(argType)));
                    }
                    else
                    {
                        pointerWrapArgs.Add((i, ExistentialBypassEmitter.RenderSwiftTypeSpec(argType)));
                    }
                }
                else
                {
                    pointerWrapArgs.Add((i, ExistentialBypassEmitter.RenderSwiftTypeSpec(argType)));
                }
            }

            if (pointerWrapArgs.Count > 0 || heapAllocArgs.Count > 0)
                anyClosureNeedsComplexPath = true;
            perClosureAnalysis[ci] = (paramDecls, pointerWrapArgs, directArgs, heapAllocArgs);
        }

        if (anyClosureNeedsComplexPath)
        {
            // Complex path: at least one closure has value-type args needing withUnsafePointer or heap allocation
            EmitSwiftMultiClosureWithPointerWrapping(swiftWriter, method, closures,
                passableNonClosureParams, perClosureAnalysis, returnPrefix, callTarget);
        }
        else
        {
            // Simple path: all closure args are direct (primitives or classes)
            var allCallArgs = new List<string>();
            foreach (var arg in method.CSSignature.Skip(1))
            {
                if (arg.HasDefaultArg && !closureArgSet.Contains(arg) && !passableByArg.ContainsKey(arg))
                    continue;

                if (closureByArg.TryGetValue(arg, out var ci))
                {
                    var closureCsName = NameProvider.StripVerbatimPrefix(ci.ParamName);
                    var analysis = perClosureAnalysis[ci];
                    var cdeclCallArgs = new List<string>();
                    for (int i = 0; i < ci.ClosureArgs.Count; i++)
                    {
                        var direct = analysis.directArgs.FirstOrDefault(d => d.index == i);
                        cdeclCallArgs.Add(direct.conversion);
                    }
                    cdeclCallArgs.Add($"{closureCsName}Context");

                    var cdeclVarName = closures.Count > 1 ? $"cdecl{ci.Index}" : "cdecl";
                    var cdeclCall = $"{cdeclVarName}({string.Join(", ", cdeclCallArgs)})";
                    if (!ci.Spec.ReturnType.IsEmptyTuple)
                        cdeclCall += " != 0";

                    var closureParamStr = string.Join(", ", analysis.paramDecls);
                    var callLabel = GetSwiftArgLabel(ci.Arg);
                    var closureBody = analysis.paramDecls.Count > 0
                        ? $"{{ {closureParamStr} in {cdeclCall} }}"
                        : $"{{ {cdeclCall} }}";
                    allCallArgs.Add($"{callLabel}{closureBody}");
                }
                else if (passableByArg.TryGetValue(arg, out var passable))
                {
                    var label = GetSwiftArgLabel(passable.arg);
                    var paramName = NameProvider.EscapeSwiftKeyword(passable.csName);
                    allCallArgs.Add($"{label}{paramName}");
                }
            }

            swiftWriter.WriteLine($"    {returnPrefix}{callTarget}.{NameProvider.ParserNameToSwift(method)}({string.Join(", ", allCallArgs)})");
        }

        swiftWriter.WriteLine("}");
        swiftWriter.WriteLine("}"); // Close extension
        swiftWriter.WriteLine();
    }

    /// <summary>
    /// Emits the method call body when at least one closure has value-type args needing withUnsafePointer.
    /// Handles N closures with interleaved non-closure params.
    /// </summary>
    private static void EmitSwiftMultiClosureWithPointerWrapping(
        SwiftWriter swiftWriter,
        MethodDecl method,
        List<ClosureInfo> closures,
        List<(ArgumentDecl arg, string csName, string csType, ParamAbiCategory category)> passableNonClosureParams,
        Dictionary<ClosureInfo, (List<string> paramDecls, List<(int index, string swiftType)> pointerWrapArgs, List<(int index, string conversion)> directArgs, List<(int index, string swiftType)> heapAllocArgs)> perClosureAnalysis,
        string returnPrefix,
        string callTarget)
    {
        // For the pointer wrapping path, we build let-bindings for each closure adapter,
        // then emit the method call with all args.
        var indent = "    ";
        var closureArgSet = new HashSet<ArgumentDecl>(closures.Select(c => c.Arg));
        var closureByArg = closures.ToDictionary(c => c.Arg);
        var passableByArg = passableNonClosureParams.ToDictionary(p => p.arg);

        // For each closure that has pointer-wrap args, we need withUnsafePointer nesting.
        // Strategy: emit each closure adapter as a local closure variable, then call the method.
        // This avoids deeply nested trailing closure syntax which doesn't work with multiple closures.
        foreach (var ci in closures)
        {
            var analysis = perClosureAnalysis[ci];
            var closureCsName = NameProvider.StripVerbatimPrefix(ci.ParamName);
            var cdeclVarName = closures.Count > 1 ? $"cdecl{ci.Index}" : "cdecl";
            var adapterName = $"__adapter{ci.Index}";

            // Build the closure adapter type signature
            var swiftParamTypes = ci.ClosureArgs.Select(a => ExistentialBypassEmitter.RenderSwiftTypeSpec(a)).ToList();
            var swiftRetType = ci.Spec.ReturnType.IsEmptyTuple ? "Void" : "Swift.Bool";
            var closureType = $"({string.Join(", ", swiftParamTypes)}) -> {swiftRetType}";

            var closureParamStr = string.Join(", ", analysis.paramDecls);
            var adapterOpen = analysis.paramDecls.Count > 0
                ? $"{{ {closureParamStr} in"
                : "{";
            swiftWriter.WriteLine($"{indent}let {adapterName}: {closureType} = {adapterOpen}");

            if (analysis.pointerWrapArgs.Count > 0 || analysis.heapAllocArgs.Count > 0)
            {
                var currentIndent = indent + indent;

                // D1: Emit heap allocation for complex enum args (flat, before withUnsafePointer nesting)
                foreach (var (idx, swiftType) in analysis.heapAllocArgs)
                {
                    swiftWriter.WriteLine($"{currentIndent}let __heap{ci.Index}_{idx} = UnsafeMutableRawPointer.allocate(byteCount: MemoryLayout<{swiftType}>.size, alignment: MemoryLayout<{swiftType}>.alignment)");
                    swiftWriter.WriteLine($"{currentIndent}__heap{ci.Index}_{idx}.initializeMemory(as: {swiftType}.self, repeating: __p{ci.Index}_{idx}, count: 1)");
                }

                // withUnsafePointer nesting for bound generic struct args
                for (int w = 0; w < analysis.pointerWrapArgs.Count; w++)
                {
                    var (idx, _) = analysis.pointerWrapArgs[w];
                    swiftWriter.WriteLine($"{currentIndent}withUnsafePointer(to: __p{ci.Index}_{idx}) {{ __ptr{ci.Index}_{idx} in");
                    currentIndent += indent;
                }

                var cdeclCallArgs = new List<string>();
                for (int i = 0; i < ci.ClosureArgs.Count; i++)
                {
                    var heapArg = analysis.heapAllocArgs.FirstOrDefault(h => h.index == i);
                    if (heapArg != default)
                    {
                        cdeclCallArgs.Add($"__heap{ci.Index}_{i}");
                    }
                    else
                    {
                        var ptrArg = analysis.pointerWrapArgs.FirstOrDefault(p => p.index == i);
                        if (ptrArg != default)
                        {
                            cdeclCallArgs.Add($"UnsafeMutableRawPointer(mutating: __ptr{ci.Index}_{i})");
                        }
                        else
                        {
                            var direct = analysis.directArgs.FirstOrDefault(d => d.index == i);
                            cdeclCallArgs.Add(direct.conversion);
                        }
                    }
                }
                cdeclCallArgs.Add($"{closureCsName}Context");

                var cdeclExpr = $"{cdeclVarName}({string.Join(", ", cdeclCallArgs)})";
                if (!ci.Spec.ReturnType.IsEmptyTuple)
                    cdeclExpr += " != 0";
                swiftWriter.WriteLine($"{currentIndent}{cdeclExpr}");

                for (int w = analysis.pointerWrapArgs.Count - 1; w >= 0; w--)
                {
                    currentIndent = currentIndent.Substring(indent.Length);
                    swiftWriter.WriteLine($"{currentIndent}}}");
                }
            }
            else
            {
                // All args direct
                var cdeclCallArgs = new List<string>();
                for (int i = 0; i < ci.ClosureArgs.Count; i++)
                {
                    var direct = analysis.directArgs.FirstOrDefault(d => d.index == i);
                    cdeclCallArgs.Add(direct.conversion);
                }
                cdeclCallArgs.Add($"{closureCsName}Context");

                var cdeclExpr = $"{cdeclVarName}({string.Join(", ", cdeclCallArgs)})";
                if (!ci.Spec.ReturnType.IsEmptyTuple)
                    cdeclExpr += " != 0";
                swiftWriter.WriteLine($"{indent}{indent}{cdeclExpr}");
            }

            swiftWriter.WriteLine($"{indent}}}");
        }

        // Build method call with all args in parameter order
        var allCallArgs = new List<string>();
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg.HasDefaultArg && !closureArgSet.Contains(arg) && !passableByArg.ContainsKey(arg))
                continue;

            if (closureByArg.TryGetValue(arg, out var ci))
            {
                var callLabel = GetSwiftArgLabel(ci.Arg);
                var adapterName = $"__adapter{ci.Index}";
                allCallArgs.Add($"{callLabel}{adapterName}");
            }
            else if (passableByArg.TryGetValue(arg, out var passable))
            {
                var label = GetSwiftArgLabel(passable.arg);
                var paramName = NameProvider.EscapeSwiftKeyword(passable.csName);
                allCallArgs.Add($"{label}{paramName}");
            }
        }

        swiftWriter.WriteLine($"{indent}{returnPrefix}{callTarget}.{NameProvider.ParserNameToSwift(method)}({string.Join(", ", allCallArgs)})");
    }

    // ─── C# Callback ───────────────────────────────────────────────────

    private static void EmitCallback(
        CSharpWriter csWriter,
        List<TypeSpec> closureArgs,
        bool closureReturnIsVoid,
        string callbackBaseName,
        MethodEnvironment env)
    {
        var paramParts = new List<string>();
        for (int i = 0; i < closureArgs.Count; i++)
        {
            paramParts.Add($"{GetCallbackParamType(closureArgs[i], env)} arg{i}");
        }
        paramParts.Add("IntPtr contextPtr");

        string returnType = "void";
        if (!closureReturnIsVoid)
            returnType = "byte"; // Only Bool return supported for now

        csWriter.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]");
        csWriter.WriteLine($"private static unsafe {returnType} {callbackBaseName}({string.Join(", ", paramParts)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        csWriter.WriteLine("var handle = GCHandle.FromIntPtr(contextPtr);");

        // Build inner delegate type from callback params
        var innerTypeArgs = new List<string>();
        for (int i = 0; i < closureArgs.Count; i++)
        {
            innerTypeArgs.Add(GetCallbackParamType(closureArgs[i], env));
        }

        if (!closureReturnIsVoid)
        {
            // Bool return → Func<..., bool>
            innerTypeArgs.Add("bool");
            csWriter.WriteLine($"var callback = (Func<{string.Join(", ", innerTypeArgs)}>)handle.Target!;");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            var callArgs = string.Join(", ", Enumerable.Range(0, closureArgs.Count).Select(i => $"arg{i}"));
            csWriter.WriteLine($"return (byte)(callback({callArgs}) ? 1 : 0);");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("catch { return 0; }");
        }
        else
        {
            // Void return → Action<...>
            if (innerTypeArgs.Count > 0)
            {
                csWriter.WriteLine($"var callback = (Action<{string.Join(", ", innerTypeArgs)}>)handle.Target!;");
            }
            else
            {
                csWriter.WriteLine("var callback = (Action)handle.Target!;");
            }
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            var callArgs = string.Join(", ", Enumerable.Range(0, closureArgs.Count).Select(i => $"arg{i}"));
            csWriter.WriteLine($"callback({callArgs});");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("catch { }");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    // ─── Function Pointer Field ────────────────────────────────────────

    private static void EmitFunctionPointerField(
        CSharpWriter csWriter,
        List<TypeSpec> closureArgs,
        bool closureReturnIsVoid,
        string callbackBaseName,
        MethodEnvironment env)
    {
        var delegateParts = new List<string>();
        for (int i = 0; i < closureArgs.Count; i++)
            delegateParts.Add(GetCallbackParamType(closureArgs[i], env));
        delegateParts.Add("IntPtr"); // context

        string returnPart = closureReturnIsVoid ? "void" : "byte";
        delegateParts.Add(returnPart);

        csWriter.WriteLine($"internal static readonly unsafe IntPtr s_{callbackBaseName} = " +
            $"(IntPtr)(delegate* unmanaged[Cdecl]<{string.Join(", ", delegateParts)}>)&{callbackBaseName};");
        csWriter.WriteLine();
    }

    // ─── P/Invoke ──────────────────────────────────────────────────────

    private static void EmitPInvoke(
        CSharpWriter csWriter,
        MethodDecl method,
        string asyncLibName,
        List<ClosureInfo> closures,
        List<(ArgumentDecl arg, string csName, string csType, ParamAbiCategory category)> passableNonClosureParams,
        MethodEnvironment env)
    {
        var pinvokeParams = new List<string>();

        // Non-closure passable params
        foreach (var (arg, csName, csType, category) in passableNonClosureParams)
        {
            switch (category)
            {
                case ParamAbiCategory.PayloadHandle:
                case ParamAbiCategory.ObjCHandle:
                    pinvokeParams.Add($"IntPtr {csName}");
                    break;
                case ParamAbiCategory.Primitive:
                    if (MarshallingHelpers.IsBoolType(arg.SwiftTypeSpec))
                        pinvokeParams.Add($"[MarshalAs(UnmanagedType.U1)] bool {csName}");
                    else
                        pinvokeParams.Add($"{GetPInvokePrimitiveType(arg.SwiftTypeSpec)} {csName}");
                    break;
            }
        }

        // N × (funcPtr, context) pairs — one per closure
        foreach (var ci in closures)
        {
            var suffix = closures.Count > 1 ? $"_{ci.Index}" : "";
            pinvokeParams.Add($"IntPtr funcPtr{suffix}");
            pinvokeParams.Add($"IntPtr context{suffix}");
        }

        // SwiftSelf last (standard Swift calling convention) — instance methods only
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
            // Primitive return
            pinvokeReturnType = GetPInvokePrimitiveType(returnSpec);
        }

        var silgenName = $"SBW_{closures[0].CallbackBaseName}_{method.Name}";
        var pInvokeName = $"PInvoke_{closures[0].CallbackBaseName}";

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
        List<ClosureInfo> closures,
        List<(ArgumentDecl arg, string csName, string csType, ParamAbiCategory category)> passableNonClosureParams,
        MethodEnvironment env,
        TypeDecl? parentDecl,
        string helperClassName)
    {
        var pInvokeName = $"PInvoke_{closures[0].CallbackBaseName}";

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

        // Build per-closure delegate types
        var closureDelegateTypes = new List<string>();
        var closureArgCSharpTypesAll = new List<List<string>>();
        foreach (var ci in closures)
        {
            var closureArgCSharpTypes = new List<string>();
            foreach (var arg in ci.ClosureArgs)
            {
                closureArgCSharpTypes.Add(GetCSharpTypeForClosureArg(arg, env));
            }
            closureArgCSharpTypesAll.Add(closureArgCSharpTypes);

            string delegateType;
            if (ci.ReturnIsVoid)
            {
                delegateType = closureArgCSharpTypes.Count > 0
                    ? $"Action<{string.Join(", ", closureArgCSharpTypes)}>"
                    : "Action";
            }
            else
            {
                var allTypeArgs = new List<string>(closureArgCSharpTypes) { "bool" };
                delegateType = $"Func<{string.Join(", ", allTypeArgs)}>";
            }
            closureDelegateTypes.Add(delegateType);
        }

        // Build public parameter list — non-closure passable + closure delegates
        var publicParams = new List<string>();
        foreach (var (_, csName, csType, _) in passableNonClosureParams)
        {
            publicParams.Add($"{csType} {csName}");
        }
        for (int c = 0; c < closures.Count; c++)
        {
            publicParams.Add($"{closureDelegateTypes[c]} {closures[c].ParamName}");
        }

        // Build method name using same logic as MethodEnvironment
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

        // Build inner callback delegates — one per closure
        // Each maps cdecl-typed args to user-typed args.
        for (int c = 0; c < closures.Count; c++)
        {
            var ci = closures[c];
            var closureArgCSharpTypes = closureArgCSharpTypesAll[c];
            var innerSuffix = closures.Count > 1 ? $"_{c}" : "";

            var innerTypeArgs = new List<string>();
            var innerParamDecls = new List<string>();
            for (int i = 0; i < ci.ClosureArgs.Count; i++)
            {
                var cbType = GetCallbackParamType(ci.ClosureArgs[i], env);
                innerTypeArgs.Add(cbType);
                innerParamDecls.Add($"{cbType} __p{i}");
            }

            if (!ci.ReturnIsVoid)
            {
                innerTypeArgs.Add("bool");
                csWriter.WriteLine($"Func<{string.Join(", ", innerTypeArgs)}> __inner{innerSuffix} = ({string.Join(", ", innerParamDecls)}) =>");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                for (int i = 0; i < ci.ClosureArgs.Count; i++)
                {
                    EmitArgMarshal(csWriter, ci.ClosureArgs[i], closureArgCSharpTypes[i], i);
                }
                var userArgs = string.Join(", ", Enumerable.Range(0, ci.ClosureArgs.Count).Select(i => $"__a{i}"));
                csWriter.WriteLine($"return {ci.ParamName}({userArgs});");
                csWriter.Indent--;
                csWriter.WriteLine("};");
            }
            else
            {
                if (innerTypeArgs.Count > 0)
                {
                    csWriter.WriteLine($"Action<{string.Join(", ", innerTypeArgs)}> __inner{innerSuffix} = ({string.Join(", ", innerParamDecls)}) =>");
                    csWriter.WriteLine("{");
                    csWriter.Indent++;
                    for (int i = 0; i < ci.ClosureArgs.Count; i++)
                    {
                        EmitArgMarshal(csWriter, ci.ClosureArgs[i], closureArgCSharpTypes[i], i);
                    }
                    var userArgs = string.Join(", ", Enumerable.Range(0, ci.ClosureArgs.Count).Select(i => $"__a{i}"));
                    csWriter.WriteLine($"{ci.ParamName}({userArgs});");
                    csWriter.Indent--;
                    csWriter.WriteLine("};");
                }
                else
                {
                    csWriter.WriteLine($"Action __inner{innerSuffix} = () => {ci.ParamName}();");
                }
            }

            // Allocate GCHandle — intentionally leaked for @escaping closure lifetime
            csWriter.WriteLine($"var __gcHandle{innerSuffix} = GCHandle.Alloc(__inner{innerSuffix});");
        }

        // When in a generic type, callback pointer and P/Invoke live in the helper class
        var helperPrefix = string.IsNullOrEmpty(helperClassName) ? "" : $"{helperClassName}.";

        // Build P/Invoke call arguments
        var callArgs = new List<string>();

        // Non-closure passable params
        foreach (var (arg, csName, _, category) in passableNonClosureParams)
        {
            switch (category)
            {
                case ParamAbiCategory.ObjCHandle:
                    callArgs.Add($"{csName}.Handle");
                    break;
                case ParamAbiCategory.PayloadHandle:
                    callArgs.Add($"{csName}.Payload.DangerousGetHandle()");
                    break;
                case ParamAbiCategory.Primitive:
                    callArgs.Add(csName);
                    break;
            }
        }

        // N × (funcPtr, context) pairs — one per closure
        for (int c = 0; c < closures.Count; c++)
        {
            var ci = closures[c];
            var innerSuffix = closures.Count > 1 ? $"_{c}" : "";
            callArgs.Add($"{helperPrefix}s_{ci.CallbackBaseName}");
            callArgs.Add($"GCHandle.ToIntPtr(__gcHandle{innerSuffix})");
        }

        // SwiftSelf — instance methods only
        if (!isStatic)
        {
            callArgs.Add("new SwiftSelf((void*)Payload.DangerousGetHandle())");
        }

        if (returnsClass)
        {
            csWriter.WriteLine($"var __result = {helperPrefix}{pInvokeName}({string.Join(", ", callArgs)});");

            // Construct C# object from IntPtr via NativeMemory + SwiftMarshal pattern
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
            // Primitive return
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

    private static void EmitArgMarshal(
        CSharpWriter csWriter,
        TypeSpec argType,
        string csharpType,
        int index)
    {
        if (argType is NamedTypeSpec named && named.Name == "Swift.Bool")
        {
            // Bool comes as byte from cdecl callback
            csWriter.WriteLine($"var __a{index} = __p{index} != 0;");
        }
        else if (argType is NamedTypeSpec prim && MarshallingHelpers.IsSwiftPrimitive(prim.Name))
        {
            // Primitives come as their native C# type — direct passthrough
            csWriter.WriteLine($"var __a{index} = __p{index};");
        }
        else
        {
            // Bound generics / classes come as IntPtr — marshal via SwiftMarshal
            csWriter.WriteLine($"var __a{index} = SwiftMarshal.MarshalFromSwift<{csharpType}>(__p{index});");
        }
    }

    // ─── Type Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Checks if a closure arg type is a bound generic (NamedTypeSpec with GenericParameters
    /// that's NOT a stdlib container like Optional/Array/Dictionary/Set).
    /// </summary>
    private static bool IsBoundGenericClosureArg(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec named) return false;
        if (!named.ContainsGenericParameters) return false;

        // Exclude stdlib containers — they go through normal pipeline
        return named.Name switch
        {
            "Swift.Optional" or "Swift.Array" or "Swift.Dictionary" or "Swift.Set" => false,
            _ => true
        };
    }

    /// <summary>
    /// Checks if a closure argument type is supported by this emitter.
    /// Supports: primitives, classes, ObjC-bridged types, and bound generics whose base
    /// type resolves in TypeDatabase.
    /// </summary>
    private static bool IsClosureArgSupported(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is not NamedTypeSpec named) return false;

        // Primitives
        if (MarshallingHelpers.IsSwiftPrimitive(named.Name)) return true;

        // Bound generics — check base type resolves and each generic arg is valid
        if (named.ContainsGenericParameters)
        {
            // Exclude stdlib containers
            if (named.Name is "Swift.Optional" or "Swift.Array" or "Swift.Dictionary" or "Swift.Set")
                return false;

            try
            {
                if (!typeDatabase.TryGetTypeRecord(
                    SwiftTypeName.FromModuleQualifiedName(named.Name), out _))
                    return false;
            }
            catch (ArgumentException) { return false; }

            // Verify each generic arg resolves and is a type that can satisfy
            // ISwiftObject constraints (classes from the same module, not ObjC-only types)
            foreach (var genArg in named.GenericParameters)
            {
                if (genArg is NamedTypeSpec genNamed)
                {
                    if (MarshallingHelpers.IsSwiftPrimitive(genNamed.Name)) continue;
                    try
                    {
                        if (!typeDatabase.TryGetTypeRecord(
                            SwiftTypeName.FromModuleQualifiedName(genNamed.Name), out var genRecord))
                            return false;

                        // ObjC-bridged types (like NSUrlSessionWebSocketMessage) don't implement
                        // ISwiftObject, so they can't be used as generic args in bound generic types
                        // that have ISwiftObject constraints.
                        if (MarshallingHelpers.IsObjCBridged(genRecord))
                            return false;
                    }
                    catch (ArgumentException) { return false; }
                }
            }

            return true;
        }

        // Class / struct / enum types — check TypeDatabase
        try
        {
            if (typeDatabase.TryGetTypeRecord(
                SwiftTypeName.FromModuleQualifiedName(named.Name), out var record))
            {
                // D1: Complex enums supported via heap-allocated pointer ABI.
                // Simple enums are excluded — they're blittable integers but MCB's
                // pointer ABI (IntPtr + MarshalFromSwift<T>) doesn't support C# enum types.
                if (record.Kind == TypeRecordKind.Enum &&
                    (record.Flags & TypeRecordFlags.SimpleEnum) == 0)
                    return true;

                return record.Kind == TypeRecordKind.Class ||
                       MarshallingHelpers.IsObjCBridged(record);
            }
        }
        catch (ArgumentException) { }

        return false;
    }

    /// <summary>
    /// Checks if the closure return type is supported. Only Void and Bool for now.
    /// </summary>
    private static bool IsClosureReturnSupported(TypeSpec returnType, ITypeDatabase typeDatabase)
    {
        if (returnType.IsEmptyTuple) return true;
        if (returnType is NamedTypeSpec named && named.Name == "Swift.Bool") return true;

        return false;
    }

    /// <summary>
    /// Checks if a non-closure parameter can be passed through or omitted (default).
    /// Uses ParamAbiCategory to classify: Primitive, ObjCHandle, and PayloadHandle are passable.
    /// </summary>
    private static bool IsNonClosureParamPassable(ArgumentDecl arg, ITypeDatabase typeDatabase)
    {
        // Params with defaults are omitted — Swift fills them
        if (arg.HasDefaultArg) return true;

        var category = ClassifyParam(arg, typeDatabase);
        return category is ParamAbiCategory.Primitive
            or ParamAbiCategory.ObjCHandle
            or ParamAbiCategory.PayloadHandle;
    }

    /// <summary>
    /// Checks if the method return type is supported (void, DynamicSelf, class, primitive).
    /// </summary>
    private static bool IsReturnTypeSupported(TypeSpec returnSpec, ITypeDatabase typeDatabase)
    {
        if (returnSpec.IsEmptyTuple) return true;
        if (returnSpec.IsDynamicSelf) return true; // Self → parent class type
        if (returnSpec is not NamedTypeSpec named) return false;

        // Primitives
        if (MarshallingHelpers.IsSwiftPrimitive(named.Name)) return true;

        // Classes
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
    /// Gets the C# type and ABI category for a non-closure parameter.
    /// </summary>
    private static (string csType, ParamAbiCategory category) GetNonClosureParamCSharpType(
        ArgumentDecl arg, MethodEnvironment env)
    {
        var category = ClassifyParam(arg, env.TypeDatabase);
        var typeSpec = arg.SwiftTypeSpec;

        switch (category)
        {
            case ParamAbiCategory.Primitive:
                return (GetCSharpPrimitiveType(((NamedTypeSpec)typeSpec).Name), category);

            case ParamAbiCategory.ObjCHandle:
            case ParamAbiCategory.PayloadHandle:
                if (env.TypeDatabase.TryGetTypeRecord(typeSpec, out var record))
                    return (record.CSharpTypeName.FullyQualifiedName, category);
                return ("IntPtr", category);

            default:
                return ("IntPtr", ParamAbiCategory.Unsupported);
        }
    }

    /// <summary>
    /// Gets the C# type name for a closure argument TypeSpec.
    /// </summary>
    private static string GetCSharpTypeForClosureArg(TypeSpec argType, MethodEnvironment env)
    {
        if (argType is NamedTypeSpec namedArg)
        {
            // Primitives
            if (namedArg.Name == "Swift.Bool") return "bool";
            if (MarshallingHelpers.IsSwiftPrimitive(namedArg.Name))
                return GetCSharpPrimitiveType(namedArg.Name);

            // Bound generic (e.g., DataResponse<Data, AFError>) — use BoundGenericsHandler
            if (namedArg.ContainsGenericParameters)
            {
                return env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(
                    argType, GenericContext.Empty);
            }

            // Class or enum type
            if (env.TypeDatabase.TryGetTypeRecord(argType, out var record))
                return record.CSharpTypeName.FullyQualifiedName;
        }

        return "IntPtr"; // Fallback
    }

    /// <summary>
    /// Gets the C# return type for the method.
    /// </summary>
    private static string GetCSharpReturnType(TypeSpec returnSpec, MethodEnvironment env)
    {
        if (returnSpec is NamedTypeSpec namedRet)
        {
            // DynamicSelf → parent class type
            if (returnSpec.IsDynamicSelf)
            {
                var parentDecl = env.MethodDecl.ParentDecl as TypeDecl;
                if (parentDecl != null)
                {
                    var parentTypeSpec = new NamedTypeSpec(parentDecl.SwiftTypeName.ModuleQualifiedName);
                    if (env.TypeDatabase.TryGetTypeRecord(parentTypeSpec, out var parentRecord))
                    {
                        var baseName = parentRecord.CSharpTypeName.FullyQualifiedName;
                        // For generic parent types, append C# generic type parameters
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
    /// Gets the callback parameter type for the cdecl callback.
    /// Primitives use their native C# types; reference/value types use IntPtr.
    /// Must match the Swift @convention(c) types from GetSwiftCdeclParamType.
    /// </summary>
    private static string GetCallbackParamType(TypeSpec argType, MethodEnvironment env)
    {
        if (argType is NamedTypeSpec named)
        {
            if (named.Name == "Swift.Bool") return "byte";
            if (MarshallingHelpers.IsSwiftPrimitive(named.Name))
                return GetCSharpPrimitiveType(named.Name);
        }

        // Bound generics, classes: IntPtr (pointer ABI)
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

            // Class types and value types: UnsafeMutableRawPointer
            return "UnsafeMutableRawPointer";
        }

        return "UnsafeMutableRawPointer";
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
    /// Checks if a NamedTypeSpec is a class type according to TypeDatabase.
    /// </summary>
    private static bool IsClassTypeForSwift(NamedTypeSpec named, ITypeDatabase typeDatabase)
    {
        var lookupName = named.Name;
        try
        {
            if (typeDatabase.TryGetTypeRecord(
                SwiftTypeName.FromModuleQualifiedName(lookupName), out var record))
            {
                return record.Kind == TypeRecordKind.Class ||
                       MarshallingHelpers.IsObjCBridged(record);
            }
        }
        catch (ArgumentException) { }

        return false;
    }

    /// <summary>
    /// Gets the Swift argument label for a parameter.
    /// </summary>
    private static string GetSwiftArgLabel(ArgumentDecl arg)
    {
        var name = arg.Name;
        if (name.StartsWith("arg"))
            return ""; // Unlabeled
        if (name.StartsWith("_"))
            return $"{name.Substring(1)}: "; // Strip leading underscore
        return $"{name}: ";
    }

    // ─── ParamAbiCategory ─────────────────────────────────────────────

    /// <summary>
    /// Classifies a non-closure parameter's ABI category for the MethodClosureBridge.
    /// Determines both eligibility (which types are passable) and emission
    /// (how the param is passed in Swift wrapper, P/Invoke, and public method).
    /// </summary>
    internal enum ParamAbiCategory
    {
        /// <summary>Swift primitives (Int, Bool, Double, etc.) — pass by value.</summary>
        Primitive,
        /// <summary>ObjC-bridged classes (UIView, UIImage) — use .Handle for IntPtr.</summary>
        ObjCHandle,
        /// <summary>Swift-native classes and non-frozen structs — use .Payload.DangerousGetHandle() for IntPtr.</summary>
        PayloadHandle,
        /// <summary>Native-remapped types (Foundation.URL → NSUrl) — NOT passable. Requires FromX/ToX conversion.</summary>
        NativeRemapped,
        /// <summary>Frozen structs (by-value or Buffer) — NOT passable. Would need Buffer/.Value marshalling.</summary>
        FrozenStruct,
        /// <summary>Pointer/buffer types (UnsafePointer, etc.) — NOT passable. Mapped to System.IntPtr, no Payload.</summary>
        PointerType,
        /// <summary>Unknown/unresolvable type — NOT passable.</summary>
        Unsupported,
    }

    /// <summary>
    /// Classifies a non-closure parameter's ABI category based on type database lookup.
    /// </summary>
    internal static ParamAbiCategory ClassifyParam(ArgumentDecl arg, ITypeDatabase typeDatabase)
    {
        var typeSpec = arg.SwiftTypeSpec;
        if (typeSpec is not NamedTypeSpec named)
            return ParamAbiCategory.Unsupported;

        if (MarshallingHelpers.IsSwiftPrimitive(named.Name))
            return ParamAbiCategory.Primitive;

        if (IsSwiftPointerType(named.Name))
            return ParamAbiCategory.PointerType;

        try
        {
            if (typeDatabase.TryGetTypeRecord(
                SwiftTypeName.FromModuleQualifiedName(named.Name), out var record))
            {
                if (MarshallingHelpers.IsObjCBridged(record))
                    return ParamAbiCategory.ObjCHandle;

                // ObjC-rooted classes (Swift classes inheriting NSObject) use .Handle, not .Payload
                if (MarshallingHelpers.IsObjCRooted(record))
                    return ParamAbiCategory.ObjCHandle;

                // Native-remapped types (Foundation.URL → NSUrl, Foundation.Data → NSData)
                // require FromX/ToX conversion that MethodClosureBridge doesn't emit.
                // Must be checked before struct/class classification.
                if (record.NativeTypeName != null)
                    return ParamAbiCategory.NativeRemapped;

                if (record.Kind == TypeRecordKind.Class)
                    return ParamAbiCategory.PayloadHandle;

                if (record.Kind == TypeRecordKind.Struct)
                {
                    // Non-frozen structs → NonFrozenStructProjection → SafeHandle with Payload
                    // (TypeProjectionFactory.cs:283-285)
                    if (!MarshallingHelpers.IsTypeFrozen(record))
                        return ParamAbiCategory.PayloadHandle;

                    // Frozen structs → BlittableProjection or FrozenWithMemoryProjection
                    return ParamAbiCategory.FrozenStruct;
                }
            }
        }
        catch (ArgumentException) { }

        return ParamAbiCategory.Unsupported;
    }

    /// <summary>
    /// Determines whether a Swift type name represents a pointer type.
    /// Pointer types are mapped to System.IntPtr and have no Payload — not passable.
    /// </summary>
    private static bool IsSwiftPointerType(string name) =>
        name is "Swift.UnsafePointer" or "Swift.UnsafeMutablePointer"
            or "Swift.UnsafeRawPointer" or "Swift.UnsafeMutableRawPointer"
            or "Swift.UnsafeBufferPointer" or "Swift.UnsafeMutableBufferPointer"
            or "Swift.UnsafeRawBufferPointer" or "Swift.UnsafeMutableRawBufferPointer"
            or "Swift.OpaquePointer" or "Builtin.RawPointer";
}
