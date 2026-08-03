// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Set-validity check over the generator's OWN synthesized overload set: two emitted overloads can
/// each be individually valid yet be mutually ambiguous under C# overload resolution, so a consumer
/// call site that reaches both fails with CS0121. The exact-projected-signature dedup
/// (<see cref="MethodEnvironment.EmittedProjectedSignatures"/>) is structurally blind to this — it
/// only catches two members that project to the SAME signature, not two DIFFERENT signatures that a
/// single argument list can satisfy equally well.
///
/// This is not a member-shape prediction gate: it never inspects a Swift input shape to guess whether
/// a binding would work. It compares synthesized candidates against the family's already-reserved
/// signatures and declines to write a candidate whose presence would make the set unusable.
///
/// <para><b>The applicability rule.</b> For two same-named candidates with projected parameter type
/// lists of lengths <c>n</c> and <c>m</c>, required (non-optional) counts <c>r</c> and <c>s</c>, and
/// <c>P</c> = the length of the longest prefix on which the two type lists agree, an argument list of
/// <c>k</c> positional arguments is applicable to both iff <c>max(r,s) &lt;= k &lt;= P</c>. C# breaks
/// such a tie in favor of a candidate whose parameters ALL have corresponding arguments (ECMA-334
/// §12.6.4.3), so a <c>k</c> equal to <c>n</c> or <c>m</c> resolves cleanly. What remains ambiguous is
/// <c>k &lt;= min(n,m) - 1</c>. Combining: the pair is CS0121-ambiguous iff
/// <c>max(r,s) &lt;= min(P, min(n,m) - 1)</c>. Equal arity does not exempt a pair — two same-length
/// lists that diverge at position <c>P</c> are still tied for every call shorter than that — but two
/// lists that agree at EVERY position are one signature written twice, which is the exact-key dedup's
/// job and is excluded here so a reservation cannot be found ambiguous with itself.
/// </para>
///
/// <para>A corollary worth stating because it bounds the blast radius: the tie-break means BOTH sides
/// must carry at least one C# optional parameter for the rule to fire. A candidate with no optional
/// parameters has <c>r == n</c>, and <c>n &gt; min(n,m) - 1</c> whenever <c>n &lt;= m</c>, so it can
/// never be the ambiguous partner of a shorter or equal-length candidate.
/// </para>
///
/// <para>The rule is deliberately conservative on the prefix: it requires the two type lists to agree
/// ORDINALLY on the shared prefix. Two candidates whose prefixes differ but admit a common argument
/// through implicit conversions can also be ambiguous; that shape is not detected here, because
/// deciding it needs a real conversion lattice over projected type NAMES (the key carries strings, not
/// resolved types) and a false suppression permanently removes public API surface. Under-detection
/// leaves today's behavior; over-detection deletes a working member.
/// </para>
/// </summary>
internal static class OverloadAmbiguityGuard
{
    /// <summary>
    /// The C#-overload-resolution-relevant shape of one emitted member: the projected signature key
    /// split into its name, its parameter type list, and how many of those parameters a caller MUST
    /// supply (i.e. arity minus the trailing run of C# optional parameters).
    /// </summary>
    internal readonly record struct OverloadShape(
        string Name,
        IReadOnlyList<string> ParameterTypes,
        int RequiredCount);

    /// <summary>
    /// Splits a projected signature key (<c>"Name(T1,T2)"</c>, optionally suffixed with a
    /// <c>`N</c> generic arity and/or prefixed with a <c>failable-factory:</c> namespace) into a
    /// shape. The name component keeps the arity suffix and the namespace prefix so two candidates
    /// only ever compare when they would emit under the same C# name AND the same method-generic
    /// arity — a generic-arity mismatch is not an overload tie in C#.
    /// </summary>
    internal static OverloadShape ParseKey(string projectedKey, int requiredCount)
    {
        int open = projectedKey.IndexOf('(');
        int close = projectedKey.LastIndexOf(')');
        if (open <= 0 || close < open)
            return new OverloadShape(projectedKey, Array.Empty<string>(), requiredCount);

        var name = projectedKey.Substring(0, open) + projectedKey.Substring(close + 1);
        var inner = projectedKey.Substring(open + 1, close - open - 1);
        var types = SplitTopLevel(inner);
        // Clamp: a caller that mis-reports the optional tail must never widen the ambiguous set.
        var required = Math.Clamp(requiredCount, 0, types.Count);
        return new OverloadShape(name, types, required);
    }

    /// <summary>
    /// Splits a comma-separated projected type list at TOP-LEVEL commas only — generic arguments
    /// (<c>IReadOnlyDictionary&lt;string,int&gt;</c>), tuples and array ranks all carry interior
    /// commas that are part of a single parameter type.
    /// </summary>
    private static List<string> SplitTopLevel(string inner)
    {
        var parts = new List<string>();
        if (string.IsNullOrEmpty(inner))
            return parts;

        int depth = 0;
        int start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c is '<' or '(' or '[')
                depth++;
            else if (c is '>' or ')' or ']')
                depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(inner.Substring(start, i - start));
                start = i + 1;
            }
        }
        parts.Add(inner.Substring(start));
        return parts;
    }

    /// <summary>
    /// True when a consumer call site exists that binds neither candidate better than the other.
    /// See the type doc for the derivation.
    /// </summary>
    internal static bool AreAmbiguous(in OverloadShape a, in OverloadShape b)
    {
        if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal))
            return false;

        int n = a.ParameterTypes.Count;
        int m = b.ParameterTypes.Count;
        int shared = Math.Min(n, m);
        int prefix = 0;
        while (prefix < shared &&
               string.Equals(a.ParameterTypes[prefix], b.ParameterTypes[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        // Same arity AND the same types throughout is one signature written twice — the exact-key
        // dedup owns that, and reporting it here would let a member collide with its own reservation.
        // Same arity with a type that DIFFERS somewhere is still a real tie whenever a short-enough
        // argument list stops before the difference, so it is NOT excluded.
        if (n == m && prefix == n)
            return false;

        int ceiling = Math.Min(prefix, shared - 1);
        return Math.Max(a.RequiredCount, b.RequiredCount) <= ceiling;
    }

    /// <summary>
    /// Records the resolution-relevant shape of a projected signature that was just reserved into
    /// <see cref="MethodEnvironment.EmittedProjectedSignatures"/>. A reservation with no recorded
    /// shape is read back as fully-required (no optional parameters), which can only ever make the
    /// check MORE permissive — an unrecorded producer degrades to today's behavior rather than to a
    /// spurious suppression.
    /// </summary>
    internal static void RecordReservation(
        IDictionary<string, int>? reservedShapes, string projectedKey, int requiredCount)
    {
        if (reservedShapes == null || string.IsNullOrEmpty(projectedKey))
            return;
        reservedShapes[projectedKey] = requiredCount;
    }

    /// <summary>
    /// Returns the already-reserved projected key that <paramref name="candidateKey"/> would be
    /// CS0121-ambiguous with, or null when the candidate is safe to write.
    /// </summary>
    internal static string? FindAmbiguousReservation(
        IReadOnlySet<string>? reservedKeys,
        IReadOnlyDictionary<string, int>? reservedShapes,
        string candidateKey,
        int candidateRequiredCount)
    {
        if (reservedKeys == null || reservedKeys.Count == 0 || string.IsNullOrEmpty(candidateKey))
            return null;

        var candidate = ParseKey(candidateKey, candidateRequiredCount);
        // A candidate with no optional parameters cannot be the ambiguous partner of anything
        // (see the corollary in the type doc) — skip the scan entirely.
        if (candidate.RequiredCount >= candidate.ParameterTypes.Count)
            return null;

        foreach (var reservedKey in reservedKeys)
        {
            int reservedRequired;
            if (reservedShapes == null || !reservedShapes.TryGetValue(reservedKey, out reservedRequired))
                reservedRequired = int.MaxValue; // unrecorded ⇒ treated as fully required

            var reserved = ParseKey(reservedKey, reservedRequired);
            if (AreAmbiguous(candidate, reserved))
                return reservedKey;
        }

        return null;
    }

    /// <summary>
    /// How many trailing C# parameters of <paramref name="decl"/> will be emitted as OPTIONAL.
    ///
    /// Mirrors the emitted signature exactly, and must move with it: the signature builder applies
    /// mapped Swift defaults only within the maximal trailing suffix where every defaulted parameter
    /// also has a C#-expressible constant (a defaulted parameter whose expression does not map ends
    /// the suffix), and the async path appends one more optional parameter — the trailing
    /// <c>CancellationToken cancellationToken = default</c> — after all of them.
    /// </summary>
    internal static int CountOptionalTail(MethodDecl decl, ITypeDatabase typeDatabase)
    {
        int optional = decl.IsAsync ? 1 : 0;

        // Accessors never carry user-facing parameter defaults.
        if (decl.IsAccessor)
            return optional;

        var args = decl.CSSignature.Skip(1)
            .Where(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple)
            .ToList();

        var visibleGenericNames = BaseHandler.CollectVisibleGenericParamNames(decl);
        for (int i = args.Count - 1; i >= 0; i--)
        {
            if (!args[i].HasDefaultArg || args[i].SwiftDefaultExpression == null)
                break;
            var mapped = SwiftDefaultValueMapper.TryMapToCSharpDefault(
                args[i].SwiftDefaultExpression!, args[i].SwiftTypeSpec, typeDatabase, visibleGenericNames);
            if (mapped == null)
                break;
            optional++;
        }

        return optional;
    }

    /// <summary>
    /// Convenience for the common call: how many parameters a caller MUST supply for
    /// <paramref name="decl"/>'s emitted C# signature, derived from the projected key's arity minus
    /// the optional tail.
    /// </summary>
    internal static int RequiredCountFor(MethodDecl decl, ITypeDatabase typeDatabase, string projectedKey)
    {
        var shape = ParseKey(projectedKey, int.MaxValue);
        return Math.Max(0, shape.ParameterTypes.Count - CountOptionalTail(decl, typeDatabase));
    }
}
