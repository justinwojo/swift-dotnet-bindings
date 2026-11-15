// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Writes a module's transition manifest (<see cref="SwiftTransitionManifestFormat"/>) beside its C#:
/// the manifest itself, which the binding embeds as a resource, and the C# file declaring its
/// locator attribute, which the binding compiles. A module whose plan set records no Swift
/// transition has no manifest.
/// </summary>
/// <remarks>
/// Every producer writes it from the plan and guard sidecars it has just settled: generation once its
/// sidecars are written, the wrapper compile's routing (<see cref="SwiftBridgeFinalizer"/>) once it has
/// judged the receipt, and the hand-written table products. When the module routes through bridges,
/// the bridge assembly of every architecture gets the manifest hash
/// (<see cref="WithNativeHash"/>), so a wrapper is linked from the same manifest the assembly embeds.
/// </remarks>
public static class SwiftTransitionManifestWriter
{
    /// <summary>A written (or composed) manifest: its text, hash and whether it routes through bridges.</summary>
    public sealed record Manifest(string Module, string Stem, string Text, string Hash, bool RoutesBridges)
    {
        public string Resource => SwiftTransitionManifestFormat.FileName(Stem);
    }

    /// <summary>The contract a manifest is written under: the lowering-evidence contract of this generator.</summary>
    public static (int Plan, int Facts, int Collector) Contract
    {
        get
        {
            var current = SwiftLoweringContract.Current;
            return (current.PlanVersion, current.FactsVersion, current.CollectorVersion);
        }
    }

    /// <summary>
    /// The manifest of a generated module with plan sidecar text <paramref name="plans"/> and guard
    /// sidecar text <paramref name="guards"/>, or null when the plans record no transition.
    /// </summary>
    public static Manifest? Compose(string module, string stem, string? library, string plans, string? guards, string? origin = null)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(plans);
        if (document?["coverage"] is not System.Text.Json.Nodes.JsonArray { Count: > 0 })
            return null;
        var handWritten = origin == SwiftTransitionManifestFormat.HandWritten;
        var text = SwiftTransitionManifestFormat.Compose(
            module, origin ?? SwiftTransitionManifestFormat.Generated,
            handWritten ? null : SwiftLoweringEvidenceCollector.Generator, Contract, library, plans, guards);
        var routes = !SwiftTransitionManifestFormat.BridgedSymbols((System.Text.Json.Nodes.JsonObject)document).IsEmpty;
        return new Manifest(module, stem, text, SwiftTransitionManifestFormat.Hash(text), routes);
    }

    /// <summary>
    /// The manifest of the module whose sidecars generation (or routing) just wrote under
    /// <paramref name="@namespace"/> in <paramref name="directory"/>, read back from them; null when
    /// the module has no plan sidecar or its plans record no transition.
    /// </summary>
    public static Manifest? ComposeFromSidecars(string directory, string @namespace, string module, string? library)
    {
        var plansPath = Path.Combine(directory, SwiftCallPlanSet.FileName(@namespace));
        if (!File.Exists(plansPath))
            return null;
        var guardsPath = Path.Combine(directory, SwiftTransitionGuards.SidecarFileName(@namespace));
        return Compose(module, @namespace, library, File.ReadAllText(plansPath), File.Exists(guardsPath) ? File.ReadAllText(guardsPath) : null);
    }

    /// <summary>
    /// Writes <paramref name="manifest"/> and its attribute file under <paramref name="stem"/>, or
    /// removes both when it is null. Only a file whose content changes is written. Returns whether
    /// anything changed.
    /// </summary>
    public static bool Write(string directory, string stem, Manifest? manifest, Action<string, string>? write = null)
    {
        write ??= File.WriteAllText;
        var manifestPath = Path.Combine(directory, SwiftTransitionManifestFormat.FileName(stem));
        var attributePath = Path.Combine(directory, SwiftTransitionManifestFormat.AttributeFileName(stem));
        if (manifest is null)
        {
            var existed = File.Exists(manifestPath) || File.Exists(attributePath);
            if (File.Exists(attributePath))
                File.Delete(attributePath);
            if (File.Exists(manifestPath))
                File.Delete(manifestPath);
            return existed;
        }
        var changed = false;
        if (!File.Exists(manifestPath) || File.ReadAllText(manifestPath) != manifest.Text)
        {
            write(manifestPath, manifest.Text);
            changed = true;
        }
        var attribute = SwiftTransitionManifestFormat.AttributeSource([(manifest.Resource, manifest.Hash)]);
        if (!File.Exists(attributePath) || File.ReadAllText(attributePath) != attribute)
        {
            write(attributePath, attribute);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// <paramref name="bridges"/> (a bridge assembly) with the manifest hash appended, when
    /// <paramref name="manifest"/> routes through bridges; the assembly as it is otherwise.
    /// </summary>
    public static string WithNativeHash(string bridges, Manifest? manifest)
    {
        if (manifest is not { RoutesBridges: true })
            return bridges;
        var hash = SwiftTransitionManifestFormat.NativeAssembly(SwiftTransitionManifestFormat.NativeSymbol(manifest.Module), manifest.Hash);
        // Before the file's closing directive, which stays its last line.
        const string closing = ".subsections_via_symbols\n";
        return bridges.EndsWith(closing, StringComparison.Ordinal)
            ? bridges[..^closing.Length] + hash + closing
            : bridges + hash;
    }
}
