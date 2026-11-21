// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// This file is link-compiled into projects that do not enable nullable reference types, so the
// annotations below are opted into locally rather than inherited from a csproj.
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using BindingsGeneration;

/// <summary>What the OS load-safety gate found in one wrapper slice.</summary>
/// <remarks>
/// <see cref="SafeStrongLoads"/> are the OS-resident loads the linker kept strong although every
/// import from them is weak: an umbrella re-exporting a library only its allowed clients may link.
/// </remarks>
internal sealed record OsLoadVerdict(
    int OsResidentLoadCount, int WeakOsResidentLoadCount, IReadOnlyList<string> SafeStrongLoads, IReadOnlyList<string> Failures);

/// <summary>
/// A provider whose shipped symbols the load-safety gate knows: an OS-resident dylib, checked against
/// the shipped inventory, or a supplied provider, checked against its own list.
/// </summary>
/// <remarks><see cref="OsVersion"/> is the OS whose libraries the provider's symbol list describes.</remarks>
internal sealed record CoveredProvider(string InstallName, Func<string, bool> Ships, string? OsVersion = null);

/// <summary>A Swift symbol an image imports, or a P/Invoke names, that its provider's inventory OS lacks.</summary>
internal sealed record AbsentSymbol(string Arch, string Symbol, string InstallName, string? OsVersion);

/// <summary>What the absent-symbol check found in one image.</summary>
internal sealed record OsAbsentImportVerdict(int CoveredSwiftImports, IReadOnlyList<AbsentSymbol> Absent, IReadOnlyList<string> Failures);

/// <summary>Which exports of one image reach which absent imports, and what could not be attributed.</summary>
internal sealed record OsAbsentReachVerdict(IReadOnlyDictionary<string, IReadOnlyList<AbsentSymbol>> ByExport, IReadOnlyList<string> Failures);

/// <summary>
/// The pure verdict behind the OS load-safety gate (load-safety property P1: a missing OS symbol can
/// never stop a wrapper loading). Every dylib a wrapper slice loads from under <c>/System/</c> must be
/// loaded with <c>LC_LOAD_WEAK_DYLIB</c>, which also makes every import from it weak, or be an umbrella
/// the linker keeps strong for a restricted re-export with every import from it weak; and the slice
/// must load at least one, so a read that found no load commands at all cannot pass. P2 requires every
/// import of a symbol the inventory's OS lacks to be weak and attributes it to the exports that reach it
/// (<see cref="WrapperImportReach"/>), so the gate can hold every P/Invoke of those exports to a refusal
/// in the compiled IL (<c>OsFloorGuardCheck</c>). BCL-only plus the shared
/// <see cref="OsFrameworkWeakLinking"/> parsers, so the unit tests link-compile it.
/// </summary>
internal static class OsLoadSafetyAudit
{
    /// <summary>
    /// The load half of the gate: every OS-resident load must be weak, or be one a missing symbol cannot
    /// fail (<see cref="OsFrameworkWeakLinking.UnsafeStrongLoads"/>: an umbrella the linker keeps strong
    /// for a restricted re-export, every import from it weak, judged against <paramref name="sdkPath"/>, and
    /// present on <paramref name="inventory"/>'s OS).
    /// </summary>
    public static OsLoadVerdict EvaluateLoads(
        string leg, string binary, IReadOnlyList<OsFrameworkWeakLinking.LoadedDylib> loads,
        IReadOnlyList<OsFrameworkWeakLinking.ImportedSymbol> imports, string? sdkPath, OsSymbolInventory inventory)
    {
        var failures = new List<string>();
        var os = loads.Where(l => OsFrameworkWeakLinking.IsOsResident(l.InstallName)).ToList();
        var weak = os.Count(l => l.Kind == OsFrameworkWeakLinking.LoadKind.Weak);
        if (os.Count == 0)
            failures.Add($"{leg}: {binary} loads no OS-resident dylib at all; the load commands were not read, " +
                         "or the wrapper no longer links the OS frameworks this gate exists to check.");
        var strong = OsFrameworkWeakLinking.StrongOsResidentLoads(loads);
        var unsafeLoads = strong.Count == 0 ? strong : OsFrameworkWeakLinking.UnsafeStrongLoads(strong, imports, sdkPath, inventory);
        foreach (var load in unsafeLoads)
            failures.Add($"{leg}: {binary} loads {load} strongly; every /System/ dylib a wrapper loads must be weak-linked " +
                         "(the linker keeps only an umbrella re-exporting a restricted library strong, and then every import from it must be weak).");
        return new OsLoadVerdict(os.Count, weak, strong.Except(unsafeLoads, StringComparer.Ordinal).ToList(), failures);
    }

    /// <summary>
    /// The platform variant (the inventory's .tbd target suffix) of an xcframework slice directory:
    /// <c>ios-arm64</c> is <c>ios</c>, <c>ios-arm64_x86_64-simulator</c> is <c>ios-simulator</c>,
    /// <c>ios-arm64_x86_64-maccatalyst</c> is <c>maccatalyst</c>.
    /// </summary>
    public static string SliceVariant(string sliceId)
    {
        var parts = sliceId.Split('-');
        var platform = parts[0];
        if (parts.Length > 2 && parts[^1] == "simulator")
            return platform + "-simulator";
        if (parts.Length > 2 && parts[^1] == "maccatalyst")
            return "maccatalyst";
        return platform;
    }

    /// <summary>
    /// Load-safety P2 for one linked image: every Swift symbol it imports from a covered provider that
    /// the provider does not ship must be imported weakly, so it binds to null rather than stopping the
    /// image loading; those imports are returned for <see cref="AttributeAbsentImports"/>, which finds the
    /// exports that reach them. An import from a dylib the image does not load fails, since its provider
    /// cannot be resolved.
    /// </summary>
    public static OsAbsentImportVerdict EvaluateImports(
        string label, IReadOnlyList<OsFrameworkWeakLinking.ImportedSymbol> imports, IReadOnlyList<OsFrameworkWeakLinking.LoadedDylib> loads,
        Func<string, CoveredProvider?> coverage)
    {
        var byLeaf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var load in loads)
            byLeaf.TryAdd(OsFrameworkWeakLinking.ImportLeaf(load.InstallName), load.InstallName);

        var failures = new List<string>();
        var absent = new List<AbsentSymbol>();
        int covered = 0;
        foreach (var import in imports)
        {
            if (!import.Symbol.StartsWith("_$s", StringComparison.Ordinal) || import.From.StartsWith('<'))
                continue;
            if (!byLeaf.TryGetValue(import.From, out var installName))
            {
                failures.Add($"{label} [{import.Arch}]: imports {import.Symbol} from '{import.From}', which it does not load.");
                continue;
            }
            if (coverage(installName) is not { } provider)
                continue;
            covered++;
            if (provider.Ships(import.Symbol[1..]))
                continue;
            if (!import.Weak)
                failures.Add($"{label} [{import.Arch}]: imports {import.Symbol} from {installName} strongly, and the inventory's OS does not ship it.");
            else
                absent.Add(new AbsentSymbol(import.Arch, import.Symbol, installName, provider.OsVersion));
        }
        return new OsAbsentImportVerdict(covered, absent.Distinct().ToList(), failures);
    }

    /// <summary>
    /// Attributes each absent import of one image to the exports that reach it, read from the image's
    /// arm64 code (<paramref name="arm64"/>). Fails closed: an arm64 absent import no export reaches
    /// (referenced only from data the reach does not follow, from code no export calls, or from nothing
    /// the read follows at all), and an
    /// absent import of another architecture the arm64 code does not also import, since nothing read
    /// that architecture's reach.
    /// </summary>
    public static OsAbsentReachVerdict AttributeAbsentImports(string label, IReadOnlyList<AbsentSymbol> absent, WrapperImportReach arm64)
    {
        var failures = new List<string>();
        var arm64Absent = absent.Where(a => a.Arch == "arm64").GroupBy(a => a.Symbol, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var other in absent.Where(a => a.Arch != "arm64" && !arm64Absent.ContainsKey(a.Symbol)))
            failures.Add($"{label} [{other.Arch}]: imports {other.Symbol} from {other.InstallName}, which the inventory's OS does not ship, " +
                         "and its arm64 code does not, so no read of the image attributes it to an export.");
        foreach (var (import, holders) in arm64.Unattributed(arm64Absent.Keys))
            failures.Add(holders.Count > 0
                ? $"{label} [arm64]: {import}, which the inventory's OS does not ship, is referenced by {string.Join(", ", holders)} " +
                  "and reached from no export, so no P/Invoke can be held to a guard for it."
                : $"{label} [arm64]: {import}, which the inventory's OS does not ship, is imported but referenced by nothing the " +
                  "read of its code and fixups follows, so no P/Invoke can be held to a guard for it.");
        var byExport = new Dictionary<string, IReadOnlyList<AbsentSymbol>>(StringComparer.Ordinal);
        foreach (var export in arm64.Exports)
        {
            var reached = arm64.ImportsReachedFrom(export).Where(arm64Absent.ContainsKey).Select(i => arm64Absent[i]).ToList();
            if (reached.Count > 0)
                byExport[export] = reached;
        }
        return new OsAbsentReachVerdict(byExport, failures);
    }

    /// <summary>
    /// The absent symbol a direct P/Invoke names: a <c>$s…</c> entry point of a covered provider that the
    /// provider does not ship. Null otherwise.
    /// </summary>
    public static AbsentSymbol? DirectAbsentSymbol(string library, string entryPoint, Func<string, CoveredProvider?> coverage)
    {
        var symbol = entryPoint.TrimStart('_');
        if (!symbol.StartsWith("$s", StringComparison.Ordinal) || coverage(library) is not { } provider || provider.Ships(symbol))
            return null;
        return new AbsentSymbol("", "_" + symbol, provider.InstallName, provider.OsVersion);
    }

    /// <summary>
    /// Resolves a library (an install name or a P/Invoke library name) to the provider that covers it:
    /// a supplied provider by install name or framework name, else the shipped inventory for any
    /// OS-resident path (<c>/System/</c>, <c>/usr/lib/swift/</c>) or bare name
    /// <paramref name="isOsFrameworkName"/> accepts (a system-framework binding's P/Invoke library).
    /// Null for a library neither covers.
    /// </summary>
    public static CoveredProvider? Resolve(
        string library, IReadOnlyList<OsSymbolInventory.Provider> supplied, Func<string, bool> shippedOnVariant,
        Func<string, bool>? isOsFrameworkName = null, string? shippedOsVersion = null)
    {
        var framework = OsFrameworkWeakLinking.FrameworkName(library);
        foreach (var provider in supplied)
        {
            if (string.Equals(provider.InstallName, library, StringComparison.Ordinal)
                || string.Equals(provider.ModuleName, library, StringComparison.Ordinal)
                || (framework is not null && string.Equals(provider.ModuleName, framework, StringComparison.Ordinal)))
                return new CoveredProvider(provider.InstallName, provider.Symbols.Contains, provider.OsVersion);
        }
        if (library.StartsWith("/System/", StringComparison.Ordinal)
            || library.StartsWith("/usr/lib/swift/", StringComparison.Ordinal))
            return new CoveredProvider(library, shippedOnVariant, shippedOsVersion);
        if (!library.Contains('/') && isOsFrameworkName?.Invoke(library) == true)
            return new CoveredProvider($"/System/Library/Frameworks/{library}.framework/{library}", shippedOnVariant, shippedOsVersion);
        return null;
    }

    /// <summary>
    /// A supplied inventory (<see cref="OsSymbolInventory.Format"/>, JSON) for one provider: every Swift
    /// symbol in <paramref name="nmLines"/> (<c>nm -gUj</c> output) except those containing one of
    /// <paramref name="omissions"/>, which the inventory's OS is then taken not to ship. Fails when a
    /// rule matches nothing, so a renamed fixture cannot leave the inventory silently complete.
    /// </summary>
    public static string BuildSuppliedInventory(
        IEnumerable<string> nmLines, string installName, string moduleName, string osVersion, string floor,
        IReadOnlyList<string> omissions, out IReadOnlyDictionary<string, int> omittedByRule)
    {
        var all = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in nmLines)
        {
            var symbol = line.Trim();
            if (symbol.StartsWith("_$s", StringComparison.Ordinal))
                all.Add(symbol[1..]);
        }
        var counts = omissions.ToDictionary(o => o, _ => 0, StringComparer.Ordinal);
        var kept = new List<string>();
        foreach (var symbol in all)
        {
            var rule = omissions.FirstOrDefault(o => symbol.Contains(o, StringComparison.Ordinal));
            if (rule is null)
                kept.Add(symbol);
            else
                counts[rule]++;
        }
        omittedByRule = counts;
        if (kept.Count == 0)
            throw new InvalidOperationException($"{moduleName}: no Swift symbol left to list; nm read nothing.");
        foreach (var (rule, count) in counts)
        {
            if (count == 0)
                throw new InvalidOperationException($"{moduleName}: omission '{rule}' matched no exported symbol.");
        }
        // Written with the raw writer: the unit tests link-compile this file under trim analysis.
        using var buffer = new System.IO.MemoryStream();
        using (var json = new System.Text.Json.Utf8JsonWriter(buffer, new System.Text.Json.JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("format", OsSymbolInventory.Format);
            json.WriteStartArray("providers");
            json.WriteStartObject();
            json.WriteString("installName", installName);
            json.WriteString("module", moduleName);
            json.WriteString("osVersion", osVersion);
            json.WriteString("floor", floor);
            json.WriteStartArray("symbols");
            foreach (var symbol in kept)
                json.WriteStringValue(symbol);
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// The declarations a binding report records as raised by an inferred OS floor (each
    /// <c>InferredOsFloors</c> entry with <c>Raised</c> set), read wherever the report nests them. The
    /// gate reads only these, as a positive control on generation; what it accepts as a refusal it reads
    /// from the compiled IL.
    /// </summary>
    public static List<string> ReadRaisedDeclarations(string reportJson)
    {
        var raised = new List<string>();
        using var document = System.Text.Json.JsonDocument.Parse(reportJson);
        void Walk(System.Text.Json.JsonElement element)
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "InferredOsFloors" && property.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var item in property.Value.EnumerateArray())
                        {
                            if (item.TryGetProperty("Raised", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.True
                                && item.TryGetProperty("Declaration", out var d))
                                raised.Add(d.GetString()!);
                        }
                    }
                    else
                        Walk(property.Value);
                }
            }
            else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    Walk(item);
            }
        }
        Walk(document.RootElement);
        return raised.Distinct(StringComparer.Ordinal).ToList();
    }
}
