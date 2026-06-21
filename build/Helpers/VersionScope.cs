// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;

/// <summary>
/// Carries the shipped version(s) into a NuGet pack/publish without mutating any
/// source-controlled file.
/// </summary>
/// <remarks>
/// The version a package ships under reaches every artifact through MSBuild
/// properties rather than by rewriting checked-in files:
/// <list type="bullet">
///   <item><c>SwiftBindingsSdkVersion</c> / <c>SwiftBindingsAppleVersion</c> feed the
///   four <c>&lt;PackageVersion&gt;</c> elements (and bake the generator's
///   <c>DefaultSwiftRuntimeVersion</c> const via the obj/GeneratedVersion.cs target).</item>
///   <item><c>SwiftRuntimePackageVersionRange</c> floors the Apple supplement's outbound
///   Runtime dependency to <c>[X.Y.Z,)</c>.</item>
///   <item><c>SwiftBindingsSdkPropsToPack</c> / <c>SwiftBindingsTemplateJsonToPack</c> point
///   the SDK and Templates packs at version-baked copies of <c>Sdk.props</c> and
///   <c>template.json</c> staged under the gitignored <c>artifacts/</c> tree — those two files
///   ship verbatim and are consumed at end-user build time, so they cannot read repo
///   properties and must instead carry the real version in their own content.</item>
/// </list>
/// The source files keep a <c>0.0.0-dev</c> sentinel so a plain <c>dotnet pack</c> still
/// produces something coherent; the real version only ever lives in the staged copies and the
/// passed properties, never in the working tree. Because nothing is mutated, there is no
/// backup/restore — <see cref="Dispose"/> only removes the staged copies.
///
/// The Apple supplement (<c>SwiftBindings.Apple</c>) is versioned per Apple SDK train and
/// stamped independently, so consumers can adopt a new supplement release without waiting on a
/// Runtime/SDK bump. When <paramref name="appleVersion"/> is null the main version is used for
/// the supplement as well, preserving the pre-split behavior.
/// </remarks>
public sealed class VersionScope : IDisposable
{
    private readonly string _version;
    private readonly string _appleVersion;
    private readonly string _supplementRuntimeRange;
    private readonly AbsolutePath _stagingDir;
    private readonly string _stagedSdkProps;
    private readonly string _stagedTemplateJson;

    public VersionScope(string version, AbsolutePath repoRoot, string? appleVersion = null)
    {
        _version = version;
        _appleVersion = appleVersion ?? version;
        // Floor-only range for the supplement's outbound Runtime dependency. RuntimeVersionRange
        // is the shared single source of truth — link-compiled by both the generator (standalone
        // csproj emission) and this build project — so the floor and the SDK's bounded range
        // cannot drift.
        _supplementRuntimeRange = BindingsGeneration.RuntimeVersionRange.BuildMinimumOnly(version);

        var sourceSdkProps = repoRoot / "src" / "Swift.Bindings.Sdk" / "Sdk" / "Sdk.props";
        var sourceTemplateJson = repoRoot / "src" / "Swift.Bindings.Templates" / "content"
            / "swift-binding" / ".template.config" / "template.json";
        foreach (var f in new[] { sourceSdkProps, sourceTemplateJson })
            if (!File.Exists(f))
                throw new FileNotFoundException($"Version source file not found: {f}");

        // Stage version-baked copies under the gitignored artifacts/ tree. Clean + recreate so a
        // re-run at the same version cannot pick up a stale baked copy from a prior run.
        _stagingDir = repoRoot / "artifacts" / "version-staging" / version;
        if (Directory.Exists(_stagingDir))
            Directory.Delete(_stagingDir, recursive: true);
        Directory.CreateDirectory(_stagingDir);

        _stagedSdkProps = _stagingDir / "Sdk.props";
        _stagedTemplateJson = _stagingDir / "template.json";

        BakeSdkProps(sourceSdkProps, _stagedSdkProps, version, _appleVersion);
        BakeTemplateJson(sourceTemplateJson, _stagedTemplateJson, version);
    }

    /// <summary>
    /// Adds the version properties to a pack invocation. Properties a given project does not
    /// read are simply ignored, so one uniform application is correct for every package.
    /// </summary>
    public DotNetPackSettings Apply(DotNetPackSettings settings) =>
        settings
            .SetProperty("SwiftBindingsSdkVersion", _version)
            .SetProperty("SwiftBindingsAppleVersion", _appleVersion)
            .SetProperty("SwiftRuntimePackageVersionRange", MsBuildPropertyValue.Escape(_supplementRuntimeRange))
            .SetProperty("SwiftBindingsSdkPropsToPack", _stagedSdkProps)
            .SetProperty("SwiftBindingsTemplateJsonToPack", _stagedTemplateJson);

    /// <summary>
    /// Adds the version properties to a publish invocation (used for the generator publish, which
    /// bakes <c>DefaultSwiftRuntimeVersion</c> from <c>SwiftBindingsSdkVersion</c>).
    /// </summary>
    public DotNetPublishSettings Apply(DotNetPublishSettings settings) =>
        settings
            .SetProperty("SwiftBindingsSdkVersion", _version)
            .SetProperty("SwiftBindingsAppleVersion", _appleVersion)
            .SetProperty("SwiftRuntimePackageVersionRange", MsBuildPropertyValue.Escape(_supplementRuntimeRange))
            .SetProperty("SwiftBindingsSdkPropsToPack", _stagedSdkProps)
            .SetProperty("SwiftBindingsTemplateJsonToPack", _stagedTemplateJson);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_stagingDir))
                Directory.Delete(_stagingDir, recursive: true);
        }
        catch
        {
            // Staging lives under the gitignored artifacts/ tree; a failed cleanup only leaves a
            // stale baked copy that the next same-version run overwrites. Not worth throwing over.
        }
    }

    /// <summary>
    /// Bakes a version-stamped copy of Sdk.props. Sets <c>_SwiftBindingSdkVersion</c>,
    /// <c>SwiftRuntimeVersion</c>, <c>SwiftRuntimePackageVersionRange</c> (the SDK-emitted
    /// PackageReference range — bounded, not bare, so NuGet cannot float consumers across a
    /// compatibility boundary), and <c>SwiftAppleSupplementVersion</c>.
    /// </summary>
    private static void BakeSdkProps(string source, string dest, string version, string appleVersion)
    {
        var doc = XDocument.Load(source, LoadOptions.PreserveWhitespace);
        SetElementValue(doc, source, "_SwiftBindingSdkVersion", version);
        SetElementValue(doc, source, "SwiftRuntimeVersion", version);
        SetElementValue(doc, source, "SwiftRuntimePackageVersionRange",
            BindingsGeneration.RuntimeVersionRange.Build(version));
        SetElementValue(doc, source, "SwiftAppleSupplementVersion", appleVersion);
        SaveXml(doc, dest);
    }

    private static void SetElementValue(XDocument doc, string source, string name, string value)
    {
        var element = doc.Descendants(name).FirstOrDefault()
            ?? throw new InvalidOperationException($"<{name}> not found in {source}");
        element.Value = value;
    }

    /// <summary>
    /// Bakes a version-stamped copy of template.json: only the <c>sdkVersion</c> symbol's
    /// <c>defaultValue</c> becomes the real version. Its <c>replaces</c> token deliberately stays
    /// the source <c>0.0.0-dev</c> sentinel — that is the literal string packed verbatim into
    /// ProjectName.csproj's <c>Sdk</c> attribute, which the template engine swaps for
    /// <c>defaultValue</c> when a user runs <c>dotnet new</c>. Rewriting <c>replaces</c> would
    /// break that swap.
    /// </summary>
    /// <remarks>
    /// JsonNode's property iteration order tracks insertion order, so the symbol object is rebuilt
    /// with a fixed key order to keep the on-disk form byte-stable regardless of the source's key
    /// order.
    /// </remarks>
    private static void BakeTemplateJson(string source, string dest, string version)
    {
        var node = JsonNode.Parse(File.ReadAllText(source))!;
        var symbols = node["symbols"]!.AsObject();
        var existing = symbols["sdkVersion"]!.AsObject();
        var rebuilt = new JsonObject();
        // Known keys first in a fixed order; preserve any future/additional keys afterwards.
        rebuilt["type"] = existing["type"]?.DeepClone();
        rebuilt["datatype"] = existing["datatype"]?.DeepClone();
        rebuilt["description"] = existing["description"]?.DeepClone();
        rebuilt["defaultValue"] = version;
        rebuilt["replaces"] = existing["replaces"]?.DeepClone();
        foreach (var kvp in existing)
        {
            if (rebuilt.ContainsKey(kvp.Key)) continue;
            rebuilt[kvp.Key] = kvp.Value?.DeepClone();
        }
        // Drop any rebuilt keys whose source was null (DeepClone on null yields null; JsonObject keeps them).
        foreach (var key in rebuilt.Where(kvp => kvp.Value is null).Select(kvp => kvp.Key).ToList())
            rebuilt.Remove(key);
        symbols["sdkVersion"] = rebuilt;
        File.WriteAllText(dest, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
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
