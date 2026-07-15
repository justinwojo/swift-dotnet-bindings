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
/// Supports multiple inner closures per outer and multiple outer closures per method.
/// A single Swift wrapper is emitted per method that receives all outer closures'
/// funcPtr/context pairs and dispatches to the original method.
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
        string CallbackBaseName, string ParamName, int Index, bool IsEffectivelyEscaping);
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

            // ABI passability allowlist is canonical on MethodClosureBridge.IsAbiCategoryPassable,
            // but NestedClosureBridge's wrapper-body emission switches (around lines 891/895 for
            // the Swift @_cdecl shape and 1066/1069 for the C# call site) do not yet carry a
            // Utf8Slice case — admitting Swift.String through the canonical predicate would land
            // it in those switches' `default:` branches and emit a single `IntPtr {csName}` with
            // no UTF-8 pinning/reconstruction. Reject Utf8Slice locally with reasoning until the
            // NestedClosureBridge body grows the matching ptr+len pair.
            var category = MethodClosureBridge.ClassifyParam(arg, typeDatabase);
            if (!MethodClosureBridge.IsAbiCategoryPassable(category)
                || category == MethodClosureBridge.ParamAbiCategory.Utf8Slice)
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
            // Always use indexed naming (NCB_{hash}_0, _1, …) so adding a closure later
            // doesn't silently rename the first symbol from bare NCB_{hash} to NCB_{hash}_0.
            var baseName = $"NCB_{mangledHash}_{closureIndex}";

            // Escaping (or Optional<closure>, which is always escaping in Swift)
            // outer closures get a Swift-ARC owner-token box around the GCHandle context so
            // the GCHandle is freed when Swift releases the closure.
            var isEffectivelyEscaping = WrapperValidation.IsEffectivelyEscaping(
                cts, arg.SwiftTypeSpec, env.ClosureHandler);

            nestedClosures.Add(new NestedClosureInfo(
                cts, arg, outerArgs, innerClosures, outerNonClosureArgs,
                baseName, paramName, closureIndex, isEffectivelyEscaping));

            closureIndex++;
        }

        if (nestedClosures.Count == 0)
            return false;

        var asyncLibName = env.TypeDatabase.AsyncLibraryName ?? "SwiftBindings";
        // Namespaces the escaping inner-box release symbol per module — several modules can
        // link into one wrapper library, so a bare symbol would collide at link time.
        var bridgeModuleName = parentDecl?.SwiftTypeName?.Module ?? method.ModuleDecl?.Name ?? "Module";

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

        // Register the SBW_ symbol with the wrapper-symbol contract before emitting
        // the Swift wrapper. The matching P/Invoke at EmitPInvoke uses this name as
        // its EntryPoint; without registration a future Cdecl path would trip the
        // contract check.
        var bridgeSilgenName = $"SBW_{nestedClosures[0].CallbackBaseName}_{method.Name}";
        // same callback-base-name namespace as MethodClosureBridge —
        // the nested-closure variant is distinguished by the per-nested closure's unique
        // CallbackBaseName, owned exclusively by the closure-bridge family. Per-kind
        // method bucket is collision-safe.
        ctx.TryAddMethodWrapperSymbol(bridgeSilgenName);

        // Emit a single Swift wrapper that receives all outer closures' funcPtr/context pairs
        // and dispatches to the original method. The wrapper symbol matches the first outer
        // closure's callback base name (always indexed _0).
        EmitSwiftWrapper(swiftWriter, method, env, parentDecl, nestedClosures, passableNonClosureParams, ctx);

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
                EmitCallback(helperCsWriter, nc.OuterArgs, nc.InnerClosures, nc.CallbackBaseName, env, asyncLibName, bridgeModuleName);
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
                EmitCallback(csWriter, nc.OuterArgs, nc.InnerClosures, nc.CallbackBaseName, env, asyncLibName, bridgeModuleName);
                EmitFunctionPointerField(csWriter, nc.OuterArgs, nc.InnerClosures, nc.CallbackBaseName, env);
            }
            EmitPInvoke(csWriter, method, asyncLibName, nestedClosures, passableNonClosureParams, env);
        }

        // Public method always in the class body
        EmitPublicMethod(csWriter, method, nestedClosures, passableNonClosureParams,
            env, parentDecl, helperClassName);

        method.MarkEmitted();
        return true;
    }

    // ─── Swift Wrapper ─────────────────────────────────────────────────

    /// <summary>
    /// The collision-guarded synthetic Swift identifiers used across the @_cdecl wrapper:
    /// the explicit <c>self</c> pointer param, the <c>__self</c> reconstruction local, and
    /// per-outer-closure <c>cdecl</c> / <c>_box</c> names keyed by outer-closure index.
    /// </summary>
    private readonly record struct ClosureBridgeSyntheticNames(
        string SelfParam,
        string SelfLocal,
        IReadOnlyDictionary<int, string> Cdecl,
        IReadOnlyDictionary<int, string> Box);

    /// <summary>
    /// The @_cdecl wrapper hardcodes synthetic Swift identifiers (<c>self_</c>,
    /// <c>__self</c>, per-outer-closure <c>cdecl</c>/<c>cdecl{N}</c> and <c>_box_{N}</c>). A
    /// user param spelled the same — e.g. <c>func run(self_: Int, outer: …)</c> — would
    /// otherwise produce an "invalid redeclaration" and the generator would emit broken Swift
    /// at exit 0. Reserve every synthetic through a <see cref="SyntheticNameScope"/> seeded
    /// with the user-controlled identifiers in the wrapper's scope (non-closure param names +
    /// each outer closure's <c>FuncPtr</c>/<c>Context</c> params): collision-free input yields
    /// the original names verbatim, collisions get a <c>__</c>-prefixed variant.
    ///
    /// The computed struct is threaded down to the call-emitting helpers
    /// (EmitSingleOuterMethodCall / EmitMultiOuterMethodCall / EmitOuterAdapterBody) which
    /// write into the same Swift function scope — those helpers don't all receive the seed
    /// inputs, so passing the resolved names is simpler than recomputing.
    /// </summary>
    private static ClosureBridgeSyntheticNames ComputeSyntheticNames(
        List<NestedClosureInfo> nestedClosures,
        List<(ArgumentDecl arg, string csName, string csType, MethodClosureBridge.ParamAbiCategory category)> passableNonClosureParams)
    {
        var reserved = new List<string>();
        foreach (var (_, csName, _, _) in passableNonClosureParams)
            reserved.Add(NameProvider.StripVerbatimPrefix(csName));
        foreach (var nc in nestedClosures)
        {
            var n = NameProvider.StripVerbatimPrefix(NameProvider.GetCSharpParameterName(nc.Arg));
            reserved.Add($"{n}FuncPtr");
            reserved.Add($"{n}Context");
        }

        var scope = new SyntheticNameScope(reserved);
        var selfParam = scope.Reserve("self_");
        var selfLocal = scope.Reserve("__self");
        var cdecl = new Dictionary<int, string>();
        var box = new Dictionary<int, string>();
        bool multiOuter = nestedClosures.Count > 1;
        foreach (var nc in nestedClosures)
        {
            cdecl[nc.Index] = scope.Reserve(multiOuter ? $"cdecl{nc.Index}" : "cdecl");
            box[nc.Index] = scope.Reserve($"_box_{nc.Index}");
        }

        return new ClosureBridgeSyntheticNames(selfParam, selfLocal, cdecl, box);
    }

    private static void EmitSwiftWrapper(
        SwiftWriter swiftWriter,
        MethodDecl method,
        MethodEnvironment env,
        TypeDecl? parentDecl,
        List<NestedClosureInfo> nestedClosures,
        List<(ArgumentDecl arg, string csName, string csType, MethodClosureBridge.ParamAbiCategory category)> passableNonClosureParams,
        ModuleEmissionContext? ctx = null)
    {
        // Emit the per-module `_sbWrapClosureContext` helper if any outer closure is escaping
        // — its `_SBClosureCtx` box upcalls SwiftClosureContext.DestroyClosureContext from
        // deinit, freeing the GCHandle exactly once when Swift releases the closure.
        if (nestedClosures.Any(nc => nc.IsEffectivelyEscaping))
            ClosureContextHelperEmitter.EmitIfNeeded(swiftWriter, ctx);

        // Escaping inner closures hand their +1 AnyObject-box retain to a finalizable owner on
        // the C# side; this per-module @_cdecl helper is the release entry that owner calls.
        if (nestedClosures.Any(nc => nc.InnerClosures.Any(ic => ic.Spec.IsEscaping)))
        {
            var moduleName = parentDecl?.SwiftTypeName?.Module ?? method.ModuleDecl?.Name ?? "Module";
            EmitInnerBoxReleaseHelperIfNeeded(swiftWriter, moduleName, ctx);
        }

        bool isInstance = method.MethodType != MethodType.Static && parentDecl != null;
        var typeName = parentDecl?.SwiftTypeName?.ModuleQualifiedName ?? parentDecl?.Name ?? "";
        bool multiOuter = nestedClosures.Count > 1;
        bool parentIsClass = parentDecl is ClassDecl;

        // Collision-guard the wrapper's synthetic Swift identifiers against
        // user-controlled param/closure names. Computed once here and threaded into the
        // call-emitting helpers so every emission site uses the identical resolved name.
        var synth = ComputeSyntheticNames(nestedClosures, passableNonClosureParams);

        // Wrapper symbol is always keyed off the first outer closure's callback base name
        // (_0-indexed). For single-outer methods this produces byte-identical
        // output; for multi-outer, the single wrapper owns all outer closures' ABI pairs.
        var silgenName = $"SBW_{nestedClosures[0].CallbackBaseName}_{method.Name}";

        // Build Swift wrapper params: non-closure passable first, then funcPtr/context per outer closure.
        var swiftParams = new List<string>();

        foreach (var (arg, csName, _, _) in passableNonClosureParams)
        {
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
            var paramName = NameProvider.EscapeSwiftKeyword(csName);
            swiftParams.Add($"    _ {paramName}: {swiftType}");
        }

        foreach (var nc in nestedClosures)
        {
            var closureCsName = NameProvider.StripVerbatimPrefix(NameProvider.GetCSharpParameterName(nc.Arg));
            swiftParams.Add($"    _ {closureCsName}FuncPtr: UnsafeMutableRawPointer?");
            swiftParams.Add($"    _ {closureCsName}Context: UnsafeMutableRawPointer?");
        }

        // SwiftSelf last — instance methods only. Matches the C# P/Invoke signature
        // (which appends SwiftSelf self_) and the CallConvCdecl convention on both sides.
        if (isInstance)
        {
            swiftParams.Add($"    _ {synth.SelfParam}: UnsafeMutableRawPointer");
        }

        // Method return type. `@_cdecl` requires ObjC-representable result types, so class
        // instances (including DynamicSelf on a class) must be bridged to an opaque raw
        // pointer — the C# P/Invoke already declares IntPtr for these.
        var methodReturnSpec = method.CSSignature[0].SwiftTypeSpec;
        bool returnsValue = !methodReturnSpec.IsEmptyTuple;
        bool returnsReference =
            returnsValue &&
            ((methodReturnSpec.IsDynamicSelf && isInstance && parentIsClass) ||
             env.ClosureHandler.IsReferenceType(methodReturnSpec));
        string swiftReturnType;
        if (!returnsValue)
        {
            swiftReturnType = "";
        }
        else if (returnsReference)
        {
            swiftReturnType = " -> UnsafeMutableRawPointer";
        }
        else
        {
            swiftReturnType = $" -> {ExistentialBypassEmitter.RenderSwiftTypeSpecForReturnType(methodReturnSpec)}";
        }

        // Emit wrapper header as a free @_cdecl function. Using @_cdecl (not @_silgen_name inside
        // `extension`) keeps the symbol on the C cdecl ABI so the C# P/Invoke's CallConvCdecl +
        // explicit SwiftSelf parameter lines up with a regular Swift cdecl argument list.
        swiftWriter.WriteLine($"@_cdecl(\"{silgenName}\")");
        swiftWriter.WriteLine($"public func _sb_{silgenName}(");
        swiftWriter.WriteLine(string.Join(",\n", swiftParams));
        swiftWriter.WriteLine($"){swiftReturnType} {{");

        // Reconstruct `self` for instance methods. Classes live behind an Unmanaged pointer;
        // non-frozen structs are passed by raw pointer so we load through .pointee.
        if (isInstance)
        {
            if (parentIsClass)
                swiftWriter.WriteLine($"    let {synth.SelfLocal} = Unmanaged<{typeName}>.fromOpaque({synth.SelfParam}).takeUnretainedValue()");
            else
                swiftWriter.WriteLine($"    let {synth.SelfLocal} = {synth.SelfParam}.assumingMemoryBound(to: {typeName}.self).pointee");
        }

        // Emit inner trampolines for each outer closure. For single-outer, naming matches the
        // pre-multi-outer scheme (innerTrampoline / innerTrampoline0 / innerTrampoline1 …).
        // For multi-outer, we namespace by outer index (innerTrampoline_{o}_{i}).
        foreach (var nc in nestedClosures)
        {
            EmitInnerTrampolinesForOuter(swiftWriter, nc, multiOuter, env);
        }

        // Reconstruct each outer cdecl function from its funcPtr. For escaping outers, also
        // wrap the GCHandle context in a Swift-ARC owner-token `_SBClosureCtx` box; the outer
        // adapter closure captures `_box_N` via its capture list so the box's lifetime tracks
        // the stored closure's. When Swift releases the closure, the box's deinit upcalls the
        // C# free callback.
        foreach (var nc in nestedClosures)
        {
            var closureCsName = NameProvider.StripVerbatimPrefix(NameProvider.GetCSharpParameterName(nc.Arg));
            var cdeclType = BuildOuterCdeclType(nc, env);
            var cdeclVar = synth.Cdecl[nc.Index];
            swiftWriter.WriteLine($"    let {cdeclVar} = unsafeBitCast({closureCsName}FuncPtr!, to: {cdeclType})");

            if (nc.IsEffectivelyEscaping)
            {
                swiftWriter.WriteLine($"    let {synth.Box[nc.Index]}: AnyObject = {ClosureContextHelperEmitter.WrapFunctionName}({closureCsName}Context!)");
            }
        }
        swiftWriter.WriteLine();

        // Emit the method call. For single-outer we keep the existing inline-trailing-closure style
        // (nonClosureArgs come first, then the single closure as the final labeled arg). For multi-outer
        // we iterate the signature in declared order and emit non-closure/closure args interleaved.
        // Reference returns are wrapped with Unmanaged.passRetained(...).toOpaque() so the @_cdecl
        // result type (UnsafeMutableRawPointer) matches the C# P/Invoke's IntPtr return.
        var returnPrefix = returnsValue
            ? (returnsReference ? "return Unmanaged.passRetained(" : "return ")
            : "";
        var returnSuffix = returnsReference ? ").toOpaque()" : "";
        var callTarget = isInstance ? synth.SelfLocal : typeName;
        var methodSwiftName = NameProvider.ParserNameToSwift(method);

        if (!multiOuter)
        {
            EmitSingleOuterMethodCall(swiftWriter, nestedClosures[0], passableNonClosureParams,
                returnPrefix, returnSuffix, callTarget, methodSwiftName, multiOuter: false, env: env, synth: synth);
        }
        else
        {
            EmitMultiOuterMethodCall(swiftWriter, method, nestedClosures, passableNonClosureParams,
                returnPrefix, returnSuffix, callTarget, methodSwiftName, env, synth);
        }

        swiftWriter.WriteLine("}");
        swiftWriter.WriteLine();
    }

    /// <summary>
    /// Emits inner-closure trampolines for one outer closure. Naming respects single-outer legacy
    /// ("innerTrampoline", "innerTrampoline0", "innerTrampoline1") vs multi-outer namespaced form
    /// ("innerTrampoline_{outerIdx}_{innerIdx}").
    /// </summary>
    private static void EmitInnerTrampolinesForOuter(
        SwiftWriter swiftWriter,
        NestedClosureInfo nc,
        bool multiOuter,
        MethodEnvironment env)
    {
        bool multiInner = nc.InnerClosures.Count > 1;

        for (int j = 0; j < nc.InnerClosures.Count; j++)
        {
            var ic = nc.InnerClosures[j];
            var innerArgs = ic.Args;
            var innerClosureSpec = ic.Spec;

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

            var trampolineName = InnerTrampolineName(multiOuter, multiInner, nc.Index, j);
            var boxSuffix = InnerBoxSuffix(multiOuter, multiInner, nc.Index, j);

            var innerTrampolineParams = new List<string>();
            for (int i = 0; i < innerArgs.Count; i++)
            {
                innerTrampolineParams.Add($"_ __ip{i}: {GetSwiftCdeclParamType(innerArgs[i], env)}");
            }
            innerTrampolineParams.Add($"_ __closureBox{boxSuffix}: UnsafeMutableRawPointer");

            swiftWriter.WriteLine($"    let {trampolineName}: {innerTrampolineType} = {{ {string.Join(", ", innerTrampolineParams.Select(p => p.Split(' ')[1].TrimEnd(':')))} in");

            // Uses takeUnretainedValue (no retain change) — the box keeps the inner closure alive
            // via the adapter's passRetained(+1), so a borrow here is safe across multiple inner
            // calls during the outer invocation. The adapter balances that +1 with a release after
            // cdecl() returns for non-escaping inner closures; escaping inner closures keep
            // the box leaked because the borrow may outlive the outer call.
            swiftWriter.WriteLine($"        let innerClosure = Unmanaged<AnyObject>.fromOpaque(__closureBox{boxSuffix}).takeUnretainedValue() as! {innerClosureSwiftType}");

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
    }

    /// <summary>
    /// Builds the @convention(c) type string for an outer closure's cdecl callback:
    /// (@convention(c) (outerArgs..., outerContext) -> Void).self
    /// </summary>
    private static string BuildOuterCdeclType(NestedClosureInfo nc, MethodEnvironment env)
    {
        var innerIndices = nc.InnerClosures.Select(ic => ic.OuterArgIndex).ToHashSet();
        var cdeclParamTypes = new List<string>();
        for (int i = 0; i < nc.OuterArgs.Count; i++)
        {
            if (innerIndices.Contains(i))
            {
                cdeclParamTypes.Add("UnsafeMutableRawPointer?"); // innerFuncPtr
                cdeclParamTypes.Add("UnsafeMutableRawPointer?"); // innerContext
            }
            else
            {
                cdeclParamTypes.Add(GetSwiftCdeclParamType(nc.OuterArgs[i], env));
            }
        }
        cdeclParamTypes.Add("UnsafeMutableRawPointer?"); // outer context
        return $"(@convention(c) ({string.Join(", ", cdeclParamTypes)}) -> Void).self";
    }

    /// <summary>
    /// Single-outer emission path — preserves the pre-multi-outer byte-identical Swift output.
    /// Non-closure passable args are emitted first, then the single outer closure as a labeled
    /// inline closure argument.
    /// </summary>
    private static void EmitSingleOuterMethodCall(
        SwiftWriter swiftWriter,
        NestedClosureInfo nc,
        List<(ArgumentDecl arg, string csName, string csType, MethodClosureBridge.ParamAbiCategory category)> passableNonClosureParams,
        string returnPrefix,
        string returnSuffix,
        string callTarget,
        string methodSwiftName,
        bool multiOuter,
        MethodEnvironment env,
        ClosureBridgeSyntheticNames synth)
    {
        var callLabel = GetSwiftArgLabel(nc.Arg);

        var nonClosureCallArgs = new List<string>();
        foreach (var (arg, csName, _, _) in passableNonClosureParams)
        {
            var label = GetSwiftArgLabel(arg);
            var paramName = NameProvider.EscapeSwiftKeyword(csName);
            nonClosureCallArgs.Add($"{label}{paramName}");
        }

        var outerParamDecls = new List<string>();
        for (int i = 0; i < nc.OuterArgs.Count; i++)
        {
            outerParamDecls.Add($"__op{i}");
        }
        var outerParamStr = string.Join(", ", outerParamDecls);

        var prefixStr = nonClosureCallArgs.Count > 0
            ? string.Join(", ", nonClosureCallArgs) + ", "
            : "";

        // Escaping outer closures explicitly capture their `_box_N` owner-token. The capture pulls
        // the box into the stored closure so Swift ARC tracks its lifetime — when Swift releases
        // the closure, the box's deinit upcalls the C# free callback.
        var captureList = nc.IsEffectivelyEscaping ? $"[{synth.Box[nc.Index]}] " : "";
        swiftWriter.WriteLine($"    {returnPrefix}{callTarget}.{methodSwiftName}({prefixStr}{callLabel}{{ {captureList}{outerParamStr} in");
        EmitOuterAdapterBody(swiftWriter, nc, multiOuter, indent: "        ", env, synth);
        swiftWriter.WriteLine($"    }}){returnSuffix}");
    }

    /// <summary>
    /// Multi-outer emission path — iterates declared parameter order, emitting each non-closure arg
    /// as a labeled pass-through and each outer closure as a labeled inline closure expression.
    /// </summary>
    private static void EmitMultiOuterMethodCall(
        SwiftWriter swiftWriter,
        MethodDecl method,
        List<NestedClosureInfo> nestedClosures,
        List<(ArgumentDecl arg, string csName, string csType, MethodClosureBridge.ParamAbiCategory category)> passableNonClosureParams,
        string returnPrefix,
        string returnSuffix,
        string callTarget,
        string methodSwiftName,
        MethodEnvironment env,
        ClosureBridgeSyntheticNames synth)
    {
        var passableByArg = passableNonClosureParams.ToDictionary(p => p.arg);
        var nestedByArg = nestedClosures.ToDictionary(n => n.Arg);

        // Collect emittable args in source order (skip default-arg params we don't pass).
        var emitOrder = new List<ArgumentDecl>();
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (nestedByArg.ContainsKey(arg) || passableByArg.ContainsKey(arg))
                emitOrder.Add(arg);
        }

        swiftWriter.WriteLine($"    {returnPrefix}{callTarget}.{methodSwiftName}(");

        for (int k = 0; k < emitOrder.Count; k++)
        {
            var arg = emitOrder[k];
            var isLast = k == emitOrder.Count - 1;
            var trailingComma = isLast ? "" : ",";
            var label = GetSwiftArgLabel(arg);

            if (nestedByArg.TryGetValue(arg, out var nc))
            {
                var outerParamDecls = new List<string>();
                for (int i = 0; i < nc.OuterArgs.Count; i++)
                    outerParamDecls.Add($"__op{i}");
                var outerParamStr = string.Join(", ", outerParamDecls);

                // Escaping outer: capture `_box_N` to track its lifetime via Swift ARC.
                var captureList = nc.IsEffectivelyEscaping ? $"[{synth.Box[nc.Index]}] " : "";
                swiftWriter.WriteLine($"        {label}{{ {captureList}{outerParamStr} in");
                EmitOuterAdapterBody(swiftWriter, nc, multiOuter: true, indent: "            ", env, synth);
                swiftWriter.WriteLine($"        }}{trailingComma}");
            }
            else
            {
                var (_, csName, _, _) = passableByArg[arg];
                var paramName = NameProvider.EscapeSwiftKeyword(csName);
                swiftWriter.WriteLine($"        {label}{paramName}{trailingComma}");
            }
        }

        swiftWriter.WriteLine($"    ){returnSuffix}");
    }

    /// <summary>
    /// Emits the body of one outer closure's adapter — boxes inner closures, bitcasts the matching
    /// trampoline, converts non-closure outer args, and calls the cdecl function with outerContext last.
    /// </summary>
    private static void EmitOuterAdapterBody(
        SwiftWriter swiftWriter,
        NestedClosureInfo nc,
        bool multiOuter,
        string indent,
        MethodEnvironment env,
        ClosureBridgeSyntheticNames synth)
    {
        bool multiInner = nc.InnerClosures.Count > 1;
        var cdeclVar = synth.Cdecl[nc.Index];
        var closureCsName = NameProvider.StripVerbatimPrefix(NameProvider.GetCSharpParameterName(nc.Arg));
        var contextVar = $"{closureCsName}Context";

        // Observe the captured box explicitly so the optimizer cannot elide it. Without
        // this, the capture-list-only reference can be dropped, breaking the lifetime
        // contract that drives the deinit upcall.
        if (nc.IsEffectivelyEscaping)
            swiftWriter.WriteLine($"{indent}_ = {synth.Box[nc.Index]}");

        var cdeclCallArgs = new List<string>();
        var innerBoxesToRelease = new List<string>();
        for (int i = 0; i < nc.OuterArgs.Count; i++)
        {
            var innerMatch = nc.InnerClosures.FindIndex(ic => ic.OuterArgIndex == i);
            if (innerMatch >= 0)
            {
                var trampolineName = InnerTrampolineName(multiOuter, multiInner, nc.Index, innerMatch);
                var suffix = InnerBoxSuffix(multiOuter, multiInner, nc.Index, innerMatch);
                swiftWriter.WriteLine($"{indent}let __innerBox{suffix} = Unmanaged.passRetained(__op{i} as AnyObject).toOpaque()");
                swiftWriter.WriteLine($"{indent}let __innerFuncPtr{suffix} = unsafeBitCast({trampolineName}, to: UnsafeMutableRawPointer?.self)");
                cdeclCallArgs.Add($"__innerFuncPtr{suffix}");
                cdeclCallArgs.Add($"__innerBox{suffix}");
                // A non-escaping inner closure is valid only for the duration of this outer-closure
                // invocation, so the +1 box retain (passRetained above) must be balanced once cdecl()
                // returns. The inner trampoline borrows the box via takeUnretainedValue, so the box
                // stays alive across however many times the inner closure is called during the outer
                // call; we only drop our +1 after.
                // An escaping inner closure must outlive the call, so its +1 transfers to the
                // managed side: the C# callback wraps the box in a finalizable owner captured by
                // the inner delegate, and the owner balances the retain through the wrapper's
                // per-module release helper once the delegate becomes unreachable.
                if (!nc.InnerClosures[innerMatch].Spec.IsEscaping)
                    innerBoxesToRelease.Add($"__innerBox{suffix}");
            }
            else
            {
                cdeclCallArgs.Add(GetSwiftOuterArgConversion(nc.OuterArgs[i], $"__op{i}", env));
            }
        }
        cdeclCallArgs.Add(contextVar); // outer context

        swiftWriter.WriteLine($"{indent}{cdeclVar}({string.Join(", ", cdeclCallArgs)})");
        foreach (var box in innerBoxesToRelease)
            swiftWriter.WriteLine($"{indent}Unmanaged<AnyObject>.fromOpaque({box}).release()");
    }

    private static string InnerTrampolineName(bool multiOuter, bool multiInner, int outerIndex, int innerIndex)
    {
        if (multiOuter)
            return $"innerTrampoline_{outerIndex}_{innerIndex}";
        return multiInner ? $"innerTrampoline{innerIndex}" : "innerTrampoline";
    }

    private static string InnerBoxSuffix(bool multiOuter, bool multiInner, int outerIndex, int innerIndex)
    {
        if (multiOuter)
            return $"_{outerIndex}_{innerIndex}";
        return multiInner ? $"{innerIndex}" : "";
    }

    // ─── Escaping inner-box release helper ─────────────────────────────

    /// <summary>
    /// The per-module @_cdecl symbol that balances the +1 retain minted on an escaping
    /// inner closure's AnyObject box once the managed inner delegate is collected.
    /// </summary>
    internal static string GetInnerBoxReleaseSymbolName(string moduleName)
        => $"SBW_NCB_ReleaseInnerBox_{moduleName}";

    /// <summary>
    /// Emits the escaping inner-box release helper into the Swift wrapper, once per release
    /// symbol. The symbol is namespaced by the PARENT decl's module — a cross-module extension
    /// names a foreign module — so a single emitted module can require several distinct helpers;
    /// gating on anything coarser (e.g. once per context) would leave a later parent module's
    /// DllImport pointing at a symbol the wrapper never defines, and the finalizer's
    /// swallow-everything catch would turn that into a silent per-box leak.
    /// The helper lives in the wrapper library (not the shared runtime native) because every
    /// nested-closure P/Invoke already targets it, so the symbol is guaranteed loadable even
    /// in hosts that run without the runtime's native companion.
    /// </summary>
    private static void EmitInnerBoxReleaseHelperIfNeeded(
        SwiftWriter swiftWriter, string moduleName, ModuleEmissionContext? ctx)
    {
        ctx ??= ModuleEmissionContext.Default;
        var symbol = GetInnerBoxReleaseSymbolName(moduleName);
        if (!ctx.TryAddNcbInnerBoxReleaseSymbol(symbol))
            return;

        // The matching C# DllImport (the box owner's finalizer) targets this EntryPoint;
        // register it with the wrapper-symbol contract like the bridge P/Invoke above.
        ctx.TryAddMethodWrapperSymbol(symbol);
        // The Swift function name reuses the symbol so two helpers in one wrapper file
        // (distinct parent modules) don't collide as same-named Swift declarations.
        swiftWriter.WriteLines($$"""
            // Balances the +1 retain (Unmanaged.passRetained) an outer-closure adapter mints on
            // an escaping inner closure's AnyObject box. Ownership of that retain transfers to a
            // generated C# owner captured by the managed inner delegate; the owner's finalizer
            // calls this once the delegate is unreachable. Keeping the release inside the wrapper
            // gives the GC finalizer thread a single Cdecl boundary into our own dylib instead of
            // a direct libswiftCore call, which is unsafe on that thread under Mono.
            @_cdecl("{{symbol}}")
            public func {{symbol}}(_ box: UnsafeMutableRawPointer) {
                Unmanaged<AnyObject>.fromOpaque(box).release()
            }

            """);
    }

    // ─── C# Callback ───────────────────────────────────────────────────

    private static void EmitCallback(
        CSharpWriter csWriter,
        List<TypeSpec> outerArgs,
        List<InnerClosureInfo> innerClosures,
        string callbackBaseName,
        MethodEnvironment env,
        string asyncLibName,
        string moduleName)
    {
        bool multiInner = innerClosures.Count > 1;
        var innerIndices = innerClosures.Select(ic => ic.OuterArgIndex).ToHashSet();

        // Escaping inner closures transfer their Swift-side +1 box retain to a finalizable
        // owner captured by the managed inner delegate; emit the owner type once per callback.
        if (innerClosures.Any(ic => ic.Spec.IsEscaping))
            EmitInnerBoxOwnerClass(csWriter, callbackBaseName, asyncLibName, moduleName);

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

        // Resolve the outer delegate from the GCHandle inside the guarded try so a
        // bad/freed handle (handle.Target throwing) faults via FailFast rather than unwinding
        // out of the [UnmanagedCallersOnly] frame into the Swift @_cdecl caller → SIGABRT.
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine($"var callback = ({outerDelegateType})handle.Target!;");

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

                // For an escaping inner closure, adopt the +1 retain the Swift adapter minted
                // on its box: the owner is captured by the delegate below and its finalizer
                // releases the box once the delegate becomes unreachable.
                bool adoptsInnerBox = ic.Spec.IsEscaping;
                if (adoptsInnerBox)
                    csWriter.WriteLine($"var __innerBoxOwner{suffix} = new {callbackBaseName}_InnerBoxOwner(innerContext{suffix});");

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
                    if (adoptsInnerBox)
                        EmitInnerBoxKeepAlive(csWriter, suffix);
                    if (innerClosureSpec.ReturnType is NamedTypeSpec innerRetNamed && innerRetNamed.Name == "Swift.Bool")
                        csWriter.WriteLine($"return __innerRet != 0;");
                    else
                        csWriter.WriteLine($"return ({innerReturnCSharpType})__innerRet;");
                }
                else
                {
                    csWriter.WriteLine($"(({innerDelegatePtrType})innerFuncPtr{suffix})({string.Join(", ", innerCallArgs)});");
                    if (adoptsInnerBox)
                        EmitInnerBoxKeepAlive(csWriter, suffix);
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
        ClosureEmitter.EmitNonThrowingFailFastCatch(csWriter);

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    /// <summary>
    /// Emits the keep-alive that pins the inner-box owner (and thus the box's +1 retain)
    /// across the native trampoline call, so a GC during the call cannot finalize the owner
    /// while Swift is still executing against the box.
    /// </summary>
    private static void EmitInnerBoxKeepAlive(CSharpWriter csWriter, string suffix)
    {
        csWriter.WriteLine($"GC.KeepAlive(__innerBoxOwner{suffix});");
    }

    /// <summary>
    /// Emits the finalizable owner that adopts the +1 retain the Swift adapter minted on an
    /// escaping inner closure's AnyObject box. The inner delegate captures one owner per box;
    /// the delegate is the only path that can ever reach the box again, so once it becomes
    /// unreachable the box is provably dead and the finalizer releases it through the
    /// wrapper's own @_cdecl helper — a single Cdecl boundary, safe on the finalizer thread.
    /// </summary>
    private static void EmitInnerBoxOwnerClass(
        CSharpWriter csWriter, string callbackBaseName, string asyncLibName, string moduleName)
    {
        var ownerName = $"{callbackBaseName}_InnerBoxOwner";
        var releaseSymbol = GetInnerBoxReleaseSymbolName(moduleName);

        csWriter.WriteLine($"private sealed class {ownerName}");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("private readonly IntPtr _box;");
        csWriter.WriteLine($"internal {ownerName}(IntPtr box) => _box = box;");
        csWriter.WriteLine();
        csWriter.WriteLine($"[DllImport(\"{asyncLibName}\", EntryPoint = \"{releaseSymbol}\", CallingConvention = CallingConvention.Cdecl)]");
        csWriter.WriteLine("private static extern void ReleaseInnerBox(IntPtr box);");
        csWriter.WriteLine();
        csWriter.WriteLine($"~{ownerName}()");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("// Never throw from a finalizer. Skipping at shutdown (or on a native fault)");
        csWriter.WriteLine("// degrades to leaking one closure box, which the OS reclaims with the process.");
        csWriter.WriteLine("if (_box == IntPtr.Zero || global::System.Environment.HasShutdownStarted)");
        csWriter.Indent++;
        csWriter.WriteLine("return;");
        csWriter.Indent--;
        csWriter.WriteLine("try { ReleaseInnerBox(_box); } catch { }");
        csWriter.Indent--;
        csWriter.WriteLine("}");
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
            // The trailing self param hardcodes `self_`; a user non-closure param
            // projected to the same name would be a CS0100 duplicate. Guard it against the
            // other P/Invoke param names. Call-site args are positional, so the renamed param
            // needs no call-site change.
            var pinvokeReserved = new List<string>();
            foreach (var (_, csName, _, _) in passableNonClosureParams)
                pinvokeReserved.Add(csName);
            var selfPInvokeName = new SyntheticNameScope(pinvokeReserved).Reserve("self_");
            pinvokeParams.Add($"SwiftSelf {selfPInvokeName}");
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
            Visibility = PInvokeVisibility.Internal,
            EmissionContext = env.EmissionContext,
            EnforceWrapperContract = true
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

        // Use env.CSharpMethodName so the projected-signature collision suffix from
        // IHandler.HandleBaseDecl (CollisionIndex) reaches the emitted public method.
        // Mirror of MethodClosureBridge.EmitPublicMethod.
        var methodName = env.CSharpMethodName;

        var isStatic = method.MethodType == MethodType.Static;
        var staticKeyword = isStatic ? "static " : "";

        XmlDocCommentEmitter.EmitMethodDocComment(csWriter, method);

        csWriter.WriteLine($"public {staticKeyword}unsafe {returnType} {methodName}({string.Join(", ", publicParams)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // When in a generic type, callback pointer and P/Invoke live in the helper class
        var helperPrefix = string.IsNullOrEmpty(helperClassName) ? "" : $"{helperClassName}.";

        // Pre-declare GCHandle (and per-escaping-closure transfer flag) at method scope so a
        // throw between alloc and the P/Invoke returning successfully (e.g. ObjectDisposedException
        // from Payload.DangerousGetHandle on a previous arg, or DllNotFoundException on entry-point
        // resolution) frees the handle in `finally`. For escaping outer closures Swift assumes
        // lifetime ownership through the `_SBClosureCtx` box deinit upcall on the happy path; the
        // finally only frees handles whose ownership never moved into Swift. For non-escaping
        // outers the trampoline fires synchronously inside the call and the handle becomes
        // unreachable on return — same behaviour as MCB / pre-existing NCB output.
        foreach (var nc in nestedClosures)
        {
            csWriter.WriteLine($"GCHandle __gcHandle_{nc.Index} = default;");
            if (nc.IsEffectivelyEscaping)
            {
                csWriter.WriteLine($"bool __transferred_{nc.Index} = false;");
            }
        }

        // Always wrap the alloc + P/Invoke in try/finally so EVERY outer closure's GCHandle is
        // released — escaping handles transferred to Swift's `_SBClosureCtx` box are left alive
        // by the `!__transferred` gate, while non-escaping handles (invoked synchronously inside
        // the call, never owned by Swift) are freed unconditionally on return. Gating the
        // finally on `anyEscaping` previously leaked the GCHandle for every non-escaping outer.
        bool hasClosures = nestedClosures.Count > 0;
        if (hasClosures)
        {
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
        }

        // Allocate GCHandle for each outer closure delegate. Allocs live inside
        // the try so that an OOM mid-loop frees any handle already taken.
        foreach (var nc in nestedClosures)
        {
            csWriter.WriteLine($"__gcHandle_{nc.Index} = GCHandle.Alloc({nc.ParamName});");
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
            EmitClosureOwnershipTransferred(csWriter, nestedClosures);
            csWriter.WriteLine($"return ({returnType})SwiftMarshal.MarshalFromSwift<{returnType}>(__result);");
        }
        else if (!returnSpec.IsEmptyTuple)
        {
            // Primitive return — capture before marking transfer so the flag is only flipped
            // on a successful P/Invoke return.
            csWriter.WriteLine($"var __pinvokeResult = {helperPrefix}{pInvokeName}({string.Join(", ", callArgs)});");
            EmitClosureOwnershipTransferred(csWriter, nestedClosures);
            csWriter.WriteLine("return __pinvokeResult;");
        }
        else
        {
            csWriter.WriteLine($"{helperPrefix}{pInvokeName}({string.Join(", ", callArgs)});");
            EmitClosureOwnershipTransferred(csWriter, nestedClosures);
        }

        if (hasClosures)
        {
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            foreach (var nc in nestedClosures)
            {
                if (nc.IsEffectivelyEscaping)
                    csWriter.WriteLine($"if (!__transferred_{nc.Index} && __gcHandle_{nc.Index}.IsAllocated) __gcHandle_{nc.Index}.Free();");
                else
                    csWriter.WriteLine($"if (__gcHandle_{nc.Index}.IsAllocated) __gcHandle_{nc.Index}.Free();");
            }
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    /// <summary>
    /// Emits the per-closure `__transferred_{index} = true;` lines for all escaping outer closures,
    /// to be placed immediately after a successful P/Invoke call in <see cref="EmitPublicMethod"/>.
    /// Paired with the finally block emitted there which frees handles when transfer is still false.
    /// </summary>
    private static void EmitClosureOwnershipTransferred(CSharpWriter csWriter, List<NestedClosureInfo> nestedClosures)
    {
        foreach (var nc in nestedClosures)
        {
            if (!nc.IsEffectivelyEscaping) continue;
            csWriter.WriteLine($"__transferred_{nc.Index} = true;");
        }
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
            if (IsOptionalReferenceType(named, env.ClosureHandler))
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
            if (IsOptionalReferenceType(named, env.ClosureHandler))
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
    /// Checks if a type is <c>Optional&lt;class&gt;</c> with single-nullable-pointer ABI. Gated by the
    /// shared <see cref="ClosureHandler.IsOptionalReferenceArg"/> (true only for a true reference inner),
    /// the same predicate every other closure bridge uses for this position. An
    /// <c>Optional&lt;value-type&gt;</c> closure arg is excluded: Swift passes its value representation
    /// across the closure boundary, so it must not be read as an object pointer here.
    /// </summary>
    private static bool IsOptionalReferenceType(TypeSpec typeSpec, ClosureHandler closureHandler)
        => closureHandler.IsOptionalReferenceArg(typeSpec);

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
            if (IsOptionalReferenceType(named, env.ClosureHandler))
            {
                var innerCsType = GetCSharpTypeForOuterArg(named.GenericParameters[0], env);
                return $"{innerCsType}?";
            }

            if (named.Name == "Swift.Bool") return "bool";
            if (MarshallingHelpers.IsSwiftPrimitive(named.Name))
                return MarshallingHelpers.MapSwiftPrimitiveToCSharpType(named.Name);

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
    /// <summary>
    /// Gets the C# callback-delegate parameter type for a closure argument.
    /// Delegates to the canonical implementation in SwiftBuilder.
    /// </summary>
    private static string GetCallbackParamType(TypeSpec argType, MethodEnvironment env)
        => SwiftBuilder.GetCSharpCallbackParamType(argType, env.ClosureHandler);

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
            if (IsOptionalReferenceType(named, env.ClosureHandler))
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
            if (IsOptionalReferenceType(named, env.ClosureHandler))
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
                return (MarshallingHelpers.MapSwiftPrimitiveToCSharpType(((NamedTypeSpec)typeSpec).Name), category);

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
                return MarshallingHelpers.MapSwiftPrimitiveToCSharpType(namedRet.Name);

            if (env.TypeDatabase.TryGetTypeRecord(returnSpec, out var record))
                return record.CSharpTypeName.FullyQualifiedName;
        }

        return "IntPtr";
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
    /// Gets the Swift argument label for a parameter. Delegates to the canonical
    /// <see cref="CdeclParamMapper.BuildSwiftCallArgLabel"/> (provenance-aware) so labels that
    /// genuinely begin with '_' (e.g. <c>_self</c>) are not corrupted by the legacy
    /// underscore-stripping recovery.
    /// </summary>
    private static string GetSwiftArgLabel(ArgumentDecl arg)
        => CdeclParamMapper.BuildSwiftCallArgLabel(arg);
}
