// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Writes a generator artifact into the output directory so that a concurrent reader sees either
/// the previous whole file or the next one, never a partial one: a same-directory temp is filled,
/// flushed to disk, then renamed onto the final path. <c>File.Move(overwrite: true)</c> is
/// rename(2) on POSIX, atomic because the temp lives on the same filesystem as its destination.
/// </summary>
/// <remarks>
/// The temp file name is unique per writer, which is what makes the helper safe to call from two
/// processes at once. Concurrent generator invocations against one output directory are a normal
/// build shape — a parallel build matrix regenerates the same RID-agnostic dependency project
/// (<c>obj/&lt;cfg&gt;/&lt;tfm&gt;/swift-binding/</c>) from two cells at the same time — and with a
/// fixed temp name the second writer's <see cref="FileMode.Create"/>/<see cref="FileShare.None"/>
/// open throws, because .NET enforces share modes across processes on macOS via advisory locks.
/// Unique temps let both writers run to completion and the rename decides the winner. Interleaving
/// is last-writer-wins, which is correct for these artifacts: the same module and the same inputs
/// through a deterministic generator produce identical bytes.
/// </remarks>
internal static class AtomicArtifactWriter
{
    /// <summary>
    /// How old an unconsumed temp file must be before a later writer reclaims it. Generously past
    /// any plausible write duration so the sweep can only ever see an abandoned temp, never one a
    /// peer process is actively filling.
    /// </summary>
    private static readonly TimeSpan StaleTempFileAge = TimeSpan.FromHours(1);

    /// <summary>Distinguishes temp files created concurrently within one process.</summary>
    private static int _tempFileSequence;

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="finalPath"/> via a same-directory temp
    /// and an atomic rename.
    /// </summary>
    internal static void Write(string finalPath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        ArgumentNullException.ThrowIfNull(content);

        var dir = Path.GetDirectoryName(finalPath)!;
        var fileName = Path.GetFileName(finalPath);
        // Process id separates concurrent processes (unique among live processes); the counter
        // separates concurrent threads within one. Keeping ".tmp" last means anything already
        // configured to ignore temp files still ignores these.
        var tmpPath = Path.Combine(
            dir,
            $"{fileName}.{Environment.ProcessId}-{Interlocked.Increment(ref _tempFileSequence)}.tmp");

        SweepAbandonedTempFiles(dir, fileName);

        try
        {
            using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tmpPath, finalPath, overwrite: true);
        }
        catch
        {
            // A failed write must not leave its temp behind: unique names would otherwise
            // accumulate one orphan per failure. The successful path consumes the temp via the
            // rename, so there is nothing to delete there.
            try
            {
                File.Delete(tmpPath);
            }
            catch (Exception cleanupFailure) when (
                cleanupFailure is IOException or UnauthorizedAccessException)
            {
                // Best effort — the original failure is what the caller needs to see.
            }

            throw;
        }
    }

    /// <summary>
    /// Deletes temp files left by a writer that died between creating its temp and renaming it.
    /// Only files matching this writer's temp shape and older than <see cref="StaleTempFileAge"/>
    /// are removed, so a peer process mid-write is never touched. Best effort: a sweep failure must
    /// never sink the write it precedes.
    /// </summary>
    private static void SweepAbandonedTempFiles(string dir, string fileName)
    {
        try
        {
            var cutoff = DateTime.UtcNow - StaleTempFileAge;
            foreach (var candidate in Directory.EnumerateFiles(dir, fileName + "*.tmp"))
            {
                if (File.GetLastWriteTimeUtc(candidate) < cutoff)
                    File.Delete(candidate);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Orphans are inert; failing the write over one would be strictly worse.
        }
    }
}
