// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Newtonsoft.Json;

namespace BindingsGeneration.AppleTypesManifest;

// Per-type positive list of Swift identities approved for `[StructLayout(Sequential)]`
// emission. Default storage strategy is VWT-backed opaque; this list is consulted only
// when an entry wants the sequential (frozen-trivially-copyable) path. The gate also
// requires frozen=true, non-generic, size/alignment validated against the live SDK —
// the whitelist name alone is not sufficient. The positive-list design exists to keep
// additions deliberate: a single wrong sequential emission on a non-frozen type would
// be an ABI regression that binds every downstream package.
public sealed class SequentialLayoutWhitelist
{
    private HashSet<string>? _lookup;

    [JsonProperty("approved_identities", Order = 1)]
    public List<string> ApprovedIdentities { get; set; } = new();

    // Ordinal comparison to match every other identity comparison in the pipeline
    // (TypeOwnerRegistry, AppleTypesManifestBuilder, etc.). A culture-sensitive default
    // comparer would silently miss a casing/locale-aware edge and the whitelist opt-in
    // would never activate for the affected identity.
    public bool Contains(string swiftIdentity)
    {
        var lookup = _lookup ??= new HashSet<string>(ApprovedIdentities, StringComparer.Ordinal);
        return lookup.Contains(swiftIdentity);
    }

    public static SequentialLayoutWhitelist Empty() => new();

    public static SequentialLayoutWhitelist Load(string path)
    {
        var text = File.ReadAllText(path);
        var loaded = JsonConvert.DeserializeObject<SequentialLayoutWhitelist>(text);
        // Fail-closed on null (empty file, literal `null`, or malformed top-level): silently
        // returning an empty whitelist would look identical to a legitimate empty list and mask
        // a corrupted ship artifact. Match AppleTypesManifestBuilder.IngestAbiJson's pattern.
        return loaded ?? throw new InvalidDataException(
            $"SequentialLayoutWhitelist: '{path}' deserialized to null. Expected a JSON object " +
            "with 'approved_identities'. An empty whitelist must still be a valid object ({}).");
    }
}
