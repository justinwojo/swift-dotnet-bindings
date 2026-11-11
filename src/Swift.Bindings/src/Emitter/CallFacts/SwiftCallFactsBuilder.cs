// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace BindingsGeneration;

/// <summary>
/// Derives <see cref="SwiftCallFacts"/> from the parser's model of a module: what Swift declared for
/// each member, read from the ABI JSON through the model's typed fields. Nothing here reads emitted
/// C#, and nothing here decides a physical outcome.
/// </summary>
public sealed class SwiftCallFactsBuilder
{
    private readonly ITypeDatabase _typeDatabase;
    private readonly Dictionary<string, (TypeDecl Decl, bool IsOwn)> _typesByName = new(StringComparer.Ordinal);

    public SwiftCallFactsBuilder(IEnumerable<ModuleDecl> modules, ITypeDatabase typeDatabase)
    {
        _typeDatabase = typeDatabase;
        foreach (var module in modules)
        {
            foreach (var type in module.Types)
                IndexType(type, IsOwnDeclaration(type, module.Name));
            foreach (var protocol in module.Protocols)
                IndexType(protocol, IsOwnDeclaration(protocol, module.Name));
            foreach (var protocols in module.DependencyProtocols.Values)
                foreach (var protocol in protocols)
                    IndexType(protocol, isOwn: false);
        }
    }

    private void IndexType(TypeDecl type, bool isOwn)
    {
        // A module that extends another module's type holds a declaration of it too, carrying
        // only the extension's members. The defining module's declaration is the one that says
        // what the type is, so it replaces an extension's whichever is seen first.
        var name = type.SwiftTypeName.ModuleQualifiedName;
        if (!_typesByName.TryGetValue(name, out var existing) || (!existing.IsOwn && isOwn))
            _typesByName[name] = (type, isOwn);
        foreach (var nested in type.Types)
            IndexType(nested, isOwn: true);
    }

    /// <summary>
    /// True when a top-level declaration sits in the module that defines the type, not in an
    /// extension elsewhere. A nested declaration always belongs to the module whose tree holds it:
    /// the digester's declaration of an extended foreign type carries only what the extending
    /// module adds, so a type nested in it is the extending module's own (its name is still
    /// qualified by the foreign module, which is why the name alone cannot say).
    /// </summary>
    private static bool IsOwnDeclaration(TypeDecl type, string walkingModule) =>
        string.Equals(walkingModule, type.SwiftTypeName.Module, StringComparison.Ordinal);

    /// <summary>The defining module's declaration of a type, or the given one when no other is known.</summary>
    private TypeDecl Canonical(TypeDecl type) =>
        _typesByName.TryGetValue(type.SwiftTypeName.ModuleQualifiedName, out var entry) ? entry.Decl : type;

    /// <summary>Every facts entry the module's declarations name.</summary>
    public IEnumerable<SwiftCallFacts> BuildModule(ModuleDecl module)
    {
        foreach (var method in module.Methods)
            yield return ForMethod(method, parent: null, MemberContext.Plain);
        foreach (var property in module.Properties)
            foreach (var facts in ForProperty(property, parent: null))
                yield return facts;
        foreach (var type in module.Types)
            foreach (var facts in ForType(type, module.Name, IsOwnDeclaration(type, module.Name)))
                yield return facts;
        foreach (var protocol in module.Protocols)
            foreach (var facts in ForType(protocol, module.Name, IsOwnDeclaration(protocol, module.Name)))
                yield return facts;
    }

    private IEnumerable<SwiftCallFacts> ForType(TypeDecl type, string module, bool declaredHere)
    {
        // The metadata accessor belongs to the module that declares the type.
        if (type is not ProtocolDecl && declaredHere && MetadataAccessorSymbol(type) is { } accessor)
            yield return ForMetadataAccessor(accessor, type, module);
        foreach (var method in type.Methods)
        {
            // A protocol-extension member the generator injected into a conforming type is its own
            // wrapper function, not a Swift declaration of the type; its producer owes its facts.
            if (method.IsProtocolExtensionMethod)
                continue;
            yield return ForMethod(method, type, MemberContext.Plain);
        }
        foreach (var op in type.Operators)
            yield return ForMethod(op.UnderlyingMethod, type, MemberContext.Plain);
        foreach (var property in type.Properties)
            foreach (var facts in ForProperty(property, type))
                yield return facts;
        foreach (var subscript in type.Subscripts)
            foreach (var accessorDecl in subscript.Accessors)
                yield return ForMethod(accessorDecl.Method, type, new MemberContext(AccessorOf(accessorDecl), subscript.Name, subscript.AvailabilityAnnotations, IsSubscript: true));
        foreach (var nested in type.Types)
            foreach (var facts in ForType(nested, module, declaredHere: true))
                yield return facts;
    }

    private IEnumerable<SwiftCallFacts> ForProperty(PropertyDecl property, TypeDecl? parent)
    {
        foreach (var accessorDecl in property.Accessors)
        {
            var annotations = accessorDecl is SetAccessorDecl && property.SetterAvailabilityAnnotations is { } setter
                ? (IReadOnlyList<AvailabilityAnnotation>)setter.Concat(property.AvailabilityAnnotations ?? new()).ToList()
                : property.AvailabilityAnnotations;
            yield return ForMethod(accessorDecl.Method, parent, new MemberContext(AccessorOf(accessorDecl), property.Name, annotations, IsSubscript: false));
        }
    }

    private static SwiftAccessorKind AccessorOf(AccessorDecl accessor) =>
        accessor is SetAccessorDecl ? SwiftAccessorKind.Set : SwiftAccessorKind.Get;

    /// <summary>How a method is reached: directly, or as a property's or subscript's accessor.</summary>
    internal sealed record MemberContext(
        SwiftAccessorKind Accessor,
        string? StorageName,
        IReadOnlyList<AvailabilityAnnotation>? StorageAvailability,
        bool IsSubscript)
    {
        public static readonly MemberContext Plain = new(SwiftAccessorKind.None, null, null, false);
    }

    /// <summary>Builds the facts of one method, initializer or accessor.</summary>
    internal SwiftCallFacts ForMethod(MethodDecl method, TypeDecl? parent, MemberContext context)
    {
        var unknowns = new List<string>();
        var generics = GenericsOf(method, parent);
        var genericNames = GenericNamesOf(method, parent, generics);

        var kind = context.Accessor != SwiftAccessorKind.None ? SwiftCallTargetKind.Accessor
            : method.IsConstructor ? SwiftCallTargetKind.Initializer
            : parent is null ? SwiftCallTargetKind.FreeFunction
            : SwiftCallTargetKind.Method;

        var target = new SwiftCallTarget
        {
            Kind = kind,
            Accessor = context.Accessor,
            Entry = SwiftEntryKind.Implementation,
            Symbol = method.MangledName,
            Module = method.ModuleDecl?.Name ?? parent?.ModuleDecl?.Name,
            DeclaringType = parent?.SwiftTypeName.ModuleQualifiedName,
            MemberName = context.StorageName ?? method.GetSwiftName(),
            Availability = AvailabilityOf(method.AvailabilityAnnotations, context.StorageAvailability, parent),
            HasDispatchThunk = HasDispatchThunk(method, parent, kind),
        };

        var self = SelfOf(method, parent, context, unknowns);

        // CSSignature[0] is the result. A setter's value comes first in Swift order, then a
        // subscript's indices — the order the model already keeps.
        var parameters = ImmutableArray.CreateBuilder<SwiftParameterFacts>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var argument = method.CSSignature[i];
            var index = i - 1;
            var closure = ClosureKindOf(argument.SwiftTypeSpec);
            var (convention, source) = ConventionOf(argument, DefaultConventionOf(method, context, closure));
            parameters.Add(new SwiftParameterFacts
            {
                Index = index,
                Name = string.IsNullOrEmpty(argument.Name) ? NullIfEmpty(argument.PrivateName) : argument.Name,
                Type = ResolveType(argument.SwiftTypeSpec, genericNames, unknowns, $"parameter {index}"),
                Convention = convention,
                ConventionSource = source,
                Closure = closure,
                // The parser lowers `T...` to an array whose element carries the variadic mark.
                IsVariadic = SwiftABIParser.IsVariadicArraySpec(argument.SwiftTypeSpec),
            });
        }
        // The parser marks the element only where it can tell which parameter is `T...`.
        if (method.HasVariadicParameter && !parameters.Any(p => p.IsVariadic))
            unknowns.Add("parameters: the member is variadic, but the parser could not tell which parameter");

        var resultSpec = method.CSSignature.Count > 0 ? method.CSSignature[0].SwiftTypeSpec : null;
        var result = context.Accessor == SwiftAccessorKind.Set || resultSpec is null
            ? SwiftResultFacts.Void
            : new SwiftResultFacts
            {
                Type = ResolveType(resultSpec, genericNames, unknowns, "result"),
                IsFailableInitializer = method.IsConstructor && method.IsFailable,
            };

        var effects = new SwiftCallEffects
        {
            Throws = method.Throws || method.HasTypedThrows,
            TypedThrows = method.ThrownErrorType?.ToString(true),
            // The parser marks a synchronous actor-isolated member async so the binding awaits it;
            // the symbol itself is synchronous.
            IsAsync = method.IsAsync && !method.IsAsyncByIsolation,
            IsCoroutine = context.Accessor is SwiftAccessorKind.Modify or SwiftAccessorKind.Read,
            HasParameterPacks = HasParameterPacks(method, parent),
        };

        if (string.IsNullOrEmpty(method.MangledName))
            unknowns.Add("symbol: the declaration has no mangled name");

        return new SwiftCallFacts
        {
            Target = target,
            Self = self,
            Parameters = parameters.ToImmutable(),
            Result = result,
            Effects = effects,
            Generics = generics,
            Unknowns = unknowns.ToImmutableArray(),
        };
    }

    /// <summary>
    /// The convention a parameter takes when it does not spell one. An initializer's and a
    /// setter's parameters (a subscript setter's indices too) are consumed, every other function's
    /// are borrowed, and a non-escaping closure is always borrowed.
    /// </summary>
    private static SwiftParameterConvention DefaultConventionOf(MethodDecl method, MemberContext context, SwiftClosureKind closure)
    {
        if (closure == SwiftClosureKind.NonEscaping)
            return SwiftParameterConvention.Guaranteed;
        return method.IsConstructor || context.Accessor == SwiftAccessorKind.Set
            ? SwiftParameterConvention.Owned
            : SwiftParameterConvention.Guaranteed;
    }

    /// <summary>
    /// Whether the member is reached through a dispatch thunk (<c>…Tj</c>): a protocol
    /// requirement, or an overridable instance member of a class — not final, on a class that is
    /// not final, and not declared in an extension. A class's static, <c>class</c> and initializer
    /// entries are not derived: the model does not say which of them have a vtable entry.
    /// </summary>
    private bool HasDispatchThunk(MethodDecl method, TypeDecl? parent, SwiftCallTargetKind kind)
    {
        if (parent is null || kind is not (SwiftCallTargetKind.Method or SwiftCallTargetKind.Accessor or SwiftCallTargetKind.Initializer))
            return false;
        return Canonical(parent) switch
        {
            ProtocolDecl => !method.IsExtensionMethod || method.IsProtocolRequirement,
            ClassDecl c => !c.IsFinal && !method.IsFinal && !method.IsExtensionMethod
                && !method.IsConstructor && method.MethodType == MethodType.Instance,
            _ => false,
        };
    }

    private SwiftSelfFacts SelfOf(MethodDecl method, TypeDecl? parent, MemberContext context, List<string> unknowns)
    {
        if (parent is null)
            return SwiftSelfFacts.None;

        // An extension member's parent may be an extending module's partial declaration; what
        // self is comes from the defining module's.
        parent = Canonical(parent);
        var isStatic = method.MethodType == MethodType.Static;
        var selfType = SelfTypeOf(parent, unknowns);

        switch (parent)
        {
            case ClassDecl:
            {
                var abstraction = parent.IsGeneric ? SwiftSelfAbstraction.GenericParent : SwiftSelfAbstraction.Concrete;
                // A class has two initializer entries: the allocating one (fC) takes the metatype,
                // the initializing one (fc) the already-allocated instance.
                if (method.IsConstructor)
                {
                    var initializing = method.MangledName.EndsWith("fc", StringComparison.Ordinal);
                    return new SwiftSelfFacts
                    {
                        Kind = initializing ? SwiftCallSelfKind.Reference : SwiftCallSelfKind.ThickMetatype,
                        Mutation = initializing ? SwiftSelfMutation.Nonmutating : SwiftSelfMutation.NotApplicable,
                        Abstraction = abstraction,
                        Type = selfType,
                    };
                }
                if (isStatic)
                    return Metatype(SwiftCallSelfKind.ThickMetatype, abstraction, selfType);
                return new SwiftSelfFacts
                {
                    Kind = SwiftCallSelfKind.Reference,
                    Mutation = ReferenceMutationOf(method),
                    Abstraction = abstraction,
                    Type = selfType,
                };
            }

            case StructDecl or EnumDecl:
            {
                var abstraction = parent.IsGeneric ? SwiftSelfAbstraction.GenericParent : SwiftSelfAbstraction.Concrete;
                if (isStatic || method.IsConstructor)
                    return Metatype(SwiftCallSelfKind.ThinMetatype, abstraction, selfType);
                return new SwiftSelfFacts
                {
                    Kind = SwiftCallSelfKind.Value,
                    Mutation = ValueMutationOf(method, context),
                    Abstraction = abstraction,
                    Type = selfType,
                };
            }

            case ProtocolDecl protocol:
            {
                // A protocol member's self is the generic Self: a requirement is reached through
                // its dispatch thunk, an extension member is a generic function over Self.
                var abstraction = !method.IsProtocolRequirement && method.IsExtensionMethod
                    ? SwiftSelfAbstraction.ProtocolExtension
                    : SwiftSelfAbstraction.ProtocolRequirement;
                var genericSelf = new SwiftTypeFacts
                {
                    Spelling = "Self",
                    Category = SwiftTypeCategory.GenericParameter,
                    IsClassBound = protocol.IsClassBound,
                };
                if (isStatic || method.IsConstructor)
                    return Metatype(SwiftCallSelfKind.ThickMetatype, abstraction, genericSelf);
                return new SwiftSelfFacts
                {
                    Kind = protocol.IsClassBound ? SwiftCallSelfKind.Reference : SwiftCallSelfKind.Value,
                    Mutation = protocol.IsClassBound ? ReferenceMutationOf(method) : ValueMutationOf(method, context),
                    Abstraction = abstraction,
                    Type = genericSelf,
                };
            }

            default:
                unknowns.Add($"self: declaring type '{parent.SwiftTypeName}' is a {parent.GetType().Name}, which has no self rule");
                return new SwiftSelfFacts
                {
                    Kind = SwiftCallSelfKind.None,
                    Mutation = SwiftSelfMutation.NotApplicable,
                    Abstraction = SwiftSelfAbstraction.Concrete,
                    Type = selfType,
                };
        }
    }

    private static SwiftSelfFacts Metatype(SwiftCallSelfKind kind, SwiftSelfAbstraction abstraction, SwiftTypeFacts selfType) => new()
    {
        Kind = kind,
        Mutation = SwiftSelfMutation.NotApplicable,
        Abstraction = abstraction,
        Type = selfType,
    };

    /// <summary>
    /// A value self is borrowed unless the declaration says otherwise. A setter mutates self unless
    /// it is declared <c>nonmutating</c>.
    /// </summary>
    private static SwiftSelfMutation ValueMutationOf(MethodDecl method, MemberContext context)
    {
        if (method.IsConsuming)
            return SwiftSelfMutation.Consuming;
        if (method.IsBorrowing)
            return SwiftSelfMutation.Borrowing;
        if (method.IsMutating || method.IsDeclaredMutatingSubscriptGetter)
            return SwiftSelfMutation.Mutating;
        if (context.Accessor == SwiftAccessorKind.Set && !method.IsNonmutating)
            return SwiftSelfMutation.Mutating;
        return SwiftSelfMutation.Nonmutating;
    }

    /// <summary>A reference self is borrowed unless the member is <c>consuming</c>; it is never inout.</summary>
    private static SwiftSelfMutation ReferenceMutationOf(MethodDecl method) =>
        method.IsConsuming ? SwiftSelfMutation.Consuming
        : method.IsBorrowing ? SwiftSelfMutation.Borrowing
        : SwiftSelfMutation.Nonmutating;

    private SwiftTypeFacts SelfTypeOf(TypeDecl parent, List<string> unknowns) => parent switch
    {
        ClassDecl c => new SwiftTypeFacts { Spelling = parent.SwiftTypeName.ModuleQualifiedName, Category = SwiftTypeCategory.Class, IsClassBound = true },
        StructDecl s => new SwiftTypeFacts { Spelling = parent.SwiftTypeName.ModuleQualifiedName, Category = SwiftTypeCategory.Struct, IsFrozen = s.IsFrozen },
        EnumDecl e => new SwiftTypeFacts { Spelling = parent.SwiftTypeName.ModuleQualifiedName, Category = SwiftTypeCategory.Enum, IsFrozen = e.IsFrozen },
        ProtocolDecl p => new SwiftTypeFacts { Spelling = "Self", Category = SwiftTypeCategory.GenericParameter, IsClassBound = p.IsClassBound },
        _ => new SwiftTypeFacts { Spelling = parent.SwiftTypeName.ModuleQualifiedName, Category = SwiftTypeCategory.Unresolved },
    };

    private static (SwiftParameterConvention, SwiftConventionSource) ConventionOf(ArgumentDecl argument, SwiftParameterConvention defaultConvention)
    {
        if (argument.IsInOut || argument.Ownership == ParameterOwnership.InOut)
            return (SwiftParameterConvention.Inout, SwiftConventionSource.Explicit);
        return argument.Ownership switch
        {
            ParameterOwnership.Owned => (SwiftParameterConvention.Owned, SwiftConventionSource.Explicit),
            ParameterOwnership.Shared => (SwiftParameterConvention.Guaranteed, SwiftConventionSource.Explicit),
            _ => (defaultConvention, SwiftConventionSource.Default),
        };
    }

    private static SwiftClosureKind ClosureKindOf(TypeSpec spec) => spec switch
    {
        ClosureTypeSpec closure => closure.IsEscaping ? SwiftClosureKind.Escaping : SwiftClosureKind.NonEscaping,
        // An optional closure is always escaping: it is stored in an Optional.
        NamedTypeSpec { Name: "Swift.Optional" } optional when optional.GenericParameters.Count == 1
            && optional.GenericParameters[0] is ClosureTypeSpec => SwiftClosureKind.Escaping,
        _ => SwiftClosureKind.NotClosure,
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // ---- generics ----

    private static SwiftGenericSignatureFacts GenericsOf(MethodDecl method, TypeDecl? parent)
    {
        // A member's own signature already includes its context's parameters; an accessor of a
        // generic type's property may carry none, and then its context's is the one in scope.
        var raw = !string.IsNullOrWhiteSpace(method.RawGenericSig) ? method.RawGenericSig : RawSignatureOf(parent);
        if (string.IsNullOrWhiteSpace(raw))
            return SwiftGenericSignatureFacts.Empty;
        return GenericsOf(raw);
    }

    private static string? RawSignatureOf(TypeDecl? type)
    {
        for (var current = type; current is not null; current = current.ParentDecl as TypeDecl)
        {
            var raw = current is ProtocolDecl protocol ? protocol.GenericSignature : current.RawGenericSig;
            if (!string.IsNullOrWhiteSpace(raw))
                return raw;
        }
        return null;
    }

    private static HashSet<string> GenericNamesOf(MethodDecl method, TypeDecl? parent, SwiftGenericSignatureFacts generics)
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { "Self" };
        foreach (var p in generics.Parameters)
            names.Add(p);
        void AddAll(IEnumerable<GenericArgumentDecl> args)
        {
            foreach (var a in args)
            {
                names.Add(a.TypeName);
                if (!string.IsNullOrEmpty(a.SugaredTypeName))
                    names.Add(a.SugaredTypeName);
            }
        }
        AddAll(method.GenericParameters);
        for (var current = parent; current is not null; current = current.ParentDecl as TypeDecl)
            AddAll(current.GenericParameters);
        return names;
    }

    private static bool HasParameterPacks(MethodDecl method, TypeDecl? parent)
    {
        static bool HasEach(string? sig) => sig is not null && (sig.Contains("each ", StringComparison.Ordinal) || sig.Contains("repeat ", StringComparison.Ordinal));
        if (HasEach(method.RawGenericSig) || HasEach(RawSignatureOf(parent)))
            return true;
        return method.CSSignature.Any(a => HasEach(a.SwiftTypeSpec.ToString(true)));
    }

    // ---- availability ----

    private static ImmutableArray<SwiftAvailabilityFloor> AvailabilityOf(
        IReadOnlyList<AvailabilityAnnotation>? own,
        IReadOnlyList<AvailabilityAnnotation>? storage,
        TypeDecl? parent)
    {
        var floors = new Dictionary<string, string>(StringComparer.Ordinal);
        void Merge(IEnumerable<AvailabilityAnnotation>? annotations)
        {
            if (annotations is null)
                return;
            foreach (var a in annotations)
            {
                if (a.IsRuntimeSupportFloor || string.IsNullOrEmpty(a.Platform) || string.IsNullOrEmpty(a.IntroducedVersion) || a.Platform == "*")
                    continue;
                if (!floors.TryGetValue(a.Platform, out var existing) || CompareVersions(a.IntroducedVersion, existing) > 0)
                    floors[a.Platform] = a.IntroducedVersion;
            }
        }
        Merge(own);
        Merge(storage);
        for (var current = parent; current is not null; current = current.ParentDecl as TypeDecl)
            Merge(current.AvailabilityAnnotations);
        return floors
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new SwiftAvailabilityFloor(kv.Key, kv.Value))
            .ToImmutableArray();
    }

    private static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int va = i < pa.Length && int.TryParse(pa[i], out var x) ? x : 0;
            int vb = i < pb.Length && int.TryParse(pb[i], out var y) ? y : 0;
            if (va != vb)
                return va.CompareTo(vb);
        }
        return 0;
    }

    // ---- types ----

    /// <summary>Resolves a Swift type to its category, recording an unknown if it cannot.</summary>
    internal SwiftTypeFacts ResolveType(TypeSpec spec, IReadOnlySet<string> genericNames, List<string> unknowns, string where)
    {
        var facts = Resolve(spec, genericNames);
        if (facts.Category == SwiftTypeCategory.Unresolved)
            unknowns.Add($"{where}: type '{facts.Spelling}' is not known to the module or the type database");
        return facts;
    }

    private SwiftTypeFacts Resolve(TypeSpec spec, IReadOnlySet<string> genericNames)
    {
        var spelling = spec.ToString(true);
        switch (spec)
        {
            case TupleTypeSpec tuple:
                if (tuple.IsEmptyTuple)
                    return SwiftTypeFacts.Void;
                if (tuple.Elements.Count == 1)
                    return Resolve(tuple.Elements[0], genericNames);
                return new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Tuple };
            case ClosureTypeSpec:
                return new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Closure };
            case ProtocolListTypeSpec list:
            {
                // A composition is class-bound when any member is: AnyObject, a class-bound
                // protocol, or a superclass constraint. A member neither database knows leaves
                // that open, so the composition is not described at all.
                var members = list.Protocols.Keys.Select(ClassBoundnessOf).ToList();
                if (members.Any(m => m is null))
                    return new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Unresolved };
                return new SwiftTypeFacts
                {
                    Spelling = spelling,
                    // `some P` is one concrete type the callee chooses, not a box.
                    Category = list.IsOpaque ? SwiftTypeCategory.Opaque : SwiftTypeCategory.Existential,
                    IsClassBound = members.Any(m => m == true),
                };
            }
            case AssociatedTypeReferenceSpec:
                return new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.GenericParameter };
            case NamedTypeSpec named:
                return ResolveNamed(named, spelling, genericNames);
            default:
                return new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Unresolved };
        }
    }

    private SwiftTypeFacts ResolveNamed(NamedTypeSpec named, string spelling, IReadOnlySet<string> genericNames)
    {
        // A nested type below a generic parent keeps its inner names in the InnerType chain
        // (Module.Outer<T>.Inner), so the full name is rebuilt from it.
        var name = FullNameOf(named);

        if (name is "Swift.Void" or "Void" or "()")
            return SwiftTypeFacts.Void;

        // A metatype first: T.Type is a metatype even when T is a generic parameter.
        if (name.EndsWith(".Type", StringComparison.Ordinal) || name.EndsWith(".Protocol", StringComparison.Ordinal))
            return new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Metatype };

        // A generic parameter, or a dependent member of one (τ_0_0.Element, Self.Item).
        var root = name.Split('.')[0];
        if (genericNames.Contains(name) || genericNames.Contains(root) || name.StartsWith("τ_", StringComparison.Ordinal))
            return new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.GenericParameter };

        if (name is "Any" or "Swift.Any")
            return new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Existential };
        if (name is "AnyObject" or "Swift.AnyObject")
            return new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Existential, IsClassBound = true };

        if (_typesByName.TryGetValue(name, out var entry))
            return FromDecl(entry.Decl, spelling);

        if (TryPeekRecord(name, out var record))
            return FromRecord(record, spelling);

        return new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Unresolved };
    }

    /// <summary>The module-qualified name with the InnerType chain's names appended, without generic arguments.</summary>
    private static string FullNameOf(NamedTypeSpec named)
    {
        var name = named.Name;
        for (var inner = named.InnerType; inner is not null; inner = inner.InnerType)
            name += "." + inner.Name;
        return name;
    }

    /// <summary>
    /// A record a loaded database holds, found without resolving the name for emission: the
    /// facts describe declarations, so looking a type up must not record a package reference or
    /// accept a resolver's fallback projection as what the type is.
    /// </summary>
    private bool TryPeekRecord(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TypeRecord? record)
    {
        record = null;
        return SwiftTypeName.TryFromModuleQualifiedName(name, out var typeName)
            && _typeDatabase.TryPeekTypeRecord(typeName, out record);
    }

    /// <summary>Whether a composition member forces a class-bound composition; null when the member is not known.</summary>
    private bool? ClassBoundnessOf(NamedTypeSpec member)
    {
        var name = FullNameOf(member);
        if (name is "AnyObject" or "Swift.AnyObject")
            return true;
        var facts = _typesByName.TryGetValue(name, out var entry) ? FromDecl(entry.Decl, name)
            : TryPeekRecord(name, out var record) ? FromRecord(record, name)
            : null;
        return facts?.Category switch
        {
            SwiftTypeCategory.Class => true,
            SwiftTypeCategory.Existential => facts.IsClassBound,
            _ => null,
        };
    }

    private static SwiftTypeFacts FromDecl(TypeDecl decl, string spelling) => decl switch
    {
        ClassDecl => new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Class, IsClassBound = true },
        StructDecl s => new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Struct, IsFrozen = s.IsFrozen },
        EnumDecl e => new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Enum, IsFrozen = e.IsFrozen },
        ProtocolDecl p => new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Existential, IsClassBound = p.IsClassBound },
        _ => new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Unresolved },
    };

    private static SwiftTypeFacts FromRecord(TypeRecord record, string spelling)
    {
        var frozen = (record.Flags & TypeRecordFlags.Frozen) != 0;
        return record.Kind switch
        {
            TypeRecordKind.Class => new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Class, IsClassBound = true },
            TypeRecordKind.Struct => new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Struct, IsFrozen = frozen },
            TypeRecordKind.Enum => new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Enum, IsFrozen = frozen },
            TypeRecordKind.Protocol or TypeRecordKind.Existential => new SwiftTypeFacts
            {
                Spelling = spelling,
                Category = SwiftTypeCategory.Existential,
                IsClassBound = (record.Flags & TypeRecordFlags.ClassBound) != 0,
            },
            _ => new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Unresolved },
        };
    }

    // ---- metadata accessors ----

    private static string? MetadataAccessorSymbol(TypeDecl type) => type switch
    {
        StructDecl s when !string.IsNullOrEmpty(s.MetadataAccessor) => s.MetadataAccessor,
        EnumDecl e when !string.IsNullOrEmpty(e.MetadataAccessor) => e.MetadataAccessor,
        _ when !string.IsNullOrEmpty(type.MangledName) => type.MangledName + "Ma",
        _ => null,
    };

    /// <summary>
    /// A type-metadata accessor takes the metadata request and the type's key arguments — one
    /// metadata per generic parameter and one witness table per conformance requirement, in
    /// signature order — and returns the metadata response. The key arguments are the
    /// <see cref="SwiftCallFacts.Generics"/>; how many travel in registers is the planner's call.
    /// </summary>
    private static SwiftCallFacts ForMetadataAccessor(string symbol, TypeDecl type, string declaringModule)
    {
        var raw = RawSignatureOf(type);
        var generics = string.IsNullOrWhiteSpace(raw) ? SwiftGenericSignatureFacts.Empty : GenericsOf(raw);
        return MetadataAccessorFacts(symbol, declaringModule, type.SwiftTypeName.ModuleQualifiedName, generics, AvailabilityOf(null, null, type));
    }

    private static SwiftGenericSignatureFacts GenericsOf(string raw)
    {
        var model = GenericSignatureParser.ParseSignature(raw);
        // A parameter constrained inline (`<τ_0_0 : P>`) appears only as a requirement's subject.
        var parameters = new List<string>(model.Parameters);
        foreach (var requirement in model.Requirements)
        {
            var root = requirement.SubjectRoot;
            if (requirement.IsDirect && root.Length > 0 && !parameters.Contains(root))
                parameters.Add(root);
        }
        return new SwiftGenericSignatureFacts
        {
            Parameters = parameters.ToImmutableArray(),
            Requirements = model.Requirements
                .Select(r => new SwiftGenericRequirementFacts(
                    string.Join(".", r.Subject),
                    r.Target,
                    r.Kind == GenericRequirementKind.SameType ? SwiftGenericRequirementKind.SameType : SwiftGenericRequirementKind.Conformance))
                .ToImmutableArray(),
        };
    }

    private static SwiftCallFacts MetadataAccessorFacts(
        string symbol, string? module, string type, SwiftGenericSignatureFacts generics, ImmutableArray<SwiftAvailabilityFloor> availability) => new()
    {
        Target = new SwiftCallTarget
        {
            Kind = SwiftCallTargetKind.MetadataAccessor,
            Symbol = symbol,
            Module = module,
            DeclaringType = type,
            Availability = availability,
        },
        Self = SwiftSelfFacts.None,
        Parameters = ImmutableArray.Create(new SwiftParameterFacts
        {
            Index = 0,
            Name = "request",
            Type = SwiftTypeFacts.Word("MetadataRequest"),
            Convention = SwiftParameterConvention.Guaranteed,
            ConventionSource = SwiftConventionSource.Default,
        }),
        // MetadataResponse is a two-word aggregate: the metadata and its state.
        Result = new SwiftResultFacts
        {
            Type = new SwiftTypeFacts { Spelling = "MetadataResponse", Category = SwiftTypeCategory.Struct, IsFrozen = true },
        },
        Effects = SwiftCallEffects.None,
        Generics = generics,
    };

    /// <summary>
    /// The metadata accessors of the standard-library generic types the generator binds directly.
    /// They have no declaration in any module the generator parses, so their signatures are
    /// written here, as Swift declares them.
    /// </summary>
    internal static IEnumerable<SwiftCallFacts> StandardLibraryMetadataAccessors()
    {
        yield return MetadataAccessorFacts("$sSqMa", "Swift", "Swift.Optional", GenericsOf("<τ_0_0>"), ImmutableArray<SwiftAvailabilityFloor>.Empty);
        yield return MetadataAccessorFacts("$sSaMa", "Swift", "Swift.Array", GenericsOf("<τ_0_0>"), ImmutableArray<SwiftAvailabilityFloor>.Empty);
        yield return MetadataAccessorFacts("$sShMa", "Swift", "Swift.Set", GenericsOf("<τ_0_0 where τ_0_0 : Swift.Hashable>"), ImmutableArray<SwiftAvailabilityFloor>.Empty);
        yield return MetadataAccessorFacts("$sSDMa", "Swift", "Swift.Dictionary", GenericsOf("<τ_0_0, τ_0_1 where τ_0_0 : Swift.Hashable>"), ImmutableArray<SwiftAvailabilityFloor>.Empty);
    }
}
