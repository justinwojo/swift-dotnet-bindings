// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>One manifest row joined to the scanned member it names, and how strictly it matched.</summary>
public sealed record SurfaceManifestMatch(SurfaceApiManifestRow Row, SurfaceMemberObservation Member, string Tier);

public sealed record SurfaceManifestJoinResult(
    IReadOnlyList<SurfaceManifestMatch> Matches,
    IReadOnlyList<SurfaceApiManifestRow> Phantom,
    IReadOnlyList<SurfaceApiManifestRow> Ambiguous);

/// <summary>
/// Joins API-manifest rows to the C# members a syntax scan found.
/// <para>
/// The manifest key is the generator's projected overload key (<c>ModuleEmissionContext.BuildApiManifestKey</c>),
/// not the emitted text, so the two spell the same member differently: the projection names a type
/// as far as the emitted file's <c>using</c> directives let it (<c>IEnumerable&lt;M.T&gt;</c> where the
/// file writes <c>global::System.Collections.Generic.IEnumerable&lt;global::M.T&gt;</c>), drops reference
/// nullability in places, keeps a generic method's arity marker where the emitter erased it to a
/// non-generic overload, spells a type parameter it could not resolve as <c>Swift.AnyType</c> and a
/// Swift array parameter as whichever read-only collection interface the projection chose, and spells
/// the owner as the Swift parent path — which includes the caseless enums the emitter turns into C#
/// namespaces, and is empty for a free function the emitter writes into a container class. Every
/// manifest in the corpus predates any producer-side join key, so the join is built from what both
/// spellings already carry.
/// </para>
/// <para>
/// Rows are matched in tiers, strictest first. <b>exact</b>: the normalized key as before (several
/// rows may name one member, as they always could). Then, one-to-one against members no row has
/// claimed yet, always on the same owner and name and the same list punctuation, each relaxation
/// one-way, the way the projection loses the distinction: <b>qualification</b> — a type name in the row
/// may lack the qualifier of a namespace the projection names unqualified (<c>System</c>,
/// <c>System.Collections.Generic</c>, <c>System.Runtime.InteropServices.Swift</c>, <c>Swift.Runtime</c>); <b>nullability</b> — the row may also lack a
/// <c>?</c> the member carries on a type that is not a value type; <b>generic-arity</b> — the row may also
/// carry an arity marker the member lacks; <b>projection</b> — the row's <c>Swift.AnyType</c> may also
/// stand for a type parameter of the member, and one read-only collection interface for another. Each
/// tier only forgets a distinction the projection is known to lose; none matches on name and count
/// alone. Within a tier a row is claimed only when it has exactly one candidate, no other pending row
/// has that same single candidate, and every other unclaimed member the projection tier would admit is
/// some other row's single candidate; anything else stays pending for the next tier and, if no tier
/// settles it, is reported ambiguous (it had candidates) or phantom (it never had one). Both fail the
/// target closed, and every match records its tier.
/// </para>
/// </summary>
public static class SurfaceManifestJoin
{
    public const string Exact = "exact";
    public const string Qualification = "qualification";
    public const string Nullability = "nullability";
    public const string GenericArity = "generic-arity";
    public const string Projection = "projection";

    private static readonly HashSet<string> CollectionInterfaces = new(StringComparer.Ordinal)
    {
        "IEnumerable", "IReadOnlyList",
    };

    /// <summary>
    /// The namespaces whose types the projection names unqualified, as the emitted files' usings and
    /// aliases allow (<c>IEnumerable</c>, <c>Action</c>, <c>SwiftError</c>, <c>ISwiftObject</c>): the only qualifier a row may
    /// lack, and only in front of a bare type name.
    /// </summary>
    private static readonly string[] UnqualifiedNamespaces = ["System.Collections.Generic", "System", "System.Runtime.InteropServices.Swift", "Swift.Runtime"];

    /// <summary>The C# value types every target can name; a target's own structs and enums are added.</summary>
    private static readonly string[] BuiltInValueTypes =
    [
        "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "nint", "nuint",
        "char", "float", "double", "decimal",
        "System.Boolean", "System.Byte", "System.SByte", "System.Int16", "System.UInt16", "System.Int32",
        "System.UInt32", "System.Int64", "System.UInt64", "System.IntPtr", "System.UIntPtr", "System.Char",
        "System.Single", "System.Double", "System.Decimal", "System.Runtime.InteropServices.NFloat",
    ];

    private static readonly Regex TokenRegex = new(
        @"@?[A-Za-z_][A-Za-z0-9_]*(?:\.@?[A-Za-z_][A-Za-z0-9_]*)*|[0-9]+|\S",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static SurfaceManifestJoinResult Join(
        IReadOnlyList<SurfaceMemberObservation> members,
        IReadOnlyList<SurfaceApiManifestRow> rows)
    {
        var matches = new List<SurfaceManifestMatch>();
        var ambiguous = new HashSet<SurfaceApiManifestRow>(ReferenceEqualityComparer.Instance);
        var claimed = new HashSet<SurfaceMemberObservation>(ReferenceEqualityComparer.Instance);
        var valueTypes = ValueTypes(members);

        // Each row is read against the namespace its own manifest file was written for.
        // A type some row names as its owner is a Swift type, so it is not the free-function
        // container even when it carries one of the container names.
        var indexes = new Dictionary<(string Root, string Module), FormIndex>();
        FormIndex IndexFor(SurfaceApiManifestRow row)
        {
            var key = (row.RootNamespace, row.Module);
            if (!indexes.TryGetValue(key, out var index))
            {
                var rowOwners = rows
                    .Where(r => r.RootNamespace == row.RootNamespace && r.Module == row.Module)
                    .Select(r => FirstOwnerSegment(r.Signature))
                    .OfType<string>()
                    .ToHashSet(StringComparer.Ordinal);
                indexes[key] = index = new FormIndex(members
                    .SelectMany(m => MemberForms(m, row.RootNamespace, row.Module, rowOwners))
                    .ToList());
            }
            return index;
        }

        List<SurfaceMemberObservation> Unclaimed(SurfaceApiManifestRow row, SignatureShape rowShape, string tier)
            => IndexFor(row).Lookup(tier, TierKey(rowShape, tier))
                .Where(f => !claimed.Contains(f.Member))
                .Where(f => Compatible(rowShape, f.Shape, tier, valueTypes))
                .Select(f => f.Member)
                .Distinct(ReferenceEqualityComparer.Instance)
                .Cast<SurfaceMemberObservation>()
                .ToList();

        // Tier exact: the historical join, many rows to one member allowed.
        var pending = new List<SurfaceApiManifestRow>();
        foreach (var row in rows)
        {
            var normalized = SurfaceSyntaxScanner.NormalizeManifestSignature(row.Signature);
            var candidates = IndexFor(row).Lookup(Exact, normalized)
                .Select(f => f.Member)
                .Distinct(ReferenceEqualityComparer.Instance)
                .Cast<SurfaceMemberObservation>()
                .ToList();
            if (candidates.Count == 1)
            {
                matches.Add(new SurfaceManifestMatch(row, candidates[0], Exact));
                claimed.Add(candidates[0]);
            }
            else
            {
                if (candidates.Count > 1) ambiguous.Add(row);
                pending.Add(row);
            }
        }

        // Exact-tier ambiguity is final: a looser tier would only see more candidates.
        pending.RemoveAll(ambiguous.Contains);

        foreach (var tier in new[] { Qualification, Nullability, GenericArity, Projection })
        {
            // Claims at a tier can settle rows that competed for the claimed members, so the tier
            // repeats until it claims nothing more.
            var progressed = true;
            while (pending.Count > 0 && progressed)
            {
                progressed = false;
                var shapes = new Dictionary<SurfaceApiManifestRow, SignatureShape>(ReferenceEqualityComparer.Instance);
                var candidatesByRow = new Dictionary<SurfaceApiManifestRow, List<SurfaceMemberObservation>>(ReferenceEqualityComparer.Instance);
                foreach (var row in pending)
                {
                    shapes[row] = Parse(SurfaceSyntaxScanner.NormalizeManifestSignature(row.Signature));
                    candidatesByRow[row] = Unclaimed(row, shapes[row], tier);
                }

                var singleTargets = new Dictionary<SurfaceMemberObservation, int>(ReferenceEqualityComparer.Instance);
                foreach (var candidates in candidatesByRow.Values)
                    if (candidates.Count == 1)
                        singleTargets[candidates[0]] = singleTargets.GetValueOrDefault(candidates[0]) + 1;

                var stillPending = new List<SurfaceApiManifestRow>();
                foreach (var row in pending)
                {
                    var candidates = candidatesByRow[row];
                    var member = candidates.Count == 1 && singleTargets[candidates[0]] == 1 ? candidates[0] : null;
                    // A stricter tier must not settle a row while the loosest reading still admits
                    // another unclaimed member that no other row accounts for: that member could be
                    // the one the row describes (its own overload deleted, a sibling standing in).
                    if (member is not null && tier != Projection
                        && Unclaimed(row, shapes[row], Projection)
                            .Any(other => !ReferenceEquals(other, member) && singleTargets.GetValueOrDefault(other) != 1))
                        member = null;
                    if (member is not null)
                    {
                        matches.Add(new SurfaceManifestMatch(row, member, tier));
                        claimed.Add(member);
                        ambiguous.Remove(row);
                        progressed = true;
                        continue;
                    }
                    if (candidates.Count > 0) ambiguous.Add(row);
                    stillPending.Add(row);
                }
                pending = stillPending;
            }
        }

        var unsettled = new HashSet<SurfaceApiManifestRow>(pending, ReferenceEqualityComparer.Instance);
        var phantom = rows.Where(row => unsettled.Contains(row) && !ambiguous.Contains(row)).ToList();
        return new SurfaceManifestJoinResult(
            matches,
            phantom,
            rows.Where(ambiguous.Contains).ToList());
    }

    /// <summary>
    /// The owner path the producer writes for <paramref name="key"/>: the namespaces below the
    /// manifest's root namespace (each a Swift caseless enum), then the enclosing C# types. Null when
    /// the member lies outside the root namespace, so the producer cannot have described it.
    /// </summary>
    public static string? ProducerOwner(SurfacePublicKey key, string rootNamespace)
    {
        var enclosing = key.EnclosingTypes.Select(SurfaceSyntaxScanner.RemoveArity);
        string relative;
        if (string.Equals(key.Namespace, rootNamespace, StringComparison.Ordinal))
            relative = "";
        else if (key.Namespace.StartsWith(rootNamespace + ".", StringComparison.Ordinal))
            relative = key.Namespace[(rootNamespace.Length + 1)..];
        else
            return null;
        return string.Join('.', (relative.Length == 0 ? [] : relative.Split('.')).Concat(enclosing));
    }

    /// <summary>
    /// Whether <paramref name="owner"/> can be the class the generator writes a module's free
    /// functions into (ModuleHandler): the module name, unless the root namespace already ends with it;
    /// then Functions, or GlobalFunctions, {Module}Functions or FunctionsN when a type holds the name.
    /// Which of those is the container the names alone cannot say; the join also drops any a row
    /// names as its owner.
    /// </summary>
    public static bool IsFreeFunctionContainer(string owner, string module, string rootNamespace)
    {
        var stutters = string.Equals(rootNamespace, module, StringComparison.Ordinal)
            || rootNamespace.EndsWith("." + module, StringComparison.Ordinal);
        if (!stutters)
            return string.Equals(owner, module, StringComparison.Ordinal);
        return owner is "Functions" or "GlobalFunctions"
            || string.Equals(owner, module + "Functions", StringComparison.Ordinal)
            || Regex.IsMatch(owner, "^Functions[0-9]+$", RegexOptions.CultureInvariant);
    }

    private static IEnumerable<MemberForm> MemberForms(
        SurfaceMemberObservation member, string rootNamespace, string module, IReadOnlySet<string> rowOwners)
    {
        if (member.PublicKey.DeclarationKind is "class" or "struct" or "interface" or "enum" or "record"
            or "record-struct" or "type" or "delegate")
            yield break;
        var owner = ProducerOwner(member.PublicKey, rootNamespace);
        if (owner is null) yield break;
        yield return Form(member, SurfaceSyntaxScanner.BuildManifestSignature(member.PublicKey, owner));
        // A free function, a static method of the module's container class: the producer writes no
        // owner for it.
        if (member.PublicKey.DeclarationKind is "method"
            && member.PublicKey.Static
            && member.PublicKey.EnclosingTypes.Count == 1
            && string.Equals(member.PublicKey.Namespace, rootNamespace, StringComparison.Ordinal)
            && IsFreeFunctionContainer(owner, module, rootNamespace)
            && !rowOwners.Contains(owner))
            yield return Form(member, SurfaceSyntaxScanner.BuildManifestSignature(member.PublicKey, ""));
    }

    private static MemberForm Form(SurfaceMemberObservation member, string signature)
    {
        var normalized = SurfaceSyntaxScanner.NormalizeManifestSignature(signature);
        return new MemberForm(member, normalized, Parse(normalized, member.PublicKey.ManifestGenericNames));
    }

    /// <summary>The first segment of a row's owner path, or null for an owner-less row.</summary>
    private static string? FirstOwnerSegment(string signature)
    {
        var head = Parse(SurfaceSyntaxScanner.NormalizeManifestSignature(signature)).Head;
        var dot = head.IndexOf('.');
        return dot < 0 ? null : head[..dot];
    }

    /// <summary>The full names of the value types a row's missing <c>?</c> cannot have dropped.</summary>
    private static HashSet<string> ValueTypes(IReadOnlyList<SurfaceMemberObservation> members)
    {
        var set = new HashSet<string>(BuiltInValueTypes, StringComparer.Ordinal);
        foreach (var member in members)
        {
            var key = member.PublicKey;
            if (key.DeclarationKind is not ("struct" or "enum" or "record-struct"))
                continue;
            var path = (key.Namespace.Length == 0 ? [] : new[] { key.Namespace })
                .Concat(key.EnclosingTypes.Select(SurfaceSyntaxScanner.RemoveArity))
                .Append(SurfaceSyntaxScanner.RemoveArity(key.Name));
            set.Add(string.Join('.', path));
        }
        return set;
    }

    /// <summary>
    /// A key split into its head (owner path and name, compared verbatim) and the tokens of its
    /// parameter or index list and arity marker. A property key has no list.
    /// </summary>
    internal static SignatureShape Parse(string signature, IReadOnlyList<string>? genericNames = null)
    {
        var generics = genericNames ?? [];
        var paren = signature.IndexOf('(');
        var bracket = signature.IndexOf('[');
        var listStart = paren < 0 ? bracket : bracket < 0 ? paren : Math.Min(paren, bracket);
        if (listStart < 0)
            return new SignatureShape(signature, [], HasList: false, generics);
        var head = signature[..listStart];
        var tokens = TokenRegex.Matches(signature[listStart..]).Select(m => m.Value).ToList();
        return new SignatureShape(head, tokens, HasList: true, generics);
    }

    /// <summary>
    /// The lookup key of a tier, coarse enough that every member <see cref="Compatible"/> can accept
    /// shares the row's key: names by their last segment, <c>?</c> dropped from nullability on, the arity
    /// marker from generic-arity on, and at projection every name read alike (the list's punctuation
    /// remains).
    /// </summary>
    internal static string TierKey(SignatureShape shape, string tier)
    {
        if (!shape.HasList) return shape.Head;
        var sb = new StringBuilder(shape.Head);
        var tokens = tier is GenericArity or Projection ? WithoutArity(shape.Tokens.ToList()) : shape.Tokens;
        foreach (var token in tokens)
        {
            if (token == "?" && tier != Qualification) continue;
            sb.Append(!IsName(token) ? token : tier == Projection ? "#" : LastSegment(token)).Append(' ');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Whether <paramref name="member"/> can be what <paramref name="row"/> describes at
    /// <paramref name="tier"/>, token for token, each relaxation one-way, the way the projection loses
    /// it. Punctuation is identical, and a type name is the member's or lacks only a qualifier the
    /// projection omits (<see cref="UnqualifiedNamespaces"/>). From nullability on the row may lack a
    /// <c>?</c> the member carries on a type that is not a value type: the producer drops it only for a
    /// C# reference type (<c>ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity</c>). From
    /// generic-arity on the row may carry an arity marker the member lacks (an overload whose type
    /// parameter the emitter erased), never the reverse. At projection the row's <c>Swift.AnyType</c>
    /// stands for a type parameter of the member, and one read-only collection interface for another.
    /// </summary>
    internal static bool Compatible(SignatureShape row, SignatureShape member, string tier, IReadOnlySet<string> valueTypes)
    {
        if (!string.Equals(row.Head, member.Head, StringComparison.Ordinal) || row.HasList != member.HasList)
            return false;
        var a = WithoutArity(row.Tokens.ToList());
        var b = WithoutArity(member.Tokens.ToList());
        var rowArity = row.Tokens.Skip(a.Count).ToList();
        var memberArity = member.Tokens.Skip(b.Count).ToList();
        if (!rowArity.SequenceEqual(memberArity, StringComparer.Ordinal)
            && !(tier is GenericArity or Projection && memberArity.Count == 0))
            return false;
        var nullabilityMayDiffer = tier != Qualification;
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            if (Equivalent(a[i], b[j], member, tier)) { i++; j++; continue; }
            if (nullabilityMayDiffer && b[j] == "?" && !AnnotatesValueType(b, j, valueTypes)) { j++; continue; }
            return false;
        }
        while (j < b.Count && nullabilityMayDiffer && b[j] == "?" && !AnnotatesValueType(b, j, valueTypes)) j++;
        return i == a.Count && j == b.Count;
    }

    /// <summary>Whether the <c>?</c> at <paramref name="index"/> makes a value type nullable.</summary>
    private static bool AnnotatesValueType(IReadOnlyList<string> tokens, int index, IReadOnlySet<string> valueTypes)
    {
        var k = index - 1;
        if (k < 0) return false;
        if (tokens[k] == ")") return true; // a tuple, a ValueTuple
        if (tokens[k] == ">")
        {
            // A constructed type: the name before its matching '<'.
            var depth = 0;
            for (; k >= 0; k--)
            {
                if (tokens[k] == ">") depth++;
                else if (tokens[k] == "<" && --depth == 0) { k--; break; }
            }
        }
        if (k < 0 || !IsName(tokens[k])) return false;
        return valueTypes.Contains(tokens[k]);
    }

    private static bool Equivalent(string rowToken, string memberToken, SignatureShape member, string tier)
    {
        if (!IsName(rowToken) || !IsName(memberToken))
            return string.Equals(rowToken, memberToken, StringComparison.Ordinal);
        if (SameType(rowToken, memberToken))
            return true;
        if (tier != Projection)
            return false;
        return (rowToken == "Swift.AnyType" && (member.GenericNames.Contains(memberToken) || memberToken == "Swift.AnyType"))
            || (IsCollectionInterface(rowToken) && IsCollectionInterface(memberToken));
    }

    private static bool SameType(string rowToken, string memberToken)
        => string.Equals(rowToken, memberToken, StringComparison.Ordinal)
            || (!rowToken.Contains('.', StringComparison.Ordinal)
                && UnqualifiedNamespaces.Any(ns => string.Equals(memberToken, ns + "." + rowToken, StringComparison.Ordinal)));

    private static bool IsCollectionInterface(string token)
        => CollectionInterfaces.Contains(token)
            || (token.StartsWith("System.Collections.Generic.", StringComparison.Ordinal)
                && CollectionInterfaces.Contains(token["System.Collections.Generic.".Length..]));

    private static List<string> WithoutArity(List<string> tokens)
    {
        var tick = tokens.LastIndexOf("`");
        var close = Math.Max(tokens.LastIndexOf(")"), tokens.LastIndexOf("]"));
        return tick > close ? tokens.Take(tick).ToList() : tokens;
    }

    private static bool IsName(string token) => token.Length > 0 && (char.IsLetter(token[0]) || token[0] is '_' or '@');

    private static string LastSegment(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[(dot + 1)..];
    }

    internal sealed record SignatureShape(string Head, IReadOnlyList<string> Tokens, bool HasList, IReadOnlyList<string> GenericNames);

    private sealed record MemberForm(SurfaceMemberObservation Member, string Normalized, SignatureShape Shape);

    /// <summary>The member forms of one manifest root, looked up by each tier's key.</summary>
    private sealed class FormIndex(IReadOnlyList<MemberForm> forms)
    {
        private readonly Dictionary<string, ILookup<string, MemberForm>> _byTier = new(StringComparer.Ordinal);

        public IEnumerable<MemberForm> Lookup(string tier, string key)
        {
            if (!_byTier.TryGetValue(tier, out var lookup))
                _byTier[tier] = lookup = forms.ToLookup(
                    f => tier == Exact ? f.Normalized : TierKey(f.Shape, tier), StringComparer.Ordinal);
            return lookup[key];
        }
    }
}
