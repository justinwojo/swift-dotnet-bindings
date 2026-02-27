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

        // Find exactly one closure parameter with bound generic args
        ClosureTypeSpec? closureSpec = null;
        ArgumentDecl? closureArg = null;
        bool hasBoundGenericInClosure = false;
        int closureCount = 0;

        foreach (var arg in method.CSSignature.Skip(1))
        {
            var cts = closureHandler.GetClosureTypeSpec(arg);
            if (cts != null)
            {
                closureCount++;
                if (closureCount > 1) return false; // Only single closure supported
                closureSpec = cts;
                closureArg = arg;

                // Check if closure has async — not supported
                if (cts.IsAsync) return false;

                // Check closure args for bound generic types
                foreach (var closureArgType in cts.EachArgument())
                {
                    if (IsBoundGenericClosureArg(closureArgType))
                        hasBoundGenericInClosure = true;

                    if (!IsClosureArgSupported(closureArgType, typeDatabase))
                        return false;
                }

                // Check closure return type
                if (!cts.ReturnType.IsEmptyTuple)
                {
                    if (!IsClosureReturnSupported(cts.ReturnType, typeDatabase))
                        return false;
                }
            }
        }

        if (closureSpec == null || closureArg == null) return false;

        // Key gate: ONLY activate when at least one closure arg is a bound generic type
        if (!hasBoundGenericInClosure) return false;

        // Check non-closure params: each must be a class (IntPtr), primitive, or have a default value
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg == closureArg) continue;
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

        // Find the closure parameter
        ClosureTypeSpec? closureTypeSpec = null;
        ArgumentDecl? closureArg = null;
        foreach (var arg in method.CSSignature.Skip(1))
        {
            var cts = env.ClosureHandler.GetClosureTypeSpec(arg);
            if (cts != null)
            {
                closureTypeSpec = cts;
                closureArg = arg;
                break;
            }
        }

        if (closureTypeSpec == null || closureArg == null)
            return false;

        var closureArgs = closureTypeSpec.EachArgument().ToList();
        var closureReturnIsVoid = closureTypeSpec.ReturnType.IsEmptyTuple;

        var asyncLibName = env.TypeDatabase.AsyncLibraryName ?? "SwiftBindings";
        var mangledHash = EmitterUtility.DeterministicHash8(method.MangledName);
        var closureParamName = NameProvider.GetCSharpParameterName(closureArg);
        var callbackBaseName = $"MCB_{mangledHash}";

        // Determine which non-closure params to pass through (not defaulted)
        var passableNonClosureParams = new List<(ArgumentDecl arg, string csName, string csType, bool isClass, bool isObjCBridged)>();
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg == closureArg) continue;
            if (arg.HasDefaultArg) continue; // Omit defaulted params — Swift fills them

            var csName = NameProvider.GetCSharpParameterName(arg);
            var (csType, isClass, isObjCBridged) = GetNonClosureParamCSharpType(arg, env);
            passableNonClosureParams.Add((arg, csName, csType, isClass, isObjCBridged));
        }

        // Emit Swift wrapper
        EmitSwiftWrapper(swiftWriter, method, env, parentDecl, closureArg, closureTypeSpec,
            closureArgs, passableNonClosureParams, callbackBaseName);

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

            EmitCallback(helperCsWriter, closureArgs, closureReturnIsVoid, callbackBaseName, env);
            EmitFunctionPointerField(helperCsWriter, closureArgs, closureReturnIsVoid, callbackBaseName, env);
            EmitPInvoke(helperCsWriter, method, asyncLibName, closureArg, passableNonClosureParams,
                callbackBaseName, env);

            helperCsWriter.Flush();
            env.PInvokeHelperContext.RawCodeBlocks.Add(helperWriter.ToString());
        }
        else
        {
            EmitCallback(csWriter, closureArgs, closureReturnIsVoid, callbackBaseName, env);
            EmitFunctionPointerField(csWriter, closureArgs, closureReturnIsVoid, callbackBaseName, env);
            EmitPInvoke(csWriter, method, asyncLibName, closureArg, passableNonClosureParams,
                callbackBaseName, env);
        }

        // Public method always in the class body
        EmitPublicMethod(csWriter, method, closureTypeSpec, closureArg, closureArgs,
            closureReturnIsVoid, passableNonClosureParams, callbackBaseName, closureParamName,
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
        ClosureTypeSpec closureTypeSpec,
        List<TypeSpec> closureArgs,
        List<(ArgumentDecl arg, string csName, string csType, bool isClass, bool isObjCBridged)> passableNonClosureParams,
        string callbackBaseName)
    {
        bool isInstance = method.MethodType != MethodType.Static && parentDecl != null;
        var typeName = parentDecl?.SwiftTypeName?.ModuleQualifiedName ?? parentDecl?.Name ?? "";

        // Build @_silgen_name symbol
        var silgenName = $"SBW_{callbackBaseName}_{method.Name}";

        // Build Swift wrapper params
        var swiftParams = new List<string>();

        // Non-closure passable params first
        foreach (var (arg, csName, _, isClass, _) in passableNonClosureParams)
        {
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
            var paramName = NameProvider.EscapeSwiftKeyword(csName);
            swiftParams.Add($"    _ {paramName}: {swiftType}");
        }

        // Closure → funcPtr + context pair
        var closureCsName = NameProvider.StripVerbatimPrefix(NameProvider.GetCSharpParameterName(closureArg));
        swiftParams.Add($"    _ {closureCsName}FuncPtr: UnsafeMutableRawPointer?");
        swiftParams.Add($"    _ {closureCsName}Context: UnsafeMutableRawPointer?");

        // Build @convention(c) callback type for the cdecl funcPtr
        var cdeclParamTypes = new List<string>();
        for (int i = 0; i < closureArgs.Count; i++)
        {
            cdeclParamTypes.Add(GetSwiftCdeclParamType(closureArgs[i], env));
        }
        cdeclParamTypes.Add("UnsafeMutableRawPointer?"); // context
        var cdeclReturnType = closureTypeSpec.ReturnType.IsEmptyTuple ? "Void" : "UInt8"; // only Void/Bool supported
        var cdeclType = $"(@convention(c) ({string.Join(", ", cdeclParamTypes)}) -> {cdeclReturnType}).self";

        // Build closure adapter body — how each closure arg gets passed to cdecl
        var closureParamDecls = new List<string>();
        var pointerWrapArgs = new List<(int index, string swiftType)>(); // args needing withUnsafePointer
        var directArgs = new List<(int index, string conversion)>(); // args passed directly or via conversion

        for (int i = 0; i < closureArgs.Count; i++)
        {
            var argType = closureArgs[i];
            var paramName = $"__p{i}";
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(argType);
            closureParamDecls.Add($"{paramName}");

            if (argType is NamedTypeSpec named)
            {
                if (MarshallingHelpers.IsSwiftPrimitive(named.Name))
                {
                    // Primitives pass by value; Bool → UInt8 for cdecl boundary
                    if (named.Name == "Swift.Bool")
                        directArgs.Add((i, $"({paramName} ? 1 : 0)"));
                    else
                        directArgs.Add((i, paramName));
                }
                else if (IsClassTypeForSwift(named, env.TypeDatabase))
                {
                    // Class types: Unmanaged.passUnretained().toOpaque()
                    directArgs.Add((i, $"Unmanaged.passUnretained({paramName}).toOpaque()"));
                }
                else
                {
                    // Value types (bound generics, structs): withUnsafePointer
                    pointerWrapArgs.Add((i, swiftType));
                }
            }
            else
            {
                // Fallback — treat as value type
                pointerWrapArgs.Add((i, ExistentialBypassEmitter.RenderSwiftTypeSpec(argType)));
            }
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

        // Reconstruct cdecl function from pointer
        swiftWriter.WriteLine($"    let cdecl = unsafeBitCast({closureCsName}FuncPtr!, to: {cdeclType})");

        // Build call to original method
        var callLabel = GetSwiftArgLabel(closureArg);
        var nonClosureCallArgs = new List<string>();
        foreach (var (arg, csName, _, _, _) in passableNonClosureParams)
        {
            var label = GetSwiftArgLabel(arg);
            var paramName = NameProvider.EscapeSwiftKeyword(csName);
            nonClosureCallArgs.Add($"{label}{paramName}");
        }

        // For defaulted params, we simply don't pass them — Swift fills defaults.
        // Build the closure adapter
        var closureParamStr = string.Join(", ", closureParamDecls);
        var returnPrefix = returnsValue ? "return " : "";
        var callTarget = isInstance ? "self" : "Self";

        if (pointerWrapArgs.Count > 0)
        {
            // Need withUnsafePointer wrapping for value-type args
            EmitSwiftClosureWithPointerWrapping(swiftWriter, method, closureParamStr,
                callLabel, nonClosureCallArgs, pointerWrapArgs, directArgs, closureArgs,
                closureCsName, returnPrefix, callTarget, closureTypeSpec.ReturnType.IsEmptyTuple);
        }
        else
        {
            // All args are direct (primitives or classes)
            var cdeclCallArgs = new List<string>();
            for (int i = 0; i < closureArgs.Count; i++)
            {
                var direct = directArgs.FirstOrDefault(d => d.index == i);
                cdeclCallArgs.Add(direct.conversion);
            }
            cdeclCallArgs.Add($"{closureCsName}Context");

            var cdeclCall = $"cdecl({string.Join(", ", cdeclCallArgs)})";
            // Bool-returning closures: cdecl returns UInt8, original expects Bool
            if (!closureTypeSpec.ReturnType.IsEmptyTuple)
                cdeclCall += " != 0";

            var methodCallArgs = new List<string>(nonClosureCallArgs);
            methodCallArgs.Add($"{callLabel}{{ {closureParamStr} in {cdeclCall} }}");

            swiftWriter.WriteLine($"    {returnPrefix}{callTarget}.{NameProvider.ParserNameToSwift(method)}({string.Join(", ", methodCallArgs)})");
        }

        swiftWriter.WriteLine("}");
        swiftWriter.WriteLine("}"); // Close extension
        swiftWriter.WriteLine();
    }

    private static void EmitSwiftClosureWithPointerWrapping(
        SwiftWriter swiftWriter,
        MethodDecl method,
        string closureParamStr,
        string callLabel,
        List<string> nonClosureCallArgs,
        List<(int index, string swiftType)> pointerWrapArgs,
        List<(int index, string conversion)> directArgs,
        List<TypeSpec> closureArgs,
        string closureCsName,
        string returnPrefix,
        string callTarget,
        bool closureReturnIsVoid)
    {
        var indent = "    ";

        // Build the method call prefix (non-closure args before the closure)
        var prefixArgs = new List<string>(nonClosureCallArgs);
        var prefixStr = prefixArgs.Count > 0
            ? string.Join(", ", prefixArgs) + ", "
            : "";

        // Open the method call with trailing closure syntax
        swiftWriter.WriteLine($"{indent}{returnPrefix}{callTarget}.{NameProvider.ParserNameToSwift(method)}({prefixStr}{callLabel}{{ {closureParamStr} in");

        // Nest withUnsafePointer calls
        var currentIndent = indent + indent;
        for (int w = 0; w < pointerWrapArgs.Count; w++)
        {
            var (idx, _) = pointerWrapArgs[w];
            swiftWriter.WriteLine($"{currentIndent}withUnsafePointer(to: __p{idx}) {{ __ptr{idx} in");
            currentIndent += indent;
        }

        // Build cdecl call with all resolved args
        var cdeclCallArgs = new List<string>();
        for (int i = 0; i < closureArgs.Count; i++)
        {
            var ptrArg = pointerWrapArgs.FirstOrDefault(p => p.index == i);
            if (ptrArg != default)
            {
                cdeclCallArgs.Add($"UnsafeMutableRawPointer(mutating: __ptr{i})");
            }
            else
            {
                var direct = directArgs.FirstOrDefault(d => d.index == i);
                cdeclCallArgs.Add(direct.conversion);
            }
        }
        cdeclCallArgs.Add($"{closureCsName}Context");

        var cdeclExpr = $"cdecl({string.Join(", ", cdeclCallArgs)})";
        // Bool-returning closures: cdecl returns UInt8, original expects Bool
        if (!closureReturnIsVoid)
            cdeclExpr += " != 0";
        swiftWriter.WriteLine($"{currentIndent}{cdeclExpr}");

        // Close withUnsafePointer braces
        for (int w = pointerWrapArgs.Count - 1; w >= 0; w--)
        {
            currentIndent = currentIndent.Substring(indent.Length);
            swiftWriter.WriteLine($"{currentIndent}}}");
        }

        // Close the closure and method call
        swiftWriter.WriteLine($"{indent}}})");
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
        ArgumentDecl closureArg,
        List<(ArgumentDecl arg, string csName, string csType, bool isClass, bool isObjCBridged)> passableNonClosureParams,
        string callbackBaseName,
        MethodEnvironment env)
    {
        var pinvokeParams = new List<string>();

        // Non-closure passable params
        foreach (var (arg, csName, csType, isClass, _) in passableNonClosureParams)
        {
            if (isClass)
                pinvokeParams.Add($"IntPtr {csName}");
            else
            {
                // Primitive — use the P/Invoke type
                var pinvokeType = GetPInvokePrimitiveType(arg.SwiftTypeSpec);
                if (MarshallingHelpers.IsBoolType(arg.SwiftTypeSpec))
                    pinvokeParams.Add($"[MarshalAs(UnmanagedType.U1)] bool {csName}");
                else
                    pinvokeParams.Add($"{pinvokeType} {csName}");
            }
        }

        // Closure → funcPtr + context
        pinvokeParams.Add("IntPtr funcPtr");
        pinvokeParams.Add("IntPtr context");

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
        ClosureTypeSpec closureTypeSpec,
        ArgumentDecl closureArg,
        List<TypeSpec> closureArgs,
        bool closureReturnIsVoid,
        List<(ArgumentDecl arg, string csName, string csType, bool isClass, bool isObjCBridged)> passableNonClosureParams,
        string callbackBaseName,
        string closureParamName,
        MethodEnvironment env,
        TypeDecl? parentDecl,
        string helperClassName)
    {
        var pInvokeName = $"PInvoke_{callbackBaseName}";

        // Build C# types for closure arguments
        var closureArgCSharpTypes = new List<string>();
        foreach (var arg in closureArgs)
        {
            closureArgCSharpTypes.Add(GetCSharpTypeForClosureArg(arg, env));
        }

        // Build delegate type
        string delegateType;
        if (closureReturnIsVoid)
        {
            delegateType = closureArgCSharpTypes.Count > 0
                ? $"Action<{string.Join(", ", closureArgCSharpTypes)}>"
                : "Action";
        }
        else
        {
            // Only Bool return supported for now
            var allTypeArgs = new List<string>(closureArgCSharpTypes) { "bool" };
            delegateType = $"Func<{string.Join(", ", allTypeArgs)}>";
        }

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

        // Build public parameter list — non-closure passable + closure delegate
        var publicParams = new List<string>();
        foreach (var (_, csName, csType, _, _) in passableNonClosureParams)
        {
            publicParams.Add($"{csType} {csName}");
        }
        publicParams.Add($"{delegateType} {closureParamName}");

        // Build method name using same logic as MethodEnvironment
        var methodName = NameProvider.GetPublicMethodName(
            method.Name, method.IsAsync,
            hasReturnValue: !returnSpec.IsEmptyTuple,
            env.SiblingPropertyNames,
            isSelfReturning: MethodEnvironment.IsSelfReturningMethod(method),
            parentTypeName: (method.ParentDecl as TypeDecl)?.Name,
            parameterCount: method.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a)));

        var isStatic = method.MethodType == MethodType.Static;
        var staticKeyword = isStatic ? "static " : "";

        XmlDocCommentEmitter.EmitMethodDocComment(csWriter, method);

        csWriter.WriteLine($"public {staticKeyword}unsafe {returnType} {methodName}({string.Join(", ", publicParams)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Build inner callback delegate that maps cdecl-typed args to user-typed args.
        // Primitives stay as their native types; bound generics/classes come as IntPtr.
        var innerTypeArgs = new List<string>();
        var innerParamDecls = new List<string>();
        for (int i = 0; i < closureArgs.Count; i++)
        {
            var cbType = GetCallbackParamType(closureArgs[i], env);
            innerTypeArgs.Add(cbType);
            innerParamDecls.Add($"{cbType} __p{i}");
        }

        if (!closureReturnIsVoid)
        {
            // Bool return
            innerTypeArgs.Add("bool");
            csWriter.WriteLine($"Func<{string.Join(", ", innerTypeArgs)}> __inner = ({string.Join(", ", innerParamDecls)}) =>");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            for (int i = 0; i < closureArgs.Count; i++)
            {
                EmitArgMarshal(csWriter, closureArgs[i], closureArgCSharpTypes[i], i);
            }
            var userArgs = string.Join(", ", Enumerable.Range(0, closureArgs.Count).Select(i => $"__a{i}"));
            csWriter.WriteLine($"return {closureParamName}({userArgs});");
            csWriter.Indent--;
            csWriter.WriteLine("};");
        }
        else
        {
            // Void return
            if (innerTypeArgs.Count > 0)
            {
                csWriter.WriteLine($"Action<{string.Join(", ", innerTypeArgs)}> __inner = ({string.Join(", ", innerParamDecls)}) =>");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                for (int i = 0; i < closureArgs.Count; i++)
                {
                    EmitArgMarshal(csWriter, closureArgs[i], closureArgCSharpTypes[i], i);
                }
                var userArgs = string.Join(", ", Enumerable.Range(0, closureArgs.Count).Select(i => $"__a{i}"));
                csWriter.WriteLine($"{closureParamName}({userArgs});");
                csWriter.Indent--;
                csWriter.WriteLine("};");
            }
            else
            {
                csWriter.WriteLine($"Action __inner = () => {closureParamName}();");
            }
        }

        // Allocate GCHandle — intentionally leaked for @escaping closure lifetime
        csWriter.WriteLine("var __gcHandle = GCHandle.Alloc(__inner);");

        // When in a generic type, callback pointer and P/Invoke live in the helper class
        var helperPrefix = string.IsNullOrEmpty(helperClassName) ? "" : $"{helperClassName}.";

        // Build P/Invoke call arguments
        var callArgs = new List<string>();

        // Non-closure passable params
        foreach (var (arg, csName, _, isClass, isObjCBridged) in passableNonClosureParams)
        {
            if (isClass)
            {
                // ObjC-bridged types (UIViewController, etc.) use .Handle;
                // Swift-native classes use .Payload.DangerousGetHandle()
                if (isObjCBridged)
                    callArgs.Add($"{csName}.Handle");
                else
                    callArgs.Add($"{csName}.Payload.DangerousGetHandle()");
            }
            else
                callArgs.Add(csName);
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

        // Class / struct types — check TypeDatabase
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
    /// Checks if the closure return type is supported. Only Void and Bool for now.
    /// </summary>
    private static bool IsClosureReturnSupported(TypeSpec returnType, ITypeDatabase typeDatabase)
    {
        if (returnType.IsEmptyTuple) return true;
        if (returnType is NamedTypeSpec named && named.Name == "Swift.Bool") return true;

        return false;
    }

    /// <summary>
    /// Checks if a non-closure parameter can be passed through (class, primitive) or omitted (default).
    /// </summary>
    private static bool IsNonClosureParamPassable(ArgumentDecl arg, ITypeDatabase typeDatabase)
    {
        // Params with defaults are omitted — Swift fills them
        if (arg.HasDefaultArg) return true;

        var typeSpec = arg.SwiftTypeSpec;
        if (typeSpec is not NamedTypeSpec named) return false;

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
    /// Gets the C# type, class flag, and ObjC-bridged flag for a non-closure parameter.
    /// ObjC-bridged types use .Handle; Swift-native classes use .Payload.DangerousGetHandle().
    /// </summary>
    private static (string csType, bool isClass, bool isObjCBridged) GetNonClosureParamCSharpType(
        ArgumentDecl arg, MethodEnvironment env)
    {
        var typeSpec = arg.SwiftTypeSpec;
        if (typeSpec is NamedTypeSpec named)
        {
            if (MarshallingHelpers.IsSwiftPrimitive(named.Name))
            {
                var csType = GetCSharpPrimitiveType(named.Name);
                return (csType, false, false);
            }

            if (env.TypeDatabase.TryGetTypeRecord(typeSpec, out var record))
            {
                bool isObjC = MarshallingHelpers.IsObjCBridged(record);
                return (record.CSharpTypeName.FullyQualifiedName, true, isObjC);
            }
        }

        return ("IntPtr", true, false);
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

            // Class type
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
}
