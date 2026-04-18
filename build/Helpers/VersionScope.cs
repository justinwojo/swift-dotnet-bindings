// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Nuke.Common.IO;

/// <summary>
/// Temporarily stamps version numbers in project files for NuGet packaging.
/// Restores originals on Dispose (even on exception).
/// Replaces the backup-sed-restore-on-trap pattern in pack-all.sh.
/// </summary>
/// <remarks>
/// Runtime, SDK, Templates, Sdk.props, template metadata, and the generator's
/// <c>DefaultSwiftRuntimeVersion</c> constant all share the main version.
/// The Apple supplement (<c>SwiftBindings.Apple</c>) is versioned per Apple
/// SDK train and stamped independently, so consumers can adopt a new
/// supplement release without waiting on a Runtime/SDK bump. When
/// <paramref name="appleVersion"/> is null the main version is used for the
/// supplement as well, preserving the pre-split behavior.
/// </remarks>
public sealed class VersionScope : IDisposable
{
    private readonly Dictionary<string, byte[]> _originals = new();

    public VersionScope(string version, AbsolutePath repoRoot, string? appleVersion = null)
    {
        var effectiveAppleVersion = appleVersion ?? version;

        var files = new[]
        {
            repoRoot / "src" / "Swift.Runtime" / "src" / "Swift.Runtime.csproj",
            repoRoot / "src" / "Swift.Bindings.Sdk" / "Swift.Bindings.Sdk.csproj",
            repoRoot / "src" / "Swift.Bindings.Templates" / "Swift.Bindings.Templates.csproj",
            repoRoot / "src" / "Swift.Bindings.Apple" / "Swift.Bindings.Apple.csproj",
            repoRoot / "src" / "Swift.Bindings.Sdk" / "Sdk" / "Sdk.props",
            repoRoot / "src" / "Swift.Bindings.Templates" / "content" / "swift-binding" / "ProjectName.csproj",
            repoRoot / "src" / "Swift.Bindings.Templates" / "content" / "swift-binding" / ".template.config" / "template.json",
            repoRoot / "src" / "Swift.Bindings" / "src" / "Emitter" / "BindingProjectEmitter.cs",
        };

        foreach (var file in files)
        {
            if (!File.Exists(file))
                throw new FileNotFoundException($"Version file not found: {file}");
            _originals[file] = File.ReadAllBytes(file);
        }

        // Apply version stamps — XML files via XDocument, others via text/JSON.
        // Apple supplement stamps from appleVersion; everything else from the main version.
        StampPackageVersion(files[0], version);                 // Runtime .csproj
        StampPackageVersion(files[1], version);                 // SDK .csproj
        StampPackageVersion(files[2], version);                 // Templates .csproj
        StampPackageVersion(files[3], effectiveAppleVersion);   // Apple .csproj
        StampSupplementRuntimeRange(files[3], version);         // Apple .csproj Runtime dep range
        StampSdkProps(files[4], version, effectiveAppleVersion); // _SwiftBindingSdkVersion + SwiftRuntimeVersion + SwiftAppleSupplementVersion
        StampTemplateSdk(files[5], version);                    // Sdk="SwiftBindings.Sdk/..."
        StampTemplateJson(files[6], version);                   // template.json sdkVersion symbol
        StampGeneratorDefault(files[7], version);               // DefaultSwiftRuntimeVersion constant
    }

    public void Dispose()
    {
        Exception? firstError = null;
        foreach (var (file, bytes) in _originals)
        {
            try
            {
                File.WriteAllBytes(file, bytes);
            }
            catch (Exception ex)
            {
                firstError ??= ex;
            }
        }
        if (firstError != null)
            throw new AggregateException("Failed to restore one or more version files", firstError);
    }

    /// <summary>
    /// Sets the <PackageVersion> element value in a .csproj file.
    /// </summary>
    private static void StampPackageVersion(string file, string version)
    {
        var doc = XDocument.Load(file, LoadOptions.PreserveWhitespace);
        var element = doc.Descendants("PackageVersion").FirstOrDefault()
            ?? throw new InvalidOperationException($"<PackageVersion> not found in {file}");
        element.Value = version;
        SaveXml(doc, file);
    }

    /// <summary>
    /// Sets the <SwiftRuntimePackageVersionRange> property in the supplement csproj
    /// so its Runtime ProjectReference &lt;Version&gt; metadata evaluates to the bounded
    /// range at pack time. Without this the supplement nupkg would declare Runtime as
    /// an unbounded min-only dep (inherited from Swift.Runtime's PackageVersion), which
    /// would let consumers float into a future incompatible Runtime minor.
    /// </summary>
    private static void StampSupplementRuntimeRange(string file, string version)
    {
        var doc = XDocument.Load(file, LoadOptions.PreserveWhitespace);
        var element = doc.Descendants("SwiftRuntimePackageVersionRange").FirstOrDefault()
            ?? throw new InvalidOperationException($"<SwiftRuntimePackageVersionRange> not found in {file}");
        element.Value = BindingsGeneration.RuntimeVersionRange.Build(version);
        SaveXml(doc, file);
    }

    /// <summary>
    /// Sets <_SwiftBindingSdkVersion>, <SwiftRuntimeVersion>,
    /// <SwiftRuntimePackageVersionRange>, and <SwiftAppleSupplementVersion> in Sdk.props.
    /// The range is the single source of truth for the SDK-emitted PackageReference —
    /// bare "0.8.0" would let NuGet float consumers into 0.9.0 where compatibility is not
    /// guaranteed. The supplement version is stamped separately so consumers can adopt a
    /// new Apple SDK train without waiting on a Runtime/SDK bump.
    /// </summary>
    private static void StampSdkProps(string file, string version, string appleVersion)
    {
        var doc = XDocument.Load(file, LoadOptions.PreserveWhitespace);

        var sdkVersion = doc.Descendants("_SwiftBindingSdkVersion").FirstOrDefault()
            ?? throw new InvalidOperationException($"<_SwiftBindingSdkVersion> not found in {file}");
        sdkVersion.Value = version;

        var runtimeVersion = doc.Descendants("SwiftRuntimeVersion").FirstOrDefault()
            ?? throw new InvalidOperationException($"<SwiftRuntimeVersion> not found in {file}");
        runtimeVersion.Value = version;

        var runtimeRange = doc.Descendants("SwiftRuntimePackageVersionRange").FirstOrDefault()
            ?? throw new InvalidOperationException($"<SwiftRuntimePackageVersionRange> not found in {file}");
        // RuntimeVersionRange is the shared single source of truth — the same file
        // is link-compiled by both the generator (for standalone csproj emission via
        // BindingProjectEmitter.BuildBoundedRuntimeVersionRange) and this build project
        // (for Sdk.props stamping). Two callers, one implementation, zero drift risk.
        runtimeRange.Value = BindingsGeneration.RuntimeVersionRange.Build(version);

        var appleSupplementVersion = doc.Descendants("SwiftAppleSupplementVersion").FirstOrDefault()
            ?? throw new InvalidOperationException($"<SwiftAppleSupplementVersion> not found in {file}");
        appleSupplementVersion.Value = appleVersion;

        SaveXml(doc, file);
    }

    /// <summary>
    /// Updates the Sdk="SwiftBindings.Sdk/..." attribute on the root Project element.
    /// </summary>
    private static void StampTemplateSdk(string file, string version)
    {
        var doc = XDocument.Load(file, LoadOptions.PreserveWhitespace);
        var root = doc.Root
            ?? throw new InvalidOperationException($"No root element in {file}");
        var sdkAttr = root.Attribute("Sdk")
            ?? throw new InvalidOperationException($"No Sdk attribute on <Project> in {file}");
        sdkAttr.Value = $"SwiftBindings.Sdk/{version}";
        SaveXml(doc, file);
    }

    /// <summary>
    /// Updates the sdkVersion symbol's defaultValue in template.json.
    /// </summary>
    /// <remarks>
    /// JsonNode's property iteration order tracks insertion order, so blind assignment to
    /// <c>sdk["defaultValue"] / sdk["replaces"]</c> either preserves existing order (if the
    /// keys were present) or appends in call order (if they weren't). Mixed-case inputs would
    /// re-serialize with keys in different orders across stamp calls, producing noisy diffs
    /// on every pack. Rebuild the symbol object with a fixed key order so the on-disk form is
    /// byte-stable regardless of the input template's key order.
    /// </remarks>
    private static void StampTemplateJson(string file, string version)
    {
        var node = JsonNode.Parse(File.ReadAllText(file))!;
        var symbols = node["symbols"]!.AsObject();
        var existing = symbols["sdkVersion"]!.AsObject();
        var rebuilt = new JsonObject();
        // Known keys first in a fixed order; preserve any future/additional keys afterwards.
        rebuilt["type"] = existing["type"]?.DeepClone();
        rebuilt["datatype"] = existing["datatype"]?.DeepClone();
        rebuilt["description"] = existing["description"]?.DeepClone();
        rebuilt["defaultValue"] = version;
        rebuilt["replaces"] = version;
        foreach (var kvp in existing)
        {
            if (rebuilt.ContainsKey(kvp.Key)) continue;
            rebuilt[kvp.Key] = kvp.Value?.DeepClone();
        }
        // Drop any rebuilt keys whose source was null (DeepClone on null yields null; JsonObject keeps them).
        foreach (var key in rebuilt.Where(kvp => kvp.Value is null).Select(kvp => kvp.Key).ToList())
            rebuilt.Remove(key);
        symbols["sdkVersion"] = rebuilt;
        File.WriteAllText(file, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>
    /// Updates the DefaultSwiftRuntimeVersion constant in BindingProjectEmitter.cs.
    /// </summary>
    private static void StampGeneratorDefault(string file, string version)
    {
        var content = File.ReadAllText(file);
        content = System.Text.RegularExpressions.Regex.Replace(content,
            @"DefaultSwiftRuntimeVersion\s*=\s*""[^""]*""",
            $@"DefaultSwiftRuntimeVersion = ""{version}""");
        File.WriteAllText(file, content);
    }

    /// <summary>
    /// Saves an XDocument without adding an XML declaration, preserving the original format.
    /// </summary>
    private static void SaveXml(XDocument doc, string file)
    {
        using var writer = new StreamWriter(file);
        doc.Save(writer, SaveOptions.DisableFormatting);
    }
}
