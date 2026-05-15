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
/// Dynamic PWT resolution for the Swift wrapper path is tracked in
/// <c>src/docs/roadmap.md</c> ("Wrapper-helper path dynamic PWT resolution").
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

        // S5 audited (Tier C): metadata-accessor helpers live in a dedicated `_metadata_accessor` bucket — no other emitter writes to it. dedupKey includes mangledName + PWT count, helper name is hashed for cross-type collision safety.
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
    /// resolution from the Swift wrapper path is tracked in <c>src/docs/roadmap.md</c>
    /// ("Wrapper-helper path dynamic PWT resolution").
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
    /// See src/docs/constrained-generic-metadata-witness-tables.md "MetatypeHelperEmitter
    /// Swift wrapper path" for the full design rationale. Audited across the validation
    /// matrix; no current library triggers this gate (those types either have no emittable
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
    /// Returns <c>true</c> when the wrapper-helper's dlsym'd <c>...Ma</c> call would cross the
    /// (num_metadata + num_pwts) &gt; 3 register threshold and thus require Swift's indirect-buffer
    /// metadata-accessor ABI. <see cref="EmitMetadataAccessorHelperIfNeeded"/> always declares the
    /// accessor as a thin function with explicit <c>(request, metadata..., pwt...)</c> args; calling
    /// the buffer-mode ABI through that signature shifts caller-saved registers and PAC-traps on
    /// arm64e. Callers MUST gate on this predicate to refuse emission for over-threshold types.
    /// Buffer-mode emission is tracked in <c>src/docs/roadmap.md</c>.
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
}
