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
/// Follows the DestroyWrapperEmitter pattern. State tracked on <see cref="ModuleEmissionContext"/>.
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

        // Generic parent type — allow class constructors with concrete (non-T-referencing) signatures
        if (env.ParentDecl is TypeDecl typeDecl && typeDecl.IsGeneric)
        {
            if (!CanEmitGenericClassConstructorWrapper(env, typeDecl))
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

        // Skip constructors with raw ABI generic type params (τ_0_0) in signature.
        // These leak from parent type generics and cause Swift compilation failures.
        if (WrapperValidation.HasRawGenericTypeParams(env.MethodDecl))
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

        bool isGenericParent = MethodWrapperEmitter.IsGenericClassParent(env.ParentDecl);

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
                            env.ClosureHandler.RequiresThunk(closureTypeSpec) &&
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
                    if (isGenericParent && parentTypeDecl != null)
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
        if (isGenericParent)
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
        if (isGenericParent && protocolName != null)
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
        if (isGenericParent && protocolName != null)
        {
            swiftWriter.WriteLine($"let anyType: Any.Type = unsafeBitCast(_metadata0, to: Any.Type.self)");
            swiftWriter.WriteLine($"let initType = anyType as! any {protocolName}.Type");
        }

        // Emit the body based on constructor type
        if (isGenericParent && protocolName != null)
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
            EmitThrowingStructBody(swiftWriter, callExpr, moduleQualifiedSwiftName, isFailable);
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
            swiftWriter.WriteLine($"let result: {moduleQualifiedSwiftName}? = {callExpr}");
            swiftWriter.WriteLine($"resultPtr.assumingMemoryBound(to: Optional<{moduleQualifiedSwiftName}>.self).initialize(to: result)");
        }
        else
        {
            // Non-failable, non-throwing struct constructor
            swiftWriter.WriteLine($"let result = {callExpr}");
            swiftWriter.WriteLine($"resultPtr.assumingMemoryBound(to: {moduleQualifiedSwiftName}.self).initialize(to: result)");
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
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
            return ($"_ {label}: UnsafeMutableRawPointer?",
                    $"let {label}Val: {swiftInnerType}? = {label}.map {{ Unmanaged<{swiftInnerType}>.fromOpaque($0).takeUnretainedValue() }}",
                    $"{argLabel}{label}Val");
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

            var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
            return ($"_ {label}: UnsafeRawPointer",
                    $"let {label}Val = {label}.load(as: {swiftType}.self)",
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

            // Complex enums: pass as pointer
            if (typeRecord.Kind == TypeRecordKind.Enum)
            {
                var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
                return ($"_ {label}: UnsafeRawPointer",
                        $"let {label}Val = {label}.load(as: {swiftType}.self)",
                        $"{argLabel}{label}Val");
            }

            // Non-frozen structs: C# passes SafeHandle (IntPtr), receive as pointer
            if (!MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
                return ($"_ {label}: UnsafeRawPointer",
                        $"let {label}Val = {label}.load(as: {swiftType}.self)",
                        $"{argLabel}{label}Val");
            }

            // Frozen structs (including those with memory management like String):
            // C# passes the struct value directly (Buffer or blittable struct),
            // so @_cdecl must accept the Swift type by value — not as a pointer.
            if (MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(swiftTypeSpec);
                return ($"_ {label}: {swiftType}", null, $"{argLabel}{label}");
            }
        }

        // Fallback: pass as UnsafeRawPointer
        var fallbackSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
        return ($"_ {label}: UnsafeRawPointer",
                $"let {label}Val = {label}.load(as: {fallbackSwiftType}.self)",
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
        "Swift.String" or "String" => "String",
        _ => "Int" // fallback
    };

    // ═══════════════════════════════════════════════════════════════════════
    // Generic parent class support — protocol-based type erasure for constructors
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true when a constructor on a generic parent type can be wrapped via @_cdecl
    /// using protocol-based type erasure with metatype dispatch.
    /// </summary>
    internal static bool CanEmitGenericClassConstructorWrapper(MethodEnvironment env, TypeDecl parentTypeDecl)
    {
        // Only class types — protocol metatype dispatch requires AnyObject
        if (parentTypeDecl is not ClassDecl)
            return false;

        // Constructor params must not reference the parent's generic type parameters
        var genericParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet();

        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (MethodWrapperEmitter.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames))
                return false;
        }

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
    private static void EmitThrowingStructBody(SwiftWriter sw, string callExpr, string swiftTypeName, bool isFailable)
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
            sw.WriteLines($$"""
                do {
                    let result = try {{callExpr}}
                    resultPtr.assumingMemoryBound(to: {{swiftTypeName}}.self).initialize(to: result)
                } catch {
                    errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
                }
                """);
        }
    }
}
