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
        var isGeneric = DeclaresOwnGenerics(member.RawSignature, member.MethodName);
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
    private static HashSet<string> AliasCandidatesIn(IEnumerable<TypeSpec> types, ITypeDatabase? typeDatabase)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        void Visit(TypeSpec type)
        {
            switch (type)
            {
                case NamedTypeSpec named:
                    if (typeDatabase is null || !IsNominal(named, typeDatabase))
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

    /// <summary>
    /// Whether a type database holds <paramref name="named"/> as a nominal type of that name. Only the
    /// records the databases hold count: a record the resolver would synthesize for an unknown name
    /// that looks like an ObjC class says nothing about whether the name is a typealias.
    /// </summary>
    private static bool IsNominal(NamedTypeSpec named, ITypeDatabase typeDatabase)
    {
        if (named.Name == "Self")
            return false;
        var spelled = ProtocolExtensionEmitter.WithoutModuleQualifiers(named.Name);
        foreach (var name in named.Name.Contains('.') ? [named.Name] : new[] { $"Swift.{named.Name}" })
        {
            if (SwiftTypeName.TryFromModuleQualifiedName(name, out var typeName)
                && typeDatabase.TryPeekTypeRecord(typeName, out var record))
                return string.Equals(
                    ProtocolExtensionEmitter.WithoutModuleQualifiers(record.SwiftTypeName.ModuleQualifiedName),
                    spelled, StringComparison.Ordinal);
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

    /// <summary>
    /// Whether the interface declaration <paramref name="signature"/> of the method
    /// <paramref name="name"/> has generic parameters of its own: a generic parameter clause, or a
    /// parameter of type <c>some P</c>, which is a generic parameter of the method in the ABI.
    /// </summary>
    internal static bool DeclaresOwnGenerics(string signature, string name)
        => FuncNamed(signature, name) is { Success: true } at && signature[(at.Index + at.Length)..].TrimStart().StartsWith('<')
            || OpaqueParameter.IsMatch(ParameterClause(signature, name));

    // `func name` in a declaration, the name escaped in backticks or not (`func `repeat``).
    private static System.Text.RegularExpressions.Match FuncNamed(string signature, string name)
        => System.Text.RegularExpressions.Regex.Match(signature,
            $@"(?<![\w`])func\s+`?{System.Text.RegularExpressions.Regex.Escape(name)}`?(?![\w`])");

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
        var at = FuncNamed(signature, name);
        if (!at.Success)
            return "";
        var angle = 0;
        for (var i = at.Index + at.Length; i < signature.Length; i++)
        {
            var c = signature[i];
            if (c == '<') angle++;
            else if (c == '>' && angle > 0) angle--;
            else if (c == '(' && angle == 0)
                return TypesOfClause(signature, i);
        }
        return "";
    }

    internal static string TypesOfClause(string signature, int open)
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
    /// Writes the pre-pass declines of every carried receiver that no listed member stands for into
    /// the live report session: the cross-module path binds or reports each member the ABI lists, so
    /// a declined declaration the receiver's pairing (<see cref="ForeignReceiverPairing"/>) paired with
    /// one is accounted for there. Call once, after emission has settled and before the report is
    /// completed.
    /// </summary>
    public static void Settle(ModuleDecl moduleDecl, ModuleEmissionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(moduleDecl);
        ArgumentNullException.ThrowIfNull(ctx);

        var outcomes = ctx.CarriedPrePassOutcomes;
        if (outcomes.Count == 0)
            return;
        // The pre-pass that recorded the outcomes pairs its declarations when the caller has not.
        var pairing = ctx.ForeignReceiverPairing
            ?? throw new InvalidOperationException("Carried pre-pass outcomes were recorded without a receiver pairing.");
        foreach (var outcome in outcomes)
        {
            var onReceiver = OutcomesOn(outcomes, outcome.Receiver);
            if (outcome.Bound || onReceiver.Any(o => o.Bound && o.Identity.SameMember(outcome.Identity)))
                continue;
            if (pairing.IsPaired(outcome.Declaration))
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
    /// The public members this module adds to <paramref name="receiver"/> that the ABI lists and
    /// another path binds or reports, of the kinds an interface declaration the extension reader does
    /// not take can be (methods, initializers, properties, subscripts, operators, nested types), or
    /// null when no other path reports the receiver's members. A carried class or struct is bound by
    /// the cross-module path; a foreign protocol the parser kept for its extension members has the
    /// members its extensions add reported by <see cref="ProtocolExtensionEmitter.ReportUnextractedMembers"/>.
    /// </summary>
    internal static List<ListedMember>? ListedForDeclarations(ModuleDecl moduleDecl, string receiver)
    {
        var carried = ReceiversNamed(moduleDecl.Types, receiver).ToList();
        if (carried.Count > 0)
        {
            return carried.SelectMany(type => type.Methods.Cast<BaseDecl>()
                    .Concat(type.Properties).Concat(type.Subscripts).Concat(type.Operators).Concat(type.Types))
                .Select(member => Listed(moduleDecl, member))
                .OfType<ListedMember>()
                .ToList();
        }
        if (ProtocolExtensionEmitter.ReportedForeignProtocol(moduleDecl, receiver) is { } protocol)
        {
            // A protocol's extension members are the ones this module's ABI lists on it.
            return protocol.ExtensionMembers
                .Select(member => Listed(moduleDecl, member, onProtocol: true))
                .OfType<ListedMember>()
                .ToList();
        }
        return null;
    }

    private static ListedMember? Listed(ModuleDecl moduleDecl, BaseDecl member, bool onProtocol = false)
    {
        static string? Type(ArgumentDecl argument) => argument.SwiftTypeSpec?.ToString();
        if (!onProtocol && member.ModuleDecl?.Name != moduleDecl.Name)
            return null;
        switch (member)
        {
            case MethodDecl method when !method.IsAccessor && !method.IsModuleInternal && !method.IsSpiProtected:
                var labels = ProtocolExtensionEmitter.AbiLabelKey(method);
                // An instance method of an isolated type is called async across the isolation
                // boundary, but its declaration, which the interface prints, is synchronous.
                return new ListedMember(member, BindingItemKind.Method,
                    method.IsConstructor ? "init" : method.GetSwiftName(),
                    method.IsConstructor ? "init" + labels[labels.IndexOf('(')..] : labels,
                    !method.IsConstructor && method.MethodType == MethodType.Static,
                    SignatureOf(method, labels: null),
                    method.IsConstructor ? null : method.IsAsync && !method.IsAsyncByIsolation,
                    // The ABI folds a generic receiver's parameters into each method's generic
                    // signature, and a protocol's Self into each extension member's.
                    method.IsConstructor || onProtocol ? null : WrapperValidation.HasMethodOwnGenericParameters(method));
            case PropertyDecl property when !property.IsModuleInternal && !property.IsSpiProtected:
                return new ListedMember(member, BindingItemKind.Property, property.GetSwiftName(), null, property.IsStatic, null);
            case SubscriptDecl subscript when !subscript.IsModuleInternal && !subscript.IsSpiProtected:
                return new ListedMember(member, BindingItemKind.Subscript, "subscript", null, subscript.IsStatic,
                    ForeignOverloadPairing.ListedSignature(LabelsOf(subscript.PrintedName),
                        subscript.IndexParameters.Select(Type), subscript.ReturnTypeSpec?.ToString()));
            case OperatorDecl op when !op.UnderlyingMethod.IsModuleInternal && !op.UnderlyingMethod.IsSpiProtected:
                return new ListedMember(member, BindingItemKind.Operator, op.OperatorSymbol, null,
                    op.UnderlyingMethod.MethodType == MethodType.Static, SignatureOf(op.UnderlyingMethod, labels: null));
            case TypeDecl nested when !nested.IsModuleInternal:
                return new ListedMember(member, BindingItemKind.Type, nested.Name, null, false, null);
            default:
                return null;
        }
    }

    // The first signature slot is the result; an initializer's is its own type, which is not compared.
    private static OverloadSignature SignatureOf(MethodDecl method, string? labels)
        => ForeignOverloadPairing.ListedSignature(labels,
            method.CSSignature.Skip(1).Select(a => a.SwiftTypeSpec?.ToString()),
            method.IsConstructor || method.CSSignature.Count == 0 ? null : method.CSSignature[0].SwiftTypeSpec?.ToString());

    /// <summary>The argument labels of a printed name (<c>a:_:</c> of <c>subscript(a:_:)</c>), or null.</summary>
    internal static string? LabelsOf(string? printedName)
    {
        var open = printedName?.IndexOf('(') ?? -1;
        return open < 0 || !printedName!.EndsWith(')') ? null : printedName[(open + 1)..^1];
    }

    /// <summary>
    /// Whether the interface type <paramref name="declared"/> may be the ABI type
    /// <paramref name="listed"/> spelled another way: where the two differ, the interface names a type
    /// no database holds as a nominal type of that name, which may be a typealias the ABI resolves.
    /// Without a database every named type may be one; structure (tuple, closure, generic arguments
    /// of a known type) still has to agree.
    /// </summary>
    internal static bool MayBeSpelledAs(string declared, string listed, ITypeDatabase typeDatabase)
    {
        TypeSpec? Parse(string text)
        {
            try { return TypeSpecParser.Parse(text); }
            catch (Exception) { return null; }
        }
        if (Parse(declared) is not { } declaredType || Parse(listed) is not { } listedType)
            return false;
        var aliases = AliasCandidatesIn(new[] { declaredType }, typeDatabase);
        return CarriedMemberIdentity.MayBeSpellingOf(declaredType, listedType, aliases);
    }

    /// <summary>
    /// A member the ABI lists on a receiver (<paramref name="source"/>, an ABI declaration or a member the
    /// parser parked): its kind, name, labels (null when not compared), staticness, async-ness and
    /// whether it has generic parameters of its own (null when unknown), and signature (null when it
    /// has none to compare).
    /// </summary>
    internal sealed class ListedMember(
        object source, BindingItemKind kind, string name, string? printedName, bool? isStatic, OverloadSignature? signature,
        bool? isAsync = null, bool? isGeneric = null, bool parked = false)
    {
        /// <summary>The listed member itself, which a pairing verdict is kept for.</summary>
        public object Source { get; } = source;

        public OverloadSignature? Signature { get; } = signature;

        /// <summary>A member the parser parked with a dropped receiver. A subscript's labels are compared
        /// with its types, since the ABI may print them erased.</summary>
        internal static ListedMember Parked(UnboundForeignReceiverMember parked)
            => new(parked, parked.Kind, parked.Name,
                parked.Kind == BindingItemKind.Subscript || string.IsNullOrEmpty(parked.PrintedName) ? null : parked.PrintedName,
                parked.IsStatic, parked.Signature, parked.IsAsync, parked: true);

        /// <summary>Whether an interface declaration of this kind, name, labels, staticness, async-ness and
        /// genericity may be this member.</summary>
        public bool HasShapeOf(ForeignReceiverPairing.Declaration declared)
            => kind == declared.Kind
                && string.Equals(name, declared.Name, StringComparison.Ordinal)
                // A parked member may be any declaration whose labels the reader did not record.
                && (printedName is null || (parked && string.IsNullOrEmpty(declared.PrintedName))
                    || string.Equals(printedName, declared.PrintedName, StringComparison.Ordinal))
                && (kind is BindingItemKind.Type or BindingItemKind.Operator || isStatic is null || isStatic == declared.IsStatic)
                && (isAsync is null || declared.IsAsync is null || isAsync == declared.IsAsync)
                && (isGeneric is null || declared.IsGeneric is null || isGeneric == declared.IsGeneric);
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

    internal static bool MayBeSpellingOf(TypeSpec declared, TypeSpec abi, IReadOnlySet<string> aliases)
    {
        if (string.Equals(Unqualified(declared), Unqualified(abi), StringComparison.Ordinal))
            return true;
        switch (declared)
        {
            case NamedTypeSpec named when aliases.Contains(named.Name):
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
/// <param name="Declaration">The interface declaration, whose pairing verdict decides its row.</param>
internal sealed record CarriedPrePassOutcome(
    string Receiver,
    CarriedMemberIdentity Identity,
    bool Bound,
    DeclId MemberId,
    ProtocolExtensionMethodDecl Declaration,
    SkipReason Reason = SkipReason.UnsupportedSignature,
    string Details = "");
