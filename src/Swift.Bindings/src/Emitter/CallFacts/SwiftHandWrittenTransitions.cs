// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BindingsGeneration;

/// <summary>One hand-written Swift-convention import and the facts of the symbol it binds.</summary>
/// <param name="Method">The C# method name of the import.</param>
public sealed record SwiftHandWrittenTransition(string Method, string EntryPoint, string Library, SwiftCallFacts Facts);

/// <summary>
/// How Swift passes one type, stated by hand for a type the layout resolver does not derive: the
/// outcome, the IR type of each direct piece, and why.
/// </summary>
public sealed record SwiftLoweringOverride(string Type, SwiftLoweringOutcome Outcome, ImmutableArray<string> Pieces, string Reason)
{
    public SwiftLoweringAnswer Answer()
    {
        if (Outcome != SwiftLoweringOutcome.Direct)
            return Outcome == SwiftLoweringOutcome.Indirect ? SwiftLoweringAnswer.IndirectBecause(Reason) : SwiftLoweringAnswer.UnknownBecause(Reason);
        var pieces = ImmutableArray.CreateBuilder<SwiftLoweredPiece>();
        int offset = 0;
        foreach (var ir in Pieces)
        {
            var (size, kind) = ir switch
            {
                "ptr" => (8, SwiftScalarKind.Pointer),
                "i64" => (8, SwiftScalarKind.Int64),
                "i32" => (4, SwiftScalarKind.Int32),
                "i16" => (2, SwiftScalarKind.Int16),
                "i8" => (1, SwiftScalarKind.Int8),
                "i1" => (1, SwiftScalarKind.Bool),
                "float" => (4, SwiftScalarKind.Float),
                "double" => (8, SwiftScalarKind.Double),
                _ => throw new InvalidDataException($"the lowering override of {Type} names the IR type '{ir}', which is not one scalar"),
            };
            offset = SwiftLeafLayout.AlignUp(offset, size);
            pieces.Add(new SwiftLoweredPiece(offset, size, kind));
            offset += size;
        }
        return SwiftLoweringAnswer.DirectLayout(pieces.ToImmutable(), Reason);
    }
}

/// <summary>
/// The facts of an assembly's hand-written Swift-convention imports, which no generator records: the
/// runtime's own and the Apple supplement's hand-written types. Each import is matched to its
/// declaration by method, entry point and library, through the same ledger the generator builds, so
/// a declaration with no entry and an entry with no declaration both show.
/// </summary>
public sealed record SwiftHandWrittenTransitionTable
{
    public const int CurrentSchema = 1;

    public required int Schema { get; init; }

    /// <summary>The assembly whose sources declare the imports.</summary>
    public required string Assembly { get; init; }

    /// <summary>How Swift passes the types the facts name that the resolver does not derive.</summary>
    public ImmutableArray<SwiftLoweringOverride> Lowering { get; init; } = ImmutableArray<SwiftLoweringOverride>.Empty;

    public required ImmutableArray<SwiftHandWrittenTransition> Transitions { get; init; }

    /// <summary>
    /// Where the assembly's bridges are built, when its executable transitions are routed through them;
    /// null for an assembly whose transitions are only planned.
    /// </summary>
    public SwiftHandWrittenBridges? Bridges { get; init; }

    public static SwiftHandWrittenTransitionTable Load(string path)
    {
        var table = JsonSerializer.Deserialize(File.ReadAllText(path), SwiftHandWrittenTransitionJsonContext.Default.SwiftHandWrittenTransitionTable)
            ?? throw new InvalidDataException($"{path} holds no transition table");
        if (table.Schema != CurrentSchema)
            throw new InvalidDataException($"{path} has schema {table.Schema}; this generator reads {CurrentSchema}");
        if (table.Transitions.IsDefault || table.Transitions.Any(t => t.Facts is null))
            throw new InvalidDataException($"{path} lists a transition with no facts");
        if (table.Transitions.Any(t => t.Facts.FactsVersion != SwiftCallFacts.CurrentVersion))
            throw new InvalidDataException($"{path} holds facts of another version; this generator writes {SwiftCallFacts.CurrentVersion}");
        // The serializer sets an array the JSON leaves out to its default, not to the property's
        // initial value: an omitted list means an empty one.
        return table with
        {
            Lowering = table.Lowering.IsDefault ? [] : table.Lowering.Select(o => o.Pieces.IsDefault ? o with { Pieces = [] } : o).ToImmutableArray(),
            Transitions = table.Transitions
                .Select(t => t.Facts.Unknowns.IsDefault ? t with { Facts = t.Facts with { Unknowns = [] } } : t)
                .ToImmutableArray(),
        };
    }

    /// <summary>
    /// The ledger of <paramref name="sources"/> (the assembly's C#) with this table's facts recorded
    /// for the imports they declare.
    /// </summary>
    public SwiftTransitionLedger Ledger(IEnumerable<string> sources) =>
        SwiftTransitionLedger.Build(Assembly, sources, Transitions.Select(t => new SwiftTransitionRecord
        {
            MethodName = t.Method,
            EntryPoint = t.EntryPoint,
            Library = t.Library,
            Facts = t.Facts,
            Source = SwiftCallFactsSource.Declared,
        }));

    /// <summary><paramref name="inner"/>, with this table's overrides answered first.</summary>
    public ISwiftValueLowering LoweringOver(ISwiftValueLowering inner) =>
        new SwiftOverriddenLowering(inner, Lowering.ToDictionary(o => o.Type, o => o.Answer(), StringComparer.Ordinal));
}

/// <summary>
/// How a hand-written assembly routes its executable transitions through bridges, every path relative
/// to the table's own directory. The Swift-convention imports are declared in
/// <see cref="Declarations"/>, a C# file the assembly does not compile; the bridge products are written
/// from it by the same routing a generated binding goes through: <see cref="Managed"/>, the compiled
/// C# (each bridged import as its shim and Cdecl twin, any other import as it was declared), and
/// <see cref="Native"/><c>.{arm64,x86_64}.s</c>, the bridges the assembly's native library links.
/// </summary>
/// <param name="Library">The native library the twins import from: the one that links the bridges.</param>
public sealed record SwiftHandWrittenBridges(string Library, string Declarations, string Managed, string Native)
{
    /// <summary>The bridge assembly file of <paramref name="architecture"/>, relative like <see cref="Native"/>.</summary>
    public string NativeFile(SwiftArchitecture architecture) =>
        $"{Native}.{(architecture == SwiftArchitecture.Arm64 ? "arm64" : "x86_64")}.s";
}

/// <summary>A lowering that answers some types by hand and asks another for the rest.</summary>
public sealed class SwiftOverriddenLowering : ISwiftValueLowering
{
    private readonly ISwiftValueLowering _inner;
    private readonly IReadOnlyDictionary<string, SwiftLoweringAnswer> _overrides;

    public SwiftOverriddenLowering(ISwiftValueLowering inner, IReadOnlyDictionary<string, SwiftLoweringAnswer> overrides)
    {
        _inner = inner;
        _overrides = overrides;
    }

    public SwiftLoweringAnswer Lower(SwiftTypeFacts type) => Lower(type, null, false);

    public SwiftLoweringAnswer LowerResult(SwiftTypeFacts type) => Lower(type, null, true);

    public SwiftLoweringAnswer Lower(SwiftTypeFacts type, SwiftGenericSignatureFacts? generics, bool asResult) =>
        _overrides.TryGetValue(type.Spelling, out var answer) ? answer : _inner.Lower(type, generics, asResult);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(SwiftHandWrittenTransitionTable))]
internal partial class SwiftHandWrittenTransitionJsonContext : JsonSerializerContext
{
}
