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
/// in <see cref="DesignedConversions"/> with the boundary conversion that makes it correct.
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

    private sealed record SwiftSide(string? Unprobed, string[] Leaves, int Size);

    private static Dictionary<string, SwiftSide> LoadAuditTable()
    {
        using var table = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "build", "baselines", "platform-type-abi.json")));
        var rows = new Dictionary<string, SwiftSide>(StringComparer.Ordinal);
        foreach (var row in table.RootElement.GetProperty("types").EnumerateObject())
        {
            rows[row.Name] = row.Value.TryGetProperty("unprobed", out var unprobed)
                ? new SwiftSide(unprobed.GetString(), Array.Empty<string>(), 0)
                : new SwiftSide(null, row.Value.GetProperty("leaves").EnumerateArray().Select(l => l.GetString()!).ToArray(), row.Value.GetProperty("size").GetInt32());
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

        var paths = references.ReferencePaths.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        using var context = new MetadataLoadContext(new PathAssemblyResolver(paths), "System.Runtime");
        var assemblies = paths.Select(p => { try { return context.LoadFromAssemblyPath(p); } catch (BadImageFormatException) { return null; } })
            .Where(a => a is not null).Select(a => a!).ToList();
        Type? Find(string fullName) => assemblies.Select(a => a.GetType(fullName)).FirstOrDefault(t => t is not null);

        var database = new TypeDatabase();
        foreach (var xml in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Swift"), "*Database.xml").OrderBy(p => p, StringComparer.Ordinal))
            await database.LoadModuleDatabaseFromFile(xml);

        var table = LoadAuditTable();
        var mismatches = new List<string>();
        var staleConversions = new List<string>();
        var notCompared = new List<string>();
        foreach (var (name, source) in ProjectionEntries())
        {
            if (!table.TryGetValue(name, out var swift))
                continue; // AuditTable_RecordsExactlyTheProjections reports it
            if (swift.Unprobed is not null)
            {
                notCompared.Add($"{name}: no Swift type of that spelling ({swift.Unprobed})");
                continue;
            }
            if (!database.TryGetTypeRecord(new NamedTypeSpec(name), out var record))
            {
                notCompared.Add($"{name}: the generator resolves no record");
                continue;
            }
            var carrier = CarrierName(record);
            if (carrier.StartsWith("Swift.", StringComparison.Ordinal))
            {
                notCompared.Add($"{name}: carried by the runtime's own {carrier}");
                continue;
            }
            var managed = ManagedLeaves(carrier, record, Find);
            if (managed is null)
            {
                notCompared.Add($"{name}: {carrier} is not in the iOS reference assembly");
                continue;
            }
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
        foreach (var line in notCompared)
            _output.WriteLine("not compared " + line);

        Assert.True(mismatches.Count == 0,
            "These projections carry a managed value of another representation than the Swift value, so every route that "
            + "passes one reads or writes the wrong bytes:\n" + string.Join("\n", mismatches));
        Assert.True(staleConversions.Count == 0,
            "These designed conversions now match the Swift representation; drop them from DesignedConversions:\n" + string.Join("\n", staleConversions));
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
