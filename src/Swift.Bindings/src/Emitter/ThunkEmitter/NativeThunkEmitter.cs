// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;

namespace BindingsGeneration;

/// <summary>
/// Central coordinator for native ARM64 thunk emission.
/// Determines whether a method can be thunked (vs. requiring @_cdecl wrapper),
/// generates unique thunk symbols, and builds ThunkDescriptors for assembly codegen.
///
/// Thunks handle conditions 5-9 and 11 from the RequiresCdeclForAbiSafety condition map:
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
///   3. Class allocating constructors (C# codegen coupled with @_cdecl pattern)
///   4. Struct constructors (C# codegen coupled with @_cdecl pattern)
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

        // Constructors: C# ConstructorHandler codegen is coupled with @_cdecl pattern
        // (UsesCdeclConstructorWrapper, explicit resultPtr, IntPtr vs SwiftIndirectResult).
        // Constructor thunks need dedicated C# codegen — defer to a future session.
        if (methodDecl.IsConstructor)
            return false;

        // Inout parameters — write-back semantics incompatible
        if (methodDecl.CSSignature.Skip(1).Any(a => a.IsInOut))
            return false;

        // Inherited generic context — can't emit thunk for nested types with inherited generics
        if (env.ParentDecl is TypeDecl td && td.IsGeneric
            && WrapperValidation.IsInheritedGenericContext(td))
            return false;

        // Tuple and closure return types can't be lowered (TypeLowering only handles NamedTypeSpec).
        // Tuples >16B need return bridging that requires field layout knowledge the thunk lacks.
        // Closure returns have complex ABI (func pointer + context) incompatible with thunks.
        var returnSpec = methodDecl.CSSignature.First().SwiftTypeSpec;
        if (!returnSpec.IsEmptyTuple && returnSpec is TupleTypeSpec or ClosureTypeSpec)
            return false;

        // DynamicSelf (Self return type): The C# marshaller treats DynamicSelf as requiring
        // indirect result (IsTypeInherentlyIndirect returns true), but it's actually a class
        // pointer returned in x0 (single register). The PInvokeEmitter has a fast path that
        // converts this to IntPtr for @_cdecl/thunk, but reject from thunking as defense-in-depth
        // to avoid any mismatch between indirect result expectations and the thunk assembly.
        if (returnSpec.IsDynamicSelf)
            return false;

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
    /// <returns>The thunk symbol (with leading underscore).</returns>
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
            IsInstanceMethod: methodDecl.MethodType == MethodType.Instance && !isConstructor,
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
    /// For void returns and types that don't need return bridging (≤16B), lowering isn't required.
    /// For types that DO need return bridging (>16B direct returns), lowering must succeed.
    /// </summary>
    private static bool IsReturnTypeLowerable(MethodEnvironment env)
    {
        var returnSpec = env.MethodDecl.CSSignature.First().SwiftTypeSpec;
        if (returnSpec.IsEmptyTuple)
            return true; // Void — no return to lower

        var lowering = TypeLowering.LowerReturnType(returnSpec, env.TypeDatabase);
        if (lowering != null)
            return true; // Successfully lowered

        // Can't lower — check if we actually need lowering for this return type
        // If the return doesn't need bridging (≤16B or indirect via x8), thunk can still work
        // via tail call. But if it DOES need bridging, we can't thunk it.
        return !NeedsReturnBridging(returnSpec, env.TypeDatabase);
    }

    /// <summary>
    /// Checks if a return type needs bridging (Swift returns in registers but cdecl returns via x8).
    /// This happens for direct returns > 16 bytes.
    /// </summary>
    private static bool NeedsReturnBridging(TypeSpec returnSpec, ITypeDatabase typeDb)
    {
        // If we can't even look up the type, assume it doesn't need bridging
        // (it would use indirect return, which both ABIs handle the same way)
        if (!typeDb.TryGetTypeRecord(returnSpec, out var record))
            return false;

        // Classes return a pointer (8 bytes) — no bridging
        if (record.Kind == TypeRecordKind.Class)
            return false;

        // Non-frozen structs are always indirect — no bridging
        if (record.Kind == TypeRecordKind.Struct && !record.Flags.HasFlag(TypeRecordFlags.Frozen))
            return false;

        // Frozen struct with inline size > 16 and ≤ 32 needs register→buffer bridging
        if (record.InlineSize.HasValue && record.InlineSize.Value > 16 && record.InlineSize.Value <= 32)
            return true;

        return false;
    }

    /// <summary>
    /// Checks if the self type can be lowered for instance methods on frozen structs.
    /// Non-frozen structs and classes don't need self lowering (they use pointer self).
    /// Frozen structs with InlineSize > 8 need multi-register self passing which
    /// ThunkAssemblyEmitter doesn't support yet (only does `mov x20, x0` for single register).
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

        // Frozen struct — check if self fits in a single register (8 bytes).
        // ThunkAssemblyEmitter only handles single-register self (`mov x20, x0`).
        // Multi-register self (InlineSize > 8) requires splitting across x20+x21 etc.,
        // which the assembly emitter doesn't support yet. Route to @_cdecl instead.
        var parentSpec = new NamedTypeSpec(parentType.SwiftTypeName.ModuleQualifiedName);
        if (env.TypeDatabase.TryGetTypeRecord(parentSpec, out var record))
        {
            if (record.InlineSize.HasValue && record.InlineSize.Value > 8)
            {
                // Multi-register self — ThunkAssemblyEmitter can't handle this yet.
                // Fall back to @_cdecl which handles it correctly.
                return false;
            }
        }

        return true; // Single-register or unknown — thunk can handle
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
        // The TBD uses the underscore-prefixed symbol
        var prefixedSymbol = "_" + swiftSymbol;
        return moduleDecl.ExportedSymbols.Contains(prefixedSymbol);
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
