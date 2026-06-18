// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Single source of truth for the Swift symbol-mangling fragments the generator assumes when it
/// reasons about a symbol by string surgery rather than by demangling.
///
/// Finding 60 (architecture review §2009–2022): the suffix/prefix grammar of Swift's stable
/// mangling was duplicated as bare string literals across the parser and emitters — <c>method.MangledName
/// + "Tq"</c> here, <c>+ "Tu"</c>/<c>"TjTu"</c> there, <c>StartsWith("$ss")</c> elsewhere — so a
/// toolchain grammar change had no single audit point. This module owns every assumed fragment as a
/// named, documented constant and exposes the small helpers that previously inlined the concatenation,
/// so a future mangling change is a one-file review. <see cref="ManglingProbesTests"/> pins both the
/// literal value and the documented meaning of each constant.
///
/// This is the *interim* home Finding 60 asks for. It deliberately does NOT revive the in-tree
/// demangler (<c>Swift5Demangler</c>); routing these string probes through real demangle reductions is
/// Finding 17's destination, kept separate on purpose.
/// </summary>
internal static class ManglingProbes
{
    // --- Suffix grammar (appended to a base symbol's mangled name) ---

    /// <summary>
    /// Protocol method descriptor suffix. A required protocol method whose <c>{base}Tq</c> symbol is
    /// absent from the TBD has no emitted method descriptor, so EveryProtocol conformance is skipped.
    /// </summary>
    internal const string MethodDescriptorSuffix = "Tq";

    /// <summary>Async function pointer suffix. Present in the TBD for an <c>async</c> entry point.</summary>
    internal const string AsyncFunctionSuffix = "Tu";

    /// <summary>Class dispatch thunk suffix (non-final class instance members dispatch through a thunk).</summary>
    internal const string DispatchThunkSuffix = "Tj";

    /// <summary>
    /// Dispatch-thunk + async suffix. A class property's async accessor is exported through its dispatch
    /// thunk, so its async marker appears as <c>{base}TjTu</c> rather than a bare <c>{base}Tu</c>.
    /// </summary>
    internal const string AsyncDispatchThunkSuffix = DispatchThunkSuffix + AsyncFunctionSuffix;

    // --- Prefix grammar (a mangled name begins with one of these) ---

    /// <summary>Stable mangling prefix (Swift 5+ ABI-stable symbols).</summary>
    internal const string StablePrefix = "$s";

    /// <summary>Stable mangling prefix with a leading underscore (e.g. <c>@_originallyDefinedIn</c> symbols).</summary>
    internal const string StablePrefixUnderscored = "_" + StablePrefix;

    /// <summary>Standard-library substitution prefix (<c>$ss…</c> = a symbol rooted in the Swift module).</summary>
    internal const string StdlibPrefix = StablePrefix + "s";

    /// <summary>
    /// True when the TBD carries the <c>{mangledName}Tq</c> method-descriptor symbol for a protocol
    /// requirement (i.e. the protocol method has an emitted descriptor).
    /// </summary>
    internal static bool HasMethodDescriptor(ISet<string> tbdSymbols, string mangledName) =>
        tbdSymbols.Contains(mangledName + MethodDescriptorSuffix);

    /// <summary>
    /// True when the TBD marks the accessor's mangled name as <c>async</c> — either as a bare
    /// <c>{mangledName}Tu</c> (free/struct accessor) or as <c>{mangledName}TjTu</c> (class accessor
    /// exported through its dispatch thunk).
    /// </summary>
    internal static bool IsAsyncAccessor(ISet<string> tbdSymbols, string mangledName) =>
        tbdSymbols.Contains(mangledName + AsyncFunctionSuffix)
        || tbdSymbols.Contains(mangledName + AsyncDispatchThunkSuffix);

    /// <summary>
    /// The mangled-name prefix that encodes membership in <paramref name="module"/>:
    /// <c>$s{length}{moduleName}</c>. A stable Swift symbol rooted in a module begins with this.
    /// </summary>
    internal static string ModulePrefix(string module) => $"{StablePrefix}{module.Length}{module}";

    /// <summary>True when the mangled name is a standard-library substitution symbol (<c>$ss…</c>).</summary>
    internal static bool IsStdlibMangledName(string? mangledName) =>
        !string.IsNullOrEmpty(mangledName) && mangledName.StartsWith(StdlibPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Extracts the originating module from a length-prefixed stable mangled name.
    ///
    /// Unlike the USR (which records the CURRENT module), the stable mangled name carries the ORIGINAL
    /// module of an <c>@_originallyDefinedIn</c> type — which is what the TBD's protocol-conformance-
    /// descriptor symbols are mangled with (e.g. RealityKit's AnchorEntity re-exported as
    /// RealityFoundation.AnchorEntity still mangles as <c>$s10RealityKit12AnchorEntityC…Mc</c>).
    /// Returns false for stdlib substitutions (<c>$ss…</c>, <c>$sSH</c>) that carry no length prefix,
    /// for non-Swift mangled names, and for truncated/over-running length prefixes.
    /// </summary>
    internal static bool TryGetModuleFromMangledName(string? mangled, [NotNullWhen(true)] out string? module)
    {
        module = null;
        if (string.IsNullOrEmpty(mangled))
            return false;
        int i;
        if (mangled.StartsWith(StablePrefixUnderscored, StringComparison.Ordinal)) i = StablePrefixUnderscored.Length;
        else if (mangled.StartsWith(StablePrefix, StringComparison.Ordinal)) i = StablePrefix.Length;
        else return false;
        int digitStart = i;
        while (i < mangled.Length && char.IsDigit(mangled[i])) i++;
        if (i == digitStart) // stdlib substitution (e.g. "$ss8...", "$sSH") — no length prefix
            return false;
        if (!int.TryParse(mangled.AsSpan(digitStart, i - digitStart), out int len) || len <= 0)
            return false;
        if (i + len > mangled.Length)
            return false;
        module = mangled.Substring(i, len);
        return true;
    }
}
