// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// The Swift-side facts of every symbol a module's declarations name, keyed by mangled symbol.
/// Built once per emission from the parser's model of the module and of its dependency modules,
/// before any member is emitted, so an emitter asks for the facts of the symbol it is about to
/// bind instead of reconstructing them from the strings it renders.
/// </summary>
/// <remarks>
/// The table answers for three families of symbol: a declared member (function, method,
/// initializer, accessor, operator), the dispatch thunk (<c>…Tj</c>) of a class member or protocol
/// requirement, and the metadata accessor (<c>…Ma</c>) of a declared type or of a standard-library
/// generic type the generator binds. A symbol the generator synthesizes into its own wrapper has no
/// declaration here; its producer supplies its facts.
/// </remarks>
public sealed class SwiftCallFactsTable
{
    /// <summary>A table that knows no symbol.</summary>
    public static readonly SwiftCallFactsTable Empty = new(
        new Dictionary<string, SwiftCallFacts>(StringComparer.Ordinal), ImmutableArray<string>.Empty,
        new SwiftCallFactsBuilder(Array.Empty<ModuleDecl>(), typeDatabase: null));

    private readonly Dictionary<string, SwiftCallFacts> _bySymbol;
    private readonly SwiftCallFactsBuilder _builder;

    private SwiftCallFactsTable(Dictionary<string, SwiftCallFacts> bySymbol, ImmutableArray<string> conflicts, SwiftCallFactsBuilder builder)
    {
        _bySymbol = bySymbol;
        Conflicts = conflicts;
        _builder = builder;
    }

    /// <summary>
    /// The facts of a function the generator synthesized into its wrapper, from the declaration its
    /// producer wrote. Its types resolve against the same modules and databases as the declared
    /// symbols', so a synthesized function and the member it wraps describe a type the same way.
    /// </summary>
    public SwiftCallFacts Describe(SyntheticSwiftFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return _builder.ForSynthetic(function);
    }

    /// <summary>
    /// The facts of a call through a Swift closure's function pointer, from the closure's function
    /// type, resolved against the same modules and databases as the declared symbols'.
    /// </summary>
    public SwiftCallFacts DescribeClosure(ClosureTypeSpec closure, string pointerSource, IEnumerable<string> genericNames, string? module)
    {
        ArgumentNullException.ThrowIfNull(closure);
        return _builder.ForClosure(closure, pointerSource, genericNames, module);
    }

    /// <summary>The number of symbols with facts, dispatch thunks excluded (they are derived on lookup).</summary>
    public int Count => _bySymbol.Count;

    /// <summary>Every recorded facts entry, in symbol order.</summary>
    public IEnumerable<SwiftCallFacts> All => _bySymbol.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value);

    /// <summary>
    /// Symbols two declarations claimed with different facts. The first declaration's facts are
    /// kept; a conflict is reported, never resolved by guessing.
    /// </summary>
    public ImmutableArray<string> Conflicts { get; }

    /// <summary>Looks up the facts of the symbol a transition binds.</summary>
    public bool TryGet(string symbol, [NotNullWhen(true)] out SwiftCallFacts? facts)
    {
        if (_bySymbol.TryGetValue(symbol, out facts))
            return true;

        // A dispatch thunk takes exactly the implementation's arguments; it dispatches through the
        // class vtable or the protocol witness table. Only a member that has one answers.
        if (symbol.EndsWith("Tj", StringComparison.Ordinal)
            && _bySymbol.TryGetValue(symbol[..^2], out var implementation)
            && implementation.Target.HasDispatchThunk)
        {
            facts = implementation with
            {
                Target = implementation.Target with { Symbol = symbol, Entry = SwiftEntryKind.DispatchThunk },
            };
            return true;
        }

        facts = null;
        return false;
    }

    /// <summary>
    /// Builds the table for a module. Dependency modules the type database knows are walked too,
    /// so a transition that binds a dependency's symbol still finds its facts.
    /// </summary>
    public static SwiftCallFactsTable Build(ModuleDecl module, ITypeDatabase typeDatabase)
    {
        var modules = new List<ModuleDecl> { module };
        modules.AddRange(typeDatabase.GetDependencyModuleDecls().Where(d => !ReferenceEquals(d, module) && d.Name != module.Name));

        var builder = new SwiftCallFactsBuilder(modules, typeDatabase);
        var bySymbol = new Dictionary<string, SwiftCallFacts>(StringComparer.Ordinal);
        var conflicts = new List<string>();

        void Add(SwiftCallFacts facts)
        {
            var symbol = facts.Target.Symbol;
            if (string.IsNullOrEmpty(symbol))
                return;
            if (bySymbol.TryGetValue(symbol, out var existing))
            {
                if (existing != facts)
                    conflicts.Add(symbol);
                return;
            }
            bySymbol.Add(symbol, facts);
        }

        foreach (var m in modules)
        {
            foreach (var facts in builder.BuildModule(m))
                Add(facts);
        }
        foreach (var facts in SwiftCallFactsBuilder.StandardLibraryMetadataAccessors())
            Add(facts);

        return new SwiftCallFactsTable(bySymbol, conflicts.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray(), builder);
    }
}
