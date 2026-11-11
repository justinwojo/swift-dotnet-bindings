// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// What a RuntimeTestsApp build recorded about itself, written beside the app bundle once the
/// bundle is final. A later <c>--skip-build</c> run reads it back to prove it is installing that
/// build rather than whatever happens to sit at the bundle path.
/// </summary>
/// <remarks>
/// Two levels of match. <see cref="Refusal"/> covers what makes reuse wrong outright: no receipt,
/// another lane (framework, engine, configuration, compiled-in opt-in tests) or another toolchain.
/// <see cref="StaleReason"/> covers what makes reuse merely unproven: the source changed since the
/// build, or the bundle itself did. A reuse that passes both is the same build the current source
/// and toolchain produce, so its results can be graded like a fresh build's; an unproven one still
/// runs, as a partial run, the way every <c>--skip-build</c> run did before receipts existed.
/// </remarks>
public sealed record AppBuildReceipt(string Lane, string Toolchain, string Source, string Bundle, DateTimeOffset BuiltAt)
{
    public const string FileSuffix = ".bindingtests-build.json";

    public static string PathFor(string appPath) => Path.TrimEndingDirectorySeparator(appPath) + FileSuffix;

    public void Save(string appPath) => File.WriteAllText(PathFor(appPath), new JsonObject
    {
        ["lane"] = Lane,
        ["toolchain"] = Toolchain,
        ["source"] = Source,
        ["bundle"] = Bundle,
        ["builtAt"] = BuiltAt.ToString("O"),
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>The recorded receipt, or null when there is none or it cannot be read.</summary>
    public static AppBuildReceipt? Load(string appPath)
    {
        var path = PathFor(appPath);
        if (!File.Exists(path))
            return null;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject json)
                return null;
            string? Text(string name) => json[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            return Text("lane") is { } lane && Text("toolchain") is { } toolchain && Text("source") is { } source &&
                   Text("bundle") is { } bundle && DateTimeOffset.TryParse(Text("builtAt"), null,
                       System.Globalization.DateTimeStyles.RoundtripKind, out var builtAt)
                ? new AppBuildReceipt(lane, toolchain, source, bundle, builtAt)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Why reusing the app is refused outright, or null when it may be reused.</summary>
    public static string? Refusal(AppBuildReceipt? recorded, string lane, string toolchain)
    {
        if (recorded == null)
            return "it has no readable build receipt";
        if (recorded.Lane != lane)
            return $"it was built for {recorded.Lane}, not {lane}";
        if (recorded.Toolchain != toolchain)
            return "it was built with a different SDK, workload or Xcode";
        return null;
    }

    /// <summary>
    /// Why the reused app cannot stand in for a fresh build, or null when it is that build. The
    /// bundle digest is computed only when the source still matches.
    /// </summary>
    public string? StaleReason(string source, Func<string> bundleDigest)
    {
        if (Source != source)
            return "the source changed since it was built";
        if (Bundle != bundleDigest())
            return "the app bundle changed since it was built";
        return null;
    }

    /// <summary>
    /// SHA-256 over the bundle tree: every entry's relative path and kind, each file's content and
    /// each symbolic link's target (never followed), in ordinal order.
    /// </summary>
    public static string BundleDigest(string appPath)
    {
        var root = new DirectoryInfo(appPath);
        if (!root.Exists)
            throw new DirectoryNotFoundException($"App bundle not found: {appPath}");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string text) => hash.AppendData(Encoding.UTF8.GetBytes(text));
        void Walk(DirectoryInfo directory, string relative)
        {
            foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                var path = relative + "/" + entry.Name;
                if (entry.LinkTarget is { } target)
                    Append($"L\0{path}\0{target}\n");
                else if (entry is DirectoryInfo subdirectory)
                {
                    Append($"D\0{path}\n");
                    Walk(subdirectory, path);
                }
                else
                {
                    using var stream = File.OpenRead(entry.FullName);
                    Append($"F\0{path}\0{Convert.ToHexString(SHA256.HashData(stream))}\n");
                }
            }
        }
        Walk(root, "");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
