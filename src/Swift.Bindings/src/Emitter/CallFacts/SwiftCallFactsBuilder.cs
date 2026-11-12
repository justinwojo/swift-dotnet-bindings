// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using BindingsGeneration.Demangling;

namespace BindingsGeneration;

/// <summary>
/// Derives <see cref="SwiftCallFacts"/> from the parser's model of a module: what Swift declared for
/// each member, read from the ABI JSON through the model's typed fields. Nothing here reads emitted
/// C#, and nothing here decides a physical outcome.
/// </summary>
public sealed class SwiftCallFactsBuilder
{
    private readonly ITypeDatabase? _typeDatabase;
    private readonly Dictionary<string, (TypeDecl Decl, bool IsOwn)> _typesByName = new(StringComparer.Ordinal);
    private readonly List<string> _moduleNames = new();
    private readonly Swift5Demangler _demangler = new();

    public SwiftCallFactsBuilder(IEnumerable<ModuleDecl> modules, ITypeDatabase? typeDatabase)
    {
        _typeDatabase = typeDatabase;
        foreach (var module in modules)
        {
            _moduleNames.Add(module.Name);
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
            yield return ForMethod(accessorDecl.Method, parent, new MemberContext(AccessorOf(accessorDecl), property.Name, annotations, IsSubscript: false, IsLazyStorage: property.IsLazy));
        }
    }

    private static SwiftAccessorKind AccessorOf(AccessorDecl accessor) =>
        accessor is SetAccessorDecl ? SwiftAccessorKind.Set : SwiftAccessorKind.Get;

    /// <summary>How a method is reached: directly, or as a property's or subscript's accessor.</summary>
    internal sealed record MemberContext(
        SwiftAccessorKind Accessor,
        string? StorageName,
        IReadOnlyList<AvailabilityAnnotation>? StorageAvailability,
        bool IsSubscript,
        bool IsLazyStorage = false)
    {
        public static readonly MemberContext Plain = new(SwiftAccessorKind.None, null, null, false);
    }

    /// <summary>Builds the facts of one method, initializer or accessor.</summary>
    internal SwiftCallFacts ForMethod(MethodDecl method, TypeDecl? parent, MemberContext context)
    {
        var unknowns = new List<string>();
        var generics = ClassifyRequirements(GenericsOf(method, parent));
        var genericNames = GenericNamesOf(method, parent, generics);
        // The ABI descriptor prints every function representation alike; the symbol says which.
        var closures = UniformRepresentation(_demangler.FunctionTypeKindsInSignature(method.MangledName));

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
                Type = ResolveType(argument.SwiftTypeSpec, genericNames, unknowns, $"parameter {index}", generics: generics, closures: closures),
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
                Type = DynamicSelfOf(ResolveType(resultSpec, genericNames, unknowns, "result", generics: generics, closures: closures), parent),
                IsFailableInitializer = method.IsConstructor && method.IsFailable,
            };

        // The symbol says whether the callee throws a typed error even when the interface facts that
        // name the error type are missing; the type itself, when only the symbol has it, is spelled
        // from the demangled tree.
        var typedThrows = method.ThrownErrorType?.ToString(true);
        if (typedThrows is null && method.MangledName is { Length: > 0 } mangled
            && _demangler.TryGetTypedThrows(mangled, out var demangledError))
        {
            typedThrows = demangledError?.ToString(true);
            if (typedThrows is null)
                unknowns.Add("effects: the symbol throws a typed error whose type could not be spelled");
        }

        var effects = new SwiftCallEffects
        {
            Throws = method.Throws || method.HasTypedThrows || typedThrows is not null,
            TypedThrows = typedThrows,
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
    /// Builds the facts of a function the generator synthesized into its wrapper, from the
    /// declaration its producer wrote. The types resolve exactly as a declared member's do.
    /// </summary>
    internal SwiftCallFacts ForSynthetic(SyntheticSwiftFunction function)
    {
        var unknowns = new List<string>();
        // A function in an extension of a generic type is generic over the type's parameters too.
        var outer = string.IsNullOrWhiteSpace(function.DeclaringTypeGenericSignature) ? SwiftGenericSignatureFacts.Empty : GenericsOf(function.DeclaringTypeGenericSignature);
        var own = string.IsNullOrWhiteSpace(function.GenericSignature) ? SwiftGenericSignatureFacts.Empty : GenericsOf(function.GenericSignature);
        var generics = ClassifyRequirements(MergeGenerics(outer, own));
        if (!generics.IsEmpty)
        {
            generics = generics with
            {
                OwnParameters = own.Parameters.Where(p => !outer.Parameters.Contains(p)).ToImmutableArray(),
                Requirements = generics.Requirements
                    .Select(r => r with { IsInherited = outer.Requirements.Any(o => o.Subject == r.Subject && o.Target == r.Target && o.Kind == r.Kind) })
                    .ToImmutableArray(),
            };
        }
        var genericNames = new HashSet<string>(StringComparer.Ordinal) { "Self" };
        foreach (var p in generics.Parameters)
            genericNames.Add(p);
        foreach (var p in function.DeclaringTypeGenericParameters)
            genericNames.Add(p);
        var genericParent = !string.IsNullOrWhiteSpace(function.DeclaringTypeGenericSignature)
            || function.DeclaringTypeGenericParameters.Length > 0;
        // Without the parent's signature the metadata and witness tables the callee takes are not known.
        if (genericParent && string.IsNullOrWhiteSpace(function.DeclaringTypeGenericSignature))
            unknowns.Add($"self: '{function.DeclaringType}' is generic but its generic signature is not known");

        var writtenIn = new List<string> { function.Module };
        writtenIn.AddRange(_moduleNames);

        (SwiftTypeFacts Facts, TypeSpec? Spec) Written(string text, string where)
        {
            TypeSpec? spec;
            try
            {
                spec = TypeSpecParser.Parse(BracketAttributeArguments(text));
            }
            catch (TypeSpecParseException)
            {
                spec = null;
            }
            if (spec is null)
            {
                unknowns.Add($"{where}: the written type '{text}' does not parse");
                return (new SwiftTypeFacts { Spelling = text, Category = SwiftTypeCategory.Unresolved }, null);
            }
            // Source the generator wrote spells a function type's representation when it is not thick.
            return (ResolveType(spec, genericNames, unknowns, where, writtenIn, generics, SwiftFunctionRepresentation.Thick), spec);
        }

        var self = function.Self.Kind == SwiftCallSelfKind.None
            ? SwiftSelfFacts.None
            : new SwiftSelfFacts
            {
                Kind = function.Self.Kind,
                Mutation = function.Self.Mutation,
                Abstraction = genericParent ? SwiftSelfAbstraction.GenericParent : SwiftSelfAbstraction.Concrete,
                Type = function.Self.Type is { } selfType ? Written(selfType, "self").Facts : null,
            };

        var parameters = ImmutableArray.CreateBuilder<SwiftParameterFacts>(function.Parameters.Length);
        for (int i = 0; i < function.Parameters.Length; i++)
        {
            var written = function.Parameters[i];
            var (type, spec) = Written(written.SwiftType, $"parameter {i}");
            var inout = spec?.IsInOut == true;
            var convention = written.Convention
                ?? (inout ? SwiftParameterConvention.Inout : SwiftParameterConvention.Guaranteed);
            parameters.Add(new SwiftParameterFacts
            {
                Index = i,
                Name = NullIfEmpty(written.Name),
                Type = type,
                Convention = convention,
                ConventionSource = written.Convention is not null || inout ? SwiftConventionSource.Explicit : SwiftConventionSource.Default,
                Closure = spec is null ? SwiftClosureKind.NotClosure : ClosureKindOf(spec),
            });
        }

        // In an extension of a class, a written Self is the dynamic class type, as for the member
        // the function forwards to: one reference, not an address-only generic value.
        var result = function.ResultType is { } resultType
            ? new SwiftResultFacts { Type = DynamicSelfOf(Written(resultType, "result").Facts, function.DeclaringTypeIsClass) }
            : SwiftResultFacts.Void;
        if (result.Type.Category == SwiftTypeCategory.Void)
            result = SwiftResultFacts.Void;

        if (string.IsNullOrEmpty(function.Symbol))
            unknowns.Add("symbol: the synthesized function has no symbol");

        return new SwiftCallFacts
        {
            Target = new SwiftCallTarget
            {
                Kind = SwiftCallTargetKind.SyntheticWrapper,
                Symbol = function.Symbol,
                Module = function.Module,
                DeclaringType = function.DeclaringType,
                MemberName = function.MemberName,
                Availability = function.Availability.IsDefault ? ImmutableArray<SwiftAvailabilityFloor>.Empty : function.Availability,
                Synthesizer = function.Synthesizer,
                Forwards = function.Forwards,
            },
            Self = self,
            Parameters = parameters.ToImmutable(),
            Result = result,
            Effects = new SwiftCallEffects { Throws = function.Throws, IsAsync = function.IsAsync },
            Generics = generics,
            Unknowns = unknowns.ToImmutableArray(),
        };
    }

    /// <summary>
    /// The facts of a call through a Swift closure's function pointer, read from the closure's
    /// function type: what Swift passes to a reverse callback the generator hands it, or what a
    /// forward <c>calli</c> passes to a closure Swift returned. Self is the closure context. The
    /// arguments are borrowed unless the type spells <c>inout</c> or an ownership modifier, and the
    /// result is owned. A closure invocation takes no generic arguments of its own (its context
    /// captured them), so a generic parameter in its type is only a type fact.
    /// </summary>
    /// <param name="pointerSource">The stable name the facts are identified by.</param>
    /// <param name="genericNames">The generic parameter names in scope where the closure type is spelled.</param>
    /// <param name="module">The module the closure type is spelled in; its unqualified names resolve there first.</param>
    internal SwiftCallFacts ForClosure(ClosureTypeSpec closure, string pointerSource, IEnumerable<string> genericNames, string? module)
    {
        ArgumentNullException.ThrowIfNull(closure);
        var unknowns = new List<string>();
        var names = new HashSet<string>(genericNames, StringComparer.Ordinal) { "Self" };
        var writtenIn = new List<string>();
        if (!string.IsNullOrEmpty(module))
            writtenIn.Add(module);
        writtenIn.AddRange(_moduleNames);

        if (IsCFunctionPointer(closure))
            unknowns.Add("closure: a @convention(c) function type has no context and is not a Swift closure call");

        var parameters = ImmutableArray.CreateBuilder<SwiftParameterFacts>();
        var index = 0;
        string? modifier = null;
        foreach (var argument in closure.EachArgument())
        {
            // The type-spec grammar has no field for an ownership modifier: `(borrowing T) -> U` arrives
            // as two arguments, the bare modifier and then the type it applies to.
            if (OwnershipModifierOf(argument) is { } spelled)
            {
                if (modifier is not null)
                    unknowns.Add($"closure parameter {index}: ownership modifier '{modifier}' is followed by another modifier");
                modifier = spelled;
                continue;
            }
            var (convention, source) = ClosureArgumentConventionOf(argument, modifier);
            modifier = null;
            parameters.Add(new SwiftParameterFacts
            {
                Index = index,
                Type = ResolveType(argument, names, unknowns, $"closure parameter {index}", writtenIn),
                Convention = convention,
                ConventionSource = source,
                Closure = ClosureKindOf(argument),
            });
            index++;
        }
        if (modifier is not null)
            unknowns.Add($"closure parameter {index}: ownership modifier '{modifier}' applies to no parameter");

        var resultType = ResolveType(closure.ReturnType, names, unknowns, "closure result", writtenIn);
        var result = resultType.Category == SwiftTypeCategory.Void ? SwiftResultFacts.Void : new SwiftResultFacts { Type = resultType };

        return new SwiftCallFacts
        {
            Target = new SwiftCallTarget
            {
                Kind = SwiftCallTargetKind.ClosureInvocation,
                Entry = SwiftEntryKind.FunctionPointer,
                PointerSource = pointerSource,
                Module = string.IsNullOrEmpty(module) ? null : module,
            },
            Self = new SwiftSelfFacts
            {
                Kind = SwiftCallSelfKind.ClosureContext,
                Mutation = SwiftSelfMutation.NotApplicable,
                Abstraction = SwiftSelfAbstraction.Concrete,
            },
            Parameters = parameters.ToImmutable(),
            Result = result,
            Effects = new SwiftCallEffects { Throws = closure.Throws, IsAsync = closure.IsAsync },
            Unknowns = unknowns.ToImmutableArray(),
        };
    }

    /// <summary>
    /// A closure argument's convention. A function type's parameters are borrowed by default;
    /// <c>inout</c> and an explicit ownership modifier are the only other spellings.
    /// </summary>
    private static (SwiftParameterConvention, SwiftConventionSource) ClosureArgumentConventionOf(TypeSpec argument, string? modifier)
    {
        if (argument.IsInOut)
            return (SwiftParameterConvention.Inout, SwiftConventionSource.Explicit);
        return modifier switch
        {
            "__owned" or "consuming" => (SwiftParameterConvention.Owned, SwiftConventionSource.Explicit),
            "__shared" or "borrowing" => (SwiftParameterConvention.Guaranteed, SwiftConventionSource.Explicit),
            _ => (SwiftParameterConvention.Guaranteed, SwiftConventionSource.Default),
        };
    }

    private static readonly string[] OwnershipModifiers = { "__owned", "consuming", "__shared", "borrowing" };

    /// <summary>The ownership modifier an argument is, when the type-spec parser split one off as a bare name.</summary>
    private static string? OwnershipModifierOf(TypeSpec argument) =>
        argument is NamedTypeSpec { GenericParameters.Count: 0 } named && !argument.IsInOut && argument.TypeLabel is null
            ? OwnershipModifiers.FirstOrDefault(m => named.Name == m)
            : null;

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
    /// requirement, or an overridable member of a class — an instance member or a <c>class</c>
    /// member, not final, on a class that is not final, and not declared in an extension. The ABI
    /// marks a class's <c>static</c> member final, so it has no vtable entry and no thunk; nor does
    /// an initializer, which binds its own exported entry. These are the gates
    /// <see cref="SwiftCallTargetResolver"/> applies when it binds a member through its thunk.
    /// </summary>
    private bool HasDispatchThunk(MethodDecl method, TypeDecl? parent, SwiftCallTargetKind kind)
    {
        if (parent is null || kind is not (SwiftCallTargetKind.Method or SwiftCallTargetKind.Accessor or SwiftCallTargetKind.Initializer))
            return false;
        return Canonical(parent) switch
        {
            ProtocolDecl => !method.IsExtensionMethod || method.IsProtocolRequirement,
            ClassDecl c => !c.IsFinal && !method.IsFinal && !method.IsExtensionMethod
                && !method.IsConstructor,
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
                var classBound = IsClassBound(protocol);
                var genericSelf = new SwiftTypeFacts
                {
                    Spelling = "Self",
                    Category = SwiftTypeCategory.GenericParameter,
                    IsClassBound = classBound,
                };
                if (isStatic || method.IsConstructor)
                    return Metatype(SwiftCallSelfKind.ThickMetatype, abstraction, genericSelf);
                return new SwiftSelfFacts
                {
                    Kind = classBound ? SwiftCallSelfKind.Reference : SwiftCallSelfKind.Value,
                    Mutation = classBound ? ReferenceMutationOf(method) : ValueMutationOf(method, context),
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

    /// <summary>
    /// A class member's <c>Self</c> is the dynamic type of its class instance: one reference, like
    /// a generic parameter bound to the class.
    /// </summary>
    private SwiftTypeFacts DynamicSelfOf(SwiftTypeFacts type, TypeDecl? parent) =>
        DynamicSelfOf(type, parent is not null && Canonical(parent) is ClassDecl);

    private static SwiftTypeFacts DynamicSelfOf(SwiftTypeFacts type, bool inClass) =>
        type is { Category: SwiftTypeCategory.GenericParameter, Spelling: "Self" } && inClass
            ? type with { IsClassBound = true }
            : type;

    private static SwiftSelfFacts Metatype(SwiftCallSelfKind kind, SwiftSelfAbstraction abstraction, SwiftTypeFacts selfType) => new()
    {
        Kind = kind,
        Mutation = SwiftSelfMutation.NotApplicable,
        Abstraction = abstraction,
        Type = selfType,
    };

    /// <summary>
    /// A value self is borrowed unless the declaration says otherwise. A setter mutates self unless
    /// it is declared <c>nonmutating</c>; a lazy property's getter mutates it too, since it stores
    /// the value it computes.
    /// </summary>
    private static SwiftSelfMutation ValueMutationOf(MethodDecl method, MemberContext context)
    {
        if (method.IsConsuming)
            return SwiftSelfMutation.Consuming;
        if (method.IsBorrowing)
            return SwiftSelfMutation.Borrowing;
        if (method.IsMutating)
            return SwiftSelfMutation.Mutating;
        if (context.Accessor == SwiftAccessorKind.Set && !method.IsNonmutating)
            return SwiftSelfMutation.Mutating;
        if (context.Accessor == SwiftAccessorKind.Get && context.IsLazyStorage)
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
        ProtocolDecl p => new SwiftTypeFacts { Spelling = "Self", Category = SwiftTypeCategory.GenericParameter, IsClassBound = IsClassBound(p) },
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
        // A C function pointer carries no context, so nothing escapes with it.
        ClosureTypeSpec closure when IsCFunctionPointer(closure) => SwiftClosureKind.NotClosure,
        ClosureTypeSpec closure => closure.IsEscaping ? SwiftClosureKind.Escaping : SwiftClosureKind.NonEscaping,
        // An optional closure is always escaping: it is stored in an Optional.
        NamedTypeSpec { Name: "Swift.Optional" } optional when optional.GenericParameters.Count == 1
            && optional.GenericParameters[0] is ClosureTypeSpec => SwiftClosureKind.Escaping,
        _ => SwiftClosureKind.NotClosure,
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>
    /// Rewrites a Swift source attribute's arguments, <c>@convention(c)</c>, into the bracketed
    /// form the type-spec grammar reads, <c>@convention[c]</c>. Parentheses that do not follow an
    /// attribute name directly are left alone.
    /// </summary>
    internal static string BracketAttributeArguments(string text)
    {
        if (!text.Contains('@'))
            return text;
        var sb = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            sb.Append(c);
            i++;
            if (c != '@')
                continue;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                sb.Append(text[i++]);
            if (i >= text.Length || text[i] != '(')
                continue;
            int depth = 0, start = i;
            for (; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')' && --depth == 0) break;
            }
            if (i >= text.Length)
                return text; // unbalanced: leave it for the parser to reject
            sb.Append('[').Append(text, start + 1, i - start - 1).Append(']');
            i++;
        }
        return sb.ToString();
    }

    // ---- generics ----

    private static SwiftGenericSignatureFacts GenericsOf(MethodDecl method, TypeDecl? parent)
    {
        // A member's own signature already includes its context's parameters; an accessor of a
        // generic type's property may carry none, and then its context's is the one in scope. A
        // protocol member's context is Self and its conformance to the protocol, which the witness
        // table supplies.
        var context = parent is ProtocolDecl protocol ? ProtocolContextSignature(protocol, method.RawGenericSig) : RawSignatureOf(parent);
        var raw = !string.IsNullOrWhiteSpace(method.RawGenericSig) ? method.RawGenericSig : context;
        if (string.IsNullOrWhiteSpace(raw))
            return SwiftGenericSignatureFacts.Empty;
        var generics = GenericsOf(raw);
        var inherited = string.IsNullOrWhiteSpace(context) ? SwiftGenericSignatureFacts.Empty : GenericsOf(context);
        return generics with
        {
            OwnParameters = generics.Parameters.Where(p => !inherited.Parameters.Contains(p)).ToImmutableArray(),
            Requirements = generics.Requirements.Select(r => r with { IsInherited = inherited.Requirements.Contains(r) }).ToImmutableArray(),
        };
    }

    /// <summary>An outer context's signature followed by a member's own: parameters outermost first.</summary>
    private static SwiftGenericSignatureFacts MergeGenerics(SwiftGenericSignatureFacts outer, SwiftGenericSignatureFacts own)
    {
        if (outer.IsEmpty && outer.Requirements.IsDefaultOrEmpty)
            return own;
        if (own.IsEmpty && own.Requirements.IsDefaultOrEmpty)
            return outer;
        return new SwiftGenericSignatureFacts
        {
            Parameters = outer.Parameters.Concat(own.Parameters).Distinct(StringComparer.Ordinal).ToImmutableArray(),
            Requirements = outer.Requirements.Concat(own.Requirements).Distinct().ToImmutableArray(),
        };
    }

    /// <summary>
    /// The signature a protocol's members are written in: <c>&lt;τ_0_0 where τ_0_0 : M.P&gt;</c>, or
    /// with <c>Self</c> in its place when the member's own signature spells it so, as the digester does.
    /// </summary>
    private static string ProtocolContextSignature(ProtocolDecl protocol, string? memberSignature)
    {
        var self = !string.IsNullOrWhiteSpace(memberSignature)
            && GenericSignatureParser.ParseSignature(memberSignature).Parameters is ["Self", ..] ? "Self" : "τ_0_0";
        return $"<{self} where {self} : {protocol.SwiftTypeName.ModuleQualifiedName}>";
    }

    internal static string? RawSignatureOf(TypeDecl? type)
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

    internal static ImmutableArray<SwiftAvailabilityFloor> AvailabilityOf(
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
    /// <param name="writtenIn">
    /// For a type a producer wrote into Swift source, the modules whose names that source sees
    /// unqualified, the function's own first. Null for a declared type, which the ABI JSON always
    /// spells module-qualified.
    /// </param>
    /// <param name="generics">The generic signature in scope, which says whether a generic parameter is class-bound.</param>
    /// <param name="closures">
    /// The representation of a function type the spec spells without a <c>@convention</c>
    /// attribute, when the source records it.
    /// </param>
    internal SwiftTypeFacts ResolveType(TypeSpec spec, IReadOnlySet<string> genericNames, List<string> unknowns, string where,
        IReadOnlyList<string>? writtenIn = null, SwiftGenericSignatureFacts? generics = null, SwiftFunctionRepresentation? closures = null)
    {
        var facts = Resolve(spec, genericNames, writtenIn);
        if (facts.Category == SwiftTypeCategory.Unresolved)
        {
            unknowns.Add(spec is NamedTypeSpec named && IsAmbiguousMetatypeSpelling(FullNameOf(named))
                ? $"{where}: '{facts.Spelling}' names both a metatype and a nested type, which the parsed type spelling does not tell apart"
                : $"{where}: type '{facts.Spelling}' is not known to the module or the type database");
        }
        if (facts.Category == SwiftTypeCategory.GenericParameter && generics?.IsClassBound(facts.Spelling) == true)
            facts = facts with { IsClassBound = true };
        if (RepresentationOf(spec, closures) is { } representation)
            facts = facts with { Representation = representation };
        return facts;
    }

    /// <summary>
    /// The representation of every function type <paramref name="spec"/> spells without a C
    /// convention: the one its <c>@convention</c> attribute names, else <paramref name="unattributed"/>.
    /// Null when it spells none, or when they differ or are not known.
    /// </summary>
    /// <summary>
    /// The one representation every function type in a signature has, from the symbol's function
    /// type kinds; null when the symbol does not demangle or the signature mixes representations.
    /// </summary>
    internal static SwiftFunctionRepresentation? UniformRepresentation(IReadOnlyList<NodeKind>? kinds)
    {
        if (kinds is null)
            return null;
        var found = kinds.Select(kind => kind switch
        {
            NodeKind.FunctionType or NodeKind.NoEscapeFunctionType or NodeKind.AutoClosureType or NodeKind.EscapingAutoClosureType =>
                SwiftFunctionRepresentation.Thick,
            NodeKind.ThinFunctionType => SwiftFunctionRepresentation.Thin,
            NodeKind.ObjCBlock => SwiftFunctionRepresentation.Block,
            NodeKind.CFunctionPointer => SwiftFunctionRepresentation.C,
            _ => (SwiftFunctionRepresentation?)null,
        }).Distinct().ToList();
        return found.Count switch
        {
            // No function type at all: whatever a spelling shows is thick, which nothing will ask.
            0 => SwiftFunctionRepresentation.Thick,
            1 => found[0],
            _ => null,
        };
    }

    private static SwiftFunctionRepresentation? RepresentationOf(TypeSpec spec, SwiftFunctionRepresentation? unattributed)
    {
        var found = new HashSet<SwiftFunctionRepresentation?>();
        void Walk(TypeSpec? t)
        {
            switch (t)
            {
                case null:
                    return;
                case ClosureTypeSpec closure:
                    if (!IsCFunctionPointer(closure))
                    {
                        var attribute = closure.HasAttributes
                            ? closure.Attributes.FirstOrDefault(a => a.Name == "convention" && a.Parameters.Count == 1)?.Parameters[0]
                            : null;
                        found.Add(attribute switch
                        {
                            "c" => SwiftFunctionRepresentation.C,
                            "block" => SwiftFunctionRepresentation.Block,
                            "thin" => SwiftFunctionRepresentation.Thin,
                            "swift" => SwiftFunctionRepresentation.Thick,
                            null => unattributed,
                            _ => null,
                        });
                    }
                    Walk(closure.Arguments);
                    Walk(closure.ReturnType);
                    return;
                case TupleTypeSpec tuple:
                    foreach (var element in tuple.Elements)
                        Walk(element);
                    return;
                case NamedTypeSpec named:
                    foreach (var argument in named.GenericParameters)
                        Walk(argument);
                    Walk(named.InnerType);
                    return;
                case ProtocolListTypeSpec:
                    return;
                default:
                    found.Add(null);
                    return;
            }
        }
        Walk(spec);
        return found.Count == 1 ? found.First() : null;
    }

    /// <summary>
    /// Records what each conformance requirement's target is: a Swift protocol (one witness table),
    /// a class-bound one, an <c>@objc</c> or marker protocol (none), <c>AnyObject</c> or a superclass.
    /// </summary>
    private SwiftGenericSignatureFacts ClassifyRequirements(SwiftGenericSignatureFacts generics)
    {
        if (generics.Requirements.IsDefaultOrEmpty)
            return generics;
        return generics with
        {
            Requirements = generics.Requirements
                .Select(r => r.Kind == SwiftGenericRequirementKind.Conformance ? r with { TargetKind = RequirementTargetOf(r.Target) } : r)
                .ToImmutableArray(),
        };
    }

    private static readonly HashSet<string> MarkerProtocols = new(StringComparer.Ordinal)
    {
        "Swift.Sendable", "Swift.Copyable", "Swift.Escapable", "Swift.BitwiseCopyable", "Swift._BitwiseCopyable", "Swift.SendableMetatype",
    };

    private SwiftRequirementTargetKind RequirementTargetOf(string target)
    {
        if (target is "AnyObject" or "Swift.AnyObject")
            return SwiftRequirementTargetKind.AnyObject;
        if (MarkerProtocols.Contains(target))
            return SwiftRequirementTargetKind.MarkerProtocol;
        if (_typesByName.TryGetValue(target, out var entry))
        {
            return entry.Decl switch
            {
                ProtocolDecl { IsMarker: true } => SwiftRequirementTargetKind.MarkerProtocol,
                ProtocolDecl { IsObjC: true } => SwiftRequirementTargetKind.ObjCProtocol,
                ProtocolDecl p when IsClassBound(p) => SwiftRequirementTargetKind.ClassBoundProtocol,
                ProtocolDecl => SwiftRequirementTargetKind.Protocol,
                ClassDecl => SwiftRequirementTargetKind.Superclass,
                _ => SwiftRequirementTargetKind.Unknown,
            };
        }
        if (TryPeekRecord(target, out var record))
        {
            return record.Kind switch
            {
                // The type database does not record whether a protocol is @objc, and an @objc
                // protocol takes no witness table.
                TypeRecordKind.Protocol when (record.Flags & TypeRecordFlags.ObjCBridged) != 0 => SwiftRequirementTargetKind.Unknown,
                TypeRecordKind.Protocol when (record.Flags & TypeRecordFlags.ClassBound) != 0 => SwiftRequirementTargetKind.Unknown,
                TypeRecordKind.Protocol when target.StartsWith("Swift.", StringComparison.Ordinal) => SwiftRequirementTargetKind.Protocol,
                TypeRecordKind.Class => SwiftRequirementTargetKind.Superclass,
                _ => SwiftRequirementTargetKind.Unknown,
            };
        }
        return SwiftRequirementTargetKind.Unknown;
    }

    private SwiftTypeFacts Resolve(TypeSpec spec, IReadOnlySet<string> genericNames, IReadOnlyList<string>? writtenIn)
    {
        var spelling = spec.ToString(true);
        switch (spec)
        {
            case TupleTypeSpec tuple:
                if (tuple.IsEmptyTuple)
                    return SwiftTypeFacts.Void;
                if (tuple.Elements.Count == 1)
                    return Resolve(tuple.Elements[0], genericNames, writtenIn);
                return new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Tuple };
            case ClosureTypeSpec closure when IsCFunctionPointer(closure):
                return new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.CFunctionPointer };
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
                return ResolveNamed(named, spelling, genericNames, writtenIn);
            default:
                return new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Unresolved };
        }
    }

    /// <summary>
    /// A <c>@convention(c)</c> function type: a bare C function pointer, one word with no context.
    /// The demangler marks it on the spec; a written type carries it as its attribute.
    /// </summary>
    private static bool IsCFunctionPointer(ClosureTypeSpec closure) =>
        closure.IsConventionC
        || (closure.HasAttributes && closure.Attributes.Exists(a =>
            a.Name == "convention" && a.Parameters.Count == 1 && a.Parameters[0] == "c"));

    private SwiftTypeFacts ResolveNamed(NamedTypeSpec named, string spelling, IReadOnlySet<string> genericNames,
        IReadOnlyList<string>? writtenIn)
    {
        // A nested type below a generic parent keeps its inner names in the InnerType chain
        // (Module.Outer<T>.Inner), so the full name is rebuilt from it.
        var name = FullNameOf(named);

        if (name is "Swift.Void" or "Void" or "()")
            return SwiftTypeFacts.Void;

        // A metatype first: T.Type is a metatype even when T is a generic parameter. A type nested
        // in T and named Type parses the same way, and is refused; a module's own top-level type
        // named Type is no metatype, since a module is not a type.
        if (IsMetatypeSpelling(name) && !IsDeclaredTypeName(name))
            return new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Metatype };
        if (IsAmbiguousMetatypeSpelling(name))
            return new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Unresolved };

        // A generic parameter, or a dependent member of one (τ_0_0.Element, Self.Item).
        var root = name.Split('.')[0];
        if (genericNames.Contains(name) || genericNames.Contains(root) || name.StartsWith("τ_", StringComparison.Ordinal))
            return new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.GenericParameter };

        if (name is "Any" or "Swift.Any")
            return new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Existential };
        if (name is "AnyObject" or "Swift.AnyObject")
            return new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Existential, IsClassBound = true };

        // Every Swift pointer shape is a frozen one-word struct: the projection the type
        // database's pointer strategy makes on purpose, not a guess. It is checked before the
        // database's own records, which list the typed pointers as resilient.
        if (TypeDatabaseExtensions.IsPointerType(named))
            return FromRecord(TypeDatabaseExtensions.IntPtrType, spelling);

        if (_typesByName.TryGetValue(name, out var entry))
            return FromDecl(entry.Decl, spelling);

        if (TryPeekRecord(name, out var record))
            return FromRecord(record, spelling);

        // Source the generator wrote may name a type the way the module it imports sees it.
        // The function's own module is the one the name means; another module is taken only
        // when exactly one of them declares the name.
        if (writtenIn is { Count: > 0 } && !name.Contains('.'))
        {
            if (TryResolveIn(writtenIn[0], name, spelling) is { } own)
                return own;
            var others = writtenIn.Skip(1).Distinct(StringComparer.Ordinal)
                .Select(module => TryResolveIn(module, name, spelling))
                .OfType<SwiftTypeFacts>()
                .ToList();
            if (others.Count == 1)
                return others[0];
        }

        return new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Unresolved };
    }

    /// <summary>
    /// True when <paramref name="name"/>, spelled like a metatype (<c>M.T.Type</c>), is also the name
    /// of a declared type (a type <c>Type</c> nested in <c>M.T</c>). The ABI descriptor writes the
    /// nested type with back ticks, but the type-spec parser drops them, so the parsed name no longer
    /// tells the two apart.
    /// </summary>
    private bool IsAmbiguousMetatypeSpelling(string name) =>
        IsMetatypeSpelling(name) && IsDeclaredTypeName(name) && IsDeclaredTypeName(name[..name.LastIndexOf('.')]);

    private static bool IsMetatypeSpelling(string name) =>
        name.EndsWith(".Type", StringComparison.Ordinal) || name.EndsWith(".Protocol", StringComparison.Ordinal);

    private bool IsDeclaredTypeName(string name) => _typesByName.ContainsKey(name) || TryPeekRecord(name, out _);

    private SwiftTypeFacts? TryResolveIn(string module, string name, string spelling)
    {
        var qualified = module + "." + name;
        if (_typesByName.TryGetValue(qualified, out var entry))
            return FromDecl(entry.Decl, spelling);
        return TryPeekRecord(qualified, out var record) ? FromRecord(record, spelling) : null;
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
            && _typeDatabase is not null
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

    /// <summary>Whether a protocol is class-bound, directly or through a protocol it refines.</summary>
    private bool IsClassBound(ProtocolDecl protocol) => SwiftProtocolBounds.IsClassBound(
        protocol,
        name => _typesByName.TryGetValue(name, out var entry) ? entry.Decl as ProtocolDecl : null,
        name => TryPeekRecord(name, out var record) && record.Kind == TypeRecordKind.Protocol
            ? (record.Flags & TypeRecordFlags.ClassBound) != 0
            : null);

    private SwiftTypeFacts FromDecl(TypeDecl decl, string spelling) => decl switch
    {
        ClassDecl => new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Class, IsClassBound = true },
        StructDecl s => new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Struct, IsFrozen = s.IsFrozen },
        EnumDecl e => new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Enum, IsFrozen = e.IsFrozen },
        ProtocolDecl p => new SwiftTypeFacts { Spelling = spelling, Category = SwiftTypeCategory.Existential, IsClassBound = IsClassBound(p) },
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

    /// <summary>
    /// The facts of a non-generic type's metadata accessor known only by its symbol: one the
    /// generator names from a manifest rather than from a parsed declaration.
    /// </summary>
    internal static SwiftCallFacts ForMetadataAccessorSymbol(
        string symbol, string module, string type, ImmutableArray<SwiftAvailabilityFloor> availability) =>
        MetadataAccessorFacts(symbol, module, type, SwiftGenericSignatureFacts.Empty, availability);

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
