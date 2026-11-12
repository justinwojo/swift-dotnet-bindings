// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Emits C# overloads for methods with trailing default parameters.
/// For each overload, generates a Swift wrapper that calls the original method
/// with fewer arguments (letting Swift supply the defaults) and a corresponding
/// C# method + P/Invoke pointing to the wrapper.
/// </summary>
public static class DefaultParameterOverloadEmitter
{
    // Closure + inout String previously reached a silgen primary (then recovery could
    // withdraw it), while its independent trim was already emitted from the original
    // Swift symbol. Qualifying the primary for Cdecl must not rename that shipped trim.
    private static string GetOverloadSymbolBase(MethodEnvironment env)
        => env.MethodDecl.HasClosureCdeclWrapper && env.MethodDecl.UsesCdeclMethodWrapper &&
           env.MethodDecl.CSSignature.Skip(1).Any(a => a.IsInOut && MarshallingHelpers.IsSwiftString(a.SwiftTypeSpec))
            ? env.MethodDecl.MangledName
            : env.EmissionSymbol;

    /// <summary>
    /// Maximum number of overloads to generate per method.
    /// Limits combinatorial explosion for methods with many defaults.
    /// </summary>
    private const int MaxOverloads = 4;

    /// <summary>
    /// Same as <see cref="TryEmitOverloads"/> but reports whether at least one overload was
    /// actually emitted. A denial comment is not a recovered callable.
    ///
    /// Used by the placeholder-rejection recovery path in the method/constructor handlers: when a
    /// full signature is unbindable because a TRAILING DEFAULTED parameter resolves to the
    /// <c>AnyType</c> placeholder (e.g. a defaulted parameter whose type is an unmapped platform
    /// enum), the full form is genuinely unavailable — but Swift supplies the trailing defaults, so
    /// a truncated overload that omits the unbindable tail binds cleanly. <see cref="TryEmitOverloads"/>
    /// already re-validates every trimmed variant for placeholders and sibling collisions, so it
    /// emits only the variants whose remaining parameters are all bindable, and nothing when the
    /// placeholder sits in a required parameter or the return type (no trim can remove it). The
    /// caller uses the return value to suppress the loud "unsupported" drop comment when a working
    /// truncated form was recovered, so it does not sit under a misleading drop notice.
    /// </summary>
    public static bool TryEmitRecoveryOverloads(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ILogger logger,
        ModuleEmissionContext? emissionContext = null)
    {
        return TryEmitOverloadsWithRecipe(csWriter, swiftWriter, env, logger, emissionContext);
    }

    /// <summary>
    /// Emits overloads for a method with trailing default parameters.
    /// Called after the primary method has already been emitted.
    /// </summary>
    public static void TryEmitOverloads(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ILogger logger,
        ModuleEmissionContext? emissionContext = null)
    {
        TryEmitOverloadsWithRecipe(csWriter, swiftWriter, env, logger, emissionContext);
    }

    private static bool TryEmitOverloadsWithRecipe(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ILogger logger,
        ModuleEmissionContext? emissionContext)
    {
        emissionContext ??= env.EmissionContext;
        DefaultOverloadReplayRecipe? recipe = null;
        var producerEnv = env;
        if (emissionContext != null && emissionContext.TryGetDefaultOverloadRecipe(env.SourceDeclId, out var previous))
        {
            recipe = previous;
            producerEnv = recipe.CreateEnvironment(env);
            producerEnv.EmissionContext = emissionContext;
        }
        var emitted = TryEmitOverloadsCore(csWriter, swiftWriter, producerEnv, logger, emissionContext);
        // The core reads producer inputs and mutates its candidate declarations/environments.
        // Capture only when a real bundle completed, preserving cheap declines (including unknown
        // TypeSpecs on methods with no defaults). A swallowed containment signal abandons the render.
        if (emitted && emissionContext != null && EmissionAttempt.Current?.Abandoned != true)
        {
            recipe ??= DefaultOverloadReplayRecipe.Capture(producerEnv);
            emissionContext.RecordDefaultOverloadRecipe(recipe.SourceDeclId, recipe);
        }
        return emitted;
    }

    private static bool TryEmitOverloadsCore(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ILogger logger,
        ModuleEmissionContext? emissionContext)
    {
        var methodDecl = env.MethodDecl;
        int emittedCount = 0;

        // Skip property accessors
        if (methodDecl.IsAccessor)
            return false;

        // Skip module-internal methods
        if (methodDecl.IsModuleInternal)
            return false;

        // Skip methods on internal parent types.
        // Note: nested types (e.g. OuterType.NestedType) are not registered in TypeDatabase,
        // so only check IsModuleInternal — TryGetTypeRecord would incorrectly reject them.
        if (methodDecl.ParentDecl is TypeDecl parentTypeDecl && parentTypeDecl.IsModuleInternal)
            return false;

        // Skip methods on generic parent types — Swift extension syntax can't express
        // the generic parameters (e.g., `extension Keyframe` instead of `extension Keyframe<T>`),
        // and generic type params (τ_0_0) in parameter types aren't valid Swift identifiers.
        if (methodDecl.ParentDecl is TypeDecl parentType && parentType.IsGeneric)
            return false;

        // Skip methods with method-level generics whose own params can't be expressed
        // in the @_silgen_name shim signature. We allow class-bound non-CSM async/throws
        // generics (the StoreKit2 `purchase<S: UIScene>(... options: Set<…> = [])` shape):
        // AsyncMethodGenericBridgeEmitter emits the primary @_cdecl overload, and the
        // method-own generics + where-clause are threaded through the trim @_silgen_name
        // shim signature below so the trim variants compile.
        //
        // For everything else, the @_silgen_name shim can't express raw ABI type params
        // (τ_0_0, τ_0_1, ...) — bail.
        if (methodDecl.IsGeneric &&
            !AsyncMethodGenericBridgeEmitter.IsEligible(methodDecl, env.TypeDatabase))
            return false;

        var trailingDefaultCount = CountTrailingDefaults(methodDecl);

        // Every defaulted parameter, interior ones included. CountTrailingDefaults only sees the
        // contiguous run at the end, so a method whose defaults are ALL interior counts zero while
        // still stranding the shortest callable form — the gate can't key on the trailing count alone.
        var allArgs = methodDecl.CSSignature.Skip(1).ToList();
        var defaultedIndices = new List<int>();
        for (int i = 0; i < allArgs.Count; i++)
        {
            if (IsDebugParameter(allArgs[i]))
                continue; // debug params are stripped from the public signature, not "defaults"
            if (allArgs[i].HasDefaultArg)
                defaultedIndices.Add(i);
        }

        if (trailingDefaultCount == 0 && defaultedIndices.Count == 0)
            return false;

        // Default-parameter overloads on custom-global-actor-isolated parents emit as
        // `extension Type { static func _dbw_*(...) }`, and those extensions inherit the
        // type's actor isolation. Synchronous constructors are wholesale-skipped via
        // SWIFTBIND022 in MethodHandler (no synchronous entry into custom global-actor
        // isolation from a foreign runtime), so any sync overload extension would be
        // dead code. Async constructors are surfaced as `static Task<T> CreateAsync`
        // factories; the implicit actor hop at the `await` inside the wrapper lets the
        // overload extension legally call the actor-isolated init, so trimmed factory
        // overloads route through the same async-factory pipeline as the primary.
        if (methodDecl.ParentDecl is TypeDecl actorIsolatedParent &&
            actorIsolatedParent.IsCustomActorIsolated &&
            !(methodDecl.IsConstructor && methodDecl.IsAsync))
        {
            logger.LogInformation(
                "SWIFTBIND022: Skipping default-parameter overloads for '{Name}' on '{ParentName}' — " +
                "the custom global actor ('{Isolator}') isolating this type has no synchronous-entry " +
                "mechanism we can wrap, so the synchronous projection is skipped wholesale; the overload " +
                "extension would be unreachable. Async constructors are exempt and route through the " +
                "async-factory pipeline. The primary method signature is unaffected.",
                methodDecl.Name,
                actorIsolatedParent.Name,
                actorIsolatedParent.CustomActorIsolatorName ?? "<unknown>");
            return false;
        }

        // When all trailing defaults have C#-mappable inline values the primary signature already
        // carries `= value` defaults, so the trailing-trim variants would add nothing. That is NOT
        // true of the all-defaults form below: a default that sits BEFORE a required parameter can
        // never become a C# optional (C# optionals must be trailing), so the ergonomic
        // "supply only what Swift requires" form is unreachable from the primary alone. A primary
        // that omits every default (PrimaryOmitsDefaultedParameters) carries no inline tail at all,
        // so for it the trailing trims are the only way to pass a mappable default.
        bool trailingDefaultsAreMappable = !env.PrimaryOmitsDefaultedParameters &&
            AllTrailingDefaultsAreCSharpMappable(methodDecl, env.TypeDatabase);

        // The all-defaults form is redundant when the trim loop already reaches it (every default is
        // trailing AND the run fits under the cap) or when the primary already IS it (every default
        // is trailing and C#-expressible inline). Otherwise it is the only way to reach the shortest
        // callable form — which is exactly the case a method with an interior default, or with more
        // trailing defaults than the cap allows, strands today.
        bool everyDefaultIsTrailing = defaultedIndices.Count == trailingDefaultCount;
        bool allDefaultsAlreadyReachable = everyDefaultIsTrailing &&
            (trailingDefaultCount <= MaxOverloads || trailingDefaultsAreMappable);
        bool wantAllDefaultsForm = defaultedIndices.Count > 0
            && !allDefaultsAlreadyReachable
            && CanOmitEveryDefault(allArgs, defaultedIndices);

        // The reduced call has to reach THIS declaration on the Swift side too. Omitting arguments
        // makes the call shorter, and a shorter call fits MORE of the declarations sharing this Swift
        // name: a sibling that needs fewer defaults, or a favored one over this disfavored one, would
        // silently take the call, and an equally good one makes swiftc reject the shim as ambiguous.
        // The candidate loop below plans every candidate's call; this form is decided here because
        // the answer feeds the cap arithmetic.
        if (wantAllDefaultsForm)
        {
            var allDefaultsDropped = new HashSet<ArgumentDecl>(
                defaultedIndices.Select(i => allArgs[i]), ReferenceEqualityComparer.Instance);
            var allDefaultsPlan = SwiftOverloadCallPinning.Plan(methodDecl, allDefaultsDropped.Contains, env.TypeDatabase);
            if (allDefaultsPlan.IsRefused)
            {
                logger.LogInformation(
                    "Skipping the all-defaults overload for '{Name}': omitting every defaulted " +
                    "argument leaves a Swift call that resolves to the same-named '{Sibling}', and no " +
                    "omitted default can be spelled out to keep it on this declaration. The primary " +
                    "signature and the trailing-trim overloads are unaffected.",
                    methodDecl.Name,
                    allDefaultsPlan.WinningSibling);
                emissionContext?.TryRecordSuppressedAmbiguousOverload(
                    GetProjectedOverloadKey(
                        BuildOverloadDeclDropping(GetOverloadSymbolBase(env), methodDecl, defaultedIndices),
                        env.TypeDatabase, env.SiblingPropertyNames, env.DisambiguatedNameInput),
                    "Swift " + allDefaultsPlan.WinningSibling,
                    candidateWasFuller: false);
                wantAllDefaultsForm = false;
            }
            // The C# side of the same question, asked HERE rather than in the candidate loop because
            // the answer changes the cap arithmetic below: the all-defaults form spends one of the
            // MaxOverloads slots, so a candidate the sibling-collision rule will throw away anyway must
            // not also cost a trailing trim that would otherwise have been emitted.
            //
            // Report it the way the candidate loop reports its own declines. This arm runs BEFORE
            // the loop, so without an explicit log + report row the ergonomic form disappears with
            // no trace anywhere — the one decline in this emitter a reader could not account for.
            else
            {
                var allDefaultsDecl = BuildOverloadDeclDropping(GetOverloadSymbolBase(env), methodDecl, defaultedIndices);
                var declinedKey = GetProjectedOverloadKey(
                    allDefaultsDecl, env.TypeDatabase, env.SiblingPropertyNames, env.DisambiguatedNameInput);
                if (TryFindSignatureCollision(
                        methodDecl, allDefaultsDecl, declinedKey,
                        OverloadAmbiguityGuard.RequiredCountFor(allDefaultsDecl, env.TypeDatabase, declinedKey),
                        env, out var collidingKey))
                {
                    logger.LogDebug(
                        "DefaultParameterOverload: declining the all-defaults overload for {Name} — a consumer " +
                        "could not tell it from the existing sibling {Other}: {Key}",
                        methodDecl.Name, collidingKey, declinedKey);
                    emissionContext?.TryRecordSuppressedAmbiguousOverload(
                        declinedKey, collidingKey, candidateWasFuller: false);
                    wantAllDefaultsForm = false;
                }
            }
        }

        if (trailingDefaultsAreMappable && !wantAllDefaultsForm)
            return false;

        // Cap accounting: the trim ladder keeps the WHOLE MaxOverloads budget, and the all-defaults
        // form is added on top of it rather than taking one of its slots. Charging both to one budget
        // looks like a fair trade but is not a symmetric one: the slot it takes is the deepest trim,
        // i.e. the shortest arity the ladder had, so a method whose trailing-default run exceeds the
        // cap silently LOSES a public overload that consumers already call and gains a shorter one
        // that no existing call site uses — a source break handed out in exchange for an ergonomic
        // addition. Emission stays bounded: the extra form is at most one per method, and only for a
        // method whose shortest callable form the trim ladder cannot reach at all.
        //
        // When the primary already carries the whole inline default tail, the trim variants stay
        // declined (that was this method's historical early-out) and only the all-defaults form is
        // added.
        var overloadCount = trailingDefaultsAreMappable
            ? 0
            : Math.Min(trailingDefaultCount, MaxOverloads);

        // Candidate list, most-trimmed first (fewest params first in source output). DropIndices is
        // null for a plain trailing trim — those keep routing through the historical builder so the
        // shape that has always worked is byte-for-byte unchanged.
        var candidateSpecs = new List<(int SymbolTrim, IReadOnlyList<int>? DropIndices)>();
        if (wantAllDefaultsForm)
            candidateSpecs.Add((defaultedIndices.Count, defaultedIndices));
        for (int trim = overloadCount; trim >= 1; trim--)
            candidateSpecs.Add((trim, null));

        // PHASE 1 — build and validate every candidate WITHOUT writing any of it. The set has to be
        // enumerable before the first write, because a candidate can be mutually ambiguous with a
        // sibling candidate and the survivor of such a pair is the FULLER signature, which this loop
        // has not reached yet when it emits the shorter one.
        var prepared = new List<PreparedOverload>();
        foreach (var (symbolTrim, dropIndices) in candidateSpecs)
        {
            int trim = symbolTrim;
            var overloadDecl = dropIndices == null
                ? BuildOverloadDecl(GetOverloadSymbolBase(env), methodDecl, trim)
                : BuildOverloadDeclDropping(GetOverloadSymbolBase(env), methodDecl, dropIndices);
            var droppedArgs = new HashSet<ArgumentDecl>(
                (dropIndices ?? Enumerable.Range(allArgs.Count - trim, trim).ToList()).Select(i => allArgs[i]),
                ReferenceEqualityComparer.Instance);

            // Identity is fixed by the captured producer symbol. Deny before preparation or
            // reservations so a withdrawn trim cannot consume a healthy sibling's name.
            using var candidateCsScope = csWriter.BeginFragment(FragmentOwners.ForDecl(overloadDecl));
            using var candidateSwiftScope = swiftWriter.BeginFragment(FragmentOwners.ForDeclWrapper(overloadDecl));
            if (EmissionSeam.TryDenyUpFront(overloadDecl, csWriter))
                continue;
            EmissionSeam.Guard(overloadDecl, RecoveryScope.LeafApi, overloadDecl.ParentDecl as TypeDecl, () =>
            {
                // Note: HasClosureCdeclWrapper is NOT set on cloned overload decls.
                // DefaultParam wrappers use @_silgen_name to intercept the original Swift symbol,
                // which forces the function type to match the original ABI. Closure params must
                // remain native Swift types in the wrapper. Only standalone closure wrappers
                // (emitted at MethodHandler level with their own unique symbol) can use Cdecl params.

                // Create environment with overload decl
                var overloadEnv = new MethodEnvironment(
                    overloadDecl,
                    env.TypeDatabase,
                    env.SiblingPropertyNames,
                    env.PInvokeHelperContext,
                    env.CompositionCollector);
                overloadEnv.DisambiguatedNameInput = env.DisambiguatedNameInput;
                // When the primary method adopted a disambiguated ancestor slot name (e.g. `override
                // ProcessSecond`), every generated trimmed/default-arg overload must emit under the SAME
                // adopted name. DisambiguatedNameInput alone does not carry it — CSharpMethodName recomputes
                // from the bare NameProvider name, which would yield `Process` and silently bind the trimmed
                // overload to the wrong base slot. Propagate the adopted name.
                overloadEnv.AdoptedOverrideCSharpName = env.AdoptedOverrideCSharpName;
                // FB-1b: a recovered colliding failable init emits under a label-disambiguated factory name
                // (e.g. TryCreateWithMessengerPageId); its default-arg trimmed overloads must share that name.
                overloadEnv.FailableFactoryName = env.FailableFactoryName;
                // Same for a recovered colliding NON-failable init: the primary emitted as a static
                // `CreateWith{Labels}` factory, so its trimmed overloads must be factories of that name too —
                // otherwise they would emit as constructors and re-create the collision the recovery resolved.
                overloadEnv.InitFactoryName = env.InitFactoryName;
                overloadEnv.EmissionContext = env.EmissionContext;

                // Set @_cdecl constructor wrapper flags BEFORE SignatureHandler construction.
                // Compute the @_cdecl symbol from the original MangledName (before EmitSwiftWrapper changes it).
                string? silgenSymbolForCdecl = null;
                string? cdeclSymbolForRestore = null;
                if (overloadDecl.IsConstructor && ConstructorWrapperEmitter.ShouldEmitWrapper(overloadEnv))
                {
                    var parentType_ = overloadDecl.ParentDecl as TypeDecl;
                    // Save the @_silgen_name symbol (current MangledName = DBW_...) — the @_cdecl wrapper calls it
                    silgenSymbolForCdecl = overloadDecl.MangledName;
                    var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
                        parentType_!.SwiftTypeName.Module,
                        parentType_.Name,
                        overloadDecl.MangledName);
                    cdeclSymbolForRestore = cdeclSymbol;
                    overloadDecl.UsesCdeclConstructorWrapper = true;
                    // UsesWrapperLibrary already set by BuildOverloadDecl
                    overloadEnv.PromoteSymbol(cdeclSymbol);

                    // Propagate HasClosureParams for @_cdecl constructor overloads with closures
                    if (overloadDecl.CSSignature.Skip(1).Any(overloadEnv.ClosureHandler.IsClosure))
                        overloadDecl.HasClosureParams = true;
                }

                // Set @_cdecl method wrapper flags BEFORE SignatureHandler construction.
                // Mirrors the constructor wrapper pattern above for non-constructor methods.
                // Check the ORIGINAL method's flag (not the overload's) because BuildOverloadDecl
                // unconditionally sets UsesWrapperLibrary=true, which would cause ShouldEmitWrapper
                // to return false on the overload.
                string? silgenSymbolForMethodCdecl = null;
                string? cdeclSymbolForMethodRestore = null;
                if (!overloadDecl.IsConstructor && methodDecl.UsesCdeclMethodWrapper)
                {
                    var parentType_ = overloadDecl.ParentDecl as TypeDecl;
                    var parentModule_ = overloadDecl.ParentDecl as ModuleDecl;
                    string moduleName_ = parentType_?.SwiftTypeName.Module ?? parentModule_?.Name ?? "";
                    string typeName_ = parentType_?.Name ?? "Free";
                    silgenSymbolForMethodCdecl = overloadDecl.MangledName;
                    var cdeclSymbol = MethodWrapperEmitter.GetMethodSymbolName(
                        moduleName_,
                        typeName_,
                        overloadDecl.Name,
                        overloadDecl.MangledName);
                    cdeclSymbolForMethodRestore = cdeclSymbol;
                    overloadDecl.UsesCdeclMethodWrapper = true;
                    // UsesWrapperLibrary already set by BuildOverloadDecl
                    overloadEnv.PromoteSymbol(cdeclSymbol);

                    // Propagate HasClosureParams for @_cdecl method overloads with closures
                    if (overloadDecl.CSSignature.Skip(1).Any(overloadEnv.ClosureHandler.IsClosure))
                        overloadDecl.HasClosureParams = true;
                }

                // Also check if the overload itself qualifies for @_cdecl independently.
                // The trimmed overload may have removed problematic params that blocked the base method.
                // Guard 13 (UsesWrapperLibrary) is true because BuildOverloadDecl sets it;
                // temporarily clear to run ShouldEmitWrapper.
                // All eligible methods get @_cdecl wrappers — CallConvSwift is eliminated.
                if (!overloadDecl.IsConstructor && silgenSymbolForMethodCdecl == null)
                {
                    overloadDecl.UsesWrapperLibrary = false;
                    bool wrapperRequired = WrapperValidation.DetermineMethodWrapperDecision(overloadEnv) == WrapperDecision.WrapperRequired;
                    overloadDecl.UsesWrapperLibrary = true;
                    if (wrapperRequired)
                    {
                        var parentType_ = overloadDecl.ParentDecl as TypeDecl;
                        var parentModule_ = overloadDecl.ParentDecl as ModuleDecl;
                        string moduleName_ = parentType_?.SwiftTypeName.Module ?? parentModule_?.Name ?? "";
                        string typeName_ = parentType_?.Name ?? "Free";
                        silgenSymbolForMethodCdecl = overloadDecl.MangledName;
                        var cdeclSymbol = MethodWrapperEmitter.GetMethodSymbolName(
                            moduleName_,
                            typeName_,
                            overloadDecl.Name,
                            overloadDecl.MangledName);
                        cdeclSymbolForMethodRestore = cdeclSymbol;
                        overloadDecl.UsesCdeclMethodWrapper = true;
                        overloadEnv.PromoteSymbol(cdeclSymbol);
                        if (overloadDecl.CSSignature.Skip(1).Any(overloadEnv.ClosureHandler.IsClosure))
                            overloadDecl.HasClosureParams = true;
                    }
                }

                // Check if the overload signature is fully marshallable
                var signatureHandler = new SignatureHandler(overloadEnv);
                if (signatureHandler.GetWrapperSignature().ContainsPlaceholder)
                {
                    logger.LogDebug("DefaultParameterOverload: skipping overload (trim {Trim}) for {Name} — signature contains placeholder", trim, methodDecl.Name);
                    return;
                }

                // A closure-typed inout param can't be expressed by this @_silgen_name shim:
                // EmitSwiftWrapper unconditionally forces @escaping onto every closure parameter
                // it forwards (the original method may require it), but `inout` and `@escaping`
                // are mutually exclusive on a Swift function parameter — swiftc rejects the
                // combination outright. Skip rather than emit an uncompilable wrapper.
                if (overloadDecl.CSSignature.Skip(1).Any(a => a.IsInOut && a.SwiftTypeSpec is ClosureTypeSpec))
                {
                    logger.LogDebug("DefaultParameterOverload: skipping overload (trim {Trim}) for {Name} — inout closure parameter cannot be expressed in the @_silgen_name shim", trim, methodDecl.Name);
                    return;
                }

                // The shim calls the original by name without the dropped arguments. Plan that call
                // so it still reaches this declaration rather than a same-named sibling, spelling out
                // a dropped default where that is what keeps it here.
                var pinPlan = SwiftOverloadCallPinning.Plan(methodDecl, droppedArgs.Contains, env.TypeDatabase);

                // C6/C7: the projected C# signature this candidate would claim. Computed here (not at the
                // reservation below) because the ambiguity filter needs the whole candidate set's
                // signatures before the first of them is written.
                //
                // The key also seeds the API-manifest entry after emission: a recovered overload whose
                // FULL signature was rejected has no primary manifest entry, so without this it lands in
                // the generated C# but is absent from api-surface.md / the api-manifest — the exact drift
                // the surface doc exists to prevent, one path deeper.
                {
                    // Rebuild the key under the primary's disambiguated base name, so a trimmed overload of
                    // `ConfigureZebra` reserves `ConfigureZebra(...)` rather than the bare `Configure(...)`.
                    var projectedKey = GetProjectedOverloadKey(overloadDecl, env.TypeDatabase, env.SiblingPropertyNames, env.DisambiguatedNameInput);
                    // When the primary method adopted a disambiguated ancestor slot
                    // name, this trimmed overload emits under that SAME adopted name (propagated to
                    // overloadEnv.AdoptedOverrideCSharpName at construction above), so CSharpMethodName reads
                    // `ProcessSecond` not the recomputed bare `Process`. GetProjectedOverloadKey rebuilds the key
                    // from the local NameProvider name, so substitute the adopted name into the key's name
                    // component — otherwise the reserved key (`Process()`) diverges from the emitted name
                    // (`ProcessSecond()`): a sibling naturally projecting to `ProcessSecond()` would not collide
                    // (→ duplicate CS0111) and a real `Process()` sibling would be wrongly blocked. Adoption is
                    // mutually exclusive with a non-null DisambiguatedNameInput (a self-disambiguating override
                    // resolves adoption to null), so the two never compound.
                    if (overloadEnv.AdoptedOverrideCSharpName != null)
                    {
                        int keyParen = projectedKey.IndexOf('(');
                        if (keyParen > 0)
                            projectedKey = overloadEnv.AdoptedOverrideCSharpName + projectedKey.Substring(keyParen);
                    }
                    // FB-1b: a recovered colliding failable init's default-arg trimmed overloads emit under the
                    // label-disambiguated factory name (overloadEnv.FailableFactoryName, propagated above), NOT the
                    // ctor name GetProjectedOverloadKey rebuilds. Re-key into the SAME "failable-factory:" namespace
                    // the main dedup loop reserved the full factory under (prefix + factory name + input-type list),
                    // so a trimmed factory overload dedups only against other factory overloads of the same
                    // name+arity. Without this the trimmed overload dedups in the ctor namespace and a trimmed
                    // input list matching an UNRELATED init's ctor(...) key would silently drop a valid overload —
                    // the very silent-drop FB-1b exists to prevent, one path deeper. The factory name already
                    // carries the disambiguation, so the numeric collision suffix applied above is irrelevant here
                    // (only the params substring, which the suffix leaves untouched, is used); adoption and a
                    // failable factory are mutually exclusive, so this never double-applies.
                    if (overloadEnv.FailableFactoryName != null)
                    {
                        int factoryParen = projectedKey.IndexOf('(');
                        if (factoryParen > 0)
                            projectedKey = "failable-factory:" + overloadEnv.FailableFactoryName + projectedKey.Substring(factoryParen);
                    }
                    // A recovered colliding NON-failable init's trimmed overloads likewise emit under the
                    // factory name rather than the ctor name the key builder rebuilds. This lane stays in the
                    // ORDINARY key namespace (unlike the failable one): its factory carries no trailing `out`,
                    // so `CreateWithHost(string)` really can duplicate a natural method of that name and inputs,
                    // and hiding it in a private namespace would fail open on exactly that CS0111.
                    if (overloadEnv.InitFactoryName != null)
                    {
                        int initFactoryParen = projectedKey.IndexOf('(');
                        if (initFactoryParen > 0)
                            projectedKey = overloadEnv.InitFactoryName + projectedKey.Substring(initFactoryParen);
                    }

                    if (pinPlan.IsRefused)
                    {
                        logger.LogInformation(
                            "Skipping a default-parameter overload of '{Name}': without the omitted arguments the " +
                            "Swift call resolves to the same-named '{Sibling}', and no omitted default can be " +
                            "spelled out to keep it on this declaration: {Key}",
                            methodDecl.Name, pinPlan.WinningSibling, projectedKey);
                        emissionContext?.TryRecordSuppressedAmbiguousOverload(
                            projectedKey, "Swift " + pinPlan.WinningSibling, candidateWasFuller: false);
                        return;
                    }

                    int requiredCount = OverloadAmbiguityGuard.RequiredCountFor(overloadDecl, env.TypeDatabase, projectedKey);

                    // A same-named declaration of the type that a consumer could not tell this overload
                    // from — the same C# signature (CS0111), or a call both bind equally well (CS0121).
                    if (TryFindSignatureCollision(methodDecl, overloadDecl, projectedKey, requiredCount, env, out var collidingKey))
                    {
                        logger.LogDebug(
                            "DefaultParameterOverload: declining overload (trim {Trim}) for {Name} — a consumer could not tell it from the sibling {Other}: {Key}",
                            trim, methodDecl.Name, collidingKey, projectedKey);
                        emissionContext?.TryRecordSuppressedAmbiguousOverload(
                            projectedKey, collidingKey!, candidateWasFuller: false);
                        return;
                    }

                    prepared.Add(new PreparedOverload
                    {
                        Decl = overloadDecl,
                        Env = overloadEnv,
                        SignatureHandler = signatureHandler,
                        SymbolTrim = trim,
                        SilgenSymbolForCdecl = silgenSymbolForCdecl,
                        CdeclSymbolForRestore = cdeclSymbolForRestore,
                        SilgenSymbolForMethodCdecl = silgenSymbolForMethodCdecl,
                        CdeclSymbolForMethodRestore = cdeclSymbolForMethodRestore,
                        ProjectedKey = projectedKey,
                        RequiredCount = requiredCount,
                        DeclaredCount = overloadDecl.CSSignature.Count - 1,
                        DroppedArgs = droppedArgs,
                        PinPlan = pinPlan,
                    });
                }
            });
        }

        // PHASE 2 — internal set validity. Two candidates from THIS producer can each be individually
        // valid and still be mutually ambiguous at a consumer's call site (CS0121). Walk the set
        // fullest-first and drop a candidate that ties with a survivor: the fuller signature is the
        // strictly more capable one, so it is the one kept. This ordering is only enforceable because
        // the whole candidate set exists before any of it is written.
        var suppressedByPeer = new bool[prepared.Count];
        var fullestFirst = Enumerable.Range(0, prepared.Count)
            .OrderByDescending(i => prepared[i].DeclaredCount)
            .ThenBy(i => i)
            .ToList();
        for (int oi = 1; oi < fullestFirst.Count; oi++)
        {
            int i = fullestFirst[oi];
            var candidateShape = OverloadAmbiguityGuard.ParseKey(prepared[i].ProjectedKey, prepared[i].RequiredCount);
            for (int oj = 0; oj < oi; oj++)
            {
                int j = fullestFirst[oj];
                if (suppressedByPeer[j])
                    continue;
                var survivorShape = OverloadAmbiguityGuard.ParseKey(prepared[j].ProjectedKey, prepared[j].RequiredCount);
                if (!OverloadAmbiguityGuard.AreAmbiguous(candidateShape, survivorShape))
                    continue;
                suppressedByPeer[i] = true;
                logger.LogDebug(
                    "DefaultParameterOverload: declining overload (trim {Trim}) for {Name} — a call site would bind it and the fuller sibling {Other} equally well: {Key}",
                    prepared[i].SymbolTrim, methodDecl.Name, prepared[j].ProjectedKey, prepared[i].ProjectedKey);
                emissionContext?.TryRecordSuppressedAmbiguousOverload(
                    prepared[i].ProjectedKey, prepared[j].ProjectedKey, candidateWasFuller: false);
                break;
            }
        }

        // PHASE 3 — write the survivors in candidate order (most-trimmed first, the order the
        // generated source has always used).
        for (int ci = 0; ci < prepared.Count; ci++)
        {
            if (suppressedByPeer[ci])
                continue;

            var candidate = prepared[ci];
            var overloadDecl = candidate.Decl;
            var overloadEnv = candidate.Env;
            var signatureHandler = candidate.SignatureHandler;
            var trim = candidate.SymbolTrim;
            var silgenSymbolForCdecl = candidate.SilgenSymbolForCdecl;
            var cdeclSymbolForRestore = candidate.CdeclSymbolForRestore;
            var silgenSymbolForMethodCdecl = candidate.SilgenSymbolForMethodCdecl;
            var cdeclSymbolForMethodRestore = candidate.CdeclSymbolForMethodRestore;

            using var candidateCsScope = csWriter.BeginFragment(FragmentOwners.ForDecl(overloadDecl));
            using var candidateSwiftScope = swiftWriter.BeginFragment(FragmentOwners.ForDeclWrapper(overloadDecl));
            EmissionSeam.Guard(overloadDecl, RecoveryScope.LeafApi, overloadDecl.ParentDecl as TypeDecl, () =>
            {
                string? recordedProjectedKey = null;
                if (env.EmittedProjectedSignatures != null)
                {
                    var projectedKey = candidate.ProjectedKey;

                    // Cross-producer set validity. Emission is immediate-write, so an already-reserved
                    // signature can never be retracted — the synthesized candidate is the one declined
                    // even when it is the fuller of the two (the inversion is reported).
                    var conflicting = OverloadAmbiguityGuard.FindAmbiguousReservation(
                        env.EmittedProjectedSignatures, env.ReservedOverloadShapes, projectedKey, candidate.RequiredCount);
                    if (conflicting != null)
                    {
                        // Both sides are measured on the PROJECTED key, not on the Swift parameter list:
                        // an async member's emitted signature carries a trailing CancellationToken that
                        // the decl's CSSignature does not, so comparing a raw declared count against a
                        // projected arity would report an async candidate a parameter shorter than it is
                        // and mis-rank the inversion note.
                        bool candidateWasFuller =
                            OverloadAmbiguityGuard.ParseKey(projectedKey, 0).ParameterTypes.Count >
                            OverloadAmbiguityGuard.ParseKey(conflicting, 0).ParameterTypes.Count;
                        logger.LogDebug(
                            "DefaultParameterOverload: declining overload (trim {Trim}) for {Name} — a call site would bind it and the already-emitted {Other} equally well: {Key}",
                            trim, methodDecl.Name, conflicting, projectedKey);
                        emissionContext?.TryRecordSuppressedAmbiguousOverload(projectedKey, conflicting, candidateWasFuller);
                        return;
                    }

                    if (!env.EmittedProjectedSignatures.Add(projectedKey))
                    {
                        logger.LogDebug("DefaultParameterOverload: skipping overload (trim {Trim}) for {Name} — projected signature collides: {Key}", trim, methodDecl.Name, projectedKey);
                        return;
                    }
                    OverloadAmbiguityGuard.RecordReservation(env.ReservedOverloadShapes, projectedKey, candidate.RequiredCount);
                    recordedProjectedKey = projectedKey;
                }

                // EmitSwiftWrapper reads overloadDecl.MangledName (immutable) as the @_silgen_name target.
                // If using cdecl, promote the env's emission symbol to the silgen symbol so P/Invoke
                // routing resolves the original before the cdecl re-promotion below.
                if (cdeclSymbolForRestore != null)
                    overloadEnv.PromoteSymbol(silgenSymbolForCdecl!);
                if (cdeclSymbolForMethodRestore != null)
                    overloadEnv.PromoteSymbol(silgenSymbolForMethodCdecl!);

                // Emit Swift @_silgen_name wrapper
                EmitSwiftWrapper(swiftWriter, methodDecl, overloadDecl, env, trim, candidate.DroppedArgs, candidate.PinPlan);

                // Promote the env's emission symbol to the cdecl symbol — the value P/Invoke routing reads.
                // (overloadDecl.MangledName is immutable; EmitSwiftWrapper read it directly for the @_silgen_name.)
                if (cdeclSymbolForRestore != null)
                    overloadEnv.PromoteSymbol(cdeclSymbolForRestore);
                if (cdeclSymbolForMethodRestore != null)
                    overloadEnv.PromoteSymbol(cdeclSymbolForMethodRestore);

                // Emit @_cdecl constructor wrapper that calls the @_silgen_name function
                if (silgenSymbolForCdecl != null && overloadDecl.UsesCdeclConstructorWrapper)
                {
                    // Use the canonical trim count from the loop variable to ensure the
                    // silgen function name matches what EmitSwiftWrapper emitted.
                    var silgenFuncName = GetSilgenFuncName(GetOverloadSymbolBase(env), methodDecl, trim);
                    ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(
                        swiftWriter, overloadEnv, emissionContext, silgenTarget: silgenFuncName);
                }

                // Emit @_cdecl method wrapper that calls the @_silgen_name function.
                // Skip for async methods — @_cdecl wrappers are synchronous and cannot call
                // async _dbw_ extension methods (would be missing 'await').
                if (silgenSymbolForMethodCdecl != null && overloadDecl.UsesCdeclMethodWrapper
                    && !overloadDecl.IsAsync)
                {
                    // Use the canonical trim count from the loop variable to ensure the
                    // silgen function name matches what EmitSwiftWrapper emitted.
                    var silgenFuncName = GetSilgenFuncName(GetOverloadSymbolBase(env), methodDecl, trim);
                    bool silgenUsesResultBuf = env.BoundGenericsHandler.IsLargeOptionalReturn(overloadDecl);
                    MethodWrapperEmitter.EmitSwiftMethodWrapper(
                        swiftWriter, overloadEnv, emissionContext, silgenTarget: silgenFuncName,
                        silgenHasResultBuffer: silgenUsesResultBuf);
                }

                // Delegate C# emission to normal pipeline.
                // Position-aware degradation (mirrors MethodHandler) via the single source of truth
                // (MethodEnvironment.ReturnProjectsToExistentialUnion): a return the wrapper actually projects to
                // union is excluded from BOTH the single [UnsupportedSwiftType] anchor scan AND the SWIFTBIND023
                // degradation record, while an ineligible position still degrades + warns here. The SAME predicate
                // drives the signature builder, the return-body wrapper, and the [return: OriginalSwiftType]
                // suppression, so a default-parameter overload can't keep a stale degradation marker.
                var overloadSignatureArgs = overloadDecl.CSSignature;
                bool overloadReturnProjectsToUnion = overloadEnv.ReturnProjectsToExistentialUnion;

                var overloadDegradedSpecs = (overloadReturnProjectsToUnion
                    ? overloadSignatureArgs.Skip(1)
                    : overloadSignatureArgs.AsEnumerable())
                    .Select(a => a.SwiftTypeSpec)
                    .ToList();

                TypeDatabaseExtensions.AnyTypeFallbackInfo? fallbackInfo = null;
                foreach (var spec in overloadDegradedSpecs)
                {
                    if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(env.TypeDatabase, env.ClosureHandler, spec, out var foundFallbackInfo))
                    {
                        fallbackInfo = foundFallbackInfo;
                        break;
                    }
                }

                // The single flag above names only the first degraded position, but SWIFTBIND023 promises
                // one loud warning per DISTINCT degraded existential. Record every degraded position so an
                // existential that only appears as a 2nd+ position is not silently degraded to object; dedup
                // makes the overlap with the flag above harmless. A union-projected return is excluded above.
                UnsupportedSwiftTypeSupport.RecordExistentialDegradations(
                    emissionContext, env.TypeDatabase, env.ClosureHandler,
                    overloadDegradedSpecs);

                var wrapperEmitter = new WrapperEmitter(overloadEnv, signatureHandler, fallbackInfo, emissionContext);
                if (overloadDecl.IsConstructor && !overloadDecl.IsFailable && !overloadDecl.IsAsync)
                {
                    // A trimmed overload of a recovered colliding init must stay a factory of the same name —
                    // emitting it as a constructor would re-create the very collision the recovery resolved.
                    // The parent shape and result convention are the primary's, which already cleared
                    // CanEmitInitFactory, so there is no second gate to apply here.
                    if (overloadEnv.InitFactoryName != null)
                        wrapperEmitter.EmitInitFactory(csWriter);
                    else
                        wrapperEmitter.EmitConstructor(csWriter);
                }
                else if (overloadDecl.IsConstructor && overloadDecl.IsFailable)
                {
                    wrapperEmitter.EmitFailableFactory(csWriter);
                }
                else
                {
                    wrapperEmitter.EmitMethod(csWriter, swiftWriter);
                }
                PInvokeEmitter.EmitPInvoke(csWriter, overloadEnv, signatureHandler);
                emittedCount++;

                // Record this recovered overload in the API manifest so it surfaces in api-surface.md.
                // The member's FULL signature was rejected (a trailing defaulted param resolved to an
                // AnyType placeholder), so it has no primary manifest entry from the main dedup loop and
                // WasEmitted stays false — recording the callable TRUNCATED form here (not the full
                // unbindable signature) keeps the emitted C# and the documented surface in agreement.
                // Guarded on recordedProjectedKey so a synthetic env with no dedup set (unit harness that
                // never assigns EmittedProjectedSignatures) records nothing. The name and parameter list
                // come from whichever writer above emitted the declaration — a constructor writes under
                // the type's own name, a failable init under its (possibly label-disambiguated) factory
                // name plus a trailing `out` result — so the entry names the member as written.
                if (emissionContext != null && recordedProjectedKey != null)
                {
                    emissionContext.RecordApiManifestEntry(
                        ModuleEmissionContext.BuildApiManifestKey(
                            overloadDecl.ParentDecl, overloadEnv.CSharpMethodName, recordedProjectedKey, env.TypeDatabase,
                            emissionContext.GetEmittedApiShape(overloadDecl)),
                        overloadEnv.EmissionSymbol);
                }
            });
        }
        return emittedCount != 0;
    }

    /// <summary>
    /// One fully-built, fully-validated overload candidate, held between this producer's build phase
    /// and its write phase. Buffering the set is what makes "keep the fuller of an ambiguous pair"
    /// enforceable: the fuller candidate is built after the shorter one, so a write-as-you-go loop
    /// has already committed the shorter one by the time it can see the tie.
    /// </summary>
    private sealed class PreparedOverload
    {
        public required MethodDecl Decl { get; init; }
        public required MethodEnvironment Env { get; init; }
        public required SignatureHandler SignatureHandler { get; init; }

        /// <summary>Trim count that names this candidate's Swift shim (DBW_ symbol / _dbw_ function).</summary>
        public required int SymbolTrim { get; init; }

        public string? SilgenSymbolForCdecl { get; init; }
        public string? CdeclSymbolForRestore { get; init; }
        public string? SilgenSymbolForMethodCdecl { get; init; }
        public string? CdeclSymbolForMethodRestore { get; init; }

        /// <summary>Projected C# signature this candidate would claim, post adopted-name / factory re-keying.</summary>
        public required string ProjectedKey { get; init; }

        /// <summary>How many parameters a caller must supply (arity minus the C# optional tail).</summary>
        public required int RequiredCount { get; init; }

        /// <summary>Declared C# parameter count — the "fullness" the ambiguity tie-break ranks on.</summary>
        public required int DeclaredCount { get; init; }

        /// <summary>The original's parameters this candidate's Swift call leaves out.</summary>
        public required IReadOnlySet<ArgumentDecl> DroppedArgs { get; init; }

        /// <summary>Which of <see cref="DroppedArgs"/> the call spells out to stay on the original.</summary>
        public required SwiftCallPinPlan PinPlan { get; init; }
    }

    /// <summary>
    /// Whether every parameter in <paramref name="dropIndices"/> can be omitted from the Swift call
    /// the wrapper makes, so Swift itself supplies the real default value.
    ///
    /// A LABELED defaulted parameter is always omissible: Swift matches the remaining arguments by
    /// label, so removing one from the call site cannot shift what the others bind to. An UNLABELED
    /// one is omissible only when every KEPT parameter after it is labeled — a label pins its own
    /// argument no matter what was dropped ahead of it, but a kept UNLABELED follower is matched
    /// positionally, and Swift then has to decide whether it fills the omitted slot or the one after
    /// it, which is exactly the mis-binding (or outright "missing argument" rejection) that would
    /// leave an uncompilable wrapper behind. That last class is the fail-closed one: the candidate is
    /// not built at all, and the member keeps today's behavior.
    ///
    /// Omission is used rather than textually substituting the parameter's recorded Swift default
    /// expression: an expression from the interface can name symbols that are not visible from the
    /// wrapper's module (internal statics, private helpers), which compiles the wrapper out of the
    /// dylib and leaves the C# P/Invoke bound to a symbol that no longer exists. Letting Swift
    /// evaluate its own default cannot get the value wrong, and needs no expression at all. The one
    /// exception is a default that must be spelled out to keep the shortened call on this
    /// declaration, and <see cref="SwiftOverloadCallPinning"/> only ever spells out a literal.
    /// </summary>
    private static bool CanOmitEveryDefault(IReadOnlyList<ArgumentDecl> args, IReadOnlyList<int> dropIndices)
    {
        var dropped = new HashSet<int>(dropIndices);
        foreach (var index in dropIndices)
        {
            if (CdeclParamMapper.BuildSwiftCallArgLabel(args[index]).Length > 0)
                continue; // labeled — omission is unambiguous at the Swift call site

            for (int j = index + 1; j < args.Count; j++)
            {
                if (dropped.Contains(j) || IsDebugParameter(args[j]))
                    continue;
                if (CdeclParamMapper.BuildSwiftCallArgLabel(args[j]).Length == 0)
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Counts the number of consecutive trailing parameters with HasDefaultArg.
    /// </summary>
    internal static int CountTrailingDefaults(MethodDecl methodDecl)
    {
        var args = methodDecl.CSSignature.Skip(1).ToList(); // skip return type
        if (args.Count == 0) return 0;

        int count = 0;
        for (int i = args.Count - 1; i >= 0; i--)
        {
            if (IsDebugParameter(args[i]))
                continue; // debug params are always omitted; don't count them
            if (args[i].HasDefaultArg)
                count++;
            else
                break;
        }
        return count;
    }

    /// <summary>
    /// Creates a new MethodDecl with the last 'trimCount' parameters removed.
    /// The MangledName points to the Swift wrapper symbol.
    /// </summary>
    internal static MethodDecl BuildOverloadDecl(MethodDecl original, int trimCount)
        => BuildOverloadDecl(original.MangledName, original, trimCount);

    /// <summary>
    /// AF13: emission-scoped overload — uses <paramref name="baseSymbol"/> (the primary method's
    /// <c>env.EmissionSymbol</c>) as the hash base for the cloned overload's DBW_ wrapper symbol, so the
    /// generated symbol tracks the promoted symbol rather than the decl's (now-immutable) MangledName.
    /// </summary>
    internal static MethodDecl BuildOverloadDecl(string baseSymbol, MethodDecl original, int trimCount)
    {
        var argCount = original.CSSignature.Count - 1;
        var dropped = new HashSet<int>(Enumerable.Range(argCount - trimCount, trimCount));
        return BuildOverloadDeclCore(baseSymbol, original, dropped, trimCount);
    }

    /// <summary>
    /// Same clone, but the omitted parameters are named by INDEX rather than by a trailing count —
    /// so an interior defaulted parameter can be dropped while later required ones are kept. The
    /// Swift shim still receives only the kept arguments and calls the real declaration omitting the
    /// rest, which is what makes Swift evaluate its own defaults for them.
    ///
    /// <paramref name="dropIndices"/> are indices into the parameter list (return type excluded).
    /// The shim symbol's trim component is the drop COUNT, which keeps it distinct from every
    /// trailing-trim sibling: a trailing trim of the same count would have to drop a different set,
    /// and the two are never both produced for one method.
    /// </summary>
    internal static MethodDecl BuildOverloadDeclDropping(string baseSymbol, MethodDecl original, IReadOnlyList<int> dropIndices)
        => BuildOverloadDeclCore(baseSymbol, original, new HashSet<int>(dropIndices), dropIndices.Count);

    private static MethodDecl BuildOverloadDeclCore(string baseSymbol, MethodDecl original, HashSet<int> dropIndices, int symbolTrimCount)
    {
        var wrapperSymbol = BuildWrapperSymbol(baseSymbol, original, symbolTrimCount);

        var overload = new MethodDecl
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
            CallsDefaultArgumentShim = true,
            AvailabilityAnnotations = original.AvailabilityAnnotations,
        };

        // Copy return type
        var returnArg = original.CSSignature[0];
        overload.CSSignature.Add(new ArgumentDecl
        {
            SwiftTypeSpec = returnArg.SwiftTypeSpec,
            Name = returnArg.Name,
            PrivateName = returnArg.PrivateName,
            IsInOut = returnArg.IsInOut,
            Ownership = returnArg.Ownership,
            IsGeneric = returnArg.IsGeneric,
            HasDefaultArg = returnArg.HasDefaultArg,
            ParentDecl = overload,
            ModuleDecl = returnArg.ModuleDecl
        });

        // Copy non-trimmed parameters. Ownership is an intrinsic, position-independent
        // property of the parameter (like IsInOut/IsGeneric) and MUST be carried over: a
        // `consuming` (Owned) parameter that survives into a trimmed default-overload would
        // otherwise revert to ParameterOwnership.Default and route off the .move()/MarkConsumed
        // path → double-free (consuming-ownership SIGABRT). CSharpName is deliberately NOT copied — it is re-deduped
        // per overload against the overload's own (shorter) signature.
        var args = original.CSSignature.Skip(1).ToList();
        for (int i = 0; i < args.Count; i++)
        {
            if (dropIndices.Contains(i))
                continue;
            var arg = args[i];
            overload.CSSignature.Add(new ArgumentDecl
            {
                SwiftTypeSpec = arg.SwiftTypeSpec,
                Name = arg.Name,
                PrivateName = arg.PrivateName,
                IsInOut = arg.IsInOut,
                Ownership = arg.Ownership,
                IsGeneric = arg.IsGeneric,
                HasDefaultArg = arg.HasDefaultArg,
                ParentDecl = overload,
                ModuleDecl = arg.ModuleDecl
            });
        }

        return overload;
    }

    /// <summary>
    /// Builds a reduced clone of <paramref name="original"/> with the last
    /// <paramref name="dropCount"/> trailing parameters removed, preserving every other parser
    /// ABI fact (silgen MangledName, all flags, attributes) unchanged.
    ///
    /// Unlike <see cref="BuildOverloadDecl(MethodDecl, int)"/> (which forces a DBW_ silgen-shim
    /// symbol and UsesWrapperLibrary=true for the post-processor's @_silgen_name path), this
    /// clone is meant to be routed back through the NORMAL constructor/method handler as a
    /// fresh primary: the handler then makes its own @_cdecl wrapper decision and emits a real
    /// C# constructor/method calling the Swift declaration with the kept arguments (Swift
    /// supplies the dropped trailing defaults). Used by the pre-gate trailing-default rescue,
    /// which emits a reduced overload when the full member is dropped solely because a trailing
    /// default-valued parameter has an unbindable type.
    /// </summary>
    internal static MethodDecl BuildGateReducedDecl(MethodDecl original, int dropCount)
    {
        // Clone via `with` so EVERY parser ABI fact carries over unchanged — override / final /
        // actor-isolation / SPI / extension-method / consuming-or-borrowing-self / typed-throws /
        // variadic / … — and only the parameter list shrinks. Hand-copying a hand-picked subset
        // silently dropped semantically load-bearing flags: a lost @MainActor flag makes the
        // reduced @_cdecl wrapper miss its actor annotation (Swift isolation error), a lost
        // override flag emits a name-hiding method instead of an override, and a lost @_spi flag
        // lets a non-externally-callable member slip past the rescue's re-validation. Emission-
        // mutable state (WasEmitted / EmittedCSharpName / WrapperStrategy) is reset because the
        // reduced decl re-enters dedup + emission as a fresh primary; it is already default here
        // (the original was dropped at validation, before any emission ran), but the explicit
        // reset keeps that invariant from silently breaking if validation later sets one.
        var reduced = original with
        {
            GenericParameters = new List<GenericArgumentDecl>(original.GenericParameters),
            CSSignature = new List<ArgumentDecl>(),
            WasEmitted = false,
            EmittedCSharpName = null,
            WrapperStrategy = WrapperStrategy.None,
            // Force the @_cdecl Swift-source wrapper (which fills the dropped trailing defaults)
            // and suppress the native thunk (which would `bl` the full-ABI symbol with the dropped
            // parameter's register uninitialized → runtime fault).
            IsGateReducedOverload = true,
        };

        // Clone each kept ArgumentDecl with `with` so EVERY per-parameter parser fact carries over
        // — IsConstLiteral, SwiftDefaultExpression, IsUnlabeledSubscriptIndex, Ownership, IsInOut,
        // IsGeneric, HasDefaultArg — and only ParentDecl is re-pointed at the reduced clone.
        // Hand-copying a hand-picked subset silently dropped load-bearing flags: a lost
        // IsConstLiteral lets a `_const` param look non-const, so the wrap-required gate accepts a
        // candidate the @_cdecl emitter must reject (it passes a runtime value to a compile-time
        // literal parameter → Swift wrapper compile error). CSharpName is the one emission-mutable
        // field reset to null: the reduced decl re-enters dedup as a fresh primary and re-dedupes
        // its parameter names against its own (shorter) signature.
        var returnArg = original.CSSignature[0];
        reduced.CSSignature.Add(returnArg with { ParentDecl = reduced, CSharpName = null });

        var args = original.CSSignature.Skip(1).ToList();
        var keepCount = args.Count - dropCount;
        for (int i = 0; i < keepCount; i++)
        {
            reduced.CSSignature.Add(args[i] with { ParentDecl = reduced, CSharpName = null });
        }

        return reduced;
    }

    /// <summary>
    /// Emits a Swift wrapper function that calls the original method
    /// with fewer arguments, letting Swift fill in the defaults.
    /// </summary>
    /// <param name="trim">The canonical trim count (loop variable) — number of trailing default params removed.</param>
    /// <param name="droppedArgs">The original's parameters the call leaves out.</param>
    /// <param name="pinPlan">Which dropped parameters the call spells out to stay on the original.</param>
    private static void EmitSwiftWrapper(
        SwiftWriter swiftWriter,
        MethodDecl originalMethodDecl,
        MethodDecl overloadDecl,
        MethodEnvironment env,
        int trim,
        IReadOnlySet<ArgumentDecl> droppedArgs,
        SwiftCallPinPlan pinPlan)
    {
        var wrapperSymbol = overloadDecl.MangledName;
        var parentTypeDecl = originalMethodDecl.ParentDecl as TypeDecl;
        bool isFreeFunction = parentTypeDecl == null;

        // Build parameter list for the wrapper function (only kept params)
        var swiftParams = new List<string>();
        var derefLines = new List<string>();
        var keptArgs = overloadDecl.CSSignature.Skip(1).ToList();
        // This shim's signature is written into the wrapper, where a bare name resolves against
        // every import — so a bound-module type sharing a name with an imported module's type is
        // ambiguous to swiftc. Qualify the bound module's own types; everything else stays bare.
        // QualificationPolicy compares against NamedTypeSpec.Module, the raw ABI spelling —
        // never backtick-escaped (see the sibling derivation in ExistentialBypassEmitter for
        // why UnescapeModuleName is the wrong transform for this comparison key).
        var boundModuleName = originalMethodDecl.ModuleDecl?.Name ?? "";
        // Sibling bindings so a reserved-name escape also dodges a sibling user param.
        // The call-value loop below recomputes the identical set, keeping decls and values in sync.
        var siblings = CdeclParamMapper.CollectSiblingBindingNames(keptArgs);
        for (int i = 0; i < keptArgs.Count; i++)
        {
            var arg = keptArgs[i];
            // Keyword rename + sanitize + reserved/sibling escape (canonical helper). The
            // call-value loop below derives the matching arg identically.
            var rawLabel = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            var label = CdeclParamMapper.BuildSwiftBindingName(rawLabel, siblings);

            // Large Optional params: accept UnsafeRawPointer, dereference in body
            if (OptionalPointerWrapperEmitter.ShouldWidenParam(arg, env.BoundGenericsHandler))
            {
                swiftParams.Add($"_ {label}: Swift.UnsafeRawPointer");
                derefLines.Add(OptionalPointerWrapperEmitter.GetDerefCode(arg, label, label, env.TypeDatabase));
            }
            else
            {
                // Render param as native Swift type — @_silgen_name forces original function type
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpecForWrapperSignature(
                    arg.SwiftTypeSpec, boundModuleName);
                // Wrapper functions always need @escaping on closure parameters because
                // the closure is passed to the original method which may require it.
                // Also add @Sendable for async closures (required in Swift 5.5+ concurrency).
                if (arg.SwiftTypeSpec is ClosureTypeSpec closureSpec)
                {
                    if (!swiftType.StartsWith("@escaping"))
                    {
                        if (closureSpec.IsAsync && !swiftType.Contains("@Sendable"))
                            swiftType = $"@escaping @Sendable {swiftType}";
                        else
                            swiftType = $"@escaping {swiftType}";
                    }
                    else if (closureSpec.IsAsync && !swiftType.Contains("@Sendable"))
                    {
                        swiftType = swiftType.Replace("@escaping ", "@escaping @Sendable ");
                    }
                }
                // RenderSwiftTypeSpecForWrapperSignature never emits a top-level `inout ` —
                // this @_silgen_name shim intercepts the ORIGINAL Swift symbol, so its
                // signature must match the original function type exactly, `inout` included,
                // or the ABI the caller (the shim itself, called by the promoted @_cdecl/P-Invoke
                // path) and the callee (the real method, called below) disagree on whether this
                // slot is a pass-by-reference parameter. ArgumentDecl.IsInOut (not TypeSpec.IsInOut,
                // which this renderer does not consult for a top-level param) is the parser-sourced
                // fact for it. A closure-typed inout can't reach here — @escaping and inout are
                // mutually exclusive in Swift, so TryEmitOverloads/EmitDebugParamWrapper skip that
                // shape entirely rather than emit it.
                if (arg.IsInOut && !swiftType.StartsWith("inout "))
                {
                    swiftType = $"inout {swiftType}";
                }
                swiftParams.Add($"_ {label}: {swiftType}");
            }
        }

        // Check if the return type is a large Optional that needs an out-buffer
        bool hasLargeOptionalReturn = env.BoundGenericsHandler.IsLargeOptionalReturn(overloadDecl);
        if (hasLargeOptionalReturn)
        {
            swiftParams.Add("_ _resultBuf: Swift.UnsafeMutableRawPointer");
        }

        var swiftParamString = string.Join(", ", swiftParams);

        // Method-own generics (e.g., StoreKit2 `purchase<S: UIScene>(... options = [])`):
        // emit the trim shim with the same generic header and where-clause as the original.
        // Parent-type generics are filtered out by BuildMethodOwnGenericParams.
        var methodOwnGenericParams = AsyncHarnessEmitter.BuildMethodOwnGenericParams(originalMethodDecl);
        var methodOwnWhereClause = "";
        if (!string.IsNullOrEmpty(methodOwnGenericParams))
        {
            var parentTypeParamNames = parentTypeDecl != null && parentTypeDecl.IsGeneric
                ? new HashSet<string>(parentTypeDecl.GenericParameters.Select(p => p.TypeName))
                : new HashSet<string>();
            var methodOwnGenericDecls = originalMethodDecl.GenericParameters
                .Where(p => !parentTypeParamNames.Contains(p.TypeName))
                .ToList();
            // Free functions live at module scope and need module-qualified protocol names;
            // type-method extensions live inside the module so the bare name is unambiguous.
            methodOwnWhereClause = WrapperEmitterHelpers.BuildSwiftWhereClause(
                methodOwnGenericDecls, moduleQualify: isFreeFunction);
        }

        // Build call arguments — kept params with their original labels, in the original's parameter
        // order, plus any dropped default the plan spells out to keep the call on the original.
        var callArgs = new List<string>();
        int keptIndex = 0;
        foreach (var originalArg in originalMethodDecl.CSSignature.Skip(1))
        {
            if (droppedArgs.Contains(originalArg))
            {
                if (pinPlan.PinFor(originalArg) is { } pin)
                    callArgs.Add(pin);
                continue;
            }
            var arg = keptArgs[keptIndex++];
            // Identical to the param-decl loop (same sibling set + BuildSwiftBindingName) so the
            // value references the same binding.
            var rawLabel = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            var privateName = CdeclParamMapper.BuildSwiftBindingName(rawLabel, siblings);

            // Use dereferenced value for large Optional params
            bool isWidened = OptionalPointerWrapperEmitter.ShouldWidenParam(arg, env.BoundGenericsHandler);
            var valueRef = isWidened ? $"{privateName}Val" : privateName;

            // Provenance-aware call label (canonical builder) — preserves labels that genuinely
            // begin with '_' (e.g. _self) and backtick-escapes keywords.
            var argStr = CdeclParamMapper.BuildSwiftCallArgLabel(arg);

            // Forward the wrapper's own `inout` parameter to the original method with `&` —
            // required by Swift at every call site passing an lvalue to an inout parameter
            // (matches the `inout` prefix added to the declaration above). The widened-Optional
            // deref path is excluded: GetDerefCode always declares its local with `let`, so
            // `&{name}Val` would not compile — that combination isn't reachable from a real
            // trailing-default inout param today, and isn't fixed here (out of scope).
            var refPrefix = (arg.IsInOut && !isWidened) ? "&" : "";

            // Call args use native param names — @_silgen_name preserves original ABI
            // The shim receives an autoclosure parameter as a plain closure; SwiftClosureArgument
            // forwards its value for Swift to wrap back into the autoclosure.
            callArgs.Add(argStr + SwiftClosureArgument.Render(refPrefix + valueRef, arg.SwiftTypeSpec));
        }
        var callArgString = string.Join(", ", callArgs);

        // Render return type from original method
        var returnTypeSpec = originalMethodDecl.CSSignature.First().SwiftTypeSpec;
        var returnType = ExistentialBypassEmitter.RenderSwiftTypeSpecForWrapperReturnType(
            returnTypeSpec, boundModuleName);
        bool isVoid = returnTypeSpec is TupleTypeSpec tupleTypeSpec && tupleTypeSpec.IsEmptyTuple;
        bool throws = originalMethodDecl.Throws;

        var originalMethodName = NameProvider.ParserNameToSwift(originalMethodDecl);
        // For the wrapper function name, use the raw (unescaped) Swift name.
        // `_dbw_init_HASH_N` is a valid identifier — backtick escaping from
        // ParserNameToSwift would produce `_dbw_`init`_HASH_N` (invalid syntax).
        var rawMethodName = originalMethodDecl.GetSwiftName();
        var asyncKeyword = originalMethodDecl.IsAsync ? " async" : "";
        var awaitPrefix = originalMethodDecl.IsAsync ? "await " : "";
        var throwsClause = throws ? " throws" : "";
        var returnClause = (isVoid || hasLargeOptionalReturn) ? "" : $" -> {returnType}";
        var tryPrefix = throws ? "try " : "";
        // Use the canonical trim count passed from the loop, not a recomputed value.
        // This ensures the silgen function name matches the @_cdecl dispatch reference.
        var swiftFuncName = GetSilgenFuncName(GetOverloadSymbolBase(env), originalMethodDecl, trim);

        swiftWriter.WriteLine();

        // Merge the original method's availability with its parent type chain.
        // @_silgen_name wrappers are top-level (free funcs) or inside an extension that
        // doesn't inherit the target type's availability at the Swift compiler level, so
        // they must carry the same @available lines as the @_cdecl wrappers do.
        var mergedAvailability = WrapperEmitterHelpers.MergeAvailability(
            originalMethodDecl.AvailabilityAnnotations, parentTypeDecl);

        if (isFreeFunction)
        {
            // Free function — emit standalone @_silgen_name function
            var moduleName = ArraySliceNormalizationEmitter.UnescapeModuleName(originalMethodDecl.ModuleDecl?.Name ?? "");
            var callPrefix = !string.IsNullOrEmpty(moduleName) ? $"{moduleName}." : "";

            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, mergedAvailability);
            swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
            swiftWriter.WriteLine($"public func {swiftFuncName}{methodOwnGenericParams}({swiftParamString}){asyncKeyword}{throwsClause}{returnClause}{methodOwnWhereClause} {{");
            swiftWriter.Indent++;

            foreach (var line in derefLines)
                swiftWriter.WriteLine(line);

            var callExpr = $"{tryPrefix}{awaitPrefix}{callPrefix}{originalMethodName}({callArgString})";
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
        }
        else if (originalMethodDecl.IsConstructor)
        {
            // Constructor — emit as static factory function in extension
            var swiftModuleQualifiedName = parentTypeDecl!.SwiftTypeName.ModuleQualifiedName;

            // Constructor return type is the parent type itself
            var ctorReturnType = swiftModuleQualifiedName;
            var ctorReturnClause = originalMethodDecl.IsFailable
                ? $" -> {ctorReturnType}?"
                : $" -> {ctorReturnType}";

            // Availability must sit on the extension itself — the `extension ... {` line
            // references the target type, so an inner-function @available is too late.
            // The @_silgen_name wrapper inside is symbol-bearing, but this plain extension is not;
            // the anchor (led ahead of the availability) pins the symbol-less extension to the member
            // that owns it, and the post-processor strips it with the block it names.
            OriginAnchorEmitter.Write(swiftWriter, FragmentOwners.ForDeclWrapper(overloadDecl).Artifact);
            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, mergedAvailability);
            swiftWriter.WriteLine($"extension {swiftModuleQualifiedName} {{");
            swiftWriter.Indent++;

            swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
            swiftWriter.WriteLine($"public static func {swiftFuncName}{methodOwnGenericParams}({swiftParamString}){asyncKeyword}{throwsClause}{ctorReturnClause}{methodOwnWhereClause} {{");
            swiftWriter.Indent++;

            foreach (var line in derefLines)
                swiftWriter.WriteLine(line);

            var callExpr = $"{tryPrefix}{awaitPrefix}{swiftModuleQualifiedName}({callArgString})";
            swiftWriter.WriteLine($"return {callExpr}");

            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }
        else
        {
            // Type method — emit as extension
            var swiftModuleQualifiedName = parentTypeDecl!.SwiftTypeName.ModuleQualifiedName;
            bool isStatic = originalMethodDecl.MethodType == MethodType.Static;
            var staticKeyword = isStatic ? "static " : "";
            var selfPrefix = isStatic ? "Self" : "self";
            // Mutating methods on value types (struct/enum) require the `mutating` keyword on
            // the @_silgen_name shim — otherwise `self.{originalMethod}(...)` inside the
            // wrapper body fails to compile against an immutable `self`, the wrapper is
            // dropped from the dylib, and the @_cdecl trampoline that calls
            // `.pointee.{shim}(...)` references a missing symbol → EntryPointNotFoundException.
            // Classes don't need it (mutation flows through the reference).
            bool needsMutating = !isStatic
                && originalMethodDecl.IsMutating
                && !(parentTypeDecl is ClassDecl);
            var mutatingKeyword = needsMutating ? "mutating " : "";

            // Availability must sit on the extension itself — the `extension ... {` line
            // references the target type, so an inner-function @available is too late.
            // The @_silgen_name wrapper inside is symbol-bearing, but this plain extension is not;
            // the anchor (led ahead of the availability) pins the symbol-less extension to the member
            // that owns it, and the post-processor strips it with the block it names.
            OriginAnchorEmitter.Write(swiftWriter, FragmentOwners.ForDeclWrapper(overloadDecl).Artifact);
            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, mergedAvailability);
            swiftWriter.WriteLine($"extension {swiftModuleQualifiedName} {{");
            swiftWriter.Indent++;

            swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
            swiftWriter.WriteLine($"public {staticKeyword}{mutatingKeyword}func {swiftFuncName}{methodOwnGenericParams}({swiftParamString}){asyncKeyword}{throwsClause}{returnClause}{methodOwnWhereClause} {{");
            swiftWriter.Indent++;

            foreach (var line in derefLines)
                swiftWriter.WriteLine(line);

            var callExpr = $"{tryPrefix}{awaitPrefix}{selfPrefix}.{originalMethodName}({callArgString})";
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
    /// Creates a projected C# method key for an overload, matching the format used by
    /// HandleBaseDecl's GetProjectedCSharpMethodKey. (C6/C7)
    /// Internal so the concrete-specialization emitter can reserve each specialized primary's real
    /// signature in <see cref="MethodEnvironment.EmittedProjectedSignatures"/> before the overloads
    /// built on it run — a primary omits every defaulted parameter, so an overload that omits the same
    /// set would otherwise re-declare it (CS0111).
    /// Thin shim over <see cref="ProtocolSignatureHelper.BuildProjectedMethodKey"/> on the class path.
    /// The :228 caller passes env.SiblingPropertyNames; the CSM callers pass null, consistent with
    /// their trimEnv (which carries no sibling set).
    /// </summary>
    internal static string GetProjectedOverloadKey(MethodDecl overloadDecl, ITypeDatabase typeDatabase, IReadOnlySet<string>? siblingPropertyNames = null, string? nameOverride = null)
        => ProtocolSignatureHelper.BuildProjectedMethodKey(overloadDecl, typeDatabase, new ProtocolSignatureHelper.ProjectedKeyOptions
        {
            PropertyNames = siblingPropertyNames,
            IncludeParentTypeName = true,
            // The default-overload path never applies the closure-tombstone collapse and never logs
            // (no logger in scope) — matches this builder's prior behavior exactly.
            TreatAsClosureTombstone = false,
            Logger = null,
            NameOverride = nameOverride,
        });

    /// <summary>
    /// Finds a same-named declaration of the type that a consumer could not tell the candidate
    /// overload from: one whose own C# signature is the same (CS0111), or that a call site binds as
    /// well as the candidate (CS0121). The candidate's source declaration — and the declaration a
    /// specialized source was built from — is never its own rival.
    ///
    /// <para>Methods compare full projected signatures, so a sibling that only shares the arity does
    /// not cost the overload. Constructors keep the arity rule: a trimmed initializer can emit under a
    /// factory name that a sibling's projected key does not carry, so its real C# name cannot be
    /// rebuilt here.</para>
    /// </summary>
    private static bool TryFindSignatureCollision(
        MethodDecl sourceDecl,
        MethodDecl overloadDecl,
        string candidateKey,
        int candidateRequiredCount,
        MethodEnvironment env,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? collidingKey)
    {
        collidingKey = null;
        IEnumerable<MethodDecl>? siblingMethods = overloadDecl.ParentDecl switch
        {
            TypeDecl typeDecl => typeDecl.Methods,
            _ => overloadDecl.ModuleDecl?.Methods
        };
        if (siblingMethods == null)
            return false;

        var origin = sourceDecl.SpecializedFrom ?? sourceDecl;
        int overloadParamCount = overloadDecl.CSSignature.Count - 1;
        var candidateShape = OverloadAmbiguityGuard.ParseKey(candidateKey, candidateRequiredCount);
        foreach (var sibling in siblingMethods)
        {
            if (sibling.Name != overloadDecl.Name ||
                sibling.IsConstructor != overloadDecl.IsConstructor ||
                ReferenceEquals(sibling, sourceDecl) || ReferenceEquals(sibling, origin) ||
                sibling.MangledName == overloadDecl.MangledName ||
                sibling.MangledName == origin.MangledName)
            {
                continue;
            }

            if (overloadDecl.IsConstructor)
            {
                if (sibling.CSSignature.Count - 1 != overloadParamCount)
                    continue;
                collidingKey = GetProjectedOverloadKey(sibling, env.TypeDatabase, env.SiblingPropertyNames);
                return true;
            }

            // A module-internal sibling declares no C# member.
            if (sibling.IsModuleInternal)
                continue;

            var siblingKey = GetProjectedOverloadKey(sibling, env.TypeDatabase, env.SiblingPropertyNames);
            bool sameSignature = string.Equals(
                ConcreteProtocolSpecializationEmitter.CsmOverloadLedger.CanonicalSpelling(siblingKey),
                ConcreteProtocolSpecializationEmitter.CsmOverloadLedger.CanonicalSpelling(candidateKey),
                StringComparison.Ordinal);
            if (sameSignature || OverloadAmbiguityGuard.AreAmbiguous(
                    candidateShape,
                    OverloadAmbiguityGuard.ParseKey(
                        siblingKey, OverloadAmbiguityGuard.RequiredCountFor(sibling, env.TypeDatabase, siblingKey))))
            {
                collidingKey = siblingKey;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Builds the wrapper symbol name: DBW_{TypeName}_{MethodName}_{Hash8}_{TrimCount}
    /// </summary>
    private static string BuildWrapperSymbol(MethodDecl methodDecl, int trimCount)
        => BuildWrapperSymbol(methodDecl.MangledName, methodDecl, trimCount);

    /// <summary>
    /// AF13: emission-scoped overload — hashes the supplied <paramref name="baseSymbol"/>
    /// (the primary method's <c>env.EmissionSymbol</c>, promoted by MethodHandler before DPO runs)
    /// instead of <c>methodDecl.MangledName</c>. Once the parsed model stops mutating, the decl's
    /// MangledName reverts to its silgen value, so hashing the emission symbol keeps the DBW_ symbol
    /// stable on the promoted-symbol hash the wrapper actually intercepts.
    /// </summary>
    private static string BuildWrapperSymbol(string baseSymbol, MethodDecl methodDecl, int trimCount)
    {
        var parentDecl = methodDecl.ParentDecl as TypeDecl;
        var typeName = parentDecl?.Name ?? "Global";
        var hash = DeterministicHash8(baseSymbol);
        return $"DBW_{typeName}_{methodDecl.Name}_{hash}_{trimCount}";
    }

    /// <summary>
    /// Returns true if the method has any debug parameters (#file, #line, etc.).
    /// </summary>
    internal static bool HasDebugParameters(MethodDecl methodDecl)
        => methodDecl.CSSignature.Skip(1).Any(IsDebugParameter);

    /// <summary>
    /// Whether emission will install the debug-default-parameter Swift wrapper for this method.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the ONE definition of that decision: <c>MethodHandler</c> calls it to decide, and
    /// <see cref="WrapperValidation.IsAsyncCdeclEligible"/> calls it to predict. The install runs
    /// EARLY in the flag-setting phase and sets <c>UsesWrapperLibrary</c>, which every later wrapper
    /// branch — including the async <c>@_cdecl</c> promotion — treats as "another generator owns
    /// this method". A pre-emission caller reading the raw flag therefore sees `false` and predicts
    /// a promotion emission will decline, the exact inverse of reading a promotion flag that is not
    /// yet set.
    /// </para>
    /// <para>
    /// The <c>UsesWrapperLibrary</c> conjunct makes the predicate phase-agnostic: after the install
    /// the flag is set (and the debug params are gone from <c>CSSignature</c>), so this returns
    /// false while the caller's own <c>UsesWrapperLibrary</c> check does the declining.
    /// </para>
    /// </remarks>
    internal static bool WillInstallDebugParamWrapper(MethodDecl methodDecl)
        => !methodDecl.UsesWrapperLibrary && HasDebugParameters(methodDecl);

    /// <summary>
    /// Emits a Swift @_silgen_name wrapper that strips debug params from a method,
    /// letting Swift supply the defaults. Updates the MethodDecl's MangledName to
    /// point to the wrapper symbol so the P/Invoke targets it instead of the original.
    /// </summary>
    /// <summary>
    /// The same-named declaration the debug-parameter shim's call would reach instead of
    /// <paramref name="methodDecl"/> when no spelled-out default keeps it there, or null when the
    /// call is safe or the member takes a fallback that calls no Swift member by name. A non-null
    /// answer means the member must be declined before <see cref="EmitDebugParamWrapper"/>.
    /// </summary>
    internal static string? DebugParamCallWinner(MethodDecl methodDecl, ITypeDatabase? typeDatabase)
    {
        if (WrapperValidation.HasRawGenericTypeParams(methodDecl) ||
            methodDecl.IsModuleInternal ||
            (methodDecl.ParentDecl is TypeDecl parent && parent.IsModuleInternal) ||
            methodDecl.CSSignature.Skip(1).Any(a => !IsDebugParameter(a) && a.IsInOut && a.SwiftTypeSpec is ClosureTypeSpec))
            return null;
        return SwiftOverloadCallPinning.Plan(methodDecl, IsDebugParameter, typeDatabase).WinningSibling;
    }

    internal static void EmitDebugParamWrapper(SwiftWriter swiftWriter, MethodEnvironment env)
    {
        var methodDecl = env.MethodDecl;
        var parentTypeDecl = methodDecl.ParentDecl as TypeDecl;
        bool isFreeFunction = parentTypeDecl == null;

        // Build wrapper symbol. AF13: hash env.EmissionSymbol (== methodDecl.MangledName until the
        // model stops mutating; the promoted symbol thereafter) so the DBG_ symbol and the env
        // promotion below stay on the same base. wrapperSymbol feeds both the Swift @_silgen_name
        // string and the env promotion at the end, so both move together.
        var hash = DeterministicHash8(env.EmissionSymbol);
        var typeName = parentTypeDecl?.Name ?? "Global";
        var wrapperSymbol = $"DBG_{typeName}_{methodDecl.Name}_{hash}";

        // Gather kept (non-debug) params
        var keptArgs = methodDecl.CSSignature.Skip(1).Where(a => !IsDebugParameter(a)).ToList();
        // The call below leaves the debug params out; a same-named sibling without them would win
        // that call, so the plan spells them out (a magic literal means the same thing written out).
        var originalArgs = methodDecl.CSSignature.Skip(1).ToList();
        var pinPlan = SwiftOverloadCallPinning.Plan(methodDecl, IsDebugParameter, env.TypeDatabase);
        if (pinPlan.IsRefused && DebugParamCallWinner(methodDecl, env.TypeDatabase) != null)
            throw new InvalidOperationException(
                $"The debug-parameter shim for '{SwiftOverloadCallPinning.Describe(methodDecl)}' would call '{pinPlan.WinningSibling}'; " +
                "the caller must decline the member (DebugParamCallWinner) before installing the shim.");

        // Build Swift parameter list for the wrapper
        var swiftParams = new List<string>();
        var derefLines = new List<string>();
        // Bare names in this shim's signature resolve against every wrapper import, so a
        // bound-module type sharing a name with an imported module's type is ambiguous.
        // QualificationPolicy compares against NamedTypeSpec.Module, the raw ABI spelling —
        // never backtick-escaped (see the sibling derivation in ExistentialBypassEmitter for
        // why UnescapeModuleName is the wrong transform for this comparison key).
        var boundModuleName = methodDecl.ModuleDecl?.Name ?? "";
        // Sibling bindings so a reserved-name escape also dodges a sibling user param.
        // The call-arg loop below recomputes the identical set, keeping decls and values in sync.
        var siblings = CdeclParamMapper.CollectSiblingBindingNames(keptArgs);
        foreach (var arg in keptArgs)
        {
            // Keyword rename + sanitize + reserved/sibling escape (canonical helper). The
            // call-arg loop below derives the matching arg identically.
            var rawLabel = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            var label = CdeclParamMapper.BuildSwiftBindingName(rawLabel, siblings);
            if (OptionalPointerWrapperEmitter.ShouldWidenParam(arg, env.BoundGenericsHandler))
            {
                swiftParams.Add($"_ {label}: Swift.UnsafeRawPointer");
                derefLines.Add(OptionalPointerWrapperEmitter.GetDerefCode(arg, label, label, env.TypeDatabase));
            }
            else
            {
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpecForWrapperSignature(
                    arg.SwiftTypeSpec, boundModuleName);
                if (arg.SwiftTypeSpec is ClosureTypeSpec closureSpec)
                {
                    if (!swiftType.StartsWith("@escaping"))
                    {
                        if (closureSpec.IsAsync && !swiftType.Contains("@Sendable"))
                            swiftType = $"@escaping @Sendable {swiftType}";
                        else
                            swiftType = $"@escaping {swiftType}";
                    }
                    else if (closureSpec.IsAsync && !swiftType.Contains("@Sendable"))
                    {
                        swiftType = swiftType.Replace("@escaping ", "@escaping @Sendable ");
                    }
                }
                // Same inout-preservation requirement as EmitSwiftWrapper above — this is the
                // same @_silgen_name-shim shape (a debug-param-stripping variant, not a
                // trailing-default trim), so the original function type's `inout` must survive
                // here too, or the shim's ABI diverges from the original it intercepts.
                if (arg.IsInOut && !swiftType.StartsWith("inout "))
                {
                    swiftType = $"inout {swiftType}";
                }
                swiftParams.Add($"_ {label}: {swiftType}");
            }
        }

        // Large optional return buffer
        bool hasLargeOptionalReturn = env.BoundGenericsHandler.IsLargeOptionalReturn(methodDecl);
        if (hasLargeOptionalReturn)
            swiftParams.Add("_ _resultBuf: Swift.UnsafeMutableRawPointer");

        var swiftParamString = string.Join(", ", swiftParams);

        // Build call arguments, in the original's order, spelling out the debug params the plan pins
        var callArgs = new List<string>();
        foreach (var arg in originalArgs)
        {
            if (IsDebugParameter(arg))
            {
                if (pinPlan.PinFor(arg) is { } pin)
                    callArgs.Add(pin);
                continue;
            }
            // Identical to the param-decl loop (same sibling set + BuildSwiftBindingName) so the
            // value references the same binding.
            var rawLabel = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            var privateName = CdeclParamMapper.BuildSwiftBindingName(rawLabel, siblings);
            bool isWidened = OptionalPointerWrapperEmitter.ShouldWidenParam(arg, env.BoundGenericsHandler);
            var valueRef = isWidened ? $"{privateName}Val" : privateName;
            // Provenance-aware call label (canonical builder) — preserves labels that genuinely
            // begin with '_' (e.g. _self) and backtick-escapes keywords.
            var argStr = CdeclParamMapper.BuildSwiftCallArgLabel(arg);

            // Same `&`-forwarding requirement as EmitSwiftWrapper above; same widened-Optional
            // exclusion (GetDerefCode's local is a `let`, so `&{name}Val` would not compile).
            var refPrefix = (arg.IsInOut && !isWidened) ? "&" : "";

            // The shim receives an autoclosure parameter as a plain closure; SwiftClosureArgument
            // forwards its value for Swift to wrap back into the autoclosure.
            callArgs.Add(argStr + SwiftClosureArgument.Render(refPrefix + valueRef, arg.SwiftTypeSpec));
        }
        var callArgString = string.Join(", ", callArgs);

        // Return type
        var returnTypeSpec = methodDecl.CSSignature.First().SwiftTypeSpec;
        var returnType = ExistentialBypassEmitter.RenderSwiftTypeSpecForWrapperReturnType(
            returnTypeSpec, boundModuleName);
        bool isVoid = returnTypeSpec is TupleTypeSpec tupleTypeSpec && tupleTypeSpec.IsEmptyTuple;
        bool throws = methodDecl.Throws;
        var asyncKeyword = methodDecl.IsAsync ? " async" : "";
        var awaitPrefix = methodDecl.IsAsync ? "await " : "";
        var throwsClause = throws ? " throws" : "";
        var returnClause = (isVoid || hasLargeOptionalReturn) ? "" : $" -> {returnType}";
        var tryPrefix = throws ? "try " : "";

        var swiftFuncName = $"_dbg_{methodDecl.Name}_{hash}";

        // If the method has raw generic type params (τ_0_0, etc.), the Swift wrapper would
        // contain invalid source. Skip the entire wrapper — don't emit Swift code, don't
        // retarget MangledName, don't set UsesWrapperLibrary. Only strip debug params from
        // CSSignature so downstream C# emission doesn't see #file/#line params.
        // The method will emit via CallConvSwift targeting the original mangled name.
        if (WrapperValidation.HasRawGenericTypeParams(methodDecl))
        {
            StripDebugParameters(methodDecl);
            return;
        }

        // Internal methods: strip debug params but don't emit Swift wrapper — the wrapper
        // would call an inaccessible internal member. The method will use the original
        // mangled name via CallConvSwift (same pattern as raw generic type params above).
        if (methodDecl.IsModuleInternal ||
            (methodDecl.ParentDecl is TypeDecl parentInternal && parentInternal.IsModuleInternal))
        {
            StripDebugParameters(methodDecl);
            return;
        }

        // Same inout-closure conflict as TryEmitOverloads's trim shim (this is the same
        // @_silgen_name-shim shape): @escaping is forced onto every closure param above, but
        // `inout` + `@escaping` is rejected by swiftc. Same fallback as the two skips above.
        if (keptArgs.Any(a => a.IsInOut && a.SwiftTypeSpec is ClosureTypeSpec))
        {
            StripDebugParameters(methodDecl);
            return;
        }

        swiftWriter.WriteLine();

        if (isFreeFunction)
        {
            var moduleName = ArraySliceNormalizationEmitter.UnescapeModuleName(methodDecl.ModuleDecl?.Name ?? "");
            var callPrefix = !string.IsNullOrEmpty(moduleName) ? $"{moduleName}." : "";
            swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
            swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}){asyncKeyword}{throwsClause}{returnClause} {{");
            swiftWriter.Indent++;
            foreach (var line in derefLines) swiftWriter.WriteLine(line);
            var escapedMethodName = NameProvider.ParserNameToSwift(methodDecl);
            var callExpr = $"{tryPrefix}{awaitPrefix}{callPrefix}{escapedMethodName}({callArgString})";
            if (hasLargeOptionalReturn)
                foreach (var bufLine in OptionalPointerWrapperEmitter.GetReturnBufferCode(callExpr, returnType))
                    swiftWriter.WriteLine(bufLine);
            else
                swiftWriter.WriteLine(isVoid ? callExpr : $"return {callExpr}");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }
        else if (methodDecl.IsConstructor)
        {
            // Constructor — emit as static factory function in extension
            // (mirrors EmitSwiftOverloadWrapper constructor branch)
            var swiftModuleQualifiedName = parentTypeDecl!.SwiftTypeName.ModuleQualifiedName;
            var ctorReturnType = swiftModuleQualifiedName;
            var ctorReturnClause = methodDecl.IsFailable
                ? $" -> {ctorReturnType}?"
                : $" -> {ctorReturnType}";
            // The @_silgen_name wrapper inside is symbol-bearing, but this plain extension is not;
            // the anchor pins the symbol-less extension to the member that owns it, and the
            // post-processor strips it with the block it names.
            OriginAnchorEmitter.Write(swiftWriter, FragmentOwners.ForDeclWrapper(methodDecl).Artifact);
            swiftWriter.WriteLine($"extension {swiftModuleQualifiedName} {{");
            swiftWriter.Indent++;
            swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
            swiftWriter.WriteLine($"public static func {swiftFuncName}({swiftParamString}){asyncKeyword}{throwsClause}{ctorReturnClause} {{");
            swiftWriter.Indent++;
            foreach (var line in derefLines) swiftWriter.WriteLine(line);
            var callExpr = $"{tryPrefix}{awaitPrefix}{swiftModuleQualifiedName}({callArgString})";
            swiftWriter.WriteLine($"return {callExpr}");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }
        else
        {
            var swiftModuleQualifiedName = parentTypeDecl!.SwiftTypeName.ModuleQualifiedName;
            bool isStatic = methodDecl.MethodType == MethodType.Static;
            var staticKeyword = isStatic ? "static " : "";
            var selfPrefix = isStatic ? "Self" : "self";
            // Mirror the EmitSwiftWrapper rule: mutating methods on value types need
            // `mutating` on the @_silgen_name shim so `self.<originalMethod>(...)` compiles.
            bool dbgNeedsMutating = !isStatic
                && methodDecl.IsMutating
                && !(parentTypeDecl is ClassDecl);
            var dbgMutatingKeyword = dbgNeedsMutating ? "mutating " : "";
            // The @_silgen_name wrapper inside is symbol-bearing, but this plain extension is not;
            // the anchor pins the symbol-less extension to the member that owns it, and the
            // post-processor strips it with the block it names.
            OriginAnchorEmitter.Write(swiftWriter, FragmentOwners.ForDeclWrapper(methodDecl).Artifact);
            swiftWriter.WriteLine($"extension {swiftModuleQualifiedName} {{");
            swiftWriter.Indent++;
            swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
            swiftWriter.WriteLine($"public {staticKeyword}{dbgMutatingKeyword}func {swiftFuncName}({swiftParamString}){asyncKeyword}{throwsClause}{returnClause} {{");
            swiftWriter.Indent++;
            foreach (var line in derefLines) swiftWriter.WriteLine(line);
            var callExpr = $"{tryPrefix}{awaitPrefix}{selfPrefix}.{NameProvider.ParserNameToSwift(methodDecl)}({callArgString})";
            if (hasLargeOptionalReturn)
                foreach (var bufLine in OptionalPointerWrapperEmitter.GetReturnBufferCode(callExpr, returnType))
                    swiftWriter.WriteLine(bufLine);
            else
                swiftWriter.WriteLine(isVoid ? callExpr : $"return {callExpr}");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }

        // Promote the emission env's symbol to target the wrapper.
        env.PromoteSymbol(wrapperSymbol);
        methodDecl.UsesWrapperLibrary = true;

        // Remove debug params from CSSignature so downstream iterators
        // (marshalling, SafeHandle, closure callbacks) never see them.
        StripDebugParameters(methodDecl);
    }

    /// <summary>
    /// Removes the debug parameters from <paramref name="methodDecl"/>'s <see cref="MethodDecl.CSSignature"/>,
    /// keeping the list Swift declares in <see cref="MethodDecl.SignatureBeforeDebugStrip"/> for
    /// the overload replay of later calls.
    /// </summary>
    private static void StripDebugParameters(MethodDecl methodDecl)
    {
        methodDecl.SignatureBeforeDebugStrip ??= methodDecl.CSSignature;
        methodDecl.CSSignature = methodDecl.CSSignature
            .Where((a, i) => i == 0 || !IsDebugParameter(a))
            .ToList();
    }

    /// <summary>
    /// Returns true when every trailing default parameter has a SwiftDefaultExpression
    /// that maps to a valid C# compile-time constant. When true, the primary method
    /// already has inline `= value` defaults and overloads are unnecessary.
    /// </summary>
    internal static bool AllTrailingDefaultsAreCSharpMappable(MethodDecl methodDecl, ITypeDatabase typeDatabase)
    {
        var args = methodDecl.CSSignature.Skip(1).ToList();
        if (args.Count == 0) return false;

        bool foundAnyDefault = false;
        // Surface generic-parameter names visible in the method scope so a `nil` default on a
        // sugared unconstrained-T (e.g. `Value?`) maps to `default` and counts as mappable
        // — same threading as MethodSignature.ResolveDefaultValues.
        var visibleGenericNames = BaseHandler.CollectVisibleGenericParamNames(methodDecl);

        // Walk backward through trailing defaults
        for (int i = args.Count - 1; i >= 0; i--)
        {
            if (IsDebugParameter(args[i]))
                continue;
            if (!args[i].HasDefaultArg)
                break;
            foundAnyDefault = true;
            // Must have both the Swift expression AND a successful C# mapping
            if (args[i].SwiftDefaultExpression == null)
                return false;
            var mapped = SwiftDefaultValueMapper.TryMapToCSharpDefault(
                args[i].SwiftDefaultExpression!, args[i].SwiftTypeSpec, typeDatabase,
                visibleGenericNames);
            if (mapped == null)
                return false;
        }

        return foundAnyDefault;
    }

    /// <summary>
    /// Computes the canonical Swift function name for a default-parameter overload's
    /// @_silgen_name wrapper. Used by both <see cref="EmitSwiftWrapper"/> and the
    /// @_cdecl dispatch section in <see cref="TryEmitOverloads"/> to ensure they
    /// reference the same function name.
    /// </summary>
    internal static string GetSilgenFuncName(MethodDecl methodDecl, int trimCount)
        => GetSilgenFuncName(methodDecl.MangledName, methodDecl, trimCount);

    /// <summary>
    /// AF13: emission-scoped overload — hashes the supplied <paramref name="baseSymbol"/>
    /// (the primary method's <c>env.EmissionSymbol</c>) so the <c>_dbw_</c> Swift function name
    /// matches the DBW_ symbol built from the same promoted symbol, independent of the decl's
    /// (now-immutable, silgen-valued) MangledName.
    /// </summary>
    internal static string GetSilgenFuncName(string baseSymbol, MethodDecl methodDecl, int trimCount)
    {
        var rawMethodName = methodDecl.GetSwiftName();
        return $"_dbw_{rawMethodName}_{DeterministicHash8(baseSymbol)}_{trimCount}";
    }

    internal static string DeterministicHash8(string input) => EmitterUtility.DeterministicHash8(input);

    /// <summary>
    /// Detects Swift compiler-injected debug parameters (#file, #line, #column, #function).
    /// These always have HasDefaultArg=true and use specific type+name combinations:
    ///   - file/_file with StaticString (NOT String — real file params use String)
    ///   - line/_line with UInt (NOT Int — #line produces UInt)
    ///   - column/_column with UInt
    ///   - function/_function with StaticString
    /// </summary>
    internal static bool IsDebugParameter(ArgumentDecl arg)
    {
        if (!arg.HasDefaultArg)
            return false;

        var name = arg.Name;
        // Strip leading underscore for matching (Swift convention for hiding labels)
        var baseName = name.StartsWith("_") ? name.Substring(1) : name;

        if (arg.SwiftTypeSpec is not NamedTypeSpec namedType)
            return false;

        var typeName = namedType.Name;

        return baseName switch
        {
            "file" or "filePath" => typeName == "Swift.StaticString",
            "line" or "column" => typeName == "Swift.UInt",
            "function" => typeName == "Swift.StaticString",
            _ => false
        };
    }
}
