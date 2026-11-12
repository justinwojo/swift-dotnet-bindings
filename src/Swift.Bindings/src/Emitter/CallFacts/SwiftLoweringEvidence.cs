// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BindingsGeneration;

/// <summary>
/// A file of swiftc lowering receipts: what swiftc gave each bridge target of an artifact, per
/// compiler and per slice, with the provenance of the compile that showed it. A plan's prediction is
/// a model; a receipt is the compiler's answer for one artifact. Whether that answer makes a plan
/// executable is decided by a <see cref="SwiftLoweringJudgement"/>: <see cref="SwiftLoweringAuthorization"/>
/// for an artifact built by one compiler, <see cref="SwiftLoweringQualification"/> for the SDK's own
/// artifacts and its qualification corpus, which must hold under every qualified compiler family.
/// </summary>
/// <remarks>
/// The same format serves the SDK's committed qualification record (one receipt per artifact and
/// compiler family) and a build's own receipt (one artifact, the compiler that built it). A receipt
/// names the slices it was collected for; the artifact's claimed slices are the judgement's, never
/// the file's.
/// </remarks>
public sealed record SwiftLoweringEvidence
{
    public const int CurrentSchema = 2;

    public required int Schema { get; init; }

    /// <summary>One receipt per artifact and compiler, in (artifact, compiler) order.</summary>
    public required ImmutableArray<SwiftLoweringReceipt> Receipts { get; init; }

    public static SwiftLoweringEvidence Empty => new() { Schema = CurrentSchema, Receipts = [] };

    /// <summary>The reason a closure's plan is never executable on evidence: swiftc defines no function under a closure's pointer.</summary>
    public const string ClosureReason = "a closure invocation has no swiftc definition to compare: its plan has no evidence";

    public static SwiftLoweringEvidence Load(string path) => Parse(File.ReadAllText(path), path);

    /// <summary>Reads receipts from <paramref name="json"/>; <paramref name="origin"/> names it in errors.</summary>
    public static SwiftLoweringEvidence Parse(string json, string origin)
    {
        // The schema is read first, so a file of another schema is reported as such rather than as
        // whatever the current shape fails to bind from it.
        using (var document = JsonDocument.Parse(json))
        {
            var schema = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : -1;
            if (schema != CurrentSchema)
                throw new InvalidDataException($"{origin} has lowering-evidence schema {schema}; this generator reads {CurrentSchema} (rerun swift-lowering-oracle, or recollect the receipt)");
        }
        var evidence = JsonSerializer.Deserialize(json, SwiftLoweringEvidenceJsonContext.Default.SwiftLoweringEvidence)
            ?? throw new InvalidDataException($"{origin} holds no lowering evidence");
        if (evidence.Receipts.IsDefault)
            throw new InvalidDataException($"{origin} lists no receipts");
        var seen = new HashSet<(string, string)>();
        foreach (var receipt in evidence.Receipts)
        {
            if (string.IsNullOrWhiteSpace(receipt.Artifact) || string.IsNullOrWhiteSpace(receipt.Compiler) || receipt.Contract is null)
                throw new InvalidDataException($"{origin}: a receipt names no artifact, compiler or contract");
            if (!seen.Add((receipt.Artifact, receipt.Compiler)))
                throw new InvalidDataException($"{origin}: two receipts of {receipt.Artifact} under {receipt.Compiler}");
            if (receipt.Slices is null || receipt.Slices.Count == 0)
                throw new InvalidDataException($"{origin}: the receipt of {receipt.Artifact} under {receipt.Compiler} covers no slice");
            foreach (var (triple, slice) in receipt.Slices)
            {
                if (string.IsNullOrWhiteSpace(slice.Sdk) || slice.Sources.IsDefault || slice.Symbols is null)
                    throw new InvalidDataException($"{origin}: {receipt.Artifact} on {triple} under {receipt.Compiler} records no SDK, sources or symbols");
                if (slice.Sources.Any(source => source.Flags.IsDefault || string.IsNullOrWhiteSpace(source.Digest)))
                    throw new InvalidDataException($"{origin}: {receipt.Artifact} on {triple} under {receipt.Compiler} records a source with no digest or flags");
                if (slice.Sources.GroupBy(source => (source.Role, source.Name)).FirstOrDefault(g => g.Count() > 1) is { } twice)
                    throw new InvalidDataException($"{origin}: {receipt.Artifact} on {triple} under {receipt.Compiler} records the {twice.Key.Role} {twice.Key.Name} twice");
            }
        }
        return evidence;
    }

    /// <summary>The evidence as JSON, every receipt, slice and symbol in ordinal order whatever comparer it was read with.</summary>
    public string ToJson() => JsonSerializer.Serialize(this with
    {
        Receipts = Receipts
            .OrderBy(r => r.Artifact, StringComparer.Ordinal)
            .ThenBy(r => r.Compiler, StringComparer.Ordinal)
            .Select(r => r with
            {
                Slices = r.Slices.ToImmutableSortedDictionary(p => p.Key, p => p.Value.Canonical(), StringComparer.Ordinal),
            })
            .ToImmutableArray(),
    }, SwiftLoweringEvidenceJsonContext.Default.SwiftLoweringEvidence) + "\n";

    /// <summary>Every receipt of <paramref name="artifact"/>.</summary>
    public IEnumerable<SwiftLoweringReceipt> ReceiptsOf(string artifact) => Receipts.Where(r => r.Artifact == artifact);

    /// <summary>
    /// This evidence with <paramref name="artifact"/>'s evidence on <paramref name="triple"/> under
    /// <paramref name="compiler"/> replaced, the way a collector records one slice at a time. A
    /// receipt is kept per artifact and compiler family: recording under another version of the same
    /// family replaces that family's receipt, and recording under another contract drops the
    /// receipt's other slices, which were collected under the old one.
    /// </summary>
    public SwiftLoweringEvidence WithSlice(
        string artifact, string compiler, SwiftLoweringContract contract, string? generator, string triple, SwiftLoweringSlice slice)
    {
        var family = SwiftCompilerIdentity.Family(compiler);
        var existing = Receipts.FirstOrDefault(r => r.Artifact == artifact && r.Compiler == compiler && r.Contract == contract);
        var slices = (existing?.Slices ?? ImmutableSortedDictionary<string, SwiftLoweringSlice>.Empty.WithComparers(StringComparer.Ordinal))
            .SetItem(triple, slice.Canonical());
        var receipt = new SwiftLoweringReceipt
        {
            Artifact = artifact,
            Compiler = compiler,
            Contract = contract,
            Generator = generator,
            Slices = slices,
        };
        var kept = Receipts.Where(r => r.Artifact != artifact
            || (r.Compiler != compiler && (family is null || SwiftCompilerIdentity.Family(r.Compiler) != family)));
        return this with
        {
            Receipts = kept.Append(receipt)
                .OrderBy(r => r.Artifact, StringComparer.Ordinal)
                .ThenBy(r => r.Compiler, StringComparer.Ordinal)
                .ToImmutableArray(),
        };
    }
}

/// <summary>
/// The evidence of one artifact under one compiler: per slice (target triple), the symbols swiftc
/// showed and the compile that showed them.
/// </summary>
/// <param name="Artifact">The module whose plans the receipt answers: a binding's module, or a hand-written table's assembly.</param>
/// <param name="Compiler"><c>swiftc --version</c>'s first line.</param>
/// <param name="Contract">The plan, facts and collector versions the evidence was collected under.</param>
/// <param name="Generator">The generator that recorded it. Informational: the contract versions, not the build, decide whether it still reads the same way.</param>
public sealed record SwiftLoweringReceipt
{
    public required string Artifact { get; init; }

    public required string Compiler { get; init; }

    public required SwiftLoweringContract Contract { get; init; }

    public string? Generator { get; init; }

    public required ImmutableSortedDictionary<string, SwiftLoweringSlice> Slices { get; init; }
}

/// <summary>
/// One slice of a receipt: the SDK and sources of the compile, each symbol's <c>convention signature</c>,
/// and, for a target the collector looked for and could not observe, why.
/// </summary>
/// <param name="Sdk">The SDK identity (<see cref="SwiftToolchainProbe.SdkIdentity"/>), e.g. <c>iphoneos 26.2 (23C53)</c>.</param>
/// <param name="Sources">Every input whose IR the evidence was read from, with its digest and the flags it was compiled with.</param>
/// <param name="Symbols">Each observed symbol's calling convention and normalized signature, or a <c>conflict: </c> record.</param>
/// <param name="Unobserved">Targets the collector asked about and could not observe, each with its reason; never evidence.</param>
public sealed record SwiftLoweringSlice
{
    public required string Sdk { get; init; }

    public required ImmutableArray<SwiftEvidenceSource> Sources { get; init; }

    public required ImmutableSortedDictionary<string, string> Symbols { get; init; }

    public ImmutableSortedDictionary<string, string>? Unobserved { get; init; }

    internal SwiftLoweringSlice Canonical() => this with
    {
        Sources = Sources.OrderBy(s => s.Role, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal).ToImmutableArray(),
        Symbols = Symbols.ToImmutableSortedDictionary(StringComparer.Ordinal),
        Unobserved = Unobserved is null || Unobserved.Count == 0 ? null : Unobserved.ToImmutableSortedDictionary(StringComparer.Ordinal),
    };
}

/// <summary>One input a slice's evidence was read from: what it is, its content digest, and the swiftc flags it was compiled with.</summary>
/// <param name="Role">One of the role constants: what part the input plays.</param>
/// <param name="Name">The module (or probe) the input was compiled as.</param>
/// <param name="Digest"><c>sha256:</c> over the input's files (<see cref="DigestOf"/>).</param>
/// <param name="Flags">The swiftc flags of the compile, without paths, the target, the SDK, the module name or outputs.</param>
public sealed record SwiftEvidenceSource(string Role, string Name, string Digest, ImmutableArray<string> Flags)
{
    /// <summary>The generator's wrapper source: its IR defines the wrapper-hosted targets and declares the library symbols it calls.</summary>
    public const string WrapperRole = "wrapper";

    /// <summary>A probe that calls targets through ordinary Swift, so swiftc declares each one as a client calls it.</summary>
    public const string ProbeRole = "probe";

    /// <summary>A module interface the evidence compile imported.</summary>
    public const string InterfaceRole = "interface";

    /// <summary>A library's own sources, compiled to their definition IR: available to the qualification corpus, never to a binary framework's consumer.</summary>
    public const string DefinitionRole = "definition";

    public bool Equals(SwiftEvidenceSource? other) =>
        other is not null && Role == other.Role && Name == other.Name && Digest == other.Digest && Flags.SequenceEqual(other.Flags, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Role, Name, Digest);

    public override string ToString() => $"{Role} {Name} {Digest} [{string.Join(' ', Flags)}]";

    /// <summary>
    /// The digest of <paramref name="files"/>: SHA-256 over each file's name (not its directory, so a
    /// checkout's location does not change it) and bytes, in name order.
    /// </summary>
    public static string DigestOf(IEnumerable<string> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(Path.GetFileName, StringComparer.Ordinal).ThenBy(f => f, StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(file);
            hash.AppendData(Encoding.UTF8.GetBytes($"{Path.GetFileName(file)}\n{bytes.Length}\n"));
            hash.AppendData(bytes);
        }
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

/// <summary>The versions a receipt's evidence depends on: how plans and facts are written, and how evidence is read out of IR.</summary>
public sealed record SwiftLoweringContract(int PlanVersion, int FactsVersion, int CollectorVersion)
{
    /// <summary>
    /// The collector contract: how a symbol's evidence is read from swiftc's IR (the normalized
    /// signature, a definition over a declaration, disagreeing modules as a conflict). Raise it when
    /// that reading changes, so evidence read the old way is no longer authorizing.
    /// </summary>
    public const int CurrentCollectorVersion = 1;

    public static SwiftLoweringContract Current => new(SwiftCallPlan.Version, SwiftCallFacts.CurrentVersion, CurrentCollectorVersion);

    public override string ToString() => $"plan {PlanVersion}, facts {FactsVersion}, collector {CollectorVersion}";
}

/// <summary>
/// The artifact a set of plans is judged for and the slices (target triples) it claims to support.
/// Evidence must cover every claimed slice; evidence of a slice it does not claim never authorizes
/// anything, though a contradiction there still counts against the plan.
/// </summary>
public sealed record SwiftArtifactScope
{
    public SwiftArtifactScope(string artifact, IEnumerable<string> slices)
    {
        if (string.IsNullOrWhiteSpace(artifact))
            throw new ArgumentException("an artifact scope names its artifact", nameof(artifact));
        var list = slices.ToImmutableArray();
        if (list.IsEmpty)
            throw new ArgumentException($"the scope of {artifact} claims no slice", nameof(slices));
        if (list.Distinct(StringComparer.Ordinal).Count() != list.Length)
            throw new ArgumentException($"the scope of {artifact} claims a slice twice", nameof(slices));
        foreach (var triple in list)
            _ = SdkOf(triple);
        Artifact = artifact;
        Slices = list.Order(StringComparer.Ordinal).ToImmutableArray();
    }

    public string Artifact { get; }

    /// <summary>The claimed triples, in ordinal order.</summary>
    public ImmutableArray<string> Slices { get; }

    public bool Equals(SwiftArtifactScope? other) =>
        other is not null && Artifact == other.Artifact && Slices.SequenceEqual(other.Slices, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Artifact, Slices.Length);

    public override string ToString() => $"{Artifact} [{string.Join(", ", Slices)}]";

    /// <summary>
    /// The evidence triples of a platform lane: what a binding generated for <paramref name="platform"/>
    /// claims when no build names its own slices. Each is the device or simulator slice of one
    /// architecture at the platform's evidence deployment target.
    /// </summary>
    public static ImmutableArray<string> PlatformSlices(string platform) => platform switch
    {
        "ios" => ["arm64-apple-ios15.0", "arm64-apple-ios15.0-simulator", "x86_64-apple-ios15.0-simulator"],
        "tvos" => ["arm64-apple-tvos15.0", "arm64-apple-tvos15.0-simulator", "x86_64-apple-tvos15.0-simulator"],
        "maccatalyst" => ["arm64-apple-ios15.0-macabi", "x86_64-apple-ios15.0-macabi"],
        "macos" => ["arm64-apple-macosx12.0", "x86_64-apple-macosx12.0"],
        _ => throw new ArgumentException($"no evidence slices are defined for the platform '{platform}'", nameof(platform)),
    };

    public static SwiftArtifactScope ForPlatform(string artifact, string platform) => new(artifact, PlatformSlices(platform));

    /// <summary>The SDK a triple compiles against.</summary>
    public static string SdkOf(string triple)
    {
        var parts = triple.Split('-');
        if (parts.Length < 3 || parts[1] != "apple" || parts[0] is not ("arm64" or "x86_64"))
            throw new ArgumentException($"'{triple}' is not an Apple arm64 or x86_64 target triple", nameof(triple));
        var os = parts[2];
        var environment = parts.Length > 3 ? parts[3] : null;
        return (os, environment) switch
        {
            _ when os.StartsWith("ios", StringComparison.Ordinal) && environment == "macabi" => "macosx",
            _ when os.StartsWith("ios", StringComparison.Ordinal) && environment == "simulator" => "iphonesimulator",
            _ when os.StartsWith("ios", StringComparison.Ordinal) && environment is null => "iphoneos",
            _ when os.StartsWith("tvos", StringComparison.Ordinal) && environment == "simulator" => "appletvsimulator",
            _ when os.StartsWith("tvos", StringComparison.Ordinal) && environment is null => "appletvos",
            _ when os.StartsWith("macos", StringComparison.Ordinal) && environment is null => "macosx",
            _ => throw new ArgumentException($"'{triple}' names no platform the evidence knows", nameof(triple)),
        };
    }
}

/// <summary>The scope a plan sidecar was judged under and how, recorded so a gate re-judges it the same way.</summary>
/// <param name="Basis"><see cref="SwiftLoweringJudgement.QualificationBasis"/> or <see cref="SwiftLoweringJudgement.AuthorizationBasis"/>.</param>
public sealed record SwiftPlanEvidenceScope(string Artifact, ImmutableArray<string> Slices, string Basis)
{
    [JsonIgnore]
    public SwiftArtifactScope Scope => new(Artifact, Slices);

    public bool Equals(SwiftPlanEvidenceScope? other) =>
        other is not null && Artifact == other.Artifact && Basis == other.Basis && Slices.SequenceEqual(other.Slices, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Artifact, Basis, Slices.Length);
}

/// <summary>Reads a compiler identity line.</summary>
public static class SwiftCompilerIdentity
{
    /// <summary>
    /// The compiler family of a <c>swiftc --version</c> line: its vendor and major.minor version, e.g.
    /// <c>Apple Swift 6.2</c> for <c>Apple Swift version 6.2.4 (swiftlang-6.2.4.1.4 clang-1700.6.4.2)</c>.
    /// Null for a line that names no Swift version.
    /// </summary>
    public static string? Family(string compiler)
    {
        const string marker = "Swift version ";
        var at = compiler.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            return null;
        var vendor = compiler[..at].Trim();
        var version = compiler[(at + marker.Length)..].Split(' ', 2)[0].Split('.');
        if (version.Length < 2 || !int.TryParse(version[0], out var major) || !int.TryParse(version[1], out var minor))
            return null;
        return $"{(vendor.Length > 0 ? vendor + " " : "")}Swift {major}.{minor}";
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SwiftLoweringEvidence))]
internal partial class SwiftLoweringEvidenceJsonContext : JsonSerializerContext
{
}
