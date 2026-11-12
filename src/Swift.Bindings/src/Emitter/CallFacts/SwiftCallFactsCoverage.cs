// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>How a Swift-convention transition's facts were found.</summary>
public enum SwiftCallFactsStatus
{
    /// <summary>The callee is declared, and every fact was determined.</summary>
    Complete,

    /// <summary>The callee is declared, but some fact could not be determined; the reasons are listed.</summary>
    Incomplete,

    /// <summary>The callee is a function the generator synthesized into its wrapper; its producer owes the facts.</summary>
    Synthetic,

    /// <summary>No declaration names the callee.</summary>
    Unknown,
}

/// <summary>One emitted Swift-convention transition and the facts found for it.</summary>
public sealed record SwiftCallFactsCoverageEntry(
    string Method,
    string EntryPoint,
    string Library,
    SwiftCallFactsStatus Status,
    string? FactsId,
    ImmutableArray<string> Reasons);

/// <summary>
/// The facts coverage of one module's emitted Swift-convention transitions: every forward
/// <c>CallConvSwift</c> P/Invoke the module emitted, matched to the facts of the symbol it binds.
/// It reads the emission's recorded <see cref="AbiCallPlan"/>s and the facts table, and writes
/// nothing but its own sidecar, so it cannot change what is emitted.
/// </summary>
public sealed class SwiftCallFactsCoverage
{
    /// <summary>The sidecar schema. Bump it on any change to the sidecar's shape.</summary>
    public const int SchemaVersion = 1;

    public required string Module { get; init; }

    public required ImmutableArray<SwiftCallFactsCoverageEntry> Entries { get; init; }

    /// <summary>
    /// The facts the entries cite, and those of every declared symbol whose facts are
    /// incomplete, so each incomplete symbol carries its reasons. One per id.
    /// </summary>
    public required ImmutableArray<SwiftCallFacts> Facts { get; init; }

    public ImmutableArray<string> Conflicts { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>
    /// Every symbol the table has facts for, with whether they are complete. Several emission paths
    /// (metadata accessors, operators, the wrapper's synthesized entry points) do not record a plan,
    /// so their transitions are not among the <see cref="Entries"/>; this index lets a consumer of
    /// the sidecar, such as an IL cross-check, match any transition to the declaration it binds.
    /// Dispatch thunks are not listed: a <c>…Tj</c> symbol has the facts of the symbol without it,
    /// when that member dispatches (see <see cref="SwiftCallFactsTable.TryGet"/>).
    /// </summary>
    public ImmutableSortedDictionary<string, SwiftCallFactsStatus> DeclaredSymbols { get; init; } =
        ImmutableSortedDictionary<string, SwiftCallFactsStatus>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>The number of declared symbols per target kind.</summary>
    public ImmutableSortedDictionary<string, int> DeclaredByTargetKind { get; init; } =
        ImmutableSortedDictionary<string, int>.Empty.WithComparers(StringComparer.Ordinal);

    public int Count(SwiftCallFactsStatus status) => Entries.Count(e => e.Status == status);

    /// <summary>
    /// Classifies every Swift-convention plan. A symbol the table knows gets its facts; a symbol
    /// bound to the generator's own wrapper library that no declaration names is synthetic; anything
    /// else is unknown, with the reason.
    /// </summary>
    public static SwiftCallFactsCoverage Compute(
        string module,
        IEnumerable<AbiCallPlan> plans,
        SwiftCallFactsTable table,
        string? wrapperLibrary)
    {
        var entries = ImmutableArray.CreateBuilder<SwiftCallFactsCoverageEntry>();
        var cited = new SortedDictionary<string, SwiftCallFacts>(StringComparer.Ordinal);

        foreach (var plan in plans.Where(p => p.CallingConvention == PInvokeCallingConvention.Swift))
        {
            if (table.TryGet(plan.EntryPoint, out var facts))
            {
                cited.TryAdd(facts.Id, facts);
                entries.Add(new SwiftCallFactsCoverageEntry(
                    plan.MethodName, plan.EntryPoint, plan.Library,
                    facts.IsComplete ? SwiftCallFactsStatus.Complete : SwiftCallFactsStatus.Incomplete,
                    facts.Id,
                    facts.IsComplete ? ImmutableArray<string>.Empty : facts.Unknowns));
                continue;
            }

            if (IsWrapperLibrary(plan.Library, wrapperLibrary))
            {
                entries.Add(new SwiftCallFactsCoverageEntry(
                    plan.MethodName, plan.EntryPoint, plan.Library, SwiftCallFactsStatus.Synthetic, null,
                    ImmutableArray.Create("synthesized into the wrapper; its producer supplies the facts")));
                continue;
            }

            var reason = plan.EntryPoint.EndsWith("Ma", StringComparison.Ordinal)
                ? $"metadata accessor of a type no parsed module declares and the standard-library table does not list"
                : "no declaration in the module or its dependencies has this symbol";
            entries.Add(new SwiftCallFactsCoverageEntry(
                plan.MethodName, plan.EntryPoint, plan.Library, SwiftCallFactsStatus.Unknown, null,
                ImmutableArray.Create(reason)));
        }

        foreach (var facts in table.All.Where(f => !f.IsComplete))
            cited.TryAdd(facts.Id, facts);

        return new SwiftCallFactsCoverage
        {
            Module = module,
            Entries = entries
                .OrderBy(e => e.EntryPoint, StringComparer.Ordinal)
                .ThenBy(e => e.Method, StringComparer.Ordinal)
                .ThenBy(e => e.Library, StringComparer.Ordinal)
                .ToImmutableArray(),
            Facts = cited.Values.ToImmutableArray(),
            Conflicts = table.Conflicts,
            DeclaredSymbols = table.All.ToImmutableSortedDictionary(
                f => f.Target.Symbol!,
                f => f.IsComplete ? SwiftCallFactsStatus.Complete : SwiftCallFactsStatus.Incomplete,
                StringComparer.Ordinal),
            DeclaredByTargetKind = table.All
                .GroupBy(f => f.Target.Kind)
                .ToImmutableSortedDictionary(g => g.Key.ToString(), g => g.Count(), StringComparer.Ordinal),
        };
    }

    private static bool IsWrapperLibrary(string library, string? wrapperLibrary) =>
        !string.IsNullOrEmpty(wrapperLibrary)
        && (string.Equals(library, wrapperLibrary, StringComparison.Ordinal)
            || library.EndsWith("/" + wrapperLibrary, StringComparison.Ordinal));

    /// <summary>The sidecar's file name for a module namespace.</summary>
    public static string FileName(string @namespace) => $"{@namespace}.swift-call-facts.json";

    /// <summary>The sidecar document.</summary>
    public SwiftCallFactsDocument ToDocument() => new()
    {
        Schema = SchemaVersion,
        FactsVersion = SwiftCallFacts.CurrentVersion,
        Module = Module,
        Totals = new SwiftCallFactsTotals
        {
            SwiftTransitions = Entries.Length,
            Complete = Count(SwiftCallFactsStatus.Complete),
            Incomplete = Count(SwiftCallFactsStatus.Incomplete),
            Synthetic = Count(SwiftCallFactsStatus.Synthetic),
            Unknown = Count(SwiftCallFactsStatus.Unknown),
            FactsByTargetKind = Facts
                .GroupBy(f => f.Target.Kind)
                .ToImmutableSortedDictionary(g => g.Key.ToString(), g => g.Count(), StringComparer.Ordinal),
            DeclaredSymbols = DeclaredSymbols.Count,
            DeclaredIncomplete = DeclaredSymbols.Count(kv => kv.Value != SwiftCallFactsStatus.Complete),
            DeclaredByTargetKind = DeclaredByTargetKind,
        },
        Conflicts = Conflicts,
        Transitions = Entries,
        DeclaredSymbols = DeclaredSymbols,
        Facts = Facts,
    };

    /// <summary>Serializes the coverage, deterministically.</summary>
    public string ToJson() =>
        JsonSerializer.Serialize(ToDocument(), SwiftCallFactsJsonContext.Default.SwiftCallFactsDocument) + "\n";

    /// <summary>
    /// Writes the sidecar next to the module's C#. A diagnostic artifact: a failure to write it is
    /// logged and never fails generation.
    /// </summary>
    public void Write(string outputDirectory, string @namespace, ILogger logger)
    {
        try
        {
            File.WriteAllText(Path.Combine(outputDirectory, FileName(@namespace)), ToJson());
            var incomplete = Count(SwiftCallFactsStatus.Incomplete) + Count(SwiftCallFactsStatus.Unknown);
            if (incomplete > 0)
                logger.LogInformation(
                    "Swift call facts for {Module}: {Incomplete} of {Total} Swift-convention transitions lack complete facts",
                    Module, incomplete, Entries.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            logger.LogWarning("Could not write the Swift call facts sidecar for {Module}: {Message}", Module, ex.Message);
        }
    }
}

/// <summary>The serialized form of <see cref="SwiftCallFactsCoverage"/>.</summary>
public sealed record SwiftCallFactsDocument
{
    public required int Schema { get; init; }
    public required int FactsVersion { get; init; }
    public required string Module { get; init; }
    public required SwiftCallFactsTotals Totals { get; init; }
    public required ImmutableArray<string> Conflicts { get; init; }
    public required ImmutableArray<SwiftCallFactsCoverageEntry> Transitions { get; init; }
    public required ImmutableSortedDictionary<string, SwiftCallFactsStatus> DeclaredSymbols { get; init; }
    public required ImmutableArray<SwiftCallFacts> Facts { get; init; }
}

/// <summary>The sidecar's counts.</summary>
public sealed record SwiftCallFactsTotals
{
    public required int SwiftTransitions { get; init; }
    public required int Complete { get; init; }
    public required int Incomplete { get; init; }
    public required int Synthetic { get; init; }
    public required int Unknown { get; init; }
    public required ImmutableSortedDictionary<string, int> FactsByTargetKind { get; init; }
    public required int DeclaredSymbols { get; init; }
    public required int DeclaredIncomplete { get; init; }
    public required ImmutableSortedDictionary<string, int> DeclaredByTargetKind { get; init; }
}

/// <summary>Source-generated, trim-safe serialization of the facts sidecar.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SwiftCallFactsDocument))]
internal partial class SwiftCallFactsJsonContext : JsonSerializerContext
{
}
