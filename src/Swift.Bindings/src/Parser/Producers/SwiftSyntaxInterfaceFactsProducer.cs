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
/// SwiftSyntax provides full fact coverage: MainActor*, the
/// actor isolation cluster, availability cluster, typed throws, type-and-member
/// collection (<see cref="InterfaceFactKind.PublicTypeNames"/>,
/// <see cref="InterfaceFactKind.InternalMemberKeys"/>,
/// <see cref="InterfaceFactKind.PublicMemberNames"/>,
/// <see cref="InterfaceFactKind.MarkerProtocolConformances"/>), enum facts
/// (<see cref="InterfaceFactKind.EnumCaseLabels"/>,
/// <see cref="InterfaceFactKind.EnumCaseRawValues"/>), signature facts
/// (<see cref="InterfaceFactKind.ParameterNames"/>,
/// <see cref="InterfaceFactKind.DefaultParameterValues"/>,
/// <see cref="InterfaceFactKind.AutoclosureParameters"/>,
/// <see cref="InterfaceFactKind.SubscriptLabels"/>,
/// <see cref="InterfaceFactKind.VariadicMembers"/>,
/// <see cref="InterfaceFactKind.ConstLiteralParameters"/>,
/// <see cref="InterfaceFactKind.ClosureParameterAttributes"/>),
/// <see cref="InterfaceFactKind.AsyncAccessorMembers"/>,
/// <see cref="InterfaceFactKind.SpiOnlyConformances"/> (from the sibling
/// <c>.private.swiftinterface</c>), <see cref="InterfaceFactKind.ObjCRuntimeNames"/>,
/// and protocol-level facts
/// (<see cref="InterfaceFactKind.ConventionCProtocols"/>,
/// <see cref="InterfaceFactKind.ConventionCProtocolPositions"/>,
/// <see cref="InterfaceFactKind.HiddenRequirementProtocols"/>).
/// <see cref="ProducerResult.CoveredFacts"/> carries the host binary's declared coverage
/// so the aggregator merges per-fact correctly during the migration window.
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
        // Resolve the default from the single timeout source (env-overridable, raised from the
        // historical 60s) so a direct construction without an explicit timeout gets the same
        // headroom as the production path, not the old hardcoded value.
        _timeout = timeout ?? GeneratorTimeouts.ResolveParserTimeout();
    }

    /// <summary>
    /// Convert a public swiftinterface path to its private companion path
    /// (<c>foo.swiftinterface</c> → <c>foo.private.swiftinterface</c>).
    /// Returns null when the input does not end in <c>.swiftinterface</c> so callers
    /// can treat absence as "no private interface" rather than constructing a bogus
    /// path and probing for it. Idempotent: a path already ending in
    /// <c>.private.swiftinterface</c> is returned unchanged.
    /// </summary>
    internal static string? DerivePrivateSwiftInterfacePath(string swiftInterfacePath)
    {
        const string suffix = ".swiftinterface";
        if (string.IsNullOrEmpty(swiftInterfacePath) ||
            !swiftInterfacePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;

        // Idempotent: if the caller already passed the private path, return it as-is.
        const string privateSuffix = ".private.swiftinterface";
        if (swiftInterfacePath.EndsWith(privateSuffix, StringComparison.OrdinalIgnoreCase))
            return swiftInterfacePath;

        return swiftInterfacePath.Substring(0, swiftInterfacePath.Length - suffix.Length) + privateSuffix;
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
    /// Returns null when no candidate exists. This generator is macOS-only and the host
    /// binary is required to extract interface facts, so callers treat a null here as a
    /// hard error rather than degrading.
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
            // No swiftinterface — no facts to produce. Declare zero coverage so every fact
            // falls back to the empty default in SwiftInterfaceFacts.Empty.
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

        // SPI-only conformances live in the sibling `*.private.swiftinterface`. Derive its
        // path and pass it ONLY when the file actually exists. When absent (most frameworks
        // ship no private interface), the host still declares SpiOnlyConformances coverage
        // with an empty payload, so an absent private interface reads as "no SPI conformances"
        // rather than "fact not covered".
        var privatePath = DerivePrivateSwiftInterfacePath(swiftInterfacePath);
        if (privatePath is not null && File.Exists(privatePath))
        {
            psi.ArgumentList.Add("--private-input");
            psi.ArgumentList.Add(privatePath);
        }

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
            // Block (bounded) until the killed tree is reaped so its pipes/children are torn down
            // before we unwind — otherwise an orphaned swift-frontend can keep consuming a
            // contended runner after we've already reported the timeout.
            try { process.WaitForExit(10000); } catch { /* best effort */ }
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
            ActorIsolatedMembers = covered.Contains(InterfaceFactKind.ActorIsolatedMembers)
                ? new HashSet<string>(parsed.Facts.ActorIsolatedMembers ?? new List<string>())
                : null,
            MainActorIsolatedMembers = covered.Contains(InterfaceFactKind.MainActorIsolatedMembers)
                ? new HashSet<string>(parsed.Facts.MainActorIsolatedMembers ?? new List<string>())
                : null,
            NonisolatedMembers = covered.Contains(InterfaceFactKind.NonisolatedMembers)
                ? new HashSet<string>(parsed.Facts.NonisolatedMembers ?? new List<string>())
                : null,
            CustomActorTypes = covered.Contains(InterfaceFactKind.CustomActorTypes)
                ? new HashSet<string>(parsed.Facts.CustomActorTypes ?? new List<string>())
                : null,
            CustomActorIsolatorMap = covered.Contains(InterfaceFactKind.CustomActorIsolatorMap)
                ? new Dictionary<string, string>(parsed.Facts.CustomActorIsolatorMap ?? new Dictionary<string, string>())
                : null,
            AvailabilityAnnotations = covered.Contains(InterfaceFactKind.AvailabilityAnnotations)
                ? ConvertAvailabilityAnnotations(parsed.Facts.AvailabilityAnnotations)
                : null,
            AvailabilityAnnotationPositions = covered.Contains(InterfaceFactKind.AvailabilityAnnotationPositions)
                ? ConvertPositions(parsed.Facts.AvailabilityAnnotationPositions)
                : null,
            TypedThrowsErrors = covered.Contains(InterfaceFactKind.TypedThrowsErrors)
                ? new Dictionary<string, string>(parsed.Facts.TypedThrowsErrors ?? new Dictionary<string, string>())
                : null,

            // Type & member collection.
            PublicTypeNames = covered.Contains(InterfaceFactKind.PublicTypeNames)
                ? new HashSet<string>(parsed.Facts.PublicTypeNames ?? new List<string>())
                : null,
            InternalMemberKeys = covered.Contains(InterfaceFactKind.InternalMemberKeys)
                ? new HashSet<string>(parsed.Facts.InternalMemberKeys ?? new List<string>())
                : null,
            PublicMemberNames = covered.Contains(InterfaceFactKind.PublicMemberNames)
                ? new HashSet<string>(parsed.Facts.PublicMemberNames ?? new List<string>())
                : null,
            MarkerProtocolConformances = covered.Contains(InterfaceFactKind.MarkerProtocolConformances)
                ? ConvertListDict(parsed.Facts.MarkerProtocolConformances)
                : null,

            // Enum facts.
            EnumCaseLabels = covered.Contains(InterfaceFactKind.EnumCaseLabels)
                ? ConvertListDict(parsed.Facts.EnumCaseLabels)
                : null,
            EnumCaseRawValues = covered.Contains(InterfaceFactKind.EnumCaseRawValues)
                ? new Dictionary<string, string>(parsed.Facts.EnumCaseRawValues ?? new Dictionary<string, string>())
                : null,

            // Signature facts.
            ParameterNames = covered.Contains(InterfaceFactKind.ParameterNames)
                ? ConvertListDict(parsed.Facts.ParameterNames)
                : null,
            DefaultParameterValues = covered.Contains(InterfaceFactKind.DefaultParameterValues)
                ? ConvertListDict(parsed.Facts.DefaultParameterValues)
                : null,
            AutoclosureParameters = covered.Contains(InterfaceFactKind.AutoclosureParameters)
                ? ConvertListDict(parsed.Facts.AutoclosureParameters)
                : null,
            ConstLiteralParameters = covered.Contains(InterfaceFactKind.ConstLiteralParameters)
                ? ConvertListDict(parsed.Facts.ConstLiteralParameters)
                : null,
            ClosureParameterAttributes = covered.Contains(InterfaceFactKind.ClosureParameterAttributes)
                ? ConvertListDict(parsed.Facts.ClosureParameterAttributes)
                : null,
            SubscriptLabels = covered.Contains(InterfaceFactKind.SubscriptLabels)
                ? ConvertListDict(parsed.Facts.SubscriptLabels)
                : null,
            VariadicMembers = covered.Contains(InterfaceFactKind.VariadicMembers)
                ? new HashSet<string>(parsed.Facts.VariadicMembers ?? new List<string>())
                : null,
            AsyncAccessorMembers = covered.Contains(InterfaceFactKind.AsyncAccessorMembers)
                ? new HashSet<string>(parsed.Facts.AsyncAccessorMembers ?? new List<string>())
                : null,
            SpiOnlyConformances = covered.Contains(InterfaceFactKind.SpiOnlyConformances)
                ? new HashSet<string>(parsed.Facts.SpiOnlyConformances ?? new List<string>())
                : null,
            ObjCRuntimeNames = covered.Contains(InterfaceFactKind.ObjCRuntimeNames)
                ? new Dictionary<string, string>(parsed.Facts.ObjCRuntimeNames ?? new Dictionary<string, string>())
                : null,

            // Protocol-level facts.
            ConventionCProtocols = covered.Contains(InterfaceFactKind.ConventionCProtocols)
                ? new HashSet<string>(parsed.Facts.ConventionCProtocols ?? new List<string>())
                : null,
            ConventionCProtocolPositions = covered.Contains(InterfaceFactKind.ConventionCProtocolPositions)
                ? ConvertPositions(parsed.Facts.ConventionCProtocolPositions)
                : null,
            HiddenRequirementProtocols = covered.Contains(InterfaceFactKind.HiddenRequirementProtocols)
                ? ConvertHiddenRequirements(parsed.Facts.HiddenRequirementProtocols)
                : null,

            // Non-fact methods migrated behind the producer abstraction.
            ProtocolNames = covered.Contains(InterfaceFactKind.ProtocolNames)
                ? new HashSet<string>(parsed.Facts.ProtocolNames ?? new List<string>())
                : null,
            ProtocolExtensionMethods = covered.Contains(InterfaceFactKind.ProtocolExtensionMethods)
                ? ConvertProtocolExtensionMethods(parsed.Facts.ProtocolExtensionMethods)
                : null,
            ExtensionMemberCandidates = covered.Contains(InterfaceFactKind.ExtensionMemberCandidates)
                ? ConvertExtensionMemberCandidates(parsed.Facts.ExtensionMemberCandidates)
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

    /// <summary>
    /// Converts a wire-shape `Dictionary&lt;string, List&lt;T&gt;&gt;` to a fresh dictionary
    /// (so the consumer sees a private, non-aliased copy). Used for fact dictionaries
    /// whose values are lists.
    /// </summary>
    private static Dictionary<string, List<T>> ConvertListDict<T>(Dictionary<string, List<T>>? input)
    {
        var result = new Dictionary<string, List<T>>();
        if (input is null) return result;
        foreach (var kv in input)
        {
            result[kv.Key] = new List<T>(kv.Value);
        }
        return result;
    }

    /// <summary>
    /// Converts the wire-shape JSON list-of-strings dictionary into the
    /// `Dictionary&lt;string, HashSet&lt;string&gt;&gt;` shape expected by
    /// <see cref="PartialSwiftInterfaceFacts.HiddenRequirementProtocols"/>.
    /// </summary>
    private static Dictionary<string, HashSet<string>> ConvertHiddenRequirements(Dictionary<string, List<string>>? input)
    {
        var result = new Dictionary<string, HashSet<string>>();
        if (input is null) return result;
        foreach (var kv in input)
        {
            result[kv.Key] = new HashSet<string>(kv.Value);
        }
        return result;
    }

    private static Dictionary<string, List<ProtocolExtensionMethodDecl>> ConvertProtocolExtensionMethods(
        Dictionary<string, List<ProtocolExtensionMethodJson>>? input)
    {
        var result = new Dictionary<string, List<ProtocolExtensionMethodDecl>>();
        if (input is null) return result;
        foreach (var kv in input)
        {
            var list = new List<ProtocolExtensionMethodDecl>(kv.Value.Count);
            foreach (var m in kv.Value)
            {
                list.Add(new ProtocolExtensionMethodDecl
                {
                    ProtocolQualifiedName = kv.Key,
                    MethodName = m.MethodName,
                    RawSignature = m.RawSignature,
                    PrintedName = m.PrintedName,
                    ReturnsSelf = m.ReturnsSelf,
                    IsMainActorIsolated = m.IsMainActorIsolated,
                    IsStatic = m.IsStatic,
                    IsProperty = m.IsProperty,
                    HasSetter = m.HasSetter,
                    IsDeprecated = m.IsDeprecated,
                    IsMutating = m.IsMutating,
                    WhereConstraints = new List<string>(m.WhereConstraints),
                });
            }
            result[kv.Key] = list;
        }
        return result;
    }

    private static List<ExtensionMemberCandidate> ConvertExtensionMemberCandidates(
        List<ExtensionMemberCandidateJson>? input)
    {
        var result = new List<ExtensionMemberCandidate>();
        if (input is null) return result;
        foreach (var c in input)
        {
            result.Add(new ExtensionMemberCandidate
            {
                ExtendedTypeName = c.ExtendedTypeName,
                MethodName = c.MethodName,
                RawSignature = c.RawSignature,
                PrintedName = c.PrintedName,
                ReturnsSelf = c.ReturnsSelf,
                IsMainActorIsolated = c.IsMainActorIsolated,
                IsStatic = c.IsStatic,
                IsProperty = c.IsProperty,
                HasSetter = c.HasSetter,
                IsDeprecated = c.IsDeprecated,
                IsMutating = c.IsMutating,
                WhereConstraints = new List<string>(c.WhereConstraints),
            });
        }
        return result;
    }

    private static Dictionary<string, List<AvailabilityAnnotation>> ConvertAvailabilityAnnotations(
        Dictionary<string, List<AvailabilityAnnotationJson>>? input)
    {
        var result = new Dictionary<string, List<AvailabilityAnnotation>>();
        if (input is null) return result;
        foreach (var kv in input)
        {
            var list = new List<AvailabilityAnnotation>(kv.Value.Count);
            foreach (var a in kv.Value)
            {
                list.Add(new AvailabilityAnnotation(
                    a.Platform,
                    a.IntroducedVersion,
                    a.DeprecatedVersion,
                    a.ObsoletedVersion,
                    a.IsUnconditionallyDeprecated,
                    a.IsUnconditionallyUnavailable,
                    a.Message,
                    a.Renamed));
            }
            result[kv.Key] = list;
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
        if (covered.Contains(InterfaceFactKind.ActorIsolatedMembers) && payload.ActorIsolatedMembers is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared ActorIsolatedMembers coverage but emitted null facts.actorIsolatedMembers.");
        if (covered.Contains(InterfaceFactKind.MainActorIsolatedMembers) && payload.MainActorIsolatedMembers is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared MainActorIsolatedMembers coverage but emitted null facts.mainActorIsolatedMembers.");
        if (covered.Contains(InterfaceFactKind.NonisolatedMembers) && payload.NonisolatedMembers is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared NonisolatedMembers coverage but emitted null facts.nonisolatedMembers.");
        if (covered.Contains(InterfaceFactKind.CustomActorTypes) && payload.CustomActorTypes is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared CustomActorTypes coverage but emitted null facts.customActorTypes.");
        if (covered.Contains(InterfaceFactKind.CustomActorIsolatorMap) && payload.CustomActorIsolatorMap is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared CustomActorIsolatorMap coverage but emitted null facts.customActorIsolatorMap.");
        if (covered.Contains(InterfaceFactKind.AvailabilityAnnotations) && payload.AvailabilityAnnotations is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared AvailabilityAnnotations coverage but emitted null facts.availabilityAnnotations.");
        if (covered.Contains(InterfaceFactKind.AvailabilityAnnotationPositions) && payload.AvailabilityAnnotationPositions is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared AvailabilityAnnotationPositions coverage but emitted null facts.availabilityAnnotationPositions.");
        if (covered.Contains(InterfaceFactKind.TypedThrowsErrors) && payload.TypedThrowsErrors is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared TypedThrowsErrors coverage but emitted null facts.typedThrowsErrors.");

        // Type & member collection.
        if (covered.Contains(InterfaceFactKind.PublicTypeNames) && payload.PublicTypeNames is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared PublicTypeNames coverage but emitted null facts.publicTypeNames.");
        if (covered.Contains(InterfaceFactKind.InternalMemberKeys) && payload.InternalMemberKeys is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared InternalMemberKeys coverage but emitted null facts.internalMemberKeys.");
        if (covered.Contains(InterfaceFactKind.PublicMemberNames) && payload.PublicMemberNames is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared PublicMemberNames coverage but emitted null facts.publicMemberNames.");
        if (covered.Contains(InterfaceFactKind.MarkerProtocolConformances) && payload.MarkerProtocolConformances is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared MarkerProtocolConformances coverage but emitted null facts.markerProtocolConformances.");

        // Enum facts.
        if (covered.Contains(InterfaceFactKind.EnumCaseLabels) && payload.EnumCaseLabels is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared EnumCaseLabels coverage but emitted null facts.enumCaseLabels.");
        if (covered.Contains(InterfaceFactKind.EnumCaseRawValues) && payload.EnumCaseRawValues is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared EnumCaseRawValues coverage but emitted null facts.enumCaseRawValues.");

        // Signature facts.
        if (covered.Contains(InterfaceFactKind.ParameterNames) && payload.ParameterNames is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared ParameterNames coverage but emitted null facts.parameterNames.");
        if (covered.Contains(InterfaceFactKind.DefaultParameterValues) && payload.DefaultParameterValues is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared DefaultParameterValues coverage but emitted null facts.defaultParameterValues.");
        if (covered.Contains(InterfaceFactKind.AutoclosureParameters) && payload.AutoclosureParameters is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared AutoclosureParameters coverage but emitted null facts.autoclosureParameters.");
        if (covered.Contains(InterfaceFactKind.ConstLiteralParameters) && payload.ConstLiteralParameters is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared ConstLiteralParameters coverage but emitted null facts.constLiteralParameters.");
        if (covered.Contains(InterfaceFactKind.ClosureParameterAttributes) && payload.ClosureParameterAttributes is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared ClosureParameterAttributes coverage but emitted null facts.closureParameterAttributes.");
        if (covered.Contains(InterfaceFactKind.SubscriptLabels) && payload.SubscriptLabels is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared SubscriptLabels coverage but emitted null facts.subscriptLabels.");
        if (covered.Contains(InterfaceFactKind.VariadicMembers) && payload.VariadicMembers is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared VariadicMembers coverage but emitted null facts.variadicMembers.");
        if (covered.Contains(InterfaceFactKind.AsyncAccessorMembers) && payload.AsyncAccessorMembers is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared AsyncAccessorMembers coverage but emitted null facts.asyncAccessorMembers.");
        if (covered.Contains(InterfaceFactKind.SpiOnlyConformances) && payload.SpiOnlyConformances is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared SpiOnlyConformances coverage but emitted null facts.spiOnlyConformances.");
        if (covered.Contains(InterfaceFactKind.ObjCRuntimeNames) && payload.ObjCRuntimeNames is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared ObjCRuntimeNames coverage but emitted null facts.objcRuntimeNames.");

        // Protocol-level facts.
        if (covered.Contains(InterfaceFactKind.ConventionCProtocols) && payload.ConventionCProtocols is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared ConventionCProtocols coverage but emitted null facts.conventionCProtocols.");
        if (covered.Contains(InterfaceFactKind.ConventionCProtocolPositions) && payload.ConventionCProtocolPositions is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared ConventionCProtocolPositions coverage but emitted null facts.conventionCProtocolPositions.");
        if (covered.Contains(InterfaceFactKind.HiddenRequirementProtocols) && payload.HiddenRequirementProtocols is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared HiddenRequirementProtocols coverage but emitted null facts.hiddenRequirementProtocols.");

        // Non-fact methods migrated behind the producer abstraction.
        if (covered.Contains(InterfaceFactKind.ProtocolNames) && payload.ProtocolNames is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared ProtocolNames coverage but emitted null facts.protocolNames.");
        if (covered.Contains(InterfaceFactKind.ProtocolExtensionMethods) && payload.ProtocolExtensionMethods is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared ProtocolExtensionMethods coverage but emitted null facts.protocolExtensionMethods.");
        if (covered.Contains(InterfaceFactKind.ExtensionMemberCandidates) && payload.ExtensionMemberCandidates is null)
            throw new InvalidOperationException("SwiftInterfaceParser declared ExtensionMemberCandidates coverage but emitted null facts.extensionMemberCandidates.");
    }

    // Deserialization options live on InterfaceFactsJsonContext (source-generated) — see
    // InterfaceFactsJson.cs. The PropertyNamingPolicy / UnmappedMemberHandling settings on
    // [JsonSourceGenerationOptions] are the authoritative drift signal.
}
