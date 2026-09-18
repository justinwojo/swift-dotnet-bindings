// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.Demangling;

namespace BindingsGeneration;

/// <summary>
/// One requirement for which Swift's generic calling convention passes a protocol witness table:
/// a conformance of a generic parameter itself (<c>τ_0_0 : Swift.Sequence</c>) to a Swift protocol.
/// </summary>
internal readonly record struct WitnessRequirement(long Depth, long Index, string ProtocolModuleQualifiedName)
{
    internal string GenericParameter => $"τ_{Depth}_{Index}";
}

/// <summary>
/// Reads the witness-table-bearing requirements of a generic Swift function from its mangled name.
///
/// <para>The parsed generic signature cannot answer this. It spells a protocol conformance and a
/// superclass bound the same way (<c>τ_0_0 : X</c>), and only the first takes a witness table, so a
/// constraint whose target the type database does not know is ambiguous there. The mangling is
/// not: a protocol requirement and a base-class requirement are distinct operators, and the
/// demangler keeps the difference as a protocol node versus a type node.</para>
///
/// <para>Only requirements on a generic parameter itself count. A conformance of a dependent
/// member (<c>τ_0_0.Element : Hashable</c>) is reached through the parameter's own witness table
/// and adds no argument, an Objective-C protocol (context <c>__C</c>) has no witness table, and a
/// marker protocol (<c>Swift.Sendable</c> and friends) is spelled in the signature but erased at
/// runtime.</para>
/// </summary>
internal static class GenericWitnessRequirements
{
    /// <summary>
    /// The witness-bearing requirements in <paramref name="mangledName"/>'s generic signature, or
    /// null when the symbol does not demangle, in which case the caller cannot tell and must not
    /// assume there are none.
    /// </summary>
    internal static IReadOnlyList<WitnessRequirement>? Read(string? mangledName)
        => ReadSignature(mangledName)?.Requirements;

    /// <summary>
    /// The witness-bearing requirements on the generic parameters a member declares itself, or null
    /// when the symbol does not demangle.
    ///
    /// <para>A member of a generic type is mangled against its context, so its own parameters are
    /// the deepest ones the symbol mentions: <c>G&lt;X&gt;.m&lt;Y&gt;</c> spells <c>Y</c> as
    /// <c>τ_1_0</c>. Requirements on shallower parameters belong to an enclosing context, whose
    /// witnesses reach the callee through that context's metadata rather than as arguments counted
    /// against the member's own generic parameters. Only meaningful for a member that declares
    /// generic parameters of its own.</para>
    /// </summary>
    internal static IReadOnlyList<WitnessRequirement>? ReadOwn(string? mangledName)
    {
        var signature = ReadSignature(mangledName);
        if (signature is null)
            return null;
        var (requirements, deepest) = signature.Value;
        return requirements.Where(r => r.Depth == deepest).ToList();
    }

    private static (IReadOnlyList<WitnessRequirement> Requirements, long Deepest)? ReadSignature(string? mangledName)
    {
        if (string.IsNullOrEmpty(mangledName))
            return null;

        Demangling.Node? root;
        try
        {
            root = new Swift5Demangler().DemangleSymbol(mangledName);
        }
        catch (Exception)
        {
            return null;
        }
        if (root is null)
            return null;

        var requirements = new List<WitnessRequirement>();
        if (!Collect(root, requirements))
            return null;
        return (requirements, DeepestParameter(root));
    }

    private static long DeepestParameter(Demangling.Node node)
    {
        long deepest = -1;
        if (node.Kind == NodeKind.DependentGenericParamType && node.Children.Count == 2 && node.Children[0].HasIndex)
            deepest = node.Children[0].Index;
        foreach (var child in node.Children)
            deepest = Math.Max(deepest, DeepestParameter(child));
        return deepest;
    }

    private static bool Collect(Demangling.Node node, List<WitnessRequirement> requirements)
    {
        if (node.Kind == NodeKind.DependentGenericConformanceRequirement)
        {
            if (node.Children.Count != 2)
                return false;

            var subject = Unwrap(node.Children[0]);
            var target = Unwrap(node.Children[1]);
            if (target.Kind != NodeKind.Protocol)
                return true; // base-class bound: metadata only
            if (subject.Kind != NodeKind.DependentGenericParamType)
                return true; // dependent member: reached through its root's witness table

            var protocolName = QualifiedName(target);
            if (protocolName is null)
                return false;
            if (protocolName.StartsWith("__C.", StringComparison.Ordinal))
                return true; // Objective-C protocol: no witness table
            if (TypeDatabaseExtensions.IsStdlibMarkerProtocol(protocolName))
                return true; // marker protocol: spelled in the signature, erased at runtime

            if (subject.Children.Count != 2 || !subject.Children[0].HasIndex || !subject.Children[1].HasIndex)
                return false;
            requirements.Add(new WitnessRequirement(subject.Children[0].Index, subject.Children[1].Index, protocolName));
            return true;
        }

        foreach (var child in node.Children)
        {
            if (!Collect(child, requirements))
                return false;
        }
        return true;
    }

    private static Demangling.Node Unwrap(Demangling.Node node) =>
        node.Kind == NodeKind.Type && node.Children.Count == 1 ? node.Children[0] : node;

    private static string? QualifiedName(Demangling.Node node)
    {
        switch (node.Kind)
        {
            case NodeKind.Module:
            case NodeKind.Identifier:
                return node.HasText ? node.Text : null;
            case NodeKind.Type:
                return node.Children.Count == 1 ? QualifiedName(node.Children[0]) : null;
            default:
                if (node.Children.Count != 2)
                    return null;
                var context = QualifiedName(node.Children[0]);
                var name = QualifiedName(node.Children[1]);
                return context is null || name is null ? null : $"{context}.{name}";
        }
    }
}
