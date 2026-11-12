// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// The swiftc lowering receipt a wrapper compile collects for its module (<c>--collect-swift-lowering-evidence</c>).
/// </summary>
/// <remarks>
/// After a slice's wrapper links, a second swiftc pass emits the IR of the same post-processed wrapper
/// sources with the same compiler, SDK and frontend flags, and the slice's lowering probe
/// (<see cref="SwiftLoweringProbe"/>) emits the IR of ordinary calls to the other targets through the
/// library's public interface. What the wrapper's IR defines (a wrapper-hosted target) or declares (a
/// library symbol the wrapper calls), and what the probe's IR declares, for the module's forward plan
/// targets is the slice's evidence. Every other target is recorded as unobserved, with the reason.
/// <para>
/// A receipt is built only from the compile passes the shipped wrapper is made of, and only when every
/// slice of them was observed. A slice whose IR pass failed, or that has no Swift source to observe,
/// leaves no receipt at all: a receipt missing a slice the wrapper ships is never written.
/// </para>
/// </remarks>
public sealed class SwiftWrapperLoweringEvidence
{
    private readonly List<Pass> _passes = new();
    private readonly Func<string, string?> _sdkIdentity;

    private SwiftWrapperLoweringEvidence(
        string artifact, string platform, string compiler, SortedSet<string> targets, Func<string, string?> sdkIdentity)
    {
        Artifact = artifact;
        Platform = platform;
        Compiler = compiler;
        Targets = targets.ToImmutableSortedSet(StringComparer.Ordinal);
        _sdkIdentity = sdkIdentity;
    }

    /// <summary>The module the receipt answers for.</summary>
    public string Artifact { get; }

    /// <summary>The selected compiler's full version line, which every slice of the receipt is collected by.</summary>
    public string Compiler { get; }

    /// <summary>The platform lane, whose evidence slices (<see cref="SwiftArtifactScope.PlatformSlices"/>) key the receipt.</summary>
    public string Platform { get; }

    /// <summary>The forward plan targets of the module: what each slice's IR is searched for.</summary>
    public ImmutableSortedSet<string> Targets { get; }

    /// <summary>
    /// Why a target the wrapper's IR did not show is unobserved, when the post-processor did not strip it
    /// and no probe asked for it (a wrapper of no bound module has no interface to probe).
    /// </summary>
    public const string NotInWrapperReason =
        "the wrapper's IR neither defines nor declares it: the wrapper does not call it, and no probe asks for it";

    /// <summary>Why a target the post-processor stripped from the wrapper is unobserved.</summary>
    public const string StrippedReason = "the post-processor stripped it from the wrapper";

    /// <summary>
    /// The flags a wrapper source records: the frontend flags of the wrapper compile that are not paths,
    /// outputs, the SDK or the module name, and the exact target, whose deployment version the slice key
    /// leaves out.
    /// </summary>
    public static ImmutableArray<string> WrapperFlags(string compiledTriple) =>
        ["-parse-as-library", "-strict-concurrency=minimal", "-target", compiledTriple];

    /// <summary>
    /// Starts collecting the receipt of the wrapper compile about to run in <paramref name="outputDirectory"/>.
    /// Any receipt already at <paramref name="receiptPath"/> is removed first, so that a compile that
    /// fails or is interrupted leaves no receipt describing an earlier wrapper; a removal that fails
    /// throws. Null, with the reason logged, when nothing will be collected: the selected compiler
    /// could not be identified or is not a qualified family (its receipt could never authorize a plan),
    /// or <see cref="ForOutput"/> declines.
    /// </summary>
    public static SwiftWrapperLoweringEvidence? Begin(
        string receiptPath, string outputDirectory, string moduleName, string platform, ILogger logger,
        Func<string?>? compilerProbe = null, Func<string, string?>? sdkIdentity = null)
    {
        Discard(receiptPath);
        var compiler = (compilerProbe ?? SwiftToolchainProbe.Compiler)();
        if (string.IsNullOrWhiteSpace(compiler))
        {
            logger.LogWarning("Lowering evidence is not collected for {Module}: the selected Swift compiler could not be identified (xcrun swiftc --version).",
                moduleName);
            return null;
        }
        if (!SwiftLoweringQualification.IsQualified(compiler))
        {
            logger.LogInformation("Lowering evidence is not collected for {Module}: {Compiler} is not a qualified compiler family ({Families}), so its receipt could authorize nothing.",
                moduleName, compiler, string.Join(", ", SwiftLoweringQualification.Families));
            return null;
        }
        return ForOutput(outputDirectory, moduleName, platform, compiler, logger, sdkIdentity);
    }

    /// <summary>
    /// Writes the receipt of the shipped wrapper (<see cref="Receipt"/>) to <paramref name="receiptPath"/>
    /// atomically: a reader sees no receipt or the whole one. Call it only once the wrapper compile has
    /// succeeded. Returns whether a receipt was written; when none can be built, the reason is logged
    /// and nothing is written.
    /// </summary>
    public bool Commit(string receiptPath, IReadOnlyCollection<string> unmergedArchitectures, ILogger logger)
    {
        if (Receipt(unmergedArchitectures, out var why) is not { } receipt)
        {
            logger.LogWarning("No lowering receipt is written for {Module}: {Reason}", Artifact, why);
            return false;
        }
        try
        {
            SwiftLoweringEvidenceCollector.Write(receiptPath, receipt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The atomic writer leaves no partial file, so a failed write leaves no receipt.
            logger.LogWarning("No lowering receipt is written for {Module}: {Path} could not be written: {Message}",
                Artifact, receiptPath, ex.Message);
            return false;
        }
        var slices = receipt.Receipts.Single().Slices;
        logger.LogInformation("Wrote the lowering receipt of {Module} under {Compiler} for {Slices} to {Path}.",
            Artifact, Compiler, string.Join(", ", slices.Keys), receiptPath);
        return true;
    }

    /// <summary>Removes the receipt at <paramref name="receiptPath"/>, if any. A removal that fails throws.</summary>
    public static void Discard(string receiptPath)
    {
        if (File.Exists(receiptPath))
            File.Delete(receiptPath);
    }

    /// <summary>
    /// The collection for <paramref name="moduleName"/>'s wrapper in <paramref name="outputDirectory"/>
    /// under <paramref name="compiler"/>, with its targets read from the module's plan sidecars there;
    /// null, with a warning, when the platform claims no evidence slices or the module has no plan sidecar.
    /// </summary>
    public static SwiftWrapperLoweringEvidence? ForOutput(
        string outputDirectory, string moduleName, string platform, string compiler, ILogger logger,
        Func<string, string?>? sdkIdentity = null)
    {
        try
        {
            _ = SwiftArtifactScope.PlatformSlices(platform);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning("Lowering evidence is not collected for {Module}: {Reason}", moduleName, ex.Message);
            return null;
        }
        var own = new List<string>();
        foreach (var sidecar in Directory.Exists(outputDirectory)
                     ? Directory.GetFiles(outputDirectory, "*" + SwiftCallPlanSet.FileName(string.Empty)).Order(StringComparer.Ordinal)
                     : Enumerable.Empty<string>())
        {
            try
            {
                if (SwiftCallPlanSet.Read(sidecar).Module == moduleName)
                    own.Add(sidecar);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                logger.LogWarning("Lowering evidence is not collected for {Module}: its plan sidecar {Sidecar} could not be read: {Message}",
                    moduleName, sidecar, ex.Message);
                return null;
            }
        }
        if (own.Count == 0)
        {
            logger.LogWarning("Lowering evidence is not collected for {Module}: {Directory} holds no plan sidecar of the module.",
                moduleName, outputDirectory);
            return null;
        }
        return new SwiftWrapperLoweringEvidence(moduleName, platform, compiler, SwiftLoweringEvidenceCollector.Targets(own),
            sdkIdentity ?? SwiftToolchainProbe.SdkIdentity);
    }

    /// <summary>Starts one compile pass: the wrapper for one requested architecture (null for the default).</summary>
    public Pass BeginPass(string? architecture)
    {
        var pass = new Pass(this, architecture);
        _passes.Add(pass);
        return pass;
    }

    /// <summary>
    /// The receipt of the shipped wrapper: the slices of the first pass and of every later pass whose
    /// architecture is not in <paramref name="unmergedArchitectures"/> (a pass that was not folded into
    /// the shipped wrapper says nothing about it). A slice two passes both built keeps the first pass's
    /// evidence, as the shipped wrapper keeps the first pass's binary of it. Null, with
    /// <paramref name="why"/>, when no pass ran or any slice of those passes was not observed.
    /// </summary>
    public SwiftLoweringEvidence? Receipt(IReadOnlyCollection<string> unmergedArchitectures, out string? why)
    {
        why = null;
        var shipped = _passes.Take(1)
            .Concat(_passes.Skip(1).Where(p => p.Architecture is null || !unmergedArchitectures.Contains(p.Architecture)))
            .ToList();
        if (shipped.Count == 0)
        {
            why = "no wrapper compile pass ran";
            return null;
        }
        if (shipped.SelectMany(p => p.Failures).FirstOrDefault() is { } failure)
        {
            why = failure;
            return null;
        }
        var evidence = SwiftLoweringEvidence.Empty;
        var recorded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (triple, slice) in shipped.SelectMany(p => p.Slices))
        {
            if (recorded.Add(triple))
                evidence = evidence.WithSlice(Artifact, Compiler, SwiftLoweringContract.Current, SwiftLoweringEvidenceCollector.Generator, triple, slice);
        }
        if (recorded.Count == 0)
        {
            why = "no wrapper slice was observed";
            return null;
        }
        return evidence;
    }

    /// <summary>One compile pass's observations, slice by slice.</summary>
    public sealed class Pass
    {
        private readonly SwiftWrapperLoweringEvidence _owner;
        private readonly List<(string Triple, SwiftLoweringSlice Slice)> _slices = new();
        private readonly List<string> _failures = new();

        internal Pass(SwiftWrapperLoweringEvidence owner, string? architecture)
        {
            _owner = owner;
            Architecture = architecture;
        }

        public string? Architecture { get; }

        public IReadOnlyList<(string Triple, SwiftLoweringSlice Slice)> Slices => _slices;

        /// <summary>Why a slice of this pass has no evidence; any one keeps the receipt from being written.</summary>
        public IReadOnlyList<string> Failures => _failures;

        /// <summary>The targets the collection searches each slice's IR for.</summary>
        public ImmutableSortedSet<string> Targets => _owner.Targets;

        /// <summary>
        /// Records what <paramref name="modules"/>, the IR of the wrapper compiled for
        /// <paramref name="compiledTriple"/> from <paramref name="sources"/>, and the slice's
        /// <paramref name="probe"/>, if one ran, show for every target. A target neither shows is
        /// unobserved with the probe's reason when the probe was asked for it; otherwise, when it is in
        /// <paramref name="stripped"/>, because the post-processor stripped it. Returns the recorded slice,
        /// or null when the slice could not be keyed (which is recorded as a failure).
        /// </summary>
        public SwiftLoweringSlice? Observe(
            string compiledTriple, IReadOnlyList<SwiftIrModule> modules, IReadOnlyList<SwiftEvidenceSource> sources,
            IReadOnlySet<string>? stripped = null, SwiftLoweringProbe.Result? probe = null)
        {
            if (!TrySlice(compiledTriple, out var triple, out var sdk))
                return null;
            var observed = probe is null ? modules : modules.Concat(probe.Modules).ToList();
            var symbols = SwiftLoweringEvidenceCollector.Signatures(_owner.Targets, observed);
            var slice = SwiftLoweringEvidenceCollector.Slice(sdk, sources, _owner.Targets, symbols,
                t => probe is not null && probe.Unseen.TryGetValue(t, out var why) ? why
                    : stripped is not null && stripped.Contains(t) ? StrippedReason
                    : NotInWrapperReason);
            _slices.Add((triple, slice));
            return slice;
        }

        /// <summary>Records that the slice compiled for <paramref name="compiledTriple"/> could not be observed, and why.</summary>
        public void Fail(string compiledTriple, string why) => _failures.Add($"{compiledTriple}: {why}");

        private bool TrySlice(string compiledTriple, out string triple, out string sdk)
        {
            triple = sdk = string.Empty;
            if (SwiftArtifactScope.EvidenceSliceOf(_owner.Platform, compiledTriple) is not { } key)
            {
                Fail(compiledTriple, $"the {_owner.Platform} lane claims no evidence slice for this target");
                return false;
            }
            var sdkName = SwiftArtifactScope.SdkOf(key);
            if (_owner._sdkIdentity(sdkName) is not { } identity)
            {
                Fail(compiledTriple, $"the {sdkName} SDK could not be identified (xcrun --sdk {sdkName} --show-sdk-version)");
                return false;
            }
            (triple, sdk) = (key, identity);
            return true;
        }
    }

    /// <summary>
    /// The interface sources a wrapper compile for one slice could import: the bound module's framework,
    /// then every other framework in <paramref name="frameworkSearchPath"/> (a slice may ship peer
    /// frameworks beside the bound one) and in <paramref name="dependencySearchPaths"/>, the first of
    /// each name, as swiftc's search order finds it. Each is digested over its <c>Modules</c>,
    /// <c>Headers</c> and <c>PrivateHeaders</c> files, which are what swiftc reads to import it. Platform
    /// paths the SDK owns are left out: the slice's SDK identity covers them.
    /// </summary>
    public static IReadOnlyList<SwiftEvidenceSource> InterfaceSources(
        string moduleName, string frameworkSearchPath, IEnumerable<string>? dependencySearchPaths)
    {
        var sources = new List<SwiftEvidenceSource>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string name, string frameworkDirectory)
        {
            if (!seen.Add(name))
                return;
            var files = new[] { "Modules", "Headers", "PrivateHeaders" }
                .Select(d => Path.Combine(frameworkDirectory, d))
                .Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                .ToList();
            // An imported interface is not compiled by the evidence compile, so it records no flags.
            if (files.Count > 0)
                sources.Add(new SwiftEvidenceSource(SwiftEvidenceSource.InterfaceRole, name, SwiftEvidenceSource.DigestOf(files), ImmutableArray<string>.Empty));
        }

        Add(moduleName, Path.Combine(frameworkSearchPath, moduleName + ".framework"));
        foreach (var path in (dependencySearchPaths ?? []).Prepend(frameworkSearchPath))
        {
            if (!Directory.Exists(path))
                continue;
            foreach (var framework in Directory.GetDirectories(path, "*.framework").Order(StringComparer.Ordinal))
                Add(Path.GetFileNameWithoutExtension(framework), framework);
        }
        return sources;
    }
}
