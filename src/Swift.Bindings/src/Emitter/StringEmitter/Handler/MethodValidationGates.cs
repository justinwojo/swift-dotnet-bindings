// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared validation checks used by MethodHandler, ConstructorHandler, and PropertyHandler
/// to determine whether a method/accessor can be emitted.
/// </summary>
internal static class MethodValidationGates
{
    /// <summary>
    /// Checks if the method has constraints on protocols with associated types.
    /// Such protocols generate generic C# interfaces which can't be used as constraints without type arguments.
    /// Used by MethodHandler.Emit, ConstructorHandler.Emit, and PropertyHandler preflight.
    ///
    /// For conditional extension methods, the method's genericSig includes ALL constraints
    /// (parent type + extension). Parent-baseline constraints on SUPPORTED protocols (no
    /// associated types, no Self) are skipped here because they're already handled by the
    /// type-level where clause. Parent-baseline constraints on UNSUPPORTED protocols (PAT or
    /// Self) still block the method — the type-level where clause also skips them, so nobody
    /// else enforces the constraint, and the P/Invoke would be missing witness table parameters.
    ///
    /// Extra constraints (conditional extension) on supported protocols are allowed through —
    /// the P/Invoke and witness table infrastructure already handles them.
    /// </summary>
    public static bool HasUnsupportedProtocolConstraints(MethodEnvironment methodEnv)
        => HasUnsupportedProtocolConstraints(methodEnv.MethodDecl, methodEnv.TypeDatabase);

    /// <summary>
    /// Overload for pipeline use — takes MethodDecl + ITypeDatabase directly,
    /// avoiding the need to construct a MethodEnvironment.
    /// </summary>
    public static bool HasUnsupportedProtocolConstraints(MethodDecl methodDecl, ITypeDatabase typeDatabase)
    {
        if (!methodDecl.IsGeneric)
            return false;

        var parentTypeGenericParams = methodDecl.ParentDecl is TypeDecl parentType
            ? parentType.GenericParameters
            : null;

        foreach (var param in methodDecl.GenericParameters)
        {
            foreach (var conformance in param.GenericConformances)
            {
                if (conformance.Kind != ConformanceKind.Protocol)
                    continue;

                // For parent-baseline constraints on SUPPORTED protocols (no PAT, no Self),
                // skip — the type-level where clause already handles them.
                // For UNSUPPORTED protocols, we must still block even if parent-declared,
                // because the type-level where clause also skips them (GenericTypeEmitter
                // line 85), so the constraint is never enforced and P/Invoke would lack
                // the required witness table parameter.
                if (IsParentBaselineConstraint(param, conformance, parentTypeGenericParams) &&
                    !IsUnsupportedProtocolConstraint(conformance.ConformanceTarget, typeDatabase))
                    continue;

                // Block if the protocol has associated types or self requirements.
                if (IsUnsupportedProtocolConstraint(conformance.ConformanceTarget, typeDatabase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether a conformance is a "conditional extension constraint" —
    /// i.e., it appears on the method's generic parameters but NOT on the parent type's
    /// generic parameters for the same type parameter. These are constraints added by a
    /// Swift conditional extension (e.g., <c>extension Table&lt;T&gt; where T: FetchableRecord</c>).
    /// </summary>
    /// <returns><c>true</c> if the conformance is NOT in the parent type's baseline (i.e., is extra).</returns>
    internal static bool IsConditionalExtensionConstraint(
        GenericArgumentDecl methodParam,
        GenericParameterConformance conformance,
        IReadOnlyList<GenericArgumentDecl>? parentTypeGenericParams)
    {
        return !IsParentBaselineConstraint(methodParam, conformance, parentTypeGenericParams);
    }

    /// <summary>
    /// Checks whether a protocol makes the entire method unsupported (i.e., the method must
    /// be skipped because the constraint can't be satisfied in C#). Returns <c>true</c> only
    /// for protocols with associated types or <c>Self</c> requirements, which generate
    /// generic interfaces that can't be used as non-generic constraints.
    ///
    /// Well-known runtime-only marker protocols (<c>Sendable</c>, <c>Copyable</c>, etc.) are
    /// NOT unsupported here — they're <em>ignorable</em>: the method still emits, but the
    /// constraint is silently dropped at where-clause and PWT-extraction time. That filtering
    /// happens in <see cref="IsProtocolAvailableForConstraint(SwiftTypeName, ITypeDatabase)"/>.
    /// </summary>
    internal static bool IsUnsupportedProtocolConstraint(SwiftTypeName protocolTypeName, ITypeDatabase typeDatabase)
    {
        if (typeDatabase.TryGetTypeRecord(protocolTypeName, out var record) &&
            record.Kind == TypeRecordKind.Protocol)
        {
            return record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) ||
                   record.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement);
        }

        return false;
    }

    /// <summary>
    /// Decides whether a protocol can be emitted as a generic constraint in C# (i.e., as
    /// <c>I{Protocol}</c> in a <c>where T : ISwiftObject, I{Protocol}</c> clause and as a
    /// matching <c>ProtocolWitnessTable.GetOrThrow&lt;T, I{Protocol}&gt;()</c> extraction).
    ///
    /// Returns <c>false</c> when:
    /// <list type="bullet">
    ///   <item><description>the protocol is not in the TypeDatabase (unknown — fail closed),</description></item>
    ///   <item><description>the record isn't a protocol kind,</description></item>
    ///   <item><description>the protocol has associated types or a <c>Self</c> requirement
    ///   (<see cref="IsUnsupportedProtocolConstraint"/> would already have blocked the method),</description></item>
    ///   <item><description>or the protocol is a well-known runtime-only marker
    ///   (<c>Sendable</c> / <c>Copyable</c> / <c>Escapable</c> / <c>SendableMetatype</c> /
    ///   <c>_Concurrency.Actor</c>) — these have TypeRecords purely so actor / Sendable-conforming
    ///   types can resolve their conformance arrays, but they have no projected C# interface.
    ///   Emitting <c>ISendableMetatype</c> as a constraint would produce CS0246, and emitting a
    ///   PWT extraction for them would produce a missing P/Invoke parameter.</description></item>
    /// </list>
    ///
    /// This is the single source of truth for the "drop constraint, keep method" half of the
    /// gate split. The matching "skip method entirely" half lives in
    /// <see cref="IsUnsupportedProtocolConstraint"/>.
    /// </summary>
    internal static bool IsProtocolAvailableForConstraint(SwiftTypeName protocolTypeName, ITypeDatabase typeDatabase)
    {
        if (typeDatabase.TryGetTypeRecord(protocolTypeName, out var record))
        {
            return record.Kind == TypeRecordKind.Protocol &&
                   !record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) &&
                   !record.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement) &&
                   !TypeDatabaseExtensions.IsWellKnownRuntimeProtocol(record);
        }

        return false;
    }

    /// <summary>
    /// Returns true if the conformance is part of the parent type's baseline constraints
    /// for the matching generic parameter. Parent-baseline constraints are already handled
    /// by the type-level where clause and should not be re-checked at the method level.
    /// </summary>
    private static bool IsParentBaselineConstraint(
        GenericArgumentDecl methodParam,
        GenericParameterConformance conformance,
        IReadOnlyList<GenericArgumentDecl>? parentTypeGenericParams)
    {
        if (parentTypeGenericParams == null || parentTypeGenericParams.Count == 0)
            return false;

        // Find matching parent param by TypeName (e.g., "τ_0_0")
        var parentParam = parentTypeGenericParams.FirstOrDefault(p => p.TypeName == methodParam.TypeName);
        if (parentParam == null)
            return false;

        // Check if the parent param declares this same conformance
        return parentParam.GenericConformances.Any(pc =>
            pc.Kind == ConformanceKind.Protocol &&
            pc.ConformanceTarget == conformance.ConformanceTarget);
    }
}
