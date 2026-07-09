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

            // Throwing constructors flow through the same CanEmitConcreteOverloadForPairing /
            // ConstructorAdmissibility preflight as non-throwing ones — `throws` and `IsConstructor`
            // are orthogonal dimensions here. The Swift wrapper composes them (do/catch + errorOut
            // around a `try ParentType(...)` call, class `Unmanaged.passRetained` vs struct
            // `resultPtr.initializeMemory`), and the preflight already rejects the genuinely
            // unsafe inits (internal/unavailable, `_const`, unrepresentable parent pins). This is
            // the CryptoKit HPKE Sender/Recipient shape — every HPKE init is `init<…>(…) throws`.

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
        // AF13: the disambiguating hash base is the symbol the method's *own* main emission
        // settled on (constructor-wrapper promotion etc.), recovered from the emission-scoped
        // side table the base handler populated when it emitted this type's methods. Historically
        // this read `method.MangledName` after that emission mutated it in place; the side table
        // preserves the exact promoted (or, when un-promoted, silgen) value without mutation.
        var hashInput = emissionContext.GetMethodEmissionSymbolOrMangled(method)
            + string.Concat(pairing.Select(p => "|" + p.Conformer.SwiftQualifiedName));
        var mangledHash = EmitterUtility.DeterministicHash8(hashInput);
        var cdeclSymbol = $"SBW_CSM_{moduleName}_{parentTypeDecl.Name}_{safeConformerName}_{methodName}_{mangledHash}";

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

        // C# signature dedup runs BEFORE registry registration so a duplicate
        // visible signature does not leak its cdeclSymbol into ModuleEmissionContext;
        // the wrapper-symbol contract gate must only see symbols whose Swift wrapper
        // actually emitted.
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

        // Registry guard — catches different pairings producing the same cdeclSymbol.
        // The `SBW_CSM_` prefix is a dedicated namespace for per-conformer specialization
        // wrappers; no other emitter produces an `SBW_CSM_` symbol. Per-kind method
        // bucket is collision-safe.
        if (!emissionContext.TryAddMethodWrapperSymbol(cdeclSymbol))
            return false;

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
            cdeclSymbol, moduleName, wrapperLibPath, isConstructor, isStatic, isClass,
            isVoidReturn, isStringReturn, returnsGenericParam, typeDatabase,
            emissionContext, mergedAvailability, isExtension);

        // Wire DefaultParameterOverloadEmitter so per-conformer specialized methods get
        // trim-overload shortcuts for trailing default params (the StoreKit
        // `purchase(confirmIn:options: = [])` shape, but on the sync side). The CSM-sync
        // primary above already auto-fills all trailing defaults via Swift, so its public
        // surface matches the maximally-trimmed trim variant. Pre-populate the projected
        // signature set with the auto-trim primary's key to suppress the duplicate; less-
        // trimmed variants (one default exposed, two defaults exposed, …) emit cleanly
        // because their signatures are strictly longer than the auto-trim primary.
        // Constructors are out of scope for this wiring — sync CSM constructors take a
        // bespoke `From{Conformer}(…)` factory shape that the standard overload emitter
        // doesn't model. Generic constructors with collection-typed defaults (e.g.
        // `init<S: P>(..., options: Set<Int> = [], tag: Int = 1)`) therefore only get the
        // CSM factory shape that auto-fills every default; intermediate factory overloads
        // exposing `options` while letting Swift fill `tag` are not currently emitted.
        // Tracked alongside option (a) in the gap-doc.
        //
        // When a method combines a non-mappable trailing default with a Swift
        // compiler-injected debug param (#file, #line, #column, #function):
        // BuildOverloadDecl removes raw trailing args while CountTrailingDefaults skips
        // debugs, so the auto-trim seed key could target the wrong shape. Empirical
        // verification on a purpose-built fixture (DefaultedHasherWithFile, see
        // DefaultedTrimOverloadWithFileTests) showed this is not reachable: the parser
        // strips trailing debug defaults from CSSignature before the emitter sees them,
        // so the two helpers agree on the arg set. The IsDebugParameter skip in
        // CountTrailingDefaults is harmless under that invariant; if a future parser
        // change started passing debug args through, CountTrailingDefaults and
        // BuildOverloadDecl would disagree at this seeding site, and the fix would be
        // to drop `trailingDefaults + trailingDebugCount` raw args when computing the
        // auto-trim seed key.
        if (!isConstructor)
        {
            EmitTrimOverloadsForCsmSync(
                csWriter, swiftWriter, method, parentTypeDecl, pairing,
                cdeclSymbol, typeDatabase, emissionContext, mergedAvailability, logger);
        }

        logger.LogInformation(
            "Emitted concrete specialization: {Type}.{Method}<{Pairing}>",
            parentTypeDecl.Name, method.Name, safeConformerName);

        return true;
    }

    /// <summary>
    /// Emit trim overloads on top of a CSM-sync per-conformer specialized method.
    /// Substitutes the pairing's generic params into the original method's CSSignature
    /// (mirroring CSM-async <c>TryBuildEmissionPlan</c>), clears the GenericParameters
    /// list so the trim emitter's <c>methodDecl.IsGeneric</c> bail no longer fires, and
    /// pre-populates the projected-signature dedup set with the CSM-sync primary's key
    /// so the most-trimmed trim variant doesn't produce a CS0111 duplicate.
    /// </summary>
    private static void EmitTrimOverloadsForCsmSync(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodDecl method,
        TypeDecl parentTypeDecl,
        (ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)[] pairing,
        string cdeclSymbol,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        IReadOnlyList<AvailabilityAnnotation>? mergedAvailability,
        ILogger logger)
    {
        // Build conformer TypeSpecs for substitution. Mirrors IsEmittableAsyncPairing's
        // preparation step in the async path.
        var conformerTypeSpecs = new NamedTypeSpec[pairing.Length];
        for (int i = 0; i < pairing.Length; i++)
        {
            if (!TryBuildConformerTypeSpec(pairing[i].Conformer, out conformerTypeSpecs[i]))
            {
                logger.LogDebug(
                    "CSM-sync trim: Skipping {Method} — could not build conformer TypeSpec for {Conformer}.",
                    method.Name, pairing[i].Conformer.SwiftQualifiedName);
                return;
            }
        }

        // Substitute generic params sequentially, same approach as async TryBuildEmissionPlan.
        // Bail on any unresolved associated-type reference so we never emit a trim shim
        // whose Swift body would reference a placeholder type.
        var substitutedSignature = new List<ArgumentDecl>(method.CSSignature);
        for (int i = 0; i < pairing.Length; i++)
        {
            var genericName = pairing[i].Param.GenericParam.TypeName;
            var altGenericName = GetAlternateDepthName(genericName);
            bool substitutionOk = true;

            for (int j = 0; j < substitutedSignature.Count; j++)
            {
                var arg = substitutedSignature[j];
                var substituted = SubstituteTypeSpec(
                    arg.SwiftTypeSpec, genericName, altGenericName,
                    conformerTypeSpecs[i], pairing[i].Conformer, ref substitutionOk);
                if (!substitutionOk)
                {
                    logger.LogDebug(
                        "CSM-sync trim: Skipping {Method}+{Conformer} — substitution failed (unresolved associated type).",
                        method.Name, pairing[i].Conformer.SwiftQualifiedName);
                    return;
                }

                substitutedSignature[j] = arg with
                {
                    SwiftTypeSpec = substituted,
                    IsGeneric = arg.IsGeneric && ReferenceEquals(substituted, arg.SwiftTypeSpec) ? arg.IsGeneric : false,
                };
            }
        }

        // Synthesize a non-generic MethodDecl with substituted CSSignature. The trim
        // emitter checks methodDecl.IsGeneric (==> GenericParameters.Count > 0) and
        // bails on generic methods, so clearing the list re-enables emission. Default
        // arg flags ride through unchanged on each ArgumentDecl.
        //
        // MangledName is set to the per-conformer cdeclSymbol so DefaultParameterOverloadEmitter
        // .BuildWrapperSymbol's DeterministicHash8(methodDecl.MangledName) produces a unique
        // DBW_ symbol per conformer pairing — without this, sibling conformer iterations
        // would all hash the original generic method's mangled name and collide on the
        // same `DBW_{TypeName}_{MethodName}_{hash}_{trim}` symbol in the wrapper dylib.
        var synthesized = method with
        {
            MangledName = cdeclSymbol,
            GenericParameters = new List<GenericArgumentDecl>(),
            CSSignature = substitutedSignature,
            RawGenericSig = null,
            AvailabilityAnnotations = mergedAvailability is null
                ? null
                : new List<AvailabilityAnnotation>(mergedAvailability),
            WasEmitted = false,
        };

        var trailingDefaults = DefaultParameterOverloadEmitter.CountTrailingDefaults(synthesized);
        if (trailingDefaults == 0)
            return; // No trailing defaults → nothing for the trim emitter to do.

        var trimEnv = new MethodEnvironment(synthesized, typeDatabase);
        trimEnv.EmissionContext = emissionContext;

        // Pre-populate the projected-signature set with the auto-trim primary's key.
        // The CSM-sync primary above (EmitCSharpMethod) emits a public surface that
        // skips every HasDefaultArg arg, which is exactly the shape of the maximally-
        // trimmed trim variant (trim == trailingDefaults). Without this seed, the trim
        // emitter would happily emit a duplicate method and Roslyn would reject the
        // generated source with CS0111.
        var autoTrimPrimaryDecl = DefaultParameterOverloadEmitter.BuildOverloadDecl(synthesized, trailingDefaults);
        var autoTrimPrimaryKey = DefaultParameterOverloadEmitter.GetProjectedOverloadKey(autoTrimPrimaryDecl, typeDatabase);
        trimEnv.EmittedProjectedSignatures = new HashSet<string>(StringComparer.Ordinal) { autoTrimPrimaryKey };

        DefaultParameterOverloadEmitter.TryEmitOverloads(
            csWriter, swiftWriter, trimEnv, logger, emissionContext);
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

    /// <summary>
    /// Substitutes Swift <c>Self</c> (→ the closed parent type) AND every pairing generic
    /// (→ its chosen conformer) in <paramref name="typeSpec"/>. The ABI carries <c>Self</c>
    /// literally for protocol/class methods whose signature references the dynamic receiver
    /// (e.g. <c>func enumerated() -> EnumeratedCursor&lt;Self&gt;</c>); when the specialization
    /// is closed over a concrete receiver, <c>Self</c> is known and resolves to it, so
    /// <c>EnumeratedCursor&lt;Self&gt;</c> on receiver <c>RecordCursor&lt;ColumnInfo&gt;</c>
    /// renders as <c>EnumeratedCursor&lt;RecordCursor&lt;ColumnInfo&gt;&gt;</c>. Self is resolved
    /// first so the pairing pass still sees a structured tree. Used at both the C# public-signature
    /// render and the Swift <c>@_cdecl</c> wrapper render so the two stay ABI-aligned.
    /// </summary>
    internal static TypeSpec SubstituteSelfAndPairingGenericsInTypeSpec(
        TypeSpec typeSpec,
        TypeDecl parentTypeDecl,
        (ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)[] pairing)
    {
        var current = typeSpec;
        if (BuildClosedParentTypeSpec(parentTypeDecl, pairing) is { } closedParent)
            current = SubstituteSelfInTypeSpec(current, closedParent);
        return SubstitutePairingGenericsInTypeSpec(current, pairing);
    }

    /// <summary>
    /// Builds the closed parent type as a <see cref="NamedTypeSpec"/> — the concrete receiver
    /// the specialization is emitted on (e.g. <c>GenericCursor&lt;Module.ColumnType&gt;</c>, or the
    /// bare parent for a non-generic receiver). Used to resolve Swift <c>Self</c> and to validate
    /// the receiver against the parent's C# generic constraints. Returns null when a parent-generic
    /// conformer can't be rendered as a TypeSpec, so callers fall back to their prior behavior.
    /// </summary>
    private static NamedTypeSpec? BuildClosedParentTypeSpec(
        TypeDecl parentTypeDecl,
        (ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)[] pairing)
    {
        var spec = new NamedTypeSpec(parentTypeDecl.SwiftTypeName.ModuleQualifiedName);
        foreach (var (_, conformer) in pairing.Where(p => p.Param.IsParentGeneric))
        {
            if (!TryBuildConformerTypeSpec(conformer, out var conformerSpec))
                return null;
            spec.GenericParameters.Add(conformerSpec);
        }
        return spec;
    }

    /// <summary>
    /// Replaces literal Swift <c>Self</c> references in <paramref name="typeSpec"/> with
    /// <paramref name="closedParentSpec"/>. Recurses into generic args, tuple elements, and
    /// closure arg/return positions. Mirrors the <see cref="SubstituteTypeSpec"/> generic-param
    /// substitution but keyed on the dynamic-Self name rather than a pairing generic.
    ///
    /// Also closes a bare reference to the parent's own generic nominal type. A protocol-
    /// extension requirement returning <c>Self</c> (e.g. <c>AnimationDefinition.repeated() -&gt;
    /// Self</c>) is carried in the ABI as the unbound conformer name (<c>FromToByAnimation</c>,
    /// no <c>&lt;Value&gt;</c>) once <c>Self</c> is resolved to the conformer. Swift's type
    /// checker infers the arguments from the call's RHS, but C# requires them explicitly, so an
    /// unclosed reference becomes CS0305. When the name matches the closed parent's nominal and
    /// carries no arguments while the parent is generic, substitute the closed parent.
    /// </summary>
    private static TypeSpec SubstituteSelfInTypeSpec(TypeSpec typeSpec, NamedTypeSpec closedParentSpec)
    {
        switch (typeSpec)
        {
            case NamedTypeSpec named:
                if (named.Name == "Self" || named.Name.EndsWith(".Self"))
                    return CloneNamedTypeSpec(closedParentSpec);
                if (named.Name == closedParentSpec.Name &&
                    named.GenericParameters.Count == 0 &&
                    closedParentSpec.GenericParameters.Count > 0)
                    return CloneNamedTypeSpec(closedParentSpec);
                if (named.GenericParameters.Count == 0)
                    return typeSpec;
                var newNamed = new NamedTypeSpec(named.Name);
                CopyTypeSpecProps(named, newNamed);
                foreach (var gp in named.GenericParameters)
                    newNamed.GenericParameters.Add(SubstituteSelfInTypeSpec(gp, closedParentSpec));
                return newNamed;

            case TupleTypeSpec tuple:
                var newElements = new List<TypeSpec>(tuple.Elements.Count);
                foreach (var e in tuple.Elements)
                    newElements.Add(SubstituteSelfInTypeSpec(e, closedParentSpec));
                var newTuple = new TupleTypeSpec(newElements);
                CopyTypeSpecProps(tuple, newTuple);
                return newTuple;

            case ClosureTypeSpec closure:
                var newClosure = new ClosureTypeSpec(
                    SubstituteSelfInTypeSpec(closure.Arguments, closedParentSpec),
                    SubstituteSelfInTypeSpec(closure.ReturnType, closedParentSpec))
                {
                    Throws = closure.Throws,
                    IsAsync = closure.IsAsync,
                };
                CopyTypeSpecProps(closure, newClosure);
                return newClosure;

            default:
                return typeSpec;
        }
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

        // Result pointer for indirect returns. Cache the mapping so the sentinel-return
        // path for throws can consult it without re-classifying.
        bool needsResultPtr = false;
        CdeclReturnMapping? directReturnMapping = null;
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
                directReturnMapping = mapping;
            }
        }

        // Struct constructors return via result pointer (class constructors use Unmanaged)
        if (isConstructor && !isClass)
            needsResultPtr = true;

        if (needsResultPtr || isStringReturn)
            swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");

        // Regular parameters. Every param's internal binding is hand-emitted as `_<label>` (NOT
        // routed through Map), so escape that form here: a param internally named `_self` yields
        // `__self`, duplicating the receiver body local `let __self` → swiftc rejects + silently
        // drops the wrapper. `__self`/`_self` are reserved so the escape resolves the clash;
        // siblings cover a binding that collides with another user param.
        var siblings = CdeclParamMapper.CollectSiblingBindingNames(method.CSSignature.Skip(1));
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;

            var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            // Escaped `_`-prefixed binding used for the @_cdecl param, the forwarding call arg, and
            // any derived (Len/Utf8Ptr/Utf8Len) tokens below. "_" + label avoids the literal token
            // so the replace below leaves this line intact.
            var b = NameProvider.EscapeReservedSwiftWrapperLabel("_" + label, siblings);
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
                        swiftParams.Add($"_ {b}: UnsafeMutableRawPointer");
                        callArgs.Add($"{argLabel}unsafeBitCast(OpaquePointer({b}), to: {concreteSwiftType}.self)");
                        break;
                    case ConformerCategory.RawBuffer:
                        // byte[] / [UInt8]: receive (ptr, length), reconstruct as Foundation.Data
                        // zero-copy. The C# side pins via fixed(byte*) for the duration of the
                        // @_cdecl call, so .none deallocator is safe (Swift never outlives the
                        // pin — this is a synchronous call). Swift infers D = Foundation.Data
                        // at the call site regardless of the conformer's nominal [UInt8] identity,
                        // which is fine: both [UInt8] and Data conform to DataProtocol.
                        swiftParams.Add($"_ {b}: UnsafeRawPointer");
                        swiftParams.Add($"_ {b}Len: Int");
                        callArgs.Add($"{argLabel}Data(bytesNoCopy: UnsafeMutableRawPointer(mutating: {b}), count: {b}Len, deallocator: .none)");
                        break;
                    case ConformerCategory.InlineSwiftStruct:
                        // Foundation.Data (and future allowlisted value structs): the C# side
                        // pins &data via fixed(Data*) and passes (IntPtr)p. Swift loads via
                        // assumingMemoryBound+pointee, same shape as NonFrozenStruct.
                        swiftParams.Add($"_ {b}: UnsafeRawPointer");
                        callArgs.Add($"{argLabel}{b}.assumingMemoryBound(to: {concreteSwiftType}.self).pointee");
                        break;
                    default:
                        // Frozen and non-frozen structs: pass as pointer, load value.
                        // Even frozen structs use pointer indirection because their C# binding
                        // is a class with SafeHandle, not a blittable C# struct.
                        swiftParams.Add($"_ {b}: UnsafeRawPointer");
                        callArgs.Add($"{argLabel}{b}.assumingMemoryBound(to: {concreteSwiftType}.self).pointee");
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
                        swiftParams.Add($"_ {b}: {ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec)}");
                        callArgs.Add($"{argLabel}{b}");
                        break;
                    case MethodClosureBridge.ParamAbiCategory.ObjCHandle:
                    case MethodClosureBridge.ParamAbiCategory.PayloadHandle:
                        swiftParams.Add($"_ {b}: UnsafeRawPointer");
                        var swiftTypeName = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
                        // PayloadHandle covers both Swift classes and non-frozen structs.
                        // Discriminate: Swift classes (and ObjC-bridged) — the IntPtr IS the
                        // object reference, so unsafeBitCast(OpaquePointer) recovers the class.
                        // Non-frozen structs — the IntPtr points to a flat value-witness-table
                        // layout buffer (NativeMemory.Alloc(_payloadSize) on the C# side), so
                        // .assumingMemoryBound(to:).pointee is the correct VWT-aware load.
                        // Using unsafeBitCast on a non-frozen struct param reinterprets the
                        // pointer's bits as the struct content and corrupts every property
                        // read (e.g., CryptoKit's SymmetricKey would surface as AES.GCM.seal
                        // throwing incorrectKeySize even though the same handle reads
                        // bitCount correctly through the property-getter path).
                        bool argIsClass = arg.SwiftTypeSpec is NamedTypeSpec namedArg
                            && (abiCategory == MethodClosureBridge.ParamAbiCategory.ObjCHandle
                                || MethodClosureBridge.IsClassTypeForSwift(namedArg, typeDatabase));
                        if (argIsClass)
                            callArgs.Add($"{argLabel}unsafeBitCast(OpaquePointer({b}), to: {swiftTypeName}.self)");
                        else
                            callArgs.Add($"{argLabel}{b}.assumingMemoryBound(to: {swiftTypeName}.self).pointee");
                        break;
                    case MethodClosureBridge.ParamAbiCategory.Utf8Slice:
                        // Swift.String passes as (UTF-8 byte pointer, length) pair — mirrors
                        // MethodClosureBridge.cs ~333-338 / ~404-409. The reconstruction
                        // expression is a single Swift literal so we inline it directly into
                        // the call site rather than threading a prelude `let {name}Val` line
                        // (the param is consumed exactly once in CSM wrapper bodies).
                        swiftParams.Add($"_ {b}Utf8Ptr: UnsafePointer<UInt8>");
                        swiftParams.Add($"_ {b}Utf8Len: Int");
                        callArgs.Add($"{argLabel}String(bytes: UnsafeBufferPointer(start: {b}Utf8Ptr, count: {b}Utf8Len), encoding: .utf8)!");
                        break;
                    case MethodClosureBridge.ParamAbiCategory.KeyPathFamily:
                    {
                        // Swift KeyPath family — param crosses @_cdecl as UnsafeRawPointer
                        // because @_cdecl rejects KeyPath<R,V> directly. Reconstruct via
                        // takeUnretainedValue (no retain consumed): C# passes @guaranteed
                        // with the SafeHandle kept alive by DangerousGetHandle across the call.
                        var swiftKpType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(arg.SwiftTypeSpec);
                        swiftParams.Add($"_ {b}: UnsafeRawPointer");
                        callArgs.Add($"{argLabel}Unmanaged<{swiftKpType}>.fromOpaque({b}).takeUnretainedValue()");
                        break;
                    }
                    case MethodClosureBridge.ParamAbiCategory.NativeRemapped
                        when IsConcreteFoundationDataParam(arg):
                        // Foundation.Data: @_cdecl can't take Data by value (it bridges to NSData*),
                        // so the param crosses as the canonical two-Int-word decomposition of the
                        // 16-byte Swift.Foundation.Data struct — mirroring the ordinary cdecl
                        // wrapper path (CdeclParamMapper Foundation.Data arm). unsafeBitCast is a
                        // pure reinterpret with no retain, so it MOVES the +1 the C# side's
                        // FromByteArray created into Swift (which releases it at end of call):
                        // ownership-balanced, no leak. The word tokens carry the escaped binding
                        // (`b` already starts with `_`) so they never collide with a sibling param.
                        swiftParams.Add($"_ _dW0{b}: Int");
                        swiftParams.Add($"_ _dW1{b}: Int");
                        callArgs.Add($"{argLabel}unsafeBitCast((_dW0{b}, _dW1{b}), to: Foundation.Data.self)");
                        break;
                    case MethodClosureBridge.ParamAbiCategory.FrozenStruct
                        when arg.SwiftTypeSpec is NamedTypeSpec frozenParamNamed
                            && typeDatabase.TryGetTypeRecord(frozenParamNamed, out var frozenParamRecord)
                            && ProjectsAsBlittableValueStruct(frozenParamNamed, frozenParamRecord):
                        // Frozen, trivially-copyable struct: the C# side pins &v and passes
                        // (IntPtr)p. Swift loads via assumingMemoryBound+pointee, the same shape as
                        // the InlineSwiftStruct (Data) and non-frozen-struct PayloadHandle arms.
                        swiftParams.Add($"_ {b}: UnsafeRawPointer");
                        callArgs.Add($"{argLabel}{b}.assumingMemoryBound(to: {ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec)}.self).pointee");
                        break;
                    default:
                        swiftParams.Add($"_ {b}: UnsafeRawPointer");
                        callArgs.Add($"{argLabel}{b}");
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

        // errorOut parameter for throwing methods. Goes last, after self_, matching the
        // non-CSM @_cdecl wrapper layout in OptionalPointerWrapperEmitter.EmitCdeclWrapper.
        if (method.Throws)
        {
            swiftParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>");
            // Ensure SBW_GetErrorDescription / SBW_ReleaseError are emitted once per module.
            ErrorDescriptionEmitter.EmitIfNeeded(swiftWriter, moduleName, emissionContext);
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
        else if (method.IsExtensionPropertyGetter)
        {
            // A read-only extension-default property surfaced as a synthetic getter method
            // is READ, not called — `__self.name`, no parens. Emitting `__self.name()` makes
            // swiftc reject the wrapper with "cannot call value of non-function type".
            callExpr = $"{callTarget}.{NameProvider.ParserNameToSwift(method)}";
        }
        else
        {
            callExpr = $"{callTarget}.{NameProvider.ParserNameToSwift(method)}({string.Join(", ", callArgs)})";
        }

        // Return type. For any direct-return shape with a cdecl projection
        // (Bool→Int8, SimpleEnum→rawValueType, Class→UnsafeMutableRawPointer,
        // Optional<Class>→UnsafeMutableRawPointer?, Direct primitive→Swift primitive),
        // always use mapping.CdeclReturnType so the header matches what
        // EmitCdeclDirectReturn writes. The gate is NOT `throws` — a non-throwing CSM
        // method returning a SimpleEnum/Class/Optional<Class> has the same ABI
        // constraint: @_cdecl can't return Swift enums/classes. Without the mapping the
        // Swift compiler silently strips the wrapper and the P/Invoke entry point
        // disappears at runtime. For Direct primitives CdeclReturnType equals
        // RenderSwiftTypeSpec(typeSpec), so this is a no-op widening of the existing
        // behavior.
        bool throws = method.Throws;
        string swiftReturnType;
        if (isConstructor)
            swiftReturnType = isClass ? " -> UnsafeMutableRawPointer" : "";
        else if (isVoidReturn || isStringReturn || needsResultPtr)
            swiftReturnType = "";
        else if (returnsGenericParam)
            swiftReturnType = $" -> {returnConcreteSwiftType}";
        else if (directReturnMapping is not null)
            swiftReturnType = $" -> {directReturnMapping.CdeclReturnType}";
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

        // Throwing methods wrap the entire body in `do { ... } catch { ... }` with an
        // errorOut write on the catch path. Per-return-shape sentinel returns mirror
        // OptionalPointerWrapperEmitter.EmitCdeclSentinelReturn so the generated C#
        // side's `out IntPtr errorPtr` check can discriminate success vs. failure
        // without aliasing against a legitimate success value.
        string bodyIndent = throws ? "        " : "    ";
        string tryPrefix = throws ? "try " : "";
        string callExprWithTry = $"{tryPrefix}{callExpr}";

        if (throws)
            swiftWriter.WriteLine("    do {");

        if (isConstructor)
        {
            if (isClass)
            {
                swiftWriter.WriteLine($"{bodyIndent}let _result = {callExprWithTry}");
                swiftWriter.WriteLine($"{bodyIndent}return Unmanaged.passRetained(_result as AnyObject).toOpaque()");
            }
            else
            {
                // Struct constructor: return via initializeMemory through result pointer
                swiftWriter.WriteLine($"{bodyIndent}let _result = {callExprWithTry}");
                swiftWriter.WriteLine($"{bodyIndent}resultPtr.initializeMemory(as: ({parentSwiftName}).self, repeating: _result, count: 1)");
            }
        }
        else if (isVoidReturn)
        {
            swiftWriter.WriteLine($"{bodyIndent}{callExprWithTry}");
            if (!string.IsNullOrEmpty(selfWriteBack))
                swiftWriter.WriteLine($"{bodyIndent}{selfWriteBack}");
        }
        else if (isStringReturn)
        {
            // Mutating + string return: emit the write-back immediately after `let result = ...`
            // (before the string serializes) so callers observe the mutation. Without this the
            // mutation would live only on the local `var __self` copy.
            OptionalPointerWrapperEmitter.EmitStringReturnBody(
                swiftWriter, callExprWithTry, bodyIndent,
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
                // Return type may CONTAIN pairing generics (e.g. `HashedAuthenticationCode<H>`)
                // or a literal `Self` (e.g. `EnumeratedCursor<Self>`). Substitute each pairing's
                // method-level and parent-level generic param — and `Self` → the closed parent —
                // with the concrete conformer so the rendered type doesn't leak an unresolved `H`
                // or `Self` into `initializeMemory(as:)`. Falls back to the unsubstituted render on
                // failure (matches previous behavior — no regression for shapes we couldn't handle).
                var substitutedReturn = SubstituteSelfAndPairingGenericsInTypeSpec(returnTypeSpec, parentTypeDecl, pairing);
                returnTypeStr = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(substitutedReturn);
            }
            // Explicit type annotation on _result — needed when the callee is a generic
            // method whose return type can only be inferred from the binding site (e.g.
            // a generic `Row.decode<T>(atIndex:)` method). Without this, Swift emits
            // "type of expression is ambiguous" and strips the wrapper.
            swiftWriter.WriteLine($"{bodyIndent}let _result: ({returnTypeStr}) = {callExprWithTry}");
            if (!string.IsNullOrEmpty(selfWriteBack))
                swiftWriter.WriteLine($"{bodyIndent}{selfWriteBack}");
            swiftWriter.WriteLine($"{bodyIndent}resultPtr.initializeMemory(as: ({returnTypeStr}).self, repeating: _result, count: 1)");
        }
        else
        {
            if (!string.IsNullOrEmpty(selfWriteBack))
            {
                swiftWriter.WriteLine($"{bodyIndent}let _result = {callExprWithTry}");
                swiftWriter.WriteLine($"{bodyIndent}{selfWriteBack}");
                // Mutating + directReturnMapping: the @_cdecl header declares the mapped
                // return type (Int8 for Bool, rawValueType for SimpleEnum,
                // UnsafeMutableRawPointer for ClassPointer). Returning raw `_result`
                // would fail Swift type-check and silently strip the @_cdecl symbol.
                // Route `_result` through the same helper the non-writeback branch uses
                // regardless of `throws` — the ABI constraint applies either way.
                if (directReturnMapping is not null)
                {
                    OptionalPointerWrapperEmitter.EmitCdeclDirectReturn(
                        swiftWriter, "_result", returnTypeSpec, typeDatabase,
                        directReturnMapping, bodyIndent);
                }
                else
                {
                    swiftWriter.WriteLine($"{bodyIndent}return _result");
                }
            }
            else
            {
                // Direct @_cdecl return: route through the shared helper so Bool → Int8,
                // SimpleEnum → rawValue, and Class → Unmanaged.passRetained projections
                // stay in lockstep with the non-CSM path. directReturnMapping is non-null
                // here (we populated it earlier for the non-generic-param, non-string,
                // non-void, non-constructor direct-return branch). Applied unconditionally:
                // for CdeclReturnKind.Direct the helper emits a plain `return callExpr`,
                // matching the previous non-throws behavior for primitives.
                if (directReturnMapping is not null)
                {
                    OptionalPointerWrapperEmitter.EmitCdeclDirectReturn(
                        swiftWriter, callExprWithTry, returnTypeSpec, typeDatabase,
                        directReturnMapping, bodyIndent);
                }
                else
                {
                    swiftWriter.WriteLine($"{bodyIndent}return {callExprWithTry}");
                }
            }
        }

        if (throws)
        {
            swiftWriter.WriteLine("    } catch {");
            swiftWriter.WriteLine("        errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()");

            // Sentinel return on the error path, sized to the declared @_cdecl return type.
            // Indirect-result / string / void shapes return `Void`, so no sentinel is needed.
            bool needsSentinel =
                (isConstructor && isClass)
                || (!isConstructor && !isVoidReturn && !isStringReturn && !needsResultPtr);
            if (needsSentinel)
            {
                if (isConstructor && isClass)
                {
                    // Constructor returns UnsafeMutableRawPointer (ClassPointer). Mirror
                    // EmitCdeclSentinelReturn's ClassPointer branch.
                    swiftWriter.WriteLine("        return UnsafeMutableRawPointer(bitPattern: 1)!");
                }
                else
                {
                    OptionalPointerWrapperEmitter.EmitCdeclSentinelReturn(
                        swiftWriter, directReturnMapping, "        ");
                }
            }

            swiftWriter.WriteLine("    }");
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
        string moduleName,
        string wrapperLibPath,
        bool isConstructor,
        bool isStatic,
        bool isClass,
        bool isVoidReturn,
        bool isStringReturn,
        bool returnsGenericParam,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
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

        // The generated C# hardcodes synthetic names in two scopes that a user
        // parameter can collide with: (1) the public method body's locals (resultPtr,
        // _result, errorPtr) — a user param spelling one shadows it (CS0136); and (2) the
        // P/Invoke declaration's own synthetic params (the indirect-return `IntPtr resultPtr`
        // and throwing `out IntPtr errorPtr`), which sit in the SAME parameter list as the
        // forwarded user params — a user param spelling one is a duplicate parameter name
        // (CS0100). Both ship uncompilable C# at exit 0. Reserve the synthetics against the
        // in-scope user identifiers up front (BEFORE the P/Invoke param list is built below)
        // so the same reserved spelling guards body locals AND P/Invoke params; collision-free
        // input keeps the original names verbatim (output is byte-identical to before).
        var reservedUserNames = new List<string>();
        foreach (var preArg in method.CSSignature.Skip(1))
        {
            if (preArg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(preArg)) continue;
            if (preArg.HasDefaultArg) continue;
            reservedUserNames.Add(NameProvider.GetCSharpParameterName(preArg));
        }
        // The extension-method receiver is an explicit `this {ConcreteParent} self` first
        // public param; reserve it too so no synthetic ever collides with it.
        if (isExtension && !isStatic)
            reservedUserNames.Add("self");

        var syntheticScope = new SyntheticNameScope(reservedUserNames);
        string resultPtrName = syntheticScope.Reserve("resultPtr");
        string resultLocalName = syntheticScope.Reserve("_result");
        string errorPtrName = syntheticScope.Reserve("errorPtr");

        // Pins: each entry is a C# fixed-statement "fixed (byte* _pfoo = foo)" that must
        // wrap the pinvoke call. InlineSwiftStruct uses `&param` directly (unmanaged
        // value type — no fixed needed) but still requires an `unsafe` context.
        var fixedStatements = new List<string>();
        bool needsUnsafe = false;
        // Prelude locals emitted BEFORE the fixed-block stack opens — currently used by
        // Utf8Slice (Swift.String) params to allocate `var __{bareName}Utf8 = UTF8.GetBytes(...)`
        // so the matching `fixed (byte* __{bareName}Ptr = __{bareName}Utf8)` pin has a source
        // binding. Mirrors MethodClosureBridge.cs ~1283-1292 / ~1352-1357 verbatim.
        var preludeLocals = new List<string>();

        bool needsResultPtr = false;
        // Captured so the direct-return branch can convert the raw _cdecl value (IntPtr
        // for ClassPointer, raw scalar for SimpleEnum, nullable IntPtr for
        // OptionalClassPointer) back into the projected C# type. Stays null for
        // constructors/void/string/generic-param/indirect-result paths.
        CdeclReturnMapping? directReturnMapping = null;
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
                directReturnMapping = mapping;
            }
        }

        if (needsResultPtr || isStringReturn)
            pinvokeParams.Add($"IntPtr {resultPtrName}");

        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;
            if (arg.HasDefaultArg) continue;

            var csName = NameProvider.GetCSharpParameterName(arg);

            if (TryMatchGenericParam(arg.SwiftTypeSpec, pairing, out _, out var matchedConformerObj))
            {
                var matchedConformer = matchedConformerObj!;
                var conformerCsType = ResolveConformerCSharpTypeRef(matchedConformer, typeDatabase);
                // Generic param → concrete type
                var category = ClassifyConformerForCSharp(matchedConformer, typeDatabase);
                switch (category)
                {
                    case ConformerCategory.Class:
                        publicParams.Add($"{conformerCsType} {csName}");
                        pinvokeParams.Add($"IntPtr {csName}");
                        callArgs.Add($"{csName}.Payload.DangerousGetHandle()");
                        break;
                    case ConformerCategory.RawBuffer:
                        // byte[] / [UInt8]: pin via fixed(byte*), pass (ptr, length).
                        // Swift reconstructs as Data(bytesNoCopy:...,deallocator:.none);
                        // pin lifetime covers the entire @_cdecl call.
                        publicParams.Add($"{conformerCsType} {csName}");
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
                        var csTypeName = InlineSwiftStructAllowlist[matchedConformer.SwiftQualifiedName].CSharpType;
                        publicParams.Add($"{csTypeName} {csName}");
                        pinvokeParams.Add($"IntPtr {csName}");
                        callArgs.Add($"(IntPtr)(&{csName})");
                        needsUnsafe = true;
                        break;
                    default:
                        // Frozen and non-frozen structs: pass via IntPtr.
                        // Even frozen structs are C# classes with SafeHandle, not blittable structs.
                        publicParams.Add($"{conformerCsType} {csName}");
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
                    case MethodClosureBridge.ParamAbiCategory.Utf8Slice:
                    {
                        // Swift.String passed as (UTF-8 byte pointer, length) pair.
                        // Mirrors MethodClosureBridge.cs ~1029-1031 (P/Invoke params),
                        // ~1283-1292 (byte[] prelude), ~1311-1315 (call args), and
                        // ~1352-1357 (fixed-block pin) verbatim so the ABI matches the
                        // Swift @_cdecl wrapper's ptr+len signature emitted above.
                        // `bareName` strips any `@` verbatim prefix so the synthesized
                        // locals are valid C# identifiers.
                        var bareName = NameProvider.StripVerbatimPrefix(csName);
                        publicParams.Add($"string {csName}");
                        pinvokeParams.Add($"IntPtr {csName}Utf8Ptr");
                        pinvokeParams.Add($"nint {csName}Utf8Len");
                        preludeLocals.Add($"var __{bareName}Utf8 = System.Text.Encoding.UTF8.GetBytes({csName});");
                        fixedStatements.Add($"fixed (byte* __{bareName}Ptr = __{bareName}Utf8)");
                        callArgs.Add($"(IntPtr)__{bareName}Ptr");
                        callArgs.Add($"(nint)__{bareName}Utf8.Length");
                        needsUnsafe = true;
                        break;
                    }
                    case MethodClosureBridge.ParamAbiCategory.KeyPathFamily:
                    {
                        // Swift KeyPath family — the C# wrapper IS the SafeHandle, so the
                        // P/Invoke argument is DangerousGetHandle() with no .Payload hop
                        // (Payload returns `this`). Mirrors KeyPathProjection.GetParameterPlan.
                        // The public C# type is built explicitly because the KeyPath family
                        // has no TypeRecord — ResolvePublicCSharpType's fallback would drop
                        // the `Swift.` qualifier.
                        publicParams.Add($"{BuildKeyPathPublicCSharpType((NamedTypeSpec)arg.SwiftTypeSpec, typeDatabase)} {csName}");
                        pinvokeParams.Add($"IntPtr {csName}");
                        callArgs.Add($"{csName}.DangerousGetHandle()");
                        break;
                    }
                    case MethodClosureBridge.ParamAbiCategory.NativeRemapped
                        when IsConcreteFoundationDataParam(arg):
                    {
                        // Foundation.Data: idiomatic public surface is byte[] (matching the
                        // allowlist's IdiomaticPublicType and the Data return projection).
                        // Convert to the inline Swift.Foundation.Data value, then decompose its
                        // 16-byte layout into two nint words — the SAME naming + ownership-moving
                        // shape the ordinary cdecl method/constructor wrappers use (see
                        // WrapperEmitter.Marshalling's ShouldDecomposeDataForCdecl arm:
                        // `{name}_w0`/`{name}_w1` words off a `{name}Swift` holder). FromByteArray's
                        // +1 is carried in the words and released Swift-side via unsafeBitCast.
                        // Unsafe.As/Unsafe.Add are ref-reinterprets, not pointer ops, so no `unsafe`
                        // block. Keeping the word naming identical to the ordinary-cdecl Data path
                        // keeps the two Data-decomposition emitters in sync.
                        var bareName = NameProvider.StripVerbatimPrefix(csName);
                        publicParams.Add($"byte[] {csName}");
                        pinvokeParams.Add($"nint {bareName}_w0");
                        pinvokeParams.Add($"nint {bareName}_w1");
                        preludeLocals.Add($"var {bareName}Swift = global::Swift.Foundation.Data.FromByteArray({csName});");
                        preludeLocals.Add($"nint {bareName}_w0 = System.Runtime.CompilerServices.Unsafe.As<global::Swift.Foundation.Data, nint>(ref {bareName}Swift);");
                        preludeLocals.Add($"nint {bareName}_w1 = System.Runtime.CompilerServices.Unsafe.Add(ref System.Runtime.CompilerServices.Unsafe.As<global::Swift.Foundation.Data, nint>(ref {bareName}Swift), 1);");
                        callArgs.Add($"{bareName}_w0");
                        callArgs.Add($"{bareName}_w1");
                        break;
                    }
                    case MethodClosureBridge.ParamAbiCategory.FrozenStruct
                        when arg.SwiftTypeSpec is NamedTypeSpec frozenParamNamed
                            && typeDatabase.TryGetTypeRecord(frozenParamNamed, out var frozenParamRecord)
                            && ProjectsAsBlittableValueStruct(frozenParamNamed, frozenParamRecord):
                    {
                        // Frozen, trivially-copyable struct projected as a C# value struct
                        // (ISwiftObject): pin-and-pass the bytes via (IntPtr)(&v) inside an unsafe
                        // block -- no `fixed` needed for an unmanaged local. The Swift wrapper reads
                        // it back through assumingMemoryBound(to:).pointee. Same shape as the
                        // InlineSwiftStruct (Foundation.Data) arm, but keyed on the type's own
                        // value-struct projection rather than the allowlist.
                        var frozenCsType = ResolvePublicCSharpType(arg.SwiftTypeSpec, typeDatabase);
                        publicParams.Add($"{frozenCsType} {csName}");
                        pinvokeParams.Add($"IntPtr {csName}");
                        callArgs.Add($"(IntPtr)(&{csName})");
                        needsUnsafe = true;
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

        // Throwing method: append the `out IntPtr errorPtr` parameter last so the P/Invoke
        // matches the Swift wrapper's errorOut position. The param name is reserved
        // (errorPtrName) so a user param literally named `errorPtr` does not duplicate it.
        bool throws = method.Throws;
        if (throws)
        {
            pinvokeParams.Add($"out IntPtr {errorPtrName}");
            callArgs.Add($"out var {errorPtrName}");
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
                csReturnType = ResolveConformerCSharpTypeRef(returnConformer!, typeDatabase);
            else if (isStringReturn)
                csReturnType = "string";
            else
            {
                // Composite return types that carry pairing generics (e.g.
                // `HashedAuthenticationCode<H>` on `HMAC<H>.authenticationCode`) or a literal
                // `Self` (e.g. `EnumeratedCursor<Self>` on `Cursor.enumerated()`) must have
                // those generics — and `Self` → the closed parent — substituted before the C#
                // type is resolved; otherwise an unresolved `H`/`Self` leaks into the public
                // signature and Roslyn reports CS0246. Mirrors the Swift-side substitution used
                // for `initializeMemory(as:)` at the result-pointer path.
                var returnTypeSpec = method.CSSignature.First().SwiftTypeSpec;
                var substitutedReturn = SubstituteSelfAndPairingGenericsInTypeSpec(returnTypeSpec, parentTypeDecl, pairing);
                csReturnType = ResolvePublicCSharpType(substitutedReturn, typeDatabase);
            }
        }

        // Marshal/size type for indirect results defaults to the public return type and
        // diverges only for inline value structs with an idiomatic projection: the public
        // surface becomes the idiomatic type (Foundation.Data -> byte[]) while the wire is
        // sized and marshaled on the ISwiftObject type (Swift.Foundation.Data). This mirrors
        // DataProjection.PublicType / PInvokeType in the main emitter path -- so a concrete
        // overload returning Data presents `byte[]` (a drop-in for the generic stub it
        // shadows) instead of leaking the raw Swift.Foundation.Data value type and losing
        // overload-resolution compatibility with that stub.
        string csReturnMarshalType = csReturnType;
        string returnProjectionSuffix = string.Empty;
        if (!isVoidReturn && !isConstructor && !returnsGenericParam && !isStringReturn)
        {
            var inlineReturnSpec = SubstituteSelfAndPairingGenericsInTypeSpec(
                method.CSSignature.First().SwiftTypeSpec, parentTypeDecl, pairing);
            if (inlineReturnSpec is NamedTypeSpec inlineReturnNamed
                && InlineSwiftStructAllowlist.TryGetValue(inlineReturnNamed.Name, out var inlineReturnInfo)
                && inlineReturnInfo.IdiomaticPublicType is { } idiomaticReturnType)
            {
                csReturnMarshalType = inlineReturnInfo.CSharpType;
                csReturnType = idiomaticReturnType;
                returnProjectionSuffix = inlineReturnInfo.MarshalToPublicSuffix ?? string.Empty;
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
            // Direct primitive return: match the Swift primitive's C# projection (byte,
            // short, int, float, …) so (a) Roslyn accepts the bare `return pinvokeCall`
            // statement without an explicit cast and (b) the P/Invoke honors the actual
            // ABI size rather than always reading an 8-byte IntPtr. Non-primitive direct
            // returns (ObjC handles, payload handles) stay on IntPtr.
            // `Swift.Int` projects to `nint`, which is the same storage as IntPtr — both
            // paths are byte-compatible, but routing through GetPInvokePrimitiveType keeps
            // the signature honest and avoids surprise on 32-bit platforms. Bool keeps its
            // dedicated branch because PInvokeEmitHelper emits `[MarshalAs(U1)]` for bool
            // and the primitive helper would return "bool" without the marshalling hint.
            var returnTypeSpec = method.CSSignature.First().SwiftTypeSpec;
            if (MarshallingHelpers.IsBoolType(returnTypeSpec))
            {
                pinvokeReturn = "bool";
            }
            else if (returnTypeSpec is NamedTypeSpec rn &&
                MarshallingHelpers.IsSwiftPrimitive(rn.Name))
            {
                pinvokeReturn = MethodClosureBridge.GetPInvokePrimitiveType(returnTypeSpec);
            }
            else if (directReturnMapping is not null &&
                directReturnMapping.Kind == CdeclReturnKind.SimpleEnum)
            {
                // SimpleEnum @_cdecl returns the raw value type (Int8/UInt8/.../Int).
                // The P/Invoke signature must match so C# doesn't read past the ABI
                // return slot. Falls back to `int` when the TypeRecord is unavailable.
                pinvokeReturn = typeDatabase.TryGetTypeRecord(returnTypeSpec, out var enumRecord)
                    ? EnumHandler.GetCSharpEnumUnderlyingType(enumRecord.RawValueTypeName)
                    : "int";
            }
            else
            {
                pinvokeReturn = "IntPtr";
            }
        }

        // --- Emit error-helper P/Invokes (SBW_GetErrorDescription / SBW_ReleaseError) ---
        // Emitted once per enclosing C# class. Match the typeKey that
        // WrapperEmitter.Marshalling uses (SwiftTypeName.ModuleQualifiedName) so when an
        // existing generic-throwing fallback already emitted the helpers into the same
        // class body, CSM's second pass treats them as already-emitted and does not
        // produce a duplicate partial that tanks the `LibraryImportGenerator` analyzer.
        // Extension CSM lives in its own per-parent-conformer partial class, so its key
        // must be distinct from the parent type's key.
        if (throws)
        {
            string csTypeKey;
            if (isExtension)
            {
                var parentTupleNames = pairing
                    .Where(p => p.Param.IsParentGeneric)
                    .Select(p => SanitizeTypeName(p.Conformer.CSharpType));
                csTypeKey = $"{parentTypeDecl.Name}{string.Concat(parentTupleNames)}CsmExtensions";
            }
            else
            {
                csTypeKey = parentTypeDecl.SwiftTypeName?.ModuleQualifiedName ?? parentTypeDecl.Name;
            }

            ErrorDescriptionEmitter.EmitCSharpBaseErrorPInvokesIfNeeded(
                csWriter, csTypeKey, moduleName, wrapperLibPath,
                pInvokeHelperContext: null, emissionContext);
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
        AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(
            csWriter, mergedAvailability,
            $"{parentTypeDecl.Name}.{methodName}");

        // Build P/Invoke call
        var pinvokeCallArgs = new List<string>();
        if (needsResultPtr || isStringReturn)
            pinvokeCallArgs.Add(resultPtrName);
        pinvokeCallArgs.AddRange(callArgs);

        string pinvokeCall = $"{cdeclSymbol}({string.Join(", ", pinvokeCallArgs)})";

        // Cleanup discrimination for resultPtr by return-type shape (see
        // WriteNewFromPayloadFrozenStruct / WriteNewFromPayloadNonFrozenStruct):
        //   1. Direct-wrap (non-frozen struct, complex enum): NewFromPayload stores the
        //      wire handle into a SwiftSafeHandle. Ownership transfers — caller MUST NOT
        //      free resultPtr (the SafeHandle's ReleaseHandle calls NativeMemory.Free).
        //      Match the allocator: NativeMemory.Alloc.
        //   2. Copy-out (frozen + RequiresMemoryManagement, IsFrozenStructProjectedAsClass):
        //      NewFromPayload allocates a fresh buffer and InitializeWithCopy's into it.
        //      The wire buffer is independent and still holds +1 retains on internal refs.
        //      Caller MUST run VWT Destroy on the wire then Free it.
        //   3. Pure value (frozen, no RequiresMemoryManagement): NewFromPayload does
        //      `*(T*)handle` — a byte copy with no retain semantics. Caller frees the wire.
        //   4. Class conformer (returnsGenericParam only — class parents take no resultPtr):
        //      Swift writes the class pointer into resultPtr via `initializeMemory` (the carrier
        //      owns the +1). C# reads the slot's contents via MarshalOwnedClassFromSlot and adopts
        //      that +1, then raw-frees the one-word carrier below. Keeps the alloc-then-free shape
        //      (a class carrier has no internal refs to VWT-Destroy); only the marshal call differs
        //      from the pure-value path (was wrapping the carrier address as the
        //      instance pointer → use-after-free on the carrier + leak of the real instance).
        // Class constructors take no resultPtr (direct UnsafeMutableRawPointer return) and
        // are excluded. String returns copy out via ReadUtf8Slice.
        //
        // Soundness: when a conformer/parent's TypeRecord can't be resolved, fall back to
        // the `ClassifyConformerForCSharp` category — same source preflight uses to admit
        // the pairing. Returning to a "silently default to direct-wrap" path would leak
        // frozen-with-memory conformers via the wire's +1 retains.
        bool returnTypeIsDirectWrap = false;
        bool returnTypeNeedsWireDestroy = false;
        // Class conformer carrier (returnsGenericParam only): the Swift wrapper stores the
        // instance pointer INTO resultPtr via initializeMemory, so the carrier owns the +1 and
        // C# must read the slot's contents (MarshalOwnedClassFromSlot), not wrap the carrier
        // address. Keeps the alloc+raw-free shape (a class carrier is one word, no internal
        // refs to VWT-Destroy); only the marshal call differs.
        bool returnTypeIsClassCarrier = false;
        if (needsResultPtr)
        {
            TypeRecord? returnRecord = null;
            if (isConstructor && parentTypeDecl.SwiftTypeName != null)
            {
                typeDatabase.TryGetTypeRecord(parentTypeDecl.SwiftTypeName, out returnRecord);
            }
            else if (returnsGenericParam)
            {
                if (returnConformer?.SwiftType != null)
                    typeDatabase.TryGetTypeRecord(returnConformer.SwiftType, out returnRecord);
                if (returnRecord == null && returnConformer != null)
                {
                    // Unresolved record: defer to the structural classifier (same logic
                    // that admitted the pairing at preflight). NonFrozenStruct/complex-enum
                    // categories take ownership-transfer; Class takes the legacy alloc+free
                    // shape (see contract #4 above); everything else stays pure-value.
                    var category = ClassifyConformerForCSharp(returnConformer, typeDatabase);
                    returnTypeIsDirectWrap = category == ConformerCategory.NonFrozenStruct;
                    returnTypeIsClassCarrier = category == ConformerCategory.Class;
                }
            }
            else
            {
                var lookupSpec = SubstitutePairingGenericsInTypeSpec(method.CSSignature.First().SwiftTypeSpec, pairing);
                typeDatabase.TryGetTypeRecord(lookupSpec, out returnRecord);
            }
            if (returnRecord != null)
            {
                bool isNonFrozenStruct = returnRecord.Kind == TypeRecordKind.Struct
                    && !MarshallingHelpers.IsTypeFrozen(returnRecord);
                bool isComplexEnum = returnRecord.Kind == TypeRecordKind.Enum
                    && !returnRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum);
                returnTypeIsDirectWrap = isNonFrozenStruct || isComplexEnum;
                returnTypeNeedsWireDestroy = MarshallingHelpers.IsFrozenStructProjectedAsClass(returnRecord);
                // Only generic-param returns store a class instance into the carrier; class
                // parents take no resultPtr (direct class-pointer return), so gate on returnsGenericParam.
                returnTypeIsClassCarrier = returnsGenericParam && returnRecord.Kind == TypeRecordKind.Class;
            }
        }
        bool needsResultPtrOwnershipTransfer = needsResultPtr && returnTypeIsDirectWrap;
        bool needsResultPtrDestroyWireRetains = needsResultPtr && !needsResultPtrOwnershipTransfer && returnTypeNeedsWireDestroy;
        if (needsResultPtr || isStringReturn)
        {
            if (isStringReturn)
            {
                // SBW_Utf8Slice is exactly 2 machine words
                csWriter.WriteLine($"IntPtr {resultPtrName} = System.Runtime.InteropServices.Marshal.AllocHGlobal(nint.Size * 2);");
            }
            else if (needsResultPtrOwnershipTransfer)
            {
                // NativeMemory.Alloc matches the allocator that SwiftSafeHandle.ReleaseHandle
                // frees with (NativeMemory.Free). Don't wrap in try/finally — the returned
                // SafeHandle owns the buffer. The (IntPtr)(void*) cast requires an unsafe
                // context; methods taking only handle/blittable args aren't marked unsafe,
                // so wrap the alloc in a local unsafe block.
                csWriter.WriteLine($"IntPtr {resultPtrName};");
                csWriter.WriteLine($"unsafe {{ {resultPtrName} = (IntPtr)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)SwiftMarshal.GetSwiftTypeSize<{csReturnMarshalType}>()); }}");
            }
            else
            {
                // Struct constructor or other alloc+free case.
                csWriter.WriteLine($"IntPtr {resultPtrName} = System.Runtime.InteropServices.Marshal.AllocHGlobal(SwiftMarshal.GetSwiftTypeSize<{csReturnMarshalType}>());");
            }
            if (!needsResultPtrOwnershipTransfer)
            {
                csWriter.WriteLine("try");
                csWriter.WriteLine("{");
                csWriter.Indent++;
            }
        }

        // Prelude locals (byte[] allocations for Utf8Slice params) must precede the
        // fixed-block stack so the `fixed (... = local)` source binding resolves.
        foreach (var preludeStmt in preludeLocals)
            csWriter.WriteLine(preludeStmt);

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

        // For throws, all direct-return shapes must capture the P/Invoke result into a local
        // so the errorPtr check can run *after* the call but *before* we use the result.
        // The result of `ThrowSwiftError` is unreachable — the call throws — so using it
        // inline as the return expression is unsafe.
        //
        // Ownership-transfer returns (needsResultPtrOwnershipTransfer) have no try/finally
        // guarding the NativeMemory.Alloc — the returned SafeHandle takes ownership on
        // success. On the error path, however, ThrowSwiftError aborts before MarshalFromSwift
        // runs, so the buffer would leak. Free it explicitly on that path.
        string errorCheck;
        if (!throws)
        {
            errorCheck = string.Empty;
        }
        else if (needsResultPtrOwnershipTransfer)
        {
            errorCheck = $"if ({errorPtrName} != IntPtr.Zero) {{ unsafe {{ System.Runtime.InteropServices.NativeMemory.Free((void*){resultPtrName}); }} SwiftMarshal.ThrowSwiftError({errorPtrName}, SBW_GetErrorDescription({errorPtrName}), SBW_ReleaseError); }}";
        }
        else
        {
            errorCheck = $"if ({errorPtrName} != IntPtr.Zero) SwiftMarshal.ThrowSwiftError({errorPtrName}, SBW_GetErrorDescription({errorPtrName}), SBW_ReleaseError);";
        }

        if (isConstructor)
        {
            if (isClass)
            {
                if (throws)
                {
                    csWriter.WriteLine($"var {resultLocalName} = {pinvokeCall};");
                    csWriter.WriteLine(errorCheck);
                    csWriter.WriteLine($"return new {csReturnType}(new Swift.Runtime.SwiftHandle({resultLocalName}));");
                }
                else
                {
                    csWriter.WriteLine($"return new {csReturnType}(new Swift.Runtime.SwiftHandle({pinvokeCall}));");
                }
            }
            else
            {
                // Struct constructor: call writes into resultPtr, then marshal back.
                // Constructors are projection-excluded (the byte[]-projection block above is
                // !isConstructor-gated), so csReturnType is the ISwiftObject marshal type here
                // and no .ToByteArray()-style suffix applies -- a Data initializer returns a Data,
                // not its byte projection. Only the method path below (needsResultPtr) can diverge
                // public-vs-wire, which is why it uses csReturnMarshalType + returnProjectionSuffix.
                csWriter.WriteLine($"{pinvokeCall};");
                if (throws) csWriter.WriteLine(errorCheck);
                if (needsResultPtrDestroyWireRetains)
                {
                    // Frozen-with-memory parent: NewFromPayload copies into its own buffer
                    // (InitializeWithCopy bumps internal refs). Release the wire's +1 on
                    // internal refs before the finally Free reclaims the wire buffer —
                    // otherwise the retained inner allocations leak per call.
                    csWriter.WriteLine($"var {resultLocalName} = SwiftMarshal.MarshalFromSwift<{csReturnType}>({resultPtrName});");
                    csWriter.WriteLine($"SwiftMarshal.DestroyWireBufferRetains<{csReturnType}>({resultPtrName});");
                    csWriter.WriteLine($"return {resultLocalName};");
                }
                else
                {
                    csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{csReturnType}>({resultPtrName});");
                }
            }
        }
        else if (isVoidReturn)
        {
            csWriter.WriteLine($"{pinvokeCall};");
            if (throws) csWriter.WriteLine(errorCheck);
        }
        else if (isStringReturn)
        {
            csWriter.WriteLine($"{pinvokeCall};");
            if (throws) csWriter.WriteLine(errorCheck);
            csWriter.WriteLine($"return SwiftMarshal.ReadUtf8Slice({resultPtrName});");
        }
        else if (needsResultPtr)
        {
            csWriter.WriteLine($"{pinvokeCall};");
            if (throws) csWriter.WriteLine(errorCheck);
            if (needsResultPtrDestroyWireRetains)
            {
                // Frozen-with-memory return type: see struct-constructor branch above.
                csWriter.WriteLine($"var {resultLocalName} = SwiftMarshal.MarshalFromSwift<{csReturnMarshalType}>({resultPtrName});");
                csWriter.WriteLine($"SwiftMarshal.DestroyWireBufferRetains<{csReturnMarshalType}>({resultPtrName});");
                csWriter.WriteLine($"return {resultLocalName}{returnProjectionSuffix};");
            }
            else if (returnTypeIsClassCarrier)
            {
                // Class conformer: Swift's initializeMemory stored the instance
                // pointer INTO the carrier with a +1. Read the slot's contents and adopt that +1
                // (no extra retain); the carrier word is raw-freed below in finally.
                csWriter.WriteLine($"return SwiftMarshal.MarshalOwnedClassFromSlot<{csReturnMarshalType}>({resultPtrName}){returnProjectionSuffix};");
            }
            else
            {
                csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{csReturnMarshalType}>({resultPtrName}){returnProjectionSuffix};");
            }
        }
        else if (returnsGenericParam)
        {
            // Unreachable in practice: returnsGenericParam forces needsResultPtr above.
            // Kept for parity with the pre-throws shape table.
            csWriter.WriteLine($"return {pinvokeCall};");
        }
        else
        {
            // Direct @_cdecl return. Capture into a local so:
            //   1. the errorPtr check can run before using _result (throws path)
            //   2. the directReturnMapping-based projection converts the raw ABI value
            //      (raw scalar for SimpleEnum, IntPtr for ClassPointer, nullable IntPtr
            //      for OptionalClassPointer) back to the public C# type.
            // Mirrors the Swift side's unconditional EmitCdeclDirectReturn — non-Direct
            // kinds need the conversion whether or not the method throws, because the
            // Swift @_cdecl ABI is identical across both paths. For CdeclReturnKind.Direct
            // the switch falls through to `return _result;`, equivalent to the prior
            // inline `return pinvokeCall;`.
            csWriter.WriteLine($"var {resultLocalName} = {pinvokeCall};");
            if (throws) csWriter.WriteLine(errorCheck);
            switch (directReturnMapping?.Kind)
            {
                case CdeclReturnKind.SimpleEnum:
                    csWriter.WriteLine($"return ({csReturnType}){resultLocalName};");
                    break;
                case CdeclReturnKind.ClassPointer:
                    csWriter.WriteLine($"return new {csReturnType}(new Swift.Runtime.SwiftHandle({resultLocalName}));");
                    break;
                case CdeclReturnKind.OptionalClassPointer:
                    // csReturnType is `MyClass?` — strip the `?` for the constructor call.
                    csWriter.WriteLine($"return {resultLocalName} == IntPtr.Zero ? null : new {csReturnType.TrimEnd('?')}(new Swift.Runtime.SwiftHandle({resultLocalName}));");
                    break;
                default:
                    csWriter.WriteLine($"return {resultLocalName};");
                    break;
            }
        }

        // Close fixed blocks (reverse nesting order)
        for (int i = 0; i < fixedStatements.Count; i++)
        {
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        if ((needsResultPtr || isStringReturn) && !needsResultPtrOwnershipTransfer)
        {
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine($"finally {{ System.Runtime.InteropServices.Marshal.FreeHGlobal({resultPtrName}); }}");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");

        method.MarkEmitted();
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

    // Per-entry metadata for the inline-struct allowlist.
    //
    // CSharpType:     fully-qualified C# type emitted for parameter declarations and the
    //                 `(IntPtr)(&v)` pin-pass. Generated binding files don't `using
    //                 Swift.Foundation`, so the value-type identity needs to be explicit.
    // IsISwiftObject: whether the C# type implements ISwiftObject. Controls eligibility
    //                 for the indirect-result return path: `SwiftMarshal.GetSwiftTypeSize<T>()`
    //                 is constrained to `T : ISwiftObject`, so non-ISwiftObject entries
    //                 (e.g. System.Guid) MUST be rejected by the indirect-result gate
    //                 in `CanEmitConcreteOverloadForPairing` even though they are valid
    //                 inline-struct parameter conformers.
    // IdiomaticPublicType: when set, a concrete CSM overload returning this type presents this
    //                 idiomatic type on its public surface (e.g. Foundation.Data -> byte[])
    //                 while the wire is still sized/marshaled on CSharpType. null means the
    //                 public surface is CSharpType (no projection). Mirrors DataProjection.PublicType.
    // MarshalToPublicSuffix: C# expression suffix converting a marshaled CSharpType value to
    //                 IdiomaticPublicType (e.g. ".ToByteArray()" for Data -> byte[]).
    private readonly record struct InlineSwiftStructInfo(string CSharpType, bool IsISwiftObject, string? IdiomaticPublicType = null, string? MarshalToPublicSuffix = null);

    /// <summary>
    /// Test-only contract assertion: returns whether the given Swift qualified name is a
    /// known inline-struct conformer whose C# binding implements ISwiftObject (and is
    /// therefore eligible for the indirect-result return path that relies on
    /// <c>SwiftMarshal.GetSwiftTypeSize&lt;T&gt;()</c>'s <c>T : ISwiftObject</c> constraint).
    /// Returns <c>(false, false)</c> for names not in the allowlist.
    /// </summary>
    internal static (bool IsInlineStruct, bool IsISwiftObject) GetInlineSwiftStructIndirectReturnEligibilityForTesting(string swiftQualifiedName)
        => InlineSwiftStructAllowlist.TryGetValue(swiftQualifiedName, out var info)
            ? (true, info.IsISwiftObject)
            : (false, false);

    /// <summary>
    /// Test-only contract assertion: returns the idiomatic public-surface projection for a
    /// known inline-struct conformer's indirect return -- the public type and the marshal-to-public
    /// suffix (e.g. Foundation.Data -> ("byte[]", ".ToByteArray()")). Returns (null, null) for
    /// names not in the allowlist or with no distinct projection (e.g. Foundation.UUID).
    /// </summary>
    internal static (string? PublicType, string? Suffix) GetInlineSwiftStructReturnProjectionForTesting(string swiftQualifiedName)
        => InlineSwiftStructAllowlist.TryGetValue(swiftQualifiedName, out var info)
            ? (info.IdiomaticPublicType, info.MarshalToPublicSuffix)
            : (null, null);

    /// <summary>
    /// Test-only contract assertion: whether <paramref name="record"/> (named by
    /// <paramref name="named"/>) projects to a C# value struct implementing ISwiftObject and so
    /// is eligible for the frozen-trivial CSM return/param paths (pin-and-pass + indirect-result).
    /// Delegates to <see cref="ProjectsAsBlittableValueStruct"/>.
    /// </summary>
    internal static bool ProjectsAsBlittableValueStructForTesting(NamedTypeSpec named, TypeRecord record)
        => ProjectsAsBlittableValueStruct(named, record);

    // Conformers whose C# binding is a blittable value-type (rather than a class with
    // SafeHandle) and therefore gets pin-and-pass marshalling instead of
    // `.Payload.DangerousGetHandle()`. Two flavors live here:
    //   • Hand-written ISwiftObject value structs we own (e.g. Foundation.Data → the
    //     Swift.Foundation.Data inline struct in Swift.Runtime). The NativeTypeName
    //     (NSData) is just how we bridge into Foundation, not a signal that the C# side
    //     lacks a pinnable layout. IsISwiftObject = true (eligible for indirect-return).
    //   • .NET built-in value types remapped from Foundation primitives (Foundation.UUID
    //     → System.Guid). Both Swift.Foundation.UUID (frozen 16-byte struct) and
    //     System.Guid (16-byte unmanaged struct) share the same byte-level layout under
    //     the convention `*(System.Guid*)uuidBytes` already used by
    //     ConstrainedExtensionEmitter's FoundationUUID return shape. Without this entry,
    //     the conformer falls into the FrozenStruct arm and emits
    //     `guid.Payload.DangerousGetHandle()` — CS1061 since Guid has no Payload.
    //     IsISwiftObject = false: indirect-result paths would emit
    //     `GetSwiftTypeSize<System.Guid>()` which fails the `T : ISwiftObject` constraint
    //     at compile time, so generic-return UUID specializations are rejected upstream
    //     by the indirect-result-is-ISwiftObject gate.
    private static readonly Dictionary<string, InlineSwiftStructInfo> InlineSwiftStructAllowlist = new(StringComparer.Ordinal)
    {
        ["Foundation.Data"] = new("global::Swift.Foundation.Data", IsISwiftObject: true, IdiomaticPublicType: "byte[]", MarshalToPublicSuffix: ".ToByteArray()"),
        ["Foundation.UUID"] = new("System.Guid", IsISwiftObject: false)
    };

    /// <summary>
    /// True when a frozen, trivially-copyable Swift struct projects to a C# value struct that
    /// implements ISwiftObject -- the shape eligible for CSM pin-and-pass parameter marshalling
    /// and indirect-result return marshalling (e.g. an ECDSA <c>signature(for:) -> ECDSASignature</c>).
    ///
    /// Such a struct is laid out inline (no SafeHandle/Buffer), so a parameter crosses the @_cdecl
    /// boundary as a pinned <c>(IntPtr)(&amp;v)</c> byte copy (Swift reads it back via
    /// <c>assumingMemoryBound(to:).pointee</c>) and a return is sized via <c>GetSwiftTypeSize&lt;T&gt;</c>
    /// and read back via <c>MarshalFromSwift&lt;T&gt;</c> (both constrained to <c>T : ISwiftObject</c>),
    /// then byte-copied with no retain semantics before the wire buffer is freed.
    ///
    /// Excludes types whose C# projection is NOT an inline ISwiftObject value struct:
    ///   - non-frozen / RequiresMemoryManagement structs (project to a class with SafeHandle/Buffer)
    ///   - NativeTypeName-remapped types (e.g. Foundation.UUID -> System.Guid) -- not ISwiftObject
    ///   - known Apple value types (CGFloat, simd_*) -- remapped to .NET primitives, handled separately
    ///   - ObjC-bridged / -bridgeable structs (cross as object pointers, not struct bytes)
    ///   - non-copyable (~Copyable) structs (a byte copy would violate move-only semantics)
    ///   - structs the pre-emission pass records as skipped (a frozen value struct whose Buffer
    ///     layout is indeterminate, or whose sub-word Optional&lt;primitive&gt; fields shift a
    ///     following field's byte offset, is recorded skipped and never declared) -- admitting one
    ///     would emit a CSM overload referencing a C# type that is never generated (CS0246). The
    ///     flag checks above cannot see this: such a struct is still frozen and non-RMM, so the
    ///     authoritative "will this type be emitted?" oracle is the skipped-type set, keyed by the
    ///     record's canonical declaration identity (the same key the member gate consults).
    /// </summary>
    private static bool ProjectsAsBlittableValueStruct(NamedTypeSpec named, TypeRecord record)
    {
        return record.Kind == TypeRecordKind.Struct
            && MarshallingHelpers.IsTypeFrozen(record)
            && !MarshallingHelpers.RequiresMemoryManagement(record)
            && record.NativeTypeName == null
            && !record.Flags.HasFlag(TypeRecordFlags.ObjCBridged)
            && !record.Flags.HasFlag(TypeRecordFlags.ObjCBridgeable)
            && !record.Flags.HasFlag(TypeRecordFlags.NonCopyable)
            && !TypeDatabaseExtensions.IsKnownAppleValueType(named)
            && !ReportCollector.IsTypeSkipped(record.SwiftTypeName);
    }

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

    internal enum StructuralEmitReject { None, NestedType, ObjCBridged, NonISwiftObjectConformer, BlittableStructProjection }

    // Per-conformer structural gate used by TryEmitConcreteOverload's preflight AND by
    // IsCsmSyncEligibleForGenericParent. Keeping this single source of truth means the
    // sync suppression predicate cannot declare eligibility for a pairing the emitter
    // will silently drop — if a new rejection is added here, both paths learn about it.
    internal static StructuralEmitReject ClassifyConformerStructurally(
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        ITypeDatabase typeDatabase)
    {
        // A nested-type conformer (module-qualified name with >2 dot segments, e.g.
        // `KeyVault.Agreement.PublicKey` — HPKE's `Curve25519.KeyAgreement.PublicKey` shape)
        // is emittable iff its C# projection has a resolvable, referenceable name. Nested
        // types emit as namespace-facade / static-class chains whose fully-qualified C# name
        // (`KeyVault.Agreement.PublicKey`) is a valid reference from the host method's module
        // namespace — the generated registration code already names them that way. Only reject
        // a nested conformer whose type record can't be resolved to a concrete C# name.
        if (conformer.SwiftType != null &&
            conformer.SwiftType.ModuleQualifiedName.Split('.').Length > 2 &&
            !typeDatabase.TryGetTypeRecord(conformer.SwiftType, out _))
            return StructuralEmitReject.NestedType;

        var category = ClassifyConformerForSwiftParam(conformer, typeDatabase);
        if (category != ConformerCategory.InlineSwiftStruct &&
            conformer.SwiftType != null &&
            typeDatabase.TryGetTypeRecord(conformer.SwiftType, out var record) &&
            (record.NativeTypeName != null
                || MarshallingHelpers.IsObjCBridged(record)
                || MarshallingHelpers.IsObjCRooted(record)))
            return StructuralEmitReject.ObjCBridged;

        // Reject simple-enum and unemittable-enum conformers. The CSM emitter's struct/enum
        // arms can't render them. Two enum shapes fail:
        //   • SimpleEnum (no associated values, frozen, non-generic, integral or no
        //     raw value): emits as a plain C# `enum` value type with no ISwiftObject
        //     impl. (Historically this also tripped CS0315 at a `where T : ISwiftObject`
        //     parent constraint; GenericTypeEmitter now DROPS that seed for descriptor-
        //     path-safe PATs, so the parent may be unconstrained — but the emitter still
        //     has no value-enum conformer arm, so the rejection stands.)
        //   • Unemittable (e.g. single-case no-payload, TypeMetadata.Size == 0): not
        //     emitted at all → CS0234 at the missing type reference.
        // Complex enums (associated-value cases) DO project to C# classes that
        // implement ISwiftObject and render correctly through the SafeHandle
        // `.Payload.DangerousGetHandle()` path used for class-projected conformers,
        // so they must NOT be rejected here. Without this narrowing, every PAT-
        // constrained parent whose only conformers are complex enums would have its
        // parent-only CSM disabled even though the emitter can render them.
        if (conformer.SwiftType != null &&
            typeDatabase.TryGetTypeRecord(conformer.SwiftType, out var enumRecord) &&
            enumRecord.Kind == TypeRecordKind.Enum &&
            (enumRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum) ||
             enumRecord.Flags.HasFlag(TypeRecordFlags.Unemittable)))
            return StructuralEmitReject.NonISwiftObjectConformer;

        // Reject frozen-trivial-layout struct conformers (e.g. `@frozen struct
        // SummableInt32 { let value: Int32 }`). These project to C# `struct`s that
        // DO implement ISwiftObject (so the parent constraint is satisfied), but
        // the CSM emitter's only struct-conformer arms today are the SafeHandle
        // `.Payload.DangerousGetHandle()` path (class-projected frozen structs and
        // non-frozen structs) and the InlineSwiftStruct allowlist (pin-and-pass
        // via `(IntPtr)(&v)`, currently scoped to hand-written Foundation.Data /
        // Foundation.UUID). A frozen + non-memory-managed struct emits as a C#
        // value `struct` with no Payload member — emitting `item.Payload.DangerousGetHandle()`
        // gives CS1061. Auto-detected pin-and-pass for this shape is a real
        // emission feature; until it's wired through, reject these conformers
        // structurally so the parent-only sync CSM path doesn't widen into a
        // category the emitter can't render.
        if (conformer.SwiftType != null &&
            typeDatabase.TryGetTypeRecord(conformer.SwiftType, out var structRecord) &&
            structRecord.Kind == TypeRecordKind.Struct &&
            structRecord.Flags.HasFlag(TypeRecordFlags.Frozen) &&
            !structRecord.Flags.HasFlag(TypeRecordFlags.RequiresMemoryManagement) &&
            !InlineSwiftStructAllowlist.ContainsKey(conformer.SwiftQualifiedName))
            return StructuralEmitReject.BlittableStructProjection;

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
        // Per-conformer structural gate: rejects nested-type conformers whose C# name
        // can't be resolved (resolvable nested types like `KeyVault.Agreement.PublicKey`
        // ARE admitted), ObjC-bridged conformers, and Swift enum conformers (their C#
        // binding either lacks ISwiftObject — simple/raw-value enums — or isn't emitted
        // at all — single-case no-payload).
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
                case StructuralEmitReject.NonISwiftObjectConformer:
                    rejectReason = $"Swift enum conformer '{conformer.SwiftQualifiedName}' does not satisfy `where T : ISwiftObject` constraint of generic parent";
                    return false;
                case StructuralEmitReject.BlittableStructProjection:
                    rejectReason = $"frozen-trivial-layout struct conformer '{conformer.SwiftQualifiedName}' projects to C# value `struct` (no Payload member) — CSM emitter lacks pin-and-pass for this shape";
                    return false;
            }
        }

        // Closed-receiver C# constraint gate: a parent-generic pairing closes the parent type
        // over its conformers (e.g. `FastDatabaseValueCursor<System.Guid>`). The C# declaration
        // of `FastDatabaseValueCursor<TValue>` carries `where TValue : IDatabaseValueConvertible,
        // IStatementColumnConvertible, ISwiftObject`, seeded from the Swift generic signature.
        // A conformer like Foundation.UUID satisfies those protocols in Swift (via third-party
        // extensions) but its C# projection System.Guid cannot implement those interfaces, so the
        // closed receiver type is uninstantiable (CS0315/CS0311). Reuse the bound-generic
        // constraint validator — the same logic that produces the "does not satisfy constraint"
        // diagnostic on the open-generic use-site path — so the gate stays in lockstep with the
        // C# `where` clauses GenericTypeEmitter actually emits (PAT/Self/Sendable constraints it
        // drops are skipped here too, avoiding over-rejection).
        if (parentTypeDecl.IsGeneric &&
            BuildClosedParentTypeSpec(parentTypeDecl, pairing) is { GenericParameters.Count: > 0 } closedReceiver &&
            new BoundGenericsHandler(typeDatabase).TryGetFirstUnsatisfiedConstraint(closedReceiver, method, out var constraintDetails))
        {
            rejectReason = $"closed receiver type violates C# generic constraints: {constraintDetails}";
            return false;
        }

        bool isConstructor = method.IsConstructor;
        bool isClass = parentTypeDecl is ClassDecl;

        // Cheap, receiver-independent ctor filters the normal `@_cdecl` wrapper path applies but
        // CSM historically did not mirror: `_const` params (a runtime wrapper cannot supply a
        // compile-time-constant literal) and internal/unavailable inits. Shared with the open
        // ctor path via ConstructorAdmissibility so the three erasure paths stay in lockstep.
        if (isConstructor &&
            !ConstructorAdmissibility.PassesConstructorCheapFilters(method, out var cheapReject))
        {
            rejectReason = cheapReject;
            return false;
        }

        // A constructor that pins a parent generic parameter to an unrepresentable concrete type
        // (`where RowDecoder == ()`) cannot be emitted as a CSM closed form either: the pin is
        // dropped by GenericSignatureParser so the per-conformer constraint evaluation below never
        // sees it, and a closed form closing over a DIFFERENT parameter leaves the pinned parameter
        // generic (the `()` target is never an enumerated conformer). Refuse it the same way the
        // open-erasure gate does, via the shared admissibility helper.
        if (isConstructor &&
            ConstructorAdmissibility.HasUnrepresentableConcreteParentPin(method, parentTypeDecl))
        {
            rejectReason = "constructor pins a parent generic parameter to an unrepresentable concrete type (`== ()`)";
            return false;
        }

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
                // Indirect result allocates with `GetSwiftTypeSize<T>()` (T : ISwiftObject).
                // InlineSwiftStruct conformers are mixed: Foundation.Data maps to an
                // ISwiftObject (Swift.Foundation.Data) — safe; Foundation.UUID maps to
                // System.Guid — NOT an ISwiftObject — emission would fail the constraint.
                // Consult the allowlist's IsISwiftObject flag rather than treating the
                // category as a single bucket.
                bool inlineIsSwiftObject = category == ConformerCategory.InlineSwiftStruct
                    && InlineSwiftStructAllowlist.TryGetValue(
                        returnConformer!.SwiftQualifiedName, out var inlineInfo)
                    && inlineInfo.IsISwiftObject;
                indirectReturnIsSwiftObject = category is
                    ConformerCategory.NonFrozenStruct
                    or ConformerCategory.Class
                    || inlineIsSwiftObject;
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
                    // A frozen-trivial inline struct (e.g. Foundation.Data) is rejected by the
                    // TypeRecord check above (frozen, no RequiresMemoryManagement) yet maps to an
                    // ISwiftObject C# binding (Swift.Foundation.Data) whose GetSwiftTypeSize<T>() is
                    // valid. Admit it via the allowlist's IsISwiftObject flag. Keyed on
                    // NamedTypeSpec.Name (module-qualified, e.g. "Foundation.Data") rather than a
                    // conformer's SwiftQualifiedName because this non-generic-param arm has only the
                    // raw return TypeSpec -- no conformer object -- and Name matches the allowlist keys.
                    if (!indirectReturnIsSwiftObject
                        && returnTypeSpec is NamedTypeSpec inlineNamed
                        && InlineSwiftStructAllowlist.TryGetValue(inlineNamed.Name, out var inlineRetInfo)
                        && inlineRetInfo.IsISwiftObject)
                    {
                        indirectReturnIsSwiftObject = true;
                    }
                    // A frozen, trivially-copyable struct that isn't an allowlisted inline struct
                    // still projects to a C# value struct implementing ISwiftObject (e.g. an ECDSA
                    // `signature(for:) -> ECDSASignature`). The pure-value indirect-result path
                    // (frozen, no RequiresMemoryManagement) sizes it via GetSwiftTypeSize<T>, reads
                    // it back via MarshalFromSwift<T>, and just byte-copies then frees the wire -- no
                    // retains to manage. Admit it so typed-struct returns concretize, not only Data.
                    if (!indirectReturnIsSwiftObject
                        && returnTypeSpec is NamedTypeSpec valueStructNamed
                        && typeDatabase.TryGetTypeRecord(valueStructNamed, out var valueStructRecord)
                        && ProjectsAsBlittableValueStruct(valueStructNamed, valueStructRecord))
                    {
                        indirectReturnIsSwiftObject = true;
                    }
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

        // `inout` params: the per-conformer @_cdecl wrapper emitter renders params as
        // by-value (no `inout` Swift prefix, no `&` at the call site, no `ref` on the
        // C# P/Invoke). The Swift wrapper would fail to compile (argument label/value
        // mismatch with the underlying method that takes `inout`), leaving the
        // CallConvCdecl symbol absent from the dylib and the generated C# P/Invoke
        // pointing at a missing entry point. BoundGenericsHandler already handles
        // inout-on-generic-parent via the open-generic emission path; defer to it.
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;
            if (arg.HasDefaultArg) continue;
            if (arg.IsInOut || arg.SwiftTypeSpec.IsInOut)
            {
                rejectReason = "inout parameter not supported by CSM emitter";
                return false;
            }
        }

        // Sibling-overload C# signature collision: the CSM emitter's dedup key is
        // {C# method name | param public-CSharp-types} — argument labels are dropped.
        // Two Swift methods with the same base name but different labels (e.g.
        // `index(after: Int) -> Int` and `index(before: Int) -> Int`) collapse to
        // identical dedup keys and one is silently dropped, stripping its surface.
        // BoundGenericsHandler emits these via the open-generic path with sibling-
        // aware C# collision suffixes (`Index` + `Index2`); defer to it so both
        // siblings survive. Constructors don't collide here because their CSM C#
        // name is `From{Conformers}` (pairing-derived), not pascal-cased Swift name.
        if (!isConstructor && HasUnresolvableCsmSiblingCollision(method, parentTypeDecl, pairing, typeDatabase))
        {
            rejectReason = "sibling overload would collide under CSM dedup (different argument labels collapse to identical C# signature)";
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
        if (!DoesPairingSatisfyAssociatedTypeConstraints(method, parentTypeDecl, pairing, typeDatabase))
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
    /// Same-type bounds (<c>==</c>) require exact-name equality. Subtype bounds (<c>:</c>) are
    /// handled in three ways depending on the target's nature:
    /// <list type="bullet">
    ///   <item><description>Class target (e.g. <c>S.Element : RealityKit.Entity</c>) — accepts
    ///   the conformer's recorded <see cref="ConcreteSpecializationEngine.ConcreteConformer.AssociatedTypes"/>
    ///   value when it equals the target name OR has the target in its
    ///   <see cref="TypeRecord.SuperclassTypeName"/> chain (Swift class subtype admits subclasses).
    ///   Unresolvable chains fail closed.</description></item>
    ///   <item><description>Protocol target (true protocol conformance like <c>S.Element : Hashable</c>)
    ///   — looks up the conformer Element's <see cref="TypeRecord"/> and walks its
    ///   <see cref="TypeRecord.ProtocolConformances"/> transitively (refining edges like
    ///   <c>Hashable : Equatable</c>): accepts when the target appears anywhere in the closure.
    ///   Element record unresolvable, or its <see cref="TypeRecord.ProtocolConformances"/>
    ///   not populated, fails closed — same posture as the class-chain path.</description></item>
    ///   <item><description>Other resolved kinds (Struct/Enum/Existential on a <c>:</c> clause —
    ///   uncommon ABI shape, typically a same-type alias) — pass-through.</description></item>
    /// </list>
    /// Without this filter, parent-declared same-type floors like <c>S.Element == Album</c> would
    /// pass straight through and the pairing machinery would enumerate every Sequence conformer
    /// (e.g. <c>S = [UInt8]</c>), emitting an uncompilable wrapper.
    /// </summary>
    internal static bool DoesPairingSatisfyAssociatedTypeConstraints(
        MethodDecl method,
        TypeDecl parentTypeDecl,
        IReadOnlyList<(ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)> pairing,
        ITypeDatabase? typeDatabase = null)
    {
        foreach (var (param, conformer) in pairing)
        {
            var assocList = param.GenericParam?.AssosiatedTypeConformances;
            if (assocList == null || assocList.Count == 0)
                continue;

            foreach (var assoc in assocList)
            {
                if (assoc.Path == null || assoc.Path.Length < 2)
                    continue;

                // Resolve the target's record once — drives the per-target-kind branching below.
                TypeRecord? targetRecord = null;
                if (typeDatabase is not null)
                {
                    typeDatabase.TryGetTypeRecord(assoc.ConformanceTarget, out targetRecord);
                }

                // ConcreteType (`==`) and Protocol (`:`) bounds are the only kinds we handle.
                // For `:` clauses where the resolved target is Struct/Enum/Existential — an
                // uncommon ABI shape (typically a same-type alias surfaced as a conformance
                // bound) — preserve the historical pass-through. We only tighten Class and
                // Protocol cases here.
                if (assoc.Kind == ConformanceKind.Protocol &&
                    targetRecord is not null &&
                    targetRecord.Kind != TypeRecordKind.Class &&
                    targetRecord.Kind != TypeRecordKind.Protocol)
                {
                    continue;
                }
                if (assoc.Kind != ConformanceKind.ConcreteType && assoc.Kind != ConformanceKind.Protocol)
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

                // Parent-generic-param target: a constraint like `S.Element == TMusicItemType`
                // where `TMusicItemType` is the parent type's own generic parameter (e.g.
                // `MusicItemCollection<TMusicItemType>`). The parent-generic isn't a concrete
                // type here — it's an open placeholder the specialization engine binds
                // separately. Any conformer whose `Element` is admitted by the engine's
                // cross-param coupling is acceptable for this site, so skip the concrete-name
                // equality check rather than fail-close. Concrete-target mismatches
                // (e.g. `S.Element == Album` vs `[UInt8]`) still reject because `expected`
                // in that case is a fully qualified type name, not a bare generic-param name.
                if (IsParentGenericParamName(expected, parentTypeDecl))
                    continue;

                if (conformer.AssociatedTypes is null)
                    return false;
                if (!conformer.AssociatedTypes.TryGetValue(assocName, out var declared))
                    return false;

                // Exact-name fast path: valid for same-type (`==`) constraints and
                // class-subtype (`:` over a class target) — `Element == Animal` /
                // `Element : Animal` both accept `Element = Animal`. NOT valid for a
                // protocol target: Swift rejects `[any P]` for `where S.Element : P`
                // because an existential `any P` does not itself conform to `P`
                // (`type 'any P' cannot conform to 'P'`). For protocol targets we
                // skip the fast path and fall through to the conformance walk —
                // which correctly rejects `Element == TargetProtocol` because the
                // protocol's own `ProtocolConformances` (its inherited protocols)
                // does not include itself.
                bool isProtocolTarget = assoc.Kind == ConformanceKind.Protocol &&
                                        targetRecord is not null &&
                                        targetRecord.Kind == TypeRecordKind.Protocol;
                if (!isProtocolTarget && string.Equals(declared, expected, StringComparison.Ordinal))
                    continue;

                // Protocol target: verify the conformer Element conforms to the target
                // protocol (directly or transitively via refining edges like
                // `Hashable : Equatable`). The Element's TypeRecord.ProtocolConformances
                // carries direct edges only; we walk transitively here.
                if (isProtocolTarget)
                {
                    if (typeDatabase is not null &&
                        IsDeclaredConformingToProtocol(declared, assoc.ConformanceTarget, typeDatabase))
                    {
                        continue;
                    }
                    return false;
                }

                // For Protocol-kind constraints with a class target, Swift's `:` is a subtype
                // bound — `where S.Element : Animal` accepts `[Dog]` when `Dog : Animal`. Exact-
                // name match alone would falsely reject valid subclasses. Walk the conformer's
                // superclass chain via TypeDatabase: if `expected` appears anywhere up the chain,
                // accept. ConcreteType (`==`) constraints stay strict — same-type bounds don't
                // admit subclasses. Unresolvable chain (records missing from typeDatabase) keeps
                // the prior fail-closed semantics — we'd rather drop a specialization than emit
                // a wrapper whose subtype relationship we couldn't verify.
                if (assoc.Kind == ConformanceKind.Protocol &&
                    typeDatabase is not null &&
                    IsDeclaredSubclassOfExpected(declared, expected, typeDatabase))
                {
                    continue;
                }

                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Returns true when <paramref name="declared"/> names a type whose
    /// <see cref="TypeRecord.ProtocolConformances"/> chain transitively contains
    /// <paramref name="target"/>. Walks each direct conformance's own
    /// <see cref="TypeRecord.ProtocolConformances"/> recursively to follow protocol
    /// refining edges (<c>Hashable : Equatable</c> etc.). Cycles are guarded by a
    /// visited set; unresolvable hops are skipped (sibling conformances may still
    /// match). Used to admit valid Swift subtype pairings under
    /// protocol-conformance bounds (<c>S.Element : Hashable</c> accepts a UInt8
    /// element whose record declares <c>Hashable</c>). Fail-closed when the
    /// declared element's record is missing or has no populated
    /// <see cref="TypeRecord.ProtocolConformances"/> list — same posture as the
    /// class-chain path.
    /// </summary>
    private static bool IsDeclaredConformingToProtocol(
        string declared, SwiftTypeName target, ITypeDatabase typeDatabase)
    {
        // Strip a bound-generic suffix to the head — `Swift.Array<Foo>` resolves
        // its conformances at `Swift.Array`, not at the bound form.
        var ltIndex = declared.IndexOf('<');
        if (ltIndex > 0)
            declared = declared.Substring(0, ltIndex);

        SwiftTypeName declaredName;
        try
        {
            declaredName = SwiftTypeName.FromModuleQualifiedName(declared);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!typeDatabase.TryGetTypeRecord(declaredName, out var declaredRecord))
            return false;

        // Reject existential element types. Swift refuses `[any P]` (and `[any Q]`
        // where `Q : P`) for `where S.Element : P` — only concrete types satisfy
        // a generic protocol-conformance constraint, even when the existential's
        // protocol refines the target. Without this guard the DFS below would
        // walk a protocol record's `ProtocolConformances` (which stores its
        // inherited protocols for protocol-kind records) and incorrectly accept
        // `Element = ChildProtocol` for `Element : P` because the inheritance
        // chain reaches `P`. Fail closed instead.
        if (declaredRecord.Kind == TypeRecordKind.Protocol)
            return false;

        if (declaredRecord.ProtocolConformances is null)
            return false;

        var visited = new HashSet<string>(StringComparer.Ordinal) { declaredName.ModuleQualifiedName };
        return WalkProtocolConformancesTransitively(
            declaredRecord.ProtocolConformances, target, typeDatabase, visited);
    }

    /// <summary>
    /// DFS over a list of direct protocol conformances looking for
    /// <paramref name="target"/>. Recurses into each conformance's own
    /// <see cref="TypeRecord.ProtocolConformances"/> to follow refining edges.
    /// Unresolvable conformance entries are silently skipped — we can't decide
    /// from them, but a sibling branch may still match.
    /// </summary>
    private static bool WalkProtocolConformancesTransitively(
        IReadOnlyList<SwiftTypeName> conformances,
        SwiftTypeName target,
        ITypeDatabase typeDatabase,
        HashSet<string> visited)
    {
        foreach (var p in conformances)
        {
            if (string.Equals(p.ModuleQualifiedName, target.ModuleQualifiedName, StringComparison.Ordinal))
                return true;
            if (!visited.Add(p.ModuleQualifiedName))
                continue;
            if (!typeDatabase.TryGetTypeRecord(p, out var pRecord))
                continue;
            if (pRecord.ProtocolConformances is { Count: > 0 } inherited &&
                WalkProtocolConformancesTransitively(inherited, target, typeDatabase, visited))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="declared"/> names a class that has
    /// <paramref name="expected"/> in its <see cref="TypeRecord.SuperclassTypeName"/>
    /// chain. Both names must resolve in <paramref name="typeDatabase"/>; any
    /// unresolved hop returns false (fail-closed). Used to admit valid Swift subclass
    /// pairings under class-inheritance bounds (`S.Element : Animal` accepts `[Dog]`).
    /// </summary>
    private static bool IsDeclaredSubclassOfExpected(
        string declared, string expected, ITypeDatabase typeDatabase)
    {
        if (declared.Contains('<') || expected.Contains('<'))
            return false;

        SwiftTypeName declaredName, expectedName;
        try
        {
            declaredName = SwiftTypeName.FromModuleQualifiedName(declared);
            expectedName = SwiftTypeName.FromModuleQualifiedName(expected);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!typeDatabase.TryGetTypeRecord(expectedName, out var expectedRecord) ||
            expectedRecord.Kind != TypeRecordKind.Class)
            return false;

        if (!typeDatabase.TryGetTypeRecord(declaredName, out var declaredRecord) ||
            declaredRecord.Kind != TypeRecordKind.Class)
            return false;

        var visited = new HashSet<string>(StringComparer.Ordinal) { declaredName.ModuleQualifiedName };
        var cursor = declaredRecord;
        while (cursor.SuperclassTypeName is not null)
        {
            if (string.Equals(cursor.SuperclassTypeName.ModuleQualifiedName, expected, StringComparison.Ordinal))
                return true;
            if (!visited.Add(cursor.SuperclassTypeName.ModuleQualifiedName))
                return false;
            if (!typeDatabase.TryGetTypeRecord(cursor.SuperclassTypeName, out var parent))
                return false;
            cursor = parent;
        }
        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="name"/> names one of
    /// <paramref name="parentTypeDecl"/>'s generic parameters (either the raw
    /// <c>τ_0_*</c> form or the sugared form). Used by
    /// <see cref="DoesPairingSatisfyAssociatedTypeConstraints"/> to recognize constraints
    /// whose target is the parent type's open generic slot rather than a concrete type.
    /// </summary>
    private static bool IsParentGenericParamName(string name, TypeDecl parentTypeDecl)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        foreach (var parentGeneric in parentTypeDecl.GenericParameters)
        {
            if (string.Equals(parentGeneric.TypeName, name, StringComparison.Ordinal))
                return true;
            if (!string.IsNullOrEmpty(parentGeneric.SugaredTypeName) &&
                string.Equals(parentGeneric.SugaredTypeName, name, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true when this method shares a CSM dedup key with another method on the
    /// same parent. The CSM emitter's <see cref="BuildCSharpSignatureKey"/> uses
    /// {C# method name | param public-CSharp-types} — argument labels are not part of
    /// the key, so two Swift methods that differ only by label (e.g.
    /// <c>index(after: Int) -> Int</c> and <c>index(before: Int) -> Int</c>) hash to
    /// the same key and one is silently dropped, deleting its surface entirely. The
    /// caller defers these to the BoundGenericsHandler open-generic emission, which
    /// already disambiguates label-only siblings with C# collision suffixes
    /// (<c>Index</c> + <c>Index2</c>).
    /// </summary>
    internal static bool HasUnresolvableCsmSiblingCollision(
        MethodDecl method,
        TypeDecl parentTypeDecl,
        (ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)[] pairing,
        ITypeDatabase typeDatabase)
    {
        var thisCsName = NameProvider.ToPascalCase(method.Name);
        var thisKey = BuildCSharpSignatureKey(thisCsName, method, pairing, typeDatabase);
        foreach (var other in parentTypeDecl.Methods)
        {
            if (ReferenceEquals(other, method)) continue;
            if (other.IsConstructor) continue;
            if (other.IsAccessor) continue;
            if (other.IsSubscriptAccessor) continue;
            // Static-vs-instance overloads don't collide in CSM (extension-on-instance
            // vs static helper land in different surfaces) — skip the shape mismatch.
            if (other.MethodType != method.MethodType) continue;
            var otherCsName = NameProvider.ToPascalCase(other.Name);
            if (!string.Equals(otherCsName, thisCsName, StringComparison.Ordinal)) continue;
            var otherKey = BuildCSharpSignatureKey(otherCsName, other, pairing, typeDatabase);
            if (string.Equals(otherKey, thisKey, StringComparison.Ordinal))
                return true;
        }
        return false;
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

            // CSM has dedicated KeyPathFamily switch arms (see the param-render switches in
            // both the C# bridge and the Swift @_cdecl wrapper below), so it admits a
            // strict superset of the closure-bridge layer's IsAbiCategoryPassable. Honors
            // the predicate↔emitter contract: KeyPathFamily is "passable" here exactly
            // because the CSM emitter renders it.
            var category = MethodClosureBridge.ClassifyParam(arg, typeDatabase);
            if (!MethodClosureBridge.IsAbiCategoryPassableForCsm(category))
            {
                // Foundation.Data classifies as NativeRemapped (Data ↔ NSData), which the
                // closure-bridge layer treats as not-passable because it has no concrete-param
                // renderer there. The CSM emitter DOES render it: a concrete Data param crosses
                // the hand-authored @_cdecl boundary as the canonical two-Int-word decomposition
                // (public byte[] → Swift.Foundation.Data.FromByteArray → two nint words → Swift
                // unsafeBitCast back to Foundation.Data), the same ownership-balanced shape the
                // ordinary method/constructor cdecl wrappers use (CdeclParamMapper Foundation.Data
                // arm). Admit it here so the predicate↔emitter contract holds — the param-render
                // switches below carry the matching NativeRemapped/Data arms.
                if (IsConcreteFoundationDataParam(arg))
                    continue;
                // A frozen, trivially-copyable struct param projects to a C# value struct
                // implementing ISwiftObject. The CSM param-render switches give it a pin-and-pass
                // arm (C# passes `(IntPtr)(&v)`; the Swift wrapper reads it back via
                // assumingMemoryBound(to:).pointee), so admit it here even though FrozenStruct is
                // not in the closure-bridge layer's passable set. ProjectsAsBlittableValueStruct
                // keeps it tight -- RMM/remapped/ObjC/non-copyable structs (no value-struct
                // projection) still reject.
                if (category == MethodClosureBridge.ParamAbiCategory.FrozenStruct
                    && arg.SwiftTypeSpec is NamedTypeSpec frozenParamNamed
                    && typeDatabase.TryGetTypeRecord(frozenParamNamed, out var frozenParamRecord)
                    && ProjectsAsBlittableValueStruct(frozenParamNamed, frozenParamRecord))
                    continue;
                return false;
            }

            // …but the KeyPathFamily renderer (BuildKeyPathPublicCSharpType) can only produce a
            // valid signature when every KeyPath generic argument resolves to a qualified C#
            // type. A foreign-framework root (e.g. CoreSpotlight's CSSearchableItemAttributeSet
            // in an AppIntents-only generation) has no TypeRecord and would render unqualified,
            // failing to compile. Reject the specialization rather than emit broken code —
            // mirroring the null-return the projection-based path already uses for the same case.
            if (category == MethodClosureBridge.ParamAbiCategory.KeyPathFamily &&
                arg.SwiftTypeSpec is NamedTypeSpec keyPathSpec &&
                !keyPathSpec.GenericParameters.All(g => IsKeyPathGenericArgResolvable(g, typeDatabase)))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// A concrete (non-generic) <c>Foundation.Data</c> parameter. The CSM param-render switches
    /// give it the two-Int-word decomposition arm (public <c>byte[]</c> on the C# side,
    /// <c>unsafeBitCast</c> reconstruction on the Swift side), so the compatibility preflight
    /// admits it even though it classifies as the not-otherwise-passable <c>NativeRemapped</c>
    /// category. Kept tight to Foundation.Data — other NativeRemapped types (NSString, NSURL, …)
    /// have no concrete-param renderer here and must still reject.
    /// </summary>
    private static bool IsConcreteFoundationDataParam(ArgumentDecl arg)
        => arg.SwiftTypeSpec is NamedTypeSpec named && named.Name == FoundationDataSwiftName;

    private const string FoundationDataSwiftName = "Foundation.Data";

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
                // Key off the emitted form so two pairings that produce identical C#
                // signatures (after SCREAMING_CASE canonicalization) collide here and
                // one of them is suppressed. Using the raw hint string would let a
                // shadow specialization slip through for SHA3_*-style conformers.
                parts.Add(ResolveConformerCSharpTypeRef(matchedConformer!, typeDatabase));
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

    /// <summary>
    /// Builds the public C# type spelling for a Swift KeyPath family parameter
    /// (<c>Swift.KeyPath&lt;TRoot, TValue&gt;</c>, <c>Swift.WritableKeyPath&lt;...&gt;</c>, etc.).
    /// The Swift KeyPath family has no <c>TypeRecord</c> in any database — the family lives
    /// only in <see cref="TypeProjectionFactory"/>'s arity table — so
    /// <see cref="ResolvePublicCSharpType"/>'s fallback would drop the <c>Swift.</c>
    /// qualifier (returning a bare <c>KeyPath&lt;R,V&gt;</c> that fails to resolve). This
    /// helper mirrors <see cref="KeyPathProjection"/>'s public-type construction so the
    /// CSM emitter renders the same shape as the property/accessor path.
    /// </summary>
    private static string BuildKeyPathPublicCSharpType(NamedTypeSpec keyPathTypeSpec, ITypeDatabase typeDatabase)
    {
        // KeyPathFamilyArities (private to TypeProjectionFactory) gates this — we trust
        // the caller has already classified the type as KeyPathFamily, so the prefix is
        // always "Swift." and the short name is the last segment.
        var shortName = keyPathTypeSpec.Name.Substring("Swift.".Length);
        if (keyPathTypeSpec.GenericParameters.Count == 0)
            return $"global::Swift.{shortName}";
        var genericArgs = keyPathTypeSpec.GenericParameters
            .Select(g => ResolveKeyPathGenericArgPublicType(g, typeDatabase));
        return $"global::Swift.{shortName}<{string.Join(", ", genericArgs)}>";
    }

    /// <summary>
    /// Idiomatic public-type rendering for a KeyPath family generic argument. Mirrors the OUT
    /// path's projection-chain output (e.g. <see cref="StringProjection"/>'s <c>"string"</c>,
    /// <see cref="BoolProjection"/>'s <c>"bool"</c>) so a <c>KeyPath&lt;R, Swift.String&gt;</c> param
    /// renders as <c>Swift.KeyPath&lt;R, string&gt;</c> — matching what factory methods like
    /// <c>KeyPathFactory.MakeTitlePath()</c> return. Without this, the param signature would
    /// use <see cref="ResolvePublicCSharpType"/>'s TypeRecord-derived form (<c>Swift.SwiftString</c>)
    /// and CS1503 would fire at the call site against a <c>KeyPath&lt;R, string&gt;</c> argument.
    /// </summary>
    /// <summary>
    /// Swift primitive → idiomatic C# keyword for KeyPath generic arguments. Single source of
    /// truth shared by <see cref="ResolveKeyPathGenericArgPublicType"/> (which renders the
    /// keyword) and <see cref="IsKeyPathGenericArgResolvable"/> (which treats membership as
    /// "always resolvable without a TypeRecord"), so the two cannot drift.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> s_keyPathPrimitiveCSharpRenders =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Swift.String"] = "string",
            ["Swift.Bool"]   = "bool",
            ["Swift.Int"]    = "nint",
            ["Swift.UInt"]   = "nuint",
            ["Swift.Int8"]   = "sbyte",
            ["Swift.Int16"]  = "short",
            ["Swift.Int32"]  = "int",
            ["Swift.Int64"]  = "long",
            ["Swift.UInt8"]  = "byte",
            ["Swift.UInt16"] = "ushort",
            ["Swift.UInt32"] = "uint",
            ["Swift.UInt64"] = "ulong",
            ["Swift.Float"]  = "float",
            ["Swift.Double"] = "double",
        };

    private static string ResolveKeyPathGenericArgPublicType(TypeSpec arg, ITypeDatabase typeDatabase)
    {
        if (arg is NamedTypeSpec named &&
            s_keyPathPrimitiveCSharpRenders.TryGetValue(named.Name, out var keyword))
        {
            return keyword;
        }
        return ResolvePublicCSharpType(arg, typeDatabase);
    }

    /// <summary>
    /// A KeyPath generic argument is admissible only when it renders to a QUALIFIED, in-scope
    /// C# type. <see cref="ResolvePublicCSharpType"/> falls back to an UNqualified bare name
    /// (<c>named.Name.Split('.').Last()</c>) for any named type with no <c>TypeRecord</c> — e.g.
    /// a foreign-framework KeyPath root like CoreSpotlight's <c>CSSearchableItemAttributeSet</c>
    /// in an AppIntents-only generation — yielding a <c>PartialKeyPath&lt;CSSearchableItemAttributeSet&gt;</c>
    /// that fails to compile (CS0246). The projection-based (non-CSM) path already drops such a
    /// KeyPath by returning <c>null</c> from <c>TypeProjectionFactory.Project</c>; this mirrors
    /// that so the admissibility predicate and the renderer agree. Recurses into nested generic
    /// arguments because the renderer renders those too.
    /// </summary>
    internal static bool IsKeyPathGenericArgResolvable(TypeSpec arg, ITypeDatabase typeDatabase)
    {
        // Non-named args render to "IntPtr" (resolvable); leave that path unchanged.
        if (arg is not NamedTypeSpec named)
            return true;

        if (s_keyPathPrimitiveCSharpRenders.ContainsKey(named.Name))
            return true;

        bool hasRecord;
        try
        {
            var typeName = SwiftTypeName.FromModuleQualifiedName(named.Name);
            hasRecord = typeDatabase.TryGetTypeRecord(typeName, out _);
        }
        catch (ArgumentException)
        {
            hasRecord = false;
        }
        if (!hasRecord)
            return false;

        return named.GenericParameters.All(g => IsKeyPathGenericArgResolvable(g, typeDatabase));
    }

    private static string ResolvePublicCSharpType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is NamedTypeSpec named)
        {
            string baseName;
            try
            {
                var typeName = SwiftTypeName.FromModuleQualifiedName(named.Name);
                baseName = typeDatabase.TryGetTypeRecord(typeName, out var record)
                    ? record.CSharpTypeName.FullyQualifiedName
                    : named.Name.Split('.').Last();
            }
            catch (ArgumentException)
            {
                baseName = named.Name.Split('.').Last();
            }

            // Bound-generic return/param types (e.g. `HashedAuthenticationCode<SHA256>`)
            // must carry their type arguments through to the emitted C# signature or
            // Roslyn reports CS0305 "requires N type arguments" on the resolved open
            // generic. Recurse into GenericParameters; non-generic args fall out with
            // the existing NamedTypeSpec path.
            if (named.GenericParameters.Count == 0)
                return baseName;

            var args = named.GenericParameters
                .Select(g => ResolvePublicCSharpType(g, typeDatabase));
            return $"{baseName}<{string.Join(", ", args)}>";
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

        var args = parentEntries.Select(p => ResolveConformerCSharpTypeRef(p.Conformer, typeDatabase));
        return $"{parentTypeDecl.Name}<{string.Join(", ", args)}>";
    }

    /// <summary>
    /// Normalizes a conformer's CSharpType (sourced verbatim from specialization-hints.json)
    /// to the identifier the generator actually emits. Swift SCREAMING_CASE types like
    /// <c>SHA3_256</c> flow through <see cref="NameProvider.ToPascalCaseForTypeName"/> to
    /// become <c>Sha3256</c>; using the raw hint value as a receiver-type argument produces
    /// references to types that don't exist (CS0246 / missing receiver). Dotted names are
    /// split and only the leaf is canonicalized so namespace-qualified hints survive.
    /// </summary>
    /// <summary>
    /// Canonicalizes a conformer's C# type as stored in specialization-hints.json
    /// (or derived from a Swift ABI name) into the identifier form actually emitted
    /// by the generator. Only applies to bare SCREAMING_CASE identifiers
    /// (e.g. <c>SHA3_256</c> → <c>Sha3256</c>); anything with namespace qualification,
    /// generic args, array brackets, pointers, or other non-identifier characters
    /// passes through unchanged so <c>byte[]</c>, <c>Foundation.Data</c>,
    /// <c>CryptoKit.SymmetricKey</c>, and generic specializations like
    /// <c>Array&lt;Byte&gt;</c> are preserved verbatim.
    /// </summary>
    internal static string CanonicalizeConformerCSharpType(string csharpType)
    {
        if (string.IsNullOrEmpty(csharpType))
            return csharpType;
        if (!IsBareScreamingCaseIdentifier(csharpType))
            return csharpType;
        return NameProvider.ToPascalCaseForTypeName(csharpType);
    }

    /// <summary>
    /// Resolves the C# type-reference name for a conformer as it is actually emitted, accounting
    /// for nested-type collision renames. <see cref="ConcreteSpecializationEngine.ConcreteConformer.CSharpType"/>
    /// is captured at conformance-index time (Program.cs), which runs BEFORE the nested-type rename
    /// pre-pass (<see cref="NameProvider.PrecomputeNestedTypeRenames"/>) mutates the type record's
    /// <c>CSharpTypeName</c> for sibling-member collisions — e.g. Swift <c>Codec.Encoding</c> becomes
    /// C# <c>Codec.EncodingKind</c> when <c>Codec</c> also exposes an <c>Encoding</c> property. For a
    /// nested conformer whose type record is live, re-resolve the post-rename fully-qualified name so
    /// the emitted overload references the type by the name actually declared. Flat conformers and
    /// hint conformers (<c>byte[]</c>, <c>Foundation.Data</c>) are never collision-renamed in-module,
    /// so they keep their cached name. The result is still routed through
    /// <see cref="CanonicalizeConformerCSharpType"/> for SCREAMING_CASE normalization.
    /// </summary>
    internal static string ResolveConformerCSharpTypeRef(
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        ITypeDatabase typeDatabase)
    {
        if (conformer.SwiftType != null &&
            conformer.SwiftType.ModuleQualifiedName.Split('.').Length > 2 &&
            typeDatabase.TryGetTypeRecord(conformer.SwiftType, out var record))
        {
            return CanonicalizeConformerCSharpType(record.CSharpTypeName.FullyQualifiedName);
        }
        return CanonicalizeConformerCSharpType(conformer.CSharpType);
    }

    internal static bool IsBareScreamingCaseIdentifier(string s)
    {
        if (string.IsNullOrEmpty(s))
            return false;
        if (s.IndexOf('_') < 0)
            return false;
        foreach (var ch in s)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '_'))
                return false;
        }
        return true;
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

                // Per-method where-clause filter: some methods (notably `init()` and
                // constrained extension methods) carry stricter parent-generic constraints
                // than the parent type declares. Without skipping these for non-satisfying
                // parent tuples, the generated Swift wrapper instantiates e.g.
                // `MusicCatalogResourceRequest<Album>()` even though `Album` does not
                // conform to `MusicCatalogTopLevelResourceRequesting`. The engine records
                // rejections so the emission report explains the absence.
                if (!engine.ParentTupleSatisfiesMethodConstraints(method, typeDecl, parentTuple))
                {
                    continue;
                }

                // Async methods on generic parents are emit-eligible IFF they
                // have zero method-own generic parameters (the "parent-only" shape). The
                // dispatch lives in TryEmitParentOnlyAsyncOverload which writes directly
                // into the already-open *CsmExtensions partial class. Async methods that
                // also have method-own generics are not yet supported — they fall through
                // to the continue below.
                if (method.IsAsync)
                {
                    var asyncMethodParams = spec.SpecializableParams
                        .Where(p => !p.IsParentGeneric)
                        .ToList();
                    if (asyncMethodParams.Count == 0)
                    {
                        TryEmitParentOnlyAsyncOverload(
                            csWriter, swiftWriter, method, typeDecl, parentTuple,
                            moduleName, wrapperLibPath, typeDatabase, emissionContext,
                            emittedSignatures, logger);
                    }
                    // Async with method-own generics still rejects — not yet supported.
                    continue;
                }
                // Throwing constructors are admitted here on the same footing as non-throwing
                // ones — see the top-level CSM path: `throws`/`IsConstructor` are orthogonal and
                // the shared ConstructorAdmissibility preflight gates the genuinely unsafe inits.

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
