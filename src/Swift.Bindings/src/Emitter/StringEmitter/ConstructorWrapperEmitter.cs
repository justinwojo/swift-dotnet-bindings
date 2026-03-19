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

        // Only in xcframework mode where the wrapper library exists
        if (!WrapperValidation.IsXCFrameworkMode(env.TypeDatabase))
            return false;

        // Skip internal constructors — wrapper can't call them from external code
        if (env.MethodDecl.IsModuleInternal)
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
        // Two paths:
        // 1. Generic class with concrete params → existing protocol metatype dispatch
        // 2. Generic struct/class with T-typed params → protocol with static factory (UnsafeRawPointer params)
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

        // Skip async constructors (async uses its own wrapper pattern)
        if (env.MethodDecl.IsAsync)
            return false;

        // Skip non-copyable (~Copyable) struct types — defense-in-depth guard.
        // In Swift 6.2+, ALL types explicitly list both Copyable and Escapable in ABI JSON.
        // Non-copyable types list Escapable WITHOUT Copyable.
        if (WrapperValidation.IsNonCopyableStructParent(env.ParentDecl))
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

        // Skip constructors with UnsafeRawBufferPointer/UnsafeMutableRawBufferPointer parameters.
        // Buffer pointer types are multi-word structs (base + count) that @_cdecl can't represent
        // in the C calling convention. The Swift compiler rejects them with:
        // "type of the parameter cannot be represented in Objective-C".
        if (HasBufferPointerParameter(env))
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
    /// Checks whether any closure parameter is an async closure.
    /// </summary>
    private static bool HasAnyAsyncClosure(MethodEnvironment env)
        => WrapperValidation.HasAnyAsyncClosure(env);

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
    /// Checks if any parameter is a buffer pointer type (UnsafeRawBufferPointer,
    /// UnsafeMutableRawBufferPointer, UnsafeBufferPointer, UnsafeMutableBufferPointer).
    /// These are multi-word structs that can't be represented in the @_cdecl C ABI.
    /// </summary>
    internal static bool HasBufferPointerParameter(MethodEnvironment env)
    {
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec is NamedTypeSpec namedSpec)
            {
                var name = namedSpec.Name;
                if (name.EndsWith("BufferPointer", StringComparison.Ordinal) &&
                    (name.Contains("UnsafeRawBufferPointer") ||
                     name.Contains("UnsafeMutableRawBufferPointer") ||
                     name.Contains("UnsafeBufferPointer") ||
                     name.Contains("UnsafeMutableBufferPointer")))
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
        bool needsStaticFactory = isGenericParent && parentTypeDecl != null &&
            NeedsGenericStaticFactory(env, parentTypeDecl);

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
                            !env.ClosureHandler.IsAsyncThrowingClosure(closureTypeSpec))
                        {
                            var csName = NameProvider.StripVerbatimPrefix(
                                NameProvider.GetCSharpParameterName(arg));
                            swiftParams.Add($"_ {csName}FuncPtr: UnsafeMutableRawPointer?");
                            swiftParams.Add($"_ {csName}Context: UnsafeMutableRawPointer?");

                            bool isOptional = env.ClosureHandler.IsOptionalClosure(arg.SwiftTypeSpec);
                            closureAdapterLines.AddRange(
                                ClosureEmitter.GetSwiftClosureAdapterCode(
                                    csName, closureTypeSpec, env.ClosureHandler, isOptional));

                            var adapterName = $"_adapted_{csName}";
                            var argLabel = omitLabels ? "" : ClosureEmitter.GetSwiftArgLabelForCdecl(arg);
                            var autoClosureSuffix = closureTypeSpec.IsAutoClosure ? "()" : "";
                            callArgs.Add($"{argLabel}{adapterName}{autoClosureSuffix}");
                            continue;
                        }

                        var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
                        if (label == "_")
                            label = $"arg{i}";
                        var (cdeclParam, reconstruction, callArg) = GetCdeclParamMapping(arg, label, env, omitLabels);
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
                swiftWriter, methodDecl, symbolName, moduleQualifiedSwiftName, isFailable, throws);
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

        // For generic parent types: emit metadata accessor helper at module scope (before @_cdecl)
        string? metaHelperName = null;
        if (isGenericClassParent && protocolName != null)
        {
            metaHelperName = EmitMetadataAccessorHelperIfNeeded(swiftWriter, parentTypeDecl!, ctx);
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
        if (parentTypeDecl?.IsMainActorIsolated == true)
        {
            swiftWriter.WriteLine("@MainActor");
        }

        swiftWriter.WriteLines($$"""
            @_cdecl("{{symbolName}}")
            """);

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
            var metaArgs = string.Join(", ", Enumerable.Range(0, parentTypeDecl!.GenericParameters.Count).Select(i => $"_metadata{i}"));
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
    /// type via dlsym. This converts T.self metadata into GenericType&lt;T&gt;.self metadata, which is
    /// needed for protocol metatype dispatch. Deduplicates by type mangled name.
    /// Returns the helper function name (e.g., "_sbw_meta_GenericClass").
    /// </summary>
    internal static string EmitMetadataAccessorHelperIfNeeded(
        SwiftWriter swiftWriter,
        TypeDecl parentTypeDecl,
        ModuleEmissionContext ctx)
    {
        var typeName = parentTypeDecl.Name.Replace(".", "_");
        var helperName = $"_sbw_meta_{typeName}";
        var mangledName = parentTypeDecl.MangledName;

        if (!ctx.TryAddMetadataAccessorHelper(mangledName))
            return helperName; // Already emitted, just return the name

        var metaSymbol = $"{mangledName}Ma";
        var genericCount = parentTypeDecl.GenericParameters.Count;

        // Build parameter list: one UnsafeRawPointer per generic parameter
        var paramList = string.Join(", ",
            Enumerable.Range(0, genericCount).Select(i => $"_ t{i}: UnsafeRawPointer"));

        // Build function type: (Int, UnsafeRawPointer, ...) -> (UnsafeRawPointer, Int)
        var fnParamTypes = string.Join(", ",
            new[] { "Int" }.Concat(
                Enumerable.Range(0, genericCount).Select(_ => "UnsafeRawPointer")));

        // Build call arguments: (0, t0, t1, ...)
        var callArgs = string.Join(", ",
            new[] { "0" }.Concat(
                Enumerable.Range(0, genericCount).Select(i => $"t{i}")));

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            private func {{helperName}}({{paramList}}) -> UnsafeRawPointer {
                typealias _Fn = @convention(thin) ({{fnParamTypes}}) -> (UnsafeRawPointer, Int)
                let fn = unsafeBitCast(dlsym(dlopen(nil, RTLD_LAZY), "{{metaSymbol}}")!, to: _Fn.self)
                return fn({{callArgs}}).0
            }
            """);

        return helperName;
    }

    /// <summary>
    /// Maps a constructor parameter to its @_cdecl-compatible Swift type, reconstruction code,
    /// and call argument expression.
    /// </summary>
    /// <param name="omitLabels">When true, omit argument labels (used when calling _dbw_init_* which uses _ for all params).</param>
    /// <param name="useUtf8Strings">When true, String params use UTF-8 ptr+len (for subscript/enum case wrappers
    /// where C# already sends UTF-8). When false, uses two Int words matching SwiftString.Buffer layout.</param>
    internal static (string cdeclParam, string? reconstruction, string callArg) GetCdeclParamMapping(
        ArgumentDecl arg, string label, MethodEnvironment env, bool omitLabels = false, bool useUtf8Strings = false)
    {
        var swiftTypeSpec = arg.SwiftTypeSpec;

        // Swift keywords (in, for, repeat, etc.) can't be used as bare identifiers
        // in @_cdecl wrapper bodies. Rename to avoid conflicts — the call argument
        // label comes from arg.Name, so it's unaffected by this rename.
        if (NameProvider.IsSwiftKeyword(label))
            label = $"{label}Param";

        // Strip type-syntax characters (<>[]()) that could appear in demangled parameter names
        label = SwiftBuilder.SanitizeIdentifier(label);

        // Determine the Swift argument label for the init call
        // When calling _dbw_init_* (omitLabels=true), all params use _ (no external label)
        var argLabel = omitLabels ? "" : arg.Name switch
        {
            var n when n.StartsWith("arg") => "",
            "_" => "",  // Unlabeled parameter (Name set to "_") — no argument label
            var n when n.StartsWith("_") => $"{n.Substring(1)}: ",
            var n when string.IsNullOrEmpty(n) => "",
            var n => $"{n}: "
        };

        // Primitives pass through directly
        if (IsCdeclPrimitive(swiftTypeSpec))
        {
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(swiftTypeSpec);

            // Bool: Swift @_cdecl receives Int8, needs != 0 conversion
            if (MarshallingHelpers.IsBoolType(swiftType) || swiftType == "Bool")
            {
                return ($"_ {label}: Int8",
                        $"let {label}Val = {label} != 0",
                        $"{argLabel}{label}Val");
            }

            return ($"_ {label}: {swiftType}", null, $"{argLabel}{label}");
        }

        // AnyObject: IS a class reference by definition — use Unmanaged<AnyObject> marshalling.
        // Without this, AnyObject falls through to protocol existential path which emits
        // `any AnyObject.self` (not valid Swift metatype syntax).
        if (IsAnyObjectType(swiftTypeSpec))
        {
            return ($"_ {label}: UnsafeMutableRawPointer",
                    $"let {label}Val: AnyObject = Unmanaged<AnyObject>.fromOpaque({label}).takeUnretainedValue()",
                    $"{argLabel}{label}Val");
        }

        // Protocol existentials are not C-representable in @_cdecl functions.
        // Marshal as UnsafeRawPointer and reconstruct inside the wrapper body.
        if (IsProtocolExistentialType(swiftTypeSpec, env.TypeDatabase))
        {
            var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
            return ($"_ {label}: UnsafeRawPointer",
                    $"let {label}Val: {swiftType} = {label}.load(as: {swiftType}.self)",
                    $"{argLabel}{label}Val");
        }

        // Optional<reference type>: nullable pointer ABI.
        // C# passes IntPtr (0 for nil, object pointer for non-nil) via PayloadBuffer<IntPtr>.Buffer.
        // @_cdecl receives UnsafeMutableRawPointer? (nullable pointer maps to void* in C ABI).
        if (MethodWrapperEmitter.IsOptionalWithReferenceInner(swiftTypeSpec, env.TypeDatabase))
        {
            var innerType = ((NamedTypeSpec)swiftTypeSpec).GenericParameters[0];
            var swiftInnerType = ExistentialBypassEmitter.RenderSwiftTypeSpec(innerType);

            // Check if the inner type is an ObjC-bridged struct (e.g., NSZone, IndexPath).
            // Unmanaged<T> requires T: AnyObject, so ObjC-bridged structs need
            // Unmanaged<AnyObject> + cast. Synthetic ObjCBridged records from Apple framework
            // heuristics have Kind=Class but may represent Swift structs (e.g., NSZone),
            // so ObjCBridged types always use the AnyObject bridge for safety.
            // Also use AnyObject for types without TypeRecords (fallback) since we can't
            // verify they're true classes.
            bool useAnyObjectBridge = true;
            if (innerType is NamedTypeSpec innerNamed &&
                env.TypeDatabase.TryGetTypeRecord(innerNamed, out var innerRecord))
            {
                // True class (not ObjC-bridged) — Unmanaged<ClassName> is safe.
                // ObjC-bridged types use AnyObject because the synthetic TypeRecord
                // may report Kind=Class for types that are actually Swift structs.
                useAnyObjectBridge = innerRecord.Kind != TypeRecordKind.Class ||
                                     MarshallingHelpers.IsObjCBridged(innerRecord);
            }

            string reconstruction;
            if (useAnyObjectBridge)
                reconstruction = $"let {label}Val: {swiftInnerType}? = {label}.map {{ Unmanaged<AnyObject>.fromOpaque($0).takeUnretainedValue() as! {swiftInnerType} }}";
            else
                reconstruction = $"let {label}Val: {swiftInnerType}? = {label}.map {{ Unmanaged<{swiftInnerType}>.fromOpaque($0).takeUnretainedValue() }}";

            return ($"_ {label}: UnsafeMutableRawPointer?",
                    reconstruction,
                    $"{argLabel}{label}Val");
        }

        // Optional<BlittablePrimitive>: read value and tag byte separately from UnsafeRawPointer
        // instead of using assumingMemoryBound(to: Optional<T>.self).pointee, which misinterprets
        // the tag byte for Optional<Int32> on some runtimes.
        if (swiftTypeSpec is NamedTypeSpec optSpec && optSpec.Name == "Swift.Optional"
            && optSpec.GenericParameters.Count == 1)
        {
            var innerSpec = optSpec.GenericParameters[0];
            if (innerSpec is NamedTypeSpec innerNamed && IsBlittablePrimitiveSwiftType(innerNamed.Name))
            {
                var rawType = GetSwiftRawValueType(innerNamed.Name);
                // Compute the tag byte offset = size of the inner type
                var tagOffset = innerNamed.Name switch
                {
                    "Swift.Bool" or "Bool" or "Swift.Int8" or "Int8" or "Swift.UInt8" or "UInt8" => "1",
                    "Swift.Int16" or "Int16" or "Swift.UInt16" or "UInt16" => "2",
                    "Swift.Int32" or "Int32" or "Swift.UInt32" or "UInt32" or "Swift.Float" or "Float" => "4",
                    _ => "8" // Int, UInt, Int64, UInt64, Double, CGFloat
                };
                // Read payload and tag separately, reconstruct Optional
                var reconstruction = $"let {label}Opt: {rawType}? = {label}.advanced(by: {tagOffset}).load(as: UInt8.self) == 0 ? {label}.load(as: {rawType}.self) : nil";
                return ($"_ {label}: UnsafeRawPointer",
                        reconstruction,
                        $"{argLabel}{label}Opt");
            }
        }

        // Generic container types (Optional<T>, Array<T>, Dictionary<K,V>, etc.)
        // are not C-representable in @_cdecl functions. Marshal as UnsafeRawPointer.
        if (IsGenericContainerType(swiftTypeSpec))
        {
            // When calling _dbw_init_* (omitLabels=true) and the param is a large Optional
            // that _dbw_init_* also widens to UnsafeRawPointer, pass the pointer through directly
            // instead of loading the Optional value (which would cause a type mismatch).
            if (omitLabels && OptionalPointerWrapperEmitter.ShouldWidenParam(arg, env.BoundGenericsHandler))
            {
                return ($"_ {label}: UnsafeRawPointer",
                        null,
                        $"{label}");
            }

            // Use assumingMemoryBound(to:).pointee instead of load(as:) — for generic containers
            // like Optional<EnumWithAssociatedValues>, load(as:) can SIGSEGV because the container
            // may not satisfy BitwiseCopyable constraints. assumingMemoryBound(to:).pointee
            // uses typed pointer access with proper value semantics.
            var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
            return ($"_ {label}: UnsafeRawPointer",
                    $"let {label}Val = {label}.assumingMemoryBound(to: {swiftType}.self).pointee",
                    $"{argLabel}{label}Val");
        }

        // Foundation.Date: @_cdecl bridges Date ↔ NSDate* (ObjC interop) which is incompatible
        // with the raw double that C# passes. Accept Double and reconstruct Date inside wrapper.
        if (swiftTypeSpec is NamedTypeSpec dateNamed && dateNamed.Name == "Foundation.Date")
        {
            return ($"_ {label}: Double",
                    $"let {label}Val = Foundation.Date(timeIntervalSinceReferenceDate: {label})",
                    $"{argLabel}{label}Val");
        }

        // Foundation.Data: @_cdecl bridges Data ↔ NSData* (ObjC interop) which is incompatible
        // with the raw Data buffer that C# passes via CallConvCdecl.
        // Accept as two Int words matching the 16-byte struct layout and reconstruct.
        // On ARM64, C# passes Swift.Data (16-byte struct) in two consecutive GP registers,
        // exactly matching two Int parameters in the @_cdecl signature.
        // Same pattern as the String ↔ NSString* workaround.
        if (swiftTypeSpec is NamedTypeSpec dataNamed && dataNamed.Name == "Foundation.Data")
        {
            return ($"_ _dW0_{label}: Int, _ _dW1_{label}: Int",
                    $"let {label}Val = unsafeBitCast((_dW0_{label}, _dW1_{label}), to: Foundation.Data.self)",
                    $"{argLabel}{label}Val");
        }

        // String: @_cdecl bridges String ↔ NSString* (ObjC interop) which is incompatible
        // with the raw SwiftString.Buffer that C# passes via CallConvCdecl.
        if (swiftTypeSpec is NamedTypeSpec strNamed && strNamed.Name == "Swift.String")
        {
            if (useUtf8Strings)
            {
                // UTF-8 pointer + length: C# encodes to UTF-8 bytes, pins them, and passes
                // (IntPtr ptr, nint len). NativeAOT-safe — no struct marshalling needed.
                // nint matches Swift's Int (64-bit on ARM64) to avoid truncation.
                // Used by subscript and enum case wrappers where C# already sends UTF-8.
                return ($"_ {label}Utf8Ptr: UnsafePointer<UInt8>, _ {label}Utf8Len: Int",
                        $"let {label}Val = String(bytes: UnsafeBufferPointer(start: {label}Utf8Ptr, count: {label}Utf8Len), encoding: .utf8)!",
                        $"{argLabel}{label}Val");
            }
            else
            {
                // Two Int words matching the 16-byte buffer layout: C# passes SwiftString.Buffer
                // (16-byte struct) in two consecutive GP registers on ARM64.
                // Used by constructor/method wrappers where C# marshals via SwiftString.
                return ($"_ _sW0_{label}: Int, _ _sW1_{label}: Int",
                        $"let {label}Val = unsafeBitCast((_sW0_{label}, _sW1_{label}), to: String.self)",
                        $"{argLabel}{label}Val");
            }
        }

        // Classes: receive as UnsafeMutableRawPointer, reconstruct via Unmanaged
        if (env.TypeDatabase.TryGetTypeRecord(swiftTypeSpec, out var typeRecord))
        {
            if (typeRecord.Kind == TypeRecordKind.Class ||
                MarshallingHelpers.IsObjCBridged(typeRecord) ||
                MarshallingHelpers.IsObjCRooted(typeRecord))
            {
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(swiftTypeSpec);

                // Check for NSString typedef structs (e.g., CALayerContentsGravity, CATransitionType).
                // These are ObjC-bridged in the type database but are Swift structs wrapping NSString,
                // not class types. Unmanaged<T> requires T to be a class, so reconstruct via
                // NSString → String → init(rawValue:) instead.
                if (swiftTypeSpec is NamedTypeSpec nsTypedef &&
                    AppleFrameworkRegistry.TryGetNetTypeName(nsTypedef.Name, out var remapped) &&
                    remapped == "Foundation.NSString")
                {
                    return ($"_ {label}: UnsafeMutableRawPointer",
                            $"let {label}Val = {swiftType}(rawValue: Unmanaged<NSString>.fromOpaque({label}).takeUnretainedValue() as String)",
                            $"{argLabel}{label}Val");
                }

                // ObjC-bridged types (e.g., IndexPath bridged to NSIndexPath) may be Swift structs
                // but passed as class pointers across FFI. Use Unmanaged<AnyObject> + cast to handle
                // both true classes and bridged structs safely. Unmanaged<T> requires T: AnyObject,
                // so Unmanaged<IndexPath> fails for bridged structs.
                if (MarshallingHelpers.IsObjCBridged(typeRecord))
                {
                    return ($"_ {label}: UnsafeMutableRawPointer",
                            $"let {label}Val = Unmanaged<AnyObject>.fromOpaque({label}).takeUnretainedValue() as! {swiftType}",
                            $"{argLabel}{label}Val");
                }

                return ($"_ {label}: UnsafeMutableRawPointer",
                        $"let {label}Val = Unmanaged<{swiftType}>.fromOpaque({label}).takeUnretainedValue()",
                        $"{argLabel}{label}Val");
            }

            // Protocol/Existential TypeRecords: not C-representable, pass as pointer
            if (typeRecord.Kind == TypeRecordKind.Protocol ||
                typeRecord.Kind == TypeRecordKind.Existential)
            {
                var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
                return ($"_ {label}: UnsafeRawPointer",
                        $"let {label}Val: {swiftType} = {label}.load(as: {swiftType}.self)",
                        $"{argLabel}{label}Val");
            }

            // Simple enums: pass raw value as C-compatible integer, reconstruct safely.
            // unsafeBitCast crashes when enum storage size differs from parameter type
            // (e.g., a 3-case `: Int` enum stored in 1 byte vs 8-byte Int parameter).
            if (typeRecord.Kind == TypeRecordKind.Enum && typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            {
                var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
                var rawType = GetSwiftRawValueType(typeRecord.RawValueTypeName);

                string conversion;
                if (!string.IsNullOrEmpty(typeRecord.RawValueTypeName))
                {
                    // RawRepresentable enum: init(rawValue:) safely maps raw value → case
                    // regardless of in-memory storage size. The synthesized init?(rawValue:)
                    // has the same access level as the type and is always available from
                    // the wrapper module. Guard against invalid raw values from C# (e.g.,
                    // casting an arbitrary integer to the enum type).
                    conversion = $"guard let {label}Val = {swiftType}(rawValue: {label}) else {{ preconditionFailure(\"Invalid raw value \\({label}) for {swiftType}\") }}";
                }
                else
                {
                    // Tag-only enum (no RawRepresentable): C# sends the case index as
                    // a widened integer. Extract the tag from the low bytes via safe
                    // memory load (little-endian: tag is in the first N bytes).
                    conversion = $"var {label}Raw = {label}; let {label}Val = withUnsafeMutablePointer(to: &{label}Raw) {{ UnsafeMutableRawPointer($0).load(as: {swiftType}.self) }}";
                }

                return ($"_ {label}: {rawType}", conversion, $"{argLabel}{label}Val");
            }

            // Complex enums: pass as pointer.
            // Use assumingMemoryBound(to:).pointee instead of load(as:) — for enums with
            // non-BitwiseCopyable fields (e.g., String raw-value enums), load(as:) creates
            // a bitwise copy without proper reference semantics, causing SIGBUS.
            // assumingMemoryBound(to:).pointee uses typed pointer access with proper value semantics.
            if (typeRecord.Kind == TypeRecordKind.Enum)
            {
                var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
                return ($"_ {label}: UnsafeRawPointer",
                        $"let {label}Val = {label}.assumingMemoryBound(to: {swiftType}.self).pointee",
                        $"{argLabel}{label}Val");
            }

            // Non-frozen structs: C# passes SafeHandle (IntPtr), receive as pointer.
            // Use assumingMemoryBound for consistency with enum/container paths.
            if (!MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
                return ($"_ {label}: UnsafeRawPointer",
                        $"let {label}Val = {label}.assumingMemoryBound(to: {swiftType}.self).pointee",
                        $"{argLabel}{label}Val");
            }

            // Frozen structs: system/Apple types pass by-value, custom types via UnsafeRawPointer.
            if (MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                // System framework frozen structs (CGRect, Date, etc.) are C-representable
                // and safe for @_cdecl by-value passing. Custom frozen structs from third-party
                // libraries trigger "Swift structs cannot be represented in Objective-C".
                if (swiftTypeSpec is NamedTypeSpec frozenNamed && IsSystemFrozenStruct(frozenNamed))
                {
                    var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(swiftTypeSpec);
                    return ($"_ {label}: {swiftType}", null, $"{argLabel}{label}");
                }

                // Custom frozen structs: pass as UnsafeRawPointer and reconstruct.
                // Use assumingMemoryBound(to:).pointee instead of load(as:) — frozen structs
                // with reference-counted fields (String, Array, Optional) are not BitwiseCopyable.
                var moduleQualifiedType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
                return ($"_ {label}: UnsafeRawPointer",
                        $"let {label}Val = {label}.assumingMemoryBound(to: {moduleQualifiedType}.self).pointee",
                        $"{argLabel}{label}Val");
            }
        }

        // Fallback: pass as UnsafeRawPointer.
        // Use assumingMemoryBound for consistency with all other pointer reconstruction paths.
        var fallbackSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
        return ($"_ {label}: UnsafeRawPointer",
                $"let {label}Val = {label}.assumingMemoryBound(to: {fallbackSwiftType}.self).pointee",
                $"{argLabel}{label}Val");
    }

    /// <summary>
    /// Checks whether a type spec represents a protocol existential (any Protocol),
    /// including Optional-wrapped protocol existentials.
    /// Protocol existentials are not C-representable and must be marshalled as UnsafeRawPointer in @_cdecl functions.
    /// </summary>
    internal static bool IsProtocolExistentialType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        // Direct protocol list: any Protocol or any P1 & P2
        if (typeSpec is ProtocolListTypeSpec)
            return true;

        // Optional<Protocol>: NamedTypeSpec("Swift.Optional") wrapping a ProtocolListTypeSpec
        if (typeSpec is NamedTypeSpec namedSpec && namedSpec.Name == "Swift.Optional" &&
            namedSpec.GenericParameters.Count == 1 && namedSpec.GenericParameters[0] is ProtocolListTypeSpec)
            return true;

        // Single protocol referenced by name: check TypeRecord
        if (typeSpec is NamedTypeSpec singleNamed &&
            typeDatabase.TryGetTypeRecord(singleNamed, out var record) &&
            (record.Kind == TypeRecordKind.Protocol || record.Kind == TypeRecordKind.Existential))
            return true;

        // Optional<SingleProtocol> referenced by name
        if (typeSpec is NamedTypeSpec optNamed && optNamed.Name == "Swift.Optional" &&
            optNamed.GenericParameters.Count == 1 && optNamed.GenericParameters[0] is NamedTypeSpec innerNamed &&
            typeDatabase.TryGetTypeRecord(innerNamed, out var innerRecord) &&
            (innerRecord.Kind == TypeRecordKind.Protocol || innerRecord.Kind == TypeRecordKind.Existential))
            return true;

        return false;
    }

    /// <summary>
    /// Checks whether a type spec represents AnyObject (the universal class protocol).
    /// AnyObject IS a class reference by definition and should use Unmanaged marshalling,
    /// not existential .load(as:) which produces invalid `any AnyObject.self` syntax.
    /// </summary>
    internal static bool IsAnyObjectType(TypeSpec typeSpec)
    {
        if (typeSpec is ProtocolListTypeSpec protocolList &&
            protocolList.Protocols.Count == 1 &&
            protocolList.Protocols.Keys.First() is NamedTypeSpec protoNamed &&
            (protoNamed.Name == "AnyObject" || protoNamed.Name == "Swift.AnyObject"))
            return true;

        if (typeSpec is NamedTypeSpec named &&
            (named.Name == "AnyObject" || named.Name == "Swift.AnyObject"))
            return true;

        return false;
    }

    /// <summary>
    /// Checks whether a type spec is a generic container type (Optional, Array, Dictionary, Set, Result).
    /// These Swift generic types are not C-representable in @_cdecl functions and must be
    /// marshalled as UnsafeRawPointer with .load(as:) reconstruction in the wrapper body.
    /// </summary>
    internal static bool IsGenericContainerType(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec named || named.GenericParameters.Count == 0)
            return false;

        return named.Name is "Swift.Optional" or "Swift.Array" or "Swift.Dictionary"
            or "Swift.Set" or "Swift.Result";
    }

    /// <summary>
    /// Returns true for frozen structs from system/Apple frameworks that are C-representable
    /// and safe for by-value @_cdecl passing. Covers:
    /// - Types in AppleFrameworkRegistry.ValueTypes (explicitly registered Apple value types)
    /// - Types from known system C-bridging modules (CoreGraphics, CoreFoundation, Darwin, simd)
    ///   that are not in the Apple framework registry but are always C-representable
    /// Does NOT include arbitrary third-party dependency modules — those may contain custom
    /// Swift structs that trigger "cannot be represented in Objective-C".
    /// </summary>
    internal static bool IsSystemFrozenStruct(NamedTypeSpec typeSpec)
    {
        if (!typeSpec.HasModule())
            return false;
        // Explicitly registered Apple value types (Foundation.Date, ARKit.ARRaycastQuery, etc.)
        if (AppleFrameworkRegistry.IsKnownValueType(typeSpec.Name))
            return true;
        // System C-bridging modules whose frozen structs are always C-representable.
        // These modules expose C structs via Swift overlays — they are inherently @_cdecl-safe.
        var module = SwiftTypeName.FromTypeSpec(typeSpec).Module;
        return module is "CoreGraphics" or "CoreFoundation" or "Darwin" or "simd"
            or "Swift" or "ObjectiveC" or "_Concurrency";
    }

    /// <summary>
    /// Returns true for types that can be passed directly through the C ABI
    /// without pointer wrapping (integers, floats, etc.).
    /// </summary>
    internal static bool IsCdeclPrimitive(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec named)
            return false;

        return named.Name switch
        {
            "Swift.Int" or "Swift.UInt" or "Swift.Int8" or "Swift.UInt8" or
            "Swift.Int16" or "Swift.UInt16" or "Swift.Int32" or "Swift.UInt32" or
            "Swift.Int64" or "Swift.UInt64" or
            "Swift.Float" or "Swift.Double" or "Swift.Bool" or
            "CoreFoundation.CGFloat" => true,
            _ => false
        };
    }

    /// <summary>
    /// Returns true for Swift types that are blittable primitives (integers, floats, bool).
    /// Used for Optional&lt;BlittablePrimitive&gt; split-parameter pattern.
    /// </summary>
    /// <summary>
    /// Returns true if the Swift type is a blittable primitive whose Optional uses an appended
    /// tag byte (not extra inhabitants). Bool is excluded — Optional&lt;Bool&gt; uses extra
    /// inhabitants (size 1 == Optional size 1), so there is no separate tag byte to read/write.
    /// </summary>
    internal static bool IsBlittablePrimitiveSwiftType(string typeName) => typeName switch
    {
        "Swift.Int" or "Swift.UInt" or "Swift.Int8" or "Swift.UInt8" or
        "Swift.Int16" or "Swift.UInt16" or "Swift.Int32" or "Swift.UInt32" or
        "Swift.Int64" or "Swift.UInt64" or
        "Swift.Float" or "Swift.Double" or
        "CoreFoundation.CGFloat" or "CGFloat" or
        "Int" or "UInt" or "Int8" or "UInt8" or
        "Int16" or "UInt16" or "Int32" or "UInt32" or
        "Int64" or "UInt64" or
        "Float" or "Double" => true,
        _ => false
    };

    /// <summary>
    /// Maps C# enum underlying type names to Swift raw value type names.
    /// </summary>
    internal static string GetSwiftRawValueType(string? rawValueTypeName) => rawValueTypeName switch
    {
        "Swift.Int" or "Int" => "Int",
        "Swift.UInt" or "UInt" => "UInt",
        "Swift.Int8" or "Int8" => "Int8",
        "Swift.UInt8" or "UInt8" => "UInt8",
        "Swift.Int16" or "Int16" => "Int16",
        "Swift.UInt16" or "UInt16" => "UInt16",
        "Swift.Int32" or "Int32" => "Int32",
        "Swift.UInt32" or "UInt32" => "UInt32",
        "Swift.Int64" or "Int64" => "Int64",
        "Swift.UInt64" or "UInt64" => "UInt64",
        "Swift.Bool" or "Bool" => "Bool",
        "Swift.Float" or "Float" => "Float",
        "Swift.Double" or "Double" => "Double",
        "CoreFoundation.CGFloat" or "CGFloat" => "CGFloat",
        "Swift.String" or "String" => "String",
        _ => "Int" // fallback
    };

    // ═══════════════════════════════════════════════════════════════════════
    // Generic parent type support — protocol-based type erasure for constructors
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true when a constructor on a generic parent type can be wrapped via @_cdecl
    /// using protocol-based type erasure with metatype dispatch.
    ///
    /// Two paths:
    /// 1. Generic class with concrete params — original protocol init dispatch (AnyObject)
    /// 2. Generic struct or class with T-typed params — protocol with static factory
    ///    that uses UnsafeRawPointer for T positions. Works for both struct and class parents.
    /// </summary>
    internal static bool CanEmitGenericConstructorWrapper(MethodEnvironment env, TypeDecl parentTypeDecl)
    {
        // Path 1: Generic class with concrete (non-T-referencing) params — existing approach
        if (parentTypeDecl is ClassDecl)
        {
            var genericParamNames = parentTypeDecl.GenericParameters
                .Select(p => p.TypeName)
                .ToHashSet();

            bool hasGenericParams = env.MethodDecl.CSSignature.Skip(1)
                .Any(arg => MethodWrapperEmitter.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames));

            if (!hasGenericParams)
                return true; // Path 1: no T in params, use existing metatype dispatch
        }

        // Path 2: Generic struct or class with T-typed params — protocol-based static factory
        // The protocol has a static factory method with UnsafeRawPointer params for T positions.
        // Supported for both struct and class parent types.
        return CanEmitGenericStaticFactoryWrapper(env, parentTypeDecl);
    }

    /// <summary>
    /// Backward-compatible alias for external callers.
    /// </summary>
    internal static bool CanEmitGenericClassConstructorWrapper(MethodEnvironment env, TypeDecl parentTypeDecl)
        => CanEmitGenericConstructorWrapper(env, parentTypeDecl);

    /// <summary>
    /// Returns true when a generic constructor can use the static factory protocol pattern.
    /// This pattern creates a protocol with a static factory method whose signature uses
    /// UnsafeRawPointer for T-typed parameters. The generic type unconditionally conforms
    /// to the protocol, and the @_cdecl wrapper dispatches via metatype cast.
    ///
    /// Requirements:
    /// - All T-typed params must be simple (direct generic param or pointer-compatible)
    /// - No closure parameters that reference T
    /// - Constructor must not be failable (for now — failable adds Optional complexity)
    /// </summary>
    internal static bool CanEmitGenericStaticFactoryWrapper(MethodEnvironment env, TypeDecl parentTypeDecl)
    {
        var genericParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet();

        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;

            // Closure parameters that reference T are not supported
            if (env.ClosureHandler.IsClosure(arg))
            {
                var closureSpec = env.ClosureHandler.GetClosureTypeSpec(arg);
                if (closureSpec != null && WrapperValidation.TypeSpecReferencesGenericParam(closureSpec, genericParamNames))
                    return false;
            }

            // For params that reference T: they must be simple generic params (just τ_0_0)
            // or non-generic types. Complex generic compositions (Array<T>, Optional<T>) in
            // constructor params are deferred.
            if (WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames))
            {
                // Allow direct generic param (e.g., T itself) — will be passed as UnsafeRawPointer
                if (arg.SwiftTypeSpec is NamedTypeSpec named && genericParamNames.Contains(named.Name))
                    continue;
                // Block complex generic compositions for now (Array<T>, Optional<T>, etc.)
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns true when a constructor needs the generic static factory approach
    /// (as opposed to the existing concrete-param metatype dispatch approach).
    /// </summary>
    internal static bool NeedsGenericStaticFactory(MethodEnvironment env, TypeDecl parentTypeDecl)
    {
        if (!parentTypeDecl.IsGeneric) return false;

        // If parent is a class with no T in params, use the existing metatype dispatch
        if (parentTypeDecl is ClassDecl)
        {
            var genericParamNames = parentTypeDecl.GenericParameters
                .Select(p => p.TypeName)
                .ToHashSet();
            bool hasGenericParams = env.MethodDecl.CSSignature.Skip(1)
                .Any(arg => MethodWrapperEmitter.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames));
            if (!hasGenericParams) return false;
        }

        // All generic struct constructors need static factory approach
        return true;
    }

    /// <summary>
    /// Emits protocol declaration and conformance for a constructor on a generic class type.
    /// Uses AnyObject constraint so protocol existential metatype dispatch works for class inits.
    /// </summary>
    private static string EmitConstructorProtocolAndConformance(
        SwiftWriter swiftWriter, MethodDecl methodDecl, string symbolName,
        string moduleQualifiedName, bool isFailable, bool throws)
    {
        var protocolName = $"_SBW_CI_{EmitterUtility.DeterministicHash8(symbolName)}";

        // Build init parameter declaration
        var initParams = new List<string>();
        var keptArgs = methodDecl.CSSignature.Skip(1).ToList();

        for (int i = 0; i < keptArgs.Count; i++)
        {
            var arg = keptArgs[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;

            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
            var label = arg.Name switch
            {
                var n when n.StartsWith("arg") => "_",
                var n when n.StartsWith("_") => n.Substring(1),
                var n when string.IsNullOrEmpty(n) => "_",
                var n => n
            };
            initParams.Add($"{label}: {swiftType}");
        }

        var paramString = string.Join(", ", initParams);
        var throwsClause = throws ? " throws" : "";
        var failableQ = isFailable ? "?" : "";

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            private protocol {{protocolName}}: AnyObject {
                init{{failableQ}}({{paramString}}){{throwsClause}}
            }
            extension {{moduleQualifiedName}}: {{protocolName}} {}
            """);

        return protocolName;
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
                var n when n.StartsWith("arg") => "_",
                "_" => "_",
                var n when n.StartsWith("_") => n.Substring(1),
                var n when string.IsNullOrEmpty(n) => "_",
                var n => n
            };

            if (WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames))
            {
                // T-typed param → UnsafeRawPointer in protocol, reconstructed in extension body
                protocolParams.Add($"{argLabel} {label}: UnsafeRawPointer");
                cdeclParams.Add($"_ {label}: UnsafeRawPointer");
                cdeclCallArgs.Add($"{(argLabel == "_" ? "" : argLabel + ": ")}{label}");

                // In the extension body, reconstruct T from UnsafeRawPointer
                // Use sugared source names (T, Element) instead of ABI names (τ_0_0)
                var swiftType = WrapperValidation.RenderSwiftTypeSpecWithSugaredNames(arg.SwiftTypeSpec, abiToSugaredName);
                extensionBodyLines.Add($"let {label}Val = {label}.assumingMemoryBound(to: {swiftType}.self).pointee");
                initCallArgs.Add($"{(argLabel == "_" ? "" : argLabel + ": ")}{label}Val");
            }
            else
            {
                // Concrete param → pass through directly
                var (cdeclParam, reconstruction, _) = GetCdeclParamMapping(arg, label, env, false);
                // For the protocol, use the Swift type
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
                protocolParams.Add($"{argLabel} {label}: {swiftType}");
                cdeclParams.Add(cdeclParam);

                if (reconstruction != null)
                {
                    // In @_cdecl, reconstruct the C param; pass reconstructed value to protocol
                    cdeclCallArgs.Add($"{(argLabel == "_" ? "" : argLabel + ": ")}{label}Val");
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
            private protocol {{protocolName}} {
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
            var ctorType = moduleQualifiedSwiftName;
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
            // Struct: write to resultPtr
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

        swiftWriter.WriteLines($$"""
            extension {{moduleQualifiedSwiftName}}: {{protocolName}} {
                static func {{factoryMethodName}}({{protocolParamString}}){{throwsClause}}{{(isClass ? $" -> UnsafeMutableRawPointer{(isFailable ? "?" : "")}" : "")}} {
                    {{extensionBody}}
                }
            }
            """);

        // Emit metadata accessor helper at module scope (before @_cdecl wrapper)
        var gsfHelperName = EmitMetadataAccessorHelperIfNeeded(swiftWriter, parentTypeDecl, ctx);

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

        if (parentTypeDecl.IsMainActorIsolated)
        {
            swiftWriter.WriteLine("@MainActor");
        }

        swiftWriter.WriteLines($$"""
            @_cdecl("{{symbolName}}")
            """);

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

            var (_, reconstruction, _) = GetCdeclParamMapping(arg, label, env, false);
            if (reconstruction != null)
                swiftWriter.WriteLine(reconstruction);
        }

        // Metatype dispatch — convert T.self → ParentType<T>.self via metadata accessor
        var metaArgs = string.Join(", ", Enumerable.Range(0, parentTypeDecl.GenericParameters.Count).Select(i => $"_metadata{i}"));
        swiftWriter.WriteLine($"let parentMeta = {gsfHelperName}({metaArgs})");
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
            if (!IsBlittablePrimitiveSwiftType(innerNamed.Name)) continue;

            var tagOffset = innerNamed.Name switch
            {
                "Swift.Bool" or "Bool" or "Swift.Int8" or "Int8" or "Swift.UInt8" or "UInt8" => "1",
                "Swift.Int16" or "Int16" or "Swift.UInt16" or "UInt16" => "2",
                "Swift.Int32" or "Int32" or "Swift.UInt32" or "UInt32" or "Swift.Float" or "Float" => "4",
                _ => "8" // Int, UInt, Int64, UInt64, Double, CGFloat
            };
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
