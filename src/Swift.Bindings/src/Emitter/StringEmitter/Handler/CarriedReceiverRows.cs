// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// The one owner of the skip rows for members a module adds to a carried receiver: a class or struct
/// of another module that the parser kept, so that two paths see its members. The interface pre-pass
/// (<see cref="ForeignTypeExtensionEmitter"/>) reads them from the interface text and binds the ones
/// it can marshal. The cross-module path (<see cref="CrossModuleExtensionEmitter"/>) reads them from
/// the ABI and binds others. The pre-pass runs first and records here what it bound and declined.
///
/// <para>Each row is decided by full member identity (<see cref="CarriedMemberIdentity"/>), so an
/// overload is never taken for another one. An ABI decline is written in place unless the pre-pass
/// bound the same member (<see cref="PrePassBound"/>). A pre-pass decline is written by
/// <see cref="Settle"/> only when the ABI lists no such member, since the cross-module path rows or
/// binds every member the ABI lists.</para>
/// </summary>
internal static class CarriedReceiverRows
{
    /// <summary>The identity of a member the interface pre-pass handled.</summary>
    internal static CarriedMemberIdentity IdentityOf(
        ProtocolExtensionMethodDecl member, ITypeDatabase typeDatabase, ILogger logger)
    {
        if (member.IsProperty)
            return new CarriedMemberIdentity(true, member.IsStatic, false, false, member.MethodName, member.MethodName, null);
        // A generic parameter is spelled by its source name in the interface and by its depth and
        // index in the ABI, so a generic member's types do not compare; its labels and flags do. A
        // parameter of type `some P` is a generic parameter of the method in the ABI.
        var isGeneric = member.RawSignature.Contains($"func {member.MethodName}<", StringComparison.Ordinal)
            || OpaqueParameter.IsMatch(ParameterClause(member.RawSignature, member.MethodName));
        var signature = isGeneric ? null : ProtocolExtensionEmitter.ExtensionOverloadKey(member, typeDatabase, logger);
        var types = signature is null ? null : ProtocolExtensionEmitter.ExtensionTypeSlots(member, typeDatabase, logger);
        return new CarriedMemberIdentity(
            IsProperty: false,
            member.IsStatic,
            ProtocolExtensionEmitter.IsAsyncSignature(member.RawSignature),
            isGeneric,
            member.MethodName,
            member.PrintedName,
            signature is null ? null : ProtocolExtensionEmitter.WithoutModuleQualifiers(signature))
        {
            Types = types,
            AliasCandidates = types is null ? null : AliasCandidatesIn(types, typeDatabase),
        };
    }

    /// <summary>
    /// The named types in <paramref name="types"/> that may be typealiases: each one the type database
    /// does not know as a nominal type of that name. The ABI spells a typealias by what it resolves to,
    /// so only these may read differently on the two sides of one member.
    /// </summary>
    private static HashSet<string> AliasCandidatesIn(IEnumerable<TypeSpec> types, ITypeDatabase typeDatabase)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        void Visit(TypeSpec type)
        {
            switch (type)
            {
                case NamedTypeSpec named:
                    if (named.GenericParameters.Count == 0 && !IsNominal(named, typeDatabase))
                        candidates.Add(named.Name);
                    foreach (var argument in named.GenericParameters)
                        Visit(argument);
                    break;
                case TupleTypeSpec tuple:
                    foreach (var element in tuple.Elements)
                        Visit(element);
                    break;
                case ClosureTypeSpec closure:
                    Visit(closure.Arguments);
                    Visit(closure.ReturnType);
                    break;
            }
        }
        foreach (var type in types)
            Visit(type);
        return candidates;
    }

    /// <summary>Whether the type database knows <paramref name="named"/> as a nominal type of that name.</summary>
    private static bool IsNominal(NamedTypeSpec named, ITypeDatabase typeDatabase)
    {
        if (named.Name == "Self")
            return false;
        var spelled = ProtocolExtensionEmitter.WithoutModuleQualifiers(named.Name);
        foreach (var name in named.Name.Contains('.') ? [named.Name] : new[] { named.Name, $"Swift.{named.Name}" })
        {
            try
            {
                if (typeDatabase.TryGetTypeRecord(new NamedTypeSpec(name), out var record))
                    return string.Equals(
                        ProtocolExtensionEmitter.WithoutModuleQualifiers(record.SwiftTypeName.ModuleQualifiedName),
                        spelled, StringComparison.Ordinal);
            }
            catch (Exception)
            {
                // A name the resolver cannot read is not known to be nominal.
            }
        }
        return false;
    }

    /// <summary>
    /// The identity of a member the ABI lists, or null for a shape the pre-pass never reads
    /// (an initializer, a subscript, an operator).
    /// </summary>
    internal static CarriedMemberIdentity? IdentityOf(BaseDecl member)
    {
        switch (member)
        {
            case PropertyDecl property:
                return new CarriedMemberIdentity(true, property.IsStatic, false, false,
                    property.GetSwiftName(), property.GetSwiftName(), null);
            case MethodDecl { IsConstructor: false, IsAccessor: false } method:
                // An instance method of an isolated type is called async across the isolation
                // boundary, but its declaration, which the interface prints, is synchronous.
                var isAsync = method.IsAsync && !method.IsAsyncByIsolation;
                // The ABI folds a generic receiver's parameters into each method's generic signature;
                // only the method's own make it generic, as the interface declares it.
                var isGeneric = WrapperValidation.HasMethodOwnGenericParameters(method);
                var signature = ProtocolExtensionEmitter.AbiOverloadKey(method);
                if (method.IsAsync && !isAsync)
                    signature = signature.Replace(" async -> ", " -> ", StringComparison.Ordinal);
                // A receiver's generic parameter is spelled by depth and index here and by its source
                // name in the interface; types compare once it is spelled by name.
                var comparable = isGeneric ? null : WithSourceGenericNames(signature, method);
                return new CarriedMemberIdentity(
                    IsProperty: false,
                    method.MethodType == MethodType.Static,
                    isAsync,
                    isGeneric,
                    method.GetSwiftName(),
                    ProtocolExtensionEmitter.AbiLabelKey(method),
                    comparable is null ? null : ProtocolExtensionEmitter.WithoutModuleQualifiers(comparable))
                {
                    Types = comparable is null ? null : AbiTypeSlots(method),
                };
            default:
                return null;
        }
    }

    /// <summary>The parameter types of an ABI method, then its return type.</summary>
    private static List<TypeSpec> AbiTypeSlots(MethodDecl method)
    {
        var slots = method.CSSignature.Skip(1).Select(a => a.SwiftTypeSpec ?? TupleTypeSpec.Empty).ToList();
        slots.Add(method.CSSignature.Count > 0 ? method.CSSignature[0].SwiftTypeSpec ?? TupleTypeSpec.Empty : TupleTypeSpec.Empty);
        return slots;
    }

    private static readonly System.Text.RegularExpressions.Regex OpaqueParameter =
        new(@"(?<![\w.])some\s+(?![A-Za-z_]\w*\s*:)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex AbiGenericParameter =
        new(@"τ_\d+_\d+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// <paramref name="signature"/> with each generic parameter spelled by the name its declaration
    /// gives it, or null when one has no such name.
    /// </summary>
    private static string? WithSourceGenericNames(string signature, MethodDecl method)
    {
        var unnamed = false;
        var named = AbiGenericParameter.Replace(signature, m =>
        {
            var name = method.GenericParameters.FirstOrDefault(p => p.TypeName == m.Value)?.SugaredTypeName;
            if (string.IsNullOrEmpty(name) || name.StartsWith("τ_", StringComparison.Ordinal))
                unnamed = true;
            return name ?? m.Value;
        });
        return unnamed ? null : named;
    }

    /// <summary>
    /// The parameter clause of <paramref name="name"/>'s declaration in <paramref name="signature"/>
    /// without its default values, or "" when it cannot be found. A string literal never opens or
    /// closes a clause.
    /// </summary>
    private static string ParameterClause(string signature, string name)
    {
        var at = signature.IndexOf($"func {name}", StringComparison.Ordinal);
        if (at < 0)
            return "";
        var angle = 0;
        for (var i = at + 5 + name.Length; i < signature.Length; i++)
        {
            var c = signature[i];
            if (c == '<') angle++;
            else if (c == '>' && angle > 0) angle--;
            else if (c == '(' && angle == 0)
                return TypesOfClause(signature, i);
        }
        return "";
    }

    private static string TypesOfClause(string signature, int open)
    {
        var types = new System.Text.StringBuilder();
        var depth = 0;
        var inDefault = false;
        for (var j = open; j < signature.Length; j++)
        {
            var c = signature[j];
            if (c == '"')
            {
                // Skip the literal, escapes included.
                for (j++; j < signature.Length && signature[j] != '"'; j++)
                    if (signature[j] == '\\') j++;
                continue;
            }
            if (c is '(' or '[' or '<') depth++;
            else if (c is ')' or ']' || (c == '>' && signature[j - 1] != '-'))
            {
                if (--depth == 0)
                    return types.Append(')').ToString();
            }
            if (depth == 1 && c == '=')
                inDefault = true;
            else if (depth == 1 && c == ',')
                inDefault = false;
            if (!inDefault)
                types.Append(c);
        }
        return "";
    }

    /// <summary>
    /// Whether the interface pre-pass bound <paramref name="member"/> of <paramref name="receiver"/>,
    /// which the ABI path declined. A shape the pre-pass never reads is never bound by it.
    /// </summary>
    internal static bool PrePassBound(ModuleEmissionContext ctx, string receiver, BaseDecl member)
    {
        if (IdentityOf(member) is not { } identity)
            return false;
        var onReceiver = OutcomesOn(ctx.CarriedPrePassOutcomes, receiver);
        var listed = member.ParentDecl is TypeDecl type ? ListedOn(type, member.ModuleDecl?.Name).ToList() : [identity];
        return onReceiver.Any(o => o.Bound && Corresponds(o.Identity, onReceiver, identity, listed));
    }

    /// <summary>
    /// Writes the pre-pass declines of every carried receiver that the ABI does not list into the
    /// live report session. Call once, after emission has settled and before the report is completed.
    /// </summary>
    public static void Settle(ModuleDecl moduleDecl, ModuleEmissionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(moduleDecl);
        ArgumentNullException.ThrowIfNull(ctx);

        var outcomes = ctx.CarriedPrePassOutcomes;
        var abiMembers = new Dictionary<string, List<CarriedMemberIdentity>>(StringComparer.Ordinal);
        foreach (var outcome in outcomes)
        {
            var onReceiver = OutcomesOn(outcomes, outcome.Receiver);
            if (outcome.Bound || onReceiver.Any(o => o.Bound && o.Identity.SameMember(outcome.Identity)))
                continue;
            if (!abiMembers.TryGetValue(outcome.Receiver, out var listed))
                abiMembers[outcome.Receiver] = listed = AbiMembersOf(moduleDecl, outcome.Receiver);
            if (listed.Any(a => Corresponds(outcome.Identity, onReceiver, a, listed)))
                continue;
            ReportCollector.RecordMemberSkipped(outcome.MemberId, moduleDecl, outcome.Reason, outcome.Details);
        }
    }

    /// <summary>
    /// Whether an interface member and an ABI member are one declaration. Types decide between
    /// overloads of one name, labels and flags. When each side declares that shape once, the two are
    /// the same member if every type that reads differently is one the interface may spell through a
    /// typealias the ABI resolves (<see cref="CarriedMemberIdentity.TypesMayBeSpellingsOf"/>); a known
    /// nominal type against another type is two members, each with its own row.
    /// </summary>
    private static bool Corresponds(
        CarriedMemberIdentity declared, IReadOnlyList<CarriedPrePassOutcome> declaredOnReceiver,
        CarriedMemberIdentity abi, IReadOnlyList<CarriedMemberIdentity> abiOnReceiver)
        => declared.SameMember(abi)
            || (declared.SameShape(abi)
                && declared.TypesMayBeSpellingsOf(abi)
                && declaredOnReceiver.Count(o => o.Identity.SameShape(declared)) == 1
                && abiOnReceiver.Count(a => a.SameShape(abi)) == 1);

    private static List<CarriedPrePassOutcome> OutcomesOn(IReadOnlyList<CarriedPrePassOutcome> outcomes, string receiver)
        => outcomes.Where(o => string.Equals(o.Receiver, receiver, StringComparison.Ordinal)).ToList();

    /// <summary>
    /// The public members the current module adds to <paramref name="receiver"/> that the ABI lists,
    /// in the set the cross-module path walks. An internal or SPI member the ABI lists
    /// (<c>@usableFromInline internal</c>) is never bound or reported by that path, so it never
    /// stands for a member the interface declares.
    /// </summary>
    private static List<CarriedMemberIdentity> AbiMembersOf(ModuleDecl moduleDecl, string receiver)
        => ReceiversNamed(moduleDecl.Types, receiver).SelectMany(t => ListedOn(t, moduleDecl.Name)).ToList();

    private static IEnumerable<CarriedMemberIdentity> ListedOn(TypeDecl type, string? moduleName)
    {
        foreach (var method in type.Methods)
        {
            if (method.ModuleDecl?.Name == moduleName && !method.IsModuleInternal && !method.IsSpiProtected
                && IdentityOf(method) is { } identity)
                yield return identity;
        }
        foreach (var property in type.Properties)
        {
            if (property.ModuleDecl?.Name == moduleName && !property.IsModuleInternal && !property.IsSpiProtected
                && IdentityOf(property) is { } identity)
                yield return identity;
        }
    }

    /// <summary>
    /// Whether the ABI lists, on the carried receiver <paramref name="receiver"/>, a public member this
    /// module adds that the cross-module path then binds or reports: a method or property of the same
    /// name, labels and staticness, or an initializer, subscript, operator or nested type of the same
    /// name.
    /// </summary>
    internal static bool AbiLists(
        ModuleDecl moduleDecl, string receiver, BindingItemKind kind, string name, string printedName, bool isStatic)
    {
        foreach (var type in ReceiversNamed(moduleDecl.Types, receiver))
        {
            bool Own(BaseDecl d) => d.ModuleDecl?.Name == moduleDecl.Name;
            var listed = kind switch
            {
                BindingItemKind.Method when name == "init" => type.Methods.Any(m =>
                    m.IsConstructor && Own(m) && !m.IsModuleInternal && !m.IsSpiProtected),
                BindingItemKind.Method => type.Methods.Any(m =>
                    !m.IsConstructor && !m.IsAccessor && Own(m) && !m.IsModuleInternal && !m.IsSpiProtected
                    && (m.MethodType == MethodType.Static) == isStatic
                    && string.Equals(ProtocolExtensionEmitter.AbiLabelKey(m), printedName, StringComparison.Ordinal)),
                BindingItemKind.Property => type.Properties.Any(p =>
                    Own(p) && !p.IsModuleInternal && !p.IsSpiProtected && p.IsStatic == isStatic
                    && string.Equals(p.GetSwiftName(), name, StringComparison.Ordinal)),
                BindingItemKind.Subscript => type.Subscripts.Any(s => Own(s) && !s.IsModuleInternal),
                BindingItemKind.Operator => type.Operators.Any(o =>
                    Own(o) && string.Equals(o.Name, name, StringComparison.Ordinal)),
                BindingItemKind.Type => type.Types.Any(t =>
                    Own(t) && string.Equals(t.Name, name, StringComparison.Ordinal)),
                _ => false,
            };
            if (listed)
                return true;
        }
        return false;
    }

    private static IEnumerable<TypeDecl> ReceiversNamed(IEnumerable<TypeDecl> types, string receiver)
    {
        foreach (var type in types)
        {
            if (type is ClassDecl or StructDecl
                && string.Equals(type.SwiftTypeName.ModuleQualifiedName, receiver, StringComparison.Ordinal))
                yield return type;
            foreach (var nested in ReceiversNamed(type.Types, receiver))
                yield return nested;
        }
    }
}

/// <summary>
/// A member's identity across the interface text and the ABI: kind, staticness, name, labels,
/// async-ness, genericity and, where both sides parse it, parameter and return types with module
/// qualifiers dropped (the ABI names a type moved by <c>@_originallyDefinedIn</c> by its first
/// module). Swift overloads on every one of these.
/// </summary>
internal sealed record CarriedMemberIdentity(
    bool IsProperty,
    bool IsStatic,
    bool IsAsync,
    bool IsGeneric,
    string Name,
    string LabelKey,
    string? SignatureKey)
{
    /// <summary>
    /// Whether both identities name the same declaration. Types decide only when both sides
    /// parsed them; otherwise name, labels and the flags do.
    /// </summary>
    public bool SameMember(CarriedMemberIdentity other)
        => SameShape(other)
            && (IsProperty
                || SignatureKey is null
                || other.SignatureKey is null
                || string.Equals(SignatureKey, other.SignatureKey, StringComparison.Ordinal));

    /// <summary>
    /// The parameter types, then the return type, as this side spells them; null when they were not
    /// read (a property, a generic member, a signature that does not parse).
    /// </summary>
    public IReadOnlyList<TypeSpec>? Types { get; init; }

    /// <summary>
    /// The named types of an interface member that may be typealiases (not known as nominal types of
    /// that name); null on the ABI side, which resolves every typealias.
    /// </summary>
    public IReadOnlySet<string>? AliasCandidates { get; init; }

    /// <summary>
    /// Whether this interface member's types may be the ABI member's types spelled another way: they
    /// read the same with module qualifiers dropped, or every place they differ is a named type of
    /// this side that may be a typealias. A known nominal type that reads differently from the ABI's
    /// type, or a structure (tuple, closure, generic arguments) that differs, is another member.
    /// </summary>
    public bool TypesMayBeSpellingsOf(CarriedMemberIdentity abi)
    {
        if (IsProperty)
            return true;
        if (Types is not { } mine || abi.Types is not { } theirs || mine.Count != theirs.Count)
            return false;
        var aliases = AliasCandidates ?? new HashSet<string>();
        for (var i = 0; i < mine.Count; i++)
        {
            if (!MayBeSpellingOf(mine[i], theirs[i], aliases))
                return false;
        }
        return true;
    }

    private static bool MayBeSpellingOf(TypeSpec declared, TypeSpec abi, IReadOnlySet<string> aliases)
    {
        if (string.Equals(Unqualified(declared), Unqualified(abi), StringComparison.Ordinal))
            return true;
        switch (declared)
        {
            case NamedTypeSpec { GenericParameters.Count: 0 } named when aliases.Contains(named.Name):
                return true;
            case NamedTypeSpec named when abi is NamedTypeSpec other
                && string.Equals(ProtocolExtensionEmitter.WithoutModuleQualifiers(named.Name),
                    ProtocolExtensionEmitter.WithoutModuleQualifiers(other.Name), StringComparison.Ordinal)
                && named.GenericParameters.Count == other.GenericParameters.Count:
                return named.GenericParameters.Zip(other.GenericParameters).All(p => MayBeSpellingOf(p.First, p.Second, aliases));
            case TupleTypeSpec tuple when abi is TupleTypeSpec other && tuple.Elements.Count == other.Elements.Count:
                return tuple.Elements.Zip(other.Elements).All(p => MayBeSpellingOf(p.First, p.Second, aliases));
            case ClosureTypeSpec closure when abi is ClosureTypeSpec other:
                return MayBeSpellingOf(closure.Arguments, other.Arguments, aliases)
                    && MayBeSpellingOf(closure.ReturnType, other.ReturnType, aliases);
            default:
                return false;
        }
    }

    private static string Unqualified(TypeSpec type) => ProtocolExtensionEmitter.WithoutModuleQualifiers(type.ToString());

    /// <summary>Whether both identities agree on everything but their types.</summary>
    public bool SameShape(CarriedMemberIdentity other)
    {
        if (IsProperty != other.IsProperty
            || IsStatic != other.IsStatic
            || !string.Equals(Name, other.Name, StringComparison.Ordinal))
            return false;
        if (IsProperty)
            return true;
        return IsAsync == other.IsAsync
            && IsGeneric == other.IsGeneric
            && string.Equals(LabelKey, other.LabelKey, StringComparison.Ordinal);
    }
}

/// <summary>What the interface pre-pass did with one member of a carried receiver.</summary>
internal sealed record CarriedPrePassOutcome(
    string Receiver,
    CarriedMemberIdentity Identity,
    bool Bound,
    DeclId MemberId,
    SkipReason Reason = SkipReason.UnsupportedSignature,
    string Details = "");
