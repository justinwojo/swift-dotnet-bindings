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
    /// One closure argument in declaration order: whether it is a method-generic parameter
    /// (specialized to a value-witness buffer pointer and read back to <c>T</c> on the C# side) versus
    /// a concrete class argument (marshalled by its projected C# type), plus the original
    /// <see cref="TypeSpec"/> and the C# surface type ("T" for a generic arg).
    /// </summary>
    private readonly record struct GcbClosureArg(bool IsGeneric, TypeSpec Spec, string CSharpType);

    /// <summary>
    /// True if the method's generic parameter appears anywhere other than the closure's RETURN
    /// position — i.e. in a closure argument or a non-closure parameter. Those positions are
    /// monomorphized to <c>UnsafeMutableRawPointer</c>, so the void (T = Void) variant cannot bind them
    /// and is suppressed. Mirrors the predicate <see cref="EmitSwiftWrappers"/> and
    /// <see cref="EmitCSharp"/> both gate the void variant on.
    /// </summary>
    private static bool TUsedOutsideClosureReturn(
        MethodDecl methodDecl, ArgumentDecl closureArg, ClosureTypeSpec closureTypeSpec)
    {
        foreach (var arg in closureTypeSpec.EachArgument())
            if (arg is NamedTypeSpec n && TypeSpecHelpers.IsGenericTypeParameter(n.Name))
                return true;
        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            if (arg == closureArg) continue;
            if (IsGenericNonClosureParam(arg))
                return true;
        }
        return false;
    }

    /// <summary>True if the parameter is a bare method-generic type parameter (e.g. <c>value: T</c>).</summary>
    private static bool IsGenericNonClosureParam(ArgumentDecl arg)
        => arg.SwiftTypeSpec is NamedTypeSpec ns && TypeSpecHelpers.IsGenericTypeParameter(ns.Name);

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
        ctx ??= ModuleEmissionContext.CreateImplicitFallback();
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

        methodDecl.MarkEmitted();
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
            if (!IsBridgePassableParam(arg, env.TypeDatabase))
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
            if (!IsBridgePassableParam(arg, env.TypeDatabase))
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
            if (!IsBridgePassableParam(arg, typeDatabase))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if a non-closure parameter can be safely passed through the bridge P/Invoke.
    /// Supports class types (IntPtr via handle), ObjC-rooted (IntPtr via Handle),
    /// and primitives (pass by value). Reuses MCB's ParamAbiCategory classification.
    /// </summary>
    private static bool IsBridgePassableParam(ArgumentDecl arg, ITypeDatabase typeDatabase)
    {
        // A bare generic type parameter (e.g. `value: T`) is passable: the bridge specializes
        // T = UnsafeMutableRawPointer, so the C# side allocates a value-witness buffer, marshals the
        // T into it, and passes the buffer pointer as a plain IntPtr (the Swift wrapper forwards it to
        // the closure unchanged). MethodClosureBridge.ClassifyParam has no TypeRecord for a bare
        // generic param and would classify it Unsupported, so admit it explicitly here.
        if (arg.SwiftTypeSpec is NamedTypeSpec genericNamed
            && TypeSpecHelpers.IsGenericTypeParameter(genericNamed.Name))
            return true;

        // ABI passability allowlist is canonical on MethodClosureBridge.IsAbiCategoryPassable,
        // but GenericClosureBridgeEmitter's body-emission switches (around lines 646/654 for
        // the C# P/Invoke params and 908/916 for the C# call site) do not yet carry a
        // Utf8Slice case — admitting Swift.String through the canonical predicate would land
        // it in those switches' `default:` branches and emit a single `IntPtr {csName}` with
        // no UTF-8 pinning/reconstruction. Reject Utf8Slice locally with reasoning until the
        // GCB body grows the matching ptr+len pair (mirrors NestedClosureBridge.IsSupported).
        var category = MethodClosureBridge.ClassifyParam(arg, typeDatabase);
        return MethodClosureBridge.IsAbiCategoryPassable(category)
            && category != MethodClosureBridge.ParamAbiCategory.Utf8Slice;
    }

    // ─── SBW_CreateError Helper ───────────────────────────────────────

    private static void EmitCreateErrorHelperIfNeeded(SwiftWriter swiftWriter, string moduleName, ModuleEmissionContext ctx)
        // create-error helper lands in the `_direct_helper` bucket; the
        // SwiftErrorMintHelperEmitted flag gates re-emission within a module pass. Shared with the
        // standard throwing-closure callback path via SwiftErrorMintEmitter.
        => SwiftErrorMintEmitter.EmitSwiftHelperIfNeeded(swiftWriter, moduleName, ctx);

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

        // Get argument label for the closure parameter
        string closureLabel = GetSwiftArgLabel(closureArg);

        // Build non-closure parameter info. A bare generic non-closure parameter (`value: T`) is
        // rendered as `UnsafeMutableRawPointer` — the wrapper monomorphizes T = UnsafeMutableRawPointer,
        // so "T" is not in scope here; the C# side passes a value-witness buffer pointer for it and the
        // Swift method infers T = UnsafeMutableRawPointer from this argument and the closure signature.
        var nonClosureParams = new List<(ArgumentDecl arg, string swiftName, string swiftType, string label)>();
        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            if (arg == closureArg) continue;
            var name = NameProvider.EscapeSwiftKeyword(NameProvider.GetCSharpParameterName(arg));
            var type = IsGenericNonClosureParam(arg)
                ? "UnsafeMutableRawPointer"
                : ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
            var label = GetSwiftArgLabel(arg);
            nonClosureParams.Add((arg, name, type, label));
        }

        // The void (T = Void) wrapper/overload is only valid when the generic parameter appears
        // SOLELY in the closure's return position (the classic `(X) throws -> T` shape, specialized to
        // `(X) throws -> Void`). Once T also appears in a closure ARGUMENT or a non-closure parameter,
        // those positions are rendered `UnsafeMutableRawPointer` and cannot simultaneously bind to
        // T = Void — the void wrapper would be a Swift type error (and the C# overload would reference
        // `T` in a non-generic method), so it is suppressed rather than emitted-then-stripped.
        bool emitVoidVariant = !TUsedOutsideClosureReturn(methodDecl, closureArg, closureTypeSpec);

        // The @_silgen_name wrapper hardcodes synthetic Swift identifiers in the same scope
        // as the user's non-closure params — the cdecl rebind local, the self pointer param
        // + its bound local, the result buffer param, and the thrown-error locals. A user
        // param spelled the same would produce an "invalid redeclaration" emitted at exit 0.
        // Reserve each synthetic against the user param names (and the closure's own
        // FuncPtr/Context params); collision-free input yields the names verbatim, collisions
        // get a `__`-prefixed variant.
        var synthReserved = nonClosureParams.Select(p => p.swiftName).ToList();
        synthReserved.Add($"{csClosureName}FuncPtr");
        synthReserved.Add($"{csClosureName}Context");
        var nameScope = new SyntheticNameScope(synthReserved);
        var selfParamName = nameScope.Reserve("_self");
        var selfLocalName = nameScope.Reserve("__self");
        var resultBufName = nameScope.Reserve("_resultBuf");
        var cdeclName = nameScope.Reserve("cdecl");
        var innerErrorName = nameScope.Reserve("innerError");
        var errName = nameScope.Reserve("err");

        string selfConversion = "";
        if (isInstance)
        {
            selfConversion = isClass
                ? $"let {selfLocalName} = unsafeBitCast(OpaquePointer({selfParamName}), to: {typeName}.self)"
                : $"let {selfLocalName} = {selfParamName}.assumingMemoryBound(to: {typeName}.self).pointee";
        }

        string callTarget = isInstance ? selfLocalName : typeName;

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

        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            parentDecl, methodDecl.IsMainActorIsolated, methodDecl.IsNonisolated);

        // --- Returning variant (T = UnsafeMutableRawPointer) ---
        {
            var swiftParams = new List<string>();
            swiftParams.Add($"_ {csClosureName}FuncPtr: UnsafeMutableRawPointer?");
            swiftParams.Add($"_ {csClosureName}Context: UnsafeMutableRawPointer?");
            swiftParams.Add($"_ {resultBufName}: UnsafeMutableRawPointer");
            foreach (var p in nonClosureParams)
                swiftParams.Add($"_ {p.swiftName}: {p.swiftType}");
            if (isInstance)
                swiftParams.Add($"_ {selfParamName}: UnsafeMutableRawPointer");

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
            cdeclCallArgsFull.Add(resultBufName);
            cdeclCallArgsFull.Add($"&{innerErrorName}");
            cdeclCallArgsFull.Add($"{csClosureName}Context");

            if (needsMainActor)
                swiftWriter.WriteLine("@MainActor");
            swiftWriter.WriteLine($"@_silgen_name(\"{returningSymbol}\")");
            swiftWriter.WriteLine($"public func {NameProvider.GetPInvokeName(methodDecl)}_XC(");
            swiftWriter.WriteLine($"    {string.Join(",\n    ", swiftParams)}");
            swiftWriter.WriteLine($"){throwsStr} {{");

            if (!string.IsNullOrEmpty(selfConversion))
                swiftWriter.WriteLine($"    {selfConversion}");

            swiftWriter.WriteLine($"    let {cdeclName} = unsafeBitCast({csClosureName}FuncPtr!, to: {cdeclTypeStr})");

            // Emit the call with inline closure.
            // Replace __CLOSURE__ with the closure opening — the closure body spans multiple lines,
            // ending with }) which closes both the closure brace and the method call paren.
            var closureOpening = $"{{ ({closureParamStr}){throwsInClosure} -> UnsafeMutableRawPointer in";
            var callLine = fullCallArgs.Replace("__CLOSURE__", closureOpening);
            swiftWriter.WriteLine($"    let _: UnsafeMutableRawPointer = {tryPrefix}{callTarget}.{NameProvider.ParserNameToSwift(methodDecl)}({callLine}");
            swiftWriter.WriteLine($"        var {innerErrorName}: UnsafeMutableRawPointer? = nil");
            swiftWriter.WriteLine($"        {cdeclName}({string.Join(", ", cdeclCallArgsFull)})");
            swiftWriter.WriteLine($"        if let {errName} = {innerErrorName} {{");
            swiftWriter.WriteLine($"            throw unsafeBitCast({errName}, to: Swift.Error.self)");
            swiftWriter.WriteLine($"        }}");
            swiftWriter.WriteLine($"        return {resultBufName}");
            swiftWriter.WriteLine($"    }})");

            swiftWriter.WriteLine("}");
            swiftWriter.WriteLine();
        }

        // --- Void variant (T = Void, no resultBuf) ---
        if (emitVoidVariant)
        {
            var swiftParams = new List<string>();
            swiftParams.Add($"_ {csClosureName}FuncPtr: UnsafeMutableRawPointer?");
            swiftParams.Add($"_ {csClosureName}Context: UnsafeMutableRawPointer?");
            foreach (var p in nonClosureParams)
                swiftParams.Add($"_ {p.swiftName}: {p.swiftType}");
            if (isInstance)
                swiftParams.Add($"_ {selfParamName}: UnsafeMutableRawPointer");

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
            cdeclCallArgsFull.Add($"&{innerErrorName}");
            cdeclCallArgsFull.Add($"{csClosureName}Context");

            if (needsMainActor)
                swiftWriter.WriteLine("@MainActor");
            swiftWriter.WriteLine($"@_silgen_name(\"{voidSymbol}\")");
            swiftWriter.WriteLine($"public func {NameProvider.GetPInvokeName(methodDecl)}_XC_void(");
            swiftWriter.WriteLine($"    {string.Join(",\n    ", swiftParams)}");
            swiftWriter.WriteLine($"){throwsStr} {{");

            if (!string.IsNullOrEmpty(selfConversion))
                swiftWriter.WriteLine($"    {selfConversion}");

            swiftWriter.WriteLine($"    let {cdeclName} = unsafeBitCast({csClosureName}FuncPtr!, to: {cdeclTypeStr})");

            var closureOpening = $"{{ ({closureParamStr}){throwsInClosure} -> Void in";
            var callLine = fullCallArgs.Replace("__CLOSURE__", closureOpening);
            swiftWriter.WriteLine($"    {tryPrefix}{callTarget}.{NameProvider.ParserNameToSwift(methodDecl)}({callLine}");
            swiftWriter.WriteLine($"        var {innerErrorName}: UnsafeMutableRawPointer? = nil");
            swiftWriter.WriteLine($"        {cdeclName}({string.Join(", ", cdeclCallArgsFull)})");
            swiftWriter.WriteLine($"        if let {errName} = {innerErrorName} {{");
            swiftWriter.WriteLine($"            throw unsafeBitCast({errName}, to: Swift.Error.self)");
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

        // Resolve ALL closure arguments in declaration order. A generic type parameter in argument
        // position becomes a `T` surface type marshalled through a value-witness buffer pointer; a
        // concrete arg keeps its projected C# type. Every arg — generic or concrete — contributes one
        // void* to the Swift cdecl callback, so the C# callback must declare and forward one void* per
        // arg too (the historical skip-generics list under-counted them, the gate-(c) ABI mismatch).
        var closureArgs = new List<GcbClosureArg>();
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (arg is NamedTypeSpec named && TypeSpecHelpers.IsGenericTypeParameter(named.Name))
                closureArgs.Add(new GcbClosureArg(IsGeneric: true, arg, "T"));
            else
                closureArgs.Add(new GcbClosureArg(IsGeneric: false, arg, GetCSharpTypeForClosureArg(arg, env)));
        }

        // The void (T = Void) variant is invalid once T appears outside the closure return — see
        // EmitSwiftWrappers. Skip its callback, field, P/Invoke, and public overload entirely.
        bool emitVoidVariant = !TUsedOutsideClosureReturn(methodDecl, closureArg, closureTypeSpec);

        var callbackNameRet = $"GenericClosureBridge_{mangledHash}_Callback";
        var callbackNameVoid = $"GenericClosureBridge_{mangledHash}_VoidCallback";

        // --- Callbacks ---
        EmitReturningCallback(csWriter, closureArgs.Count, callbackNameRet, moduleName);
        if (emitVoidVariant)
            EmitVoidCallback(csWriter, closureArgs.Count, callbackNameVoid, moduleName);

        // --- Static callback pointer fields ---
        var retDelegateParts = BuildCallbackDelegateType(closureArgs.Count, hasResultBuf: true);
        csWriter.WriteLine($"private static readonly unsafe IntPtr s_{callbackNameRet} = (IntPtr)(delegate* unmanaged[Cdecl]<{retDelegateParts}>)&{callbackNameRet};");
        if (emitVoidVariant)
        {
            var voidDelegateParts = BuildCallbackDelegateType(closureArgs.Count, hasResultBuf: false);
            csWriter.WriteLine($"private static readonly unsafe IntPtr s_{callbackNameVoid} = (IntPtr)(delegate* unmanaged[Cdecl]<{voidDelegateParts}>)&{callbackNameVoid};");
        }
        csWriter.WriteLine();

        // --- P/Invoke declarations ---
        EmitCreateErrorPInvoke(csWriter, moduleName, asyncLibName, env, ctx);
        EmitErrorHelperPInvokes(csWriter, moduleName, asyncLibName, env, ctx);
        EmitPInvokeDeclarations(csWriter, pInvokeName, returningSymbol, voidSymbol, asyncLibName,
            methodDecl, env, closureArg, emitVoidVariant);

        // --- Public methods ---
        var classParent = parentDecl as ClassDecl;
        var selfExpr = classParent != null
            ? (classParent.IsObjCRooted ? "Handle" : "_handle.DangerousGetHandle()")
            : "_payload.DangerousGetHandle()";
        EmitPublicReturningMethod(csWriter, methodDecl, methodName, closureArgs,
            callbackNameRet, pInvokeName, csClosureName, closureTypeSpec, env, selfExpr, closureArg);
        if (emitVoidVariant)
            EmitPublicVoidMethod(csWriter, methodDecl, methodName, closureArgs,
                callbackNameVoid, pInvokeName, csClosureName, closureTypeSpec, env, selfExpr);
    }

    private static void EmitReturningCallback(
        CSharpWriter csWriter,
        int closureArgCount,
        string callbackName,
        string moduleName)
    {
        // Callback params: (closureArgs..., resultBuf, errorOut, context)
        // Each closure arg arrives as a separate void* from the Swift cdecl callback — generic args
        // included, so the count matches the Swift cdecl signature exactly (no ABI under-count).
        // The GCHandle stores object[] { Action<IntPtr[], IntPtr> } where the delegate
        // was created by the public generic method with T captured via closure.
        var paramParts = new List<string>();
        for (int i = 0; i < closureArgCount; i++)
            paramParts.Add($"void* arg{i}");
        paramParts.Add("void* resultBuf");
        paramParts.Add("void** errorOut");
        paramParts.Add("IntPtr contextPtr");

        csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
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
        if (closureArgCount > 0)
        {
            var argEntries = Enumerable.Range(0, closureArgCount)
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
        csWriter.WriteLine($"*errorOut = (void*)SBW_CreateError_{moduleName}(ex.Message, ex.GetType().FullName);");
        csWriter.Indent--;
        csWriter.WriteLine("}");

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    private static void EmitVoidCallback(
        CSharpWriter csWriter,
        int closureArgCount,
        string callbackName,
        string moduleName)
    {
        // Void callback: (closureArgs..., errorOut, context) — no resultBuf. Only emitted when T is
        // confined to the closure return (so every closure arg here is concrete; no generic args).
        var paramParts = new List<string>();
        for (int i = 0; i < closureArgCount; i++)
            paramParts.Add($"void* arg{i}");
        paramParts.Add("void** errorOut");
        paramParts.Add("IntPtr contextPtr");

        csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
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
        if (closureArgCount > 0)
        {
            var argEntries = Enumerable.Range(0, closureArgCount)
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
        csWriter.WriteLine($"*errorOut = (void*)SBW_CreateError_{moduleName}(ex.Message, ex.GetType().FullName);");
        csWriter.Indent--;
        csWriter.WriteLine("}");

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    private static void EmitCreateErrorPInvoke(CSharpWriter csWriter, string moduleName, string asyncLibName,
        MethodEnvironment env, ModuleEmissionContext ctx)
        => SwiftErrorMintEmitter.EmitPInvokeIfNeeded(csWriter, moduleName, asyncLibName, env, ctx);

    private static void EmitErrorHelperPInvokes(CSharpWriter csWriter, string moduleName, string asyncLibName,
        MethodEnvironment env, ModuleEmissionContext ctx)
    {
        var typeKey = (env.ParentDecl as TypeDecl)?.SwiftTypeName.ModuleQualifiedName ?? moduleName;
        ErrorDescriptionEmitter.EmitCSharpBaseErrorPInvokesIfNeeded(
            csWriter, typeKey, moduleName, asyncLibName,
            pInvokeHelperContext: null, ctx);
    }

    private static void EmitPInvokeDeclarations(
        CSharpWriter csWriter,
        string pInvokeName,
        string returningSymbol,
        string voidSymbol,
        string asyncLibName,
        MethodDecl methodDecl,
        MethodEnvironment env,
        ArgumentDecl closureArg,
        bool emitVoidVariant)
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
        if (!emitVoidVariant)
            return;
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
            var category = MethodClosureBridge.ClassifyParam(arg, env.TypeDatabase);
            switch (category)
            {
                case MethodClosureBridge.ParamAbiCategory.Primitive:
                    if (MarshallingHelpers.IsBoolType(arg.SwiftTypeSpec))
                        paramList.Add($"{MarshallingHelpers.BoolPInvokeParamAttribute} bool {csName}");
                    else
                        paramList.Add($"{MethodClosureBridge.GetPInvokePrimitiveType(arg.SwiftTypeSpec)} {csName}");
                    break;
                default:
                    paramList.Add($"IntPtr {csName}");
                    break;
            }
        }
        // The Swift @_silgen_name wrapper is a FREE function that takes the receiver as an
        // ordinary trailing parameter (`_ _self: UnsafeMutableRawPointer`), NOT as a `self`
        // parameter. Under the Swift calling convention an ordinary parameter lands in the next
        // regular GPR (x0..x7), whereas SwiftSelf is pinned to the self register (x20). Declaring
        // the receiver as `SwiftSelf self_` therefore passes the pointer in x20 while the wrapper
        // reads it from a regular GPR — the wrapper sees garbage and crashes. Pass it as a plain
        // IntPtr so it occupies the regular-GPR slot the wrapper expects. The `$s…` entry point
        // keeps the P/Invoke on CallConvSwift, so a throwing method's `out SwiftError` still maps
        // to the error register (x21).
        if (methodDecl.MethodType == MethodType.Instance)
            paramList.Add("IntPtr __self");
    }

    private static void EmitPublicReturningMethod(
        CSharpWriter csWriter,
        MethodDecl methodDecl,
        string methodName,
        List<GcbClosureArg> closureArgs,
        string callbackName,
        string pInvokeName,
        string csClosureName,
        ClosureTypeSpec closureTypeSpec,
        MethodEnvironment env,
        string selfExpr,
        ArgumentDecl closureArg)
    {
        // Build Func<ArgTypes..., T> type — a generic closure arg surfaces as `T`, a concrete arg as
        // its projected C# type, then the (generic) return `T`. For `(T) throws -> T` this is Func<T,T>.
        var funcTypeParams = closureArgs.Select(a => a.CSharpType).ToList();
        funcTypeParams.Add("T");
        var funcType = $"Func<{string.Join(", ", funcTypeParams)}>";

        // Generic non-closure parameters (`value: T`) marshalled through their own value-witness buffer.
        var genericValueParams = methodDecl.CSSignature.Skip(1)
            .Where(a => a != closureArg && IsGenericNonClosureParam(a))
            .Select(a => NameProvider.GetCSharpParameterName(a))
            .ToList();

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

        // Each generic non-closure value (`value: T`) is passed to the Swift wrapper as its own
        // value-witness buffer pointer. Declare the pointer + a liveness flag BEFORE the try so the
        // finally can release them; the alloc + +1-taking MarshalToSwift happen inside the try. The
        // flag flips true only after MarshalToSwift returns (the +1 is taken), mirroring resultSlotLive:
        // a throw before that leaves the flag false, so the finally frees the raw bytes without
        // Destroying a slot that holds no reference.
        foreach (var p in genericValueParams)
        {
            var bufName = GenericValueBufferName(p);
            csWriter.WriteLine($"void* {bufName} = null;");
            csWriter.WriteLine($"bool {bufName}Live = false;");
        }
        // The Swift callback writes the closure result (+1) into resultBuf during the P/Invoke; the
        // moved read below transfers that +1 to the returned wrapper. resultSlotLive tracks whether
        // an unconsumed +1 is currently in resultBuf: the callback sets it true the instant it
        // writes (so a Swift method that invokes the closure and THEN throws still releases the +1
        // on the error path), and the moved read clears it once the +1 is adopted. If the read
        // throws BEFORE adopting (MarshalMovedValueFromSlot leaves the slot intact on every throw
        // path, by contract — see its doc), the flag stays true, so the outer finally value-witness
        // Destroys before the raw AlignedFree, else the conformer / COW-storage +1 leaks. Mirrors
        // the SwiftArray/SwiftDictionary slotLive guard.
        csWriter.WriteLine("bool resultSlotLive = false;");
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Allocate + marshal each generic non-closure value (+1) into its buffer before the call.
        foreach (var p in genericValueParams)
        {
            var bufName = GenericValueBufferName(p);
            csWriter.WriteLine($"{bufName} = NativeMemory.AlignedAlloc(size, (nuint)metadata.Alignment);");
            csWriter.WriteLine($"var {bufName}Span = new Span<byte>({bufName}, (int)metadata.Size);");
            csWriter.WriteLine($"SwiftMarshal.MarshalToSwift({p}, ref {bufName}Span);");
            csWriter.WriteLine($"{bufName}Live = true;");
        }

        // Create the invoke delegate that captures T and the user's Func via closure.
        // The callback (non-generic, [UnmanagedCallersOnly]) extracts and calls this delegate.
        // Delegate signature: Action<IntPtr[], IntPtr> (argsArray, resultBufPtr)
        csWriter.WriteLine("Action<IntPtr[], IntPtr> invoke = (IntPtr[] args, IntPtr resBufPtr) =>");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        // Marshal each closure arg from IntPtr — callback params are borrowed references. A generic
        // arg's void* is the address of the (caller-owned) value buffer: read it back to T with an
        // independent +1 via the borrowed slot reader. A concrete class arg takes an owning +1
        // (MarshalBorrowedClassFromSwift) so the wrapper handed to the user's closure balances on
        // Dispose/finalize instead of over-releasing a borrowed handle.
        for (int i = 0; i < closureArgs.Count; i++)
        {
            if (closureArgs[i].IsGeneric)
                csWriter.WriteLine($"var a{i} = SwiftMarshal.MarshalBorrowedValueFromSlot<T>((void*)args[{i}], metadata);");
            else
                csWriter.WriteLine($"var a{i} = {env.ClosureHandler.BorrowedCallbackArgMarshal(closureArgs[i].Spec, closureArgs[i].CSharpType, $"args[{i}]")};");
        }
        var userCallArgs = Enumerable.Range(0, closureArgs.Count).Select(i => $"a{i}");
        csWriter.WriteLine($"var result = {csClosureName}({string.Join(", ", userCallArgs)});");
        csWriter.WriteLine("var resBufSpan = new Span<byte>((void*)resBufPtr, (int)metadata.Size);");
        // The Swift wrapper passes the SAME resultBuf to this callback (it is the outer method's
        // return slot, not a separate per-call buffer), so the closure writes its +1 directly into
        // it. Mark the slot live the moment that write completes — NOT after the post-P/Invoke error
        // check — because a Swift generic method can invoke the closure (populating resultBuf) and
        // THEN throw; if liveness were set only after the error check, that throw would exit first
        // and the outer finally would AlignedFree the buffer without releasing the +1 → leak. A
        // method that invokes the closure more than once reuses this one slot, and MarshalToSwift
        // treats the destination as raw storage (no destructor for prior bytes), so release any
        // already-written +1 before overwriting.
        csWriter.WriteLine("if (resultSlotLive) SwiftMarshal.DestroyWireBufferRetains(resBufPtr, metadata);");
        csWriter.WriteLine("SwiftMarshal.MarshalToSwift(result, ref resBufSpan);");
        csWriter.WriteLine("resultSlotLive = true;");
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
        AddNonClosurePInvokeCallArgs(callArgs, methodDecl, env.TypeDatabase);
        // Receiver passed as a regular IntPtr argument (regular GPR), matching the free-function
        // Swift wrapper — see AddNonClosureAndSelfParams for the self-register ABI rationale.
        if (methodDecl.MethodType == MethodType.Instance)
            callArgs.Add($"(IntPtr)({selfExpr})");
        if (methodDecl.Throws)
            callArgs.Add("out var swiftError");

        csWriter.WriteLine($"{pInvokeName}_XC({string.Join(", ", callArgs)});");

        if (methodDecl.Throws)
        {
            // Route the untyped Swift throw through the single source (SwiftMarshal.ThrowSwiftError) so
            // the thrown SwiftException carries the live error box on .ErrorHandle, identical to the
            // canonical method path — instead of eagerly releasing it and throwing a message-only,
            // identity-lossy SwiftRuntimeException. ThrowSwiftError reads + frees the description and
            // transfers ownership of the error box to the exception (released on finalization). The
            // surrounding try/finally blocks still run on the throw, freeing resultBuf and the GCHandle.
            csWriter.WriteLine("if (swiftError.Value != null)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("var _errorPtr = (IntPtr)swiftError.Value;");
            csWriter.WriteLine("global::Swift.Runtime.InteropServices.SwiftMarshal.ThrowSwiftError(_errorPtr, SBW_GetErrorDescription(_errorPtr), SBW_ReleaseError);");
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        // The callback wrote the closure result INTO resultBuf via MarshalToSwift, which for a
        // true Swift class stores the object pointer in the slot (and InitializeWithCopy took a
        // +1). resultBuf is therefore an initialized, owned value slot that we free RAW below
        // (AlignedFree, no value-witness Destroy). MarshalMovedValueFromSlot is the canonical
        // reader for exactly that contract: it dereferences the slot for a true class (the buffer
        // ADDRESS is not the object pointer — *(IntPtr*)slot is), reads bytes directly for POD
        // value types, and copy-then-Destroys for non-POD structs — transferring the slot's +1 to
        // the returned wrapper in every case. The old MarshalFromSwift<T>(new IntPtr(resultBuf))
        // passed the buffer address as if it were the value/object pointer, which crashed for
        // class T (it treated the buffer address as the instance, then freed it).
        // resultSlotLive was set by the callback (above) the instant it wrote a +1 into resultBuf,
        // so a Swift method that invoked the closure and THEN threw already has the slot marked live
        // and the error path's outer finally releases it — the old "set live here, after the error
        // check" shape leaked that +1 because the throw exited before reaching this point. Clear it
        // immediately after the moved read adopts the +1; a throw from MarshalMovedValueFromSlot
        // leaves it true, so the outer finally releases the intact slot's +1 instead of leaking it.
        csWriter.WriteLine("var __movedResult = SwiftMarshal.MarshalMovedValueFromSlot<T>(resultBuf, metadata);");
        csWriter.WriteLine("resultSlotLive = false;");
        csWriter.WriteLine("return __movedResult;");

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("finally { if (gcHandle.IsAllocated) gcHandle.Free(); }");

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("finally");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        // Release each generic non-closure value buffer: the +1 taken by MarshalToSwift is balanced by
        // the closure's borrowed read (which took its own independent +1), so destroy the buffer's +1
        // here, then free the raw bytes. Guarded by the per-buffer liveness flag so a throw before the
        // +1 was taken only frees raw memory.
        foreach (var p in genericValueParams)
        {
            var bufName = GenericValueBufferName(p);
            csWriter.WriteLine($"if ({bufName}Live) SwiftMarshal.DestroyWireBufferRetains((IntPtr){bufName}, metadata);");
            csWriter.WriteLine($"if ({bufName} != null) NativeMemory.AlignedFree({bufName});");
        }
        // A throw between the callback's +1 write and the moved read (or from the moved read itself)
        // leaves an unconsumed +1 in resultBuf; release it via the non-generic, Mono-safe wire-buffer
        // destroy (no fresh generic instantiation forced inside a finally) before the raw free.
        csWriter.WriteLine("if (resultSlotLive) SwiftMarshal.DestroyWireBufferRetains((IntPtr)resultBuf, metadata);");
        csWriter.WriteLine("NativeMemory.AlignedFree(resultBuf);");
        csWriter.Indent--;
        csWriter.WriteLine("}");

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    private static void EmitPublicVoidMethod(
        CSharpWriter csWriter,
        MethodDecl methodDecl,
        string methodName,
        List<GcbClosureArg> closureArgs,
        string callbackName,
        string pInvokeName,
        string csClosureName,
        ClosureTypeSpec closureTypeSpec,
        MethodEnvironment env,
        string selfExpr)
    {
        // The void variant is only emitted when T is confined to the closure return, so every closure
        // arg here is concrete (no generic args) and no generic non-closure params exist.
        var actionType = closureArgs.Count > 0
            ? $"Action<{string.Join(", ", closureArgs.Select(a => a.CSharpType))}>"
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
        // Callback params are borrowed references. Class args take an owning +1 so the wrapper handed
        // to the user's closure balances on Dispose/finalize — see BorrowedCallbackArgMarshal.
        for (int i = 0; i < closureArgs.Count; i++)
            csWriter.WriteLine($"var a{i} = {env.ClosureHandler.BorrowedCallbackArgMarshal(closureArgs[i].Spec, closureArgs[i].CSharpType, $"args[{i}]")};");
        var userCallArgs = Enumerable.Range(0, closureArgs.Count).Select(i => $"a{i}");
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
        AddNonClosurePInvokeCallArgs(callArgs, methodDecl, env.TypeDatabase);
        // Receiver passed as a regular IntPtr argument (regular GPR), matching the free-function
        // Swift wrapper — see AddNonClosureAndSelfParams for the self-register ABI rationale.
        if (methodDecl.MethodType == MethodType.Instance)
            callArgs.Add($"(IntPtr)({selfExpr})");
        if (methodDecl.Throws)
            callArgs.Add("out var swiftError");

        csWriter.WriteLine($"{pInvokeName}_XC_void({string.Join(", ", callArgs)});");

        if (methodDecl.Throws)
        {
            // Route the untyped Swift throw through the single source (SwiftMarshal.ThrowSwiftError) so
            // the thrown SwiftException carries the live error box on .ErrorHandle, identical to the
            // canonical method path — instead of eagerly releasing it and throwing a message-only,
            // identity-lossy SwiftRuntimeException. The surrounding finally still frees the GCHandle.
            csWriter.WriteLine("if (swiftError.Value != null)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("var _errorPtr = (IntPtr)swiftError.Value;");
            csWriter.WriteLine("global::Swift.Runtime.InteropServices.SwiftMarshal.ThrowSwiftError(_errorPtr, SBW_GetErrorDescription(_errorPtr), SBW_ReleaseError);");
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
            // A generic non-closure param (`value: T`) surfaces as the method's own `T` type parameter.
            var csType = IsGenericNonClosureParam(arg) ? "T" : GetPublicParamType(arg, env);
            publicParams.Add($"{csType} {csName}");
        }
    }

    /// <summary>
    /// Adds non-closure arguments to the P/Invoke call args list with proper marshalling.
    /// Skips closure arguments (both bare ClosureTypeSpec and Optional-wrapped closures).
    /// </summary>
    private static void AddNonClosurePInvokeCallArgs(
        List<string> callArgs,
        MethodDecl methodDecl,
        ITypeDatabase typeDatabase)
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
            // A generic non-closure param is passed as the value-witness buffer pointer the returning
            // method allocated and marshalled into (the void overload, which never has a generic
            // non-closure param, never reaches this branch). See GenericValueBufferName.
            if (IsGenericNonClosureParam(arg))
            {
                callArgs.Add($"(IntPtr){GenericValueBufferName(csName)}");
                continue;
            }
            var category = MethodClosureBridge.ClassifyParam(arg, typeDatabase);
            switch (category)
            {
                case MethodClosureBridge.ParamAbiCategory.ObjCHandle:
                    callArgs.Add($"{csName}.Handle");
                    break;
                case MethodClosureBridge.ParamAbiCategory.PayloadHandle:
                    callArgs.Add($"{csName}.Payload.DangerousGetHandle()");
                    break;
                default:
                    callArgs.Add(csName);
                    break;
            }
        }
    }

    /// <summary>
    /// The local name of the value-witness buffer the returning method allocates for a generic
    /// non-closure parameter named <paramref name="csName"/>. Distinct from the public parameter name
    /// (the <c>T</c> wrapper) so both can coexist in the method body.
    /// </summary>
    private static string GenericValueBufferName(string csName) => $"{csName}__valueBuf";

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

    private static string BuildCallbackDelegateType(int closureArgCount, bool hasResultBuf)
    {
        var parts = new List<string>();
        for (int i = 0; i < closureArgCount; i++)
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
