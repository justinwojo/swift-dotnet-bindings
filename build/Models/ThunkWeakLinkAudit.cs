// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// This file is link-compiled into projects that do not enable nullable reference types, so the
// annotations below are opted into locally rather than inherited from a csproj.
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>One bound symbol from <c>dyld_info -imports</c>, for one architecture slice.</summary>
public sealed record DyldImport(string Arch, string Symbol, string Library, bool IsWeak);

/// <summary>One undefined (externally resolved) symbol of an assembled object, from <c>nm -m</c>.</summary>
public sealed record ObjectReference(string Symbol, bool IsWeak);

/// <summary>
/// A fixture floored above every deployment target, named by a fragment of its mangled symbols — a
/// whole type (<c>15FutureGatedTypeV</c>) or one member. <see cref="ReachedThroughThunk"/> marks the
/// fixtures the generator binds through a native thunk; the rest reach the wrapper from Swift code.
/// </summary>
public sealed record FutureFlooredFixture(string MangledFragment, bool ReachedThroughThunk);

/// <summary>
/// The pure verdict behind the thunk weak-link gate.
///
/// <para><b>Why it matters.</b> A thunk names its targets in hand-written assembly, outside swiftc,
/// so it gets none of swiftc's availability-driven weak linking. A strong import of a symbol newer
/// than the wrapper's deployment target makes dyld refuse to load the wrapper — and the app that
/// embeds it — on an older OS, whether or not the member is ever called.</para>
///
/// <para><b>Two levels, because the linker merges.</b> The generator declares every thunk target
/// <c>.weak_reference</c>, and the linker keeps an import strong when any other reference to it is
/// strong — so in the linked wrapper a thunk target the Swift code also uses unconditionally is,
/// correctly, still strong. The directive is therefore checked where it is the only reference: in
/// the assembled thunk object, every undefined symbol must be weak. What the end user depends on is
/// checked in the linked wrapper: every import of a fixture floored above the deployment target must
/// be weak there, whichever reference it came from, and the fixtures bound through thunks must still
/// arrive through one so the check keeps covering the thunk path.</para>
///
/// <para><b>Self-contained by design.</b> BCL-only, so the unit-test project link-compiles it and
/// tests <see cref="Evaluate"/> directly; the Nuke side only runs the tools and throws.</para>
/// </summary>
public static class ThunkWeakLinkAudit
{
    static readonly Regex BranchTarget = new(
        @"^\s+(?:b|bl|jmp|callq)\s+(\S+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    // `<path> [arm64]:` — one section per architecture slice in a (possibly fat) binary.
    static readonly Regex ArchHeader = new(@"\[([^\]\s]+)\]:\s*$", RegexOptions.CultureInvariant);

    // `      0x0008  _os_release [weak-import] (from libSystem)`
    static readonly Regex ImportLine = new(
        @"^\s*0x[0-9A-Fa-f]+\s+(\S+)\s+(\[weak-import\]\s+)?\(from ([^)]+)\)\s*$", RegexOptions.CultureInvariant);

    // `                 (undefined) weak external _$s3Lib1fyyF`
    static readonly Regex UndefinedLine = new(
        @"^\s*\(undefined\)\s+(weak\s+)?external\s+(\S+)\s*$", RegexOptions.CultureInvariant);

    /// <summary>Every symbol a thunk assembly file branches or calls to.</summary>
    public static IReadOnlySet<string> ParseBranchTargets(string assembly) =>
        BranchTarget.Matches(assembly).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>Parses <c>nm -m</c> output for an object file into its undefined symbols.</summary>
    public static IReadOnlyList<ObjectReference> ParseUndefinedSymbols(string nmOutput) =>
        nmOutput.Split('\n')
            .Select(l => UndefinedLine.Match(l.TrimEnd('\r')))
            .Where(m => m.Success)
            .Select(m => new ObjectReference(m.Groups[2].Value, m.Groups[1].Success))
            .ToList();

    /// <summary>Parses <c>dyld_info -imports</c> output into one row per (slice, import).</summary>
    public static IReadOnlyList<DyldImport> ParseImports(string dyldInfoOutput)
    {
        var imports = new List<DyldImport>();
        string arch = "";
        foreach (var raw in dyldInfoOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var header = ArchHeader.Match(line);
            if (header.Success && !line.StartsWith(" ", StringComparison.Ordinal))
            {
                arch = header.Groups[1].Value;
                continue;
            }
            var import = ImportLine.Match(line);
            if (import.Success)
                imports.Add(new DyldImport(arch, import.Groups[1].Value, import.Groups[3].Value.Trim(), import.Groups[2].Success));
        }
        return imports;
    }

    /// <summary>
    /// Judges one wrapper. <paramref name="objectReferences"/> are the undefined symbols of the
    /// assembled thunk objects (every architecture the generator emitted); <paramref name="imports"/>
    /// are the linked wrapper's imports. Every <paramref name="futureFlooredFixtures"/> entry must be
    /// imported (so the check is not vacuous) and imported only weakly; the ones marked
    /// <see cref="FutureFlooredFixture.ReachedThroughThunk"/> must also arrive through a thunk.
    /// </summary>
    public static ThunkWeakLinkVerdict Evaluate(
        string leg,
        IReadOnlySet<string> thunkTargets,
        IReadOnlyList<ObjectReference> objectReferences,
        IReadOnlyList<DyldImport> imports,
        IReadOnlyList<FutureFlooredFixture> futureFlooredFixtures)
    {
        var failures = new List<string>();

        if (thunkTargets.Count == 0)
            failures.Add($"{leg}: no thunk branch targets parsed from the assembly — the thunk files are missing or the parser no longer matches the emitter.");
        if (objectReferences.Count == 0)
            failures.Add($"{leg}: the assembled thunk objects reference no undefined symbols — the objects are missing or the parser no longer matches `nm -m`.");
        if (imports.Count == 0)
            failures.Add($"{leg}: `dyld_info -imports` listed no imports — the wrapper binary is missing or the parser no longer matches the tool's output.");

        // Level 1 — the directive, where it is the only reference.
        var strongInObject = objectReferences.Where(r => !r.IsWeak).Select(r => r.Symbol)
            .Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        foreach (var symbol in strongInObject)
            failures.Add($"{leg}: the thunk object references {symbol} STRONGLY; every thunk target must be declared `.weak_reference`.");
        if (objectReferences.Count > 0)
        {
            var referenced = objectReferences.Select(r => r.Symbol).ToHashSet(StringComparer.Ordinal);
            foreach (var symbol in thunkTargets.Where(t => !referenced.Contains(t)).OrderBy(s => s, StringComparer.Ordinal))
                failures.Add($"{leg}: thunk target {symbol} is not an undefined reference of the assembled thunk objects — the objects are stale or incomplete.");
        }

        // Level 2 — what dyld sees in the linked wrapper.
        var importedTargets = imports.Where(i => thunkTargets.Contains(i.Symbol)).ToList();
        if (thunkTargets.Count > 0 && imports.Count > 0 && importedTargets.Count == 0)
            failures.Add($"{leg}: no thunk target is imported by the wrapper, so the linked-binary check would pass vacuously.");

        foreach (var (token, reachedThroughThunk) in futureFlooredFixtures)
        {
            var matched = imports.Where(i => i.Symbol.Contains(token, StringComparison.Ordinal)).ToList();
            if (matched.Count == 0)
            {
                failures.Add($"{leg}: no import matches the future-floored fixture `{token}` — the fixture no longer reaches the wrapper.");
                continue;
            }
            if (reachedThroughThunk && !matched.Any(i => thunkTargets.Contains(i.Symbol)))
                failures.Add($"{leg}: the future-floored fixture `{token}` is no longer reached through a native thunk, so it no longer covers the thunk path.");
            foreach (var i in matched.Where(i => !i.IsWeak))
                failures.Add($"{leg}: future-floored {i.Symbol} is a STRONG import from {i.Library} [{i.Arch}]; dyld would refuse to load the wrapper on an OS without it.");
        }

        return new ThunkWeakLinkVerdict(
            leg,
            thunkTargets.Count,
            objectReferences.Where(r => r.IsWeak).Select(r => r.Symbol).Distinct(StringComparer.Ordinal).Count(),
            importedTargets.Select(i => i.Symbol).Distinct(StringComparer.Ordinal).Count(),
            importedTargets.Where(i => !i.IsWeak).Select(i => i.Symbol).Distinct(StringComparer.Ordinal).Count(),
            failures);
    }
}

/// <summary>
/// The outcome of <see cref="ThunkWeakLinkAudit.Evaluate"/> for one wrapper.
/// <see cref="StrongImportedThunkTargetCount"/> is informational: a thunk target the Swift code also
/// references unconditionally is correctly strong in the linked wrapper.
/// </summary>
public sealed record ThunkWeakLinkVerdict(
    string Leg,
    int ThunkTargetCount,
    int WeakObjectReferenceCount,
    int ImportedThunkTargetCount,
    int StrongImportedThunkTargetCount,
    IReadOnlyList<string> Failures)
{
    public bool Passed => Failures.Count == 0;
}
