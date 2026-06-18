// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Calling convention for P/Invoke declarations.
/// Most methods use CallConvCdecl (routed through @_cdecl wrappers or native ARM64 thunks).
/// Methods with WrapperStrategy.None that target raw Swift symbols use CallConvSwift.
/// </summary>
public enum PInvokeCallingConvention
{
    /// <summary>C calling convention via CallConvCdecl. Used for @_cdecl wrappers and native thunks.</summary>
    Cdecl,

    /// <summary>Swift calling convention via CallConvSwift. Used for direct Swift symbol calls
    /// (WrapperStrategy.None) where no wrapper or thunk is available.</summary>
    Swift
}

/// <summary>
/// Visibility for P/Invoke declarations.
/// </summary>
public enum PInvokeVisibility
{
    Private,
    Internal,
    Public
}

/// <summary>
/// Describes a P/Invoke declaration for centralized emission.
/// </summary>
public record PInvokeEmissionInfo
{
    /// <summary>The library path for LibraryImport.</summary>
    public required string LibraryPath { get; init; }

    /// <summary>The entry point (mangled Swift symbol name).</summary>
    public required string EntryPoint { get; init; }

    /// <summary>The P/Invoke method name.</summary>
    public required string MethodName { get; init; }

    /// <summary>The return type of the P/Invoke method.</summary>
    public required string ReturnType { get; init; }

    /// <summary>The parameter list string for the P/Invoke method.</summary>
    public required string ParametersString { get; init; }

    /// <summary>Calling convention: Cdecl for wrappers/thunks, Swift for direct symbol calls.</summary>
    public PInvokeCallingConvention CallingConvention { get; init; } = PInvokeCallingConvention.Cdecl;

    /// <summary>Visibility modifier for the declaration.</summary>
    public PInvokeVisibility Visibility { get; init; } = PInvokeVisibility.Private;

    /// <summary>Whether to emit the 'new' modifier (for derived class metadata accessors).</summary>
    public bool HasNewModifier { get; init; }

    /// <summary>Whether to use fully-qualified type names (for marker protocol overloads).</summary>
    public bool UseFullyQualifiedNames { get; init; }

    /// <summary>Additional TypeMetadata parameters for generic type support.</summary>
    public IReadOnlyList<string>? MetadataParameters { get; init; }

    /// <summary>Whether this P/Invoke is for an async method (always returns void).</summary>
    public bool IsAsync { get; init; }

    /// <summary>Whether the method needs the 'unsafe' modifier.</summary>
    public bool IsUnsafe { get; init; }

    /// <summary>
    /// Optional handle to the per-module emission context. When supplied alongside
    /// <see cref="EnforceWrapperContract"/>, <see cref="PInvokeEmitHelper"/> consults
    /// <see cref="ModuleEmissionContext.IsWrapperSymbolRegistered"/> before emitting
    /// — refusing (via <see cref="WrapperSymbolContractException"/>) when the entry
    /// point matches the wrapper-symbol convention but no wrapper-emit path
    /// registered it. Required for the in-band wrapper-symbol contract check.
    /// </summary>
    public ModuleEmissionContext? EmissionContext { get; init; }

    /// <summary>
    /// When true and <see cref="EmissionContext"/> is non-null, the in-band
    /// wrapper-symbol contract check fires. Default is false so existing call
    /// sites preserve their current behaviour while the contract is rolled out
    /// chokepoint-by-chokepoint.
    /// </summary>
    public bool EnforceWrapperContract { get; init; }
}

/// <summary>
/// Centralized helper for emitting P/Invoke declarations.
/// Eliminates duplicated emission logic across 19 emitter files.
/// </summary>
public static class PInvokeEmitHelper
{
    /// <summary>
    /// Emit a P/Invoke declaration to a CSharpWriter.
    /// </summary>
    public static void EmitDeclaration(CSharpWriter csWriter, PInvokeEmissionInfo info)
    {
        foreach (var line in FormatDeclarationLines(info))
        {
            csWriter.WriteLine(line);
        }
    }

    /// <summary>
    /// Returns true when <paramref name="entryPoint"/> follows the wrapper-symbol
    /// naming convention (SBW_… prefix). Wrapper-emit owns this prefix; binding-emit
    /// only ever produces it when it expects to call into a wrapper that wrapper-emit
    /// emitted. Direct Swift mangled symbols ($s…) and ObjC selectors are excluded.
    /// </summary>
    public static bool IsWrapperEntryPoint(string entryPoint) =>
        !string.IsNullOrEmpty(entryPoint) && entryPoint.StartsWith("SBW_", StringComparison.Ordinal);

    /// <summary>
    /// Returns true when <paramref name="entryPoint"/> follows the Swift-CC wrapper-symbol
    /// naming convention (SBSW_… prefix). SBSW_ wrappers are emitted on the Swift side as
    /// <c>@_silgen_name</c> functions whose signature is not C-representable; the C# P/Invoke
    /// must declare <see cref="PInvokeCallingConvention.Swift"/> to match.
    /// </summary>
    public static bool IsSwiftCCWrapperEntryPoint(string entryPoint) =>
        !string.IsNullOrEmpty(entryPoint) && entryPoint.StartsWith("SBSW_", StringComparison.Ordinal);

    /// <summary>
    /// Single decision point for (entry-point, calling-convention) pairing. Swift mangled
    /// symbols (<c>$s…</c>) always use <see cref="PInvokeCallingConvention.Swift"/>; the
    /// <c>SBW_…</c> cdecl-wrapper convention always uses <see cref="PInvokeCallingConvention.Cdecl"/>;
    /// every other prefix uses the caller-supplied convention. The asymmetric handling of
    /// the two prefixes is intentional:
    /// <list type="bullet">
    ///   <item><description><c>$s…</c> + <see cref="PInvokeCallingConvention.Cdecl"/>: silently
    ///   coerced to <see cref="PInvokeCallingConvention.Swift"/>. Swift owns the <c>$s</c> mangling
    ///   convention; any contradiction is always safely resolved by picking Swift CC. This shape
    ///   was the 0.10.0 mangled-symbol desync; the coercion here, combined with the post-emit
    ///   <c>EntryPointCallConvPairingTests</c> reflection audit, makes it impossible to ship.</description></item>
    ///   <item><description><c>SBW_…</c> + <see cref="PInvokeCallingConvention.Swift"/>: throws.
    ///   <c>SBW_</c> is OUR naming convention reserved for <c>@_cdecl</c> wrappers; a mismatch
    ///   here means a real wrapper-emit / binding-emit desync that needs a code-level fix, not
    ///   a silent rewrite. <c>@_silgen_name</c> (Swift CC) wrappers must pick a different prefix
    ///   (e.g. <c>SBSW_</c>) so the pair stays self-describing.</description></item>
    /// </list>
    /// </summary>
    public static PInvokeCallingConvention SelectCallingConvention(
        string entryPoint,
        PInvokeCallingConvention callerSpec)
    {
        if (string.IsNullOrEmpty(entryPoint))
            return callerSpec;

        // Swift mangled symbols use Swift's calling convention exclusively. Silent coercion
        // here — combined with the post-emit reflection audit — ensures the 0.10.0 mangled-
        // symbol + Cdecl bug cannot ship even when an upstream caller (test fixture or
        // production gate) leaves the convention at its default.
        if (entryPoint.StartsWith(ManglingProbes.StablePrefix, StringComparison.Ordinal))
            return PInvokeCallingConvention.Swift;

        // SBSW_ is reserved for @_silgen_name (Swift CC) wrappers — used when the wrapper
        // signature cannot be made C-representable (non-@objc class self, non-blittable
        // passthrough args). Pin to Swift CC; refuse a Cdecl request explicitly so a
        // mis-paired caller surfaces here instead of producing a silent ABI desync.
        // (Branch order vs the SBW_ check below is independent: StartsWith("SBW_") requires
        // the underscore as the fourth character, so it cannot swallow an SBSW_ entry point.)
        if (entryPoint.StartsWith("SBSW_", StringComparison.Ordinal))
        {
            if (callerSpec == PInvokeCallingConvention.Cdecl)
                throw new InvalidOperationException(
                    $"P/Invoke entry point '{entryPoint}' starts with the SBSW_ (Swift-CC wrapper) " +
                    "convention prefix but the caller requested CallConvCdecl. Either the wrapper " +
                    "needs to be emitted as @_cdecl with an SBW_ prefix or the caller needs to pass " +
                    "CallingConvention = PInvokeCallingConvention.Swift.");
            return PInvokeCallingConvention.Swift;
        }

        // SBW_ is reserved for @_cdecl wrappers. @_silgen_name wrappers (Swift CC) must
        // pick a different prefix (e.g. SBSW_) so the pair stays self-describing. Unlike
        // the $s case, a contradiction here points to a wrapper-emit/binding-emit desync
        // that needs an explicit code-level fix.
        if (entryPoint.StartsWith("SBW_", StringComparison.Ordinal))
        {
            if (callerSpec == PInvokeCallingConvention.Swift)
                throw new InvalidOperationException(
                    $"P/Invoke entry point '{entryPoint}' starts with the SBW_ (cdecl wrapper) " +
                    "convention prefix but the caller requested CallConvSwift. Either the wrapper " +
                    "needs to be emitted as @_cdecl (the SBW_ convention) or the entry-point prefix " +
                    "needs to change to signal Swift calling convention (e.g. SBSW_).");
            return PInvokeCallingConvention.Cdecl;
        }

        return callerSpec;
    }

    /// <summary>
    /// Format a P/Invoke declaration as individual lines (unindented).
    /// Callers prepend their own indentation when appending to StringBuilder or raw strings.
    /// </summary>
    /// <exception cref="WrapperSymbolContractException">
    /// Thrown when <see cref="PInvokeEmissionInfo.EnforceWrapperContract"/> is true,
    /// <see cref="PInvokeEmissionInfo.EmissionContext"/> is non-null, the entry point
    /// matches the wrapper-symbol convention for its resolved calling convention
    /// (SBW_… with <see cref="PInvokeCallingConvention.Cdecl"/> or SBSW_… with
    /// <see cref="PInvokeCallingConvention.Swift"/>), and the symbol was never
    /// registered.
    /// </exception>
    public static IReadOnlyList<string> FormatDeclarationLines(PInvokeEmissionInfo info)
    {
        // Single-point (entry-point, call-conv) pairing — Swift mangled $s… always uses
        // Swift CC, SBW_… always uses Cdecl. Any caller spec that contradicts the prefix
        // implication is reconciled here so the desync that produced the 0.10.0 mangled-
        // symbol + Cdecl bug is impossible at construction time.
        var resolvedCallingConvention = SelectCallingConvention(info.EntryPoint, info.CallingConvention);

        // In-band wrapper-symbol contract: refuse to emit a P/Invoke whose entry
        // point looks like a wrapper symbol (SBW_… cdecl or SBSW_… Swift-CC) when
        // wrapper-emit never registered it. Catches the failure shape behind the
        // three 0.10.0 bugs where binding-emit referenced a symbol that wrapper-emit
        // never produced. Both prefix shapes funnel through the same registry
        // (RegisterWrapperSymbolInternal), so the registered-check is identical;
        // only the (prefix, resolved-CC) gate differs.
        if (info.EnforceWrapperContract &&
            info.EmissionContext != null &&
            ((resolvedCallingConvention == PInvokeCallingConvention.Cdecl &&
              IsWrapperEntryPoint(info.EntryPoint)) ||
             (resolvedCallingConvention == PInvokeCallingConvention.Swift &&
              IsSwiftCCWrapperEntryPoint(info.EntryPoint))) &&
            !info.EmissionContext.IsWrapperSymbolRegistered(info.EntryPoint))
        {
            throw new WrapperSymbolContractException(info.EntryPoint, info.MethodName);
        }

        var lines = new List<string>();

        // Calling convention attribute — use the resolved convention
        var callConvType = resolvedCallingConvention switch
        {
            PInvokeCallingConvention.Swift => "CallConvSwift",
            _ => "CallConvCdecl"
        };

        // If any parameter or return type is string, add StringMarshalling attribute
        var needsStringMarshalling = info.ParametersString.Contains("string ")
            || info.ParametersString.Contains("string?")
            || (!info.IsAsync && info.ReturnType == "string");
        var stringMarshalSuffix = needsStringMarshalling ? ", StringMarshalling = StringMarshalling.Utf8" : "";

        if (info.UseFullyQualifiedNames)
        {
            lines.Add($"[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] {{ typeof(global::System.Runtime.CompilerServices.{callConvType}) }})]");
            lines.Add($"[global::System.Runtime.InteropServices.LibraryImport(\"{info.LibraryPath}\", EntryPoint = \"{info.EntryPoint}\"{stringMarshalSuffix})]");
        }
        else
        {
            lines.Add($"[UnmanagedCallConv(CallConvs = new Type[] {{ typeof({callConvType}) }})]");
            lines.Add($"[LibraryImport(\"{info.LibraryPath}\", EntryPoint = \"{info.EntryPoint}\"{stringMarshalSuffix})]");
        }

        // Return type (async always returns void)
        var returnTypeStr = info.IsAsync ? "void" : info.ReturnType;

        // Bool return marshalling
        if (MarshallingHelpers.IsBoolType(returnTypeStr))
            lines.Add("[return: MarshalAs(UnmanagedType.U1)]");

        // Build parameter string with metadata parameters
        var paramsStr = info.ParametersString;
        if (info.MetadataParameters != null && info.MetadataParameters.Count > 0)
        {
            var metadataParams = string.Join(", ", info.MetadataParameters);
            if (!string.IsNullOrEmpty(paramsStr))
                paramsStr = $"{paramsStr}, {metadataParams}";
            else
                paramsStr = metadataParams;
        }

        // Universal C# P/Invoke parameter-name dedup. A synthetic the emitter appends
        // (SwiftSelf self_, the simple-enum `tag` discriminator, SwiftIndirectResult result, …)
        // can collide with a user-derived parameter spelled the same way — producing CS0100
        // "duplicate parameter name". The standard Parameter-list path already deduplicates
        // upstream (MethodSignature.DeduplicateParameterNames), but the emitters that build
        // raw param strings (cross-module extensions, simple-enum methods, generic bridges)
        // bypass it. Deduping here — the single point every P/Invoke decl flows through —
        // closes the whole category at once. P/Invoke parameter names are positional-only at
        // the call site, so a rename is always output-safe; and because this only rewrites a
        // string that actually contains a duplicate, the common (collision-free) case is byte-
        // identical to before.
        paramsStr = DeduplicateCSharpParamNames(paramsStr);

        // Build modifiers
        var visibility = info.Visibility switch
        {
            PInvokeVisibility.Private => "private",
            PInvokeVisibility.Internal => "internal",
            PInvokeVisibility.Public => "public",
            _ => "private"
        };

        var newModifier = info.HasNewModifier ? "new " : "";
        var unsafeModifier = info.IsUnsafe ? "unsafe " : "";

        lines.Add($"{visibility} static {newModifier}{unsafeModifier}partial {returnTypeStr} {info.MethodName}({paramsStr});");

        return lines;
    }

    /// <summary>
    /// Deduplicates C# parameter names in a P/Invoke parameter-list string, renaming later
    /// duplicates to <c>{name}_{N}</c> (matching the upstream <c>MethodSignature.DeduplicateParameterNames</c>
    /// scheme). Parameter names are positional-only at the P/Invoke call site, so renaming is
    /// always output-safe.
    /// <para>
    /// Defensive by construction: it splits only on top-level commas (tracking <c>&lt;&gt; [] () {}</c>
    /// nesting so generics, attributes, and <c>delegate* unmanaged[...]&lt;...&gt;</c> function pointers
    /// stay intact), and BAILS — returning the input unchanged — if any segment yields no extractable
    /// identifier or if no name actually repeats. The only strings it rewrites are well-formed lists that
    /// contain a genuine duplicate, which are exactly the ones that would otherwise fail to compile.
    /// </para>
    /// </summary>
    internal static string DeduplicateCSharpParamNames(string paramsStr)
    {
        if (string.IsNullOrWhiteSpace(paramsStr))
            return paramsStr;

        var segments = SplitTopLevel(paramsStr);
        if (segments.Count < 2)
            return paramsStr;

        // Extract the trailing identifier (the parameter name) from each segment.
        var names = new string[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            var name = ExtractTrailingIdentifier(segments[i]);
            if (name == null)
                return paramsStr; // unparseable segment — leave the whole list untouched
            names[i] = name;
        }

        // Only rewrite when a name actually repeats.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in names)
        {
            counts.TryGetValue(n, out var c);
            counts[n] = c + 1;
        }
        if (!counts.Values.Any(c => c > 1))
            return paramsStr;

        var allNames = new HashSet<string>(names, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rebuilt = new List<string>(segments.Count);
        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i].Trim();
            var name = names[i];
            if (seen.Add(name))
            {
                rebuilt.Add(segment); // first occurrence keeps its name
                continue;
            }

            var suffix = 1;
            var candidate = $"{name}_{suffix}";
            while (!allNames.Add(candidate))
            {
                suffix++;
                candidate = $"{name}_{suffix}";
            }

            // Replace only the trailing name token (it is the suffix of the trimmed segment).
            rebuilt.Add(string.Concat(segment.AsSpan(0, segment.Length - name.Length), candidate));
        }

        return string.Join(", ", rebuilt);
    }

    /// <summary>
    /// Splits a C# parameter-list string on top-level commas, tracking angle/square/round/curly
    /// bracket nesting so commas inside generic arguments, attributes, or function-pointer
    /// signatures don't split a parameter.
    /// </summary>
    private static List<string> SplitTopLevel(string s)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '<': case '[': case '(': case '{': depth++; break;
                case '>': case ']': case ')': case '}': if (depth > 0) depth--; break;
                case ',':
                    if (depth == 0)
                    {
                        result.Add(s.Substring(start, i - start));
                        start = i + 1;
                    }
                    break;
            }
        }
        result.Add(s.Substring(start));
        return result;
    }

    /// <summary>
    /// Returns the trailing C# identifier of a parameter segment (its name), or null when the
    /// segment has no valid trailing identifier. A leading <c>@</c> verbatim prefix is preserved.
    /// </summary>
    private static string? ExtractTrailingIdentifier(string segment)
    {
        var s = segment.TrimEnd();
        int end = s.Length;
        int i = end;
        while (i > 0)
        {
            char c = s[i - 1];
            if (char.IsLetterOrDigit(c) || c == '_')
                i--;
            else
                break;
        }
        // Allow a verbatim '@' prefix immediately before the identifier run.
        if (i > 0 && s[i - 1] == '@')
            i--;
        if (i >= end)
            return null; // no trailing identifier chars

        var name = s.Substring(i);
        // A valid identifier must start with '@', '_' or a letter — not a digit.
        var first = name[0] == '@' && name.Length > 1 ? name[1] : name[0];
        if (!(char.IsLetter(first) || first == '_'))
            return null;
        return name;
    }
}
