// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Which interface declaration each member the ABI lists on a receiver stands for, decided once per
/// receiver over every declaration the interface makes there: the extension members the reader took
/// (<see cref="SwiftInterfaceFacts.ResolveForeignExtensions"/>), the declarations it saw and did not
/// take (<see cref="DroppedExtensionCandidate"/>), and a protocol's extension methods it extracted
/// (<see cref="SwiftInterfaceFacts.ProtocolExtensionMethods"/>). The listed members are the ones
/// another path binds or reports: a carried receiver's ABI members, a reported protocol's
/// <see cref="ProtocolDecl.ExtensionMembers"/>, or the public members the parser parked with a
/// dropped receiver.
///
/// <para>Each pass that writes rows for these declarations reads its verdict from here
/// (<see cref="CarriedReceiverRows.Settle"/>, the foreign-extension pre-pass, the dropped-declaration
/// report and <see cref="ProtocolExtensionEmitter.ReportUnextractedMembers"/>), so an overload whose
/// declarations the reader split between two of them is still paired with one listed member, and
/// the declaration left over keeps its row. A declaration whose signature reads the same as a listed
/// member always takes it before one that only may be it (<see cref="ForeignOverloadPairing.Pair"/>).
/// Whether the pre-pass bound a member is a separate question (<see cref="CarriedReceiverRows.PrePassBound"/>).</para>
/// </summary>
internal sealed class ForeignReceiverPairing
{
    /// <summary>Where the interface reader put a declaration; one declaration may be in two places.</summary>
    [Flags]
    internal enum Origins
    {
        None = 0,

        /// <summary>Taken as an extension member of another module's type.</summary>
        Taken = 1,

        /// <summary>Extracted as a protocol-extension method, which injection binds or reports.</summary>
        Extracted = 2,

        /// <summary>Seen and not taken.</summary>
        Dropped = 4,
    }

    /// <summary>One declaration the interface makes on a receiver, and the listed member it stands for.</summary>
    internal sealed class Declaration
    {
        public required BindingItemKind Kind { get; init; }
        public required string Name { get; init; }
        public required string PrintedName { get; init; }
        public required bool IsStatic { get; init; }

        /// <summary>Whether the declaration is async; null when that does not tell overloads apart.</summary>
        public bool? IsAsync { get; init; }

        /// <summary>Whether the method has generic parameters of its own; null for other kinds.</summary>
        public bool? IsGeneric { get; init; }

        public OverloadSignature? Signature { get; init; }
        public Origins Origin { get; set; }

        /// <summary>The listed member this declaration stands for, or null.</summary>
        public object? Listed { get; set; }
    }

    private readonly Dictionary<object, Declaration> _bySource = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, Declaration> _byListed = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, List<Declaration>> _byReceiver = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="source"/> (a taken or extracted <see cref="ProtocolExtensionMethodDecl"/>,
    /// or a <see cref="DroppedExtensionCandidate"/>) stands for a listed member. A declaration that took
    /// no part in the pairing (one that supplies a requirement's default) stands for none.
    /// </summary>
    public bool IsPaired(object source) => _bySource.TryGetValue(source, out var declaration) && declaration.Listed is not null;

    /// <summary>The declaration a listed member (an ABI <see cref="BaseDecl"/> or a parked member) stands for, or null.</summary>
    public Declaration? PairedWith(object listed) => _byListed.GetValueOrDefault(listed);

    /// <summary>Every declaration on <paramref name="receiver"/>, in the order the pairing saw them.</summary>
    public IReadOnlyList<Declaration> On(string receiver)
        => _byReceiver.TryGetValue(receiver, out var declarations) ? declarations : Array.Empty<Declaration>();

    /// <summary>
    /// Pairs every declaration on each receiver with the members listed there. Pass the same
    /// instances the row-writing passes later read; the verdicts are kept by reference.
    /// </summary>
    public static ForeignReceiverPairing Compute(
        ModuleDecl moduleDecl,
        IReadOnlyDictionary<string, List<ProtocolExtensionMethodDecl>> taken,
        IEnumerable<DroppedExtensionCandidate> dropped,
        IReadOnlyDictionary<string, List<ProtocolExtensionMethodDecl>> extracted,
        ITypeDatabase typeDatabase)
    {
        ArgumentNullException.ThrowIfNull(moduleDecl);
        var pairing = new ForeignReceiverPairing();
        var byText = new Dictionary<string, Dictionary<string, Declaration>>(StringComparer.Ordinal);
        var requirements = new Dictionary<string, ForeignProtocolRequirements.Requirements?>(StringComparer.Ordinal);

        ForeignProtocolRequirements.Requirements? RequirementsOf(string receiver)
        {
            if (!requirements.TryGetValue(receiver, out var known))
                requirements[receiver] = known = ForeignTypeExtensionEmitter.RequirementsOfReceiver(moduleDecl, receiver);
            return known;
        }

        void Add(string receiver, object source, string text, Origins origin, Func<Declaration> create)
        {
            if (!byText.TryGetValue(receiver, out var declarations))
            {
                byText[receiver] = declarations = new Dictionary<string, Declaration>(StringComparer.Ordinal);
                pairing._byReceiver[receiver] = new List<Declaration>();
            }
            // The same declaration read twice (taken and extracted, or listed twice) is one member.
            if (!declarations.TryGetValue(text, out var declaration))
            {
                declarations[text] = declaration = create();
                pairing._byReceiver[receiver].Add(declaration);
            }
            declaration.Origin |= origin;
            pairing._bySource[source] = declaration;
        }

        foreach (var (receiver, members) in taken)
        {
            // An Objective-C class the cross-module path does not carry is bound by the pre-pass, which
            // reports what it declines itself.
            var carried = ForeignTypeExtensionEmitter.IsCarriedCrossModuleReceiver(moduleDecl.Types, receiver);
            if (!carried && ForeignTypeExtensionEmitter.IsForeignObjCClassType(receiver, moduleDecl.ForeignTypeDeclKinds))
                continue;
            foreach (var member in members)
            {
                // A default of the receiver protocol's own requirement is reached through the requirement.
                if (!carried && RequirementsOf(receiver)?.Declares(ForeignProtocolRequirements.KeyOf(member)) == true)
                    continue;
                Add(receiver, member, TextOf(member), Origins.Taken, () => FromMember(member));
            }
        }

        foreach (var (rawReceiver, members) in extracted)
        {
            var receiver = ProtocolExtensionEmitter.InModule(moduleDecl, rawReceiver);
            foreach (var member in members)
                Add(receiver, member, TextOf(member), Origins.Extracted, () => FromMember(member));
        }

        foreach (var candidate in dropped)
        {
            if (ForeignTypeExtensionEmitter.KindOf(candidate) is not { } kind)
                continue;
            var receiver = candidate.ExtendedTypeName;
            if (RequirementsOf(receiver) is { } known && ForeignTypeExtensionEmitter.SuppliesRequirement(known, candidate, kind))
                continue;
            Add(receiver, candidate,
                $"{kind}|{candidate.IsStatic}|{candidate.RawSignature}|{candidate.WhereClause}",
                Origins.Dropped,
                () => new Declaration
                {
                    Kind = kind,
                    Name = candidate.Name,
                    PrintedName = candidate.PrintedName,
                    IsStatic = candidate.IsStatic,
                    IsAsync = kind == BindingItemKind.Method ? ProtocolExtensionEmitter.IsAsyncSignature(candidate.RawSignature) : null,
                    IsGeneric = candidate.Kind == "method"
                        ? CarriedReceiverRows.DeclaresOwnGenerics(candidate.RawSignature, candidate.Name)
                        : null,
                    Signature = ForeignTypeExtensionEmitter.SignatureOf(candidate, kind),
                });
        }

        foreach (var (receiver, declarations) in pairing._byReceiver)
        {
            var listed = ListedOn(moduleDecl, receiver);
            if (listed.Count == 0)
                continue;
            var pairs = ForeignOverloadPairing.Pair(
                declarations,
                listed,
                (declaration, member) => member.HasShapeOf(declaration),
                declaration => declaration.Signature,
                member => member.Signature,
                typeDatabase);
            foreach (var (index, member) in pairs)
            {
                declarations[index].Listed = member.Source;
                pairing._byListed[member.Source] = declarations[index];
            }
        }
        return pairing;
    }

    /// <summary>
    /// The members another path binds or reports on <paramref name="receiver"/>: what
    /// <see cref="CarriedReceiverRows.ListedForDeclarations"/> names for a carried receiver or a
    /// reported protocol, and otherwise the public members the parser parked with the dropped
    /// receiver. An internal member the ABI lists (<c>@usableFromInline</c>) is never a public one
    /// the interface declares.
    /// </summary>
    private static List<CarriedReceiverRows.ListedMember> ListedOn(ModuleDecl moduleDecl, string receiver)
        => CarriedReceiverRows.ListedForDeclarations(moduleDecl, receiver)
            ?? moduleDecl.UnboundForeignReceiverMembers
                .Where(parked => parked.IsPublic
                    && string.Equals(parked.ForeignTypeQualifiedName, receiver, StringComparison.Ordinal))
                .Select(CarriedReceiverRows.ListedMember.Parked)
                .ToList();

    private static string TextOf(ProtocolExtensionMethodDecl member)
        => $"{(member.IsProperty ? BindingItemKind.Property : BindingItemKind.Method)}|{member.IsStatic}|{member.RawSignature}|{string.Join(", ", member.WhereConstraints)}";

    private static Declaration FromMember(ProtocolExtensionMethodDecl member) => new()
    {
        Kind = member.IsProperty ? BindingItemKind.Property : BindingItemKind.Method,
        Name = member.MethodName,
        PrintedName = member.PrintedName,
        IsStatic = member.IsStatic,
        IsAsync = member.IsProperty ? null : ProtocolExtensionEmitter.IsAsyncSignature(member.RawSignature),
        IsGeneric = member.IsProperty ? null : CarriedReceiverRows.DeclaresOwnGenerics(member.RawSignature, member.MethodName),
        Signature = member.IsProperty ? null : ForeignOverloadPairing.SignatureOf(member.RawSignature, "func", member.MethodName),
    };
}
