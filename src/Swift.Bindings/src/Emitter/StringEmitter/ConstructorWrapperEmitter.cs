// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits per-constructor @_cdecl Swift wrappers that route constructor P/Invokes
/// through C calling convention, eliminating CallConvSwift ABI mismatches on NativeAOT/ARM64.
///
/// For each constructor, generates a @_cdecl free function in the wrapper library that:
/// - Receives C-compatible parameters (primitives pass through, structs/classes as UnsafeRawPointer)
/// - Calls the actual Swift init
/// - Returns the result via C ABI (class → retained pointer, struct → writes to result buffer)
///
/// Handles failable (init?), throwing (init() throws), and combined (init?() throws) constructors.
/// State tracked on <see cref="ModuleEmissionContext"/>.
/// </summary>
public static class ConstructorWrapperEmitter
{
    /// <summary>
    /// Pure query: determines whether a constructor should use a @_cdecl wrapper.
    /// Guards: xcframework mode (wrapper lib exists), non-generic parent type,
    /// no closure parameters (deferred to follow-up).
    /// </summary>
    public static bool ShouldEmitWrapper(MethodEnvironment env)
    {
        if (!env.MethodDecl.IsConstructor)
            return false;

        // Shared guards: xcframework, internal, non-copyable, async, actor isolation, inherited generic context
        if (!WrapperValidation.CanEmitMember(env, MemberKind.Constructor,
            isModuleInternal: env.MethodDecl.IsModuleInternal,
            isAsync: env.MethodDecl.IsAsync,
            isActorIsolated: env.MethodDecl.IsActorIsolated,
            isMainActorIsolated: env.MethodDecl.IsMainActorIsolated,
            isNonisolated: env.MethodDecl.IsNonisolated))
            return false;

        // Skip failable inits on non-frozen struct types.
        // Non-frozen struct failable inits already work through CallConvSwift on Mono
        // (the VWT operations in TryCreate are compatible). Routing them through @_cdecl
        // can cause memory corruption because the Optional<T>.initialize(to:) in the
        // Swift wrapper interacts poorly with the VWT-based tag/copy operations in TryCreate.
        if (env.MethodDecl.IsFailable && env.ParentDecl is StructDecl failableStruct &&
            !failableStruct.IsFrozen)
            return false;

        // Generic parent type — allow constructors using protocol-based type erasure.
        // (inherited generic context is already checked by CanEmitMember)
        if (env.ParentDecl is TypeDecl typeDecl && typeDecl.IsGeneric)
        {
            if (!CanEmitGenericConstructorWrapper(env, typeDecl))
                return false;
        }

        // Closure parameters: allowed only when NeedsClosureCdeclWrapper validates them
        // AND no plain async closures (GetSwiftClosureAdapterCode only emits sync adapters).
        if (env.MethodDecl.CSSignature.Skip(1).Any(env.ClosureHandler.IsClosure))
        {
            if (!ClosureEmitter.NeedsClosureCdeclWrapper(env.MethodDecl, env.ClosureHandler))
                return false;
            if (HasAnyAsyncClosure(env))
                return false;
        }

        // Skip constructors with metatype parameters (including Optional<Metatype>).
        // Metatypes aren't C-representable: the wrapper would render a bare "Type" token
        // through CdeclParamMapper.Map and the generated C# fails to compile. Same boundary
        // as the method-level gate (MethodWrapperEmitter.HasUnsupportedTypeSignature 14b).
        if (env.MethodDecl.CSSignature.Skip(1)
                .Any(a => WrapperValidation.IsMetatypeTypeIncludingOptional(a.SwiftTypeSpec)))
            return false;

        // Skip constructors with non-copyable (~Copyable) struct parameters.
        // The @_cdecl wrapper passes frozen structs by value through the C ABI, which
        // requires copying. Non-copyable types can't be copied, so the wrapper won't compile.
        // C# passes frozen structs by value too, so there's no pointer fallback available.
        if (HasNonCopyableStructParameter(env))
            return false;

        // Skip constructors with nested frozen struct parameters.
        // @_cdecl can't represent nested Swift types (e.g. NestedOuter.Inner) in C ABI.
        // The Swift compiler rejects these with: "type of the parameter cannot be represented in Objective-C".
        if (HasNestedFrozenStructParameter(env))
            return false;

        // Skip constructors with unsupported buffer pointer parameters
        // (UnsafeBufferPointer<T>, UnsafeMutableBufferPointer<T>).
        // UnsafeRawBufferPointer / UnsafeMutableRawBufferPointer are supported via
        // CdeclParamMapper (split into ptr+len at the C ABI boundary).
        if (HasUnsupportedBufferPointerParameter(env))
            return false;

        // Skip constructors with raw ABI generic type params (τ_0_0) in signature,
        // UNLESS the parent is a generic type (where T params are handled by static factory dispatch).
        if (WrapperValidation.HasRawGenericTypeParams(env.MethodDecl))
        {
            if (!(env.ParentDecl is TypeDecl rawGenTd && rawGenTd.IsGeneric))
                return false;
        }

        // Skip constructors with variadic parameters detected from the demangler.
        // Swift variadic params (T...) appear as Array<T> in ABI JSON. The @_cdecl wrapper
        // would pass [T] where T... is expected, causing a compilation error.
        if (env.MethodDecl.HasVariadicParameter)
            return false;

        // Skip constructors with `_const` (compile-time-constant) parameters — e.g.
        // AppIntents.IntentCollectionSize.init(min: _const Int, max: _const Int).
        // The @_cdecl wrapper passes runtime values; Swift rejects the call with
        // "expect a compile-time constant literal". ABI JSON strips this annotation;
        // the flag is sourced from the swiftinterface via SwiftABIParser. Shared with
        // CSM via ConstructorAdmissibility so all erasure paths drop `_const` inits alike.
        if (ConstructorAdmissibility.HasConstLiteralParameter(env.MethodDecl))
            return false;

        // Skip constructors with variadic expansion pattern: N individual protocol params
        // followed by Array<SameProtocol>. The wrapper passes the array as a positional arg,
        // but Swift resolves to the variadic overload causing type mismatch.
        // E.g., CompositeDisposable(_:_:_:_:_:) with 4x Disposable + 1x [Disposable].
        if (HasVariadicExpansionPattern(env))
            return false;

        return true;
    }

    /// <summary>
    /// Detects variadic expansion pattern in ABI JSON.
    /// Swift expands `init(_ args: T...)` as `init(_:_:..._:)` with N individual `T` params
    /// + one trailing `Array&lt;T&gt;`. ALL params must be unnamed (`_:`) — labeled params like
    /// `init(primary: T, all: [T])` are genuine overloads, not variadic expansions.
    /// The wrapper can't call these correctly because Swift overload resolution picks the variadic
    /// overload and rejects the array argument as a non-conforming type.
    /// </summary>
    internal static bool HasVariadicExpansionPattern(MethodEnvironment env)
    {
        var args = env.MethodDecl.CSSignature.Skip(1).ToList(); // skip return type
        if (args.Count < 2) return false;

        var lastArg = args[args.Count - 1];
        // Check if last param is Array<T>
        if (lastArg.SwiftTypeSpec is not NamedTypeSpec lastNamed) return false;
        if ((lastNamed.Name != "Array" && lastNamed.Name != "Swift.Array") || !lastNamed.GenericParameters.Any()) return false;

        var elementType = lastNamed.GenericParameters[0].ToString();

        // ALL params (including the trailing array) must be unnamed (`_:`)
        // to distinguish ABI variadic expansion from genuine labeled overloads.
        // The parser renames `_` params to `arg0`, `arg1`, etc. via ExtractParameterNames,
        // so we check for the generated `argN` pattern as well.
        if (args.Any(a => !string.IsNullOrEmpty(a.Name) && a.Name != "_" &&
            !System.Text.RegularExpressions.Regex.IsMatch(a.Name, @"^arg\d+$")))
            return false;

        // Check if at least one preceding param has the same element type
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i].SwiftTypeSpec.ToString() == elementType)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks whether any closure parameter is an async closure. Session A's
    /// async-closure bridge routes only through the method-wrapper path; all
    /// async closures on constructors are still rejected here.
    /// </summary>
    private static bool HasAnyAsyncClosure(MethodEnvironment env)
        => env.MethodDecl.CSSignature.Skip(1)
            .Where(env.ClosureHandler.IsClosure)
            .Any(arg =>
            {
                var spec = env.ClosureHandler.GetClosureTypeSpec(arg);
                return spec != null && env.ClosureHandler.IsAsyncClosure(spec);
            });

    /// <summary>
    /// Checks whether any constructor parameter is a protocol existential type.
    /// Covers two forms that produce ABI/semantic mismatches with the C# P/Invoke:
    /// 1. ProtocolListTypeSpec or NamedTypeSpec.IsAny — PInvokeEmitter emits ExistentialContainer by value
    /// 2. NamedTypeSpec resolving to Protocol/Existential TypeRecord — PInvokeEmitter emits SafeHandle
    /// Both mismatch the @_cdecl wrapper's UnsafeRawPointer expectation.
    /// </summary>
    private static bool HasProtocolExistentialParameter(MethodEnvironment env)
    {
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            // Form 1: ProtocolListTypeSpec or IsAny (caught by ExistentialHandler)
            if (env.ExistentialHandler.IsExistential(arg.SwiftTypeSpec))
                return true;

            // Form 2: NamedTypeSpec resolving to Protocol/Existential TypeRecord
            if (arg.SwiftTypeSpec is NamedTypeSpec namedSpec &&
                env.TypeDatabase.TryGetTypeRecord(namedSpec, out var typeRecord) &&
                (typeRecord.Kind == TypeRecordKind.Protocol || typeRecord.Kind == TypeRecordKind.Existential))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Checks whether any constructor parameter is a non-copyable (~Copyable) frozen struct.
    /// Non-copyable types are detected by an explicit Swift.Escapable conformance in their
    /// TypeDecl (normal Copyable types have both Copyable and Escapable implicitly, unlisted).
    /// For cross-module types where the StructDecl isn't available, falls back to the
    /// NonCopyable flag on the TypeRecord.
    /// </summary>
    internal static bool HasNonCopyableStructParameter(MethodEnvironment env)
    {
        var moduleTypes = env.MethodDecl.ModuleDecl?.Types;

        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec is not NamedTypeSpec namedSpec)
                continue;

            // Try same-module StructDecl first (has full conformance info)
            if (moduleTypes != null)
            {
                var paramStructDecl = FindStructDecl(moduleTypes, namedSpec.Name);
                if (paramStructDecl != null)
                {
                    // In Swift 6.2+, ALL types list both Copyable and Escapable.
                    // Non-copyable types list Escapable WITHOUT Copyable.
                    if (paramStructDecl.Conformances.Any(c => c.Protocol.ToString() == "Swift.Escapable") &&
                        !paramStructDecl.Conformances.Any(c => c.Protocol.ToString() == "Swift.Copyable"))
                        return true;
                    continue;
                }
            }

            // Cross-module fallback: check TypeRecord.NonCopyable flag
            if (env.TypeDatabase.TryGetTypeRecord(namedSpec, out var typeRecord) &&
                typeRecord.Flags.HasFlag(TypeRecordFlags.NonCopyable))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks whether any constructor parameter is a nested frozen struct type.
    /// @_cdecl can't represent nested Swift types (e.g. NestedOuter.Inner) in C ABI —
    /// the Swift compiler rejects with "type of the parameter cannot be represented in Objective-C".
    /// Non-frozen nested structs are fine because they're passed as UnsafeRawPointer.
    /// </summary>
    internal static bool HasNestedFrozenStructParameter(MethodEnvironment env)
    {
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec is not NamedTypeSpec namedSpec)
                continue;

            if (!env.TypeDatabase.TryGetTypeRecord(namedSpec, out var typeRecord))
                continue;

            if (typeRecord.Kind != TypeRecordKind.Struct)
                continue;

            if (!MarshallingHelpers.IsTypeFrozen(typeRecord))
                continue;

            // Nested type: the name after stripping the module prefix still contains a dot.
            // e.g. "ModuleName.NestedOuter.Inner" → "NestedOuter.Inner" (has dot = nested)
            // vs   "ModuleName.Point" → "Point" (no dot = top-level)
            var name = namedSpec.Name;
            var dotIndex = name.IndexOf('.');
            if (dotIndex >= 0)
            {
                var afterModule = name.Substring(dotIndex + 1);
                if (afterModule.Contains('.'))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks if any parameter is an unsupported buffer pointer type:
    /// UnsafeBufferPointer&lt;T&gt; or UnsafeMutableBufferPointer&lt;T&gt;. These are multi-word
    /// structs that can't be represented in the @_cdecl C ABI and don't yet have cross-ABI
    /// marshalling. UnsafeRawBufferPointer and UnsafeMutableRawBufferPointer are NOT treated
    /// as unsupported — CdeclParamMapper splits both into (ptr, len) at the @_cdecl boundary
    /// and the C# side exposes ReadOnlySpan&lt;byte&gt; / Span&lt;byte&gt;. See
    /// src/docs/Design/unsafe-mutable-raw-buffer-pointer.md.
    /// </summary>
    internal static bool HasUnsupportedBufferPointerParameter(MethodEnvironment env)
    {
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec is NamedTypeSpec namedSpec)
            {
                var name = namedSpec.Name;
                if (name == "Swift.UnsafeBufferPointer" ||
                    name == "Swift.UnsafeMutableBufferPointer")
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Finds a StructDecl by module-qualified name in a type list (including nested types).
    /// </summary>
    private static StructDecl? FindStructDecl(IEnumerable<TypeDecl> types, string qualifiedName)
    {
        foreach (var type in types)
        {
            if (type is StructDecl sd && sd.SwiftTypeName?.ModuleQualifiedName == qualifiedName)
                return sd;
            if (type.Types?.Count > 0)
            {
                var nested = FindStructDecl(type.Types, qualifiedName);
                if (nested != null) return nested;
            }
        }
        return null;
    }

    /// <summary>
    /// Gets the @_cdecl symbol name for a constructor wrapper.
    /// Pure function — no side effects, safe to call before emission.
    /// </summary>
    /// <param name="moduleName">The Swift module name (e.g., "Lottie").</param>
    /// <param name="typeName">The Swift type name (e.g., "LottieAnimationView").</param>
    /// <param name="originalMangledName">The original mangled name to hash for uniqueness.</param>
    public static string GetConstructorSymbolName(string moduleName, string typeName, string originalMangledName)
    {
        var hash = EmitterUtility.DeterministicHash8(originalMangledName);
        var safeTypeName = typeName.Replace(".", "_");
        return $"SBW_{moduleName}_{safeTypeName}_init_{hash}";
    }

    /// <summary>
    /// Emits a Swift @_cdecl wrapper function for a constructor.
    /// The wrapper receives C-compatible parameters, calls the Swift init,
    /// and returns the result via C ABI.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer for the wrapper .swift file.</param>
    /// <param name="env">The method environment with constructor info.</param>
    /// <param name="ctx">The per-module emission context for dedup tracking.</param>
    /// <param name="silgenTarget">Optional @_silgen_name symbol to call instead of direct init (for default param overloads).</param>
    public static void EmitSwiftConstructorWrapper(
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ModuleEmissionContext? ctx = null,
        string? silgenTarget = null)
    {
        ctx ??= ModuleEmissionContext.Default;

        var methodDecl = env.MethodDecl;
        var parentTypeDecl = env.ParentDecl as TypeDecl;
        if (parentTypeDecl == null) return;

        var symbolName = methodDecl.MangledName; // Already set to cdecl symbol by caller
        // S5 audited (Tier B): the constructor bucket is structurally distinct from the
        // method/property/subscript buckets — no other emitter ever registers a constructor
        // mangled name. The cdecl symbol is unique per overload by construction, so the
        // per-kind dedup gate is collision-safe without routing through the structural-
        // identity registry.
        if (!ctx.TryAddConstructorWrapperSymbol(symbolName))
            return; // Already emitted

        var moduleName = parentTypeDecl.SwiftTypeName.Module;
        var moduleQualifiedSwiftName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;

        bool isClass = env.ParentDecl is ClassDecl;
        bool isFailable = methodDecl.IsFailable;
        bool throws = methodDecl.Throws;
        bool isFailableClass = isFailable && isClass;
        bool isFailableStruct = isFailable && !isClass;

        // Classes return pointers (non-failable: UnsafeMutableRawPointer, failable: UnsafeMutableRawPointer?).
        // Structs write to result buffer (non-failable: T, failable: Optional<T>).
        // Build Swift parameter list for the @_cdecl wrapper.
        // Phase ordering is determined by CdeclSignatureContract.
        var swiftParams = new List<string>();
        var reconstructionLines = new List<string>();
        var closureAdapterLines = new List<string>();
        var callArgs = new List<string>();
        var keptArgs = methodDecl.CSSignature.Skip(1).ToList();

        // When calling a _dbw_init_* function (silgenTarget != null), all parameters use _ (no external labels).
        // Only direct init calls need argument labels.
        bool omitLabels = silgenTarget != null;

        bool isGenericParent = WrapperValidation.IsGenericParent(env.ParentDecl);
        bool needsStaticFactory = WrapperValidation.NeedsGenericDispatch(env, MemberKind.Constructor);

        // For generic static factory constructors, delegate to the specialized emitter.
        // This emitter creates a protocol with a static _sbw_create method, extends the generic
        // type to conform, and dispatches through metatype cast in the @_cdecl wrapper.
        if (needsStaticFactory)
        {
            EmitGenericStaticFactoryConstructor(swiftWriter, env, ctx, symbolName,
                parentTypeDecl!, moduleQualifiedSwiftName, isClass, isFailable, throws);
            return;
        }

        bool isGenericClassParent = MethodWrapperEmitter.IsGenericClassParent(env.ParentDecl);

        var order = CdeclSignatureContract.DetermineParameterOrder(env);
        foreach (var phase in order.Phases)
        {
            switch (phase)
            {
                case CdeclPhase.ResultPtr:
                    swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");
                    break;

                case CdeclPhase.ErrorOut:
                    swiftParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>");
                    break;

                case CdeclPhase.Arguments:
                    var closureParamCount = keptArgs.Count(env.ClosureHandler.IsClosure);
                    for (int i = 0; i < keptArgs.Count; i++)
                    {
                        var arg = keptArgs[i];
                        if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                            continue;
                        if (arg.SwiftTypeSpec.IsEmptyTuple)
                            continue;

                        // Closure parameters: two @_cdecl params (funcPtr + context) + adapter code
                        var closureTypeSpec = env.ClosureHandler.GetClosureTypeSpec(arg);
                        if (closureTypeSpec != null &&
                            env.ClosureHandler.IsSupportedClosure(closureTypeSpec) &&
                            env.ClosureHandler.RequiresThunk(closureTypeSpec, methodDecl.MangledName, closureParamCount) &&
                            !env.ClosureHandler.IsAsyncClosure(closureTypeSpec))
                        {
                            var csName = NameProvider.StripVerbatimPrefix(
                                NameProvider.GetCSharpParameterName(arg));
                            swiftParams.Add($"_ {csName}FuncPtr: UnsafeMutableRawPointer?");
                            swiftParams.Add($"_ {csName}Context: UnsafeMutableRawPointer?");

                            bool isOptional = env.ClosureHandler.IsOptionalClosure(arg.SwiftTypeSpec);
                            bool isEscaping = WrapperValidation.IsEffectivelyEscaping(
                                closureTypeSpec, arg.SwiftTypeSpec, env.ClosureHandler);
                            if (isEscaping)
                                ClosureContextHelperEmitter.EmitIfNeeded(swiftWriter, ctx);
                            closureAdapterLines.AddRange(
                                ClosureEmitter.GetSwiftClosureAdapterCode(
                                    csName, closureTypeSpec, env.ClosureHandler, isOptional, isEscaping));

                            var adapterName = $"_adapted_{csName}";
                            var argLabel = omitLabels ? "" : ClosureEmitter.GetSwiftArgLabelForCdecl(arg);
                            var autoClosureSuffix = closureTypeSpec.IsAutoClosure ? "()" : "";
                            callArgs.Add($"{argLabel}{adapterName}{autoClosureSuffix}");
                            continue;
                        }

                        var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
                        if (label == "_")
                            label = $"arg{i}";
                        var (cdeclParam, reconstruction, callArg) = CdeclParamMapper.Map(arg, label, env, omitLabels);
                        swiftParams.Add(cdeclParam);
                        if (reconstruction != null)
                            reconstructionLines.Add(reconstruction);
                        callArgs.Add(callArg);
                    }
                    break;

                case CdeclPhase.Metadata:
                    if (isGenericClassParent && parentTypeDecl != null)
                    {
                        for (int i = 0; i < parentTypeDecl.GenericParameters.Count; i++)
                        {
                            swiftParams.Add($"_ _metadata{i}: UnsafeRawPointer");
                        }
                        // Add PWT parameters for constrained generic types.
                        // Includes resolvable conformances AND PAT/Self-requirement conformances
                        // with captured descriptor symbols (threaded by the C# call site via
                        // {HelperClass}.Get{Proto}PWT(metadata).Handle). Both classes of slot
                        // are declared as UnsafeRawPointer here; the difference is only in how
                        // the C# call site materializes the IntPtr.
                        int ctorPwtCount = MetatypeHelperEmitter.GetTotalPwtParameterCount(parentTypeDecl, env.TypeDatabase);
                        for (int pi = 0; pi < ctorPwtCount; pi++)
                        {
                            swiftParams.Add($"_ _pwt{pi}: UnsafeRawPointer");
                        }
                    }
                    break;
            }
        }

        var swiftParamString = string.Join(", ", swiftParams);

        // Build return type
        string returnClause;
        if (isClass && !isFailable)
            returnClause = " -> UnsafeMutableRawPointer";
        else if (isFailableClass)
            returnClause = " -> UnsafeMutableRawPointer?";
        else
            returnClause = ""; // void (writes to resultPtr)

        // Build the Swift function name (internal, doesn't need to be pretty)
        var swiftFuncName = $"_sbw_init_{EmitterUtility.DeterministicHash8(symbolName)}";

        // Insert nil args for stripped optional closures at their original positions.
        // The wrapper loop above only processes kept args (closures stripped from CSSignature),
        // so callArgs has entries only for non-closure params. We interleave nils at the
        // positions where closures originally appeared using the saved original arg list.
        if (methodDecl.HasNilOptionalClosures && methodDecl.OriginalArgsWithNilClosures != null)
        {
            var merged = new List<string>();
            int keptIdx = 0;
            foreach (var (arg, isNilClosure, argLabel) in methodDecl.OriginalArgsWithNilClosures)
            {
                if (isNilClosure)
                {
                    merged.Add(omitLabels ? "nil" : $"{argLabel}nil");
                }
                else
                {
                    // Skip debug/empty tuple params that don't produce callArgs entries
                    if (DefaultParameterOverloadEmitter.IsDebugParameter(arg) || arg.SwiftTypeSpec.IsEmptyTuple)
                        continue;
                    if (keptIdx < callArgs.Count)
                        merged.Add(callArgs[keptIdx++]);
                }
            }
            callArgs = merged;
        }

        // Build call arguments string
        var callArgString = string.Join(", ", callArgs);

        // For generic parent class types, emit protocol + conformance for metatype dispatch.
        // Note: _metadata0 is the specialized type metadata (e.g., GenericCache<String>.self),
        // NOT a per-generic-param T metadata. unsafeBitCast(_metadata0, to: Any.Type.self)
        // gives the concrete class type with all generic params already baked in.
        // Extra _metadata1..N params are accepted to match PInvokeSignatureBuilder ordering
        // but are unused — the isa pointer on the class provides the actual metadata.
        string? protocolName = null;
        if (isGenericClassParent)
        {
            protocolName = EmitConstructorProtocolAndConformance(
                swiftWriter, methodDecl, symbolName, moduleQualifiedSwiftName, isFailable, throws,
                parentTypeDecl!);
        }

        // Build the call expression.
        // Note: generic parent path takes precedence over silgenTarget. Default-param overloads
        // on generic class constructors would need the _dbw_init_* path combined with metatype
        // dispatch, which is not yet supported. In practice, default-param overloads on generic
        // classes are rare and the C# side doesn't generate them for generic parents.
        string callExpr;
        if (isGenericClassParent && protocolName != null)
        {
            // Protocol metatype dispatch: use metadata → Any.Type → protocol.Type → init
            // The init call goes through the protocol existential metatype
            callExpr = $"initType.init({callArgString})";
        }
        else if (silgenTarget != null)
        {
            // Default param overload: call the @_silgen_name wrapper via its extension method
            // The @_silgen_name wrapper is a static factory on the type
            callExpr = $"{moduleQualifiedSwiftName}.{silgenTarget}({callArgString})";
        }
        else
        {
            callExpr = $"{moduleQualifiedSwiftName}({callArgString})";
        }

        // For generic parent types: emit metadata accessor helper at module scope (before @_cdecl).
        // Uses the GSF variant (total PWT count) so the helper signature matches the
        // @_cdecl wrapper's _pwtN declarations and the C# call site's dynamic-PWT extraction.
        string? metaHelperName = null;
        if (isGenericClassParent && protocolName != null)
        {
            metaHelperName = EmitMetadataAccessorHelperIfNeededForGsf(swiftWriter, parentTypeDecl!, ctx, env.TypeDatabase);
        }

        // Emit the @_cdecl function
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Constructor @_cdecl wrapper for {{moduleQualifiedSwiftName}}.
            // Routes constructor through C calling convention to avoid CallConvSwift crash on NativeAOT.
            """);

        // Add @MainActor annotation when the parent type is @MainActor-isolated.
        // Without this, calling a @MainActor init from a non-isolated @_cdecl function
        // causes a Swift 6 compile error. Safe for synchronous functions (no runtime dispatch).
        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, symbolName,
            needsMainActor: parentTypeDecl?.IsMainActorIsolated == true,
            availabilityAnnotations: WrapperEmitterHelpers.MergeAvailability(env.MethodDecl.AvailabilityAnnotations, parentTypeDecl));

        swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}){returnClause} {{");
        swiftWriter.Indent++;

        // Emit parameter reconstruction lines
        foreach (var line in reconstructionLines)
        {
            swiftWriter.WriteLine(line);
        }

        // Emit closure adapter lines (reconstruct native Swift closures from Cdecl func ptrs)
        foreach (var line in closureAdapterLines)
        {
            swiftWriter.WriteLine(line);
        }

        // For generic parent types: reconstruct metatype from metadata pointer
        // _metadata0 is T.self, not GenericClass<T>.self — call the parent type's metadata accessor
        // via dlsym to convert T.self → GenericClass<T>.self before protocol dispatch.
        if (isGenericClassParent && protocolName != null && metaHelperName != null)
        {
            var metaArgsList = Enumerable.Range(0, parentTypeDecl!.GenericParameters.Count).Select(i => $"_metadata{i}");
            // Include PWT arguments for constrained generic types (resolvable + descriptor-based dynamic)
            var pwtArgsList = Enumerable.Range(0, MetatypeHelperEmitter.GetTotalPwtParameterCount(parentTypeDecl, env.TypeDatabase)).Select(i => $"_pwt{i}");
            var metaArgs = string.Join(", ", metaArgsList.Concat(pwtArgsList));
            swiftWriter.WriteLine($"let parentMeta = {metaHelperName}({metaArgs})");
            swiftWriter.WriteLine($"let initType = unsafeBitCast(parentMeta, to: Any.Type.self) as! any {protocolName}.Type");
        }

        // Emit the body based on constructor type
        if (isGenericClassParent && protocolName != null)
        {
            // Generic parent class: use protocol metatype dispatch
            EmitGenericClassBody(swiftWriter, callExpr, isFailable, throws);
        }
        else if (throws && isClass && !isFailable)
        {
            // Throwing class constructor
            EmitThrowingClassBody(swiftWriter, callExpr);
        }
        else if (throws && isFailableClass)
        {
            // Failable + throwing class constructor
            EmitFailableThrowingClassBody(swiftWriter, callExpr);
        }
        else if (throws && !isClass)
        {
            // Throwing struct constructor
            EmitThrowingStructBody(swiftWriter, callExpr, moduleQualifiedSwiftName, isFailable, parentTypeDecl);
        }
        else if (isFailableClass)
        {
            // Failable class constructor (non-throwing)
            swiftWriter.WriteLine($"guard let result = {callExpr} else {{ return nil }}");
            swiftWriter.WriteLine("return Unmanaged.passRetained(result).toOpaque()");
        }
        else if (isClass)
        {
            // Non-failable, non-throwing class constructor
            swiftWriter.WriteLine($"let result = {callExpr}");
            swiftWriter.WriteLine("return Unmanaged.passRetained(result).toOpaque()");
        }
        else if (isFailableStruct)
        {
            // Failable struct constructor (non-throwing)
            // Note: tag fixup not applied here — resultPtr contains Optional<T> (not T),
            // so field offsets from MemoryLayout<T> don't directly apply. The Optional<T>
            // wrapper adds its own tag byte at the end. If a failable struct init has
            // Optional<BlittablePrimitive> fields that get corrupted, this path needs
            // a fixup variant that accounts for the Optional<T> payload offset.
            swiftWriter.WriteLine($"let result: {moduleQualifiedSwiftName}? = {callExpr}");
            swiftWriter.WriteLine($"resultPtr.assumingMemoryBound(to: Optional<{moduleQualifiedSwiftName}>.self).initialize(to: result)");
        }
        else
        {
            // Non-failable, non-throwing struct constructor
            swiftWriter.WriteLine($"let result = {callExpr}");
            swiftWriter.WriteLine($"resultPtr.assumingMemoryBound(to: {moduleQualifiedSwiftName}.self).initialize(to: result)");
            // Fix Optional<BlittablePrimitive> tag bytes after initialize(to:).
            // Mono's initializeMemory/initialize(to:) can corrupt the tag byte for Optional<Int32> etc.
            // inside frozen structs, reading None as Some(0). Write correct tag bytes explicitly.
            EmitOptionalBlittableTagFixup(swiftWriter, parentTypeDecl, moduleQualifiedSwiftName);
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Emits a private Swift helper function that calls the metadata accessor for a generic parent
    /// type via dlsym. Delegates to <see cref="MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded"/>.
    /// Uses resolvable PWT count to match what the C# P/Invoke side passes for the non-GSF
    /// constructor path (concrete-params on generic class parents). The GSF path uses
    /// <see cref="EmitMetadataAccessorHelperIfNeededForGsf"/> which threads PAT/Self-requirement
    /// conformances via the dynamic-PWT runtime path.
    /// </summary>
    internal static string EmitMetadataAccessorHelperIfNeeded(
        SwiftWriter swiftWriter,
        TypeDecl parentTypeDecl,
        ModuleEmissionContext ctx,
        ITypeDatabase typeDatabase)
    {
        int pwtCount = MetatypeHelperEmitter.GetResolvablePwtParameterCount(parentTypeDecl, typeDatabase);
        return MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(swiftWriter, parentTypeDecl, ctx, pwtCount);
    }

    /// <summary>
    /// GSF cdecl-constructor variant: emits a helper sized for resolvable + dynamic-PWT
    /// conformances. Matches the slot count threaded by the C# call site via
    /// <c>PInvokeHelperContext.PwtEntries</c> (resolvable) plus
    /// <c>{HelperClass}.Get{Proto}PWT(metadata).Handle</c> for PAT/Self-requirement
    /// conformances with a captured protocol-descriptor symbol. The dedup key in the
    /// shared helper differentiates the GSF (total) variant from the non-GSF (resolvable)
    /// variant by PWT count.
    /// </summary>
    internal static string EmitMetadataAccessorHelperIfNeededForGsf(
        SwiftWriter swiftWriter,
        TypeDecl parentTypeDecl,
        ModuleEmissionContext ctx,
        ITypeDatabase typeDatabase)
    {
        int pwtCount = MetatypeHelperEmitter.GetTotalPwtParameterCount(parentTypeDecl, typeDatabase);
        return MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(swiftWriter, parentTypeDecl, ctx, pwtCount);
    }


    // ═══════════════════════════════════════════════════════════════════════
    // Generic parent type support — protocol-based type erasure for constructors
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true when a constructor on a generic parent type can be wrapped.
    /// Delegates to <see cref="GenericDispatchEmitter.CanEmitGenericDispatch"/>.
    /// </summary>
    internal static bool CanEmitGenericConstructorWrapper(MethodEnvironment env, TypeDecl parentTypeDecl)
        => GenericDispatchEmitter.CanEmitGenericDispatch(env, parentTypeDecl, GenericDispatchKind.Constructor);

    /// <summary>
    /// Backward-compatible alias. Delegates to <see cref="GenericDispatchEmitter.CanEmitGenericDispatch"/>.
    /// </summary>
    internal static bool CanEmitGenericClassConstructorWrapper(MethodEnvironment env, TypeDecl parentTypeDecl)
        => GenericDispatchEmitter.CanEmitGenericDispatch(env, parentTypeDecl, GenericDispatchKind.Constructor);

    /// <summary>
    /// Returns true when a constructor needs the generic static factory approach.
    /// Delegates to <see cref="GenericDispatchEmitter.NeedsStaticDispatch"/>.
    /// </summary>
    internal static bool NeedsGenericStaticFactory(MethodEnvironment env, TypeDecl parentTypeDecl)
        => GenericDispatchEmitter.NeedsStaticDispatch(env, parentTypeDecl, GenericDispatchKind.Constructor);

    /// <summary>
    /// Emits protocol declaration and conformance for a constructor on a generic class type.
    /// Uses AnyObject constraint so protocol existential metatype dispatch works for class inits.
    /// Delegates to <see cref="GenericProtocolEmitter"/> for the shared protocol+conformance pattern.
    /// </summary>
    private static string EmitConstructorProtocolAndConformance(
        SwiftWriter swiftWriter, MethodDecl methodDecl, string symbolName,
        string moduleQualifiedName, bool isFailable, bool throws,
        TypeDecl parentTypeDecl)
    {
        var memberDecl = GenericProtocolEmitter.BuildConstructorMemberDeclaration(
            methodDecl, methodDecl.ModuleDecl!, isFailable, throws);
        var extensionAvailability = WrapperEmitterHelpers.MergeAvailability(
            methodDecl.AvailabilityAnnotations, parentTypeDecl);
        return GenericProtocolEmitter.EmitProtocolAndConformance(
            swiftWriter, "CI", symbolName, memberDecl, moduleQualifiedName,
            protocolConstraint: "AnyObject",
            extensionAvailability: extensionAvailability);
    }

    /// <summary>
    /// Emits the body of a generic parent class constructor wrapper using protocol metatype dispatch.
    /// The metatype reconstruction (let anyType / let initType) is already emitted before this call.
    /// The result is a protocol existential, so it needs 'as AnyObject' for Unmanaged.passRetained().
    /// Only class types reach here (structs are excluded by CanEmitGenericClassConstructorWrapper).
    /// </summary>
    private static void EmitGenericClassBody(SwiftWriter sw, string callExpr, bool isFailable, bool throws)
    {
        if (throws && isFailable)
        {
            sw.WriteLines($$"""
                do {
                    guard let result = try {{callExpr}} else { return nil }
                    return Unmanaged.passRetained(result as AnyObject).toOpaque()
                } catch {
                    errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
                    return nil
                }
                """);
        }
        else if (throws)
        {
            sw.WriteLines($$"""
                do {
                    let result = try {{callExpr}}
                    return Unmanaged.passRetained(result as AnyObject).toOpaque()
                } catch {
                    errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
                    return UnsafeMutableRawPointer(bitPattern: 1)!
                }
                """);
        }
        else if (isFailable)
        {
            sw.WriteLines($$"""
                guard let result = {{callExpr}} else { return nil }
                return Unmanaged.passRetained(result as AnyObject).toOpaque()
                """);
        }
        else
        {
            sw.WriteLines($$"""
                let result = {{callExpr}}
                return Unmanaged.passRetained(result as AnyObject).toOpaque()
                """);
        }
    }

    /// <summary>
    /// Emits the body of a throwing class constructor wrapper.
    /// </summary>
    private static void EmitThrowingClassBody(SwiftWriter sw, string callExpr)
    {
        sw.WriteLines($$"""
            do {
                let result = try {{callExpr}}
                return Unmanaged.passRetained(result).toOpaque()
            } catch {
                errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
                return UnsafeMutableRawPointer(bitPattern: 1)!
            }
            """);
    }

    /// <summary>
    /// Emits the body of a failable+throwing class constructor wrapper.
    /// </summary>
    private static void EmitFailableThrowingClassBody(SwiftWriter sw, string callExpr)
    {
        sw.WriteLines($$"""
            do {
                guard let result = try {{callExpr}} else { return nil }
                return Unmanaged.passRetained(result).toOpaque()
            } catch {
                errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
                return nil
            }
            """);
    }

    /// <summary>
    /// Emits the body of a throwing struct constructor wrapper.
    /// </summary>
    private static void EmitThrowingStructBody(SwiftWriter sw, string callExpr, string swiftTypeName, bool isFailable, TypeDecl? parentTypeDecl = null)
    {
        if (isFailable)
        {
            sw.WriteLines($$"""
                do {
                    let result: {{swiftTypeName}}? = try {{callExpr}}
                    resultPtr.assumingMemoryBound(to: Optional<{{swiftTypeName}}>.self).initialize(to: result)
                } catch {
                    errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
                }
                """);
        }
        else
        {
            sw.WriteLine("do {");
            sw.Indent++;
            sw.WriteLine($"let result = try {callExpr}");
            sw.WriteLine($"resultPtr.assumingMemoryBound(to: {swiftTypeName}.self).initialize(to: result)");
            // Fix Optional<BlittablePrimitive> tag bytes after initialize(to:).
            EmitOptionalBlittableTagFixup(sw, parentTypeDecl, swiftTypeName);
            sw.Indent--;
            sw.WriteLines($$"""
                } catch {
                    errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
                }
                """);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Generic static factory — protocol-based type erasure for constructors with T params
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Emits a @_cdecl constructor wrapper for generic types where T appears in the constructor
    /// parameters. Uses protocol-based type erasure with a static factory method:
    /// 1. Defines a protocol with a static factory method (UnsafeRawPointer params for T positions)
    /// 2. Extends the generic type to unconditionally conform to the protocol
    /// 3. The @_cdecl wrapper receives metadata, casts to protocol type, calls static factory
    ///
    /// This approach works for both generic struct and generic class constructors.
    /// </summary>
    private static void EmitGenericStaticFactoryConstructor(
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ModuleEmissionContext ctx,
        string symbolName,
        TypeDecl parentTypeDecl,
        string moduleQualifiedSwiftName,
        bool isClass,
        bool isFailable,
        bool throws)
    {
        var methodDecl = env.MethodDecl;
        var keptArgs = methodDecl.CSSignature.Skip(1).ToList();
        var genericParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet();

        // Map ABI generic param names (τ_0_0) to sugared source names (T, Element, etc.)
        var abiToSugaredName = WrapperValidation.GetAbiToSugaredNameMap(parentTypeDecl);

        var protocolName = $"_SBW_GSF_{EmitterUtility.DeterministicHash8(symbolName)}";

        // Build the protocol's static factory method parameters.
        // T-typed params become UnsafeRawPointer; concrete params keep their Swift types.
        var protocolParams = new List<string>();
        var extensionBodyLines = new List<string>();
        var initCallArgs = new List<string>();
        var cdeclParams = new List<string>();
        var cdeclCallArgs = new List<string>();

        // Struct constructors need a resultPtr for the output.
        // Class constructors return UnsafeMutableRawPointer.
        if (!isClass)
        {
            cdeclParams.Add("_ resultPtr: UnsafeMutableRawPointer");
        }
        if (throws)
        {
            cdeclParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>");
            protocolParams.Add("errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>");
            cdeclCallArgs.Add("errorOut: errorOut");
        }

        int argIndex = 0;
        for (int i = 0; i < keptArgs.Count; i++)
        {
            var arg = keptArgs[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;

            var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            if (label == "_")
                label = $"arg{argIndex}";
            if (NameProvider.IsSwiftKeyword(label))
                label = $"{label}Param";
            label = SwiftBuilder.SanitizeIdentifier(label);

            var argLabel = arg.Name switch
            {
                var n when SwiftBuilder.IsAutoGeneratedArgName(n) => "_",
                "_" => "_",
                var n when n.StartsWith("_") => n.Substring(1),
                var n when string.IsNullOrEmpty(n) => "_",
                var n => n
            };

            // When argLabel == label, Swift syntax is just "label:" (no redundant duplicate)
            var paramPrefix = (argLabel == label) ? label : $"{argLabel} {label}";

            if (WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames))
            {
                // T-typed param → UnsafeRawPointer in protocol, reconstructed in extension body
                protocolParams.Add($"{paramPrefix}: UnsafeRawPointer");
                cdeclParams.Add($"_ {label}: UnsafeRawPointer");
                cdeclCallArgs.Add($"{(argLabel == "_" ? "" : argLabel + ": ")}{label}");

                // In the extension body, reconstruct T from UnsafeRawPointer
                // Use sugared source names (T, Element) instead of ABI names (τ_0_0)
                var swiftType = WrapperValidation.RenderSwiftTypeSpecWithSugaredNames(arg.SwiftTypeSpec, abiToSugaredName);
                if (GenericDispatchEmitter.IsKeyPathFamilyOfParentGeneric(arg.SwiftTypeSpec, genericParamNames))
                {
                    // KeyPath family is always a Swift class — the C# marshalling site
                    // passes the class reference itself (DangerousGetHandle / SafeHandlePin.Handle),
                    // not a pointer-to-class-ref. assumingMemoryBound(to:).pointee would
                    // re-load through the address (returning the class metadata pointer);
                    // Unmanaged.fromOpaque interprets the value as the class reference directly.
                    extensionBodyLines.Add($"let {label}Val = Unmanaged<{swiftType}>.fromOpaque({label}).takeUnretainedValue()");
                }
                else
                {
                    extensionBodyLines.Add($"let {label}Val = {label}.assumingMemoryBound(to: {swiftType}.self).pointee");
                }
                initCallArgs.Add($"{(argLabel == "_" ? "" : argLabel + ": ")}{label}Val");
            }
            else
            {
                // Concrete param → pass through directly
                var (cdeclParam, reconstruction, callExpr) = CdeclParamMapper.Map(arg, label, env, false);
                // For the protocol/extension, render the Swift type module-qualified and
                // existential-aware. This protocol+extension is emitted at file scope where
                // the wrapper imports several modules, so an unqualified nested generic
                // argument can collide across them (e.g. BlinkIDUX's
                // `any CameraFrameAnalyzer<BlinkID.CameraFrame, BlinkIDUX.UIEvent>` rendered
                // bare as `CameraFrameAnalyzer<CameraFrame, UIEvent>` makes `UIEvent` ambiguous
                // with `UIKit.UIEvent`). Qualified names are always valid Swift, and the helper
                // restores the `any` keyword for protocol existentials (a bare
                // protocol-with-primary-associated-types name is a Swift 6 error).
                var swiftType = CdeclParamMapper.RenderModuleQualifiedSwiftTypeWithExistentialAny(
                    arg.SwiftTypeSpec, env.TypeDatabase);
                protocolParams.Add($"{paramPrefix}: {swiftType}");
                cdeclParams.Add(cdeclParam);

                if (reconstruction != null)
                {
                    // Use CdeclParamMapper's actual call expression — the local-variable suffix
                    // varies (Val vs Opt for Optional<BlittablePrimitive>); hardcoding "Val"
                    // drifts when the mapper picks a different name.
                    cdeclCallArgs.Add(callExpr);
                }
                else
                {
                    cdeclCallArgs.Add($"{(argLabel == "_" ? "" : argLabel + ": ")}{label}");
                }

                initCallArgs.Add($"{(argLabel == "_" ? "" : argLabel + ": ")}{label}");
            }
            argIndex++;
        }

        // Add metadata parameter(s) to @_cdecl
        for (int i = 0; i < parentTypeDecl.GenericParameters.Count; i++)
        {
            cdeclParams.Add($"_ _metadata{i}: UnsafeRawPointer");
        }

        // Add PWT parameter(s) for constrained generic types.
        // Includes BOTH resolvable conformances (C# emits via ProtocolWitnessTable.GetOrThrowAuto)
        // AND PAT/Self-requirement conformances with a captured descriptor symbol (C# emits via
        // {HelperClass}.Get{Proto}PWT(metadata).Handle through the dynamic-PWT runtime path).
        // Both classes of slot are declared identically on the Swift side; the difference is
        // only in how the C# call site materializes the IntPtr.
        int gsfPwtCount = MetatypeHelperEmitter.GetTotalPwtParameterCount(parentTypeDecl, env.TypeDatabase);
        for (int pi = 0; pi < gsfPwtCount; pi++)
        {
            cdeclParams.Add($"_ _pwt{pi}: UnsafeRawPointer");
        }

        // Build protocol factory method signature
        var ctorHash = EmitterUtility.DeterministicHash8(symbolName);
        var factoryMethodName = $"_sbw_create_{ctorHash}";
        string protocolReturnType;
        if (isClass)
            protocolReturnType = throws ? "UnsafeMutableRawPointer" : "UnsafeMutableRawPointer";
        else
            protocolReturnType = "";  // Writes to resultPtr

        // For struct types, the factory also needs resultPtr
        if (!isClass)
        {
            protocolParams.Insert(throws ? 1 : 0, "resultPtr: UnsafeMutableRawPointer");
            cdeclCallArgs.Insert(throws ? 1 : 0, "resultPtr: resultPtr");
        }

        var protocolParamString = string.Join(", ", protocolParams);
        var throwsClause = throws ? " throws" : "";
        var failableQ = isFailable ? "?" : "";

        // Compute availability up-front so the protocol, conformance extension, and @_cdecl
        // wrapper all share the same floor. The protocol's static method signature can name
        // availability-gated types (e.g. SpatialForceFalloff at iOS 18+); without the matching
        // @available on the protocol, Swift rejects the signature when the deployment target
        // is older than the referenced API.
        var extensionAvailability = WrapperEmitterHelpers.MergeAvailability(
            env.MethodDecl.AvailabilityAnnotations, parentTypeDecl);
        var extensionAvailPrefix = WrapperEmitterHelpers.BuildAvailabilityHeredocPrefix(
            extensionAvailability, "");

        // Emit protocol declaration
        swiftWriter.WriteLine();
        string protocolMethodDecl;
        if (isClass)
        {
            protocolMethodDecl = $"static func {factoryMethodName}({protocolParamString}){throwsClause} -> UnsafeMutableRawPointer{(isFailable ? "?" : "")}";
        }
        else
        {
            protocolMethodDecl = $"static func {factoryMethodName}({protocolParamString}){throwsClause}";
        }

        swiftWriter.WriteLines($$"""
            {{extensionAvailPrefix}}private protocol {{protocolName}} {
                {{protocolMethodDecl}}
            }
            """);

        // Build the extension body: call the real init, handle result
        var initCallArgString = string.Join(", ", initCallArgs);

        var extensionLines = new List<string>();
        extensionLines.AddRange(extensionBodyLines);

        if (isClass)
        {
            // Use concrete type name instead of Self for class constructors.
            // Swift requires `required init` for `Self(...)` in protocol extensions,
            // but we can't add `required` to the original type. Using the concrete
            // module-qualified name avoids this requirement.
            //
            // For generic parents, append the sugared generic param list explicitly
            // (`Foo<T>`). Swift can't always infer the parameters from init args —
            // notably no-arg inits on non-final generic classes — and the extension's
            // generic context puts the sugared param names in scope.
            var ctorType = moduleQualifiedSwiftName;
            if (parentTypeDecl.GenericParameters.Count > 0)
            {
                var sugared = string.Join(", ", parentTypeDecl.GenericParameters.Select(p =>
                    string.IsNullOrEmpty(p.SugaredTypeName) ? p.TypeName : p.SugaredTypeName));
                ctorType = $"{moduleQualifiedSwiftName}<{sugared}>";
            }
            if (throws && isFailable)
            {
                extensionLines.Add($"guard let result = try {ctorType}({initCallArgString}) else {{ return nil }}");
                extensionLines.Add("return Unmanaged.passRetained(result as AnyObject).toOpaque()");
            }
            else if (throws)
            {
                extensionLines.Add($"let result = try {ctorType}({initCallArgString})");
                extensionLines.Add("return Unmanaged.passRetained(result as AnyObject).toOpaque()");
            }
            else if (isFailable)
            {
                extensionLines.Add($"guard let result = {ctorType}({initCallArgString}) else {{ return nil }}");
                extensionLines.Add("return Unmanaged.passRetained(result as AnyObject).toOpaque()");
            }
            else
            {
                extensionLines.Add($"let result = {ctorType}({initCallArgString})");
                extensionLines.Add("return Unmanaged.passRetained(result as AnyObject).toOpaque()");
            }
        }
        else
        {
            // Struct: write to resultPtr via initializeMemory(as:repeating:count:).
            // Per constraints.md ("BitwiseCopyable in Swift 6+"): for non-bitwise-copyable
            // structs (those with class fields), initializeMemory properly handles ARC
            // retain/release on unbound NativeMemory.Alloc'd buffers. The cross-host fault
            // (doc 14 hypothesis 3) is in metadata-resolution, not reconstruction shape, so
            // we keep this ARC-safe form here.
            if (throws && isFailable)
            {
                extensionLines.Add($"let result: Self? = try Self({initCallArgString})");
                extensionLines.Add("resultPtr.initializeMemory(as: Optional<Self>.self, repeating: result, count: 1)");
            }
            else if (throws)
            {
                extensionLines.Add($"let result = try Self({initCallArgString})");
                extensionLines.Add("resultPtr.initializeMemory(as: Self.self, repeating: result, count: 1)");
            }
            else if (isFailable)
            {
                extensionLines.Add($"let result: Self? = Self({initCallArgString})");
                extensionLines.Add("resultPtr.initializeMemory(as: Optional<Self>.self, repeating: result, count: 1)");
            }
            else
            {
                extensionLines.Add($"let result = Self({initCallArgString})");
                extensionLines.Add("resultPtr.initializeMemory(as: Self.self, repeating: result, count: 1)");
            }
        }

        // Build extension implementation
        var extensionBody = string.Join("\n        ", extensionLines);

        // When the constructor itself constrains the parent's generic param — via a same-type
        // requirement (e.g. SampledAnimation.init(jointNames:) requires Value == JointTransforms)
        // OR a stricter conformance requirement (e.g. MusicCatalogResourceRequest.init() requires
        // MusicItemType : MusicCatalogTopLevelResourceRequesting) — the conformance extension must
        // inherit that constraint. Without it, Self(…) inside the factory body fails Swift
        // type-checking because the unconstrained extension can't see the specialized init.
        var extensionWhereClause = WrapperEmitterHelpers.BuildParentSameTypeExtensionWhere(
            env.MethodDecl, parentTypeDecl, includeConformanceConstraints: true);

        swiftWriter.WriteLines($$"""
            {{extensionAvailPrefix}}extension {{moduleQualifiedSwiftName}}: {{protocolName}}{{extensionWhereClause}} {
                static func {{factoryMethodName}}({{protocolParamString}}){{throwsClause}}{{(isClass ? $" -> UnsafeMutableRawPointer{(isFailable ? "?" : "")}" : "")}} {
                    {{extensionBody}}
                }
            }
            """);

        // Emit metadata accessor helper at module scope (before @_cdecl wrapper).
        // GSF path uses the total-count variant so PAT/Self-requirement conformances
        // with descriptor symbols get an explicit PWT slot in the helper signature
        // (matching the @_cdecl wrapper's _pwtN declarations above).
        var gsfHelperName = EmitMetadataAccessorHelperIfNeededForGsf(swiftWriter, parentTypeDecl, ctx, env.TypeDatabase);

        // Emit @_cdecl wrapper
        var cdeclParamString = string.Join(", ", cdeclParams);
        var swiftFuncName = $"_sbw_init_{EmitterUtility.DeterministicHash8(symbolName)}";

        string cdeclReturnClause;
        if (isClass && !isFailable)
            cdeclReturnClause = " -> UnsafeMutableRawPointer";
        else if (isClass && isFailable)
            cdeclReturnClause = " -> UnsafeMutableRawPointer?";
        else
            cdeclReturnClause = "";

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Constructor @_cdecl wrapper for {{moduleQualifiedSwiftName}} (generic static factory).
            // Routes through protocol-based type erasure to avoid CallConvSwift crash.
            """);

        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, symbolName,
            needsMainActor: parentTypeDecl.IsMainActorIsolated,
            availabilityAnnotations: WrapperEmitterHelpers.MergeAvailability(env.MethodDecl.AvailabilityAnnotations, parentTypeDecl));

        swiftWriter.WriteLine($"public func {swiftFuncName}({cdeclParamString}){cdeclReturnClause} {{");
        swiftWriter.Indent++;

        // Emit parameter reconstruction for concrete params
        // (The @_cdecl receives C types; reconstruct to Swift types before calling protocol method)
        for (int i = 0; i < keptArgs.Count; i++)
        {
            var arg = keptArgs[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;
            if (WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames))
                continue; // T-typed params are already UnsafeRawPointer, passed through to protocol

            var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            if (label == "_") label = $"arg{i}";
            if (NameProvider.IsSwiftKeyword(label)) label = $"{label}Param";
            label = SwiftBuilder.SanitizeIdentifier(label);

            var (_, reconstruction, _) = CdeclParamMapper.Map(arg, label, env, false);
            if (reconstruction != null)
                swiftWriter.WriteLine(reconstruction);
        }

        // Metatype dispatch — convert T.self → ParentType<T>.self via metadata accessor.
        // Pass ALL declared PWT slots (resolvable + descriptor-based dynamic) so the call
        // site matches the @_cdecl wrapper's _pwtN signature exactly.
        var gsfMetaArgsList = Enumerable.Range(0, parentTypeDecl.GenericParameters.Count).Select(i => $"_metadata{i}");
        var gsfPwtArgsList = Enumerable.Range(0, MetatypeHelperEmitter.GetTotalPwtParameterCount(parentTypeDecl, env.TypeDatabase)).Select(i => $"_pwt{i}");
        var gsfMetaArgs = string.Join(", ", gsfMetaArgsList.Concat(gsfPwtArgsList));
        swiftWriter.WriteLine($"let parentMeta = {gsfHelperName}({gsfMetaArgs})");
        swiftWriter.WriteLine("let metatype = unsafeBitCast(parentMeta, to: Any.Type.self) as! any _SBW_GSF_" +
            EmitterUtility.DeterministicHash8(symbolName) + ".Type");

        // Call the protocol static factory
        var cdeclCallArgString = string.Join(", ", cdeclCallArgs);

        if (throws)
        {
            swiftWriter.WriteLine("do {");
            swiftWriter.Indent++;

            if (isClass)
            {
                if (isFailable)
                {
                    swiftWriter.WriteLine($"return try metatype.{factoryMethodName}({cdeclCallArgString})");
                }
                else
                {
                    swiftWriter.WriteLine($"return try metatype.{factoryMethodName}({cdeclCallArgString})");
                }
            }
            else
            {
                swiftWriter.WriteLine($"try metatype.{factoryMethodName}({cdeclCallArgString})");
            }

            swiftWriter.Indent--;
            swiftWriter.WriteLines("""
                } catch {
                    errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
                """);
            if (isClass)
            {
                swiftWriter.WriteLine(isFailable ? "    return nil" : "    return UnsafeMutableRawPointer(bitPattern: 1)!");
            }
            swiftWriter.WriteLine("}");
        }
        else if (isClass)
        {
            swiftWriter.WriteLine($"return metatype.{factoryMethodName}({cdeclCallArgString})");
        }
        else
        {
            swiftWriter.WriteLine($"metatype.{factoryMethodName}({cdeclCallArgString})");
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    // ==================== Optional<BlittablePrimitive> Tag Fixup ====================

    /// <summary>
    /// Returns the list of stored properties on a struct that are Optional&lt;BlittablePrimitive&gt;.
    /// These properties have tag bytes that can be corrupted by Mono's initialize(to:)/initializeMemory
    /// when the full struct is written at once.
    /// </summary>
    internal static List<(string propertyName, string innerTypeName, string tagOffset)> GetOptionalBlittablePrimitiveProperties(TypeDecl? parentTypeDecl)
    {
        var result = new List<(string propertyName, string innerTypeName, string tagOffset)>();
        if (parentTypeDecl == null) return result;

        foreach (var prop in parentTypeDecl.Properties)
        {
            if (!prop.HasStorage) continue;
            if (prop.SwiftTypeSpec is not NamedTypeSpec optSpec) continue;
            if (optSpec.Name != "Swift.Optional" || optSpec.GenericParameters.Count != 1) continue;

            var innerSpec = optSpec.GenericParameters[0];
            if (innerSpec is not NamedTypeSpec innerNamed) continue;
            if (!CdeclParamMapper.IsBlittablePrimitiveSwiftType(innerNamed.Name)) continue;

            var tagOffset = OptionalMarshalClassifier.GetSwiftTagByteOffsetString(innerNamed.Name) ?? "8";
            result.Add((prop.Name, innerNamed.Name, tagOffset));
        }
        return result;
    }

    /// <summary>
    /// Emits Swift code to fix Optional&lt;BlittablePrimitive&gt; tag bytes after a struct has been
    /// written to a result pointer via initialize(to:) or initializeMemory(as:repeating:count:).
    ///
    /// Mono's memory operations can corrupt the tag byte for Optional&lt;Int32&gt; etc. inside frozen
    /// structs — the None discriminator reads as 0 (Some) instead of 1. This fixup uses
    /// MemoryLayout.offset(of:) to find each Optional field and explicitly writes the correct tag byte.
    /// </summary>
    internal static void EmitOptionalBlittableTagFixup(SwiftWriter sw, TypeDecl? parentTypeDecl, string moduleQualifiedSwiftName)
    {
        var optionalFields = GetOptionalBlittablePrimitiveProperties(parentTypeDecl);
        if (optionalFields.Count == 0) return;

        foreach (var (propertyName, _, tagOffset) in optionalFields)
        {
            // Use MemoryLayout<T>.offset(of:) to find the field's byte offset within the struct,
            // then advance by the inner type's size to reach the tag byte.
            // Tag byte: 0 = Some, 1 = None (matches Swift Optional enum layout).
            sw.WriteLine($"if let _fo = MemoryLayout<{moduleQualifiedSwiftName}>.offset(of: \\{moduleQualifiedSwiftName}.{propertyName}) {{");
            sw.Indent++;
            sw.WriteLine($"resultPtr.advanced(by: _fo + {tagOffset}).assumingMemoryBound(to: UInt8.self).pointee = result.{propertyName} == nil ? 1 : 0");
            sw.Indent--;
            sw.WriteLine("}");
        }
    }

    // ==================== Optional Tag Helper ====================

    /// <summary>
    /// Gets the @_cdecl symbol name for an Optional tag helper function.
    /// One per type, shared across all failable inits on that type.
    /// </summary>
    public static string GetOptionalTagSymbolName(string moduleName, string typeName)
    {
        var safeTypeName = typeName.Replace(".", "_");
        return $"SBW_GetOptionalTag_{moduleName}_{safeTypeName}";
    }

    /// <summary>
    /// Emits a @_cdecl helper function that extracts the Optional tag from a buffer.
    /// Returns 0 (Some) or 1 (None), matching Swift Optional enum tag semantics.
    /// Replaces VWT->GetEnumTag function pointer calls which crash on Mono.
    /// Deduped per type via ModuleEmissionContext.
    /// </summary>
    public static void EmitOptionalTagHelper(
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ModuleEmissionContext ctx)
    {
        var parentTypeDecl = env.ParentDecl as TypeDecl;
        if (parentTypeDecl == null) return;

        var moduleName = parentTypeDecl.SwiftTypeName.Module;
        var symbolName = GetOptionalTagSymbolName(moduleName, parentTypeDecl.Name);

        // S5 audited (Tier C): singleton helper per (module, type). The fixed
        // `SBW_GetOptionalTag_` prefix is uniquely shaped and cannot alias any
        // method/property/constructor wrapper symbol; the per-kind helper dedup
        // gate is sufficient.
        if (!ctx.TryAddOptionalTagHelperSymbol(symbolName))
            return; // Already emitted for this type

        var moduleQualifiedSwiftName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        var funcHash = EmitterUtility.DeterministicHash8(symbolName);

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Optional tag helper for {{moduleQualifiedSwiftName}}.
            // Returns 0 (Some) or 1 (None) — matches Swift Optional enum tag layout.
            // Avoids VWT->GetEnumTag function pointer call which crashes on Mono.
            @_cdecl("{{symbolName}}")
            """);
        swiftWriter.WriteLine($"public func _sbw_getOptionalTag_{funcHash}(_ ptr: UnsafeRawPointer) -> UInt32 {{");
        swiftWriter.Indent++;
        swiftWriter.WriteLine($"let optional = ptr.load(as: Optional<{moduleQualifiedSwiftName}>.self)");
        swiftWriter.WriteLine("return optional == nil ? 1 : 0");
        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }
}
