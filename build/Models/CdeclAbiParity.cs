// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// This file is link-compiled into projects that do not enable nullable reference types, so the
// annotations below are opted into locally rather than inherited from a csproj.
#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>How a scalar crosses the C calling convention: in a general-purpose register, in a
/// floating-point register, or not at all (a void return). <see cref="Unknown"/> is a type whose
/// lowering this text-level reader cannot decide (a struct by value, an enum, a managed type).</summary>
public enum CdeclCarrierClass { Unknown, Void, Integer, Float }

/// <summary>The register a value occupies: its class and width in bits. Pointers, references and
/// function pointers are 64-bit integers (every Apple target this generator ships is 64-bit).</summary>
public readonly record struct CdeclCarrier(CdeclCarrierClass Class, int Bits)
{
    public static readonly CdeclCarrier Unknown = new(CdeclCarrierClass.Unknown, 0);
    public static readonly CdeclCarrier Void = new(CdeclCarrierClass.Void, 0);
    public static readonly CdeclCarrier Pointer = new(CdeclCarrierClass.Integer, 64);
    public static CdeclCarrier Int(int bits) => new(CdeclCarrierClass.Integer, bits);
    public static CdeclCarrier Fp(int bits) => new(CdeclCarrierClass.Float, bits);

    public bool IsDecided => Class != CdeclCarrierClass.Unknown;

    public override string ToString() => Class switch
    {
        CdeclCarrierClass.Unknown => "undecided",
        CdeclCarrierClass.Void => "void",
        CdeclCarrierClass.Integer => $"int{Bits}",
        _ => $"fp{Bits}",
    };
}

/// <summary>One parameter or return value as written, with the carrier read from it.</summary>
public sealed record CdeclSlot(string Text, CdeclCarrier Carrier);

/// <summary>
/// One side of a C-convention call: a Swift <c>@_cdecl</c> definition, or a C# P/Invoke naming it.
/// <see cref="Library"/> is the image the symbol lives in (the Swift side: the framework the file
/// compiles into; the C# side: the import's library argument).
/// </summary>
public sealed record CdeclSignature(
    string EntryPoint,
    string Library,
    IReadOnlyList<CdeclSlot> Parameters,
    CdeclSlot Return,
    string Source);

/// <summary>A generated Swift file and the framework it compiles into.</summary>
public sealed record CdeclSwiftSource(string Path, string Library);

/// <summary>
/// A pair that must be found and checked argument by argument, so the gate cannot pass because a
/// parser stopped matching. Every parameter on both sides must be decided and the arities equal.
/// </summary>
public sealed record CdeclPositiveControl(string Library, string EntryPoint, string Why);

public sealed record CdeclAbiParityVerdict(
    IReadOnlyList<string> Failures,
    int SwiftDefinitionCount,
    int PInvokeCount,
    int PairCount,
    int FullyCheckedPairCount,
    int CheckedArgumentCount,
    int SkippedArgumentCount,
    int ArityUncheckedPairCount,
    IReadOnlyDictionary<string, int> PairsByLibrary)
{
    public bool Passed => Failures.Count == 0;
}

/// <summary>
/// The pure verdict behind the cdecl ABI parity gate.
///
/// <para><b>Why it matters.</b> A <c>@_cdecl</c> Swift function and the C# P/Invoke that calls it
/// are emitted by separate code, each declaring its own parameter list. Nothing ties the two
/// together: a C# <c>int</c> against a Swift <c>Int</c> compiles on both sides, and on arm64 it
/// even works under most engines, because the upper half of the register usually happens to be
/// zero. The procedure-call standard leaves those bits unspecified for a 32-bit argument, so an
/// engine that leaves garbage there (an interpreter, for instance) hands Swift a huge count. A
/// register-class mismatch (an integer where Swift reads a floating-point register) is wrong on
/// every engine. Neither shows up until a call misbehaves.</para>
///
/// <para><b>What it reads.</b> The emitted text, on both sides: each <c>@_cdecl("…")</c> function
/// in the generated Swift, and each <c>[LibraryImport]</c>/<c>[DllImport]</c> in the generated C#
/// whose entry point (explicit or the method name) and library name such a function. Each
/// parameter and the return value is lowered to a register class and width: integer and
/// floating-point scalars by width, every pointer, reference, <c>inout</c> and function pointer as a
/// 64-bit integer, and C# <c>bool</c> by its actual marshalling (<c>[MarshalAs(U1)]</c> is one
/// byte; a <c>DllImport</c> with no <c>MarshalAs</c> is the 4-byte Win32 <c>BOOL</c>; a
/// <c>LibraryImport</c> refuses an unannotated <c>bool</c>, so that shape is undecided).</para>
///
/// <para><b>What it skips.</b> A type whose lowering text cannot decide (a struct passed by value,
/// an enum, a managed class) is skipped and counted. Arguments are compared by position when both
/// sides have the same count; when the counts differ, the pair is an arity failure only if every
/// argument on both sides is decided — a struct by value may legitimately split into several Swift
/// words, so a pair holding an undecided argument is counted as not arity-checked instead.</para>
///
/// <para><b>Self-contained by design.</b> BCL-only, so the unit-test project link-compiles it and
/// tests the parsers and <see cref="Evaluate"/> directly; the Nuke side only names the files and
/// throws.</para>
/// </summary>
public static class CdeclAbiParity
{
    static readonly Regex CdeclAttribute = new(@"@_cdecl\(\s*""([^""]+)""\s*\)", RegexOptions.CultureInvariant);
    static readonly Regex SwiftFunc = new(@"\bfunc\s+(`?[\p{L}_][\p{L}\p{Nd}_]*`?)", RegexOptions.CultureInvariant);
    static readonly Regex ImportAttribute = new(
        @"\[\s*(?:global::)?(?:System\.Runtime\.InteropServices\.)?(LibraryImport|DllImport)(?:Attribute)?\s*\(",
        RegexOptions.CultureInvariant);
    static readonly Regex EntryPointArgument = new(@"\bEntryPoint\s*=\s*""([^""]*)""", RegexOptions.CultureInvariant);
    static readonly Regex LeadingStringLiteral = new(@"^\s*""([^""]*)""", RegexOptions.CultureInvariant);
    static readonly Regex MarshalAsKind = new(
        @"MarshalAs(?:Attribute)?\s*\(\s*(?:global::)?(?:System\.Runtime\.InteropServices\.)?UnmanagedType\.(\w+)",
        RegexOptions.CultureInvariant);
    static readonly Regex Identifier = new(@"^@?[\p{L}_][\p{L}\p{Nd}_]*$", RegexOptions.CultureInvariant);
    static readonly Regex SwiftTypealias = new(
        @"^[ \t]*(?:(?:public|internal|fileprivate|private)[ \t]+)?typealias[ \t]+([A-Za-z_][A-Za-z0-9_]*)[ \t]*=[ \t]*(.+?)[ \t]*$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);
    static readonly Regex SwiftTypedPointer = new(
        @"^(?:Swift\.)?(?:UnsafePointer|UnsafeMutablePointer|AutoreleasingUnsafeMutablePointer)<.*>$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    static readonly HashSet<string> CSharpModifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "internal", "protected", "static", "extern", "partial", "unsafe", "new",
        "readonly", "file", "sealed", "override", "virtual",
    };

    // Swift scalar spellings a @_cdecl signature can carry, by register (C typealiases included).
    static readonly Dictionary<string, CdeclCarrier> SwiftScalars = new(StringComparer.Ordinal)
    {
        ["Int"] = CdeclCarrier.Int(64), ["UInt"] = CdeclCarrier.Int(64),
        ["Int64"] = CdeclCarrier.Int(64), ["UInt64"] = CdeclCarrier.Int(64),
        ["CLong"] = CdeclCarrier.Int(64), ["CUnsignedLong"] = CdeclCarrier.Int(64),
        ["CLongLong"] = CdeclCarrier.Int(64), ["CUnsignedLongLong"] = CdeclCarrier.Int(64),
        ["Int32"] = CdeclCarrier.Int(32), ["UInt32"] = CdeclCarrier.Int(32),
        ["CInt"] = CdeclCarrier.Int(32), ["CUnsignedInt"] = CdeclCarrier.Int(32),
        ["Int16"] = CdeclCarrier.Int(16), ["UInt16"] = CdeclCarrier.Int(16),
        ["CShort"] = CdeclCarrier.Int(16), ["CUnsignedShort"] = CdeclCarrier.Int(16),
        ["Int8"] = CdeclCarrier.Int(8), ["UInt8"] = CdeclCarrier.Int(8),
        ["CChar"] = CdeclCarrier.Int(8), ["CSignedChar"] = CdeclCarrier.Int(8), ["CUnsignedChar"] = CdeclCarrier.Int(8),
        ["Bool"] = CdeclCarrier.Int(8), ["CBool"] = CdeclCarrier.Int(8),
        ["Double"] = CdeclCarrier.Fp(64), ["Float64"] = CdeclCarrier.Fp(64), ["CDouble"] = CdeclCarrier.Fp(64),
        ["CGFloat"] = CdeclCarrier.Fp(64),
        ["Float"] = CdeclCarrier.Fp(32), ["Float32"] = CdeclCarrier.Fp(32), ["CFloat"] = CdeclCarrier.Fp(32),
        ["UnsafeRawPointer"] = CdeclCarrier.Pointer, ["UnsafeMutableRawPointer"] = CdeclCarrier.Pointer,
        ["OpaquePointer"] = CdeclCarrier.Pointer,
        ["AnyObject"] = CdeclCarrier.Pointer, ["AnyClass"] = CdeclCarrier.Pointer, ["Selector"] = CdeclCarrier.Pointer,
        ["Void"] = CdeclCarrier.Void, ["()"] = CdeclCarrier.Void, ["Never"] = CdeclCarrier.Void,
    };

    static readonly HashSet<string> SwiftPointerScalars = new(StringComparer.Ordinal)
    {
        "UnsafeRawPointer", "UnsafeMutableRawPointer", "OpaquePointer", "AnyObject", "AnyClass", "Selector",
    };

    // Module prefixes a generated Swift spelling may qualify a scalar with.
    static readonly string[] SwiftModulePrefixes = { "Swift.", "Foundation.", "CoreGraphics.", "CoreFoundation.", "Darwin.", "ObjectiveC." };

    static readonly Dictionary<string, CdeclCarrier> CSharpScalars = new(StringComparer.Ordinal)
    {
        ["long"] = CdeclCarrier.Int(64), ["ulong"] = CdeclCarrier.Int(64),
        ["Int64"] = CdeclCarrier.Int(64), ["UInt64"] = CdeclCarrier.Int(64),
        ["nint"] = CdeclCarrier.Pointer, ["nuint"] = CdeclCarrier.Pointer,
        ["IntPtr"] = CdeclCarrier.Pointer, ["UIntPtr"] = CdeclCarrier.Pointer,
        ["Runtime.InteropServices.SafeHandle"] = CdeclCarrier.Pointer,
        ["int"] = CdeclCarrier.Int(32), ["uint"] = CdeclCarrier.Int(32),
        ["Int32"] = CdeclCarrier.Int(32), ["UInt32"] = CdeclCarrier.Int(32),
        ["short"] = CdeclCarrier.Int(16), ["ushort"] = CdeclCarrier.Int(16),
        ["Int16"] = CdeclCarrier.Int(16), ["UInt16"] = CdeclCarrier.Int(16),
        ["byte"] = CdeclCarrier.Int(8), ["sbyte"] = CdeclCarrier.Int(8),
        ["Byte"] = CdeclCarrier.Int(8), ["SByte"] = CdeclCarrier.Int(8),
        ["double"] = CdeclCarrier.Fp(64), ["Double"] = CdeclCarrier.Fp(64),
        ["Runtime.InteropServices.NFloat"] = CdeclCarrier.Fp(64),
        ["float"] = CdeclCarrier.Fp(32), ["Single"] = CdeclCarrier.Fp(32),
        ["void"] = CdeclCarrier.Void, ["Void"] = CdeclCarrier.Void,
    };

    // ---------------------------------------------------------------- Swift

    /// <summary>Every <c>@_cdecl</c> function in one generated Swift file.</summary>
    public static IReadOnlyList<CdeclSignature> ParseSwiftCdecls(string text, string library, string source)
    {
        var result = new List<CdeclSignature>();
        var aliases = ParseSwiftTypealiases(text);
        var attributes = CdeclAttribute.Matches(text);
        for (int a = 0; a < attributes.Count; a++)
        {
            var attribute = attributes[a];
            // The declaration follows the attribute (other attributes and modifiers may sit
            // between). It must start before the next @_cdecl and before any body brace.
            int limit = a + 1 < attributes.Count ? attributes[a + 1].Index : text.Length;
            int brace = text.IndexOf('{', attribute.Index + attribute.Length);
            if (brace >= 0 && brace < limit)
                limit = brace;
            var func = SwiftFunc.Match(text, attribute.Index + attribute.Length, limit - (attribute.Index + attribute.Length));
            if (!func.Success)
                continue;

            int open = SkipWhitespace(text, func.Index + func.Length);
            if (open < text.Length && text[open] == '<')
                open = SkipWhitespace(text, SkipBalanced(text, open, '<', '>'));
            if (open >= text.Length || text[open] != '(')
                continue;
            int close = SkipBalanced(text, open, '(', ')') - 1;
            var parameters = SplitTopLevel(text.Substring(open + 1, close - open - 1))
                .Select(p => SwiftParameter(p, aliases))
                .ToList();

            // Return clause: `-> T` before the body brace (a @_cdecl function cannot throw).
            int bodyStart = text.IndexOf('{', close);
            var tail = bodyStart < 0 ? "" : text.Substring(close + 1, bodyStart - close - 1);
            var arrow = tail.IndexOf("->", StringComparison.Ordinal);
            var returnText = arrow < 0 ? "Void" : tail.Substring(arrow + 2).Trim();
            var returnCarrier = arrow < 0 ? CdeclCarrier.Void : SwiftCarrier(returnText, aliases);

            result.Add(new CdeclSignature(attribute.Groups[1].Value, library, parameters,
                new CdeclSlot(returnText, returnCarrier), source));
        }
        return result;
    }

    /// <summary>
    /// The file's one-line <c>typealias</c>es (the generator names its <c>@convention(c)</c> callback
    /// types this way). A name declared twice with different right-hand sides — two nested
    /// declarations that happen to share it — is dropped rather than guessed.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseSwiftTypealiases(string text)
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in SwiftTypealias.Matches(text))
        {
            var name = m.Groups[1].Value;
            var target = m.Groups[2].Value.TrimEnd('\r').Trim();
            if (aliases.TryGetValue(name, out var existing) && existing != target)
                ambiguous.Add(name);
            aliases[name] = target;
        }
        foreach (var name in ambiguous)
            aliases.Remove(name);
        return aliases;
    }

    static CdeclSlot SwiftParameter(string parameter, IReadOnlyDictionary<string, string> aliases)
    {
        var colon = IndexOfTopLevel(parameter, ':');
        var type = colon < 0 ? parameter : parameter.Substring(colon + 1);
        var equals = IndexOfTopLevel(type, '=');
        if (equals >= 0)
            type = type.Substring(0, equals);
        type = type.Trim();
        return new CdeclSlot(type, SwiftCarrier(type, aliases));
    }

    /// <summary>The register a Swift type spelling occupies in a <c>@_cdecl</c> signature.
    /// <paramref name="aliases"/> resolves the file's own typealiases.</summary>
    public static CdeclCarrier SwiftCarrier(string type, IReadOnlyDictionary<string, string>? aliases = null) =>
        SwiftCarrier(type, aliases, depth: 0, out _);

    // `pointerLike` marks the spellings whose Optional is still C-representable (nil is the null
    // pointer): pointers, object references, selectors and C function pointers — not an `Int` that
    // happens to share their register.
    static CdeclCarrier SwiftCarrier(string type, IReadOnlyDictionary<string, string>? aliases, int depth, out bool pointerLike)
    {
        pointerLike = false;
        var t = type.Trim();
        // Parameter attributes and ownership modifiers that do not change the lowering.
        while (true)
        {
            var stripped = StripPrefix(t, "@escaping", "@autoclosure", "@Sendable", "__owned", "__shared",
                "borrowing", "consuming", "sending");
            if (stripped == t)
                break;
            t = stripped;
        }
        if (StartsWithWord(t, "inout"))
        {
            pointerLike = true;
            return CdeclCarrier.Pointer;
        }

        // Optional: only an optional pointer or function pointer is C-representable (nil is 0).
        var optional = false;
        if (t.EndsWith('?') || t.EndsWith('!'))
        {
            optional = true;
            t = t.Substring(0, t.Length - 1).Trim();
        }
        else if ((t.StartsWith("Swift.Optional<", StringComparison.Ordinal) || t.StartsWith("Optional<", StringComparison.Ordinal))
                 && t.EndsWith('>'))
        {
            optional = true;
            t = t.Substring(t.IndexOf('<') + 1, t.Length - t.IndexOf('<') - 2).Trim();
        }
        while (t.Length > 2 && t[0] == '(' && SkipBalanced(t, 0, '(', ')') == t.Length && IndexOfTopLevel(t.Substring(1, t.Length - 2), ',') < 0)
            t = t.Substring(1, t.Length - 2).Trim();

        CdeclCarrier carrier;
        if (aliases != null && depth < 8 && aliases.TryGetValue(t, out var aliased))
            carrier = SwiftCarrier(aliased, aliases, depth + 1, out pointerLike);
        else if (t.StartsWith("@convention(c)", StringComparison.Ordinal) || t.StartsWith("@convention(block)", StringComparison.Ordinal)
                 || SwiftTypedPointer.IsMatch(t))
        {
            carrier = CdeclCarrier.Pointer;
            pointerLike = true;
        }
        else
        {
            var bare = t;
            foreach (var prefix in SwiftModulePrefixes)
                if (bare.StartsWith(prefix, StringComparison.Ordinal))
                {
                    bare = bare.Substring(prefix.Length);
                    break;
                }
            carrier = SwiftScalars.TryGetValue(bare, out var c) ? c : CdeclCarrier.Unknown;
            pointerLike = SwiftPointerScalars.Contains(bare);
        }

        if (optional)
            return pointerLike ? carrier : CdeclCarrier.Unknown;
        return carrier;
    }

    // ---------------------------------------------------------------- C#

    /// <summary>
    /// Every <c>[LibraryImport]</c>/<c>[DllImport]</c> declaration in one generated C# file. The entry
    /// point is the <c>EntryPoint</c> argument, else the method name; the library is the first
    /// argument when it is a string literal (otherwise its expression text, which pairs with nothing).
    /// </summary>
    public static IReadOnlyList<CdeclSignature> ParsePInvokes(string text, string source)
    {
        var result = new List<CdeclSignature>();
        foreach (Match attribute in ImportAttribute.Matches(text))
        {
            var isDllImport = attribute.Groups[1].Value == "DllImport";
            int argsOpen = attribute.Index + attribute.Length - 1;
            int argsEnd = SkipBalanced(text, argsOpen, '(', ')');
            var args = text.Substring(argsOpen + 1, Math.Max(0, argsEnd - argsOpen - 2));
            var libraryLiteral = LeadingStringLiteral.Match(args);
            var library = libraryLiteral.Success ? libraryLiteral.Groups[1].Value : args.Split(',')[0].Trim();
            var entryArgument = EntryPointArgument.Match(args);

            // Close the import's attribute section, then collect any further attribute sections
            // (`[return: MarshalAs(…)]`, `[UnmanagedCallConv(…)]`) up to the declaration.
            int i = SkipWhitespace(text, argsEnd);
            if (i < text.Length && text[i] == ']')
                i++;
            // Attribute sections may also precede the import attribute: take those since the end of
            // the previous member.
            var declarationAttributes = new StringBuilder();
            int previousMember = text.LastIndexOfAny(new[] { ';', '{', '}' }, Math.Max(0, attribute.Index - 1));
            declarationAttributes.Append(text, previousMember + 1, attribute.Index - previousMember - 1);
            while (true)
            {
                i = SkipTrivia(text, i);
                if (i >= text.Length || text[i] != '[')
                    break;
                int end = SkipBalanced(text, i, '[', ']');
                declarationAttributes.Append(text, i, end - i);
                i = end;
            }
            int semicolon = IndexOfTopLevel(text, ';', i);
            if (semicolon < 0)
                continue;
            var declaration = text.Substring(i, semicolon - i);
            int paramsOpen = IndexOfTopLevel(declaration, '(');
            if (paramsOpen < 0)
                continue;
            int paramsClose = SkipBalanced(declaration, paramsOpen, '(', ')') - 1;
            var head = declaration.Substring(0, paramsOpen).Trim();
            var headTokens = SplitTypeTokens(head);
            if (headTokens.Count < 2)
                continue;
            var methodName = headTokens[^1];
            var returnTokens = headTokens.Take(headTokens.Count - 1).SkipWhile(CSharpModifiers.Contains).ToList();
            var returnText = string.Join(" ", returnTokens);
            var returnMarshal = Regex.Match(declarationAttributes.ToString(), @"return\s*:[^\]]*");
            var returnCarrier = CSharpCarrier(returnText, returnMarshal.Success ? returnMarshal.Value : "", isDllImport);

            var parameters = SplitTopLevel(declaration.Substring(paramsOpen + 1, paramsClose - paramsOpen - 1))
                .Select(p => CSharpParameter(p, isDllImport))
                .ToList();

            result.Add(new CdeclSignature(
                entryArgument.Success ? entryArgument.Groups[1].Value : methodName.TrimStart('@'),
                library, parameters, new CdeclSlot(returnText, returnCarrier), source));
        }
        return result;
    }

    static CdeclSlot CSharpParameter(string parameter, bool isDllImport)
    {
        var p = parameter.Trim();
        var attributes = new StringBuilder();
        while (p.StartsWith('['))
        {
            int end = SkipBalanced(p, 0, '[', ']');
            attributes.Append(p, 0, end);
            p = p.Substring(end).Trim();
        }
        var equals = IndexOfTopLevel(p, '=');
        if (equals >= 0)
            p = p.Substring(0, equals).Trim();
        var tokens = SplitTypeTokens(p);
        // The last token is the parameter name; everything before is modifiers and the type.
        if (tokens.Count >= 2 && Identifier.IsMatch(tokens[^1]))
            tokens.RemoveAt(tokens.Count - 1);
        var type = string.Join(" ", tokens);
        return new CdeclSlot(type, CSharpCarrier(type, attributes.ToString(), isDllImport));
    }

    /// <summary>
    /// The register a C# P/Invoke type occupies after marshalling. <paramref name="attributes"/> is
    /// the attribute text on the parameter (or the <c>return:</c> section), read for <c>MarshalAs</c>.
    /// </summary>
    public static CdeclCarrier CSharpCarrier(string type, string attributes, bool isDllImport)
    {
        var t = type.Trim();
        if (t.StartsWith("scoped ", StringComparison.Ordinal))
            t = t.Substring("scoped ".Length).Trim();
        if (StartsWithWord(t, "ref") || StartsWithWord(t, "out") || StartsWithWord(t, "in"))
            return CdeclCarrier.Pointer;
        if (StartsWithWord(t, "params") || StartsWithWord(t, "this"))
            return CdeclCarrier.Unknown;
        if (t.StartsWith("delegate*", StringComparison.Ordinal) || t.EndsWith('*') || t.EndsWith("[]", StringComparison.Ordinal))
            return CdeclCarrier.Pointer;

        if (t.StartsWith("global::", StringComparison.Ordinal))
            t = t.Substring("global::".Length);
        if (t.StartsWith("System.", StringComparison.Ordinal))
            t = t.Substring("System.".Length);

        // A string always crosses as a character pointer: DllImport defaults to one, and
        // LibraryImport refuses a string with no marshalling, so a compiled one names a pointer kind.
        if (t is "string" or "string?" or "String" or "String?")
            return CdeclCarrier.Pointer;

        if (t is "bool" or "Boolean")
        {
            var marshal = MarshalAsKind.Match(attributes);
            if (marshal.Success)
                return marshal.Groups[1].Value switch
                {
                    "U1" or "I1" => CdeclCarrier.Int(8),
                    "Bool" or "I4" or "U4" => CdeclCarrier.Int(32),
                    "VariantBool" or "I2" or "U2" => CdeclCarrier.Int(16),
                    _ => CdeclCarrier.Unknown,
                };
            // An unannotated bool depends on the assembly, not the declaration: with runtime
            // marshalling (the default) DllImport passes a 4-byte Win32 BOOL and LibraryImport refuses
            // to compile it; with runtime marshalling disabled, which generated bindings set, both
            // pass one byte. The text alone cannot tell, so the shape is left undecided.
            return CdeclCarrier.Unknown;
        }
        return CSharpScalars.TryGetValue(t, out var c) ? c : CdeclCarrier.Unknown;
    }

    // ---------------------------------------------------------------- Verdict

    /// <summary>
    /// Pairs each P/Invoke with the <c>@_cdecl</c> definition(s) of the same library and entry
    /// point, and judges every pair. Imports naming a library no Swift source compiles into, or an
    /// entry point no source defines, are out of scope (other gates own missing symbols).
    /// <paramref name="minimumPairs"/> and <paramref name="positiveControls"/> turn a parser that
    /// stopped matching into a failure instead of a pass.
    /// </summary>
    public static CdeclAbiParityVerdict Evaluate(
        IReadOnlyList<CdeclSignature> swiftDefinitions,
        IReadOnlyList<CdeclSignature> pinvokes,
        int minimumPairs,
        IReadOnlyList<CdeclPositiveControl> positiveControls)
    {
        var failures = new List<string>();
        var definitions = swiftDefinitions
            .GroupBy(d => (d.Library, d.EntryPoint))
            .ToDictionary(g => g.Key, g => g.ToList());
        var libraries = swiftDefinitions.Select(d => d.Library).ToHashSet(StringComparer.Ordinal);

        int pairs = 0, fullyChecked = 0, checkedArgs = 0, skippedArgs = 0, arityUnchecked = 0;
        var pairsByLibrary = libraries.ToDictionary(l => l, _ => 0, StringComparer.Ordinal);
        var paired = new List<(CdeclSignature PInvoke, CdeclSignature Swift)>();

        foreach (var pinvoke in pinvokes)
        {
            if (!definitions.TryGetValue((pinvoke.Library, pinvoke.EntryPoint), out var swiftSides))
                continue;
            pairsByLibrary[pinvoke.Library]++;
            foreach (var swift in swiftSides)
            {
                pairs++;
                paired.Add((pinvoke, swift));
                var where = $"{pinvoke.EntryPoint} ({pinvoke.Source} ↔ {swift.Source})";
                var allDecided = pinvoke.Parameters.All(p => p.Carrier.IsDecided) && swift.Parameters.All(p => p.Carrier.IsDecided);

                if (pinvoke.Parameters.Count != swift.Parameters.Count)
                {
                    if (allDecided)
                        failures.Add($"ARITY {where}: C# passes {pinvoke.Parameters.Count} argument(s) " +
                            $"[{Describe(pinvoke.Parameters)}], Swift reads {swift.Parameters.Count} [{Describe(swift.Parameters)}].");
                    else
                    {
                        // An undecided argument (a struct by value) may occupy a different number of
                        // words on each side, so the positions after it shift. The arguments before
                        // the first undecided one and after the last one still line up: check those
                        // from each end and skip the run in between.
                        arityUnchecked++;
                        int csCount = pinvoke.Parameters.Count, swCount = swift.Parameters.Count;
                        int prefix = 0;
                        while (prefix < Math.Min(csCount, swCount)
                               && pinvoke.Parameters[prefix].Carrier.IsDecided && swift.Parameters[prefix].Carrier.IsDecided)
                            prefix++;
                        int suffix = 0;
                        while (suffix < Math.Min(csCount, swCount) - prefix
                               && pinvoke.Parameters[csCount - 1 - suffix].Carrier.IsDecided
                               && swift.Parameters[swCount - 1 - suffix].Carrier.IsDecided)
                            suffix++;
                        var aligned = Enumerable.Range(0, prefix).Select(k => (Cs: k, Sw: k))
                            .Concat(Enumerable.Range(0, suffix).Select(k => (Cs: csCount - 1 - k, Sw: swCount - 1 - k)));
                        foreach (var (csIndex, swIndex) in aligned)
                        {
                            checkedArgs++;
                            CompareArgument(csIndex, swIndex);
                        }
                        skippedArgs += csCount + swCount - 2 * (prefix + suffix);
                    }
                }
                else
                {
                    for (int i = 0; i < pinvoke.Parameters.Count; i++)
                    {
                        var cs = pinvoke.Parameters[i];
                        var sw = swift.Parameters[i];
                        if (!cs.Carrier.IsDecided || !sw.Carrier.IsDecided)
                        {
                            skippedArgs++;
                            continue;
                        }
                        checkedArgs++;
                        CompareArgument(i, i);
                    }
                    if (allDecided)
                        fullyChecked++;
                }

                void CompareArgument(int csIndex, int swIndex)
                {
                    var cs = pinvoke.Parameters[csIndex];
                    var sw = swift.Parameters[swIndex];
                    if (cs.Carrier != sw.Carrier)
                        failures.Add($"{Kind(cs.Carrier, sw.Carrier)} {where} argument {csIndex}" +
                            (csIndex == swIndex ? "" : $" (Swift argument {swIndex})") +
                            $": C# `{cs.Text}` is {cs.Carrier}, Swift `{sw.Text}` is {sw.Carrier}.");
                }

                if (pinvoke.Return.Carrier.IsDecided && swift.Return.Carrier.IsDecided && pinvoke.Return.Carrier != swift.Return.Carrier)
                    failures.Add($"{Kind(pinvoke.Return.Carrier, swift.Return.Carrier)} {where} return: C# `{pinvoke.Return.Text}` is {pinvoke.Return.Carrier}, " +
                        $"Swift `{swift.Return.Text}` is {swift.Return.Carrier}.");
            }
        }

        // Vacuity guards.
        if (pairs < minimumPairs)
            failures.Add($"only {pairs} P/Invoke ↔ @_cdecl pair(s) found, below the floor of {minimumPairs} — the generated output is missing or a parser no longer matches the emitters.");
        foreach (var (library, count) in pairsByLibrary.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (count == 0)
                failures.Add($"no P/Invoke pairs with a @_cdecl compiled into {library} — that source's parse or its C# imports were lost.");
        foreach (var control in positiveControls)
        {
            var hits = paired.Where(p => p.PInvoke.Library == control.Library && p.PInvoke.EntryPoint == control.EntryPoint).ToList();
            if (hits.Count == 0)
                failures.Add($"positive control {control.EntryPoint} in {control.Library} was not paired ({control.Why}).");
            foreach (var (pinvoke, swift) in hits)
            {
                var undecided = pinvoke.Parameters.Concat(swift.Parameters).Where(p => !p.Carrier.IsDecided).Select(p => p.Text).ToList();
                if (pinvoke.Parameters.Count == 0 || pinvoke.Parameters.Count != swift.Parameters.Count || undecided.Count > 0)
                    failures.Add($"positive control {control.EntryPoint} in {control.Library} was not checked argument by argument " +
                        $"(C# {pinvoke.Parameters.Count}, Swift {swift.Parameters.Count}, undecided: {string.Join(", ", undecided)}) ({control.Why}).");
            }
        }

        return new CdeclAbiParityVerdict(failures, swiftDefinitions.Count, pinvokes.Count, pairs, fullyChecked,
            checkedArgs, skippedArgs, arityUnchecked, pairsByLibrary);

        static string Describe(IReadOnlyList<CdeclSlot> slots) => string.Join(", ", slots.Select(s => $"{s.Text}: {s.Carrier}"));
        static string Kind(CdeclCarrier cs, CdeclCarrier sw) => cs.Class != sw.Class ? "CLASS" : "WIDTH";
    }

    /// <summary>
    /// Reads a generated output tree: the named Swift sources, and every <c>.cs</c> under
    /// <paramref name="csharpRoot"/> except build products (<c>bin</c>, <c>obj</c>, dot-directories).
    /// A named Swift source that is missing, or that holds no <c>@_cdecl</c>, is a failure.
    /// </summary>
    public static CdeclAbiParityVerdict EvaluateTree(
        string csharpRoot,
        IReadOnlyList<CdeclSwiftSource> swiftSources,
        int minimumPairs,
        IReadOnlyList<CdeclPositiveControl> positiveControls)
    {
        var sourceFailures = new List<string>();
        var swift = new List<CdeclSignature>();
        foreach (var source in swiftSources)
        {
            if (!File.Exists(source.Path))
            {
                sourceFailures.Add($"generated Swift source {source.Path} ({source.Library}) not found.");
                continue;
            }
            var parsed = ParseSwiftCdecls(File.ReadAllText(source.Path), source.Library, Path.GetFileName(source.Path));
            if (parsed.Count == 0)
                sourceFailures.Add($"no @_cdecl function parsed from {source.Path} — the parser no longer matches the emitter.");
            swift.AddRange(parsed);
        }

        var pinvokes = new List<CdeclSignature>();
        foreach (var file in EnumerateCSharp(csharpRoot))
            pinvokes.AddRange(ParsePInvokes(File.ReadAllText(file), Path.GetRelativePath(csharpRoot, file)));

        var verdict = Evaluate(swift, pinvokes, minimumPairs, positiveControls);
        return sourceFailures.Count == 0 ? verdict : verdict with { Failures = sourceFailures.Concat(verdict.Failures).ToList() };
    }

    static IEnumerable<string> EnumerateCSharp(string root)
    {
        if (!Directory.Exists(root))
            yield break;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var file in Directory.GetFiles(dir, "*.cs").OrderBy(f => f, StringComparer.Ordinal))
                yield return file;
            foreach (var sub in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name is "bin" or "obj" || name.StartsWith('.'))
                    continue;
                pending.Push(sub);
            }
        }
    }

    // ---------------------------------------------------------------- Text helpers

    static int SkipWhitespace(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i]))
            i++;
        return i;
    }

    // Whitespace and `//` / `/* */` comments.
    static int SkipTrivia(string s, int i)
    {
        while (true)
        {
            i = SkipWhitespace(s, i);
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '/')
            {
                var nl = s.IndexOf('\n', i);
                i = nl < 0 ? s.Length : nl + 1;
            }
            else if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? s.Length : end + 2;
            }
            else
                return i;
        }
    }

    /// <summary>Index just past the bracket matching the one at <paramref name="open"/>. Every
    /// bracket kind nests; a <c>&gt;</c> that ends a Swift <c>-&gt;</c> arrow is not a bracket.</summary>
    static int SkipBalanced(string s, int open, char openChar, char closeChar)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '(' or '[' or '<' or '{')
                depth++;
            else if (c is ')' or ']' or '}' || (c == '>' && !(i > 0 && s[i - 1] == '-')))
            {
                depth--;
                if (depth == 0)
                    return i + 1;
            }
        }
        return s.Length;
    }

    static int IndexOfTopLevel(string s, char target, int start = 0)
    {
        int depth = 0;
        for (int i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (depth == 0 && c == target)
                return i;
            if (c is '(' or '[' or '<' or '{')
                depth++;
            else if ((c is ')' or ']' or '}' || (c == '>' && !(i > 0 && s[i - 1] == '-'))) && depth > 0)
                depth--;
        }
        return -1;
    }

    static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '(' or '[' or '<' or '{')
                depth++;
            else if ((c is ')' or ']' or '}' || (c == '>' && !(i > 0 && s[i - 1] == '-'))) && depth > 0)
                depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(s.Substring(start, i - start));
                start = i + 1;
            }
        }
        parts.Add(s.Substring(start));
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    // Whitespace-separated tokens, keeping bracketed runs (generic arguments, function-pointer
    // signatures) inside the token they belong to.
    static List<string> SplitTypeTokens(string s)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        foreach (var c in s)
        {
            if (c is '(' or '[' or '<')
                depth++;
            else if (c is ')' or ']' or '>' && depth > 0)
                depth--;
            if (char.IsWhiteSpace(c) && depth == 0)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    static bool StartsWithWord(string s, string word) =>
        s.StartsWith(word, StringComparison.Ordinal) && s.Length > word.Length && char.IsWhiteSpace(s[word.Length]);

    static string StripPrefix(string s, params string[] prefixes)
    {
        foreach (var prefix in prefixes)
            if (s.StartsWith(prefix, StringComparison.Ordinal) && s.Length > prefix.Length && char.IsWhiteSpace(s[prefix.Length]))
                return s.Substring(prefix.Length).Trim();
        return s;
    }
}
