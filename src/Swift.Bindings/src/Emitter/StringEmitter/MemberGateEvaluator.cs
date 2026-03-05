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

    public bool IsSkipped => Disposition == GateDisposition.Skip;
    public bool IsEmittable => Disposition != GateDisposition.Skip;
    public bool IsInterfaceOnly => Disposition == GateDisposition.InterfaceOnly;

    public static readonly GateResult Pass = new() { Disposition = GateDisposition.Emit };

    public static GateResult Skipped(SkipReason reason, string details) =>
        new() { Disposition = GateDisposition.Skip, Reason = reason, Details = details };

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
                return GateResult.Skipped(SkipReason.AnyTypeFallback, "Property type contains AnyType as a generic type argument, which violates generic constraints.");
        }

        // P5: Unsupported module references (types registered in type database are allowed through)
        if (MemberEmissionValidator.ReferencesUnsupportedModule(property.SwiftTypeSpec, _typeDatabase))
            return GateResult.Skipped(SkipReason.SwiftUIConstraint, "Property type references unsupported module (SwiftUI/Combine).");

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

        return GateResult.Pass;
    }

    /// <summary>
    /// Full evaluation for a protocol method. Checks hard gates and soft gates
    /// (closure/existential → InterfaceOnly with accumulate pattern).
    /// </summary>
    public GateResult EvaluateMethod(MethodDecl method, ModuleDecl? moduleDecl, ProtocolDecl? protocolContext)
    {
        var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);
        var softFlags = SoftGateFlags.None;

        // M4: Leaked associated type reference (e.g., TElement, TRowDecoder.ID)
        if (method.CSSignature.Any(arg => MemberEmissionValidator.ContainsAssociatedTypeReference(arg.SwiftTypeSpec)))
            return GateResult.Skipped(SkipReason.UnsupportedSignature, "Method signature contains unresolvable associated type reference.");

        // M5: Non-ISwiftObject bound generic args
        bool hasNonSwiftObjectArg = method.CSSignature.Any(arg =>
            boundGenericsHandler.IsBoundGeneric(arg) &&
            boundGenericsHandler.HasNonSwiftObjectGenericArg(arg.SwiftTypeSpec));
        if (hasNonSwiftObjectArg)
            return GateResult.Skipped(SkipReason.UnsatisfiedGenericConstraint, "Bound generic contains type argument that cannot satisfy C# ISwiftObject constraint.");

        // M6: Bare generic usage in method signature
        if (HasBareGenericInMethodSignature(method, moduleDecl, boundGenericsHandler))
            return GateResult.Skipped(SkipReason.UnsupportedSignature, "Method signature uses generic type without type arguments.");

        // M7: Existential parameter (soft gate — accumulate)
        if (protocolContext != null)
        {
            var existentialHandler = new ExistentialHandler(_typeDatabase);
            bool hasExistentialParam = method.CSSignature.Skip(1).Any(arg =>
                existentialHandler.IsExistential(arg.SwiftTypeSpec) ||
                existentialHandler.IsOptionalExistential(arg.SwiftTypeSpec));
            if (hasExistentialParam)
                softFlags |= SoftGateFlags.HasExistentialParam;
        }

        // M8: Closure parameter (soft gate — accumulate)
        if (protocolContext != null)
        {
            var closureHandler = new ClosureHandler(_typeDatabase);
            bool hasClosureParam = method.CSSignature.Skip(1).Any(arg =>
                closureHandler.IsClosure(arg));
            if (hasClosureParam)
                softFlags |= SoftGateFlags.HasClosureParam;
        }

        // M9: AnyType as generic type argument (uses projection for TSelf-awareness)
        if (protocolContext != null && HasAnyTypeGenericArgInMethodSignature(method, protocolContext))
            return GateResult.Skipped(SkipReason.AnyTypeFallback, "Method return type or parameter contains AnyType as a generic type argument.");

        // M10: Unsupported module references (types registered in type database are allowed through)
        bool hasUnsupportedModuleRef = method.CSSignature.Any(arg =>
            MemberEmissionValidator.ReferencesUnsupportedModule(arg.SwiftTypeSpec, _typeDatabase));
        if (hasUnsupportedModuleRef)
            return GateResult.Skipped(SkipReason.SwiftUIConstraint, "Method signature references unsupported module (SwiftUI/Combine).");

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
        var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);
        var resolvedModuleDecl = subscript.ModuleDecl ?? moduleDecl;

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
                return GateResult.Skipped(SkipReason.AnyTypeFallback, "Subscript type contains AnyType as a generic type argument, which violates generic constraints.");

            foreach (var param in subscript.IndexParameters)
            {
                var paramTypeName = ProtocolSignatureHelper.ProjectTypeToCSharp(param.SwiftTypeSpec, _typeDatabase, protocolContext, isParameter: true);
                if (ContainsAnyTypeGenericArg(paramTypeName) ||
                    TypeDatabaseExtensions.IsBareGenericTypeName(paramTypeName))
                    return GateResult.Skipped(SkipReason.AnyTypeFallback, "Subscript type contains AnyType as a generic type argument, which violates generic constraints.");
            }
        }

        // S5: Unsupported module references (types registered in type database are allowed through)
        if (MemberEmissionValidator.ReferencesUnsupportedModule(subscript.ReturnTypeSpec, _typeDatabase) ||
            subscript.IndexParameters.Any(p => MemberEmissionValidator.ReferencesUnsupportedModule(p.SwiftTypeSpec, _typeDatabase)))
            return GateResult.Skipped(SkipReason.SwiftUIConstraint, "Subscript signature references unsupported module (SwiftUI/Combine).");

        return GateResult.Pass;
    }

    /// <summary>
    /// Hard-gate-only evaluation for concrete type context. Returns Skip or Emit only.
    /// No soft gates, no InterfaceOnly. Used by MethodHandler and MemberEmissionValidator.
    /// Checks: bare generic, non-ISwiftObject bound generic, unsupported module.
    /// </summary>
    public GateResult EvaluateHardGates(MethodDecl method, ModuleDecl? moduleDecl)
    {
        var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);

        // Leaked associated type reference (e.g., TElement, TRowDecoder.ID)
        if (method.CSSignature.Any(arg => MemberEmissionValidator.ContainsAssociatedTypeReference(arg.SwiftTypeSpec)))
            return GateResult.Skipped(SkipReason.UnsupportedSignature, "Method signature contains unresolvable associated type reference.");

        foreach (var argument in method.CSSignature)
        {
            // Bare generic usage
            if (boundGenericsHandler.HasBareGenericUsage(argument.SwiftTypeSpec, method.ModuleDecl ?? moduleDecl))
                return GateResult.Skipped(SkipReason.UnsupportedSignature,
                    $"Type '{argument.SwiftTypeSpec}' contains generic declaration used without type arguments.");

            // Non-ISwiftObject bound generic (only check actual bound generics)
            if (boundGenericsHandler.IsBoundGeneric(argument) &&
                boundGenericsHandler.HasNonSwiftObjectGenericArg(argument.SwiftTypeSpec))
                return GateResult.Skipped(SkipReason.UnsatisfiedGenericConstraint,
                    "Bound generic contains type argument that cannot satisfy C# ISwiftObject constraint.");
        }

        // Unsupported module references (types registered in type database are allowed through)
        bool hasUnsupportedModuleRef = method.CSSignature.Any(arg =>
            MemberEmissionValidator.ReferencesUnsupportedModule(arg.SwiftTypeSpec, _typeDatabase));
        if (hasUnsupportedModuleRef)
        {
            var unsupportedArg = method.CSSignature.First(arg =>
                MemberEmissionValidator.ReferencesUnsupportedModule(arg.SwiftTypeSpec, _typeDatabase));
            return GateResult.Skipped(SkipReason.SwiftUIConstraint,
                $"Method signature references unsupported module (SwiftUI/Combine) in '{unsupportedArg.SwiftTypeSpec}'.");
        }

        return GateResult.Pass;
    }

    /// <summary>
    /// Hard-gate-only evaluation for a concrete-type property. Returns Skip or Emit only.
    /// Checks: bare generic, non-ISwiftObject bound generic, unsupported module.
    /// </summary>
    public GateResult EvaluatePropertyHardGates(PropertyDecl property, ModuleDecl? moduleDecl)
    {
        var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);

        // Leaked associated type reference (e.g., TElement, TRowDecoder.ID)
        if (MemberEmissionValidator.ContainsAssociatedTypeReference(property.SwiftTypeSpec))
            return GateResult.Skipped(SkipReason.UnsupportedSignature, "Property type contains unresolvable associated type reference.");

        // Bare generic usage
        if (boundGenericsHandler.HasBareGenericUsage(property.SwiftTypeSpec, property.ModuleDecl ?? moduleDecl))
            return GateResult.Skipped(SkipReason.UnsupportedSignature, "Property type uses generic type without type arguments.");

        // Unsupported module references (types registered in type database are allowed through)
        if (MemberEmissionValidator.ReferencesUnsupportedModule(property.SwiftTypeSpec, _typeDatabase))
            return GateResult.Skipped(SkipReason.SwiftUIConstraint, "Property type references unsupported module (SwiftUI/Combine).");

        // Non-ISwiftObject bound generic
        if (property.SwiftTypeSpec is NamedTypeSpec propNamedType &&
            propNamedType.ContainsGenericParameters &&
            boundGenericsHandler.HasNonSwiftObjectGenericArg(property.SwiftTypeSpec))
            return GateResult.Skipped(SkipReason.UnsatisfiedGenericConstraint, "Bound generic contains type argument that cannot satisfy C# ISwiftObject constraint.");

        return GateResult.Pass;
    }

    /// <summary>
    /// Checks if a resolved C# type name contains AnyType as a generic type argument
    /// (inside angle brackets). Plain AnyType as a standalone type is NOT flagged —
    /// it's degraded but compilable. Only AnyType within a bound generic (e.g.,
    /// BatchedCollection&lt;Swift.AnyType&gt;) is problematic because it violates
    /// generic constraints.
    /// </summary>
    public static bool ContainsAnyTypeGenericArg(string csharpTypeName)
    {
        int angleBracketStart = csharpTypeName.IndexOf('<');
        if (angleBracketStart < 0) return false;
        var genericPart = csharpTypeName.Substring(angleBracketStart);
        // Token-aware match: ensure "AnyType" is a standalone type identifier,
        // not part of a larger name (e.g., reject "MyAnyTypeModel")
        int idx = 0;
        while ((idx = genericPart.IndexOf("AnyType", idx, StringComparison.Ordinal)) >= 0)
        {
            bool startOk = idx == 0 || !IsIdentifierChar(genericPart[idx - 1]);
            int end = idx + "AnyType".Length;
            bool endOk = end >= genericPart.Length || !IsIdentifierChar(genericPart[end]);
            if (startOk && endOk) return true;
            idx++;
        }
        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

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
}
