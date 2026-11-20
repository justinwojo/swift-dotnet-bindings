// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// The one rule for whether two readings of a member's types, one on each side (the interface text and
/// the ABI, or the interface and what its reader extracted), name the same types. Readings that agree
/// module-qualified match. Readings that agree only once module qualifiers are dropped match only when
/// that unqualified reading is unique on both sides: the ABI names a type moved by
/// <c>@_originallyDefinedIn</c> by the module that first defined it where the interface names its
/// current one, which is the same type, but two overloads whose types differ only by module
/// (<c>Swift.Int</c> and a local type named <c>Int</c>) are two members, each with its own row.
/// </summary>
internal static class ModuleQualifiedKeys
{
    /// <summary><paramref name="key"/> with the module dropped from each qualified type name.</summary>
    internal static string Unqualified(string key) => ProtocolExtensionEmitter.WithoutModuleQualifiers(key);

    /// <summary>
    /// Whether <paramref name="mine"/>, one of the keys of <paramref name="mySide"/>, and
    /// <paramref name="theirs"/>, one of the keys of <paramref name="theirSide"/>, name the same types.
    /// </summary>
    internal static bool Match(string mine, IEnumerable<string> mySide, string theirs, IEnumerable<string> theirSide)
    {
        if (string.Equals(mine, theirs, StringComparison.Ordinal))
            return true;
        var unqualified = Unqualified(mine);
        return string.Equals(unqualified, Unqualified(theirs), StringComparison.Ordinal)
            && UniquelyReads(unqualified, mySide.Append(mine))
            && UniquelyReads(unqualified, theirSide.Append(theirs));
    }

    /// <summary>
    /// Whether the one candidate whose reading agrees with <paramref name="mine"/> only unqualified is
    /// unambiguous: exactly one of <paramref name="theirs"/> agrees, and no other of
    /// <paramref name="myRivals"/> agrees with it. For a pairing that takes members from a pool, where
    /// each side is what is still unpaired.
    /// </summary>
    internal static bool UniqueOnBothSides<TMine, TTheirs>(
        IReadOnlyCollection<TTheirs> theirs, IEnumerable<TMine> myRivals, Func<TMine, TTheirs, bool> agreeUnqualified)
        => theirs.Count == 1 && !myRivals.Any(rival => agreeUnqualified(rival, theirs.First()));

    private static bool UniquelyReads(string unqualified, IEnumerable<string> side)
        => side.Where(k => string.Equals(Unqualified(k), unqualified, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Count() == 1;
}
