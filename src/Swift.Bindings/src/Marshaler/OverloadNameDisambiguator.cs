// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text;

namespace BindingsGeneration;

/// <summary>How a colliding overload got the C# name it emits under.</summary>
internal enum OverloadNameOutcome
{
    /// <summary>Uncontested, or the family's single bare-name owner — emits under its natural shaped name.</summary>
    Natural,
    /// <summary>Discriminated by its Swift argument labels (<c>configure(zebra:)</c> → <c>ConfigureZebra</c>).</summary>
    LabelDerived,
    /// <summary>Discriminated by its Swift parameter types (<c>transform(_: RefBox?)</c> → <c>TransformWithOptionalRefBox</c>).</summary>
    TypeDerived,
    /// <summary>Nothing in the ladder could distinguish it from an already-named sibling — dropped, with a report entry.</summary>
    Refused,
}

/// <summary>
/// One member's assignment. <see cref="NameInput"/> is a Swift-LEVEL base name that replaces
/// <c>decl.Name</c> BEFORE <c>NameProvider.GetPublicMethodName</c> shapes it — never a finished C#
/// name. Feeding the shaper an input (rather than concatenating onto its output) is what keeps the
/// property-collision rename, the <c>Get</c> prefix, the CS0542/CS0102 renames and the <c>Async</c>
/// suffix applying to the disambiguated name exactly as they do to a natural one: an async
/// <c>configure(zebra:)</c> becomes <c>ConfigureZebraAsync</c>, not <c>ConfigureAsyncZebra</c>.
/// </summary>
internal readonly record struct OverloadNameAssignment(
    OverloadNameOutcome Outcome,
    string? NameInput,
    string? Detail)
{
    public static OverloadNameAssignment Natural { get; } = new(OverloadNameOutcome.Natural, null, null);

    public bool IsRefused => Outcome == OverloadNameOutcome.Refused;
}

/// <summary>
/// Assigns content-derived C# names to Swift overloads that collide on the projected C# key.
///
/// The rule this replaces gave the bare name to the first-declared overload and numbered the rest
/// (<c>Configure</c> / <c>Configure2</c> / <c>Configure3</c>). Both halves of that were problems: the
/// suffix carries no meaning at a call site, and the rank shifts when upstream inserts an overload
/// earlier in the file — silently renaming API a consumer already compiled against. Every name here is
/// a function of the member's own Swift signature, so inserting a sibling cannot rename an existing
/// binding, and the name says which overload it is.
///
/// The ladder, applied to a family (all members sharing one projected key):
/// <list type="number">
/// <item><description><b>Bare-name ownership.</b> The bare name goes to the member that is BOTH
/// label-less AND whose name reaches the contested spelling with no collision-avoidance rename applied
/// — a content fact, not a rank. Zero such members (an all-labeled family) or more than one
/// (two label-less siblings that differ only by type) means nobody owns it and every member is
/// discriminated.</description></item>
/// <item><description><b>Label discrimination.</b> <c>method.Name</c> plus each non-positional,
/// non-synthesized external argument label, ObjC-selector style. Deliberately NOT trimmed against what
/// the siblings happen to be called: a family-relative trim makes a member's name depend on its
/// neighbours, so adding one overload renames another — the instability being cured.</description></item>
/// <item><description><b>Type discrimination.</b> <c>With{Type}[And{Type}]</c> from the SWIFT parameter
/// types. Swift-side and not projected: the family is grouped BY the projected key, so every member's
/// projected parameter list is identical by construction and a projected-type token would be constant
/// across the family. Swift types also keep the public name independent of projection changes
/// (<c>nint</c> vs <c>long</c>), which would otherwise be silent source breaks.</description></item>
/// <item><description><b>Refusal.</b> Two members whose Swift parameter lists are also identical differ
/// only by return type (or are true duplicates); C# cannot express both and no token can name them
/// apart. The later one is dropped with a <c>DuplicateSignature</c> report entry naming both Swift
/// signatures, rather than emitted as an opaque <c>Name2</c>.</description></item>
/// </list>
///
/// The map is a pure function of the type's method set and is memoized per <see cref="TypeDecl"/>, so
/// the emission loop and the separately-invoked <c>ProtocolConformanceValidator</c> (which has to
/// predict the very name the class body will emit, or it keeps a conformance the class never satisfies
/// → CS0535) resolve the SAME instance without threading state between them.
///
/// Grouping and assignment are deliberately property-AGNOSTIC, matching
/// <see cref="ProtocolMethodDisambiguator"/>: a <c>Foo</c>→<c>FooMethod</c> property-collision rename
/// shifts every member of a family uniformly, so it cannot change whether two members collide, and
/// threading a caller-supplied property set in here would make the map's content vary by caller — the
/// cross-site divergence the memo exists to prevent. The residual it cannot see (a property rename that
/// pushes an uncontested method onto a name a sibling already took) is caught by the emission loop's own
/// escalation through <see cref="Escalate"/>, which walks the same ladder.
/// </summary>
internal static class OverloadNameDisambiguator
{
    private static readonly Dictionary<MethodDecl, OverloadNameAssignment> EmptyMap = new(ReferenceEqualityComparer.Instance);

    // Keyed by reference identity: TypeDecl is a record, so value equality would alias two distinct
    // types with equal contents onto one map.
    private static readonly ConditionalWeakTable<TypeDecl, Dictionary<MethodDecl, OverloadNameAssignment>> _cache = new();

    /// <summary>
    /// The overload-name assignments for one type body. Memoized per <paramref name="parent"/>; every
    /// caller that needs to know what a class member will be called reads this, so the emitter and the
    /// conformance validator cannot disagree.
    /// </summary>
    public static IReadOnlyDictionary<MethodDecl, OverloadNameAssignment> ForTypeBody(TypeDecl? parent, ITypeDatabase typeDatabase)
    {
        if (parent == null)
            return EmptyMap;
        return _cache.GetValue(parent, p => ComputeForTypeBody(p, typeDatabase));
    }

    /// <summary>
    /// The assignment for one method, or <see cref="OverloadNameAssignment.Natural"/> when it is in no
    /// collision family. Convenience over <see cref="ForTypeBody"/> for single-member queries.
    /// </summary>
    public static OverloadNameAssignment ForMethod(MethodDecl method, ITypeDatabase typeDatabase)
        => ForTypeBody(method.ParentDecl as TypeDecl, typeDatabase).TryGetValue(method, out var a)
            ? a
            : OverloadNameAssignment.Natural;

    private static Dictionary<MethodDecl, OverloadNameAssignment> ComputeForTypeBody(TypeDecl parent, ITypeDatabase typeDatabase)
    {
        // Mirrors the emission loop's structural filters: accessors never take an overload name,
        // constructors cannot be renamed (a projected-key collision skips them instead), and a
        // primary-signature duplicate never reaches the projected-collision block at all, so it must
        // not pad a family with a member that does not emit. The loop ALSO applies the member
        // validation pipeline; that is not reproduced here (the validator has no access to it), so a
        // family may include a member the loop later skips. The only effect is that a survivor keeps a
        // discriminated name it could have gone without — deterministic, and never a wrong name.
        var primarySeen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<(MethodDecl Method, string ProjectedKey)>();
        foreach (var m in parent.Methods)
        {
            if (m.IsConstructor || m.IsAccessor || m.IsSubscriptAccessor)
                continue;
            if (!primarySeen.Add(BaseHandler.GetMethodSignatureKey(m, typeDatabase, null)))
                continue;
            candidates.Add((m, BaseHandler.GetProjectedCSharpMethodKey(m, typeDatabase, null, siblingPropertyNames: null)));
        }

        return Resolve(
            candidates,
            (decl, nameOverride) => BaseHandler.GetProjectedCSharpMethodKey(decl, typeDatabase, null, siblingPropertyNames: null, nameOverride: nameOverride),
            decl => UnrenamedNaturalName(decl));
    }

    /// <summary>
    /// Resolves one scope's collision families. <paramref name="emittingMethods"/> is the caller's
    /// emitting partition, tagged with the projected key it dedups on, IN the order the caller's
    /// emission walk visits them; only the refusal tie-break consults that order (which of two
    /// C#-indistinguishable members survives), never a name.
    /// </summary>
    /// <param name="projectWithNameOverride">Rebuilds a member's projected key under a candidate base
    /// name — the SAME key builder the caller dedups with, so a candidate name is tested against the
    /// key it will actually occupy.</param>
    /// <param name="unrenamedNaturalName">The member's shaped C# name with every collision-avoidance
    /// rename (property, CS0542 parent-name, CS0102 generic-parameter) suppressed. Bare-name ownership
    /// keys off this: a member that only reaches the contested spelling BECAUSE it was renamed out of
    /// its own natural one has no claim on it.</param>
    public static Dictionary<MethodDecl, OverloadNameAssignment> Resolve(
        IReadOnlyList<(MethodDecl Method, string ProjectedKey)> emittingMethods,
        Func<MethodDecl, string, string> projectWithNameOverride,
        Func<MethodDecl, string> unrenamedNaturalName)
    {
        var result = new Dictionary<MethodDecl, OverloadNameAssignment>(ReferenceEqualityComparer.Instance);

        var families = new Dictionary<string, List<MethodDecl>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (method, projectedKey) in emittingMethods)
        {
            if (!families.TryGetValue(projectedKey, out var list))
            {
                families[projectedKey] = list = new List<MethodDecl>();
                order.Add(projectedKey);
            }
            list.Add(method);
        }

        // Scope-wide occupancy: every uncontested member holds its natural key, so a discriminated
        // name can never silently land on one. Without this the ladder would happily rename
        // `configure(mode:)` to `ConfigureMode` on top of a real `configureMode(_:)` sibling.
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in order)
        {
            if (families[key].Count == 1)
                reserved.Add(key);
        }

        foreach (var key in order)
        {
            var family = families[key];
            if (family.Count < 2)
                continue;

            var contestedName = NameComponentOf(key);

            // Bare-name ownership. A member owns the contested name only if it is label-less (nothing
            // to discriminate it with) AND its own un-renamed natural name IS that spelling — the
            // second half is what stops a member that merely got renamed ONTO the contested name
            // (`conflict(_:)` → `ConflictMethod`, because a `conflict` property took `Conflict`) from
            // out-claiming the sibling literally named `conflictMethod`.
            MethodDecl? owner = null;
            int ownerCandidates = 0;
            foreach (var m in family)
            {
                if (!string.Equals(BuildLabelDerivedNameInput(m), m.Name, StringComparison.Ordinal))
                    continue;
                if (!string.Equals(unrenamedNaturalName(m), contestedName, StringComparison.Ordinal))
                    continue;
                ownerCandidates++;
                owner ??= m;
            }
            if (ownerCandidates != 1)
                owner = null;

            var discriminands = new List<MethodDecl>(family.Count);
            foreach (var m in family)
            {
                if (ReferenceEquals(m, owner))
                {
                    result[m] = OverloadNameAssignment.Natural;
                    reserved.Add(key);
                }
                else
                {
                    discriminands.Add(m);
                }
            }
            if (discriminands.Count == 0)
                continue;

            // Rung selection is per-FAMILY, not per-member: the first rung at which every discriminand
            // gets a distinct, unreserved key wins for all of them. Assigning member-by-member would
            // hand the label rung to whichever sibling the walk reached first and push the other down
            // a rung — declaration order leaking back into the names.
            var labelInputs = discriminands.Select(BuildLabelDerivedNameInput).ToList();
            var typeInputs = discriminands
                .Select((m, i) => BuildTypeDerivedNameInput(m, labelInputs[i]))
                .ToList();

            var chosen = RungFits(discriminands, labelInputs, projectWithNameOverride, reserved) ? labelInputs
                : RungFits(discriminands, typeInputs, projectWithNameOverride, reserved) ? typeInputs
                : null;

            if (chosen != null)
            {
                bool isLabelRung = ReferenceEquals(chosen, labelInputs);
                for (int i = 0; i < discriminands.Count; i++)
                {
                    var m = discriminands[i];
                    var nameInput = chosen[i];
                    reserved.Add(projectWithNameOverride(m, nameInput));
                    // A label-less member in an unowned family keeps its own name as the "input" — the
                    // ladder found nothing to add. Report it as Natural so the emitter takes the plain
                    // shaping path and no consumer-visible rename is recorded.
                    if (string.Equals(nameInput, m.Name, StringComparison.Ordinal))
                    {
                        result[m] = OverloadNameAssignment.Natural;
                    }
                    else
                    {
                        var outcome = isLabelRung ? OverloadNameOutcome.LabelDerived : OverloadNameOutcome.TypeDerived;
                        result[m] = new OverloadNameAssignment(outcome, nameInput, null);
                        RecordAssignment(m, nameInput, outcome);
                    }
                }
                continue;
            }

            // No rung separates the family. Whatever distinguishes these members in Swift — a return
            // type, a type that erases to the same projection — is invisible at a C# call site, so a
            // second member under an invented name would be unusable rather than merely ugly. Keep the
            // first that fits and refuse the rest, naming both signatures in the report.
            MethodDecl? survivor = null;
            for (int i = 0; i < discriminands.Count; i++)
            {
                var m = discriminands[i];
                var nameInput = typeInputs[i];
                var candidateKey = projectWithNameOverride(m, nameInput);
                if (reserved.Add(candidateKey))
                {
                    survivor ??= m;
                    if (string.Equals(nameInput, m.Name, StringComparison.Ordinal))
                    {
                        result[m] = OverloadNameAssignment.Natural;
                    }
                    else
                    {
                        result[m] = new OverloadNameAssignment(OverloadNameOutcome.TypeDerived, nameInput, null);
                        RecordAssignment(m, nameInput, OverloadNameOutcome.TypeDerived);
                    }
                }
                else
                {
                    var against = survivor ?? owner ?? family[0];
                    result[m] = new OverloadNameAssignment(
                        OverloadNameOutcome.Refused,
                        null,
                        $"Projects to the same C# signature as '{DescribeSwiftSignature(against)}' " +
                        $"and no argument label or parameter type distinguishes them: '{DescribeSwiftSignature(m)}'. " +
                        "Emitting it under a numeric suffix would give consumers two identically-callable members.");
                }
            }
        }

        return result;
    }

    private static bool RungFits(
        IReadOnlyList<MethodDecl> discriminands,
        IReadOnlyList<string> nameInputs,
        Func<MethodDecl, string, string> projectWithNameOverride,
        IReadOnlySet<string> reserved)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < discriminands.Count; i++)
        {
            var key = projectWithNameOverride(discriminands[i], nameInputs[i]);
            if (reserved.Contains(key) || !seen.Add(key))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Walks the same ladder for a single member whose key is already taken at emission time — the
    /// residual the property-agnostic map cannot see (a property-collision rename landing a member on a
    /// sibling's name) and the adopted-override case (a natural sibling projecting onto the ancestor
    /// slot name an override reserved before the loop). Returns the accepted name input, or null when
    /// nothing in the ladder is free, in which case the caller refuses the member.
    /// </summary>
    public static string? Escalate(
        MethodDecl method,
        Func<string, string> projectWithNameOverride,
        Func<string, bool> tryReserve)
    {
        var labelInput = BuildLabelDerivedNameInput(method);
        if (!string.Equals(labelInput, method.Name, StringComparison.Ordinal) &&
            tryReserve(projectWithNameOverride(labelInput)))
        {
            RecordAssignment(method, labelInput, OverloadNameOutcome.LabelDerived);
            return labelInput;
        }

        var typeInput = BuildTypeDerivedNameInput(method, labelInput);
        if (!string.Equals(typeInput, method.Name, StringComparison.Ordinal) &&
            tryReserve(projectWithNameOverride(typeInput)))
        {
            RecordAssignment(method, typeInput, OverloadNameOutcome.TypeDerived);
            return typeInput;
        }

        return null;
    }

    /// <summary>
    /// ObjC-selector-style base name: the method's bare name followed by the capitalized external label
    /// of each non-empty, non-underscore, non-synthesized argument. <c>conversationManager(_:didActivate:)</c>
    /// yields <c>conversationManagerDidActivate</c>, which the downstream shaping pass PascalCases.
    ///
    /// Argument labels are Swift identifiers and flow into the C# name unsanitized — the same assumption
    /// the generator already makes for <c>method.Name</c>. A label that is not a legal C# identifier
    /// fails closed at the compile gate; sanitizing only this path would be an inconsistent half-fix of a
    /// codebase-wide identifier concern.
    /// </summary>
    public static string BuildLabelDerivedNameInput(MethodDecl method)
    {
        var sb = new StringBuilder(method.Name);
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var arg = method.CSSignature[i];
            if (arg.SwiftTypeSpec == null || arg.SwiftTypeSpec.IsEmptyTuple)
                continue;
            var label = arg.GetSwiftName();
            if (string.IsNullOrEmpty(label) || label == "_" || SwiftBuilder.IsAutoGeneratedArgName(label))
                continue;
            AppendCapitalized(sb, label);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Appends <c>With{Type}[And{Type}…]</c> built from the SWIFT parameter types. Used when labels do
    /// not separate a family — identical labels over different Swift types, or an all-positional family
    /// such as <c>transform(_: RefBox)</c> / <c>transform(_: RefBox?)</c>, whose C# nullability is erased
    /// on the projected key but whose Swift specs still differ.
    /// </summary>
    public static string BuildTypeDerivedNameInput(MethodDecl method, string baseInput)
    {
        var sb = new StringBuilder(baseInput);
        bool first = true;
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var arg = method.CSSignature[i];
            if (arg.SwiftTypeSpec == null || arg.SwiftTypeSpec.IsEmptyTuple)
                continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            sb.Append(first ? "With" : "And");
            sb.Append(BuildSwiftTypeToken(arg.SwiftTypeSpec));
            first = false;
        }
        // A zero-parameter family cannot be type-separated either; hand back the base so the caller's
        // distinctness check fails it and the refusal arm takes over.
        return sb.ToString();
    }

    /// <summary>
    /// Renders a Swift type spec as a PascalCase identifier fragment. Module qualification and generic
    /// punctuation are dropped, <c>Optional&lt;T&gt;</c> reads as <c>OptionalT</c>, and anything the
    /// structured cases miss falls back to the sanitized printed form so the result is always a usable
    /// identifier fragment.
    /// </summary>
    public static string BuildSwiftTypeToken(TypeSpec spec)
    {
        switch (spec)
        {
            case NamedTypeSpec named:
            {
                var sb = new StringBuilder();
                AppendCapitalized(sb, named.NameWithoutModule);
                foreach (var g in named.GenericParameters)
                    sb.Append(BuildSwiftTypeToken(g));
                // `Foo!` and `Foo?` are the same C# projection but distinct Swift declarations; the
                // sugar flag lives beside the spec rather than in it, so fold it in explicitly.
                if (named.IsImplicitlyUnwrappedOptional)
                    sb.Append("Unwrapped");
                return Sanitize(sb.ToString());
            }
            case TupleTypeSpec tuple:
            {
                var sb = new StringBuilder("Tuple");
                foreach (var e in tuple.Elements)
                    sb.Append(BuildSwiftTypeToken(e));
                return Sanitize(sb.ToString());
            }
            case ClosureTypeSpec:
                return "Closure";
            case ProtocolListTypeSpec protoList:
            {
                var sb = new StringBuilder();
                // SortedList enumerates in key order, so composition member order in the Swift source
                // cannot shuffle the token.
                foreach (var p in protoList.Protocols.Keys)
                    AppendCapitalized(sb, p.NameWithoutModule);
                return Sanitize(sb.Length == 0 ? "Any" : sb.ToString());
            }
            default:
                return Sanitize(spec?.ToString() ?? "Unknown");
        }
    }

    /// <summary>
    /// The member's shaped C# name with every collision-avoidance rename suppressed — no sibling
    /// property set, no parent type name (CS0542), no parent generic parameter names (CS0102). This is
    /// the name the member would carry if it were the only thing in the type, which is exactly the claim
    /// bare-name ownership tests.
    /// </summary>
    private static string UnrenamedNaturalName(MethodDecl decl) => ShapeNameInput(decl, decl.Name);

    /// <summary>
    /// Shapes a base-name input the way the emitter will, with the collision-avoidance renames
    /// suppressed. Both halves of a report record go through here so the record's natural and emitted
    /// names differ ONLY by what the ladder did — the property/CS0542 renames shift a family uniformly
    /// and would otherwise show up as spurious differences in the record.
    /// </summary>
    private static string ShapeNameInput(MethodDecl decl, string nameInput)
    {
        var ctx = PublicMethodNameContext.ForMethod(decl, siblingPropertyNames: null) with
        {
            ParentTypeName = null,
            ParentGenericParameterNames = null,
            MethodName = nameInput,
        };
        return NameProvider.GetPublicMethodName(ctx);
    }

    /// <summary>
    /// Publishes one ladder decision to the binding report. The ship gate reads these rather than the
    /// emitted identifiers: only a record carrying BOTH names can tell a resolver-assigned
    /// <c>Configure2</c> from an author's own <c>Vector3</c>.
    /// </summary>
    /// <summary>
    /// Ledger entry for the protocol lane, which resolves its own families through
    /// <see cref="ProtocolMethodDisambiguator"/> but must land in the same records — the ship gate's
    /// claim is about the whole public surface, not one lane's share of it.
    /// </summary>
    public static void RecordProtocolAssignment(MethodDecl method, OverloadNameOutcome outcome, string nameInput)
        => RecordAssignment(method, nameInput, outcome);

    private static void RecordAssignment(MethodDecl method, string nameInput, OverloadNameOutcome outcome)
        => ReportCollector.RecordOverloadRenamed(
            method.ParentDecl?.Name ?? "<module>",
            DescribeSwiftSignature(method),
            UnrenamedNaturalName(method),
            ShapeNameInput(method, nameInput),
            outcome.ToString());

    /// <summary>Swift-source-shaped rendering of a member, for a refusal report a consumer can act on.</summary>
    private static string DescribeSwiftSignature(MethodDecl method)
    {
        var sb = new StringBuilder(method.Name);
        sb.Append('(');
        bool first = true;
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var arg = method.CSSignature[i];
            if (arg.SwiftTypeSpec == null || arg.SwiftTypeSpec.IsEmptyTuple)
                continue;
            if (!first) sb.Append(", ");
            var label = arg.GetSwiftName();
            sb.Append(string.IsNullOrEmpty(label) ? "_" : label);
            sb.Append(": ").Append(arg.SwiftTypeSpec.ToString());
            first = false;
        }
        sb.Append(')');
        var ret = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        if (ret != null && !ret.IsEmptyTuple)
            sb.Append(" -> ").Append(ret.ToString());
        return sb.ToString();
    }

    private static string NameComponentOf(string projectedKey)
    {
        int paren = projectedKey.IndexOf('(');
        return paren < 0 ? projectedKey : projectedKey[..paren];
    }

    private static void AppendCapitalized(StringBuilder sb, string value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        sb.Append(char.ToUpperInvariant(value[0]));
        if (value.Length > 1)
            sb.Append(value, 1, value.Length - 1);
    }

    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(c);
        }
        return sb.Length == 0 ? "Value" : sb.ToString();
    }
}
