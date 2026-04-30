// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration.Producers;

/// <summary>
/// SwiftSyntax-backed producer. Shells out to the SwiftInterfaceParser host binary
/// (tools/SwiftInterfaceParser, built by `nuke compile`) with `--input &lt;path&gt;`,
/// reads JSON from stdout, and converts it into a <see cref="PartialSwiftInterfaceFacts"/>.
/// <para/>
/// Session 1 covers <see cref="InterfaceFactKind.MainActorTypes"/> +
/// <see cref="InterfaceFactKind.MainActorTypePositions"/> only. Subsequent sessions extend
/// coverage; <see cref="ProducerResult.CoveredFacts"/> carries the host binary's declared
/// coverage so the aggregator merges per-fact correctly during the migration window.
/// <para/>
/// FAILURE BEHAVIOR: any of (binary missing, non-zero exit, malformed JSON, schema mismatch,
/// unknown fact name in coveredFacts) is a HARD ERROR — surfaced as
/// <see cref="InvalidOperationException"/> with the original stderr included. The audit
/// flagged silent fallback as a drift risk; we'd rather fail visibly than emit half-correct
/// bindings.
/// </summary>
public sealed class SwiftSyntaxInterfaceFactsProducer : IInterfaceFactsProducer
{
    public string Name => "swift-syntax";

    private readonly string _binaryPath;
    private readonly TimeSpan _timeout;

    public SwiftSyntaxInterfaceFactsProducer(string binaryPath, TimeSpan? timeout = null)
    {
        _binaryPath = binaryPath ?? throw new ArgumentNullException(nameof(binaryPath));
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>
    /// Locate the SwiftInterfaceParser binary. Probe order:
    /// <list type="number">
    /// <item><c>SWIFT_INTERFACE_PARSER_PATH</c> environment variable (tests / overrides).</item>
    /// <item><c>&lt;assembly-dir&gt;/swift-interface-parser/SwiftInterfaceParser</c> — flat layout.</item>
    /// <item><c>&lt;assembly-dir&gt;/../../swift-interface-parser/SwiftInterfaceParser</c> — NuGet
    ///   layout (tools/net10.0/any/Swift.Bindings.dll → tools/swift-interface-parser/SwiftInterfaceParser).</item>
    /// <item>Walk up to the repo root and try <c>src/Swift.Bindings.Sdk/tools/swift-interface-parser/SwiftInterfaceParser</c>
    ///   (dev-mode fallback when running from <c>src/Swift.Bindings/src/bin/.../</c>).</item>
    /// </list>
    /// Returns null when no candidate exists. Callers (CLI handler) decide whether that's
    /// fatal — the regex producer has no equivalent dependency, so this only matters when
    /// SwiftSyntax is selected.
    /// </summary>
    public static string? TryLocateBinary()
    {
        var fromEnv = Environment.GetEnvironmentVariable("SWIFT_INTERFACE_PARSER_PATH");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "swift-interface-parser", "SwiftInterfaceParser"),
            Path.Combine(baseDir, "..", "..", "swift-interface-parser", "SwiftInterfaceParser"),
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full))
                return full;
        }

        // Dev fallback: walk up looking for the repo's Sdk staging dir.
        var dir = baseDir;
        for (int depth = 0; depth < 10 && !string.IsNullOrEmpty(dir); depth++)
        {
            var candidate = Path.Combine(dir, "src", "Swift.Bindings.Sdk", "tools", "swift-interface-parser", "SwiftInterfaceParser");
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    public ProducerResult Produce(string swiftInterfacePath, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(swiftInterfacePath) || !File.Exists(swiftInterfacePath))
        {
            // No swiftinterface — no facts to produce. Declare zero coverage so the aggregator
            // falls through to whatever covers each fact next (regex or, eventually, nothing).
            return new ProducerResult(PartialSwiftInterfaceFacts.Empty, new HashSet<InterfaceFactKind>());
        }

        if (!File.Exists(_binaryPath))
        {
            throw new InvalidOperationException(
                $"SwiftSyntaxInterfaceFactsProducer: binary not found at '{_binaryPath}'. " +
                "Run `nuke compile` (Darwin only) to build tools/SwiftInterfaceParser, " +
                "or set SWIFT_INTERFACE_PARSER_PATH.");
        }

        var psi = new ProcessStartInfo(_binaryPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--input");
        psi.ArgumentList.Add(swiftInterfacePath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start SwiftInterfaceParser at '{_binaryPath}'.");

        // Drain stdout/stderr asynchronously while the child runs. If we waited for exit
        // first and then read the pipes, a child that emitted enough JSON or stderr to
        // fill the OS pipe buffer would block on its own write — and we'd report a false
        // timeout. The pipes are bounded (~64K on macOS); SwiftInterfaceParser routinely
        // emits more than that for large interfaces.
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuilder.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)_timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new InvalidOperationException(
                $"SwiftInterfaceParser timed out after {_timeout.TotalSeconds}s on '{swiftInterfacePath}'.");
        }
        // Block until the async readers have finished consuming the pipe contents — without
        // this, ReadToEnd-equivalent semantics aren't guaranteed and we can race the last
        // few bytes of output.
        process.WaitForExit();

        var stdout = stdoutBuilder.ToString();
        var stderr = stderrBuilder.ToString();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"SwiftInterfaceParser exited {process.ExitCode} on '{swiftInterfacePath}'.\n" +
                $"stderr:\n{stderr}");
        }

        InterfaceFactsJson? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(stdout, InterfaceFactsJsonContext.Default.InterfaceFactsJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"SwiftInterfaceParser produced invalid JSON for '{swiftInterfacePath}': {ex.Message}\n" +
                $"stdout:\n{stdout}", ex);
        }
        if (parsed is null)
        {
            throw new InvalidOperationException(
                $"SwiftInterfaceParser produced null JSON for '{swiftInterfacePath}'. stdout:\n{stdout}");
        }

        if (parsed.SchemaVersion != InterfaceFactsJson.ExpectedSchemaVersion)
        {
            throw new InvalidOperationException(
                $"SwiftInterfaceParser schema version mismatch: producer reports {parsed.SchemaVersion}, " +
                $"expected {InterfaceFactsJson.ExpectedSchemaVersion}. Rebuild the host binary " +
                "(`nuke compile`) or update the .NET deserializer in lockstep.");
        }

        // Convert coveredFacts strings → InterfaceFactKind. Unknown names are a hard error
        // (drift signal). We deliberately do NOT silently drop unrecognized coverage entries.
        var covered = new HashSet<InterfaceFactKind>();
        foreach (var name in parsed.CoveredFacts)
        {
            if (!Enum.TryParse<InterfaceFactKind>(name, out var kind))
            {
                throw new InvalidOperationException(
                    $"SwiftInterfaceParser declared coverage of unknown fact '{name}'. " +
                    "Likely a host-binary / .NET-deserializer skew — rebuild both.");
            }
            covered.Add(kind);
        }

        var partial = new PartialSwiftInterfaceFacts
        {
            MainActorTypes = covered.Contains(InterfaceFactKind.MainActorTypes)
                ? new HashSet<string>(parsed.Facts.MainActorTypes ?? new List<string>())
                : null,
            MainActorTypePositions = covered.Contains(InterfaceFactKind.MainActorTypePositions)
                ? ConvertPositions(parsed.Facts.MainActorTypePositions)
                : null,
        };

        // Defense-in-depth: if a producer claims coverage but ships null payload, that's a
        // bug worth surfacing. Empty payload + claimed coverage is fine (= "I covered it,
        // found nothing"); null + claimed coverage is incoherent.
        ValidateCoverageAgainstPayload(covered, parsed.Facts);

        if (parsed.Facts.MainActorTypes is { } types && types.Count > 0)
            logger.LogInformation("SwiftInterfaceParser found {Count} @MainActor types", types.Count);

        return new ProducerResult(partial, covered);
    }

    private static Dictionary<string, SourcePosition> ConvertPositions(Dictionary<string, SourcePositionJson>? input)
    {
        var result = new Dictionary<string, SourcePosition>();
        if (input is null) return result;
        foreach (var kv in input)
        {
            result[kv.Key] = new SourcePosition(kv.Value.FilePath, kv.Value.Line, kv.Value.Column);
        }
        return result;
    }

    private static void ValidateCoverageAgainstPayload(HashSet<InterfaceFactKind> covered, InterfaceFactsJsonPayload payload)
    {
        // Each covered fact MUST have a non-null payload entry. Empty is fine, null is a bug.
        if (covered.Contains(InterfaceFactKind.MainActorTypes) && payload.MainActorTypes is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared MainActorTypes coverage but emitted null facts.mainActorTypes.");
        if (covered.Contains(InterfaceFactKind.MainActorTypePositions) && payload.MainActorTypePositions is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared MainActorTypePositions coverage but emitted null facts.mainActorTypePositions.");
    }

    // Deserialization options live on InterfaceFactsJsonContext (source-generated) — see
    // InterfaceFactsJson.cs. The PropertyNamingPolicy / UnmappedMemberHandling settings on
    // [JsonSourceGenerationOptions] are the authoritative drift signal.
}
