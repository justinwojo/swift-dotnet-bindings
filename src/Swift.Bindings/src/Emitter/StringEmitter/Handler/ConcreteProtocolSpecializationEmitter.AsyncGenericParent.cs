// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Parent-only async CSM emission — the async sibling of the sync emission inside
/// <see cref="ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializationsForGenericParent"/>.
/// <para>
/// The async CSM path previously hard-rejected generic parents at three sites:
/// <c>PassesAsyncMethodLevelGuards</c> (blanket <c>parentTypeDecl.IsGeneric</c>),
/// <c>EmitConcreteSpecializationsForGenericParent</c> (an <c>if (method.IsAsync) continue;</c>
/// skip inside the per-conformer extension loop), and <c>IsCsmAsyncEligible</c>
/// (an <c>ownParamCount == 0</c> bail keyed to the closed-conformer + method-own-generic
/// shape). This file adds the parent-only-async predicate
/// <see cref="ConcreteProtocolSpecializationEmitter.IsCsmAsyncEligibleForGenericParent"/>
/// and the matching emitter
/// <see cref="ConcreteProtocolSpecializationEmitter.TryEmitParentOnlyAsyncOverload"/>, both
/// scoped tight to the shape that <c>MusicLibraryRequest&lt;T&gt;.response()</c> needs:
/// instance method, zero method-own generic params, zero method parameters, return type
/// substitutes through the parent's associated-type table to a closed ISwiftObject.
/// </para>
/// <para>
/// Hand-rolled to avoid large refactors: the existing async harness
/// machinery (<see cref="AsyncHarnessEmitter"/> + <see cref="WrapperEmitter"/>) is
/// instance-shaped, references <c>_payload</c> / <c>_handle</c> / <c>this</c> throughout
/// the emit pipeline, and the Swift wrapper template branches on <c>MethodType.Static</c>
/// vs instance — plumbing an <c>isExtension</c> flag through all of it would be a large,
/// risky refactor for one new emission shape. Instead the public C# extension body and
/// Swift @_cdecl wrapper are emitted directly here, reusing only the shared substitution +
/// pairing-validation helpers from <c>ConcreteProtocolSpecializationEmitter.Async.cs</c>.
/// </para>
/// </summary>
public static partial class ConcreteProtocolSpecializationEmitter
{
    // ─── Eligibility (consumed by MemberValidationPipeline) ─────

    /// <summary>
    /// Returns true if the method will be routed through the parent-only async CSM
    /// emission path on a generic parent, meaning the pipeline's unspecialized
    /// generic emission should be suppressed.
    /// <para>
    /// Mirrors the skip conditions of <see cref="TryEmitParentOnlyAsyncOverload"/> exactly
    /// so the predicate cannot declare suppressibility for a method the emitter will then
    /// drop (same contract as <c>IsCsmSyncEligibleForGenericParent</c>). The pipeline calls this AFTER the closed-
    /// conformer async eligibility predicate <c>IsCsmAsyncEligible</c> so the two routes
    /// don't trip over each other.
    /// </para>
    /// </summary>
    public static bool IsCsmAsyncEligibleForGenericParent(
        MethodDecl method,
        TypeDecl parentTypeDecl,
        ITypeDatabase typeDatabase,
        ConcreteSpecializationEngine engine)
    {
        if (!method.IsAsync) return false;
        if (method.IsAccessor) return false;
        if (method.IsConstructor) return false;
        if (method.MethodType == MethodType.Static) return false;
        if (method.IsMutating) return false;
        if (method.IsMainActorIsolated || method.IsActorIsolated) return false;
        if (method.HasTypedThrows) return false;

        // Only generic-parent cases route here. Non-generic parents go through the
        // existing closed-conformer async CSM path (IsCsmAsyncEligible).
        if (!parentTypeDecl.IsGeneric) return false;

        // Nested generic parents can't host an extension class on a closed receiver
        // from outside their enclosing type — same restriction as sync CSM.
        if (parentTypeDecl.ParentDecl is TypeDecl) return false;

        // Value-type parents only. The Swift wrapper does `let __self = self_.pointee`
        // before `Task { await __self.{call}() }` — that capture is sound for structs/enums
        // (value-type copy is Sendable when the constraint chain is) but a class parent
        // would copy the strong reference into the Task, extending lifetime past the
        // synchronous wrapper return and tangling with ARC + Sendable enforcement.
        // Value-type copy is Sendable when the constraint chain is; lift to
        // class parents only when an emit shape that retains/releases through a separate
        // capture box is implemented.
        if (parentTypeDecl is ClassDecl) return false;

        if (!WrapperValidation.IsXCFrameworkMode(typeDatabase)) return false;

        // Method-own generics stay rejected — they would require existential opening
        // and per-generic specialization that the parent-only emitter does not yet
        // implement. Wider shapes still emit on the open-generic surface unchanged.
        var parentParamNames = new HashSet<string>(
            parentTypeDecl.GenericParameters.Select(p => p.TypeName));
        var ownGenericCount = method.GenericParameters
            .Count(p => !parentParamNames.Contains(p.TypeName));
        if (ownGenericCount != 0) return false;

        // CSSignature[0] is the return type. Any further entries are parameters.
        // Parameter signatures are accepted selectively for Swift.String params only:
        // every method parameter must classify as Utf8Slice, otherwise the emitter
        // does not yet have a marshalling path for it and we stay rejected. Other
        // ABI categories continue to fall back to the open-generic surface
        // unchanged (selective-opt-out pattern, same shape as NCB/GCB allowlist).
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var arg = method.CSSignature[i];
            if (arg.SwiftTypeSpec.IsEmptyTuple) return false;
            if (MethodClosureBridge.ClassifyParam(arg, typeDatabase)
                != MethodClosureBridge.ParamAbiCategory.Utf8Slice)
                return false;
        }

        var specializable = engine.FindSpecializableMethods(parentTypeDecl)
            .FirstOrDefault(sm => ReferenceEquals(sm.Method, method));
        if (specializable is null) return false;

        // Must have parent-generic specialization; without it the emission would
        // remain on the open-generic surface, not the per-conformer extension.
        if (!specializable.SpecializableParams.Any(p => p.IsParentGeneric)) return false;

        var moduleName = parentTypeDecl.SwiftTypeName.Module;
        var pairingCount = ComputePairingCount(specializable.SpecializableParams);
        if (pairingCount == 0 || pairingCount > MaxCsmCartesianProductSize) return false;

        // Walk the cartesian product the same way the emitter does and return true on
        // the first pairing that fully validates.
        foreach (var pairing in CartesianPairings(specializable.SpecializableParams))
        {
            if (!ConformerPairingSatisfiesCoupling(pairing)) continue;
            // Mirror the per-method where-clause filter the emitter applies before
            // TryEmitParentOnlyAsyncOverload (see ConcreteProtocolSpecializationEmitter.cs
            // line 2663). Without this, the predicate can return true on a pairing the
            // emitter later rejects, and the pipeline's RoutedElsewhere has already
            // suppressed the open-generic surface → silent method drop.
            if (!engine.ParentTupleSatisfiesMethodConstraints(method, parentTypeDecl, pairing))
                continue;
            if (!IsEmittableParentOnlyAsyncPairing(
                    method, parentTypeDecl, pairing, typeDatabase, moduleName,
                    out _, out _))
                continue;
            return true;
        }
        return false;
    }

    // ─── Per-pairing emitter (called from EmitConcreteSpecializationsForGenericParent) ─

    /// <summary>
    /// Emits a single parent-only async CSM overload: per-conformer success and (for
    /// throwing methods) error <c>[UnmanagedCallersOnly]</c> callbacks, the
    /// <c>[LibraryImport]</c> P/Invoke, the public <c>Task&lt;T&gt;</c> extension
    /// wrapper, and the Swift <c>@_cdecl</c> async function that drives them. Returns
    /// true if the overload emitted, false if any per-pairing gate rejected.
    /// <para>
    /// The caller (<see cref="EmitConcreteSpecializationsForGenericParent"/>) has
    /// already opened the per-conformer-tuple <c>*CsmExtensions</c> partial class
    /// and indented into its body — this method writes directly into that scope.
    /// </para>
    /// </summary>
    internal static bool TryEmitParentOnlyAsyncOverload(
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
        ILogger logger)
    {
        // Method-level guards — must agree with the predicate above.
        if (!method.IsAsync) return false;
        if (method.IsAccessor || method.IsConstructor) return false;
        if (method.MethodType == MethodType.Static) return false;
        if (method.IsMutating) return false;
        if (method.IsMainActorIsolated || method.IsActorIsolated) return false;
        if (method.HasTypedThrows) return false;
        // Selective per-param admission walk — mirror of the predicate. See
        // IsCsmAsyncEligibleForGenericParent for the rationale. Only Utf8Slice
        // (Swift.String) params are admitted; every other ABI category routes
        // through the open-generic surface.
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var arg = method.CSSignature[i];
            if (arg.SwiftTypeSpec.IsEmptyTuple) return false;
            if (MethodClosureBridge.ClassifyParam(arg, typeDatabase)
                != MethodClosureBridge.ParamAbiCategory.Utf8Slice)
                return false;
        }
        // Value-type parents only (see predicate for rationale).
        if (parentTypeDecl is ClassDecl) return false;

        if (!IsEmittableParentOnlyAsyncPairing(
                method, parentTypeDecl, pairing, typeDatabase, moduleName,
                out var substitutedReturnSpec, out var returnIsBlittable))
        {
            return false;
        }

        // Build symbol name — mirror the sync CSM convention (SBW_CSM_ prefix +
        // module + parent + conformers + method + 8-char hash) plus an `_async`
        // suffix so the sync and async wrappers for the same method+conformer
        // tuple stay distinct on both the Swift and the C# sides.
        var parentConformerEntries = pairing.Where(p => p.Param.IsParentGeneric).ToList();
        var safeConformerName = string.Join(
            "_",
            parentConformerEntries.Select(p => SanitizeTypeName(p.Conformer.SwiftQualifiedName)));
        var hashInput = method.MangledName
            + string.Concat(pairing.Select(p => "|" + p.Conformer.SwiftQualifiedName))
            + "|async";
        var mangledHash = EmitterUtility.DeterministicHash8(hashInput);
        var cdeclSymbol =
            $"SBW_CSM_{moduleName}_{parentTypeDecl.Name}_{safeConformerName}_{method.Name}_{mangledHash}_async";

        // C# signature dedup — same shape as the sync emitter, scoped to the
        // extension class via the caller-supplied `emittedSignatures` set.
        var csMethodName = NameProvider.ToPascalCase(method.Name) + "Async";
        var sigKey = $"async|{csMethodName}";
        if (!emittedSignatures.Add(sigKey))
        {
            logger.LogDebug(
                "CSM-async (parent-only): Skipping {Method} for {Pairing} — duplicate C# signature.",
                method.Name, safeConformerName);
            return false;
        }

        // Wrapper-symbol registry guard. Same `SBW_CSM_` prefix as the sync path, but the
        // `_async` suffix on the hash seed + symbol means a sync and async wrapper for the
        // same (method, pairing) never collide. Per-kind method bucket is collision-safe.
        if (!emissionContext.TryAddMethodWrapperSymbol(cdeclSymbol))
        {
            return false;
        }

        // Resolve the closed Swift parent name and C# extension receiver name. These
        // are the only spelling primitives needed for the Swift @_cdecl wrapper and
        // the C# `this {Parent}<{Conformer}> bag` parameter.
        var parentSwiftName = BuildConcreteParentSwiftName(parentTypeDecl, pairing);
        var parentCsName = BuildConcreteParentCsharpName(parentTypeDecl, pairing, typeDatabase);

        // Resolve the substituted return type on both sides. The Swift side uses the
        // module-qualified Swift type name (so `Foundation.MusicLibraryResponse` survives
        // round-trip into a different module's Swift wrapper); the C# side uses the
        // TypeDatabase's projected C# name (`StringResponse`, with its namespace if any).
        var returnSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(substitutedReturnSpec);
        var returnCsType = ResolvePublicCSharpType(substitutedReturnSpec, typeDatabase);

        bool throws = method.Throws;

        // Merge availability across method + parent + every pairing conformer. Same
        // contract as the sync CSM path.
        var mergedAvailability = WrapperEmitterHelpers.MergeAvailability(
            method.AvailabilityAnnotations, parentTypeDecl);
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

        // ── Emit Swift @_cdecl wrapper ────────────────────────────────
        EmitParentOnlyAsyncSwiftWrapper(
            swiftWriter, method, parentSwiftName, returnSwiftType,
            cdeclSymbol, moduleName, throws, typeDatabase, emissionContext,
            mergedAvailability);

        // ── Emit error helper P/Invokes (throws only) ─────────────────
        // SBW_GetErrorDescription + SBW_ReleaseError are dedup-keyed by C# type
        // (`*CsmExtensions`); they emit once per extension class regardless of
        // how many throwing async overloads land there.
        if (throws)
        {
            // Compute the same parent-conformer-suffix typeKey the sync CSM throws path
            // uses, so that if the extension class ALREADY hosts a throwing sync overload
            // we don't double-emit the helper P/Invokes.
            var parentTupleNames = pairing
                .Where(p => p.Param.IsParentGeneric)
                .Select(p => SanitizeTypeName(p.Conformer.CSharpType));
            var csTypeKey = $"{parentTypeDecl.Name}{string.Concat(parentTupleNames)}CsmExtensions";

            ErrorDescriptionEmitter.EmitCSharpBaseErrorPInvokesIfNeeded(
                csWriter, csTypeKey, moduleName, wrapperLibPath,
                pInvokeHelperContext: null, emissionContext);
        }

        // ── Emit C# extension body + P/Invoke + callbacks ─────────────
        EmitParentOnlyAsyncCSharpExtension(
            csWriter, method, parentCsName, returnCsType,
            cdeclSymbol, csMethodName, wrapperLibPath, throws,
            mergedAvailability, parentTypeDecl, typeDatabase, returnIsBlittable);

        method.WasEmitted = true;

        logger.LogInformation(
            "Emitted parent-only async specialization: {Type}.{Method}<{Pairing}> → {Symbol}",
            parentTypeDecl.Name, method.Name, safeConformerName, cdeclSymbol);

        return true;
    }

    // ─── Pairing-level validation (shared between predicate + emitter) ─

    /// <summary>
    /// Substitutes the conformer pairing into the method's return type, then runs the
    /// per-pairing structural guards (hint scope, ObjC bridging, NativeTypeName, return
    /// type resolves to a non-IntPtr C# spelling). Returns the substituted return
    /// <see cref="TypeSpec"/> on success so the predicate doesn't redo the work.
    /// </summary>
    private static bool IsEmittableParentOnlyAsyncPairing(
        MethodDecl method,
        TypeDecl parentTypeDecl,
        IReadOnlyList<(ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)> pairing,
        ITypeDatabase typeDatabase,
        string moduleName,
        out TypeSpec substitutedReturnSpec,
        out bool returnIsBlittable)
    {
        substitutedReturnSpec = null!;
        returnIsBlittable = false;

        // Hint-scope + module-allowlist + opaque/objc guards — parity with the closed-
        // conformer async path's IsEmittableAsyncPairing.
        for (int i = 0; i < pairing.Count; i++)
        {
            var param = pairing[i].Param;
            var conformer = pairing[i].Conformer;

            if (!ConcreteSpecializationEngine.HasKnownHintConformers(
                    param.ConstraintProtocol.ToString(), moduleName))
                return false;

            if (!ConcreteSpecializationEngine.IsConformerAllowedForModule(conformer, moduleName))
                return false;

            // Nested-conformer guard. The synchronous CSM path resolves nested-type
            // conformers (via ClassifyConformerStructurally + ResolveConformerCSharpTypeRef)
            // and emits them by their post-rename C# name. That relaxation is deliberately
            // NOT extended to the async path: parent-only async specialization only ever runs
            // over hint-registered conformers (the HasKnownHintConformers gate above), and no
            // nested-type hint conformer exists — every shape that drives nested conformers
            // (HPKE Sender/Recipient inits) is synchronous. Until an async surface needs a
            // nested hint conformer there is nothing to name here, so this stays a hard reject
            // rather than duplicating the sync re-resolution with no test coverage.
            if (conformer.SwiftType != null &&
                conformer.SwiftType.ModuleQualifiedName.Split('.').Length > 2)
                return false;

            // ObjC bridging / NativeTypeName: same exclusion as the closed-conformer
            // async path; the marshalling would need ObjC-handle paths we don't yet
            // implement on the parent-only-async surface.
            if (conformer.SwiftType != null &&
                typeDatabase.TryGetTypeRecord(conformer.SwiftType, out var conformerRecord) &&
                (conformerRecord.NativeTypeName != null
                    || MarshallingHelpers.IsObjCBridged(conformerRecord)
                    || MarshallingHelpers.IsObjCRooted(conformerRecord)))
                return false;
        }

        // Build conformer TypeSpecs once. Mirrors TryBuildEmissionPlan in
        // ConcreteProtocolSpecializationEmitter.Async.cs.
        var conformerTypeSpecs = new NamedTypeSpec[pairing.Count];
        for (int i = 0; i < pairing.Count; i++)
        {
            if (!TryBuildConformerTypeSpec(pairing[i].Conformer, out conformerTypeSpecs[i]))
                return false;
        }

        // Substitute the conformer pairing into the return type. Bail on any unresolved
        // associated-type reference — without a known resolution we'd emit invalid Swift
        // and C# referencing a placeholder identifier.
        var returnSpec = method.CSSignature.First().SwiftTypeSpec;
        if (returnSpec.IsEmptyTuple) return false; // void-returning async — not yet supported

        var current = returnSpec;
        for (int i = 0; i < pairing.Count; i++)
        {
            var genericName = pairing[i].Param.GenericParam.TypeName;
            var altGenericName = GetAlternateDepthName(genericName);
            bool substitutionOk = true;
            current = SubstituteTypeSpec(
                current, genericName, altGenericName,
                conformerTypeSpecs[i], pairing[i].Conformer, ref substitutionOk);
            if (!substitutionOk) return false;
        }

        // The substituted return must resolve to a known C# type spelling. ResolvePublicCSharpType
        // returns "IntPtr" only when it could not name the type — reject that as a hard fail so
        // we never emit `Task<IntPtr>` extensions for unknown types.
        if (current is not NamedTypeSpec) return false;
        var resolved = ResolvePublicCSharpType(current, typeDatabase);
        if (string.IsNullOrEmpty(resolved) || resolved == "IntPtr") return false;

        // Substituted return must classify as one of two well-defined emission shapes:
        //
        // (a) ISwiftObject-backed (non-frozen struct, "ClassWithOpaquePayload"). The async
        //     harness allocates a carrier via NativeMemory.Alloc, hands it to Swift's @_cdecl
        //     wrapper (which initializeMemory's the result into it), then C# MarshalFromSwift
        //     wraps the SAME pointer in a SwiftSafeHandle via NewFromPayload. The SafeHandle
        //     owns the buffer — its ReleaseHandle calls NativeMemory.Free on Dispose. So the
        //     success callback must NOT free `resultPtr` (doing so double-frees and the first
        //     read of the returned object dereferences poisoned memory → SIGSEGV).
        //
        // (b) Blittable primitive (Int/UInt/Float/Double family, CGFloat — see
        //     `CdeclParamMapper.IsBlittablePrimitiveSwiftType`). The Swift wrapper
        //     initializeMemory's the value into the carrier identically; on the C# side
        //     MarshalFromSwift<T> returns a *value copy*, so the success callback MUST free
        //     the carrier or the buffer leaks.
        //
        // Anything else (Swift class, frozen-struct-projected-as-class, complex enum) still
        // rejects here — each needs a different copy/retain/release sequence.
        if (current is NamedTypeSpec namedReturn)
        {
            if (!IsReturnTypeEmittable(namedReturn, typeDatabase, out returnIsBlittable))
                return false;
        }

        substitutedReturnSpec = current;
        return true;
    }

    /// <summary>
    /// Returns true when the substituted return type matches one of the two well-defined
    /// emission shapes the parent-only async path supports, and sets
    /// <paramref name="isBlittablePrimitive"/> to indicate which one:
    /// <list type="bullet">
    /// <item><term><c>false</c> (SafeHandle-backed)</term>
    /// <description>Non-frozen struct ("ClassWithOpaquePayload"). Its <c>NewFromPayload</c>
    /// wraps the SAME <c>NativeMemory.Alloc</c>'d pointer in a <c>SwiftSafeHandle</c>
    /// directly (no <c>InitializeWithCopy</c>, no class-pointer re-read). The
    /// SafeHandle's <c>ReleaseHandle</c> later frees via <c>NativeMemory.Free</c>, so
    /// the success-callback MUST elide its own free or the buffer is double-freed and
    /// the first read of the returned object dereferences poisoned memory.</description></item>
    /// <item><term><c>true</c> (blittable primitive, owns-buffer)</term>
    /// <description>Numeric primitive admitted by
    /// <see cref="CdeclParamMapper.IsBlittablePrimitiveSwiftType"/> (Int/UInt/Float/Double
    /// family, CGFloat). <c>MarshalFromSwift&lt;T&gt;</c> returns a *value copy*, so the
    /// success-callback OWNS the carrier and MUST <c>NativeMemory.Free</c> it after
    /// reading. Blittable returns were previously rejected conservatively.</description></item>
    /// </list>
    /// <para>
    /// Deliberately rejected (each would need its own emit shape, deferred to a future
    /// session that actually exercises it):
    /// <list type="bullet">
    /// <item><term><see cref="TypeRecordKind.Class"/></term><description>Swift class. The wire
    /// carrier holds an inline class pointer (with +1 retain) but <c>NewFromPayload</c>
    /// wraps THAT pointer in a <c>SwiftClassHandle</c> — the carrier itself is
    /// transient. Skipping the free leaks the carrier; including it races the ARC
    /// retain extraction. Needs read-then-free emit.</description></item>
    /// <item><term>Frozen struct projected as class (<c>IsFrozenStructProjectedAsClass</c>,
    /// "ClassWithBufferStruct")</term><description>Its <c>NewFromPayload</c> does an
    /// <c>InitializeWithCopy</c> from the wire carrier into a fresh
    /// <c>NativeMemory.Alloc</c> buffer owned by the SafeHandle. The wire carrier
    /// holds +1 retains that must be VWT-destroyed then <c>NativeMemory.Free</c>'d
    /// separately.</description></item>
    /// <item><term>Complex enum (<see cref="TypeRecordKind.Enum"/> + non-simple)</term>
    /// <description>Unverified whether its <c>NewFromPayload</c> is the simple
    /// pointer-wrap or the copy-into-fresh-buffer shape. Reject conservatively until
    /// a fixture proves the contract.</description></item>
    /// <item><term>Bool</term><description>Excluded from <c>IsBlittablePrimitiveSwiftType</c>
    /// because its Optional uses extra-inhabitants; not needed for the current parent-only
    /// async surface and would deserve its own fixture before lifting.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    private static bool IsReturnTypeEmittable(
        NamedTypeSpec namedReturn,
        ITypeDatabase typeDatabase,
        out bool isBlittablePrimitive)
    {
        isBlittablePrimitive = false;

        // Blittable-primitive admission first — `IsBlittablePrimitiveSwiftType` is the
        // canonical allowlist used by every other emitter (CdeclParamMapper, ClosureEmitter,
        // OptionalMarshalStrategy, ClosureHandler), so widening the parent-only async
        // surface through it keeps the strict-vs-loose contract aligned with the rest of
        // the generator. Bool is intentionally excluded from that allowlist (see comment
        // in `CdeclParamMapper.IsBlittablePrimitiveSwiftType`).
        if (CdeclParamMapper.IsBlittablePrimitiveSwiftType(namedReturn.Name))
        {
            isBlittablePrimitive = true;
            return true;
        }

        // Otherwise require a TypeRecord and the non-frozen-struct ("ClassWithOpaquePayload")
        // shape, whose NewFromPayload wraps the incoming pointer directly.
        try
        {
            var typeName = SwiftTypeName.FromModuleQualifiedName(namedReturn.Name);
            if (!typeDatabase.TryGetTypeRecord(typeName, out var returnRecord))
                return false;
            return returnRecord.Kind == TypeRecordKind.Struct
                   && !MarshallingHelpers.IsTypeFrozen(returnRecord);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // ─── Swift @_cdecl wrapper emission ─────────────────────────────────

    /// <summary>
    /// Emits the Swift <c>@_cdecl</c> async wrapper for a parent-only async overload:
    /// closed parent self deserialization, `Task { await ... }` driver, indirect-result
    /// initializeMemory, and (for throwing methods) a do/catch with an error-callback
    /// invocation that carries the boxed Swift error pointer back to C#.
    /// </summary>
    private static void EmitParentOnlyAsyncSwiftWrapper(
        SwiftWriter swiftWriter,
        MethodDecl method,
        string parentSwiftName,
        string returnSwiftType,
        string cdeclSymbol,
        string moduleName,
        bool throws,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        IReadOnlyList<AvailabilityAnnotation>? mergedAvailability)
    {
        // Param layout (must match the C# pinvokeParams below in the same order):
        //   resultPtr: UnsafeMutableRawPointer (indirect return)
        //   <Utf8Slice user params...> (each → Utf8Ptr + Utf8Len pair)
        //   self_: UnsafeRawPointer (non-mutating borrowed const pointer)
        //   completion: @convention(c) (UnsafeMutableRawPointer, UnsafeMutableRawPointer) -> Void
        //   errorCallback (throws only): same signature
        //   context: UnsafeMutableRawPointer (GCHandle for TCS)
        var swiftParams = new List<string>
        {
            "_ resultPtr: UnsafeMutableRawPointer",
        };

        // Per-param Utf8Slice marshalling. The selective-opt-out gate above has
        // already filtered to Utf8Slice only — every arg here is Swift.String.
        // Inline the mapping rather than threading CdeclParamMapper through this
        // scope (no MethodEnvironment available here, and the Utf8Slice case is a
        // single literal that matches the MGBE/AMGBE/CPSE shape verbatim).
        //
        // Reconstructions emit BEFORE the `Task {` block so the Swift String
        // captures into the Task body; the raw Utf8 pointer's lifetime is bound
        // to the synchronous wrapper return only.
        var callArgs = new List<string>();
        var reconstructions = new List<string>();
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            label = SwiftBuilder.SanitizeIdentifier(label);
            if (NameProvider.IsSwiftKeyword(label))
                label = $"{label}Param";
            var argLabel = ClosureEmitter.GetSwiftArgLabelForCdecl(arg);
            swiftParams.Add($"_ _{label}Utf8Ptr: UnsafePointer<UInt8>");
            swiftParams.Add($"_ _{label}Utf8Len: Int");
            reconstructions.Add(
                $"let _{label}Val = String(bytes: UnsafeBufferPointer(start: _{label}Utf8Ptr, count: _{label}Utf8Len), encoding: .utf8)!");
            callArgs.Add($"{argLabel}_{label}Val");
        }

        swiftParams.Add("_ self_: UnsafeRawPointer");
        swiftParams.Add(
            "_ completion: @convention(c) (UnsafeMutableRawPointer, UnsafeMutableRawPointer) -> Void");
        if (throws)
        {
            swiftParams.Add(
                "_ errorCallback: @convention(c) (UnsafeMutableRawPointer, UnsafeMutableRawPointer) -> Void");
        }
        swiftParams.Add("_ context: UnsafeMutableRawPointer");

        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            method.ParentDecl, method.IsMainActorIsolated, method.IsNonisolated);

        var methodCallSwift = NameProvider.ParserNameToSwift(method);
        var callArgsSwift = string.Join(", ", callArgs);

        swiftWriter.WriteLine();
        swiftWriter.WriteLine(
            $"// Parent-only async specialization: {parentSwiftName}.{method.Name}");

        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, cdeclSymbol, needsMainActor, mergedAvailability);
        swiftWriter.WriteLine($"public func {cdeclSymbol}(");
        swiftWriter.WriteLine($"    {string.Join(",\n    ", swiftParams)}");
        swiftWriter.WriteLine(") {");

        // Pre-read the self value before launching the Task. The Task captures `__self`
        // (a `let` value-typed copy), not the raw pointer — the pointer's lifetime is
        // bound to the synchronous wrapper call and is unsafe to dereference inside the
        // async closure. The value-type copy is safe because the entire fixture surface
        // (and the design target, MusicLibraryRequest<T>) operates on Sendable value
        // types under the `where Item.Response: Sendable` constraint.
        swiftWriter.WriteLine(
            $"    let __self = self_.assumingMemoryBound(to: {parentSwiftName}.self).pointee");

        // Reconstruct Utf8Slice params BEFORE launching the Task. Each reconstruction is
        // `let _{label}Val = String(bytes: UnsafeBufferPointer(start: …, count: …), encoding: .utf8)!`.
        // The Task body captures the Swift String values, NOT the raw Utf8 pointers — the
        // pointer lifetime is bound to the synchronous wrapper return (same lifetime rule
        // as `__self` above). Mirrors AMGBE's reconstruction-before-Task pattern.
        foreach (var reconstruction in reconstructions)
            swiftWriter.WriteLine($"    {reconstruction}");

        // Launch the Task. `@_cdecl` callees return synchronously to C#; the async work
        // proceeds in the background and signals C# via the completion callback.
        swiftWriter.WriteLine("    Task {");

        if (throws)
        {
            swiftWriter.WriteLine("        do {");
            swiftWriter.WriteLine(
                $"            let _result = try await __self.{methodCallSwift}({callArgsSwift})");
            swiftWriter.WriteLine(
                $"            resultPtr.initializeMemory(as: ({returnSwiftType}).self, repeating: _result, count: 1)");
            swiftWriter.WriteLine("            completion(resultPtr, context)");
            swiftWriter.WriteLine("        } catch {");
            swiftWriter.WriteLine(
                "            let errorPtr = Unmanaged.passRetained(error as AnyObject).toOpaque()");
            swiftWriter.WriteLine("            errorCallback(errorPtr, context)");
            swiftWriter.WriteLine("        }");
        }
        else
        {
            swiftWriter.WriteLine(
                $"        let _result = await __self.{methodCallSwift}({callArgsSwift})");
            swiftWriter.WriteLine(
                $"        resultPtr.initializeMemory(as: ({returnSwiftType}).self, repeating: _result, count: 1)");
            swiftWriter.WriteLine("        completion(resultPtr, context)");
        }

        swiftWriter.WriteLine("    }");
        swiftWriter.WriteLine("}");
    }

    // ─── C# extension body + P/Invoke + callbacks ──────────────────────

    /// <summary>
    /// Emits the C# surface for a parent-only async overload, inside the already-open
    /// per-conformer-tuple <c>*CsmExtensions</c> static partial class. The emission
    /// consists of (1) per-overload private static <c>[UnmanagedCallersOnly]</c> success
    /// and (for throws) error callbacks, (2) the matching delegate-function-pointer
    /// fields, (3) a <c>[LibraryImport]</c> P/Invoke declaration, and (4) the public
    /// <c>Task&lt;T&gt;</c> extension method that allocates the result buffer, pins the
    /// <see cref="TaskCompletionSource{TResult}"/> as a GCHandle, invokes the P/Invoke,
    /// and returns the task.
    /// </summary>
    private static void EmitParentOnlyAsyncCSharpExtension(
        CSharpWriter csWriter,
        MethodDecl method,
        string parentCsName,
        string returnCsType,
        string cdeclSymbol,
        string csMethodName,
        string wrapperLibPath,
        bool throws,
        IReadOnlyList<AvailabilityAnnotation>? mergedAvailability,
        TypeDecl parentTypeDecl,
        ITypeDatabase typeDatabase,
        bool returnIsBlittable)
    {
        // Pre-compute the Utf8Slice user-param list. The selective-opt-out gate has
        // already filtered method.CSSignature.Skip(1) down to Utf8Slice only — every
        // entry that survives here projects to a `string` public param + (IntPtr ptr,
        // nint len) pair across the @_cdecl boundary. Pin lifetime wraps ONLY the
        // synchronous P/Invoke (which schedules the Swift Task); the Swift wrapper
        // reconstructs `let _{label}Val = String(...)` BEFORE its `Task {` block so
        // the C# pin does not need to survive the await. Mirrors AMGBE.
        var utf8Args = method.CSSignature.Skip(1)
            .Where(a => !a.SwiftTypeSpec.IsEmptyTuple
                        && MethodClosureBridge.ClassifyParam(a, typeDatabase)
                           == MethodClosureBridge.ParamAbiCategory.Utf8Slice)
            .ToList();
        // Build unique per-overload identifier suffix so multiple parent-only-async
        // overloads in the same extension class never collide on field/method names.
        // The cdecl-symbol hash already participates in `cdeclSymbol`; reusing it
        // ensures the C# identifiers stay deterministic alongside the Swift symbol.
        var hashSuffix = cdeclSymbol.Substring(cdeclSymbol.LastIndexOf('_') + 1);
        // hashSuffix is "async"; use the preceding hash chunk instead.
        var symbolParts = cdeclSymbol.Split('_');
        var hashChunk = symbolParts.Length >= 2 ? symbolParts[symbolParts.Length - 2] : "x";
        var idSuffix = $"{method.Name}_{hashChunk}";

        var successCallbackField = $"_SuccessCb_{idSuffix}";
        var successCallbackMethod = $"OnSuccess_{idSuffix}";
        var errorCallbackField = $"_ErrorCb_{idSuffix}";
        var errorCallbackMethod = $"OnError_{idSuffix}";

        csWriter.WriteLine();

        // ── Success callback ───────────────────────────────────────────
        // [UnmanagedCallersOnly] members live in the (already non-generic) extension
        // class body directly — no CS7042 hoisting needed because the enclosing
        // *CsmExtensions class is itself non-generic.
        //
        // Ownership transfer (depends on returnIsBlittable, decided by IsReturnTypeEmittable):
        //   returnIsBlittable=false (SafeHandle-backed, non-frozen struct):
        //     MarshalFromSwift<T>(resultPtr) for ISwiftObject T calls T.NewFromPayload(resultPtr),
        //     which wraps the SAME pointer in a SwiftSafeHandle<T>. The SafeHandle then owns
        //     the buffer — its ReleaseHandle calls NativeMemory.Free on Dispose. So the
        //     success path MUST NOT free resultPtr here (doing so double-frees and the first
        //     read of the returned object dereferences poisoned memory).
        //   returnIsBlittable=true (numeric primitive, owns-buffer):
        //     MarshalFromSwift<T>(resultPtr) returns a *value copy* and the carrier is NOT
        //     handed to any SafeHandle. The success path OWNS the buffer and MUST
        //     NativeMemory.Free it after reading, or it leaks.
        // Either way the GCHandle is freed in finally; the error path (when present) still
        // frees its uninitialized buffer because Swift never handed it to NewFromPayload.
        //
        // The holder still carries resultPtr so the error callback (throws only) can
        // free its uninitialized buffer — Swift never wrote to it on the error branch
        // and never handed it to NewFromPayload.
        csWriter.WriteLine(
            $"private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> {successCallbackField} = &{successCallbackMethod};");
        csWriter.WriteLine(
            "[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        csWriter.WriteLine(
            $"private static unsafe void {successCallbackMethod}(IntPtr resultPtr, IntPtr context)");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("var handle = global::System.Runtime.InteropServices.GCHandle.FromIntPtr(context);");
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine(
            $"var result = global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{returnCsType}>(resultPtr);");
        csWriter.WriteLine(
            $"if (handle.Target is object[] holder && holder[0] is global::System.Threading.Tasks.TaskCompletionSource<{returnCsType}> tcs)");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("tcs.TrySetResult(result);");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        // Outer catch — [UnmanagedCallersOnly] callbacks MUST NOT let exceptions escape;
        // the runtime fail-fasts the process on an unhandled exception across the
        // managed/native boundary. Route any unexpected throw into the TCS so the
        // awaiting caller sees a faulted task instead of a process crash.
        csWriter.WriteLine("catch (global::System.Exception ex)");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine(
            $"if (handle.IsAllocated && handle.Target is object[] holder2 && holder2[0] is global::System.Threading.Tasks.TaskCompletionSource<{returnCsType}> tcs2)");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("tcs2.TrySetException(ex);");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("catch { /* swallow — cannot escape UnmanagedCallersOnly */ }");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("finally");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        if (returnIsBlittable)
        {
            // Blittable return: MarshalFromSwift<T> returned a value copy; we own the
            // carrier and must free it (or it leaks). Free in finally so we still
            // recover on the catch path above — the carrier was never handed to any
            // SafeHandle, so freeing it is unconditionally safe once MarshalFromSwift
            // has either read it or thrown.
            csWriter.WriteLine("global::System.Runtime.InteropServices.NativeMemory.Free((void*)resultPtr);");
        }
        // Else (SafeHandle-backed return): MUST NOT free resultPtr — the returned
        // SwiftSafeHandle now owns it. Freeing here would double-free and the first
        // read of the returned object would dereference poisoned memory.
        //
        // IsAllocated guard prevents a double-Free crash if a malformed Swift caller
        // were to invoke both success and error callbacks for the same context (Swift's
        // contract is exactly-one-callback, but defense-in-depth costs nothing here).
        csWriter.WriteLine("if (handle.IsAllocated) handle.Free();");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.Indent--;
        csWriter.WriteLine("}");

        // ── Error callback (throws only) ───────────────────────────────
        if (throws)
        {
            // Module name is needed to reference the SBW_GetErrorDescription /
            // SBW_ReleaseError P/Invokes that ErrorDescriptionEmitter emits into the
            // SAME extension class above (their EntryPoint is module-suffixed, but the
            // C# method names are unsuffixed `SBW_GetErrorDescription`/`SBW_ReleaseError`).
            csWriter.WriteLine();
            csWriter.WriteLine(
                $"private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> {errorCallbackField} = &{errorCallbackMethod};");
            csWriter.WriteLine(
                "[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
            csWriter.WriteLine(
                $"private static unsafe void {errorCallbackMethod}(IntPtr errorPtr, IntPtr context)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("var handle = global::System.Runtime.InteropServices.GCHandle.FromIntPtr(context);");
            csWriter.WriteLine("IntPtr resultPtr = IntPtr.Zero;");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine(
                $"if (handle.Target is object[] holder && holder[0] is global::System.Threading.Tasks.TaskCompletionSource<{returnCsType}> tcs)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("resultPtr = (IntPtr)(nint)holder[1]!;");
            // Build a SwiftException via the standard ThrowSwiftError helper. Wrap in
            // try/catch so we capture the constructed exception without unwinding past
            // the UnmanagedCallersOnly boundary (which would crash the process).
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine(
                "global::Swift.Runtime.InteropServices.SwiftMarshal.ThrowSwiftError(errorPtr, SBW_GetErrorDescription(errorPtr), SBW_ReleaseError);");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("catch (global::System.Exception ex)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("tcs.TrySetException(ex);");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("if (resultPtr != IntPtr.Zero) global::System.Runtime.InteropServices.NativeMemory.Free((void*)resultPtr);");
            // Defense-in-depth: IsAllocated guard prevents process-crash if both
            // callbacks fire for the same context (see success callback rationale).
            csWriter.WriteLine("if (handle.IsAllocated) handle.Free();");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        // ── P/Invoke ───────────────────────────────────────────────────
        // Param order must match the Swift @_cdecl wrapper exactly:
        //   resultPtr → <Utf8Slice user params: ptr+len pairs> → self_ → completion
        //     → errorCallback (if throws) → context
        csWriter.WriteLine();
        var pinvokeParams = new List<string>
        {
            "IntPtr resultPtr",
        };
        foreach (var utf8Arg in utf8Args)
        {
            var csName = NameProvider.GetCSharpParameterName(utf8Arg);
            pinvokeParams.Add($"IntPtr {csName}Utf8Ptr");
            pinvokeParams.Add($"nint {csName}Utf8Len");
        }
        pinvokeParams.Add("IntPtr self_");
        pinvokeParams.Add("delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> completion");
        if (throws)
            pinvokeParams.Add("delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> errorCallback");
        pinvokeParams.Add("IntPtr context");

        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
            csWriter, mergedAvailability, parentTypeDecl.AvailabilityAnnotations);
        csWriter.WriteLine(
            "[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        csWriter.WriteLine(
            $"[global::System.Runtime.InteropServices.LibraryImport(\"{wrapperLibPath}\", EntryPoint = \"{cdeclSymbol}\")]");
        csWriter.WriteLine(
            $"internal static unsafe partial void {cdeclSymbol}({string.Join(", ", pinvokeParams)});");

        // ── Public extension method ────────────────────────────────────
        // Build public-method param list. Receiver first (`this Parent self`), then one
        // `string {csName}` per Utf8Slice arg in declaration order. The signature stays
        // ergonomic — callers see plain `string` params; the (ptr, len) pair is an
        // internal P/Invoke detail.
        var publicParams = new List<string> { $"this {parentCsName} self" };
        foreach (var utf8Arg in utf8Args)
        {
            var csName = NameProvider.GetCSharpParameterName(utf8Arg);
            publicParams.Add($"string {csName}");
        }

        // The public method body hardcodes synthetic locals (tcs, resultPtr, holder,
        // handle). A user parameter spelling any of them would shadow the synthetic (CS0136)
        // and the generator would emit uncompilable C# at exit 0. Reserve each against the
        // in-scope user identifiers (the `self` receiver + every Utf8Slice param); with no
        // collision the original name is returned verbatim, so output is unchanged. The
        // success/error callback methods take fixed params (no user names) and stay literal.
        var asyncScope = new SyntheticNameScope(
            new[] { "self" }.Concat(utf8Args.Select(NameProvider.GetCSharpParameterName)));
        string tcsName = asyncScope.Reserve("tcs");
        string asyncResultPtrName = asyncScope.Reserve("resultPtr");
        string holderName = asyncScope.Reserve("holder");
        string handleName = asyncScope.Reserve("handle");

        csWriter.WriteLine();
        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
            csWriter, mergedAvailability, parentTypeDecl.AvailabilityAnnotations);
        csWriter.WriteLine(
            $"/// <summary>Parent-only async specialization: {parentCsName}.{method.Name}.</summary>");
        csWriter.WriteLine(
            $"public static unsafe global::System.Threading.Tasks.Task<{returnCsType}> {csMethodName}({string.Join(", ", publicParams)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine(
            $"var {tcsName} = new global::System.Threading.Tasks.TaskCompletionSource<{returnCsType}>();");
        // Size source diverges by return shape:
        //   SafeHandle-backed (non-frozen struct):
        //     `SwiftMarshal.GetSwiftTypeSize<T>()` queries Swift TypeMetadata.Size — required
        //     because the Swift size can differ from the C# `sizeof` of the wrapper (the
        //     wrapper holds a SafeHandle, not the value layout). The generic is constrained
        //     to ISwiftObject, which the SafeHandle wrapper class satisfies.
        //   Blittable primitive:
        //     The C# projection IS the value layout (`int` for `Int32`, `double` for `Double`,
        //     etc.), so `sizeof({returnCsType})` is the exact right size. Cannot use
        //     `GetSwiftTypeSize<T>` here — its `where T : ISwiftObject` constraint excludes
        //     `int`/`double`/`float`/etc., which is the CS0315 the predicate widen tripped on.
        if (returnIsBlittable)
        {
            csWriter.WriteLine(
                $"var {asyncResultPtrName} = (IntPtr)global::System.Runtime.InteropServices.NativeMemory.Alloc((nuint)sizeof({returnCsType}));");
        }
        else
        {
            csWriter.WriteLine(
                $"var {asyncResultPtrName} = (IntPtr)global::System.Runtime.InteropServices.NativeMemory.Alloc((nuint)global::Swift.Runtime.InteropServices.SwiftMarshal.GetSwiftTypeSize<{returnCsType}>());");
        }
        // Holder carries both the TCS and the resultPtr to whichever callback fires so
        // C# can free the buffer in both paths. Boxing IntPtr (nint) into object[] is
        // safe because the runtime preserves the pointer value verbatim.
        csWriter.WriteLine($"var {holderName} = new object[] {{ {tcsName}, (object)(nint){asyncResultPtrName} }};");
        csWriter.WriteLine(
            $"var {handleName} = global::System.Runtime.InteropServices.GCHandle.Alloc({holderName});");
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Build P/Invoke call argument list in the SAME order as pinvokeParams above:
        //   resultPtr → <Utf8Slice ptr+len pairs> → self_ → completion → errorCallback
        //   (throws) → context. Each Utf8Slice arg also contributes a (csName, bareName)
        //   entry to utf8SliceLocals which drives the byte[] prelude + nested `fixed`
        //   block stack.
        var callArgs = new List<string> { asyncResultPtrName };
        var utf8SliceLocals = new List<(string csName, string bareName)>();
        foreach (var utf8Arg in utf8Args)
        {
            var csName = NameProvider.GetCSharpParameterName(utf8Arg);
            var bareName = NameProvider.StripVerbatimPrefix(csName);
            utf8SliceLocals.Add((csName, bareName));
            callArgs.Add($"(IntPtr)__{bareName}Ptr");
            callArgs.Add($"(nint)__{bareName}Utf8.Length");
        }
        callArgs.Add("((global::Swift.Runtime.ISwiftObject)self).SwiftHandle");
        callArgs.Add(successCallbackField);
        if (throws) callArgs.Add(errorCallbackField);
        callArgs.Add($"global::System.Runtime.InteropServices.GCHandle.ToIntPtr({handleName})");

        // byte[] prelude (inside the try-block — UTF8.GetBytes can throw on null input,
        // and the catch below correctly cleans up handle + resultPtr in that case).
        foreach (var (csName, bareName) in utf8SliceLocals)
            csWriter.WriteLine($"var __{bareName}Utf8 = System.Text.Encoding.UTF8.GetBytes({csName});");

        // Open nested `fixed` blocks for each Utf8Slice byte[]. The pin only needs to
        // cover the synchronous P/Invoke (which schedules the Swift Task); the Swift
        // wrapper reconstructs `let _{label}Val = String(...)` BEFORE its `Task {`
        // block so the pin doesn't survive the await.
        foreach (var (_, bareName) in utf8SliceLocals)
        {
            csWriter.WriteLine($"fixed (byte* __{bareName}Ptr = __{bareName}Utf8)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
        }

        csWriter.WriteLine($"{cdeclSymbol}({string.Join(", ", callArgs)});");

        // Close `fixed` blocks in reverse order.
        for (int i = 0; i < utf8SliceLocals.Count; i++)
        {
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("catch");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        // Synchronous-path safety net: if the P/Invoke itself throws before Swift could
        // schedule the Task (e.g. DllNotFoundException), neither callback will fire so
        // we free the buffer + handle here and let the caller see the original failure.
        csWriter.WriteLine($"if ({handleName}.IsAllocated) {handleName}.Free();");
        csWriter.WriteLine($"global::System.Runtime.InteropServices.NativeMemory.Free((void*){asyncResultPtrName});");
        csWriter.WriteLine("throw;");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine($"return {tcsName}.Task;");
        csWriter.Indent--;
        csWriter.WriteLine("}");
    }
}
