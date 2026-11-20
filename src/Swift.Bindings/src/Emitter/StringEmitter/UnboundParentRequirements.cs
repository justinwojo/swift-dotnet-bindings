// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Gives a module's protocol the requirements it inherits from a protocol of an SDK module the run
/// never loads (LiveCommunicationKit's <c>LiveAssistanceExtensionConfiguration :
/// ExtensionFoundation.AppExtensionConfiguration</c>). No walk sees such a parent: it is neither in
/// this module nor in a bound dependency's database, so the carrier's conformance to the child would
/// lack the parent's witnesses, and the wrapper compile would withdraw it. This pre-pass reads the
/// parent's requirements without a default from its SDK interface
/// (<see cref="ForeignProtocolRequirements.TypedParentOf"/>) and adds them to the child as its own
/// protocol requirements, so every later walk (the C# interface, the vtable layout, the receivers and
/// the carrier's conformance extension) treats them as the child's, through the one slot layout
/// (<see cref="VtableLayoutBuilder"/>). The carrier's witness in the child's extension satisfies the
/// parent's requirement too.
///
/// <para>
/// All or nothing per parent: a parent whose requirements do not all read with concrete types, whose
/// types do not all project, one of whose requirements carries its own availability, or which more
/// than one protocol of the module refines (the carrier's one conformance to the parent would share a
/// single witness between them) gets nothing added, and its child's conformance stays with the
/// verify-recover loop (withdrawn with its report row). A requirement the child declares, or defaults
/// in an unconstrained extension of its own, with the same labels and types is not added, nor one the
/// module implements in an extension of the parent itself (unconstrained, or constrained to the
/// child alone). One
/// unbound level only.
/// </para>
///
/// <para>
/// An added requirement has no symbol of its own in this module: its <see cref="MethodDecl.MangledName"/>
/// is a synthetic name (<see cref="SyntheticSymbolPrefix"/>) used only to identify the declaration. The
/// carrier reaches it through the witness accessors, never by that name;
/// <see cref="WrapperSymbolIntegrityGate"/> fails the generation if a P/Invoke ever names it.
/// </para>
/// </summary>
internal static class UnboundParentRequirements
{
    /// <summary>The prefix of an added requirement's synthetic <see cref="MethodDecl.MangledName"/>.</summary>
    internal const string SyntheticSymbolPrefix = "SBUNBOUND_";

    /// <summary>
    /// Modules whose protocols other arms of the carrier's conformance logic decide (trivial markers,
    /// stubs, the ObjC-rooted carrier), and which this pre-pass therefore leaves alone.
    /// </summary>
    private static readonly HashSet<string> SystemModules = new(StringComparer.Ordinal)
    {
        "Swift", "_Concurrency", "Foundation", "ObjectiveC", "_StringProcessing", "Observation",
    };

    /// <summary>
    /// Adds to each protocol of <paramref name="moduleDecl"/> the requirements without a default of
    /// every protocol it refines from an unbound SDK module, and returns what it added.
    /// </summary>
    internal static IReadOnlyList<MethodDecl> Inject(ModuleDecl moduleDecl, ITypeDatabase typeDatabase, ILogger logger,
        IReadOnlyList<ExtensionMemberCandidate>? extensionMembers = null)
    {
        extensionMembers ??= Array.Empty<ExtensionMemberCandidate>();
        var added = new List<MethodDecl>();
        // The carrier witnesses a parent's requirement once for every child: Swift's conformance of
        // EveryProtocol to the parent is one, so a witness written in one child's extension also
        // answers for every other child. A parent more than one protocol of the module refines is
        // left to the wrapper compile.
        var refiners = moduleDecl.Protocols
            .SelectMany(p => UnboundParents(moduleDecl, p))
            .GroupBy(name => name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var protocol in moduleDecl.Protocols)
        {
            if (protocol.IsObjC || protocol.IsMarker)
                continue;
            foreach (var parentName in UnboundParents(moduleDecl, protocol))
            {
                if (refiners[parentName] > 1)
                {
                    logger.LogDebug(
                        "Protocol {Protocol}: {Parent} is refined by {Count} protocols of the module, which would share one witness; its conformance is left to the wrapper compile",
                        protocol.Name, parentName, refiners[parentName]);
                    continue;
                }
                if (ForeignProtocolRequirements.TypedParentOf(parentName) is not { } parent)
                {
                    logger.LogDebug(
                        "Protocol {Protocol}: the requirements of {Parent} do not all read from its SDK interface; its conformance is left to the wrapper compile",
                        protocol.Name, parentName);
                    continue;
                }
                // A requirement introduced after its parent needs its own availability on every
                // walk that reaches it, which an added requirement does not carry.
                if (parent.Requirements.FirstOrDefault(r => r.Attributes.Any(IsAvailability)) is { } floored)
                {
                    logger.LogDebug(
                        "Protocol {Protocol}: requirement {Requirement} of {Parent} carries its own availability; its conformance is left to the wrapper compile",
                        protocol.Name, floored.Key, parent.QualifiedName);
                    continue;
                }
                if (parent.Requirements.SelectMany(TypesOf).FirstOrDefault(type => !Projects(type, typeDatabase)) is { } unprojected)
                {
                    logger.LogDebug(
                        "Protocol {Protocol}: a requirement of {Parent} takes or returns {Type}, which has no projection; its conformance is left to the wrapper compile",
                        protocol.Name, parent.QualifiedName, unprojected);
                    continue;
                }
                foreach (var requirement in parent.Requirements)
                {
                    if (protocol.Methods.Any(m => !m.IsAccessor && Declares(m, requirement)))
                        continue;
                    if (ChildDefaults(protocol, requirement))
                        continue;
                    if (ModuleDefaultsOnParent(protocol, parent, requirement, extensionMembers))
                        continue;
                    var method = Build(moduleDecl, protocol, parent, requirement);
                    protocol.Methods.Add(method);
                    added.Add(method);
                }
            }
        }
        return added;
    }

    /// <summary>
    /// The qualified names of the protocols <paramref name="protocol"/> refines from modules this run
    /// neither generates nor loads as a dependency, from both channels that carry an inheritance: the
    /// parsed conformances and the generic signature's <c>Self</c> clauses.
    /// </summary>
    internal static IEnumerable<string> UnboundParents(ModuleDecl moduleDecl, ProtocolDecl protocol)
    {
        var names = protocol.InheritedProtocols.Select(p => p.Name);
        if (!string.IsNullOrEmpty(protocol.GenericSignature))
            names = names.Concat(GenericSignatureParser.ParseSignature(protocol.GenericSignature).DirectConformanceTargets("τ_0_0", "Self"));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var dot = name.IndexOf('.');
            if (dot <= 0 || name.Contains('<') || name.Contains('&'))
                continue;
            var module = name[..dot];
            if (string.Equals(module, moduleDecl.Name, StringComparison.Ordinal)
                || moduleDecl.DependencyProtocols.ContainsKey(module)
                || SystemModules.Contains(module)
                || AppleFrameworkRegistry.IsAutoBridgeModule(module)
                || AppleFrameworkRegistry.IsOptionalFallbackModule(module)
                || name[(dot + 1)..].StartsWith('_'))
                continue;
            if (seen.Add(name))
                yield return name;
        }
    }

    /// <summary>
    /// Whether an unconstrained extension of <paramref name="protocol"/> in its own module implements
    /// the requirement keyed <paramref name="key"/>: every conformer then has a witness, so the
    /// requirement is not the child's to add. An extension constrained beyond the child itself (or a
    /// generic method of its own) does not default it for every conformer and is not counted.
    /// </summary>
    private static bool ChildDefaults(ProtocolDecl protocol, ForeignProtocolRequirements.TypedRequirement requirement)
    {
        var self = protocol.SwiftTypeName.ModuleQualifiedName;
        return protocol.ExtensionMembers.OfType<MethodDecl>().Any(m =>
            !m.IsConstructor && m.MethodType == MethodType.Instance && !m.IsModuleInternal
            && Declares(m, requirement)
            && m.ParsedGenericSignature is var signature
            && signature.Parameters.All(p => p == "τ_0_0")
            && signature.Requirements.All(r => r.Kind == GenericRequirementKind.Conformance && r.IsDirect
                && r.SubjectRoot == "τ_0_0" && string.Equals(r.Target, self, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Whether the module being bound implements the requirement in an extension of the parent
    /// itself (<c>extension ExtensionFoundation.AppExtensionConfiguration { public func accept… }</c>),
    /// unconstrained or constrained to the child alone: every conformer of the child, the carrier
    /// included, then has Swift's witness, so the requirement is not the child's to add (adding it
    /// would make a C# member required and route the call through managed dispatch instead). Only
    /// an instance method with the requirement's labels and types counts; a member whose signature
    /// does not read is compared by its labels, as the SDK interface's defaults are.
    /// </summary>
    private static bool ModuleDefaultsOnParent(
        ProtocolDecl protocol,
        ForeignProtocolRequirements.TypedParent parent,
        ForeignProtocolRequirements.TypedRequirement requirement,
        IReadOnlyList<ExtensionMemberCandidate> extensionMembers)
    {
        foreach (var member in extensionMembers)
        {
            if (member.IsStatic || member.IsProperty
                || !string.Equals(StripGenericArguments(member.ExtendedTypeName), parent.QualifiedName, StringComparison.Ordinal)
                || !member.WhereConstraints.All(c => ConstrainsSelfToChildAlone(c, protocol))
                || !string.Equals(member.PrintedName + (ProtocolExtensionEmitter.IsAsyncSignature(member.RawSignature) ? " async" : string.Empty),
                    requirement.Key, StringComparison.Ordinal))
                continue;
            var signature = ProtocolExtensionEmitter.ParseFunctionSignature(
                member.RawSignature, member.MethodName, returnsSelf: false, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            if (signature is not { } parsed)
                return true;
            if (parsed.parameters.Count == requirement.Parameters.Count
                && SameType(parsed.returnTypeSpec ?? TupleTypeSpec.Empty, requirement.ReturnType)
                && parsed.parameters.Select(p => p.typeSpec).Zip(requirement.Parameters).All(pair => SameType(pair.First, pair.Second.Type)))
                return true;
        }
        return false;
    }

    private static string StripGenericArguments(string name)
    {
        var angle = name.IndexOf('<');
        return (angle < 0 ? name : name[..angle]).Trim();
    }

    // `Self : Module.Child` (or the child's bare name): the constraint every conformer of the child meets.
    private static bool ConstrainsSelfToChildAlone(string constraint, ProtocolDecl protocol)
    {
        var colon = constraint.IndexOf(':');
        if (colon < 0 || constraint[..colon].Trim() != "Self")
            return false;
        var target = constraint[(colon + 1)..].Trim();
        return string.Equals(target, protocol.SwiftTypeName.ModuleQualifiedName, StringComparison.Ordinal)
            || string.Equals(target, protocol.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="method"/> has <paramref name="requirement"/>'s name, labels, parameter
    /// types and return type: a same-label overload of other types neither declares nor defaults it.
    /// </summary>
    private static bool Declares(MethodDecl method, ForeignProtocolRequirements.TypedRequirement requirement)
    {
        if (!string.Equals(ProtocolExtensionEmitter.RequirementKeyOf(method), requirement.Key, StringComparison.Ordinal)
            || method.CSSignature.Count != requirement.Parameters.Count + 1)
            return false;
        if (!SameType(method.CSSignature[0].SwiftTypeSpec, requirement.ReturnType))
            return false;
        for (var i = 0; i < requirement.Parameters.Count; i++)
        {
            if (!SameType(method.CSSignature[i + 1].SwiftTypeSpec, requirement.Parameters[i].Type))
                return false;
        }
        return true;
    }

    private static bool SameType(TypeSpec? declared, TypeSpec required)
        => IsVoid(declared) || IsVoid(required)
            ? IsVoid(declared) && IsVoid(required)
            : string.Equals(declared!.ToString(), required.ToString(), StringComparison.Ordinal);

    private static bool IsVoid(TypeSpec? type)
        => type is null or TupleTypeSpec { IsEmptyTuple: true }
            || type.ToString() is "()" or "Void" or "Swift.Void";

    private static bool IsAvailability(string attribute)
        => attribute.StartsWith("@available", StringComparison.Ordinal);

    private static IEnumerable<TypeSpec> TypesOf(ForeignProtocolRequirements.TypedRequirement requirement)
        => requirement.Parameters.Select(p => p.Type).Append(requirement.ReturnType);

    /// <summary>
    /// Whether a requirement's type projects to C#: <c>Void</c>, or a named type (and its generic
    /// arguments) the type database resolves to a record other than an absent Apple projection.
    /// Tuples and function types are not taken here.
    /// </summary>
    private static bool Projects(TypeSpec type, ITypeDatabase typeDatabase) => type switch
    {
        TupleTypeSpec { IsEmptyTuple: true } => true,
        NamedTypeSpec named => typeDatabase.TryGetTypeRecord(named, out var record)
            && (record.Flags & TypeRecordFlags.AbsentAppleProjection) == 0
            && named.GenericParameters.All(argument => Projects(argument, typeDatabase)),
        _ => false,
    };

    private static MethodDecl Build(
        ModuleDecl moduleDecl,
        ProtocolDecl protocol,
        ForeignProtocolRequirements.TypedParent parent,
        ForeignProtocolRequirements.TypedRequirement requirement)
    {
        var method = new MethodDecl
        {
            Name = requirement.Name,
            MangledName = SyntheticSymbol(protocol, parent.QualifiedName, requirement.Key),
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            Throws = requirement.Throws,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = protocol,
            ModuleDecl = moduleDecl,
            IsProtocolRequirement = true,
            IsNonisolated = requirement.IsNonisolated,
            IsMainActorIsolated = requirement.IsMainActorIsolated && !requirement.IsNonisolated,
            IsActorIsolated = requirement.IsMainActorIsolated && !requirement.IsNonisolated,
            InheritedFromUnboundProtocol = parent.QualifiedName,
        };
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = string.Empty,
            PrivateName = string.Empty,
            SwiftTypeSpec = requirement.ReturnType,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = method,
            ModuleDecl = moduleDecl,
        });
        foreach (var parameter in requirement.Parameters)
        {
            method.CSSignature.Add(new ArgumentDecl
            {
                Name = parameter.Label,
                PrivateName = parameter.PrivateName,
                SwiftTypeSpec = parameter.Type,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = method,
                ModuleDecl = moduleDecl,
            });
        }
        return method;
    }

    /// <summary>
    /// A deterministic identifier-safe name for an added requirement: the prefix, the child, the parent
    /// and the requirement key, each character outside <c>[A-Za-z0-9_]</c> written as <c>_</c>.
    /// </summary>
    internal static string SyntheticSymbol(ProtocolDecl protocol, string parentQualifiedName, string requirementKey)
    {
        var text = $"{protocol.SwiftTypeName.ModuleQualifiedName}_{parentQualifiedName}_{requirementKey}";
        var builder = new StringBuilder(SyntheticSymbolPrefix, SyntheticSymbolPrefix.Length + text.Length);
        foreach (var c in text)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        return builder.ToString();
    }
}
