// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Chooses the shipped <c>SwiftBindings.Runtime</c> version a pack's public surface is ApiCompat-diffed
/// against, from the repository's <c>sdk-v*</c> release tags.
/// </summary>
/// <remarks>
/// Generated bindings compile against the Runtime and reference its members by exact signature, so a
/// member removed or reshaped between two Runtime versions breaks every already-shipped binding at
/// load or call time. The contract enforced at pack time is additivity over the last <em>stable</em>
/// package consumers actually compiled against, so prerelease tags are never baselines and a pack
/// that re-uses an already-tagged version (a local rebuild of a shipped version) is diffed against the
/// version before it. Kept dependency-free (no Nuke types) so it can be link-compiled into the
/// unit-test project and asserted directly.
/// </remarks>
public static class ApiCompatBaselineSelector
{
    /// <summary>Prefix of the SDK-lane release tags (<c>sdk-v0.19.4</c>).</summary>
    public const string SdkReleaseTagPrefix = "sdk-v";

    /// <summary>
    /// File name of the incremental-build stamp the SDK's <c>RunPackageValidation</c> target writes
    /// under the project's intermediate directory. The target is skipped as up-to-date whenever the
    /// packed inputs are older than this stamp — and the baseline version is not one of its inputs —
    /// so a pack must remove it to be sure the diff actually runs against the baseline it resolved.
    /// </summary>
    public const string ValidatePackageSemaphoreFileName = "Microsoft.NET.ApiCompat.ValidatePackage.semaphore";

    /// <summary>
    /// Picks the highest stable release tag strictly below <paramref name="packVersion"/>'s release
    /// core, or <c>null</c> when no tag qualifies. Tags without the SDK prefix, prerelease tags, and
    /// tags that are not <c>Major.Minor.Patch</c> are ignored.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="packVersion"/> is not a <c>Major.Minor.Patch[-prerelease]</c> version.
    /// </exception>
    public static string? Select(IEnumerable<string> sdkTags, string packVersion)
    {
        var target = ParseReleaseCore(packVersion)
            ?? throw new InvalidOperationException(
                $"--version '{packVersion}' is not a Major.Minor.Patch[-prerelease] version; pass --api-compat-baseline explicitly.");

        return sdkTags
            .Select(t => t.Trim())
            .Where(t => t.StartsWith(SdkReleaseTagPrefix, StringComparison.Ordinal))
            .Select(t => t.Substring(SdkReleaseTagPrefix.Length))
            .Where(v => !v.Contains('-') && !v.Contains('+'))
            .Select(v => (Text: v, Core: ParseReleaseCore(v)))
            .Where(x => x.Core is not null && x.Core < target)
            .OrderByDescending(x => x.Core)
            .Select(x => x.Text)
            .FirstOrDefault();
    }

    /// <summary>
    /// <c>Major.Minor.Patch</c> of a version string with any prerelease or build suffix dropped, or
    /// <c>null</c> when the core is not exactly three numeric components.
    /// </summary>
    public static Version? ParseReleaseCore(string version)
    {
        var core = version.Split('-', '+')[0];
        if (core.Count(c => c == '.') != 2)
            return null;
        return Version.TryParse(core, out var parsed) ? parsed : null;
    }
}
