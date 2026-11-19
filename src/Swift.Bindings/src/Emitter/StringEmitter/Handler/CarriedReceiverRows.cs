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
        return new CarriedMemberIdentity(
            IsProperty: false,
            member.IsStatic,
            ProtocolExtensionEmitter.IsAsyncSignature(member.RawSignature),
            isGeneric,
            member.MethodName,
            member.PrintedName,
            signature is null ? null : ProtocolExtensionEmitter.WithoutModuleQualifiers(signature));
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
                // A receiver's generic parameter is spelled by depth and index here and by name there.
                var comparable = !isGeneric && !signature.Contains("τ_", StringComparison.Ordinal);
                return new CarriedMemberIdentity(
                    IsProperty: false,
                    method.MethodType == MethodType.Static,
                    isAsync,
                    isGeneric,
                    method.GetSwiftName(),
                    ProtocolExtensionEmitter.AbiLabelKey(method),
                    comparable ? ProtocolExtensionEmitter.WithoutModuleQualifiers(signature) : null);
            default:
                return null;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex OpaqueParameter =
        new(@"(?<![\w.])some\s+(?![A-Za-z_]\w*\s*:)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The parenthesized parameter clause of <paramref name="name"/>'s declaration in
    /// <paramref name="signature"/>, or "" when it cannot be found.
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
            {
                var depth = 0;
                for (var j = i; j < signature.Length; j++)
                {
                    if (signature[j] == '(') depth++;
                    else if (signature[j] == ')' && --depth == 0)
                        return signature[i..(j + 1)];
                }
                return "";
            }
        }
        return "";
    }

    /// <summary>
    /// Whether the interface pre-pass bound <paramref name="member"/> of <paramref name="receiver"/>,
    /// which the ABI path declined. A shape the pre-pass never reads is never bound by it.
    /// </summary>
    internal static bool PrePassBound(ModuleEmissionContext ctx, string receiver, BaseDecl member)
        => IdentityOf(member) is { } identity && PrePassBound(ctx.CarriedPrePassOutcomes, receiver, identity);

    private static bool PrePassBound(
        IReadOnlyList<CarriedPrePassOutcome> outcomes, string receiver, CarriedMemberIdentity identity)
        => outcomes.Any(o => o.Bound
            && string.Equals(o.Receiver, receiver, StringComparison.Ordinal)
            && o.Identity.SameMember(identity));

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
            if (outcome.Bound || PrePassBound(outcomes, outcome.Receiver, outcome.Identity))
                continue;
            if (!abiMembers.TryGetValue(outcome.Receiver, out var listed))
                abiMembers[outcome.Receiver] = listed = AbiMembersOf(moduleDecl, outcome.Receiver);
            if (listed.Any(a => a.SameMember(outcome.Identity)))
                continue;
            ReportCollector.RecordMemberSkipped(outcome.MemberId, moduleDecl, outcome.Reason, outcome.Details);
        }
    }

    /// <summary>
    /// The members the current module adds to <paramref name="receiver"/> that the ABI lists, in
    /// the set the cross-module path walks.
    /// </summary>
    private static List<CarriedMemberIdentity> AbiMembersOf(ModuleDecl moduleDecl, string receiver)
    {
        var listed = new List<CarriedMemberIdentity>();
        foreach (var type in ReceiversNamed(moduleDecl.Types, receiver))
        {
            foreach (var method in type.Methods)
            {
                if (method.ModuleDecl?.Name == moduleDecl.Name && IdentityOf(method) is { } identity)
                    listed.Add(identity);
            }
            foreach (var property in type.Properties)
            {
                if (property.ModuleDecl?.Name == moduleDecl.Name && IdentityOf(property) is { } identity)
                    listed.Add(identity);
            }
        }
        return listed;
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
    {
        if (IsProperty != other.IsProperty
            || IsStatic != other.IsStatic
            || !string.Equals(Name, other.Name, StringComparison.Ordinal))
            return false;
        if (IsProperty)
            return true;
        if (IsAsync != other.IsAsync
            || IsGeneric != other.IsGeneric
            || !string.Equals(LabelKey, other.LabelKey, StringComparison.Ordinal))
            return false;
        return SignatureKey is null
            || other.SignatureKey is null
            || string.Equals(SignatureKey, other.SignatureKey, StringComparison.Ordinal);
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
