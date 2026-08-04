// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// This file is link-compiled into projects that do not enable nullable reference types, so the
// annotations below are opted into locally rather than inherited from a csproj.
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// The de-collision decision records the generator writes into <c>binding-report.json</c>, plus the
/// pure verdict the overload-name ship gate draws from them.
///
/// <para><b>Two lanes, one artifact, opposite policies.</b> <see cref="OverloadRenameRecord"/> is
/// the overload resolver's ledger, and its contract is that no assignment may be the natural name
/// plus digits: an overload name has to come from the member's own Swift argument labels or
/// parameter types, and a family neither can separate is refused outright rather than numbered.
/// <see cref="CaseOnlyRenameRecord"/> is the case-only member arm's ledger, and it assigns exactly
/// what the other lane forbids — two Swift spellings differing only by case (<c>url</c> alongside
/// <c>URL</c>) carry no labels and no parameter types, so there is nothing for a semantic token to
/// say and the later declaration takes a numeric suffix. Reading both from one list would force the
/// gate to either fail on a deliberate decision or stay blind to it; they are separate channels so
/// the gate can hard-fail one and merely REPORT the other.</para>
///
/// <para><b>Self-contained by design.</b> BCL-only (no Nuke, source-generated JSON) so the
/// unit-test project can link-compile and test <see cref="Evaluate"/> directly — the same pattern
/// as <c>ApiManifestBaseline</c> / <c>SkipSurfaceBaseline</c>.</para>
/// </summary>
internal static class OverloadNameLedger
{
    /// <summary>Parses one <c>binding-report.json</c> into the two decision lanes.</summary>
    /// <exception cref="JsonException">The text is not readable JSON.</exception>
    public static OverloadNameLedgerDocument Parse(string json)
        => string.IsNullOrWhiteSpace(json)
            ? new OverloadNameLedgerDocument()
            : JsonSerializer.Deserialize(json, OverloadNameLedgerJsonContext.Default.OverloadNameLedgerDocument)
              ?? new OverloadNameLedgerDocument();

    /// <summary>
    /// Draws the gate's verdict over the decisions collected from every report in a run.
    ///
    /// <para>Three findings, only two of which are failures:</para>
    /// <list type="bullet">
    ///   <item><description>An overload assignment that IS the natural name plus digits — the
    ///   policy violation the gate exists for. Fails.</description></item>
    ///   <item><description>Either lane recording nothing at all. Fails as a positive control: the
    ///   corpus this runs over contains both colliding overloads and case-only sibling members, so
    ///   an empty lane means its records stopped reaching the artifact — a manifest round-trip that
    ///   dropped the section, a resolver that stopped recording — and the gate would otherwise pass
    ///   vacuously in exactly the situation it exists to catch.</description></item>
    ///   <item><description>A case-only assignment that is the natural name plus digits. NOT a
    ///   failure — it is the arm's designed output — but surfaced as its own category so the
    ///   numeric names on the public surface are counted somewhere a reader can see them.</description></item>
    /// </list>
    /// </summary>
    public static OverloadNameLedgerVerdict Evaluate(
        IReadOnlyList<OverloadRenameRecord> overloadRenames,
        IReadOnlyList<CaseOnlyRenameRecord> caseOnlyRenames)
    {
        ArgumentNullException.ThrowIfNull(overloadRenames);
        ArgumentNullException.ThrowIfNull(caseOnlyRenames);

        var failures = new List<string>();

        if (overloadRenames.Count == 0)
            failures.Add(
                "the generated bindings recorded ZERO overload-disambiguation decisions. The " +
                "corpus contains colliding overloads, so an empty ledger means the resolver's " +
                "records are no longer reaching binding-report.json — the gate cannot verify " +
                "anything. Check ReportCollector.RecordOverloadRenamed and the " +
                "GenerationSection.OverloadRenames round-trip.");

        if (caseOnlyRenames.Count == 0)
            failures.Add(
                "the generated bindings recorded ZERO case-only member renames. The corpus " +
                "contains sibling properties whose Swift names differ only by case, and that arm " +
                "assigns numeric names to the public surface — an empty ledger means those " +
                "assignments are invisible again. Check " +
                "ReportCollector.RecordCaseOnlyRenamed and the " +
                "GenerationSection.CaseOnlyRenames round-trip.");

        var numericOverloads = overloadRenames.Where(IsNumericAssignment).ToList();
        if (numericOverloads.Count > 0)
            failures.Add(
                $"{numericOverloads.Count} public member(s) carry a resolver-assigned numeric " +
                "suffix. Overload names must come from Swift argument labels or parameter types; a " +
                "family neither can separate is refused with a report entry, never numbered.");

        return new OverloadNameLedgerVerdict
        {
            NumericOverloadAssignments = numericOverloads,
            CaseOnlyAssignments = caseOnlyRenames,
            NumericCaseOnlyAssignments = caseOnlyRenames.Where(IsNumericAssignment).ToList(),
            OverloadSchemeBreakdown = Breakdown(overloadRenames.Select(r => r.Scheme)),
            CaseOnlySchemeBreakdown = Breakdown(caseOnlyRenames.Select(r => r.Scheme)),
            Failures = failures,
        };
    }

    /// <summary>
    /// A record is a numeric assignment when the emitted name is the natural name followed only by
    /// digits. Both names come from the same record, so a name that merely ENDS in a digit —
    /// <c>Vector3</c>, <c>Utf8</c>, <c>Sha256</c> — cannot trip this: its natural name ends in the
    /// same digits and the two are equal.
    /// </summary>
    public static bool IsNumericAssignment(IRenameRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var natural = record.NaturalName;
        var emitted = record.EmittedName;
        if (string.IsNullOrEmpty(natural) || string.IsNullOrEmpty(emitted))
            return false;
        if (emitted.Length <= natural.Length || !emitted.StartsWith(natural, StringComparison.Ordinal))
            return false;
        return emitted.Skip(natural.Length).All(char.IsAsciiDigit);
    }

    private static string Breakdown(IEnumerable<string?> schemes)
        => string.Join(
            ", ",
            schemes
                .Select(s => string.IsNullOrEmpty(s) ? "Unknown" : s!)
                .GroupBy(s => s, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key}: {g.Count()}"));
}

/// <summary>The two naming facts every decision record carries, whichever lane wrote it.</summary>
internal interface IRenameRecord
{
    /// <summary>The C# name the member would carry uncontested.</summary>
    string? NaturalName { get; }

    /// <summary>The C# name actually emitted.</summary>
    string? EmittedName { get; }
}

/// <summary>Just enough of <c>binding-report.json</c> to read the two decision lanes.</summary>
internal sealed class OverloadNameLedgerDocument
{
    [JsonPropertyName("OverloadRenames")]
    public List<OverloadRenameRecord>? OverloadRenames { get; set; }

    [JsonPropertyName("CaseOnlyRenames")]
    public List<CaseOnlyRenameRecord>? CaseOnlyRenames { get; set; }
}

/// <summary>Mirror of the generator's <c>OverloadRenameItem</c>.</summary>
internal sealed class OverloadRenameRecord : IRenameRecord
{
    public string? DeclaringName { get; set; }
    public string? SwiftSignature { get; set; }
    public string? NaturalName { get; set; }
    public string? EmittedName { get; set; }
    public string? Scheme { get; set; }
}

/// <summary>Mirror of the generator's <c>CaseOnlyRenameItem</c>.</summary>
internal sealed class CaseOnlyRenameRecord : IRenameRecord
{
    public string? DeclaringName { get; set; }
    public string? SwiftName { get; set; }
    public string? NaturalName { get; set; }
    public string? EmittedName { get; set; }
    public string? Scheme { get; set; }
}

/// <summary>The gate's verdict over one run's decision records.</summary>
internal sealed class OverloadNameLedgerVerdict
{
    /// <summary>Overload assignments that are the natural name plus digits — the policy breach.</summary>
    public IReadOnlyList<OverloadRenameRecord> NumericOverloadAssignments { get; init; }
        = Array.Empty<OverloadRenameRecord>();

    /// <summary>Every case-only member rename recorded this run.</summary>
    public IReadOnlyList<CaseOnlyRenameRecord> CaseOnlyAssignments { get; init; }
        = Array.Empty<CaseOnlyRenameRecord>();

    /// <summary>
    /// The subset of <see cref="CaseOnlyAssignments"/> that carries a numeric suffix. Reported, not
    /// failed: the case-only arm has no labels or parameter types to name a member by, so a numeric
    /// suffix is its designed output rather than a resolver giving up.
    /// </summary>
    public IReadOnlyList<CaseOnlyRenameRecord> NumericCaseOnlyAssignments { get; init; }
        = Array.Empty<CaseOnlyRenameRecord>();

    /// <summary>Per-scheme counts over the overload lane, rendered for the gate log.</summary>
    public string OverloadSchemeBreakdown { get; init; } = "";

    /// <summary>Per-scheme counts over the case-only lane, rendered for the gate log.</summary>
    public string CaseOnlySchemeBreakdown { get; init; } = "";

    /// <summary>Every reason the gate must go red. Empty means it passes.</summary>
    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();

    public bool Passed => Failures.Count == 0;
}

/// <summary>Source-generation context — keeps deserialization AOT-safe (no reflection) so the model
/// link-compiles cleanly into the IsAotCompatible unit-test project. Case-insensitive because the
/// report is serialized by the generator's Newtonsoft writer under its own casing convention, and
/// the gate must not break if that convention shifts.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(OverloadNameLedgerDocument))]
internal partial class OverloadNameLedgerJsonContext : JsonSerializerContext
{
}
