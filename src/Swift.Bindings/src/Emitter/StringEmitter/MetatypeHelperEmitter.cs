// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared utility for emitting metadata accessor helper functions in Swift @_cdecl wrappers.
/// These helpers call the generic parent type's metadata accessor via dlsym to convert
/// T.self metadata into GenericType&lt;T&gt;.self metadata for protocol metatype dispatch.
///
/// Previously lived in ConstructorWrapperEmitter but was called from Method, Property,
/// and Constructor emitters. Extracted here to remove the implicit dependency.
///
/// Deduplication is tracked by <see cref="ModuleEmissionContext.TryAddMetadataAccessorHelper"/>.
///
/// <para>
/// **Fail-closed contract**: Callers MUST gate on
/// <see cref="HasUnresolvableTypeConformances"/> before invoking
/// <see cref="EmitMetadataAccessorHelperIfNeeded"/>. The helper renders the dlsym call
/// site with <see cref="GetResolvablePwtParameterCount"/> PWT slots, which silently
/// undercounts when the parent type has Self-requirement or associated-type
/// constraints. The dlsym'd <c>...Ma</c> symbol's actual signature includes ALL PWTs,
/// so a mismatch corrupts caller-saved registers and PAC-traps on arm64e (NativeAOT).
/// The central gate lives in <see cref="GenericDispatchEmitter.CanEmitGenericDispatch"/>.
/// Dynamic PWT resolution for the Swift wrapper path is not yet implemented.
/// </para>
/// </summary>
public static class MetatypeHelperEmitter
{
    /// <summary>
    /// Emits a private Swift helper function that calls the metadata accessor for a generic parent
    /// type via dlsym. This converts T.self metadata into GenericType&lt;T&gt;.self metadata, which is
    /// needed for protocol metatype dispatch. Deduplicates by type mangled name + PWT count.
    /// Returns the helper function name (e.g., "_sbw_meta_ABCD1234").
    ///
    /// For constrained generic types (e.g., ConstrainedBox&lt;T: Describable&gt;), the metadata accessor
    /// also requires protocol witness table (PWT) pointers for each conformance. The helper accepts
    /// these as additional UnsafeRawPointer parameters after the type metadata parameters.
    ///
    /// The <paramref name="pwtCount"/> parameter controls how many PWT parameters are included.
    /// Callers must match the PWT count to what the C# P/Invoke side passes:
    /// - Constructor wrappers: include PWT for all resolvable conformances
    /// - Property/method wrappers: include PWT for all resolvable conformances
    /// - Unresolvable conformances (protocols with associated types/Self requirements): excluded
    /// </summary>
    /// <param name="swiftWriter">The Swift writer to emit to.</param>
    /// <param name="parentTypeDecl">The generic parent type declaration.</param>
    /// <param name="ctx">The per-module emission context for deduplication.</param>
    /// <param name="pwtCount">Number of PWT parameters to include (0 = no PWT).</param>
    /// <returns>The helper function name to use in subsequent call sites.</returns>
    public static string EmitMetadataAccessorHelperIfNeeded(
        SwiftWriter swiftWriter,
        TypeDecl parentTypeDecl,
        ModuleEmissionContext ctx,
        int pwtCount = -1)
    {
        // Default: compute PWT count from all conformances (backward compat for constructors)
        if (pwtCount < 0)
            pwtCount = GetPwtParameterCount(parentTypeDecl);

        var mangledName = parentTypeDecl.MangledName;
        // Include PWT count in the dedup key so callers with different PWT needs get separate helpers.
        var dedupKey = pwtCount > 0 ? $"{mangledName}:pwt{pwtCount}" : mangledName;

        // Use mangled name hash for uniqueness — two types with the same short name
        // (e.g., DiskStorage.Backend<T> and MemoryStorage.Backend<T>) need distinct helpers.
        // Include PWT count in the hash to differentiate PWT vs non-PWT variants.
        var hashInput = pwtCount > 0 ? $"{mangledName}:pwt{pwtCount}" : mangledName;
        var helperName = $"_sbw_meta_{EmitterUtility.DeterministicHash8(hashInput)}";

        // metadata-accessor helpers live in a dedicated `_metadata_accessor` bucket — no other emitter writes to it. dedupKey includes mangledName + PWT count, helper name is hashed for cross-type collision safety.
        if (!ctx.TryAddMetadataAccessorHelper(dedupKey))
            return helperName; // Already emitted, just return the name

        var metaSymbol = $"{mangledName}Ma";
        var genericCount = parentTypeDecl.GenericParameters.Count;

        // Build PWT parameter lists based on the explicit count
        var pwtParams = new List<string>();
        var pwtFnTypes = new List<string>();
        var pwtCallArgs = new List<string>();
        for (int i = 0; i < pwtCount; i++)
        {
            pwtParams.Add($"_ pwt{i}: UnsafeRawPointer");
            pwtFnTypes.Add("UnsafeRawPointer");
            pwtCallArgs.Add($"pwt{i}");
        }

        // Build parameter list: type metadata + PWT
        var allParams = Enumerable.Range(0, genericCount).Select(i => $"_ t{i}: UnsafeRawPointer")
            .Concat(pwtParams);
        var paramList = string.Join(", ", allParams);

        // Build function type: (Int, T_metadata..., PWT...) -> (UnsafeRawPointer, Int)
        var allFnTypes = Enumerable.Range(0, genericCount).Select(_ => "UnsafeRawPointer")
            .Concat(pwtFnTypes);
        var fnParamTypes = string.Join(", ",
            new[] { "Int" }.Concat(allFnTypes));

        // Build call arguments: (0, t0, t1, ..., pwt0, pwt1, ...)
        var allCallArgs = Enumerable.Range(0, genericCount).Select(i => $"t{i}")
            .Concat(pwtCallArgs);
        var callArgs = string.Join(", ",
            new[] { "0" }.Concat(allCallArgs));

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            private func {{helperName}}({{paramList}}) -> UnsafeRawPointer {
                typealias _Fn = @convention(thin) ({{fnParamTypes}}) -> (UnsafeRawPointer, Int)
                let fn = unsafeBitCast(dlsym(dlopen(nil, RTLD_LAZY), "{{metaSymbol}}")!, to: _Fn.self)
                return fn({{callArgs}}).0
            }
            """);

        return helperName;
    }

    /// <summary>
    /// Returns the total number of PWT parameters for a generic type's metadata accessor.
    /// This is the sum of all protocol conformances across all generic parameters.
    /// Note: This counts ALL conformances. For conformances filtered by type database
    /// availability, use <see cref="GetResolvablePwtParameterCount"/>.
    /// </summary>
    public static int GetPwtParameterCount(TypeDecl parentTypeDecl)
    {
        return parentTypeDecl.GenericParameters
            .Sum(gp => gp.GenericConformances.Count);
    }

    /// <summary>
    /// Returns the number of PWT parameters that the GSF cdecl-constructor path threads —
    /// resolvable conformances (no associated types, no Self requirements, projectable as
    /// a C# interface) PLUS PAT/Self-requirement conformances whose protocol descriptor
    /// symbol the parser captured (the dynamic-PWT path, resolved at runtime via
    /// <c>SwiftConformance.GetWitnessTableOrThrow</c>).
    ///
    /// Mirrors the conformance-counting rules in
    /// <see cref="PInvokeHelperContext.CreateIfGeneric(TypeDecl, ITypeDatabase)"/> so the
    /// Swift @_cdecl wrapper signature, the <c>_sbw_meta_X</c> helper signature, and the
    /// C# call site all agree on the slot count. Without that agreement, the dlsym'd
    /// <c>...Ma</c> symbol's caller-saved registers shift and PAC-trap on arm64e.
    /// Class-bound generic constraints contribute no PWT slot.
    /// </summary>
    public static int GetTotalPwtParameterCount(TypeDecl parentTypeDecl, ITypeDatabase typeDatabase)
    {
        int count = 0;
        foreach (var gp in parentTypeDecl.GenericParameters)
        {
            foreach (var conformance in gp.GenericConformances)
            {
                if (conformance.Kind != ConformanceKind.Protocol)
                    continue;
                if (!typeDatabase.TryGetTypeRecord(conformance.ConformanceTarget, out var record))
                    continue;
                if (record.Kind == TypeRecordKind.Class)
                    continue;
                if (record.Kind != TypeRecordKind.Protocol)
                    continue;

                bool isResolvable =
                    !record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) &&
                    !record.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement);

                if (isResolvable)
                {
                    // Mirror PInvokeEmitter.HandleProtocolConformance: every well-known
                    // runtime protocol (Sendable / Copyable / Escapable / SendableMetatype /
                    // _Concurrency.Actor / Swift.Error) is rejected by
                    // IsProtocolAvailableForConstraint, so the C# @_cdecl P/Invoke
                    // declaration does NOT add a slot for it. Counting them on the Swift
                    // side would over-declare _pwtN and shift the caller-saved registers
                    // on arm64e — PAC-trap on the first dlsym'd Ma call. Parents that
                    // actually carry a Swift.Error constraint are gate-blocked upstream
                    // via HasWrapperHelperGateBlocker so they never reach this counter
                    // with a constraint the C# call site can't satisfy.
                    //
                    // The marker check also catches Swift.BitwiseCopyable, which is a
                    // stdlib marker (no witness table, no descriptor) but is NOT in
                    // IsWellKnownRuntimeProtocol's set. Without this extra skip, a
                    // T: BitwiseCopyable constraint would increment _pwtN with no
                    // matching Ma slot.
                    if (TypeDatabaseExtensions.IsWellKnownRuntimeProtocol(record))
                        continue;
                    if (TypeDatabaseExtensions.IsStdlibMarkerProtocol(record))
                        continue;
                    count++;
                }
                else if (!string.IsNullOrEmpty(record.ProtocolDescriptorSymbol))
                {
                    // PAT / Self-requirement with descriptor → dynamic-PWT slot threaded
                    // through {HelperClass}.Get{Protocol}PWT(metadata).Handle.
                    count++;
                }
            }
        }
        return count;
    }

    /// <summary>
    /// Returns the number of PWT parameters that the wrapper-side metadata-accessor
    /// helper currently passes — restricted to conformances on protocols that can be
    /// projected as a static C# interface (no associated types, no Self requirements).
    ///
    /// This intentionally undercounts versus the actual <c>...Ma</c> symbol's ABI when
    /// any of the parent type's constraints are unresolvable. Callers MUST gate on
    /// <see cref="HasUnresolvableTypeConformances"/> upstream so they never reach this
    /// helper for a type whose metadata accessor expects more PWTs than this returns —
    /// that mismatch is a guaranteed PAC trap / SIGSEGV at runtime, not a recoverable
    /// condition. The fail-closed gate lives in
    /// <see cref="GenericDispatchEmitter.CanEmitGenericDispatch"/>. Dynamic PWT
    /// resolution from the Swift wrapper path is not yet implemented.
    /// </summary>
    public static int GetResolvablePwtParameterCount(TypeDecl parentTypeDecl, ITypeDatabase typeDatabase)
    {
        int count = 0;
        foreach (var gp in parentTypeDecl.GenericParameters)
        {
            foreach (var conformance in gp.GenericConformances)
            {
                if (typeDatabase.TryGetTypeRecord(conformance.ConformanceTarget, out var record))
                {
                    if (record.Kind == TypeRecordKind.Protocol &&
                        !record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) &&
                        !record.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement))
                    {
                        // Same lockstep rule as GetTotalPwtParameterCount: every well-known
                        // runtime protocol (Sendable / Copyable / Escapable / SendableMetatype
                        // / _Concurrency.Actor / Swift.Error) is rejected by the C# side
                        // (IsProtocolAvailableForConstraint), so counting them here would
                        // over-declare _pwtN on the Swift wrapper and shift caller-saved
                        // registers on arm64e — PAC-trap on the first dlsym'd Ma call.
                        // Property and Subscript wrappers use this counter directly without
                        // going through HasWrapperHelperGateBlocker; the skip is what keeps
                        // their Swift _pwtN signature consistent with the C# P/Invoke decl.
                        // The marker check also catches BitwiseCopyable (in
                        // IsStdlibMarkerProtocol but not in IsWellKnownRuntimeProtocol).
                        if (TypeDatabaseExtensions.IsWellKnownRuntimeProtocol(record))
                            continue;
                        if (TypeDatabaseExtensions.IsStdlibMarkerProtocol(record))
                            continue;
                        count++;
                    }
                }
            }
        }
        return count;
    }

    /// <summary>
    /// Returns <c>true</c> when the generic parent type has at least one protocol-conformance
    /// constraint whose runtime witness table the wrapper-helper path cannot supply — i.e. a
    /// constraint that <see cref="GetResolvablePwtParameterCount"/> drops because the protocol
    /// has associated types or a Self requirement. Counts ONLY conformances on protocols that
    /// the type database knows about so the gate ignores unknown stdlib protocols (Hashable,
    /// Collection, ...) the same way the existing legacy filter does — failing on unknown
    /// would regress every Alamofire/GRDB/RxSwift/DifferenceKit constrained generic.
    /// </summary>
    /// <remarks>
    /// This is the fail-closed predicate used by
    /// <see cref="GenericDispatchEmitter.CanEmitGenericDispatch"/> to refuse member emission
    /// for any generic type whose metadata accessor would be called with the wrong PWT count.
    /// No current library triggers this gate (those types either have no emittable
    /// members today, or their constraint protocols are not yet in the type database).
    /// Adding a new library that DOES trigger it should be loud, not silent — hence the
    /// gate.
    /// </remarks>
    public static bool HasUnresolvableTypeConformances(TypeDecl parentTypeDecl, ITypeDatabase typeDatabase)
    {
        if (!parentTypeDecl.IsGeneric)
            return false;

        foreach (var gp in parentTypeDecl.GenericParameters)
        {
            foreach (var conformance in gp.GenericConformances)
            {
                if (conformance.Kind != ConformanceKind.Protocol)
                    continue;

                if (!typeDatabase.TryGetTypeRecord(conformance.ConformanceTarget, out var record))
                    continue; // unknown protocol — already silently dropped, mirrors legacy filter
                if (record.Kind != TypeRecordKind.Protocol)
                    continue;

                if (record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) ||
                    record.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <c>true</c> when the parent type has any well-known runtime protocol
    /// conformance whose witness table the wrapper-helper path cannot materialize —
    /// i.e. <c>_Concurrency.Actor</c> or <c>Swift.Error</c>. Both are rejected on the
    /// C# side by <see cref="Handler.MethodValidationGates.IsProtocolAvailableForConstraint"/>
    /// (so the P/Invoke declaration omits the slot), but the dlsym'd <c>...Ma</c>
    /// symbol DOES expect a PWT for them in its ABI signature — there is no way to
    /// call it correctly without one. Gating these types out at the wrapper-helper
    /// boundary keeps the Swift <c>_pwtN</c> signature, the C# P/Invoke decl, and
    /// the helper's <c>...Ma</c> invocation all in lockstep.
    ///
    /// Pure marker protocols (<c>Swift.Sendable</c> / <c>Swift.Copyable</c> /
    /// <c>Swift.Escapable</c> / <c>Swift.SendableMetatype</c> / <c>Swift.BitwiseCopyable</c>)
    /// are intentionally NOT flagged here: they carry no witness table, no protocol
    /// descriptor, and never appear in <c>...Ma</c> signatures. Both
    /// <see cref="GetTotalPwtParameterCount"/> and <see cref="GetResolvablePwtParameterCount"/>
    /// already skip them, so a parent constrained only by markers can route through
    /// the GSF / static-dispatch path with all three signatures naturally matching at
    /// zero slots.
    /// </summary>
    public static bool HasWellKnownRuntimeProtocolConformance(TypeDecl parentTypeDecl, ITypeDatabase typeDatabase)
    {
        if (!parentTypeDecl.IsGeneric)
            return false;

        foreach (var gp in parentTypeDecl.GenericParameters)
        {
            foreach (var conformance in gp.GenericConformances)
            {
                if (conformance.Kind != ConformanceKind.Protocol)
                    continue;
                if (!typeDatabase.TryGetTypeRecord(conformance.ConformanceTarget, out var record))
                    continue;
                if (record.Kind != TypeRecordKind.Protocol)
                    continue;
                if (!TypeDatabaseExtensions.IsWellKnownRuntimeProtocol(record))
                    continue;
                if (TypeDatabaseExtensions.IsStdlibMarkerProtocol(record))
                    continue;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Stricter variant of <see cref="HasUnresolvableTypeConformances"/> used by the GSF
    /// cdecl-constructor path: returns <c>true</c> only when the parent has a PAT /
    /// Self-requirement conformance that ALSO lacks a captured protocol-descriptor symbol.
    /// Conformances whose descriptor symbol IS known are handed off to the dynamic-PWT
    /// runtime path (<c>{HelperClass}.Get{Proto}PWT(metadata)</c> →
    /// <c>SwiftConformance.GetWitnessTableOrThrow</c>) and contribute a real slot to both
    /// the @_cdecl signature and the <c>_sbw_meta_X</c> helper — so they do NOT undercount
    /// against the Ma symbol. Only constructors are admitted to the dynamic-PWT path today;
    /// the property, method, and subscript paths still gate on the strict predicate above
    /// because their C# side does not yet thread dynamic PWTs to the @_cdecl wrapper.
    /// </summary>
    public static bool HasUnresolvableTypeConformancesWithoutDescriptor(TypeDecl parentTypeDecl, ITypeDatabase typeDatabase)
    {
        if (!parentTypeDecl.IsGeneric)
            return false;

        foreach (var gp in parentTypeDecl.GenericParameters)
        {
            foreach (var conformance in gp.GenericConformances)
            {
                if (conformance.Kind != ConformanceKind.Protocol)
                    continue;

                if (!typeDatabase.TryGetTypeRecord(conformance.ConformanceTarget, out var record))
                    continue;
                if (record.Kind != TypeRecordKind.Protocol)
                    continue;

                bool unresolvable =
                    record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) ||
                    record.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement);

                if (!unresolvable)
                    continue;

                if (string.IsNullOrEmpty(record.ProtocolDescriptorSymbol))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <c>true</c> when the wrapper-helper's dlsym'd <c>...Ma</c> call would cross the
    /// (num_metadata + num_pwts) &gt; 3 register threshold and thus require Swift's indirect-buffer
    /// metadata-accessor ABI. <see cref="EmitMetadataAccessorHelperIfNeeded"/> always declares the
    /// accessor as a thin function with explicit <c>(request, metadata..., pwt...)</c> args; calling
    /// the buffer-mode ABI through that signature shifts caller-saved registers and PAC-traps on
    /// arm64e. Callers MUST gate on this predicate to refuse emission for over-threshold types.
    /// Buffer-mode emission is not yet implemented.
    /// </summary>
    /// <remarks>
    /// Counts the same conformances the wrapper helper itself passes — i.e.
    /// <see cref="GetResolvablePwtParameterCount"/>. With
    /// <see cref="HasUnresolvableTypeConformances"/> already gating, the resolvable count equals
    /// the total count Swift's <c>Ma</c> symbol uses (marker protocols are filtered at parse time
    /// and unknown stdlib protocols are silently dropped on both sides — see the legacy filter
    /// notes on <see cref="GetResolvablePwtParameterCount"/>). The fail-closed gate lives in
    /// <see cref="GenericDispatchEmitter.CanEmitGenericDispatch"/>.
    /// </remarks>
    public static bool WouldExceedRegisterArgumentThreshold(TypeDecl parentTypeDecl, ITypeDatabase typeDatabase)
    {
        if (!parentTypeDecl.IsGeneric)
            return false;

        int totalArgs = parentTypeDecl.GenericParameters.Count
            + GetResolvablePwtParameterCount(parentTypeDecl, typeDatabase);
        return totalArgs > 3;
    }

    /// <summary>
    /// Variant of <see cref="WouldExceedRegisterArgumentThreshold"/> that counts PAT/
    /// Self-requirement conformances threaded through the dynamic-PWT path. Used by the
    /// GSF cdecl-constructor path where the @_cdecl signature now carries an
    /// <c>UnsafeRawPointer</c> slot for each such conformance.
    /// </summary>
    public static bool WouldExceedRegisterArgumentThresholdTotal(TypeDecl parentTypeDecl, ITypeDatabase typeDatabase)
    {
        if (!parentTypeDecl.IsGeneric)
            return false;

        int totalArgs = parentTypeDecl.GenericParameters.Count
            + GetTotalPwtParameterCount(parentTypeDecl, typeDatabase);
        return totalArgs > 3;
    }
}
