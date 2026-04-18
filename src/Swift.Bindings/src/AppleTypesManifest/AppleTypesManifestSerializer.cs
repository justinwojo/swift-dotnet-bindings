// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Newtonsoft.Json;

namespace BindingsGeneration.AppleTypesManifest;

// Writes a manifest to disk using the same two-space indent + trailing newline format as
// the hand-seeded reference file, so diffs stay reviewable. Json.NET honors the property
// ordering declared in the model via `JsonProperty(Order=...)`.
public static class AppleTypesManifestSerializer
{
    public static string Serialize(Manifest manifest)
    {
        var settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Include,
        };
        return JsonConvert.SerializeObject(manifest, settings);
    }

    public static void WriteTo(Manifest manifest, string path)
    {
        var content = Serialize(manifest);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, content + Environment.NewLine);
    }
}
