// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.ApiCompat.cs — the ApiCompat baseline for the SwiftBindings.Runtime pack.
//
// Generated bindings compile against SwiftBindings.Runtime and reference its members by exact
// signature, so a member removed or reshaped between two Runtime versions breaks every
// already-shipped binding at load or call time. NuGet's package validation diffs the pack against
// a baseline package (PackageValidationBaselineVersion) and fails on such a change. The baseline
// is the last shipped version, which moves at every release — so it is resolved per pack from the
// sdk-v* release tags instead of being hard-coded in the csproj. The tag selection itself lives in
// Models/ApiCompatBaselineSelector.cs (BCL-only, link-compiled into the unit tests).

using System;
using System.Linq;
using Nuke.Common;
using Nuke.Common.Tooling;

partial class Build
{
    /// <summary>
    /// The SwiftBindings.Runtime version this pack's public surface is diffed against:
    /// <c>--api-compat-baseline</c> when given, else the highest stable <c>sdk-v*</c> tag whose
    /// version is below the pack version. Throws when nothing qualifies, so a pack never runs
    /// with a silently absent baseline.
    /// </summary>
    string ResolveApiCompatBaseline(string packVersion)
    {
        if (!string.IsNullOrWhiteSpace(ApiCompatBaseline))
            return ApiCompatBaseline!.Trim();

        var tags = ProcessTasks.StartProcess("git", $"tag -l {ApiCompatBaselineSelector.SdkReleaseTagPrefix}*", RootDirectory, logOutput: false, logInvocation: false)
            .AssertZeroExitCode()
            .Output
            .Select(o => o.Text.Trim())
            .Where(t => t.Length > 0);

        return ApiCompatBaselineSelector.Select(tags, packVersion)
            ?? throw new InvalidOperationException(
                $"No stable {ApiCompatBaselineSelector.SdkReleaseTagPrefix}* tag below --version {packVersion} to use as the ApiCompat baseline. " +
                "Fetch tags (`git fetch --tags`), pass --api-compat-baseline <version> explicitly, or " +
                "pass --skip-api-compat for a local pack that ships nowhere.");
    }
}
