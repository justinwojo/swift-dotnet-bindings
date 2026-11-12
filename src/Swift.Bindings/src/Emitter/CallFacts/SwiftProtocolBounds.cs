// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Whether a protocol is class-bound, so that its existential is a reference (followed by
/// witness tables) and its <c>Self</c> a single reference.
/// </summary>
internal static class SwiftProtocolBounds
{
    /// <summary>
    /// True when the protocol, or any protocol it refines, is declared <c>: AnyObject</c> or with a
    /// superclass constraint, or is <c>@objc</c>. Class-boundness is inherited: a protocol refining a
    /// class-bound one is class-bound although its own declaration says nothing.
    /// </summary>
    /// <param name="declared">A refined protocol's declaration by module-qualified name, when one is in hand.</param>
    /// <param name="recorded">
    /// Whether a type database records a refined protocol, by module-qualified name, as class-bound;
    /// null when it records no such protocol.
    /// </param>
    public static bool IsClassBound(ProtocolDecl protocol, Func<string, ProtocolDecl?> declared, Func<string, bool?> recorded)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        return IsClassBound(protocol, declared, recorded, new HashSet<string>(StringComparer.Ordinal));
    }

    private static bool IsClassBound(ProtocolDecl protocol, Func<string, ProtocolDecl?> declared, Func<string, bool?> recorded, HashSet<string> visited)
    {
        if (protocol.IsClassBound || protocol.IsObjC)
            return true;
        if (!visited.Add(protocol.SwiftTypeName.ModuleQualifiedName))
            return false;
        foreach (var inherited in protocol.InheritedProtocols)
        {
            var name = SwiftLayoutResolver.FullNameOf(inherited);
            if (name is "AnyObject" or "Swift.AnyObject")
                return true;
            // A refined protocol is named module-qualified; a bare name is the protocol's own module's.
            if (!name.Contains('.'))
                name = protocol.SwiftTypeName.Module + "." + name;
            if (declared(name) is { } refined)
            {
                if (IsClassBound(refined, declared, recorded, visited))
                    return true;
                continue;
            }
            if (recorded(name) == true)
                return true;
        }
        return false;
    }
}
