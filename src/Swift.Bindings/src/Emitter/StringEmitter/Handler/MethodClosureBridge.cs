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
        int Index,
        bool IsOptional,
        bool IsEffectivelyEscaping);

    /// <summary>
    /// Builds the wrapper symbol name for an MCB bridge. Uses the <c>SBSW_</c> prefix when the
    /// Swift wrapper must be a generic-extension <c>@_silgen_name</c> (generic parent + instance
    /// method — <c>@_cdecl</c> is illegal on a generic extension method, so the Swift CC is the
    /// only legal pairing). Uses the regular <c>SBW_</c> prefix otherwise, which signals the
    /// matching <c>@_cdecl</c> + <c>CallConvCdecl</c> P/Invoke. The distinct prefix keeps the
    /// (entry-point → calling-convention) pairing self-describing: <c>SBW_</c> ↔ Cdecl,
    /// <c>SBSW_</c> ↔ Swift, enforced centrally by <see cref="PInvokeEmitHelper.SelectCallingConvention"/>.
    /// </summary>
    private static string BuildBridgeSymbolName(MethodDecl method, TypeDecl? parentDecl, string callbackBaseName)
    {
        bool isInstance = method.MethodType != MethodType.Static && parentDecl != null;
        bool isGenericParent = parentDecl is TypeDecl ptd && ptd.IsGeneric;
        var prefix = (isGenericParent && isInstance) ? "SBSW_" : "SBW_";
        return $"{prefix}{callbackBaseName}_{method.Name}";
    }

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

        // Collect ALL closure parameters — require at least one with bound generic, complex enum,
        // or any Swift.Error existential arg to activate MCB
        var closureArgs = new List<(ClosureTypeSpec spec, ArgumentDecl arg)>();
        bool hasBoundGenericInClosure = false;
        bool hasComplexEnumInClosure = false;
        bool hasErrorExistentialInClosure = false;

        foreach (var arg in method.CSSignature.Skip(1))
        {
            var cts = closureHandler.GetClosureTypeSpec(arg);
            if (cts != null)
            {
                // Check if closure has async — not supported
                if (cts.IsAsync) return false;

                // Check closure args for bound generic types, complex enums, and any Error existentials
                int optionalAnyErrorArgsInThisClosure = 0;
                foreach (var closureArgType in cts.EachArgument())
                {
                    if (IsBoundGenericClosureArg(closureArgType))
                        hasBoundGenericInClosure = true;

                    if (closureHandler.IsComplexEnum(closureArgType))
                        hasComplexEnumInClosure = true;

                    if (IsAnyErrorExistential(closureArgType) ||
                        IsOptionalAnyErrorExistential(closureArgType) ||
                        IsSwiftResultWithAnyErrorFailure(closureArgType))
                        hasErrorExistentialInClosure = true;

                    if (IsOptionalAnyErrorExistential(closureArgType))
                        optionalAnyErrorArgsInThisClosure++;

                    if (!IsClosureArgSupported(closureArgType, typeDatabase))
                        return false;
                }

                // The Swift body emitter only supports one Optional<any Error> arg per closure
                // (single if-let branch). Multiple would require nested/composed branching that
                // isn't implemented — reject at the gate rather than crash during emission.
                if (optionalAnyErrorArgsInThisClosure > 1)
                    return false;

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

        // Key gate: ONLY activate when at least one closure arg is a bound generic type,
        // a complex enum (D1: heap allocation), or an any Swift.Error existential (pointer ABI via
        // ExistentialContainer1). Otherwise the normal @_cdecl path handles it.
        if (!hasBoundGenericInClosure && !hasComplexEnumInClosure && !hasErrorExistentialInClosure)
            return false;

        // Generic parent types: instance methods use @_silgen_name extension (inherits
        // generic context) + CallConvSwift/SwiftSelf on C# side. Static methods on generic
        // types are still blocked — they require type metadata passing which is complex.
        if (method.ParentDecl is TypeDecl parentTd && parentTd.IsGeneric &&
            method.MethodType == MethodType.Static)
            return false;

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
                // Always use indexed naming (MCB_{hash}_0, _1, …) so adding a closure later
                // doesn't silently rename the first symbol from bare MCB_{hash} to MCB_{hash}_0.
                var cbName = $"MCB_{mangledHash}_{closureIndex}";
                // Optional<Closure>: delegate is nullable on C# side; Swift adapter must
                // pass nil to the target method when funcPtr is nil. Per constraints.md,
                // Optional closures are always escaping (GCHandle still leaked on non-nil path).
                var isOptional = env.ClosureHandler.IsOptionalClosure(arg.SwiftTypeSpec);
                // Escaping (or Optional<closure>, which is always escaping per constraints.md)
                // closures get a Swift-ARC owner-token box around the GCHandle context so
                // the GCHandle is freed when Swift releases the closure (Bug 1 Cat 3 / Bug 3 Case 2).
                var isEffectivelyEscaping = WrapperValidation.IsEffectivelyEscaping(
                    cts, arg.SwiftTypeSpec, env.ClosureHandler);

                closures.Add(new ClosureInfo(cts, arg, cArgs, retIsVoid, cbName, paramName, closureIndex, isOptional, isEffectivelyEscaping));
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

        // Emit closure-context owner-token helpers if any closure is escaping.
        // Idempotent per module — first MCB site that needs them emits, others no-op.
        if (closures.Any(c => c.IsEffectivelyEscaping))
            ClosureContextHelperEmitter.EmitIfNeeded(swiftWriter, ctx);

        // Register the wrapper symbol with the wrapper-symbol contract before emitting
        // the Swift wrapper. EmitSwiftWrapper writes either @_cdecl (non-generic parent,
        // SBW_ prefix, Cdecl P/Invoke) or @_silgen_name (generic parent, SBSW_ prefix,
        // Swift CC P/Invoke). Distinct prefixes keep the (entry-point → calling-convention)
        // pairing self-describing and let PInvokeEmitHelper.SelectCallingConvention enforce
        // it at construction time. The contract check fires for both pairings now
        // (Cdecl + SBW_ and Swift + SBSW_), so both branches must register or the
        // matching P/Invoke would throw WrapperSymbolContractException.
        var bridgeSilgenName = BuildBridgeSymbolName(method, parentDecl, closures[0].CallbackBaseName);
        // S5 audited (Tier B): the bridge symbol is keyed on the closure type's
        // CallbackBaseName, not the method's own mangled name. That namespace is owned
        // exclusively by MethodClosureBridge / NestedClosureBridge and cannot collide
        // with any method/property/constructor wrapper. Per-kind method bucket is safe.
        ctx.TryAddMethodWrapperSymbol(bridgeSilgenName);

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

    /// <summary>
    /// The collision-guarded synthetic Swift identifiers used across the @_cdecl wrapper:
    /// the explicit <c>self</c> pointer param, the <c>selfObj</c> reconstruction local, and
    /// per-closure <c>cdecl</c> / <c>_box</c> names keyed by closure index.
    /// </summary>
    private readonly record struct ClosureBridgeSyntheticNames(
        string SelfParam,
        string SelfObj,
        IReadOnlyDictionary<int, string> Cdecl,
        IReadOnlyDictionary<int, string> Box,
        IReadOnlyDictionary<int, string> Adapter);

    /// <summary>
    /// P1-22 (C1): the @_cdecl wrapper hardcodes synthetic Swift identifiers (<c>self_</c>,
    /// <c>selfObj</c>, per-closure <c>cdecl</c>/<c>cdecl{N}</c> and <c>_box_{N}</c>). A user
    /// param spelled the same — e.g. <c>func run(self_: Int, _ cb: …)</c> — would otherwise
    /// produce an "invalid redeclaration" and the generator would emit broken Swift at
    /// exit 0. Reserve every synthetic through a <see cref="SyntheticNameScope"/> seeded with
    /// the user-controlled identifiers in this wrapper's scope (non-closure param names +
    /// each closure's <c>FuncPtr</c>/<c>Context</c> params): collision-free input yields the
    /// original names verbatim, collisions get a <c>__</c>-prefixed variant.
    ///
    /// This is a PURE function of its inputs so both emission paths (EmitSwiftWrapper and the
    /// call-body emitter EmitSwiftMultiClosureWithPointerWrapping, which write into the same
    /// Swift function scope) derive the identical mapping without threading names by param.
    /// </summary>
    private static ClosureBridgeSyntheticNames ComputeSyntheticNames(
        List<ClosureInfo> closures,
        List<(ArgumentDecl arg, string csName, string csType, ParamAbiCategory category)> passableNonClosureParams)
    {
        var reserved = new List<string>();
        foreach (var (_, csName, _, _) in passableNonClosureParams)
            reserved.Add(NameProvider.StripVerbatimPrefix(csName));
        foreach (var ci in closures)
        {
            var n = NameProvider.StripVerbatimPrefix(ci.ParamName);
            reserved.Add($"{n}FuncPtr");
            reserved.Add($"{n}Context");
        }

        var scope = new SyntheticNameScope(reserved);
        var selfParam = scope.Reserve("self_");
        var selfObj = scope.Reserve("selfObj");
        var cdecl = new Dictionary<int, string>();
        var box = new Dictionary<int, string>();
        var adapter = new Dictionary<int, string>();
        foreach (var ci in closures)
        {
            cdecl[ci.Index] = scope.Reserve(closures.Count > 1 ? $"cdecl{ci.Index}" : "cdecl");
            box[ci.Index] = scope.Reserve($"_box_{ci.Index}");
            // The pointer-wrapping call body declares a `let __adapter{N}` local in this same
            // scope; reserve it so a user non-closure param spelled `__adapter{N}` forces the
            // synthetic to a `__`-escaped variant rather than producing an invalid Swift
            // redeclaration emitted at exit 0.
            adapter[ci.Index] = scope.Reserve($"__adapter{ci.Index}");
        }

        return new ClosureBridgeSyntheticNames(selfParam, selfObj, cdecl, box, adapter);
    }

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

        // Generic parent types use @_silgen_name extension to inherit generic context.
        // Non-generic types use @_cdecl free function with explicit self parameter.
        bool isGenericParent = parentDecl is TypeDecl ptd && ptd.IsGeneric;

        // SBW_ ↔ @_cdecl (Cdecl), SBSW_ ↔ @_silgen_name (Swift CC) — prefix selected by
        // BuildBridgeSymbolName so the (symbol → CC) pairing is self-describing and the
        // central PInvokeEmitHelper.SelectCallingConvention check stays an identity.
        var silgenName = BuildBridgeSymbolName(method, parentDecl, closures[0].CallbackBaseName);

        // Build Swift wrapper params
        var swiftParams = new List<string>();
        // Track non-primitive params that need pointer-to-value loading inside the body.
        // isClass distinguishes Swift classes (Unmanaged unwrap) from non-frozen structs (.pointee load).
        var pointerLoadParams = new List<(string paramName, string swiftType, bool isClass)>();
        // Utf8Slice params reconstructed from (ptr, len) into a Swift.String before the body call.
        var utf8SliceParams = new List<string>();

        // Non-closure passable params first.
        // For @_cdecl, non-primitive types (PayloadHandle) must be passed as UnsafeRawPointer
        // and loaded inside the function body (Swift structs/classes aren't C-representable).
        foreach (var (arg, csName, _, category) in passableNonClosureParams)
        {
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
            var paramName = NameProvider.EscapeSwiftKeyword(csName);
            if (category == ParamAbiCategory.PayloadHandle)
            {
                swiftParams.Add($"    _ {paramName}: UnsafeRawPointer");
                // Classes need Unmanaged unwrap; non-frozen structs need .pointee load
                bool isClass = arg.SwiftTypeSpec is NamedTypeSpec named &&
                    IsClassTypeForSwift(named, env.TypeDatabase);
                pointerLoadParams.Add((paramName, swiftType, isClass));
            }
            else if (category == ParamAbiCategory.Utf8Slice)
            {
                swiftParams.Add($"    _ {paramName}Utf8Ptr: UnsafePointer<UInt8>");
                swiftParams.Add($"    _ {paramName}Utf8Len: Int");
                utf8SliceParams.Add(paramName);
            }
            else
            {
                swiftParams.Add($"    _ {paramName}: {swiftType}");
            }
        }

        // Each closure → funcPtr + context pair
        foreach (var ci in closures)
        {
            var closureCsName = NameProvider.StripVerbatimPrefix(ci.ParamName);
            swiftParams.Add($"    _ {closureCsName}FuncPtr: UnsafeMutableRawPointer?");
            swiftParams.Add($"    _ {closureCsName}Context: UnsafeMutableRawPointer?");
        }

        // P1-22 (C1): the @_cdecl wrapper hardcodes synthetic Swift identifiers
        // (`self_`, `selfObj`, per-closure `cdecl`/`cdecl{N}` and `_box_{N}`). A user
        // param spelled the same (`func run(self_: Int, _ cb: …)`) would produce an
        // `invalid redeclaration` and the generator would emit broken Swift at exit 0.
        // ComputeSyntheticNames reserves each synthetic through a scope seeded with the
        // user-controlled identifiers: collision-free input yields the original names
        // verbatim, collisions get a `__`-prefixed variant. The call-body emitter
        // (EmitSwiftMultiClosureWithPointerWrapping) emits into the SAME Swift function
        // scope, so it derives the identical mapping from the same inputs rather than
        // taking the names by parameter.
        var synth = ComputeSyntheticNames(closures, passableNonClosureParams);
        var selfParamName = synth.SelfParam;
        var selfObjName = synth.SelfObj;
        var cdeclNames = synth.Cdecl;
        var boxNames = synth.Box;

        // Build return type — non-primitive returns use UnsafeMutableRawPointer
        var returnSpec = method.CSSignature[0].SwiftTypeSpec;
        bool returnsValue = !returnSpec.IsEmptyTuple;
        bool returnsClass = returnsValue && returnSpec is NamedTypeSpec rts &&
            !MarshallingHelpers.IsSwiftPrimitive(rts.Name);
        var swiftReturnType = !returnsValue ? ""
            : returnsClass ? " -> UnsafeMutableRawPointer"
            : $" -> {ExistentialBypassEmitter.RenderSwiftTypeSpecForReturnType(returnSpec)}";

        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            parentDecl, method.IsMainActorIsolated, method.IsNonisolated);
        var availability = WrapperEmitterHelpers.MergeAvailability(method.AvailabilityAnnotations, parentDecl);

        if (isGenericParent && isInstance)
        {
            // Generic parent: emit @_silgen_name extension method.
            // Self is implicit (in x20 register via Swift calling convention).
            // C# P/Invoke uses CallConvSwift + SwiftSelf to match.
            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);
            if (needsMainActor)
                swiftWriter.WriteLine("@MainActor");
            swiftWriter.WriteLine($"extension {typeName} {{");
            swiftWriter.WriteLine($"@_silgen_name(\"{silgenName}\")");
            swiftWriter.WriteLine($"func _sbw_mcb_{closures[0].CallbackBaseName}_{method.Name}(");
            swiftWriter.WriteLine(string.Join(",\n", swiftParams));
            swiftWriter.WriteLine($"){swiftReturnType} {{");
        }
        else
        {
            // Non-generic parent: emit @_cdecl free function with explicit self parameter.
            if (isInstance)
            {
                swiftParams.Add($"    _ {selfParamName}: UnsafeMutableRawPointer");
            }
            WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, silgenName, needsMainActor, availability);
            swiftWriter.WriteLine($"public func _sbw_mcb_{closures[0].CallbackBaseName}_{method.Name}(");
            swiftWriter.WriteLine(string.Join(",\n", swiftParams));
            swiftWriter.WriteLine($"){swiftReturnType} {{");
        }

        // Load non-primitive params from UnsafeRawPointer.
        // Classes: the pointer IS the object reference — use Unmanaged to recover it.
        // Non-frozen structs: the pointer points to value storage — load via .pointee.
        foreach (var (paramName, swiftType, isClass) in pointerLoadParams)
        {
            if (isClass)
                swiftWriter.WriteLine($"    let {paramName}Val = Unmanaged<{swiftType}>.fromOpaque({paramName}).takeUnretainedValue()");
            else
                swiftWriter.WriteLine($"    let {paramName}Val = {paramName}.assumingMemoryBound(to: {swiftType}.self).pointee");
        }

        // Reconstruct Swift.String from (UTF-8 byte pointer, length) pair passed by C# via `fixed`.
        foreach (var paramName in utf8SliceParams)
        {
            swiftWriter.WriteLine(
                $"    let {paramName}Val = String(bytes: UnsafeBufferPointer(start: {paramName}Utf8Ptr, count: {paramName}Utf8Len), encoding: .utf8)!");
        }

        // Reconstruct cdecl functions from pointers — one per non-Optional closure.
        // Optional closures defer cdecl reconstruction to inside their `.map` adapter so
        // we never force-unwrap a nil funcPtr when the caller passed null.
        // For escaping non-Optional closures, also wrap the GCHandle context in a Swift
        // ARC-owned `_SBClosureCtx` box (Bug 1 Cat 3 / Bug 3 Case 2). The adapter closure
        // captures `_box_X` via its capture list so the box's lifetime tracks the closure's;
        // when Swift releases the closure, the box's deinit upcalls the C# free callback.
        foreach (var ci in closures)
        {
            if (ci.IsOptional) continue;

            var closureCsName = NameProvider.StripVerbatimPrefix(ci.ParamName);
            var cdeclParamTypes = new List<string>();
            for (int i = 0; i < ci.ClosureArgs.Count; i++)
            {
                cdeclParamTypes.Add(GetSwiftCdeclParamType(ci.ClosureArgs[i], env));
            }
            cdeclParamTypes.Add("UnsafeMutableRawPointer?"); // context
            var cdeclReturnType = ci.Spec.ReturnType.IsEmptyTuple ? "Void" : "UInt8";
            var cdeclType = $"(@convention(c) ({string.Join(", ", cdeclParamTypes)}) -> {cdeclReturnType}).self";
            var cdeclVarName = cdeclNames[ci.Index];
            swiftWriter.WriteLine($"    let {cdeclVarName} = unsafeBitCast({closureCsName}FuncPtr!, to: {cdeclType})");

            if (ci.IsEffectivelyEscaping)
            {
                swiftWriter.WriteLine($"    let {boxNames[ci.Index]}: AnyObject = {ClosureContextHelperEmitter.WrapFunctionName}({closureCsName}Context!)");
            }
        }

        // For non-generic parents, unwrap self from the explicit pointer parameter.
        // For generic parents, self is implicit from the extension.
        bool isMutatingValueType = isInstance && !(parentDecl is ClassDecl) && method.IsMutating;
        if (isInstance && !isGenericParent && !isMutatingValueType)
        {
            bool isClassParent = parentDecl is ClassDecl;
            if (isClassParent)
                swiftWriter.WriteLine($"    let {selfObjName} = Unmanaged<{typeName}>.fromOpaque({selfParamName}).takeUnretainedValue()");
            else
                swiftWriter.WriteLine($"    let {selfObjName} = {selfParamName}.assumingMemoryBound(to: {typeName}.self).pointee");
        }

        // Build original method call arguments in parameter order
        // Class returns need Unmanaged.passRetained().toOpaque() to return as UnsafeMutableRawPointer
        var returnPrefix = returnsClass ? "return Unmanaged.passRetained("
            : returnsValue ? "return "
            : "";
        var returnSuffix = returnsClass ? ").toOpaque()" : "";
        string callTarget;
        if (isGenericParent && isInstance)
            callTarget = "self"; // Extension method: self is implicit
        else if (isMutatingValueType)
            callTarget = $"{selfParamName}.assumingMemoryBound(to: {typeName}.self).pointee";
        else if (isInstance)
            callTarget = selfObjName;
        else
            callTarget = typeName;

        // Collect all method call args in parameter order, interleaving non-closure and closure args
        var methodCallArgs = new List<string>();
        var closureArgSet = new HashSet<ArgumentDecl>(closures.Select(c => c.Arg));
        var closureByArg = closures.ToDictionary(c => c.Arg);
        var passableByArg = passableNonClosureParams.ToDictionary(p => p.arg);

        // Track whether any closure needs withUnsafePointer wrapping or heap allocation
        bool anyClosureNeedsComplexPath = false;
        var perClosureAnalysis = new Dictionary<ClosureInfo, (List<string> paramDecls, List<(int index, string swiftType)> pointerWrapArgs, List<(int index, string conversion)> directArgs, List<(int index, string swiftType)> heapAllocArgs, List<(int index, string swiftType)> optionalExistentialArgs)>();

        foreach (var ci in closures)
        {
            var paramDecls = new List<string>();
            var pointerWrapArgs = new List<(int index, string swiftType)>();
            var directArgs = new List<(int index, string conversion)>();
            var heapAllocArgs = new List<(int index, string swiftType)>();
            var optionalExistentialArgs = new List<(int index, string swiftType)>();

            for (int i = 0; i < ci.ClosureArgs.Count; i++)
            {
                var argType = ci.ClosureArgs[i];
                var paramName = $"__p{ci.Index}_{i}";
                paramDecls.Add(paramName);

                // Optional<any Error> needs if-let branching (nil passes IntPtr.Zero to cdecl,
                // non-nil wraps the container with withUnsafePointer). Route to its own bucket
                // so the body emitter can split branches cleanly.
                if (IsOptionalAnyErrorExistential(argType))
                {
                    optionalExistentialArgs.Add((i, ExistentialBypassEmitter.RenderSwiftTypeSpec(argType)));
                }
                else if (argType is NamedTypeSpec named)
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
                    else if (IsOptionalClassArg(argType, env))
                    {
                        // Optional<Class/ObjC>: nil-propagate via `?.map`. nil stays nil (IntPtr.Zero
                        // on the C# side); non-nil becomes a borrowed opaque pointer.
                        directArgs.Add((i, $"{paramName}.map {{ Unmanaged.passUnretained($0).toOpaque() }}"));
                    }
                    else if (env.ClosureHandler.IsComplexEnum(argType))
                    {
                        // D1: Complex enums use heap allocation — C# takes ownership via SwiftSafeHandle
                        heapAllocArgs.Add((i, ExistentialBypassEmitter.RenderSwiftTypeSpec(argType)));
                    }
                    else if (env.ClosureHandler.GetSimpleEnumInfo(argType) is { hasRawValue: true } enumInfo)
                    {
                        // Simple enums with numeric raw values marshal as their underlying scalar.
                        // Swift's raw value type (e.g., `Int`) and the cdecl scalar (e.g., `Int64`)
                        // are nominally distinct, so wrap in an explicit conversion to satisfy the
                        // @convention(c) signature.
                        directArgs.Add((i, $"{enumInfo.swiftScalar}({paramName}.rawValue)"));
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

            if (pointerWrapArgs.Count > 0 || heapAllocArgs.Count > 0 || optionalExistentialArgs.Count > 0)
                anyClosureNeedsComplexPath = true;
            // Optional closures need a let-bound adapter (Optional type annotation required
            // for Swift type inference + .map-based nil handling), so always take the
            // complex-path emission.
            if (ci.IsOptional)
                anyClosureNeedsComplexPath = true;
            perClosureAnalysis[ci] = (paramDecls, pointerWrapArgs, directArgs, heapAllocArgs, optionalExistentialArgs);
        }

        if (anyClosureNeedsComplexPath)
        {
            // Complex path: at least one closure has value-type args needing withUnsafePointer or heap allocation
            EmitSwiftMultiClosureWithPointerWrapping(swiftWriter, method, env, closures,
                passableNonClosureParams, perClosureAnalysis, returnPrefix, returnSuffix, callTarget);
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

                    var cdeclVarName = cdeclNames[ci.Index];
                    var cdeclCall = $"{cdeclVarName}({string.Join(", ", cdeclCallArgs)})";
                    if (!ci.Spec.ReturnType.IsEmptyTuple)
                        cdeclCall += " != 0";

                    // Escaping closures explicitly capture the owner-token box so its lifetime
                    // tracks the stored closure (Bug 1 Cat 3 / Bug 3 Case 2).
                    var captureList = ci.IsEffectivelyEscaping ? $"[{boxNames[ci.Index]}] " : "";
                    var observeBox = ci.IsEffectivelyEscaping ? $"_ = {boxNames[ci.Index]}; " : "";
                    var closureParamStr = string.Join(", ", analysis.paramDecls);
                    var callLabel = GetSwiftArgLabel(ci.Arg);
                    var closureBody = analysis.paramDecls.Count > 0
                        ? $"{{ {captureList}{closureParamStr} in {observeBox}{cdeclCall} }}"
                        : ci.IsEffectivelyEscaping
                            ? $"{{ {captureList}in {observeBox}{cdeclCall} }}"
                            : $"{{ {cdeclCall} }}";
                    allCallArgs.Add($"{callLabel}{closureBody}");
                }
                else if (passableByArg.TryGetValue(arg, out var passable))
                {
                    var label = GetSwiftArgLabel(passable.arg);
                    var paramName = NameProvider.EscapeSwiftKeyword(passable.csName);
                    // PayloadHandle/Utf8Slice params are reconstructed into a local → use {name}Val
                    var valSuffix = passable.category is ParamAbiCategory.PayloadHandle
                        or ParamAbiCategory.Utf8Slice ? "Val" : "";
                    allCallArgs.Add($"{label}{paramName}{valSuffix}");
                }
            }

            swiftWriter.WriteLine($"    {returnPrefix}{callTarget}.{NameProvider.ParserNameToSwift(method)}({string.Join(", ", allCallArgs)}){returnSuffix}");
        }

        swiftWriter.WriteLine("}");
        // Close extension block for generic parent types
        if (isGenericParent && isInstance)
            swiftWriter.WriteLine("}");
        swiftWriter.WriteLine();
    }

    /// <summary>
    /// Emits the method call body when at least one closure has value-type args needing withUnsafePointer.
    /// Handles N closures with interleaved non-closure params.
    /// </summary>
    private static void EmitSwiftMultiClosureWithPointerWrapping(
        SwiftWriter swiftWriter,
        MethodDecl method,
        MethodEnvironment env,
        List<ClosureInfo> closures,
        List<(ArgumentDecl arg, string csName, string csType, ParamAbiCategory category)> passableNonClosureParams,
        Dictionary<ClosureInfo, (List<string> paramDecls, List<(int index, string swiftType)> pointerWrapArgs, List<(int index, string conversion)> directArgs, List<(int index, string swiftType)> heapAllocArgs, List<(int index, string swiftType)> optionalExistentialArgs)> perClosureAnalysis,
        string returnPrefix,
        string returnSuffix,
        string callTarget)
    {
        // For the pointer wrapping path, we build let-bindings for each closure adapter,
        // then emit the method call with all args.
        var indent = "    ";
        var closureArgSet = new HashSet<ArgumentDecl>(closures.Select(c => c.Arg));
        var closureByArg = closures.ToDictionary(c => c.Arg);
        var passableByArg = passableNonClosureParams.ToDictionary(p => p.arg);

        // P1-22 (C1): this emits into the SAME Swift function scope as EmitSwiftWrapper, so
        // it MUST derive the identical synthetic-name mapping. ComputeSyntheticNames is a
        // pure function of (closures, passableNonClosureParams) — the same inputs both
        // methods receive — so the `cdecl`/`_box_N` names here match the box declarations
        // emitted by the caller even when a user param forces a `__`-escaped variant.
        var synth = ComputeSyntheticNames(closures, passableNonClosureParams);

        // For each closure that has pointer-wrap args, we need withUnsafePointer nesting.
        // Strategy: emit each closure adapter as a local closure variable, then call the method.
        // This avoids deeply nested trailing closure syntax which doesn't work with multiple closures.
        foreach (var ci in closures)
        {
            var analysis = perClosureAnalysis[ci];
            var closureCsName = NameProvider.StripVerbatimPrefix(ci.ParamName);
            var cdeclVarName = synth.Cdecl[ci.Index];
            var adapterName = synth.Adapter[ci.Index];

            // Build the closure adapter type signature. Existentials (`any Error`) must
            // render with the `any` keyword so Swift 6 accepts the closure signature —
            // RenderSwiftTypeSpec alone just emits "Error", which is rejected.
            var swiftParamTypes = ci.ClosureArgs.Select(RenderSwiftClosureArgType).ToList();
            var swiftRetType = ci.Spec.ReturnType.IsEmptyTuple ? "Void" : "Swift.Bool";
            var closureType = $"({string.Join(", ", swiftParamTypes)}) -> {swiftRetType}";
            // Optional closures: wrap the adapter body in `.map` so nil funcPtr stays nil
            // (no force-unwrap, no synthetic empty closure) and reconstruct cdecl inside the
            // map closure using the locally bound pointer.
            var adapterType = ci.IsOptional ? $"({closureType})?" : closureType;

            // Escaping closures explicitly capture the owner-token box (Bug 1 Cat 3 / Bug 3 Case 2).
            // Capture list pulls `_box_N` into the stored closure so Swift ARC tracks its lifetime;
            // when Swift releases the closure, the box's deinit upcalls the C# free callback.
            var captureList = ci.IsEffectivelyEscaping ? $"[{synth.Box[ci.Index]}] " : "";
            var observeBoxLine = ci.IsEffectivelyEscaping ? $"_ = {synth.Box[ci.Index]}" : null;

            var closureParamStr = string.Join(", ", analysis.paramDecls);
            if (ci.IsOptional)
            {
                // `let __adapter0: ((ArgType) -> RetType)? = handlerFuncPtr.map { __fp in
                //     let cdecl = unsafeBitCast(__fp, to: (@convention(c) ...).self)
                //     let _box_0: AnyObject = _sbWrapClosureContext(handlerContext!)
                //     return { [_box_0] __p0_0, ... in _ = _box_0; cdecl(...) ... }
                // }`
                var cdeclParamTypes = new List<string>();
                for (int i = 0; i < ci.ClosureArgs.Count; i++)
                    cdeclParamTypes.Add(GetSwiftCdeclParamType(ci.ClosureArgs[i], env));
                cdeclParamTypes.Add("UnsafeMutableRawPointer?");
                var cdeclReturnType = ci.Spec.ReturnType.IsEmptyTuple ? "Void" : "UInt8";
                var cdeclRebindType = $"(@convention(c) ({string.Join(", ", cdeclParamTypes)}) -> {cdeclReturnType}).self";
                swiftWriter.WriteLine($"{indent}let {adapterName}: {adapterType} = {closureCsName}FuncPtr.map {{ __fp in");
                swiftWriter.WriteLine($"{indent}{indent}let {cdeclVarName} = unsafeBitCast(__fp, to: {cdeclRebindType})");
                if (ci.IsEffectivelyEscaping)
                {
                    // Pair invariant: when funcPtr is non-nil, context is non-nil (set together
                    // by the C# wrapper). Force-unwrap is safe here.
                    swiftWriter.WriteLine($"{indent}{indent}let {synth.Box[ci.Index]}: AnyObject = {ClosureContextHelperEmitter.WrapFunctionName}({closureCsName}Context!)");
                }
                string returnPrefixInner;
                if (analysis.paramDecls.Count > 0)
                    returnPrefixInner = $"return {{ {captureList}{closureParamStr} in";
                else if (ci.IsEffectivelyEscaping)
                    returnPrefixInner = $"return {{ {captureList}in";
                else
                    returnPrefixInner = "return {";
                swiftWriter.WriteLine($"{indent}{indent}{returnPrefixInner}");
            }
            else
            {
                string adapterOpen;
                if (analysis.paramDecls.Count > 0)
                    adapterOpen = $"{{ {captureList}{closureParamStr} in";
                else if (ci.IsEffectivelyEscaping)
                    adapterOpen = $"{{ {captureList}in";
                else
                    adapterOpen = "{";
                swiftWriter.WriteLine($"{indent}let {adapterName}: {adapterType} = {adapterOpen}");
            }

            // Optional adapters have two extra open braces (`.map { __fp in` and `return {...}`),
            // so their body sits one extra indent level deeper and closes with an extra `}`.
            var bodyBaseIndent = ci.IsOptional ? indent + indent + indent : indent + indent;

            // Defensive observability for the captured box — Swift's capture list already
            // retains it, but a `_ = _box_N` reference makes the dependency obvious to
            // static analyzers and survives any future refactor that drops the capture list.
            if (observeBoxLine != null)
                swiftWriter.WriteLine($"{bodyBaseIndent}{observeBoxLine}");

            // D1: Heap allocations sit outside any if-let branch — they're independent of
            // optional-existential nil-vs-not and C# takes ownership either way (VWT Destroy
            // + NativeMemory.Free on disposal). Duplicating them per-branch would leak.
            // MCB's only heap-alloc branch is the IsComplexEnum case at line ~464; the broader
            // owning-vs-borrowing split for the non-MCB Swift wrapper lives in
            // ClosureEmitter.SwiftWrapper.cs. See bug-0.10.0-swift-wrapper-payload-buffer-leak.md.
            foreach (var (idx, swiftType) in analysis.heapAllocArgs)
            {
                swiftWriter.WriteLine($"{bodyBaseIndent}let __heap{ci.Index}_{idx} = UnsafeMutableRawPointer.allocate(byteCount: MemoryLayout<{swiftType}>.size, alignment: MemoryLayout<{swiftType}>.alignment)");
                swiftWriter.WriteLine($"{bodyBaseIndent}__heap{ci.Index}_{idx}.initializeMemory(as: {swiftType}.self, repeating: __p{ci.Index}_{idx}, count: 1)");
            }

            // Local helper: emits the withUnsafePointer nesting over pointerWrapArgs, the cdecl
            // call, and the closing braces. `optOverrides[i]` replaces the argument expression
            // at slot `i` (used by the Optional<any Error> if-let branches to inject `nil` or
            // `UnsafeMutableRawPointer(mutating:__ptr)` explicitly).
            //
            // `firstLinePrefix` is prepended to the FIRST line emitted by this helper (either
            // the outermost `withUnsafePointer(...) { ... in` or the direct cdecl call). The
            // adapter closure has a non-void Bool return AND a multi-statement body whenever
            // an observe line, heap allocs, or optional-existential branches are present —
            // Swift requires explicit `return` on the last value-producing statement. Inner
            // withUnsafePointer trailing closures stay single-expression and auto-return.
            void EmitCdeclInvocation(string baseIndent, Dictionary<int, string> optOverrides, string firstLinePrefix)
            {
                var currentIndent = baseIndent;
                for (int w = 0; w < analysis.pointerWrapArgs.Count; w++)
                {
                    var (pwIdx, _) = analysis.pointerWrapArgs[w];
                    var linePrefix = (w == 0) ? firstLinePrefix : "";
                    swiftWriter.WriteLine($"{currentIndent}{linePrefix}withUnsafePointer(to: __p{ci.Index}_{pwIdx}) {{ __ptr{ci.Index}_{pwIdx} in");
                    currentIndent += indent;
                }

                var cdeclCallArgs = new List<string>();
                for (int i = 0; i < ci.ClosureArgs.Count; i++)
                {
                    if (optOverrides.TryGetValue(i, out var ovr))
                    {
                        cdeclCallArgs.Add(ovr);
                        continue;
                    }

                    var heapArg = analysis.heapAllocArgs.FirstOrDefault(h => h.index == i);
                    if (heapArg != default)
                    {
                        cdeclCallArgs.Add($"__heap{ci.Index}_{i}");
                        continue;
                    }

                    var ptrArg = analysis.pointerWrapArgs.FirstOrDefault(p => p.index == i);
                    if (ptrArg != default)
                    {
                        cdeclCallArgs.Add($"UnsafeMutableRawPointer(mutating: __ptr{ci.Index}_{i})");
                        continue;
                    }

                    var direct = analysis.directArgs.FirstOrDefault(d => d.index == i);
                    cdeclCallArgs.Add(direct.conversion);
                }
                cdeclCallArgs.Add($"{closureCsName}Context");

                var cdeclExpr = $"{cdeclVarName}({string.Join(", ", cdeclCallArgs)})";
                if (!ci.Spec.ReturnType.IsEmptyTuple)
                    cdeclExpr += " != 0";
                var cdeclLinePrefix = (analysis.pointerWrapArgs.Count == 0) ? firstLinePrefix : "";
                swiftWriter.WriteLine($"{currentIndent}{cdeclLinePrefix}{cdeclExpr}");

                for (int w = analysis.pointerWrapArgs.Count - 1; w >= 0; w--)
                {
                    currentIndent = currentIndent.Substring(indent.Length);
                    swiftWriter.WriteLine($"{currentIndent}}}");
                }
            }

            // Adapter closures have a non-void Bool return whenever the original Swift closure
            // returned a non-Void type. Adding `_ = _box_N` (escaping) or heap allocs makes the
            // body multi-statement, so the last value-producing statement needs explicit `return`.
            // Always emitting `return` for non-void is harmless even in single-expression bodies.
            // (Distinct name from the outer-scope `returnPrefix` used by the method-call emission.)
            var closureReturnPrefix = ci.Spec.ReturnType.IsEmptyTuple ? "" : "return ";

            if (analysis.optionalExistentialArgs.Count == 0)
            {
                if (analysis.pointerWrapArgs.Count > 0 || analysis.heapAllocArgs.Count > 0)
                {
                    EmitCdeclInvocation(bodyBaseIndent, new Dictionary<int, string>(), closureReturnPrefix);
                }
                else
                {
                    // All args direct — skip the withUnsafePointer scaffolding for clarity
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
                    swiftWriter.WriteLine($"{bodyBaseIndent}{closureReturnPrefix}{cdeclExpr}");
                }
            }
            else if (analysis.optionalExistentialArgs.Count == 1)
            {
                // Pattern A: `(any Error)?` — nil passes IntPtr.Zero to cdecl, non-nil passes a
                // pointer to a withUnsafePointer-borrowed ExistentialContainer. Two cdecl calls
                // (one per branch) are simpler than trying to lift the pointer out of the block.
                //
                // The `if-let / else` is a Swift statement (not expression), so each branch needs
                // its own `return` for non-void adapters. The if-branch puts `return` on the
                // manually-emitted outer withUnsafePointer; the inner trailing closure stays
                // single-expression and auto-returns. The else-branch passes `closureReturnPrefix`
                // through to EmitCdeclInvocation.
                var (optIdx, _) = analysis.optionalExistentialArgs[0];
                var valName = $"__val{ci.Index}_{optIdx}";
                var ptrName = $"__ptr{ci.Index}_{optIdx}";
                swiftWriter.WriteLine($"{bodyBaseIndent}if let {valName} = __p{ci.Index}_{optIdx} {{");
                var ifBodyIndent = bodyBaseIndent + indent;
                swiftWriter.WriteLine($"{ifBodyIndent}{closureReturnPrefix}withUnsafePointer(to: {valName}) {{ {ptrName} in");
                EmitCdeclInvocation(
                    ifBodyIndent + indent,
                    new Dictionary<int, string> { [optIdx] = $"UnsafeMutableRawPointer(mutating: {ptrName})" },
                    firstLinePrefix: "");
                swiftWriter.WriteLine($"{ifBodyIndent}}}");
                swiftWriter.WriteLine($"{bodyBaseIndent}}} else {{");
                EmitCdeclInvocation(
                    bodyBaseIndent + indent,
                    new Dictionary<int, string> { [optIdx] = "nil" },
                    closureReturnPrefix);
                swiftWriter.WriteLine($"{bodyBaseIndent}}}");
            }
            else
            {
                throw new InvalidOperationException(
                    $"MethodClosureBridge: closures with more than one Optional<any Error> parameter " +
                    $"are not yet supported (method: {method.Name}, count: {analysis.optionalExistentialArgs.Count}).");
            }

            // Close the adapter closure. Optional adapters also close the `.map { __fp in }` wrapper.
            if (ci.IsOptional)
            {
                swiftWriter.WriteLine($"{indent}{indent}}}"); // close `return { ... }`
                swiftWriter.WriteLine($"{indent}}}");          // close `.map { __fp in ... }`
            }
            else
            {
                swiftWriter.WriteLine($"{indent}}}");
            }
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
                var adapterName = synth.Adapter[ci.Index];
                allCallArgs.Add($"{callLabel}{adapterName}");
            }
            else if (passableByArg.TryGetValue(arg, out var passable))
            {
                var label = GetSwiftArgLabel(passable.arg);
                var paramName = NameProvider.EscapeSwiftKeyword(passable.csName);
                // PayloadHandle/Utf8Slice params were reconstructed → use {name}Val
                var valSuffix = passable.category is ParamAbiCategory.PayloadHandle
                    or ParamAbiCategory.Utf8Slice ? "Val" : "";
                allCallArgs.Add($"{label}{paramName}{valSuffix}");
            }
        }

        swiftWriter.WriteLine($"{indent}{returnPrefix}{callTarget}.{NameProvider.ParserNameToSwift(method)}({string.Join(", ", allCallArgs)}){returnSuffix}");
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

        csWriter.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
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

        // GCHandle is freed Swift-side: for escaping closures the wrapper wraps `contextPtr`
        // in an `_SBClosureCtx` ARC box; when Swift releases the closure (and thus the box),
        // the box's deinit upcalls the C# free callback registered by
        // `SwiftClosureContext.EnsureRegistered`, freeing the GCHandle exactly once. The
        // trampoline must therefore NOT free the handle — multi-shot closures (e.g.,
        // AsyncCallbackClosures.processMultiple) would corrupt calls 2..N.

        if (!closureReturnIsVoid)
        {
            // Bool return → Func<..., bool>
            innerTypeArgs.Add("bool");
            // P0-01: resolve the delegate from the GCHandle *inside* the guarded try. A bad or
            // already-freed handle makes handle.Target throw; if that ran before the try the
            // exception would unwind out of the [UnmanagedCallersOnly] frame into the Swift
            // @_cdecl caller → SIGABRT. Inside the try it routes through FailFast instead.
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"var callback = (Func<{string.Join(", ", innerTypeArgs)}>)handle.Target!;");
            var callArgs = string.Join(", ", Enumerable.Range(0, closureArgs.Count).Select(i => $"arg{i}"));
            csWriter.WriteLine($"return (byte)(callback({callArgs}) ? 1 : 0);");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            ClosureEmitter.EmitNonThrowingFailFastCatch(csWriter);
        }
        else
        {
            // Void return → Action<...>. P0-01: resolve the delegate inside the try (see the
            // bool branch) so a bad/freed handle faults via FailFast, not a SIGABRT escape.
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            if (innerTypeArgs.Count > 0)
            {
                csWriter.WriteLine($"var callback = (Action<{string.Join(", ", innerTypeArgs)}>)handle.Target!;");
            }
            else
            {
                csWriter.WriteLine("var callback = (Action)handle.Target!;");
            }
            var callArgs = string.Join(", ", Enumerable.Range(0, closureArgs.Count).Select(i => $"arg{i}"));
            csWriter.WriteLine($"callback({callArgs});");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            ClosureEmitter.EmitNonThrowingFailFastCatch(csWriter);
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
                case ParamAbiCategory.Utf8Slice:
                    pinvokeParams.Add($"IntPtr {csName}Utf8Ptr");
                    pinvokeParams.Add($"nint {csName}Utf8Len");
                    break;
            }
        }

        // N × (funcPtr, closureCtx) pairs — one per closure
        // Use __closureCtx prefix to avoid collision with user parameter names like 'context'
        foreach (var ci in closures)
        {
            var suffix = closures.Count > 1 ? $"_{ci.Index}" : "";
            pinvokeParams.Add($"IntPtr __funcPtr{suffix}");
            pinvokeParams.Add($"IntPtr __closureCtx{suffix}");
        }

        // Self parameter — instance methods only.
        // Generic parents use SwiftSelf (Swift calling convention, self in x20 register).
        // Non-generic parents use IntPtr (C calling convention, self as trailing parameter).
        bool isInstance = method.MethodType != MethodType.Static;
        bool isGenericParent = method.ParentDecl is TypeDecl gpTd && gpTd.IsGeneric;
        bool usesSwiftCallingConvention = isGenericParent && isInstance;
        if (isInstance)
        {
            // P1-22 (C1): the trailing self param hardcodes `self_`; a user non-closure param
            // projected to the same name would be a CS0100 duplicate (the closure pair names
            // are already `__`-prefixed synthetics, so only a user `self_` can collide). Guard
            // it against the other P/Invoke param names. Call-site args are positional, so the
            // renamed param needs no call-site change.
            var pinvokeReserved = new List<string>();
            foreach (var (_, csName, _, _) in passableNonClosureParams)
                pinvokeReserved.Add(csName);
            var selfPInvokeName = new SyntheticNameScope(pinvokeReserved).Reserve("self_");
            pinvokeParams.Add(usesSwiftCallingConvention ? $"SwiftSelf {selfPInvokeName}" : $"IntPtr {selfPInvokeName}");
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

        var silgenName = BuildBridgeSymbolName(method, method.ParentDecl as TypeDecl, closures[0].CallbackBaseName);
        var pInvokeName = $"PInvoke_{closures[0].CallbackBaseName}";

        PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
        {
            LibraryPath = asyncLibName,
            EntryPoint = silgenName,
            MethodName = pInvokeName,
            ReturnType = pinvokeReturnType,
            ParametersString = string.Join(", ", pinvokeParams),
            Visibility = PInvokeVisibility.Internal,
            CallingConvention = usesSwiftCallingConvention
                ? PInvokeCallingConvention.Swift
                : PInvokeCallingConvention.Cdecl,
            EmissionContext = env.EmissionContext,
            EnforceWrapperContract = true
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
            // Optional closure parameter → nullable delegate so callers can pass null.
            if (ci.IsOptional) delegateType += "?";
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

        // Use env.CSharpMethodName so that the projected-signature collision suffix
        // assigned by IHandler.HandleBaseDecl (CollisionIndex) actually reaches the
        // emitted public method. Recomputing via NameProvider.GetPublicMethodName here
        // would drop the suffix and produce CS0111 duplicates when two Swift overloads
        // (e.g. Auth.signIn(email:password:) vs signIn(email:link:)) project to the
        // same C# parameter list.
        var methodName = env.CSharpMethodName;

        var isStatic = method.MethodType == MethodType.Static;
        var staticKeyword = isStatic ? "static " : "";

        XmlDocCommentEmitter.EmitMethodDocComment(csWriter, method);

        csWriter.WriteLine($"public {staticKeyword}unsafe {returnType} {methodName}({string.Join(", ", publicParams)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // When in a generic type, callback pointer and P/Invoke live in the helper class
        var helperPrefix = string.IsNullOrEmpty(helperClassName) ? "" : $"{helperClassName}.";

        // Build inner callback delegates — one per closure.
        // Each maps cdecl-typed args to user-typed args.
        // For Optional closures, guard the GCHandle alloc + funcPtr wiring on the delegate
        // being non-null so callers can pass null. For escaping (incl. Optional<closure>) the
        // GCHandle is freed Swift-side via the `_SBClosureCtx` box deinit upcall on the
        // happy path; the finally below only frees when ownership transfer never completed
        // (e.g. the P/Invoke threw before Swift constructed the box).
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

            // Pre-declare the ClosureHandle at method scope so the finally block can dispose it
            // unconditionally. For Optional closures the construction sits inside an
            // `if (param != null)` block; pre-declaring keeps the variable visible to finally
            // regardless (default(ClosureHandle).Dispose() is a no-op).
            csWriter.WriteLine($"ClosureHandle __gcHandle{innerSuffix} = default;");

            if (ci.IsOptional)
            {
                // IntPtr.Zero pair communicates "no closure" to the Swift adapter.
                csWriter.WriteLine($"IntPtr __funcPtr{innerSuffix} = IntPtr.Zero;");
                csWriter.WriteLine($"IntPtr __ctxPtr{innerSuffix} = IntPtr.Zero;");
                csWriter.WriteLine($"if ({ci.ParamName} != null)");
                csWriter.WriteLine("{");
                csWriter.Indent++;
            }

            if (!ci.ReturnIsVoid)
            {
                innerTypeArgs.Add("bool");
                csWriter.WriteLine($"Func<{string.Join(", ", innerTypeArgs)}> __inner{innerSuffix} = ({string.Join(", ", innerParamDecls)}) =>");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                for (int i = 0; i < ci.ClosureArgs.Count; i++)
                {
                    EmitArgMarshal(csWriter, ci.ClosureArgs[i], closureArgCSharpTypes[i], i, env);
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
                        EmitArgMarshal(csWriter, ci.ClosureArgs[i], closureArgCSharpTypes[i], i, env);
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

            // Allocate the ClosureHandle. For escaping closures Swift takes lifetime ownership
            // through the `_SBClosureCtx` box (deinit upcalls the C# free callback); the helper
            // suppresses Free on MarkOwnershipTransferred. For non-escaping closures the
            // trampoline is invoked synchronously inside the call; the helper frees the handle
            // deterministically in the finally below.
            var policy = ci.IsEffectivelyEscaping
                ? "ClosureHandlePolicy.Escaping"
                : "ClosureHandlePolicy.NonEscaping";
            csWriter.WriteLine($"__gcHandle{innerSuffix} = new ClosureHandle(__inner{innerSuffix}, {policy});");

            if (ci.IsOptional)
            {
                csWriter.WriteLine($"__funcPtr{innerSuffix} = {helperPrefix}s_{ci.CallbackBaseName};");
                csWriter.WriteLine($"__ctxPtr{innerSuffix} = __gcHandle{innerSuffix}.Context;");
                csWriter.Indent--;
                csWriter.WriteLine("}");
            }
        }

        // Wrap the P/Invoke section in try/finally so the ClosureHandle for every closure
        // (escaping or non-escaping) is disposed in the finally — escaping handles transferred
        // to Swift's `_SBClosureCtx` box are left alive by the helper's policy gate, while
        // non-escaping handles and escaping handles whose ownership never transferred (the
        // P/Invoke threw before MarkOwnershipTransferred ran) are freed locally.
        bool hasClosures = closures.Count > 0;
        if (hasClosures)
        {
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
        }

        // Utf8Slice params: allocate UTF-8 bytes up front; pin via `fixed` around the P/Invoke call below.
        // `bareName` strips any `@` verbatim prefix from `csName` so the local identifiers are valid.
        var utf8SliceLocals = new List<(string csName, string bareName)>();
        foreach (var (_, csName, _, category) in passableNonClosureParams)
        {
            if (category != ParamAbiCategory.Utf8Slice) continue;
            var bareName = NameProvider.StripVerbatimPrefix(csName);
            utf8SliceLocals.Add((csName, bareName));
            csWriter.WriteLine($"var __{bareName}Utf8 = System.Text.Encoding.UTF8.GetBytes({csName});");
        }

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
                case ParamAbiCategory.Utf8Slice:
                    var bareName = NameProvider.StripVerbatimPrefix(csName);
                    callArgs.Add($"(IntPtr)__{bareName}Ptr");
                    callArgs.Add($"(nint)__{bareName}Utf8.Length");
                    break;
            }
        }

        // N × (funcPtr, context) pairs — one per closure
        for (int c = 0; c < closures.Count; c++)
        {
            var ci = closures[c];
            var innerSuffix = closures.Count > 1 ? $"_{c}" : "";
            if (ci.IsOptional)
            {
                callArgs.Add($"__funcPtr{innerSuffix}");
                callArgs.Add($"__ctxPtr{innerSuffix}");
            }
            else
            {
                callArgs.Add($"{helperPrefix}s_{ci.CallbackBaseName}");
                callArgs.Add($"__gcHandle{innerSuffix}.Context");
            }
        }

        // Self parameter — instance methods only.
        // Generic parents: SwiftSelf (Swift calling convention, self in x20).
        // Non-generic parents: IntPtr directly (C calling convention, self as trailing arg).
        if (!isStatic)
        {
            bool usesSwiftSelf = method.ParentDecl is TypeDecl gpTd && gpTd.IsGeneric;
            bool isObjCRooted = method.ParentDecl is ClassDecl cd && cd.IsObjCRooted;
            var selfHandle = isObjCRooted ? "Handle" : "Payload.DangerousGetHandle()";
            callArgs.Add(usesSwiftSelf
                ? $"new SwiftSelf((void*){selfHandle})"
                : selfHandle);
        }

        // Pin UTF-8 byte arrays for Swift.String params so the Swift @_cdecl adapter can read
        // them via UnsafeBufferPointer. Fixed block must wrap the entire P/Invoke call (and its
        // return-value marshalling for class returns, since the Swift side may still be reading).
        foreach (var (_, bareName) in utf8SliceLocals)
        {
            csWriter.WriteLine($"fixed (byte* __{bareName}Ptr = __{bareName}Utf8)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
        }

        if (returnsClass)
        {
            csWriter.WriteLine($"var __result = {helperPrefix}{pInvokeName}({string.Join(", ", callArgs)});");
            EmitClosureOwnershipTransferred(csWriter, closures);
            csWriter.WriteLine($"return ({returnType})SwiftMarshal.MarshalFromSwift<{returnType}>(__result);");
        }
        else if (!returnSpec.IsEmptyTuple)
        {
            // Primitive return — capture before marking transfer so the flag is only flipped
            // on a successful P/Invoke return.
            csWriter.WriteLine($"var __pinvokeResult = {helperPrefix}{pInvokeName}({string.Join(", ", callArgs)});");
            EmitClosureOwnershipTransferred(csWriter, closures);
            csWriter.WriteLine("return __pinvokeResult;");
        }
        else
        {
            csWriter.WriteLine($"{helperPrefix}{pInvokeName}({string.Join(", ", callArgs)});");
            EmitClosureOwnershipTransferred(csWriter, closures);
        }

        for (int i = 0; i < utf8SliceLocals.Count; i++)
        {
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        if (hasClosures)
        {
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            for (int c = 0; c < closures.Count; c++)
            {
                var innerSuffix = closures.Count > 1 ? $"_{c}" : "";
                csWriter.WriteLine($"__gcHandle{innerSuffix}.Dispose();");
            }
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    /// <summary>
    /// Emits the per-closure <c>__gcHandle{suffix}.MarkOwnershipTransferred();</c> calls
    /// for all escaping closures, to be placed immediately after a successful P/Invoke call
    /// in <see cref="EmitPublicMethod"/>. Pairs with the finally block emitted there which
    /// disposes each handle — the helper's policy gate suppresses Free for transferred
    /// escaping handles (Swift's `_SBClosureCtx` deinit will fire instead) while still
    /// freeing non-transferred and non-escaping ones.
    /// </summary>
    private static void EmitClosureOwnershipTransferred(CSharpWriter csWriter, List<ClosureInfo> closures)
    {
        for (int c = 0; c < closures.Count; c++)
        {
            var ci = closures[c];
            if (!ci.IsEffectivelyEscaping) continue;
            var innerSuffix = closures.Count > 1 ? $"_{c}" : "";
            csWriter.WriteLine($"__gcHandle{innerSuffix}.MarkOwnershipTransferred();");
        }
    }

    // ─── Arg Marshalling ───────────────────────────────────────────────

    private static void EmitArgMarshal(
        CSharpWriter csWriter,
        TypeSpec argType,
        string csharpType,
        int index,
        MethodEnvironment env)
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
        else if (IsAnyErrorExistential(argType))
        {
            // any Swift.Error — IntPtr points to a 5-word ExistentialContainer1 on the Swift stack
            // (borrowed via withUnsafePointer). Copy the container out and wrap as AnyError so the
            // managed value outlives the callback frame. Payload references are retained via Swift's
            // normal existential-copy semantics by the caller — we read bytes, we do not take ownership.
            csWriter.WriteLine($"var __a{index} = new global::Swift.Foundation.AnyError(*(global::Swift.Runtime.ExistentialContainer1*)__p{index});");
        }
        else if (IsOptionalAnyErrorExistential(argType))
        {
            // Optional<any Error>: Swift adapter sends nil via IntPtr.Zero; otherwise pointer to
            // a borrowed ExistentialContainer1. Copy out to produce a managed-lifetime AnyError?.
            csWriter.WriteLine($"global::Swift.Foundation.AnyError? __a{index} = __p{index} == IntPtr.Zero ? null : new global::Swift.Foundation.AnyError(*(global::Swift.Runtime.ExistentialContainer1*)__p{index});");
        }
        else if (IsSwiftResultWithAnyErrorFailure(argType))
        {
            // Swift.Result<T, any Error>: Swift adapter wraps the enum with withUnsafePointer
            // so we receive a stack-lifetime pointer. NewFromPayload heap-copies the payload
            // via the VWT (InitializeWithCopy), so the SafeHandle owns the copy — must NOT
            // suppress finalization (that's MarshalBorrowedFromSwift's job). Use MarshalFromSwift.
            csWriter.WriteLine($"var __a{index} = SwiftMarshal.MarshalFromSwift<{csharpType}>(__p{index});");
        }
        else if (env.ClosureHandler.GetSimpleEnumInfo(argType) is { hasRawValue: true })
        {
            // Simple enum with numeric raw value — Swift wrapper passed `.rawValue`, so the
            // callback receives the underlying integer directly. Cast to the typed enum.
            csWriter.WriteLine($"var __a{index} = ({csharpType})__p{index};");
        }
        else if (IsOptionalClassArg(argType, env) &&
                 argType is NamedTypeSpec optClassArg &&
                 optClassArg.GenericParameters[0] is NamedTypeSpec innerClassSpec &&
                 env.TypeDatabase.TryGetTypeRecord(innerClassSpec, out var innerClassRec))
        {
            // Optional<Class/ObjC> — Swift passed nil as IntPtr.Zero; non-nil is a borrowed ref.
            // MarshalBorrowedFromSwift suppresses the finalizer (caller owns lifetime).
            var innerCs = innerClassRec.CSharpTypeName.FullyQualifiedName;
            csWriter.WriteLine($"{csharpType} __a{index} = __p{index} == IntPtr.Zero ? null : SwiftMarshal.MarshalBorrowedFromSwift<{innerCs}>(__p{index});");
        }
        else if (env.ClosureHandler.IsComplexEnum(argType))
        {
            // Complex enum: Swift adapter heap-allocates the buffer (UnsafeMutableRawPointer.allocate
            // + initializeMemory at the heapAllocArgs branch above) and transfers ownership to the
            // C# callback — no Swift-side defer. MarshalFromSwift<T> constructs the ISwiftObject
            // wrapper whose SafeHandle pairs VWT.Destroy + NativeMemory.Free on disposal.
            // MarshalBorrowedFromSwift would SuppressFinalize the SafeHandle, leaving no one to
            // free the heap buffer — the no-dispose path would leak the payload.
            csWriter.WriteLine($"var __a{index} = SwiftMarshal.MarshalFromSwift<{csharpType}>(__p{index});");
        }
        else
        {
            // Bound generics / classes come as IntPtr — marshal via MarshalBorrowedFromSwift.
            // Callback parameters are borrowed references from Swift (the caller owns them).
            // MarshalBorrowedFromSwift suppresses the finalizer to prevent double-release.
            csWriter.WriteLine($"var __a{index} = SwiftMarshal.MarshalBorrowedFromSwift<{csharpType}>(__p{index});");
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
    /// Renders a closure argument type for the Swift adapter signature. Wraps the
    /// result in <c>any</c> when the type is an existential (currently only
    /// <c>any Swift.Error</c>) so Swift 6 accepts the closure signature — the
    /// shared <see cref="ExistentialBypassEmitter.RenderSwiftTypeSpec"/> does not
    /// emit the <c>any</c> keyword because most callers render concrete types.
    /// </summary>
    private static string RenderSwiftClosureArgType(TypeSpec typeSpec)
    {
        // Optional<any Error> must render as `(any Swift.Error)?` so Swift 6 accepts the
        // closure signature — `Swift.Error?` loses the existential `any` keyword.
        if (IsOptionalAnyErrorExistential(typeSpec))
            return "(any Swift.Error)?";

        var rendered = ExistentialBypassEmitter.RenderSwiftTypeSpec(typeSpec);
        // ProtocolListTypeSpec already renders with an "any " prefix; only NamedTypeSpec
        // existentials need it added here. Guard prevents "any any Error".
        if (IsAnyErrorExistential(typeSpec) && !rendered.StartsWith("any ", StringComparison.Ordinal))
            return $"any {rendered}";
        return rendered;
    }

    /// <summary>
    /// Checks whether a TypeSpec is the <c>any Swift.Error</c> existential — the only
    /// existential currently supported as an MCB closure argument. The C# runtime type
    /// is <see cref="Swift.Foundation.AnyError"/>, and Swift passes its 5-word existential container
    /// through <c>withUnsafePointer</c> → <c>UnsafeMutableRawPointer</c> (pointer ABI,
    /// same shape as bound generic args).
    /// <para>
    /// The parser produces two different shapes for the same type depending on source:
    /// <list type="bullet">
    /// <item><c>NamedTypeSpec("Swift.Error") { IsAny = true }</c> — from bare
    /// <c>any Error</c> / <c>any Swift.Error</c> in printedName strings, which is
    /// the common ABI JSON path via <see cref="TypeSpecParser"/>.</item>
    /// <item><c>ProtocolListTypeSpec</c> with a single <c>Swift.Error</c> protocol —
    /// from ABI JSON ProtocolComposition nodes. Rare in practice for single-protocol
    /// existentials but allowed for forward compatibility.</item>
    /// </list>
    /// Both shapes must be accepted so closure parameters like
    /// <c>(any Error) -&gt; Void</c> route through MCB.
    /// </para>
    /// </summary>
    internal static bool IsAnyErrorExistential(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec named && named.IsAny && named.Name == "Swift.Error")
            return true;

        if (typeSpec is ProtocolListTypeSpec protoList && protoList.Protocols.Count == 1)
        {
            var proto = protoList.Protocols.Keys.First();
            return proto.Name == "Swift.Error";
        }

        return false;
    }

    /// <summary>
    /// Checks whether a TypeSpec is <c>Optional&lt;any Swift.Error&gt;</c> — Pattern A.
    /// Stripe completion handlers (<c>PaymentSheet.FlowController.update</c>, etc.) deliver
    /// errors via this shape: nil on success, existential container on failure.
    /// ABI: <c>UnsafeMutableRawPointer?</c> — nil maps to C# <c>Swift.Foundation.AnyError?</c> = null.
    /// </summary>
    internal static bool IsOptionalAnyErrorExistential(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec named) return false;
        if (named.Name != "Swift.Optional") return false;
        if (named.GenericParameters.Count != 1) return false;
        return IsAnyErrorExistential(named.GenericParameters[0]);
    }

    /// <summary>
    /// Checks whether a TypeSpec is <c>Swift.Result&lt;T, any Swift.Error&gt;</c> — Pattern B.
    /// Stripe's <c>(Result&lt;PaymentSheet.FlowController, any Error&gt;) -&gt; Void</c> completion
    /// handlers pass this shape. Routed through <c>withUnsafePointer</c> on the Swift side so
    /// the C# callback receives a pointer to the Result enum payload; the C# wrapper then
    /// heap-copies via <c>SwiftResult&lt;T, ExistentialContainer1&gt;.NewFromPayload</c>.
    /// </summary>
    internal static bool IsSwiftResultWithAnyErrorFailure(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec named) return false;
        if (named.Name != "Swift.Result") return false;
        if (named.GenericParameters.Count != 2) return false;
        return IsAnyErrorExistential(named.GenericParameters[1]);
    }

    /// <summary>
    /// Checks if a closure argument type is supported by this emitter.
    /// Supports: primitives, classes, ObjC-bridged types, bound generics whose base
    /// type resolves in TypeDatabase, and <c>any Swift.Error</c> existential.
    /// </summary>
    private static bool IsClosureArgSupported(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        // any Swift.Error — bridged through ExistentialContainer1 pointer, wrapped as AnyError in C#.
        if (IsAnyErrorExistential(typeSpec)) return true;

        // Optional<any Error> — nil-pointer ABI (UnsafeMutableRawPointer?), C# = Swift.Foundation.AnyError?.
        if (IsOptionalAnyErrorExistential(typeSpec)) return true;

        // Result<T, any Error> — stdlib generic not in TypeDatabase; recognize explicitly.
        // Bridged via withUnsafePointer (Swift) + SwiftResult<T, ExistentialContainer1>.NewFromPayload (C#).
        if (IsSwiftResultWithAnyErrorFailure(typeSpec)) return true;

        if (typeSpec is not NamedTypeSpec named) return false;

        // Primitives
        if (MarshallingHelpers.IsSwiftPrimitive(named.Name)) return true;

        // Optional<Class/ObjC>: nil-pointer ABI — Swift passes the class as
        // `UnsafeMutableRawPointer?`, C# receives `IntPtr` (Zero = nil). Arrays, dictionaries,
        // sets, optionals-of-primitive, optional-of-struct remain unsupported to keep the
        // existing rejection surface minimal.
        if (named.ContainsGenericParameters && named.Name == "Swift.Optional" &&
            named.GenericParameters.Count == 1 &&
            named.GenericParameters[0] is NamedTypeSpec optInner &&
            !MarshallingHelpers.IsSwiftPrimitive(optInner.Name))
        {
            try
            {
                if (typeDatabase.TryGetTypeRecord(
                    SwiftTypeName.FromModuleQualifiedName(optInner.Name), out var innerRecord))
                {
                    if (innerRecord.Kind == TypeRecordKind.Class ||
                        MarshallingHelpers.IsObjCBridged(innerRecord))
                        return true;
                }
            }
            catch (ArgumentException) { }
        }

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
                if (record.Kind == TypeRecordKind.Enum)
                {
                    // Complex enums: heap-allocated pointer ABI.
                    if ((record.Flags & TypeRecordFlags.SimpleEnum) == 0)
                        return true;

                    // Simple enums: pass raw value as the underlying integer across the cdecl
                    // boundary. String-backed raw values fall outside the integer ABI — skip
                    // those (they would need a pointer path).
                    return !string.IsNullOrEmpty(record.RawValueTypeName) &&
                           record.RawValueTypeName != "String";
                }

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
    /// Source-of-truth predicate for which <see cref="ParamAbiCategory"/> values are
    /// safe to pass directly through a @_cdecl bridge — both this closure bridge AND
    /// the CSM engine / sync+async generic bridges share this allowlist. Add new
    /// categories here only; never duplicate the disjunction at call sites.
    /// </summary>
    internal static bool IsAbiCategoryPassable(ParamAbiCategory category)
    {
        return category is ParamAbiCategory.Primitive
            or ParamAbiCategory.ObjCHandle
            or ParamAbiCategory.PayloadHandle
            or ParamAbiCategory.Utf8Slice;
    }

    /// <summary>
    /// CSM-specific passability predicate. Strict superset of <see cref="IsAbiCategoryPassable"/>:
    /// adds <see cref="ParamAbiCategory.KeyPathFamily"/> because the CSM emitter has dedicated
    /// arms in both the C# bridge and the Swift @_cdecl wrapper for KeyPath params, but the
    /// closure-bridge variants (MethodClosureBridge, GenericClosureBridgeEmitter,
    /// NestedClosureBridge, MethodGenericBridgeEmitter, ConstrainedExistentialBridge) have not
    /// yet been extended to emit KeyPath marshalling switch arms. Keeping the predicates split
    /// honors the predicate↔emitter contract: a category is "passable" exactly where the
    /// downstream emission can render it.
    /// </summary>
    internal static bool IsAbiCategoryPassableForCsm(ParamAbiCategory category)
    {
        return IsAbiCategoryPassable(category)
            || category is ParamAbiCategory.KeyPathFamily;
    }

    /// <summary>
    /// Checks if a non-closure parameter can be passed through or omitted (default).
    /// Delegates the category-level allowlist to <see cref="IsAbiCategoryPassable"/>.
    /// </summary>
    private static bool IsNonClosureParamPassable(ArgumentDecl arg, ITypeDatabase typeDatabase)
    {
        // Params with defaults are omitted — Swift fills them
        if (arg.HasDefaultArg) return true;

        return IsAbiCategoryPassable(ClassifyParam(arg, typeDatabase));
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

            case ParamAbiCategory.Utf8Slice:
                return ("string", category);

            default:
                return ("IntPtr", ParamAbiCategory.Unsupported);
        }
    }

    /// <summary>
    /// Gets the C# type name for a closure argument TypeSpec.
    /// </summary>
    private static string GetCSharpTypeForClosureArg(TypeSpec argType, MethodEnvironment env)
    {
        // any Swift.Error existential → Swift.Foundation.AnyError runtime type
        if (IsAnyErrorExistential(argType))
            return "Swift.Foundation.AnyError";

        // Optional<any Error> → nullable struct so callbacks can observe the nil branch.
        if (IsOptionalAnyErrorExistential(argType))
            return "Swift.Foundation.AnyError?";

        if (argType is NamedTypeSpec namedArg)
        {
            // Primitives
            if (namedArg.Name == "Swift.Bool") return "bool";
            if (MarshallingHelpers.IsSwiftPrimitive(namedArg.Name))
                return GetCSharpPrimitiveType(namedArg.Name);

            // Optional<Class/ObjC>: project as `ClassT?` — the user-facing delegate receives
            // a nullable reference. Matches the nil-pointer ABI handled by EmitArgMarshal.
            if (IsOptionalClassArg(argType, env) &&
                namedArg.GenericParameters[0] is NamedTypeSpec innerClass &&
                env.TypeDatabase.TryGetTypeRecord(innerClass, out var innerRecord))
            {
                return $"{innerRecord.CSharpTypeName.FullyQualifiedName}?";
            }

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

            // Simple enum: pass raw value via the enum's C# underlying integer type.
            var enumInfo = env.ClosureHandler.GetSimpleEnumInfo(argType);
            if (enumInfo != null)
                return enumInfo.Value.csUnderlying;

            // Optional<Class/ObjC>: nil-pointer ABI — IntPtr (Zero = nil).
            if (IsOptionalClassArg(argType, env)) return "IntPtr";
        }

        // Bound generics, classes: IntPtr (pointer ABI)
        return "IntPtr";
    }

    /// <summary>
    /// Checks whether <paramref name="argType"/> is <c>Swift.Optional&lt;T&gt;</c> where
    /// <c>T</c> is a Swift class or ObjC-bridged class. These use the nil-pointer ABI
    /// (IntPtr.Zero signals nil; non-zero is a borrowed reference).
    /// </summary>
    private static bool IsOptionalClassArg(TypeSpec argType, MethodEnvironment env)
    {
        if (argType is not NamedTypeSpec named) return false;
        if (named.Name != "Swift.Optional") return false;
        if (named.GenericParameters.Count != 1) return false;
        if (named.GenericParameters[0] is not NamedTypeSpec inner) return false;
        if (MarshallingHelpers.IsSwiftPrimitive(inner.Name)) return false;
        try
        {
            if (env.TypeDatabase.TryGetTypeRecord(
                SwiftTypeName.FromModuleQualifiedName(inner.Name), out var record))
            {
                return record.Kind == TypeRecordKind.Class ||
                       MarshallingHelpers.IsObjCBridged(record);
            }
        }
        catch (ArgumentException) { }
        return false;
    }

    /// <summary>
    /// Gets the Swift cdecl-compatible type for a closure argument.
    /// Delegates to the canonical implementation in SwiftBuilder.
    /// </summary>
    private static string GetSwiftCdeclParamType(TypeSpec argType, MethodEnvironment env)
        => SwiftBuilder.GetSwiftCdeclParamType(argType, env.ClosureHandler);

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
    internal static string GetPInvokePrimitiveType(TypeSpec typeSpec)
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
    /// Used by both MethodClosureBridge and ConcreteProtocolSpecializationEmitter
    /// to discriminate Swift class params (where the IntPtr IS the object reference,
    /// reconstruct via unsafeBitCast or Unmanaged.fromOpaque) from non-frozen struct
    /// params (where the IntPtr points to a flat value-witness-table-layout buffer,
    /// reconstruct via .assumingMemoryBound(to:).pointee).
    /// </summary>
    internal static bool IsClassTypeForSwift(NamedTypeSpec named, ITypeDatabase typeDatabase)
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
        // Unlabeled: bare "_", empty, or a synthesized positional name (argN) — Swift renders
        // these call sites with no argument label.
        if (string.IsNullOrEmpty(name) || name == "_" || SwiftBuilder.IsAutoGeneratedArgName(name))
            return ""; // Unlabeled
        // Otherwise the ABI printedName carries the genuine external label verbatim — including
        // labels that legitimately begin with "_" (e.g. "_box_0"). Stripping the underscore
        // would emit the wrong label and the @_cdecl symbol would fail to link (the function is
        // silently dropped from the dylib → EntryPointNotFoundException at runtime).
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
        /// <summary>Swift.String → split into (UTF-8 byte pointer, length) pair, C# side pins via fixed.</summary>
        Utf8Slice,
        /// <summary>
        /// Swift KeyPath family (AnyKeyPath, PartialKeyPath&lt;Root&gt;, KeyPath&lt;Root,Value&gt;,
        /// WritableKeyPath, ReferenceWritableKeyPath) — single-pointer @_cdecl ABI. The C#
        /// wrapper IS a SafeHandle (<see cref="Swift.KeyPath{TRoot,TValue}"/> derives directly
        /// from SafeHandleZeroOrMinusOneIsInvalid), so the P/Invoke argument is
        /// <c>paramName.DangerousGetHandle()</c> — NOT <c>.Payload.DangerousGetHandle()</c>
        /// and NOT <c>((ISwiftObject)x).SwiftHandle</c> (the wrapper does not implement
        /// ISwiftObject — see <see cref="KeyPathProjection"/>). Mirrors
        /// <see cref="KeyPathProjection.GetParameterPlan"/>.
        /// </summary>
        KeyPathFamily,
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

        if (named.Name == "Swift.String")
            return ParamAbiCategory.Utf8Slice;

        if (IsSwiftPointerType(named.Name))
            return ParamAbiCategory.PointerType;

        // KeyPath family — Swift KeyPath/WritableKeyPath/etc. have no TypeRecord
        // (the family lives only in TypeProjectionFactory.KeyPathFamilyArities)
        // so the TryGetTypeRecord block below cannot route them. Classify here
        // before falling through to Unsupported.
        if (TypeProjectionFactory.IsKeyPathFamily(named.Name))
            return ParamAbiCategory.KeyPathFamily;

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
