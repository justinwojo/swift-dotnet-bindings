// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;

namespace BindingsGeneration;

/// <summary>
/// Sync-path eligibility predicate for concrete protocol specialization. The async path
/// already has an <see cref="IsCsmAsyncEligible"/> predicate that drives a Phase-4a
/// intercept in <see cref="MemberValidationPipeline"/>, suppressing the unspecialized
/// generic emission so only the concrete overloads appear. The sync path lacked an
/// equivalent predicate; when the parent type is generic, concrete overloads emit as
/// extension methods on a <c>{Type}{ParentConformer}CsmExtensions</c> class while the
/// open-generic instance method remained on the class body — instance methods shadow
/// extensions during C# overload resolution, so calls like <c>container.Append(item, data)</c>
/// dispatched to the broken open-generic path instead of the CSM extension.
///
/// This predicate scopes the fix to exactly the cases where the shadow actually occurs:
/// sync methods on a generic parent whose CSM pairings emit as extensions. For
/// non-generic-parent sync CSM (e.g. <c>DataHasher.Update</c>), the concrete overloads
/// emit as instance methods and win overload resolution naturally, so no suppression is
/// needed and this predicate returns false.
///
/// The open-generic form is only withheld when it has no <c>@_cdecl</c> wrapper — the broken
/// path above is the direct call it otherwise falls back to. One that wraps is sound, and keeping
/// it keeps the member on every instantiation under its own name
/// (<see cref="WithholdsOpenGenericForGenericParent"/>).
/// </summary>
public static partial class ConcreteProtocolSpecializationEmitter
{
    /// <summary>
    /// Returns true if the method will be routed through the CSM-sync emission path on a
    /// generic parent (i.e. <see cref="EmitConcreteSpecializationsForGenericParent"/>), so
    /// closed extension overloads will exist for it. Whether its open-generic form is withheld as
    /// well is the separate <see cref="WithholdsOpenGenericForGenericParent"/> decision.
    ///
    /// Mirrors the skip conditions of <see cref="EmitConcreteSpecializationsForGenericParent"/>
    /// so the predicate cannot declare suppressibility for a method the emitter will drop.
    /// </summary>
    public static bool IsCsmSyncEligibleForGenericParent(
        MethodDecl method,
        TypeDecl parentTypeDecl,
        ITypeDatabase typeDatabase,
        ConcreteSpecializationEngine engine)
    {
        if (method.IsAsync) return false;
        if (method.IsAccessor) return false;
        if (method.IsConstructor) return false;

        // Static methods: the CSM emitter only routes instance methods through the
        // generic-parent extension path. Suppressing the open-generic emission for a
        // static would strip its surface without any replacement.
        if (method.MethodType == MethodType.Static) return false;

        // Only generic-parent cases emit as extensions. Non-generic parents emit concrete
        // overloads as instance methods, which win resolution without suppression.
        if (!parentTypeDecl.IsGeneric) return false;

        // Nested generic types are not targeted by EmitConcreteSpecializationsForGenericParent
        // (the three handler call sites gate on top-level types). Suppressing the open generic
        // for a nested parent would strip the method entirely.
        if (parentTypeDecl.ParentDecl is TypeDecl) return false;

        if (!WrapperValidation.IsXCFrameworkMode(typeDatabase)) return false;

        var specializable = engine.FindSpecializableMethods(parentTypeDecl)
            .FirstOrDefault(sm => ReferenceEquals(sm.Method, method));
        if (specializable is null) return false;

        // Must have parent-generic specialization: without it, emission stays on the
        // instance-method path (which won't shadow).
        if (!specializable.SpecializableParams.Any(p => p.IsParentGeneric)) return false;

        // Every method-own generic param must be specializable. A partially-specialized
        // method still needs the open-generic surface for the unspecialized param, so
        // suppression would break it.
        var parentParamNames = new HashSet<string>(
            parentTypeDecl.GenericParameters.Select(p => p.TypeName));
        var ownParamCount = method.GenericParameters
            .Count(p => !parentParamNames.Contains(p.TypeName));
        var ownSpecializableCount = specializable.SpecializableParams
            .Count(p => !p.IsParentGeneric);
        if (ownSpecializableCount != ownParamCount) return false;

        var pairingCount = ComputePairingCount(specializable.SpecializableParams);
        if (pairingCount == 0 || pairingCount > MaxCsmCartesianProductSize) return false;

        // At least one pairing must pass both coupling + the full emitter preflight.
        // If no pairing survives, the emitter produces nothing for this method and
        // suppressing the open generic would strip the method's symbol entirely.
        foreach (var pairing in CartesianPairings(specializable.SpecializableParams))
        {
            if (!ConformerPairingSatisfiesCoupling(pairing)) continue;
            if (!CanEmitConcreteOverloadForPairing(method, parentTypeDecl, pairing, typeDatabase, out _)) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a sync instance method's open-generic form is withheld in favour of its CSM
    /// extension overloads. The single decision both the member pipeline (which drops the
    /// open-generic emission) and <see cref="ProtocolExtensionEmitter"/> (which then skips the
    /// open form's Swift wrapper) consult, so the two cannot disagree about whether the open
    /// form ships.
    /// </summary>
    /// <remarks>
    /// The extension overloads cover only the conformers the module declares, under a
    /// CSM-shaped name. Withholding the open form therefore removes the member from every other
    /// instantiation, and renames it on the ones it keeps. That is only worth doing when the open
    /// form has no sound route of its own: the direct <c>CallConvSwift</c> call a generic-parent
    /// method falls back to without a wrapper is the one this suppression was introduced to keep
    /// callers away from. An open form that gets a <c>@_cdecl</c> wrapper is kept, and because an
    /// instance method is preferred over an extension method, callers bind to it; the extension
    /// overloads stay available alongside.
    /// </remarks>
    public static bool WithholdsOpenGenericForGenericParent(
        MethodDecl method,
        TypeDecl parentTypeDecl,
        ITypeDatabase typeDatabase,
        ConcreteSpecializationEngine engine)
        => IsCsmSyncEligibleForGenericParent(method, parentTypeDecl, typeDatabase, engine)
           && !OpenGenericFormHasCdeclWrapper(method, typeDatabase);

    // Keyed by reference: MethodDecl is a record, and value equality would let a clone share a verdict.
    private static readonly ConditionalWeakTable<MethodDecl, StrongBox<bool>> s_openFormHasCdeclWrapper = new();

    /// <summary>
    /// Whether the method's open-generic form would be emitted through a <c>@_cdecl</c> method
    /// wrapper, asked of the same wrapper decision the method handler takes.
    /// </summary>
    /// <remarks>
    /// Memoized on first ask because the answer must not move once emission starts: emitting the
    /// wrapper sets <see cref="MethodDecl.UsesWrapperLibrary"/> on the decl, which that decision
    /// reads as "another wrapper owns this member", so a re-validation after emission would
    /// otherwise flip the verdict for a member that did ship. Every first ask precedes emission —
    /// validation runs before the handler, and the protocol-extension pass runs before both.
    /// The decision reads only the decl and the type database, so no emission context is needed.
    /// </remarks>
    internal static bool OpenGenericFormHasCdeclWrapper(MethodDecl method, ITypeDatabase typeDatabase)
        => s_openFormHasCdeclWrapper.GetValue(method, m => new StrongBox<bool>(
            WrapperValidation.DetermineMethodWrapperDecision(new MethodEnvironment(m, typeDatabase))
                == WrapperDecision.WrapperRequired)).Value;
}
