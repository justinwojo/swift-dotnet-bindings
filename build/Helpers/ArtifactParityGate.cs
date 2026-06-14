// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// ArtifactParityGate.cs — pure, dependency-light cross-artifact parity logic
// for the `nuke binding-tests --compile-only` parity gate.
//
// The generator emits three artifacts that MUST agree on the ABI:
//   1. The C# bindings   (`SwiftBindingsTestLib.cs`)           — P/Invokes, struct
//      `Buffer` mirrors, and reverse-dispatch vtable `[StructLayout]` mirrors.
//   2. The compiled Swift libraries (main dylib + the generator's own async
//      wrapper dylib)                                          — the actual
//      exported `@_cdecl` symbols and `{P}_vtable` struct layouts.
//   3. The Swift ABI JSON (`*.abi.json`)                       — the source of
//      truth for a struct's stored-instance-property arity.
//
// When these drift, the failure is invisible at C# compile time and surfaces
// only at runtime as `EntryPointNotFoundException`, an out-of-bounds read, or
// silent wrong-slot dispatch. This class diffs the three artifacts so the drift
// fails the compile-only gate instead.
//
// Three checks, each grounded in a documented defect class
// (`src/docs/architecture-review-2026-06.md`):
//   • Symbol existence  — every *called* P/Invoke `EntryPoint` must exist in the
//     `nm -gU` symbol set of the dylib its `LibraryImport` names; and every
//     generator-authored wrapper export must be referenced by some P/Invoke.
//     Catches Defect A (dangling generic-enum case-ctor imports) and Defect
//     cluster D's member-path symbols (generate-then-strip / never-emitted).
//   • Struct-mirror arity — the set of Swift stored *instance* properties a C#
//     `Buffer` mirrors must equal the struct's ABI stored-instance-property set.
//     Catches Defect B (`static let` leaking into a frozen-struct Buffer →
//     over-sized mirror → OOB read).
//   • Vtable parity — for every protocol with both a C# `{P}SwiftVTable` mirror
//     and a Swift `{P}_vtable` struct, the ordered field-name lists must match.
//     Catches Finding 8 (field-count mismatch) and Defect C (an `@objc optional`
//     member skewing required-member slot indices).
//
// PURITY CONTRACT: this file is link-compiled into the unit-test project
// (`Swift.Bindings.Unit.Tests`), so it must depend only on the BCL — no Nuke,
// no Serilog, no filesystem access on the hot path. Every function takes already
// -read strings / already-parsed collections and returns plain data. The harness
// (`Build.Parity.cs`) owns the I/O: locating dylibs, running `nm`, reading files,
// logging, and turning violations into a thrown exception.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>
/// Pure cross-artifact parity logic. See file header for the three checks and
/// the defect classes each one closes.
/// </summary>
public static class ArtifactParityGate
{
    // ===================================================================
    //  Data types
    // ===================================================================

    /// <summary>A single P/Invoke declaration parsed from the generated C#.</summary>
    /// <param name="Library">The <c>LibraryImport</c>/<c>DllImport</c> library name.</param>
    /// <param name="EntryPoint">The native symbol the P/Invoke binds to (explicit
    /// <c>EntryPoint=</c>, or the method name when omitted).</param>
    /// <param name="Method">The C# partial/extern method identifier.</param>
    /// <param name="IsCalled">Whether the method is invoked anywhere outside its own
    /// declaration. An uncalled P/Invoke binding a missing symbol cannot fault at
    /// runtime, so the forward symbol check is scoped to called ones.</param>
    public sealed record ParityExtern(string Library, string EntryPoint, string Method, bool IsCalled);

    /// <summary>A struct-arity divergence: the C# Buffer stems vs the ABI stored-instance props.</summary>
    public sealed record StructArityFinding(string Struct, IReadOnlyList<string> BufferExtra, IReadOnlyList<string> BufferMissing)
    {
        // Stable identity key so the same divergence aggregates across runs.
        public string Key => $"{Struct}|extra={string.Join(",", BufferExtra)}|missing={string.Join(",", BufferMissing)}";
    }

    /// <summary>A vtable field-list divergence for a protocol present in both languages.</summary>
    public sealed record VtableFinding(string Protocol, IReadOnlyList<string> CsFields, IReadOnlyList<string> SwiftFields)
    {
        public string Key => $"{Protocol}|cs={string.Join(",", CsFields)}|swift={string.Join(",", SwiftFields)}";
    }

    /// <summary>The full, pre-baseline parity state computed from the artifacts.</summary>
    public sealed record ParityFindings(
        // Forward: called P/Invoke EntryPoints absent from their library's dylib, by library.
        IReadOnlyDictionary<string, IReadOnlyList<string>> ForwardMissingByLibrary,
        // Libraries referenced by externs that we have no dylib for (system libs) — reported, not gated.
        IReadOnlyDictionary<string, int> SkippedLibraries,
        // Reverse: generator-authored wrapper exports not referenced by any P/Invoke.
        IReadOnlyList<string> ReverseOrphans,
        // Gate 2: struct Buffer mirror divergences.
        IReadOnlyList<StructArityFinding> StructArity,
        // Gate 3: protocols whose C#/Swift vtable field lists diverge.
        IReadOnlyList<VtableFinding> VtableFieldMismatches,
        // Gate 3: protocols with a C# mirror but no Swift {P}_vtable (benign for markers/enums).
        IReadOnlyList<string> VtableCsOnly,
        // Gate 3: protocols with a Swift {P}_vtable but no C# mirror — always a violation.
        IReadOnlyList<string> VtableSwiftOnly);

    /// <summary>A single gate violation, ready to be logged and rolled up into a failure.</summary>
    public sealed record ParityViolation(string Gate, string Key, string Detail);

    // ===================================================================
    //  C# binding parsing
    // ===================================================================

    // Matches a LibraryImport/DllImport attribute, capturing the library name and
    // (optional) EntryPoint. The attribute body can carry other named args before
    // or after EntryPoint, so we scan within the attribute parentheses. The arg capture
    // is `]`-delimited, which assumes no argument value itself contains a `]` before the
    // closing `)` — true for every form the generator emits (string literals + `typeof`),
    // and a violation would only drop that one extern, surfacing as a forward/reverse
    // delta rather than a silent pass.
    private static readonly Regex ImportAttr = new(
        @"\[\s*(?:global::)?(?:[\w.]*\.)?(?:LibraryImport|DllImport)\s*\(\s*""(?<lib>[^""]+)""(?<args>[^\]]*?)\)\s*\]",
        RegexOptions.Compiled);

    private static readonly Regex EntryPointArg = new(
        @"EntryPoint\s*=\s*""(?<ep>[^""]+)""", RegexOptions.Compiled);

    // The method declaration that follows the import attribute(s). Anchored on the
    // `partial`/`extern` keyword (P/Invoke decls always carry one) so we skip any
    // interleaved attributes (e.g. [UnmanagedCallConv(...)] whose `typeof(...)`
    // would otherwise be mis-read as the method name). `[^(;{]*?` keeps the match
    // on the declaration up to the first parameter paren.
    private static readonly Regex MethodAfterImport = new(
        @"\b(?:partial|extern)\b[^(;{]*?\b(?<method>\w+)\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// Parses every P/Invoke from the generated C# into <see cref="ParityExtern"/>,
    /// including a precise call-site determination for <see cref="ParityExtern.IsCalled"/>.
    /// </summary>
    public static IReadOnlyList<ParityExtern> ParseExterns(string csSource)
    {
        var raw = new List<(string Library, string EntryPoint, string Method)>();

        foreach (Match m in ImportAttr.Matches(csSource))
        {
            var lib = m.Groups["lib"].Value;
            var args = m.Groups["args"].Value;

            // Find the method declaration after the attribute block. Look ahead from
            // the end of the attribute; bounded window so a parse hiccup can't run away.
            var window = csSource.Substring(m.Index + m.Length,
                Math.Min(600, csSource.Length - (m.Index + m.Length)));
            var mm = MethodAfterImport.Match(window);
            if (!mm.Success) continue; // not a method-targeting import we can reason about
            var method = mm.Groups["method"].Value;

            var epMatch = EntryPointArg.Match(args);
            var entryPoint = epMatch.Success ? epMatch.Groups["ep"].Value : method;

            raw.Add((lib, entryPoint, method));
        }

        // Call-site determination: a method is "called" if its identifier appears in
        // an invocation `name(` somewhere beyond its own declaration sites. Each extern
        // contributes exactly one declaration-site `name(`; if total invocations of
        // that name exceed the declaration count, it is invoked for real.
        var declCount = new Dictionary<string, int>();
        foreach (var r in raw)
            declCount[r.Method] = declCount.TryGetValue(r.Method, out var n) ? n + 1 : 0 + 1;

        var invokeCount = new Dictionary<string, int>();
        foreach (Match c in InvocationToken.Matches(csSource))
        {
            var name = c.Groups["name"].Value;
            if (declCount.ContainsKey(name))
                invokeCount[name] = invokeCount.TryGetValue(name, out var n) ? n + 1 : 1;
        }

        bool IsCalled(string method)
            => invokeCount.TryGetValue(method, out var calls)
               && calls > (declCount.TryGetValue(method, out var d) ? d : 0);

        return raw.Select(r => new ParityExtern(r.Library, r.EntryPoint, r.Method, IsCalled(r.Method))).ToList();
    }

    private static readonly Regex InvocationToken = new(@"\b(?<name>\w+)\s*\(", RegexOptions.Compiled);

    /// <summary>All native symbols referenced by the C#: explicit EntryPoints plus
    /// the method identifiers (the symbol an EntryPoint-less import binds to).</summary>
    public static IReadOnlySet<string> ReferencedSymbols(IReadOnlyList<ParityExtern> externs)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in externs)
        {
            set.Add(e.EntryPoint);
            set.Add(e.Method);
        }
        return set;
    }

    // ===================================================================
    //  nm symbol parsing
    // ===================================================================

    /// <summary>
    /// Parses <c>nm -gU</c> output into a defined-external symbol set. Mach-O symbols
    /// carry a leading underscore that the C-level <c>@_cdecl</c> name does not, so we
    /// strip exactly one. Takes the last whitespace-delimited token per line (the name)
    /// to be column-format agnostic.
    /// </summary>
    public static IReadOnlySet<string> ParseNmSymbols(string nmOutput)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in nmOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var lastSpace = trimmed.LastIndexOf(' ');
            var name = lastSpace >= 0 ? trimmed.Substring(lastSpace + 1) : trimmed;
            if (name.Length == 0) continue;
            if (name[0] == '_') name = name.Substring(1);
            set.Add(name);
        }
        return set;
    }

    /// <summary>
    /// True for symbols the generator authors into the async wrapper (and so are
    /// candidates for the reverse "is this export referenced?" check). Excludes Swift
    /// runtime/stdlib/mangled symbols the generator never binds directly. Native
    /// <c>thunk_*</c> trampolines are also intentionally excluded: they are an internal
    /// ABI detail with no 1:1 "every export must be referenced" contract, so including
    /// them would manufacture reverse-orphan noise. Their forward direction is still
    /// covered — a <i>called</i> <c>thunk_*</c> P/Invoke whose symbol is absent still
    /// trips the forward gate.
    /// </summary>
    public static bool IsAuthoredWrapperSymbol(string symbol)
        => symbol.StartsWith("SBW_", StringComparison.Ordinal)
           || symbol.StartsWith("SBSW_", StringComparison.Ordinal)
           || symbol.StartsWith("Get_SwiftBindingsTestLib", StringComparison.Ordinal)
           || WitnessTableSymbol.IsMatch(symbol)
           || SetVtableSymbol.IsMatch(symbol);

    private static readonly Regex WitnessTableSymbol = new(@"^Get_Every[A-Za-z0-9_]*Protocol_[A-Za-z0-9_]+_WitnessTable$", RegexOptions.Compiled);
    private static readonly Regex SetVtableSymbol = new(@"^Set[A-Za-z0-9_]+_vtable$", RegexOptions.Compiled);

    // ===================================================================
    //  C# struct layout mirror parsing  (Gate 2)
    // ===================================================================
    //
    // A frozen Swift struct's C# layout takes one of two shapes:
    //   • Class host — `class X : … ISwiftStruct` with a nested `struct Buffer`
    //     holding the backing fields (`StructHostClass` + the Buffer parse below).
    //   • Direct value type — `struct X : … ISwiftObject` with the backing fields
    //     inline in its own body (`DirectLayoutStruct` below).
    // A *generic* host (`class X<T> : … ISwiftStruct`) is deliberately NOT matched:
    // its layout depends on `T`, so the generator emits no fixed Buffer — it routes
    // through `PayloadBuffer<T>` indirection instead — and there is no fixed arity to
    // check. Both shapes mark every backing field with the same generator comment
    // (`// Note: Do not access this field directly`), which is the precise layout-field
    // discriminator the direct-struct path keys on to skip a struct's non-layout fields.

    private static readonly Regex StructHostClass = new(
        @"\bclass\s+(?<name>\w+)\s*:\s*[^\n{]*\bISwiftStruct\b", RegexOptions.Compiled);

    // A non-generic value-type struct implementing a Swift marker interface. `\w+\s*:`
    // (no `<…>` before the colon) excludes generic structs, and the interface clause
    // excludes the bare nested `struct Buffer` (which has no base list).
    private static readonly Regex DirectLayoutStruct = new(
        @"\bstruct\s+(?<name>\w+)\s*:\s*[^\n{]*\bISwift\w+", RegexOptions.Compiled);

    // A backing layout field carrying the generator's "do not access" marker. Used to
    // pick layout fields out of a direct struct's body (which also holds methods,
    // properties, and P/Invoke decls) without misreading a non-layout private field.
    private static readonly Regex MarkedLayoutField = new(
        @"\bprivate\s+[\w<>.\[\]?]+\s+(?<field>\w+)\s*;\s*//\s*Note: Do not access this field directly",
        RegexOptions.Compiled);

    /// <summary>
    /// For each <c>ISwiftStruct</c> class with a nested <c>struct Buffer</c>, returns the
    /// distinct Swift-property "stems" the Buffer mirrors. A multi-word Swift property
    /// expands to several C# fields (<c>storedString_0_</c>, <c>storedString_1_</c>); both
    /// collapse to the stem <c>storedString</c>, so the stem-set is robust to word count
    /// and compares cleanly against the ABI's stored-property names.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseCsBufferStems(string csSource)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (Match m in StructHostClass.Matches(csSource))
        {
            var name = m.Groups["name"].Value;
            var braceOpen = csSource.IndexOf('{', m.Index + m.Length);
            if (braceOpen < 0) continue;
            var braceClose = MatchingBrace(csSource, braceOpen);
            if (braceClose < 0) continue;
            var body = csSource.Substring(braceOpen, braceClose - braceOpen + 1);

            var bm = BufferStruct.Match(body);
            if (!bm.Success) continue;
            var bOpen = body.IndexOf('{', bm.Index + bm.Length - 1);
            if (bOpen < 0) continue;
            var bClose = MatchingBrace(body, bOpen);
            if (bClose < 0) continue;
            var bufferBody = body.Substring(bOpen, bClose - bOpen + 1);

            var stems = new List<string>();
            foreach (Match fm in PrivateField.Matches(bufferBody))
                AddDistinct(stems, FieldStem(fm.Groups["field"].Value));

            // Only structs that actually declare a Buffer participate. A class may match
            // ISwiftStruct without a Buffer (no frozen layout) — those have no arity to check.
            if (!result.ContainsKey(name))
                result[name] = stems;
        }

        return result;
    }

    /// <summary>
    /// For each non-generic value-type struct whose backing layout fields are inline in
    /// its own body (rather than in a nested <c>Buffer</c>), returns the distinct
    /// Swift-property stems it mirrors. Layout fields are identified by the generator's
    /// "do not access" marker so the struct's many non-layout private members (handles,
    /// P/Invoke decls) are not miscounted. A struct with no marked layout field is
    /// omitted — like a class with no Buffer, it has no fixed arity to check.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseCsDirectStructStems(string csSource)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (Match m in DirectLayoutStruct.Matches(csSource))
        {
            var name = m.Groups["name"].Value;
            if (result.ContainsKey(name)) continue;

            var open = csSource.IndexOf('{', m.Index + m.Length);
            if (open < 0) continue;
            var close = MatchingBrace(csSource, open);
            if (close < 0) continue;
            var body = csSource.Substring(open, close - open + 1);

            var stems = new List<string>();
            foreach (Match fm in MarkedLayoutField.Matches(body))
                AddDistinct(stems, FieldStem(fm.Groups["field"].Value));

            if (stems.Count > 0)
                result[name] = stems;
        }

        return result;
    }

    /// <summary>The combined C# layout-mirror map across both shapes (class-host Buffer
    /// and inline direct struct), keyed by the type name the ABI knows. The two shapes
    /// are disjoint by type, so a class host never collides with a direct struct.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseCsLayoutStems(string csSource)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(ParseCsBufferStems(csSource), StringComparer.Ordinal);
        foreach (var (name, stems) in ParseCsDirectStructStems(csSource))
            if (!result.ContainsKey(name))
                result[name] = stems;
        return result;
    }

    private static readonly Regex BufferStruct = new(@"\bstruct\s+Buffer\b\s*\{", RegexOptions.Compiled);
    private static readonly Regex PrivateField = new(@"\bprivate\s+[\w<>.\[\]?]+\s+(?<field>\w+)\s*;", RegexOptions.Compiled);
    private static readonly Regex FieldStemTail = new(@"_\d+_$", RegexOptions.Compiled);

    /// <summary>Reduces a Buffer field name to its Swift-property stem
    /// (<c>storedString_0_</c> → <c>storedString</c>, <c>storedInt_</c> → <c>storedInt</c>).</summary>
    public static string FieldStem(string field)
    {
        var s = FieldStemTail.Replace(field, "");
        if (s.EndsWith("_", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
        return s;
    }

    private static void AddDistinct(List<string> list, string value)
    {
        if (!list.Contains(value)) list.Add(value);
    }

    // ===================================================================
    //  Swift ABI JSON parsing  (Gate 2)
    // ===================================================================

    /// <summary>
    /// Walks the Swift ABI JSON and returns, per struct, the names of its stored
    /// <i>instance</i> properties — the fields that contribute to the struct's memory
    /// layout. Static stored properties (<c>static let version</c>) and computed
    /// properties are excluded; those must NOT appear in a Buffer mirror.
    /// </summary>
    /// <param name="abiJson">A single ABI document (<c>{"ABIRoot": …}</c>) or a JSON
    /// array of several such documents — the harness concatenates the main and
    /// dependency module ABIs as <c>[doc1, doc2]</c> so one call covers both. When two
    /// modules declare a same-named struct (the home module's primary decl plus an
    /// importing module's extension re-emission), the first decl that actually reports
    /// storage wins — see <see cref="WalkAbi"/> — so a storage-less re-emission can never
    /// mask the real layout, and two genuinely-distinct same-name structs are never
    /// cross-contaminated.</param>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseAbiStoredInstanceProps(string abiJson)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(abiJson);
        // A multi-module merge arrives as a JSON array of `{"ABIRoot": …}` wrappers, where
        // TryGetProperty would throw — hand the array straight to WalkAbi, which enumerates
        // each document and descends into its nested ABIRoot. Only unwrap when the root is a
        // single object (the common single-document case).
        var rootEl = doc.RootElement;
        var root = rootEl.ValueKind == JsonValueKind.Object && rootEl.TryGetProperty("ABIRoot", out var abiRoot)
            ? abiRoot
            : rootEl;
        WalkAbi(root, result);
        return result;
    }

    private static void WalkAbi(JsonElement node, Dictionary<string, IReadOnlyList<string>> acc)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            // A merged array passes raw `{"ABIRoot": …}` wrappers down here; descend into
            // the nested root so multi-document ABIs walk the same as a single document.
            if (node.TryGetProperty("ABIRoot", out var nestedRoot))
                WalkAbi(nestedRoot, acc);

            if (GetString(node, "declKind") == "Struct" && GetString(node, "name") is string structName)
            {
                var props = new List<string>();
                if (node.TryGetProperty("children", out var kids) && kids.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in kids.EnumerateArray())
                    {
                        if (GetString(c, "kind") != "Var") continue;
                        if (!GetBool(c, "hasStorage")) continue;
                        if (GetBool(c, "static")) continue; // static stored props don't shape instance layout
                        if (GetString(c, "name") is string pn) props.Add(pn);
                    }
                }
                // FIRST-NON-EMPTY-WINS across every decl of this struct name. A single type
                // is split across its primary decl and extension re-emissions — including
                // CROSS-MODULE: an importing module re-emits an imported struct to hang its
                // own extension's computed members off it (e.g. main re-emits the dependency's
                // `DependencyPoint` carrying only a computed `manhattanDistance`, no storage).
                // Swift extensions CANNOT add stored properties, so a type's stored layout lives
                // in exactly ONE decl; every other decl of that name contributes an empty set.
                // We therefore take the first decl that actually reports storage — which both
                //   (a) fixes the cross-module split: the storage-less re-emission no longer
                //       masks the real `{x, y}` layout (the bug "first-decl-wins" had), and
                //   (b) avoids cross-contamination: two GENUINELY-DISTINCT same-simple-name
                //       structs (e.g. `A.Tag` and `B.Tag` with different stored vars) are not
                //       unioned, so a real C# layout leak whose field name happens to be the
                //       OTHER struct's stored prop is still flagged as extra rather than masked.
                // An all-empty struct still records an empty set so an unexpected C# layout
                // field surfaces as a BufferExtra. (static stored props were filtered out above.)
                if (props.Count > 0)
                {
                    if (!acc.TryGetValue(structName, out var existing) || existing.Count == 0)
                        acc[structName] = props;
                }
                else if (!acc.ContainsKey(structName))
                {
                    acc[structName] = props; // empty — keep the key so a stray C# field is caught
                }
            }

            if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                foreach (var c in children.EnumerateArray())
                    WalkAbi(c, acc);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in node.EnumerateArray()) WalkAbi(c, acc);
        }
    }

    private static string? GetString(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    // ===================================================================
    //  Vtable parsing  (Gate 3)
    // ===================================================================

    // C# mirror: `private struct {P}SwiftVTable\n{ public IntPtr csVTHandle; ... }`.
    // Excludes the sibling `{P}LocalVTable` (managed-delegate struct, different layout).
    private static readonly Regex CsVtableStruct = new(
        @"\bstruct\s+(?<proto>\w+)SwiftVTable\b", RegexOptions.Compiled);
    private static readonly Regex CsVtableField = new(
        @"\bpublic\s+[\w<>.\[\]?*]+\s+(?<field>\w+)\s*;", RegexOptions.Compiled);

    /// <summary>Returns, per protocol, the ordered field-name list of its C#
    /// <c>{P}SwiftVTable</c> <c>[StructLayout]</c> mirror.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseCsVtables(string csSource)
        => ParseBraceDelimitedFields(csSource, CsVtableStruct, "proto", CsVtableField, "field");

    // Swift: `fileprivate struct {P}_vtable {\n var csVTHandle: ...; var func_x: ...; }`.
    private static readonly Regex SwiftVtableStruct = new(
        @"\bstruct\s+(?<proto>\w+)_vtable\b", RegexOptions.Compiled);
    private static readonly Regex SwiftVtableField = new(
        @"\bvar\s+(?<field>\w+)\s*:", RegexOptions.Compiled);

    /// <summary>Returns, per protocol, the ordered field-name list of its Swift
    /// <c>{P}_vtable</c> struct.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseSwiftVtables(string swiftSource)
        => ParseBraceDelimitedFields(swiftSource, SwiftVtableStruct, "proto", SwiftVtableField, "field");

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseBraceDelimitedFields(
        string source, Regex header, string keyGroup, Regex field, string fieldGroup)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (Match m in header.Matches(source))
        {
            var key = m.Groups[keyGroup].Value;
            var open = source.IndexOf('{', m.Index + m.Length);
            if (open < 0) continue;
            var close = MatchingBrace(source, open);
            if (close < 0) continue;
            var body = source.Substring(open, close - open + 1);

            var fields = new List<string>();
            foreach (Match fm in field.Matches(body))
                fields.Add(fm.Groups[fieldGroup].Value);

            if (!result.ContainsKey(key)) result[key] = fields;
        }
        return result;
    }

    // ===================================================================
    //  Compute findings
    // ===================================================================

    /// <summary>
    /// Computes the full pre-baseline parity state from the three artifacts. The caller
    /// supplies the dylib symbol sets (from <c>nm</c>) keyed by the <c>LibraryImport</c>
    /// name; libraries with no entry here are reported under
    /// <see cref="ParityFindings.SkippedLibraries"/> and not gated (system libs).
    /// </summary>
    /// <param name="wrapperAuthoredSymbols">Authored exports of the generator wrapper
    /// dylib (already filtered by <see cref="IsAuthoredWrapperSymbol"/>) for the reverse check.</param>
    public static ParityFindings ComputeFindings(
        string csSource,
        string swiftWrapperSource,
        string abiJson,
        IReadOnlyDictionary<string, IReadOnlySet<string>> symbolsByLibrary,
        IReadOnlySet<string> wrapperAuthoredSymbols)
    {
        var externs = ParseExterns(csSource);

        // ---- Gate 1 forward ----
        var missingByLib = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenForward = new HashSet<(string, string)>();
        foreach (var e in externs)
        {
            if (!symbolsByLibrary.TryGetValue(e.Library, out var syms))
            {
                skipped[e.Library] = skipped.TryGetValue(e.Library, out var n) ? n + 1 : 1;
                continue;
            }
            if (!e.IsCalled) continue;                       // uncalled bindings cannot fault
            if (syms.Contains(e.EntryPoint)) continue;       // symbol present — fine
            if (!seenForward.Add((e.Library, e.EntryPoint))) continue; // distinct pairs only
            if (!missingByLib.TryGetValue(e.Library, out var list))
                missingByLib[e.Library] = list = new List<string>();
            list.Add(e.EntryPoint);
        }
        foreach (var k in missingByLib.Keys.ToList())
            missingByLib[k] = missingByLib[k].OrderBy(s => s, StringComparer.Ordinal).ToList();

        // ---- Gate 1 reverse ----
        var referenced = ReferencedSymbols(externs);
        var orphans = wrapperAuthoredSymbols
            .Where(s => !referenced.Contains(s))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        // ---- Gate 2 struct arity ----
        var csBuffers = ParseCsLayoutStems(csSource);
        var abiProps = ParseAbiStoredInstanceProps(abiJson);
        var arity = new List<StructArityFinding>();
        foreach (var (name, stems) in csBuffers.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!abiProps.TryGetValue(name, out var inst)) continue; // no ABI struct (e.g. enum backing) — out of scope
            var instSet = new HashSet<string>(inst, StringComparer.Ordinal);
            var stemSet = new HashSet<string>(stems, StringComparer.Ordinal);
            var extra = stems.Where(s => !instSet.Contains(s)).OrderBy(s => s, StringComparer.Ordinal).ToList();
            var miss = inst.Where(s => !stemSet.Contains(s)).OrderBy(s => s, StringComparer.Ordinal).ToList();
            if (extra.Count > 0 || miss.Count > 0)
                arity.Add(new StructArityFinding(name, extra, miss));
        }

        // ---- Gate 3 vtable parity ----
        var csVtables = ParseCsVtables(csSource);
        var swiftVtables = ParseSwiftVtables(swiftWrapperSource);
        var vtableMismatch = new List<VtableFinding>();
        foreach (var proto in csVtables.Keys.Intersect(swiftVtables.Keys).OrderBy(s => s, StringComparer.Ordinal))
        {
            var cs = csVtables[proto];
            var sw = swiftVtables[proto];
            if (!cs.SequenceEqual(sw, StringComparer.Ordinal))
                vtableMismatch.Add(new VtableFinding(proto, cs, sw));
        }
        var csOnly = csVtables.Keys.Where(p => !swiftVtables.ContainsKey(p)).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var swiftOnly = swiftVtables.Keys.Where(p => !csVtables.ContainsKey(p)).OrderBy(s => s, StringComparer.Ordinal).ToList();

        return new ParityFindings(
            missingByLib.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal),
            skipped,
            orphans,
            arity,
            vtableMismatch,
            csOnly,
            swiftOnly);
    }

    // ===================================================================
    //  Diff against baseline → violations
    // ===================================================================

    /// <summary>
    /// Reduces the computed findings to the set of violations the gate fails on, after
    /// subtracting the committed baseline of known pre-existing divergences. Swift-only
    /// vtables are never baselineable — a Swift <c>{P}_vtable</c> with no C# mirror means
    /// reverse dispatch for that protocol cannot be wired, so it always fails.
    /// </summary>
    public static IReadOnlyList<ParityViolation> DiffAgainstBaseline(ParityFindings findings, ParityBaseline baseline)
    {
        var violations = new List<ParityViolation>();

        // Gate 1 forward.
        foreach (var (lib, missing) in findings.ForwardMissingByLibrary)
        {
            var known = baseline.SymbolForwardKnownMissing.TryGetValue(lib, out var k)
                ? new HashSet<string>(k, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            foreach (var ep in missing)
                if (!known.Contains(ep))
                    violations.Add(new ParityViolation("symbol-forward", $"{lib}:{ep}",
                        $"called P/Invoke binds '{ep}' but it is absent from the '{lib}' dylib (nm -gU)"));
        }

        // Gate 1 reverse.
        var knownOrphans = new HashSet<string>(baseline.SymbolReverseKnownOrphans, StringComparer.Ordinal);
        foreach (var orphan in findings.ReverseOrphans)
            if (!knownOrphans.Contains(orphan))
                violations.Add(new ParityViolation("symbol-reverse", orphan,
                    $"generator wrapper exports '{orphan}' but no P/Invoke references it"));

        // Gate 2 struct arity.
        var knownArity = new HashSet<string>(baseline.StructArityKnownMismatches, StringComparer.Ordinal);
        foreach (var f in findings.StructArity)
            if (!knownArity.Contains(f.Key))
                violations.Add(new ParityViolation("struct-arity", f.Key,
                    DescribeArity(f)));

        // Gate 3 vtable field mismatch.
        var knownVtable = new HashSet<string>(baseline.VtableFieldKnownMismatches.Select(v => v.Key), StringComparer.Ordinal);
        foreach (var f in findings.VtableFieldMismatches)
            if (!knownVtable.Contains(f.Key))
                violations.Add(new ParityViolation("vtable-parity", f.Key,
                    $"protocol '{f.Protocol}' vtable fields diverge — C#=[{string.Join(", ", f.CsFields)}] Swift=[{string.Join(", ", f.SwiftFields)}]"));

        // Gate 3 C#-only mirrors (baselineable — benign for markers/enums).
        var knownCsOnly = new HashSet<string>(baseline.VtableCsOnlyKnown, StringComparer.Ordinal);
        foreach (var proto in findings.VtableCsOnly)
            if (!knownCsOnly.Contains(proto))
                violations.Add(new ParityViolation("vtable-cs-only", proto,
                    $"C# emits a '{proto}SwiftVTable' mirror but Swift has no '{proto}_vtable' struct"));

        // Gate 3 Swift-only — never baselineable.
        foreach (var proto in findings.VtableSwiftOnly)
            violations.Add(new ParityViolation("vtable-swift-only", proto,
                $"Swift declares '{proto}_vtable' but C# emits no '{proto}SwiftVTable' mirror — reverse dispatch unbindable"));

        return violations;
    }

    private static string DescribeArity(StructArityFinding f)
    {
        var parts = new List<string>();
        if (f.BufferExtra.Count > 0) parts.Add($"Buffer mirrors non-instance prop(s) [{string.Join(", ", f.BufferExtra)}]");
        if (f.BufferMissing.Count > 0) parts.Add($"Buffer omits stored instance prop(s) [{string.Join(", ", f.BufferMissing)}]");
        return $"struct '{f.Struct}': {string.Join("; ", parts)}";
    }

    // ===================================================================
    //  Baseline model (pure — no Nuke; harness does file I/O)
    // ===================================================================

    /// <summary>
    /// Typed model for <c>build/baselines/parity-baseline.json</c>. Records the
    /// pre-existing cross-artifact divergences this branch carries (e.g. the mixed-
    /// generic member-path symbols of Defect cluster D, the Finding 8 / Defect C vtable
    /// over-emissions) so the gate is green now yet fails on any NEW divergence. Mirrors
    /// the <c>SkipSurfaceBaseline</c> / <c>ValidationBaseline</c> ratchet pattern; each
    /// category is keyed finely enough that a genuinely new defect of the same shape is
    /// not silently absorbed.
    /// </summary>
    public sealed class ParityBaseline
    {
        [JsonPropertyName("git_sha")] public string GitSha { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";

        /// <summary>Called P/Invoke EntryPoints currently absent from their library's dylib, by library.</summary>
        [JsonPropertyName("symbol_forward_known_missing")]
        public Dictionary<string, List<string>> SymbolForwardKnownMissing { get; set; } = new(StringComparer.Ordinal);

        /// <summary>Generator-authored wrapper exports currently referenced by no P/Invoke.</summary>
        [JsonPropertyName("symbol_reverse_known_orphans")]
        public List<string> SymbolReverseKnownOrphans { get; set; } = new();

        /// <summary>Struct-arity divergence keys (<see cref="StructArityFinding.Key"/>) currently present.</summary>
        [JsonPropertyName("struct_arity_known_mismatches")]
        public List<string> StructArityKnownMismatches { get; set; } = new();

        /// <summary>Vtable field-list divergences currently present, stored with both field
        /// lists so the on-disk baseline is reviewable and a divergence that gets <i>worse</i>
        /// (different field set) re-trips the gate.</summary>
        [JsonPropertyName("vtable_field_known_mismatches")]
        public List<VtableBaselineEntry> VtableFieldKnownMismatches { get; set; } = new();

        /// <summary>Protocols with a C# mirror but no Swift <c>{P}_vtable</c> (benign markers/enums).</summary>
        [JsonPropertyName("vtable_cs_only_known")]
        public List<string> VtableCsOnlyKnown { get; set; } = new();

        public sealed class VtableBaselineEntry
        {
            [JsonPropertyName("protocol")] public string Protocol { get; set; } = "";
            [JsonPropertyName("cs_fields")] public List<string> CsFields { get; set; } = new();
            [JsonPropertyName("swift_fields")] public List<string> SwiftFields { get; set; } = new();

            // Must match VtableFinding.Key exactly so seed/diff agree.
            [JsonIgnore] public string Key => $"{Protocol}|cs={string.Join(",", CsFields)}|swift={string.Join(",", SwiftFields)}";
        }

        // Source-generated (de)serialization so the model stays AOT-safe — this file is
        // link-compiled into the IsAotCompatible unit-test project, where reflection-based
        // JsonSerializer<T> would trip IL2026/IL3050 (warnings-as-errors).
        public static ParityBaseline Parse(string json)
            => string.IsNullOrWhiteSpace(json)
                ? new()
                : JsonSerializer.Deserialize(json, ParityBaselineJsonContext.Default.ParityBaseline) ?? new();

        public string ToJson()
            => JsonSerializer.Serialize(this, ParityBaselineJsonContext.Default.ParityBaseline);

        /// <summary>Builds a baseline that exactly absorbs the supplied findings (used by the
        /// reseed target so the committed baseline can never drift from the gate's own logic).
        /// Swift-only findings are intentionally NOT recorded — they are never baselineable.</summary>
        public static ParityBaseline Seed(ParityFindings findings, string gitSha, string description) => new()
        {
            GitSha = gitSha,
            Description = description,
            SymbolForwardKnownMissing = findings.ForwardMissingByLibrary
                .ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal),
            SymbolReverseKnownOrphans = findings.ReverseOrphans.ToList(),
            StructArityKnownMismatches = findings.StructArity.Select(f => f.Key).ToList(),
            VtableFieldKnownMismatches = findings.VtableFieldMismatches
                .Select(f => new VtableBaselineEntry { Protocol = f.Protocol, CsFields = f.CsFields.ToList(), SwiftFields = f.SwiftFields.ToList() })
                .ToList(),
            VtableCsOnlyKnown = findings.VtableCsOnly.ToList(),
        };
    }

    // ===================================================================
    //  Shared helpers
    // ===================================================================

    /// <summary>Index of the <c>}</c> matching the <c>{</c> at <paramref name="openIndex"/>,
    /// or -1 if unbalanced. Brace-aware (not regex) so nested types parse correctly.</summary>
    public static int MatchingBrace(string s, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }
}

/// <summary>Source-generation context for <see cref="ArtifactParityGate.ParityBaseline"/> —
/// keeps baseline (de)serialization AOT-safe (no reflection) so the helper link-compiles
/// cleanly into the IsAotCompatible unit-test project.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ArtifactParityGate.ParityBaseline))]
internal partial class ParityBaselineJsonContext : JsonSerializerContext
{
}
