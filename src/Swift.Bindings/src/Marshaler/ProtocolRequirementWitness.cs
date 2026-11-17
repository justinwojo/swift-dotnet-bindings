// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;

namespace BindingsGeneration;

/// <summary>
/// Whether a member of a nominal type witnesses a requirement of a protocol the type conforms to.
/// </summary>
/// <remarks>
/// A witness implements the C# interface member implicitly, so its projected type must be the one the
/// interface declares. The interface keeps every existential return at <c>object</c>
/// (<c>ExistentialUnion</c> has no input marshalling, and a C# implementation must be able to produce
/// the value), so a witness may not take the read-only union projection its own return position would
/// otherwise allow: the class member returning <c>ExistentialUnion</c> while the interface asks for
/// <c>object</c> is CS0738, which fails the whole module. The match is by Swift name, which can only
/// keep a non-witness overload at <c>object</c> too; that direction is always sound.
/// </remarks>
internal static class ProtocolRequirementWitness
{
    /// <summary>
    /// True when <paramref name="parent"/> (or, for a class, an ancestor whose conformance it inherits)
    /// conforms to a protocol the generator knows that declares a requirement named
    /// <paramref name="swiftName"/> of the same kind.
    /// </summary>
    public static bool WitnessesConformedRequirement(BaseDecl? parent, string swiftName, bool isProperty)
    {
        if (parent is not TypeDecl typeDecl || typeDecl is ProtocolDecl || string.IsNullOrEmpty(swiftName))
            return false;

        for (TypeDecl? current = typeDecl; current is not null; current = (current as ClassDecl)?.ResolvedSuperclass)
        {
            var conformances = current switch
            {
                ClassDecl c => c.Conformances,
                StructDecl s => s.Conformances,
                EnumDecl e => e.Conformances,
                _ => null,
            };
            if (conformances is null)
                continue;
            foreach (var conformance in conformances)
            {
                var protocol = FindProtocol(current.ModuleDecl ?? typeDecl.ModuleDecl, conformance.Protocol);
                if (protocol is null)
                    continue;
                if (isProperty
                    ? protocol.Properties.Any(p => p.Name == swiftName)
                    : protocol.Methods.Any(m => m.Name == swiftName && !m.IsProtocolExtensionMethod && !m.IsAccessor))
                    return true;
            }
        }
        return false;
    }

    private static ProtocolDecl? FindProtocol(ModuleDecl? moduleDecl, SwiftTypeName protocolName)
    {
        if (moduleDecl is null)
            return null;
        bool Matches(ProtocolDecl p) => p.SwiftTypeName?.Module == protocolName.Module && p.SwiftTypeName?.Name == protocolName.Name;
        return moduleDecl.Protocols.FirstOrDefault(Matches)
            ?? moduleDecl.DependencyProtocols.Values.SelectMany(list => list).FirstOrDefault(Matches);
    }
}
