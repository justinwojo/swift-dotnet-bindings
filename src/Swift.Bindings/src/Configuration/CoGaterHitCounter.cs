// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using Newtonsoft.Json;

namespace BindingsGeneration
{
    /// <summary>
    /// Temporary instrumentation for the M3 cogater inventory pass: counts post-emission
    /// handler hits per library so the Session 2/3 fix-or-keep decisions can be sized.
    /// Active only when SWIFTBIND_DUMP_COGATER_COUNTS=1; all paths are zero-cost otherwise.
    /// Deleted in M3 close per the standing rule on milestone scaffolding.
    /// </summary>
    internal static class CoGaterHitCounter
    {
        private static readonly bool s_active =
            string.Equals(
                Environment.GetEnvironmentVariable("SWIFTBIND_DUMP_COGATER_COUNTS"),
                "1",
                StringComparison.Ordinal);

        private static readonly ConcurrentDictionary<string, int> s_counts = new(StringComparer.Ordinal);
        private static readonly object s_dumpLock = new();

        public static bool IsActive => s_active;

        public static void Increment(string handlerName)
        {
            if (!s_active) return;
            s_counts.AddOrUpdate(handlerName, 1, (_, v) => v + 1);
        }

        /// <summary>
        /// Merges in-memory counts into <paramref name="outputDirectory"/>/cogater-counts.json
        /// under <paramref name="moduleName"/>. Multiple generator phases that share an output
        /// directory accumulate additively into the same file. No-op when the env var is unset
        /// or no counter has fired.
        /// </summary>
        public static void TryDump(string outputDirectory, string moduleName)
        {
            if (!s_active) return;
            if (s_counts.IsEmpty) return;
            if (string.IsNullOrEmpty(outputDirectory) || string.IsNullOrEmpty(moduleName)) return;

            // In-process lock serializes concurrent TryDump calls within one process so they
            // can't independently snapshot/clear and lose increments to each other.
            lock (s_dumpLock)
            {
                if (s_counts.IsEmpty) return;

                FileStream? lockHandle = null;
                try
                {
                    var path = Path.Combine(outputDirectory, "cogater-counts.json");
                    var lockPath = path + ".lock";

                    // Cross-process serialization via a sidecar lock file. FileShare.None
                    // throws (does not wait) when another process holds the lock; retry on
                    // IOException with backoff so a brief contender doesn't lose its
                    // snapshot. If we can't acquire within the retry budget, return without
                    // touching s_counts — the next TryDump retries. Other FileStream
                    // exceptions (UnauthorizedAccessException, DirectoryNotFoundException,
                    // PathTooLongException, ArgumentException, …) fall through to the outer
                    // catch and are swallowed without touching s_counts.
                    for (int attempt = 0; attempt < 40 && lockHandle is null; attempt++)
                    {
                        try
                        {
                            lockHandle = new FileStream(
                                lockPath,
                                FileMode.OpenOrCreate,
                                FileAccess.ReadWrite,
                                FileShare.None);
                        }
                        catch (IOException)
                        {
                            Thread.Sleep(50);
                        }
                    }
                    if (lockHandle is null) return;

                    // Atomic per-key snapshot under the held lock: TryRemove returns the
                    // value and removes the entry as one operation, so concurrent Increment
                    // calls either land in this snapshot or in a fresh entry the next dump
                    // picks up — never silently dropped between a bulk-copy and a Clear().
                    var snapshot = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var key in s_counts.Keys)
                    {
                        if (s_counts.TryRemove(key, out var value) && value > 0)
                            snapshot[key] = value;
                    }
                    if (snapshot.Count == 0) return;

                    // Past this point a failure drops the snapshot rather than carrying it
                    // forward to a later module — that misattribution would corrupt the
                    // histogram more than a one-module gap.
                    Dictionary<string, Dictionary<string, int>> existing;
                    if (File.Exists(path))
                    {
                        var json = File.ReadAllText(path);
                        existing = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, int>>>(json)
                            ?? new(StringComparer.Ordinal);
                    }
                    else
                    {
                        existing = new(StringComparer.Ordinal);
                    }

                    if (!existing.TryGetValue(moduleName, out var moduleCounts))
                    {
                        moduleCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                        existing[moduleName] = moduleCounts;
                    }
                    foreach (var kv in snapshot)
                    {
                        moduleCounts[kv.Key] = moduleCounts.TryGetValue(kv.Key, out var v)
                            ? v + kv.Value
                            : kv.Value;
                    }

                    File.WriteAllText(path, JsonConvert.SerializeObject(existing, Formatting.Indented));
                }
                catch
                {
                    // Instrumentation must never fail the build. Covers any exception from
                    // path validation, file-lock acquisition (non-IOException), JSON
                    // (de)serialization, or read/write I/O.
                }
                finally
                {
                    lockHandle?.Dispose();
                }
            }
        }
    }
}
