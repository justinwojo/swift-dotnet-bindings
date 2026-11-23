// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using SwiftBindings.TransitionGate;

namespace SwiftBindings.Tasks;

/// <summary>
/// Refuses to build an app against a compiled binding whose Swift transitions the app's engine
/// cannot be shown to run safely (SWIFTBIND132). Runs in every app that resolves
/// <c>SwiftBindings.Runtime</c>, after reference resolution, on the engines that mishandle such
/// transitions: Mono (the iOS and tvOS simulators, Mac Catalyst, device Mono AOT) and .NET 11's
/// CoreCLR on iOS, tvOS and Mac Catalyst. The targets decide the engine; this task inspects.
/// </summary>
/// <remarks>
/// Every resolved reference that can make a Swift transition (<see cref="ConsumerTransitionCheck.MayHaveSwiftTransitions"/>)
/// is checked (<see cref="ConsumerTransitionCheck.Check"/>): package, project and plain references
/// alike, direct or transitive, from the implementation assembly an app loads (a reference
/// assembly drops the private P/Invokes). Results are cached by MVID in <see cref="CacheFile"/>,
/// keyed also on the task, the other candidates' MVIDs and the native references, so an
/// incremental build reads no assembly twice.
/// </remarks>
public sealed class SwiftTransitionConsumerCheck : Microsoft.Build.Utilities.Task
{
    /// <summary>The diagnostic code of a refused reference.</summary>
    public const string Code = "SWIFTBIND132";

    /// <summary>How many problems a diagnostic names before it summarizes the rest.</summary>
    private const int Named = 3;

    /// <summary>The compile references (<c>@(ReferencePath)</c>).</summary>
    [Required]
    public ITaskItem[] CompileReferences { get; set; } = [];

    /// <summary>The copy-local runtime assemblies (<c>@(ReferenceCopyLocalPaths)</c>), which win over a compile reference of the same name.</summary>
    public ITaskItem[] RuntimeReferences { get; set; } = [];

    /// <summary>The app's native references (<c>@(NativeReference)</c>): the images a routed library's hash and exports are checked in.</summary>
    public ITaskItem[] NativeReferences { get; set; } = [];

    /// <summary>Packages (or, for a project or plain reference, assemblies) whose refusal is downgraded to a warning.</summary>
    public ITaskItem[] Suppressions { get; set; } = [];

    /// <summary>The engine this build runs on, as the diagnostic names it.</summary>
    [Required]
    public string Engine { get; set; } = "";

    [Required]
    public string CacheFile { get; set; } = "";

    [Required]
    public string ScratchDirectory { get; set; } = "";

    /// <summary>Every assembly checked, with its <c>Verdict</c>, <c>State</c> and <c>Origin</c>.</summary>
    [Output]
    public ITaskItem[] Checked { get; private set; } = [];

    private sealed record Candidate(string Path, string Name, Guid Mvid, string Origin, string SuppressionKey);

    private sealed class CacheDocument
    {
        public string Context { get; set; } = "";
        public Dictionary<string, CachedResult> Results { get; set; } = new();
    }

    private sealed class CachedResult
    {
        public string Name { get; set; } = "";
        public string Verdict { get; set; } = "";
        public string? State { get; set; }
        public List<string> Problems { get; set; } = new();
        public List<string> Unverified { get; set; } = new();
        public string Remedy { get; set; } = nameof(ConsumerTransitionRemedy.None);

        /// <summary>Whether a native image of the binding's wrapper is not the one its assembly was generated with.</summary>
        public bool WrapperMismatch { get; set; }

        /// <summary>Each recorded bridge withdrawal: its module, reason, and the members it restored with the engines they refuse on.</summary>
        public List<CachedWithdrawal> Withdrawals { get; set; } = new();
    }

    private sealed class CachedWithdrawal
    {
        public string Module { get; set; } = "";
        public string Library { get; set; } = "";
        public string Reason { get; set; } = "";
        public int Members { get; set; }
        public string Refusals { get; set; } = "";
    }

    public override bool Execute()
    {
        var candidates = Candidates();
        var natives = NativeReferences.Select(n => n.GetMetadata("FullPath")).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var references = CompileReferences.Concat(RuntimeReferences).Select(r => r.GetMetadata("FullPath"))
            .Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var context = Context(candidates, references, natives);
        var cache = ReadCache(context);
        var referenceDirectories = references
            .Select(Path.GetDirectoryName)
            .Where(d => !string.IsNullOrEmpty(d)).Select(d => d!)
            .Distinct(StringComparer.Ordinal).ToList();

        var images = new NativeImages(natives);
        var checkedItems = new List<ITaskItem>();
        var scanned = 0;
        foreach (var candidate in candidates)
        {
            var key = candidate.Mvid.ToString("N");
            if (!cache.Results.TryGetValue(key, out var result))
            {
                scanned++;
                result = Inspect(candidate, images, referenceDirectories);
                cache.Results[key] = result;
            }
            var item = new TaskItem(candidate.Path);
            item.SetMetadata("Verdict", result.Verdict);
            item.SetMetadata("State", result.State ?? "");
            item.SetMetadata("Origin", candidate.Origin);
            checkedItems.Add(item);
            Report(candidate, result);
        }
        Checked = checkedItems.ToArray();
        Log.LogMessage(MessageImportance.Low, "Swift transition check ({0}): {1} reference(s) can make Swift transitions, {2} inspected, {3} from the cache.",
            Engine, candidates.Count, scanned, candidates.Count - scanned);
        foreach (var unused in Suppressions.Select(s => s.ItemSpec)
                     .Where(s => !candidates.Any(c => string.Equals(c.SuppressionKey, s, StringComparison.OrdinalIgnoreCase))))
            Log.LogMessage(MessageImportance.Normal, "SwiftBindingsTransitionSuppress names {0}, which no reference of this app that makes Swift transitions comes from.", unused);
        WriteCache(cache);
        return !Log.HasLoggedErrors;
    }

    /// <summary>The references that can make Swift transitions, one per assembly name, the runtime copy first.</summary>
    private List<Candidate> Candidates()
    {
        var byName = new Dictionary<string, ITaskItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in RuntimeReferences.Concat(CompileReferences))
        {
            var path = item.GetMetadata("FullPath");
            if (!string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase) || IsPlatform(item) || !File.Exists(path))
                continue;
            byName.TryAdd(Path.GetFileNameWithoutExtension(path), item);
        }

        var candidates = new List<Candidate>();
        foreach (var item in byName.Values.OrderBy(i => i.GetMetadata("FullPath"), StringComparer.Ordinal))
        {
            var path = item.GetMetadata("FullPath");
            var (name, mvid, may) = Identify(path);
            if (!may)
                continue;
            var package = item.GetMetadata("NuGetPackageId");
            var project = item.GetMetadata("MSBuildSourceProjectFile");
            var origin = package.Length > 0 ? $"package {package} {item.GetMetadata("NuGetPackageVersion")}".TrimEnd()
                : project.Length > 0 ? $"project {Path.GetFileName(project)}"
                : $"reference {path}";
            candidates.Add(new Candidate(path, name, mvid, origin, package.Length > 0 ? package : name));
        }
        return candidates;
    }

    /// <summary>A framework or runtime-pack assembly, which is never a binding.</summary>
    private static bool IsPlatform(ITaskItem item) =>
        item.GetMetadata("FrameworkReferenceName").Length > 0
        || item.GetMetadata("FrameworkName").Length > 0
        || item.GetMetadata("NuGetPackageId").StartsWith("Microsoft.NETCore.App.", StringComparison.OrdinalIgnoreCase);

    /// <summary>The assembly's name and MVID, and whether it can make a Swift transition.</summary>
    private static (string Name, Guid Mvid, bool May) Identify(string path)
    {
        var image = File.ReadAllBytes(path);
        try
        {
            using var pe = new PEReader(new MemoryStream(image, writable: false));
            if (!pe.HasMetadata)
                return (Path.GetFileNameWithoutExtension(path), Guid.Empty, false);
            var md = pe.GetMetadataReader();
            var name = md.IsAssembly ? md.GetString(md.GetAssemblyDefinition().Name) : Path.GetFileNameWithoutExtension(path);
            return (name, md.GetGuid(md.GetModuleDefinition().Mvid), ConsumerTransitionCheck.MayHaveSwiftTransitions(image));
        }
        catch (BadImageFormatException)
        {
            // Not a managed assembly: nothing to check.
            return (Path.GetFileNameWithoutExtension(path), Guid.Empty, false);
        }
    }

    private CachedResult Inspect(Candidate candidate, IReadOnlyList<PackagedNative> images, IReadOnlyList<string> referenceDirectories)
    {
        var assembly = new PackagedAssembly(candidate.Path, File.ReadAllBytes(candidate.Path),
            new[] { Path.GetDirectoryName(candidate.Path)! }.Concat(referenceDirectories).Distinct(StringComparer.Ordinal).ToArray());
        var scratch = Path.Combine(ScratchDirectory, candidate.Mvid.ToString("N"));
        try
        {
            var result = ConsumerTransitionCheck.Check(assembly, images, requireNativeImages: false, scratch);
            return new CachedResult
            {
                Name = result.AssemblyName,
                Verdict = result.Verdict.ToString(),
                State = result.State.ToString(),
                Problems = result.Problems.ToList(),
                Unverified = result.UnverifiedLibraries.ToList(),
                Remedy = result.Remedy.ToString(),
                WrapperMismatch = result.WrapperMismatch,
                Withdrawals = result.Withdrawals.Select(w => new CachedWithdrawal
                {
                    Module = w.Module,
                    Library = w.Library,
                    Reason = w.Reason,
                    Members = w.Transitions.Length,
                    Refusals = w.Describe(),
                }).ToList(),
            };
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidDataException or IOException)
        {
            return new CachedResult
            {
                Name = candidate.Name,
                Verdict = nameof(ConsumerTransitionVerdict.Refused),
                Problems = [$"its transitions could not be inspected: {ex.Message}"],
                Remedy = nameof(ConsumerTransitionRemedy.Regenerate),
            };
        }
        finally
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
    }

    private void Report(Candidate candidate, CachedResult result)
    {
        foreach (var library in result.Unverified)
            Log.LogMessage(MessageImportance.Normal,
                result.Withdrawals.Any(w => w.Library == library)
                    ? "{0} ({1}) records its bridges in {2} as withdrawn, and no native reference of this app provides {2}; that they are gone was not checked."
                    : "{0} ({1}) routes Swift calls through bridges in {2}, which no native reference of this app provides; its hash and exports were not checked.",
                Path.GetFileName(candidate.Path), candidate.Origin, library);
        if (result.Verdict != nameof(ConsumerTransitionVerdict.Refused))
        {
            Log.LogMessage(MessageImportance.Low, "{0} ({1}): {2}{3}.", Path.GetFileName(candidate.Path), candidate.Origin, result.Verdict,
                result.State is null ? "" : $" ({result.State})");
            foreach (var withdrawal in result.Withdrawals)
                Log.LogMessage(MessageImportance.High,
                    "{0} ({1}) is a partial binding: {2}'s bridges were withdrawn ({3}), so {4} member(s) keep their guarded direct import and throw before Swift runs on the engines named: {5}. This build runs on {6}.",
                    Path.GetFileName(candidate.Path), candidate.Origin, withdrawal.Module, withdrawal.Reason, withdrawal.Members, withdrawal.Refusals, Engine);
            return;
        }

        var suppressed = Suppressions.Any(s => string.Equals(s.ItemSpec, candidate.SuppressionKey, StringComparison.OrdinalIgnoreCase));
        var message = Diagnostic(candidate, result, Engine, suppressed);
        if (suppressed)
            Log.LogWarning(null, Code, null, null, 0, 0, 0, 0, "{0}", message);
        else
            Log.LogError(null, Code, null, null, 0, 0, 0, 0, "{0}", message);
        foreach (var problem in result.Problems)
            Log.LogMessage(MessageImportance.Normal, "  {0}: {1}", Path.GetFileName(candidate.Path), problem);
    }

    /// <summary>The text of SWIFTBIND132.</summary>
    private static string Diagnostic(Candidate candidate, CachedResult result, string engine, bool suppressed)
    {
        var count = result.Problems.Count;
        var what = result.State switch
        {
            nameof(TransitionEvidenceState.AbsentNonPrimitive) =>
                $"carries no Swift transition manifest, and {count} of its Swift transitions are not primitive-only calls",
            nameof(TransitionEvidenceState.TamperedOrMismatched) =>
                $"carries a Swift transition manifest that is tampered with or does not describe the assembly ({count} problem(s))",
            nameof(TransitionEvidenceState.PresentValid) =>
                $"carries a valid Swift transition manifest, but {count} check(s) of its transitions fail",
            _ => $"has Swift transitions that could not be checked ({count} problem(s))",
        };
        var named = string.Join("; ", result.Problems.Take(Named));
        var rest = count > Named ? $"; and {count - Named} more (listed below)" : "";
        var subject = $"{Path.GetFileName(candidate.Path)} ({candidate.Origin})";
        var remedy = Remedy(candidate, result);
        var lead = suppressed ? $"{subject} is allowed by <SwiftBindingsTransitionSuppress Include=\"{candidate.SuppressionKey}\" />, but " : $"{subject} ";
        var tail = suppressed
            ? "The suppression accepts the risk for this package only; remove it once the package is updated."
            : $"To accept the risk for this package only, add <SwiftBindingsTransitionSuppress Include=\"{candidate.SuppressionKey}\" /> (the error becomes this warning).";
        return $"{lead}{what}: {named}{rest}. This build runs on {engine}. {AffectedEngines(result)} {remedy} {tail}";
    }

    /// <summary>
    /// Which engines the refusal's cause breaks. An unsafe transition breaks only the engines that
    /// mishandle it; a wrapper that is not the assembly's own breaks the binding on every engine,
    /// although only the builds this check runs in detect it.
    /// </summary>
    private static string AffectedEngines(CachedResult result)
    {
        const string transitions = "Mono (iOS and tvOS simulators, Mac Catalyst, device Mono AOT) and the .NET 11 CoreCLR interpreter (Debug, and whatever ReadyToRun leaves to it in Release)";
        if (!result.WrapperMismatch)
            return $"Affected engines: {transitions}; NativeAOT is not affected.";
        const string wrapper = "A native wrapper that is not the one the assembly was generated with breaks the binding on every engine, NativeAOT and macOS included: "
                               + "only the builds this check runs in detect it, and SWIFTBIND133 refuses such a package when it is packed or merged.";
        return result.Remedy == nameof(ConsumerTransitionRemedy.WrapperMismatch)
            ? wrapper
            : $"Affected engines of its transitions: {transitions}. {wrapper}";
    }

    /// <summary>What SWIFTBIND132 tells the author to do, from what refused the assembly and where it comes from.</summary>
    private static string Remedy(Candidate candidate, CachedResult result)
    {
        var package = candidate.Origin.StartsWith("package ", StringComparison.Ordinal);
        var withdrawal = result.Withdrawals.FirstOrDefault();
        var relink = withdrawal is null ? "" : $"{withdrawal.Module}'s bridges were withdrawn because {withdrawal.Reason}. ";
        return result.Remedy switch
        {
            nameof(ConsumerTransitionRemedy.CleanRebuild) => relink + (package
                ? $"Its native images keep the bridges or manifest hash of an earlier routing: update {candidate.SuppressionKey} to a version packed from a clean build of the binding."
                : "Its native images keep the bridges or manifest hash of an earlier routing: rebuild the binding clean (delete its bin/ and obj/)."),
            nameof(ConsumerTransitionRemedy.ReceiptMismatch) => relink
                + "The relinked wrapper contradicts the lowering receipt the bridges were authorized by, so the restored imports cannot be shown safe. "
                + (package ? $"Update {candidate.SuppressionKey} to a version whose wrapper builds reproducibly." : "Make the wrapper's inputs stable between its compiles, then rebuild the binding clean."),
            nameof(ConsumerTransitionRemedy.WithdrawalIncoherent) => relink
                + "The binding does not hold up as the withdrawal it records, which a clean rebuild does not fix: "
                + (package ? $"update {candidate.SuppressionKey} to a version that routes its bridges." : "fix the relink failure so the binding routes its bridges."),
            nameof(ConsumerTransitionRemedy.WrapperMismatch) => package
                ? $"Its native wrapper is not the one its assembly was generated with (a wrapper of another copy or build of the package): update {candidate.SuppressionKey} to a version packed from one clean build per copy."
                : "Its native wrapper is not the one its assembly was generated with: rebuild the binding clean (delete its bin/ and obj/).",
            _ => package
                ? $"Update {candidate.SuppressionKey} to a version built with the current SwiftBindings.Sdk, or regenerate the binding with the current SDK."
                : "Regenerate the binding with the current SwiftBindings.Sdk (a clean build of the binding project).",
        };
    }

    /// <summary>
    /// What the cached results depend on besides each assembly's own MVID: the task, the candidates,
    /// every other reference (an inspection resolves carrier types through them, so replacing one can
    /// change a verdict) and the native references, the last two by path, size and write time.
    /// </summary>
    private static string Context(IReadOnlyList<Candidate> candidates, IReadOnlyList<string> references, IReadOnlyList<string> natives)
    {
        var text = new StringBuilder();
        text.Append(typeof(SwiftTransitionConsumerCheck).Assembly.ManifestModule.ModuleVersionId).Append('\n');
        foreach (var candidate in candidates)
            text.Append(candidate.Name).Append('|').Append(candidate.Mvid).Append('\n');
        foreach (var reference in references)
        {
            var info = new FileInfo(reference);
            text.Append(reference).Append('|').Append(info.Exists ? info.Length : -1).Append('|').Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0).Append('\n');
        }
        foreach (var native in natives)
        {
            text.Append(native).Append('\n');
            IEnumerable<string> files = Directory.Exists(native) ? Directory.EnumerateFiles(native, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                : File.Exists(native) ? [native] : [];
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                text.Append(' ').Append(file).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private CacheDocument ReadCache(string context)
    {
        try
        {
            if (File.Exists(CacheFile) && JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(CacheFile)) is { } read && read.Context == context)
                return read;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.LogMessage(MessageImportance.Low, "The Swift transition check cache {0} is not read: {1}", CacheFile, ex.Message);
        }
        return new CacheDocument { Context = context };
    }

    private void WriteCache(CacheDocument cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(cache));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogMessage(MessageImportance.Low, "The Swift transition check cache {0} is not written: {1}", CacheFile, ex.Message);
        }
    }

    /// <summary>The native images of the app's native references, read on demand per library.</summary>
    private sealed class NativeImages(IReadOnlyList<string> references) : IReadOnlyList<PackagedNative>
    {
        private List<PackagedNative>? all;

        private List<PackagedNative> All => all ??= Load();

        // ConsumerTransitionCheck filters by library, so every reference's images of every library
        // are offered; a reference contributes the images of the library it is named after.
        private List<PackagedNative> Load()
        {
            var images = new List<PackagedNative>();
            foreach (var reference in references)
            {
                var library = Path.GetFileNameWithoutExtension(reference.TrimEnd('/', '\\'));
                if (library.StartsWith("lib", StringComparison.Ordinal) && reference.EndsWith(".dylib", StringComparison.Ordinal))
                    library = library[3..];
                foreach (var path in ConsumerTransitionCheck.NativeReferenceImages(reference, library))
                    images.Add(new PackagedNative(library, path, File.ReadAllBytes(path)));
            }
            return images;
        }

        public PackagedNative this[int index] => All[index];
        public int Count => All.Count;
        public IEnumerator<PackagedNative> GetEnumerator() => All.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
