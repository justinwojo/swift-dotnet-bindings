// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;

namespace BindingsGeneration.Tests;

/// <summary>
/// Holds every platform type the generator projects onto a managed carrier to the Swift
/// representation the SDK gives it. The projections are the bundled type databases' entities and
/// the Apple framework registry's <c>typeRemaps</c> and <c>valueTypes</c>; the Swift side is
/// <c>build/baselines/platform-type-abi.json</c>, measured from the SDK by
/// <c>build/scripts/platform-type-abi-probe.py</c>. Each entry's carrier is resolved through the
/// generator's own type database and read from the platform reference assembly, then compared leaf
/// by leaf in register class and size: a carrier of another width or class reads or writes the
/// wrong bytes on every route that passes the value. A projection that differs on purpose is listed
/// in <see cref="DesignedConversions"/> with the boundary conversion that makes it correct. A
/// projection the test cannot compare is listed in <c>build/baselines/platform-type-abi-exclusions.json</c>
/// under the reason it cannot be, and an unlisted one fails the test.
/// </summary>
public class PlatformTypeAbiAuditTests
{
    private readonly ITestOutputHelper _output;

    public PlatformTypeAbiAuditTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Projections whose carrier differs from the Swift value on purpose, with the conversion that
    /// carries every route across the boundary.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> DesignedConversions = new Dictionary<string, string>
    {
        // SIMD vectors travel in memory: the wrapper takes and returns them through a pointer and
        // rebuilds the vector in Swift, because a .NET Vector is an HFA of scalars, not a vector.
        ["simd.simd_float2"] = "passed through memory: a Swift SIMD2<Float> is one vector register",
        ["simd.simd_float3"] = "passed through memory: a Swift SIMD3<Float> is a padded 16-byte vector",
        ["simd.simd_float4"] = "passed through memory: a Swift SIMD4<Float> is one vector register",
        ["simd.simd_float4x4"] = "passed through memory: a Swift simd_float4x4 is four vector columns",
        ["simd.simd_quatf"] = "passed through memory: a Swift simd_quatf is one vector register",
        // UUID is sixteen bytes on both sides; Swift aligns it to 1, Guid to 4, and the wrapper copies
        // the bytes through a pointer.
        ["Foundation.UUID"] = "copied through a pointer: the same sixteen bytes at a different alignment",
        // Foundation's resilient value types bridge to their Objective-C class: the wrapper converts
        // with `as` on each side of the boundary, and only the object crosses it.
        ["Foundation.Calendar"] = "bridged to NSCalendar in the wrapper",
        ["Foundation.CharacterSet"] = "bridged to NSCharacterSet in the wrapper",
        ["Foundation.Decimal"] = "bridged to NSDecimalNumber in the wrapper",
        ["Foundation.IndexPath"] = "bridged to NSIndexPath in the wrapper",
        ["Foundation.Locale"] = "bridged to NSLocale in the wrapper",
        ["Foundation.URLSessionWebSocketTask.Message"] = "bridged to NSURLSessionWebSocketMessage in the wrapper",
    };

    private static string RepoRoot { get; } = LocateRepoRoot();

    private static string LocateRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BindingTests", "RuntimeTestsApp", "RuntimeTestsApp.csproj")))
                return directory.FullName;
        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }

    private static readonly string[] s_runtimeModules = { "Swift", "SwiftUI", "_Concurrency" };

    /// <summary>The projections, by Swift name, exactly as the probe script enumerates them.</summary>
    private static SortedDictionary<string, string> ProjectionEntries()
    {
        var names = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(Path.Combine(RepoRoot, "src", "Swift.Runtime", "src", "Swift"), "*Database.xml").OrderBy(p => p, StringComparer.Ordinal))
        {
            foreach (var decl in XDocument.Load(path).Descendants("entity").Select(e => e.Element("typedeclaration")))
            {
                if (decl is null || s_runtimeModules.Contains((string?)decl.Attribute("module")))
                    continue;
                names.TryAdd($"{decl.Attribute("module")!.Value}.{decl.Attribute("name")!.Value}", Path.GetFileName(path));
            }
        }
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "src", "Swift.Bindings", "src", "Data", "apple-frameworks.json")));
        foreach (var framework in registry.RootElement.GetProperty("frameworks").EnumerateArray())
        {
            var module = framework.GetProperty("module").GetString()!;
            if (framework.TryGetProperty("typeRemaps", out var remaps))
                foreach (var remap in remaps.EnumerateObject())
                    names.TryAdd($"{module}.{remap.Name}", "apple-frameworks.json typeRemaps");
            if (framework.TryGetProperty("valueTypes", out var valueTypes))
                foreach (var entry in valueTypes.EnumerateArray())
                    names.TryAdd($"{module}.{(entry.ValueKind == JsonValueKind.Object ? entry.GetProperty("name").GetString() : entry.GetString())}", "apple-frameworks.json valueTypes");
        }
        return names;
    }

    private sealed record SwiftSide(string? Unprobed, string[] Leaves, int Size, bool? AddressOnly = null);

    private static Dictionary<string, SwiftSide> LoadAuditTable()
    {
        using var table = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "build", "baselines", "platform-type-abi.json")));
        var rows = new Dictionary<string, SwiftSide>(StringComparer.Ordinal);
        foreach (var row in table.RootElement.GetProperty("types").EnumerateObject())
        {
            rows[row.Name] = row.Value.TryGetProperty("unprobed", out var unprobed)
                ? new SwiftSide(unprobed.GetString(), Array.Empty<string>(), 0)
                : new SwiftSide(null, row.Value.GetProperty("leaves").EnumerateArray().Select(l => l.GetString()!).ToArray(), row.Value.GetProperty("size").GetInt32(),
                    row.Value.TryGetProperty("addressOnly", out var addressOnly) ? addressOnly.GetBoolean() : null);
        }
        return rows;
    }

    [Fact]
    public void AuditTable_RecordsExactlyTheProjections()
    {
        var entries = ProjectionEntries();
        var table = LoadAuditTable();
        var missing = entries.Keys.Where(n => !table.ContainsKey(n)).ToList();
        var stale = table.Keys.Where(n => !entries.ContainsKey(n)).ToList();
        Assert.True(missing.Count == 0 && stale.Count == 0,
            "build/baselines/platform-type-abi.json is out of date; rerun build/scripts/platform-type-abi-probe.py. "
            + $"Not recorded: {string.Join(", ", missing)}. No longer projected: {string.Join(", ", stale)}.");
    }

    /// <summary>
    /// A platform value the SDK declares without <c>@frozen</c> is address-only to every client: Swift
    /// takes it <c>@in_guaranteed</c> and returns it indirectly, whatever its size. When a bundled
    /// database still calls it frozen and projects it onto a by-value managed carrier, each
    /// Swift-convention route would put the value in a register where Swift reads an address, so the
    /// generator has to know it is resilient (<c>SwiftLayoutResolver.ResilientPlatformValues</c>) and
    /// route it through memory. This holds that list to the conventions the probe read from SIL, in
    /// both directions.
    /// </summary>
    [Fact]
    public async Task EveryAddressOnlyValueCarriedByValue_IsKnownResilient()
    {
        var database = new TypeDatabase();
        foreach (var xml in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Swift"), "*Database.xml").OrderBy(p => p, StringComparer.Ordinal))
            await database.LoadModuleDatabaseFromFile(xml);

        var table = LoadAuditTable();
        var unknown = new List<string>();
        var checkedRows = 0;
        foreach (var (name, swift) in table)
        {
            if (swift.AddressOnly != true || !database.TryGetTypeRecord(new NamedTypeSpec(name), out var record))
                continue;
            checkedRows++;
            var carrier = CarrierName(record);
            var carriedByValue = record.Kind == TypeRecordKind.Struct && MarshallingHelpers.IsTypeFrozen(record)
                && !carrier.StartsWith("Swift.", StringComparison.Ordinal);
            if (carriedByValue && !SwiftLayoutResolver.ResilientPlatformValues.Contains(name))
                unknown.Add($"{name} (carried as {carrier})");
        }
        Assert.True(checkedRows > 0, "no address-only row resolved to a record, so nothing was checked; rerun build/scripts/platform-type-abi-probe.py");
        Assert.True(unknown.Count == 0,
            "These platform values are address-only in the SDK but a database projects them frozen onto a by-value carrier, and "
            + "SwiftLayoutResolver.ResilientPlatformValues does not list them, so a Swift-convention route passes them in a register:\n"
            + string.Join("\n", unknown));

        var stale = SwiftLayoutResolver.ResilientPlatformValues
            .Where(n => !table.TryGetValue(n, out var row) || row.AddressOnly != true).ToList();
        Assert.True(stale.Count == 0,
            "SwiftLayoutResolver.ResilientPlatformValues lists values the SDK does not pass by address: " + string.Join(", ", stale));
    }

    /// <summary>
    /// A name both a bundled database and the registry's <c>typeRemaps</c> project must project onto
    /// the same carrier. The database shadows the remap when it is loaded, so the carrier test reads
    /// only the database's; a remap that disagrees would project the name onto another carrier on
    /// the paths that consult the registry alone.
    /// </summary>
    [Fact]
    public void RegistryRemaps_AgreeWithTheDatabases()
    {
        var databases = new Dictionary<string, (string Carrier, string File)>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(Path.Combine(RepoRoot, "src", "Swift.Runtime", "src", "Swift"), "*Database.xml").OrderBy(p => p, StringComparer.Ordinal))
        {
            foreach (var entity in XDocument.Load(path).Descendants("entity"))
            {
                if (entity.Element("typedeclaration") is not { } decl)
                    continue;
                databases.TryAdd($"{decl.Attribute("module")!.Value}.{decl.Attribute("name")!.Value}",
                    ($"{entity.Attribute("managedNameSpace")?.Value}.{entity.Attribute("managedTypeName")?.Value}", Path.GetFileName(path)));
            }
        }
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "src", "Swift.Bindings", "src", "Data", "apple-frameworks.json")));
        var compared = 0;
        var disagreements = new List<string>();
        foreach (var framework in registry.RootElement.GetProperty("frameworks").EnumerateArray())
        {
            if (!framework.TryGetProperty("typeRemaps", out var remaps))
                continue;
            var module = framework.GetProperty("module").GetString()!;
            foreach (var remap in remaps.EnumerateObject())
            {
                if (!databases.TryGetValue($"{module}.{remap.Name}", out var database))
                    continue;
                compared++;
                if (!string.Equals(database.Carrier, remap.Value.GetString(), StringComparison.Ordinal))
                    disagreements.Add($"{module}.{remap.Name}: typeRemaps {remap.Value.GetString()}, {database.File} {database.Carrier}");
            }
        }
        Assert.True(compared > 0, "no name is projected by both a database and typeRemaps, so nothing was compared");
        Assert.True(disagreements.Count == 0, "typeRemaps and the databases project these names onto different carriers:\n" + string.Join("\n", disagreements));
    }

    [SkippableFact]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Reads a reference assembly's metadata through a MetadataLoadContext; nothing is trimmed or executed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Reads a reference assembly's metadata through a MetadataLoadContext; nothing is trimmed or executed.")]
    public async Task EveryProjectedCarrier_MatchesItsSwiftRepresentation()
    {
        var references = ManagedReferenceContext.Resolve(PlatformInfoFactory.Create(ApplePlatform.iOS));
        Skip.IfNot(references.IsAvailable, "the iOS reference pack is not installed");
        // The generator resolves registry-described enums against the platform surface it binds to.
        using var referenceScope = AppleTypeSurfaceIndex.UseReferenceContext(references);

        var paths = references.ReferencePaths.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        // The runtime's own carriers (Swift.CGPoint, Swift.DispatchQueue, ...) are read from the
        // Swift.Runtime assembly the generator ships beside.
        var runtimeAssembly = Path.Combine(AppContext.BaseDirectory, "Swift.Runtime.dll");
        Assert.True(File.Exists(runtimeAssembly), $"{runtimeAssembly} is missing, so the runtime's carriers cannot be compared");
        using var context = new MetadataLoadContext(new PathAssemblyResolver(paths.Append(runtimeAssembly)), "System.Runtime");
        var assemblies = paths.Append(runtimeAssembly).Select(p => { try { return context.LoadFromAssemblyPath(p); } catch (BadImageFormatException) { return null; } })
            .Where(a => a is not null).Select(a => a!).ToList();
        Type? Find(string fullName) => assemblies.Select(a => a.GetType(fullName)).FirstOrDefault(t => t is not null);

        var database = new TypeDatabase();
        foreach (var xml in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Swift"), "*Database.xml").OrderBy(p => p, StringComparer.Ordinal))
            await database.LoadModuleDatabaseFromFile(xml);

        var table = LoadAuditTable();
        var exclusions = LoadExclusions();
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var mismatches = new List<string>();
        var staleConversions = new List<string>();
        var unclassified = new List<string>();
        var compared = 0;

        // A row this test cannot compare must be listed in the exclusion ledger under the reason it
        // has; anything else is a carrier nobody has accounted for.
        bool Exclude(string name, ExclusionCategory category, string detail)
        {
            if (exclusions.TryGetValue(name, out var entry) && entry.Category == category)
            {
                excluded.Add(name);
                _output.WriteLine($"not compared {name} [{category}]: {entry.Reason}");
            }
            else
            {
                unclassified.Add(exclusions.TryGetValue(name, out var other)
                    ? $"{name}: {detail}, but the ledger lists it as {other.Category}"
                    : $"{name}: {detail}");
            }
            return true;
        }

        foreach (var (name, source) in ProjectionEntries())
        {
            if (!table.TryGetValue(name, out var swift))
                continue; // AuditTable_RecordsExactlyTheProjections reports it
            if (swift.Unprobed is not null)
            {
                Exclude(name, ExclusionCategory.NoSwiftSpelling, $"no Swift type of that spelling ({swift.Unprobed})");
                continue;
            }
            string carrier;
            string[]? managed;
            if (database.TryGetTypeRecord(new NamedTypeSpec(name), out var record))
            {
                carrier = CarrierName(record);
                if (carrier.StartsWith("Swift.", StringComparison.Ordinal) && swift.Leaves.Any(l => l.StartsWith("opaque", StringComparison.Ordinal)))
                {
                    Exclude(name, ExclusionCategory.OpaquePayload, $"the runtime's {carrier} holds an opaque Swift payload");
                    continue;
                }
                managed = ManagedLeaves(carrier, record, Find);
                if (managed is null)
                {
                    Exclude(name, ExclusionCategory.NotInReferenceAssembly, $"{carrier} is in neither the iOS reference assembly nor Swift.Runtime");
                    continue;
                }
            }
            else if (exclusions.TryGetValue(name, out var alias) && alias.Category == ExclusionCategory.AliasCarrier && alias.Carrier is { } aliasCarrier)
            {
                carrier = aliasCarrier;
                managed = Find(carrier) is { } aliasType ? FlattenAll(aliasType) : null;
                excluded.Add(name);
                if (managed is null)
                {
                    unclassified.Add($"{name}: the ledger's alias carrier {carrier} is in neither the iOS reference assembly nor Swift.Runtime");
                    continue;
                }
            }
            else
            {
                Exclude(name, ExclusionCategory.NotProjected, "the generator resolves no record");
                continue;
            }
            compared++;
            var swiftClasses = swift.Leaves.Select(SwiftClass).ToArray();
            var agrees = swiftClasses.SequenceEqual(managed);
            var line = $"{name} ({source}): Swift [{string.Join(",", swift.Leaves)}] ({swift.Size} bytes) vs {carrier} [{string.Join(",", managed)}]";
            _output.WriteLine((agrees ? "equivalent  " : "differs     ") + line);
            if (DesignedConversions.ContainsKey(name))
            {
                if (agrees)
                    staleConversions.Add(line);
            }
            else if (!agrees)
            {
                mismatches.Add(line);
            }
        }

        Assert.True(compared > 0, "no projection resolved to a carrier, so nothing was compared");
        Assert.True(mismatches.Count == 0,
            "These projections carry a managed value of another representation than the Swift value, so every route that "
            + "passes one reads or writes the wrong bytes:\n" + string.Join("\n", mismatches));
        Assert.True(staleConversions.Count == 0,
            "These designed conversions now match the Swift representation; drop them from DesignedConversions:\n" + string.Join("\n", staleConversions));
        Assert.True(unclassified.Count == 0,
            "These projections cannot be compared and build/baselines/platform-type-abi-exclusions.json does not list them under "
            + "that reason. Give each a carrier the test can compare, or classify it in the ledger with the reason no route "
            + "passes it wrongly:\n" + string.Join("\n", unclassified));
        var stale = exclusions.Keys.Where(n => !excluded.Contains(n)).ToList();
        Assert.True(stale.Count == 0,
            "build/baselines/platform-type-abi-exclusions.json lists rows this test now compares or no longer sees; drop them:\n"
            + string.Join("\n", stale));
    }

    internal enum ExclusionCategory
    {
        /// <summary>The SDK declares no Swift type of the projected spelling on the probed platform.</summary>
        NoSwiftSpelling,
        /// <summary>The runtime carrier holds the Swift value's bytes opaquely and moves them by value witnesses.</summary>
        OpaquePayload,
        /// <summary>The managed carrier exists in neither the iOS reference assembly nor Swift.Runtime.</summary>
        NotInReferenceAssembly,
        /// <summary>No record and no carrier: the generator binds no member that passes the type.</summary>
        NotProjected,
        /// <summary>No record, but the generator resolves the name to a recorded carrier, which is compared.</summary>
        AliasCarrier,
    }

    internal sealed record Exclusion(ExclusionCategory Category, string Reason, string? Carrier);

    internal static Dictionary<string, Exclusion> LoadExclusions()
    {
        using var ledger = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "build", "baselines", "platform-type-abi-exclusions.json")));
        var rows = new Dictionary<string, Exclusion>(StringComparer.Ordinal);
        foreach (var row in ledger.RootElement.GetProperty("rows").EnumerateObject())
        {
            var category = Enum.Parse<ExclusionCategory>(row.Value.GetProperty("category").GetString()!);
            var reason = row.Value.GetProperty("reason").GetString();
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidOperationException($"platform-type-abi-exclusions.json: {row.Name} has no reason");
            var carrier = row.Value.TryGetProperty("carrier", out var c) ? c.GetString() : null;
            if ((category == ExclusionCategory.AliasCarrier) != (carrier is not null))
                throw new InvalidOperationException($"platform-type-abi-exclusions.json: {row.Name} must record a carrier exactly when it is an AliasCarrier");
            rows[row.Name] = new Exclusion(category, reason, carrier);
        }
        return rows;
    }

    /// <summary>
    /// The ledger is closed in both directions: dropping any one row turns the carrier test red, so a
    /// newly unresolvable carrier cannot pass unclassified.
    /// </summary>
    [Fact]
    public void ExclusionLedger_EveryRowHasACategoryAndReason()
    {
        var exclusions = LoadExclusions();
        Assert.NotEmpty(exclusions);
        var table = LoadAuditTable();
        var unknown = exclusions.Keys.Where(n => !table.ContainsKey(n)).ToList();
        Assert.True(unknown.Count == 0, "platform-type-abi-exclusions.json lists names the audit table does not record: " + string.Join(", ", unknown));
    }

    /// <summary>
    /// The managed type a projection's values travel as: an Objective-C object or a typed-string
    /// constant crosses as its handle, anything else as the record's managed type.
    /// </summary>
    private static string CarrierName(TypeRecord record)
    {
        if ((record.Flags & TypeRecordFlags.AppleTypedEnum) != 0 && record.NativeTypeName is { } native)
            return native.FullyQualifiedName;
        return record.CSharpTypeName.FullyQualifiedName switch
        {
            "nint" => "System.IntPtr",
            "nuint" => "System.UIntPtr",
            "double" => "System.Double",
            "float" => "System.Single",
            "long" => "System.Int64",
            "ulong" => "System.UInt64",
            "int" => "System.Int32",
            "uint" => "System.UInt32",
            "short" => "System.Int16",
            "ushort" => "System.UInt16",
            "byte" => "System.Byte",
            "sbyte" => "System.SByte",
            "bool" => "System.Boolean",
            var other => other,
        };
    }

    /// <summary>The register class and width of each leaf of a managed carrier, or null when it is absent.</summary>
    private static string[]? ManagedLeaves(string carrier, TypeRecord record, Func<string, Type?> find)
    {
        var type = find(carrier);
        if (type is null)
            return record.Kind == TypeRecordKind.Class ? new[] { "I8" } : null;
        return FlattenAll(type);
    }

    private static string[] FlattenAll(Type type)
    {
        var leaves = new List<string>();
        Flatten(type, leaves, 0);
        return leaves.ToArray();
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Reads a reference assembly's metadata through a MetadataLoadContext; nothing is trimmed or executed.")]
    private static void Flatten(Type type, List<string> leaves, int depth)
    {
        if (depth > 8)
            throw new InvalidOperationException($"{type.FullName} nests too deeply");
        if (type.IsEnum)
        {
            Flatten(type.GetEnumUnderlyingType(), leaves, depth + 1);
            return;
        }
        switch (type.FullName)
        {
            case "System.Double" or "System.Runtime.InteropServices.NFloat": leaves.Add("F8"); return;
            case "System.Single": leaves.Add("F4"); return;
            case "System.Int64" or "System.UInt64" or "System.IntPtr" or "System.UIntPtr": leaves.Add("I8"); return;
            case "System.Int32" or "System.UInt32": leaves.Add("I4"); return;
            case "System.Int16" or "System.UInt16" or "System.Char": leaves.Add("I2"); return;
            case "System.Byte" or "System.SByte" or "System.Boolean": leaves.Add("I1"); return;
        }
        if (!type.IsValueType)
        {
            leaves.Add("I8"); // an object crosses as its handle
            return;
        }
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            Flatten(field.FieldType, leaves, depth + 1);
    }

    /// <summary>The register class and width of a Swift leaf as the probe records it.</summary>
    private static string SwiftClass(string leaf) => leaf switch
    {
        "i8" or "u8" or "bool" or "int8" => "I1",
        "i16" or "u16" or "int16" => "I2",
        "i32" or "u32" or "int32" => "I4",
        "i64" or "u64" or "int64" or "ptr" => "I8",
        "f32" => "F4",
        "f64" => "F8",
        _ => leaf, // vecN and opaqueN match no managed leaf
    };
}
