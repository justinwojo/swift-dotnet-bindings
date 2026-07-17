// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace BindingsGeneration.ObjC;

/// <summary>
/// The distinguishable root causes of a failed ObjC header parse, so a caller can report WHY the
/// clang AST dump failed instead of surfacing one opaque "clang failed" line. The distinction the
/// user actually needs is tool-bug vs. packaging-bug: a missing header / missing module / a header
/// that is not valid for the target platform are all upstream packaging problems the generator
/// cannot conjure a fix for, and the message should say so precisely (naming the header / module /
/// identifier) rather than implying the binding tool malfunctioned.
/// </summary>
public enum ObjCClangFailureCause
{
    /// <summary>Cause could not be classified from the compiler output.</summary>
    Unknown,
    /// <summary>An <c>#import</c>/<c>#include</c> names a header not present on any search path.</summary>
    MissingHeader,
    /// <summary>An <c>@import</c> / module import names a module not on any framework search path.</summary>
    MissingModule,
    /// <summary>A header parsed but referenced a symbol not defined for the target platform.</summary>
    PlatformIncompatibleHeader,
}

/// <summary>
/// Structured classification of a clang AST-dump failure: the <see cref="Cause"/>, the specific
/// token that triggered it (the missing header path, module name, or undeclared identifier), and a
/// ready-to-surface, actionable message. Purely derived from the compiler's stderr — no I/O — so it
/// is trivially unit-testable against captured real-world failures.
/// </summary>
public sealed record ObjCClangFailureDiagnosis(
    ObjCClangFailureCause Cause,
    string? OffendingToken,
    string Message);

/// <summary>
/// Classifies <c>clang -ast-dump</c> stderr into a specific, user-actionable diagnosis. The three
/// recognized shapes each map to a distinct upstream-packaging failure mode; anything else is
/// reported verbatim so no signal is lost.
/// </summary>
public static class ObjCClangDiagnostics
{
    // clang: "fatal error: 'CombineCocoa/ObjcDelegateProxy.h' file not found"
    private static readonly Regex MissingHeaderRegex =
        new(@"'(?<header>[^']+\.h)' file not found", RegexOptions.Compiled);

    // clang: "error: use of undeclared identifier '_NSIG'"
    private static readonly Regex UndeclaredIdentifierRegex =
        new(@"use of undeclared identifier '(?<ident>[^']+)'", RegexOptions.Compiled);

    // Two wordings for the same failure: swift's frontend says "no such module 'X'" (a
    // .swiftinterface import), clang's own driver says "module 'X' not found" (an @import). A mixed
    // framework can trip either depending on which layer resolves the import, so recognize both.
    private static readonly Regex MissingModuleRegex =
        new(@"no such module '(?<module>[^']+)'|module '(?<module>[^']+)' not found",
            RegexOptions.Compiled);

    // "/path/to/io_uring.h:245:66: error: ..." — recover the file whose parse produced the error.
    private static readonly Regex ErrorFileRegex =
        new(@"(?<file>[^\s:]+\.h):\d+:\d+:\s+(?:fatal\s+)?error:", RegexOptions.Compiled);

    /// <summary>
    /// Diagnoses a clang AST-dump failure from its combined stderr. Preference order matters: a
    /// "file not found" fatal error aborts the parse before any later cascade, so it is checked
    /// first; a genuine platform-incompatible identifier only surfaces once every header resolved.
    /// </summary>
    public static ObjCClangFailureDiagnosis Classify(string moduleName, string? stderr)
    {
        stderr ??= string.Empty;

        var missingHeader = MissingHeaderRegex.Match(stderr);
        if (missingHeader.Success)
        {
            var header = missingHeader.Groups["header"].Value;
            return new ObjCClangFailureDiagnosis(
                ObjCClangFailureCause.MissingHeader, header,
                $"SWIFTBIND109: the ObjC surface of '{moduleName}' imports header '{header}', which is not " +
                "present in the framework distribution nor on any provided sibling / dependency / nested-" +
                "framework search path. This is an upstream packaging problem — the framework's umbrella " +
                "header references a public header the xcframework does not actually ship (or ships under a " +
                "different framework name). Fix the framework's header layout, or supply the framework that " +
                "defines it via --framework-dependency / by co-locating its .xcframework; the binding " +
                "generator cannot synthesize a header the distribution omits.");
        }

        var missingModule = MissingModuleRegex.Match(stderr);
        if (missingModule.Success)
        {
            var module = missingModule.Groups["module"].Value;
            return new ObjCClangFailureDiagnosis(
                ObjCClangFailureCause.MissingModule, module,
                $"SWIFTBIND109: the ObjC surface of '{moduleName}' imports module '{module}', which could not " +
                "be resolved on any framework search path. Provide that module's .xcframework via " +
                "--framework-dependency, or co-locate it next to this framework so it is auto-detected.");
        }

        var undeclared = UndeclaredIdentifierRegex.Match(stderr);
        if (undeclared.Success)
        {
            var ident = undeclared.Groups["ident"].Value;
            var file = ErrorFileRegex.Match(stderr) is { Success: true } fm
                ? Path.GetFileName(fm.Groups["file"].Value)
                : null;
            var where = file != null ? $"header '{file}'" : "a framework header";
            return new ObjCClangFailureDiagnosis(
                ObjCClangFailureCause.PlatformIncompatibleHeader, ident,
                $"SWIFTBIND109: {where} in '{moduleName}' failed to parse for the target platform — use of " +
                $"undeclared identifier '{ident}'. The header contains declarations that are not valid on " +
                "this Apple platform (commonly a header shipped for a non-Apple platform, e.g. a Linux-only " +
                "include compiled unconditionally). This is an upstream packaging problem — the header should " +
                "be platform-guarded; the binding generator will not force-define missing platform symbols.");
        }

        var generic = FirstErrorLine(stderr);
        return new ObjCClangFailureDiagnosis(
            ObjCClangFailureCause.Unknown, null,
            $"SWIFTBIND109: the ObjC header parse for '{moduleName}' failed. clang: " +
            (generic ?? "(no error detail captured)"));
    }

    private static string? FirstErrorLine(string stderr)
    {
        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Contains("error:", StringComparison.Ordinal))
                return line;
        }
        return string.IsNullOrWhiteSpace(stderr) ? null : stderr.Trim();
    }
}
