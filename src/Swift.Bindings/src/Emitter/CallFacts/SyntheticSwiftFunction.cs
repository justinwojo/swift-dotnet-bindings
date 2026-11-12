// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace BindingsGeneration;

/// <summary>
/// The declaration of a Swift function the generator writes into its wrapper library and binds from
/// C# with the Swift calling convention. A synthesized function has no ABI JSON entry, so the emitter
/// that writes it describes it here, from the same Swift text it writes, and the facts table turns the
/// description into <see cref="SwiftCallFacts"/> with the same type resolution declared members get.
/// </summary>
/// <remarks>
/// The description is what Swift sees: the synthesized function's own parameters in its own order, as
/// written, not the member it forwards to. Types are Swift spellings exactly as the emitter writes
/// them; one the parser cannot read, or the databases do not know, becomes an unknown in the facts,
/// never a guess.
/// </remarks>
public sealed record SyntheticSwiftFunction
{
    /// <summary>The symbol the function is exported as (its <c>@_silgen_name</c>).</summary>
    public required string Symbol { get; init; }

    public required SwiftWrapperSynthesizer Synthesizer { get; init; }

    /// <summary>The wrapper module the function is compiled into.</summary>
    public required string Module { get; init; }

    /// <summary>The declared symbol the body calls, when it forwards to exactly one.</summary>
    public string? Forwards { get; init; }

    /// <summary>For a function written inside an extension, the module-qualified extended type.</summary>
    public string? DeclaringType { get; init; }

    /// <summary>
    /// For a function written inside an extension of a generic type, the type's generic signature
    /// as its declaration carries it; the function is then compiled against the unspecialized type.
    /// </summary>
    public string? DeclaringTypeGenericSignature { get; init; }

    /// <summary>The extended type's generic parameter names as source spells them, which the written types may use.</summary>
    public ImmutableArray<string> DeclaringTypeGenericParameters { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>
    /// Whether the extended type is a class, whose <c>Self</c> is the dynamic type of one class
    /// instance: a <c>Self</c> result is then one reference, as it is for the declared member.
    /// </summary>
    public bool DeclaringTypeIsClass { get; init; }

    /// <summary>The Swift name of the function as written.</summary>
    public string? MemberName { get; init; }

    /// <summary>What the function takes as self. A top-level function takes none.</summary>
    public SyntheticSwiftSelf Self { get; init; } = SyntheticSwiftSelf.None;

    /// <summary>The parameters as written, in order.</summary>
    public ImmutableArray<SyntheticSwiftParameter> Parameters { get; init; } = ImmutableArray<SyntheticSwiftParameter>.Empty;

    /// <summary>The result type as written, or null for no result.</summary>
    public string? ResultType { get; init; }

    public bool Throws { get; init; }

    public bool IsAsync { get; init; }

    /// <summary>
    /// The generic signature as written (<c>&lt;τ_0_0 where τ_0_0 : P&gt;</c>), or null when the
    /// function is not generic.
    /// </summary>
    public string? GenericSignature { get; init; }

    /// <summary>The declared floors of the member the function wraps, which the wrapper inherits.</summary>
    public ImmutableArray<SwiftAvailabilityFloor> Availability { get; init; } = ImmutableArray<SwiftAvailabilityFloor>.Empty;

    public bool Equals(SyntheticSwiftFunction? other) =>
        other is not null
        && Symbol == other.Symbol && Synthesizer == other.Synthesizer && Module == other.Module
        && Forwards == other.Forwards && DeclaringType == other.DeclaringType && DeclaringTypeIsClass == other.DeclaringTypeIsClass
        && MemberName == other.MemberName
        && Self == other.Self && ResultType == other.ResultType && Throws == other.Throws && IsAsync == other.IsAsync
        && GenericSignature == other.GenericSignature
        && DeclaringTypeGenericSignature == other.DeclaringTypeGenericSignature
        && DeclaringTypeGenericParameters.AsSpan().SequenceEqual(other.DeclaringTypeGenericParameters.AsSpan())
        && Parameters.AsSpan().SequenceEqual(other.Parameters.AsSpan())
        && Availability.AsSpan().SequenceEqual(other.Availability.AsSpan());

    public override int GetHashCode() => HashCode.Combine(Symbol, Synthesizer, Parameters.Length, ResultType);

    /// <summary>
    /// Describes the function a producer declares with <paramref name="declaration"/>: the Swift text it
    /// writes, from the <c>@_silgen_name</c> attribute through the signature (the body, if included,
    /// is ignored). Reading the producer's own text means the description is what Swift compiles, so
    /// the two cannot drift.
    /// </summary>
    /// <param name="declaration">The written declaration, attributes included.</param>
    /// <param name="synthesizer">The producer.</param>
    /// <param name="module">The wrapper module the function is compiled into.</param>
    /// <param name="forwards">The declared symbol the body calls, when it forwards to exactly one.</param>
    /// <param name="declaringType">
    /// The extended type, when the declaration is written inside an extension; it decides what a
    /// member function takes as self. Null for a top-level function.
    /// </param>
    /// <param name="availability">The floors the wrapper inherits from the member it wraps.</param>
    /// <exception cref="FormatException">The text is not a <c>@_silgen_name</c> function declaration.</exception>
    public static SyntheticSwiftFunction FromDeclaration(
        string declaration,
        SwiftWrapperSynthesizer synthesizer,
        string module,
        string? forwards = null,
        SyntheticSwiftDeclaringType? declaringType = null,
        ImmutableArray<SwiftAvailabilityFloor> availability = default)
    {
        var header = SwiftDeclarationHeader.Read(declaration);
        return new SyntheticSwiftFunction
        {
            Symbol = header.Symbol,
            Synthesizer = synthesizer,
            Module = module,
            Forwards = forwards,
            DeclaringType = declaringType?.Type,
            DeclaringTypeGenericSignature = declaringType?.GenericSignature,
            DeclaringTypeGenericParameters = declaringType?.GenericParameters.IsDefault == false
                ? declaringType.GenericParameters
                : ImmutableArray<string>.Empty,
            DeclaringTypeIsClass = declaringType?.IsClass == true,
            MemberName = header.Name,
            Self = SelfOf(header, declaringType),
            Parameters = header.Parameters,
            ResultType = header.ResultType,
            Throws = header.Throws,
            IsAsync = header.IsAsync,
            GenericSignature = header.GenericSignature,
            Availability = availability.IsDefault ? ImmutableArray<SwiftAvailabilityFloor>.Empty : availability,
        };
    }

    /// <summary>The strictest floor per platform of <paramref name="annotations"/>, as the facts carry them.</summary>
    public static ImmutableArray<SwiftAvailabilityFloor> FloorsOf(IReadOnlyList<AvailabilityAnnotation>? annotations) =>
        SwiftCallFactsBuilder.AvailabilityOf(annotations, null, null);

    private static SyntheticSwiftSelf SelfOf(SwiftDeclarationHeader header, SyntheticSwiftDeclaringType? declaringType)
    {
        if (declaringType is null)
        {
            if (header.IsStatic || header.Mutation != SwiftSelfMutation.NotApplicable)
                throw new FormatException($"'{header.Symbol}' is declared as a member but no declaring type was given");
            return SyntheticSwiftSelf.None;
        }
        if (header.IsStatic)
            return new SyntheticSwiftSelf(
                declaringType.IsClass ? SwiftCallSelfKind.ThickMetatype : SwiftCallSelfKind.ThinMetatype,
                SwiftSelfMutation.NotApplicable,
                declaringType.Type);
        if (declaringType.IsClass)
            return new SyntheticSwiftSelf(
                SwiftCallSelfKind.Reference,
                header.Mutation is SwiftSelfMutation.Consuming or SwiftSelfMutation.Borrowing ? header.Mutation : SwiftSelfMutation.Nonmutating,
                declaringType.Type);
        return new SyntheticSwiftSelf(
            SwiftCallSelfKind.Value,
            header.Mutation == SwiftSelfMutation.NotApplicable ? SwiftSelfMutation.Nonmutating : header.Mutation,
            declaringType.Type);
    }
}

/// <summary>The type an extension that declares a synthesized function extends.</summary>
/// <param name="Type">The module-qualified Swift type.</param>
/// <param name="IsClass">Whether it is a class (its self is a reference, its metatype thick).</param>
/// <param name="GenericSignature">The type's generic signature as its declaration carries it, or null when it is not generic.</param>
/// <param name="GenericParameters">The type's generic parameter names as source spells them.</param>
public sealed record SyntheticSwiftDeclaringType(
    string Type,
    bool IsClass,
    string? GenericSignature = null,
    ImmutableArray<string> GenericParameters = default)
{
    /// <summary>The declaring type of a function written in an extension of <paramref name="type"/>.</summary>
    /// <remarks>
    /// A producer may write a type parameter by its canonical name or by its source name, so both
    /// are recorded.
    /// </remarks>
    public static SyntheticSwiftDeclaringType Of(TypeDecl type) => new(
        type.SwiftTypeName.ModuleQualifiedName,
        type is ClassDecl,
        type.IsGeneric ? SwiftCallFactsBuilder.RawSignatureOf(type) : null,
        type.IsGeneric
            ? type.GenericParameters
                .SelectMany(p => new[] { p.TypeName, p.SugaredTypeName })
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.Ordinal)
                .ToImmutableArray()
            : ImmutableArray<string>.Empty);

    public bool Equals(SyntheticSwiftDeclaringType? other) =>
        other is not null && Type == other.Type && IsClass == other.IsClass && GenericSignature == other.GenericSignature
        && (GenericParameters.IsDefault ? ImmutableArray<string>.Empty : GenericParameters).AsSpan()
            .SequenceEqual((other.GenericParameters.IsDefault ? ImmutableArray<string>.Empty : other.GenericParameters).AsSpan());

    public override int GetHashCode() => HashCode.Combine(Type, IsClass, GenericSignature);
}

/// <summary>
/// The signature of one written <c>@_silgen_name</c> function declaration, read from its text. Only
/// the generator's own wrapper declarations are read, so the grammar is the subset they use:
/// attributes, declaration modifiers, <c>func</c>, a generic parameter clause, the parameter clause,
/// effects, a result and a <c>where</c> clause.
/// </summary>
internal sealed record SwiftDeclarationHeader
{
    public required string Symbol { get; init; }
    public required string Name { get; init; }
    public required ImmutableArray<SyntheticSwiftParameter> Parameters { get; init; }
    public string? ResultType { get; init; }
    public bool Throws { get; init; }
    public bool IsAsync { get; init; }
    public bool IsStatic { get; init; }
    public SwiftSelfMutation Mutation { get; init; }
    public string? GenericSignature { get; init; }

    private const string SilgenName = "@_silgen_name(\"";

    public static SwiftDeclarationHeader Read(string declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        var at = declaration.IndexOf(SilgenName, StringComparison.Ordinal);
        if (at < 0)
            throw new FormatException("the declaration has no @_silgen_name attribute");
        var symbolStart = at + SilgenName.Length;
        var symbolEnd = declaration.IndexOf("\")", symbolStart, StringComparison.Ordinal);
        if (symbolEnd < 0)
            throw new FormatException("the @_silgen_name attribute is not closed");
        var symbol = declaration[symbolStart..symbolEnd];

        var text = declaration;
        var i = symbolEnd + 2;
        var isStatic = false;
        var mutation = SwiftSelfMutation.NotApplicable;
        while (true)
        {
            SkipSpace(text, ref i);
            if (i >= text.Length)
                throw new FormatException($"'{symbol}': no func declaration follows the attribute");
            if (text[i] == '@')
            {
                i++;
                ReadIdentifier(text, ref i);
                if (i < text.Length && text[i] == '(')
                    SkipBalanced(text, ref i, '(', ')');
                continue;
            }
            var word = ReadIdentifier(text, ref i);
            if (word.Length == 0)
                throw new FormatException($"'{symbol}': unexpected '{text[i]}' before func");
            if (word == "func")
                break;
            switch (word)
            {
                case "static" or "class":
                    isStatic = true;
                    break;
                case "mutating":
                    mutation = SwiftSelfMutation.Mutating;
                    break;
                case "nonmutating":
                    mutation = SwiftSelfMutation.Nonmutating;
                    break;
                case "consuming" or "__consuming":
                    mutation = SwiftSelfMutation.Consuming;
                    break;
                case "borrowing":
                    mutation = SwiftSelfMutation.Borrowing;
                    break;
                case "public" or "internal" or "private" or "fileprivate" or "open" or "final" or "nonisolated" or "dynamic" or "override":
                    if (i < text.Length && text[i] == '(')
                        SkipBalanced(text, ref i, '(', ')');
                    break;
                default:
                    throw new FormatException($"'{symbol}': unexpected '{word}' before func");
            }
        }

        SkipSpace(text, ref i);
        var name = ReadIdentifier(text, ref i);
        if (name.Length == 0)
            throw new FormatException($"'{symbol}': the function has no name");

        var genericParameters = new List<string>();
        var requirements = new List<string>();
        SkipSpace(text, ref i);
        if (i < text.Length && text[i] == '<')
        {
            var start = i;
            SkipBalanced(text, ref i, '<', '>');
            foreach (var parameter in SplitTopLevel(text[(start + 1)..(i - 1)], ','))
            {
                var colon = IndexOfTopLevel(parameter, ':');
                if (colon < 0)
                {
                    genericParameters.Add(parameter);
                    continue;
                }
                var parameterName = parameter[..colon].Trim();
                genericParameters.Add(parameterName);
                requirements.Add($"{parameterName} : {parameter[(colon + 1)..].Trim()}");
            }
        }

        SkipSpace(text, ref i);
        if (i >= text.Length || text[i] != '(')
            throw new FormatException($"'{symbol}': the function has no parameter clause");
        var parametersStart = i;
        SkipBalanced(text, ref i, '(', ')');
        var parameters = SplitTopLevel(text[(parametersStart + 1)..(i - 1)], ',')
            .Select(p => ReadParameter(symbol, p))
            .ToImmutableArray();

        var isAsync = false;
        var throws = false;
        string? result = null;
        while (true)
        {
            SkipSpace(text, ref i);
            if (i >= text.Length || text[i] == '{')
                break;
            if (string.CompareOrdinal(text, i, "->", 0, 2) == 0)
            {
                i += 2;
                var start = i;
                ReadTypeUntilClauseEnd(text, ref i);
                result = text[start..i].Trim();
                continue;
            }
            var word = ReadIdentifier(text, ref i);
            switch (word)
            {
                case "async":
                    isAsync = true;
                    break;
                case "throws" or "rethrows":
                    throws = true;
                    if (i < text.Length && text[i] == '(')
                        SkipBalanced(text, ref i, '(', ')');
                    break;
                case "where":
                {
                    var start = i;
                    ReadTypeUntilClauseEnd(text, ref i);
                    requirements.AddRange(SplitTopLevel(text[start..i], ','));
                    break;
                }
                default:
                    throw new FormatException($"'{symbol}': unexpected '{(word.Length > 0 ? word : text[i].ToString())}' after the parameter clause");
            }
        }

        string? genericSignature = null;
        if (genericParameters.Count > 0)
            genericSignature = requirements.Count > 0
                ? $"<{string.Join(", ", genericParameters)} where {string.Join(", ", requirements)}>"
                : $"<{string.Join(", ", genericParameters)}>";
        else if (requirements.Count > 0)
            throw new FormatException($"'{symbol}': a where clause without generic parameters");

        return new SwiftDeclarationHeader
        {
            Symbol = symbol,
            Name = name,
            Parameters = parameters,
            ResultType = result is null or "Swift.Void" or "Void" or "()" ? null : result,
            Throws = throws,
            IsAsync = isAsync,
            IsStatic = isStatic,
            Mutation = mutation,
            GenericSignature = genericSignature,
        };
    }

    private static SyntheticSwiftParameter ReadParameter(string symbol, string text)
    {
        var colon = IndexOfTopLevel(text, ':');
        if (colon < 0)
            throw new FormatException($"'{symbol}': the parameter '{text}' has no type");
        var names = text[..colon].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (names.Length is 0 or > 2)
            throw new FormatException($"'{symbol}': the parameter '{text}' is not 'label name: Type'");
        var internalName = names[^1].Trim('`');

        var type = text[(colon + 1)..];
        var equals = IndexOfTopLevel(type, '=');
        if (equals >= 0)
            type = type[..equals];
        type = type.Trim();

        SwiftParameterConvention? convention = null;
        while (true)
        {
            var space = type.IndexOf(' ');
            var specifier = space < 0 ? type : type[..space];
            SwiftParameterConvention? spelled = specifier switch
            {
                "inout" => SwiftParameterConvention.Inout,
                "__owned" or "consuming" or "sending" => SwiftParameterConvention.Owned,
                "__shared" or "borrowing" => SwiftParameterConvention.Guaranteed,
                _ => null,
            };
            if (spelled is null || space < 0)
                break;
            convention = spelled;
            type = type[(space + 1)..].TrimStart();
        }
        return new SyntheticSwiftParameter(internalName == "_" ? null : internalName, type, convention);
    }

    private static void SkipSpace(string text, ref int i)
    {
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }
            if (string.CompareOrdinal(text, i, "//", 0, 2) == 0)
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
                continue;
            }
            break;
        }
    }

    private static string ReadIdentifier(string text, ref int i)
    {
        var start = i;
        if (i < text.Length && text[i] == '`')
        {
            var close = text.IndexOf('`', i + 1);
            if (close < 0)
                throw new FormatException("an escaped identifier is not closed");
            i = close + 1;
            return text[(start + 1)..close];
        }
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] > 127))
            i++;
        return text[start..i];
    }

    /// <summary>Skips from an opening delimiter at <paramref name="i"/> past its match, nesting all three bracket kinds.</summary>
    private static void SkipBalanced(string text, ref int i, char open, char close)
    {
        if (text[i] != open)
            throw new FormatException($"expected '{open}'");
        var depth = 0;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                i = text.IndexOf('"', i + 1);
                if (i < 0)
                    throw new FormatException("a string literal is not closed");
                continue;
            }
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '>')
            {
                i++;
                continue;
            }
            if (c is '(' or '[' or '<')
                depth++;
            else if (c is ')' or ']' or '>')
            {
                depth--;
                if (depth == 0)
                {
                    if (c != close)
                        throw new FormatException($"'{open}' is closed by '{c}'");
                    i++;
                    return;
                }
            }
        }
        throw new FormatException($"'{open}' is not closed");
    }

    /// <summary>Reads a type or a requirement list up to the body, a top-level <c>where</c>, or the end.</summary>
    private static void ReadTypeUntilClauseEnd(string text, ref int i)
    {
        var depth = 0;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '>')
            {
                i++;
                continue;
            }
            if (c is '(' or '[' or '<')
                depth++;
            else if (c is ')' or ']' or '>')
                depth--;
            else if (depth == 0 && c == '{')
                return;
            else if (depth == 0 && c == 'w' && string.CompareOrdinal(text, i, "where", 0, 5) == 0
                     && (i == 0 || !IsIdentifierChar(text[i - 1]))
                     && (i + 5 >= text.Length || !IsIdentifierChar(text[i + 5])))
                return;
        }
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c > 127;

    private static int IndexOfTopLevel(string text, char target)
    {
        var depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '>')
            {
                i++;
                continue;
            }
            if (c is '(' or '[' or '<')
                depth++;
            else if (c is ')' or ']' or '>')
                depth--;
            else if (depth == 0 && c == target)
                return i;
        }
        return -1;
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        while (true)
        {
            var at = IndexOfTopLevel(text, separator);
            var part = (at < 0 ? text : text[..at]).Trim();
            if (part.Length > 0)
                parts.Add(part);
            if (at < 0)
                return parts;
            text = text[(at + 1)..];
        }
    }
}

/// <summary>What a synthesized function takes as self.</summary>
/// <param name="Kind">The self kind, as for a declared member.</param>
/// <param name="Mutation">How the function treats a value or reference self.</param>
/// <param name="Type">The Swift spelling of the self type, or null when there is none.</param>
public sealed record SyntheticSwiftSelf(SwiftCallSelfKind Kind, SwiftSelfMutation Mutation, string? Type)
{
    public static readonly SyntheticSwiftSelf None = new(SwiftCallSelfKind.None, SwiftSelfMutation.NotApplicable, null);
}

/// <summary>One parameter of a synthesized function, as written.</summary>
/// <param name="Name">The parameter's internal name as written.</param>
/// <param name="SwiftType">The Swift type as written, attributes (<c>@escaping</c>, <c>@convention(c)</c>) included.</param>
/// <param name="Convention">The ownership the declaration spells (<c>inout</c>, <c>__owned</c>), or null for the default.</param>
public sealed record SyntheticSwiftParameter(string? Name, string SwiftType, SwiftParameterConvention? Convention = null);
