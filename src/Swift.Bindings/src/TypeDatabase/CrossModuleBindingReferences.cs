// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace BindingsGeneration;

/// <summary>
/// Per-run collector of the Swift modules whose types the current emission actually resolved.
/// The csproj emitter turns the registered subset of these into sibling binding-package
/// <c>PackageReference</c> items, so a binding that names a dependency module's type in its
/// emitted C# also carries the package that supplies it.
/// </summary>
/// <remarks>
/// <para>Records every resolving module, including the module being generated and modules that
/// ship no binding package. Filtering is deliberately deferred to read time
/// (<see cref="AppleFrameworkImportDetector.ResolveDependencies"/> drops self-references and
/// any module without a registered <c>packageId</c>) so this side of the wire stays a plain
/// observation of what resolved rather than a second copy of the ownership policy.</para>
/// <para>Use-based rather than import-based: the SDK's
/// <c>--detect-apple-cross-module-deps</c> path derives the same edges from a
/// <c>.swiftinterface</c>'s <c>import</c> lines, which over-records imports whose types the
/// binding never surfaces. This collector sees only modules a resolved <c>TypeRecord</c> came
/// from, so the emitted reference set matches the namespaces the emitted C# actually uses.</para>
/// <para><c>[ThreadStatic]</c> for the same reason as
/// <see cref="AppleSupplementReferences"/>: parallel test fixtures emit independent modules on
/// their own threads, and production emission is single-threaded. Flushed by
/// <see cref="Reset"/> at the start of every module emission attempt, alongside the supplement
/// collector — a withdrawn attempt's references must not leak into the emitted csproj.</para>
/// </remarks>
public static class CrossModuleBindingReferences
{
    [ThreadStatic]
    private static Dictionary<string, SortedSet<string>>? s_current;

    /// <summary>
    /// Clears the recorded set. Callers invoke this at the start of every module emission so
    /// references from a previous module (or a discarded attempt) do not leak into the next
    /// consumer's csproj.
    /// </summary>
    public static void Reset() => s_current?.Clear();

    /// <summary>
    /// Records that the current emission resolved a type declared by <paramref name="swiftModule"/>.
    /// <paramref name="callerHint"/> names the mechanism that produced the reference, captured as
    /// provenance so a reader can see why a sibling package reference exists.
    /// </summary>
    public static void Record(string? swiftModule, string callerHint)
    {
        if (string.IsNullOrEmpty(swiftModule))
            return;
        s_current ??= new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        if (!s_current.TryGetValue(swiftModule, out var hints))
        {
            hints = new SortedSet<string>(StringComparer.Ordinal);
            s_current[swiftModule] = hints;
        }
        if (!string.IsNullOrEmpty(callerHint))
            hints.Add(callerHint);
    }

    /// <summary>True when at least one module was recorded since the last reset.</summary>
    public static bool Any => s_current is { Count: > 0 };

    /// <summary>The recorded Swift module names, ordinally sorted for deterministic output.</summary>
    public static IReadOnlyCollection<string> Current =>
        s_current is null
            ? Array.Empty<string>()
            : s_current.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray();
}
