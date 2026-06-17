// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Generates Swift wrapper functions that accept Array parameters in place of ArraySlice,
/// converting at the call site with ArraySlice(param). This allows methods blocked by
/// ArraySlice (which has no TypeDatabase registration) to be emitted with Array-based
/// signatures that the existing marshalling pipeline handles.
/// </summary>
public static class ArraySliceNormalizationEmitter
{
    /// <summary>
    /// Returns true if the given NamedTypeSpec represents Swift.ArraySlice.
    /// </summary>
    public static bool IsArraySlice(NamedTypeSpec namedTypeSpec)
    {
        return namedTypeSpec.Name == "Swift.ArraySlice";
    }

    /// <summary>
    /// Returns true if the TypeSpec directly contains ArraySlice at the top level
    /// (NamedTypeSpec name or generic parameters). Returns false for ArraySlice
    /// nested inside closures, tuples, or optionals (scope boundary).
    /// </summary>
    public static bool ContainsArraySlice(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec namedTypeSpec)
        {
            if (IsArraySlice(namedTypeSpec))
                return true;

            // Check if this is Optional wrapping ArraySlice — scope boundary, return false
            if (namedTypeSpec.Name == "Swift.Optional")
                return false;

            // Check generic parameters (e.g., Array<ArraySlice<T>> — though unlikely)
            foreach (var genericParam in namedTypeSpec.GenericParameters)
            {
                if (ContainsArraySlice(genericParam))
                    return true;
            }
        }

        // Closures, tuples — scope boundary, return false
        // (ClosureTypeSpec, TupleTypeSpec, ProtocolListTypeSpec all return false)
        return false;
    }

    /// <summary>
    /// Returns true if any parameter (not return type) in the method signature
    /// contains ArraySlice.
    /// </summary>
    public static bool HasArraySliceInSignature(MethodDecl methodDecl)
    {
        // CSSignature[0] is return type; parameters start at index 1
        for (int i = 1; i < methodDecl.CSSignature.Count; i++)
        {
            if (ContainsArraySlice(methodDecl.CSSignature[i].SwiftTypeSpec))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if the method has an unsupported shape for ArraySlice normalization.
    /// Returns true (with reason) if normalization should be skipped.
    /// </summary>
    private static bool IsUnsupportedShape(MethodDecl methodDecl, ILogger logger, out string reason)
    {
        if (methodDecl.IsAccessor)
        {
            reason = "property accessor";
            return true;
        }

        if (methodDecl.IsConstructor)
        {
            reason = "constructor";
            return true;
        }

        if (methodDecl.IsMutating && methodDecl.ParentDecl is StructDecl)
        {
            reason = "mutating method on value type";
            return true;
        }

        if (methodDecl.IsGeneric)
        {
            reason = "generic method";
            return true;
        }

        // Check for inout ArraySlice params
        for (int i = 1; i < methodDecl.CSSignature.Count; i++)
        {
            var arg = methodDecl.CSSignature[i];
            if (arg.IsInOut && ContainsArraySlice(arg.SwiftTypeSpec))
            {
                reason = "inout ArraySlice parameter";
                return true;
            }
        }

        // Check for ArraySlice inside closures or tuples in params
        for (int i = 1; i < methodDecl.CSSignature.Count; i++)
        {
            var arg = methodDecl.CSSignature[i];
            if (ContainsArraySliceInUnsupportedContext(arg.SwiftTypeSpec))
            {
                reason = "ArraySlice inside closure/tuple/optional";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Returns true if ArraySlice appears inside a closure, tuple, or optional — contexts
    /// where normalization is not supported.
    /// </summary>
    private static bool ContainsArraySliceInUnsupportedContext(TypeSpec typeSpec)
    {
        if (typeSpec is ClosureTypeSpec closureTypeSpec)
        {
            return ContainsArraySliceDeep(closureTypeSpec.Arguments) ||
                   ContainsArraySliceDeep(closureTypeSpec.ReturnType);
        }

        if (typeSpec is TupleTypeSpec tupleTypeSpec)
        {
            foreach (var element in tupleTypeSpec.Elements)
            {
                if (ContainsArraySliceDeep(element))
                    return true;
            }
            return false;
        }

        if (typeSpec is NamedTypeSpec namedTypeSpec)
        {
            // Optional wrapping ArraySlice
            if (namedTypeSpec.Name == "Swift.Optional")
            {
                foreach (var genericParam in namedTypeSpec.GenericParameters)
                {
                    if (ContainsArraySliceDeep(genericParam))
                        return true;
                }
                return false;
            }

            // Recurse into other NamedTypeSpec generic params
            foreach (var genericParam in namedTypeSpec.GenericParameters)
            {
                if (ContainsArraySliceInUnsupportedContext(genericParam))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Deep search for ArraySlice anywhere in a TypeSpec tree (for unsupported context detection).
    /// </summary>
    private static bool ContainsArraySliceDeep(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec namedTypeSpec)
        {
            if (IsArraySlice(namedTypeSpec))
                return true;
            foreach (var genericParam in namedTypeSpec.GenericParameters)
            {
                if (ContainsArraySliceDeep(genericParam))
                    return true;
            }
        }
        else if (typeSpec is TupleTypeSpec tupleTypeSpec)
        {
            foreach (var element in tupleTypeSpec.Elements)
            {
                if (ContainsArraySliceDeep(element))
                    return true;
            }
        }
        else if (typeSpec is ClosureTypeSpec closureTypeSpec)
        {
            return ContainsArraySliceDeep(closureTypeSpec.Arguments) ||
                   ContainsArraySliceDeep(closureTypeSpec.ReturnType);
        }
        return false;
    }

    /// <summary>
    /// Returns true if any parameter contains ArraySlice in an unsupported context
    /// (inside Optional, Closure, or Tuple) — these are not normalizable but should be logged.
    /// </summary>
    private static bool HasArraySliceInUnsupportedContext(MethodDecl methodDecl)
    {
        for (int i = 1; i < methodDecl.CSSignature.Count; i++)
        {
            if (ContainsArraySliceInUnsupportedContext(methodDecl.CSSignature[i].SwiftTypeSpec))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Attempts to emit a normalized method with ArraySlice parameters replaced by Array.
    /// Emits a Swift wrapper that converts Array→ArraySlice at the call site, and delegates
    /// C# emission to the normal WrapperEmitter + PInvokeEmitter pipeline.
    /// </summary>
    /// <returns>true if normalization was emitted; false to fall through to normal handling.</returns>
    public static bool TryEmitNormalizedMethod(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ILogger logger,
        ModuleEmissionContext? emissionContext = null)
    {
        var methodDecl = env.MethodDecl;

        if (!HasArraySliceInSignature(methodDecl))
        {
            // Log if ArraySlice is present but only in unsupported contexts
            if (HasArraySliceInUnsupportedContext(methodDecl))
            {
                logger.LogDebug("ArraySliceNormalization: skipping {Name} — ArraySlice only in unsupported context (Optional/Closure/Tuple)", methodDecl.Name);
            }
            return false;
        }

        if (IsUnsupportedShape(methodDecl, logger, out var reason))
        {
            logger.LogDebug("ArraySliceNormalization: skipping {Name} — {Reason}", methodDecl.Name, reason);
            return false;
        }

        // Bug #17: Skip @usableFromInline internal methods — the generated Swift wrapper
        // would call an inaccessible member from an extension in a different module.
        if (methodDecl.IsModuleInternal)
        {
            logger.LogDebug("ArraySliceNormalization: skipping {Name} — method is module-internal", methodDecl.Name);
            return false;
        }

        // Bug #15: Skip methods on types that aren't accessible from external code.
        // Internal types with @usableFromInline (e.g., BlockEncryptor, StreamEncryptor)
        // appear in the ABI but can't be extended from a separate module's wrapper file.
        if (methodDecl.ParentDecl is TypeDecl parentTypeDecl &&
            (parentTypeDecl.IsModuleInternal ||
             !env.TypeDatabase.TryGetTypeRecord(parentTypeDecl.SwiftTypeName, out _)))
        {
            logger.LogDebug("ArraySliceNormalization: skipping {Name} — parent type {Type} is module-internal or has no TypeRecord", methodDecl.Name, parentTypeDecl.SwiftTypeName.ModuleQualifiedName);
            return false;
        }

        // Build normalized MethodDecl with ArraySlice → Array replacement
        var normalizedMethodDecl = NormalizeMethodDecl(methodDecl);

        // Note: HasClosureCdeclWrapper is NOT set on cloned normalized decls.
        // ArraySlice wrappers use @_silgen_name to intercept the original Swift symbol,
        // which forces the function type to match the original ABI. Only standalone
        // closure wrappers (emitted at MethodHandler level) can use Cdecl params.

        // Create environment with normalized decl
        var normalizedEnv = new MethodEnvironment(
            normalizedMethodDecl,
            env.TypeDatabase,
            env.SiblingPropertyNames,
            env.PInvokeHelperContext,
            env.CompositionCollector);
        // Prefer the bridge's directly-supplied context over env.EmissionContext: this
        // bridge runs before MethodHandler assigns env.EmissionContext, so without this
        // the C# P/Invoke side bypasses the wrapper-symbol contract enforcement.
        normalizedEnv.EmissionContext = emissionContext ?? env.EmissionContext;

        // Check @_cdecl eligibility and set flags BEFORE SignatureHandler creation
        bool useCdecl = CanConvertToCdecl(normalizedMethodDecl, normalizedEnv);
        if (useCdecl)
        {
            var normParentType = normalizedMethodDecl.ParentDecl as TypeDecl;
            var parentModule = normalizedMethodDecl.ParentDecl as ModuleDecl;
            string moduleName = normParentType?.SwiftTypeName.Module ?? parentModule?.Name ?? "";
            string typeName = normParentType?.Name ?? "Free";
            var cdeclSymbol = MethodWrapperEmitter.GetMethodSymbolName(
                moduleName, typeName, normalizedMethodDecl.Name,
                normalizedMethodDecl.MangledName);
            normalizedMethodDecl.UsesCdeclMethodWrapper = true;
            normalizedMethodDecl.UsesFreeFunctionWrapper = true;
            normalizedMethodDecl.MangledName = cdeclSymbol;
        }
        else if (normalizedMethodDecl.MangledName.StartsWith("SBW_", StringComparison.Ordinal))
        {
            // Falling back to @_silgen_name (Swift CC P/Invoke) — rename SBW_ → SBSW_ so
            // the entry-point prefix matches the calling convention. PInvokeEmitHelper
            // enforces SBW_ ↔ Cdecl exclusively.
            normalizedMethodDecl.MangledName = "SBSW_" + normalizedMethodDecl.MangledName.Substring(4);
        }

        // Check if the normalized signature is fully marshallable
        var signatureHandler = new SignatureHandler(normalizedEnv);
        if (signatureHandler.GetWrapperSignature().ContainsPlaceholder)
        {
            logger.LogDebug("ArraySliceNormalization: skipping {Name} — normalized signature still contains placeholder", methodDecl.Name);
            return false;
        }

        // Emit Swift wrapper
        EmitSwiftWrapper(swiftWriter, methodDecl, normalizedMethodDecl, env, useCdecl, emissionContext);

        // Delegate C# emission to normal pipeline
        TypeDatabaseExtensions.AnyTypeFallbackInfo? fallbackInfo = null;
        foreach (var argument in normalizedMethodDecl.CSSignature)
        {
            if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(env.TypeDatabase, env.ClosureHandler, argument.SwiftTypeSpec, out var foundFallbackInfo))
            {
                fallbackInfo = foundFallbackInfo;
                break;
            }
        }

        var wrapperEmitter = new WrapperEmitter(normalizedEnv, signatureHandler, fallbackInfo, emissionContext);
        wrapperEmitter.EmitMethod(csWriter, swiftWriter);
        PInvokeEmitter.EmitPInvoke(csWriter, normalizedEnv, signatureHandler);

        return true;
    }

    /// <summary>
    /// Creates a new MethodDecl with ArraySlice parameters replaced by Array.
    /// Deep-copies CSSignature to avoid mutating the original.
    /// </summary>
    internal static MethodDecl NormalizeMethodDecl(MethodDecl original)
    {
        var wrapperSymbol = BuildWrapperSymbol(original);

        var normalized = new MethodDecl
        {
            Name = original.Name,
            MangledName = wrapperSymbol,
            MethodType = original.MethodType,
            IsConstructor = original.IsConstructor,
            IsFailable = original.IsFailable,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(original.GenericParameters),
            ParentDecl = original.ParentDecl,
            ModuleDecl = original.ModuleDecl,
            Throws = original.Throws,
            IsAsync = original.IsAsync,
            IsSynthesizedAccessor = original.IsSynthesizedAccessor,
            IsAccessor = original.IsAccessor,
            IsMutating = original.IsMutating,
            UsesWrapperLibrary = true,
        };

        // Deep-copy arguments, normalizing ArraySlice → Array where applicable
        foreach (var arg in original.CSSignature)
        {
            var normalizedTypeSpec = ContainsArraySlice(arg.SwiftTypeSpec)
                ? NormalizeTypeSpec(arg.SwiftTypeSpec)
                : arg.SwiftTypeSpec;

            normalized.CSSignature.Add(new ArgumentDecl
            {
                SwiftTypeSpec = normalizedTypeSpec,
                Name = arg.Name,
                PrivateName = arg.PrivateName,
                IsInOut = arg.IsInOut,
                Ownership = arg.Ownership, // preserve consuming/borrowing across normalization
                IsGeneric = arg.IsGeneric,
                HasDefaultArg = arg.HasDefaultArg,
                ParentDecl = normalized,
                ModuleDecl = arg.ModuleDecl
            });
        }

        return normalized;
    }

    /// <summary>
    /// Replaces Swift.ArraySlice with Swift.Array in a TypeSpec, preserving generic parameters.
    /// Only handles direct NamedTypeSpec replacement (scope guards ensure no closures/tuples/optionals).
    /// </summary>
    internal static TypeSpec NormalizeTypeSpec(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec namedTypeSpec)
        {
            if (IsArraySlice(namedTypeSpec))
            {
                var normalized = new NamedTypeSpec("Swift.Array");
                foreach (var genericParam in namedTypeSpec.GenericParameters)
                {
                    normalized.GenericParameters.Add(NormalizeTypeSpec(genericParam));
                }
                CopyTypeSpecMetadata(namedTypeSpec, normalized);
                return normalized;
            }

            // Recurse into generic parameters of non-ArraySlice named types
            bool anyChanged = false;
            var newGenericParams = new List<TypeSpec>();
            foreach (var genericParam in namedTypeSpec.GenericParameters)
            {
                var normalizedParam = NormalizeTypeSpec(genericParam);
                newGenericParams.Add(normalizedParam);
                if (!ReferenceEquals(normalizedParam, genericParam))
                    anyChanged = true;
            }

            if (anyChanged)
            {
                var result = new NamedTypeSpec(namedTypeSpec.Name);
                result.GenericParameters.AddRange(newGenericParams);
                CopyTypeSpecMetadata(namedTypeSpec, result);
                result.InnerType = namedTypeSpec.InnerType;
                return result;
            }
        }

        // Non-NamedTypeSpec or no changes needed — return as-is
        return typeSpec;
    }

    /// <summary>
    /// Copies TypeSpec metadata (Attributes, TypeLabel, IsInOut, IsAny, IsVariadic)
    /// from the original to a newly created TypeSpec. Follows the same pattern as
    /// TypeSpec.ReplaceName().
    /// </summary>
    private static void CopyTypeSpecMetadata(TypeSpec source, TypeSpec target)
    {
        target.Attributes.AddRange(source.Attributes);
        target.TypeLabel = source.TypeLabel;
        target.IsInOut = source.IsInOut;
        target.IsAny = source.IsAny;
        target.IsVariadic = source.IsVariadic;
    }

    /// <summary>
    /// Checks if a normalized ArraySlice method can be converted to @_cdecl.
    /// </summary>
    private static bool CanConvertToCdecl(MethodDecl normalizedMethodDecl, MethodEnvironment env)
    {
        if (!MethodWrapperEmitter.HasCdeclCompatibleFunctionShape(env))
            return false;
        foreach (var arg in normalizedMethodDecl.CSSignature.Skip(1))
        {
            // Metatype check runs BEFORE the large-optional skip so AnyClass.Type? doesn't
            // slip through as UnsafeRawPointer — same boundary as the primary wrapper gate.
            if (WrapperValidation.IsMetatypeTypeIncludingOptional(arg.SwiftTypeSpec))
                return false;
            if (OptionalPointerWrapperEmitter.ShouldWidenParam(arg, env.BoundGenericsHandler))
                continue;
            if (arg.IsGeneric) return false;
            if (CdeclParamMapper.IsProtocolExistentialType(arg.SwiftTypeSpec, env.TypeDatabase))
                return false;
            if (MethodWrapperEmitter.IsNestedFrozenStructParam(arg, env.TypeDatabase))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Emits a Swift wrapper that wraps the original ArraySlice method,
    /// accepting Array parameters and converting to ArraySlice at the call site.
    /// When useCdecl=true, emits @_cdecl with C-compatible non-ArraySlice params.
    /// </summary>
    private static void EmitSwiftWrapper(
        SwiftWriter swiftWriter,
        MethodDecl originalMethodDecl,
        MethodDecl normalizedMethodDecl,
        MethodEnvironment env,
        bool useCdecl = false,
        ModuleEmissionContext? emissionContext = null)
    {
        var wrapperSymbol = normalizedMethodDecl.MangledName;
        var parentTypeDecl = originalMethodDecl.ParentDecl as TypeDecl;
        bool isFreeFunction = parentTypeDecl == null;

        // Build parameter list for the wrapper function
        var swiftParams = new List<string>();
        var derefLines = new List<string>();
        var originalArgs = originalMethodDecl.CSSignature.Skip(1).ToList();
        var normalizedArgs = normalizedMethodDecl.CSSignature.Skip(1).ToList();
        // Sibling bindings so a reserved-name escape also dodges a sibling user param.
        // The call-value loop below recomputes the identical set, keeping param decls and forwarded
        // values in sync.
        var sliceSiblings = CdeclParamMapper.CollectSiblingBindingNames(normalizedArgs);
        for (int i = 0; i < normalizedArgs.Count; i++)
        {
            var arg = normalizedArgs[i];
            // Escape a user binding colliding with a synthetic this wrapper injects
            // (_resultBuf/self_/errorOut) OR a sibling user binding. The call-value loop below
            // escapes the matching normArg identically, keeping the param decl and the forwarded
            // value in sync. Self-excluded so a binding is never escaped against itself.
            var rawLabel = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            var label = NameProvider.EscapeReservedSwiftWrapperLabel(
                rawLabel, CdeclParamMapper.ExcludeSelf(sliceSiblings, rawLabel));

            // Large Optional params: accept UnsafeRawPointer, dereference in body
            if (OptionalPointerWrapperEmitter.ShouldWidenParam(arg, env.BoundGenericsHandler))
            {
                swiftParams.Add($"_ {label}: UnsafeRawPointer");
                derefLines.Add(OptionalPointerWrapperEmitter.GetDerefCode(arg, label, label, env.TypeDatabase));
            }
            else if (useCdecl)
            {
                // @_cdecl mode: convert to C-compatible type. label is already sibling-escaped;
                // passing siblings keeps Map's internal re-escape sibling-aware (idempotent here).
                var (cdeclParam, reconstruction, _) =
                    CdeclParamMapper.Map(arg, label, env, omitLabels: true, reservedSiblings: sliceSiblings);
                swiftParams.Add(cdeclParam);
                if (reconstruction != null) derefLines.Add(reconstruction);
            }
            else
            {
                // Render param as native Swift type — @_silgen_name forces original function type
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
                swiftParams.Add($"_ {label}: {swiftType}");
            }
        }

        // Check if the return type is a large Optional that needs an out-buffer
        bool hasLargeOptionalReturn = env.BoundGenericsHandler.IsLargeOptionalReturn(normalizedMethodDecl);
        if (hasLargeOptionalReturn)
        {
            swiftParams.Add("_ _resultBuf: UnsafeMutableRawPointer");
        }

        // For @_cdecl instance methods, add explicit self param (can't use extension)
        bool isInstance = !isFreeFunction && originalMethodDecl.MethodType != MethodType.Static;
        if (useCdecl && isInstance)
        {
            bool isClass = parentTypeDecl is ClassDecl;
            swiftParams.Add(isClass
                ? "_ self_: UnsafeMutableRawPointer"
                : "_ self_: UnsafeRawPointer");
        }

        // @_cdecl error handling: add errorOut param
        if (useCdecl && originalMethodDecl.Throws)
        {
            swiftParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>");
        }

        var swiftParamString = string.Join(", ", swiftParams);

        // Build call arguments with ArraySlice conversion
        var callArgs = new List<string>();
        for (int i = 0; i < originalArgs.Count; i++)
        {
            var origArg = originalArgs[i];
            var normArg = normalizedArgs[i];
            // Escape identically to the param-decl loop (same sibling set, self-excluded) so the
            // forwarded value references the same (possibly synthetic/sibling-escaped) binding the
            // wrapper declared.
            var rawNormName = !string.IsNullOrEmpty(normArg.PrivateName) ? normArg.PrivateName : normArg.Name;
            var privateName = NameProvider.EscapeReservedSwiftWrapperLabel(
                rawNormName, CdeclParamMapper.ExcludeSelf(sliceSiblings, rawNormName));

            // Use dereferenced value for large Optional params
            var valueRef = OptionalPointerWrapperEmitter.ShouldWidenParam(normArg, env.BoundGenericsHandler)
                ? $"{privateName}Val" : privateName;

            // For @_cdecl converted params, use the reconstructed value
            if (useCdecl && derefLines.Any(l => l.Contains($"let {privateName}Val ")))
                valueRef = $"{privateName}Val";

            // Provenance-aware call label (canonical builder) — preserves labels that genuinely
            // begin with '_' (e.g. _self) and backtick-escapes keywords.
            var argStr = CdeclParamMapper.BuildSwiftCallArgLabel(origArg);

            // If this param was normalized (ArraySlice → Array), wrap with ArraySlice()
            if (ContainsArraySlice(origArg.SwiftTypeSpec))
            {
                argStr += $"Swift.ArraySlice({valueRef})";
            }
            else
            {
                argStr += valueRef;
            }

            callArgs.Add(argStr);
        }
        var callArgString = string.Join(", ", callArgs);

        // Render return type from original method
        var returnTypeSpec = originalMethodDecl.CSSignature.First().SwiftTypeSpec;
        var returnType = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec);
        bool isVoid = returnTypeSpec is TupleTypeSpec tupleTypeSpec && tupleTypeSpec.IsEmptyTuple;
        bool throws = originalMethodDecl.Throws;

        // @_cdecl return mapping
        bool cdeclNeedsResultPtr = false;
        bool cdeclIsStringReturn = false;
        CdeclReturnMapping? cdeclReturnMapping = null;
        string returnClause;
        if (useCdecl && !isVoid && !hasLargeOptionalReturn)
        {
            var (returnMapping, needsResultPtr) = CdeclReturnMapping.Classify(returnTypeSpec, env.TypeDatabase);
            cdeclReturnMapping = returnMapping;
            cdeclIsStringReturn = WitnessDispatchEmitter.IsStringType(returnTypeSpec);
            if (cdeclIsStringReturn) needsResultPtr = true;
            cdeclNeedsResultPtr = needsResultPtr;
            returnClause = needsResultPtr ? "" : $" -> {returnMapping.CdeclReturnType}";
            if (needsResultPtr)
            {
                // ResultPtr must be FIRST per CdeclSignatureContract:
                // [ResultPtr?] [Arguments?] [Metadata] [Self?] [ErrorOut?]
                swiftParams.Insert(0, "_ resultPtr: UnsafeMutableRawPointer");
                if (cdeclIsStringReturn)
                    Utf8SliceEmitter.EmitIfNeeded(swiftWriter, emissionContext);
                // Rebuild swiftParamString with resultPtr
                swiftParamString = string.Join(", ", swiftParams);
            }
        }
        else
        {
            returnClause = (isVoid || hasLargeOptionalReturn) ? "" : $" -> {returnType}";
        }

        var originalMethodName = NameProvider.ParserNameToSwift(originalMethodDecl);
        var throwsClause = (useCdecl && throws) ? "" : (throws ? " throws" : "");
        var tryPrefix = throws ? "try " : "";
        var swiftFuncName = $"_sbw_{originalMethodName}_{DeterministicHash8(originalMethodDecl.MangledName)}";
        var annotation = useCdecl ? "@_cdecl" : "@_silgen_name";
        // Register the wrapper symbol so the wrapper-symbol contract sees it. The
        // contract gate now covers both shapes: SBW_… (cdecl) and SBSW_… (Swift CC),
        // so both branches must register or the C# P/Invoke emit will abort with
        // WrapperSymbolContractException.
        // ArraySliceNormalizationEmitter synthesizes a replacement
        // MethodDecl whose mangled name has been rewritten to use the normalized (non-
        // slice) ABI. That rewritten mangled name is unique and disjoint from the
        // original method's symbol, so it cannot alias any non-normalized wrapper.
        emissionContext?.TryAddMethodWrapperSymbol(wrapperSymbol);

        // @_silgen_name and @_cdecl wrappers are top-level Swift functions (or live in a
        // foreign extension that won't pick up the original declaration's @available); both
        // must re-apply availability or the wrapper compiles unconditionally and crashes
        // on devices below the wrapped API's introduced version.
        var availability = WrapperEmitterHelpers.MergeAvailability(
            originalMethodDecl.AvailabilityAnnotations, parentTypeDecl);

        swiftWriter.WriteLine();

        if (isFreeFunction || (useCdecl && !isFreeFunction))
        {
            // Free function or @_cdecl type method (emitted as free function with explicit self)
            var moduleName = UnescapeModuleName(originalMethodDecl.ModuleDecl?.Name ?? "");

            string callExprBase;
            if (useCdecl && !isFreeFunction)
            {
                // @_cdecl type method: reconstruct self and call
                var swiftModuleQualifiedName = parentTypeDecl!.SwiftTypeName.ModuleQualifiedName;
                bool isStatic = originalMethodDecl.MethodType == MethodType.Static;
                if (isStatic)
                {
                    callExprBase = $"{swiftModuleQualifiedName}.{originalMethodName}({callArgString})";
                }
                else
                {
                    bool isClass = parentTypeDecl is ClassDecl;
                    if (isClass)
                        derefLines.Insert(0, $"let obj = Unmanaged<{swiftModuleQualifiedName}>.fromOpaque(self_).takeUnretainedValue()");
                    else
                        derefLines.Insert(0, $"let obj = self_.load(as: {swiftModuleQualifiedName}.self)");
                    callExprBase = $"obj.{originalMethodName}({callArgString})";
                }
            }
            else
            {
                var callPrefix = !string.IsNullOrEmpty(moduleName) ? $"{moduleName}." : "";
                callExprBase = $"{callPrefix}{originalMethodName}({callArgString})";
            }

            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);
            swiftWriter.WriteLine($"{annotation}(\"{wrapperSymbol}\")");
            swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}){throwsClause}{returnClause} {{");
            swiftWriter.Indent++;

            foreach (var line in derefLines)
                swiftWriter.WriteLine(line);

            if (useCdecl && throws)
            {
                swiftWriter.WriteLine("do {");
                swiftWriter.Indent++;
                var callExpr = $"try {callExprBase}";
                if (hasLargeOptionalReturn)
                    foreach (var bufLine in OptionalPointerWrapperEmitter.GetReturnBufferCode(callExpr, returnType))
                        swiftWriter.WriteLine(bufLine);
                else
                    EmitCdeclReturnLine(swiftWriter, callExpr, isVoid, cdeclIsStringReturn, cdeclNeedsResultPtr,
                        returnTypeSpec, env.TypeDatabase, cdeclReturnMapping);
                swiftWriter.Indent--;
                swiftWriter.WriteLines("""
                    } catch {
                        errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
                    """);
                if (!isVoid && !cdeclNeedsResultPtr && !hasLargeOptionalReturn)
                    OptionalPointerWrapperEmitter.EmitCdeclSentinelReturn(swiftWriter, cdeclReturnMapping, indent: "    ");
                swiftWriter.WriteLine("}");
            }
            else if (useCdecl)
            {
                var callExpr = $"{tryPrefix}{callExprBase}";
                if (hasLargeOptionalReturn)
                    foreach (var bufLine in OptionalPointerWrapperEmitter.GetReturnBufferCode(callExpr, returnType))
                        swiftWriter.WriteLine(bufLine);
                else
                    EmitCdeclReturnLine(swiftWriter, callExpr, isVoid, cdeclIsStringReturn, cdeclNeedsResultPtr,
                        returnTypeSpec, env.TypeDatabase, cdeclReturnMapping);
            }
            else
            {
                var callExpr = $"{tryPrefix}{callExprBase}";
                if (hasLargeOptionalReturn)
                    foreach (var bufLine in OptionalPointerWrapperEmitter.GetReturnBufferCode(callExpr, returnType))
                        swiftWriter.WriteLine(bufLine);
                else
                    swiftWriter.WriteLine(isVoid ? callExpr : $"return {callExpr}");
            }

            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }
        else
        {
            // Type method — emit as extension (@_silgen_name only; @_cdecl handled above)
            var swiftModuleQualifiedName = parentTypeDecl!.SwiftTypeName.ModuleQualifiedName;
            bool isStatic = originalMethodDecl.MethodType == MethodType.Static;
            var staticKeyword = isStatic ? "static " : "";
            var selfPrefix = isStatic ? "Self" : "self";

            // Availability must precede the `extension {Type} {` line: Swift type-checks
            // the extension declaration itself against the deployment target. If the parent
            // type is iOS-18-only, an unannotated extension fails to compile on an iOS 16
            // floor before reaching the (also-annotated) wrapper function inside.
            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);
            swiftWriter.WriteLine($"extension {swiftModuleQualifiedName} {{");
            swiftWriter.Indent++;

            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);
            swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
            swiftWriter.WriteLine($"public {staticKeyword}func {swiftFuncName}({swiftParamString}){throwsClause}{returnClause} {{");
            swiftWriter.Indent++;

            foreach (var line in derefLines)
                swiftWriter.WriteLine(line);

            var callExpr = $"{tryPrefix}{selfPrefix}.{originalMethodName}({callArgString})";
            if (hasLargeOptionalReturn)
            {
                var bufferLines = OptionalPointerWrapperEmitter.GetReturnBufferCode(callExpr, returnType);
                foreach (var bufLine in bufferLines)
                    swiftWriter.WriteLine(bufLine);
            }
            else
            {
                swiftWriter.WriteLine(isVoid ? callExpr : $"return {callExpr}");
            }

            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }
    }

    /// <summary>
    /// Emits a single return line using @_cdecl return mapping (string, indirect, or direct).
    /// </summary>
    private static void EmitCdeclReturnLine(SwiftWriter swiftWriter, string callExpr,
        bool isVoid, bool cdeclIsStringReturn, bool cdeclNeedsResultPtr,
        TypeSpec returnTypeSpec, ITypeDatabase typeDatabase,
        CdeclReturnMapping? cdeclReturnMapping)
    {
        if (isVoid)
        {
            swiftWriter.WriteLine(callExpr);
        }
        else if (cdeclIsStringReturn)
        {
            OptionalPointerWrapperEmitter.EmitStringReturnBody(swiftWriter, callExpr, indent: "");
        }
        else if (cdeclNeedsResultPtr)
        {
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec);
            // Protocol existential returns need `(any P).self`, not `any P.self`.
            var metatype = swiftType.StartsWith("any ") ? $"({swiftType}).self" : $"{swiftType}.self";
            swiftWriter.WriteLine($"let result = {callExpr}");
            swiftWriter.WriteLine($"resultPtr.initializeMemory(as: {metatype}, repeating: result, count: 1)");
        }
        else
        {
            OptionalPointerWrapperEmitter.EmitCdeclDirectReturn(swiftWriter, callExpr, returnTypeSpec, typeDatabase, cdeclReturnMapping, indent: "");
        }
    }

    /// <summary>
    /// Builds the wrapper symbol name: SBW_{TypeName}_{MethodName}_{Hash8}
    /// </summary>
    private static string BuildWrapperSymbol(MethodDecl methodDecl)
    {
        var parentDecl = methodDecl.ParentDecl as TypeDecl;
        var typeName = parentDecl?.Name ?? "Global";
        var hash = DeterministicHash8(methodDecl.MangledName);
        return $"SBW_{typeName}_{methodDecl.Name}_{hash}";
    }

    internal static string DeterministicHash8(string input) => EmitterUtility.DeterministicHash8(input);

    /// <summary>
    /// Reverses the C# keyword escaping applied by SwiftABIParser.ExtractUniqueName().
    /// If a module name was escaped (e.g., "class" → "_class"), this strips the prefix
    /// and backtick-wraps it for valid Swift syntax (e.g., "`class`").
    /// </summary>
    internal static string UnescapeModuleName(string name)
    {
        if (name.Length > 1 && name[0] == '_')
        {
            var candidate = name.Substring(1);
            if (SyntaxFacts.GetKeywordKind(candidate) != SyntaxKind.None)
            {
                return $"`{candidate}`";
            }
        }
        return name;
    }
}
