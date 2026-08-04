// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Resolution and persistence for <see cref="TypeRecord.ObjCEnumCaseNames"/> — the
/// <c>raw ObjC case name → emitted C# member name</c> map an NS_ENUM record carries so that a
/// Swift-side reference to one of its cases can name the member the ObjC companion actually declared.
///
/// <para>
/// The problem this exists to solve: the C# companion strips a shared prefix off every case (the
/// enum's own type name, or the module's acronym tag), while Swift's Objective-C importer strips a
/// prefix it derives independently — the common word prefix of the enum name and its cases. The two
/// agree often and diverge silently: an enum <c>SourceKind</c> whose cases read <c>MLNMapTiler</c>
/// imports into Swift as <c>mlnMapTiler</c> (Swift finds no common prefix to strip) while the
/// companion declares <c>MapTiler</c> (the module tag stripped). PascalCasing the Swift spelling then
/// names a member that was never declared. Rather than re-implement Swift's importer, the record
/// carries the emitted names and references resolve against them.
/// </para>
/// </summary>
public static class ObjCEnumCaseNames
{
    private const char PairSeparator = ';';
    private const char NameSeparator = '=';

    /// <summary>
    /// Encodes the map for the module-database <c>objcEnumCases</c> attribute as
    /// <c>Raw=Emitted;Raw=Emitted</c>. Returns null when there is nothing to persist.
    /// </summary>
    public static string? Encode(IReadOnlyDictionary<string, string>? caseNames)
    {
        if (caseNames is not { Count: > 0 })
            return null;
        return string.Join(
            PairSeparator, caseNames.Select(kv => $"{kv.Key}{NameSeparator}{kv.Value}"));
    }

    /// <summary>
    /// Decodes the <c>objcEnumCases</c> attribute written by <see cref="Encode"/>. Malformed or empty
    /// input yields null so an older module database (which has no such attribute) loads unchanged.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Decode(string? attributeValue)
    {
        if (string.IsNullOrEmpty(attributeValue))
            return null;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in attributeValue.Split(PairSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf(NameSeparator);
            if (split <= 0 || split == pair.Length - 1)
                continue;
            map[pair[..split]] = pair[(split + 1)..];
        }

        return map.Count == 0 ? null : map;
    }

    /// <summary>
    /// Resolves the C# member name for the case <paramref name="swiftCaseSpelling"/> names, where that
    /// spelling is however the Swift side wrote it (an ABI-JSON default-value expression, e.g.
    /// <c>mapTiler</c> or <c>mlnMapTiler</c>).
    ///
    /// <para>
    /// Both the companion's strip and Swift's importer only ever remove a PREFIX, so the Swift
    /// spelling is always a case-insensitive tail of the raw ObjC case name. That is what the last
    /// rule keys on, anchored at a PascalCase word boundary so <c>Map</c> cannot resolve to
    /// <c>OUBitmap</c>, and required to be unambiguous across the case set. Earlier rules take the
    /// direct hits first, so an enum whose spelling already matches never depends on the tail rule.
    /// </para>
    /// </summary>
    public static bool TryResolveEmittedName(
        IReadOnlyDictionary<string, string>? caseNames, string swiftCaseSpelling, out string emittedName)
    {
        emittedName = string.Empty;
        if (caseNames is not { Count: > 0 } || string.IsNullOrEmpty(swiftCaseSpelling))
            return false;

        foreach (var (_, emitted) in caseNames)
        {
            if (string.Equals(emitted, swiftCaseSpelling, StringComparison.Ordinal))
            {
                emittedName = emitted;
                return true;
            }
        }

        foreach (var (raw, emitted) in caseNames)
        {
            if (string.Equals(emitted, swiftCaseSpelling, StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, swiftCaseSpelling, StringComparison.OrdinalIgnoreCase))
            {
                emittedName = emitted;
                return true;
            }
        }

        string? tailMatch = null;
        foreach (var (raw, emitted) in caseNames)
        {
            var start = raw.Length - swiftCaseSpelling.Length;
            // Same token-boundary rule the strip itself uses: a new word starts at an upper-case
            // letter or a digit (`SpeedRate1ips`).
            if (start <= 0 || !(char.IsUpper(raw[start]) || char.IsDigit(raw[start])))
                continue;
            if (string.Compare(raw, start, swiftCaseSpelling, 0, swiftCaseSpelling.Length,
                    StringComparison.OrdinalIgnoreCase) != 0)
                continue;
            if (tailMatch != null && !string.Equals(tailMatch, emitted, StringComparison.Ordinal))
                return false;
            tailMatch = emitted;
        }

        if (tailMatch == null)
            return false;

        emittedName = tailMatch;
        return true;
    }
}
