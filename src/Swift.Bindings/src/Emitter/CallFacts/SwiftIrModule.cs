// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace BindingsGeneration;

/// <summary>One function swiftc defined: its calling convention and its normalized signature.</summary>
/// <param name="CallingConvention">The IR calling convention: <c>swiftcc</c>, <c>swifttailcc</c>, <c>ccc</c>.</param>
/// <param name="Signature">
/// The result type and each parameter's type with the attributes that place it, in a fixed order:
/// <c>void (ptr noalias sret, i64, ptr swiftself)</c>. Attributes that describe a value without
/// moving it (<c>captures</c>, <c>dereferenceable</c>, <c>noundef</c>) are dropped.
/// </param>
public sealed record SwiftIrFunction(string Symbol, string CallingConvention, string Signature, bool IsExported);

/// <summary>
/// The parts of a swiftc LLVM IR module the lowering evidence reads: every function definition's and
/// declaration's signature, and every named struct type's flattened leaves.
/// </summary>
public sealed class SwiftIrModule
{
    // The attributes that change where or how an argument travels; everything else is dropped.
    private static readonly string[] PlacingAttributes =
        { "noalias", "sret", "swiftself", "swifterror", "swiftasync", "zeroext", "signext", "byval", "inreg" };

    private static readonly Regex Define = new(
        @"^define\s+(?<head>[^@]*?)\s*@(?<symbol>""[^""]+""|[-\w$.]+)\((?<parameters>.*)\)[^(]*\{\s*$",
        RegexOptions.Compiled);

    // A declaration: a function another module defines, as this module calls it.
    private static readonly Regex Declare = new(
        @"^declare\s+(?<head>[^@]*?)\s*@(?<symbol>""[^""]+""|[-\w$.]+)\((?<parameters>.*)\)[^(]*$",
        RegexOptions.Compiled);

    private static readonly Regex NamedType = new(@"^(?<name>%[-\w$.""<> ]+?)\s*=\s*type\s+(?<body>.+)$", RegexOptions.Compiled);

    private static readonly HashSet<string> Linkages = new(StringComparer.Ordinal)
    {
        "private", "internal", "available_externally", "linkonce", "weak", "common", "appending",
        "extern_weak", "linkonce_odr", "weak_odr", "external",
    };

    private static readonly HashSet<string> HiddenLinkages = new(StringComparer.Ordinal)
    {
        "private", "internal", "linkonce", "linkonce_odr", "available_externally",
    };

    private static readonly HashSet<string> CallingConventions = new(StringComparer.Ordinal)
    {
        "swiftcc", "swifttailcc", "ccc", "fastcc", "coldcc", "preserve_mostcc", "preserve_allcc",
    };

    private readonly Dictionary<string, SwiftIrFunction> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SwiftIrFunction> _declarations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _types = new(StringComparer.Ordinal);

    private SwiftIrModule() { }

    public IReadOnlyDictionary<string, SwiftIrFunction> Functions => _functions;

    /// <summary>The functions the module calls without defining, as it declares them (never exported).</summary>
    public IReadOnlyDictionary<string, SwiftIrFunction> Declarations => _declarations;

    public IReadOnlyCollection<string> TypeNames => _types.Keys;

    public static SwiftIrModule Parse(string ir)
    {
        var module = new SwiftIrModule();
        foreach (var raw in ir.Split('\n'))
            module.Read(raw.TrimEnd('\r'));
        return module;
    }

    /// <summary>
    /// Every module in <paramref name="reader"/>, read a line at a time. swiftc writes one IR module per
    /// primary file, and a multi-file compile whose IR goes to standard output writes them one after
    /// another, each starting with its <c>; ModuleID</c> line. Each is kept as its own module so a
    /// symbol two of them show differently stays visible.
    /// </summary>
    public static IReadOnlyList<SwiftIrModule> ParseModules(TextReader reader)
    {
        var modules = new List<SwiftIrModule>();
        SwiftIrModule? module = null;
        while (reader.ReadLine() is { } line)
        {
            if (module is null || (line.StartsWith(ModuleHeader, StringComparison.Ordinal) && !module.IsEmpty))
            {
                module = new SwiftIrModule();
                modules.Add(module);
            }
            module.Read(line);
        }
        return modules;
    }

    private const string ModuleHeader = "; ModuleID = ";

    private bool IsEmpty => _functions.Count == 0 && _declarations.Count == 0 && _types.Count == 0;

    private void Read(string line)
    {
        if (line.StartsWith("define ", StringComparison.Ordinal))
        {
            var function = ParseFunction(Define.Match(line), exportedByDefault: true);
            if (function is not null)
                _functions[function.Symbol] = function;
        }
        else if (line.StartsWith("declare ", StringComparison.Ordinal))
        {
            var function = ParseFunction(Declare.Match(line), exportedByDefault: false);
            if (function is not null)
                _declarations[function.Symbol] = function;
        }
        else if (line.StartsWith('%'))
        {
            var match = NamedType.Match(line);
            if (match.Success && match.Groups["body"].Value is var body && body != "opaque")
                _types[match.Groups["name"].Value.Trim()] = body.Trim();
        }
    }

    private static SwiftIrFunction? ParseFunction(Match match, bool exportedByDefault)
    {
        if (!match.Success)
            return null;
        var symbol = match.Groups["symbol"].Value.Trim('"');
        var head = Tokens(match.Groups["head"].Value);
        bool exported = exportedByDefault, sawConvention = false;
        string convention = "ccc";
        var result = new List<string>();
        foreach (var token in head)
        {
            if (!sawConvention && HiddenLinkages.Contains(token))
                exported = false;
            if (!sawConvention && (token is "hidden" or "protected"))
                exported = false;
            if (!sawConvention && (Linkages.Contains(token) || token is "hidden" or "protected" or "default" or "dso_local" or "dllexport" or "unnamed_addr" or "local_unnamed_addr"))
                continue;
            if (CallingConventions.Contains(token))
            {
                convention = token;
                sawConvention = true;
                continue;
            }
            result.Add(token);
        }
        var signature = $"{NormalizeResult(result)} ({string.Join(", ", SplitTopLevel(match.Groups["parameters"].Value).Select(NormalizeParameter))})";
        return new SwiftIrFunction(symbol, convention, signature, exported);
    }

    /// <summary>The result type, with <c>zeroext</c>/<c>signext</c> kept in front of it.</summary>
    private static string NormalizeResult(List<string> tokens)
    {
        var kept = new List<string>();
        var type = new List<string>();
        foreach (var token in tokens)
        {
            if (token is "zeroext" or "signext" or "inreg")
                kept.Add(token);
            else if (IsDroppedAttribute(token))
                continue;
            else
                type.Add(token);
        }
        return string.Join(" ", kept.Concat(new[] { string.Join(" ", type) }));
    }

    private static string NormalizeParameter(string parameter)
    {
        var tokens = Tokens(parameter);
        if (tokens.Count == 0)
            return parameter;
        var type = tokens[0];
        var attributes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens.Skip(1))
        {
            var bare = token.Split('(')[0];
            if (PlacingAttributes.Contains(bare))
                attributes.Add(bare);
        }
        var sb = new StringBuilder(type);
        foreach (var attribute in PlacingAttributes.Where(attributes.Contains))
            sb.Append(' ').Append(attribute);
        return sb.ToString();
    }

    private static bool IsDroppedAttribute(string token) =>
        token.StartsWith("dereferenceable", StringComparison.Ordinal) || token.StartsWith("align", StringComparison.Ordinal)
        || token.StartsWith("captures", StringComparison.Ordinal)
        || token is "noundef" or "nonnull" or "noalias" or "nocapture" or "readonly" or "writeonly";

    /// <summary>Splits on whitespace, keeping bracketed groups and quoted names whole.</summary>
    private static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        bool quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            if (!quoted)
            {
                if (c is '(' or '{' or '<' or '[') depth++;
                if (c is ')' or '}' or '>' or ']') depth--;
                if (char.IsWhiteSpace(c) && depth == 0)
                {
                    if (current.Length > 0) tokens.Add(current.ToString());
                    current.Clear();
                    continue;
                }
            }
            current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        bool quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            if (!quoted)
            {
                if (c is '(' or '{' or '<' or '[') depth++;
                if (c is ')' or '}' or '>' or ']') depth--;
                if (c == ',' && depth == 0)
                {
                    parts.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }
            }
            current.Append(c);
        }
        if (current.ToString().Trim() is { Length: > 0 } last)
            parts.Add(last);
        return parts;
    }

    // ---- named struct types ----

    /// <summary>
    /// The flattened stored leaves of a named IR type: every typed scalar at its offset, and byte
    /// arrays as untyped bytes (swiftc spells both an enum payload and alignment padding that way, so
    /// a byte array means "bytes of some kind", never a typed scalar). Null when the type is unknown;
    /// a <see cref="FormatException"/> when it is declared in a form this reader does not model.
    /// </summary>
    public SwiftIrLayout? LayoutOf(string typeName)
    {
        if (!_types.TryGetValue(typeName, out var body))
            return null;
        var leaves = ImmutableArray.CreateBuilder<SwiftIrLeaf>();
        var size = Flatten(body, 0, leaves, depth: 0);
        return new SwiftIrLayout(size, leaves.ToImmutable());
    }

    private int Flatten(string type, int offset, ImmutableArray<SwiftIrLeaf>.Builder leaves, int depth)
    {
        if (depth > 64)
            throw new FormatException("type nests too deeply");
        type = type.Trim();
        if (type.StartsWith("<{", StringComparison.Ordinal) && type.EndsWith("}>", StringComparison.Ordinal))
            return FlattenStruct(type[2..^2], offset, leaves, packed: true, depth);
        if (type.StartsWith('{') && type.EndsWith('}'))
            return FlattenStruct(type[1..^1], offset, leaves, packed: false, depth);
        if (type.StartsWith('[') && type.EndsWith(']'))
        {
            var inner = type[1..^1];
            var x = inner.IndexOf(" x ", StringComparison.Ordinal);
            var count = int.Parse(inner[..x]);
            var element = inner[(x + 3)..].Trim();
            if (element == "i8")
            {
                if (count > 0)
                    leaves.Add(new SwiftIrLeaf(offset, count, null));
                return count;
            }
            int cursor = offset;
            for (int i = 0; i < count; i++)
                cursor += Flatten(element, cursor, leaves, depth + 1);
            return cursor - offset;
        }
        if (type.StartsWith('%'))
        {
            if (!_types.TryGetValue(type, out var body))
                throw new FormatException($"unknown type {type}");
            return Flatten(body, offset, leaves, depth + 1);
        }
        var scalar = ScalarSize(type) ?? throw new FormatException($"no size for {type}");
        leaves.Add(new SwiftIrLeaf(offset, scalar, type));
        return scalar;
    }

    private int FlattenStruct(string elements, int offset, ImmutableArray<SwiftIrLeaf>.Builder leaves, bool packed, int depth)
    {
        int cursor = offset;
        foreach (var element in SplitTopLevel(elements))
        {
            if (!packed)
                throw new FormatException("an unpacked struct type is not modeled");
            cursor += Flatten(element, cursor, leaves, depth + 1);
        }
        return cursor - offset;
    }

    private static int? ScalarSize(string type) => type switch
    {
        "i1" or "i8" => 1,
        "i16" => 2,
        "i32" or "float" => 4,
        "i64" or "ptr" or "double" => 8,
        "i128" => 16,
        _ => null,
    };
}

/// <summary>One leaf of an IR type: a typed scalar (<see cref="IrType"/> set) or untyped bytes.</summary>
public readonly record struct SwiftIrLeaf(int Offset, int Size, string? IrType)
{
    public int End => Offset + Size;

    public override string ToString() => $"{IrType ?? "bytes"}@{Offset}:{Size}";
}

public sealed record SwiftIrLayout(int Size, ImmutableArray<SwiftIrLeaf> Leaves)
{
    public override string ToString() => $"size {Size} [{string.Join(", ", Leaves)}]";
}
