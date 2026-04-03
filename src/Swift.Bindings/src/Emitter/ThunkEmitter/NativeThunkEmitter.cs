// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;

namespace BindingsGeneration;

/// <summary>
/// Central coordinator for native ARM64 thunk emission.
/// Determines whether a method can be thunked (vs. requiring @_cdecl wrapper),
/// generates unique thunk symbols, and builds ThunkDescriptors for assembly codegen.
///
/// Thunks handle conditions 3, 5-9, and 11 from the RequiresCdeclForAbiSafety condition map:
///   3. Class allocating constructors (metatype in x20, pointer return in x0)
///   5. Static class methods (hidden metatype)
///   6. Non-final instance methods (Tj dispatch)
///   7. Non-frozen struct instance members (SwiftSelf mismatch)
///   8. Frozen struct self > 8B (multi-register self)
///   9. Return type > 16B (multi-register return)
///  11. Non-blittable params > ABI size limits (only when params are lowerable)
///
/// Thunks do NOT handle (deferred):
///   1. Typed throws (needs Swift-side error boxing)
///   2. Generic type constructors (needs Swift compiler for specialization)
///   3. Struct constructors (Mono AOT can't JIT LibraryImport struct returns; see Session 4)
///   4. Failable constructors (return Optional<Self>, needs indirect result)
///  10. Closure parameters (needs Swift adapter code)
/// </summary>
public static class NativeThunkEmitter
{
    /// <summary>
    /// Determines whether a method can be routed through a native ARM64 thunk
    /// instead of a @_cdecl Swift wrapper.
    ///
    /// Returns true when ALL of these conditions are met:
    /// - Not async (different calling convention entirely — swifttailcc)
    /// - Not generic (needs type metadata + witness tables from Swift runtime)
    /// - No typed throws (needs Swift-side error boxing)
    /// - No closure parameters (needs Swift adapter code for delegate → closure bridge)
    /// - No variadic parameters (@_cdecl can't call variadic methods either)
    /// - No indirect result required (SwiftIndirectResult maps to x8 only under CallConvSwift)
    /// - Not a struct constructor (Mono AOT can't JIT LibraryImport struct returns)
    /// - Not a failable constructor (returns Optional&lt;Self&gt;, needs indirect result)
    /// - Not a generic type constructor (needs specialized metatype dispatch)
    /// - In xcframework mode (thunk binary needs the wrapper library)
    /// - ABI field layout available for return type (if thunk needs return bridging)
    /// </summary>
    /// <param name="env">The method environment.</param>
    /// <returns>True if the method can be thunked.</returns>
    public static bool ShouldEmitThunk(MethodEnvironment env)
    {
        var methodDecl = env.MethodDecl;

        // xcframework mode required (thunks live in the wrapper library)
        if (!WrapperValidation.IsXCFrameworkMode(env.TypeDatabase))
            return false;

        // Async functions use swifttailcc (coroutine-based) — completely different ABI
        if (methodDecl.IsAsync)
            return false;

        // Generic methods need type metadata + witness tables from Swift runtime
        if (methodDecl.IsGeneric)
            return false;

        // Typed throws requires Swift-side error boxing (x21 has raw typed error)
        if (methodDecl.HasTypedThrows)
            return false;

        // Closure parameters need Swift adapter code (C# delegate → Swift closure bridge)
        if (HasClosureParameters(env))
            return false;

        // Variadic parameters: T... in Swift can't be called from thunks
        if (methodDecl.HasVariadicParameter)
            return false;

        // Generic type constructors need specialized metatype dispatch via Swift compiler
        if (methodDecl.IsConstructor && env.ParentDecl is TypeDecl genericParent
            && genericParent.IsGeneric)
            return false;

        // Non-copyable struct parent — can't thunk
        if (WrapperValidation.IsNonCopyableStructParent(env.ParentDecl))
            return false;

        // Module internal or SPI protected — not callable
        if (methodDecl.IsModuleInternal || methodDecl.IsSpiProtected)
            return false;

        // Actor isolation — needs async dispatch
        if (WrapperValidation.IsActorIsolatedMember(env.ParentDecl,
            methodDecl.IsActorIsolated, methodDecl.IsMainActorIsolated))
            return false;

        // Struct constructors: The thunk handles x8 indirect return correctly (Session 4 research
        // proved AAPCS64 x8 works for struct returns >16B under CallConvCdecl). However, Mono's
        // AOT compiler can't generate the managed-to-native wrapper when LibraryImport returns
        // a struct type ("Attempting to JIT compile method" in aot-only mode). The @_cdecl wrapper
        // approach (void return + IntPtr resultPtr) avoids this by never returning a struct.
        // Future: could use DllImport instead of LibraryImport, or explicit resultPtr in thunk.
        if (methodDecl.IsConstructor && env.ParentDecl is StructDecl)
            return false;

        // Failable constructors (init?) return Optional<Self> which requires indirect result
        // handling. The thunk can't bridge Optional return types.
        if (methodDecl.IsConstructor && methodDecl.IsFailable)
            return false;

        // Constructor class reference parameters: Swift init parameters follow +1 owned
        // convention for class references — the init body retains for storage then releases
        // the caller's reference. Our thunk passes +0 (raw pointer from C#), so the release
        // consumes a reference that doesn't exist → double-release when both the C# GC and
        // the object's deinit release the same reference. Fall back to @_cdecl wrapper where
        // Swift handles the retain/release automatically.
        if (methodDecl.IsConstructor && HasClassReferenceParameters(env))
            return false;

        // ObjC-bridgeable value types (URL) require @_cdecl wrappers for ObjC→Swift bridge
        // conversion. Thunks pass raw IntPtr (ObjC pointer) but Swift expects the value type.
        if (HasObjCBridgeableParamsOrReturn(env))
            return false;

        // Inout parameters — write-back semantics incompatible
        if (methodDecl.CSSignature.Skip(1).Any(a => a.IsInOut))
            return false;

        // Inherited generic context — can't emit thunk for nested types with inherited generics
        if (env.ParentDecl is TypeDecl td && td.IsGeneric
            && WrapperValidation.IsInheritedGenericContext(td))
            return false;

        // Non-frozen struct property accessors use opaque accessor calling conventions
        // that are incompatible with thunks:
        // - Getters: write result to indirect buffer via x8, even for small return types
        //   (e.g., 1-byte enum). Our thunk doesn't set x8 → SIGSEGV.
        // - Setters: read the new value from an indirect buffer at [x0], not from x0 directly.
        //   Our thunk passes the raw value in x0 → SIGSEGV.
        // This is a resilient ABI requirement: opaque accessors use indirect buffers so the
        // accessor signature remains stable even if the property type changes size.
        // Fall back to @_cdecl wrappers which handle the convention correctly.
        if (methodDecl.IsAccessor && env.ParentDecl is StructDecl accessorParentStruct
            && !accessorParentStruct.IsFrozen)
            return false;

        // Getter dispatch thunks (vgTj) use x9 for vtable lookup, preserving x8 as the
        // indirect return buffer. The getter writes value-type results to [x8], but our
        // bridge thunk doesn't set x8, causing SIGSEGV. Class returns use x0 (no x8 needed).
        // Direct-dispatch getters (final method, final class) use standard swiftcc return
        // convention, which TypeLowering handles correctly — no x8 hazard.
        // ObjC-bridged class types (UIColor, UIFont, etc.) are also rejected: the Tj dispatch
        // calls the Swift vtable getter which may follow ObjC return semantics (+0 unretained),
        // but the C# marshalling expects +1 (passRetained). The @_cdecl wrapper path handles
        // this correctly via explicit Unmanaged.passRetained() in the Swift wrapper.
        var returnSpec = methodDecl.CSSignature.First().SwiftTypeSpec;
        if (methodDecl.IsAccessor && !returnSpec.IsEmptyTuple)
        {
            // Only reject when the accessor actually uses a dispatch thunk (Tj suffix).
            // SwiftCallTargetResolver is the single source of truth for Tj gating.
            bool usesDispatchThunk = SwiftCallTargetResolver.Resolve(methodDecl, env.ParentDecl)
                != methodDecl.MangledName;
            if (usesDispatchThunk)
            {
                if (!env.TypeDatabase.TryGetTypeRecord(returnSpec, out var accessorReturnRecord)
                    || accessorReturnRecord.Kind != TypeRecordKind.Class
                    || MarshallingHelpers.IsObjCBridged(accessorReturnRecord))
                    return false;
            }
        }

        // Setter dispatch thunks (vsTj) pass the new value via indirect buffer for non-class
        // types. The Swift accessor reads the value from [x0] (indirect), but our thunk passes
        // the raw value in x0 → SIGSEGV for enums and value types. Class types are fine because
        // the value IS a pointer (ARC-retained). Mirrors the getter gate above.
        // ObjC-bridged class types also rejected: the setter expects @owned (+1) but the thunk
        // passes raw IntPtr (+0). The @_cdecl wrapper uses takeUnretainedValue() which lets
        // Swift ARC handle the retain on property assignment correctly.
        if (methodDecl.IsAccessor && returnSpec.IsEmptyTuple
            && MarshallingHelpers.MethodIsSetter(methodDecl))
        {
            bool usesDispatchThunk = SwiftCallTargetResolver.Resolve(methodDecl, env.ParentDecl)
                != methodDecl.MangledName;
            if (usesDispatchThunk)
            {
                // The property type is the setter's value parameter.
                // CSSignature: [0]=void return, [1]=value param (self is not in CSSignature).
                var valueParam = methodDecl.CSSignature.ElementAtOrDefault(1);
                if (valueParam != null)
                {
                    var setterTypeSpec = valueParam.SwiftTypeSpec;
                    if (!env.TypeDatabase.TryGetTypeRecord(setterTypeSpec, out var setterTypeRecord)
                        || setterTypeRecord.Kind != TypeRecordKind.Class
                        || MarshallingHelpers.IsObjCBridged(setterTypeRecord))
                        return false;
                }
            }
        }

        // Tuple and closure return types can't be lowered (TypeLowering only handles NamedTypeSpec).
        // Tuples >16B need return bridging that requires field layout knowledge the thunk lacks.
        // Closure returns have complex ABI (func pointer + context) incompatible with thunks.
        if (!returnSpec.IsEmptyTuple && returnSpec is TupleTypeSpec or ClosureTypeSpec)
            return false;

        // DynamicSelf (Self return type): The C# marshaller treats DynamicSelf as requiring
        // indirect result (IsTypeInherentlyIndirect returns true), but it's actually a class
        // pointer returned in x0 (single register). The PInvokeEmitter has a fast path that
        // converts this to IntPtr for @_cdecl/thunk, but reject from thunking as defense-in-depth
        // to avoid any mismatch between indirect result expectations and the thunk assembly.
        if (returnSpec.IsDynamicSelf)
            return false;

        // Optional<T> returns require indirect result (write to buffer via resultPtr).
        // The thunk can't handle this — it returns directly in x0, which doesn't work
        // for Optional<value-type> where the @_cdecl wrapper writes to a caller-provided
        // buffer. Must check explicitly here because MethodRequiresIndirectResult relies
        // on UsesCdeclPropertyWrapper/UsesCdeclMethodWrapper flags, which aren't set yet
        // when ShouldEmitThunk is evaluated (flags are assigned later by PropertyHandler/
        // MethodHandler after the thunk decision).
        {
            var returnSpec2 = methodDecl.CSSignature.First().SwiftTypeSpec;
            if (MethodWrapperEmitter.IsOptionalType(returnSpec2))
                return false;
        }

        // Methods requiring indirect result can't be thunked yet (Session 4).
        // Under CallConvCdecl, SwiftIndirectResult becomes a regular parameter (x0),
        // but the thunk reads x8 (AAPCS64 indirect return convention) → SIGSEGV.
        // This catches non-frozen structs, complex enums, and other types where
        // MarshallingHelpers would add SwiftIndirectResult to the P/Invoke.
        // Guard with try-catch: MethodRequiresIndirectResult calls GetTypeRecordOrThrow
        // which throws for types not in the database (e.g., ObjC-only Foundation types).
        // ShouldEmitThunk runs before type validation, so unknown types are possible.
        try
        {
            if (MarshallingHelpers.MethodRequiresIndirectResult(env))
                return false;
        }
        catch
        {
            return false; // Unknown return type — can't thunk safely
        }

        // All non-trivial parameters must be lowerable for correct register mapping.
        // Complex types (Optional<String>, non-frozen structs) that can't be lowered
        // need Swift-side type transformations (@_cdecl wrapper) the thunk can't provide.
        if (!AreAllParametersLowerable(env))
            return false;

        // Return type must be lowerable for thunk codegen (if it needs return bridging)
        if (!IsReturnTypeLowerable(env))
            return false;

        // Self type must be lowerable for instance methods on frozen structs
        if (!IsSelfTypeLowerable(env))
            return false;

        // Verify the Swift call target symbol exists in the module's exported symbols (TBD).
        // The thunk emits `bl <swift_symbol>` — if the symbol doesn't exist (e.g., ObjC-routed
        // properties, @objc dynamic methods), the linker will fail with undefined symbol errors.
        // Fall back to @_cdecl wrapper which can call through the Swift runtime instead.
        if (!IsSwiftCallTargetExported(env))
            return false;

        return true;
    }

    /// <summary>
    /// Generates a unique thunk symbol name for a method.
    /// Delegates to ThunkAssemblyEmitter.GenerateThunkSymbol.
    /// </summary>
    /// <param name="methodDecl">The method declaration.</param>
    /// <param name="moduleName">The Swift module name.</param>
    /// <returns>The thunk C symbol name (without platform prefix).</returns>
    public static string GetThunkSymbol(MethodDecl methodDecl, string moduleName)
    {
        return ThunkAssemblyEmitter.GenerateThunkSymbol(moduleName, methodDecl.MangledName);
    }

    /// <summary>
    /// Emits a native ARM64 thunk for the given method environment.
    /// Builds a ThunkDescriptor from the environment, runs TypeLowering, and calls
    /// ThunkAssemblyEmitter to produce assembly code.
    /// </summary>
    /// <param name="env">The method environment.</param>
    /// <param name="moduleName">The Swift module name.</param>
    /// <param name="asmBuilder">StringBuilder to append the assembly output to.</param>
    /// <param name="originalSwiftMangledName">The original Swift mangled name (before MangledName was overwritten with the thunk symbol).
    /// Required because callers set MangledName to the thunk symbol before calling EmitThunk.
    /// If null, falls back to methodDecl.MangledName (for backward compatibility in tests).</param>
    /// <returns>True if the thunk was emitted; false if lowering failed.</returns>
    public static bool EmitThunk(MethodEnvironment env, string moduleName, StringBuilder asmBuilder, string? originalSwiftMangledName = null)
    {
        var methodDecl = env.MethodDecl;

        // Use the original Swift mangled name for thunk symbol generation and Swift call target resolution.
        // The caller overwrites methodDecl.MangledName with the thunk symbol BEFORE calling EmitThunk,
        // so we must use the original name to avoid double-hashing and incorrect call targets.
        var swiftMangledName = originalSwiftMangledName ?? methodDecl.MangledName;

        // Build the thunk symbol from the original mangled name
        var thunkSymbol = ThunkAssemblyEmitter.GenerateThunkSymbol(moduleName, swiftMangledName);

        // Resolve the Swift call target symbol using the original mangled name
        var swiftSymbol = SwiftCallTargetResolver.ResolveWithPrefix(swiftMangledName, methodDecl, env.ParentDecl);

        // Lower the return type
        TypeLoweringResult? returnLowering = null;
        var returnSpec = methodDecl.CSSignature.First().SwiftTypeSpec;
        if (!returnSpec.IsEmptyTuple)
        {
            returnLowering = TypeLowering.LowerReturnType(returnSpec, env.TypeDatabase);
            // If return type can't be lowered and needs bridging, bail
            if (returnLowering == null && NeedsReturnBridging(returnSpec, env.TypeDatabase))
                return false;
        }

        // Lower the self type for instance methods on frozen structs
        TypeLoweringResult? selfLowering = null;
        if (methodDecl.MethodType == MethodType.Instance && !methodDecl.IsConstructor
            && env.ParentDecl is TypeDecl selfParent)
        {
            var parentSpec = new NamedTypeSpec(selfParent.SwiftTypeName.ModuleQualifiedName);
            selfLowering = TypeLowering.LowerParameterType(parentSpec, env.TypeDatabase);
        }

        // Count cdecl integer and float parameters (excluding self, error out pointer)
        int intParamCount = 0;
        int floatParamCount = 0;
        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;

            var paramLowering = TypeLowering.LowerParameterType(arg.SwiftTypeSpec, env.TypeDatabase);
            if (paramLowering != null && paramLowering.Slots.Count == 1)
            {
                if (paramLowering.Slots[0].File == RegisterFile.Float)
                    floatParamCount++;
                else
                    intParamCount++;
            }
            else
            {
                // Multi-slot or unlowerable param — count as 1 integer register (pointer)
                intParamCount++;
            }
        }

        // Resolve metadata accessor symbol for constructors and static methods
        string? metadataAccessorSymbol = null;
        bool isConstructor = methodDecl.IsConstructor;
        bool isStaticMethod = methodDecl.MethodType == MethodType.Static && !isConstructor;

        if (isConstructor || isStaticMethod)
        {
            metadataAccessorSymbol = GetMetadataAccessorSymbol(env.ParentDecl, env.TypeDatabase);
            if (metadataAccessorSymbol == null)
                return false; // Can't emit thunk without metadata accessor
        }

        var descriptor = new ThunkDescriptor(
            ThunkSymbol: thunkSymbol,
            SwiftSymbol: swiftSymbol,
            ReturnLowering: returnLowering,
            SelfLowering: selfLowering,
            ParameterCount: intParamCount,
            FloatParameterCount: floatParamCount,
            // Free functions have MethodType.Instance (ABI parser defaults non-@static to Instance),
            // but they have no self parameter. Guard on ParentDecl being a TypeDecl to exclude them.
            IsInstanceMethod: methodDecl.MethodType == MethodType.Instance && !isConstructor
                && env.ParentDecl is TypeDecl,
            IsStaticMethod: isStaticMethod,
            IsConstructor: isConstructor,
            Throws: methodDecl.Throws,
            MetadataAccessorSymbol: metadataAccessorSymbol);

        asmBuilder.Append(ThunkAssemblyEmitter.EmitThunk(descriptor));
        return true;
    }

    /// <summary>
    /// Checks if the method has any closure parameters (including optional closures).
    /// </summary>
    private static bool HasClosureParameters(MethodEnvironment env)
    {
        return env.MethodDecl.CSSignature.Skip(1).Any(env.ClosureHandler.IsClosure);
    }

    /// <summary>
    /// Checks if the method has any class reference parameters (including Optional&lt;Class&gt;).
    /// Used to reject constructors with class params from thunking — Swift init parameters
    /// follow +1 owned convention for class references, but thunks pass +0.
    /// </summary>
    private static bool HasClassReferenceParameters(MethodEnvironment env)
    {
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;

            var spec = arg.SwiftTypeSpec;

            // Unwrap Optional<T> to check the inner type
            if (spec is NamedTypeSpec named && named.Name == "Swift.Optional"
                && named.GenericParameters.Count == 1)
            {
                spec = named.GenericParameters[0];
            }

            if (env.TypeDatabase.TryGetTypeRecord(spec, out var record)
                && record.Kind == TypeRecordKind.Class)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if the method has any ObjC-bridgeable value type parameters or return type.
    /// ObjC-bridgeable types (URL) cross the @_cdecl boundary as ObjC pointers and need
    /// Swift-side bridge conversion (Unmanaged → as! URL) that only @_cdecl wrappers provide.
    /// </summary>
    private static bool HasObjCBridgeableParamsOrReturn(MethodEnvironment env)
    {
        // Check parameters (unwrap Optional<T> to check inner type)
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;
            var spec = MarshallingHelpers.UnwrapOptionalTypeSpec(arg.SwiftTypeSpec) ?? arg.SwiftTypeSpec;
            if (env.TypeDatabase.TryGetTypeRecord(spec, out var record)
                && MarshallingHelpers.IsObjCBridgeable(record))
                return true;
            // ObjC-bridgeable containers ([URL], [String: URL], Set<URL>) also need @_cdecl wrappers
            if (CdeclParamMapper.IsObjCBridgeableContainer(spec, env.TypeDatabase) ||
                CdeclParamMapper.IsOptionalObjCBridgeableContainer(arg.SwiftTypeSpec, env.TypeDatabase))
                return true;
        }
        // Check return type (unwrap Optional<T> to check inner type)
        var returnSpec = env.MethodDecl.CSSignature.First().SwiftTypeSpec;
        var unwrappedReturn = MarshallingHelpers.UnwrapOptionalTypeSpec(returnSpec) ?? returnSpec;
        if (!unwrappedReturn.IsEmptyTuple && env.TypeDatabase.TryGetTypeRecord(unwrappedReturn, out var retRecord)
            && MarshallingHelpers.IsObjCBridgeable(retRecord))
            return true;
        // ObjC-bridgeable container returns also need @_cdecl wrappers
        if (!returnSpec.IsEmptyTuple &&
            (CdeclParamMapper.IsObjCBridgeableContainer(returnSpec, env.TypeDatabase) ||
             CdeclParamMapper.IsOptionalObjCBridgeableContainer(returnSpec, env.TypeDatabase)))
            return true;
        return false;
    }

    /// <summary>
    /// Checks if all non-trivial parameters can be lowered by TypeLowering.
    /// Parameters that can't be lowered (Optional&lt;String&gt;, non-frozen structs, complex enums)
    /// need Swift-side type transformations that only @_cdecl wrappers provide.
    /// </summary>
    private static bool AreAllParametersLowerable(MethodEnvironment env)
    {
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;

            var lowering = TypeLowering.LowerParameterType(arg.SwiftTypeSpec, env.TypeDatabase);
            if (lowering != null && lowering.Slots.Count <= 1)
                continue; // Single-slot param — safe for thunk register shifting

            // Multi-slot params (e.g., 16B struct = 2 registers) can't be thunked:
            // cdecl and swiftcc may disagree on register file (int vs float) for mixed-type structs,
            // and the thunk assembly only does simple 1:1 register shifting.
            if (lowering != null && lowering.Slots.Count > 1)
                return false;

            // Can't lower — check if it's a class type (always a single pointer, safe)
            if (env.TypeDatabase.TryGetTypeRecord(arg.SwiftTypeSpec, out var record)
                && record.Kind == TypeRecordKind.Class)
                continue;

            // Unknown/unlowerable parameter — thunk can't handle it safely
            return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if the return type can be lowered for thunk codegen.
    /// For void returns and single-register returns (≤8B), lowering isn't required.
    /// For multi-register returns (>8B) without ABI field layout, the register assignment
    /// is unknown — reject to @_cdecl wrapper for safety.
    /// </summary>
    private static bool IsReturnTypeLowerable(MethodEnvironment env)
    {
        var returnSpec = env.MethodDecl.CSSignature.First().SwiftTypeSpec;
        if (returnSpec.IsEmptyTuple)
            return true; // Void — no return to lower

        var lowering = TypeLowering.LowerReturnType(returnSpec, env.TypeDatabase);
        if (lowering != null)
            return true; // Successfully lowered

        // Can't lower — check if we actually need lowering for this return type.
        // Single-register returns (≤8B) and indirect returns are safe without lowering.
        // Multi-register returns (>8B frozen structs) without layout are NOT safe —
        // the thunk can't determine HFA vs non-HFA for correct return bridging.
        return !NeedsReturnBridging(returnSpec, env.TypeDatabase);
    }

    /// <summary>
    /// Checks if a return type needs bridging that TypeLowering couldn't provide.
    /// Called only when TypeLowering returned null — determines whether to reject
    /// the function from thunking (fall back to @_cdecl wrapper).
    ///
    /// Conservative by default: only explicitly safe return types (single-register returns
    /// like class pointers, simple enums, small frozen structs) are allowed through.
    /// Everything else is rejected because without TypeLowering we can't determine the
    /// register layout, and cdecl↔swiftcc return conventions may differ for multi-register
    /// non-HFA structs (e.g., Swift.String returns x0+x1 under swiftcc but Mono JIT may
    /// expect x8 indirect return under cdecl).
    /// </summary>
    private static bool NeedsReturnBridging(TypeSpec returnSpec, ITypeDatabase typeDb)
    {
        // Type not in database: unknown layout. Reject from thunking.
        // This catches Optional<T>, generic types, and unresolved framework types.
        if (!typeDb.TryGetTypeRecord(returnSpec, out var record))
            return true;

        // Classes return a single pointer (8 bytes) — safe for tail call
        if (record.Kind == TypeRecordKind.Class)
            return false;

        // Non-frozen structs are always indirect — both ABIs agree, no bridge needed.
        // Exception: bound generic non-frozen structs (e.g., Pair<A, B>) need bridging
        // because MethodRequiresIndirectResult returns false for them during ShouldEmitThunk
        // evaluation (UsesCdecl flags not set yet), so the cdecl P/Invoke won't set up x8
        // for indirect result. Fall back to @_cdecl wrapper which handles the buffer.
        if (record.Kind == TypeRecordKind.Struct && !record.Flags.HasFlag(TypeRecordFlags.Frozen))
        {
            if (returnSpec is NamedTypeSpec { ContainsGenericParameters: true })
                return true;
            return false;
        }

        // Frozen simple enums return in a single register — safe for tail call.
        // Non-frozen simple enums are returned indirectly (resilient ABI), so they
        // DO need bridging the thunk can't provide — fall through to return true.
        if (record.Kind == TypeRecordKind.Enum && record.Flags.HasFlag(TypeRecordFlags.SimpleEnum)
            && record.Flags.HasFlag(TypeRecordFlags.Frozen))
            return false;

        // Frozen struct ≤ 8 bytes: fits in a single register — safe for tail call
        if (record.Kind == TypeRecordKind.Struct && record.Flags.HasFlag(TypeRecordFlags.Frozen)
            && record.InlineSize.HasValue && record.InlineSize.Value <= 8)
            return false;

        // Everything else: multi-register or unknown layout — needs bridging
        // that TypeLowering couldn't provide. Reject to @_cdecl wrapper.
        return true;
    }

    /// <summary>
    /// Checks if the self type can be handled by the thunk for instance methods.
    ///
    /// ABI background: ARM64 swiftcc uses a single register (x20) for self via the
    /// LLVM `swiftself` attribute. x21 is swifterror — NOT a self overflow register.
    /// For value types &gt; 8B, self is passed indirectly (pointer in x20), because
    /// x20 is a single 64-bit register that can't hold a multi-field struct value.
    ///
    /// PInvokeEmitter always emits IntPtr for self on thunked methods (line ~639),
    /// so cdecl passes self as a single pointer register. The thunk's
    /// `mov x20, x{ParameterCount}` puts this pointer in x20.
    ///
    /// For &gt; 8B frozen structs, this is correct: Swift reads self indirectly through
    /// the pointer. Field layout (int/float mix) is irrelevant — the thunk forwards
    /// the pointer without decomposing the struct.
    ///
    /// For ≤ 8B frozen structs, there is an ABI mismatch: swiftcc expects the VALUE
    /// in x20, but the thunk puts a pointer. In practice this is safe because ≤ 8B
    /// frozen struct instance methods are @inlinable and not exported in the TBD, so
    /// IsSwiftCallTargetExported() filters them before they reach assembly emission.
    /// This gate was accepted since Phase 1 and is not modified here.
    ///
    /// Note: TypeLowering.LowerStruct() models frozen structs as multi-register direct
    /// values (for return type bridging). That lowering is NOT used for self — the
    /// ThunkDescriptor.SelfLowering field is stored but never read by ThunkAssemblyEmitter.
    /// </summary>
    private static bool IsSelfTypeLowerable(MethodEnvironment env)
    {
        if (env.MethodDecl.MethodType == MethodType.Static || env.MethodDecl.IsConstructor)
            return true; // No self parameter

        if (env.ParentDecl is not TypeDecl parentType)
            return true;

        // Classes use pointer self — no lowering needed
        if (env.ParentDecl is ClassDecl)
            return true;

        // Non-frozen structs use pointer self — no lowering needed
        if (env.ParentDecl is StructDecl structDecl && !structDecl.IsFrozen)
            return true;

        // Frozen struct — verify InlineSize is available.
        // >8B: swiftcc passes self as pointer in x20. PInvokeEmitter passes IntPtr.
        //      Thunk's `mov x20, x{ParameterCount}` forwards the pointer. Correct.
        // ≤8B: swiftcc passes self as value in x20, but the thunk passes IntPtr (pointer).
        //      Accepted since Phase 1. Safe in practice: ≤8B frozen struct methods are
        //      @inlinable, have no TBD entry, and are filtered by IsSwiftCallTargetExported.
        var parentSpec = new NamedTypeSpec(parentType.SwiftTypeName.ModuleQualifiedName);
        if (env.TypeDatabase.TryGetTypeRecord(parentSpec, out var record)
            && record.InlineSize.HasValue)
        {
            return true; // InlineSize known — >8B is pointer ABI, ≤8B filtered by TBD gate
        }

        // TypeRecord not found or InlineSize unknown — conservatively reject.
        // Without InlineSize data we can't confirm this is a valid frozen struct.
        return false;
    }

    /// <summary>
    /// Verifies that the Swift call target symbol the thunk will reference (via bl instruction)
    /// is actually exported by the module's TBD. ObjC-routed properties (@objc dynamic) and
    /// methods without vtable entries don't emit Tj dispatch thunk symbols, so the thunk's
    /// bl instruction would reference a non-existent symbol and fail at link time.
    /// Returns true if the symbol is exported or if no TBD is available (optimistic).
    /// </summary>
    private static bool IsSwiftCallTargetExported(MethodEnvironment env)
    {
        var methodDecl = env.MethodDecl;
        var moduleDecl = methodDecl.ModuleDecl;
        if (moduleDecl?.ExportedSymbols == null)
            return true; // No TBD available — optimistically allow

        // Resolve the symbol the thunk would call (including Tj suffix for vtable dispatch)
        var swiftSymbol = SwiftCallTargetResolver.Resolve(methodDecl, env.ParentDecl);
        // ExportedSymbols stores symbols without leading underscore (stripped by TBD parser)
        return moduleDecl.ExportedSymbols.Contains(swiftSymbol);
    }

    /// <summary>
    /// Gets the metadata accessor symbol (with leading underscore) for the parent type.
    /// Used by constructors and static methods that need metatype in x20.
    /// The metadata accessor is stored in the TypeRecord (e.g., "$s6Module4TypeCMa" for classes).
    /// </summary>
    private static string? GetMetadataAccessorSymbol(BaseDecl parentDecl, ITypeDatabase typeDb)
    {
        if (parentDecl is not TypeDecl typeDecl)
            return null;

        // StructDecl has MetadataAccessor directly
        if (parentDecl is StructDecl sd && !string.IsNullOrEmpty(sd.MetadataAccessor))
            return "_" + sd.MetadataAccessor;

        // For classes (and other types), look up from TypeRecord in the TypeDatabase
        var parentSpec = new NamedTypeSpec(typeDecl.SwiftTypeName.ModuleQualifiedName);
        if (typeDb.TryGetTypeRecord(parentSpec, out var record)
            && !string.IsNullOrEmpty(record.MetadataAccessor))
            return "_" + record.MetadataAccessor;

        return null;
    }
}
