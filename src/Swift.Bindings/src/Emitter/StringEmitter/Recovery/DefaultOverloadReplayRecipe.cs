// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Detached inputs of one completed native-default producer invocation. This stores neither an
/// emission environment nor generated text: replay rebuilds collaborators and uses live reservations.
/// The enclosing recovery driver decides when a completed render's recipes may be replayed.
/// </summary>
internal sealed class DefaultOverloadReplayRecipe
{
    private readonly MethodDecl _method;
    private readonly string _baseSymbol;
    private readonly string? _disambiguatedName;
    private readonly string? _adoptedName;
    private readonly string? _failableFactoryName;
    private readonly string? _initFactoryName;

    internal DeclId SourceDeclId { get; }

    private DefaultOverloadReplayRecipe(MethodEnvironment env)
    {
        SourceDeclId = env.SourceDeclId;
        _method = CloneMethod(env.MethodDecl);
        _baseSymbol = env.EmissionSymbol;
        _disambiguatedName = env.DisambiguatedNameInput;
        _adoptedName = env.AdoptedOverrideCSharpName;
        _failableFactoryName = env.FailableFactoryName;
        _initFactoryName = env.InitFactoryName;
    }

    internal static DefaultOverloadReplayRecipe Capture(MethodEnvironment env) => new(env);

    /// <summary>
    /// Uses the captured post-normalization signature and symbol with the current render's services.
    /// SourceDeclId remains the original parsed identity, including any debug parameters stripped
    /// before capture. Every call returns independent mutable declarations and argument records.
    /// </summary>
    internal MethodEnvironment CreateEnvironment(MethodEnvironment current)
    {
        var rebuilt = new MethodEnvironment(CloneMethod(_method), current.TypeDatabase,
            current.SiblingPropertyNames, current.PInvokeHelperContext, current.CompositionCollector)
        {
            SourceDeclId = SourceDeclId,
            DisambiguatedNameInput = _disambiguatedName,
            AdoptedOverrideCSharpName = _adoptedName,
            FailableFactoryName = _failableFactoryName,
            InitFactoryName = _initFactoryName,
            EmittedProjectedSignatures = current.EmittedProjectedSignatures,
            ReservedOverloadShapes = current.ReservedOverloadShapes,
            EmissionContext = current.EmissionContext,
        };
        rebuilt.PromoteSymbol(_baseSymbol);
        return rebuilt;
    }

    private static MethodDecl CloneMethod(MethodDecl source)
    {
        var clone = source with
        {
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = source.GenericParameters.Select(CloneGeneric).ToList(),
            AvailabilityAnnotations = source.AvailabilityAnnotations?.Select(a => a with { }).ToList(),
            Documentation = CloneDocumentation(source.Documentation),
            OriginalArgsWithNilClosures = null,
            ThrownErrorType = source.ThrownErrorType == null ? null : CloneType(source.ThrownErrorType),
        };
        clone.CSSignature.AddRange(source.CSSignature.Select(a => CloneArgument(a, clone)));
        if (source.OriginalArgsWithNilClosures != null)
            clone.OriginalArgsWithNilClosures = source.OriginalArgsWithNilClosures
                .Select(a => (CloneArgument(a.Arg, clone), a.IsNilClosure, a.ArgLabel)).ToList();
        return clone;
    }

    private static ArgumentDecl CloneArgument(ArgumentDecl source, MethodDecl parent) => source with
    {
        ParentDecl = parent,
        SwiftTypeSpec = CloneType(source.SwiftTypeSpec),
        AvailabilityAnnotations = source.AvailabilityAnnotations?.Select(a => a with { }).ToList(),
        Documentation = CloneDocumentation(source.Documentation),
    };

    private static DocComment? CloneDocumentation(DocComment? source) => source == null ? null : source with
    {
        Parameters = new Dictionary<string, string>(source.Parameters, source.Parameters.Comparer),
        Remarks = new List<string>(source.Remarks),
    };

    private static GenericArgumentDecl CloneGeneric(GenericArgumentDecl source) => source with
    {
        GenericConformances = source.GenericConformances.Select(c => c with { Path = c.Path.ToArray() }).ToList(),
        AssosiatedTypeConformances = source.AssosiatedTypeConformances.Select(c => c with { Path = c.Path.ToArray() }).ToList(),
        UnrepresentableConcreteSameTypePins = source.UnrepresentableConcreteSameTypePins?.ToArray(),
    };

    // TypeSpec is mutable, including attributes and child collections. Keep this private closed-
    // hierarchy copy here rather than using NonReferenceCloneOf (which discards direction) or
    // reparsing a printed signature (which loses parser-only facts such as USR and convention).
    private static TypeSpec CloneType(TypeSpec source)
    {
        TypeSpec clone = source switch
        {
            NamedTypeSpec named when source.GetType() == typeof(NamedTypeSpec) => new NamedTypeSpec(named.Name)
            {
                Usr = named.Usr,
                InnerType = named.InnerType == null ? null : (NamedTypeSpec)CloneType(named.InnerType),
            },
            ClosureTypeSpec closure when source.GetType() == typeof(ClosureTypeSpec) =>
                new ClosureTypeSpec(CloneType(closure.Arguments), CloneType(closure.ReturnType))
                {
                    Throws = closure.Throws,
                    IsAsync = closure.IsAsync,
                    IsConventionC = closure.IsConventionC,
                },
            TupleTypeSpec tuple when source.GetType() == typeof(TupleTypeSpec) =>
                new TupleTypeSpec(tuple.Elements.Select(CloneType)),
            ProtocolListTypeSpec protocols when source.GetType() == typeof(ProtocolListTypeSpec) =>
                CloneProtocols(protocols),
            AssociatedTypeReferenceSpec associated when source.GetType() == typeof(AssociatedTypeReferenceSpec) =>
                new AssociatedTypeReferenceSpec(associated.BaseType, associated.AssociatedTypeName),
            _ => throw new NotSupportedException(
                $"Native default replay cannot snapshot TypeSpec '{source.GetType().FullName}'."),
        };
        clone.IsInOut = source.IsInOut;
        clone.IsAny = source.IsAny;
        clone.IsVariadic = source.IsVariadic;
        clone.IsImplicitlyUnwrappedOptional = source.IsImplicitlyUnwrappedOptional;
        clone.TypeLabel = source.TypeLabel;
        clone.GenericParameters.AddRange(source.GenericParameters.Select(CloneType));
        foreach (var attribute in source.Attributes)
        {
            var copiedAttribute = new TypeSpecAttribute(attribute.Name);
            copiedAttribute.Parameters.AddRange(attribute.Parameters);
            clone.Attributes.Add(copiedAttribute);
        }
        return clone;
    }

    private static ProtocolListTypeSpec CloneProtocols(ProtocolListTypeSpec source)
    {
        var clone = new ProtocolListTypeSpec { IsOpaque = source.IsOpaque };
        foreach (var protocol in source.Protocols)
            clone.Protocols.Add((NamedTypeSpec)CloneType(protocol.Key), protocol.Value);
        return clone;
    }
}
