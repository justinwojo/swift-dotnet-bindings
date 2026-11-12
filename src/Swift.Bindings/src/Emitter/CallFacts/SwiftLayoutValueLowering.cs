// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace BindingsGeneration;

/// <summary>
/// The <see cref="ISwiftValueLowering"/> the swiftc lowering oracle pins: a type's stored layout,
/// with every scalar at its real offset (<see cref="SwiftLayoutResolver"/>), lowered by
/// <see cref="SwiftAggregateLowering"/>. Over four pieces is indirect. A type whose layout it cannot
/// derive soundly is unknown, so the classifier refuses instead of guessing.
/// </summary>
/// <remarks>
/// Layouts come from three sources: the module's own declarations (a frozen struct's stored
/// properties, a frozen enum's cases), a small table of standard-library and SDK carriers whose
/// representation the oracle checks, and the scalar and pointer shapes. A type of another module
/// that is not in the carrier table has no declaration here, so it is unknown even when frozen.
/// </remarks>
public sealed class SwiftLayoutValueLowering : ISwiftValueLowering
{
    private readonly SwiftLayoutResolver _resolver;

    public SwiftLayoutValueLowering(IEnumerable<ModuleDecl> modules, ITypeDatabase? typeDatabase)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _resolver = new SwiftLayoutResolver(modules, typeDatabase);
    }

    /// <summary>The resolver behind this lowering, for the planner and the oracle.</summary>
    public SwiftLayoutResolver Resolver => _resolver;

    public SwiftLoweringAnswer Lower(SwiftTypeFacts type) => Lower(type, generics: null, asResult: false);

    public SwiftLoweringAnswer LowerResult(SwiftTypeFacts type) => Lower(type, generics: null, asResult: true);

    public SwiftLoweringAnswer Lower(SwiftTypeFacts type, SwiftGenericSignatureFacts? generics, bool asResult)
    {
        ArgumentNullException.ThrowIfNull(type);
        TypeSpec? spec;
        try
        {
            spec = TypeSpecParser.Parse(SwiftCallFactsBuilder.BracketAttributeArguments(type.Spelling));
        }
        catch (Exception ex) when (ex is TypeSpecParseException or ArgumentException or InvalidOperationException or FormatException)
        {
            return SwiftLoweringAnswer.UnknownBecause($"'{type.Spelling}' does not parse as a type: {ex.Message}");
        }
        if (spec is null)
            return SwiftLoweringAnswer.UnknownBecause($"'{type.Spelling}' does not parse as a type");

        // A tuple parameter is passed element by element: Swift splits it into one parameter per
        // element, and a tuple element in turn, before lowering. A tuple result is lowered whole.
        if (!asResult && spec is TupleTypeSpec { Elements.Count: > 1 })
        {
            var pieces = ImmutableArray.CreateBuilder<SwiftLoweredPiece>();
            foreach (var element in TupleLeaves(spec))
            {
                var answer = FromLayout(_resolver.Resolve(element, type.Representation, generics), element.ToString(true));
                if (answer.Outcome != SwiftLoweringOutcome.Direct)
                    return SwiftLoweringAnswer.UnknownBecause($"tuple '{type.Spelling}': element '{element.ToString(true)}' is {answer.Outcome.ToString().ToLowerInvariant()}: {answer.Reason}");
                pieces.AddRange(answer.Layout);
            }
            return SwiftLoweringAnswer.DirectLayout(pieces.ToImmutable(), $"tuple '{type.Spelling}' passes its elements one by one");
        }

        return FromLayout(_resolver.Resolve(spec, type.Representation, generics), type.Spelling);
    }

    private static IEnumerable<TypeSpec> TupleLeaves(TypeSpec spec)
    {
        if (spec is not TupleTypeSpec { Elements.Count: > 1 } tuple)
        {
            yield return spec;
            yield break;
        }
        foreach (var element in tuple.Elements)
            foreach (var leaf in TupleLeaves(element))
                yield return leaf;
    }

    private static SwiftLoweringAnswer FromLayout(SwiftLayoutResult result, string spelling)
    {
        if (result.Layout is not { } layout)
        {
            return result.IsIndirect
                ? SwiftLoweringAnswer.IndirectBecause(result.Reason)
                : SwiftLoweringAnswer.UnknownBecause(result.Reason);
        }
        var pieces = SwiftAggregateLowering.Lower(layout);
        if (SwiftAggregateLowering.PassesIndirectly(pieces))
            return SwiftLoweringAnswer.IndirectBecause($"'{spelling}' lowers to {pieces.Length} pieces, over the {SwiftAggregateLowering.MaxDirectPieces}-register limit");
        return SwiftLoweringAnswer.DirectLayout(pieces, $"'{spelling}' lowers to {pieces.Length} piece(s)");
    }
}

/// <summary>A resolved layout, or why there is none: the type is address-only, or its layout is not known.</summary>
public sealed record SwiftLayoutResult(SwiftLeafLayout? Layout, bool IsIndirect, string Reason)
{
    public static SwiftLayoutResult Of(SwiftLeafLayout layout, string reason) => new(layout, false, reason);
    public static SwiftLayoutResult Indirect(string reason) => new(null, true, reason);
    public static SwiftLayoutResult Unknown(string reason) => new(null, false, reason);
}

/// <summary>
/// Derives the inline layout Swift gives a type, as stored scalars at their offsets. It answers only
/// what it can derive soundly and is unknown otherwise.
/// </summary>
public sealed class SwiftLayoutResolver
{
    private const int MaxDepth = 32;

    private readonly Dictionary<string, (TypeDecl Decl, bool IsOwn)> _types = new(StringComparer.Ordinal);
    private readonly ITypeDatabase? _typeDatabase;

    public SwiftLayoutResolver(IEnumerable<ModuleDecl> modules, ITypeDatabase? typeDatabase)
    {
        _typeDatabase = typeDatabase;
        foreach (var module in modules)
        {
            foreach (var type in module.Types)
                Index(type, IsOwn(type, module.Name));
            foreach (var protocol in module.Protocols)
                Index(protocol, IsOwn(protocol, module.Name));
            foreach (var protocols in module.DependencyProtocols.Values)
                foreach (var protocol in protocols)
                    Index(protocol, isOwn: false);
        }
    }

    private static bool IsOwn(TypeDecl type, string module) =>
        string.Equals(type.SwiftTypeName.Module, module, StringComparison.Ordinal);

    private void Index(TypeDecl type, bool isOwn)
    {
        // A module extending another module's type holds a partial declaration of it; the defining
        // module's declaration is the one whose stored properties are complete.
        var name = type.SwiftTypeName.ModuleQualifiedName;
        if (!_types.TryGetValue(name, out var existing) || (!existing.IsOwn && isOwn))
            _types[name] = (type, isOwn);
        // A nested declaration belongs to the module whose tree holds it.
        foreach (var nested in type.Types)
            Index(nested, isOwn: true);
    }

    /// <summary>
    /// What a resolution knows beyond the declarations: the representation of function types
    /// spelled without a <c>@convention</c> attribute, the generic signature its <c>τ</c> names
    /// belong to, and, inside a generic declaration, what its parameters are bound to.
    /// </summary>
    private sealed record Scope(
        SwiftFunctionRepresentation? Functions,
        SwiftGenericSignatureFacts? Generics,
        IReadOnlyDictionary<string, SwiftLayoutResult>? Bound)
    {
        public static readonly Scope Empty = new(null, null, null);
    }

    /// <summary>The layout of <paramref name="spec"/>.</summary>
    /// <param name="functions">
    /// The representation of a function type <paramref name="spec"/> spells without a
    /// <c>@convention</c> attribute, when the caller knows it; such a function type is unknown otherwise.
    /// </param>
    /// <param name="generics">The generic signature the spec's generic parameters belong to.</param>
    public SwiftLayoutResult Resolve(TypeSpec spec, SwiftFunctionRepresentation? functions = null, SwiftGenericSignatureFacts? generics = null) =>
        Resolve(spec, 0, new Scope(functions, generics, null));

    private SwiftLayoutResult Resolve(TypeSpec spec, int depth, Scope scope)
    {
        if (depth > MaxDepth)
            return SwiftLayoutResult.Unknown($"'{spec.ToString(true)}' nests deeper than {MaxDepth} levels");
        if (spec.IsInOut)
            return SwiftLayoutResult.Unknown($"'{spec.ToString(true)}' is inout, which is an address and not a value");
        return spec switch
        {
            TupleTypeSpec tuple => ResolveTuple(tuple, depth, scope),
            ClosureTypeSpec closure => ResolveFunction(closure, scope.Functions),
            ProtocolListTypeSpec list => ResolveComposition(list),
            NamedTypeSpec named => ResolveNamed(named, depth, scope),
            _ => SwiftLayoutResult.Unknown($"'{spec.ToString(true)}' is a {spec.GetType().Name}, which has no layout rule"),
        };
    }

    private SwiftLayoutResult ResolveTuple(TupleTypeSpec tuple, int depth, Scope scope)
    {
        if (tuple.IsEmptyTuple || tuple.Elements.Count == 0)
            return SwiftLayoutResult.Of(SwiftLeafLayout.Empty, "an empty tuple has no storage");
        if (tuple.Elements.Count == 1)
            return Resolve(tuple.Elements[0], depth + 1, scope);
        var fields = new List<SwiftLeafLayout>();
        foreach (var element in tuple.Elements)
        {
            var r = Resolve(element, depth + 1, scope);
            if (r.Layout is null)
            {
                // A tuple with an address-only element is address-only; returned, each such element
                // is its own indirect result, which this model does not describe.
                return SwiftLayoutResult.Unknown($"tuple element '{element.ToString(true)}': {r.Reason}");
            }
            fields.Add(r.Layout);
        }
        return SwiftLayoutResult.Of(SwiftLeafLayout.Sequence(fields), "a tuple stores its elements in order");
    }

    /// <summary>
    /// A function value. Its representation decides its width (a thick closure is a function
    /// pointer and a context; a block, a thin function and a C function pointer one pointer), and
    /// the ABI descriptor's spelling does not record it unless it was written out.
    /// </summary>
    private static SwiftLayoutResult ResolveFunction(ClosureTypeSpec closure, SwiftFunctionRepresentation? unattributed)
    {
        if (closure.IsConventionC)
            return SwiftLayoutResult.Of(SwiftLeafLayout.Scalar(SwiftScalarKind.Pointer), "a C function pointer is one pointer");
        var convention = closure.HasAttributes
            ? closure.Attributes.FirstOrDefault(a => a.Name == "convention" && a.Parameters.Count == 1)?.Parameters[0]
            : null;
        return convention switch
        {
            "c" or "block" or "thin" => SwiftLayoutResult.Of(SwiftLeafLayout.Scalar(SwiftScalarKind.Pointer), $"a @convention({convention}) function is one pointer"),
            "swift" => ThickFunction(),
            null when unattributed is SwiftFunctionRepresentation.Thick => ThickFunction(),
            null when unattributed is SwiftFunctionRepresentation.Block or SwiftFunctionRepresentation.Thin or SwiftFunctionRepresentation.C =>
                SwiftLayoutResult.Of(SwiftLeafLayout.Scalar(SwiftScalarKind.Pointer), $"a {unattributed} function is one pointer"),
            _ => SwiftLayoutResult.Unknown($"the representation of the function type '{closure.ToString(true)}' is not recorded"),
        };

        static SwiftLayoutResult ThickFunction() =>
            SwiftLayoutResult.Of(SwiftLeafLayout.Sequence(new[] { SwiftLeafLayout.Scalar(SwiftScalarKind.Pointer), SwiftLeafLayout.Scalar(SwiftScalarKind.Pointer) }),
                "a thick function is a function pointer and a context");
    }

    private SwiftLayoutResult ResolveNamed(NamedTypeSpec named, int depth, Scope scope)
    {
        var name = FullNameOf(named);
        var spelling = named.ToString(true);

        if (scope.Bound is { } bound && named.GenericParameters.Count == 0 && bound.TryGetValue(name, out var argument))
            return argument;
        if (name.EndsWith(".Type", StringComparison.Ordinal) || name.EndsWith(".Protocol", StringComparison.Ordinal))
        {
            // A type nested in T and named Type parses like T's metatype. A module's top-level type
            // named Type is the type: a module is not a type, so it has no metatype.
            if (!IsDeclaredTypeName(name))
                return ResolveMetatype(named, name);
            if (IsDeclaredTypeName(name[..name.LastIndexOf('.')]))
                return SwiftLayoutResult.Unknown($"'{spelling}' names both a metatype and a nested type, which the parsed type spelling does not tell apart");
        }
        if (IsGenericParameterName(name))
            return ResolveGenericParameter(name, scope);

        if (name is "Swift.Void" or "Void" or "()")
            return SwiftLayoutResult.Of(SwiftLeafLayout.Empty, "Void has no storage");
        if (TryScalar(name, out var scalar))
            return SwiftLayoutResult.Of(SwiftLeafLayout.Scalar(scalar), $"'{name}' is a {scalar} scalar");
        if (AppleFrameworkRegistry.IsCGFloat(name))
            return SwiftLayoutResult.Of(SwiftLeafLayout.Scalar(SwiftScalarKind.Double), "CGFloat is a Double on 64-bit targets");
        if (IsPointer(name))
            return SwiftLayoutResult.Of(SwiftLeafLayout.Scalar(SwiftScalarKind.Pointer), $"'{name}' is one pointer");
        if (name is "Swift.Optional" && named.GenericParameters.Count == 1)
            return ResolveOptional(named.GenericParameters[0], depth, scope);
        if (name is "Swift.Range" or "Swift.ClosedRange" && named.GenericParameters.Count == 1)
        {
            // The lower bound, then the upper bound.
            var limit = Resolve(named.GenericParameters[0], depth + 1, scope);
            if (limit.Layout is not { } boundLayout)
                return limit.IsIndirect
                    ? SwiftLayoutResult.Indirect($"'{spelling}': {limit.Reason}")
                    : SwiftLayoutResult.Unknown($"'{spelling}': {limit.Reason}");
            return SwiftLayoutResult.Of(SwiftLeafLayout.Sequence(new[] { boundLayout, boundLayout }), $"'{name}' is its lower and upper bounds");
        }
        if (Carrier(name) is { } carrier)
            return carrier;
        if (name is "AnyObject" or "Swift.AnyObject")
            return SwiftLayoutResult.Of(SwiftLeafLayout.Scalar(SwiftScalarKind.Pointer), "AnyObject is one reference");
        if (name is "Any" or "Swift.Any")
            return SwiftLayoutResult.Indirect("an Any existential is address-only");

        if (_types.TryGetValue(name, out var entry))
            return ResolveDecl(entry.Decl, named, depth, scope);

        if (TryPeekRecord(name, out var record))
        {
            return record.Kind switch
            {
                TypeRecordKind.Class => SwiftLayoutResult.Of(SwiftLeafLayout.Scalar(SwiftScalarKind.Pointer), $"'{name}' is a class reference"),
                TypeRecordKind.Struct or TypeRecordKind.Enum when (record.Flags & TypeRecordFlags.Frozen) == 0 =>
                    SwiftLayoutResult.Indirect($"'{name}' is resilient to its clients"),
                TypeRecordKind.Protocol or TypeRecordKind.Existential when (record.Flags & TypeRecordFlags.ClassBound) == 0 =>
                    SwiftLayoutResult.Indirect($"the existential '{name}' is address-only"),
                _ => SwiftLayoutResult.Unknown($"'{name}' is declared in another module and is not in the carrier table"),
            };
        }
        return SwiftLayoutResult.Unknown($"'{spelling}' is not known to the module or the type database");
    }

    private static bool IsGenericParameterName(string name) =>
        name == "Self" || name.StartsWith("Self.", StringComparison.Ordinal) || name.StartsWith("τ_", StringComparison.Ordinal);

    /// <summary>
    /// A generic parameter in unspecialized code: one reference when its signature binds it to
    /// classes, otherwise an address-only value.
    /// </summary>
    private static SwiftLayoutResult ResolveGenericParameter(string name, Scope scope)
    {
        if (name.Contains('.'))
            return SwiftLayoutResult.Unknown($"the dependent member type '{name}' takes its constraints from a protocol this model does not read");
        if (scope.Generics is not { } generics || !generics.Parameters.Contains(name))
            return SwiftLayoutResult.Unknown($"the generic parameter '{name}' is not in the signature in scope");
        return generics.IsClassBound(name) switch
        {
            true => SwiftLayoutResult.Of(SwiftLeafLayout.Scalar(SwiftScalarKind.Pointer), $"'{name}' is bound to classes, so it is one reference"),
            false => SwiftLayoutResult.Indirect($"the generic parameter '{name}' is not bound to classes, so it is address-only"),
            null => SwiftLayoutResult.Unknown($"whether the generic parameter '{name}' is bound to classes is not known"),
        };
    }

    private SwiftLayoutResult ResolveDecl(TypeDecl decl, NamedTypeSpec named, int depth, Scope scope)
    {
        var name = decl.SwiftTypeName.ModuleQualifiedName;
        switch (decl)
        {
            case ClassDecl:
                return SwiftLayoutResult.Of(SwiftLeafLayout.Scalar(SwiftScalarKind.Pointer), $"'{name}' is a class reference");

            case StructDecl s:
            {
                if (!s.IsFrozen)
                    return SwiftLayoutResult.Indirect($"'{name}' is resilient to its clients");
                if (s.HasCustomAlignment)
                    return SwiftLayoutResult.Unknown($"'{name}' declares an alignment the ABI descriptor does not record");
                if (BindArguments(s, named, depth, scope, out var bound) is { } refusal)
                    return refusal;
                // A stored function's representation is not in the ABI descriptor, whatever the caller knows.
                var fieldScope = new Scope(null, null, bound);
                var fields = new List<SwiftLeafLayout>();
                foreach (var property in s.Properties)
                {
                    if (property.IsStatic || !property.HasStorage)
                        continue;
                    if (property.ReferenceOwnership != SwiftReferenceOwnership.Strong)
                        return SwiftLayoutResult.Unknown($"'{name}.{property.Name}' is a {property.ReferenceOwnership} reference, whose storage this model does not describe");
                    var field = Resolve(property.SwiftTypeSpec, depth + 1, fieldScope);
                    if (field.Layout is null)
                    {
                        // A field Swift stores out of line makes the whole struct address-only.
                        return field.IsIndirect
                            ? SwiftLayoutResult.Indirect($"'{name}.{property.Name}': {field.Reason}")
                            : SwiftLayoutResult.Unknown($"'{name}.{property.Name}': {field.Reason}");
                    }
                    fields.Add(field.Layout);
                }
                return SwiftLayoutResult.Of(SwiftLeafLayout.Sequence(fields), $"the stored properties of frozen '{name}'");
            }

            case EnumDecl e:
            {
                if (e.HasCustomAlignment)
                    return SwiftLayoutResult.Unknown($"'{name}' declares an alignment the ABI descriptor does not record");
                // An @objc enum is a C enum: its raw value, whether or not it is @frozen.
                if (e.IsObjC)
                {
                    return TryScalar(RawTypeName(e.RawValueTypeName), out var raw) && raw is not (SwiftScalarKind.Bool or SwiftScalarKind.Float or SwiftScalarKind.Double)
                        ? SwiftLayoutResult.Of(SwiftLeafLayout.Scalar(raw), $"@objc '{name}' is stored as its {e.RawValueTypeName} raw value")
                        : SwiftLayoutResult.Unknown($"@objc '{name}' has the raw type '{e.RawValueTypeName}', which has no layout rule");
                }
                if (!e.IsFrozen)
                    return SwiftLayoutResult.Indirect($"'{name}' is resilient to its clients");
                if (e.GenericParameters.Count > 0 || named.GenericParameters.Count > 0)
                    return SwiftLayoutResult.Unknown($"'{name}' is generic; its layout depends on its arguments");
                if (e.Cases.Any(c => c.AssociatedValues.Count > 0))
                    return SwiftLayoutResult.Unknown($"'{name}' has payload cases, whose layout this model does not describe");
                return e.Cases.Count switch
                {
                    0 => SwiftLayoutResult.Unknown($"'{name}' has no cases"),
                    1 => SwiftLayoutResult.Of(SwiftLeafLayout.Empty, $"single-case '{name}' has no storage"),
                    <= 256 => SwiftLayoutResult.Of(SwiftLeafLayout.OpaqueBytes(1, 1), $"'{name}' stores a one-byte tag"),
                    <= 65536 => SwiftLayoutResult.Of(SwiftLeafLayout.OpaqueBytes(2, 2), $"'{name}' stores a two-byte tag"),
                    _ => SwiftLayoutResult.Unknown($"'{name}' has more cases than this model describes"),
                };
            }

            case ProtocolDecl p:
                return ResolveExistential(new[] { p }, includesAnyObject: false, superclass: null, $"any {name}");

            default:
                return SwiftLayoutResult.Unknown($"'{name}' is a {decl.GetType().Name}, which has no layout rule");
        }
    }

    /// <summary>
    /// Binds a generic struct's parameters to the arguments <paramref name="named"/> spells, each
    /// resolved where it is spelled. Returns why it cannot, or null.
    /// </summary>
    private SwiftLayoutResult? BindArguments(StructDecl s, NamedTypeSpec named, int depth, Scope scope, out IReadOnlyDictionary<string, SwiftLayoutResult>? bound)
    {
        bound = null;
        var name = s.SwiftTypeName.ModuleQualifiedName;
        if (s.GenericParameters.Count == 0 && named.GenericParameters.Count == 0)
            return null;
        if (s.ParentDecl is TypeDecl || named.InnerType is not null)
            return SwiftLayoutResult.Unknown($"'{name}' is a generic type nested in another; its parameters are not bound here");
        var parameters = GenericSignatureParser.ParseSignature(s.RawGenericSig).Parameters;
        var map = new Dictionary<string, SwiftLayoutResult>(StringComparer.Ordinal);
        if (parameters.Count > 0 && named.GenericParameters.Count == 0
            && scope.Generics is { } generics && parameters.All(generics.Parameters.Contains))
        {
            // The declaration's own name inside its own generic context: the unspecialized type a
            // generic member's self has, each parameter standing for whatever it is bound to.
            foreach (var parameter in parameters)
                map[parameter] = ResolveGenericParameter(parameter, scope);
            bound = map;
            return null;
        }
        if (parameters.Count == 0 || parameters.Count != named.GenericParameters.Count)
            return SwiftLayoutResult.Unknown($"'{named.ToString(true)}' does not bind each of '{name}''s generic parameters");
        for (int i = 0; i < parameters.Count; i++)
            map[parameters[i]] = Resolve(named.GenericParameters[i], depth + 1, scope);
        bound = map;
        return null;
    }

    /// <summary>
    /// <c>Optional&lt;Wrapped&gt;</c>, stored as untyped data. A payload with spare bit patterns (a
    /// pointer, a <c>Bool</c>, a no-payload enum with tag values to spare) folds <c>nil</c> into one
    /// and keeps its size; a payload of integers and floating-point values only, which uses every
    /// pattern, gets a one-byte tag after it.
    /// </summary>
    private SwiftLayoutResult ResolveOptional(TypeSpec wrapped, int depth, Scope scope)
    {
        var inner = Resolve(wrapped, depth + 1, scope);
        if (inner.Layout is not { } layout)
            return inner.IsIndirect
                ? SwiftLayoutResult.Indirect($"Optional of an address-only payload: {inner.Reason}")
                : SwiftLayoutResult.Unknown($"Optional payload: {inner.Reason}");

        var leaves = layout.Leaves;
        if (leaves.Any(l => l.Kind is SwiftScalarKind.Pointer or SwiftScalarKind.Bool))
            return SwiftLayoutResult.Of(layout.AsOpaque(), "an Optional folds nil into its payload's unused bit patterns");
        if (IsSpareTagEnum(wrapped))
            return SwiftLayoutResult.Of(layout.AsOpaque(), "an Optional folds nil into a spare tag value of its payload");
        if (leaves.All(l => l.Kind is not SwiftScalarKind.Opaque))
        {
            var tagged = SwiftLeafLayout.Sequence(new[] { layout.AsOpaque(), SwiftLeafLayout.OpaqueBytes(1, 1) });
            return SwiftLayoutResult.Of(tagged with { Alignment = layout.Alignment }, "an Optional of a payload with no spare bit patterns appends a one-byte tag");
        }
        return SwiftLayoutResult.Unknown($"whether Optional<{wrapped.ToString(true)}> needs a tag byte is not derivable here");
    }

    private bool IsSpareTagEnum(TypeSpec wrapped) =>
        wrapped is NamedTypeSpec named
        && _types.TryGetValue(FullNameOf(named), out var entry)
        // An @objc enum is its raw value, every pattern of which is a valid value.
        && entry.Decl is EnumDecl { IsFrozen: true, IsObjC: false } e
        && e.Cases.Count is > 1 and < 256
        && e.Cases.All(c => c.AssociatedValues.Count == 0);

    /// <summary>
    /// A metatype: thin (no storage) for a concrete struct or enum, one pointer for a class or a
    /// generic parameter. An existential metatype (<c>any P.Type</c>) is the dynamic type's metadata
    /// followed by one witness table per non-<c>@objc</c> protocol.
    /// </summary>
    private SwiftLayoutResult ResolveMetatype(NamedTypeSpec named, string name)
    {
        if (name.EndsWith(".Protocol", StringComparison.Ordinal))
            return SwiftLayoutResult.Unknown($"the protocol metatype '{name}' has no layout rule");
        var instance = name[..^".Type".Length];
        if (named.IsAny)
            return ResolveExistentialMetatype(instance, name);
        // The metatype of a generic parameter is the metadata of whatever it is bound to.
        if (instance == "Self" || instance.StartsWith("τ_", StringComparison.Ordinal))
            return SwiftLayoutResult.Of(SwiftLeafLayout.Scalar(SwiftScalarKind.Pointer), $"'{name}' is a thick metatype");
        if (named.GenericParameters.Count > 0)
            return SwiftLayoutResult.Unknown($"the metatype of the generic instance '{named.ToString(true)}' has no layout rule");
        if (_types.TryGetValue(instance, out var entry))
        {
            return entry.Decl switch
            {
                StructDecl or EnumDecl => SwiftLayoutResult.Of(SwiftLeafLayout.Empty, $"'{name}' is a thin metatype"),
                ClassDecl => SwiftLayoutResult.Of(SwiftLeafLayout.Scalar(SwiftScalarKind.Pointer), $"'{name}' is a thick metatype"),
                _ => SwiftLayoutResult.Unknown($"the metatype '{name}' has no layout rule"),
            };
        }
        if (TryPeekRecord(instance, out var record))
        {
            return record.Kind switch
            {
                TypeRecordKind.Struct or TypeRecordKind.Enum => SwiftLayoutResult.Of(SwiftLeafLayout.Empty, $"'{name}' is a thin metatype"),
                TypeRecordKind.Class => SwiftLayoutResult.Of(SwiftLeafLayout.Scalar(SwiftScalarKind.Pointer), $"'{name}' is a thick metatype"),
                _ => SwiftLayoutResult.Unknown($"the metatype '{name}' has no layout rule"),
            };
        }
        return SwiftLayoutResult.Unknown($"the metatype '{name}' names an unknown type");
    }

    private SwiftLayoutResult ResolveExistentialMetatype(string instance, string name)
    {
        var metadata = SwiftLeafLayout.Scalar(SwiftScalarKind.Pointer);
        if (instance is "Any" or "Swift.Any" or "AnyObject" or "Swift.AnyObject")
            return SwiftLayoutResult.Of(metadata, $"'any {name}' is one metadata pointer");
        if (_types.TryGetValue(instance, out var entry) && entry.Decl is ProtocolDecl protocol)
        {
            return protocol.IsObjC
                ? SwiftLayoutResult.Of(metadata, $"'any {name}' is one metadata pointer: an @objc protocol has no witness table")
                : SwiftLayoutResult.Of(SwiftLeafLayout.Sequence(new[] { metadata, SwiftLeafLayout.Scalar(SwiftScalarKind.Pointer) }),
                    $"'any {name}' is a metadata pointer and a witness table");
        }
        return SwiftLayoutResult.Unknown($"the existential metatype 'any {name}' names a protocol this module does not declare");
    }

    private SwiftLayoutResult ResolveComposition(ProtocolListTypeSpec list)
    {
        if (list.IsOpaque)
            return SwiftLayoutResult.Indirect("an opaque type is address-only to its caller");
        var protocols = new List<ProtocolDecl>();
        bool anyObject = false;
        string? superclass = null;
        foreach (var member in list.Protocols.Keys)
        {
            var name = FullNameOf(member);
            if (name is "AnyObject" or "Swift.AnyObject")
            {
                anyObject = true;
                continue;
            }
            if (_types.TryGetValue(name, out var entry))
            {
                switch (entry.Decl)
                {
                    case ProtocolDecl p:
                        protocols.Add(p);
                        continue;
                    case ClassDecl:
                        superclass = name;
                        continue;
                }
            }
            return SwiftLayoutResult.Unknown($"composition member '{name}' is not a protocol or class this module declares");
        }
        return ResolveExistential(protocols, anyObject, superclass, list.ToString(true));
    }

    /// <summary>
    /// An existential. A class-bound one is the reference followed by one witness table per
    /// protocol that has one (neither <c>@objc</c> nor a marker); any other is an address-only box.
    /// </summary>
    private SwiftLayoutResult ResolveExistential(IReadOnlyList<ProtocolDecl> protocols, bool includesAnyObject, string? superclass, string spelling)
    {
        bool classBound = includesAnyObject || superclass is not null || protocols.Any(IsClassBound);
        if (!classBound)
            return SwiftLayoutResult.Indirect($"the existential '{spelling}' is address-only");
        var fields = new List<SwiftLeafLayout> { SwiftLeafLayout.Scalar(SwiftScalarKind.Pointer) };
        foreach (var p in protocols.Where(p => !p.IsObjC && !p.IsMarker))
            fields.Add(SwiftLeafLayout.Scalar(SwiftScalarKind.Pointer));
        return SwiftLayoutResult.Of(SwiftLeafLayout.Sequence(fields), $"the class-bound existential '{spelling}' is a reference and {fields.Count - 1} witness table(s)");
    }

    private static bool TryScalar(string name, out SwiftScalarKind kind)
    {
        switch (name)
        {
            case "Swift.Int" or "Swift.UInt" or "Swift.Int64" or "Swift.UInt64":
                kind = SwiftScalarKind.Int64;
                return true;
            case "Swift.Int32" or "Swift.UInt32" or "Swift.Unicode.Scalar":
                kind = SwiftScalarKind.Int32;
                return true;
            case "Swift.Int16" or "Swift.UInt16":
                kind = SwiftScalarKind.Int16;
                return true;
            case "Swift.Int8" or "Swift.UInt8":
                kind = SwiftScalarKind.Int8;
                return true;
            case "Swift.Bool":
                kind = SwiftScalarKind.Bool;
                return true;
            case "Swift.Float":
                kind = SwiftScalarKind.Float;
                return true;
            case "Swift.Double":
                kind = SwiftScalarKind.Double;
                return true;
            case "MetadataRequest":
                kind = SwiftScalarKind.Int64;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static bool IsPointer(string name) => name is
        "Swift.UnsafePointer" or "Swift.UnsafeMutablePointer" or "Swift.UnsafeRawPointer" or "Swift.UnsafeMutableRawPointer"
        or "Swift.OpaquePointer" or "Swift.AutoreleasingUnsafeMutablePointer" or "Swift.Unmanaged";

    /// <summary>
    /// Standard-library and SDK types whose stored representation is fixed and checked by the oracle.
    /// </summary>
    private static SwiftLayoutResult? Carrier(string name)
    {
        static SwiftLeafLayout Words(params SwiftScalarKind[] kinds) =>
            SwiftLeafLayout.Sequence(kinds.Select(k => SwiftLeafLayout.Scalar(k)));

        switch (name)
        {
            case "Swift.String" or "Swift.Character":
                // _StringObject: the count-and-flags word, then the bridge object.
                return SwiftLayoutResult.Of(Words(SwiftScalarKind.Int64, SwiftScalarKind.Pointer), $"'{name}' is a count word and a bridge object");
            case "Swift.Substring":
                // Slice<String>: the start and end indices, then the base string.
                return SwiftLayoutResult.Of(Words(SwiftScalarKind.Int64, SwiftScalarKind.Int64, SwiftScalarKind.Int64, SwiftScalarKind.Pointer), "Substring is two indices and its base string");
            case "Swift.Array" or "Swift.ContiguousArray" or "Swift.Dictionary" or "Swift.Set":
                return SwiftLayoutResult.Of(SwiftLeafLayout.Scalar(SwiftScalarKind.Pointer), $"'{name}' is one buffer reference");
            case "Swift.UnsafeBufferPointer" or "Swift.UnsafeMutableBufferPointer":
                return SwiftLayoutResult.Of(SwiftLeafLayout.Sequence(new[] { SwiftLeafLayout.OpaqueBytes(8, 8), SwiftLeafLayout.Scalar(SwiftScalarKind.Int64) }),
                    $"'{name}' is an optional start pointer and a count");
            case "Swift.UnsafeRawBufferPointer" or "Swift.UnsafeMutableRawBufferPointer":
                return SwiftLayoutResult.Of(SwiftLeafLayout.Sequence(new[] { SwiftLeafLayout.OpaqueBytes(8, 8), SwiftLeafLayout.OpaqueBytes(8, 8) }),
                    $"'{name}' is optional start and end pointers");
            case "Swift.Error":
                // An Error existential is boxed: one reference to the box.
                return SwiftLayoutResult.Of(SwiftLeafLayout.Scalar(SwiftScalarKind.Pointer), "an Error existential is one boxed reference");
            case "CoreMedia.CMTime":
            {
                // The imported C struct, declared with 4-byte packing: value, timescale, flags, epoch.
                var time = Words(SwiftScalarKind.Int64, SwiftScalarKind.Int32, SwiftScalarKind.Int32, SwiftScalarKind.Int64);
                return SwiftLayoutResult.Of(time with { Alignment = 4 }, "CMTime is a value, a timescale, flags and an epoch, packed to 4 bytes");
            }
            case "Foundation.Data":
                // Data._Representation is a multi-payload enum over two words.
                return SwiftLayoutResult.Of(SwiftLeafLayout.OpaqueBytes(16, 8), "Data is a two-word representation enum");
            case "CoreFoundation.CGPoint" or "CoreGraphics.CGPoint" or "CoreFoundation.CGSize" or "CoreGraphics.CGSize"
                or "CoreFoundation.CGVector" or "CoreGraphics.CGVector":
                return SwiftLayoutResult.Of(Words(SwiftScalarKind.Double, SwiftScalarKind.Double), $"'{name}' is two CGFloats");
            case "CoreFoundation.CGRect" or "CoreGraphics.CGRect":
                return SwiftLayoutResult.Of(Words(SwiftScalarKind.Double, SwiftScalarKind.Double, SwiftScalarKind.Double, SwiftScalarKind.Double), "CGRect is four CGFloats");
            case "Foundation._NSRange" or "Foundation.NSRange":
                return SwiftLayoutResult.Of(Words(SwiftScalarKind.Int64, SwiftScalarKind.Int64), "NSRange is a location and a length");
            case "Foundation.Decimal" or "Foundation.NSDecimal":
            {
                // The imported C struct: a 32-bit bitfield word, then eight 16-bit mantissa words.
                var fields = new List<SwiftLeafLayout> { SwiftLeafLayout.Scalar(SwiftScalarKind.Int32) };
                fields.AddRange(Enumerable.Repeat(SwiftLeafLayout.Scalar(SwiftScalarKind.Int16), 8));
                return SwiftLayoutResult.Of(SwiftLeafLayout.Sequence(fields), "Decimal is a bitfield word and eight mantissa halves");
            }
            case "Foundation.Date" or "Foundation.UUID" or "Foundation.URL" or "Foundation.IndexPath" or "Foundation.Locale":
                // Not @frozen in the SDK's Foundation, whatever the bundled database records.
                return SwiftLayoutResult.Indirect($"'{name}' is resilient in the SDK's Foundation");
        }
        return null;
    }

    /// <summary>Whether a protocol is class-bound, directly or through a protocol it refines.</summary>
    private bool IsDeclaredTypeName(string name) => _types.ContainsKey(name) || TryPeekRecord(name, out _);

    private bool IsClassBound(ProtocolDecl protocol) => SwiftProtocolBounds.IsClassBound(
        protocol,
        name => _types.TryGetValue(name, out var entry) ? entry.Decl as ProtocolDecl : null,
        name => TryPeekRecord(name, out var record) && record.Kind == TypeRecordKind.Protocol
            ? (record.Flags & TypeRecordFlags.ClassBound) != 0
            : null);

    /// <summary>An enum's raw type name, which the model keeps unqualified for the standard library's integers.</summary>
    private static string RawTypeName(string? raw) =>
        raw is null ? string.Empty : raw.Contains('.') ? raw : "Swift." + raw;

    private bool TryPeekRecord(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TypeRecord? record)
    {
        record = null;
        return _typeDatabase is not null
            && SwiftTypeName.TryFromModuleQualifiedName(name, out var typeName)
            && _typeDatabase.TryPeekTypeRecord(typeName, out record);
    }

    internal static string FullNameOf(NamedTypeSpec named)
    {
        var name = named.Name;
        for (var inner = named.InnerType; inner is not null; inner = inner.InnerType)
            name += "." + inner.Name;
        return name;
    }
}
