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
        StampSdkProps(files[4], version);                       // _SwiftBindingSdkVersion + SwiftRuntimeVersion
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
    /// Sets <_SwiftBindingSdkVersion> and <SwiftRuntimeVersion> in Sdk.props.
    /// </summary>
    private static void StampSdkProps(string file, string version)
    {
        var doc = XDocument.Load(file, LoadOptions.PreserveWhitespace);

        var sdkVersion = doc.Descendants("_SwiftBindingSdkVersion").FirstOrDefault()
            ?? throw new InvalidOperationException($"<_SwiftBindingSdkVersion> not found in {file}");
        sdkVersion.Value = version;

        var runtimeVersion = doc.Descendants("SwiftRuntimeVersion").FirstOrDefault()
            ?? throw new InvalidOperationException($"<SwiftRuntimeVersion> not found in {file}");
        runtimeVersion.Value = version;

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
    private static void StampTemplateJson(string file, string version)
    {
        var node = JsonNode.Parse(File.ReadAllText(file))!;
        var sdk = node["symbols"]!["sdkVersion"]!;
        sdk["defaultValue"] = version;
        sdk["replaces"] = version;
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
