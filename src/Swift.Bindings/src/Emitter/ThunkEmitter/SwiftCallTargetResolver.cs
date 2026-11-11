// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Resolves the callable Swift symbol for a method declaration.
/// Shared between PInvokeEmitter (for P/Invoke entry points) and ThunkAssemblyEmitter
/// (for ARM64 thunk branch targets). Centralizes the Tj dispatch thunk suffix logic
/// to avoid symbol mismatches between what P/Invoke expects and what the thunk calls.
/// </summary>
public static class SwiftCallTargetResolver
{
    /// <summary>
    /// Resolves the full callable Swift symbol for a method, including the Tj suffix
    /// for overridable class members (instance or <c>class</c>) that use vtable dispatch.
    /// </summary>
    /// <param name="methodDecl">The method declaration containing the mangled name.</param>
    /// <param name="parentDecl">The parent type declaration (ClassDecl, StructDecl, etc.), or null for free functions.</param>
    /// <returns>The fully resolved symbol name (without leading underscore — caller adds platform prefix).</returns>
    public static string Resolve(MethodDecl methodDecl, BaseDecl? parentDecl)
    {
        return Resolve(methodDecl.MangledName, methodDecl, parentDecl);
    }

    /// <summary>
    /// Resolves the full callable Swift symbol using an explicit mangled name, including
    /// the Tj suffix for overridable class members (instance or <c>class</c>) that use vtable dispatch.
    /// Use this overload to resolve against an explicit silgen mangled name — e.g. when the env's
    /// emission symbol has been promoted to a thunk symbol and the caller holds the original silgen
    /// name separately (the decl's MangledName is never overwritten).
    /// </summary>
    /// <param name="mangledName">The original Swift mangled name to resolve.</param>
    /// <param name="methodDecl">The method declaration (used for dispatch classification, not for MangledName).</param>
    /// <param name="parentDecl">The parent type declaration (ClassDecl, StructDecl, etc.), or null for free functions.</param>
    /// <returns>The fully resolved symbol name (without leading underscore — caller adds platform prefix).</returns>
    public static string Resolve(string mangledName, MethodDecl methodDecl, BaseDecl? parentDecl)
    {
        // With library evolution, an overridable class member is dispatched through its vtable
        // entry. The implementation is a local (non-exported) symbol in the dylib; only the
        // dispatch thunk (Tj suffix) is globally exported. The thunk reads the instance, or the
        // class metatype for a `class` member, from the self register and calls through that
        // class's vtable, so the caller always fills self.
        //
        // Gates (all must be true for Tj):
        // - Parent is a non-final class
        // - Member itself is not final. A `static` member of a class is a final `class` member,
        //   and the ABI marks it so; a `class` member is overridable and has a vtable entry.
        // - Not a constructor (allocating initializers bind their own exported symbol)
        // - Not an extension member (static dispatch, no vtable entry)
        if (parentDecl is not ClassDecl classParent
            || classParent.IsFinal
            || methodDecl.IsFinal
            || methodDecl.IsConstructor
            || methodDecl.IsExtensionMethod)
        {
            return mangledName;
        }

        var exported = methodDecl.ModuleDecl?.ExportedSymbols;
        var ownThunk = mangledName + ManglingProbes.DispatchThunkSuffix;

        // The library's export list, when known, outranks the declaration's attributes: a member the
        // ABI did not mark final but whose implementation is exported and whose thunk is not is called
        // directly.
        if (exported != null && !exported.Contains(ownThunk) && exported.Contains(mangledName))
            return mangledName;

        // An override whose ABI matches the member it overrides reuses that member's vtable entry, so
        // Swift emits no dispatch thunk for it. Its entry is the thunk of the member that owns the
        // entry, called with the derived instance or metatype in self, which the vtable then routes
        // to the override.
        if (mangledName == methodDecl.MangledName
            && !(exported?.Contains(ownThunk) ?? false)
            && FindVTableEntryIntroducer(methodDecl, classParent, exported) is { } introducer)
        {
            var inheritedThunk = introducer.MangledName + ManglingProbes.DispatchThunkSuffix;
            if (exported == null || exported.Contains(inheritedThunk))
                return inheritedThunk;
        }

        return ownThunk;
    }

    /// <summary>
    /// For an override declared in <paramref name="parent"/> that has no vtable entry of its own, finds
    /// the same-module ancestor member whose entry it reuses. The walk follows the override chain up the
    /// superclass chain and stops at the first member that owns an entry: one whose own dispatch thunk
    /// is exported, or one that overrides nothing. Returns null for a non-override, an accessor (its
    /// overridden property is not modelled per accessor), when no ancestor in this module declares the
    /// member, or when the ancestors' same-named members leave the overridden one uncertain.
    /// </summary>
    /// <param name="exported">The library's exported symbols, or null when unknown. Without them only an
    /// ancestor member with the very same signature is accepted, and no member is known to own an entry
    /// until the chain reaches one that overrides nothing.</param>
    internal static MethodDecl? FindVTableEntryIntroducer(MethodDecl method, ClassDecl parent, IReadOnlySet<string>? exported)
    {
        if (!method.IsOverride || method.IsAccessor || method.IsConstructor)
            return null;

        MethodDecl current = method;
        ClassDecl currentParent = parent;
        MethodDecl? introducer = null;
        while (true)
        {
            var outcome = FindOverriddenMember(currentParent, current, allowOptionalVariance: exported != null,
                out var match, out var matchParent);
            if (outcome == OverriddenMemberLookup.Uncertain)
                return null;
            if (outcome == OverriddenMemberLookup.NotDeclared)
                return introducer;
            introducer = match!;
            if (!match!.IsOverride
                || (exported != null && exported.Contains(match.MangledName + ManglingProbes.DispatchThunkSuffix)))
                return introducer;
            current = match;
            currentParent = matchParent!;
        }
    }

    private enum OverriddenMemberLookup { NotDeclared, Found, Uncertain }

    /// <summary>
    /// Picks the member that <paramref name="method"/>, declared in <paramref name="parent"/>, overrides,
    /// the way Swift's override matching does: every member the superclass chain declares under the same
    /// selector is a candidate, whichever ancestor declares it, and an exact match outranks one that
    /// only matches through variance. Name, kind, argument labels and arity narrow the candidates, and
    /// the signature decides among them: an overload set can hold several, such as
    /// <c>transform(_: Box)</c> beside <c>transform(_: Box?)</c>, spread over several ancestors.
    /// <list type="bullet">
    /// <item>A candidate with an identical signature (types, <c>inout</c>, ownership, generic requirements)
    /// wins; the nearest one,
    /// since a nearer member identical to a farther one must itself override it.</item>
    /// <item>Failing that, when <paramref name="allowOptionalVariance"/> (the export check has then already
    /// established that the override shares an entry rather than owning one), a single candidate that
    /// differs only by the Optional variance Swift allows an override (a parameter widened to its
    /// Optional, a result narrowed from one) wins, provided no other candidate exists.</item>
    /// <item>Any other outcome is <see cref="OverriddenMemberLookup.Uncertain"/>: a candidate that matches
    /// neither way may still be the overridden one through a variance not modelled here (a covariant
    /// class result), and the caller keeps the override's own thunk.</item>
    /// </list>
    /// </summary>
    private static OverriddenMemberLookup FindOverriddenMember(
        ClassDecl parent, MethodDecl method, bool allowOptionalVariance, out MethodDecl? match, out ClassDecl? matchParent)
    {
        match = null;
        matchParent = null;
        var candidates = new List<(MethodDecl Member, ClassDecl Declarer)>();
        for (var ancestor = parent.ResolvedSuperclass; ancestor != null; ancestor = ancestor.ResolvedSuperclass)
        {
            foreach (var candidate in ancestor.Methods)
            {
                if (SharesSelector(candidate, method))
                    candidates.Add((candidate, ancestor));
            }
        }
        if (candidates.Count == 0)
            return OverriddenMemberLookup.NotDeclared;

        var exact = candidates.FindIndex(candidate => HasIdenticalSignature(candidate.Member, method));
        if (exact >= 0)
        {
            (match, matchParent) = candidates[exact];
            return OverriddenMemberLookup.Found;
        }

        if (allowOptionalVariance && candidates.Count == 1 && DiffersByOptionalVariance(candidates[0].Member, method))
        {
            (match, matchParent) = candidates[0];
            return OverriddenMemberLookup.Found;
        }
        return OverriddenMemberLookup.Uncertain;
    }

    private static bool SharesSelector(MethodDecl candidate, MethodDecl method)
    {
        if (candidate.IsAccessor || candidate.IsConstructor || candidate.IsExtensionMethod)
            return false;
        if (candidate.Name != method.Name || candidate.MethodType != method.MethodType || candidate.IsAsync != method.IsAsync)
            return false;
        if (candidate.CSSignature.Count != method.CSSignature.Count
            || candidate.GenericParameters.Count != method.GenericParameters.Count)
            return false;
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            if (candidate.CSSignature[i].GetSwiftName() != method.CSSignature[i].GetSwiftName())
                return false;
        }
        return true;
    }

    private static bool HasIdenticalSignature(MethodDecl candidate, MethodDecl method)
    {
        if (!HasSameGenericSignature(candidate, method))
            return false;
        for (int i = 0; i < method.CSSignature.Count; i++)
        {
            if (!HasSameConvention(candidate.CSSignature[i], method.CSSignature[i])
                || candidate.CSSignature[i].SwiftTypeSpec.ToString() != method.CSSignature[i].SwiftTypeSpec.ToString())
                return false;
        }
        return true;
    }

    private static bool HasSameConvention(ArgumentDecl candidate, ArgumentDecl argument) =>
        candidate.IsInOut == argument.IsInOut && candidate.Ownership == argument.Ownership;

    /// <summary>
    /// The full generic signature, requirements included: <c>foo&lt;T&gt;</c> and <c>foo&lt;T: P&gt;</c>
    /// have the same parameter types but different entries, and only the constrained one takes a
    /// witness table.
    /// </summary>
    private static bool HasSameGenericSignature(MethodDecl candidate, MethodDecl method) =>
        string.Equals(candidate.RawGenericSig ?? string.Empty, method.RawGenericSig ?? string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// True when every position of <paramref name="method"/> either matches <paramref name="candidate"/>
    /// or is the variance Swift allows an override: a parameter that is the Optional of the overridden
    /// one, a result whose Optional is the overridden one. The generic signature and the conventions
    /// (<c>inout</c>, ownership) must match exactly.
    /// </summary>
    private static bool DiffersByOptionalVariance(MethodDecl candidate, MethodDecl method)
    {
        if (!HasSameGenericSignature(candidate, method))
            return false;
        var overriddenResult = candidate.CSSignature[0].SwiftTypeSpec;
        var overrideResult = method.CSSignature[0].SwiftTypeSpec;
        if (overriddenResult.ToString() != overrideResult.ToString() && !IsOptionalOf(overriddenResult, overrideResult))
            return false;
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            if (!HasSameConvention(candidate.CSSignature[i], method.CSSignature[i]))
                return false;
            var overriddenParameter = candidate.CSSignature[i].SwiftTypeSpec;
            var overrideParameter = method.CSSignature[i].SwiftTypeSpec;
            if (overriddenParameter.ToString() != overrideParameter.ToString() && !IsOptionalOf(overrideParameter, overriddenParameter))
                return false;
        }
        return true;
    }

    private static bool IsOptionalOf(TypeSpec optional, TypeSpec wrapped) =>
        optional is NamedTypeSpec { Name: "Swift.Optional", GenericParameters.Count: 1 } named
        && named.GenericParameters[0].ToString() == wrapped.ToString();

    /// <summary>
    /// Returns the symbol with the Apple linker underscore prefix for use in assembly code.
    /// </summary>
    /// <param name="methodDecl">The method declaration.</param>
    /// <param name="parentDecl">The parent type declaration, or null for free functions.</param>
    /// <returns>The symbol prefixed with underscore (e.g., "_$s6Module6methodyyF").</returns>
    public static string ResolveWithPrefix(MethodDecl methodDecl, BaseDecl? parentDecl)
    {
        return "_" + Resolve(methodDecl, parentDecl);
    }

    /// <summary>
    /// Returns the symbol with the Apple linker underscore prefix, using an explicit mangled name.
    /// Use this overload to resolve against an explicit silgen mangled name — e.g. when the env's
    /// emission symbol has been promoted to a thunk symbol and the caller holds the original silgen
    /// name separately (the decl's MangledName is never overwritten).
    /// </summary>
    /// <param name="mangledName">The original Swift mangled name.</param>
    /// <param name="methodDecl">The method declaration (used for dispatch classification).</param>
    /// <param name="parentDecl">The parent type declaration, or null for free functions.</param>
    /// <returns>The symbol prefixed with underscore.</returns>
    public static string ResolveWithPrefix(string mangledName, MethodDecl methodDecl, BaseDecl? parentDecl)
    {
        return "_" + Resolve(mangledName, methodDecl, parentDecl);
    }
}
