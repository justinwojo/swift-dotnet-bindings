// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;

namespace BindingsGeneration;

/// <summary>
/// Three-valued answer to "does type argument T satisfy protocol/class constraint C?".
/// <list type="bullet">
/// <item><see cref="Yes"/> — a conformance we can prove from a fact source (local decl,
/// class-subtyping chain, the stdlib fact table, a stripped-conformance record, or
/// transitive protocol inheritance).</item>
/// <item><see cref="No"/> — a definitive negative: the type argument is FULLY RESOLVED
/// (a local <see cref="TypeDecl"/>) and declares no matching direct or transitive
/// conformance. Swift never promised the conformance.</item>
/// <item><see cref="Unknown"/> — genuinely unprovable: the type argument is foreign (no
/// local decl) and no fact source covers the pair. The generator cannot verify it either
/// way and must fail closed.</item>
/// </list>
/// Both <see cref="No"/> and <see cref="Unknown"/> mean "do not emit"; the distinction
/// exists so callers and diagnostics can tell a Swift-never-promised drop apart from a
/// can't-prove-it drop.
/// </summary>
public enum ConformanceResult
{
    Yes,
    No,
    Unknown,
}

/// <summary>
/// Single source of truth for concrete-type conformance facts. Consolidates what was a
/// scattered heuristic chain in <see cref="BoundGenericsHandler"/> (self-conformance, the
/// hardcoded stdlib table, stripped foreign conformances, class-subtyping walks, and
/// transitive protocol inheritance) behind one fail-closed <see cref="ConformanceResult"/>
/// query.
///
/// The Swift standard library has no <see cref="TypeDecl"/> in the corpus, so its
/// conformances cannot be recovered from local declarations. Those facts live in the
/// committed, schema-versioned <c>stdlib-conformances.json</c> embedded resource — ONE
/// input to the oracle, not the whole base: foreign cross-module conformances still resolve
/// via the TypeDatabase's stripped-conformance records and transitive refinement.
/// </summary>
public sealed class ConformanceOracle
{
    /// <summary>
    /// Schema version the generator expects the embedded <c>stdlib-conformances.json</c> to
    /// carry. Bump this AND the file's <c>schemaVersion</c> in lockstep on any shape change,
    /// mirroring the <see cref="AppleFrameworkRegistry.ExpectedObjCTypeMappingsSchemaVersion"/>
    /// / SwiftInterfaceParser handshake. A stale embedded file then fails loud at load time
    /// instead of silently mis-answering every stdlib conformance query.
    /// </summary>
    public const int ExpectedStdlibSchemaVersion = 1;

    private readonly ITypeDatabase _typeDatabase;

    private static readonly Lazy<IReadOnlyDictionary<string, HashSet<string>>> s_stdlibFacts =
        new(LoadStdlibFacts);

    public ConformanceOracle(ITypeDatabase typeDatabase)
    {
        _typeDatabase = typeDatabase;

        // Touch the fact table eagerly so the schema-version handshake fires when the first
        // oracle is built (early in the generation pipeline) rather than lazily on the first
        // query deep inside member emission — a stale or missing embedded file fails loud here.
        _ = s_stdlibFacts.Value;
    }

    /// <summary>
    /// Answers whether a CONCRETE type argument satisfies a protocol- or class-bound
    /// constraint. <paramref name="typeArgumentDecl"/> is the type argument's resolved local
    /// declaration (the caller resolves it; <c>null</c> means foreign / not in the corpus).
    /// <paramref name="constraintRecord"/> is the constraint's TypeDatabase record when
    /// known (used to recognise the class-bound case), or <c>null</c>.
    /// </summary>
    public ConformanceResult ConcreteConforms(
        SwiftTypeName typeArgumentName,
        SwiftTypeName protocolConstraint,
        TypeRecord? constraintRecord,
        TypeDecl? typeArgumentDecl,
        ModuleDecl moduleDecl)
    {
        // Class-bound constraint (`<T : SomeClass>`). The parser tags every `:` clause as
        // ConformanceKind.Protocol; the resolved record tells us when the target is actually a
        // class, satisfied by class subtyping rather than protocol conformance. Checked BEFORE
        // decl resolution so XML/database-owned subclasses (no local TypeDecl) are recognised.
        if (constraintRecord != null && constraintRecord.Kind == TypeRecordKind.Class)
        {
            if (typeArgumentName == protocolConstraint)
                return ConformanceResult.Yes;
            if (IsSubclassOfViaTypeDatabase(typeArgumentName, protocolConstraint))
                return ConformanceResult.Yes;
            // Not satisfied by the TypeDatabase chain — fall through to the local-decl walk
            // below (a local class hierarchy may only be populated in moduleDecl).
        }

        if (typeArgumentDecl == null)
        {
            // External protocol type used as its own constraint — self-conformance.
            if (typeArgumentName == protocolConstraint)
                return ConformanceResult.Yes;

            // Swift stdlib facts: the generator has no declarations for stdlib types, but their
            // conformances are recorded in the committed fact table.
            if (HasStdlibConformance(typeArgumentName, protocolConstraint))
                return ConformanceResult.Yes;

            // Stripped foreign conformances: swift-api-digester drops underscore-PAT conformance
            // records (e.g. `Swift.Int : AppIntents._IntentValue`) along with the protocol decl.
            // UnderscoreProtocolSynthesizer re-parses them and registers the foreign pairs here.
            if (_typeDatabase.HasStrippedConformance(typeArgumentName, protocolConstraint))
                return ConformanceResult.Yes;

            // Foreign type, no local decl, no fact covers the pair: genuinely unprovable.
            // Fail closed.
            return ConformanceResult.Unknown;
        }

        // A protocol type used as its own constraint (resolved via decl) — self-conformance.
        if (typeArgumentDecl is ProtocolDecl && typeArgumentName == protocolConstraint)
            return ConformanceResult.Yes;

        // Locally-declared class-bound case: walk the local SuperclassNames chain in addition
        // to the TypeDatabase walk above — not every local class hierarchy round-trips through
        // the TypeDatabase (it is populated lazily for the current module's decls).
        if (constraintRecord != null &&
            constraintRecord.Kind == TypeRecordKind.Class &&
            typeArgumentDecl is ClassDecl typeArgClass)
        {
            var constraintQualifiedName = protocolConstraint.ModuleQualifiedName;
            if (typeArgClass.SuperclassNames.Any(n => n == constraintQualifiedName))
                return ConformanceResult.Yes;
        }

        // Direct conformance declared on the type argument.
        if (HasConformance(typeArgumentDecl, protocolConstraint))
            return ConformanceResult.Yes;

        // Transitive conformance: ConcreteType : ChildProtocol satisfies T : ParentProtocol
        // when ChildProtocol : ParentProtocol.
        if (HasTransitiveConformance(typeArgumentDecl, protocolConstraint, moduleDecl))
            return ConformanceResult.Yes;

        // Fully resolved local declaration that declares no matching (direct or transitive)
        // conformance: a DEFINITIVE negative, not merely unprovable.
        return ConformanceResult.No;
    }

    /// <summary>
    /// Whether the committed stdlib fact table records <paramref name="typeArgument"/> as
    /// conforming to <paramref name="protocolConstraint"/>. Exposed for callers that only
    /// need the stdlib slice (e.g. the unreachable-conformance veto), independent of the full
    /// <see cref="ConcreteConforms"/> resolution.
    /// </summary>
    public bool HasStdlibConformance(SwiftTypeName typeArgument, SwiftTypeName protocolConstraint)
        => s_stdlibFacts.Value.TryGetValue(typeArgument.ModuleQualifiedName, out var conformances) &&
           conformances.Contains(protocolConstraint.ModuleQualifiedName);

    /// <summary>
    /// Walks the TypeDatabase superclass chain from <paramref name="typeArgumentName"/>
    /// looking for an exact match against <paramref name="classConstraint"/>. Used when the
    /// type argument is an XML/database-owned class (e.g. Foundation unit subclasses) that has
    /// no local <see cref="TypeDecl"/>. Caps the walk at 64 hops to avoid pathological loops.
    /// </summary>
    public bool IsSubclassOfViaTypeDatabase(SwiftTypeName typeArgumentName, SwiftTypeName classConstraint)
    {
        var current = typeArgumentName;
        for (int i = 0; i < 64; i++)
        {
            if (!_typeDatabase.TryGetTypeRecord(current, out var record))
                return false;
            if (record.Kind != TypeRecordKind.Class)
                return false;
            if (record.SuperclassTypeName == null)
                return false;
            if (record.SuperclassTypeName == classConstraint)
                return true;
            current = record.SuperclassTypeName;
        }
        return false;
    }

    /// <summary>
    /// Whether a protocol transitively inherits from a target protocol, resolved through the
    /// module's protocol declarations. Shared with the generic-parameter constraint path.
    /// </summary>
    public bool ProtocolInheritsFrom(SwiftTypeName childProtocol, SwiftTypeName targetProtocol, ModuleDecl moduleDecl)
    {
        var visited = new HashSet<string>();
        return ProtocolInheritsFromRecursive(childProtocol, targetProtocol, moduleDecl, visited);
    }

    private static bool HasConformance(TypeDecl typeDecl, SwiftTypeName protocolType) =>
        typeDecl switch
        {
            StructDecl structDecl => structDecl.Conformances.Any(c => c.Protocol == protocolType),
            ClassDecl classDecl => classDecl.Conformances.Any(c => c.Protocol == protocolType),
            EnumDecl enumDecl => enumDecl.Conformances.Any(c => c.Protocol == protocolType),
            _ => false
        };

    /// <summary>
    /// Whether a concrete type transitively satisfies a protocol constraint via protocol
    /// inheritance. For example, ConcreteType : ChildProtocol satisfies T : ParentProtocol
    /// when ChildProtocol : ParentProtocol.
    /// </summary>
    private bool HasTransitiveConformance(TypeDecl typeDecl, SwiftTypeName targetProtocol, ModuleDecl moduleDecl)
    {
        var conformances = typeDecl switch
        {
            StructDecl s => s.Conformances,
            ClassDecl c => c.Conformances,
            EnumDecl e => e.Conformances,
            _ => null
        };

        if (conformances == null)
            return false;

        foreach (var conformance in conformances)
        {
            if (ProtocolInheritsFrom(conformance.Protocol, targetProtocol, moduleDecl))
                return true;
        }

        return false;
    }

    private bool ProtocolInheritsFromRecursive(SwiftTypeName current, SwiftTypeName target, ModuleDecl moduleDecl, HashSet<string> visited)
    {
        var key = current.ModuleQualifiedName;
        if (!visited.Add(key))
            return false;

        var protocolDecl = moduleDecl.Protocols
            .FirstOrDefault(p => p.SwiftTypeName.Module == current.Module && p.SwiftTypeName.Name == current.Name);
        if (protocolDecl == null)
            return false;

        foreach (var inherited in protocolDecl.InheritedProtocols)
        {
            var inheritedName = SwiftTypeName.FromTypeSpec(inherited);
            if (inheritedName == target)
                return true;
            if (ProtocolInheritsFromRecursive(inheritedName, target, moduleDecl, visited))
                return true;
        }

        return false;
    }

    private static IReadOnlyDictionary<string, HashSet<string>> LoadStdlibFacts()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "Swift.Bindings.Data.stdlib-conformances.json";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var file = JsonConvert.DeserializeObject<StdlibConformanceFile>(json)
            ?? throw new InvalidOperationException("Failed to deserialize stdlib-conformances.json.");

        // Schema-version handshake: a producer/consumer shape change must bump both the data
        // file and ExpectedStdlibSchemaVersion in lockstep, so a stale embedded file fails loud
        // here instead of silently mis-answering stdlib conformance queries.
        if (file.SchemaVersion != ExpectedStdlibSchemaVersion)
            throw new InvalidOperationException(
                $"stdlib-conformances.json schemaVersion {file.SchemaVersion} does not match the "
                + $"expected version {ExpectedStdlibSchemaVersion}. Regenerate or bump both in lockstep.");

        var facts = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (typeName, protocols) in file.Conformances)
            facts[typeName] = new HashSet<string>(protocols, StringComparer.Ordinal);

        return facts;
    }

    private sealed class StdlibConformanceFile
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("conformances")]
        public Dictionary<string, List<string>> Conformances { get; set; } = new();
    }
}
