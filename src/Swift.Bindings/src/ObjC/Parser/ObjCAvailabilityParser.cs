// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Globalization;

namespace BindingsGeneration.ObjC;

/// <summary>
/// Pure parser for Objective-C availability annotations recovered from header source
/// (Finding 22, recovery option a2).
/// <para/>
/// Clang's <c>-ast-dump=json</c> <c>AvailabilityAttr</c> node carries only
/// <c>{id, kind, range}</c> — the platform / introduced / deprecated / message fields are
/// <em>not</em> serialized. <see cref="ClangAstParser"/> recovers the raw annotation text from the
/// consumer header at the attribute's source offset; this class turns that text into a list of
/// <see cref="ObjCAvailability"/> records.
/// <para/>
/// The input is split into a leading token (the macro name or the bare <c>availability</c>
/// attribute keyword) and the argument list inside its parentheses. Supported shapes:
/// <list type="bullet">
/// <item><c>API_AVAILABLE(ios(13.0), macos(10.15))</c> — per-platform <c>introduced</c>.</item>
/// <item><c>API_UNAVAILABLE(ios, tvos)</c> — per-platform <c>unavailable</c>.</item>
/// <item><c>API_DEPRECATED("msg", ios(13.0, 15.0))</c> — per-platform <c>introduced</c>+<c>deprecated</c> with a message.</item>
/// <item><c>API_DEPRECATED_WITH_REPLACEMENT("repl", ios(13.0, 15.0))</c> — same, replacement string as the message.</item>
/// <item><c>__attribute__((availability(ios, introduced=13.0, deprecated=15.0, obsoleted=16.0, message="…")))</c>
///       — the bare clang attribute keyword form (single platform, key=value clauses).</item>
/// <item><c>NS_AVAILABLE_IOS(13_0)</c> / <c>NS_DEPRECATED_IOS(2_0, 9_0, "msg")</c> and the
///       <c>__IOS_AVAILABLE</c> / <c>__OSX_DEPRECATED</c> families — platform encoded in the macro name.</item>
/// </list>
/// Unknown tokens or unmappable platforms degrade to an empty result rather than throwing, so a
/// project-specific wrapper macro the parser doesn't recognize simply contributes no attribute
/// instead of breaking the whole binding.
/// </summary>
public static class ObjCAvailabilityParser
{
    /// <summary>
    /// Parses one availability annotation into zero or more <see cref="ObjCAvailability"/> records.
    /// </summary>
    /// <param name="token">The leading token: a macro name (<c>API_AVAILABLE</c>, <c>NS_DEPRECATED_IOS</c>,
    /// …) or the bare clang attribute keyword <c>availability</c>.</param>
    /// <param name="args">The raw text inside the token's outermost parentheses.</param>
    public static IReadOnlyList<ObjCAvailability> ParseInvocation(string token, string args)
    {
        if (string.IsNullOrWhiteSpace(token))
            return [];

        token = token.Trim();
        args ??= "";

        // The bare clang attribute keyword: availability(platform, key=value, …)
        if (token == "availability")
            return ParseAttributeKeyword(args);

        // API_* macro family (and their __API_* spellings).
        var apiCore = StripLeadingUnderscores(token);
        if (apiCore.StartsWith("API_UNAVAILABLE", StringComparison.Ordinal))
            return ParseApiUnavailable(args);
        if (apiCore.StartsWith("API_DEPRECATED", StringComparison.Ordinal))
            return ParseApiDeprecated(args);
        if (apiCore.StartsWith("API_AVAILABLE", StringComparison.Ordinal))
            return ParseApiAvailable(args);

        // Combined NS_AVAILABLE / NS_CLASS_AVAILABLE / NS_DEPRECATED / NS_CLASS_DEPRECATED forms:
        // the platform pair is POSITIONAL (macOS first, iOS second), not encoded in the macro name.
        // Must run BEFORE the suffix path below, which would otherwise read the trailing "_AVAILABLE"
        // / "_DEPRECATED" and mis-map "NS" / "NS_CLASS" as the platform (→ dropped, no attribute).
        var combined = ParseCombinedNSMacro(apiCore, args);
        if (combined != null)
            return combined;

        // NS_AVAILABLE_<PLAT> / NS_DEPRECATED_<PLAT> and the __<PLAT>_AVAILABLE / __<PLAT>_DEPRECATED
        // families: platform is encoded in the macro name, versions are positional.
        var named = ParseNamedPlatformMacro(token, args);
        if (named != null)
            return named;

        // Unknown token → degrade to no annotation.
        return [];
    }

    // ──────────────────────────────────────────────
    // API_* macro family
    // ──────────────────────────────────────────────

    private static IReadOnlyList<ObjCAvailability> ParseApiAvailable(string args)
    {
        var result = new List<ObjCAvailability>();
        foreach (var clause in SplitTopLevelArgs(args))
        {
            var (platform, versions) = ParsePlatformClause(clause);
            var dotnet = MapPlatform(platform);
            if (dotnet == null)
                continue;
            result.Add(new ObjCAvailability
            {
                Platform = dotnet,
                IntroducedVersion = versions.Count > 0 ? versions[0] : null
            });
        }
        return result;
    }

    private static IReadOnlyList<ObjCAvailability> ParseApiUnavailable(string args)
    {
        var result = new List<ObjCAvailability>();
        foreach (var clause in SplitTopLevelArgs(args))
        {
            // Bare platform name, or platform(...) defensively.
            var (platform, _) = ParsePlatformClause(clause);
            var dotnet = MapPlatform(platform);
            if (dotnet == null)
                continue;
            result.Add(new ObjCAvailability { Platform = dotnet, IsUnavailable = true });
        }
        return result;
    }

    private static IReadOnlyList<ObjCAvailability> ParseApiDeprecated(string args)
    {
        var parts = SplitTopLevelArgs(args);
        if (parts.Count == 0)
            return [];

        // First argument is the message / replacement string literal.
        string? message = null;
        int start = 0;
        if (parts[0].TrimStart().StartsWith('"'))
        {
            message = StripQuotes(parts[0]);
            start = 1;
        }

        var result = new List<ObjCAvailability>();
        for (int i = start; i < parts.Count; i++)
        {
            var (platform, versions) = ParsePlatformClause(parts[i]);
            var dotnet = MapPlatform(platform);
            if (dotnet == null)
                continue;
            result.Add(new ObjCAvailability
            {
                Platform = dotnet,
                IntroducedVersion = versions.Count > 0 ? versions[0] : null,
                DeprecatedVersion = versions.Count > 1 ? versions[1] : null,
                Message = message
            });
        }
        return result;
    }

    // ──────────────────────────────────────────────
    // Bare __attribute__((availability(...))) keyword
    // ──────────────────────────────────────────────

    private static IReadOnlyList<ObjCAvailability> ParseAttributeKeyword(string args)
    {
        var parts = SplitTopLevelArgs(args);
        if (parts.Count == 0)
            return [];

        var platform = MapPlatform(parts[0].Trim());
        if (platform == null)
            return [];

        string? introduced = null, deprecated = null, obsoleted = null, message = null;
        bool unavailable = false;

        for (int i = 1; i < parts.Count; i++)
        {
            var clause = parts[i].Trim();
            if (clause.Length == 0)
                continue;

            var eq = clause.IndexOf('=');
            if (eq < 0)
            {
                // Bare clause: only "unavailable" is meaningful.
                if (clause.Equals("unavailable", StringComparison.Ordinal))
                    unavailable = true;
                continue;
            }

            var key = clause[..eq].Trim();
            var value = clause[(eq + 1)..].Trim();
            switch (key)
            {
                case "introduced": introduced = NormalizeVersion(value); break;
                case "deprecated": deprecated = NormalizeVersion(value); break;
                case "obsoleted": obsoleted = NormalizeVersion(value); break;
                case "message": message = StripQuotes(value); break;
                case "replacement": message ??= StripQuotes(value); break;
            }
        }

        return
        [
            new ObjCAvailability
            {
                Platform = platform,
                IntroducedVersion = introduced,
                DeprecatedVersion = deprecated,
                ObsoletedVersion = obsoleted,
                IsUnavailable = unavailable,
                Message = message
            }
        ];
    }

    // ──────────────────────────────────────────────
    // Combined NS_AVAILABLE / NS_DEPRECATED (macOS, iOS) positional forms
    // ──────────────────────────────────────────────

    /// <summary>
    /// Parses the combined Foundation availability macros whose platform pair is positional
    /// (macOS first, iOS second) rather than encoded in the macro name:
    /// <list type="bullet">
    /// <item><c>NS_AVAILABLE(_mac, _ios)</c> / <c>NS_CLASS_AVAILABLE(_mac, _ios)</c> — <c>introduced</c> on each.</item>
    /// <item><c>NS_DEPRECATED(_macIntro, _macDep, _iosIntro, _iosDep [, "msg" …])</c> /
    ///       <c>NS_CLASS_DEPRECATED(…)</c> — <c>introduced</c>+<c>deprecated</c> on each, optional trailing message(s).</item>
    /// </list>
    /// Returns null when <paramref name="core"/> is not one of these exact tokens, so the caller
    /// falls through to the suffixed <see cref="ParseNamedPlatformMacro"/> family (which would
    /// otherwise mis-map "NS" / "NS_CLASS" as the platform and drop the annotation). A version
    /// slot that does not normalize (e.g. <c>NA</c>) is skipped via <see cref="NormalizeVersion"/>'s
    /// null result; a platform contributes a record only when it carries at least one version.
    /// </summary>
    private static IReadOnlyList<ObjCAvailability>? ParseCombinedNSMacro(string core, string args)
    {
        bool isDeprecated;
        switch (core)
        {
            case "NS_AVAILABLE":
            case "NS_CLASS_AVAILABLE":
                isDeprecated = false;
                break;
            case "NS_DEPRECATED":
            case "NS_CLASS_DEPRECATED":
                isDeprecated = true;
                break;
            default:
                return null;
        }

        var parts = SplitTopLevelArgs(args);
        if (parts.Count == 0)
            return [];

        var result = new List<ObjCAvailability>();

        if (!isDeprecated)
        {
            // (macOS introduced, iOS introduced) — positional, macOS first.
            var mac = parts.Count > 0 ? NormalizeVersion(parts[0]) : null;
            var ios = parts.Count > 1 ? NormalizeVersion(parts[1]) : null;
            if (mac != null)
                result.Add(new ObjCAvailability { Platform = "macos", IntroducedVersion = mac });
            if (ios != null)
                result.Add(new ObjCAvailability { Platform = "ios", IntroducedVersion = ios });
            return result;
        }

        // (macIntro, macDep, iosIntro, iosDep [, "msg" …]) — positional, optional trailing message.
        var macIntro = parts.Count > 0 ? NormalizeVersion(parts[0]) : null;
        var macDep = parts.Count > 1 ? NormalizeVersion(parts[1]) : null;
        var iosIntro = parts.Count > 2 ? NormalizeVersion(parts[2]) : null;
        var iosDep = parts.Count > 3 ? NormalizeVersion(parts[3]) : null;
        string? message = null;
        for (int i = 4; i < parts.Count; i++)
        {
            if (parts[i].TrimStart().StartsWith('"'))
            {
                message = StripQuotes(parts[i]);
                break;
            }
        }
        if (macIntro != null || macDep != null)
            result.Add(new ObjCAvailability { Platform = "macos", IntroducedVersion = macIntro, DeprecatedVersion = macDep, Message = message });
        if (iosIntro != null || iosDep != null)
            result.Add(new ObjCAvailability { Platform = "ios", IntroducedVersion = iosIntro, DeprecatedVersion = iosDep, Message = message });
        return result;
    }

    // ──────────────────────────────────────────────
    // NS_AVAILABLE_<PLAT> / __<PLAT>_AVAILABLE families
    // ──────────────────────────────────────────────

    private static IReadOnlyList<ObjCAvailability>? ParseNamedPlatformMacro(string token, string args)
    {
        var core = StripLeadingUnderscores(token);
        // Split into "verb" and "platform-suffix" by recognized prefixes/infixes.
        // Forms: NS_AVAILABLE_IOS, NS_DEPRECATED_MAC, IOS_AVAILABLE, OSX_DEPRECATED, TVOS_AVAILABLE…
        bool isDeprecated;
        string platformToken;

        if (core.StartsWith("NS_AVAILABLE_", StringComparison.Ordinal))
        {
            isDeprecated = false;
            platformToken = core["NS_AVAILABLE_".Length..];
        }
        else if (core.StartsWith("NS_DEPRECATED_", StringComparison.Ordinal))
        {
            isDeprecated = true;
            platformToken = core["NS_DEPRECATED_".Length..];
        }
        else if (core.EndsWith("_AVAILABLE", StringComparison.Ordinal))
        {
            isDeprecated = false;
            platformToken = core[..^"_AVAILABLE".Length];
        }
        else if (core.EndsWith("_DEPRECATED", StringComparison.Ordinal))
        {
            isDeprecated = true;
            platformToken = core[..^"_DEPRECATED".Length];
        }
        else
        {
            return null;
        }

        var dotnet = MapPlatform(platformToken);
        if (dotnet == null)
            return null;

        var parts = SplitTopLevelArgs(args);
        if (parts.Count == 0)
            return [];

        if (!isDeprecated)
        {
            var introduced = NormalizeVersion(parts[0]);
            if (introduced == null)
                return [];
            return [new ObjCAvailability { Platform = dotnet, IntroducedVersion = introduced }];
        }

        // Deprecated form: (introduced, deprecated [, "message"])
        var intro = parts.Count > 0 ? NormalizeVersion(parts[0]) : null;
        var dep = parts.Count > 1 ? NormalizeVersion(parts[1]) : null;
        string? message = null;
        for (int i = 2; i < parts.Count; i++)
        {
            if (parts[i].TrimStart().StartsWith('"'))
            {
                message = StripQuotes(parts[i]);
                break;
            }
        }
        return [new ObjCAvailability { Platform = dotnet, IntroducedVersion = intro, DeprecatedVersion = dep, Message = message }];
    }

    // ──────────────────────────────────────────────
    // Shared helpers
    // ──────────────────────────────────────────────

    /// <summary>
    /// Splits an argument list on top-level commas, ignoring commas nested inside parentheses,
    /// brackets, or string literals.
    /// </summary>
    internal static List<string> SplitTopLevelArgs(string args)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(args))
            return result;

        int depth = 0;
        bool inString = false;
        char stringQuote = '"';
        int start = 0;

        for (int i = 0; i < args.Length; i++)
        {
            char c = args[i];

            if (inString)
            {
                if (c == '\\') { i++; continue; } // skip escaped char
                if (c == stringQuote) inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                case '\'':
                    inString = true;
                    stringQuote = c;
                    break;
                case '(':
                case '[':
                case '{':
                    depth++;
                    break;
                case ')':
                case ']':
                case '}':
                    if (depth > 0) depth--;
                    break;
                case ',':
                    if (depth == 0)
                    {
                        result.Add(args[start..i].Trim());
                        start = i + 1;
                    }
                    break;
            }
        }

        var tail = args[start..].Trim();
        if (tail.Length > 0)
            result.Add(tail);

        return result;
    }

    /// <summary>
    /// Parses a single platform clause: <c>ios</c>, <c>ios(13.0)</c>, or <c>ios(13.0, 15.0)</c>.
    /// Returns the (raw, unmapped) platform spelling and its ordered version list.
    /// </summary>
    internal static (string platform, List<string> versions) ParsePlatformClause(string clause)
    {
        clause = clause.Trim();
        var open = clause.IndexOf('(');
        if (open < 0)
            return (clause, new List<string>());

        var platform = clause[..open].Trim();
        var close = clause.LastIndexOf(')');
        var inner = close > open ? clause[(open + 1)..close] : clause[(open + 1)..];

        var versions = new List<string>();
        foreach (var v in SplitTopLevelArgs(inner))
        {
            var norm = NormalizeVersion(v);
            if (norm != null)
                versions.Add(norm);
        }
        return (platform, versions);
    }

    /// <summary>
    /// Maps an Objective-C / clang platform spelling to the .NET runtime-versioning platform string.
    /// Returns null for platforms that have no .NET binding surface (e.g. <c>driverkit</c>,
    /// <c>swift</c>) so the caller can skip them.
    /// </summary>
    internal static string? MapPlatform(string objcPlatform)
    {
        switch (objcPlatform.Trim().ToLowerInvariant())
        {
            case "ios":
            case "iphoneos":
                return "ios";
            case "macos":
            case "macosx":
            case "osx":
            case "mac":
                return "macos";
            case "tvos":
                return "tvos";
            case "watchos":
                return "watchos";
            case "maccatalyst":
            case "uikitformac":
                return "maccatalyst";
            case "visionos":
            case "xros":
                return "visionos";
            default:
                return null;
        }
    }

    /// <summary>
    /// Normalizes a version token: converts the underscore form (<c>13_0</c>) to dotted
    /// (<c>13.0</c>) and trims. Returns null for empty / non-numeric tokens.
    /// </summary>
    internal static string? NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var v = version.Trim();
        // Some macro forms use underscores between version components.
        v = v.Replace('_', '.');

        // Must start with a digit to be a version (guards against stray tokens like "introduced").
        if (v.Length == 0 || !char.IsDigit(v[0]))
            return null;

        // Validate each dotted component is numeric; bail out otherwise.
        foreach (var component in v.Split('.'))
        {
            if (component.Length == 0 || !int.TryParse(component, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return null;
        }
        return v;
    }

    private static string StripQuotes(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            s = s[1..^1];
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    private static string StripLeadingUnderscores(string s)
    {
        int i = 0;
        while (i < s.Length && s[i] == '_')
            i++;
        return s[i..];
    }
}
