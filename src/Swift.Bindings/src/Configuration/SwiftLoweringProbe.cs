// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// The lowering probe of one wrapper slice: ordinary Swift that calls each plan target through the
/// library's public interface, compiled to IR with the wrapper compile's own frontend flags, so that
/// swiftc declares each target exactly as a client of the binary framework calls it.
/// </summary>
/// <remarks>
/// <para>
/// Every call is written from the ABI descriptors (<c>.abi.json</c>) the slice's frameworks ship, never
/// from the generator's prediction, and no target is named by its symbol (no <c>@_silgen_name</c>):
/// the probe calls a declaration, and swiftc chooses the entry and lowers it. Only the exact mangled
/// target counts, so a call that resolves to another overload or entry shows nothing for it.
/// </para>
/// <para>
/// Each target is called from its own never-called function. A call that does not compile is
/// attributed to its function by line and dropped with the compiler's error as its reason, and the
/// rest compile again. A target the probe cannot call, or whose call leaves no declaration of it, is
/// returned with the reason: it stays unobserved, so a probe never authorizes by omission.
/// </para>
/// </remarks>
public static class SwiftLoweringProbe
{
    /// <summary>How many times one slice's probe is compiled before the calls still failing are given up.</summary>
    public const int MaxCompiles = 4;

    /// <summary>Why a target in a module whose slice ships no ABI descriptor is unobserved.</summary>
    public static string NoDescriptorReason(string module) =>
        $"unsupported in the probe: {module} ships no ABI descriptor (.abi.json) for this slice to write a call from";

    /// <summary>
    /// Why a target no imported ABI descriptor declares is unobserved. A descriptor lists a module's ABI,
    /// which leaves out what a client never calls by symbol (an <c>@_alwaysEmitIntoClient</c> member, a
    /// non-public one).
    /// </summary>
    public const string NotDeclaredReason =
        "other: no ABI descriptor of an imported module declares it (an @_alwaysEmitIntoClient or non-public member is not in a module's ABI), so no probe call can reach it";

    /// <summary>Why a <c>@usableFromInline internal</c> target is unobserved.</summary>
    public const string UsableFromInlineReason =
        "not public from the interface: it is @usableFromInline internal, which no client can call";

    /// <summary>Why an SPI target is unobserved.</summary>
    public const string SpiReason =
        "not public from the interface: it is SPI, which the public interface leaves out";

    /// <summary>Why an <c>@_alwaysEmitIntoClient</c> target is unobserved.</summary>
    public const string AlwaysEmitIntoClientReason =
        "inlined: it is @_alwaysEmitIntoClient, so the probe's IR defines its own copy instead of calling the library's";

    /// <summary>Why a target the probe's own IR defines is unobserved.</summary>
    public const string DefinedByProbeReason =
        "inlined: the probe's IR defines its own copy instead of calling the library's";

    /// <summary>Why an <c>@_transparent</c> target is unobserved.</summary>
    public const string TransparentReason =
        "inlined: it is @_transparent, so the probe's call is inlined and its IR never declares it";

    /// <summary>Why a target the probe called, but whose IR neither declares nor defines it, is unobserved.</summary>
    public const string InlinedReason =
        "inlined: the probe calls it, but the probe's IR neither declares nor defines it";

    /// <summary>Why a target a client reaches through another entry (its dispatch thunk, or the entry its dispatch thunk dispatches to) is unobserved.</summary>
    public static string ThroughAnotherEntryReason(string entry) =>
        $"other: a client calls it through {entry}, which the probe's IR declares instead";

    /// <summary>Why a target the probe has no call form for is unobserved.</summary>
    public static string UnsupportedReason(string what) => "unsupported in the probe: " + what;

    /// <summary>Why a target whose probe call did not compile is unobserved.</summary>
    public static string DidNotCompileReason(string error) => "unsupported in the probe: its call did not compile: " + error;

    /// <summary>Why a target is unobserved when the probe as a whole could not be compiled.</summary>
    public static string ProbeFailedReason(string why) => "other: the probe did not compile: " + why;

    /// <summary>What one slice's probe is asked to show.</summary>
    /// <param name="Targets">The plan targets to call.</param>
    /// <param name="ModuleName">The bound module, whose descriptor is read first and which the probe always imports.</param>
    /// <param name="FrameworkSearchPath">The slice directory the bound framework (and any peer framework) is in.</param>
    /// <param name="DependencySearchPaths">The dependency frameworks' slice directories.</param>
    /// <param name="WrapperSources">The wrapper sources, whose imports the probe repeats.</param>
    /// <param name="FrontendArgs">The wrapper compile's frontend flags, without its module name.</param>
    /// <param name="ProbeModuleName">The module the probe is compiled as.</param>
    /// <param name="CompiledTriple">The exact target the wrapper was compiled for.</param>
    /// <param name="WorkDirectory">Where the probe's source and IR are written, and deleted from afterwards.</param>
    public sealed record Request(
        IReadOnlyCollection<string> Targets,
        string ModuleName,
        string FrameworkSearchPath,
        IReadOnlyList<string>? DependencySearchPaths,
        IReadOnlyList<string> WrapperSources,
        string FrontendArgs,
        string ProbeModuleName,
        string CompiledTriple,
        string WorkDirectory);

    /// <summary>What one slice's probe showed.</summary>
    /// <param name="Modules">The probe's IR, its declarations alone: what it defines is its own code, never a library's lowering.</param>
    /// <param name="Unseen">Every requested target the probe's IR does not declare, with the reason.</param>
    /// <param name="Source">The probe as an evidence source, when its IR was read; null when it never compiled.</param>
    /// <param name="Calls">How many targets the probe wrote a call for.</param>
    /// <param name="Compiles">How many times the probe was compiled.</param>
    /// <param name="Elapsed">The probe's whole cost: reading the descriptors, demangling, writing and compiling.</param>
    public sealed record Result(
        IReadOnlyList<SwiftIrModule> Modules,
        ImmutableSortedDictionary<string, string> Unseen,
        SwiftEvidenceSource? Source,
        int Calls,
        int Compiles,
        TimeSpan Elapsed)
    {
        /// <summary>How many of the requested targets the probe's IR declares.</summary>
        public int Shown(IEnumerable<string> targets) =>
            targets.Count(t => Modules.Any(m => m.Declarations.ContainsKey(t)));
    }

    /// <summary>
    /// Writes and compiles the probe of <paramref name="request"/>'s targets. It never throws for a
    /// probe that cannot be written or compiled: every target it cannot show is returned in
    /// <see cref="Result.Unseen"/> with the reason.
    /// </summary>
    public static Result Run(Request request, ICommandRunner runner, ILogger logger, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        var targets = request.Targets.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var unseen = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var modules = new List<SwiftIrModule>();
        SwiftEvidenceSource? source = null;
        var calls = 0;
        var compiles = 0;
        if (targets.Count == 0)
            return Finish();

        var sourcePath = Path.Combine(request.WorkDirectory, $"{request.ProbeModuleName}.{request.CompiledTriple}.swift");
        var irPath = Path.ChangeExtension(sourcePath, ".ll");
        using var index = new AbiDescriptorIndex();
        try
        {
            var descriptors = Descriptors(request.ModuleName, request.FrameworkSearchPath, request.DependencySearchPaths, request.CompiledTriple);
            var boundDescriptor = descriptors[0].Path;
            foreach (var (module, path) in descriptors)
            {
                if (path is null || targets.All(t => index.Find(t) is not null))
                    continue;
                try
                {
                    index.Load(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    logger.LogWarning("The lowering probe cannot read {Module}'s ABI descriptor {Path}: {Message}", module, path, ex.Message);
                    if (module == request.ModuleName)
                        boundDescriptor = null;
                }
            }

            var resolved = targets.Where(t => index.Find(t) is not null).ToList();
            var imports = new List<string>(WrapperImports(request.WrapperSources));
            if (boundDescriptor is not null)
                imports.AddRange(InterfaceImports(Path.ChangeExtension(Path.ChangeExtension(boundDescriptor, null), ".swiftinterface")));
            var plan = Plan(targets, index, Demangle(resolved, runner, timeoutMs), request.ModuleName, imports,
                boundDescriptor is null ? NoDescriptorReason(request.ModuleName) : NotDeclaredReason);
            foreach (var (target, why) in plan.Unprobed)
                unseen[target] = why;
            calls = plan.Calls.Length;
            var alive = plan.Calls.ToList();

            while (alive.Count > 0)
            {
                var (text, lines) = Render(plan, alive);
                File.WriteAllText(sourcePath, text);
                compiles++;
                var args = $"swiftc -emit-ir -parse-as-library {request.FrontendArgs}-module-name {request.ProbeModuleName} " +
                           $"-o \"{irPath}\" \"{sourcePath}\"";
                logger.LogDebug("Lowering probe: xcrun {Args}", args);
                var (exitCode, _, stderr) = runner.Run("xcrun", args, timeoutMs: timeoutMs);
                if (exitCode == 0)
                {
                    if (!File.Exists(irPath))
                    {
                        Fail(alive, "swiftc -emit-ir wrote no IR");
                        break;
                    }
                    var ir = File.ReadAllText(irPath);
                    var parsed = SwiftIrModule.ParseModules(new StringReader(ir));
                    source = new SwiftEvidenceSource(SwiftEvidenceSource.ProbeRole, request.ProbeModuleName,
                        SwiftEvidenceSource.DigestOf([sourcePath]), SwiftWrapperLoweringEvidence.WrapperFlags(request.CompiledTriple));
                    foreach (var call in alive)
                    {
                        if (Classify(call, parsed, ir) is { } why)
                            unseen[call.Target] = why;
                    }
                    modules.AddRange(parsed.Select(m => m.DeclarationsOnly()));
                    break;
                }

                var failed = Errors(stderr, Path.GetFileName(sourcePath), lines, out var unattributed);
                if (unattributed is not null || failed.Count == 0)
                {
                    Fail(alive, unattributed ?? $"swiftc -emit-ir exited {exitCode} without an error of a call");
                    break;
                }
                foreach (var (call, error) in failed)
                {
                    unseen[call.Target] = DidNotCompileReason(error);
                    alive.Remove(call);
                }
                if (compiles >= MaxCompiles && alive.Count > 0)
                {
                    Fail(alive, $"its calls still had errors after {MaxCompiles} compiles");
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or TimeoutException or ArgumentException or JsonException)
        {
            logger.LogWarning("The lowering probe for {Triple} failed: {Message}", request.CompiledTriple, ex.Message);
            modules.Clear();
            source = null;
            foreach (var target in targets.Where(t => !unseen.ContainsKey(t)))
                unseen[target] = ProbeFailedReason(ex.Message);
        }
        finally
        {
            foreach (var path in new[] { sourcePath, irPath })
            {
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort: the build directory is removed after the compile */ }
            }
        }
        return Finish();

        void Fail(IEnumerable<ProbeCall> remaining, string why)
        {
            foreach (var call in remaining)
                unseen[call.Target] = ProbeFailedReason(why);
        }

        Result Finish()
        {
            // Every target is accounted for: shown by a declaration, or unseen with a reason.
            foreach (var target in targets)
            {
                if (!unseen.ContainsKey(target) && !modules.Any(m => m.Declarations.ContainsKey(target)))
                    unseen[target] = ProbeFailedReason("it gave no account of this target");
            }
            return new(modules, unseen.ToImmutableSortedDictionary(StringComparer.Ordinal), source, calls, compiles, clock.Elapsed);
        }
    }

    private static readonly string[] Categories = ["not public from the interface", "inlined", "unsupported in the probe", "other"];

    /// <summary>
    /// The class of an unobserved target's reason: the probe's own classes (not public from the interface,
    /// inlined, unsupported in the probe, other), or the reason itself up to its first colon.
    /// </summary>
    public static string ReasonCategory(string reason)
    {
        foreach (var category in Categories)
        {
            if (reason.StartsWith(category + ":", StringComparison.Ordinal))
                return category;
        }
        var colon = reason.IndexOf(':');
        return colon < 0 ? reason : reason[..colon];
    }

    // ---- reading the probe's IR ----

    /// <summary>Why <paramref name="call"/>'s target is not shown by the probe's IR, or null when the IR declares it.</summary>
    private static string? Classify(ProbeCall call, IReadOnlyList<SwiftIrModule> modules, string ir)
    {
        var target = call.Target;
        if (modules.Any(m => m.Declarations.ContainsKey(target)))
            return null;
        if (modules.Any(m => m.Functions.ContainsKey(target)))
            return call.Inlining == AlwaysEmitIntoClient ? AlwaysEmitIntoClientReason : DefinedByProbeReason;
        foreach (var entry in RelatedEntries(target))
        {
            if (modules.Any(m => m.Declarations.ContainsKey(entry)))
                return ThroughAnotherEntryReason(entry);
        }
        if (ir.Contains($"@\"{target}Tu\"", StringComparison.Ordinal))
            return ThroughAnotherEntryReason($"its async function pointer {target}Tu");
        return call.Inlining == Transparent ? TransparentReason : InlinedReason;
    }

    /// <summary>The other entries a client may call in place of <paramref name="target"/>.</summary>
    private static IEnumerable<string> RelatedEntries(string target)
    {
        if (target.EndsWith("Tj", StringComparison.Ordinal))
        {
            yield return target[..^2];
            yield break;
        }
        yield return target + "Tj";
        // An initializing initializer is reached through the allocating one, which a client calls.
        if (target.EndsWith("fc", StringComparison.Ordinal))
        {
            yield return target[..^2] + "fC";
            yield return target[..^2] + "fCTj";
        }
    }

    private static readonly Regex LocatedError = new(@"^(?<file>.+?):(?<line>\d+):(?<column>\d+): error: (?<message>.*)$", RegexOptions.Compiled);

    /// <summary>
    /// The calls <paramref name="stderr"/>'s errors fall in, each with its first error; any error outside
    /// every call (in the probe's header, another file, or without a location) is returned in
    /// <paramref name="unattributed"/>, since dropping a call cannot fix it.
    /// </summary>
    internal static IReadOnlyList<(ProbeCall Call, string Error)> Errors(
        string stderr, string probeFileName, IReadOnlyList<(ProbeCall Call, int First, int Last)> lines, out string? unattributed)
    {
        unattributed = null;
        var failed = new List<(ProbeCall, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("error: ", StringComparison.Ordinal))
            {
                unattributed ??= line["error: ".Length..].Trim();
                continue;
            }
            var match = LocatedError.Match(line);
            if (!match.Success)
                continue;
            var message = match.Groups["message"].Value.Trim();
            if (!string.Equals(Path.GetFileName(match.Groups["file"].Value), probeFileName, StringComparison.Ordinal))
            {
                unattributed ??= line.Trim();
                continue;
            }
            var number = int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var owner = lines.FirstOrDefault(l => number >= l.First && number <= l.Last);
            if (owner.Call is null)
            {
                unattributed ??= line.Trim();
                continue;
            }
            if (seen.Add(owner.Call.Target))
                failed.Add((owner.Call, message.Length > 300 ? message[..300] + "..." : message));
        }
        return failed;
    }

    // ---- the probe source ----

    private const string AlwaysEmitIntoClient = "@_alwaysEmitIntoClient";
    private const string Transparent = "@_transparent";

    /// <summary>One target's call: its never-called function, and what would keep a client from declaring it.</summary>
    internal sealed record ProbeCall(string Target, string Function, string Text, string? Inlining);

    /// <summary>The probe of a set of targets: its imports, a call per target it can write, and why it cannot write the others.</summary>
    internal sealed record ProbePlan(
        ImmutableArray<string> Imports, ImmutableArray<ProbeCall> Calls, ImmutableSortedDictionary<string, string> Unprobed);

    private const string ValueHelper = "_sbProbeValue";

    /// <summary>
    /// The probe of <paramref name="targets"/>: a call for each one <paramref name="index"/> declares
    /// and the probe can write, in target order; for every other, why not (<paramref name="undeclared"/>
    /// for a target no descriptor declares).
    /// </summary>
    internal static ProbePlan Plan(
        IReadOnlyList<string> targets, AbiDescriptorIndex index, IReadOnlyDictionary<string, string> demangled,
        string boundModule, IEnumerable<string> imports, string undeclared)
    {
        var calls = ImmutableArray.CreateBuilder<ProbeCall>();
        var unprobed = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var modules = new List<string>();
        foreach (var target in targets.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (index.Find(target) is not { } declaration)
            {
                unprobed[target] = undeclared;
                continue;
            }
            var function = $"_sbProbe_{calls.Count}";
            if (CallWriter.TryWrite(target, declaration, demangled.GetValueOrDefault(target), function, out var text, out var inlining, out var why))
            {
                calls.Add(new ProbeCall(target, function, text, inlining));
                modules.Add(declaration.Module);
            }
            else
            {
                unprobed[target] = why;
            }
        }
        var ordered = new List<string> { boundModule };
        foreach (var module in imports.Concat(modules.Order(StringComparer.Ordinal)))
        {
            if (!ordered.Contains(module, StringComparer.Ordinal))
                ordered.Add(module);
        }
        return new ProbePlan(ordered.ToImmutableArray(), calls.ToImmutable(), unprobed.ToImmutableSortedDictionary(StringComparer.Ordinal));
    }

    /// <summary>The probe source of <paramref name="calls"/>, and the lines each call's function spans.</summary>
    internal static (string Text, IReadOnlyList<(ProbeCall Call, int First, int Last)> Lines) Render(ProbePlan plan, IEnumerable<ProbeCall> calls)
    {
        var text = new StringBuilder();
        var line = 0;
        void Append(string value)
        {
            text.Append(value).Append('\n');
            line++;
        }

        Append("// The lowering probe Swift.Bindings writes to read how swiftc calls each plan target. It is compiled");
        Append("// to IR with the wrapper's flags and never linked or run.");
        // The bound module is imported unconditionally; any other only where it resolves, so a module the
        // wrapper imports conditionally cannot fail the probe's header.
        Append($"import {plan.Imports[0]}");
        foreach (var module in plan.Imports.Skip(1))
        {
            Append($"#if canImport({module})");
            Append($"import {module}");
            Append("#endif");
        }
        Append("");
        Append($"@inline(never) func {ValueHelper}<T: ~Copyable>() -> T {{ fatalError() }}");
        Append("");
        var lines = new List<(ProbeCall, int, int)>();
        foreach (var call in calls)
        {
            var first = line + 1;
            foreach (var callLine in call.Text.Split('\n'))
                Append(callLine);
            lines.Add((call, first, line));
            Append("");
        }
        return (text.ToString(), lines);
    }

    private static readonly Regex ImportLine = new(
        @"^(?:@[\w]+(?:\([^)]*\))?\s+)*(?:(?:public|package|internal|fileprivate|private)\s+)?import\s+(?:(?:typealias|struct|class|enum|protocol|let|var|func)\s+)?(?<module>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    /// <summary>The modules the wrapper sources import at file scope, in order.</summary>
    internal static IReadOnlyList<string> WrapperImports(IEnumerable<string> sources)
    {
        var modules = new List<string>();
        foreach (var file in sources)
        {
            if (!File.Exists(file))
                continue;
            foreach (var line in File.ReadLines(file))
                AddImport(modules, line);
        }
        return modules;
    }

    /// <summary>The modules a module interface imports, other than the compiler's own underscored ones.</summary>
    private static IReadOnlyList<string> InterfaceImports(string interfacePath)
    {
        var modules = new List<string>();
        if (File.Exists(interfacePath))
        {
            foreach (var line in File.ReadLines(interfacePath))
                AddImport(modules, line);
        }
        return modules.Where(m => !m.StartsWith('_')).ToList();
    }

    private static void AddImport(List<string> modules, string line)
    {
        // Only a file-scope import: one at the start of its line.
        var match = ImportLine.Match(line);
        if (match.Success && match.Groups["module"].Value is var module && !modules.Contains(module, StringComparer.Ordinal))
            modules.Add(module);
    }

    // ---- the descriptors ----

    private static readonly Regex Triple = new(@"^(?<arch>[^-]+)-apple-(?<os>[a-z]+?)(?<version>[\d.]*)(?:-(?<environment>[a-z]+))?$", RegexOptions.Compiled);

    /// <summary>
    /// The name a framework's module directory gives the files of <paramref name="compiledTriple"/>'s
    /// slice: the triple without its deployment version (<c>arm64-apple-ios-simulator</c>), or null
    /// when it is not an Apple triple.
    /// </summary>
    internal static string? ModuleFileStem(string compiledTriple)
    {
        var match = Triple.Match(compiledTriple);
        if (!match.Success)
            return null;
        var os = match.Groups["os"].Value == "macosx" ? "macos" : match.Groups["os"].Value;
        var environment = match.Groups["environment"].Value;
        return $"{match.Groups["arch"].Value}-apple-{os}{(environment.Length > 0 ? "-" + environment : "")}";
    }

    /// <summary>
    /// The ABI descriptor of <paramref name="moduleName"/>'s framework in <paramref name="frameworkDirectory"/>
    /// for <paramref name="compiledTriple"/>, or null when the framework ships none for its architecture.
    /// </summary>
    internal static string? DescriptorOf(string frameworkDirectory, string moduleName, string compiledTriple)
    {
        var modules = Path.Combine(frameworkDirectory, "Modules", moduleName + ".swiftmodule");
        if (ModuleFileStem(compiledTriple) is not { } stem || !Directory.Exists(modules))
            return null;
        var exact = Path.Combine(modules, stem + ".abi.json");
        if (File.Exists(exact))
            return exact;
        // A slice's framework holds only its own platform, so its one descriptor of the architecture is it.
        var architecture = stem[..stem.IndexOf('-')];
        var candidates = Directory.GetFiles(modules, architecture + "-apple-*.abi.json");
        return candidates.Length == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// The descriptors a probe of one slice reads: the bound module's first (its path null when it has
    /// none), then every other framework's in <paramref name="frameworkSearchPath"/> and
    /// <paramref name="dependencySearchPaths"/>, the first of each name, as swiftc's search order finds it.
    /// </summary>
    internal static IReadOnlyList<(string Module, string? Path)> Descriptors(
        string moduleName, string frameworkSearchPath, IEnumerable<string>? dependencySearchPaths, string compiledTriple)
    {
        var descriptors = new List<(string, string?)>
        {
            (moduleName, DescriptorOf(Path.Combine(frameworkSearchPath, moduleName + ".framework"), moduleName, compiledTriple)),
        };
        var seen = new HashSet<string>(StringComparer.Ordinal) { moduleName };
        foreach (var path in (dependencySearchPaths ?? []).Prepend(frameworkSearchPath))
        {
            if (!Directory.Exists(path))
                continue;
            foreach (var framework in Directory.GetDirectories(path, "*.framework").Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(framework);
                if (seen.Add(name) && DescriptorOf(framework, name, compiledTriple) is { } descriptor)
                    descriptors.Add((name, descriptor));
            }
        }
        return descriptors;
    }

    /// <summary>
    /// The demangling of each of <paramref name="symbols"/> that <c>swift-demangle</c> reads; a symbol it
    /// cannot read is left out, and its call is written from the descriptor alone.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Demangle(IReadOnlyList<string> symbols, ICommandRunner runner, int timeoutMs)
    {
        var demangled = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in symbols.Chunk(64))
        {
            var (exitCode, stdout, _) = runner.Run("xcrun", "swift-demangle " + string.Join(" ", chunk.Select(s => $"\"{s}\"")), timeoutMs);
            if (exitCode != 0)
                continue;
            foreach (var line in stdout.Split('\n'))
            {
                var arrow = line.IndexOf(" ---> ", StringComparison.Ordinal);
                if (arrow > 0)
                    demangled[line[..arrow].Trim()] = line[(arrow + " ---> ".Length)..].Trim();
            }
        }
        return demangled;
    }

    /// <summary>
    /// Writes one target's call from its descriptor declaration, in a function of its own:
    /// <code>
    /// @available(iOS 99, ...) @MainActor
    /// public func _sbProbe_N&lt;T_0_0&gt;(_: T_0_0.Type) async throws where ... {
    ///     var recv: Receiver = _sbProbeValue()
    ///     let _: Result = try await recv.member(label: (_sbProbeValue() as Type))
    /// }
    /// </code>
    /// The function is never called: its body only has to type-check, so every value comes from a
    /// generic helper that never returns. It is async and main-actor isolated, so an isolated member
    /// can be called, and available nowhere, so a member of any availability can.
    /// </summary>
    private static class CallWriter
    {
        private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        {
            "associatedtype", "class", "deinit", "enum", "extension", "fileprivate", "func", "import", "init", "inout",
            "internal", "let", "open", "operator", "private", "precedencegroup", "protocol", "public", "rethrows",
            "static", "struct", "subscript", "typealias", "var", "break", "case", "catch", "continue", "default",
            "defer", "do", "else", "fallthrough", "for", "guard", "if", "in", "repeat", "return", "throw", "switch",
            "where", "while", "Any", "as", "false", "is", "nil", "self", "Self", "super", "throws", "true", "try",
            "Type", "Protocol",
        };

        private static readonly Regex GenericParameter = new(@"τ_(\d+)_(\d+)", RegexOptions.Compiled);
        private static readonly Regex ProbeParameter = new(@"\bT_\d+_\d+\b", RegexOptions.Compiled);
        private static readonly Regex DynamicSelf = new(@"(?<![\w.])Self(?!\w)", RegexOptions.Compiled);

        public static bool TryWrite(
            string target, AbiDeclaration declaration, string? demangled, string function,
            out string text, out string? inlining, out string why)
        {
            text = why = string.Empty;
            inlining = null;
            var node = declaration.Node;
            var ancestors = declaration.Ancestors;
            if (HasAttribute(node, "UsableFromInline") || ancestors.Any(a => HasAttribute(a, "UsableFromInline")))
            {
                why = UsableFromInlineReason;
                return false;
            }
            if (IsSpi(node) || ancestors.Any(IsSpi))
            {
                why = SpiReason;
                return false;
            }
            var storage = Kind(node) == "Accessor" ? ancestors.LastOrDefault() : default;
            if (HasAttribute(node, "AlwaysEmitIntoClient") || (storage.ValueKind == JsonValueKind.Object && HasAttribute(storage, "AlwaysEmitIntoClient")))
                inlining = AlwaysEmitIntoClient;
            else if (HasAttribute(node, "Transparent"))
                inlining = Transparent;

            var signature = GenericSignature(node, ancestors);
            if (signature.Contains("each ", StringComparison.Ordinal) || signature.Contains("repeat ", StringComparison.Ordinal))
            {
                why = UnsupportedReason("a parameter pack has no call form here");
                return false;
            }
            var (parameters, requirements) = SplitSignature(signature);
            var known = parameters.ToHashSet(StringComparer.Ordinal);
            var types = ancestors.Where(a => Kind(a) == "TypeDecl").ToList();
            var receiver = Receiver(types, out var isProtocol);
            var parsed = demangled is null ? default : Demangled.Parse(demangled);

            var prelude = new List<string>();
            string statement;
            switch (Kind(node))
            {
                case "Function":
                {
                    var name = Str(node, "name") ?? string.Empty;
                    var nodeParameters = Children(node).Skip(1).ToList();
                    if (!TryArguments(node, nodeParameters, parsed.Parameters, receiver, isProtocol, known, prelude, out var arguments, out why))
                        return false;
                    var result = ResultType(Children(node).FirstOrDefault(), parsed.Result, receiver, isProtocol);
                    string expression;
                    if (name.Length > 0 && !char.IsLetter(name[0]) && name[0] != '_')
                    {
                        expression = Operator(name, node, arguments, prelude.Count);
                    }
                    else if (receiver is null)
                    {
                        expression = $"{Module(node, declaration.Module)}.{Identifier(name)}({string.Join(", ", arguments)})";
                    }
                    else if (Bool(node, "static"))
                    {
                        expression = $"{receiver}.{Identifier(name)}({string.Join(", ", arguments)})";
                    }
                    else
                    {
                        prelude.Insert(0, $"var recv: {receiver} = {ValueHelper}()");
                        expression = $"recv.{Identifier(name)}({string.Join(", ", arguments)})";
                    }
                    statement = Bind(result, expression);
                    break;
                }
                case "Constructor":
                {
                    if (receiver is null)
                    {
                        why = UnsupportedReason("its initializer belongs to no type the probe can name");
                        return false;
                    }
                    if (!TryArguments(node, Children(node).Skip(1).ToList(), parsed.Parameters, receiver, isProtocol, known, prelude, out var arguments, out why))
                        return false;
                    statement = $"_ = try await {receiver}({string.Join(", ", arguments)})";
                    break;
                }
                case "Accessor":
                {
                    var accessor = Str(node, "accessorKind") ?? Str(node, "name") ?? string.Empty;
                    if (accessor is not ("get" or "set"))
                    {
                        why = UnsupportedReason($"its {accessor} accessor has no direct call form");
                        return false;
                    }
                    if (storage.ValueKind != JsonValueKind.Object)
                    {
                        why = UnsupportedReason("its accessor belongs to no storage the probe can name");
                        return false;
                    }
                    var owners = ancestors.Take(ancestors.Length - 1).Where(a => Kind(a) == "TypeDecl").ToList();
                    receiver = Receiver(owners, out isProtocol);
                    var isStatic = Bool(storage, "static");
                    var value = ResultType(Children(storage).FirstOrDefault(), null, receiver, isProtocol);
                    string reference;
                    if (Kind(storage) == "Subscript")
                    {
                        if (receiver is null)
                        {
                            why = UnsupportedReason("its subscript belongs to no type the probe can name");
                            return false;
                        }
                        if (!TryArguments(storage, Children(storage).Skip(1).ToList(), parsed.Parameters, receiver, isProtocol, known, prelude, out var arguments, out why))
                            return false;
                        reference = isStatic ? $"{receiver}[{string.Join(", ", arguments)}]" : $"recv[{string.Join(", ", arguments)}]";
                    }
                    else
                    {
                        var name = Identifier(Str(storage, "name") ?? string.Empty);
                        reference = receiver is null ? $"{Module(storage, declaration.Module)}.{name}"
                            : isStatic ? $"{receiver}.{name}"
                            : $"recv.{name}";
                    }
                    if (receiver is not null && !isStatic)
                        prelude.Insert(0, $"var recv: {receiver} = {ValueHelper}()");
                    if (accessor == "get")
                    {
                        statement = Bind(value, reference);
                    }
                    else
                    {
                        if (value is null)
                        {
                            why = UnsupportedReason("the type its setter takes cannot be spelled");
                            return false;
                        }
                        statement = $"{reference} = ({ValueHelper}() as {value})";
                    }
                    break;
                }
                default:
                    why = UnsupportedReason($"a {Str(node, "declKind") ?? Kind(node)} declaration has no call form");
                    return false;
            }

            var generics = parameters.Count == 0 ? string.Empty : $"<{string.Join(", ", parameters)}>";
            // A generic parameter must appear in the function's signature, so each one is a metatype argument.
            var metatypes = string.Join(", ", parameters.Select(p => $"_: {p}.Type"));
            var where = requirements.Length == 0 ? string.Empty : " where " + requirements;
            var body = new StringBuilder();
            body.Append("// ").Append(target).Append('\n');
            body.Append("@available(iOS 99, macOS 99, tvOS 99, watchOS 99, visionOS 99, *)\n");
            body.Append("@MainActor\n");
            body.Append($"public func {function}{generics}({metatypes}) async throws{where} {{\n");
            foreach (var line in prelude)
                body.Append("    ").Append(line).Append('\n');
            body.Append("    ").Append(statement).Append('\n');
            body.Append('}');
            text = body.ToString();
            return true;
        }

        private static string Bind(string? type, string expression) =>
            type is null ? $"_ = try await {expression}" : $"let _: {type} = try await {expression}";

        private static string Operator(string name, JsonElement node, List<string> arguments, int preludeCount)
        {
            // An operator's inout operand is written bare: the operator takes it in place.
            var operands = arguments.Select(a => a.StartsWith('&') ? a[1..] : a).ToList();
            if (operands.Count == 2)
                return $"{operands[0]} {name} {operands[1]}";
            return HasAttribute(node, "Postfix") ? $"{operands[0]}{name}" : $"{name}{operands[0]}";
        }

        /// <summary>The module a global declaration is qualified with.</summary>
        private static string Module(JsonElement node, string fallback) => Str(node, "moduleName") ?? fallback;

        private static bool TryArguments(
            JsonElement owner, IReadOnlyList<JsonElement> parameters, IReadOnlyList<Demangled.Parameter>? demangled,
            string? receiver, bool isProtocol, HashSet<string> known, List<string> prelude, out List<string> arguments, out string why)
        {
            arguments = new List<string>();
            why = string.Empty;
            var labels = Labels(Str(owner, "printedName"));
            if (labels is null || labels.Count != parameters.Count)
            {
                why = UnsupportedReason("its argument labels could not be read from the descriptor");
                return false;
            }
            if (demangled is not null && demangled.Count != parameters.Count)
                demangled = null;
            for (var i = 0; i < parameters.Count; i++)
            {
                var parameter = parameters[i];
                var info = demangled?[i];
                if (info is { Isolated: true })
                {
                    why = UnsupportedReason("an isolated parameter has no call form here");
                    return false;
                }
                // The descriptor drops a function type's convention and isolation, which the demangling keeps.
                var type = info is not null && (info.Type.Contains("@convention(", StringComparison.Ordinal) || info.Type.Contains("@isolated(", StringComparison.Ordinal))
                    ? Demangled.Spell(info.Type, known)
                    : Spell(Str(parameter, "printedName"));
                if (type is null)
                {
                    why = UnsupportedReason("a parameter type cannot be spelled");
                    return false;
                }
                type = SubstituteSelf(type, receiver, isProtocol);
                if (type is null)
                {
                    why = UnsupportedReason("a parameter type names Self outside a type");
                    return false;
                }
                string value;
                if (Str(parameter, "paramValueOwnership") == "InOut")
                {
                    prelude.Add($"var a{i}: {type} = {ValueHelper}()");
                    value = $"&a{i}";
                }
                else if (info is { Const: true })
                {
                    if (Literal(type) is not { } literal)
                    {
                        why = UnsupportedReason($"a _const parameter of type {type} has no literal here");
                        return false;
                    }
                    value = literal;
                }
                else if (info is { Variadic: true })
                {
                    var element = type.StartsWith('[') && type.EndsWith(']') ? type[1..^1] : null;
                    if (element is null)
                    {
                        why = UnsupportedReason("a variadic parameter's element type cannot be read");
                        return false;
                    }
                    value = $"({ValueHelper}() as {element})";
                }
                else if (info is { Autoclosure: true })
                {
                    var produced = Demangled.AutoclosureResult(info.Type) is { } r ? SubstituteSelf(Demangled.Spell(r, known) ?? string.Empty, receiver, isProtocol) : null;
                    if (string.IsNullOrEmpty(produced))
                    {
                        why = UnsupportedReason("an autoclosure parameter's result type cannot be read");
                        return false;
                    }
                    value = $"({ValueHelper}() as {produced})";
                }
                else
                {
                    value = $"({ValueHelper}() as {type})";
                }
                var label = labels[i];
                arguments.Add(label == "_" ? value : $"{ArgumentLabel(label)}: {value}");
            }
            return true;
        }

        private static string? Literal(string type) => type switch
        {
            "Swift.Int" or "Swift.Int8" or "Swift.Int16" or "Swift.Int32" or "Swift.Int64"
                or "Swift.UInt" or "Swift.UInt8" or "Swift.UInt16" or "Swift.UInt32" or "Swift.UInt64" => "0",
            "Swift.Double" or "Swift.Float" => "0.0",
            "Swift.Bool" => "false",
            "Swift.String" or "Swift.StaticString" => "\"\"",
            _ => null,
        };

        /// <summary>A declaration's argument labels from its printed name (<c>f(_:label:)</c>), or null when it has none to read.</summary>
        internal static IReadOnlyList<string>? Labels(string? printedName)
        {
            if (printedName is null)
                return null;
            var open = printedName.IndexOf('(');
            if (open < 0 || !printedName.EndsWith(')'))
                return null;
            var inner = printedName[(open + 1)..^1];
            if (inner.Length == 0)
                return [];
            if (!inner.EndsWith(':'))
                return null;
            return inner[..^1].Split(':');
        }

        /// <summary>
        /// The receiver of a member of <paramref name="types"/> (outermost first): the type spelled
        /// from its module with each type's own generic parameters, or the generic <c>Self</c>
        /// parameter of a protocol. Null for a global.
        /// </summary>
        private static string? Receiver(IReadOnlyList<JsonElement> types, out bool isProtocol)
        {
            isProtocol = false;
            if (types.Count == 0)
                return null;
            if (Str(types[^1], "declKind") == "Protocol")
            {
                isProtocol = true;
                return "T_0_0";
            }
            var parts = new List<string> { Str(types[0], "moduleName") ?? string.Empty };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in types)
            {
                var (own, _) = SplitSignature(Fix(Str(type, "genericSig") ?? string.Empty));
                var added = own.Where(seen.Add).ToList();
                parts.Add(Identifier(Str(type, "name") ?? string.Empty) + (added.Count > 0 ? $"<{string.Join(", ", added)}>" : string.Empty));
            }
            return parts.Any(string.IsNullOrEmpty) ? null : string.Join(".", parts);
        }

        /// <summary>The generic signature in force for <paramref name="node"/>: its own, or its nearest ancestor's.</summary>
        private static string GenericSignature(JsonElement node, ImmutableArray<JsonElement> ancestors)
        {
            if (Str(node, "genericSig") is { Length: > 0 } own)
                return Fix(own);
            for (var i = ancestors.Length - 1; i >= 0; i--)
            {
                if (Str(ancestors[i], "genericSig") is { Length: > 0 } inherited)
                    return Fix(inherited);
            }
            return string.Empty;
        }

        /// <summary><c>&lt;T_0_0, T_0_1 where T_0_0 : P&gt;</c> as its parameters and its requirements.</summary>
        private static (List<string> Parameters, string Requirements) SplitSignature(string signature)
        {
            if (signature.Length < 2 || signature[0] != '<' || signature[^1] != '>')
                return ([], string.Empty);
            var inner = signature[1..^1];
            var where = inner.IndexOf(" where ", StringComparison.Ordinal);
            var head = where < 0 ? inner : inner[..where];
            var requirements = where < 0 ? string.Empty : inner[(where + " where ".Length)..];
            return (ProbeParameter.Matches(head).Select(m => m.Value).Distinct(StringComparer.Ordinal).ToList(), requirements);
        }

        private static string? ResultType(JsonElement result, string? demangledResult, string? receiver, bool isProtocol)
        {
            if (result.ValueKind != JsonValueKind.Object)
                return null;
            // The descriptor drops a function type's convention; an opaque type cannot annotate a binding.
            if (demangledResult is not null && demangledResult.Contains("@convention(", StringComparison.Ordinal))
                return null;
            var type = Spell(Str(result, "printedName"));
            if (type is null || type.Contains("some ", StringComparison.Ordinal))
                return null;
            return SubstituteSelf(type, receiver, isProtocol);
        }

        private static string? SubstituteSelf(string type, string? receiver, bool isProtocol)
        {
            if (!DynamicSelf.IsMatch(type))
                return type;
            if (receiver is null)
                return null;
            return DynamicSelf.Replace(type, isProtocol ? "T_0_0" : receiver);
        }

        /// <summary>A descriptor type as Swift source, its generic parameters renamed for the probe.</summary>
        private static string? Spell(string? printedName)
        {
            if (string.IsNullOrWhiteSpace(printedName))
                return null;
            var type = Fix(printedName.Trim());
            foreach (var prefix in new[] { "inout ", "__owned ", "__shared ", "sending ", "borrowing ", "consuming " })
            {
                if (type.StartsWith(prefix, StringComparison.Ordinal))
                    type = type[prefix.Length..];
            }
            return type;
        }

        private static string Fix(string text) => GenericParameter.Replace(text, "T_$1_$2");

        private static string Identifier(string name) => Keywords.Contains(name) ? $"`{name}`" : name;

        private static string ArgumentLabel(string label) => label is "inout" or "var" or "let" ? $"`{label}`" : label;
    }

    /// <summary>Reads what the descriptor leaves out of a declaration from its demangled symbol.</summary>
    internal static class Demangled
    {
        /// <summary>One parameter as the demangling prints it.</summary>
        /// <param name="Type">Its type, without ownership or <c>_const</c> and without a variadic <c>...</c>.</param>
        internal sealed record Parameter(string Type, bool Variadic, bool Const, bool Isolated, bool Autoclosure);

        private static readonly Regex Label = new(@"^(?:[A-Za-z_]\w*|`[^`]+`): ", RegexOptions.Compiled);
        private static readonly Regex Name = new(@"(?<![\w.])(?<letters>[A-Z]+)(?<depth>\d*)(?!\w)", RegexOptions.Compiled);

        /// <summary>
        /// The parameters and result of a function, initializer or subscript accessor
        /// (<c>M.T.f(_: Swift.Int, label: _const Swift.Int...) throws -> Swift.Bool</c>), or nulls when the
        /// demangling is not of a call with a parameter list.
        /// </summary>
        internal static (IReadOnlyList<Parameter>? Parameters, string? Result) Parse(string demangled)
        {
            var arrow = TopLevel(demangled, " -> ");
            if (arrow < 0)
                return (null, null);
            var head = demangled[..arrow].TrimEnd();
            var result = demangled[(arrow + " -> ".Length)..].Trim();
            while (true)
            {
                if (head.EndsWith(" async", StringComparison.Ordinal))
                    head = head[..^" async".Length];
                else if (head.EndsWith(" throws", StringComparison.Ordinal))
                    head = head[..^" throws".Length];
                else if (head.EndsWith(" rethrows", StringComparison.Ordinal))
                    head = head[..^" rethrows".Length];
                else if (head.EndsWith(')') && Open(head, head.Length - 1) is var open and > 0 && head[..open].EndsWith(" throws", StringComparison.Ordinal))
                    head = head[..(open - " throws".Length)];
                else
                    break;
            }
            if (!head.EndsWith(')') || Open(head, head.Length - 1) is not (var start and >= 0))
                return (null, result);
            var inner = head[(start + 1)..^1];
            var parameters = new List<Parameter>();
            foreach (var raw in Split(inner))
            {
                var text = raw.Trim();
                if (text.Length == 0)
                    continue;
                if (Label.Match(text) is { Success: true } label)
                    text = text[label.Length..];
                bool isConst = false, isolated = false;
                for (var changed = true; changed;)
                {
                    changed = false;
                    foreach (var prefix in new[] { "_const ", "inout ", "__owned ", "__shared ", "sending ", "isolated ", "borrowing ", "consuming " })
                    {
                        if (!text.StartsWith(prefix, StringComparison.Ordinal))
                            continue;
                        isConst |= prefix == "_const ";
                        isolated |= prefix == "isolated ";
                        text = text[prefix.Length..];
                        changed = true;
                    }
                }
                var variadic = text.EndsWith("...", StringComparison.Ordinal);
                if (variadic)
                    text = text[..^3];
                parameters.Add(new Parameter(text, variadic, isConst, isolated, text.StartsWith("@autoclosure ", StringComparison.Ordinal)));
            }
            return (parameters, result);
        }

        /// <summary>What an autoclosure parameter's function type produces.</summary>
        internal static string? AutoclosureResult(string type)
        {
            var arrow = TopLevel(type, " -> ");
            return arrow < 0 ? null : type[(arrow + " -> ".Length)..].Trim();
        }

        /// <summary>
        /// A demangled type as Swift source: its generic parameters (<c>A</c>, <c>B1</c>, ...) renamed for
        /// the probe when they are among <paramref name="known"/>, and anything else left as printed.
        /// </summary>
        internal static string? Spell(string type, IReadOnlySet<string> known) => Name.Replace(type, m =>
        {
            long index = 0, scale = 1;
            foreach (var letter in m.Groups["letters"].Value)
            {
                index += (letter - 'A') * scale;
                scale *= 26;
                if (scale > 1_000_000)
                    return m.Value;
            }
            var depth = m.Groups["depth"].Value.Length == 0 ? 0 : long.Parse(m.Groups["depth"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var name = $"T_{depth}_{index}";
            return known.Contains(name) ? name : m.Value;
        });

        private static int TopLevel(string text, string token)
        {
            var depth = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c is '(' or '[' or '<' or '{')
                    depth++;
                else if (c is ')' or ']' or '}' || (c == '>' && (i == 0 || text[i - 1] != '-')))
                    depth--;
                else if (depth == 0 && string.CompareOrdinal(text, i, token, 0, token.Length) == 0)
                    return i;
            }
            return -1;
        }

        private static int Open(string text, int close)
        {
            var depth = 0;
            for (var i = close; i >= 0; i--)
            {
                var c = text[i];
                if (c is ')' or ']' || (c == '>' && (i == 0 || text[i - 1] != '-')))
                    depth++;
                else if (c is '(' or '[' or '<')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }
            return -1;
        }

        private static IEnumerable<string> Split(string text)
        {
            var depth = 0;
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c is '(' or '[' or '<')
                    depth++;
                else if (c is ')' or ']' || (c == '>' && (i == 0 || text[i - 1] != '-')))
                    depth--;
                else if (c == ',' && depth == 0)
                {
                    yield return text[start..i];
                    start = i + 1;
                }
            }
            yield return text[start..];
        }
    }

    // ---- descriptor access ----

    internal static string Kind(JsonElement node) => Str(node, "kind") ?? string.Empty;

    internal static string? Str(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    internal static IReadOnlyList<JsonElement> Children(JsonElement node, string name = "children") =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToList()
            : [];

    private static bool HasAttribute(JsonElement node, string attribute) =>
        Children(node, "declAttributes").Any(a => a.ValueKind == JsonValueKind.String && a.GetString() == attribute);

    private static bool IsSpi(JsonElement node) =>
        Children(node, "spi_group_names").Count > 0 || HasAttribute(node, "SPIAccessControl");
}

/// <summary>A declaration in an ABI descriptor: its node, the nodes it is nested in (outermost first), and the module it belongs to.</summary>
internal sealed record AbiDeclaration(JsonElement Node, ImmutableArray<JsonElement> Ancestors, string Module);

/// <summary>The declarations of a set of ABI descriptors, by mangled name.</summary>
internal sealed class AbiDescriptorIndex : IDisposable
{
    private static readonly HashSet<string> Declarations = new(StringComparer.Ordinal)
    {
        "Function", "Constructor", "Accessor", "Var", "Subscript",
    };

    private readonly List<JsonDocument> _documents = new();
    private readonly Dictionary<string, AbiDeclaration> _bySymbol = new(StringComparer.Ordinal);

    /// <summary>Reads the descriptor at <paramref name="path"/>; a declaration an earlier descriptor already holds keeps the earlier one.</summary>
    public void Load(string path)
    {
        using var stream = File.OpenRead(path);
        Add(JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 1024 }));
    }

    /// <summary>Reads a descriptor's JSON text.</summary>
    public void LoadText(string json) => Add(JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 1024 }));

    private void Add(JsonDocument document)
    {
        _documents.Add(document);
        if (document.RootElement.TryGetProperty("ABIRoot", out var root))
            Walk(root, new List<JsonElement>());
    }

    private void Walk(JsonElement node, List<JsonElement> ancestors)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return;
        var kind = SwiftLoweringProbe.Kind(node);
        if (Declarations.Contains(kind))
        {
            var module = SwiftLoweringProbe.Str(node, "moduleName")
                ?? ancestors.Select(a => SwiftLoweringProbe.Str(a, "moduleName")).LastOrDefault(m => m is not null)
                ?? string.Empty;
            var declaration = new AbiDeclaration(node, ancestors.ToImmutableArray(), module);
            foreach (var key in new[] { SwiftLoweringProbe.Str(node, "mangledName"), SwiftLoweringProbe.Str(node, "usr") })
            {
                if (key is null)
                    continue;
                var symbol = key.StartsWith("$s", StringComparison.Ordinal) ? key
                    : key.StartsWith("s:", StringComparison.Ordinal) ? "$s" + key[2..]
                    : null;
                if (symbol is not null)
                    _bySymbol.TryAdd(symbol, declaration);
            }
        }
        if (kind is "TypeNominal" or "TypeFunc" or "TypeAlias")
            return;
        ancestors.Add(node);
        foreach (var child in SwiftLoweringProbe.Children(node).Concat(SwiftLoweringProbe.Children(node, "accessors")))
            Walk(child, ancestors);
        ancestors.RemoveAt(ancestors.Count - 1);
    }

    /// <summary>
    /// The declaration <paramref name="target"/> is the entry of: itself, the member a dispatch thunk
    /// (<c>Tj</c>) dispatches, or the initializer an allocating entry (<c>fC</c>) allocates for (the
    /// descriptor names an initializer by its initializing entry, <c>fc</c>). Null when none of the
    /// descriptors declares it.
    /// </summary>
    public AbiDeclaration? Find(string target)
    {
        var symbol = target.EndsWith("Tj", StringComparison.Ordinal) ? target[..^2] : target;
        if (_bySymbol.TryGetValue(symbol, out var declaration))
            return declaration;
        if (symbol.EndsWith("fC", StringComparison.Ordinal) && _bySymbol.TryGetValue(symbol[..^2] + "fc", out declaration))
            return declaration;
        return null;
    }

    public void Dispose()
    {
        foreach (var document in _documents)
            document.Dispose();
        _documents.Clear();
    }
}
