// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Async-throws path for concrete protocol specialization. Synthesizes a concrete
/// <see cref="MethodDecl"/> per conformer, substitutes the generic parameter in the
/// signature, and reuses <see cref="WrapperEmitter"/> + <see cref="PInvokeEmitter"/>
/// to emit the async @_cdecl wrapper + CallConvCdecl P/Invoke. This delegates all
/// async marshalling (TaskCompletionSource, callbacks, cancellation, typed throws)
/// to the shared async harness instead of duplicating it in CSM.
/// </summary>
public static partial class ConcreteProtocolSpecializationEmitter
{
    internal static bool TryEmitConcreteOverloadAsync(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodDecl originalMethod,
        TypeDecl parentTypeDecl,
        IReadOnlyList<(ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)> pairing,
        string moduleName,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        ILogger logger)
    {
        // Phase B path accepts 1+ pairings. The first pairing's conformer is used for
        // symbol naming / logging — additional pairings are applied via substitution.
        var conformer = pairing[0].Conformer;

        if (!PassesAsyncMethodLevelGuards(originalMethod, parentTypeDecl, typeDatabase, logger))
            return false;

        if (!IsEmittableAsyncPairing(originalMethod, parentTypeDecl, pairing, typeDatabase, moduleName, out var conformerTypeSpecs, logger, originalMethod.Name))
            return false;

        if (!TryBuildEmissionPlan(
                originalMethod, parentTypeDecl, pairing, conformerTypeSpecs,
                typeDatabase, moduleName,
                out var synthesized, out var signatureHandler,
                out var cdeclSymbol, out var sigKey, logger))
            return false;

        // Dedup: shared with the Phase-4a predicate via ModuleEmissionContext. Commit
        // promotes the predicate's reservation to Emitted; a second pairing of the
        // same method that collapses to the same sigKey fails here, preventing CS0111
        // duplicate-member emissions without breaking predicate idempotence.
        if (!emissionContext.TryCommitCsmAsyncSignature(sigKey, originalMethod))
        {
            logger.LogDebug(
                "CSM-async: Skipping {Method} for {Conformer} — signature key {Sig} already emitted or claimed by another method.",
                originalMethod.Name, conformer.SwiftQualifiedName, sigKey);
            return false;
        }

        // Wrapper symbol dedup is independent — different methods/conformers should
        // never produce the same mangled wrapper symbol because the hash seed is
        // (method MangledName + all conformer qualified names).
        // same `SBW_CSM_` prefix as the sync path; this async-only
        // branch is gated by `IsAsync` upstream so the two paths never co-emit for the
        // same method. Per-kind method bucket is collision-safe.
        if (!emissionContext.TryAddMethodWrapperSymbol(cdeclSymbol))
        {
            return false;
        }

        var env = new MethodEnvironment(synthesized, typeDatabase);
        env.EmissionContext = emissionContext;

        csWriter.WriteLine();
        var wrapperEmitter = new WrapperEmitter(env, signatureHandler, null, emissionContext);
        wrapperEmitter.EmitMethod(csWriter, swiftWriter);
        PInvokeEmitter.EmitPInvoke(csWriter, env, signatureHandler);

        originalMethod.MarkEmitted();
        synthesized.MarkEmitted();

        // Emit trim overloads on the per-conformer specialized signature. The synthesized
        // MethodDecl has GenericParameters cleared (IsGeneric == false) and CSSignature
        // substituted with concrete conformer types, so the trim emitter's own bail on
        // method-level generics no longer fires and HasDefaultArg flags ride through.
        // Each trim variant emits its own _dbw_*/DBW_* Swift symbol whose body re-invokes
        // the original generic Swift method with the conformer-typed kept args; Swift
        // dispatches the generic at the call site and fills the trimmed defaults.
        //
        // Unlike the CSM-sync path, the CSM-async primary preserves mappable trailing
        // defaults inline (e.g. `nint tag = 13` on the C# surface), so a trim variant
        // that drops only the mappable suffix produces a signature ambiguous with the
        // primary at the call site (`AppendAsync(source, options)` would match both
        // `AppendAsync(source, options, tag = 13, ct)` and the trim-1 variant
        // `AppendAsync(source, options, ct)`). Pre-populate the projected-signature
        // dedup set with the keys the primary already covers via its inline mappable
        // defaults — those trim depths are redundant and would produce CS0121.
        env.EmittedProjectedSignatures = BuildMappableSuffixShadowKeys(synthesized, typeDatabase);

        DefaultParameterOverloadEmitter.TryEmitOverloads(
            csWriter, swiftWriter, env, logger, emissionContext);

        logger.LogInformation(
            "Emitted concrete async specialization: {Type}.{Method}<{Conformer}> → {Symbol}_async",
            parentTypeDecl.Name, originalMethod.Name, conformer.SwiftQualifiedName, cdeclSymbol);

        return true;
    }

    // ─── Shared dry-run plan (predicate + emitter) ─────────────────────

    /// <summary>
    /// Builds the full CSM-async emission plan for one pairing: cdecl wrapper symbol,
    /// substituted CSSignature, synthesized MethodDecl, SignatureHandler, and dedup
    /// sigKey. Returns false when substitution fails (unresolved
    /// <see cref="AssociatedTypeReferenceSpec"/>) or when the wrapper signature
    /// contains a placeholder type. Both the Phase-4a eligibility predicate and
    /// the real emitter go through this single path so they cannot diverge.
    /// </summary>
    private static bool TryBuildEmissionPlan(
        MethodDecl originalMethod,
        TypeDecl parentTypeDecl,
        IReadOnlyList<(ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)> pairing,
        NamedTypeSpec[] conformerTypeSpecs,
        ITypeDatabase typeDatabase,
        string moduleName,
        out MethodDecl synthesized,
        out SignatureHandler signatureHandler,
        out string cdeclSymbol,
        out string sigKey,
        ILogger? logger = null)
    {
        synthesized = null!;
        signatureHandler = null!;
        cdeclSymbol = null!;
        sigKey = null!;

        var safeConformerName = string.Join("_",
            pairing.Select(p => SanitizeTypeName(p.Conformer.SwiftQualifiedName)));
        var methodName = originalMethod.Name;
        var hashSeed = originalMethod.MangledName + string.Join("|",
            pairing.Select(p => p.Conformer.SwiftQualifiedName));
        var mangledHash = EmitterUtility.DeterministicHash8(hashSeed);
        cdeclSymbol = $"SBW_CSM_{moduleName}_{parentTypeDecl.Name}_{safeConformerName}_{methodName}_{mangledHash}";

        // Substitute each pairing's generic-param name → conformer TypeSpec. Applied
        // sequentially so AssociatedTypeReferenceSpec resolution sees earlier subs.
        var substitutedSignature = new List<ArgumentDecl>(originalMethod.CSSignature.Count);
        foreach (var arg in originalMethod.CSSignature)
            substitutedSignature.Add(arg);

        for (int i = 0; i < pairing.Count; i++)
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
                    logger?.LogDebug(
                        "CSM-async: Plan rejected for {Method}+{Conformer} — substitution failed (unresolved associated type).",
                        originalMethod.Name, pairing[i].Conformer.SwiftQualifiedName);
                    return false;
                }

                substitutedSignature[j] = arg with
                {
                    SwiftTypeSpec = substituted,
                    IsGeneric = arg.IsGeneric && ReferenceEquals(substituted, arg.SwiftTypeSpec) ? arg.IsGeneric : false,
                };
            }
        }

        var mergedAvailability = WrapperEmitterHelpers.MergeAvailability(
            originalMethod.AvailabilityAnnotations, parentTypeDecl);
        if (pairing[0].Conformer.AvailabilityAnnotations is { Count: > 0 } conformerAvailability)
        {
            var combined = mergedAvailability is null
                ? new List<AvailabilityAnnotation>()
                : new List<AvailabilityAnnotation>(mergedAvailability);
            combined.AddRange(conformerAvailability);
            mergedAvailability = combined;
        }

        synthesized = originalMethod with
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
        synthesized.UsesCdeclMethodWrapper = true;

        var env = new MethodEnvironment(synthesized, typeDatabase);
        signatureHandler = new SignatureHandler(env);
        if (signatureHandler.GetWrapperSignature().ContainsPlaceholder)
        {
            logger?.LogDebug(
                "CSM-async: Plan rejected for {Method}+{Conformer} — signature contains placeholder.",
                originalMethod.Name, pairing[0].Conformer.SwiftQualifiedName);
            return false;
        }

        var csMethodName = NameProvider.ToPascalCase(originalMethod.Name);
        sigKey = BuildAsyncSignatureKey(csMethodName, substitutedSignature, typeDatabase);

        return true;
    }

    // ─── TypeSpec substitution ──────────────────────────────────────────

    /// <summary>
    /// Recursively substitutes occurrences of <paramref name="genericName"/> (and its
    /// alternate-depth form) inside <paramref name="typeSpec"/> with
    /// <paramref name="conformerTypeSpec"/>. Resolves
    /// <see cref="AssociatedTypeReferenceSpec"/> using <paramref name="conformer"/>'s
    /// <see cref="ConcreteSpecializationEngine.ConcreteConformer.AssociatedTypes"/> map.
    /// Sets <paramref name="ok"/> = false if an associated-type reference cannot be resolved.
    /// </summary>
    private static TypeSpec SubstituteTypeSpec(
        TypeSpec typeSpec,
        string genericName,
        string altGenericName,
        NamedTypeSpec conformerTypeSpec,
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        ref bool ok)
    {
        switch (typeSpec)
        {
            case AssociatedTypeReferenceSpec assocRef:
                if (assocRef.BaseType == genericName || assocRef.BaseType == altGenericName)
                {
                    if (conformer.AssociatedTypes is { } at
                        && at.TryGetValue(assocRef.AssociatedTypeName, out var concreteName))
                    {
                        if (TryBuildNamedTypeSpecFromQualifiedName(concreteName, out var concrete))
                            return concrete;
                    }
                    // Unresolved associated type → caller falls back.
                    ok = false;
                }
                return typeSpec;

            case NamedTypeSpec named:
                if (named.Name == genericName || named.Name == altGenericName)
                {
                    return CloneNamedTypeSpec(conformerTypeSpec);
                }
                if (named.GenericParameters.Count == 0)
                {
                    return typeSpec;
                }
                var newNamed = new NamedTypeSpec(named.Name);
                CopyTypeSpecProps(named, newNamed);
                foreach (var gp in named.GenericParameters)
                {
                    newNamed.GenericParameters.Add(
                        SubstituteTypeSpec(gp, genericName, altGenericName,
                            conformerTypeSpec, conformer, ref ok));
                }
                return newNamed;

            case TupleTypeSpec tuple:
                var newElements = new List<TypeSpec>(tuple.Elements.Count);
                foreach (var e in tuple.Elements)
                {
                    newElements.Add(SubstituteTypeSpec(
                        e, genericName, altGenericName, conformerTypeSpec, conformer, ref ok));
                }
                var newTuple = new TupleTypeSpec(newElements);
                CopyTypeSpecProps(tuple, newTuple);
                return newTuple;

            case ClosureTypeSpec closure:
                var newArgs = SubstituteTypeSpec(
                    closure.Arguments, genericName, altGenericName,
                    conformerTypeSpec, conformer, ref ok);
                var newReturn = SubstituteTypeSpec(
                    closure.ReturnType, genericName, altGenericName,
                    conformerTypeSpec, conformer, ref ok);
                var newClosure = new ClosureTypeSpec(newArgs, newReturn)
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

    private static NamedTypeSpec CloneNamedTypeSpec(NamedTypeSpec source)
    {
        var clone = new NamedTypeSpec(source.Name);
        CopyTypeSpecProps(source, clone);
        foreach (var gp in source.GenericParameters)
        {
            clone.GenericParameters.Add(CloneTypeSpecShallow(gp));
        }
        return clone;
    }

    private static TypeSpec CloneTypeSpecShallow(TypeSpec t)
    {
        if (t is NamedTypeSpec n) return CloneNamedTypeSpec(n);
        return t;
    }

    private static void CopyTypeSpecProps(TypeSpec source, TypeSpec dest)
    {
        dest.IsInOut = source.IsInOut;
        dest.IsAny = source.IsAny;
        dest.TypeLabel = source.TypeLabel;
        dest.IsVariadic = source.IsVariadic;
        foreach (var attr in source.Attributes)
            dest.Attributes.Add(attr);
    }

    // ─── Conformer → NamedTypeSpec ──────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="NamedTypeSpec"/> for a conformer. Prefers
    /// <see cref="ConcreteSpecializationEngine.ConcreteConformer.SwiftType"/> when set,
    /// otherwise parses <see cref="ConcreteSpecializationEngine.ConcreteConformer.SwiftQualifiedName"/>
    /// which may include angle-bracket generics (e.g., "Swift.Array&lt;Swift.String&gt;").
    /// </summary>
    private static bool TryBuildConformerTypeSpec(
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        out NamedTypeSpec typeSpec)
    {
        if (conformer.SwiftType != null)
        {
            typeSpec = new NamedTypeSpec(conformer.SwiftType.ModuleQualifiedName);
            return true;
        }
        return TryBuildNamedTypeSpecFromQualifiedName(conformer.SwiftQualifiedName, out typeSpec);
    }

    /// <summary>
    /// Parses a module-qualified Swift type name — possibly with generic parameters in
    /// angle brackets — into a <see cref="NamedTypeSpec"/>. Supports one level of nesting
    /// and comma-separated generic arguments (e.g., "Swift.Dictionary&lt;A, B&gt;").
    /// </summary>
    private static bool TryBuildNamedTypeSpecFromQualifiedName(string qualifiedName, out NamedTypeSpec spec)
    {
        spec = null!;
        if (string.IsNullOrWhiteSpace(qualifiedName)) return false;

        var angleOpen = qualifiedName.IndexOf('<');
        if (angleOpen < 0)
        {
            spec = new NamedTypeSpec(qualifiedName);
            return true;
        }

        if (!qualifiedName.EndsWith('>')) return false;

        var baseName = qualifiedName.Substring(0, angleOpen);
        var generics = qualifiedName.Substring(angleOpen + 1, qualifiedName.Length - angleOpen - 2);

        var parts = SplitGenericArgs(generics);
        var result = new NamedTypeSpec(baseName);
        foreach (var part in parts)
        {
            if (!TryBuildNamedTypeSpecFromQualifiedName(part.Trim(), out var childSpec))
                return false;
            result.GenericParameters.Add(childSpec);
        }
        spec = result;
        return true;
    }

    private static List<string> SplitGenericArgs(string inner)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 0)
            {
                result.Add(inner.Substring(start, i - start));
                start = i + 1;
            }
        }
        result.Add(inner.Substring(start));
        return result;
    }

    // ─── Trim-emitter dedup seeding ─────────────────────────────────────

    /// <summary>
    /// Computes the projected-overload keys for trim depths the CSM-async primary
    /// already covers via its inline mappable defaults.
    ///
    /// The CSM-async primary preserves trailing defaults: mappable ones render as
    /// inline C# defaults (`nint tag = 13`), non-mappable ones force the caller to
    /// pass an explicit value (`IReadOnlySet&lt;nint&gt; options`). A trim variant that
    /// drops only the mappable suffix would be ambiguous with the primary at the call
    /// site — both signatures resolve for the same kept-prefix args. Walk the
    /// rightmost contiguous run of mappable trailing defaults and add the trim key
    /// for each depth so <see cref="DefaultParameterOverloadEmitter.TryEmitOverloads"/>
    /// skips them via <see cref="MethodEnvironment.EmittedProjectedSignatures"/>.
    ///
    /// Stops at the first non-mappable trailing default — deeper trims drop a
    /// non-mappable param that the primary cannot omit, so they expose a genuinely
    /// new public surface and must still emit.
    /// </summary>
    private static HashSet<string> BuildMappableSuffixShadowKeys(
        MethodDecl synthesized, ITypeDatabase typeDatabase)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var args = synthesized.CSSignature.Skip(1).ToList();
        var visibleGenericNames = BaseHandler.CollectVisibleGenericParamNames(synthesized);

        int suffixLen = 0;
        for (int i = args.Count - 1; i >= 0; i--)
        {
            if (DefaultParameterOverloadEmitter.IsDebugParameter(args[i]))
                continue;
            if (!args[i].HasDefaultArg)
                break;
            if (args[i].SwiftDefaultExpression == null)
                break;
            var mapped = SwiftDefaultValueMapper.TryMapToCSharpDefault(
                args[i].SwiftDefaultExpression!, args[i].SwiftTypeSpec, typeDatabase,
                visibleGenericNames);
            if (mapped == null)
                break;

            suffixLen++;
            var trimDecl = DefaultParameterOverloadEmitter.BuildOverloadDecl(synthesized, suffixLen);
            keys.Add(DefaultParameterOverloadEmitter.GetProjectedOverloadKey(trimDecl, typeDatabase));
        }
        return keys;
    }

    // ─── Signature key ──────────────────────────────────────────────────

    private static string BuildAsyncSignatureKey(
        string methodName,
        List<ArgumentDecl> substitutedSignature,
        ITypeDatabase typeDatabase)
    {
        var parts = new List<string> { methodName };
        foreach (var arg in substitutedSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (arg.HasDefaultArg) continue;
            parts.Add(arg.SwiftTypeSpec.ToString());
        }
        return string.Join("|", parts);
    }

    // ─── Shared guards ──────────────────────────────────────────────────

    /// <summary>
    /// Method-level CSM-async guards that don't depend on a specific conformer pairing.
    /// Both <see cref="TryEmitConcreteOverloadAsync"/> and <see cref="IsCsmAsyncEligible"/>
    /// share this so the predicate can't be looser than the emitter.
    /// </summary>
    private static bool PassesAsyncMethodLevelGuards(
        MethodDecl method,
        TypeDecl parentTypeDecl,
        ITypeDatabase typeDatabase,
        ILogger? logger = null)
    {
        if (!method.IsAsync) return false;
        // Generic parents are no longer blanket-rejected here. Closed-conformer
        // async CSM (this file's path) still rejects them — that case requires method-own
        // generics which would leak `Item.X` placeholders into the wrapper signature.
        // Parent-only async CSM (no method-own generics) is routed separately through
        // `IsCsmAsyncEligibleForGenericParent` and emitted via
        // `TryEmitParentOnlyAsyncOverload` inside `EmitConcreteSpecializationsForGenericParent`.
        // The check below stays scoped to the closed-conformer async path: a generic parent
        // here means we'd produce open-generic spellings and is unsupported on this branch.
        if (parentTypeDecl.IsGeneric)
        {
            logger?.LogDebug(
                "CSM-async (closed-conformer): Skipping {Method} — generic parent routes through parent-only async path.",
                method.Name);
            return false;
        }
        if (method.HasTypedThrows)
        {
            logger?.LogDebug("CSM-async: Skipping {Method} — typed throws not yet supported.", method.Name);
            return false;
        }
        if (method.IsMainActorIsolated || method.IsActorIsolated)
        {
            logger?.LogDebug("CSM-async: Skipping {Method} — actor-isolated.", method.Name);
            return false;
        }
        if (method.IsAccessor || method.IsMutating || method.IsConstructor)
        {
            logger?.LogDebug(
                "CSM-async: Skipping {Method} — accessor/mutating/constructor not in Phase A scope.",
                method.Name);
            return false;
        }
        if (!WrapperValidation.IsXCFrameworkMode(typeDatabase)) return false;
        return true;
    }

    /// <summary>
    /// Per-pairing CSM-async guards that must hold for every conformer in the pairing:
    /// associated-type constraint satisfaction (parity with the sync path's bilateral
    /// filter — class-subtype <c>where S.Element : Animal</c> and same-type
    /// <c>where S.Element == Album</c> bounds reject mismatched conformers like
    /// <c>[UInt8]</c> or <c>[SongItem]</c> here, before any wrapper symbol is reserved),
    /// Phase A hint-scope gate, opaque-parameter guard, nested-conformer guard,
    /// NativeTypeName/ObjC-bridged/rooted rejection, and TypeSpec buildability.
    /// Returns the built <paramref name="conformerTypeSpecs"/> on success so callers
    /// don't redo the work.
    /// </summary>
    private static bool IsEmittableAsyncPairing(
        MethodDecl method,
        TypeDecl parentTypeDecl,
        IReadOnlyList<(ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)> pairing,
        ITypeDatabase typeDatabase,
        string? moduleName,
        out NamedTypeSpec[] conformerTypeSpecs,
        ILogger? logger = null,
        string? methodNameForLog = null)
    {
        conformerTypeSpecs = new NamedTypeSpec[pairing.Count];

        // Bilateral associated-type filter — parity with the sync path
        // (CanEmitConcreteOverloadForPairing → DoesPairingSatisfyAssociatedTypeConstraints).
        // Without this, parent-declared same-type floors and method class-subtype bounds
        // (e.g. `where S.Element : Animal`) would slip through into the cartesian product
        // and the engine would emit an `_async` cdecl trampoline + corresponding
        // `[LibraryImport]` for every Sequence conformer (UInt8, SongItem, AlbumItem, …),
        // referencing wrapper symbols that the Swift side never compiles. First call →
        // EntryPointNotFoundException at runtime.
        if (!DoesPairingSatisfyAssociatedTypeConstraints(method, parentTypeDecl, pairing, typeDatabase))
        {
            logger?.LogDebug(
                "CSM-async: Skipping {Method} — associated-type constraint not satisfied by conformer pairing.",
                methodNameForLog);
            return false;
        }

        for (int i = 0; i < pairing.Count; i++)
        {
            var param = pairing[i].Param;
            var conformer = pairing[i].Conformer;

            if (!ConcreteSpecializationEngine.HasKnownHintConformers(
                    param.ConstraintProtocol.ToString(), moduleName))
            {
                logger?.LogDebug(
                    "CSM-async: Skipping {Method} — constraint {Protocol} not in specialization-hints.json (Phase A scope, module={Module}).",
                    methodNameForLog, param.ConstraintProtocol, moduleName);
                return false;
            }

            if (!ConcreteSpecializationEngine.IsConformerAllowedForModule(conformer, moduleName))
            {
                logger?.LogDebug(
                    "CSM-async: Skipping {Method} for {Conformer} — conformer is module-scoped and {Module} is not in its allow-list.",
                    methodNameForLog, conformer.SwiftQualifiedName, moduleName);
                return false;
            }

            if (param.GenericParam.TypeName.StartsWith("τ_opaque_", StringComparison.Ordinal)
                && param.GenericParam.AssosiatedTypeConformances.Count == 0
                && conformer.AssociatedTypes is { Count: > 0 })
            {
                logger?.LogDebug(
                    "CSM-async: Skipping {Method} — opaque `some Protocol<X>` parameter with dropped primary associated type.",
                    methodNameForLog);
                return false;
            }

            // Nested-conformer guard. Unlike the synchronous CSM path — which resolves
            // nested-type conformers and emits them by their post-rename C# name — the async
            // path hard-rejects them. Async CSM specializes only hint-registered conformers,
            // and no nested-type hint conformer exists; the shapes that drive nested conformers
            // (HPKE Sender/Recipient inits) are all synchronous. Keeping the reject avoids
            // duplicating the sync re-resolution on a surface that has nothing to name.
            if (conformer.SwiftType != null &&
                conformer.SwiftType.ModuleQualifiedName.Split('.').Length > 2)
            {
                return false;
            }

            if (conformer.SwiftType != null &&
                typeDatabase.TryGetTypeRecord(conformer.SwiftType, out var conformerRecord) &&
                (conformerRecord.NativeTypeName != null
                    || MarshallingHelpers.IsObjCBridged(conformerRecord)
                    || MarshallingHelpers.IsObjCRooted(conformerRecord)))
            {
                return false;
            }

            if (!TryBuildConformerTypeSpec(conformer, out var spec))
            {
                logger?.LogDebug(
                    "CSM-async: Skipping {Method} for {Conformer} — cannot build TypeSpec for conformer.",
                    methodNameForLog, conformer.SwiftQualifiedName);
                return false;
            }
            conformerTypeSpecs[i] = spec;
        }

        return true;
    }

    // ─── Phase D eligibility (used by MemberValidationPipeline) ─────────

    /// <summary>
    /// Returns true if the method will be routed through the CSM-async emission path,
    /// meaning the pipeline's unspecialized generic emission should be suppressed.
    /// Runs a full dry-run of <see cref="TryBuildEmissionPlan"/> for each cartesian
    /// pairing (substitution + signature-placeholder check) and, on first success,
    /// claims the dedup sigKey on <paramref name="emissionContext"/>. Both the
    /// structural checks and the dedup claim are shared with
    /// <see cref="TryEmitConcreteOverloadAsync"/>, so a predicate pass cannot claim
    /// suppressibility for a method that the emitter will then drop.
    /// </summary>
    public static bool IsCsmAsyncEligible(
        MethodDecl method,
        TypeDecl parentTypeDecl,
        ITypeDatabase typeDatabase,
        ConcreteSpecializationEngine engine,
        ModuleEmissionContext emissionContext)
    {
        if (!PassesAsyncMethodLevelGuards(method, parentTypeDecl, typeDatabase)) return false;

        var parentParamNames = parentTypeDecl.IsGeneric
            ? new HashSet<string>(parentTypeDecl.GenericParameters.Select(p => p.TypeName))
            : new HashSet<string>();
        var ownParamCount = method.GenericParameters.Count(p => !parentParamNames.Contains(p.TypeName));
        if (ownParamCount == 0) return false;

        var specializable = engine.FindSpecializableMethods(parentTypeDecl)
            .FirstOrDefault(sm => ReferenceEquals(sm.Method, method));
        if (specializable is null) return false;

        // Require EVERY method-own generic param to be specializable; a partially-specialized
        // signature can't emit because the remaining params would stay generic.
        if (specializable.SpecializableParams.Count != ownParamCount) return false;

        var moduleName = parentTypeDecl.SwiftTypeName.Module;

        // Short-circuit the predicate when the cartesian product would blow up; the
        // emitter path applies the same cap, so declaring ineligible here keeps the
        // predicate consistent with what the emitter will actually do.
        if (ComputePairingCount(specializable.SpecializableParams) > MaxCsmCartesianProductSize)
        {
            return false;
        }

        // For each cartesian pairing: coupling + per-pairing structural guards + full
        // dry-run plan + dedup claim. The first pairing that passes all four makes the
        // method suppressible. TryClaim is idempotent for the same (key, owner), so
        // the emitter will later observe the same successful pairing for this method.
        foreach (var pairing in CartesianPairings(specializable.SpecializableParams))
        {
            if (!ConformerPairingSatisfiesCoupling(pairing)) continue;
            if (!IsEmittableAsyncPairing(method, parentTypeDecl, pairing, typeDatabase, moduleName, out var conformerTypeSpecs)) continue;
            if (!TryBuildEmissionPlan(
                    method, parentTypeDecl, pairing, conformerTypeSpecs,
                    typeDatabase, moduleName,
                    out _, out _, out _, out var sigKey))
                continue;
            if (emissionContext.TryReserveCsmAsyncSignature(sigKey, method))
                return true;
        }

        return false;
    }
}
