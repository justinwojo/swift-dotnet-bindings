// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace BindingsGeneration;

/// <summary>
/// What a generated Swift call needs to keep reaching the declaration it was generated for once
/// some defaulted arguments are left out. Produced by <see cref="SwiftOverloadCallPinning.Plan"/>.
/// </summary>
internal sealed class SwiftCallPinPlan
{
    private static readonly IReadOnlyDictionary<ArgumentDecl, string> NoPins =
        new Dictionary<ArgumentDecl, string>(ReferenceEqualityComparer.Instance);

    /// <summary>A call that already reaches its declaration, or omits nothing.</summary>
    public static readonly SwiftCallPinPlan Unchanged = new(NoPins, null);

    private readonly IReadOnlyDictionary<ArgumentDecl, string> _pins;

    internal SwiftCallPinPlan(IReadOnlyDictionary<ArgumentDecl, string> pins, string? winningSibling)
    {
        _pins = pins;
        WinningSibling = winningSibling;
    }

    /// <summary>
    /// The same-named declaration Swift may bind the call to instead, when no spelled-out default
    /// can steer it back. Non-null means the call must not be emitted.
    /// </summary>
    public string? WinningSibling { get; }

    /// <summary>True when the call cannot be made to reach its declaration.</summary>
    public bool IsRefused => WinningSibling != null;

    /// <summary>How many omitted arguments the call has to spell out.</summary>
    public int PinCount => _pins.Count;

    /// <summary>
    /// The complete call argument (label included) to write in place of the omitted
    /// <paramref name="arg"/>, or null when it stays omitted.
    /// </summary>
    public string? PinFor(ArgumentDecl arg) => _pins.TryGetValue(arg, out var pin) ? pin : null;
}

/// <summary>
/// Keeps a generated Swift call that omits defaulted arguments bound to the declaration it was
/// generated for.
///
/// <para>Wrappers call the original declaration by name and leave defaulted arguments out so that
/// Swift evaluates its own defaults. A shorter call fits more declarations, though, and Swift then
/// ranks every same-named declaration that accepts it, inherited ones included: a favored one beats
/// an <c>@_disfavoredOverload</c> one, the one needing fewer defaults beats an otherwise identical
/// one, and literal typing, conversions and generic specificity weigh in too. The wrapper compiles
/// whichever wins, so a binding named for one declaration could silently run another.</para>
///
/// <para><see cref="Plan"/> is deliberately one-sided. A sibling counts as able to take the call
/// unless that is provably impossible: an argument label it does not have, or an argument no implicit
/// conversion carries to its parameter's type (between distinct closed standard-library types, into a
/// struct or enum other than its own, from a value type into a class, from an existential into a value
/// type, from a struct or enum into a protocol of its own module it does not conform to, between
/// function types of different arity, or between a function and a non-function). The target counts
/// as winning only on rules that hold whatever else the solver weighs: it is favored where the
/// sibling is disfavored; it is async where the sibling is not and neither is favored over the
/// other; or it takes every passed argument at exactly the same type and needs fewer defaults.
/// Every other sibling is a competitor. Competitors are removed by spelling out omitted
/// defaults whose text is a literal the wrapper can repeat verbatim — the smallest such set that
/// leaves none — and a spelled-out default carries the value Swift would have supplied, so the call
/// means the same thing. When no set works, the plan is refused and the caller declines the member
/// rather than risk a call to the wrong declaration.</para>
/// </summary>
internal static class SwiftOverloadCallPinning
{
    // Literals that mean the same thing in the wrapper module as in the declaring one. #file and
    // #function evaluate the same written out as omitted; #line and #column are left out because a
    // written-out one evaluates at its own token, which need not be on the call's line.
    private static readonly Regex PortableDefault = new(
        @"^(nil|true|false|\[\]|\[:\]|-?(0x[0-9A-Fa-f_]+|0b[01_]+|0o[0-7_]+|[0-9][0-9_]*(\.[0-9_]+)?([eE][+-]?[0-9]+)?)" +
        @"|""([^""\\]|\\[^(])*""|\.[A-Za-z_][A-Za-z0-9_]*|#(file|fileID|filePath|function|dsohandle))$",
        RegexOptions.CultureInvariant);

    // Above this many spellable omitted defaults only "spell none" and "spell all" are tried.
    private const int MaxExhaustivePins = 10;

    /// <summary>
    /// Plans the call <paramref name="callTarget"/>'s wrapper makes when it leaves out every
    /// defaulted parameter <paramref name="isOmitted"/> accepts (debug parameters included).
    /// <paramref name="concreteArgType"/> supplies the Swift type the wrapper actually passes for a
    /// parameter whose declared type is generic; returning null leaves it unknown.
    /// <paramref name="typeDatabase"/> says which named types are structs, enums or classes; a type
    /// it does not know is taken to be convertible.
    /// </summary>
    public static SwiftCallPinPlan Plan(
        MethodDecl callTarget,
        Func<ArgumentDecl, bool> isOmitted,
        ITypeDatabase? typeDatabase,
        Func<ArgumentDecl, string?>? concreteArgType = null)
    {
        var kinds = new NominalKinds(typeDatabase);
        var args = callTarget.CSSignature.Skip(1).Where(a => !a.SwiftTypeSpec.IsEmptyTuple).ToList();
        var omitted = new HashSet<ArgumentDecl>(
            args.Where(a => a.HasDefaultArg && isOmitted(a)),
            ReferenceEqualityComparer.Instance);
        var declaration = callTarget.SpecializedFrom ?? callTarget;
        // Debug parameters already removed from the signature are left out of every call too.
        int strippedDefaults = StrippedDebugParameterCount(callTarget, declaration);
        if (omitted.Count == 0 && strippedDefaults == 0)
            return SwiftCallPinPlan.Unchanged;

        var siblings = VisibleSiblings(declaration);
        if (siblings.Count == 0)
            return SwiftCallPinPlan.Unchanged;

        var unpinned = Competitors(declaration, siblings, args, omitted, NoPins, strippedDefaults, concreteArgType, kinds);
        if (unpinned.Count == 0)
            return SwiftCallPinPlan.Unchanged;

        // A default read from the interface is keyed by its declaration's printed name, which a
        // same-named sibling with the same labels shares; its text may be the sibling's, so it is
        // never written out.
        bool defaultsTrusted = !SharesPrintedName(declaration);
        var spellable = new List<(ArgumentDecl Arg, string Pin)>();
        foreach (var arg in args)
        {
            if (omitted.Contains(arg) && defaultsTrusted && SpellDefault(arg) is { } pin)
                spellable.Add((arg, pin));
        }

        foreach (var subset in PinSubsets(spellable.Count))
        {
            var pins = new Dictionary<ArgumentDecl, string>(ReferenceEqualityComparer.Instance);
            foreach (var index in subset)
                pins[spellable[index].Arg] = spellable[index].Pin;
            if (Competitors(declaration, siblings, args, omitted, pins, strippedDefaults, concreteArgType, kinds).Count == 0)
                return new SwiftCallPinPlan(pins, null);
        }
        return new SwiftCallPinPlan(NoPins, Describe(unpinned[0]));
    }

    /// <summary>The Swift spelling of <paramref name="method"/>, e.g. <c>create(grid:options:using:_:)</c>.</summary>
    public static string Describe(MethodDecl method)
    {
        var labels = SwiftParameters(method)
            .Where(a => !a.SwiftTypeSpec.IsEmptyTuple)
            .Select(a =>
            {
                var label = CdeclParamMapper.BuildSwiftCallArgLabel(a);
                return label.Length == 0 ? "_:" : label.TrimEnd();
            });
        return (method.IsConstructor ? "init" : method.Name) + "(" + string.Concat(labels) + ")";
    }

    private static readonly IReadOnlyDictionary<ArgumentDecl, string> NoPins =
        new Dictionary<ArgumentDecl, string>(ReferenceEqualityComparer.Instance);

    /// <summary>The parameters Swift declares, debug parameters emission removed included.</summary>
    private static IEnumerable<ArgumentDecl> SwiftParameters(MethodDecl method)
        => (method.SignatureBeforeDebugStrip ?? method.CSSignature).Skip(1);

    /// <summary>
    /// How many of the declaration's debug parameters <paramref name="callTarget"/>'s signature no
    /// longer carries: its wrapper cannot pass them, so they are always left to Swift.
    /// </summary>
    private static int StrippedDebugParameterCount(MethodDecl callTarget, MethodDecl declaration)
    {
        var full = declaration.SignatureBeforeDebugStrip ?? callTarget.SignatureBeforeDebugStrip;
        if (full == null)
            return 0;
        int declared = full.Skip(1).Count(DefaultParameterOverloadEmitter.IsDebugParameter);
        int passed = callTarget.CSSignature.Skip(1).Count(DefaultParameterOverloadEmitter.IsDebugParameter);
        return Math.Max(0, declared - passed);
    }

    private static string? SpellDefault(ArgumentDecl arg)
    {
        var expression = arg.SwiftDefaultExpression?.Trim();
        if (string.IsNullOrEmpty(expression) || !PortableDefault.IsMatch(expression))
            return null;
        return CdeclParamMapper.BuildSwiftCallArgLabel(arg) + expression;
    }

    /// <summary>
    /// The index sets of <paramref name="count"/> spellable defaults to try, smallest first, so the
    /// call spells out no more than it needs to. The empty set is skipped: the caller has already
    /// found the unpinned call at risk.
    /// </summary>
    private static IEnumerable<int[]> PinSubsets(int count)
    {
        if (count == 0)
            yield break;
        if (count > MaxExhaustivePins)
        {
            yield return Enumerable.Range(0, count).ToArray();
            yield break;
        }
        for (int size = 1; size <= count; size++)
        {
            foreach (var subset in Combinations(count, size))
                yield return subset;
        }
    }

    private static IEnumerable<int[]> Combinations(int count, int size)
    {
        var indices = Enumerable.Range(0, size).ToArray();
        while (true)
        {
            yield return (int[])indices.Clone();
            int i = size - 1;
            while (i >= 0 && indices[i] == count - size + i)
                i--;
            if (i < 0)
                yield break;
            indices[i]++;
            for (int j = i + 1; j < size; j++)
                indices[j] = indices[j - 1] + 1;
        }
    }

    /// <summary>
    /// The same-named declarations a call to <paramref name="declaration"/> could also reach: its
    /// type's own members, then those it inherits from superclasses in the module that its own
    /// members do not override.
    /// </summary>
    private static List<MethodDecl> VisibleSiblings(MethodDecl declaration)
    {
        var scopes = new List<IEnumerable<MethodDecl>>();
        if (declaration.ParentDecl is TypeDecl typeDecl)
        {
            scopes.Add(typeDecl.Methods);
            for (var ancestor = (typeDecl as ClassDecl)?.ResolvedSuperclass; ancestor != null; ancestor = ancestor.ResolvedSuperclass)
                scopes.Add(ancestor.Methods);
        }
        else if (declaration.ModuleDecl?.Methods is { } moduleMethods)
        {
            scopes.Add(moduleMethods);
        }

        var siblings = new List<MethodDecl>();
        var nearer = new List<MethodDecl> { declaration };
        for (int scope = 0; scope < scopes.Count; scope++)
        {
            var sameName = scopes[scope].Where(m =>
                    string.Equals(m.Name, declaration.Name, StringComparison.Ordinal) &&
                    m.IsConstructor == declaration.IsConstructor &&
                    m.MethodType == declaration.MethodType &&
                    !m.IsAccessor &&
                    !m.IsModuleInternal &&
                    !m.IsSpiProtected)
                .ToList();

            if (scope == 0)
            {
                // The declaration itself is normally in its own member list; a copy of it (same
                // symbol, or the same parameter list when the copy's symbol was rewritten) must not
                // count as a rival.
                bool found = sameName.Any(m => IsSameDeclaration(m, declaration));
                siblings.AddRange(sameName.Where(m =>
                    found ? !IsSameDeclaration(m, declaration) : !HasSameSignature(m, declaration)));
            }
            else
            {
                // An ancestor member is overridden only by a nearer `override` that repeats its
                // whole signature; Swift also overloads on the result type and on generic
                // constraints alone.
                siblings.AddRange(sameName.Where(m => !nearer.Any(n => n.IsOverride && HasSameSignature(n, m))));
            }
            nearer.AddRange(sameName);
        }
        return siblings;
    }

    private static bool IsSameDeclaration(MethodDecl candidate, MethodDecl declaration) =>
        ReferenceEquals(candidate, declaration) ||
        string.Equals(candidate.MangledName, declaration.MangledName, StringComparison.Ordinal);

    /// <summary>Same parameter list, result type and generic signature.</summary>
    private static bool HasSameSignature(MethodDecl a, MethodDecl b) =>
        HasSameParameterList(a, b) &&
        string.Equals(a.CSSignature[0].SwiftTypeSpec.ToString(), b.CSSignature[0].SwiftTypeSpec.ToString(), StringComparison.Ordinal) &&
        string.Equals(a.RawGenericSig, b.RawGenericSig, StringComparison.Ordinal);

    private static bool HasSameParameterList(MethodDecl a, MethodDecl b)
    {
        var left = SwiftParameters(a).Select(x => CdeclParamMapper.BuildSwiftCallArgLabel(x) + x.SwiftTypeSpec).ToList();
        var right = SwiftParameters(b).Select(x => CdeclParamMapper.BuildSwiftCallArgLabel(x) + x.SwiftTypeSpec).ToList();
        return left.SequenceEqual(right, StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether another member of the declaration's scope prints the same name — labels included —
    /// and so shares the key its interface defaults are recorded under.
    /// </summary>
    private static bool SharesPrintedName(MethodDecl declaration)
    {
        IEnumerable<MethodDecl>? members = declaration.ParentDecl switch
        {
            TypeDecl typeDecl => typeDecl.Methods,
            _ => declaration.ModuleDecl?.Methods,
        };
        if (members == null)
            return false;
        var printed = Describe(declaration);
        return members.Any(m =>
            !IsSameDeclaration(m, declaration) &&
            !m.IsAccessor &&
            string.Equals(Describe(m), printed, StringComparison.Ordinal));
    }

    /// <summary>
    /// One argument the planned call passes: a value of a known (or unknown) type, or a literal.
    /// <see cref="ValueSpec"/> is the value's structured type when it is the parameter's own.
    /// </summary>
    private readonly record struct CallArg(string Label, ArgumentDecl Parameter, string? ValueType, TypeSpec? ValueSpec, string? Literal);

    /// <summary>The siblings that are not sure to lose the call as planned.</summary>
    private static List<MethodDecl> Competitors(
        MethodDecl declaration,
        IReadOnlyList<MethodDecl> siblings,
        IReadOnlyList<ArgumentDecl> args,
        HashSet<ArgumentDecl> omitted,
        IReadOnlyDictionary<ArgumentDecl, string> pins,
        int strippedDefaults,
        Func<ArgumentDecl, string?>? concreteArgType,
        NominalKinds kinds)
    {
        var call = new List<CallArg>();
        int targetDefaults = strippedDefaults;
        foreach (var arg in args)
        {
            var label = CdeclParamMapper.BuildSwiftCallArgLabel(arg);
            if (omitted.Contains(arg))
            {
                if (pins.TryGetValue(arg, out var pin))
                    call.Add(new CallArg(label, arg, null, null, pin[label.Length..].Trim()));
                else
                    targetDefaults++;
                continue;
            }
            var concrete = concreteArgType?.Invoke(arg);
            if (concrete != null)
                call.Add(new CallArg(label, arg, concrete, null, null));
            else if (IsGenericType(arg))
                call.Add(new CallArg(label, arg, null, null, null));
            else
                call.Add(new CallArg(label, arg, arg.SwiftTypeSpec.ToString(), arg.SwiftTypeSpec, null));
        }

        var competitors = new List<MethodDecl>();
        foreach (var sibling in siblings)
        {
            if (!MayAccept(sibling, call, kinds, out var bound, out int siblingDefaults, out bool usesVariadic))
                continue;
            if (!usesVariadic && SurelyLoses(declaration, sibling, call, bound, targetDefaults, siblingDefaults))
                continue;
            competitors.Add(sibling);
        }
        return competitors;
    }

    /// <summary>
    /// Replays Swift's argument matching of <paramref name="call"/> against
    /// <paramref name="sibling"/>: each parameter binds the next argument when the label matches and
    /// the argument is not provably the wrong type, or else must supply its own default. A variadic
    /// parameter takes no argument, or the one with its label and every unlabeled one after it.
    /// Answers false only when the sibling certainly cannot take the call; whenever a variadic
    /// parameter is involved, neither ranking rule is trusted.
    /// </summary>
    private static bool MayAccept(
        MethodDecl sibling, IReadOnlyList<CallArg> call, NominalKinds kinds, out List<ArgumentDecl> bound,
        out int defaultsUsed, out bool usesVariadic)
    {
        bound = new List<ArgumentDecl>(call.Count);
        defaultsUsed = 0;
        usesVariadic = false;
        int cursor = 0;
        foreach (var param in SwiftParameters(sibling))
        {
            if (param.SwiftTypeSpec.IsEmptyTuple)
                continue;
            bool variadic = param.SwiftTypeSpec.IsVariadic || SwiftABIParser.IsVariadicArraySpec(param.SwiftTypeSpec);
            usesVariadic |= variadic;

            if (cursor < call.Count
                && string.Equals(CdeclParamMapper.BuildSwiftCallArgLabel(param), call[cursor].Label, StringComparison.Ordinal)
                && (variadic || !ProvablyRejects(param, call[cursor], kinds)))
            {
                bound.Add(param);
                cursor++;
                while (variadic && cursor < call.Count && call[cursor].Label.Length == 0)
                {
                    bound.Add(param);
                    cursor++;
                }
                continue;
            }

            if (!param.HasDefaultArg && !DefaultParameterOverloadEmitter.IsDebugParameter(param) && !variadic)
                return false;
            defaultsUsed++;
        }
        return cursor == call.Count;
    }

    /// <summary>
    /// Whether Swift is sure to rank <paramref name="sibling"/> below the target for a call both
    /// may take. Every rule needs the same type at every literal argument (literal typing outranks
    /// favoring). Then a favored target beats a disfavored sibling. An async target is awaited, so
    /// its call is in an async context, where Swift ranks a synchronous sibling below it whatever
    /// conversions either needs; only favoring weighs more. Between declarations of the same
    /// async-ness, a target taking every argument at exactly the sibling's types wins on needing
    /// fewer defaults.
    /// </summary>
    private static bool SurelyLoses(
        MethodDecl target, MethodDecl sibling, IReadOnlyList<CallArg> call, IReadOnlyList<ArgumentDecl> bound,
        int targetDefaults, int siblingDefaults)
    {
        // A synchronous target may be called from an async context, where an async sibling wins.
        if (!target.IsAsync && sibling.IsAsync)
            return false;

        bool allIdentical = true;
        for (int i = 0; i < call.Count; i++)
        {
            bool identical = !IsGenericType(call[i].Parameter) && !IsGenericType(bound[i])
                && string.Equals(call[i].Parameter.SwiftTypeSpec.ToString(), bound[i].SwiftTypeSpec.ToString(), StringComparison.Ordinal);
            if (call[i].Literal != null && !identical)
                return false;
            allIdentical &= identical;
        }

        if (!target.IsDisfavoredOverload && sibling.IsDisfavoredOverload)
            return true;
        if (target.IsDisfavoredOverload != sibling.IsDisfavoredOverload)
            return false;
        if (target.IsAsync != sibling.IsAsync)
            return true;
        return allIdentical && siblingDefaults > targetDefaults;
    }

    private static bool IsGenericType(ArgumentDecl arg) =>
        arg.IsGeneric || arg.SwiftTypeSpec.ToString().Contains("τ_", StringComparison.Ordinal);

    // ─── Provable type mismatches ───────────────────────────────────────

    // Standard-library types no implicit conversion reaches from a different one of them. Anything
    // else (classes, protocols, Any, pointers, CGFloat beside Double, user types) may convert.
    private static readonly HashSet<string> ClosedIntegers = new(StringComparer.Ordinal)
    {
        "Swift.Int", "Swift.Int8", "Swift.Int16", "Swift.Int32", "Swift.Int64",
        "Swift.UInt", "Swift.UInt8", "Swift.UInt16", "Swift.UInt32", "Swift.UInt64",
    };
    private static readonly HashSet<string> ClosedFloats = new(StringComparer.Ordinal) { "Swift.Float", "Swift.Double" };
    private static readonly HashSet<string> ClosedOthers = new(StringComparer.Ordinal)
    {
        "Swift.Bool", "Swift.String", "Swift.Character", "Swift.Array", "Swift.Set", "Swift.Dictionary",
    };

    private static bool IsClosed(string head) =>
        ClosedIntegers.Contains(head) || ClosedFloats.Contains(head) || ClosedOthers.Contains(head);

    private static bool ProvablyRejects(ArgumentDecl param, CallArg arg, NominalKinds kinds)
    {
        if (RejectsAsFunction(param.SwiftTypeSpec, arg, kinds))
            return true;
        if (IsGenericType(param) && !IsClosed(SplitType(param.SwiftTypeSpec.ToString()).Head))
            return false;
        var paramType = param.SwiftTypeSpec.ToString();
        return arg.Literal != null
            ? ProvablyRejectsLiteral(paramType, arg.Literal)
            : arg.ValueType != null && ProvablyRejectsValue(paramType, arg.ValueType, kinds);
    }

    /// <summary>
    /// Function-type mismatches: no literal but <c>nil</c> (into an optional) and no value of a
    /// known named type is a function, a function is no closed standard-library type, and a
    /// function value or closure never fits a function type of different arity — save the one-tuple
    /// parameter Swift destructures. An autoclosure parameter takes a plain value and is not judged
    /// here. A named type the database does not know may be an alias of a function type.
    /// </summary>
    private static bool RejectsAsFunction(TypeSpec paramSpec, CallArg arg, NominalKinds kinds)
    {
        var (param, paramOptional) = UnwrapOptional(paramSpec);
        if (param is ClosureTypeSpec paramFunction && !paramFunction.IsAutoClosure)
        {
            if (arg.Literal != null)
                return arg.Literal != "nil" || !paramOptional;
            if (arg.ValueSpec == null)
                return arg.ValueType != null && IsKnownNominal(SplitType(arg.ValueType).Head, kinds);
            var (value, valueOptional) = UnwrapOptional(arg.ValueSpec);
            if (valueOptional && !paramOptional)
                return true;
            return value switch
            {
                ClosureTypeSpec valueFunction => !MayDestructure(paramFunction) && !MayDestructure(valueFunction)
                    && paramFunction.ArgumentCount() != valueFunction.ArgumentCount(),
                NamedTypeSpec named => IsKnownNominal(SplitType(named.ToString()).Head, kinds),
                ProtocolListTypeSpec => true,
                _ => false,
            };
        }
        if (arg.ValueSpec != null && UnwrapOptional(arg.ValueSpec).Spec is ClosureTypeSpec
            && param is NamedTypeSpec target && !IsGenericName(target.Name))
        {
            // A function converts to Any or an existential at most.
            return IsClosed(target.Name) || kinds.Of(target.Name) is TypeRecordKind.Struct or TypeRecordKind.Enum or TypeRecordKind.Class;
        }
        return false;
    }

    private static bool IsKnownNominal(string head, NominalKinds kinds) =>
        !IsGenericName(head) && (IsClosed(head) || head == "Swift.Optional" || kinds.Of(head) is TypeRecordKind.Struct or TypeRecordKind.Enum or TypeRecordKind.Class);

    private static bool MayDestructure(ClosureTypeSpec function) =>
        function.ArgumentCount() == 1 && function.GetArgument(0) is TupleTypeSpec;

    private static (TypeSpec Spec, bool Optional) UnwrapOptional(TypeSpec spec) =>
        spec is NamedTypeSpec { Name: "Swift.Optional" } optional && optional.GenericParameters.Count == 1
            ? (optional.GenericParameters[0], true)
            : (spec, false);

    private static bool IsGenericName(string name) => name.Contains("τ_", StringComparison.Ordinal);

    private static bool ProvablyRejectsValue(string paramType, string argType, NominalKinds kinds)
    {
        if (string.Equals(paramType, argType, StringComparison.Ordinal))
            return false;
        var (paramHead, paramArgs) = SplitType(paramType);
        var (argHead, argArgs) = SplitType(argType);
        if (paramHead == "Swift.Optional")
        {
            if (paramArgs.Count != 1)
                return false;
            // T? takes a T, and U? takes a T? when U takes a T.
            var inner = argHead == "Swift.Optional" && argArgs.Count == 1 ? argArgs[0] : argType;
            return ProvablyRejectsValue(paramArgs[0], inner, kinds);
        }
        if (IsGenericName(paramHead) || IsGenericName(argHead))
            return false;
        if (ExistentialProtocol(paramHead, kinds) is { } protocol)
            return argHead != "Swift.Optional" && kinds.ProvablyLacksConformance(argHead, protocol);

        // An existential converts to no value type and no closed type (it may reach a class through
        // a superclass-constrained protocol).
        var argKind = IsClosed(argHead) ? TypeRecordKind.Struct
            : IsExistential(argHead) ? TypeRecordKind.Existential
            : kinds.Of(argHead);
        if (IsClosed(paramHead))
        {
            if (argHead == "Swift.Optional")
                return true;
            // Collections of the same kind upcast between element types; Double and CGFloat
            // convert both ways; nothing else reaches a closed type.
            if (string.Equals(paramHead, argHead, StringComparison.Ordinal))
                return false;
            if (paramHead == "Swift.Double" && IsCGFloat(argHead))
                return false;
            return argKind != null;
        }

        switch (kinds.Of(paramHead))
        {
            case TypeRecordKind.Struct or TypeRecordKind.Enum when !HasImplicitConversionsIn(paramHead):
                // A value type takes only itself (an alias of it may be spelled differently, so an
                // argument of unknown kind may still be one).
                if (string.Equals(paramHead, argHead, StringComparison.Ordinal))
                    return false;
                return argHead == "Swift.Optional" || argKind != null;
            case TypeRecordKind.Class:
                // A class takes its subclasses; no value type or optional converts to it.
                return argHead == "Swift.Optional" || argKind is TypeRecordKind.Struct or TypeRecordKind.Enum;
            default:
                return false;
        }
    }

    private static bool IsCGFloat(string name) => name.EndsWith(".CGFloat", StringComparison.Ordinal) || name == "CGFloat";

    /// <summary>Value types Swift converts other types into implicitly.</summary>
    private static bool HasImplicitConversionsIn(string head) =>
        IsCGFloat(head)
        || head is "Swift.AnyHashable" or "Swift.OpaquePointer" or "Swift.AutoreleasingUnsafeMutablePointer"
        || head.StartsWith("Swift.Unsafe", StringComparison.Ordinal);

    private static string StripParens(string type)
    {
        type = type.Trim();
        while (type.Length > 1 && type[0] == '(' && type[^1] == ')' && SplitTopLevel(type[1..^1], ',').Count == 1)
            type = type[1..^1].Trim();
        return type;
    }

    private static bool IsExistential(string head) => StripParens(head).StartsWith("any ", StringComparison.Ordinal);

    /// <summary>The single protocol an existential parameter type names, or null.</summary>
    private static string? ExistentialProtocol(string head, NominalKinds kinds)
    {
        head = StripParens(head);
        if (head.StartsWith("any ", StringComparison.Ordinal))
            head = head[4..].Trim();
        return kinds.Of(head) == TypeRecordKind.Protocol ? head : null;
    }

    /// <summary>Reads what the type database knows about named types.</summary>
    private sealed class NominalKinds(ITypeDatabase? typeDatabase)
    {
        private readonly Dictionary<string, TypeRecord?> _cache = new(StringComparer.Ordinal);

        public TypeRecordKind? Of(string head) => Record(head)?.Kind;

        private TypeRecord? Record(string head)
        {
            if (typeDatabase == null || head.Length == 0 || !head.Contains('.') ||
                head.IndexOfAny(['<', '(', ' ', '&', '[', '?', '!']) >= 0 || IsGenericName(head))
                return null;
            if (!_cache.TryGetValue(head, out var found))
            {
                found = typeDatabase.TryGetTypeRecord(SwiftTypeName.FromModuleQualifiedName(head), out var record)
                    ? record
                    : null;
                _cache[head] = found;
            }
            return found;
        }

        /// <summary>
        /// Whether a value of struct or enum <paramref name="typeHead"/> provably does not conform
        /// to <paramref name="protocol"/>. Both must come from one module: only a module importing
        /// it could add the conformance elsewhere, and a wrapper imports none. Its own conformances
        /// list every one its module declares, extensions included, and a protocol refines only
        /// what its module can see, so only refinements in that module are walked; one whose
        /// refinements are not recorded leaves the answer unknown.
        /// </summary>
        public bool ProvablyLacksConformance(string typeHead, string protocol)
        {
            var module = ModuleOf(protocol);
            if (module == null || ModuleOf(typeHead) != module)
                return false;
            if (Record(typeHead) is not { Kind: TypeRecordKind.Struct or TypeRecordKind.Enum, ProtocolConformances: { } direct } type
                || (type.Flags & (TypeRecordFlags.ObjCBridged | TypeRecordFlags.ObjCBridgeable)) != 0)
                return false;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<SwiftTypeName>(direct);
            while (pending.Count > 0)
            {
                var name = pending.Dequeue().ModuleQualifiedName;
                if (string.Equals(name, protocol, StringComparison.Ordinal))
                    return false;
                if (!seen.Add(name))
                    continue;
                var nameModule = ModuleOf(name);
                if (nameModule == null)
                    return false;
                if (nameModule != module)
                    continue;
                if (Record(name) is not { Kind: TypeRecordKind.Protocol, ProtocolConformances: { } refined })
                    return false;
                foreach (var parent in refined)
                    pending.Enqueue(parent);
            }
            return true;
        }

        private static string? ModuleOf(string qualified)
        {
            int dot = qualified.IndexOf('.');
            return dot > 0 ? qualified[..dot] : null;
        }
    }

    private static bool ProvablyRejectsLiteral(string paramType, string literal)
    {
        var (head, typeArgs) = SplitType(paramType);
        if (head == "Swift.Optional")
            return literal != "nil" && typeArgs.Count == 1 && ProvablyRejectsLiteral(typeArgs[0], literal);
        if (!IsClosed(head))
            return false;

        if (literal == "nil")
            return true;
        if (literal is "true" or "false")
            return head != "Swift.Bool";
        if (literal.StartsWith('.') || literal == "#dsohandle")
            return false;
        if (literal == "[]")
            return head is not ("Swift.Array" or "Swift.Set");
        if (literal == "[:]")
            return head != "Swift.Dictionary";
        if (literal.StartsWith('"') || literal is "#file" or "#fileID" or "#filePath" or "#function")
            return head is not ("Swift.String" or "Swift.Character");
        bool isFloat = !literal.Contains("0x", StringComparison.OrdinalIgnoreCase)
            && (literal.Contains('.') || literal.Contains('e') || literal.Contains('E'));
        return isFloat
            ? !ClosedFloats.Contains(head)
            : !ClosedIntegers.Contains(head) && !ClosedFloats.Contains(head);
    }

    /// <summary>
    /// Splits a type spelling into its head and top-level generic arguments, desugaring
    /// <c>T?</c>, <c>[T]</c> and <c>[K: V]</c>.
    /// </summary>
    private static (string Head, List<string> Args) SplitType(string type)
    {
        type = type.Trim();
        if (type.EndsWith('?') || type.EndsWith('!'))
            return ("Swift.Optional", new List<string> { type[..^1] });
        if (type.StartsWith('[') && type.EndsWith(']'))
        {
            var body = type[1..^1];
            var parts = SplitTopLevel(body, ':');
            return parts.Count == 2
                ? ("Swift.Dictionary", parts)
                : ("Swift.Array", new List<string> { body.Trim() });
        }
        int open = type.IndexOf('<');
        if (open < 0 || !type.EndsWith('>'))
            return (type, new List<string>());
        return (type[..open], SplitTopLevel(type[(open + 1)..^1], ','));
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<' or '[' or '(':
                    depth++;
                    break;
                case '>' when i > 0 && text[i - 1] == '-':
                    break; // a function arrow, not a closing bracket
                case '>' or ']' or ')':
                    depth--;
                    break;
                default:
                    if (text[i] == separator && depth == 0)
                    {
                        parts.Add(text[start..i].Trim());
                        start = i + 1;
                    }
                    break;
            }
        }
        parts.Add(text[start..].Trim());
        return parts;
    }
}
