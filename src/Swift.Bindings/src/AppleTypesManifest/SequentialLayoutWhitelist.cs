// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Newtonsoft.Json;

namespace BindingsGeneration.AppleTypesManifest;

// Per-type positive list of Swift identities approved for `[StructLayout(Sequential)]`
// emission. Default storage strategy is VWT-backed opaque; this list is consulted only
// when an entry wants the sequential (frozen-trivially-copyable) path. The gate also
// requires frozen=true, non-generic, size/alignment validated against the live SDK —
// the whitelist name alone is not sufficient. See
// `apple-swift-types-architecture.md` §Decision summary item 3 / §Q8.
public sealed class SequentialLayoutWhitelist
{
    [JsonProperty("approved_identities", Order = 1)]
    public List<string> ApprovedIdentities { get; set; } = new();

    public bool Contains(string swiftIdentity) =>
        ApprovedIdentities.Contains(swiftIdentity);

    public static SequentialLayoutWhitelist Empty() => new();

    public static SequentialLayoutWhitelist Load(string path)
    {
        var text = File.ReadAllText(path);
        var loaded = JsonConvert.DeserializeObject<SequentialLayoutWhitelist>(text);
        return loaded ?? Empty();
    }
}
