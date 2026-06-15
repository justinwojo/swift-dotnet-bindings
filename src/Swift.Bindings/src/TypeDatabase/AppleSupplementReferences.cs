// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace BindingsGeneration;

/// <summary>
/// Per-run collector of Swift identities that resolved to the <c>SwiftBindings.Apple</c>
/// supplement during binding emission. When any identity is recorded, the csproj emitter
/// adds a <c>PackageReference</c> to the supplement so the consumer assembly can actually
/// reference the projected type; non-Apple consumers stay untouched.
/// </summary>
/// <remarks>
/// <para>Thread-static because the generator processes one module per thread in tests
/// (parallel test fixtures run independent modules). Production invocation is single-threaded.
/// The recorded set is flushed with <see cref="Reset"/> at the start of each module.</para>
/// <para>Finding 14c: every record now carries a <c>callerHint</c> — the resolution
/// mechanism that produced it (a strategy name, an emitter site, a marshaler fallback). The
/// hints are aggregated per identity and surfaced as provenance on the artifact manifest's
/// emission section, so a reader can see *why* a supplement reference (and therefore the
/// consumer's <c>SwiftBindings.Apple</c> <c>PackageReference</c>) exists rather than having to
/// reverse-engineer it from a bare identity list.</para>
/// </remarks>
public static class AppleSupplementReferences
{
    [ThreadStatic]
    private static Dictionary<string, SortedSet<string>>? s_current;

    /// <summary>
    /// Clears the recorded set. Callers invoke this at the start of every module emission so
    /// references from a previous module do not leak into the next consumer's csproj.
    /// </summary>
    public static void Reset() => s_current?.Clear();

    /// <summary>
    /// Records that the current emission referenced the given Swift identity and it resolved to
    /// the Apple supplement. <paramref name="callerHint"/> names the mechanism that produced the
    /// reference (e.g. <c>"strategy:AppleSupplement"</c>, <c>"ExistentialHandler.AnyError"</c>),
    /// captured as provenance so the manifest can explain each entry.
    /// </summary>
    public static void Record(string swiftIdentity, string callerHint)
    {
        if (string.IsNullOrEmpty(swiftIdentity))
            return;
        s_current ??= new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        if (!s_current.TryGetValue(swiftIdentity, out var hints))
        {
            hints = new SortedSet<string>(StringComparer.Ordinal);
            s_current[swiftIdentity] = hints;
        }
        if (!string.IsNullOrEmpty(callerHint))
            hints.Add(callerHint);
    }

    /// <summary>True when at least one Apple supplement reference was recorded since the last reset.</summary>
    public static bool Any => s_current is { Count: > 0 };

    /// <summary>Returns the recorded identities sorted lexicographically for deterministic output.</summary>
    public static IReadOnlyCollection<string> Current =>
        s_current is null ? Array.Empty<string>() : s_current.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Finding 14c: returns each recorded identity with its aggregated provenance hints, sorted
    /// for deterministic output. Consumed by the artifact manifest's emission section.
    /// </summary>
    public static IReadOnlyList<(string Identity, IReadOnlyList<string> Provenance)> Snapshot()
    {
        if (s_current is null || s_current.Count == 0)
            return Array.Empty<(string, IReadOnlyList<string>)>();
        return s_current
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key, (IReadOnlyList<string>)kv.Value.ToArray()))
            .ToArray();
    }
}
