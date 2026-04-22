// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Emits concrete C# overloads for methods with protocol-constrained generic parameters.
///
/// When a Swift method has a generic parameter constrained to a protocol with associated types
/// or Self requirements (e.g., <c>func hash&lt;D: DataProtocol&gt;(data: D)</c>), C# cannot express
/// that constraint. This emitter generates one concrete C# overload per known conformer:
///
///   SHA256.Hash(Data data) → calls hash with D = Foundation.Data
///   SHA256.Hash(byte[] data) → calls hash with D = [UInt8]
///
/// Each overload gets its own Swift @_cdecl wrapper that calls the generic method with the
/// concrete type, eliminating the need for generic dispatch or witness table passing.
///
/// For generic constructors, emits static factory methods since C# cannot have generic constructors.
/// </summary>
public static partial class ConcreteProtocolSpecializationEmitter
{
    // Safety cap on the cartesian product of conformer pairings per method.
    // Why: a method like `WeatherKit.WeatherService.weather<T1,…,T6>` where every
    // Ti is constrained to a widely-conformed marker protocol (e.g. Swift.Sendable)
    // picks up N^6 pairings from ABI-discovered conformers and never terminates —
    // none of the pairings are emission-eligible, but the predicate sweep walks
    // the whole product. Legitimate CSM products are tiny (hint-declared protocols
    // have ≤4 conformers, so 4^6 = 4096 is the realistic worst case). 10k gives
    // headroom without letting pathological combinatorics through.
    internal const int MaxCsmCartesianProductSize = 10_000;

    /// <summary>
    /// Returns the number of cartesian-product pairings that would be enumerated for
    /// <paramref name="specParams"/>, clamped to <see cref="long.MaxValue"/> on overflow.
    /// Used to short-circuit CSM-async emission for pathologically large products
    /// (e.g. methods with many generic params constrained to marker protocols whose
    /// ABI-discovered conformer count is large).
    /// </summary>
    internal static long ComputePairingCount(
        IReadOnlyList<ConcreteSpecializationEngine.SpecializableParam> specParams)
    {
        long total = 1;
        foreach (var p in specParams)
        {
            long n = p.Conformers.Count;
            if (n == 0) return 0;
            if (total > long.MaxValue / n) return long.MaxValue;
            total *= n;
        }
        return total;
    }

    /// <summary>
    /// Scans a type's methods for specializable protocol-constrained generics and emits
    /// concrete C# overloads for each known conformer.
    /// </summary>
    public static void EmitConcreteSpecializations(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        TypeDecl typeDecl,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        ConcreteSpecializationEngine engine,
        ILogger logger)
    {
        var specializableMethods = engine.FindSpecializableMethods(typeDecl);
        if (specializableMethods.Count == 0) return;

        var moduleName = typeDecl.SwiftTypeName.Module;
        var wrapperLibPath = typeDatabase.AsyncLibraryName ?? "libSwiftBindings";
        // Track emitted C# method signatures to prevent CS0111 duplicate member errors.
        // Sync path keeps its local dedup set; async path uses the shared
        // ModuleEmissionContext claim so it agrees with the Phase-4a predicate.
        var emittedSignatures = new HashSet<string>(StringComparer.Ordinal);

        // Pre-seed with signatures of existing non-specialized methods on the type.
        // A CSM overload collides with a hand-written method when Swift has both
        // `func f(_ x: SQL)` (non-generic) AND `func f<T: P>(_ x: T)` (generic) and
        // SQL is a conformer of P. Without pre-seeding, the CSM emitter produces a
        // second `F(SQL)` overload triggering CS0111.
        var specializableMangledNames = new HashSet<string>(
            specializableMethods.Select(s => s.Method.MangledName), StringComparer.Ordinal);
        foreach (var existing in typeDecl.Methods)
        {
            if (existing.IsAccessor || existing.IsConstructor) continue;
            if (existing.IsGeneric) continue;
            if (specializableMangledNames.Contains(existing.MangledName)) continue;
            var existingCsName = NameProvider.ToPascalCase(existing.Name);
            var existingSigKey = BuildCSharpSignatureKeyForNonGeneric(existingCsName, existing, typeDatabase);
            emittedSignatures.Add(existingSigKey);
        }

        foreach (var spec in specializableMethods)
        {
            var method = spec.Method;

            // Accessors stay gated: they use different emission paths (property accessors).
            // Constructors likewise — Phase A only covers plain instance/static async methods.
            // Mutating methods are now supported for struct instance methods via
            // UnsafeMutableRawPointer self_ + pointee write-back after the call.
            if (method.IsAccessor) continue;

            // Sync throws without async is also out of scope for CSM v1.
            if (!method.IsAsync && method.Throws) continue;

            // Parent-generic specs are handled by EmitConcreteSpecializationsForGenericParent,
            // which wraps emission in a per-parent-conformer static extension class so the
            // receiver can close over the generic (e.g. `this GenericContainer<SongItem> self`).
            if (spec.SpecializableParams.Any(p => p.IsParentGeneric)) continue;

            // Verify xcframework mode
            if (!WrapperValidation.IsXCFrameworkMode(typeDatabase)) continue;

            if (spec.SpecializableParams.Count == 1)
            {
                var param = spec.SpecializableParams[0];
                foreach (var conformer in param.Conformers)
                {
                    var pairing = new[] { (param, conformer) };
                    if (method.IsAsync)
                    {
                        TryEmitConcreteOverloadAsync(
                            csWriter, swiftWriter, method, typeDecl,
                            pairing,
                            moduleName, typeDatabase, emissionContext, logger);
                    }
                    else
                    {
                        TryEmitConcreteOverload(
                            csWriter, swiftWriter, method, typeDecl, pairing,
                            moduleName, wrapperLibPath, typeDatabase, emissionContext, emittedSignatures, logger);
                    }
                }
            }
            else
            {
                var pairingCount = ComputePairingCount(spec.SpecializableParams);
                if (pairingCount > MaxCsmCartesianProductSize)
                {
                    logger.LogDebug(
                        "CSM: Skipping {Method} — cartesian product of conformer pairings ({Count}) exceeds cap ({Cap}).",
                        method.Name, pairingCount, MaxCsmCartesianProductSize);
                    continue;
                }

                // Multi-param cartesian product: enumerate all combinations of conformers,
                // filter pairs whose cross-parameter same-type constraints (e.g., S.Element == T)
                // are not satisfied. Only emit the surviving substitution pairs.
                foreach (var pairing in CartesianPairings(spec.SpecializableParams))
                {
                    if (!ConformerPairingSatisfiesCoupling(pairing))
                    {
                        logger.LogDebug(
                            "CSM: Skipping {Method} multi-param pairing — cross-param same-type constraint not satisfied.",
                            method.Name);
                        continue;
                    }

                    if (method.IsAsync)
                    {
                        TryEmitConcreteOverloadAsync(
                            csWriter, swiftWriter, method, typeDecl,
                            pairing,
                            moduleName, typeDatabase, emissionContext, logger);
                    }
                    else
                    {
                        TryEmitConcreteOverload(
                            csWriter, swiftWriter, method, typeDecl, pairing,
                            moduleName, wrapperLibPath, typeDatabase, emissionContext, emittedSignatures, logger);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Enumerates the cartesian product of conformers across each specializable param,
    /// yielding one pairing per combination.
    /// </summary>
    private static IEnumerable<(ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)[]>
        CartesianPairings(IReadOnlyList<ConcreteSpecializationEngine.SpecializableParam> specParams)
    {
        var indices = new int[specParams.Count];
        while (true)
        {
            var pairing = new (ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)[specParams.Count];
            for (int i = 0; i < specParams.Count; i++)
            {
                pairing[i] = (specParams[i], specParams[i].Conformers[indices[i]]);
            }
            yield return pairing;

            // Advance indices (odometer-style).
            int pos = specParams.Count - 1;
            while (pos >= 0)
            {
                indices[pos]++;
                if (indices[pos] < specParams[pos].Conformers.Count) break;
                indices[pos] = 0;
                pos--;
            }
            if (pos < 0) yield break;
        }
    }

    /// <summary>
    /// Checks cross-param same-type constraints (e.g., S.Element == T) captured on
    /// <see cref="ConcreteSpecializationEngine.SpecializableParam.CouplingConstraints"/>.
    /// Each coupling on S reads: "S.conformer.AssociatedTypes[AssocName] must equal the
    /// chosen conformer Swift type of OtherParamName." Concrete (non-coupling) assoc-type
    /// constraints are already validated at conformer-filter time in the engine.
    /// </summary>
    private static bool ConformerPairingSatisfiesCoupling(
        (ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)[] pairing)
    {
        var paramTypeByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (p, c) in pairing)
        {
            paramTypeByName[p.GenericParam.TypeName] = c.SwiftQualifiedName;
        }

        foreach (var (param, conformer) in pairing)
        {
            if (param.CouplingConstraints is null) continue;
            foreach (var (assocName, otherParamName) in param.CouplingConstraints)
            {
                if (conformer.AssociatedTypes is null) return false;
                if (!conformer.AssociatedTypes.TryGetValue(assocName, out var declared))
                    return false;
                if (!paramTypeByName.TryGetValue(otherParamName, out var otherConformerType))
                    return false;
                if (!string.Equals(declared, otherConformerType, StringComparison.Ordinal))
                    return false;
            }
        }

        return true;
    }

    private static bool TryEmitConcreteOverload(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodDecl method,
        TypeDecl parentTypeDecl,
        (ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)[] pairing,
        string moduleName,
        string wrapperLibPath,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        HashSet<string> emittedSignatures,
        ILogger logger,
        bool isExtension = false)
    {
        bool isConstructor = method.IsConstructor;
        bool isStatic = method.MethodType == MethodType.Static || isConstructor;
        bool isClass = parentTypeDecl is ClassDecl;

        // Build symbol name (concatenate conformer names across the pairing).
        var safeConformerName = string.Join(
            "_",
            pairing.Select(p => SanitizeTypeName(p.Conformer.SwiftQualifiedName)));

        // Shared preflight — rejects any pairing the Swift/C# emitters couldn't produce valid
        // code for. Single source of truth consulted here AND by IsCsmSyncEligibleForGenericParent,
        // so the sync suppression predicate cannot decide to skip the open-generic emission for
        // a method that this emitter will then silently drop.
        if (!CanEmitConcreteOverloadForPairing(method, parentTypeDecl, pairing, typeDatabase, out var rejectReason))
        {
            logger.LogDebug(
                "ConcreteSpecializationEmitter: Skipping {Method} for {Pairing} — {Reason}.",
                method.Name, safeConformerName, rejectReason);
            return false;
        }

        var methodName = isConstructor ? "init" : method.Name;
        var hashInput = method.MangledName
            + string.Concat(pairing.Select(p => "|" + p.Conformer.SwiftQualifiedName));
        var mangledHash = EmitterUtility.DeterministicHash8(hashInput);
        var cdeclSymbol = $"SBW_CSM_{moduleName}_{parentTypeDecl.Name}_{safeConformerName}_{methodName}_{mangledHash}";

        // Dedup guard
        if (!emissionContext.TryAddMethodWrapperSymbol(cdeclSymbol))
            return false;

        // Recompute return-type classification for the rest of this method — preflight proved it
        // won't trigger a skip, but we still need these flags to drive Swift/C# emission below.
        var returnTypeSpec = method.CSSignature.First().SwiftTypeSpec;
        bool isVoidReturn = returnTypeSpec.IsEmptyTuple;
        bool returnsGenericParam = !isVoidReturn &&
            TryMatchGenericParam(returnTypeSpec, pairing, out _, out _);
        bool isStringReturn = !isVoidReturn && !returnsGenericParam && WitnessDispatchEmitter.IsStringType(returnTypeSpec);

        if (isConstructor)
        {
            returnsGenericParam = false;
            isStringReturn = false;
            isVoidReturn = false; // Constructor returns self
        }

        // Compute C# method signature key for dedup — prevents CS0111 when multiple conformers
        // produce the same visible method signature (name + parameter types).
        var csMethodName = isConstructor
            ? $"From{string.Join("_", pairing.Select(p => SanitizeTypeName(p.Conformer.CSharpType)))}"
            : NameProvider.ToPascalCase(method.Name);
        var sigKey = BuildCSharpSignatureKey(csMethodName, method, pairing, typeDatabase);
        if (!emittedSignatures.Add(sigKey))
        {
            logger.LogDebug(
                "ConcreteSpecializationEmitter: Skipping {Method} for {Pairing} — duplicate C# signature: {Sig}.",
                method.Name, safeConformerName, sigKey);
            return false;
        }

        // Merge availability (method + parent + all conformers) once — both Swift and C#
        // sides need the same floor so generated code and callers agree.
        var mergedAvailability = WrapperEmitterHelpers.MergeAvailability(method.AvailabilityAnnotations, parentTypeDecl);
        foreach (var (_, c) in pairing)
        {
            if (c.AvailabilityAnnotations is { Count: > 0 } conformerAvailability)
            {
                var combined = mergedAvailability is null
                    ? new List<AvailabilityAnnotation>()
                    : new List<AvailabilityAnnotation>(mergedAvailability);
                combined.AddRange(conformerAvailability);
                mergedAvailability = combined;
            }
        }

        // --- Emit Swift @_cdecl wrapper ---
        EmitSwiftWrapper(
            swiftWriter, method, parentTypeDecl, pairing,
            cdeclSymbol, moduleName, isClass, isConstructor, typeDatabase, emissionContext,
            mergedAvailability);

        // --- Emit C# method ---
        EmitCSharpMethod(
            csWriter, method, parentTypeDecl, pairing,
            cdeclSymbol, wrapperLibPath, isConstructor, isStatic, isClass,
            isVoidReturn, isStringReturn, returnsGenericParam, typeDatabase,
            mergedAvailability, isExtension);

        logger.LogInformation(
            "Emitted concrete specialization: {Type}.{Method}<{Pairing}>",
            parentTypeDecl.Name, method.Name, safeConformerName);

        return true;
    }

    /// <summary>
    /// Walks the pairing and returns the first (param, conformer) whose generic-param
    /// name matches <paramref name="typeSpec"/>. Prefers exact-name match; only falls back
    /// to the alternate-depth twin when no exact match exists. This ordering matters when
    /// a parent-generic + method-generic pairing contains names that are each other's
    /// alt-depth twin (e.g. parent T=τ_0_0, method D=τ_1_0) — without the preference,
    /// a method arg typed τ_1_0 would spuriously match the parent T via its alt-twin.
    /// </summary>
    private static bool TryMatchGenericParam(
        TypeSpec typeSpec,
        (ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)[] pairing,
        out ConcreteSpecializationEngine.SpecializableParam? matchedParam,
        out ConcreteSpecializationEngine.ConcreteConformer? matchedConformer)
    {
        foreach (var entry in pairing)
        {
            if (IsGenericParamType(typeSpec, entry.Param.GenericParam.TypeName))
            {
                matchedParam = entry.Param;
                matchedConformer = entry.Conformer;
                return true;
            }
        }
        foreach (var entry in pairing)
        {
            var alt = GetAlternateDepthName(entry.Param.GenericParam.TypeName);
            if (alt != entry.Param.GenericParam.TypeName && IsGenericParamType(typeSpec, alt))
            {
                matchedParam = entry.Param;
                matchedConformer = entry.Conformer;
                return true;
            }
        }
        matchedParam = null;
        matchedConformer = null;
        return false;
    }

    /// <summary>
    /// Applies <see cref="SubstituteTypeSpec"/> over every entry in <paramref name="pairing"/>
    /// so that nested method-level generic param references inside a composite return type
    /// (e.g. <c>HashedAuthenticationCode&lt;H&gt;</c>) resolve to the conformer's concrete type.
    /// Mirrors the async path's substitution in <c>TryBuildEmissionPlan</c>; used at Swift-side
    /// render time for <c>initializeMemory(as:)</c>. If substitution reports an unresolved
    /// associated-type reference, returns the original TypeSpec so the caller can still render
    /// (no regression vs. the prior "render as-is" behavior).
    /// </summary>
    internal static TypeSpec SubstitutePairingGenericsInTypeSpec(
        TypeSpec typeSpec,
        (ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)[] pairing)
    {
        var current = typeSpec;
        foreach (var (param, conformer) in pairing)
        {
            if (!TryBuildConformerTypeSpec(conformer, out var conformerSpec))
                continue;
            var genericName = param.GenericParam.TypeName;
            var altGenericName = GetAlternateDepthName(genericName);
            bool ok = true;
            current = SubstituteTypeSpec(current, genericName, altGenericName, conformerSpec, conformer, ref ok);
            if (!ok)
                return typeSpec; // Leave original; outer caller logs/renders as today.
        }
        return current;
    }

    // ─── Swift Wrapper Generation ────────────────────────────────────

    private static void EmitSwiftWrapper(
        SwiftWriter swiftWriter,
        MethodDecl method,
        TypeDecl parentTypeDecl,
        (ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)[] pairing,
        string cdeclSymbol,
        string moduleName,
        bool isClass,
        bool isConstructor,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        IReadOnlyList<AvailabilityAnnotation>? mergedAvailability)
    {
        var parentSwiftName = BuildConcreteParentSwiftName(parentTypeDecl, pairing);
        bool isInstance = method.MethodType == MethodType.Instance && !isConstructor;

        // Classify return
        var returnTypeSpec = method.CSSignature.First().SwiftTypeSpec;
        bool isVoidReturn = returnTypeSpec.IsEmptyTuple && !isConstructor;
        bool returnsGenericParam = !isVoidReturn && !isConstructor &&
            TryMatchGenericParam(returnTypeSpec, pairing, out _, out _);
        ConcreteSpecializationEngine.ConcreteConformer? returnConformer = null;
        if (returnsGenericParam)
        {
            TryMatchGenericParam(returnTypeSpec, pairing, out _, out var rc);
            returnConformer = rc;
        }
        // Swift concrete return type: if return matches a generic, use that conformer.
        string returnConcreteSwiftType = returnConformer is null
            ? string.Empty
            : (returnConformer.SwiftLiteral ?? returnConformer.SwiftQualifiedName);
        bool isStringReturn = !isVoidReturn && !returnsGenericParam && !isConstructor &&
            WitnessDispatchEmitter.IsStringType(returnTypeSpec);

        // For string returns, ensure Utf8Slice helper
        if (isStringReturn)
        {
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter, emissionContext);
            Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleName, emissionContext);
        }

        // Build Swift parameter list
        var swiftParams = new List<string>();
        var callArgs = new List<string>();

        // Result pointer for indirect returns
        bool needsResultPtr = false;
        if (!isVoidReturn && !isStringReturn && !isConstructor)
        {
            if (returnsGenericParam)
            {
                // The concrete return type may need indirect return
                needsResultPtr = true;
            }
            else
            {
                var (mapping, _) = CdeclReturnMapping.Classify(returnTypeSpec, typeDatabase);
                needsResultPtr = mapping.Kind == CdeclReturnKind.IndirectResult;
            }
        }

        // Struct constructors return via result pointer (class constructors use Unmanaged)
        if (isConstructor && !isClass)
            needsResultPtr = true;

        if (needsResultPtr || isStringReturn)
            swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");

        // Regular parameters
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;

            var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            var argLabel = ClosureEmitter.GetSwiftArgLabelForCdecl(arg);

            if (TryMatchGenericParam(arg.SwiftTypeSpec, pairing, out _, out var matchedConformerObj))
            {
                var matchedConformer = matchedConformerObj!;
                var concreteSwiftType = matchedConformer.SwiftLiteral ?? matchedConformer.SwiftQualifiedName;
                // Generic param → receive concrete type directly
                // For non-frozen struct conformers, receive as UnsafeRawPointer
                // For frozen/class conformers, receive directly
                var category = ClassifyConformerForSwiftParam(matchedConformer, typeDatabase);
                switch (category)
                {
                    case ConformerCategory.Class:
                        swiftParams.Add($"_ _{label}: UnsafeMutableRawPointer");
                        callArgs.Add($"{argLabel}unsafeBitCast(OpaquePointer(_{label}), to: {concreteSwiftType}.self)");
                        break;
                    case ConformerCategory.RawBuffer:
                        // byte[] / [UInt8]: receive (ptr, length), reconstruct as Foundation.Data
                        // zero-copy. The C# side pins via fixed(byte*) for the duration of the
                        // @_cdecl call, so .none deallocator is safe (Swift never outlives the
                        // pin — this is a synchronous call). Swift infers D = Foundation.Data
                        // at the call site regardless of the conformer's nominal [UInt8] identity,
                        // which is fine: both [UInt8] and Data conform to DataProtocol.
                        swiftParams.Add($"_ _{label}: UnsafeRawPointer");
                        swiftParams.Add($"_ _{label}Len: Int");
                        callArgs.Add($"{argLabel}Data(bytesNoCopy: UnsafeMutableRawPointer(mutating: _{label}), count: _{label}Len, deallocator: .none)");
                        break;
                    case ConformerCategory.InlineSwiftStruct:
                        // Foundation.Data (and future allowlisted value structs): the C# side
                        // pins &data via fixed(Data*) and passes (IntPtr)p. Swift loads via
                        // assumingMemoryBound+pointee, same shape as NonFrozenStruct.
                        swiftParams.Add($"_ _{label}: UnsafeRawPointer");
                        callArgs.Add($"{argLabel}_{label}.assumingMemoryBound(to: {concreteSwiftType}.self).pointee");
                        break;
                    default:
                        // Frozen and non-frozen structs: pass as pointer, load value.
                        // Even frozen structs use pointer indirection because their C# binding
                        // is a class with SafeHandle, not a blittable C# struct.
                        swiftParams.Add($"_ _{label}: UnsafeRawPointer");
                        callArgs.Add($"{argLabel}_{label}.assumingMemoryBound(to: {concreteSwiftType}.self).pointee");
                        break;
                }
            }
            else if (arg.HasDefaultArg)
            {
                continue; // Swift fills the default
            }
            else
            {
                // Non-generic param — classify and map directly
                var abiCategory = MethodClosureBridge.ClassifyParam(arg, typeDatabase);
                switch (abiCategory)
                {
                    case MethodClosureBridge.ParamAbiCategory.Primitive:
                        swiftParams.Add($"_ _{label}: {ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec)}");
                        callArgs.Add($"{argLabel}_{label}");
                        break;
                    case MethodClosureBridge.ParamAbiCategory.ObjCHandle:
                    case MethodClosureBridge.ParamAbiCategory.PayloadHandle:
                        swiftParams.Add($"_ _{label}: UnsafeRawPointer");
                        var swiftTypeName = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
                        callArgs.Add($"{argLabel}unsafeBitCast(OpaquePointer(_{label}), to: {swiftTypeName}.self)");
                        break;
                    default:
                        swiftParams.Add($"_ _{label}: UnsafeRawPointer");
                        callArgs.Add($"{argLabel}_{label}");
                        break;
                }
            }
        }

        // Self parameter for instance methods. Mutating struct methods need a mutable
        // self pointer so we can write the modified value back via pointee assignment.
        bool needsMutatingSelf = isInstance && !isClass && method.IsMutating;
        if (isInstance)
        {
            if (isClass || needsMutatingSelf)
                swiftParams.Add("_ self_: UnsafeMutableRawPointer");
            else
                swiftParams.Add("_ self_: UnsafeRawPointer");
        }

        // Build self conversion. Mutating methods bind `var __self` so the call can
        // mutate it in place; the write-back below propagates the change to the
        // payload memory the C# SafeHandle owns.
        string selfConversion = "";
        string selfWriteBack = "";
        if (isInstance)
        {
            if (isClass)
            {
                selfConversion = $"let __self = unsafeBitCast(OpaquePointer(self_), to: {parentSwiftName}.self)";
            }
            else if (needsMutatingSelf)
            {
                selfConversion = $"var __self = self_.assumingMemoryBound(to: {parentSwiftName}.self).pointee";
                // pointee = ... routes through the value witness table and correctly
                // handles ARC for non-BitwiseCopyable structs (storeBytes would not).
                selfWriteBack = $"self_.assumingMemoryBound(to: {parentSwiftName}.self).pointee = __self";
            }
            else
            {
                selfConversion = $"let __self = self_.assumingMemoryBound(to: {parentSwiftName}.self).pointee";
            }
        }

        // Build method call
        string callTarget = isInstance ? "__self" : parentSwiftName;
        string callExpr;
        if (isConstructor)
        {
            callExpr = $"{parentSwiftName}({string.Join(", ", callArgs)})";
        }
        else
        {
            callExpr = $"{callTarget}.{NameProvider.ParserNameToSwift(method)}({string.Join(", ", callArgs)})";
        }

        // Return type
        string swiftReturnType;
        if (isConstructor)
            swiftReturnType = isClass ? " -> UnsafeMutableRawPointer" : "";
        else if (isVoidReturn || isStringReturn || needsResultPtr)
            swiftReturnType = "";
        else if (returnsGenericParam)
            swiftReturnType = $" -> {returnConcreteSwiftType}";
        else
            swiftReturnType = $" -> {ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec)}";

        // Emit
        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            parentTypeDecl, method.IsMainActorIsolated, method.IsNonisolated);

        var pairingComment = string.Join(", ", pairing.Select(p => p.Conformer.SwiftLiteral ?? p.Conformer.SwiftQualifiedName));
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($"// Concrete specialization: {parentSwiftName}.{method.Name}<{pairingComment}>");

        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, cdeclSymbol, needsMainActor, mergedAvailability);
        swiftWriter.WriteLine($"public func {cdeclSymbol}(");
        swiftWriter.WriteLine($"    {string.Join(",\n    ", swiftParams)}");
        swiftWriter.WriteLine($"){swiftReturnType} {{");

        if (!string.IsNullOrEmpty(selfConversion))
            swiftWriter.WriteLine($"    {selfConversion}");

        if (isConstructor)
        {
            if (isClass)
            {
                swiftWriter.WriteLine($"    let _result = {callExpr}");
                swiftWriter.WriteLine($"    return Unmanaged.passRetained(_result as AnyObject).toOpaque()");
            }
            else
            {
                // Struct constructor: return via initializeMemory through result pointer
                swiftWriter.WriteLine($"    let _result = {callExpr}");
                swiftWriter.WriteLine($"    resultPtr.initializeMemory(as: ({parentSwiftName}).self, repeating: _result, count: 1)");
            }
        }
        else if (isVoidReturn)
        {
            swiftWriter.WriteLine($"    {callExpr}");
            if (!string.IsNullOrEmpty(selfWriteBack))
                swiftWriter.WriteLine($"    {selfWriteBack}");
        }
        else if (isStringReturn)
        {
            // Mutating + string return: emit the write-back immediately after `let result = ...`
            // (before the string serializes) so callers observe the mutation. Without this the
            // mutation would live only on the local `var __self` copy.
            OptionalPointerWrapperEmitter.EmitStringReturnBody(
                swiftWriter, callExpr, "    ",
                postCallStatement: string.IsNullOrEmpty(selfWriteBack) ? null : selfWriteBack);
        }
        else if (needsResultPtr)
        {
            string returnTypeStr;
            if (returnsGenericParam)
            {
                returnTypeStr = returnConcreteSwiftType;
            }
            else
            {
                // Return type may CONTAIN pairing generics (e.g. `HashedAuthenticationCode<H>`).
                // Substitute each pairing's method-level and parent-level generic param with its
                // concrete conformer so the rendered type doesn't leak an unresolved `H` into
                // `initializeMemory(as:)`. Falls back to the unsubstituted render on failure
                // (matches previous behavior — no regression for shapes we couldn't handle before).
                var substitutedReturn = SubstitutePairingGenericsInTypeSpec(returnTypeSpec, pairing);
                returnTypeStr = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(substitutedReturn);
            }
            swiftWriter.WriteLine($"    let _result = {callExpr}");
            if (!string.IsNullOrEmpty(selfWriteBack))
                swiftWriter.WriteLine($"    {selfWriteBack}");
            swiftWriter.WriteLine($"    resultPtr.initializeMemory(as: ({returnTypeStr}).self, repeating: _result, count: 1)");
        }
        else
        {
            if (!string.IsNullOrEmpty(selfWriteBack))
            {
                swiftWriter.WriteLine($"    let _result = {callExpr}");
                swiftWriter.WriteLine($"    {selfWriteBack}");
                swiftWriter.WriteLine($"    return _result");
            }
            else
            {
                swiftWriter.WriteLine($"    return {callExpr}");
            }
        }

        swiftWriter.WriteLine("}");
    }

    // ─── C# Code Generation ──────────────────────────────────────────

    private static void EmitCSharpMethod(
        CSharpWriter csWriter,
        MethodDecl method,
        TypeDecl parentTypeDecl,
        (ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)[] pairing,
        string cdeclSymbol,
        string wrapperLibPath,
        bool isConstructor,
        bool isStatic,
        bool isClass,
        bool isVoidReturn,
        bool isStringReturn,
        bool returnsGenericParam,
        ITypeDatabase typeDatabase,
        IReadOnlyList<AvailabilityAnnotation>? mergedAvailability,
        bool isExtension = false)
    {
        var methodName = NameProvider.ToPascalCase(method.Name);
        if (isConstructor)
            methodName = $"From{string.Join("_", pairing.Select(p => SanitizeTypeName(p.Conformer.CSharpType)))}";

        // Resolve the return-side conformer if the return is a generic param.
        ConcreteSpecializationEngine.ConcreteConformer? returnConformer = null;
        if (returnsGenericParam)
        {
            var returnTypeSpec = method.CSSignature.First().SwiftTypeSpec;
            TryMatchGenericParam(returnTypeSpec, pairing, out _, out returnConformer);
        }

        // Build public parameter list and P/Invoke parameter list
        var publicParams = new List<string>();
        var pinvokeParams = new List<string>();
        var callArgs = new List<string>();

        // Pins: each entry is a C# fixed-statement "fixed (byte* _pfoo = foo)" that must
        // wrap the pinvoke call. InlineSwiftStruct uses `&param` directly (unmanaged
        // value type — no fixed needed) but still requires an `unsafe` context.
        var fixedStatements = new List<string>();
        bool needsUnsafe = false;

        bool needsResultPtr = false;
        if (isConstructor && !isClass)
        {
            // Struct constructors always return via result pointer
            needsResultPtr = true;
        }
        else if (!isVoidReturn && !isConstructor)
        {
            var returnTypeSpec = method.CSSignature.First().SwiftTypeSpec;
            if (returnsGenericParam)
                needsResultPtr = true;
            else if (!isStringReturn)
            {
                var (mapping, _) = CdeclReturnMapping.Classify(returnTypeSpec, typeDatabase);
                needsResultPtr = mapping.Kind == CdeclReturnKind.IndirectResult;
            }
        }

        if (needsResultPtr || isStringReturn)
            pinvokeParams.Add("IntPtr resultPtr");

        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;
            if (arg.HasDefaultArg) continue;

            var csName = NameProvider.GetCSharpParameterName(arg);

            if (TryMatchGenericParam(arg.SwiftTypeSpec, pairing, out _, out var matchedConformerObj))
            {
                var matchedConformer = matchedConformerObj!;
                // Generic param → concrete type
                var category = ClassifyConformerForCSharp(matchedConformer, typeDatabase);
                switch (category)
                {
                    case ConformerCategory.Class:
                        publicParams.Add($"{matchedConformer.CSharpType} {csName}");
                        pinvokeParams.Add($"IntPtr {csName}");
                        callArgs.Add($"{csName}.Payload.DangerousGetHandle()");
                        break;
                    case ConformerCategory.RawBuffer:
                        // byte[] / [UInt8]: pin via fixed(byte*), pass (ptr, length).
                        // Swift reconstructs as Data(bytesNoCopy:...,deallocator:.none);
                        // pin lifetime covers the entire @_cdecl call.
                        publicParams.Add($"{matchedConformer.CSharpType} {csName}");
                        pinvokeParams.Add($"IntPtr {csName}");
                        pinvokeParams.Add($"nint {csName}Len");
                        fixedStatements.Add($"fixed (byte* _p{csName} = {csName})");
                        callArgs.Add($"(IntPtr)_p{csName}");
                        callArgs.Add($"(nint)({csName} is null ? 0 : {csName}.Length)");
                        needsUnsafe = true;
                        break;
                    case ConformerCategory.InlineSwiftStruct:
                        // Foundation.Data (and future allowlisted value structs): unmanaged
                        // blittable struct, so &arg is directly usable within an unsafe block —
                        // no `fixed` required. Swift loads via pointee on the other side.
                        var csTypeName = InlineSwiftStructAllowlist[matchedConformer.SwiftQualifiedName];
                        publicParams.Add($"{csTypeName} {csName}");
                        pinvokeParams.Add($"IntPtr {csName}");
                        callArgs.Add($"(IntPtr)(&{csName})");
                        needsUnsafe = true;
                        break;
                    default:
                        // Frozen and non-frozen structs: pass via IntPtr.
                        // Even frozen structs are C# classes with SafeHandle, not blittable structs.
                        publicParams.Add($"{matchedConformer.CSharpType} {csName}");
                        pinvokeParams.Add($"IntPtr {csName}");
                        callArgs.Add($"{csName}.Payload.DangerousGetHandle()");
                        break;
                }
            }
            else
            {
                // Non-generic param
                var abiCategory = MethodClosureBridge.ClassifyParam(arg, typeDatabase);
                switch (abiCategory)
                {
                    case MethodClosureBridge.ParamAbiCategory.Primitive:
                        if (MarshallingHelpers.IsBoolType(arg.SwiftTypeSpec))
                        {
                            publicParams.Add($"bool {csName}");
                            pinvokeParams.Add($"[MarshalAs(UnmanagedType.U1)] bool {csName}");
                        }
                        else
                        {
                            var primType = MethodClosureBridge.GetPInvokePrimitiveType(arg.SwiftTypeSpec);
                            publicParams.Add($"{primType} {csName}");
                            pinvokeParams.Add($"{primType} {csName}");
                        }
                        callArgs.Add(csName);
                        break;
                    case MethodClosureBridge.ParamAbiCategory.ObjCHandle:
                    {
                        // ObjC-bridged/rooted types (UIKit.UIImage, Foundation.NSLocale, …)
                        // are .NET iOS bindings around NSObject and don't implement
                        // ISwiftObject. Pass the native NSObject handle instead.
                        var csType = ResolvePublicCSharpType(arg.SwiftTypeSpec, typeDatabase);
                        publicParams.Add($"{csType} {csName}");
                        pinvokeParams.Add($"IntPtr {csName}");
                        callArgs.Add($"{csName}.Handle");
                        break;
                    }
                    case MethodClosureBridge.ParamAbiCategory.PayloadHandle:
                    {
                        var csType = ResolvePublicCSharpType(arg.SwiftTypeSpec, typeDatabase);
                        publicParams.Add($"{csType} {csName}");
                        pinvokeParams.Add($"IntPtr {csName}");
                        // SwiftHandle is an explicit interface impl on generated ISwiftObject
                        // types, so access via an ISwiftObject cast.
                        callArgs.Add($"((global::Swift.Runtime.ISwiftObject){csName}).SwiftHandle");
                        break;
                    }
                    default:
                        return; // Unsupported param type
                }
            }
        }

        // Self parameter
        if (!isStatic)
        {
            pinvokeParams.Add("IntPtr self_");
            if (isExtension)
            {
                // Extension-method path: receiver is an explicit `this {ConcreteParent} self`
                // first public parameter. Source the P/Invoke self-arg via the ISwiftObject
                // cast since `SwiftHandle` is an explicit interface impl on the generated
                // type, not a public member.
                var concreteParentCs = BuildConcreteParentCsharpName(parentTypeDecl, pairing, typeDatabase);
                publicParams.Insert(0, $"this {concreteParentCs} self");
                callArgs.Add("((global::Swift.Runtime.ISwiftObject)self).SwiftHandle");
            }
            else
            {
                var selfExpr = isClass ? "_handle.DangerousGetHandle()" : "_payload.DangerousGetHandle()";
                callArgs.Add(selfExpr);
            }
        }

        // Determine C# return type
        string csReturnType = "void";
        if (isConstructor)
        {
            // Extension-class constructors (static factory on closed generic parent)
            // return the closed parent type, e.g. `GenericContainer<SongItem>`.
            csReturnType = isExtension
                ? BuildConcreteParentCsharpName(parentTypeDecl, pairing, typeDatabase)
                : parentTypeDecl.Name;
        }
        else if (!isVoidReturn)
        {
            if (returnsGenericParam)
                csReturnType = returnConformer!.CSharpType;
            else if (isStringReturn)
                csReturnType = "string";
            else
            {
                var returnTypeSpec = method.CSSignature.First().SwiftTypeSpec;
                csReturnType = ResolvePublicCSharpType(returnTypeSpec, typeDatabase);
            }
        }

        string pinvokeReturn;
        if (isConstructor)
        {
            pinvokeReturn = isClass ? "IntPtr" : "void"; // struct ctors use result pointer
        }
        else if (isVoidReturn || isStringReturn || needsResultPtr)
            pinvokeReturn = "void";
        else if (returnsGenericParam)
        {
            // All conformers return via IntPtr — even frozen structs are C# classes with SafeHandle
            pinvokeReturn = "IntPtr";
        }
        else
        {
            // bool returns need a dedicated P/Invoke signature so PInvokeEmitHelper
            // emits `[return: MarshalAs(UnmanagedType.U1)]` and the public method's
            // `return` statement doesn't have to coerce an IntPtr to bool.
            var returnTypeSpec = method.CSSignature.First().SwiftTypeSpec;
            pinvokeReturn = MarshallingHelpers.IsBoolType(returnTypeSpec) ? "bool" : "IntPtr";
        }

        // --- Emit P/Invoke ---
        csWriter.WriteLine();
        // P/Invoke needs the same OS guard as the public caller so CA1416 matches up when
        // the wrapper is stripped into a platform-specific assembly.
        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
            csWriter, mergedAvailability, parentTypeDecl.AvailabilityAnnotations);
        PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
        {
            LibraryPath = wrapperLibPath,
            EntryPoint = cdeclSymbol,
            MethodName = cdeclSymbol,
            ReturnType = pinvokeReturn,
            ParametersString = string.Join(", ", pinvokeParams),
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Internal
        });

        // --- Emit public method ---
        csWriter.WriteLine();
        // Extension methods live in a non-generic static partial class, so they MUST be
        // `static` regardless of whether the underlying Swift method is instance or static.
        var staticStr = (isStatic || isExtension) ? "static " : "";
        var unsafeStr = needsUnsafe ? "unsafe " : "";
        var pairingDoc = string.Join(", ", pairing.Select(p => p.Conformer.CSharpType));
        csWriter.WriteLine($"/// <summary>Concrete specialization for {pairingDoc}.</summary>");
        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
            csWriter, mergedAvailability, parentTypeDecl.AvailabilityAnnotations);
        csWriter.WriteLine($"public {unsafeStr}{staticStr}{csReturnType} {methodName}({string.Join(", ", publicParams)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Build P/Invoke call
        var pinvokeCallArgs = new List<string>();
        if (needsResultPtr || isStringReturn)
            pinvokeCallArgs.Add("resultPtr");
        pinvokeCallArgs.AddRange(callArgs);

        string pinvokeCall = $"{cdeclSymbol}({string.Join(", ", pinvokeCallArgs)})";

        if (needsResultPtr || isStringReturn)
        {
            if (isStringReturn)
            {
                // SBW_Utf8Slice is exactly 2 machine words
                csWriter.WriteLine("IntPtr resultPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(nint.Size * 2);");
            }
            else
            {
                // Non-ISwiftObject indirect results are filtered out by the skip guard in
                // EmitSpecializedMethod, so csReturnType is always an ISwiftObject class here.
                csWriter.WriteLine($"IntPtr resultPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(SwiftMarshal.GetSwiftTypeSize<{csReturnType}>());");
            }
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
        }

        // Emit nested fixed statements (if any) wrapping the pinvoke call + marshalling.
        // Holding pins across the marshal is harmless — Data(bytesNoCopy:...) was already
        // consumed on the Swift side by the time the wrapper returns, and the marginal
        // cost of keeping the pin a few instructions longer is not worth the complexity
        // of splitting the call and the marshal.
        foreach (var fixedStmt in fixedStatements)
        {
            csWriter.WriteLine(fixedStmt);
            csWriter.WriteLine("{");
            csWriter.Indent++;
        }

        if (isConstructor)
        {
            if (isClass)
            {
                csWriter.WriteLine($"return new {csReturnType}(new Swift.Runtime.SwiftHandle({pinvokeCall}));");
            }
            else
            {
                // Struct constructor: call writes into resultPtr, then marshal back
                csWriter.WriteLine($"{pinvokeCall};");
                csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{csReturnType}>(resultPtr);");
            }
        }
        else if (isVoidReturn)
        {
            csWriter.WriteLine($"{pinvokeCall};");
        }
        else if (isStringReturn)
        {
            csWriter.WriteLine($"{pinvokeCall};");
            csWriter.WriteLine("return SwiftMarshal.ReadUtf8Slice(resultPtr);");
        }
        else if (needsResultPtr)
        {
            csWriter.WriteLine($"{pinvokeCall};");
            csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{csReturnType}>(resultPtr);");
        }
        else if (returnsGenericParam)
        {
            csWriter.WriteLine($"return {pinvokeCall};");
        }
        else
        {
            csWriter.WriteLine($"return {pinvokeCall};");
        }

        // Close fixed blocks (reverse nesting order)
        for (int i = 0; i < fixedStatements.Count; i++)
        {
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        if (needsResultPtr || isStringReturn)
        {
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(resultPtr); }");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");

        method.WasEmitted = true;
    }

    // ─── Classification helpers ──────────────────────────────────────

    private enum ConformerCategory
    {
        FrozenStruct,
        NonFrozenStruct,
        Class,
        // byte[] / [UInt8] — marshalled via (IntPtr, nint) pair with fixed(byte*) pin on
        // the C# side and Data(bytesNoCopy:count:deallocator:.none) reconstruction on the
        // Swift side. Never an indirect-result return type.
        RawBuffer,
        // Hand-written ISwiftObject value structs (currently only Foundation.Data). The
        // C# binding is a blittable struct rather than a class with SafeHandle, so the
        // generic-param marshalling pins via fixed(T*) + passes (IntPtr)p instead of
        // .Payload.DangerousGetHandle(). Allowlist-driven — future inline value structs
        // need explicit registration.
        InlineSwiftStruct
    }

    // Conformers whose C# binding is a hand-written ISwiftObject value struct. These
    // bypass the ObjC/native-bridged rejection in TryEmitConcreteOverload because their
    // NativeTypeName (e.g. Foundation.Data → NSData) is an implementation detail of how
    // we bridge into Foundation, not a signal that the C# side lacks a pinnable layout.
    // Maps Swift qualified name → fully-qualified C# type used for emission (public
    // parameter type and `fixed (T* p = &v)` binding), since the generated binding file
    // doesn't `using Swift.Foundation`.
    private static readonly Dictionary<string, string> InlineSwiftStructAllowlist = new(StringComparer.Ordinal)
    {
        ["Foundation.Data"] = "global::Swift.Foundation.Data"
    };

    private static ConformerCategory ClassifyConformerForSwiftParam(
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        ITypeDatabase typeDatabase)
    {
        // byte[] / [UInt8]: hint-only conformers have SwiftType == null because
        // SwiftTypeName.FromModuleQualifiedName can't parse generic types. Detect via the
        // C# array suffix.
        if (conformer.CSharpType != null &&
            conformer.CSharpType.EndsWith("[]", StringComparison.Ordinal))
            return ConformerCategory.RawBuffer;

        if (InlineSwiftStructAllowlist.ContainsKey(conformer.SwiftQualifiedName))
            return ConformerCategory.InlineSwiftStruct;

        if (conformer.SwiftType == null) return ConformerCategory.FrozenStruct; // Hint-based, assume primitive

        if (typeDatabase.TryGetTypeRecord(conformer.SwiftType, out var record))
        {
            if (record.Kind == TypeRecordKind.Class) return ConformerCategory.Class;
            if (record.Flags.HasFlag(TypeRecordFlags.Frozen)) return ConformerCategory.FrozenStruct;
            return ConformerCategory.NonFrozenStruct;
        }

        // ABI-indexed conformers without type records: use pointer indirection as safe default.
        // Direct passing can fail if the type isn't @_cdecl-compatible (e.g., non-ObjC enums).
        return ConformerCategory.NonFrozenStruct;
    }

    private static ConformerCategory ClassifyConformerForCSharp(
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        ITypeDatabase typeDatabase) =>
        ClassifyConformerForSwiftParam(conformer, typeDatabase);

    internal enum StructuralEmitReject { None, NestedType, ObjCBridged }

    // Per-conformer structural gate used by TryEmitConcreteOverload's preflight AND by
    // IsCsmSyncEligibleForGenericParent. Keeping this single source of truth means the
    // sync suppression predicate cannot declare eligibility for a pairing the emitter
    // will silently drop — if a new rejection is added here, both paths learn about it.
    internal static StructuralEmitReject ClassifyConformerStructurally(
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        ITypeDatabase typeDatabase)
    {
        if (conformer.SwiftType != null &&
            conformer.SwiftType.ModuleQualifiedName.Split('.').Length > 2)
            return StructuralEmitReject.NestedType;

        var category = ClassifyConformerForSwiftParam(conformer, typeDatabase);
        if (category != ConformerCategory.InlineSwiftStruct &&
            conformer.SwiftType != null &&
            typeDatabase.TryGetTypeRecord(conformer.SwiftType, out var record) &&
            (record.NativeTypeName != null
                || MarshallingHelpers.IsObjCBridged(record)
                || MarshallingHelpers.IsObjCRooted(record)))
            return StructuralEmitReject.ObjCBridged;

        return StructuralEmitReject.None;
    }

    /// <summary>
    /// Single-source-of-truth preflight: decides whether this (method × pairing) combination
    /// can produce valid Swift @_cdecl + C# overload code. Consulted by
    /// <see cref="TryEmitConcreteOverload"/> AND by <c>IsCsmSyncEligibleForGenericParent</c> so
    /// the suppression predicate stays in lockstep with what the emitter actually produces —
    /// otherwise the sync predicate could suppress the open-generic emission for a method the
    /// emitter then silently drops, stripping the method's surface entirely.
    /// </summary>
    internal static bool CanEmitConcreteOverloadForPairing(
        MethodDecl method,
        TypeDecl parentTypeDecl,
        (ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)[] pairing,
        ITypeDatabase typeDatabase,
        out string? rejectReason)
    {
        // Per-conformer structural gate: no nested-type or ObjC-bridged conformers.
        foreach (var (_, conformer) in pairing)
        {
            switch (ClassifyConformerStructurally(conformer, typeDatabase))
            {
                case StructuralEmitReject.NestedType:
                    rejectReason = $"nested type conformer '{conformer.SwiftQualifiedName}'";
                    return false;
                case StructuralEmitReject.ObjCBridged:
                    rejectReason = $"ObjC/native-bridged conformer '{conformer.SwiftQualifiedName}' lacks Payload accessor";
                    return false;
            }
        }

        bool isConstructor = method.IsConstructor;
        bool isClass = parentTypeDecl is ClassDecl;

        var returnTypeSpec = method.CSSignature.First().SwiftTypeSpec;
        bool isVoidReturn = returnTypeSpec.IsEmptyTuple;
        bool returnsGenericParam = !isVoidReturn &&
            TryMatchGenericParam(returnTypeSpec, pairing, out _, out _);
        ConcreteSpecializationEngine.ConcreteConformer? returnConformer = null;
        if (returnsGenericParam)
            TryMatchGenericParam(returnTypeSpec, pairing, out _, out returnConformer);
        bool isStringReturn = !isVoidReturn && !returnsGenericParam && WitnessDispatchEmitter.IsStringType(returnTypeSpec);

        // Self return: @_cdecl global functions can't return Self.
        if (!isVoidReturn && !isConstructor && IsSelfReturn(returnTypeSpec))
        {
            rejectReason = "returns Self";
            return false;
        }

        // Failable initializer: init? returns Optional which we can't handle in @_cdecl.
        if (isConstructor && IsOptionalReturn(returnTypeSpec))
        {
            rejectReason = "failable initializer";
            return false;
        }

        // Unresolved generic param anywhere in the return tree (e.g. Container<T>, Container<T.Element>).
        if (!isVoidReturn && !returnsGenericParam && !isStringReturn && !isConstructor &&
            ContainsAnyGenericParam(returnTypeSpec))
        {
            rejectReason = "return type contains unresolved generic param";
            return false;
        }

        // Non-constructor Optional<T> return: CSM emitter lacks unwrap logic for this case.
        if (!isVoidReturn && !returnsGenericParam && !isStringReturn && !isConstructor &&
            IsOptionalReturn(returnTypeSpec))
        {
            rejectReason = "Optional return type not yet supported";
            return false;
        }

        // Indirect-result return must be ISwiftObject — GetSwiftTypeSize<T>() is T: ISwiftObject.
        if (!isVoidReturn && !isStringReturn)
        {
            bool needsIndirectResult = false;
            bool indirectReturnIsSwiftObject = true;

            if (isConstructor && !isClass)
            {
                needsIndirectResult = true;
                var parentTypeName = parentTypeDecl.SwiftTypeName;
                indirectReturnIsSwiftObject = parentTypeName != null
                    && typeDatabase.TryGetTypeRecord(parentTypeName, out var ctorRecord)
                    && ctorRecord.Kind == TypeRecordKind.Struct
                    && (!ctorRecord.Flags.HasFlag(TypeRecordFlags.Frozen)
                        || ctorRecord.Flags.HasFlag(TypeRecordFlags.RequiresMemoryManagement));
            }
            else if (returnsGenericParam)
            {
                needsIndirectResult = true;
                var category = ClassifyConformerForCSharp(returnConformer!, typeDatabase);
                indirectReturnIsSwiftObject = category is
                    ConformerCategory.NonFrozenStruct
                    or ConformerCategory.Class
                    or ConformerCategory.InlineSwiftStruct;
            }
            else if (!isConstructor)
            {
                var (mapping, _) = CdeclReturnMapping.Classify(returnTypeSpec, typeDatabase);
                if (mapping.Kind == CdeclReturnKind.IndirectResult)
                {
                    needsIndirectResult = true;
                    indirectReturnIsSwiftObject = returnTypeSpec is NamedTypeSpec irNamed
                        && typeDatabase.TryGetTypeRecord(irNamed, out var irRecord)
                        && (irRecord.Kind == TypeRecordKind.Class
                            || (irRecord.Kind == TypeRecordKind.Struct
                                && (!irRecord.Flags.HasFlag(TypeRecordFlags.Frozen)
                                    || irRecord.Flags.HasFlag(TypeRecordFlags.RequiresMemoryManagement))));
                }
            }

            if (needsIndirectResult && !indirectReturnIsSwiftObject)
            {
                rejectReason = "indirect result return type is not ISwiftObject";
                return false;
            }
        }

        // Non-generic params must be passable and not reference the pairing generics.
        if (!AreNonGenericParamsCompatible(method, pairing, typeDatabase))
        {
            rejectReason = "incompatible non-generic params";
            return false;
        }

        // Non-generic param referencing the pairing generic inside a complex type.
        if (HasNonGenericParamReferencingGeneric(method, pairing))
        {
            rejectReason = "non-generic param references generic type";
            return false;
        }

        // Bilateral associated-type filter (defense in depth over ConformerPairingSatisfiesCoupling).
        // The coupling engine only sees same-type constraints that made it onto
        // SpecializableParam.CouplingConstraints. Constraints encoded on the parent type's
        // generics (e.g. `MusicItemCollection<MusicItem>.init<S: Sequence>() where S.Element == MusicItem`)
        // or directly on the method param's AssosiatedTypeConformances with Kind=ConcreteType
        // can bypass CouplingConstraints depending on how the ABI parser captured them.
        // Recheck here so pathological pairings like `[UInt8]` against `S.Element == Album`
        // are rejected before we emit an uncompilable Swift wrapper.
        if (!DoesPairingSatisfyAssociatedTypeConstraints(method, parentTypeDecl, pairing))
        {
            rejectReason = "associated-type constraint not satisfied by conformer";
            return false;
        }

        rejectReason = null;
        return true;
    }

    /// <summary>
    /// For every generic parameter in the union of the method's and parent type's generics,
    /// verify that every <see cref="ConformanceKind.ConcreteType"/> entry on its
    /// <see cref="GenericArgumentDecl.AssosiatedTypeConformances"/> is satisfied by the chosen
    /// conformer's <see cref="ConcreteSpecializationEngine.ConcreteConformer.AssociatedTypes"/>.
    /// This catches constraints the engine's <c>CouplingConstraints</c> didn't capture — typically
    /// parent-declared same-type floors like <c>S.Element == Album</c> that the pairing machinery
    /// would otherwise enumerate (e.g. <c>S = [UInt8]</c>) and emit an uncompilable wrapper for.
    /// </summary>
    internal static bool DoesPairingSatisfyAssociatedTypeConstraints(
        MethodDecl method,
        TypeDecl parentTypeDecl,
        (ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)[] pairing)
    {
        foreach (var (param, conformer) in pairing)
        {
            var assocList = param.GenericParam?.AssosiatedTypeConformances;
            if (assocList == null || assocList.Count == 0)
                continue;

            foreach (var assoc in assocList)
            {
                if (assoc.Kind != ConformanceKind.ConcreteType)
                    continue;
                if (assoc.Path == null || assoc.Path.Length < 2)
                    continue;

                // Path[0] is the owning generic param's name; Path[1..] is the associated-type chain.
                // Single-hop (e.g. `S.Element`) resolves directly against the conformer's flat
                // AssociatedTypes map. For deeper chains (e.g. `S.SubSequence.Element`), we fall
                // back to leaf-name verification: stdlib Collection/Sequence conformers expose
                // the same `Element` through every SubSequence/Slice alias, so the leaf still has
                // to match. Fail-closed when the leaf is missing — better to drop a specialization
                // we can't verify than to emit an uncompilable wrapper.
                var assocName = assoc.Path.Length == 2 ? assoc.Path[1] : assoc.Path[assoc.Path.Length - 1];
                var expected = assoc.ConformanceTarget.ModuleQualifiedName;

                if (conformer.AssociatedTypes is null)
                    return false;
                if (!conformer.AssociatedTypes.TryGetValue(assocName, out var declared))
                    return false;
                if (!string.Equals(declared, expected, StringComparison.Ordinal))
                    return false;
            }
        }
        return true;
    }

    private static bool AreNonGenericParamsCompatible(
        MethodDecl method,
        (ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)[] pairing,
        ITypeDatabase typeDatabase)
    {
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;
            if (arg.HasDefaultArg) continue;
            if (TryMatchGenericParam(arg.SwiftTypeSpec, pairing, out _, out _)) continue;

            var category = MethodClosureBridge.ClassifyParam(arg, typeDatabase);
            if (category is not (MethodClosureBridge.ParamAbiCategory.Primitive
                or MethodClosureBridge.ParamAbiCategory.ObjCHandle
                or MethodClosureBridge.ParamAbiCategory.PayloadHandle))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsGenericParamType(TypeSpec typeSpec, string genericParamName)
    {
        return typeSpec is NamedTypeSpec named && named.Name == genericParamName;
    }

    /// <summary>
    /// Returns the alternate depth variant of a generic param name.
    /// Method-level generics can appear as τ_0_0 (on non-generic parents) or τ_1_0
    /// (on generic parents). We need to check both variants.
    /// </summary>
    private static string GetAlternateDepthName(string genericParamName)
    {
        if (genericParamName.StartsWith("τ_1_"))
            return "τ_0_" + genericParamName.Substring(4);
        if (genericParamName.StartsWith("τ_0_"))
            return "τ_1_" + genericParamName.Substring(4);
        return genericParamName;
    }

    private static bool IsSelfReturn(TypeSpec returnTypeSpec)
    {
        return returnTypeSpec is NamedTypeSpec named &&
               (named.Name == "Self" || named.Name.EndsWith(".Self"));
    }

    private static bool IsOptionalReturn(TypeSpec returnTypeSpec)
    {
        return returnTypeSpec is NamedTypeSpec named &&
               (named.Name == "Swift.Optional" || named.Name == "Optional");
    }

    /// <summary>
    /// Checks if any non-generic parameter references the generic type parameter in a complex
    /// type position (e.g., DataResponse&lt;τ_0_0, Error&gt;). These can't be simply substituted.
    /// Also checks the return type for any generic param reference at any depth.
    /// </summary>
    private static bool HasNonGenericParamReferencingGeneric(
        MethodDecl method,
        (ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)[] pairing)
    {
        // Collect every generic name (and its alternate-depth twin) covered by the pairing.
        var names = new List<string>();
        foreach (var (p, _) in pairing)
        {
            names.Add(p.GenericParam.TypeName);
            names.Add(GetAlternateDepthName(p.GenericParam.TypeName));
        }

        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;
            if (arg.HasDefaultArg) continue;
            if (TryMatchGenericParam(arg.SwiftTypeSpec, pairing, out _, out _)) continue;

            // This non-generic param must not reference any of the pairing generics.
            foreach (var name in names)
            {
                if (ContainsGenericParam(arg.SwiftTypeSpec, name)) return true;
            }
        }

        // Return-type pass: flag a param reference under an alternate-depth twin that wasn't
        // picked up by returnsGenericParam (matches prior single-param behavior).
        var returnTypeSpec = method.CSSignature.First().SwiftTypeSpec;
        if (!returnTypeSpec.IsEmptyTuple)
        {
            foreach (var (p, _) in pairing)
            {
                var altName = GetAlternateDepthName(p.GenericParam.TypeName);
                if (altName != p.GenericParam.TypeName && ContainsGenericParam(returnTypeSpec, altName)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a TypeSpec contains references to a generic parameter anywhere in its tree.
    /// Used to detect complex return types like Container&lt;T&gt; that can't be trivially substituted.
    /// </summary>
    private static bool ContainsGenericParam(TypeSpec typeSpec, string genericParamName)
    {
        if (typeSpec is AssociatedTypeReferenceSpec assocRef)
        {
            // e.g., τ_0_0.SerializedObject — base type references the generic param
            return assocRef.BaseType == genericParamName;
        }
        else if (typeSpec is NamedTypeSpec named)
        {
            // Match exact name (τ_0_0) and associated type references (τ_0_0.SerializedObject)
            if (named.Name == genericParamName || named.Name.StartsWith(genericParamName + ".")) return true;
            return named.GenericParameters.Any(gp => ContainsGenericParam(gp, genericParamName));
        }
        else if (typeSpec is TupleTypeSpec tuple)
        {
            return tuple.Elements.Any(e => ContainsGenericParam(e, genericParamName));
        }
        else if (typeSpec is ClosureTypeSpec closure)
        {
            return ContainsGenericParam(closure.ReturnType, genericParamName) ||
                   ContainsGenericParam(closure.Arguments, genericParamName);
        }
        // Check generic parameters on any TypeSpec (they're defined on the base class)
        return typeSpec.GenericParameters.Any(gp => ContainsGenericParam(gp, genericParamName));
    }

    /// <summary>
    /// Builds a key representing the C# method signature (name + parameter types)
    /// to prevent emitting duplicate overloads.
    /// </summary>
    /// Builds the same signature key format as <see cref="BuildCSharpSignatureKey"/> but
    /// for non-generic methods already emitted by the main method pipeline. Used to pre-seed
    /// the dedup set so CSM overloads don't collide with hand-written overloads.
    private static string BuildCSharpSignatureKeyForNonGeneric(
        string methodName,
        MethodDecl method,
        ITypeDatabase typeDatabase)
    {
        var parts = new List<string> { methodName };
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;
            if (arg.HasDefaultArg) continue;
            parts.Add(ResolvePublicCSharpType(arg.SwiftTypeSpec, typeDatabase));
        }
        return string.Join("|", parts);
    }

    private static string BuildCSharpSignatureKey(
        string methodName,
        MethodDecl method,
        (ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)[] pairing,
        ITypeDatabase typeDatabase)
    {
        var parts = new List<string> { methodName };

        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;
            if (arg.HasDefaultArg) continue;

            if (TryMatchGenericParam(arg.SwiftTypeSpec, pairing, out _, out var matchedConformer))
            {
                parts.Add(matchedConformer!.CSharpType);
            }
            else
            {
                parts.Add(ResolvePublicCSharpType(arg.SwiftTypeSpec, typeDatabase));
            }
        }
        return string.Join("|", parts);
    }

    /// <summary>
    /// Checks if a TypeSpec contains any unresolved generic parameter (τ_X_Y pattern)
    /// or associated type reference anywhere in its tree.
    /// </summary>
    private static bool ContainsAnyGenericParam(TypeSpec typeSpec)
    {
        if (typeSpec is AssociatedTypeReferenceSpec)
            return true;
        if (typeSpec is NamedTypeSpec named)
        {
            if (named.Name.StartsWith("τ_")) return true;
            return named.GenericParameters.Any(ContainsAnyGenericParam);
        }
        else if (typeSpec is TupleTypeSpec tuple)
        {
            return tuple.Elements.Any(ContainsAnyGenericParam);
        }
        else if (typeSpec is ClosureTypeSpec closure)
        {
            return ContainsAnyGenericParam(closure.ReturnType) ||
                   ContainsAnyGenericParam(closure.Arguments);
        }
        return typeSpec.GenericParameters.Any(ContainsAnyGenericParam);
    }

    private static string ResolvePublicCSharpType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is NamedTypeSpec named)
        {
            try
            {
                var typeName = SwiftTypeName.FromModuleQualifiedName(named.Name);
                if (typeDatabase.TryGetTypeRecord(typeName, out var record))
                    return record.CSharpTypeName.FullyQualifiedName;
            }
            catch (ArgumentException) { }
            return named.Name.Split('.').Last();
        }
        return "IntPtr";
    }

    private static string SanitizeTypeName(string name)
    {
        return name.Replace(".", "_").Replace("<", "_").Replace(">", "")
                   .Replace(",", "_").Replace(" ", "").Replace("[", "Arr_").Replace("]", "");
    }

    // ─── Concrete parent-type name builders (generic-parent CSM path) ────

    /// <summary>
    /// Returns the Swift-side module-qualified type name of the parent, with any
    /// IsParentGeneric entries in <paramref name="pairing"/> substituted for their
    /// chosen conformer. Non-generic parents (or pairings without parent-generic
    /// entries) return the unmodified module-qualified name.
    /// </summary>
    private static string BuildConcreteParentSwiftName(
        TypeDecl parentTypeDecl,
        (ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)[] pairing)
    {
        var baseName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        var parentEntries = pairing.Where(p => p.Param.IsParentGeneric).ToList();
        if (parentEntries.Count == 0) return baseName;

        var args = parentEntries.Select(p =>
            p.Conformer.SwiftLiteral ?? p.Conformer.SwiftQualifiedName);
        return $"{baseName}<{string.Join(", ", args)}>";
    }

    /// <summary>
    /// Returns the C#-side type name of the parent closed over its IsParentGeneric
    /// conformers (e.g. <c>GenericContainer&lt;SongItem&gt;</c>). Used both as the
    /// extension-method receiver type and as the constructor return type.
    /// </summary>
    private static string BuildConcreteParentCsharpName(
        TypeDecl parentTypeDecl,
        (ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)[] pairing,
        ITypeDatabase typeDatabase)
    {
        var parentEntries = pairing.Where(p => p.Param.IsParentGeneric).ToList();
        if (parentEntries.Count == 0) return parentTypeDecl.Name;

        var args = parentEntries.Select(p => p.Conformer.CSharpType);
        return $"{parentTypeDecl.Name}<{string.Join(", ", args)}>";
    }

    /// <summary>
    /// Emits concrete C# overloads for specializable methods on a generic parent type
    /// (e.g. <c>GenericContainer&lt;T: SearchableItem&gt;.append&lt;D: DataProtocol&gt;</c>).
    ///
    /// These can't live inside the parent's class body — the receiver must be a closed
    /// generic (e.g. <c>GenericContainer&lt;SongItem&gt;</c>) and an in-body static method
    /// can't name a closed form of its own surrounding generic type. Instead, each parent-
    /// conformer tuple produces a <c>{ParentName}{ParentConformerNames}CsmExtensions</c>
    /// static partial class that holds extension methods keyed on that closed receiver.
    ///
    /// Must be called AFTER the parent type's class body is closed so the extension class
    /// sits alongside it at the namespace level.
    /// </summary>
    public static void EmitConcreteSpecializationsForGenericParent(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        TypeDecl typeDecl,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        ConcreteSpecializationEngine engine,
        ILogger logger)
    {
        if (!typeDecl.IsGeneric) return;

        // Nested generic types emit their C# body inside an enclosing class. Roslyn rejects
        // nested `extension` containers (CS1109) and the extension-class-on-closed-receiver
        // pattern can't name a nested parent with its closed generic args from outside its
        // enclosing type. Punt on nested generic parents; the open-generic emission remains
        // the only surface for these methods.
        if (typeDecl.ParentDecl is TypeDecl) return;

        var specializableMethods = engine.FindSpecializableMethods(typeDecl);
        if (specializableMethods.Count == 0) return;

        // Filter to specs whose parent generics resolved (ResolveParentSpecializableParams
        // returned non-null → all-or-nothing). The engine flags these with IsParentGeneric.
        var parentGenericSpecs = specializableMethods
            .Where(s => s.SpecializableParams.Any(p => p.IsParentGeneric))
            .ToList();
        if (parentGenericSpecs.Count == 0) return;

        if (!WrapperValidation.IsXCFrameworkMode(typeDatabase)) return;

        var moduleName = typeDecl.SwiftTypeName.Module;
        var wrapperLibPath = typeDatabase.AsyncLibraryName ?? "libSwiftBindings";

        // All specs share the same parent-generic param shape (same typeDecl → same
        // generic parameters with the same resolved conformers). Derive the parent-
        // generic param list from the first spec so we can enumerate tuples once.
        var parentParams = parentGenericSpecs[0].SpecializableParams
            .TakeWhile(p => p.IsParentGeneric)
            .ToList();
        if (parentParams.Count == 0) return;

        var parentTupleCount = ComputePairingCount(parentParams);
        if (parentTupleCount > MaxCsmCartesianProductSize)
        {
            logger.LogDebug(
                "CSM: Skipping {Type} generic-parent specializations — parent-conformer tuples ({Count}) exceed cap ({Cap}).",
                typeDecl.Name, parentTupleCount, MaxCsmCartesianProductSize);
            return;
        }

        foreach (var parentTuple in CartesianPairings(parentParams))
        {
            if (!ConformerPairingSatisfiesCoupling(parentTuple)) continue;

            var parentConformerNames = string.Concat(
                parentTuple.Select(p => SanitizeTypeName(p.Conformer.CSharpType)));
            var extClassName = $"{typeDecl.Name}{parentConformerNames}CsmExtensions";

            // Open the extension wrapper class before emitting overloads so the staged
            // TryEmitConcreteOverload output (public static methods + P/Invokes) lands
            // inside the class body. Empty classes are harmless if every overload is
            // filtered out, but most real groups emit at least one overload.
            csWriter.WriteLine();
            csWriter.WriteLine($"public static unsafe partial class {extClassName}");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            var emittedSignatures = new HashSet<string>(StringComparer.Ordinal);

            foreach (var spec in parentGenericSpecs)
            {
                var method = spec.Method;
                if (method.IsAccessor) continue;
                if (!method.IsAsync && method.Throws) continue;
                if (method.IsAsync) continue; // Async CSM path does not yet support generic parents.

                var methodParams = spec.SpecializableParams
                    .Where(p => !p.IsParentGeneric)
                    .ToList();

                if (methodParams.Count == 0)
                {
                    // No method-generic params: emit one overload per parent tuple.
                    TryEmitConcreteOverload(
                        csWriter, swiftWriter, method, typeDecl, parentTuple,
                        moduleName, wrapperLibPath, typeDatabase, emissionContext,
                        emittedSignatures, logger, isExtension: true);
                    continue;
                }

                var methodPairingCount = ComputePairingCount(methodParams);
                if (methodPairingCount > MaxCsmCartesianProductSize)
                {
                    logger.LogDebug(
                        "CSM: Skipping {Method} generic-parent — method-conformer tuples ({Count}) exceed cap ({Cap}).",
                        method.Name, methodPairingCount, MaxCsmCartesianProductSize);
                    continue;
                }

                foreach (var methodPairing in CartesianPairings(methodParams))
                {
                    var fullPairing = new (ConcreteSpecializationEngine.SpecializableParam, ConcreteSpecializationEngine.ConcreteConformer)[parentTuple.Length + methodPairing.Length];
                    Array.Copy(parentTuple, 0, fullPairing, 0, parentTuple.Length);
                    Array.Copy(methodPairing, 0, fullPairing, parentTuple.Length, methodPairing.Length);

                    if (!ConformerPairingSatisfiesCoupling(fullPairing))
                    {
                        logger.LogDebug(
                            "CSM: Skipping {Method} generic-parent pairing — cross-param coupling not satisfied.",
                            method.Name);
                        continue;
                    }

                    TryEmitConcreteOverload(
                        csWriter, swiftWriter, method, typeDecl, fullPairing,
                        moduleName, wrapperLibPath, typeDatabase, emissionContext,
                        emittedSignatures, logger, isExtension: true);
                }
            }

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }
    }
}
