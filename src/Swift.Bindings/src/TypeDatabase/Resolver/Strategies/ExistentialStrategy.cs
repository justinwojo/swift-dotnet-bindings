// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Resolves existential <c>any X</c> identities (e.g. <c>any Swift.Encoder</c>),
/// bare <c>any</c> placeholders, and unqualified type names that reach the
/// resolver as parsing artifacts. Existentials degrade to
/// <see cref="TypeDatabaseExtensions.AnyType"/>; the resolver also flags this as
/// a synthetic fallback so <c>TryGetAnyTypeFallbackInfo</c> can surface the
/// degradation as a missing-binding diagnostic. <c>Swift.Any</c> and
/// <c>Swift.AnyObject</c> are deliberately excluded — those are handled by
/// <see cref="SwiftAnyAnyObjectStrategy"/>.
/// </summary>
internal sealed class ExistentialStrategy : IResolutionStrategy
{
    public string Name => "Existential";

    public bool TryResolve(
        TypeSpec typeSpec,
        ResolutionContext context,
        [NotNullWhen(true)] out TypeResolutionResult? result)
    {
        if (typeSpec is NamedTypeSpec named && TypeDatabaseExtensions.IsExistentialTypeName(named))
        {
            // Constrained existentials whose generic arguments are all concrete
            // (`any P<X>`) project as `IP<X>` through ExistentialHandler — no surface
            // degradation. Suppress the synthetic fallback so the wrapper doesn't get
            // an `[UnsupportedSwiftType("Existential type fallback", …)]` annotation
            // that contradicts the strongly-typed projection.
            // (Constrained existential Cases 1 + 2: concrete-arg `any P<X>` and plain `any P`.)
            //
            // Plain (no-generic-args) existentials over a real protocol with no
            // associated types and no Self requirement also project cleanly to
            // `IP` through the standard existential proxy. Suppress the fallback
            // there too — emitting `[UnsupportedSwiftType("Existential type fallback", …)]`
            // on a member whose body uses the working proxy is build-noise that
            // hides genuine obsoletes (e.g. Lottie `DotLottieFile.NamedAsync(…, IDotLottieCacheProvider?, …)`).
            TypeDatabaseExtensions.AnyTypeFallbackInfo? fallback =
                HasResolvableConcreteGenericArgs(named, context.Database) ||
                IsProjectablePlainExistential(named, context.Database)
                    ? null
                    : new TypeDatabaseExtensions.AnyTypeFallbackInfo(
                        "Existential type fallback",
                        typeSpec.ToString());

            result = new TypeResolutionResult(
                Record: TypeDatabaseExtensions.AnyType,
                SyntheticFallback: fallback,
                Provenance: new ResolutionProvenance($"strategy:{Name}"));
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="named"/> is a no-generic-args existential
    /// (`any P`) whose underlying protocol projects cleanly through the standard
    /// existential proxy — i.e. exists in the database with <c>Kind=Protocol</c>,
    /// carries neither <c>HasSelfRequirement</c> nor <c>HasAssociatedTypes</c>,
    /// and is neither a marker protocol (Sendable, Escapable, Copyable,
    /// SendableMetatype) nor an ObjC-existential-bridged protocol. Marker and
    /// ObjC-bridged protocols are filtered out by
    /// <see cref="ExistentialHandler.GetEffectiveProtocols"/>, so a single-protocol
    /// existential over either one collapses to <c>object</c> in
    /// <see cref="ExistentialHandler.GetPublicExistentialType"/> — that IS a real
    /// surface degradation and the fallback annotation must fire. Mirrors only the
    /// projection branch that returns <c>I{Name}</c> for plain protocols.
    /// </summary>
    private static bool IsProjectablePlainExistential(NamedTypeSpec named, ITypeDatabase typeDatabase)
    {
        if (named.GenericParameters.Count != 0)
            return false;

        // Parity with ExistentialHandler.GetEffectiveProtocols: marker and
        // ObjC-bridged protocols are stripped before projection. The single-protocol
        // path then sees an empty effective list and returns "object", not I{Name}.
        if (ExistentialHandler.IsMarkerProtocol(named))
            return false;
        if (TypeDatabaseExtensions.IsObjCExistentialBridgedProtocol(named))
            return false;

        try
        {
            var protoSwiftName = SwiftTypeName.FromTypeSpec(named);
            if (!typeDatabase.TryGetTypeRecord(protoSwiftName, out var protoRecord))
                return false;
            if (protoRecord.Kind != TypeRecordKind.Protocol)
                return false;
            if (protoRecord.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement) ||
                protoRecord.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes))
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasResolvableConcreteGenericArgs(NamedTypeSpec named, ITypeDatabase typeDatabase)
    {
        if (named.GenericParameters.Count == 0)
            return false;

        // Mirror the AssociatedTypeCount arity gate from ExistentialHandler.TryResolveExistentialGenericArgs
        // (constrained existential Cases 1+2). Primary-associated-type sugar
        // lets `any P<X, Y>` reference a 3-AT protocol with fewer args than the interface arity —
        // ExistentialHandler correctly bails to AnyType in that case, but without this gate the
        // strategy would still suppress the `[UnsupportedSwiftType("Existential type fallback", …)]`
        // annotation, leaving an opaque AnyType surface with no diagnostic. The suppression is
        // only safe when the projection actually succeeds; the projection only succeeds when the
        // arity matches the protocol's total associated-type count.
        try
        {
            var protoSwiftName = SwiftTypeName.FromTypeSpec(named);
            if (typeDatabase.TryGetTypeRecord(protoSwiftName, out var protoRecord) &&
                protoRecord.AssociatedTypeCount.HasValue &&
                protoRecord.AssociatedTypeCount.Value != named.GenericParameters.Count)
            {
                return false;
            }
        }
        catch
        {
            // Treat as unverifiable; fall through to the per-arg loop below. Legacy module
            // databases that predate AssociatedTypeCount land here and keep the prior behavior.
        }

        foreach (var gp in named.GenericParameters)
        {
            if (gp is not NamedTypeSpec namedArg)
                return false;
            if (TypeSpecHelpers.IsGenericTypeParameter(namedArg.Name))
                return false;
            if (namedArg.GenericParameters.Count > 0)
                return false; // nested generics — out of scope for the conservative projection

            try
            {
                var argSwiftName = SwiftTypeName.FromTypeSpec(namedArg);
                if (!typeDatabase.TryGetTypeRecord(argSwiftName, out var argRecord) ||
                    argRecord.CSharpTypeName == null)
                    return false;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }
}
