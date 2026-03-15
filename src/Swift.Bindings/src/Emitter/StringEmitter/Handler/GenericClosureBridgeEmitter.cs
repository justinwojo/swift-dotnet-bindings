// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits monomorphized Swift wrapper bridges for methods with generic closure parameters
/// (Pattern A: sync, method-generic, noescape, identity-forwarding return).
/// <para>
/// For methods like <c>func read&lt;T&gt;(_ block: (Database) throws -&gt; T) rethrows -&gt; T</c>,
/// generates a complete parallel emission path (Swift wrapper + C# callbacks + P/Invoke + public API).
/// Takes over the entire method emission from MethodHandler (similar to ArraySliceNormalizationEmitter).
/// </para>
/// </summary>
public static class GenericClosureBridgeEmitter
{
    // State is stored on ModuleEmissionContext (per-module instance).

    /// <summary>
    /// Attempts to emit a generic closure bridge for the given method.
    /// Returns true if the method was handled (caller should skip normal emission).
    /// Returns false if the method is not eligible (caller proceeds normally).
    /// </summary>
    public static bool TryEmit(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        TypeDecl? parentDecl,
        ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        var methodDecl = env.MethodDecl;
        if (methodDecl.IsConstructor) return false;
        if (methodDecl.UsesWrapperLibrary) return false;

        // Find the generic closure argument
        var closureInfo = FindGenericClosureArg(env);
        if (closureInfo == null) return false;

        var (closureArg, closureTypeSpec) = closureInfo.Value;

        // Get module name for SBW_CreateError
        var moduleName = methodDecl.ModuleDecl?.Name ?? "SwiftBindings";

        // Emit SBW_CreateError helper if needed
        EmitCreateErrorHelperIfNeeded(swiftWriter, moduleName, ctx);

        // Emit Swift wrappers (returning + void)
        EmitSwiftWrappers(swiftWriter, env, parentDecl, closureArg, closureTypeSpec);

        // Set method flags so PInvokeEmitter routes to wrapper library
        methodDecl.HasGenericClosureBridge = true;
        methodDecl.UsesWrapperLibrary = true;
        methodDecl.UsesFreeFunctionWrapper = true;

        // Emit C# code (callbacks + P/Invokes + public methods)
        EmitCSharp(csWriter, env, parentDecl, closureArg, closureTypeSpec, moduleName, ctx);

        methodDecl.WasEmitted = true;
        return true;
    }

    /// <summary>
    /// Finds the first generic closure argument eligible for the bridge pattern.
    /// </summary>
    private static (ArgumentDecl arg, ClosureTypeSpec closure)? FindGenericClosureArg(MethodEnvironment env)
    {
        ArgumentDecl? closureArgCandidate = null;
        ClosureTypeSpec? closureTypeSpecCandidate = null;

        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            var closureTypeSpec = env.ClosureHandler.GetClosureTypeSpec(arg);
            if (closureTypeSpec != null &&
                ClosureHandler.HasGenericTypeParameters(closureTypeSpec) &&
                env.ClosureHandler.IsMethodGenericClosureEligible(closureTypeSpec, env.MethodDecl))
            {
                closureArgCandidate = arg;
                closureTypeSpecCandidate = closureTypeSpec;
            }
        }

        if (closureArgCandidate == null || closureTypeSpecCandidate == null)
            return null;

        // Verify all non-closure parameters are IntPtr-compatible for the bridge P/Invoke.
        // Class types and nint/Int-mapped types pass as IntPtr. Value types (String, frozen structs,
        // Optional<ValueType>, existential containers) require complex P/Invoke marshalling that
        // the bridge doesn't support yet.
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (arg == closureArgCandidate) continue;
            if (!IsIntPtrCompatibleParam(arg, env.TypeDatabase))
                return null;
        }

        return (closureArgCandidate, closureTypeSpecCandidate);
    }

    /// <summary>
    /// Checks if all non-closure parameters in the method are IntPtr-compatible.
    /// Called by MemberEmissionValidator to ensure only bridgeable methods pass the gate.
    /// </summary>
    public static bool AreNonClosureParamsCompatible(MethodDecl method, ArgumentDecl closureArg, MethodEnvironment env)
    {
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg == closureArg) continue;
            if (!IsIntPtrCompatibleParam(arg, env.TypeDatabase))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Overload for MemberEmissionValidator which doesn't have a MethodEnvironment.
    /// </summary>
    public static bool AreNonClosureParamsCompatible(MethodDecl method, ArgumentDecl closureArg, ITypeDatabase typeDatabase)
    {
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg == closureArg) continue;
            if (!IsIntPtrCompatibleParam(arg, typeDatabase))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if a non-closure parameter can be safely passed as IntPtr in the bridge P/Invoke.
    /// Currently returns false for ALL parameter types because the emitted C# wrapper passes
    /// managed variables directly into a P/Invoke that declares IntPtr, with no conversion
    /// (no .DangerousGetHandle(), no implicit cast). Until proper per-type marshalling is
    /// emitted, only methods where the closure is the sole non-self parameter are eligible.
    /// </summary>
    private static bool IsIntPtrCompatibleParam(ArgumentDecl arg, ITypeDatabase typeDatabase)
    {
        // No non-closure params are supported — the bridge emits no marshalling conversions.
        return false;
    }

    // ─── SBW_CreateError Helper ───────────────────────────────────────

    private static void EmitCreateErrorHelperIfNeeded(SwiftWriter swiftWriter, string moduleName, ModuleEmissionContext ctx)
    {
        if (ctx.GenericClosureBridgeCreateErrorEmitted) return;

        var symbol = $"SBW_CreateError_{moduleName}";
        swiftWriter.WriteLines($$"""
            // Create a Swift Error from a C string message (generic closure bridge error propagation).
            @_cdecl("{{symbol}}")
            public func SBW_CreateError(_ message: UnsafePointer<CChar>) -> UnsafeMutableRawPointer {
                let msg = String(cString: message)
                let error = NSError(domain: "SwiftBindings", code: -1, userInfo: [NSLocalizedDescriptionKey: msg])
                return Unmanaged.passRetained(error as AnyObject).toOpaque()
            }

            """);
        ctx.GenericClosureBridgeCreateErrorEmitted = true;
    }

    // ─── Swift Wrapper Generation ─────────────────────────────────────

    private static void EmitSwiftWrappers(
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        TypeDecl? parentDecl,
        ArgumentDecl closureArg,
        ClosureTypeSpec closureTypeSpec)
    {
        var methodDecl = env.MethodDecl;
        var csClosureName = NameProvider.StripVerbatimPrefix(NameProvider.GetCSharpParameterName(closureArg));

        // Determine instance/self handling
        bool isInstance = methodDecl.MethodType != MethodType.Static && parentDecl != null;
        bool isClass = parentDecl is ClassDecl;
        var typeName = parentDecl?.SwiftTypeName?.ModuleQualifiedName ?? parentDecl?.Name ?? "";

        string selfConversion = "";
        if (isInstance)
        {
            selfConversion = isClass
                ? $"let __self = unsafeBitCast(OpaquePointer(_self), to: {typeName}.self)"
                : $"let __self = _self.assumingMemoryBound(to: {typeName}.self).pointee";
        }

        string callTarget = isInstance ? "__self" : typeName;

        // Get argument label for the closure parameter
        string closureLabel = GetSwiftArgLabel(closureArg);

        // Build non-closure parameter info
        var nonClosureParams = new List<(ArgumentDecl arg, string swiftName, string swiftType, string label)>();
        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            if (arg == closureArg) continue;
            var name = NameProvider.EscapeSwiftKeyword(NameProvider.GetCSharpParameterName(arg));
            var type = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
            var label = GetSwiftArgLabel(arg);
            nonClosureParams.Add((arg, name, type, label));
        }

        // Build closure parameter declarations — the wrapper specializes T = UnsafeMutableRawPointer,
        // so generic type parameters become UnsafeMutableRawPointer in the closure signature.
        var closureParamDecls = new List<string>();
        var cdeclPassArgs = new List<string>(); // args to pass to the cdecl callback
        int argIdx = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            var paramName = $"_arg{argIdx}";

            if (arg is NamedTypeSpec named && TypeSpecHelpers.IsGenericTypeParameter(named.Name))
            {
                // Generic param — specialized to UnsafeMutableRawPointer
                closureParamDecls.Add($"{paramName}: UnsafeMutableRawPointer");
                cdeclPassArgs.Add(paramName);
            }
            else
            {
                // Concrete type — render original type for the closure signature
                var renderedType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg);
                closureParamDecls.Add($"{paramName}: {renderedType}");
                // Convert to UnsafeMutableRawPointer for cdecl
                cdeclPassArgs.Add($"Unmanaged.passUnretained({paramName} as AnyObject).toOpaque()");
            }
            argIdx++;
        }

        var closureParamStr = string.Join(", ", closureParamDecls);
        var throwsInClosure = closureTypeSpec.Throws ? " throws" : "";
        var throwsStr = methodDecl.Throws ? " throws" : "";
        var tryPrefix = methodDecl.Throws ? "try " : "";

        var returningSymbol = $"{methodDecl.MangledName}_XC";
        var voidSymbol = $"{methodDecl.MangledName}_XC_void";

        // --- Returning variant (T = UnsafeMutableRawPointer) ---
        {
            var swiftParams = new List<string>();
            swiftParams.Add($"_ {csClosureName}FuncPtr: UnsafeMutableRawPointer?");
            swiftParams.Add($"_ {csClosureName}Context: UnsafeMutableRawPointer?");
            swiftParams.Add("_ _resultBuf: UnsafeMutableRawPointer");
            foreach (var p in nonClosureParams)
                swiftParams.Add($"_ {p.swiftName}: {p.swiftType}");
            if (isInstance)
                swiftParams.Add("_ _self: UnsafeMutableRawPointer");

            // Build cdecl callback type: (closureArgs..., resultBuf, errorOut, context) -> Void
            var cdeclTypeArgs = new List<string>();
            for (int i = 0; i < closureTypeSpec.EachArgument().Count(); i++)
                cdeclTypeArgs.Add("UnsafeMutableRawPointer");
            cdeclTypeArgs.Add("UnsafeMutableRawPointer"); // resultBuf
            cdeclTypeArgs.Add("UnsafeMutablePointer<UnsafeMutableRawPointer?>?"); // errorOut
            cdeclTypeArgs.Add("UnsafeMutableRawPointer?"); // context
            var cdeclTypeStr = $"(@convention(c) ({string.Join(", ", cdeclTypeArgs)}) -> Void).self";

            var fullCallArgs = BuildFullCallArgs(methodDecl, closureArg, closureLabel, nonClosureParams);

            // Build cdecl call args: (closure args..., resultBuf, &innerError, context)
            var cdeclCallArgsFull = new List<string>(cdeclPassArgs);
            cdeclCallArgsFull.Add("_resultBuf");
            cdeclCallArgsFull.Add("&innerError");
            cdeclCallArgsFull.Add($"{csClosureName}Context");

            swiftWriter.WriteLine($"@_silgen_name(\"{returningSymbol}\")");
            swiftWriter.WriteLine($"public func {NameProvider.GetPInvokeName(methodDecl)}_XC(");
            swiftWriter.WriteLine($"    {string.Join(",\n    ", swiftParams)}");
            swiftWriter.WriteLine($"){throwsStr} {{");

            if (!string.IsNullOrEmpty(selfConversion))
                swiftWriter.WriteLine($"    {selfConversion}");

            swiftWriter.WriteLine($"    let cdecl = unsafeBitCast({csClosureName}FuncPtr!, to: {cdeclTypeStr})");

            // Emit the call with inline closure.
            // Replace __CLOSURE__ with the closure opening — the closure body spans multiple lines,
            // ending with }) which closes both the closure brace and the method call paren.
            var closureOpening = $"{{ ({closureParamStr}){throwsInClosure} -> UnsafeMutableRawPointer in";
            var callLine = fullCallArgs.Replace("__CLOSURE__", closureOpening);
            swiftWriter.WriteLine($"    let _: UnsafeMutableRawPointer = {tryPrefix}{callTarget}.{NameProvider.ParserNameToSwift(methodDecl)}({callLine}");
            swiftWriter.WriteLine($"        var innerError: UnsafeMutableRawPointer? = nil");
            swiftWriter.WriteLine($"        cdecl({string.Join(", ", cdeclCallArgsFull)})");
            swiftWriter.WriteLine($"        if let err = innerError {{");
            swiftWriter.WriteLine($"            throw unsafeBitCast(err, to: Swift.Error.self)");
            swiftWriter.WriteLine($"        }}");
            swiftWriter.WriteLine($"        return _resultBuf");
            swiftWriter.WriteLine($"    }})");

            swiftWriter.WriteLine("}");
            swiftWriter.WriteLine();
        }

        // --- Void variant (T = Void, no resultBuf) ---
        {
            var swiftParams = new List<string>();
            swiftParams.Add($"_ {csClosureName}FuncPtr: UnsafeMutableRawPointer?");
            swiftParams.Add($"_ {csClosureName}Context: UnsafeMutableRawPointer?");
            foreach (var p in nonClosureParams)
                swiftParams.Add($"_ {p.swiftName}: {p.swiftType}");
            if (isInstance)
                swiftParams.Add("_ _self: UnsafeMutableRawPointer");

            // Build cdecl callback type (no resultBuf for void)
            var cdeclTypeArgs = new List<string>();
            for (int i = 0; i < closureTypeSpec.EachArgument().Count(); i++)
                cdeclTypeArgs.Add("UnsafeMutableRawPointer");
            cdeclTypeArgs.Add("UnsafeMutablePointer<UnsafeMutableRawPointer?>?"); // errorOut
            cdeclTypeArgs.Add("UnsafeMutableRawPointer?"); // context
            var cdeclTypeStr = $"(@convention(c) ({string.Join(", ", cdeclTypeArgs)}) -> Void).self";

            var fullCallArgs = BuildFullCallArgs(methodDecl, closureArg, closureLabel, nonClosureParams);

            // Cdecl args for void (no resultBuf)
            var cdeclCallArgsFull = new List<string>(cdeclPassArgs);
            cdeclCallArgsFull.Add("&innerError");
            cdeclCallArgsFull.Add($"{csClosureName}Context");

            swiftWriter.WriteLine($"@_silgen_name(\"{voidSymbol}\")");
            swiftWriter.WriteLine($"public func {NameProvider.GetPInvokeName(methodDecl)}_XC_void(");
            swiftWriter.WriteLine($"    {string.Join(",\n    ", swiftParams)}");
            swiftWriter.WriteLine($"){throwsStr} {{");

            if (!string.IsNullOrEmpty(selfConversion))
                swiftWriter.WriteLine($"    {selfConversion}");

            swiftWriter.WriteLine($"    let cdecl = unsafeBitCast({csClosureName}FuncPtr!, to: {cdeclTypeStr})");

            var closureOpening = $"{{ ({closureParamStr}){throwsInClosure} -> Void in";
            var callLine = fullCallArgs.Replace("__CLOSURE__", closureOpening);
            swiftWriter.WriteLine($"    {tryPrefix}{callTarget}.{NameProvider.ParserNameToSwift(methodDecl)}({callLine}");
            swiftWriter.WriteLine($"        var innerError: UnsafeMutableRawPointer? = nil");
            swiftWriter.WriteLine($"        cdecl({string.Join(", ", cdeclCallArgsFull)})");
            swiftWriter.WriteLine($"        if let err = innerError {{");
            swiftWriter.WriteLine($"            throw unsafeBitCast(err, to: Swift.Error.self)");
            swiftWriter.WriteLine($"        }}");
            swiftWriter.WriteLine($"    }})");

            swiftWriter.WriteLine("}");
            swiftWriter.WriteLine();
        }
    }

    /// <summary>
    /// Builds the full argument list for the Swift method call, preserving declaration order.
    /// Uses "__CLOSURE__" as a placeholder for the closure argument position.
    /// </summary>
    private static string BuildFullCallArgs(
        MethodDecl methodDecl,
        ArgumentDecl closureArg,
        string closureLabel,
        List<(ArgumentDecl arg, string swiftName, string swiftType, string label)> nonClosureParams)
    {
        var parts = new List<string>();
        var nonClosureIdx = 0;

        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            if (arg == closureArg)
            {
                parts.Add($"{closureLabel}__CLOSURE__");
            }
            else if (nonClosureIdx < nonClosureParams.Count)
            {
                var p = nonClosureParams[nonClosureIdx];
                parts.Add($"{p.label}{p.swiftName}");
                nonClosureIdx++;
            }
        }

        return string.Join(", ", parts);
    }

    // ─── C# Code Generation ──────────────────────────────────────────

    private static void EmitCSharp(
        CSharpWriter csWriter,
        MethodEnvironment env,
        TypeDecl? parentDecl,
        ArgumentDecl closureArg,
        ClosureTypeSpec closureTypeSpec,
        string moduleName,
        ModuleEmissionContext ctx)
    {
        var methodDecl = env.MethodDecl;
        var csClosureName = NameProvider.GetCSharpParameterName(closureArg);
        var mangledHash = EmitterUtility.DeterministicHash8(methodDecl.MangledName);
        var pInvokeName = NameProvider.GetPInvokeName(methodDecl);
        var methodName = NameProvider.ToPascalCase(methodDecl.Name);
        var returningSymbol = $"{methodDecl.MangledName}_XC";
        var voidSymbol = $"{methodDecl.MangledName}_XC_void";
        var asyncLibName = env.TypeDatabase.AsyncLibraryName ?? "SwiftBindings";

        // Resolve non-generic closure argument C# types (concrete types only, skip generic params)
        var closureArgTypes = new List<string>();
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (arg is NamedTypeSpec named && TypeSpecHelpers.IsGenericTypeParameter(named.Name))
                continue;
            closureArgTypes.Add(GetCSharpTypeForClosureArg(arg, env));
        }

        var callbackNameRet = $"GenericClosureBridge_{mangledHash}_Callback";
        var callbackNameVoid = $"GenericClosureBridge_{mangledHash}_VoidCallback";

        // --- Callbacks ---
        EmitReturningCallback(csWriter, closureArgTypes, callbackNameRet, moduleName);
        EmitVoidCallback(csWriter, closureArgTypes, callbackNameVoid, moduleName);

        // --- Static callback pointer fields ---
        var retDelegateParts = BuildCallbackDelegateType(closureArgTypes, hasResultBuf: true);
        var voidDelegateParts = BuildCallbackDelegateType(closureArgTypes, hasResultBuf: false);
        csWriter.WriteLine($"private static readonly unsafe IntPtr s_{callbackNameRet} = (IntPtr)(delegate* unmanaged[Cdecl]<{retDelegateParts}>)&{callbackNameRet};");
        csWriter.WriteLine($"private static readonly unsafe IntPtr s_{callbackNameVoid} = (IntPtr)(delegate* unmanaged[Cdecl]<{voidDelegateParts}>)&{callbackNameVoid};");
        csWriter.WriteLine();

        // --- P/Invoke declarations ---
        EmitCreateErrorPInvoke(csWriter, moduleName, asyncLibName, env, ctx);
        EmitErrorHelperPInvokes(csWriter, moduleName, asyncLibName, env, ctx);
        EmitPInvokeDeclarations(csWriter, pInvokeName, returningSymbol, voidSymbol, asyncLibName,
            methodDecl, env, closureArg);

        // --- Public methods ---
        var classParent = parentDecl as ClassDecl;
        var selfExpr = classParent != null
            ? (classParent.IsObjCRooted ? "Handle" : "_handle.DangerousGetHandle()")
            : "_payload.DangerousGetHandle()";
        EmitPublicReturningMethod(csWriter, methodDecl, methodName, closureArgTypes,
            callbackNameRet, pInvokeName, csClosureName, closureTypeSpec, env, selfExpr);
        EmitPublicVoidMethod(csWriter, methodDecl, methodName, closureArgTypes,
            callbackNameVoid, pInvokeName, csClosureName, closureTypeSpec, env, selfExpr);
    }

    private static void EmitReturningCallback(
        CSharpWriter csWriter,
        List<string> closureArgTypes,
        string callbackName,
        string moduleName)
    {
        // Callback params: (closureArgs..., resultBuf, errorOut, context)
        // Each closure arg arrives as a separate void* from the Swift cdecl callback.
        // The GCHandle stores object[] { Action<IntPtr[], IntPtr> } where the delegate
        // was created by the public generic method with T captured via closure.
        var paramParts = new List<string>();
        for (int i = 0; i < closureArgTypes.Count; i++)
            paramParts.Add($"void* arg{i}");
        paramParts.Add("void* resultBuf");
        paramParts.Add("void** errorOut");
        paramParts.Add("IntPtr contextPtr");

        csWriter.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        csWriter.WriteLine($"private static unsafe void {callbackName}({string.Join(", ", paramParts)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        csWriter.WriteLine("var handle = GCHandle.FromIntPtr(contextPtr);");
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        csWriter.WriteLine("var state = (object[])handle.Target!;");
        csWriter.WriteLine("var invoke = (Action<IntPtr[], IntPtr>)state[0];");

        // Build args array from individual void* params
        if (closureArgTypes.Count > 0)
        {
            var argEntries = Enumerable.Range(0, closureArgTypes.Count)
                .Select(i => $"(IntPtr)arg{i}");
            csWriter.WriteLine($"invoke(new IntPtr[] {{ {string.Join(", ", argEntries)} }}, (IntPtr)resultBuf);");
        }
        else
        {
            csWriter.WriteLine("invoke(Array.Empty<IntPtr>(), (IntPtr)resultBuf);");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("catch (Exception ex)");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine($"*errorOut = (void*)SBW_CreateError_{moduleName}(ex.Message);");
        csWriter.Indent--;
        csWriter.WriteLine("}");

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    private static void EmitVoidCallback(
        CSharpWriter csWriter,
        List<string> closureArgTypes,
        string callbackName,
        string moduleName)
    {
        // Void callback: (closureArgs..., errorOut, context) — no resultBuf
        var paramParts = new List<string>();
        for (int i = 0; i < closureArgTypes.Count; i++)
            paramParts.Add($"void* arg{i}");
        paramParts.Add("void** errorOut");
        paramParts.Add("IntPtr contextPtr");

        csWriter.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        csWriter.WriteLine($"private static unsafe void {callbackName}({string.Join(", ", paramParts)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        csWriter.WriteLine("var handle = GCHandle.FromIntPtr(contextPtr);");
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        csWriter.WriteLine("var state = (object[])handle.Target!;");
        csWriter.WriteLine("var invoke = (Action<IntPtr[]>)state[0];");

        // Build args array from individual void* params
        if (closureArgTypes.Count > 0)
        {
            var argEntries = Enumerable.Range(0, closureArgTypes.Count)
                .Select(i => $"(IntPtr)arg{i}");
            csWriter.WriteLine($"invoke(new IntPtr[] {{ {string.Join(", ", argEntries)} }});");
        }
        else
        {
            csWriter.WriteLine("invoke(Array.Empty<IntPtr>());");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("catch (Exception ex)");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine($"*errorOut = (void*)SBW_CreateError_{moduleName}(ex.Message);");
        csWriter.Indent--;
        csWriter.WriteLine("}");

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    private static void EmitCreateErrorPInvoke(CSharpWriter csWriter, string moduleName, string asyncLibName,
        MethodEnvironment env, ModuleEmissionContext ctx)
    {
        var typeKey = (env.ParentDecl as TypeDecl)?.SwiftTypeName.ModuleQualifiedName ?? moduleName;
        if (!ctx.TryAddGenericClosureBridgeErrorPInvoke(typeKey)) return;

        PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
        {
            LibraryPath = asyncLibName,
            EntryPoint = $"SBW_CreateError_{moduleName}",
            MethodName = $"SBW_CreateError_{moduleName}",
            ReturnType = "IntPtr",
            ParametersString = "[MarshalAs(UnmanagedType.LPUTF8Str)] string message",
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Internal
        });
        csWriter.WriteLine();
    }

    private static void EmitErrorHelperPInvokes(CSharpWriter csWriter, string moduleName, string asyncLibName,
        MethodEnvironment env, ModuleEmissionContext ctx)
    {
        // Use ErrorDescriptionEmitter's per-type tracking to avoid duplicating P/Invokes
        // already emitted by the regular code path (WrapperEmitter.Marshalling).
        var typeKey = (env.ParentDecl as TypeDecl)?.SwiftTypeName.ModuleQualifiedName ?? moduleName;

        if (!ErrorDescriptionEmitter.HasErrorPInvokeForType(typeKey, ctx))
        {
            ErrorDescriptionEmitter.MarkErrorPInvokeEmittedForType(typeKey, ctx);

            var descSymbol = ErrorDescriptionEmitter.GetDescriptionSymbolName(moduleName);
            var releaseSymbol = ErrorDescriptionEmitter.GetReleaseSymbolName(moduleName);

            csWriter.WriteLines($"""
                [global::System.Runtime.InteropServices.LibraryImport("{asyncLibName}", EntryPoint = "{descSymbol}")]
                private static partial IntPtr SBW_GetErrorDescription(IntPtr error);

                [global::System.Runtime.InteropServices.LibraryImport("{asyncLibName}", EntryPoint = "{releaseSymbol}")]
                private static partial void SBW_ReleaseError(IntPtr error);

                """);

            // Emit SBW_Free if not already emitted by Utf8SliceEmitter for this type
            if (!Utf8SliceEmitter.HasFreePInvokeForType(typeKey, ctx))
            {
                Utf8SliceEmitter.MarkFreePInvokeEmittedForType(typeKey, ctx);
                var freeSymbol = Utf8SliceEmitter.GetFreeSymbolName(moduleName);

                csWriter.WriteLines($"""
                    [global::System.Runtime.InteropServices.LibraryImport("{asyncLibName}", EntryPoint = "{freeSymbol}")]
                    private static partial void SBW_Free(IntPtr ptr);

                    """);
            }
        }
    }

    private static void EmitPInvokeDeclarations(
        CSharpWriter csWriter,
        string pInvokeName,
        string returningSymbol,
        string voidSymbol,
        string asyncLibName,
        MethodDecl methodDecl,
        MethodEnvironment env,
        ArgumentDecl closureArg)
    {
        // --- Returning variant P/Invoke ---
        var retParams = new List<string>();
        retParams.Add("IntPtr blockFuncPtr");
        retParams.Add("IntPtr blockContext");
        retParams.Add("void* resultBuf");
        AddNonClosureAndSelfParams(retParams, methodDecl, env, closureArg);
        if (methodDecl.Throws)
            retParams.Add("out SwiftError swiftError");

        PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
        {
            LibraryPath = asyncLibName,
            EntryPoint = returningSymbol,
            MethodName = $"{pInvokeName}_XC",
            ReturnType = "void",
            ParametersString = string.Join(", ", retParams),
            Visibility = PInvokeVisibility.Internal,
            IsUnsafe = true
        });
        csWriter.WriteLine();

        // --- Void variant P/Invoke ---
        var voidParams = new List<string>();
        voidParams.Add("IntPtr blockFuncPtr");
        voidParams.Add("IntPtr blockContext");
        AddNonClosureAndSelfParams(voidParams, methodDecl, env, closureArg);
        if (methodDecl.Throws)
            voidParams.Add("out SwiftError swiftError");

        PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
        {
            LibraryPath = asyncLibName,
            EntryPoint = voidSymbol,
            MethodName = $"{pInvokeName}_XC_void",
            ReturnType = "void",
            ParametersString = string.Join(", ", voidParams),
            Visibility = PInvokeVisibility.Internal,
            IsUnsafe = true
        });
        csWriter.WriteLine();
    }

    private static void AddNonClosureAndSelfParams(
        List<string> paramList,
        MethodDecl methodDecl,
        MethodEnvironment env,
        ArgumentDecl closureArg)
    {
        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            if (arg == closureArg) continue;
            var csName = NameProvider.GetCSharpParameterName(arg);
            paramList.Add($"IntPtr {csName}");
        }
        if (methodDecl.MethodType == MethodType.Instance)
            paramList.Add("SwiftSelf self_");
    }

    private static void EmitPublicReturningMethod(
        CSharpWriter csWriter,
        MethodDecl methodDecl,
        string methodName,
        List<string> closureArgTypes,
        string callbackName,
        string pInvokeName,
        string csClosureName,
        ClosureTypeSpec closureTypeSpec,
        MethodEnvironment env,
        string selfExpr)
    {
        // Build Func<ArgTypes..., T> type
        var funcTypeParams = new List<string>(closureArgTypes);
        funcTypeParams.Add("T");
        var funcType = $"Func<{string.Join(", ", funcTypeParams)}>";

        var publicParams = new List<string>();
        publicParams.Add($"{funcType} {csClosureName}");
        AddNonClosurePublicParams(publicParams, methodDecl, env);

        XmlDocCommentEmitter.EmitMethodDocComment(csWriter, methodDecl);
        csWriter.WriteLine($"public unsafe T {methodName}<T>({string.Join(", ", publicParams)}) where T : ISwiftObject");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Aligned result buffer allocation
        csWriter.WriteLine("var metadata = TypeMetadata.GetTypeMetadataOrThrow<T>();");
        csWriter.WriteLine("var size = metadata.Size;");
        csWriter.WriteLine("if (size == 0) size = 1;");
        csWriter.WriteLine("void* resultBuf = NativeMemory.AlignedAlloc(size, (nuint)metadata.Alignment);");
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Create the invoke delegate that captures T and the user's Func via closure.
        // The callback (non-generic, [UnmanagedCallersOnly]) extracts and calls this delegate.
        // Delegate signature: Action<IntPtr[], IntPtr> (argsArray, resultBufPtr)
        csWriter.WriteLine("Action<IntPtr[], IntPtr> invoke = (IntPtr[] args, IntPtr resBufPtr) =>");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        // Marshal each closure arg from IntPtr to concrete C# type
        for (int i = 0; i < closureArgTypes.Count; i++)
            csWriter.WriteLine($"var a{i} = SwiftMarshal.MarshalFromSwift<{closureArgTypes[i]}>(args[{i}]);");
        var userCallArgs = Enumerable.Range(0, closureArgTypes.Count).Select(i => $"a{i}");
        csWriter.WriteLine($"var result = {csClosureName}({string.Join(", ", userCallArgs)});");
        csWriter.WriteLine("var resBufSpan = new Span<byte>((void*)resBufPtr, (int)metadata.Size);");
        csWriter.WriteLine("SwiftMarshal.MarshalToSwift(result, ref resBufSpan);");
        csWriter.Indent--;
        csWriter.WriteLine("};");

        csWriter.WriteLine("var state = new object[] { invoke };");
        csWriter.WriteLine("var gcHandle = GCHandle.Alloc(state);");
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Build P/Invoke call
        var callArgs = new List<string>();
        callArgs.Add($"s_{callbackName}");
        callArgs.Add("GCHandle.ToIntPtr(gcHandle)");
        callArgs.Add("resultBuf");
        AddNonClosurePInvokeCallArgs(callArgs, methodDecl);
        if (methodDecl.MethodType == MethodType.Instance)
            callArgs.Add($"new SwiftSelf((void*){selfExpr})");
        if (methodDecl.Throws)
            callArgs.Add("out SwiftError swiftError");

        csWriter.WriteLine($"{pInvokeName}_XC({string.Join(", ", callArgs)});");

        if (methodDecl.Throws)
        {
            csWriter.WriteLine("if (swiftError.Value != null)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("string _errorMessage;");
            csWriter.WriteLine("var _errorPtr = (IntPtr)swiftError.Value;");
            csWriter.WriteLine("var _descPtr = SBW_GetErrorDescription(_errorPtr);");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("_errorMessage = _descPtr != IntPtr.Zero");
            csWriter.Indent++;
            csWriter.WriteLine("? global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(_descPtr) ?? \"Unknown Swift error\"");
            csWriter.WriteLine(": \"Unknown Swift error\";");
            csWriter.Indent--;
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("if (_descPtr != IntPtr.Zero) SBW_Free(_descPtr);");
            csWriter.WriteLine("SBW_ReleaseError(_errorPtr);");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("throw new SwiftRuntimeException(_errorMessage);");
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        csWriter.WriteLine("return SwiftMarshal.MarshalFromSwift<T>(new IntPtr(resultBuf));");

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("finally { if (gcHandle.IsAllocated) gcHandle.Free(); }");

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("finally { NativeMemory.AlignedFree(resultBuf); }");

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    private static void EmitPublicVoidMethod(
        CSharpWriter csWriter,
        MethodDecl methodDecl,
        string methodName,
        List<string> closureArgTypes,
        string callbackName,
        string pInvokeName,
        string csClosureName,
        ClosureTypeSpec closureTypeSpec,
        MethodEnvironment env,
        string selfExpr)
    {
        var actionType = closureArgTypes.Count > 0
            ? $"Action<{string.Join(", ", closureArgTypes)}>"
            : "Action";

        var publicParams = new List<string>();
        publicParams.Add($"{actionType} {csClosureName}");
        AddNonClosurePublicParams(publicParams, methodDecl, env);

        csWriter.WriteLine($"public unsafe void {methodName}({string.Join(", ", publicParams)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Create invoke delegate: Action<IntPtr[]> (argsArray)
        csWriter.WriteLine("Action<IntPtr[]> invoke = (IntPtr[] args) =>");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        for (int i = 0; i < closureArgTypes.Count; i++)
            csWriter.WriteLine($"var a{i} = SwiftMarshal.MarshalFromSwift<{closureArgTypes[i]}>(args[{i}]);");
        var userCallArgs = Enumerable.Range(0, closureArgTypes.Count).Select(i => $"a{i}");
        csWriter.WriteLine($"{csClosureName}({string.Join(", ", userCallArgs)});");
        csWriter.Indent--;
        csWriter.WriteLine("};");

        csWriter.WriteLine("var state = new object[] { invoke };");
        csWriter.WriteLine("var gcHandle = GCHandle.Alloc(state);");
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        var callArgs = new List<string>();
        callArgs.Add($"s_{callbackName}");
        callArgs.Add("GCHandle.ToIntPtr(gcHandle)");
        AddNonClosurePInvokeCallArgs(callArgs, methodDecl);
        if (methodDecl.MethodType == MethodType.Instance)
            callArgs.Add($"new SwiftSelf((void*){selfExpr})");
        if (methodDecl.Throws)
            callArgs.Add("out SwiftError swiftError");

        csWriter.WriteLine($"{pInvokeName}_XC_void({string.Join(", ", callArgs)});");

        if (methodDecl.Throws)
        {
            csWriter.WriteLine("if (swiftError.Value != null)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("string _errorMessage;");
            csWriter.WriteLine("var _errorPtr = (IntPtr)swiftError.Value;");
            csWriter.WriteLine("var _descPtr = SBW_GetErrorDescription(_errorPtr);");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("_errorMessage = _descPtr != IntPtr.Zero");
            csWriter.Indent++;
            csWriter.WriteLine("? global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(_descPtr) ?? \"Unknown Swift error\"");
            csWriter.WriteLine(": \"Unknown Swift error\";");
            csWriter.Indent--;
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("if (_descPtr != IntPtr.Zero) SBW_Free(_descPtr);");
            csWriter.WriteLine("SBW_ReleaseError(_errorPtr);");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("throw new SwiftRuntimeException(_errorMessage);");
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("finally { if (gcHandle.IsAllocated) gcHandle.Free(); }");

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static void AddNonClosurePublicParams(
        List<string> publicParams,
        MethodDecl methodDecl,
        MethodEnvironment env)
    {
        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            var closureSpec = env.ClosureHandler.GetClosureTypeSpec(arg);
            if (closureSpec != null && ClosureHandler.HasGenericTypeParameters(closureSpec))
                continue;
            var csName = NameProvider.GetCSharpParameterName(arg);
            var csType = GetPublicParamType(arg, env);
            publicParams.Add($"{csType} {csName}");
        }
    }

    /// <summary>
    /// Adds non-closure arguments to the P/Invoke call args list.
    /// Skips closure arguments (both bare ClosureTypeSpec and Optional-wrapped closures).
    /// </summary>
    private static void AddNonClosurePInvokeCallArgs(
        List<string> callArgs,
        MethodDecl methodDecl)
    {
        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            // Skip bare closure args
            if (arg.SwiftTypeSpec is ClosureTypeSpec)
                continue;
            // Skip Optional<Closure> args
            if (arg.SwiftTypeSpec is NamedTypeSpec opt && opt.Name == "Swift.Optional" &&
                opt.GenericParameters.Count == 1 && opt.GenericParameters[0] is ClosureTypeSpec)
                continue;
            var csName = NameProvider.GetCSharpParameterName(arg);
            callArgs.Add(csName);
        }
    }

    private static string GetCSharpTypeForClosureArg(TypeSpec typeSpec, MethodEnvironment env)
    {
        if (typeSpec is NamedTypeSpec namedType)
        {
            if (namedType.ContainsGenericParameters)
            {
                // Bound generic type — use BoundGenericsHandler to resolve full C# name
                // including generic parameters (e.g., Optional<Data> → SwiftOptional<Foundation.Data>).
                return env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(
                    typeSpec, GenericContext.Empty);
            }
            if (env.TypeDatabase.TryGetTypeRecord(typeSpec, out var record))
                return record.CSharpTypeName.FullyQualifiedName;
        }
        return "IntPtr";
    }

    private static string GetPublicParamType(ArgumentDecl arg, MethodEnvironment env)
    {
        if (arg.SwiftTypeSpec is NamedTypeSpec namedType && namedType.ContainsGenericParameters)
        {
            // Bound generic type — use BoundGenericsHandler for full name
            return env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(
                arg.SwiftTypeSpec, GenericContext.Empty);
        }
        var typeRecord = env.TypeDatabase.GetTypeRecordOrAnyType(arg.SwiftTypeSpec);
        return typeRecord.CSharpTypeName.FullyQualifiedName;
    }

    private static string BuildCallbackDelegateType(List<string> closureArgTypes, bool hasResultBuf)
    {
        var parts = new List<string>();
        for (int i = 0; i < closureArgTypes.Count; i++)
            parts.Add("void*");
        if (hasResultBuf) parts.Add("void*");
        parts.Add("void**"); // errorOut
        parts.Add("IntPtr"); // context
        parts.Add("void"); // return
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Gets the Swift argument label for a parameter.
    /// Delegates to the canonical implementation in ClosureEmitter.
    /// </summary>
    private static string GetSwiftArgLabel(ArgumentDecl arg)
        => ClosureEmitter.GetSwiftArgLabelForCdecl(arg);
}
