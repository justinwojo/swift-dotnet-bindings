// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BindingsGeneration;

/// <summary>
/// The content-addressed key for a cached verification verdict. It combines the five components the
/// key captures — the input ABI facts, the toolchain versions, the generator version, the settled plan
/// (the emitted source the compiler actually sees), and the denylist — into one SHA-256 digest. These
/// are not yet the <em>complete</em> input set of the external MSBuild verify (inherited
/// <c>Directory.Build.props</c>/<c>.targets</c>, <c>Directory.Packages.props</c>, <c>nuget.config</c>,
/// and the resolved runtime package body are unkeyed), which is why the cache is opt-in — see
/// <see cref="VerificationCache.CreateIfEnabled"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every component is an explicit parameter, and the feed is domain-separated and length-prefixed,
/// so flipping any one component produces a different digest (a cache miss) and no component's bytes
/// can bleed into the next. Invalidation is therefore <em>by key construction only</em>: there is no
/// time-based or heuristic expiry. A stale toolchain, a rebuilt generator (its module version id
/// changes), a re-rendered plan, or a changed denylist each yield a fresh key and force a recompute.
/// </para>
/// <para>
/// The settled plan and the denylist are listed as distinct components because they are distinct
/// causes of a different verdict, even though in the verify-recover loop the denylist manifests
/// through the settled plan (a withdrawn member is absent from the emitted source). Feeding both is a
/// belt-and-suspenders key: the plan hash alone is already denylist-sensitive, and the explicit
/// denylist makes that dependence direct rather than incidental.
/// </para>
/// </remarks>
public static class VerificationFingerprint
{
    /// <summary>
    /// Compute the fingerprint. <paramref name="abiFacts"/> and <paramref name="settledPlan"/> are
    /// hashed as raw bytes; the rest are UTF-8 encoded. The denylist is canonicalized (ordinal sort,
    /// newline-joined) so its digest is order-independent.
    /// </summary>
    public static string Compute(
        ReadOnlySpan<byte> abiFacts,
        string toolchainVersions,
        string generatorVersion,
        ReadOnlySpan<byte> settledPlan,
        IEnumerable<string> denylist)
    {
        ArgumentNullException.ThrowIfNull(toolchainVersions);
        ArgumentNullException.ThrowIfNull(generatorVersion);
        ArgumentNullException.ThrowIfNull(denylist);

        var canonicalDenylist = string.Join(
            "\n", denylist.OrderBy(d => d, StringComparer.Ordinal));

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Feed(hasher, "abi", abiFacts);
        Feed(hasher, "toolchain", Encoding.UTF8.GetBytes(toolchainVersions));
        Feed(hasher, "generator", Encoding.UTF8.GetBytes(generatorVersion));
        Feed(hasher, "plan", settledPlan);
        Feed(hasher, "denylist", Encoding.UTF8.GetBytes(canonicalDenylist));
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// Hash the contents of a set of files into one digest, ordinal-sorted by path so the result is
    /// independent of enumeration order. A path prefix separates each file's bytes from the next.
    /// Missing files are skipped (their absence is itself part of the emitted-source shape). This is
    /// the standard way to derive the <c>settledPlan</c> component from an on-disk render.
    /// </summary>
    public static byte[] HashFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths.OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!File.Exists(path))
                continue;
            Feed(hasher, path, File.ReadAllBytes(path));
        }
        return hasher.GetHashAndReset();
    }

    private static void Feed(IncrementalHash hasher, string label, ReadOnlySpan<byte> bytes)
    {
        // Length-prefixed, domain-separated: "<label>:<byteLength>\n" then the raw bytes, so no two
        // distinct (component, value) sequences can hash to the same stream.
        hasher.AppendData(Encoding.UTF8.GetBytes($"{label}:{bytes.Length}\n"));
        hasher.AppendData(bytes);
    }
}
