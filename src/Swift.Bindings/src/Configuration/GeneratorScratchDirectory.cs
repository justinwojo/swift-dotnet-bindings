// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// A scratch directory private to this generator process, for derived inputs that are consumed
/// during the run and are not part of the binding a consumer receives — the <c>.abi.json</c> and
/// <c>.tbd</c> that dependency resolution extracts from a dependency xcframework.
/// </summary>
/// <remarks>
/// Those files are named after the Swift module they describe, so writing them straight into the
/// OS temp root makes any two generator processes resolving a dependency with the same module name
/// race on one path — and unlike the manifest artifacts they are produced by an external tool
/// (<c>swift-frontend</c>, <c>tapi</c>) writing in place, so the loser reads a truncated file
/// rather than getting a clean error. The processes need not be related: a parallel build matrix,
/// or two unrelated projects that both depend on the same vendored framework, is enough. Scoping
/// the directory to the process removes the shared path entirely.
/// </remarks>
internal static class GeneratorScratchDirectory
{
    private const string DirectoryPrefix = "swiftbindings-scratch-";

    /// <summary>
    /// How old an abandoned scratch directory must be before a later process reclaims it. Well past
    /// any plausible generator run, so a live peer's directory is never removed underneath it.
    /// </summary>
    private static readonly TimeSpan AbandonedScratchAge = TimeSpan.FromDays(1);

    private static readonly Lazy<string> LazyPath = new(Create, isThreadSafe: true);

    /// <summary>The per-process scratch directory, created on first use.</summary>
    internal static string Path => LazyPath.Value;

    private static string Create()
    {
        var root = System.IO.Path.GetTempPath();
        var path = System.IO.Path.Combine(root, $"{DirectoryPrefix}{Environment.ProcessId}");

        // A recycled process id can inherit a dead peer's directory; start from a clean one so a
        // stale derived input can never be mistaken for this run's.
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reusing the directory is still correct — every file in it is overwritten by name.
        }

        Directory.CreateDirectory(path);
        SweepAbandonedScratchDirectories(root);
        return path;
    }

    /// <summary>
    /// Removes scratch directories left behind by processes that died before cleaning up. Best
    /// effort and age-bounded, so a concurrently running generator's directory is never touched.
    /// </summary>
    private static void SweepAbandonedScratchDirectories(string root)
    {
        try
        {
            var cutoff = DateTime.UtcNow - AbandonedScratchAge;
            foreach (var candidate in Directory.EnumerateDirectories(root, DirectoryPrefix + "*"))
            {
                if (Directory.GetLastWriteTimeUtc(candidate) < cutoff)
                    Directory.Delete(candidate, recursive: true);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Leftovers are inert; failing a generation over one would be strictly worse.
        }
    }
}
