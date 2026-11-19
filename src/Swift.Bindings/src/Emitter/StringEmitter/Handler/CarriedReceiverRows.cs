// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// The one owner of the skip rows for members a module adds to a carried receiver: a class or struct
/// of another module that the parser kept, so that two paths see its members. The interface pre-pass
/// (<see cref="ForeignTypeExtensionEmitter"/>) reads them from the interface text and binds the ones
/// it can marshal. The cross-module path (<see cref="CrossModuleExtensionEmitter"/>) reads them from
/// the ABI and binds others. Neither path can tell on its own whether the other bound a member. So
/// both record what they declined here, the pre-pass also records what it bound, and
/// <see cref="Settle"/> writes the rows once emission has settled.
///
/// <para>Each row is decided by full member identity (<see cref="CarriedMemberIdentity"/>), so an
/// overload is never taken for another one. An ABI decline is dropped when the pre-pass bound the
/// same member. A pre-pass decline is written only when the ABI lists no such member, since the
/// cross-module path rows or binds every member the ABI lists.</para>
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
        // index in the ABI, so a generic member's types do not compare; its labels and flags do.
        var isGeneric = member.RawSignature.Contains($"func {member.MethodName}<", StringComparison.Ordinal);
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
                var signature = ProtocolExtensionEmitter.AbiOverloadKey(method);
                if (method.IsAsync && !isAsync)
                    signature = signature.Replace(" async -> ", " -> ", StringComparison.Ordinal);
                return new CarriedMemberIdentity(
                    IsProperty: false,
                    method.MethodType == MethodType.Static,
                    isAsync,
                    method.IsGeneric,
                    method.GetSwiftName(),
                    ProtocolExtensionEmitter.AbiLabelKey(method),
                    method.IsGeneric ? null : ProtocolExtensionEmitter.WithoutModuleQualifiers(signature));
            default:
                return null;
        }
    }

    /// <summary>
    /// Writes the settled rows of every carried receiver into the live report session. Call once,
    /// after emission has settled and before the report is completed.
    /// </summary>
    public static void Settle(ModuleDecl moduleDecl, ModuleEmissionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(moduleDecl);
        ArgumentNullException.ThrowIfNull(ctx);

        var outcomes = ctx.CarriedPrePassOutcomes;
        bool PrePassBound(string receiver, CarriedMemberIdentity identity)
            => outcomes.Any(o => o.Bound
                && string.Equals(o.Receiver, receiver, StringComparison.Ordinal)
                && o.Identity.SameMember(identity));

        foreach (var decline in ctx.CarriedAbiDeclines)
        {
            if (IdentityOf(decline.Member) is { } identity && PrePassBound(decline.Receiver, identity))
                continue;
            CrossModuleExtensionEmitter.WriteDeclineRow(decline.Member, decline.Reason, decline.Details);
        }

        var abiMembers = new Dictionary<string, List<CarriedMemberIdentity>>(StringComparer.Ordinal);
        foreach (var outcome in outcomes)
        {
            if (outcome.Bound || PrePassBound(outcome.Receiver, outcome.Identity))
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

/// <summary>One member of a carried receiver the cross-module path declined, with its row.</summary>
internal sealed record CarriedAbiDecline(string Receiver, BaseDecl Member, SkipReason Reason, string Details);
