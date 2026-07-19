// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Verifies the emitted C# by running the real MSBuild build of the generated csproj and consuming
/// the C# compiler's SARIF (<c>ErrorLog</c>) output. This is the publication gate and the ground
/// truth against which the in-process probe is measured: because it IS the build the consumer runs,
/// its reference set, compiler version, and interop generator are exactly the real ones — the
/// parity the in-process probe cannot reach.
///
/// Restore/build-infrastructure failures (<c>NU####</c>/<c>MSB####</c>) are read from the console
/// and classified separately from C# compiler errors (<c>CS####</c>): only a CS error means "the
/// emitted C# does not compile". A build that fails before the compiler runs is inconclusive — the
/// verifier could not answer the question, and a healthy binding must not be failed for a feed hiccup.
/// </summary>
public static class MsbuildSarifCSharpVerifier
{
    /// <summary>
    /// Build <paramref name="csprojPath"/> and return structured C# diagnostics.
    /// </summary>
    /// <param name="csprojPath">Absolute path to the generated binding csproj.</param>
    /// <param name="runner">Subprocess abstraction (real or fake for tests).</param>
    /// <param name="swiftBindingsRepoRoot">When set, passed as <c>-p:SwiftBindingsRepoRoot=</c> so the
    /// binding resolves the in-tree Swift.Runtime (matching a source-tree consumer build); null lets
    /// the binding resolve a published SwiftBindings.Runtime package.</param>
    /// <param name="dotnetPath">The dotnet host to invoke.</param>
    /// <param name="timeoutMs">Build timeout.</param>
    /// <param name="logger">Optional logger for the raw command.</param>
    public static CSharpVerificationResult Verify(
        string csprojPath,
        ICommandRunner runner,
        string? swiftBindingsRepoRoot = null,
        string dotnetPath = "dotnet",
        int timeoutMs = 300000,
        ILogger? logger = null)
    {
        // Unique per invocation: two concurrent verifications of same-named csprojs (e.g. a parallel
        // corpus matrix, where every module emits a "<Module>.Swift.iOS.csproj") must not share one
        // SARIF path — one build's errors would then be read as the other's, a false pass or false
        // fail. The file is deleted in the finally below so the temp dir does not accumulate one
        // artifact per run.
        var sarifDir = Path.Combine(Path.GetTempPath(), "swiftbindings-csharp-verify");
        Directory.CreateDirectory(sarifDir);
        var sarifPath = Path.Combine(sarifDir,
            $"{Path.GetFileNameWithoutExtension(csprojPath)}.{Guid.NewGuid():N}.sarif");

        var repoRootArg = string.IsNullOrEmpty(swiftBindingsRepoRoot)
            ? string.Empty
            : $" -p:SwiftBindingsRepoRoot=\"{swiftBindingsRepoRoot}\"";

        // TreatWarningsAsErrors=false keeps this build hermetic against the *generator* repo's
        // warnings policy. The gate asks "does the emitted C# compile for a consumer?" — i.e. are
        // there genuine errors — and a real consumer builds the binding outside our tree, where
        // warnings are warnings. A binding generated *inside* this repo would otherwise inherit a
        // parent Directory.Build.props that turns warnings into errors, so a benign workload warning
        // (e.g. the iOS SDK's own nfloat alias tripping CS8981) would false-fail publication. Genuine
        // errors are error-severity regardless of this switch, so the command-line override (which
        // wins over any imported prop) narrows the gate to true compilability without hiding a real
        // break.
        var arguments =
            $"build \"{csprojPath}\"{repoRootArg} -p:TreatWarningsAsErrors=false " +
            $"-p:ErrorLog=\"{sarifPath},version=2.1\" -nologo -clp:NoSummary";

        logger?.LogInformation("C# verification build: {Dotnet} {Args}", dotnetPath, arguments);
        try
        {
            var (exitCode, stdout, stderr) = runner.Run(dotnetPath, arguments, timeoutMs);

            var diagnostics = new List<CSharpCompileDiagnostic>();

            // C# compiler diagnostics (with spans) come from the SARIF the compiler wrote.
            if (File.Exists(sarifPath))
            {
                try
                {
                    diagnostics.AddRange(ParseSarif(File.ReadAllText(sarifPath)));
                }
                catch (Exception ex)
                {
                    logger?.LogWarning("Failed to parse C# verification SARIF: {Message}", ex.Message);
                }
            }

            // Restore/infrastructure diagnostics (NU####/MSB####) never reach the C# compiler's SARIF —
            // they are emitted before or outside csc — so scan the console for them. Also picks up CS
            // errors if the SARIF was not produced (e.g. csc never ran), keeping the classifier honest.
            var seen = new HashSet<(string, int, int, string)>(diagnostics.Select(d => d.OrderKey));
            foreach (var consoleDiag in ParseConsoleDiagnostics(stdout + "\n" + stderr))
            {
                // Prefer the SARIF entry for a CS diagnostic (it has the span); add console-only ids.
                if (seen.Add(consoleDiag.OrderKey))
                    diagnostics.Add(consoleDiag);
            }

            return CSharpVerificationResult.FromDiagnostics(diagnostics, buildSucceeded: exitCode == 0);
        }
        finally
        {
            try { if (File.Exists(sarifPath)) File.Delete(sarifPath); }
            catch { /* temp-file hygiene only; a lingering SARIF never affects correctness */ }
        }
    }

    /// <summary>
    /// Locate the in-tree swift-bindings repo root (the directory containing
    /// <c>src/Swift.Runtime/src/Swift.Runtime.csproj</c>) by climbing from the running generator's
    /// base directory. Returns it so the verification build can pass
    /// <c>-p:SwiftBindingsRepoRoot=</c> and bind against the source-tree Swift.Runtime, matching a
    /// dev/corpus consumer build. Returns null for a published generator with no source tree (the
    /// binding then resolves a published SwiftBindings.Runtime package). AOT/trim-clean — uses
    /// <see cref="AppContext.BaseDirectory"/>, not <c>Assembly.Location</c>.
    /// </summary>
    public static string? TryFindSwiftBindingsRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var marker = Path.Combine(dir.FullName, "src", "Swift.Runtime", "src", "Swift.Runtime.csproj");
            if (File.Exists(marker))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Parse SARIF v2.1 (and tolerate v1.0) into structured diagnostics.
    /// </summary>
    internal static IReadOnlyList<CSharpCompileDiagnostic> ParseSarif(string sarifJson)
    {
        var result = new List<CSharpCompileDiagnostic>();
        using var doc = JsonDocument.Parse(sarifJson);
        if (!doc.RootElement.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var run in runs.EnumerateArray())
        {
            if (!run.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var r in results.EnumerateArray())
            {
                var id = r.TryGetProperty("ruleId", out var ruleId) ? ruleId.GetString() ?? "" : "";
                var severity = ParseSarifLevel(r.TryGetProperty("level", out var level) ? level.GetString() : null);
                var message = ExtractSarifMessage(r);
                var (file, sl, sc, el, ec) = ExtractSarifLocation(r);
                if (string.IsNullOrEmpty(id))
                    continue;
                result.Add(new CSharpCompileDiagnostic(id, severity, file, sl, sc, el, ec, message));
            }
        }
        return result;
    }

    private static string ExtractSarifMessage(JsonElement r)
    {
        if (!r.TryGetProperty("message", out var message))
            return "";
        // v2.1: message is an object with "text"; v1.0: message is a string.
        if (message.ValueKind == JsonValueKind.String)
            return message.GetString() ?? "";
        if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("text", out var text))
            return text.GetString() ?? "";
        return "";
    }

    private static (string? File, int StartLine, int StartCol, int EndLine, int EndCol) ExtractSarifLocation(JsonElement r)
    {
        if (!r.TryGetProperty("locations", out var locations) ||
            locations.ValueKind != JsonValueKind.Array ||
            locations.GetArrayLength() == 0)
            return (null, 0, 0, 0, 0);

        var loc = locations[0];

        // v2.1: locations[0].physicalLocation.{artifactLocation.uri, region}
        if (loc.TryGetProperty("physicalLocation", out var phys))
        {
            string? uri = null;
            if (phys.TryGetProperty("artifactLocation", out var art) &&
                art.TryGetProperty("uri", out var uriEl))
                uri = uriEl.GetString();
            var region = phys.TryGetProperty("region", out var reg) ? reg : default;
            return (NormalizeLocationUri(uri), ReadRegionInt(region, "startLine"), ReadRegionInt(region, "startColumn"),
                    ReadRegionInt(region, "endLine"), ReadRegionInt(region, "endColumn"));
        }

        // v1.0: locations[0].resultFile.{uri, region}
        if (loc.TryGetProperty("resultFile", out var rf))
        {
            var uri = rf.TryGetProperty("uri", out var uriEl) ? uriEl.GetString() : null;
            var region = rf.TryGetProperty("region", out var reg) ? reg : default;
            return (NormalizeLocationUri(uri), ReadRegionInt(region, "startLine"), ReadRegionInt(region, "startColumn"),
                    ReadRegionInt(region, "endLine"), ReadRegionInt(region, "endColumn"));
        }

        return (null, 0, 0, 0, 0);
    }

    /// <summary>
    /// Normalize a SARIF location URI to a local filesystem path. The C# compiler emits absolute
    /// <c>file://</c> URIs in SARIF, while MSBuild's console prints plain local paths for the same
    /// diagnostic; normalizing the URI form to a local path lets the SARIF entry and the console
    /// fallback dedup on one file key instead of double-counting every diagnostic. A relative or
    /// non-file URI is left as-is.
    /// </summary>
    private static string? NormalizeLocationUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
            return uri;
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
            return parsed.LocalPath;
        return uri;
    }

    private static int ReadRegionInt(JsonElement region, string name)
        => region.ValueKind == JsonValueKind.Object && region.TryGetProperty(name, out var v) && v.TryGetInt32(out var i)
            ? i
            : 0;

    private static CSharpDiagnosticSeverity ParseSarifLevel(string? level) => level switch
    {
        "error" => CSharpDiagnosticSeverity.Error,
        "warning" => CSharpDiagnosticSeverity.Warning,
        "note" => CSharpDiagnosticSeverity.Info,
        _ => CSharpDiagnosticSeverity.Warning,
    };

    // MSBuild/dotnet console diagnostic. Three real-world origin shapes precede the "severity code:"
    // token, all handled by the one optional prefix group:
    //   "/path/File.cs(12,5): error CS0246: message [project]"  — compiler diag with a line/col span
    //   "/path/Foo.csproj : error NU1101: message [project]"     — project-qualified restore diag (no span)
    //   "error MSB3644: message"                                 — no origin at all
    // The prefix captures the file (and, only for the paren form, the 1-based line/col); restore ids
    // (NU/MSB) carry no span. The invariant token is "<severity> <id>:" with severity ∈ error|warning
    // and id like CS0246 / NU1101 / MSB3644, which is what actually classifies the diagnostic.
    private static readonly Regex ConsoleDiagnostic = new(
        @"(?:^|\n)[^\S\n]*" +
        @"(?:(?<file>[^\n(:]+?)(?:\((?<line>\d+),(?<col>\d+)\))?[^\S\n]*:[^\S\n]+)?" +
        @"(?<sev>error|warning)[^\S\n]+(?<id>[A-Za-z]{2,}\d+)[^\S\n]*:[^\S\n]*(?<msg>[^\n]*)",
        RegexOptions.Compiled);

    /// <summary>
    /// Extract diagnostics from MSBuild/dotnet console output. Used for restore/infrastructure ids
    /// (NU/MSB) and as a fallback when the compiler never wrote SARIF.
    /// </summary>
    internal static IReadOnlyList<CSharpCompileDiagnostic> ParseConsoleDiagnostics(string console)
    {
        var result = new List<CSharpCompileDiagnostic>();
        foreach (Match m in ConsoleDiagnostic.Matches(console))
        {
            var id = m.Groups["id"].Value;
            var sev = m.Groups["sev"].Value == "error"
                ? CSharpDiagnosticSeverity.Error
                : CSharpDiagnosticSeverity.Warning;
            var msg = m.Groups["msg"].Value.Trim();

            string? file = m.Groups["file"].Success ? m.Groups["file"].Value.Trim() : null;
            int line = 0, col = 0;
            if (m.Groups["line"].Success)
            {
                int.TryParse(m.Groups["line"].Value, out line);
                int.TryParse(m.Groups["col"].Value, out col);
            }

            result.Add(new CSharpCompileDiagnostic(id, sev, file, line, col, line, col, msg));
        }
        return result;
    }
}
