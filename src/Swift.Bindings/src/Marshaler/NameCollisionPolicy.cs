// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BindingsGeneration;

/// <summary>
/// The de-collision schemes a generated binding may apply to an identifier. Each scheme names
/// exactly one collision shape, renames exactly one side of it, and draws its disambiguating
/// token from exactly one of the two vocabularies in <see cref="NameCollisionPolicy"/>.
/// </summary>
public enum NameCollisionScheme
{
    /// <summary>
    /// A nested type collides (CS0102) with a sibling property whose type IS that nested type.
    /// The <b>type</b> is renamed with a kind-aware semantic suffix — enum → <c>Kind</c>,
    /// struct/class → <c>Info</c> — so the consumer-facing property keeps its natural name.
    /// </summary>
    NestedTypeKindSuffix,

    /// <summary>
    /// A member's name equals its enclosing type's name (CS0542), or equals a sibling nested
    /// type's name where that nested type was NOT renamed by
    /// <see cref="NestedTypeKindSuffix"/>. The <b>member</b> is renamed with a <c>Value</c>
    /// suffix.
    /// </summary>
    PropertyValueSuffix,

    /// <summary>
    /// A method's projected name equals a sibling property's name (or a declaring-type generic
    /// parameter's name, CS0102). The <b>method</b> is renamed — <c>Method</c> suffix normally,
    /// <c>With</c> prefix when the method is self-returning so a fluent builder stays fluent.
    /// </summary>
    MethodSuffix,

    /// <summary>
    /// A noun-only zero-argument returning method reads as a getter, or a method's name equals
    /// its enclosing type's name (CS0542). The <b>method</b> is renamed with a <c>Get</c>
    /// prefix.
    /// </summary>
    MethodGetPrefix,

    /// <summary>
    /// A type's emitted C# name equals — case-insensitively but not ordinally — the name of a
    /// sibling Swift container that emits as a C# <i>namespace</i> rather than a class (see
    /// <c>NamespaceFacadeDetector</c>). C# accepts the pair, but a reader cannot tell the two
    /// apart at a glance and case-normalizing tooling treats them as one identifier. The
    /// <b>non-facade type</b> is renamed with the same kind-aware suffix as
    /// <see cref="NestedTypeKindSuffix"/>; the facade keeps its name because it is a namespace
    /// segment for every type nested inside it and is referenced under that spelling
    /// cross-module.
    /// </summary>
    CaseOnlyNamespaceCollision,

    /// <summary>
    /// Two sibling members' Swift names differ only by case and project onto the same (or a
    /// case-insensitively equal) C# identifier — e.g. Swift <c>url</c> and <c>URL</c> both
    /// PascalCase to <c>Url</c>. The <b>later-declared member</b> is renamed with a numeric
    /// suffix, matching the enum-case rule in <c>NameProvider.ComputeCaseNameMap</c>.
    /// </summary>
    CaseOnlyMemberCollision,
}

/// <summary>Which side of a collision a scheme renames.</summary>
public enum NameCollisionSide
{
    /// <summary>The rename lands on a type identifier.</summary>
    Type,

    /// <summary>The rename lands on a member identifier (property, method, enum case).</summary>
    Member,
}

/// <summary>
/// The single decision surface for every de-collision rename a binding applies to a type or
/// member identifier. <c>NameProvider</c>'s individual scheme sites delegate their token choice
/// and their numeric-fallback loop here, so the vocabulary and the precedence between schemes
/// live in exactly one place instead of being re-derived at each site.
///
/// <para><b>Two vocabularies, deliberately disjoint.</b> A reader of a generated binding must be
/// able to tell which side of a collision moved just from the token:
/// <list type="bullet">
///   <item><description><b>Type-side</b> tokens — <c>Info</c>, <c>Kind</c>. Seeing one means the
///   TYPE was renamed and the member kept its natural name.</description></item>
///   <item><description><b>Member-side</b> tokens — <c>Value</c>, <c>Method</c>, <c>With</c>,
///   <c>Get</c>, <c>Swift</c>. Seeing one means the MEMBER was renamed and the type kept its
///   natural name.</description></item>
/// </list>
/// The two sets must never share a token; <c>NameCollisionPolicyTests</c> pins that.
/// A numeric suffix (<c>2</c>, <c>3</c>, …) is the shared last resort on both sides — it is a
/// tiebreak, not a vocabulary, and it fires only when the semantic name is itself taken.</para>
///
/// <para><b>Precedence.</b> The schemes are evaluated in the order given by
/// <see cref="Precedence"/>, and that order is load-bearing rather than incidental:
/// <list type="number">
///   <item><description><see cref="NameCollisionScheme.NestedTypeKindSuffix"/> runs first, as a
///   module pre-pass, because renaming a type changes what every later pass sees. It resolves
///   the collision by moving the TYPE, which is why the member-side scheme below has an
///   explicit "already handled" exclusion rather than racing it.</description></item>
///   <item><description><see cref="NameCollisionScheme.CaseOnlyNamespaceCollision"/> runs
///   immediately after, in the same pre-pass phase and also on the type side, so a type renamed
///   for a namespace-case collision is already renamed before any member name is
///   computed.</description></item>
///   <item><description><see cref="NameCollisionScheme.CaseOnlyMemberCollision"/> runs next
///   because it decides a member's BASE C# name (which of two case-variant Swift spellings owns
///   the natural identifier). Every member-side scheme below operates on that base
///   name.</description></item>
///   <item><description><see cref="NameCollisionScheme.PropertyValueSuffix"/> — member side,
///   skipped for any collision the type-side pass already resolved.</description></item>
///   <item><description><see cref="NameCollisionScheme.MethodGetPrefix"/> runs BEFORE
///   <see cref="NameCollisionScheme.MethodSuffix"/>: an async getter colliding with a property
///   should read <c>GetStatusAsync</c>, not <c>StatusMethodAsync</c>. Applying the suffix first
///   would produce the latter and the <c>Get</c> prefix would then never fire, because the
///   suffixed name no longer collides.</description></item>
///   <item><description><see cref="NameCollisionScheme.MethodSuffix"/> last on the method path —
///   it is the fallback for a collision the <c>Get</c> prefix did not already
///   dissolve.</description></item>
/// </list></para>
///
/// <para><b>Persistence.</b> Every scheme's decision survives the module-database XML round-trip,
/// so a downstream module reads the name the producing module actually emitted instead of
/// recomputing it from inputs it cannot fully see:
/// <list type="bullet">
///   <item><description>Type-side schemes (<see cref="NameCollisionScheme.NestedTypeKindSuffix"/>,
///   <see cref="NameCollisionScheme.CaseOnlyNamespaceCollision"/>) persist as the type record's
///   own identity — the <c>managedTypeName</c> attribute.</description></item>
///   <item><description>Member-side property schemes persist as <c>&lt;renamedMembers&gt;</c>
///   entries on the declaring type's record. Re-derivation on load was rejected: a member rename
///   is a function of the whole sibling set (which members are emittable, which nested types
///   exist, which siblings were themselves renamed), not of the member declaration alone. The
///   entries are read back by <see cref="CaseOnlyCollisionPass"/>, which is how a type in a
///   downstream module conforming to a DEPENDENCY module's protocol binds each requirement to the
///   C# name that protocol was actually emitted under instead of choosing its own.
///   <c>&lt;emittedMethods&gt;</c> remains the cross-module authority for class instance methods:
///   it records name AND parameter types, which is strictly stronger than a rename
///   entry.</description></item>
/// </list></para>
///
/// <para><b>Not a scheme:</b> an "outer-type prefix on the nested type" rename
/// (<c>Camera.Position</c> → <c>Camera.CameraPosition</c>) was previously believed to be a fifth
/// scheme. It is not: the vendor library that produced that evidence declares the nested enum as
/// <c>CameraPosition</c> in its own <c>.swiftinterface</c>, and no bare <c>Position</c> symbol
/// exists there. The generator transcribed the Swift name faithfully; no rename fired. Do not
/// add a prefix-shaped scheme on that evidence. Distinct and deliberately NOT converged here:
/// Apple platform-type flattening (<c>UIKit.UIView.ContentMode</c> → <c>UIViewContentMode</c>)
/// mirrors the platform bindings' own spelling and is an identity mapping, not a collision fix.
/// </para>
/// </summary>
public static class NameCollisionPolicy
{
    // ---- Type-side vocabulary -------------------------------------------------------------

    /// <summary>Suffix for a renamed nested ENUM: a closed case-set (SyntaxKind, DateTimeKind).</summary>
    public const string EnumTypeSuffix = "Kind";

    /// <summary>Suffix for a renamed nested STRUCT/CLASS: a data aggregate (FileInfo, ProcessStartInfo).</summary>
    public const string AggregateTypeSuffix = "Info";

    // ---- Member-side vocabulary -----------------------------------------------------------

    /// <summary>Suffix for a property renamed away from a type name.</summary>
    public const string MemberValueSuffix = "Value";

    /// <summary>Suffix for a method renamed away from a property name.</summary>
    public const string MemberMethodSuffix = "Method";

    /// <summary>Prefix for a self-returning (fluent builder) method renamed away from a property name.</summary>
    public const string MemberBuilderPrefix = "With";

    /// <summary>Prefix for a getter-shaped method, or a method renamed away from its enclosing type's name.</summary>
    public const string MemberGetPrefix = "Get";

    /// <summary>Suffix for a method renamed away from a member inherited from the BCL (Dispose, ToString, …).</summary>
    public const string MemberInheritedSuffix = "Swift";

    /// <summary>The type-side token set. Disjoint from <see cref="MemberSideTokens"/> by design.</summary>
    public static IReadOnlyCollection<string> TypeSideTokens { get; } =
        new[] { EnumTypeSuffix, AggregateTypeSuffix };

    /// <summary>The member-side token set. Disjoint from <see cref="TypeSideTokens"/> by design.</summary>
    public static IReadOnlyCollection<string> MemberSideTokens { get; } =
        new[]
        {
            MemberValueSuffix, MemberMethodSuffix, MemberBuilderPrefix,
            MemberGetPrefix, MemberInheritedSuffix,
        };

    /// <summary>
    /// The schemes in evaluation order. See the type-level remarks for why each edge in this
    /// order is load-bearing.
    /// </summary>
    public static IReadOnlyList<NameCollisionScheme> Precedence { get; } = new[]
    {
        NameCollisionScheme.NestedTypeKindSuffix,
        NameCollisionScheme.CaseOnlyNamespaceCollision,
        NameCollisionScheme.CaseOnlyMemberCollision,
        NameCollisionScheme.PropertyValueSuffix,
        NameCollisionScheme.MethodGetPrefix,
        NameCollisionScheme.MethodSuffix,
    };

    /// <summary>Which identifier each scheme renames.</summary>
    public static NameCollisionSide SideOf(NameCollisionScheme scheme) => scheme switch
    {
        NameCollisionScheme.NestedTypeKindSuffix => NameCollisionSide.Type,
        NameCollisionScheme.CaseOnlyNamespaceCollision => NameCollisionSide.Type,
        _ => NameCollisionSide.Member,
    };

    // ---- Type-side decisions ---------------------------------------------------------------

    /// <summary>
    /// The kind-aware semantic suffix for a renamed nested type: an enum is a closed case-set
    /// (→ <c>Kind</c>), a struct/class is a data aggregate (→ <c>Info</c>).
    /// </summary>
    public static string TypeSuffixFor(TypeDecl nestedType) =>
        nestedType is EnumDecl ? EnumTypeSuffix : AggregateTypeSuffix;

    /// <summary>
    /// Builds the type-side replacement leaf for <paramref name="leafName"/>.
    /// <para>Anti-stutter: when the Swift leaf already ends in the chosen suffix (an enum
    /// <c>TokenKind</c>, a struct <c>PayloadInfo</c>) the leaf is used as-is rather than doubled
    /// into <c>KindKind</c>/<c>InfoInfo</c>; the numeric fallback then disambiguates.</para>
    /// <para>The numeric fallback fires only when the semantic name is itself already claimed —
    /// <paramref name="isTaken"/> reports that, and the returned name additionally never equals
    /// <paramref name="leafName"/> (otherwise the rename would be a no-op and the collision
    /// would survive).</para>
    /// </summary>
    public static string ResolveTypeSideName(string leafName, string suffix, Func<string, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(isTaken);
        var baseLeafName = leafName.EndsWith(suffix, StringComparison.Ordinal)
            ? leafName
            : leafName + suffix;
        var candidate = baseLeafName;
        for (int dedupSuffix = 2;
             candidate == leafName || isTaken(candidate);
             dedupSuffix++)
        {
            candidate = $"{baseLeafName}{dedupSuffix}";
        }
        return candidate;
    }

    // ---- Member-side decisions -------------------------------------------------------------

    /// <summary>
    /// The member-side name for a member that collides with a type name — its own enclosing type
    /// (CS0542) or a sibling nested type the type-side pass declined to rename.
    /// <paramref name="isTaken"/> drives the numeric fallback; pass a constant <c>false</c>
    /// predicate for the single-shot enclosing-type check, which has no sibling set to escalate
    /// against.
    /// </summary>
    public static string ResolveMemberValueName(string memberName, Func<string, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(isTaken);
        var baseName = memberName + MemberValueSuffix;
        var candidate = baseName;
        for (int dedupSuffix = 2; isTaken(candidate); dedupSuffix++)
            candidate = $"{memberName}{MemberValueSuffix}{dedupSuffix}";
        return candidate;
    }

    /// <summary>
    /// The member-side name for a method that collides with a sibling property name or with a
    /// declaring-type generic parameter. A self-returning method keeps its fluent reading via the
    /// <c>With</c> prefix; every other method takes the <c>Method</c> suffix.
    /// </summary>
    public static string ResolveMethodCollisionName(string methodName, bool isSelfReturning) =>
        isSelfReturning ? $"{MemberBuilderPrefix}{methodName}" : $"{methodName}{MemberMethodSuffix}";

    /// <summary>The member-side name for a getter-shaped method or a method colliding with its enclosing type.</summary>
    public static string ResolveGetPrefixedName(string methodName) => $"{MemberGetPrefix}{methodName}";

    /// <summary>The member-side name for a method colliding with a member inherited from the BCL.</summary>
    public static string ResolveInheritedCollisionName(string methodName) => $"{methodName}{MemberInheritedSuffix}";

    /// <summary>
    /// Deterministic numeric disambiguation for the case-only member collision: the first
    /// declaration keeps the natural identifier, each later case-variant sibling takes the next
    /// free numeric suffix. Mirrors <c>NameProvider.ComputeCaseNameMap</c>'s enum-case rule so
    /// the two case-collision paths cannot drift.
    /// </summary>
    public static string ResolveCaseOnlyMemberName(string memberName, Func<string, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(isTaken);
        var candidate = memberName;
        for (int dedupSuffix = 2; isTaken(candidate); dedupSuffix++)
            candidate = $"{memberName}{dedupSuffix}";
        return candidate;
    }
}

/// <summary>
/// One recorded de-collision decision about a MEMBER identifier, stamped onto the declaring
/// type's record so it survives the module-database XML round-trip. Type-side decisions need no
/// entry here — they are already the type record's own <c>managedTypeName</c>.
/// </summary>
/// <param name="Kind">Which member namespace the entry belongs to — see <see cref="RenamedMemberKind"/>.</param>
/// <param name="SwiftName">The member's Swift name.</param>
/// <param name="IsStatic">
/// Staticness, which is part of the identity: Swift permits a static and an instance member with
/// the same identifier, so <c>(Kind, SwiftName)</c> alone does not name one member.
/// </param>
/// <param name="CSharpName">The C# name the producing module actually emitted.</param>
/// <param name="Scheme">
/// The <see cref="NameCollisionScheme"/> that settled <paramref name="CSharpName"/>. A member that
/// passed through more than one channel records the LAST one — the earlier decision is already
/// folded into the name this entry carries, which is what a reader needs.
/// </param>
public sealed record RenamedMember(
    string Kind, string SwiftName, bool IsStatic, string CSharpName, string Scheme);

/// <summary>The member namespaces <see cref="RenamedMember.Kind"/> can name.</summary>
public static class RenamedMemberKind
{
    /// <summary>A property. The only kind written today — see <see cref="RenamedMember"/>.</summary>
    public const string Property = "property";

    /// <summary>
    /// A method. Reserved: class instance methods already persist their full emitted identity
    /// (name AND parameter types) in <c>&lt;emittedMethods&gt;</c>, which is a strictly stronger
    /// record than a rename entry and stays the cross-module authority for them.
    /// </summary>
    public const string Method = "method";
}
