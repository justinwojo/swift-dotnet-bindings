// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json.Nodes;
using BindingsGeneration;

namespace SwiftBindings.TransitionGate;

/// <summary>What a compiled assembly says about its Swift transitions (<see cref="SwiftTransitionManifestReader"/>).</summary>
public enum TransitionEvidenceState
{
    /// <summary>Every manifest the assembly locates is embedded, hashes to its locator and reads, and together they describe the assembly's transitions.</summary>
    PresentValid,

    /// <summary>No manifest, and every Swift transition of the assembly is a primitive-only forward call (or it has none).</summary>
    AbsentPrimitiveOnly,

    /// <summary>No manifest, but the assembly has a Swift transition that is not a primitive-only forward call, or its census could not be completed.</summary>
    AbsentNonPrimitive,

    /// <summary>A manifest is located but missing, tampered with, of an unknown schema or self-contradictory, or it does not describe the assembly it is in.</summary>
    TamperedOrMismatched,
}

/// <summary>A manifest embedded in an assembly: its resource, the hash its locator states, its bytes and its reading.</summary>
public sealed record EmbeddedTransitionManifest(string Resource, string Hash, byte[] Bytes, SwiftTransitionManifest Manifest);

/// <summary>The transition evidence of one assembly.</summary>
/// <param name="Path">The assembly read.</param>
/// <param name="AssemblyName">Its name.</param>
/// <param name="State">What the evidence amounts to.</param>
/// <param name="Manifests">The manifests that read, whatever the state.</param>
/// <param name="Problems">Why the state is not <see cref="TransitionEvidenceState.PresentValid"/> or <see cref="TransitionEvidenceState.AbsentPrimitiveOnly"/>; empty for those.</param>
/// <param name="Census">The transition scan of the assembly the manifests were checked against.</param>
public sealed record TransitionEvidence(
    string Path,
    string AssemblyName,
    TransitionEvidenceState State,
    ImmutableArray<EmbeddedTransitionManifest> Manifests,
    ImmutableArray<string> Problems,
    AssemblyTransitionReport Census)
{
    /// <summary>The manifests that route through bridges, with the native library and symbol each names.</summary>
    public IEnumerable<EmbeddedTransitionManifest> Routing => Manifests.Where(m => m.Manifest.NativeSymbol is not null);
}

/// <summary>
/// Reads the transition manifests a compiled assembly embeds (<see cref="SwiftTransitionManifestFormat"/>)
/// and checks them against the assembly, and checks a native image against them. Base class library
/// and <c>System.Reflection.Metadata</c> only: the build's pack gates use it, and an MSBuild task can
/// link-compile it with the rest of this directory and the format file.
/// </summary>
public static class SwiftTransitionManifestReader
{
    private const string MetadataAttribute = "System.Reflection.AssemblyMetadataAttribute";

    /// <summary>Reads the evidence of the assembly at <paramref name="path"/>; <paramref name="census"/> resolves its carrier types.</summary>
    public static TransitionEvidence Read(string path, SwiftTransitionScanOptions? census = null) =>
        Read(File.ReadAllBytes(path), path, census);

    /// <summary>
    /// Reads the evidence of <paramref name="image"/>. The assembly is also scanned
    /// (<see cref="SwiftTransitionScanner"/>, with <paramref name="census"/>'s reference directories)
    /// for its transitions: an assembly without a manifest is classed by them, and one with manifests
    /// must route exactly the bridges its manifests name, through the library they name, and import
    /// no Swift-convention entry point they do not cover.
    /// </summary>
    public static TransitionEvidence Read(byte[] image, string displayPath, SwiftTransitionScanOptions? census = null)
    {
        var scan = SwiftTransitionScanner.Scan(image, displayPath, new SwiftTransitionScanOptions
        {
            ReferenceDirectories = census?.ReferenceDirectories ?? Array.Empty<string>(),
            ReferenceImages = census?.ReferenceImages ?? Array.Empty<byte[]>(),
        });
        var problems = new List<string>();
        var manifests = ImmutableArray.CreateBuilder<EmbeddedTransitionManifest>();
        var (locators, resources) = ReadMetadata(image, problems);

        foreach (var name in resources.Keys.Where(r => r.EndsWith(SwiftTransitionManifestFormat.FileSuffix, StringComparison.Ordinal)
                                                       && !locators.Any(l => l.Resource == r)))
            problems.Add($"the manifest resource {name} has no locator attribute");
        foreach (var duplicate in locators.GroupBy(l => l.Resource, StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add($"{duplicate.Count()} locators name the manifest resource {duplicate.Key}");
        foreach (var locator in locators.DistinctBy(l => l.Resource))
        {
            if (locator.Schema != SwiftTransitionManifestFormat.Schema)
            {
                problems.Add($"the manifest {locator.Resource} has schema {locator.Schema}; this reader reads schema {SwiftTransitionManifestFormat.Schema}");
                continue;
            }
            // The resource name becomes a file name when the manifest's records are written out as
            // sidecars, so it must be one: no directory part, and the manifest suffix.
            if (locator.Resource != Path.GetFileName(locator.Resource) || locator.Resource.Contains('\\')
                || !locator.Resource.EndsWith(SwiftTransitionManifestFormat.FileSuffix, StringComparison.Ordinal)
                || locator.Resource.Length == SwiftTransitionManifestFormat.FileSuffix.Length)
            {
                problems.Add($"the locator names the manifest resource {locator.Resource}, which is not a file name ending in {SwiftTransitionManifestFormat.FileSuffix}");
                continue;
            }
            if (!resources.TryGetValue(locator.Resource, out var bytes))
            {
                problems.Add($"the locator names the manifest resource {locator.Resource}, which the assembly does not embed");
                continue;
            }
            var actual = SwiftTransitionManifestFormat.Hash(bytes);
            if (actual != locator.Hash)
            {
                problems.Add($"the manifest {locator.Resource} hashes to {actual}, but its locator states {locator.Hash}");
                continue;
            }
            try
            {
                manifests.Add(new EmbeddedTransitionManifest(locator.Resource, locator.Hash, bytes, SwiftTransitionManifestFormat.Parse(bytes, locator.Resource)));
            }
            catch (InvalidDataException ex)
            {
                problems.Add(ex.Message);
            }
        }

        var read = manifests.ToImmutable();
        var assembly = scan.AssemblyName;
        if (locators.Count == 0 && problems.Count == 0)
        {
            var nonPrimitive = NonPrimitive(scan).ToList();
            return new TransitionEvidence(displayPath, assembly,
                nonPrimitive.Count == 0 ? TransitionEvidenceState.AbsentPrimitiveOnly : TransitionEvidenceState.AbsentNonPrimitive,
                read, nonPrimitive.ToImmutableArray(), scan);
        }
        if (problems.Count == 0)
        {
            // A member the scan could not inspect is a transition the manifests cannot be checked against.
            problems.AddRange(scan.InspectionErrors.Select(e => $"{e.Member}: its transitions could not be inspected: {e.Message}"));
            problems.AddRange(Mismatches(scan, read));
        }
        return new TransitionEvidence(displayPath, assembly,
            problems.Count == 0 ? TransitionEvidenceState.PresentValid : TransitionEvidenceState.TamperedOrMismatched,
            read, problems.ToImmutableArray(), scan);
    }

    /// <summary>Why an assembly without a manifest is not primitive-only: each transition that is not a primitive-only forward call.</summary>
    private static IEnumerable<string> NonPrimitive(AssemblyTransitionReport scan)
    {
        foreach (var error in scan.InspectionErrors)
            yield return $"{error.Member}: its transitions could not be inspected: {error.Message}";
        foreach (var site in scan.Sites)
        {
            if (site.Kind == TransitionSiteKind.PInvoke && site.EntryPoint is { } entry
                && (SwiftTransitionScanner.IsForwardBridge(entry) || entry.StartsWith(SwiftTransitionScanner.ReverseBridgePrefix, StringComparison.Ordinal)))
                yield return $"{site.Member}: imports the bridge {entry}";
            else if (site.Rules.Length > 0)
                yield return $"{site.Member} ({site.EntryPoint ?? site.Kind.ToString()}): breaks {string.Join(", ", site.Rules)}";
            else if (site.Convention == TransitionConvention.Swift && site.Kind == TransitionSiteKind.UnmanagedCallersOnly)
                yield return $"{site.Member}: a Swift-convention reverse callback";
        }
    }

    /// <summary>Where the manifests do not describe the assembly: the bridges it imports and the Swift-convention entry points it calls.</summary>
    private static IEnumerable<string> Mismatches(AssemblyTransitionReport scan, ImmutableArray<EmbeddedTransitionManifest> manifests)
    {
        var named = manifests.SelectMany(m => m.Manifest.Bridges.Select(b => (Bridge: b, m.Manifest.NativeLibrary, m.Resource)))
            .ToLookup(b => b.Bridge, StringComparer.Ordinal);
        var imported = scan.Sites
            .Where(s => s.Kind == TransitionSiteKind.PInvoke && SwiftTransitionScanner.IsForwardBridge(s.EntryPoint))
            .ToLookup(s => s.EntryPoint!, StringComparer.Ordinal);
        foreach (var twin in imported)
        {
            if (!named.Contains(twin.Key))
            {
                yield return $"{twin.First().Member} imports the bridge {twin.Key}, which no manifest routes";
                continue;
            }
            var libraries = named[twin.Key].Select(n => n.NativeLibrary).ToHashSet(StringComparer.Ordinal);
            foreach (var site in twin.Where(s => !libraries.Contains(s.Library)))
                yield return $"{site.Member} imports the bridge {twin.Key} from {site.Library}; its manifest names {string.Join(", ", libraries)}";
        }
        foreach (var bridge in named.Where(b => !imported.Contains(b.Key)))
            yield return $"the manifest {bridge.First().Resource} routes {bridge.Key}, which the assembly does not import";

        var covered = manifests.SelectMany(m => EntryPoints(m.Manifest.Plans)).ToHashSet(StringComparer.Ordinal);
        foreach (var site in scan.Sites.Where(s => s.Kind == TransitionSiteKind.PInvoke && s.Convention == TransitionConvention.Swift
                                                   && s.EntryPoint is not null && !covered.Contains(s.EntryPoint)))
            yield return $"{site.Member} imports {site.EntryPoint} with the Swift convention, which no manifest covers";
    }

    /// <summary>The entry points a call-plan document's coverage records.</summary>
    private static IEnumerable<string> EntryPoints(string plans) =>
        (JsonNode.Parse(plans)?["coverage"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Select(e => e["entryPoint"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => s is not null)
            .Select(s => s!);

    /// <summary>
    /// Checks a native image of the library <paramref name="library"/> against the evidence: every
    /// slice must hold, in each routing manifest's data symbol, that manifest's hash, and must export
    /// exactly the forward bridges the manifests route through it. A library no manifest routes
    /// through must export no bridge and no manifest symbol. Returns what does not hold.
    /// </summary>
    public static ImmutableArray<string> CheckNative(TransitionEvidence evidence, string library, byte[] nativeImage, string displayPath)
    {
        var problems = new List<string>();
        ImmutableArray<MachOSlice> slices;
        try
        {
            slices = MachOSymbols.Read(nativeImage);
        }
        catch (BadImageFormatException ex)
        {
            return [$"{displayPath}: {ex.Message}"];
        }
        var routing = evidence.Routing.Where(m => m.Manifest.NativeLibrary == library).ToList();
        var expected = routing.SelectMany(m => m.Manifest.Bridges).ToHashSet(StringComparer.Ordinal);
        var symbols = routing.Select(m => m.Manifest.NativeSymbol!).ToHashSet(StringComparer.Ordinal);
        foreach (var slice in slices)
        {
            var where = $"{displayPath} ({slice.Architecture})";
            foreach (var manifest in routing)
            {
                var held = slice.CString(manifest.Manifest.NativeSymbol!);
                if (held is null)
                    problems.Add($"{where} does not export {manifest.Manifest.NativeSymbol}, the hash of {evidence.AssemblyName}'s manifest {manifest.Resource}");
                else if (held != manifest.Hash)
                    problems.Add($"{where}: {manifest.Manifest.NativeSymbol} holds {held}, but {evidence.AssemblyName} embeds {manifest.Resource} with {manifest.Hash}");
            }
            var exported = slice.Exports.Where(s => SwiftTransitionScanner.IsForwardBridge(s)).ToHashSet(StringComparer.Ordinal);
            foreach (var missing in expected.Where(b => !exported.Contains(b)).Order(StringComparer.Ordinal))
                problems.Add($"{where} does not export {missing}, which {evidence.AssemblyName} routes through it");
            foreach (var extra in exported.Where(b => !expected.Contains(b)).Order(StringComparer.Ordinal))
                problems.Add($"{where} exports {extra}, which no manifest of {evidence.AssemblyName} routes through {library}");
            foreach (var stray in slice.Exports.Where(s => s.StartsWith(SwiftTransitionManifestFormat.NativeSymbolPrefix, StringComparison.Ordinal) && !symbols.Contains(s)))
                problems.Add($"{where} exports {stray}, which no manifest of {evidence.AssemblyName} names for {library}");
        }
        return problems.ToImmutableArray();
    }

    private sealed record Locator(int Schema, string Resource, string Hash);

    /// <summary>The assembly's manifest locators and embedded resources; a locator that does not read is a problem.</summary>
    private static (List<Locator> Locators, Dictionary<string, byte[]> Resources) ReadMetadata(byte[] image, List<string> problems)
    {
        using var pe = new PEReader(new MemoryStream(image, writable: false));
        var md = pe.GetMetadataReader();
        var locators = new List<Locator>();
        if (md.IsAssembly)
        {
            foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = md.GetCustomAttribute(handle);
                if (AttributeTypeName(md, attribute) != MetadataAttribute)
                    continue;
                var value = attribute.DecodeValue(SwiftTransitionScanner.AttributeTypeProvider.Instance);
                if (value.FixedArguments.Length != 2 || value.FixedArguments[0].Value as string != SwiftTransitionManifestFormat.AttributeKey)
                    continue;
                if (SwiftTransitionManifestFormat.TryParseAttributeValue(value.FixedArguments[1].Value as string, out var schema, out var resource, out var hash, out var error))
                    locators.Add(new Locator(schema, resource, hash));
                else
                    problems.Add(error!);
            }
        }

        var resources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var directory = pe.PEHeaders.CorHeader?.ResourcesDirectory ?? default;
        foreach (var handle in md.ManifestResources)
        {
            var resource = md.GetManifestResource(handle);
            if (!resource.Implementation.IsNil || directory.Size == 0)
                continue;
            var data = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent();
            var offset = (int)resource.Offset;
            if (offset + 4 > data.Length)
                continue;
            var length = BitConverter.ToInt32(data.AsSpan(offset, 4));
            if (length < 0 || offset + 4 + length > data.Length)
                continue;
            resources[md.GetString(resource.Name)] = data.AsSpan(offset + 4, length).ToArray();
        }
        return (locators, resources);
    }

    private static string? AttributeTypeName(MetadataReader md, CustomAttribute attribute)
    {
        EntityHandle type = attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
            HandleKind.MethodDefinition => md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
            _ => default,
        };
        return type.Kind switch
        {
            HandleKind.TypeReference => md.GetTypeReference((TypeReferenceHandle)type) is var r ? $"{md.GetString(r.Namespace)}.{md.GetString(r.Name)}" : null,
            HandleKind.TypeDefinition => md.GetTypeDefinition((TypeDefinitionHandle)type) is var d ? $"{md.GetString(d.Namespace)}.{md.GetString(d.Name)}" : null,
            _ => null,
        };
    }
}
