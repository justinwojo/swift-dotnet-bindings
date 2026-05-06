// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Helper class for emitting generic type declarations in C#.
/// </summary>
public static class GenericTypeEmitter
{

    /// <summary>
    /// Returns only the type's own generic parameters, excluding those inherited from
    /// generic ancestors. Swift's ABI JSON copies an outer generic signature into every
    /// nested type's signature, so a non-generic <c>Failure</c> nested inside
    /// <c>VerificationOutcome&lt;SignedType&gt;</c> still arrives with one parameter
    /// (<c>τ_0_0</c>). Re-declaring it on the nested C# type produces CS0693
    /// (parameter shadows outer) and forces every reference to add a redundant
    /// type argument (CS0305). Mirrors
    /// <c>WrapperEmitter.GetMethodOwnGenericParams</c> for methods.
    /// </summary>
    public static List<GenericArgumentDecl> GetTypeDeclOwnGenericParams(TypeDecl typeDecl)
    {
        if (!typeDecl.IsGeneric)
            return new List<GenericArgumentDecl>();

        // Collect names declared by every generic ancestor on the parent chain. The
        // parser stamps the outer signature onto the nested decl by *name*, so a
        // name-set match is the right discriminator (depth/index aren't carried on
        // GenericArgumentDecl).
        HashSet<string>? inheritedNames = null;
        BaseDecl? current = typeDecl.ParentDecl;
        while (current is TypeDecl td)
        {
            if (td.IsGeneric)
            {
                inheritedNames ??= new HashSet<string>();
                foreach (var p in td.GenericParameters)
                    inheritedNames.Add(p.TypeName);
            }
            current = td.ParentDecl;
        }

        if (inheritedNames == null || inheritedNames.Count == 0)
            return typeDecl.GenericParameters;

        return typeDecl.GenericParameters
            .Where(p => !inheritedNames.Contains(p.TypeName))
            .ToList();
    }

    /// <summary>
    /// Gets the generic type parameter list for a type declaration.
    /// For example, if a type has parameters T and U, returns "&lt;T0, T1&gt;".
    /// Inherited parent generics are dropped; see <see cref="GetTypeDeclOwnGenericParams"/>.
    /// </summary>
    /// <param name="typeDecl">The type declaration.</param>
    /// <returns>The generic parameter list, or empty string if not generic.</returns>
    public static string GetGenericParameterList(TypeDecl typeDecl)
    {
        var ownParams = GetTypeDeclOwnGenericParams(typeDecl);
        if (ownParams.Count == 0)
            return string.Empty;

        var typeParams = ownParams
            .Select((p, i) => NameProvider.GetCSharpGenericParameterName(p, i))
            .ToList();

        return $"<{string.Join(", ", typeParams)}>";
    }

    /// <summary>
    /// Gets the type name with generic parameters appended.
    /// For example, "Box" becomes "Box&lt;T0&gt;" for a generic type.
    /// When a type database is provided, checks if the CSharpTypeName was renamed
    /// (e.g., nested type "Options" → "OptionsType" to avoid CS0102 collision with a property).
    /// </summary>
    /// <param name="typeDecl">The type declaration.</param>
    /// <param name="typeDatabase">Optional type database for CSharpTypeName rename resolution.</param>
    /// <returns>The type name with generic parameters.</returns>
    public static string GetTypeNameWithGenerics(TypeDecl typeDecl, ITypeDatabase? typeDatabase = null)
    {
        var baseName = NameProvider.ToPascalCaseForTypeName(typeDecl.Name);

        // Check if CSharpTypeName was renamed (e.g., by ComputePropertyRenames for nested type collisions).
        // The CSharpTypeName.Name may be "Parent.OptionsType" when TypeDecl.Name is still "Options".
        if (typeDatabase != null && typeDatabase.TryGetTypeRecord(typeDecl.SwiftTypeName, out var record))
        {
            var csName = record.CSharpTypeName.Name;
            // Extract the leaf name (last dot-separated segment)
            var lastDot = csName.LastIndexOf('.');
            var leafName = lastDot >= 0 ? csName.Substring(lastDot + 1) : csName;
            if (leafName != baseName)
                baseName = leafName;
        }

        return $"{baseName}{GetGenericParameterList(typeDecl)}";
    }

    /// <summary>
    /// Gets the where clause constraints for a generic type declaration.
    /// Each generic parameter gets an ISwiftObject constraint when the underlying Swift
    /// generic param declares ANY non-Sendable protocol conformance — even one whose
    /// projection is filtered out of the C# constraint list (associated types, Self
    /// requirements, Self-method-typed protocols, cross-module unregistered protocols).
    /// PWT lookup via <c>ProtocolWitnessTable.GetOrThrowAuto&lt;T, IFoo&gt;</c> requires
    /// <c>T : ISwiftObject</c>, and the descriptor-symbol path still emits PWT calls
    /// for filtered conformances even though they don't appear in the C# where clause.
    ///
    /// The ISwiftObject seed is only dropped when the Swift param truly carries zero
    /// protocol conformances — buffer-style generics like RealityFoundation's
    /// <c>MeshBuffer&lt;TElement&gt;</c>. That makes blittable instantiations
    /// (<c>Vector3</c>, <c>float</c>, <c>uint</c>) compile at the call site instead of
    /// failing CS0315.
    /// </summary>
    /// <param name="typeDecl">The type declaration.</param>
    /// <param name="typeDatabase">Optional type database for checking protocol capabilities.</param>
    /// <returns>The where clause, or empty string if no constraints.</returns>
    public static string GetWhereClause(TypeDecl typeDecl, ITypeDatabase? typeDatabase = null)
    {
        var ownParams = GetTypeDeclOwnGenericParams(typeDecl);
        if (ownParams.Count == 0)
            return string.Empty;

        var constraints = new List<string>();

        for (int i = 0; i < ownParams.Count; i++)
        {
            var param = ownParams[i];
            var typeParamName = NameProvider.GetCSharpGenericParameterName(param, i);

            // Collect protocol conformance constraints first; ISwiftObject is seeded only
            // if at least one protocol survives filtering (see method summary above).
            var paramConstraints = new List<string>();
            // Captures an ObjC-bridged class form of a Swift protocol constraint
            // (e.g. `some UIScene` / `T : UIScene` whose record is the synthetic
            // `UIKit.UIScene` class created by `CreateObjCBridgedTypeRecord`).
            // Class constraints displace `ISwiftObject` because ObjC-bridged classes
            // do not implement `ISwiftObject` and the C# `where` syntax requires the
            // class constraint first ahead of any interface constraints.
            string? classConstraint = null;

            // Track whether any class-bound constraint was emitted on this param. Class
            // constraints already imply ISwiftObject (every Swift class projects to a
            // C#-side `ISwiftObject` subtype), so the marker-protocol seeding logic
            // below must NOT re-prepend `ISwiftObject` to a `where T : ClassName` clause
            // (would give an invalid C# constraint order).
            bool hasClassBoundConstraint = false;

            // Add protocol conformance constraints
            foreach (var conformance in param.GenericConformances)
            {
                if (conformance.Kind == ConformanceKind.Protocol)
                {
                    // Skip stdlib marker protocols (Swift.Sendable, Swift.Copyable,
                    // Swift.Escapable, Swift.SendableMetatype, Swift.BitwiseCopyable).
                    // They carry no runtime witness table and have no useful C# constraint
                    // shape — emitting them as ISwiftObject-derived interfaces would
                    // re-introduce CS0315 for blittable instantiations. Module-qualified
                    // so a same-name app/framework protocol is NOT mistaken for a marker.
                    if (IsStdlibMarkerProtocol(conformance.ConformanceTarget))
                        continue;

                    // Skip constraints from unsupported framework modules (e.g. SwiftUI.View).
                    if (IsUnsupportedConstraintModule(conformance.ConformanceTarget.Module))
                        continue;

                    // Class-bound generic constraint (`<T : SomeClass>`). The parser tags
                    // every `:` clause as ConformanceKind.Protocol because it has no
                    // type-database access; consult the resolved record's Kind here so a
                    // class target emits the C# class name instead of an `I{Name}` form.
                    // The record's CSharpTypeName carries the projected class name
                    // (e.g. `Foundation.Dimension` → `Foundation.NSDimension` via the
                    // FoundationDatabase.xml mapping). Mirrors the parallel skip in
                    // PInvokeHelperEmitter.FlattenConformances: class constraints add no
                    // PWT arg, only a compile-time C# bound.
                    if (typeDatabase != null
                        && typeDatabase.TryGetTypeRecord(conformance.ConformanceTarget, out var maybeClassRecord)
                        && maybeClassRecord.Kind == TypeRecordKind.Class)
                    {
                        paramConstraints.Add(maybeClassRecord.CSharpTypeName.FullyQualifiedName);
                        hasClassBoundConstraint = true;
                        continue;
                    }

                    // Skip protocols with associated types (they generate generic interfaces
                    // which can't be used as constraints without type arguments)
                    if (typeDatabase != null && HasAssociatedTypes(typeDatabase, conformance.ConformanceTarget))
                        continue;

                    // Skip protocols whose methods use Self (τ_0_0) in parameter/return types.
                    // The interface emits AnyType for Self positions, so concrete types can't
                    // implement the interface (CS0738) and the constraint can't be satisfied.
                    if (typeDatabase != null && HasMethodSelfTypeParams(typeDatabase, conformance.ConformanceTarget))
                        continue;

                    // Skip protocols whose Self is a required associated type (Equatable,
                    // Hashable, Comparable, …). These cannot be expressed as a non-generic
                    // C# interface constraint; the PWT arg still flows via descriptor symbol
                    // through PInvokeHelperEmitter's runtime-descriptor path.
                    if (typeDatabase != null && HasSelfRequirement(typeDatabase, conformance.ConformanceTarget))
                        continue;

                    // Skip cross-module protocol constraints not registered in TypeDatabase.
                    // Same-module protocols are always registered during module processing.
                    if (typeDatabase != null
                        && conformance.ConformanceTarget.Module != (typeDecl.ModuleDecl?.Name ?? ""))
                    {
                        // Cross-module ObjC-bridged "@protocol UIScene" → C# class
                        // `UIKit.UIScene`. Capture as the type-level class constraint
                        // and continue. First match wins; multiple class targets on
                        // the same param are unrepresentable in C# (single-inheritance)
                        // anyway, so subsequent ObjC-class targets are ignored.
                        // <see cref="MethodValidationGates.TryGetClassConstraintTarget"/>
                        // also covers autoBridge framework types (UIKit/AppKit/...)
                        // that synthesize on demand instead of being pre-registered.
                        if (classConstraint == null
                            && MethodValidationGates.TryGetClassConstraintTarget(
                                conformance.ConformanceTarget, typeDatabase, out var csClassName))
                        {
                            classConstraint = csClassName;
                            continue;
                        }

                        if (!typeDatabase.TryGetTypeRecord(conformance.ConformanceTarget, out var constraintRecord))
                            continue;
                        // Skip well-known stdlib protocols that map to runtime types (not interfaces).
                        // e.g., Swift.Error → AnyError (no IError interface is emitted)
                        if (TypeDatabaseExtensions.IsWellKnownRuntimeProtocol(constraintRecord))
                            continue;
                        // Other Kind values (Struct, Enum, Protocol) fall through to
                        // the historical interface-name emission below — that path is
                        // intentionally permissive (cross-module records get "I"-prefixed
                        // interface names regardless of declared Kind, which keeps the
                        // existing supplement-resolved-as-Struct cases compiling).
                    }

                    // Convert Swift protocol name to C# interface name. Use the resolved
                    // emission namespace (umbrella fallback) so a `RealityKit`-qualified
                    // ABI conformance pointing at a `RealityFoundation` protocol record
                    // is emitted as `RealityFoundation.IProtocol`, not bare `IProtocol`.
                    var resolvedConstraintModule = typeDatabase != null
                        ? ProtocolConformanceHelper.ResolveProtocolEmissionModule(conformance.ConformanceTarget, typeDatabase)
                        : conformance.ConformanceTarget.Module;
                    var interfaceName = NameProvider.GetInterfaceName(conformance.ConformanceTarget.Name, moduleName: resolvedConstraintModule, currentModuleName: typeDecl.ModuleDecl?.Name ?? "");
                    paramConstraints.Add(interfaceName);
                }
            }

            if (classConstraint != null)
            {
                // ObjC-bridged class constraint (e.g. `where T : UIKit.UIScene`):
                // emit the class first; ISwiftObject is NOT seeded because ObjC-bridged
                // classes do not implement ISwiftObject. Any surviving interface
                // constraints are appended after the class per C# `where` syntax.
                var ordered = new List<string> { classConstraint };
                ordered.AddRange(paramConstraints);
                constraints.Add($"{typeParamName} : {string.Join(", ", ordered)}");
                continue;
            }

            // Seed ISwiftObject when the Swift param carries ANY non-Sendable protocol
            // conformance — including ones filtered from the C# constraint list (associated
            // types, Self-requirement, etc.) because the descriptor-symbol PWT path still
            // emits `ProtocolWitnessTable.GetOrThrowAuto<T, IFoo>` calls for them, which
            // require `T : ISwiftObject`. Drop the seed only when there are zero protocol
            // conformances at all, so unconstrained generics accept blittable args
            // (Vector3, float, uint, …) instead of failing CS0315 at call sites.
            //
            // Class-bound constraints (`where T : SomeClass`) already imply ISwiftObject
            // (every projected Swift class derives from `SwiftObject` and implements
            // `ISwiftObject`), AND a class-type constraint MUST appear before any
            // interface constraint per C# CS0405/CS0406 rules. Re-seeding `ISwiftObject`
            // in front would move an interface ahead of the class constraint and break
            // compilation. Skip the seed when a class bound is already present.
            bool hasAnyProtocolConformance = HasAnyNonMarkerProtocolConformance(param);
            if (paramConstraints.Count > 0 || hasAnyProtocolConformance)
            {
                if (!hasClassBoundConstraint)
                    paramConstraints.Insert(0, "ISwiftObject");
                constraints.Add($"{typeParamName} : {string.Join(", ", paramConstraints)}");
            }
        }

        if (constraints.Count == 0)
            return string.Empty;

        // Each type parameter needs its own 'where' clause in C#
        // e.g., "where T0 : ISwiftObject, IFoo where T1 : ISwiftObject, IBar"
        return string.Join(" ", constraints.Select(c => $"where {c}"));
    }

    /// <summary>
    /// Detects whether a generic type has a protocol constraint from an unsupported module
    /// (e.g. SwiftUI), which should cause the type to be skipped during emission.
    /// </summary>
    /// <param name="typeDecl">The type declaration.</param>
    /// <param name="unsupportedConstraint">The first unsupported protocol constraint encountered.</param>
    /// <returns>True if an unsupported constraint was found; otherwise false.</returns>
    public static bool TryGetUnsupportedConstraint(TypeDecl typeDecl, [NotNullWhen(true)] out SwiftTypeName? unsupportedConstraint)
    {
        unsupportedConstraint = null;
        if (!typeDecl.IsGeneric)
            return false;

        foreach (var param in typeDecl.GenericParameters)
        {
            foreach (var conformance in param.GenericConformances)
            {
                if (conformance.Kind != ConformanceKind.Protocol)
                    continue;

                if (IsUnsupportedConstraintModule(conformance.ConformanceTarget.Module))
                {
                    unsupportedConstraint = conformance.ConformanceTarget;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsUnsupportedConstraintModule(string moduleName) =>
        ValidationRuleSet.IsUnsupportedConstraintModule(moduleName);

    /// <summary>
    /// Returns true when the generic param declares at least one non-marker Swift
    /// protocol conformance. Stdlib marker protocols (<c>Swift.Sendable</c>,
    /// <c>Swift.Copyable</c>, <c>Swift.Escapable</c>, <c>Swift.SendableMetatype</c>,
    /// <c>Swift.BitwiseCopyable</c>) have no runtime witness table, so they never
    /// drive a PWT lookup. Used by <see cref="GetWhereClause"/> to decide whether
    /// the <c>ISwiftObject</c> seed must be retained even when every conformance is
    /// filtered out of the C# constraint list — filtered non-marker conformances
    /// still emit PWT lookups via the descriptor-symbol path and those calls
    /// require <c>T : ISwiftObject</c>.
    /// </summary>
    private static bool HasAnyNonMarkerProtocolConformance(GenericArgumentDecl param)
    {
        foreach (var conformance in param.GenericConformances)
        {
            if (conformance.Kind != ConformanceKind.Protocol)
                continue;
            if (IsStdlibMarkerProtocol(conformance.ConformanceTarget))
                continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Stdlib marker protocols carry no runtime witness table — the Swift compiler
    /// does not pass them as PWT args to type metadata accessors. Module-qualified
    /// to avoid misidentifying a same-name app/framework protocol as a marker.
    /// Kept in sync with <c>PInvokeHelperEmitter.IsStdlibMarkerProtocol</c> and
    /// <c>ExistentialHandler.IsMarkerProtocol</c>.
    /// </summary>
    private static bool IsStdlibMarkerProtocol(SwiftTypeName protocolTypeName) =>
        protocolTypeName.Module == "Swift" &&
        protocolTypeName.Name is "Sendable" or "Escapable" or "Copyable"
                              or "SendableMetatype" or "BitwiseCopyable";

    /// <summary>
    /// Returns true if the module is unsupported for constraint and member-level filtering.
    /// Delegates to <see cref="ValidationRuleSet.IsUnsupportedConstraintModule"/> as the
    /// single source of truth.
    /// </summary>
    public static bool IsUnsupportedModule(string moduleName) =>
        ValidationRuleSet.IsUnsupportedConstraintModule(moduleName);

    /// <summary>
    /// Checks if a protocol has associated types (which would make it a generic interface in C#).
    /// </summary>
    private static bool HasAssociatedTypes(ITypeDatabase typeDatabase, SwiftTypeName protocolTypeName)
    {
        if (typeDatabase.TryGetTypeRecord(protocolTypeName, out var record))
        {
            return record.Kind == TypeRecordKind.Protocol &&
                   record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes);
        }
        return false;
    }

    /// <summary>
    /// Checks if a protocol's methods use Self (τ_0_0) in parameter/return types.
    /// Such protocols emit AnyType for Self positions in the interface, making the
    /// constraint unsatisfiable by concrete types (CS0738/CS0311).
    /// </summary>
    private static bool HasMethodSelfTypeParams(ITypeDatabase typeDatabase, SwiftTypeName protocolTypeName)
    {
        if (typeDatabase.TryGetTypeRecord(protocolTypeName, out var record))
        {
            return record.Kind == TypeRecordKind.Protocol &&
                   record.Flags.HasFlag(TypeRecordFlags.HasMethodSelfTypeParams);
        }
        return false;
    }

    /// <summary>
    /// Checks whether a protocol has <c>Self</c> as a required associated type. The Swift
    /// metadata accessor still expects a witness-table argument for these, but they cannot
    /// be projected as a usable C# interface constraint — routed through the descriptor
    /// symbol path in <see cref="PInvokeHelperEmitter"/> instead.
    /// </summary>
    private static bool HasSelfRequirement(ITypeDatabase typeDatabase, SwiftTypeName protocolTypeName)
    {
        if (typeDatabase.TryGetTypeRecord(protocolTypeName, out var record))
        {
            return record.Kind == TypeRecordKind.Protocol &&
                   record.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement);
        }
        return false;
    }

    /// <summary>
    /// Gets the full type declaration signature including generics and where clause.
    /// For example: "Box&lt;T0&gt; where T0 : ISwiftObject"
    /// </summary>
    /// <param name="typeDecl">The type declaration.</param>
    /// <param name="typeDatabase">Optional type database for checking protocol capabilities.</param>
    /// <returns>The full type signature.</returns>
    public static string GetFullTypeSignature(TypeDecl typeDecl, ITypeDatabase? typeDatabase = null)
    {
        var name = GetTypeNameWithGenerics(typeDecl, typeDatabase);
        var whereClause = GetWhereClause(typeDecl, typeDatabase);

        if (string.IsNullOrEmpty(whereClause))
            return name;

        return $"{name} {whereClause}";
    }

    /// <summary>
    /// Generates the GetTypeMetadata implementation for a generic type.
    /// Generic types need to pass type metadata for each type parameter to the metadata accessor.
    /// </summary>
    /// <param name="typeDecl">The type declaration.</param>
    /// <returns>The GetTypeMetadata method body.</returns>
    public static string GetGenericMetadataAccessor(TypeDecl typeDecl)
    {
        var ownParams = GetTypeDeclOwnGenericParams(typeDecl);
        if (ownParams.Count == 0)
            return string.Empty;

        var typeParams = ownParams
            .Select((p, i) => $"TypeMetadata.GetTypeMetadataOrThrow<{NameProvider.GetCSharpGenericParameterName(p, i)}>()")
            .ToList();

        return string.Join(", ", typeParams);
    }
}
