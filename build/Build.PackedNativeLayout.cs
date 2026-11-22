// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.PackedNativeLayout.cs — where a packed Swift/mixed binding package keeps its natives.
//
// A binding package keys every per-copy payload by the full pack TFM, the same folder name as
// its lib/<tfm>/: the wrapper, the sliced source xcframework, the SwiftUI bridge and the resource
// bundles all live under native/<tfm>/, and buildTransitive/<tfm>/<Id>.targets NativeReferences
// them from there. A package may carry several copies of one platform (net10.0-ios26.0 next to
// net11.0-ios27.2), each with its own native/<tfm>/. Nothing ships under runtimes/ any more.
//
// Only binding packages use this layout. SwiftBindings.Runtime (native/ at the package root),
// the SwiftBindings.Apple supplement (runtimes/native/) and the pure-ObjC lane (the
// lib/<tfm>/<Asm>.resources.zip sidecar) are unchanged, so these helpers are not for them.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common.IO;

partial class Build
{
    /// <summary>The native directory of one copy, <c>native/&lt;tfm&gt;/</c>, inside an extracted binding package.</summary>
    static AbsolutePath PackedNativeDir(AbsolutePath extractDir, string tfm) => extractDir / "native" / tfm;

    /// <summary>The package-relative form of <see cref="PackedNativeDir"/>, for failure messages.</summary>
    static string PackedNativeRelative(string tfm) => $"native/{tfm}/";

    /// <summary>
    /// The Apple platform of a pack TFM: <c>net10.0-ios26.0</c> → <c>ios</c>,
    /// <c>net11.0-maccatalyst27.2</c> → <c>maccatalyst</c>. Null for a TFM with no platform part
    /// (a plain <c>net10.0</c>).
    /// </summary>
    static string? PackTfmPlatform(string tfm)
    {
        var dash = tfm.IndexOf('-');
        if (dash < 0 || dash == tfm.Length - 1)
            return null;
        var platform = tfm[(dash + 1)..].TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.');
        return platform.Length == 0 ? null : platform.ToLowerInvariant();
    }

    /// <summary>
    /// The <c>lib/&lt;tfm&gt;/</c> folder names of an extracted package whose platform is
    /// <paramref name="platform"/> (<c>ios</c>, <c>macos</c>, <c>tvos</c>, <c>maccatalyst</c>), in
    /// ordinal order. One entry per packed copy of that platform; empty when the package carries none,
    /// which every caller treats as a failure of its own.
    /// </summary>
    static IReadOnlyList<string> PackedLibTfms(AbsolutePath extractDir, string platform)
    {
        var libRoot = extractDir / "lib";
        if (!Directory.Exists(libRoot))
            return Array.Empty<string>();
        return Directory.EnumerateDirectories(libRoot)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(tfm => string.Equals(PackTfmPlatform(tfm), platform, StringComparison.OrdinalIgnoreCase))
            .OrderBy(tfm => tfm, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Null when the extracted Swift/mixed binding package has no <c>runtimes/</c> directory, else a
    /// failure line naming what it holds. Binding natives live under <c>native/&lt;tfm&gt;/</c>; a
    /// <c>runtimes/</c> tree is a leftover of the old RID-keyed layout, which consumers of the
    /// per-TFM <c>buildTransitive</c> targets never read.
    /// </summary>
    static string? PackedRuntimesDirViolation(AbsolutePath extractDir)
    {
        var runtimes = extractDir / "runtimes";
        if (!Directory.Exists(runtimes))
            return null;
        var entries = Directory.EnumerateFileSystemEntries(runtimes, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(extractDir, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Take(5)
            .ToList();
        return "binding package ships a runtimes/ directory — Swift/mixed binding natives live under " +
               $"native/<tfm>/ only (first entries: [{string.Join(", ", entries)}])";
    }
}
