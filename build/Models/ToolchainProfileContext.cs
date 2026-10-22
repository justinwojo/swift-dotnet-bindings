// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Security.Cryptography;
using System.Text;

/// <summary>The launcher's immutable identity for an isolated toolchain run.</summary>
public sealed record ToolchainProfileContext(string? Profile, string ToolchainFingerprint, string SourceFingerprint)
{
    public bool IsIsolated => !string.IsNullOrWhiteSpace(Profile);
    public bool CanWriteBaselines => !IsIsolated;
    public string EvidenceFingerprint => Hash($"{Profile}\n{ToolchainFingerprint}\n{SourceFingerprint}");

    public static ToolchainProfileContext Create(string? profile, string? toolchain, string? source)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            if (!string.IsNullOrWhiteSpace(toolchain) || !string.IsNullOrWhiteSpace(source))
                throw new InvalidOperationException("Toolchain/source fingerprints require SWIFT_BINDINGS_TOOLCHAIN_PROFILE.");
            return new(null, "", "");
        }
        if (string.IsNullOrWhiteSpace(toolchain) || string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException(
                "An isolated toolchain profile requires SWIFT_BINDINGS_TOOLCHAIN_FINGERPRINT and " +
                "SWIFT_BINDINGS_SOURCE_FINGERPRINT. Run through the repository profile launcher.");
        return new(profile, toolchain, source);
    }

    public void AssertBaselineWriteAllowed(string operation)
    {
        if (!CanWriteBaselines)
            throw new InvalidOperationException($"{operation} cannot modify canonical baselines under toolchain profile '{Profile}'.");
    }

    public void AssertEvidenceCompatible(string? recorded)
    {
        if (IsIsolated && recorded != EvidenceFingerprint)
            throw new InvalidOperationException(
                $"Cached bindings do not match toolchain profile '{Profile}' and its source/toolchain fingerprints. " +
                "Rerun without --skip-regen or --skip-build.");
    }

    public static bool IsBaselineMutationTarget(string target)
    {
        var normalized = target.Replace("-", "").Replace("_", "");
        return normalized.Equals("PromoteValidationBaseline", StringComparison.OrdinalIgnoreCase)
            || (normalized.StartsWith("Seed", StringComparison.OrdinalIgnoreCase)
                && normalized.EndsWith("Baseline", StringComparison.OrdinalIgnoreCase));
    }

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
