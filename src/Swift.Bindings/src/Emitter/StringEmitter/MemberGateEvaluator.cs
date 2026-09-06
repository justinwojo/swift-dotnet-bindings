// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Disposition of a member gate evaluation.
/// </summary>
public enum GateDisposition
{
    /// <summary>All gates pass. Full emission.</summary>
    Emit,

    /// <summary>
    /// Has closure/existential params. In interface for concrete types to implement.
    /// Proxy gets NotSupportedException stub. Only meaningful in protocol context —
    /// callers in concrete-type context should treat as Emit.
    /// </summary>
    InterfaceOnly,

    /// <summary>Member fails a hard gate. Not in interface, not emitted.</summary>
    Skip,
}

/// <summary>
/// Soft gate flags indicating why a member is InterfaceOnly.
/// </summary>
[Flags]
public enum SoftGateFlags
{
    None = 0,
    HasClosureParam = 1 << 0,
    HasExistentialParam = 1 << 1,
    HasClosureProperty = 1 << 2,
}

/// <summary>
/// Result of evaluating member gates for a protocol property, method, or subscript.
/// </summary>
public sealed class GateResult
{
    public GateDisposition Disposition { get; init; }
    public SkipReason? Reason { get; init; }
    public string? Details { get; init; }
    public SoftGateFlags SoftFlags { get; init; }

    /// <summary>
    /// Extra explanation for the report only, appended to <see cref="Details"/> by
    /// <see cref="DetailsForReport"/>. Kept off <see cref="Details"/> because that string also feeds
    /// the <c>// Unsupported:</c> comment in the generated C# on some paths — a diagnostics-only
    /// enrichment must not move generated source.
    /// </summary>
    public string? ReportDetails { get; init; }

    /// <summary>The explanation as <c>binding-report.json</c> should carry it.</summary>
    public string? DetailsForReport =>
        string.IsNullOrEmpty(ReportDetails) ? Details : Details + ReportDetails;

    public bool IsSkipped => Disposition == GateDisposition.Skip;
    public bool IsEmittable => Disposition != GateDisposition.Skip;
    public bool IsInterfaceOnly => Disposition == GateDisposition.InterfaceOnly;

    public static readonly GateResult Pass = new() { Disposition = GateDisposition.Emit };

    public static GateResult Skipped(SkipReason reason, string details, string? reportDetails = null) =>
        new() { Disposition = GateDisposition.Skip, Reason = reason, Details = details, ReportDetails = reportDetails };

    public static GateResult SoftSkip(SoftGateFlags flags) =>
        new() { Disposition = GateDisposition.InterfaceOnly, SoftFlags = flags };
}

/// <summary>
/// Centralized evaluator for member gate logic shared across ProtocolHandler,
/// ProtocolConformanceValidator, MethodHandler, and MemberEmissionValidator.
/// Eliminates duplicated type-resolution gates (bare generic, non-ISwiftObject,
/// unsupported module, AnyType generic arg, closure/existential detection).
/// </summary>
public class MemberGateEvaluator
{
    private readonly ITypeDatabase _typeDatabase;

    public MemberGateEvaluator(ITypeDatabase typeDatabase)
    {
        _typeDatabase = typeDatabase;
    }

    /// <summary>
    /// Full evaluation for a protocol property. Checks hard gates and soft gates (closure → InterfaceOnly).
    /// </summary>
    public GateResult EvaluateProperty(PropertyDecl property, ModuleDecl? moduleDecl, ProtocolDecl? protocolContext)
    {
        // Gate 0: a declaration a previous attempt threw on is refused before any other gate
        // runs. Protocol members reach here without passing through MemberValidationPipeline,
        // so this is their only denial point.
        if (EmitterFaultGate.IsDenied(DeclIdFactory.ForProperty(property), out var poisonDetails))
            return GateResult.Skipped(SkipReason.EmitterFault, poisonDetails);

        var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);

        // P2: Leaked associated type reference (e.g., TElement, TRowDecoder.ID)
        if (MemberEmissionValidator.ContainsAssociatedTypeReference(property.SwiftTypeSpec))
            return GateResult.Skipped(SkipReason.UnsupportedSignature, "Property type contains unresolvable associated type reference.");

        // P3: Bare generic usage
        if (boundGenericsHandler.HasBareGenericUsage(property.SwiftTypeSpec, property.ModuleDecl ?? moduleDecl))
            return GateResult.Skipped(SkipReason.UnsupportedSignature, "Property type uses generic type without type arguments.");

        // P4: AnyType as generic type argument (uses projection for TSelf-awareness)
        if (protocolContext != null)
        {
            var projected = ProtocolSignatureHelper.ProjectTypeToCSharp(property.SwiftTypeSpec, _typeDatabase, protocolContext);
            if (ContainsAnyTypeGenericArg(projected) ||
                TypeDatabaseExtensions.IsBareGenericTypeName(projected))
                return GateResult.Skipped(SkipReason.AnyTypeFallback,
                    "Property type contains AnyType as a generic type argument, which violates generic constraints.",
                    UnresolvedAppleTypes.DescribeSuffix(
                        new[] { property.SwiftTypeSpec }, _typeDatabase, (property.ModuleDecl ?? moduleDecl)?.Name));
        }

        // P5: Unsupported module references (types registered in type database are allowed through).
        // No scalar carve-out in protocol context — a projected LocalizedStringResource requirement
        // would still dispatch through the witness/proxy path, which transports the resilient struct.
        var propUnsupported = ValidationRuleSet.ClassifyUnsupportedReference(
            property.SwiftTypeSpec, _typeDatabase, out var propOffending);
        if (propUnsupported != ValidationRuleSet.UnsupportedReferenceKind.None)
            return GateResult.Skipped(ValidationRuleSet.ToSkipReason(propUnsupported),
                propUnsupported == ValidationRuleSet.UnsupportedReferenceKind.NetUnavailable
                    ? $"Property type references .NET-unavailable type '{propOffending}'."
                    : "Property type references unsupported module (SwiftUI/Combine).");

        // P6: Bound generic with non-ISwiftObject args
        if (property.SwiftTypeSpec is NamedTypeSpec propNamedType &&
            propNamedType.ContainsGenericParameters &&
            boundGenericsHandler.HasNonSwiftObjectGenericArg(property.SwiftTypeSpec))
            return GateResult.Skipped(SkipReason.UnsatisfiedGenericConstraint, "Bound generic contains type argument that cannot satisfy C# ISwiftObject constraint.");

        // P7: Closure property (soft gate → InterfaceOnly)
        if (protocolContext != null)
        {
            var closureHandler = new ClosureHandler(_typeDatabase);
            if (closureHandler.IsClosure(property))
                return GateResult.SoftSkip(SoftGateFlags.HasClosureProperty);
        }

        // P8: Optional existential whose inner protocol is not in the TypeDatabase.
        // Without a projected protocol type, ExistentialHandler.GetPublicExistentialType
        // falls back to "object" — we can't faithfully marshal SwiftOptional<ExistentialContainer>
        // into a meaningful C# nullable type. PropertyHandler skips such properties on the
        // conforming class side; the protocol interface MUST agree, otherwise concrete
        // types fail CS0535 (interface declares the property as `object?`, conforming
        // class has no implementation because it skipped it).
        // This is the protocol-side mirror of the existing skip in
        // PropertyHandler.cs (isOptionalExistential branch).
        var existentialHandler = new ExistentialHandler(_typeDatabase);
        if (existentialHandler.IsOptionalExistential(property.SwiftTypeSpec))
        {
            var innerProtocolList = existentialHandler.UnwrapOptionalExistential(property.SwiftTypeSpec);
            if (innerProtocolList != null &&
                existentialHandler.GetPublicExistentialType(innerProtocolList) == "object")
            {
                return GateResult.Skipped(SkipReason.AnyTypeFallback,
                    "Optional existential inner protocol not in TypeDatabase — falls back to object.",
                    UnresolvedAppleTypes.DescribeSuffix(
                        new[] { property.SwiftTypeSpec }, _typeDatabase, (property.ModuleDecl ?? moduleDecl)?.Name));
            }
        }

        // P8b: @objc protocol existential in an unsupported nested position (container/tuple/closure).
        // Only a bare `any P` / `Optional<any P>` property marshals as a single ObjC object pointer; a
        // nested position would route the reverse-dispatch receiver through the 40-byte ExistentialContainer1
        // carrier against an 8-byte @objc stride — a buffer over-read. Fail closed, in lockstep with the
        // concrete-side gate (MemberEmissionValidator.CanEmitProperty) AND the reverse-dispatch
        // VtableLayoutBuilder.ClassifyProperty: the requirement must leave the interface declaration and its
        // vtable slot together, or the vtable size desyncs → SIGSEGV.
        if (ExistentialHandler.HasUnsupportedObjCProtocolExistentialPosition(property.SwiftTypeSpec, _typeDatabase))
            return GateResult.Skipped(SkipReason.UnsupportedExistential,
                "Property has an @objc protocol existential in an unsupported nested position (container/tuple/closure).");

        // P9: Pattern 2 emission-time gate — property type reaches a name in
        // ModuleDecl.InternalTypeNames. Mirrors S6 in EvaluateSubscript and the
        // concrete-side gate in MemberValidationPipeline.ValidatePropertyEmission.
        // Protocol property emission bypasses MemberValidationPipeline, so this
        // gate must live here too — otherwise the protocol interface would declare
        // a property whose concrete-side counterpart was suppressed (CS0535).
        var resolvedPropertyModule = property.ModuleDecl ?? moduleDecl;
        var propertyInternalTypeNames = resolvedPropertyModule?.InternalTypeNames;
        if (propertyInternalTypeNames is { Count: > 0 } &&
            !string.IsNullOrEmpty(resolvedPropertyModule!.Name) &&
            InternalTypeReferenceWalker.SignatureReachesInternalType(
                property, propertyInternalTypeNames, resolvedPropertyModule.Name))
        {
            return GateResult.Skipped(SkipReason.Pattern2InternalTypeReach,
                "Property type reaches a @usableFromInline internal (or otherwise-suppressed) type.");
        }

        return GateResult.Pass;
    }

    /// <summary>
    /// Full evaluation for a protocol method. Checks hard gates and soft gates
    /// (closure/existential → InterfaceOnly with accumulate pattern).
    /// </summary>
    public GateResult EvaluateMethod(MethodDecl method, ModuleDecl? moduleDecl, ProtocolDecl? protocolContext)
    {
        // Gate 0: a declaration a previous attempt threw on is refused before any other gate
        // runs. Protocol members reach here without passing through MemberValidationPipeline,
        // so this is their only denial point.
        if (EmitterFaultGate.IsDenied(DeclIdFactory.ForMethod(method), out var poisonDetails))
            return GateResult.Skipped(SkipReason.EmitterFault, poisonDetails);

        var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);
        var softFlags = SoftGateFlags.None;

        // Leaked associated type reference (e.g., TElement, TRowDecoder.ID)
        if (method.CSSignature.Any(arg => MemberEmissionValidator.ContainsAssociatedTypeReference(arg.SwiftTypeSpec)))
            return GateResult.Skipped(SkipReason.UnsupportedSignature, "Method signature contains unresolvable associated type reference.");

        // Non-ISwiftObject bound generic args
        bool hasNonSwiftObjectArg = false;
        for (int i = 0; i < method.CSSignature.Count; i++)
        {
            var arg = method.CSSignature[i];
            bool isParameterPosition = i != 0;
            if (boundGenericsHandler.IsBoundGeneric(arg) &&
                boundGenericsHandler.HasNonSwiftObjectGenericArg(arg.SwiftTypeSpec, isParameterPosition))
            {
                hasNonSwiftObjectArg = true;
                break;
            }
        }
        if (hasNonSwiftObjectArg)
            return GateResult.Skipped(SkipReason.UnsatisfiedGenericConstraint, "Bound generic contains type argument that cannot satisfy C# ISwiftObject constraint.");

        // Bare generic usage in method signature
        if (HasBareGenericInMethodSignature(method, moduleDecl, boundGenericsHandler))
            return GateResult.Skipped(SkipReason.UnsupportedSignature, "Method signature uses generic type without type arguments.");

        // Existential parameter (soft gate — accumulate)
        if (protocolContext != null)
        {
            var existentialHandler = new ExistentialHandler(_typeDatabase);
            bool hasExistentialParam = method.CSSignature.Skip(1).Any(arg =>
                existentialHandler.IsExistential(arg.SwiftTypeSpec) ||
                existentialHandler.IsOptionalExistential(arg.SwiftTypeSpec));
            if (hasExistentialParam)
                softFlags |= SoftGateFlags.HasExistentialParam;
        }

        // @objc protocol existential in an unsupported nested position — hard, fail-closed drop, in lockstep
        // with the concrete-side gate (MemberEmissionValidator.ShouldSkipMethodEmission) AND the
        // reverse-dispatch VtableLayoutBuilder.ClassifyMethod. Only a bare `any P` / `Optional<any P>` marshals
        // as a single ObjC object pointer; a nested container/tuple/closure position would route the reverse
        // receiver through the 40-byte ExistentialContainer1 carrier against an 8-byte @objc stride (buffer
        // over-read). The gate is the SAME predicate on both sides so dropping the requirement from the
        // interface and dropping its vtable slot stay in lockstep — desyncing them would blow up vtable size → SIGSEGV.
        foreach (var arg in method.CSSignature)
        {
            if (ExistentialHandler.HasUnsupportedObjCProtocolExistentialPosition(arg.SwiftTypeSpec, _typeDatabase))
                return GateResult.Skipped(SkipReason.UnsupportedExistential,
                    "Method has an @objc protocol existential in an unsupported nested position (container/tuple/closure).");
        }

        // Closure parameter (soft gate — accumulate)
        if (protocolContext != null)
        {
            var closureHandler = new ClosureHandler(_typeDatabase);
            bool hasClosureParam = method.CSSignature.Skip(1).Any(arg =>
                closureHandler.IsClosure(arg));
            if (hasClosureParam)
                softFlags |= SoftGateFlags.HasClosureParam;
        }

        // AnyType as generic type argument (uses projection for TSelf-awareness)
        if (protocolContext != null && HasAnyTypeGenericArgInMethodSignature(method, protocolContext))
            return GateResult.Skipped(SkipReason.AnyTypeFallback,
                "Method return type or parameter contains AnyType as a generic type argument.",
                UnresolvedAppleTypes.DescribeSuffix(
                    method, _typeDatabase, (method.ModuleDecl ?? moduleDecl)?.Name));

        // Unsupported module references (types registered in type database are allowed through).
        // No scalar carve-out in protocol context — a projected requirement still dispatches
        // through the witness/proxy path, which transports the resilient struct.
        foreach (var arg in method.CSSignature)
        {
            var kind = ValidationRuleSet.ClassifyUnsupportedReference(
                arg.SwiftTypeSpec, _typeDatabase, out var offending);
            if (kind != ValidationRuleSet.UnsupportedReferenceKind.None)
                return GateResult.Skipped(ValidationRuleSet.ToSkipReason(kind),
                    kind == ValidationRuleSet.UnsupportedReferenceKind.NetUnavailable
                        ? $"Method signature references .NET-unavailable type '{offending}'."
                        : "Method signature references unsupported module (SwiftUI/Combine).");
        }

        // Pattern 2 emission-time gate — method signature reaches a name in
        // ModuleDecl.InternalTypeNames. Mirrors S6 in EvaluateSubscript and the
        // concrete-side gate in MemberValidationPipeline.ValidateMethodEmission.
        // Protocol method emission bypasses MemberValidationPipeline, so this gate
        // must live here too — otherwise the interface would declare a method whose
        // conforming-class counterpart was suppressed (CS0535).
        var resolvedMethodModule = method.ModuleDecl ?? moduleDecl;
        var methodInternalTypeNames = resolvedMethodModule?.InternalTypeNames;
        if (methodInternalTypeNames is { Count: > 0 } &&
            !string.IsNullOrEmpty(resolvedMethodModule!.Name))
        {
            var effective = MemberValidationPipeline.ExcludeParentTypeNamesForWrapperFreeMethod(
                methodInternalTypeNames, method);
            if (effective.Count > 0 &&
                InternalTypeReferenceWalker.SignatureReachesInternalType(
                    method, effective, resolvedMethodModule.Name))
            {
                return GateResult.Skipped(SkipReason.Pattern2InternalTypeReach,
                    "Method signature reaches a @usableFromInline internal (or otherwise-suppressed) type.");
            }
        }

        // If soft gates fired but no hard gate, return InterfaceOnly
        if (softFlags != SoftGateFlags.None)
            return GateResult.SoftSkip(softFlags);

        return GateResult.Pass;
    }

    /// <summary>
    /// Full evaluation for a protocol subscript. Checks hard gates only (no soft gates for subscripts).
    /// </summary>
    public GateResult EvaluateSubscript(SubscriptDecl subscript, ModuleDecl? moduleDecl, ProtocolDecl? protocolContext)
    {
        // Gate 0: a declaration a previous attempt threw on is refused before any other gate
        // runs. Protocol members reach here without passing through MemberValidationPipeline,
        // so this is their only denial point.
        if (EmitterFaultGate.IsDenied(DeclIdFactory.ForSubscript(subscript), out var poisonDetails))
            return GateResult.Skipped(SkipReason.EmitterFault, poisonDetails);

        var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);
        var resolvedModuleDecl = subscript.ModuleDecl ?? moduleDecl;

        // S1: An accessor shape no C# indexer can take (async/throwing accessor, opaque element…).
        // Same definition the concrete-side planner uses, so the interface never declares an
        // indexer its conformers are refused.
        if (SubscriptHandler.HasUnemittableAccessorShape(subscript, out var accessorShapeDetails))
            return GateResult.Skipped(SkipReason.UnsupportedSignature, accessorShapeDetails);

        // S2: Leaked associated type reference (e.g., TElement.Element)
        if (MemberEmissionValidator.ContainsAssociatedTypeReference(subscript.ReturnTypeSpec) ||
            subscript.IndexParameters.Any(p => MemberEmissionValidator.ContainsAssociatedTypeReference(p.SwiftTypeSpec)))
            return GateResult.Skipped(SkipReason.UnsupportedSignature, "Subscript type contains unresolvable associated type reference.");

        // S3: Bare generic usage
        if (boundGenericsHandler.HasBareGenericUsage(subscript.ReturnTypeSpec, resolvedModuleDecl))
            return GateResult.Skipped(SkipReason.UnsupportedSignature, "Subscript type uses generic type without type arguments.");
        foreach (var param in subscript.IndexParameters)
        {
            if (boundGenericsHandler.HasBareGenericUsage(param.SwiftTypeSpec, resolvedModuleDecl))
                return GateResult.Skipped(SkipReason.UnsupportedSignature, "Subscript type uses generic type without type arguments.");
        }

        // S4: AnyType as generic type argument
        if (protocolContext != null)
        {
            var returnTypeName = ProtocolSignatureHelper.ProjectTypeToCSharp(subscript.ReturnTypeSpec, _typeDatabase, protocolContext, isParameter: false);
            if (ContainsAnyTypeGenericArg(returnTypeName) ||
                TypeDatabaseExtensions.IsBareGenericTypeName(returnTypeName))
                return GateResult.Skipped(SkipReason.AnyTypeFallback,
                    "Subscript type contains AnyType as a generic type argument, which violates generic constraints.",
                    UnresolvedAppleTypes.DescribeSuffix(
                        new[] { subscript.ReturnTypeSpec }, _typeDatabase, (subscript.ModuleDecl ?? moduleDecl)?.Name));

            foreach (var param in subscript.IndexParameters)
            {
                var paramTypeName = ProtocolSignatureHelper.ProjectTypeToCSharp(param.SwiftTypeSpec, _typeDatabase, protocolContext, isParameter: true);
                if (ContainsAnyTypeGenericArg(paramTypeName) ||
                    TypeDatabaseExtensions.IsBareGenericTypeName(paramTypeName))
                    return GateResult.Skipped(SkipReason.AnyTypeFallback,
                        "Subscript type contains AnyType as a generic type argument, which violates generic constraints.",
                        UnresolvedAppleTypes.DescribeSuffix(
                            new[] { param.SwiftTypeSpec }, _typeDatabase, (subscript.ModuleDecl ?? moduleDecl)?.Name));
            }
        }

        // S5: Unsupported module references (types registered in type database are allowed through).
        // No scalar carve-out for subscripts — they marshal through the UTF-8/raw subscript path,
        // which is not taught the LocalizedStringResource conversion.
        foreach (var spec in subscript.IndexParameters.Select(p => p.SwiftTypeSpec).Prepend(subscript.ReturnTypeSpec))
        {
            var kind = ValidationRuleSet.ClassifyUnsupportedReference(spec, _typeDatabase, out var offending);
            if (kind != ValidationRuleSet.UnsupportedReferenceKind.None)
                return GateResult.Skipped(ValidationRuleSet.ToSkipReason(kind),
                    kind == ValidationRuleSet.UnsupportedReferenceKind.NetUnavailable
                        ? $"Subscript signature references .NET-unavailable type '{offending}'."
                        : "Subscript signature references unsupported module (SwiftUI/Combine).");
        }

        // S5b: @objc protocol existential in an unsupported nested position — hard, fail-closed drop, in
        // lockstep with the reverse-dispatch VtableLayoutBuilder.ClassifySubscript. The predicate only fires
        // for nested container/tuple/closure positions (a bare `any P` is fine), which would route the
        // reverse receiver through the 40-byte ExistentialContainer1 carrier against an 8-byte @objc stride
        // (buffer over-read). Drop from interface AND vtable slot together, else vtable size desyncs → SIGSEGV.
        foreach (var spec in subscript.IndexParameters.Select(p => p.SwiftTypeSpec).Prepend(subscript.ReturnTypeSpec))
        {
            if (ExistentialHandler.HasUnsupportedObjCProtocolExistentialPosition(spec, _typeDatabase))
                return GateResult.Skipped(SkipReason.UnsupportedExistential,
                    "Subscript has an @objc protocol existential in an unsupported nested position (container/tuple/closure).");
        }

        // S6: Pattern 2 emission-time gate — subscript signature reaches a name in
        // ModuleDecl.InternalTypeNames. Mirrors EvaluateHardGates / EvaluatePropertyHardGates
        // for the concrete-side; protocol subscripts would land here. Honors the
        // walker's qualified-first / short-name-fallback matching to avoid
        // cross-module name collisions.
        var resolvedSubscriptModule = subscript.ModuleDecl ?? moduleDecl;
        var subscriptInternalTypeNames = resolvedSubscriptModule?.InternalTypeNames;
        if (subscriptInternalTypeNames is { Count: > 0 } &&
            !string.IsNullOrEmpty(resolvedSubscriptModule!.Name) &&
            InternalTypeReferenceWalker.SignatureReachesInternalType(
                subscript, subscriptInternalTypeNames, resolvedSubscriptModule.Name))
        {
            return GateResult.Skipped(SkipReason.Pattern2InternalTypeReach,
                "Subscript signature reaches a @usableFromInline internal (or otherwise-suppressed) type.");
        }

        return GateResult.Pass;
    }

    /// <summary>
    /// Hard-gate-only evaluation for concrete type context. Returns Skip or Emit only.
    /// No soft gates, no InterfaceOnly. Used by MethodHandler and MemberEmissionValidator.
    /// Checks: bare generic, non-ISwiftObject bound generic, unsupported module, internal types.
    /// </summary>
    public GateResult EvaluateHardGates(MethodDecl method, ModuleDecl? moduleDecl)
    {
        // Gate 0: a declaration a previous attempt threw on is refused before any other gate
        // runs. Protocol members reach here without passing through MemberValidationPipeline,
        // so this is their only denial point.
        if (EmitterFaultGate.IsDenied(DeclIdFactory.ForMethod(method), out var poisonDetails))
            return GateResult.Skipped(SkipReason.EmitterFault, poisonDetails);

        var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);

        // Leaked associated type reference (e.g., TElement, TRowDecoder.ID)
        if (method.CSSignature.Any(arg => MemberEmissionValidator.ContainsAssociatedTypeReference(arg.SwiftTypeSpec)))
            return GateResult.Skipped(SkipReason.UnsupportedSignature, "Method signature contains unresolvable associated type reference.");

        for (int i = 0; i < method.CSSignature.Count; i++)
        {
            var argument = method.CSSignature[i];
            bool isParameterPosition = i != 0;

            // Bare generic usage
            if (boundGenericsHandler.HasBareGenericUsage(argument.SwiftTypeSpec, method.ModuleDecl ?? moduleDecl))
                return GateResult.Skipped(SkipReason.UnsupportedSignature,
                    $"Type '{argument.SwiftTypeSpec}' contains generic declaration used without type arguments.");

            // Non-ISwiftObject bound generic (only check actual bound generics)
            if (boundGenericsHandler.IsBoundGeneric(argument) &&
                boundGenericsHandler.HasNonSwiftObjectGenericArg(argument.SwiftTypeSpec, isParameterPosition))
                return GateResult.Skipped(SkipReason.UnsatisfiedGenericConstraint,
                    "Bound generic contains type argument that cannot satisfy C# ISwiftObject constraint.");
        }

        // Unsupported module references (types registered in type database are allowed through).
        // A bare scalar LocalizedStringResource param/return is carved out on the simple concrete
        // wire path (non-async, non-generic, non-generic-parent) so it can project as a string;
        // every other net-unavailable / SwiftUI-Combine reference still drops.
        bool allowScalar = MarshallingHelpers.AllowsProjectableScalarCarveOut(method);
        foreach (var arg in method.CSSignature)
        {
            var kind = ValidationRuleSet.ClassifyUnsupportedReference(
                arg.SwiftTypeSpec, _typeDatabase, out var offending, allowProjectableScalar: allowScalar);
            if (kind != ValidationRuleSet.UnsupportedReferenceKind.None)
                return GateResult.Skipped(ValidationRuleSet.ToSkipReason(kind),
                    kind == ValidationRuleSet.UnsupportedReferenceKind.NetUnavailable
                        ? $"Method signature references .NET-unavailable type '{offending}' in '{arg.SwiftTypeSpec}'."
                        : $"Method signature references unsupported module (SwiftUI/Combine) in '{arg.SwiftTypeSpec}'.");
        }

        // Internal type references — parameter/return types that are internal to the module
        // can't be used in Swift wrapper code (the wrapper imports the module's public API only).
        var resolvedModuleName = method.ModuleDecl?.Name ?? moduleDecl?.Name;
        if (resolvedModuleName != null)
        {
            foreach (var argument in method.CSSignature)
            {
                if (ReferencesInternalModuleType(argument.SwiftTypeSpec, _typeDatabase, resolvedModuleName))
                    return GateResult.Skipped(SkipReason.ModuleInternal,
                        $"Method signature references internal type in '{argument.SwiftTypeSpec}'.");
            }
        }

        // Raw generic type parameters (τ_0_0, τ_0_1, etc.) in a concrete type context
        // indicate method-level generics that can't be expressed in Swift wrapper code.
        // This catches cases where the method isn't marked IsGeneric but still has raw
        // generic params leaked into the signature (e.g., from ABI JSON parsing).
        if (WrapperValidation.HasRawGenericTypeParams(method))
            return GateResult.Skipped(SkipReason.UnsupportedSignature,
                "Method signature contains raw generic type parameters (τ_0_0) that cannot be expressed in wrapper code.");

        return GateResult.Pass;
    }

    /// <summary>
    /// Hard-gate-only evaluation for a concrete-type property. Returns Skip or Emit only.
    /// Checks: bare generic, non-ISwiftObject bound generic, unsupported module, internal types.
    /// </summary>
    public GateResult EvaluatePropertyHardGates(PropertyDecl property, ModuleDecl? moduleDecl)
    {
        // Gate 0: a declaration a previous attempt threw on is refused before any other gate
        // runs. Protocol members reach here without passing through MemberValidationPipeline,
        // so this is their only denial point.
        if (EmitterFaultGate.IsDenied(DeclIdFactory.ForProperty(property), out var poisonDetails))
            return GateResult.Skipped(SkipReason.EmitterFault, poisonDetails);

        var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);

        // Leaked associated type reference (e.g., TElement, TRowDecoder.ID)
        if (MemberEmissionValidator.ContainsAssociatedTypeReference(property.SwiftTypeSpec))
            return GateResult.Skipped(SkipReason.UnsupportedSignature, "Property type contains unresolvable associated type reference.");

        // Bare generic usage
        if (boundGenericsHandler.HasBareGenericUsage(property.SwiftTypeSpec, property.ModuleDecl ?? moduleDecl))
            return GateResult.Skipped(SkipReason.UnsupportedSignature, "Property type uses generic type without type arguments.");

        // Unsupported module references (types registered in type database are allowed through)
        var hardPropKind = ValidationRuleSet.ClassifyUnsupportedReference(
            property.SwiftTypeSpec, _typeDatabase, out var hardPropOffending);
        if (hardPropKind != ValidationRuleSet.UnsupportedReferenceKind.None)
            return GateResult.Skipped(ValidationRuleSet.ToSkipReason(hardPropKind),
                hardPropKind == ValidationRuleSet.UnsupportedReferenceKind.NetUnavailable
                    ? $"Property type references .NET-unavailable type '{hardPropOffending}'."
                    : "Property type references unsupported module (SwiftUI/Combine).");

        // Non-ISwiftObject bound generic
        if (property.SwiftTypeSpec is NamedTypeSpec propNamedType &&
            propNamedType.ContainsGenericParameters &&
            boundGenericsHandler.HasNonSwiftObjectGenericArg(property.SwiftTypeSpec))
            return GateResult.Skipped(SkipReason.UnsatisfiedGenericConstraint, "Bound generic contains type argument that cannot satisfy C# ISwiftObject constraint.");

        // Internal type references — property types that are internal to the module
        // can't be used in Swift wrapper code.
        var resolvedModuleName = property.ModuleDecl?.Name ?? moduleDecl?.Name;
        if (resolvedModuleName != null &&
            ReferencesInternalModuleType(property.SwiftTypeSpec, _typeDatabase, resolvedModuleName))
            return GateResult.Skipped(SkipReason.ModuleInternal,
                $"Property type references internal type in '{property.SwiftTypeSpec}'.");

        // Optional existential whose inner protocol is not in the TypeDatabase falls
        // back to "object" — we can't faithfully marshal SwiftOptional<ExistentialContainer>
        // into a meaningful C# nullable type. Mirrors the same gate in EvaluateProperty
        // so concrete classes and the protocol interface agree on skipping (otherwise
        // CS0535: interface emits the property as object?, conforming class skips it).
        var existentialHandler = new ExistentialHandler(_typeDatabase);
        if (existentialHandler.IsOptionalExistential(property.SwiftTypeSpec))
        {
            var innerProtocolList = existentialHandler.UnwrapOptionalExistential(property.SwiftTypeSpec);
            if (innerProtocolList != null &&
                existentialHandler.GetPublicExistentialType(innerProtocolList) == "object")
            {
                return GateResult.Skipped(SkipReason.AnyTypeFallback,
                    "Optional existential inner protocol not in TypeDatabase — falls back to object.");
            }
        }

        return GateResult.Pass;
    }

    /// <summary>
    /// Checks if a resolved C# type name contains AnyType as a generic type argument.
    /// Delegates to <see cref="ValidationRuleSet.ContainsAnyTypeGenericArg"/> as the canonical implementation.
    /// </summary>
    public static bool ContainsAnyTypeGenericArg(string csharpTypeName)
        => ValidationRuleSet.ContainsAnyTypeGenericArg(csharpTypeName);

    /// <summary>
    /// Checks if a method signature contains bare generic usage in return type or parameters.
    /// </summary>
    private static bool HasBareGenericInMethodSignature(MethodDecl method, ModuleDecl? moduleDecl, BoundGenericsHandler boundGenericsHandler)
    {
        var resolvedModuleDecl = method.ModuleDecl ?? moduleDecl;

        if (method.CSSignature.Count > 0)
        {
            var returnArg = method.CSSignature[0];
            if (returnArg.SwiftTypeSpec is not TupleTypeSpec tuple || !tuple.IsEmptyTuple)
            {
                if (boundGenericsHandler.HasBareGenericUsage(returnArg.SwiftTypeSpec, resolvedModuleDecl))
                    return true;
            }
        }

        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            if (boundGenericsHandler.HasBareGenericUsage(method.CSSignature[i].SwiftTypeSpec, resolvedModuleDecl))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a method's resolved C# signature contains AnyType as a generic type argument.
    /// Uses ProtocolSignatureHelper.ProjectTypeToCSharp for TSelf-aware projection.
    /// </summary>
    private bool HasAnyTypeGenericArgInMethodSignature(MethodDecl method, ProtocolDecl protocolContext)
    {
        if (method.CSSignature.Count > 0)
        {
            var returnArg = method.CSSignature[0];
            if (returnArg.SwiftTypeSpec is not TupleTypeSpec tuple || !tuple.IsEmptyTuple)
            {
                var projected = ProtocolSignatureHelper.ProjectTypeToCSharp(returnArg.SwiftTypeSpec, _typeDatabase, protocolContext);
                if (ContainsAnyTypeGenericArg(projected))
                    return true;
            }
        }

        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var projected = ProtocolSignatureHelper.ProjectTypeToCSharp(method.CSSignature[i].SwiftTypeSpec, _typeDatabase, protocolContext, isParameter: true);
            if (ContainsAnyTypeGenericArg(projected))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Recursively checks if a TypeSpec references a type that is internal to the given module.
    /// Delegates to <see cref="ValidationRuleSet.ReferencesInternalModuleType"/> as the canonical implementation.
    /// </summary>
    internal static bool ReferencesInternalModuleType(TypeSpec? typeSpec, ITypeDatabase typeDatabase, string moduleName)
        => ValidationRuleSet.ReferencesInternalModuleType(typeSpec, typeDatabase, moduleName);
}
