// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// A local, content-addressed cache of C# verification verdicts. It is an <em>economics</em> layer
/// over the verify-recover loop's most expensive stage — the external <c>dotnet build</c> the
/// Roslyn/MSBuild probe runs — and nothing more: when the fingerprint captures every input to the
/// verify verdict, a hit returns the exact verdict a miss would have produced, so the loop's decisions,
/// the settled source, and therefore the published artifacts and the report are byte-identical whether
/// the verdict was recomputed or reused. Because the current key does not yet provably capture every
/// inherited MSBuild input, the cache is opt-in — see <see cref="CreateIfEnabled"/> for the exact gap
/// and why an explicit root is required.
/// </summary>
/// <remarks>
/// <para>
/// Each entry is one JSON file named by its <see cref="VerificationFingerprint"/> digest under a
/// local cache directory that persists across generator runs (this is what lets a second run of the
/// same binding reuse the first run's verdict). Invalidation is by key construction only — the
/// generator's module version id is a fingerprint component, so a rebuilt generator, a changed
/// toolchain, a re-rendered plan, or a different denylist all yield a fresh key and a recompute.
/// There is no time-based expiry and no eviction; the directory is a pure function-memo.
/// </para>
/// <para>
/// The cache is <em>subordinate</em> by construction. It only ever short-circuits the verify-recover
/// <em>probe</em>; the authoritative post-loop publication gate (the real wrapper compile and
/// <c>VerifyGeneratedCSharp</c>) always runs uncached, so a hypothetical bad hit is caught by any
/// <em>conclusive</em> post-loop verdict and fails the build closed. The one residual — reachable only
/// on the opt-in path — is an opted-in stale hit paired with an <em>Inconclusive</em> final verify (a
/// transient infra fault the publication gate treats as non-blocking), so the cache could have removed
/// the last conclusive compile opportunity; that edge is part of what gates default-on (see
/// <c>not-planned.md</c>) and is why the cache is opt-in. Reads and writes are
/// best-effort: a torn or corrupt file (e.g. from a concurrent corpus-matrix run) is treated as a
/// miss, and a write failure just means a future miss. Writes are atomic (temp file then rename).
/// </para>
/// </remarks>
public sealed partial class VerificationCache
{
    /// <summary>Set to any non-empty value to disable the cache entirely (always miss, never store).</summary>
    private const string DisableEnvVar = "SWIFTBINDINGS_NO_VERIFY_CACHE";

    /// <summary>Overrides the cache root directory; defaults to a temp-dir subfolder when unset.</summary>
    private const string RootEnvVar = "SWIFTBINDINGS_VERIFY_CACHE";

    private readonly string _root;
    private readonly ILogger? _logger;

    public VerificationCache(string root, ILogger? logger = null)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _logger = logger;
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// The cache when the operator has opted in by pointing <c>SWIFTBINDINGS_VERIFY_CACHE</c> at a
    /// cache-root directory they control; otherwise <see langword="null"/> (uncached generation).
    /// <para>
    /// Opt-in — not default-on — because the <see cref="VerificationFingerprint"/> keys the emitted
    /// source, the verification csproj, and the ABI/toolchain/generator/denylist inputs, but NOT every
    /// input MSBuild inherits into the verify compile (a parent <c>Directory.Build.props</c>/
    /// <c>.targets</c>, <c>Directory.Packages.props</c>, <c>nuget.config</c>) nor the resolved runtime
    /// package body (the <c>SwiftBindings.Runtime</c> PackageReference version <em>range</em> is in the
    /// hashed csproj, but its resolved contents float across patch releases). Until the key provably
    /// covers those, a shared cache dir could serve a stale verdict across two runs differing only in one
    /// of them — never shipping a broken binding, since the authoritative post-loop publication gate
    /// always re-verifies uncached, but risking an unnecessary API withdrawal that diverges from an
    /// uncached run. Requiring an explicit root confines that to an operator who owns the environment and
    /// the cache lifetime. Completing the key so this can default on is tracked in not-planned.md.
    /// </para>
    /// </summary>
    public static VerificationCache? CreateIfEnabled(ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RootEnvVar)))
            return null;
        return CreateDefault(logger);
    }

    /// <summary>
    /// The backing constructor for <see cref="CreateIfEnabled"/> once opt-in is established: the local
    /// cache, or <see langword="null"/> when disabled via <c>SWIFTBINDINGS_NO_VERIFY_CACHE</c>. The root
    /// is <c>SWIFTBINDINGS_VERIFY_CACHE</c> when set, otherwise a <c>swiftbindings-verify-cache</c> folder
    /// under the OS temp directory. Private because the incomplete-fingerprint reasoning on
    /// <see cref="CreateIfEnabled"/> means the cache must never be constructed without an explicit root —
    /// there is no default-on entry point.
    /// </summary>
    private static VerificationCache? CreateDefault(ILogger? logger = null)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DisableEnvVar)))
        {
            logger?.LogInformation("Verification cache disabled via {Var}.", DisableEnvVar);
            return null;
        }

        var root = Environment.GetEnvironmentVariable(RootEnvVar);
        if (string.IsNullOrEmpty(root))
            root = Path.Combine(Path.GetTempPath(), "swiftbindings-verify-cache");

        try
        {
            return new VerificationCache(root, logger);
        }
        catch (Exception ex)
        {
            // A cache we cannot even create is not fatal — generation proceeds uncached.
            logger?.LogInformation("Verification cache unavailable ({Reason}); proceeding uncached.", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Look up a verdict by fingerprint. Returns <see langword="true"/> and the reconstructed verdict
    /// on a hit; a missing, torn, or unparseable entry is a miss.
    /// </summary>
    public bool TryGet(string fingerprint, out CSharpVerificationResult result)
    {
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);
        result = null!;
        try
        {
            var path = PathFor(fingerprint);
            if (!File.Exists(path))
                return false;

            var entry = JsonSerializer.Deserialize(
                File.ReadAllText(path), VerificationCacheJsonContext.Default.Entry);
            if (entry == null)
                return false;

            result = entry.ToResult();
            return true;
        }
        catch (Exception ex)
        {
            // Corrupt/torn read (e.g. a concurrent writer): treat as a miss, recompute.
            _logger?.LogInformation("Verification cache read miss for {Fp} ({Reason}).", Short(fingerprint), ex.Message);
            return false;
        }
    }

    /// <summary>Store a verdict under its fingerprint. Best-effort and atomic (temp file then rename).</summary>
    public void Store(string fingerprint, CSharpVerificationResult result)
    {
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);
        ArgumentNullException.ThrowIfNull(result);

        // Never persist an Inconclusive verdict. Inconclusive is not a property of the fingerprinted
        // inputs — it is a transient infrastructure fault (a restore failure, a verifier timeout, an IO
        // error) that a re-run over the same inputs may not reproduce. Caching it would make a one-off
        // fault sticky: a later run whose denylist is already non-empty hits the cached Inconclusive and
        // fails the module closed deterministically (the after-a-withdrawal branch of the verify-recover
        // loop), turning a recoverable blip into a permanent reduced/failed binding. A miss simply
        // recomputes, which is exactly what an inconclusive verdict warrants.
        if (result.Outcome == CSharpVerificationOutcome.Inconclusive)
            return;

        try
        {
            var path = PathFor(fingerprint);
            var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                Entry.From(result), VerificationCacheJsonContext.Default.Entry));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // A store failure is harmless — the next lookup simply misses and recomputes.
            _logger?.LogInformation("Verification cache store skipped for {Fp} ({Reason}).", Short(fingerprint), ex.Message);
        }
    }

    private string PathFor(string fingerprint) => Path.Combine(_root, fingerprint + ".json");

    private static string Short(string fingerprint) =>
        fingerprint.Length <= 12 ? fingerprint : fingerprint[..12];

    // Serialization DTOs. Kept separate from the domain records so the on-disk format is an explicit,
    // exact-round-trip contract independent of the domain types' computed properties. Enum values are
    // stored as their integer ordinals so the format is stable against enum-member renames.
    internal sealed class Entry
    {
        [JsonPropertyName("outcome")] public int Outcome { get; set; }
        [JsonPropertyName("diagnostics")] public List<DiagnosticEntry> Diagnostics { get; set; } = new();
        [JsonPropertyName("reason")] public string? InconclusiveReason { get; set; }

        public static Entry From(CSharpVerificationResult result) => new()
        {
            Outcome = (int)result.Outcome,
            Diagnostics = result.Diagnostics.Select(DiagnosticEntry.From).ToList(),
            InconclusiveReason = result.InconclusiveReason,
        };

        public CSharpVerificationResult ToResult() => new(
            (CSharpVerificationOutcome)Outcome,
            Diagnostics.Select(d => d.ToDiagnostic()).ToList(),
            InconclusiveReason);
    }

    internal sealed class DiagnosticEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("severity")] public int Severity { get; set; }
        [JsonPropertyName("file")] public string? FilePath { get; set; }
        [JsonPropertyName("line")] public int Line { get; set; }
        [JsonPropertyName("col")] public int Column { get; set; }
        [JsonPropertyName("endLine")] public int EndLine { get; set; }
        [JsonPropertyName("endCol")] public int EndColumn { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;

        public static DiagnosticEntry From(CSharpCompileDiagnostic d) => new()
        {
            Id = d.Id,
            Severity = (int)d.Severity,
            FilePath = d.FilePath,
            Line = d.Line,
            Column = d.Column,
            EndLine = d.EndLine,
            EndColumn = d.EndColumn,
            Message = d.Message,
        };

        public CSharpCompileDiagnostic ToDiagnostic() => new(
            Id, (CSharpDiagnosticSeverity)Severity, FilePath, Line, Column, EndLine, EndColumn, Message);
    }

    /// <summary>Source-generated serializer context for AOT/trim-safe cache-entry (de)serialization.</summary>
    [JsonSerializable(typeof(Entry))]
    internal partial class VerificationCacheJsonContext : JsonSerializerContext
    {
    }
}
