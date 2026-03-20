// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits per-method @_cdecl Swift wrappers that route instance/static method P/Invokes
/// through C calling convention, eliminating CallConvSwift ABI mismatches on NativeAOT/ARM64.
///
/// For each method, generates a @_cdecl free function in the wrapper library that:
/// - Receives C-compatible parameters (primitives pass through, structs/classes as pointers)
/// - Reconstructs self for instance methods (class: Unmanaged, struct: load/pointer)
/// - Calls the actual Swift method
/// - Returns the result via C ABI (class → retained pointer, struct → writes to result buffer)
///
/// Handles throwing methods, mutating struct methods, string returns (SBW_Utf8Slice),
/// and static methods. Follows the ConstructorWrapperEmitter/PropertyWrapperEmitter pattern.
/// State tracked on <see cref="ModuleEmissionContext"/>.
/// </summary>
public static class MethodWrapperEmitter
{
    /// <summary>
    /// Pure query: determines whether a method should use a @_cdecl wrapper.
    /// Guards: xcframework mode, non-generic parent, non-constructor, non-accessor,
    /// no closures, no protocol existentials, no async, etc.
    /// </summary>
    public static bool ShouldEmitWrapper(MethodEnvironment env)
    {
        // 1. Must NOT be a constructor (constructors handled by ConstructorWrapperEmitter)
        if (env.MethodDecl.IsConstructor)
            return false;

        // 2. Must NOT be an accessor (property accessors handled by PropertyWrapperEmitter; subscripts deferred)
        if (env.MethodDecl.IsAccessor)
            return false;

        // 3. Must NOT already have a cdecl property wrapper
        if (env.MethodDecl.UsesCdeclPropertyWrapper)
            return false;

        // 3b. Skip @_spi protected methods — wrapper can't access them without @_spi import
        if (env.MethodDecl.IsSpiProtected)
            return false;

        // 3c. Skip internal methods — wrapper can't call them from external code
        if (env.MethodDecl.IsModuleInternal)
            return false;

        // 4. xcframework mode required (wrapper library must exist)
        if (!WrapperValidation.IsXCFrameworkMode(env.TypeDatabase))
            return false;

        // 5. Must be on a type or module (free function)
        var parentTypeDecl = env.ParentDecl as TypeDecl;
        if (parentTypeDecl == null && env.ParentDecl is not ModuleDecl)
            return false;

        // 5b. Generic parent type — allow methods using protocol-based type erasure.
        // Two paths:
        // 1. Generic class with concrete params/return → existing protocol instance dispatch
        // 2. Generic struct/class with T-typed params/return → protocol with static method
        if (parentTypeDecl?.IsGeneric == true)
        {
            if (!CanEmitGenericWrapper(env, parentTypeDecl))
                return false;
        }

        // 6. No method-level generics
        if (env.MethodDecl.IsGeneric)
            return false;

        // 6b. Custom actor types / per-member custom actor: require async dispatch
        if (WrapperValidation.IsActorIsolatedMember(parentTypeDecl, env.MethodDecl.IsActorIsolated, env.MethodDecl.IsMainActorIsolated))
            return false;

        // 7. Not async (async uses its own wrapper pattern)
        if (env.MethodDecl.IsAsync)
            return false;

        // 8. Closure parameters: allowed only when NeedsClosureCdeclWrapper validates them
        // AND no plain async closures (GetSwiftClosureAdapterCode only emits sync adapters).
        if (env.MethodDecl.CSSignature.Skip(1).Any(env.ClosureHandler.IsClosure))
        {
            if (!ClosureEmitter.NeedsClosureCdeclWrapper(env.MethodDecl, env.ClosureHandler))
                return false;
            if (HasAnyAsyncClosure(env))
                return false;
        }

        // 11. Non-copyable struct guards
        if (WrapperValidation.IsNonCopyableStructParent(env.ParentDecl))
            return false;

        // 11b. No inout parameters — @_cdecl wrappers can't handle write-back semantics.
        // Primitive inout has no reconstruction line (can't make a mutable local), and non-primitive
        // inout would need post-call store-back through the pointer. Fall back to CallConvSwift.
        if (env.MethodDecl.CSSignature.Skip(1).Any(a => a.IsInOut))
            return false;

        // 11c. No variadic parameters — Swift variadic params (T...) appear as Array<T> in ABI JSON.
        // The @_cdecl wrapper would pass [T] where T... is expected, causing compilation error:
        // "cannot pass array of type '[T]' as variadic arguments of type 'T'"
        if (env.MethodDecl.HasVariadicParameter)
            return false;

        // 12. No nested frozen struct parameters
        if (HasNestedFrozenStructParameter(env))
            return false;

        // 12b. Non-primitive frozen struct parameters are now handled via UnsafeRawPointer
        // in @_cdecl wrappers — no longer a skip reason.

        // 13. Not already using wrapper library (DebugParam, ArraySlice, etc. own the wrapper)
        if (env.MethodDecl.UsesWrapperLibrary)
            return false;

        // 14-15d. Type-signature checks (metatype, opaque, DynamicSelf, unsupported generics).
        if (HasUnsupportedTypeSignature(env))
            return false;

        // 17. Nested type returns — ALLOWED. @_cdecl wrapper return types use C-compatible types
        //     (Int32 for simple enums, void+resultPtr for indirect results, UnsafeMutableRawPointer
        //     for class pointers). The nested type only appears in the function BODY.

        return true;
    }

    /// <summary>
    /// Returns true if the method has any unsupported type signature for @_cdecl wrapping.
    /// Covers: unsupported generic containers (Result, Optional&lt;existential&gt;), metatype
    /// params/return, opaque return types, and DynamicSelf on non-class parents.
    /// </summary>
    internal static bool HasUnsupportedTypeSignature(MethodEnvironment env)
    {
        // 14. No unsupported generic container params/returns (Array, Dictionary, Set, Optional<existential>).
        //     Optional<value-type> allowed (IndirectResult). Optional<existential> blocked (needs proxy).
        if (HasUnsupportedGenericContainerParamsOrReturn(env))
            return true;

        // 14b. No metatype parameters (Any.Type, T.Type) — not C-representable, renders as bare "Type"
        if (env.MethodDecl.CSSignature.Skip(1).Any(a => IsMetatypeType(a.SwiftTypeSpec)))
            return true;

        var returnSpec = env.MethodDecl.CSSignature.First().SwiftTypeSpec;

        // 14c. No metatype return types
        if (IsMetatypeType(returnSpec))
            return true;

        // 15. No opaque return types (some Protocol)
        if (returnSpec is ProtocolListTypeSpec { IsOpaque: true })
            return true;

        // 15b. Closure returns: allowed — routed through IndirectResult (resultPtr buffer).
        // @_cdecl wrapper writes closure to resultPtr via initializeMemory; C# reads SwiftClosureData.

        // 15c. Tuple returns: allowed — routed through IndirectResult (resultPtr buffer).
        // @_cdecl wrapper writes tuple to resultPtr via initializeMemory(as: (T1, T2).self).

        // 15d. DynamicSelf returns: allowed for class parents — Self resolves to parent class type.
        // @_cdecl wrapper returns Unmanaged.passRetained(result).toOpaque() (class pointer).
        // Structs/enums with DynamicSelf blocked — Unmanaged requires class type.
        if (returnSpec.IsDynamicSelf && env.ParentDecl is not ClassDecl)
            return true;

        return false;
    }

    /// <summary>
    /// Gets the @_cdecl symbol name for a method wrapper.
    /// Pure function — no side effects, safe to call before emission.
    /// </summary>
    public static string GetMethodSymbolName(string moduleName, string typeName, string methodName, string originalMangledName)
    {
        var hash = EmitterUtility.DeterministicHash8(originalMangledName);
        var safeTypeName = typeName.Replace(".", "_");
        return $"SBW_{moduleName}_{safeTypeName}_{methodName}_{hash}";
    }

    /// <summary>
    /// Emits a Swift @_cdecl wrapper function for a method.
    /// The wrapper receives C-compatible parameters, reconstructs self for instance methods,
    /// calls the Swift method, and returns the result via C ABI.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer for the wrapper .swift file.</param>
    /// <param name="env">The method environment with method info.</param>
    /// <param name="ctx">The per-module emission context for dedup tracking.</param>
    /// <param name="silgenTarget">Optional @_silgen_name symbol to call instead of direct method (for default param overloads).</param>
    /// <param name="silgenHasResultBuffer">When true, the silgen target has a _resultBuf parameter for large optional returns.</param>
    public static void EmitSwiftMethodWrapper(
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ModuleEmissionContext? ctx = null,
        string? silgenTarget = null,
        bool silgenHasResultBuffer = false)
    {
        ctx ??= ModuleEmissionContext.Default;

        var methodDecl = env.MethodDecl;
        var parentTypeDecl = env.ParentDecl as TypeDecl;
        var parentModuleDecl = env.ParentDecl as ModuleDecl;
        if (parentTypeDecl == null && parentModuleDecl == null) return;

        var symbolName = methodDecl.MangledName; // Already set to cdecl symbol by caller
        if (!ctx.TryAddMethodWrapperSymbol(symbolName))
            return; // Already emitted

        var moduleName = parentTypeDecl?.SwiftTypeName.Module ?? parentModuleDecl!.Name;
        var moduleQualifiedSwiftName = parentTypeDecl?.SwiftTypeName.ModuleQualifiedName ?? "";

        bool isClass = env.ParentDecl is ClassDecl;
        bool isStatic = methodDecl.MethodType == MethodType.Static || parentTypeDecl == null;
        bool isMutating = methodDecl.IsMutating;
        bool throws = methodDecl.Throws;

        // Determine return mapping
        var returnTypeSpec = methodDecl.CSSignature.First().SwiftTypeSpec;
        bool isVoidReturn = returnTypeSpec.IsEmptyTuple;
        bool isString = !isVoidReturn && WitnessDispatchEmitter.IsStringType(returnTypeSpec);

        var (returnMapping, needsResultPtr) = isVoidReturn
            ? (new PropertyWrapperEmitter.CdeclReturnMapping("Void", PropertyWrapperEmitter.CdeclReturnKind.Direct), false)
            : PropertyWrapperEmitter.GetCdeclReturnMapping(returnTypeSpec, env.TypeDatabase);

        // String returns always need result ptr (SBW_Utf8Slice)
        if (isString)
            needsResultPtr = true;

        // Ensure SBW_Utf8Slice infrastructure is emitted for string returns
        if (isString)
        {
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter, ctx);
            Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleName, ctx);
        }

        // Build Swift parameter list for the @_cdecl wrapper.
        // Phase ordering is determined by CdeclSignatureContract.
        // ResultPtr is handled outside the loop using the emitter's own needsResultPtr logic
        // (GetCdeclReturnMapping), matching the PInvoke pattern where HandleReturnType handles it.
        var swiftParams = new List<string>();
        var reconstructionLines = new List<string>();
        var closureAdapterLines = new List<string>();
        var callArgs = new List<string>();
        var keptArgs = methodDecl.CSSignature.Skip(1).ToList();

        // Result buffer parameter (first, for indirect results and string returns)
        if (needsResultPtr)
        {
            swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");
        }

        // When calling a silgen target, all parameters use _ (no external labels).
        bool omitLabels = silgenTarget != null;

        bool isGenericParent = WrapperValidation.IsGenericParent(env.ParentDecl);
        bool needsStaticDispatch = isGenericParent && parentTypeDecl != null &&
            NeedsGenericStaticDispatch(env, parentTypeDecl);

        // For generic static dispatch methods, delegate to the specialized emitter.
        if (needsStaticDispatch && !isStatic)
        {
            EmitGenericStaticDispatchMethod(swiftWriter, env, ctx, symbolName,
                parentTypeDecl!, moduleQualifiedSwiftName,
                isClass, isMutating, throws, returnTypeSpec, isVoidReturn, isString,
                needsResultPtr, returnMapping);
            return;
        }

        bool isGenericClassParent = IsGenericClassParent(env.ParentDecl);

        var order = CdeclSignatureContract.DetermineParameterOrder(env, overrideNeedsResultPtr: needsResultPtr);
        foreach (var phase in order.Phases)
        {
            switch (phase)
            {
                case CdeclPhase.ResultPtr:
                    break; // Already handled above

                case CdeclPhase.ErrorOut:
                    swiftParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>");
                    break;

                case CdeclPhase.Self:
                    if (isClass)
                        swiftParams.Add("_ self_: UnsafeMutableRawPointer");
                    else if (isMutating)
                        swiftParams.Add("_ self_: UnsafeMutableRawPointer");
                    else
                        swiftParams.Add("_ self_: UnsafeRawPointer");
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
                            // @autoclosure parameters: the adapted closure must be called with ()
                            // to forward the autoclosure value, not the closure itself.
                            var autoClosureSuffix = closureTypeSpec.IsAutoClosure ? "()" : "";
                            callArgs.Add($"{argLabel}{adapterName}{autoClosureSuffix}");
                            continue;
                        }

                        var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
                        if (label == "_")
                            label = $"arg{i}";
                        var (cdeclParam, reconstruction, callArg) = ConstructorWrapperEmitter.GetCdeclParamMapping(arg, label, env, omitLabels);

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

        // Build return clause
        string returnClause;
        if (isVoidReturn || needsResultPtr)
            returnClause = "";
        else
            returnClause = $" -> {returnMapping.cdeclReturnType}";

        // Build the Swift function name
        var swiftFuncName = $"_sbw_method_{EmitterUtility.DeterministicHash8(symbolName)}";

        // Build call arguments string
        var callArgString = string.Join(", ", callArgs);

        // Build the call expression
        // For mutating methods, use through-pointer access so mutations write back.
        string selfRef;
        if (isStatic && parentTypeDecl != null)
            selfRef = moduleQualifiedSwiftName;
        else if (isStatic)
            selfRef = "";  // Free function: no type prefix
        else if (isMutating && !isClass)
            selfRef = $"self_.assumingMemoryBound(to: {moduleQualifiedSwiftName}.self).pointee";
        else
            selfRef = "obj";

        // When calling a @_silgen_name target that has its own _resultBuf parameter
        // (for large optional returns), forward resultPtr and skip result handling in this wrapper.
        bool silgenHandlesResult = silgenTarget != null && silgenHasResultBuffer && needsResultPtr;
        if (silgenHandlesResult)
        {
            // Append resultPtr to the call — the silgen function writes to _resultBuf directly
            callArgString = string.IsNullOrEmpty(callArgString)
                ? "resultPtr"
                : $"{callArgString}, resultPtr";
        }

        string callExpr;
        if (silgenTarget != null)
        {
            callExpr = string.IsNullOrEmpty(selfRef)
                ? $"{silgenTarget}({callArgString})"
                : $"{selfRef}.{silgenTarget}({callArgString})";
        }
        else
        {
            var swiftMethodName = NameProvider.ParserNameToSwift(methodDecl);
            callExpr = string.IsNullOrEmpty(selfRef)
                ? $"{swiftMethodName}({callArgString})"
                : $"{selfRef}.{swiftMethodName}({callArgString})";
        }

        // For generic parent class types, emit protocol + conformance for type erasure
        string? protocolName = null;
        if (isGenericClassParent && !string.IsNullOrEmpty(moduleQualifiedSwiftName))
        {
            protocolName = $"_SBW_P_{EmitterUtility.DeterministicHash8(symbolName)}";
            EmitGenericClassProtocolAndConformance(
                swiftWriter, methodDecl, env, symbolName, moduleQualifiedSwiftName);
        }

        // Emit the @_cdecl function
        swiftWriter.WriteLine();
        var wrapperTarget = string.IsNullOrEmpty(moduleQualifiedSwiftName)
            ? $"free function {methodDecl.Name}"
            : $"{moduleQualifiedSwiftName}.{methodDecl.Name}";
        swiftWriter.WriteLines($$"""
            // Method @_cdecl wrapper for {{wrapperTarget}}.
            // Routes method through C calling convention to avoid CallConvSwift crash on NativeAOT.
            """);

        // Add @MainActor when wrapping @MainActor-isolated members.
        // Swift 6 requires the caller to share the isolation context, even at
        // -strict-concurrency=minimal. @MainActor on @_cdecl is a compile-time
        // constraint only (no ABI change). The C# consumer manages thread affinity.
        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            parentTypeDecl ?? (BaseDecl?)parentModuleDecl, methodDecl.IsMainActorIsolated, methodDecl.IsNonisolated);
        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, symbolName, needsMainActor);

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

        // Reconstruct self for instance methods
        if (!isStatic)
        {
            if (isGenericClassParent && protocolName != null)
            {
                // Generic parent class: use AnyObject + protocol cast for type erasure
                swiftWriter.WriteLine($"let obj = Unmanaged<AnyObject>.fromOpaque(self_).takeUnretainedValue() as! any {protocolName}");
            }
            else
            {
                EmitSelfReconstruction(swiftWriter, isClass, isMutating, moduleQualifiedSwiftName);
            }
        }

        // Emit the body based on method characteristics
        if (throws)
        {
            // When the silgen target handles the result buffer, the call is effectively void
            // from this wrapper's perspective — the silgen function writes to _resultBuf directly.
            EmitThrowingMethodBody(swiftWriter, callExpr, returnTypeSpec, returnMapping,
                silgenHandlesResult ? false : needsResultPtr,
                silgenHandlesResult ? true : isVoidReturn,
                silgenHandlesResult ? false : isString,
                env.TypeDatabase);
        }
        else if (silgenHandlesResult)
        {
            // The @_silgen_name function writes to _resultBuf directly — just call it.
            swiftWriter.WriteLine(callExpr);
        }
        else if (isVoidReturn)
        {
            swiftWriter.WriteLine(callExpr);
        }
        else if (isString)
        {
            EmitStringReturnBody(swiftWriter, callExpr);
        }
        else if (needsResultPtr && returnTypeSpec is ClosureTypeSpec)
        {
            // Closure returns: strip @escaping/@Sendable (parameter attributes, not valid
            // in metatype position) and wrap in parens for correct .self binding.
            var closureType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(returnTypeSpec)
                .Replace("@escaping ", "").Replace("@Sendable ", "");
            swiftWriter.WriteLine($"let result = {callExpr}");
            swiftWriter.WriteLine($"resultPtr.initializeMemory(as: ({closureType}).self, repeating: result, count: 1)");
        }
        else if (needsResultPtr)
        {
            // Non-frozen struct, complex enum, Optional<value-type>: write to result buffer
            var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(returnTypeSpec);
            // Protocol existentials (any Protocol1 & Protocol2) need parentheses before .self
            // to prevent .self from binding to only the last protocol in the composition.
            var metatype = swiftType.StartsWith("any ") ? $"({swiftType}).self" : $"{swiftType}.self";
            swiftWriter.WriteLine($"let result = {callExpr}");
            swiftWriter.WriteLine($"resultPtr.initializeMemory(as: {metatype}, repeating: result, count: 1)");
        }
        else
        {
            EmitDirectReturn(swiftWriter, callExpr, returnTypeSpec, env.TypeDatabase, returnMapping);
        }

        // For mutating struct methods, write back the mutated value
        if (!isStatic && isMutating && !isClass)
        {
            // The struct was loaded from self_ pointer; mutations happened on obj.
            // Write back the mutated value.
            // (Handled inline by using through-pointer access for mutating methods)
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Emits a @_cdecl method wrapper for generic types where T appears in the method
    /// parameters or return type. Uses protocol-based type erasure with a static dispatch method:
    /// 1. Defines a protocol with a static method (UnsafeRawPointer for T positions)
    /// 2. Extends the generic type to unconditionally conform
    /// 3. The @_cdecl wrapper receives metadata, casts to protocol type, calls static method
    /// </summary>
    private static void EmitGenericStaticDispatchMethod(
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ModuleEmissionContext? ctx,
        string symbolName,
        TypeDecl parentTypeDecl,
        string moduleQualifiedSwiftName,
        bool isClass,
        bool isMutating,
        bool throws,
        TypeSpec returnTypeSpec,
        bool isVoidReturn,
        bool isString,
        bool needsResultPtr,
        PropertyWrapperEmitter.CdeclReturnMapping returnMapping)
    {
        var methodDecl = env.MethodDecl;
        var keptArgs = methodDecl.CSSignature.Skip(1).ToList();
        var genericParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet();
        var abiToSugaredName = WrapperValidation.GetAbiToSugaredNameMap(parentTypeDecl);

        var methodHash = EmitterUtility.DeterministicHash8(symbolName);
        var protocolName = $"_SBW_GSM_{methodHash}";
        var dispatchMethodName = $"_sbw_dispatch_{methodHash}";
        var swiftMethodName = NameProvider.ParserNameToSwift(methodDecl);

        // For string returns in generic static dispatch, we need Utf8Slice infrastructure
        if (isString)
        {
            var moduleName = parentTypeDecl.SwiftTypeName.Module;
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter, ctx ?? ModuleEmissionContext.Default);
            Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleName, ctx ?? ModuleEmissionContext.Default);
        }

        // Determine if return type references T
        bool returnReferencesT = WrapperValidation.TypeSpecReferencesGenericParam(returnTypeSpec, genericParamNames);

        // Build protocol method and @_cdecl signatures
        var protocolParams = new List<string>();
        var extensionBodyLines = new List<string>();
        var methodCallArgs = new List<string>();
        var cdeclParams = new List<string>();
        var cdeclCallArgs = new List<string>();

        // Result ptr for indirect results (including T returns)
        bool cdeclNeedsResultPtr = needsResultPtr || (returnReferencesT && !isVoidReturn);
        if (cdeclNeedsResultPtr)
        {
            cdeclParams.Add("_ resultPtr: UnsafeMutableRawPointer");
            protocolParams.Add("resultPtr: UnsafeMutableRawPointer");
            cdeclCallArgs.Add("resultPtr: resultPtr");
        }

        if (throws)
        {
            cdeclParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>");
            protocolParams.Add("errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>");
            cdeclCallArgs.Add("errorOut: errorOut");
        }

        // Self parameter
        if (isClass)
            cdeclParams.Add("_ self_: UnsafeMutableRawPointer");
        else if (isMutating)
            cdeclParams.Add("_ self_: UnsafeMutableRawPointer");
        else
            cdeclParams.Add("_ self_: UnsafeRawPointer");

        protocolParams.Add(isMutating ? "selfPtr: UnsafeMutableRawPointer" : "selfPtr: UnsafeRawPointer");
        cdeclCallArgs.Add("selfPtr: self_");

        // Method arguments
        int argIndex = 0;
        for (int i = 0; i < keptArgs.Count; i++)
        {
            var arg = keptArgs[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;

            var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            if (label == "_") label = $"arg{argIndex}";
            if (NameProvider.IsSwiftKeyword(label)) label = $"{label}Param";
            label = SwiftBuilder.SanitizeIdentifier(label);

            var argLabel = arg.Name switch
            {
                var n when n.StartsWith("arg") => "_",
                "_" => "_",
                var n when n.StartsWith("_") => n.Substring(1),
                var n when string.IsNullOrEmpty(n) => "_",
                var n => n
            };

            var protocolArgLabel = argLabel == "_" ? "" : argLabel + ": ";
            var methodArgLabel = arg.Name switch
            {
                var n when n.StartsWith("arg") => "",
                "_" => "",
                var n when n.StartsWith("_") => $"{n.Substring(1)}: ",
                var n when string.IsNullOrEmpty(n) => "",
                var n => $"{n}: "
            };

            if (WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames))
            {
                protocolParams.Add($"{argLabel} {label}: UnsafeRawPointer");
                cdeclParams.Add($"_ {label}: UnsafeRawPointer");
                cdeclCallArgs.Add($"{protocolArgLabel}{label}");

                var swiftType = WrapperValidation.RenderSwiftTypeSpecWithSugaredNames(arg.SwiftTypeSpec, abiToSugaredName);
                extensionBodyLines.Add($"let {label}Val = {label}.assumingMemoryBound(to: {swiftType}.self).pointee");
                methodCallArgs.Add($"{methodArgLabel}{label}Val");
            }
            else
            {
                var (cdeclParam, reconstruction, _) = ConstructorWrapperEmitter.GetCdeclParamMapping(arg, label, env, false);
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
                protocolParams.Add($"{argLabel} {label}: {swiftType}");
                cdeclParams.Add(cdeclParam);

                if (reconstruction != null)
                {
                    cdeclCallArgs.Add($"{protocolArgLabel}{label}Val");
                }
                else
                {
                    cdeclCallArgs.Add($"{protocolArgLabel}{label}");
                }

                methodCallArgs.Add($"{methodArgLabel}{label}");
            }
            argIndex++;
        }

        // Metadata parameters for @_cdecl
        for (int mi = 0; mi < parentTypeDecl.GenericParameters.Count; mi++)
        {
            cdeclParams.Add($"_ _metadata{mi}: UnsafeRawPointer");
        }

        // Build protocol method declaration
        string protocolReturnType;
        if (isVoidReturn || cdeclNeedsResultPtr)
            protocolReturnType = "";
        else
            protocolReturnType = $" -> {returnMapping.cdeclReturnType}";

        var throwsClause = throws ? " throws" : "";
        var protocolMethodSig = $"static func {dispatchMethodName}({string.Join(", ", protocolParams)}){throwsClause}{protocolReturnType}";

        // Build extension body
        var methodCallArgString = string.Join(", ", methodCallArgs);

        // Reconstruct self inside the extension body
        if (isClass)
        {
            extensionBodyLines.Insert(0, "let obj = Unmanaged<AnyObject>.fromOpaque(selfPtr).takeUnretainedValue() as! Self");
        }
        else if (isMutating)
        {
            extensionBodyLines.Insert(0, $"var obj = selfPtr.assumingMemoryBound(to: Self.self).pointee");
        }
        else
        {
            extensionBodyLines.Insert(0, $"let obj = selfPtr.assumingMemoryBound(to: Self.self).pointee");
        }

        // Build the call and result handling
        string tryPrefix = throws ? "try " : "";
        if (isVoidReturn)
        {
            extensionBodyLines.Add($"{tryPrefix}obj.{swiftMethodName}({methodCallArgString})");
        }
        else if (isString)
        {
            // String returns: write SBW_Utf8Slice to resultPtr
            extensionBodyLines.Add($"let result: String = {tryPrefix}obj.{swiftMethodName}({methodCallArgString})");
            // For mutating methods, write back BEFORE any early return (empty string branch)
            if (isMutating && !isClass)
            {
                extensionBodyLines.Add("selfPtr.assumingMemoryBound(to: Self.self).pointee = obj");
            }
            extensionBodyLines.Add("let utf8 = Array(result.utf8)");
            extensionBodyLines.Add("if utf8.isEmpty {");
            extensionBodyLines.Add("    resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: &_sbw_emptyBuffer, len: 0), as: SBW_Utf8Slice.self)");
            extensionBodyLines.Add("    return");
            extensionBodyLines.Add("}");
            extensionBodyLines.Add("let ptr = UnsafeMutablePointer<UInt8>.allocate(capacity: utf8.count)");
            extensionBodyLines.Add("ptr.initialize(from: utf8, count: utf8.count)");
            extensionBodyLines.Add("resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: ptr, len: utf8.count), as: SBW_Utf8Slice.self)");
        }
        else if (returnReferencesT)
        {
            // T return: write to resultPtr using the concrete type from the extension
            // Use sugared names (T, Element) instead of ABI names (τ_0_0)
            var returnSwiftType = WrapperValidation.RenderSwiftTypeSpecWithSugaredNames(returnTypeSpec, abiToSugaredName);
            extensionBodyLines.Add($"let result = {tryPrefix}obj.{swiftMethodName}({methodCallArgString})");
            extensionBodyLines.Add($"resultPtr.initializeMemory(as: {returnSwiftType}.self, repeating: result, count: 1)");
        }
        else if (cdeclNeedsResultPtr)
        {
            var returnSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(returnTypeSpec);
            var metatype = returnSwiftType.StartsWith("any ") ? $"({returnSwiftType}).self" : $"{returnSwiftType}.self";
            extensionBodyLines.Add($"let result = {tryPrefix}obj.{swiftMethodName}({methodCallArgString})");
            extensionBodyLines.Add($"resultPtr.initializeMemory(as: {metatype}, repeating: result, count: 1)");
        }
        else
        {
            // Direct return — must apply the same conversions as EmitDirectGetterReturn:
            // Bool → Int8 (ternary), SimpleEnum → rawValue/tag, ClassPointer → Unmanaged
            var callExpr = $"{tryPrefix}obj.{swiftMethodName}({methodCallArgString})";
            switch (returnMapping.Kind)
            {
                case PropertyWrapperEmitter.CdeclReturnKind.Bool:
                    extensionBodyLines.Add($"let result = {callExpr}");
                    extensionBodyLines.Add("return result ? 1 : 0");
                    break;
                case PropertyWrapperEmitter.CdeclReturnKind.SimpleEnum:
                    // Tag-only enums have no rawValue — extract tag bits via pointer.
                    // Matches EmitDirectGetterReturn in PropertyWrapperEmitter.
                    if (env.TypeDatabase.TryGetTypeRecord(returnTypeSpec, out var enumRecord) &&
                        !string.IsNullOrEmpty(enumRecord.RawValueTypeName))
                    {
                        extensionBodyLines.Add($"let result = {callExpr}");
                        extensionBodyLines.Add($"return {returnMapping.cdeclReturnType}(result.rawValue)");
                    }
                    else
                    {
                        extensionBodyLines.Add($"var result = {callExpr}");
                        extensionBodyLines.Add($"return withUnsafePointer(to: &result) {{ UnsafeRawPointer($0).load(as: {returnMapping.cdeclReturnType}.self) }}");
                    }
                    break;
                case PropertyWrapperEmitter.CdeclReturnKind.ClassPointer:
                    extensionBodyLines.Add($"let result = {callExpr}");
                    extensionBodyLines.Add("return Unmanaged.passRetained(result as AnyObject).toOpaque()");
                    break;
                case PropertyWrapperEmitter.CdeclReturnKind.OptionalClassPointer:
                    extensionBodyLines.Add($"let result = {callExpr}");
                    extensionBodyLines.Add("return result.map { Unmanaged.passRetained($0 as AnyObject).toOpaque() }");
                    break;
                default:
                    extensionBodyLines.Add($"return {callExpr}");
                    break;
            }
        }

        // For mutating struct methods, write back BEFORE any return statement.
        // Skip if already handled in the string branch (which inserts write-back before early return).
        if (isMutating && !isClass && !isString)
        {
            // Insert the write-back before the last line (which contains `return`)
            // to ensure mutating state changes are persisted
            var lastIdx = extensionBodyLines.Count - 1;
            if (lastIdx >= 0 && extensionBodyLines[lastIdx].TrimStart().StartsWith("return"))
            {
                extensionBodyLines.Insert(lastIdx, "selfPtr.assumingMemoryBound(to: Self.self).pointee = obj");
            }
            else
            {
                extensionBodyLines.Add("selfPtr.assumingMemoryBound(to: Self.self).pointee = obj");
            }
        }

        // Emit protocol
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            private protocol {{protocolName}} {
                {{protocolMethodSig}}
            }
            """);

        // Emit extension
        var extensionBody = string.Join("\n        ", extensionBodyLines);
        swiftWriter.WriteLines($$"""
            extension {{moduleQualifiedSwiftName}}: {{protocolName}} {
                static func {{dispatchMethodName}}({{string.Join(", ", protocolParams)}}){{throwsClause}}{{protocolReturnType}} {
                    {{extensionBody}}
                }
            }
            """);

        // Emit metadata accessor helper at module scope (before @_cdecl)
        var methodHelperName = ConstructorWrapperEmitter.EmitMetadataAccessorHelperIfNeeded(swiftWriter, parentTypeDecl, ctx!);

        // Emit @_cdecl wrapper
        var cdeclParamString = string.Join(", ", cdeclParams);
        var swiftFuncName = $"_sbw_method_{EmitterUtility.DeterministicHash8(symbolName)}";

        string cdeclReturnClause;
        if (isVoidReturn || cdeclNeedsResultPtr)
            cdeclReturnClause = "";
        else
            cdeclReturnClause = $" -> {returnMapping.cdeclReturnType}";

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Method @_cdecl wrapper for {{moduleQualifiedSwiftName}}.{{methodDecl.Name}} (generic static dispatch).
            // Routes through protocol-based type erasure to avoid CallConvSwift crash.
            """);

        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            parentTypeDecl, methodDecl.IsMainActorIsolated, methodDecl.IsNonisolated);
        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, symbolName, needsMainActor);

        swiftWriter.WriteLine($"public func {swiftFuncName}({cdeclParamString}){cdeclReturnClause} {{");
        swiftWriter.Indent++;

        // Emit parameter reconstruction for concrete params
        for (int i = 0; i < keptArgs.Count; i++)
        {
            var arg = keptArgs[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames)) continue;

            var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            if (label == "_") label = $"arg{i}";
            if (NameProvider.IsSwiftKeyword(label)) label = $"{label}Param";
            label = SwiftBuilder.SanitizeIdentifier(label);

            var (_, reconstruction, _) = ConstructorWrapperEmitter.GetCdeclParamMapping(arg, label, env, false);
            if (reconstruction != null)
                swiftWriter.WriteLine(reconstruction);
        }

        // Metatype dispatch — convert T.self → ParentType<T>.self via metadata accessor
        var methodMetaArgs = string.Join(", ", Enumerable.Range(0, parentTypeDecl.GenericParameters.Count).Select(i => $"_metadata{i}"));
        swiftWriter.WriteLine($"let parentMeta = {methodHelperName}({methodMetaArgs})");
        swiftWriter.WriteLine($"let metatype = unsafeBitCast(parentMeta, to: Any.Type.self) as! any {protocolName}.Type");

        // Call the protocol static method
        var cdeclCallArgString = string.Join(", ", cdeclCallArgs);

        if (throws)
        {
            swiftWriter.WriteLine("do {");
            swiftWriter.Indent++;
            if (isVoidReturn || cdeclNeedsResultPtr)
                swiftWriter.WriteLine($"try metatype.{dispatchMethodName}({cdeclCallArgString})");
            else
                swiftWriter.WriteLine($"return try metatype.{dispatchMethodName}({cdeclCallArgString})");
            swiftWriter.Indent--;
            swiftWriter.WriteLines("""
                } catch {
                    errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
                """);
            if (!isVoidReturn && !cdeclNeedsResultPtr)
            {
                swiftWriter.WriteLine("    return 0"); // sentinel
            }
            swiftWriter.WriteLine("}");
        }
        else if (isVoidReturn || cdeclNeedsResultPtr)
        {
            swiftWriter.WriteLine($"metatype.{dispatchMethodName}({cdeclCallArgString})");
        }
        else
        {
            swiftWriter.WriteLine($"return metatype.{dispatchMethodName}({cdeclCallArgString})");
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Emits self reconstruction for instance methods.
    /// </summary>
    private static void EmitSelfReconstruction(SwiftWriter swiftWriter, bool isClass, bool isMutating, string moduleQualifiedSwiftName)
    {
        if (isClass)
        {
            swiftWriter.WriteLine($"let obj = Unmanaged<{moduleQualifiedSwiftName}>.fromOpaque(self_).takeUnretainedValue()");
        }
        else if (isMutating)
        {
            // Mutating method: use through-pointer access (self_.assumingMemoryBound(...).pointee)
            // so mutations write back. No separate obj variable needed — callExpr uses pointer directly.
        }
        else
        {
            swiftWriter.WriteLine($"let obj = self_.assumingMemoryBound(to: {moduleQualifiedSwiftName}.self).pointee");
        }
    }

    /// <summary>
    /// Emits the body of a throwing method wrapper.
    /// </summary>
    private static void EmitThrowingMethodBody(
        SwiftWriter swiftWriter,
        string callExpr,
        TypeSpec returnTypeSpec,
        PropertyWrapperEmitter.CdeclReturnMapping returnMapping,
        bool needsResultPtr,
        bool isVoidReturn,
        bool isString,
        ITypeDatabase typeDatabase)
    {
        swiftWriter.WriteLine("do {");
        swiftWriter.Indent++;

        if (isVoidReturn)
        {
            swiftWriter.WriteLine($"try {callExpr}");
        }
        else if (isString)
        {
            EmitStringReturnBody(swiftWriter, $"try {callExpr}");
        }
        else if (needsResultPtr && returnTypeSpec is ClosureTypeSpec)
        {
            var closureType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(returnTypeSpec)
                .Replace("@escaping ", "").Replace("@Sendable ", "");
            swiftWriter.WriteLine($"let result = try {callExpr}");
            swiftWriter.WriteLine($"resultPtr.initializeMemory(as: ({closureType}).self, repeating: result, count: 1)");
        }
        else if (needsResultPtr)
        {
            var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(returnTypeSpec);
            var metatype = swiftType.StartsWith("any ") ? $"({swiftType}).self" : $"{swiftType}.self";
            swiftWriter.WriteLine($"let result = try {callExpr}");
            swiftWriter.WriteLine($"resultPtr.initializeMemory(as: {metatype}, repeating: result, count: 1)");
        }
        else
        {
            EmitDirectReturn(swiftWriter, $"try {callExpr}", returnTypeSpec, typeDatabase, returnMapping);
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLines("""
            } catch {
                errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
            """);

        // For non-void direct returns (not via resultPtr), we need a dummy return value.
        // For resultPtr and void, Swift is happy with just the error assignment.
        if (!isVoidReturn && !needsResultPtr)
        {
            // Return a sentinel value matching the return type
            switch (returnMapping.Kind)
            {
                case PropertyWrapperEmitter.CdeclReturnKind.Bool:
                    swiftWriter.WriteLine("    return 0");
                    break;
                case PropertyWrapperEmitter.CdeclReturnKind.SimpleEnum:
                    swiftWriter.WriteLine("    return 0");
                    break;
                case PropertyWrapperEmitter.CdeclReturnKind.ClassPointer:
                    swiftWriter.WriteLine("    return UnsafeMutableRawPointer(bitPattern: 1)!");
                    break;
                case PropertyWrapperEmitter.CdeclReturnKind.OptionalClassPointer:
                    swiftWriter.WriteLine("    return nil");
                    break;
                case PropertyWrapperEmitter.CdeclReturnKind.Direct:
                    swiftWriter.WriteLine("    return 0");
                    break;
            }
        }

        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Emits the string return body using SBW_Utf8Slice pattern.
    /// Writes result to resultPtr because @_cdecl can't return Swift structs.
    /// </summary>
    private static void EmitStringReturnBody(SwiftWriter swiftWriter, string callExpr)
    {
        // Explicit `: String` annotation disambiguates overloaded methods with different return types
        // (e.g., URLEncodedFormEncoder.encode(_:) returning String vs Data).
        swiftWriter.WriteLines($$"""
            let result: String = {{callExpr}}
            let utf8 = Array(result.utf8)
            if utf8.isEmpty {
                resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: &_sbw_emptyBuffer, len: 0), as: SBW_Utf8Slice.self)
                return
            }
            let ptr = UnsafeMutablePointer<UInt8>.allocate(capacity: utf8.count)
            ptr.initialize(from: utf8, count: utf8.count)
            resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: ptr, len: utf8.count), as: SBW_Utf8Slice.self)
            """);
    }

    /// <summary>
    /// Emits a direct return statement for non-string, non-indirect-result returns.
    /// </summary>
    private static void EmitDirectReturn(SwiftWriter swiftWriter, string callExpr,
        TypeSpec typeSpec, ITypeDatabase typeDatabase, PropertyWrapperEmitter.CdeclReturnMapping mapping)
    {
        switch (mapping.Kind)
        {
            case PropertyWrapperEmitter.CdeclReturnKind.Bool:
                swiftWriter.WriteLine($"return ({callExpr}) ? 1 : 0");
                break;

            case PropertyWrapperEmitter.CdeclReturnKind.SimpleEnum:
                if (typeDatabase.TryGetTypeRecord(typeSpec, out var enumRecord) &&
                    !string.IsNullOrEmpty(enumRecord.RawValueTypeName))
                {
                    swiftWriter.WriteLine($"return {mapping.cdeclReturnType}(({callExpr}).rawValue)");
                }
                else
                {
                    swiftWriter.WriteLine($"var result = {callExpr}");
                    swiftWriter.WriteLine($"return withUnsafePointer(to: &result) {{ UnsafeRawPointer($0).load(as: {mapping.cdeclReturnType}.self) }}");
                }
                break;

            case PropertyWrapperEmitter.CdeclReturnKind.ClassPointer:
                // Use `as AnyObject` for safety — handles both true classes and ObjC-bridged structs.
                // Unmanaged.passRetained requires T: AnyObject; ObjC-bridged structs (e.g., IndexPath)
                // need the bridge cast. For true classes, `as AnyObject` is a no-op upcast.
                swiftWriter.WriteLine($"return Unmanaged.passRetained({callExpr} as AnyObject).toOpaque()");
                break;

            case PropertyWrapperEmitter.CdeclReturnKind.OptionalClassPointer:
                // Use `as AnyObject` in the .map closure — ObjC-bridged structs (e.g., NSZone,
                // IndexPath) are Swift structs and Unmanaged<T> requires T: AnyObject.
                swiftWriter.WriteLine($"return ({callExpr}).map {{ Unmanaged.passRetained($0 as AnyObject).toOpaque() }}");
                break;

            case PropertyWrapperEmitter.CdeclReturnKind.Direct:
            default:
                swiftWriter.WriteLine($"return {callExpr}");
                break;
        }
    }

    /// <summary>
    /// Checks whether any closure parameter is an async closure (IsAsync).
    /// GetSwiftClosureAdapterCode() only emits synchronous adapter code, so async closures
    /// (even non-throwing ones) are not supported in @_cdecl wrappers.
    /// </summary>
    private static bool HasAnyAsyncClosure(MethodEnvironment env)
        => WrapperValidation.HasAnyAsyncClosure(env);

    /// <summary>
    /// Checks whether any method parameter is a protocol existential type.
    /// </summary>
    private static bool HasProtocolExistentialParameter(MethodEnvironment env)
    {
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (env.ExistentialHandler.IsExistential(arg.SwiftTypeSpec))
                return true;

            if (arg.SwiftTypeSpec is NamedTypeSpec namedSpec &&
                env.TypeDatabase.TryGetTypeRecord(namedSpec, out var typeRecord) &&
                (typeRecord.Kind == TypeRecordKind.Protocol || typeRecord.Kind == TypeRecordKind.Existential))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Shared function-level eligibility check for @_cdecl wrappers.
    /// Contains guards that apply to ALL wrapper paths (method, closure, optional-pointer, async, arrayslice).
    /// Per-param checks are NOT included — each wrapper path has its own param-level gates
    /// because different wrappers transform different param types before checking.
    /// </summary>
    internal static bool HasCdeclCompatibleFunctionShape(MethodEnvironment env)
        => WrapperValidation.HasCdeclCompatibleFunctionShape(env);

    /// <summary>
    /// Per-param check: is this argument a nested frozen struct?
    /// Extracted from HasNestedFrozenStructParameter for reuse by wrapper-owned paths.
    /// </summary>
    internal static bool IsNestedFrozenStructParam(ArgumentDecl arg, ITypeDatabase typeDatabase)
        => WrapperValidation.IsNestedFrozenStructParam(arg, typeDatabase);

    /// <summary>
    /// Per-param check: is this argument a non-primitive frozen struct?
    /// Extracted from HasNonPrimitiveFrozenStructParameter for reuse by wrapper-owned paths.
    /// </summary>
    internal static bool IsNonPrimitiveFrozenStructParam(ArgumentDecl arg, ITypeDatabase typeDatabase)
        => WrapperValidation.IsNonPrimitiveFrozenStructParam(arg, typeDatabase);

    /// <summary>
    /// Checks if a parent decl is a non-copyable struct.
    /// </summary>
    private static bool IsNonCopyableStructParent(BaseDecl? parentDecl)
        => WrapperValidation.IsNonCopyableStructParent(parentDecl);

    /// <summary>
    /// Checks whether any parameter is a nested frozen struct type.
    /// </summary>
    private static bool HasNestedFrozenStructParameter(MethodEnvironment env)
        => env.MethodDecl.CSSignature.Skip(1).Any(arg => WrapperValidation.IsNestedFrozenStructParam(arg, env.TypeDatabase));

    /// <summary>
    /// Checks whether any parameter is a non-primitive frozen struct type.
    /// </summary>
    private static bool HasNonPrimitiveFrozenStructParameter(MethodEnvironment env)
        => env.MethodDecl.CSSignature.Skip(1).Any(arg => WrapperValidation.IsNonPrimitiveFrozenStructParam(arg, env.TypeDatabase));

    /// <summary>
    /// Checks whether any parameter or the return type is a generic container type
    /// that can't be handled by @_cdecl wrappers.
    /// Allows: Optional&lt;reference&gt; (nullable pointer ABI), Optional&lt;value-type&gt; (IndirectResult),
    /// Array, Dictionary, Set (UnsafeRawPointer transport).
    /// Blocks: Result&lt;T,E&gt;, Optional&lt;protocol existential&gt; (needs proxy conversion).
    /// </summary>
    private static bool HasUnsupportedGenericContainerParamsOrReturn(MethodEnvironment env)
    {
        var returnSpec = env.MethodDecl.CSSignature.First().SwiftTypeSpec;
        if (IsUnsupportedGenericContainer(returnSpec, env.TypeDatabase))
            return true;

        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (IsUnsupportedGenericContainer(arg.SwiftTypeSpec, env.TypeDatabase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true if a type is a generic container that can't be handled by @_cdecl wrappers.
    /// Allows: Optional&lt;value-type&gt; (IndirectResult), Optional&lt;reference&gt; (nullable pointer),
    /// Array, Dictionary, Set (UnsafeRawPointer transport).
    /// Blocks: Result&lt;T,E&gt;, Optional&lt;protocol existential&gt; (needs proxy conversion).
    /// </summary>
    internal static bool IsUnsupportedGenericContainer(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        => WrapperValidation.IsUnsupportedGenericContainer(typeSpec, typeDatabase);

    /// <summary>
    /// Returns true for collection container types that can be transported through @_cdecl
    /// wrappers via UnsafeRawPointer + .load(as:) / resultPtr.initializeMemory(as:).
    /// </summary>
    internal static bool IsSupportedCollectionType(TypeSpec typeSpec)
        => WrapperValidation.IsSupportedCollectionType(typeSpec);

    /// <summary>
    /// Returns true for Swift.Optional&lt;T&gt; type specs (any generic parameter count > 0).
    /// </summary>
    internal static bool IsOptionalType(TypeSpec typeSpec)
        => WrapperValidation.IsOptionalType(typeSpec);

    /// <summary>
    /// Returns true for metatype types (Any.Type, T.Type, etc.) which are not
    /// C-representable in @_cdecl wrappers. The generator renders them as bare "Type"
    /// which doesn't exist in Swift, causing compilation errors.
    /// </summary>
    internal static bool IsMetatypeType(TypeSpec typeSpec)
        => WrapperValidation.IsMetatypeType(typeSpec);

    /// <summary>
    /// Returns true for Optional types that can be handled by @_cdecl wrappers:
    /// - Optional&lt;reference&gt;: nullable pointer ABI (UnsafeMutableRawPointer?)
    /// - Optional&lt;value-type&gt;: IndirectResult via resultPtr
    /// Returns false for Optional&lt;protocol existential&gt; which needs proxy conversion
    /// that the @_cdecl IndirectResult path doesn't handle.
    /// </summary>
    internal static bool IsOptionalSupportedForCdecl(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        => WrapperValidation.IsOptionalSupportedForCdecl(typeSpec, typeDatabase);

    /// <summary>
    /// Returns true for Optional&lt;T&gt; where T is a reference-like type (Class, ObjC-bridged, ObjC-rooted).
    /// These use nullable pointer ABI (UnsafeMutableRawPointer?) in @_cdecl wrappers.
    /// </summary>
    internal static bool IsOptionalWithReferenceInner(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        => WrapperValidation.IsOptionalWithReferenceInner(typeSpec, typeDatabase);

    // ═══════════════════════════════════════════════════════════════════════
    // Generic parent class support — protocol-based type erasure
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true when a method on a generic parent type can be wrapped via @_cdecl
    /// using protocol-based type erasure.
    ///
    /// Two paths:
    /// 1. Generic class with concrete params/return — existing protocol instance dispatch
    /// 2. Generic struct/class with T-typed params/return — protocol with static method
    /// </summary>
    internal static bool CanEmitGenericWrapper(MethodEnvironment env, TypeDecl parentTypeDecl)
    {
        // Path 1: Generic class with concrete (non-T-referencing) signature — existing approach
        if (parentTypeDecl is ClassDecl && env.MethodDecl.MethodType != MethodType.Static)
        {
            if (!HasGenericTypeParamInSignature(env, parentTypeDecl))
                return true; // Path 1: concrete signature, use existing instance dispatch
        }

        // Path 2: Generic struct or class with T-typed params/return — static protocol dispatch
        return CanEmitGenericStaticMethodWrapper(env, parentTypeDecl);
    }

    /// <summary>
    /// Backward-compatible alias for external callers.
    /// </summary>
    internal static bool CanEmitGenericClassWrapper(MethodEnvironment env, TypeDecl parentTypeDecl)
        => CanEmitGenericWrapper(env, parentTypeDecl);

    /// <summary>
    /// Returns true when a generic method can use the static protocol dispatch pattern.
    /// This pattern creates a protocol with a static method whose signature uses
    /// UnsafeRawPointer for T-typed parameters and UnsafeMutableRawPointer for T-typed returns.
    ///
    /// Requirements:
    /// - Must be an instance method (not static — static dispatch needs different approach)
    /// - T-typed params must be simple direct generic params (τ_0_0), not complex compositions
    /// - T-typed returns are handled via resultPtr (UnsafeMutableRawPointer)
    /// </summary>
    internal static bool CanEmitGenericStaticMethodWrapper(MethodEnvironment env, TypeDecl parentTypeDecl)
    {
        // Instance methods only for now — static methods lack self pointer for dispatch
        if (env.MethodDecl.MethodType == MethodType.Static)
            return false;

        var genericParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet();

        // For non-class parents (structs), only allow methods that reference T in their
        // signature. Methods with concrete-only signatures may come from constrained extensions
        // (e.g., `extension Wrapper where T: Equatable`), and unconditional protocol conformances
        // can't access conditionally-available members. Fall back to CallConvSwift for these.
        if (parentTypeDecl is not ClassDecl)
        {
            bool signatureReferencesT = env.MethodDecl.CSSignature
                .Any(arg => WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames));
            if (!signatureReferencesT)
                return false;
        }

        // Check params: T-typed must be simple direct generic params
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;

            if (WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames))
            {
                // Allow direct generic param (e.g., T itself)
                if (arg.SwiftTypeSpec is NamedTypeSpec named && genericParamNames.Contains(named.Name))
                    continue;
                // Block complex generic compositions for now
                return false;
            }
        }

        // Check return: T-typed returns are OK (routed through resultPtr)
        var returnSpec = env.MethodDecl.CSSignature.First().SwiftTypeSpec;
        if (WrapperValidation.TypeSpecReferencesGenericParam(returnSpec, genericParamNames))
        {
            // Allow direct generic param return
            if (returnSpec is NamedTypeSpec named && genericParamNames.Contains(named.Name))
            { /* OK */ }
            else
                return false;
        }

        return true;
    }

    /// <summary>
    /// Returns true when a method needs the generic static protocol dispatch approach
    /// (as opposed to the existing concrete-signature instance dispatch approach).
    /// </summary>
    internal static bool NeedsGenericStaticDispatch(MethodEnvironment env, TypeDecl parentTypeDecl)
    {
        if (!parentTypeDecl.IsGeneric) return false;

        // Path 1 check: generic class with concrete signature uses existing instance dispatch
        if (parentTypeDecl is ClassDecl && env.MethodDecl.MethodType != MethodType.Static)
        {
            if (!HasGenericTypeParamInSignature(env, parentTypeDecl))
                return false; // Existing instance dispatch works
        }

        // All generic struct methods need static dispatch; class methods with T in signature need it too
        return true;
    }

    /// <summary>
    /// Checks whether any parameter or the return type references the parent type's generic
    /// type parameters (e.g., τ_0_0, τ_0_1). Methods where T appears in the signature
    /// can't use protocol-based type erasure because the protocol would need to be generic.
    /// </summary>
    internal static bool HasGenericTypeParamInSignature(MethodEnvironment env, TypeDecl parentTypeDecl)
    {
        var genericParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet();

        foreach (var arg in env.MethodDecl.CSSignature)
        {
            if (TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Recursively checks whether a TypeSpec references any of the given generic type parameter names.
    /// </summary>
    internal static bool TypeSpecReferencesGenericParam(TypeSpec spec, HashSet<string> genericParamNames)
        => WrapperValidation.TypeSpecReferencesGenericParam(spec, genericParamNames);

    /// <summary>
    /// Builds the Swift protocol method declaration string for protocol-based type erasure.
    /// The protocol declaration must exactly match the original method's signature
    /// (labels, types, throws) for the conformance to be valid.
    /// </summary>
    internal static string BuildProtocolMethodDeclaration(MethodDecl methodDecl, MethodEnvironment env)
    {
        var baseName = NameProvider.ParserNameToSwift(methodDecl);
        var keptArgs = methodDecl.CSSignature.Skip(1).ToList();

        var protocolParams = new List<string>();
        for (int i = 0; i < keptArgs.Count; i++)
        {
            var arg = keptArgs[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;

            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);

            // Determine external label
            string externalLabel;
            if (string.IsNullOrEmpty(arg.Name) || arg.Name == "_" || arg.Name.StartsWith("arg"))
                externalLabel = "_";
            else if (arg.Name.StartsWith("_"))
                externalLabel = arg.Name.Substring(1);
            else
                externalLabel = arg.Name;

            // Determine internal name
            string internalName = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : externalLabel;
            if (internalName == "_") internalName = $"p{i}";

            // Format parameter declaration
            if (externalLabel == internalName)
                protocolParams.Add($"{externalLabel}: {swiftType}");
            else
                protocolParams.Add($"{externalLabel} {internalName}: {swiftType}");
        }

        var paramString = string.Join(", ", protocolParams);
        var throwsClause = methodDecl.Throws ? " throws" : "";

        // Return type
        var returnSpec = methodDecl.CSSignature.First().SwiftTypeSpec;
        string returnClause = returnSpec.IsEmptyTuple
            ? ""
            : $" -> {ExistentialBypassEmitter.RenderSwiftTypeSpec(returnSpec)}";

        return $"func {baseName}({paramString}){throwsClause}{returnClause}";
    }

    /// <summary>
    /// Emits the protocol declaration, conformance extension, and modified self reconstruction
    /// for a method on a generic class type.
    /// </summary>
    internal static void EmitGenericClassProtocolAndConformance(
        SwiftWriter swiftWriter, MethodDecl methodDecl, MethodEnvironment env,
        string symbolName, string moduleQualifiedSwiftName)
    {
        var protocolName = $"_SBW_P_{EmitterUtility.DeterministicHash8(symbolName)}";
        var methodSig = BuildProtocolMethodDeclaration(methodDecl, env);

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            private protocol {{protocolName}} {
                {{methodSig}}
            }
            extension {{moduleQualifiedSwiftName}}: {{protocolName}} {}
            """);
    }

    /// <summary>
    /// Returns true if the given parent type declaration is a generic class type.
    /// Used to determine whether protocol-based type erasure is needed in emission.
    /// </summary>
    internal static bool IsGenericClassParent(BaseDecl? parentDecl)
        => WrapperValidation.IsGenericClassParent(parentDecl);
}
