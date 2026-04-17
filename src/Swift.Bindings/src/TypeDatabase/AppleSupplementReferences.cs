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
/// </remarks>
public static class AppleSupplementReferences
{
    [ThreadStatic]
    private static HashSet<string>? s_current;

    /// <summary>
    /// Clears the recorded set. Callers invoke this at the start of every module emission so
    /// references from a previous module do not leak into the next consumer's csproj.
    /// </summary>
    public static void Reset() => s_current?.Clear();

    /// <summary>
    /// Records that the current emission referenced the given Swift identity and it resolved
    /// to the Apple supplement.
    /// </summary>
    public static void Record(string swiftIdentity)
    {
        if (string.IsNullOrEmpty(swiftIdentity))
            return;
        (s_current ??= new HashSet<string>(StringComparer.Ordinal)).Add(swiftIdentity);
    }

    /// <summary>True when at least one Apple supplement reference was recorded since the last reset.</summary>
    public static bool Any => s_current is { Count: > 0 };

    /// <summary>Returns the recorded identities sorted lexicographically for deterministic output.</summary>
    public static IReadOnlyCollection<string> Current =>
        s_current is null ? Array.Empty<string>() : s_current.OrderBy(s => s, StringComparer.Ordinal).ToArray();
}
