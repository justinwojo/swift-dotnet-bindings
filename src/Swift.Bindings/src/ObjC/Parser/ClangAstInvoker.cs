// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration.ObjC;

/// <summary>
/// Result of umbrella header resolution.
/// </summary>
/// <summary>
/// The header to hand clang, plus the module identity needed to parse it correctly.
/// <paramref name="ClangModuleName"/> is the module name as declared in the framework's
/// <c>module.modulemap</c> when one could be read, and is what <c>-fmodule-name</c> must be given —
/// the resolver's module name is derived from the xcframework layout and can differ. A mismatched
/// <c>-fmodule-name</c> does not fail; it silently produces the same lossy AST as passing none.
/// <para/>
/// <paramref name="SynthesizedHeaderDirectory"/> is set only when <paramref name="HeaderPath"/> is a
/// header this resolver wrote rather than one shipped by the framework. It is a temp directory the
/// CALLER owns: delete it once the AST dump has run. Null means the header is a real framework file
/// and must not be touched.
/// </summary>
public sealed record UmbrellaHeaderResult(
    string HeaderPath,
    string? ModulemapPath = null,
    string? ClangModuleName = null,
    string? SynthesizedHeaderDirectory = null);

/// <summary>
/// Raised when the clang AST dump exits non-zero. Carries the raw compiler stderr so the pipeline
/// can classify the failure into a specific, user-actionable cause (missing header / module /
/// platform-incompatible header) via <see cref="ObjCClangDiagnostics"/> — instead of surfacing one
/// opaque line — while keeping this invoker a thin clang wrapper that knows nothing about modules.
/// </summary>
public sealed class ClangAstDumpException : InvalidOperationException
{
    public int ExitCode { get; }
    public string Stderr { get; }

    public ClangAstDumpException(int exitCode, string stderr)
        : base($"Clang AST dump failed (exit {exitCode}): {stderr}")
    {
        ExitCode = exitCode;
        Stderr = stderr;
    }
}

/// <summary>
/// Invokes xcrun clang to produce AST JSON from ObjC headers.
/// </summary>
public sealed class ClangAstInvoker
{
    private readonly ICommandRunner _commandRunner;
    private readonly ILogger _logger;

    public ClangAstInvoker(ICommandRunner commandRunner, ILogger logger)
    {
        _commandRunner = commandRunner;
        _logger = logger;
    }

    /// <summary>
    /// Invokes clang to dump the AST of the given header as JSON.
    /// When modulemapPath is provided, -fmodules is enabled (needed for @import strategy).
    /// <paramref name="moduleName"/> is the framework's own module name; it is required for the
    /// <c>-fmodules</c> retry to produce a usable AST (see the retry site below).
    /// </summary>
    public string InvokeClangAstDump(string headerPath, string frameworkSearchPath, bool isSimulator,
        string? modulemapPath = null, IReadOnlyList<string>? additionalFrameworkSearchPaths = null,
        SliceVariant? sliceVariant = null, string? minOSVersion = null, string? moduleName = null)
    {
        string sdkName;
        if (sliceVariant != null)
        {
            sdkName = sliceVariant.SdkName;
        }
        else
        {
            sdkName = isSimulator ? "iphonesimulator" : "iphoneos";
        }
        var (sdkExit, sdkPath, sdkErr) = _commandRunner.Run("xcrun", $"--sdk {sdkName} --show-sdk-path");
        if (sdkExit != 0 || string.IsNullOrWhiteSpace(sdkPath))
        {
            throw new InvalidOperationException(
                $"Failed to locate SDK ({sdkName}). Ensure Xcode and the platform SDK are installed. stderr: {sdkErr}");
        }

        // -fobjc-arc: dump the AST under ARC so an umbrella header that cross-imports an ARC
        // framework (the common case for third-party SDKs) matches its ownership model instead of
        // failing the parse with an ownership mismatch.
        var baseArgs = $"clang -x objective-c -fobjc-arc -Xclang -ast-dump=json " +
                   $"-isysroot \"{sdkPath}\" ";

        // Pin the target triple to the slice's actual Apple platform/arch/variant. Without it clang
        // defaults to the host (macOS) target even under an -isysroot pointing at the iOS SDK, so a
        // header guarded on the *target* platform's predefined macros (e.g. `#if TARGET_OS_IOS`, or a
        // `#if __has_include` whose availability differs per platform) can resolve against the wrong
        // platform and reference symbols the real target never defines. Aligning the triple makes the
        // parse see the same predefines the framework was compiled under. Best-effort: only when the
        // slice variant is known (the version component is near-irrelevant to header parsing, so a
        // conservative platform floor is used when the caller has no concrete deployment target).
        if (sliceVariant != null)
        {
            var triple = sliceVariant.GetTargetTriple(minOSVersion ?? DefaultMinOSVersion(sliceVariant.Platform));
            baseArgs += $"-target {triple} ";
        }

        baseArgs += $"-F \"{frameworkSearchPath}\" ";

        // XCTest (and other test-support frameworks pulled in by libraries like Quick/Nimble)
        // does NOT ship in the SDK — it lives under the platform's Developer/Library/Frameworks.
        // Add that directory so a header doing `@import XCTest;` / `#import <XCTest/XCTest.h>`
        // resolves; without it clang fails with "'XCTest/XCTest.h' file not found" and the whole
        // module fails to build. Best-effort: if the platform-path lookup fails or the directory
        // is absent we just omit the extra -F, leaving non-test-importing libraries unaffected.
        var platformFrameworks = ResolvePlatformDeveloperFrameworks(sdkName);
        if (platformFrameworks != null)
            baseArgs += $"-F \"{platformFrameworks}\" ";

        if (additionalFrameworkSearchPaths != null)
        {
            foreach (var path in additionalFrameworkSearchPaths)
                baseArgs += $"-F \"{path}\" ";
        }

        // Any path that turns -fmodules on must also name the module being built, or the framework's
        // own headers arrive as module imports instead of text and their declarations never reach
        // the AST — the same silent, exit-0 near-empty binding the retry below exists to prevent.
        // No production strategy sets modulemapPath today; the flag pairing lives here so that
        // re-introducing one cannot re-open that hole.
        if (modulemapPath != null)
        {
            baseArgs += $"-fmodules -fmodule-map-file=\"{modulemapPath}\" ";
            if (!string.IsNullOrEmpty(moduleName))
                baseArgs += $"-fmodule-name={moduleName} ";
        }

        var args = baseArgs + $"-fsyntax-only \"{headerPath}\"";

        _logger.LogInformation("Invoking clang AST dump: xcrun {Args}", args);

        var (exitCode, stdout, stderr) = _commandRunner.Run("xcrun", args, timeoutMs: 120000);

        // Retry with -fmodules if a header uses @import without modules enabled.
        //
        // -fmodule-name is not optional here. Under a bare -fmodules the umbrella header IS the
        // module's umbrella, so clang builds the framework as a module and every
        // `#import <Module/Sibling.h>` collapses to an ImportDecl — the sibling declarations never
        // enter the translation unit, and even the umbrella's own NS_ENUM is merged into the module
        // copy and never re-emitted by the JSON dumper. The framework then binds as a near-empty
        // surface with no error, because clang exits 0. Passing -fmodule-name=<Module> tells clang
        // it is *building* that module, so the module's own headers are included textually and the
        // declarations survive; @import of a DIFFERENT module (UIKit, the thing that forced this
        // retry) still resolves normally.
        //
        // The name must match the module declared in the framework's module.modulemap:
        // -fmodule-name=WrongName also exits 0 and reproduces the identical lossy AST, so a
        // mismatch is a silent no-op that looks applied.
        if (exitCode != 0 && modulemapPath == null &&
            stderr != null && stderr.Contains("use of '@import' when modules are disabled"))
        {
            var moduleNameFlag = string.IsNullOrEmpty(moduleName) ? "" : $"-fmodule-name={moduleName} ";
            if (string.IsNullOrEmpty(moduleName))
            {
                _logger.LogWarning(
                    "Retrying with -fmodules but no module name was supplied; declarations from " +
                    "headers belonging to this framework will be dropped from the AST.");
            }
            _logger.LogInformation("Retrying with -fmodules -fmodule-name={ModuleName} (header uses @import)", moduleName);
            args = baseArgs + $"-fmodules {moduleNameFlag}-fsyntax-only \"{headerPath}\"";
            (exitCode, stdout, stderr) = _commandRunner.Run("xcrun", args, timeoutMs: 120000);
        }

        if (exitCode != 0)
        {
            // Carry the raw stderr up so the pipeline can classify the specific cause (missing
            // header / module / platform-incompatible header) and name the offending token.
            throw new ClangAstDumpException(exitCode, stderr ?? string.Empty);
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException(
                "Clang AST dump returned empty output.");
        }

        return stdout;
    }

    /// <summary>
    /// A conservative minimum-OS floor per Apple platform, used to build the clang <c>-target</c>
    /// triple when the caller has no concrete deployment target to thread. The version component of
    /// the triple does not affect header resolution (only the platform/arch/variant does), so a
    /// floor at or below every supported deployment target is safe for parsing.
    /// </summary>
    private static string DefaultMinOSVersion(ApplePlatform platform) => platform switch
    {
        ApplePlatform.macOS => "12.0",
        _ => "15.0",
    };

    /// <summary>
    /// Resolves the platform's <c>Developer/Library/Frameworks</c> directory (where XCTest and
    /// other test-support frameworks live) for the given SDK, or null if it can't be located.
    /// Best-effort by design: a missing platform path simply means the extra <c>-F</c> is omitted.
    /// </summary>
    private string? ResolvePlatformDeveloperFrameworks(string sdkName)
    {
        var (exit, platformPath, _) = _commandRunner.Run("xcrun", $"--sdk {sdkName} --show-sdk-platform-path");
        if (exit != 0 || string.IsNullOrWhiteSpace(platformPath))
        {
            _logger.LogInformation("Could not locate platform path for SDK '{Sdk}'; skipping Developer/Library/Frameworks search path.", sdkName);
            return null;
        }

        var frameworks = Path.Combine(platformPath.Trim(), "Developer", "Library", "Frameworks");
        if (!Directory.Exists(frameworks))
        {
            _logger.LogInformation("Platform frameworks directory '{Path}' not found; skipping.", frameworks);
            return null;
        }

        return frameworks;
    }

    /// <summary>
    /// Finds the umbrella header to pass to clang for the given framework, along with the module name
    /// <c>-fmodule-name</c> must be given. Returns null if no suitable header can be found.
    /// <para/>
    /// The directory-umbrella strategy has no shipped header to point at, so it SYNTHESIZES one that
    /// includes every header the umbrella directory covers and reports the temp directory holding it
    /// in <see cref="UmbrellaHeaderResult.SynthesizedHeaderDirectory"/> for the caller to delete.
    /// </summary>
    public UmbrellaHeaderResult? FindUmbrellaHeader(string frameworkPath, string moduleName)
    {
        var headersDir = Path.Combine(frameworkPath, "Headers");
        var modulesDir = Path.Combine(frameworkPath, "Modules");
        var modulemapPath = Path.Combine(modulesDir, "module.modulemap");

        // The module name clang must be told, read from the modulemap itself rather than inferred
        // from the xcframework layout. -fmodule-name only works when it matches the declared module
        // id exactly, and a mismatch fails silently (exit 0, same lossy AST), so the declared name
        // wins whenever it can be read.
        var declaredModuleName = File.Exists(modulemapPath)
            ? ExtractDeclaredModuleName(File.ReadAllText(modulemapPath), moduleName)
            : null;
        var clangModuleName = declaredModuleName ?? moduleName;
        if (declaredModuleName != null && declaredModuleName != moduleName)
        {
            _logger.LogInformation(
                "Modulemap declares module '{Declared}' but the resolved module name is '{Resolved}'; " +
                "using the declared name for -fmodule-name.", declaredModuleName, moduleName);
        }

        // 1. Convention: Headers/{moduleName}.h
        var conventionHeader = Path.Combine(headersDir, $"{moduleName}.h");
        if (File.Exists(conventionHeader))
        {
            _logger.LogInformation("Found umbrella header by convention: {Path}", conventionHeader);
            return new UmbrellaHeaderResult(conventionHeader, ClangModuleName: clangModuleName);
        }

        // 2-4. Parse modulemap for umbrella header directives
        if (!File.Exists(modulemapPath))
        {
            _logger.LogWarning("No module.modulemap found at {Path}", modulemapPath);
            return null;
        }

        var modulemapContent = File.ReadAllText(modulemapPath);
        var lines = modulemapContent.Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // 2. umbrella header "X.h"
            if (line.StartsWith("umbrella header", StringComparison.Ordinal))
            {
                var headerName = ExtractQuotedString(line);
                if (headerName != null)
                {
                    var path = Path.Combine(headersDir, headerName);
                    if (File.Exists(path))
                    {
                        _logger.LogInformation("Found umbrella header from modulemap directive: {Path}", path);
                        return new UmbrellaHeaderResult(path, ClangModuleName: clangModuleName);
                    }
                }
            }

            // 3. umbrella "Headers" (directory umbrella)
            //
            // Every header in the umbrella directory is public, so synthesize a combined header
            // that imports them all and parse that textually.
            //
            // This used to emit a temp file containing only `@import {Module};` and enable
            // -fmodules via the modulemap. That yields a translation unit whose AST contains
            // nothing but clang's builtin `Protocol` interface: the framework's declarations all
            // live in the precompiled module and the JSON dumper never re-emits them, so a
            // directory-umbrella framework bound as an EMPTY surface with a zero exit code.
            // -fmodule-name does not rescue that shape either (clang accepts a module importing
            // itself, and still emits nothing) — only reading the headers textually does.
            if (line.StartsWith("umbrella \"", StringComparison.Ordinal) &&
                !line.StartsWith("umbrella header", StringComparison.Ordinal))
            {
                var umbrellaDirName = ExtractQuotedString(line);
                var umbrellaDir = umbrellaDirName != null
                    ? Path.Combine(frameworkPath, umbrellaDirName)
                    : headersDir;
                if (!Directory.Exists(umbrellaDir))
                    umbrellaDir = headersDir;

                var excluded = ExtractExcludedHeaderPaths(lines);
                // Recursive: a directory umbrella covers nested subdirectories too. Ordered so the
                // synthesized header is deterministic across runs (clang's own directory walk is
                // filesystem-ordered, which is not).
                //
                // Restricted to `*.h` even though clang's umbrella-directory rule covers every
                // header it recognizes. The synthesized translation unit is compiled as Objective-C,
                // so pulling in a C++ `.hh`/`.hpp` would take the whole AST dump down with a parse
                // error rather than adding surface — a hard failure in place of a narrow gap. ObjC
                // frameworks publish their bindable API in `.h`.
                var umbrellaHeaders = Directory
                    .GetFiles(umbrellaDir, "*.h", SearchOption.AllDirectories)
                    .Where(h => !IsExcludedHeader(h, umbrellaDir, excluded))
                    .OrderBy(h => h, StringComparer.Ordinal)
                    .ToList();

                if (umbrellaHeaders.Count > 0)
                {
                    _logger.LogInformation(
                        "Directory umbrella '{Dir}': combining {Count} headers ({Excluded} excluded by modulemap)",
                        umbrellaDir, umbrellaHeaders.Count, excluded.Count);
                    var combined = CreateCombinedHeaderFile(umbrellaHeaders, moduleName);
                    return new UmbrellaHeaderResult(
                        combined.HeaderPath,
                        ClangModuleName: clangModuleName,
                        SynthesizedHeaderDirectory: combined.Directory);
                }

                _logger.LogWarning("Directory umbrella '{Dir}' contains no headers", umbrellaDir);
                return null;
            }
        }

        // 4. Collect explicit header entries
        var explicitHeaders = new List<string>();
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if ((line.StartsWith("header ", StringComparison.Ordinal) ||
                 line.StartsWith("export header ", StringComparison.Ordinal)) &&
                !line.StartsWith("umbrella", StringComparison.Ordinal))
            {
                var headerName = ExtractQuotedString(line);
                if (headerName != null)
                {
                    var path = Path.Combine(headersDir, headerName);
                    if (File.Exists(path))
                        explicitHeaders.Add(path);
                }
            }
        }

        if (explicitHeaders.Count > 0)
        {
            _logger.LogInformation("Creating combined header from {Count} explicit modulemap entries", explicitHeaders.Count);
            var combinedExplicit = CreateCombinedHeaderFile(explicitHeaders, moduleName);
            return new UmbrellaHeaderResult(
                combinedExplicit.HeaderPath,
                ClangModuleName: clangModuleName,
                SynthesizedHeaderDirectory: combinedExplicit.Directory);
        }

        _logger.LogWarning("Could not locate umbrella header for module '{Module}'", moduleName);
        return null;
    }

    /// <summary>
    /// Reads the module name declared by a modulemap — the <c>X</c> in <c>framework module X {</c>
    /// or <c>module X {</c> — which is the only name <c>-fmodule-name</c> accepts. Returns null if
    /// no declaration can be found.
    /// <para/>
    /// A modulemap may legally declare several top-level modules (a helper or private module beside
    /// the framework's own), so taking the first one is not safe: naming the wrong module exits 0
    /// and reproduces the very lossy AST <c>-fmodule-name</c> exists to prevent. Selection is
    /// therefore, in order: an exact match on <paramref name="preferredModuleName"/> (the name the
    /// xcframework layout resolved to), else the first <c>framework module</c>, else the first
    /// plain <c>module</c>.
    /// <para/>
    /// Only brace-depth-0 declarations are considered, so nested submodules — including the
    /// <c>explicit</c>/<c>extern</c> forms and the <c>module * { … }</c> wildcard — cannot win.
    /// <para/>
    /// The scan is over TOKENS, not lines, because the modulemap grammar is token-based: a newline is
    /// ordinary whitespace, so <c>framework module</c> and <c>Name {</c> may sit on separate lines and
    /// clang still accepts it. Comments and quoted strings are stripped first — a <c>{</c> inside a
    /// comment or a header path would otherwise inflate the brace depth and hide every later top-level
    /// module, which lands us back on the wrong <c>-fmodule-name</c> (exit 0, lossy AST) that this
    /// whole path exists to avoid.
    /// </summary>
    internal static string? ExtractDeclaredModuleName(string modulemapContent, string? preferredModuleName = null)
    {
        string? firstFrameworkModule = null;
        string? firstPlainModule = null;
        var depth = 0;
        var sawFrameworkKeyword = false;
        // A qualified declaration is never this modulemap's own identity. `extern module M "other.map"`
        // is a legal TOP-LEVEL form that merely points at a module defined in another file — naming it
        // would hand -fmodule-name a module this map does not define. `explicit` only ever qualifies a
        // submodule (clang rejects it at top level), so it is rejected here for the same reason.
        var sawSubmoduleQualifier = false;
        var expectingName = false;
        var expectingNameIsFramework = false;

        foreach (var token in TokenizeModulemap(modulemapContent))
        {
            if (token == "{")
            {
                depth++;
                expectingName = false;
                sawFrameworkKeyword = false;
                sawSubmoduleQualifier = false;
                continue;
            }
            if (token == "}")
            {
                if (depth > 0) depth--;
                expectingName = false;
                sawFrameworkKeyword = false;
                sawSubmoduleQualifier = false;
                continue;
            }

            if (expectingName)
            {
                expectingName = false;
                // Module names are C identifiers; anything else (notably the `*` wildcard) is not a name.
                if (IsIdentifier(token))
                {
                    if (preferredModuleName != null && token == preferredModuleName)
                        return token;
                    if (expectingNameIsFramework)
                        firstFrameworkModule ??= token;
                    else
                        firstPlainModule ??= token;
                }
                continue;
            }

            switch (token)
            {
                case "framework":
                    sawFrameworkKeyword = depth == 0;
                    sawSubmoduleQualifier = false;
                    break;
                case "explicit":
                case "extern":
                    sawSubmoduleQualifier = true;
                    break;
                case "module":
                    if (depth == 0 && !sawSubmoduleQualifier)
                    {
                        expectingName = true;
                        expectingNameIsFramework = sawFrameworkKeyword;
                    }
                    sawFrameworkKeyword = false;
                    sawSubmoduleQualifier = false;
                    break;
                default:
                    sawFrameworkKeyword = false;
                    sawSubmoduleQualifier = false;
                    break;
            }
        }

        return firstFrameworkModule ?? firstPlainModule;

        static bool IsIdentifier(string token) =>
            token.Length > 0 && token.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    /// <summary>
    /// Splits modulemap source into the tokens the declaration scan cares about: bare words and the
    /// braces that carry nesting. Line (<c>//</c>) and block (<c>/* … */</c>) comments and
    /// double-quoted strings are dropped entirely, so neither a brace nor the word <c>module</c>
    /// inside one is mistaken for structure. Every other punctuation character is a separator.
    /// </summary>
    internal static IEnumerable<string> TokenizeModulemap(string content)
    {
        var current = new StringBuilder();
        var i = 0;
        while (i < content.Length)
        {
            var c = content[i];

            if (c == '/' && i + 1 < content.Length && content[i + 1] == '/')
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                while (i < content.Length && content[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < content.Length && content[i + 1] == '*')
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                i += 2;
                while (i + 1 < content.Length && !(content[i] == '*' && content[i + 1] == '/')) i++;
                i = i + 1 < content.Length ? i + 2 : content.Length;
                continue;
            }
            if (c == '"')
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                i++;
                while (i < content.Length && content[i] != '"')
                {
                    // A backslash escapes the next character, so an escaped quote does not end the string.
                    if (content[i] == '\\' && i + 1 < content.Length) i++;
                    i++;
                }
                i = i < content.Length ? i + 1 : i;
                continue;
            }
            if (c == '{' || c == '}')
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                yield return c.ToString();
                i++;
                continue;
            }
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                current.Append(c);
                i++;
                continue;
            }

            // Any other character (whitespace, '.', ',', '*', '[', ']', …) ends the current word.
            if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
            i++;
        }

        if (current.Length > 0) yield return current.ToString();
    }

    /// <summary>
    /// Collects the header paths a modulemap explicitly excludes (<c>exclude header "X.h"</c>),
    /// normalized to forward slashes and kept RELATIVE exactly as written. A directory umbrella
    /// covers every header in the directory EXCEPT these, so combining them all indiscriminately
    /// can pull in a header the vendor deliberately kept out of the module — frequently one that
    /// does not compile standalone.
    /// </summary>
    internal static HashSet<string> ExtractExcludedHeaderPaths(IEnumerable<string> modulemapLines)
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in modulemapLines)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("exclude header", StringComparison.Ordinal))
                continue;
            var name = ExtractQuotedString(line);
            if (!string.IsNullOrEmpty(name))
                excluded.Add(NormalizeRelativeHeaderPath(name));
        }
        return excluded;
    }

    /// <summary>
    /// Decides whether an enumerated umbrella header was excluded by the modulemap. Matching is on
    /// the header's path RELATIVE to the umbrella directory, which is how clang resolves an
    /// <c>exclude header</c> entry — not on the bare file name. Matching by name alone excludes
    /// every same-named header in the tree, so <c>exclude header "Internal/Foo.h"</c> would also
    /// drop a public <c>Public/Foo.h</c> and silently lose its declarations.
    /// </summary>
    internal static bool IsExcludedHeader(string headerPath, string umbrellaDir, HashSet<string> excludedRelativePaths)
    {
        if (excludedRelativePaths.Count == 0)
            return false;
        var relative = Path.GetRelativePath(umbrellaDir, headerPath);
        return excludedRelativePaths.Contains(NormalizeRelativeHeaderPath(relative));
    }

    private static string NormalizeRelativeHeaderPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        // Only a leading "./" is noise; a leading '.' otherwise belongs to the file name.
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized;
    }

    /// <summary>
    /// Writes a synthesized header that <c>#import</c>s each of <paramref name="headers"/> by
    /// absolute path, into a fresh temp directory the caller owns and is expected to delete once
    /// the AST dump has run.
    /// </summary>
    private (string HeaderPath, string Directory) CreateCombinedHeaderFile(List<string> headers, string moduleName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"objc_binding_{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, $"{moduleName}_combined.h");
        var imports = string.Join("\n", headers.Select(h => $"#import \"{h}\""));
        File.WriteAllText(tempFile, imports + "\n");
        _logger.LogInformation("Created combined header file from {Count} headers: {Path}", headers.Count, tempFile);
        return (tempFile, tempDir);
    }

    private static string? ExtractQuotedString(string line)
    {
        var firstQuote = line.IndexOf('"');
        if (firstQuote < 0) return null;
        var secondQuote = line.IndexOf('"', firstQuote + 1);
        if (secondQuote < 0) return null;
        return line[(firstQuote + 1)..secondQuote];
    }
}
