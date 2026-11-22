// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;

namespace SwiftBindings.Tasks;

/// <summary>The pack-time and merge-time rules of a package copy, free of MSBuild.</summary>
public static class PackageCopyRules
{
    /// <summary>
    /// SWIFTBIND137: the copy <paramref name="packTfm"/> of an Apple-framework binding must be labelled
    /// with the platform SDK it was generated from: the same major version, and no newer. A label below
    /// the SDK's major (a versionless .NET 10 library defaults to <c>26.0</c> on Xcode 27.2, whose SDK is
    /// 27.2) would let an app on the older SDK select a copy whose API and wrapper need the newer one.
    /// Only Apple-framework bindings are checked: a library binding's surface is the library's, not the
    /// SDK's, and the documented flow packs a versionless library binding on any supported toolchain.
    /// Returns the refusal, or null.
    /// </summary>
    public static string? CheckLabel(string packageId, string packTfm, string targetKind, string platformSdkVersion)
    {
        if (!string.Equals(targetKind, "AppleFramework", StringComparison.Ordinal))
            return null;
        if (!PackageCopyCheck.TryParse(packTfm, out var net, out var platform, out var label) || label is null)
            return $"{packageId}: the pack target framework '{packTfm}' names no platform version, so the copy cannot be labelled. Use a versioned TargetFramework.";
        if (!Version.TryParse(platformSdkVersion.Trim(), out var sdk))
            return $"{packageId} {packTfm}: the {platform} platform SDK version could not be determined (got '{platformSdkVersion.Trim()}'), so the copy's label cannot be checked against the SDK it was generated from. Check that Xcode is selected (xcode-select -p, or DEVELOPER_DIR).";
        if (label.Major == sdk.Major && label <= sdk)
            return null;
        var exact = $"net{net.ToString(2)}-{platform}{sdk.Major}.{Math.Max(sdk.Minor, 0)}";
        return $"{packageId} {packTfm}: this copy is labelled {platform} {label.ToString(2)}, but its API surface and wrapper were generated from the {platform} {sdk.ToString(2)} SDK. "
               + $"An app on an older toolchain whose platform version is at least {label.ToString(2)} would select it and compile against API its SDK lacks. "
               + $"Pack this copy with an explicit TargetFramework that names the SDK it is built from (for example {exact}), or on the toolchain whose SDK matches the label "
               + "(the oldest .NET 10 copy, labelled 26.0, is built with Xcode 26).";
    }

    /// <summary>
    /// SWIFTBIND138: what is wrong with the native references of a copy's consumer targets: each must
    /// point into <c>native/&lt;packTfm&gt;/</c>, and none into <c>runtimes/</c> or another copy.
    /// </summary>
    public static IEnumerable<string> CheckTargetsReferences(string packTfm, string targets)
    {
        if (targets.Contains("../runtimes/", StringComparison.Ordinal))
            yield return "reference natives under runtimes/, the layout of an earlier SwiftBindings.Sdk";
        var index = 0;
        while ((index = targets.IndexOf("../native/", index, StringComparison.Ordinal)) >= 0)
        {
            index += "../native/".Length;
            var end = targets.IndexOf('/', index);
            var folder = end < 0 ? targets[index..] : targets[index..end];
            if (!string.Equals(folder, packTfm, StringComparison.Ordinal))
            {
                yield return $"reference native/{folder}/, another copy's natives";
                yield break;
            }
        }
    }
}
