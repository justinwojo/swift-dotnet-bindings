// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Finding 58 (toolchain identity): the single declaration of the host-toolchain envelope this
    /// generator is calibrated and tested against. Before this existed, the only support matrix was a
    /// prose line in README and a scattering of implicit assumptions (an Xcode-26 ABI shape, a .NET 10
    /// SDK, the digester's <c>json_format_version</c>, a pinned swift-syntax grammar) that nothing read
    /// back at runtime — a newer/older Xcode or a drifted digester would simply mis-bind, silently.
    /// </summary>
    /// <remarks>
    /// <para>These constants are the one source of truth. <c>build/supported-toolchain.json</c> is the
    /// human-facing mirror and <c>SupportedToolchainMatrixTests</c> asserts the two can never drift; the
    /// README "Requires" line (<c>README.md</c>) is pinned to <see cref="MinXcodeMajor"/> by the same
    /// test. <see cref="AssertSupported"/> feeds the live host version into the
    /// <see cref="InputResolutionReport"/> degradation channel so an out-of-envelope toolchain is warned
    /// loudly and fails closed under <c>--strict-inputs</c> — the same fail-closed mechanism Finding 50
    /// uses for slice/arch/dependency degradations.</para>
    /// </remarks>
    internal static class SupportedToolchain
    {
        /// <summary>
        /// Minimum supported Xcode major version (the README floor: "Xcode 26 or later"). An older
        /// active Xcode is recorded as a <see cref="InputResolutionCategory.Toolchain"/> degradation.
        /// </summary>
        internal const int MinXcodeMajor = 26;

        /// <summary>
        /// Highest Xcode major this generator has actually been tested against. A <em>newer</em> Xcode
        /// is not blocked — it must still run — but it is surfaced as a degradation (amendment E: the
        /// matrix needs a tested ceiling, not just a floor) so "we have not validated this toolchain"
        /// is observable rather than assumed-fine.
        /// </summary>
        internal const int MaxXcodeMajor = 26;

        /// <summary>
        /// Minimum supported .NET SDK band (the README floor: ".NET 10 SDK"). This is <em>declarative</em>
        /// envelope, parity-pinned to <c>build/supported-toolchain.json</c> and the README by
        /// <c>SupportedToolchainMatrixTests</c> — it is intentionally NOT a startup probe. The generator
        /// itself targets <c>net10.0</c>, so a host below this floor cannot even load it; and the band a
        /// <em>consumer</em> builds bindings with is downstream of this process and not observable here.
        /// Only the Xcode version (which a net10.0 generator can run against, and which genuinely drifts
        /// independently) is probed at startup by <see cref="AssertSupported"/>.
        /// </summary>
        internal const string MinDotnetSdk = "10.0";

        /// <summary>
        /// The swift-api-digester <c>json_format_version</c> the ABI parser is calibrated against. The
        /// literal lives here so the toolchain envelope has exactly one owner;
        /// <see cref="SwiftABIParser.ExpectedAbiFormatVersion"/> forwards to it (Finding 58: "single
        /// owner of 8"). The runtime gate is <c>SwiftABIParser.GateAbiFormatVersion</c> / SWIFTBIND033.
        /// </summary>
        internal const int ExpectedAbiFormatVersion = 8;

        /// <summary>
        /// The swift-syntax tag the interface-facts producer is pinned to (Swift 6.1 grammar). Pinned
        /// in <c>tools/SwiftInterfaceParser/Package.swift</c> + <c>Package.resolved</c>; the matrix test
        /// asserts this constant equals that manifest pin so a deliberate bump moves both together.
        /// </summary>
        internal const string PinnedSwiftSyntaxVersion = "601.0.1";

        /// <summary>
        /// The resolved git revision of <see cref="PinnedSwiftSyntaxVersion"/> (from
        /// <c>tools/SwiftInterfaceParser/Package.resolved</c>). The facts JSON carries no runtime
        /// swift-syntax stamp (only its own <c>schemaVersion</c> handshake), so the strongest available
        /// assertion is the build-manifest parity check in the matrix test.
        /// </summary>
        internal const string PinnedSwiftSyntaxRevision = "f99ae8aa18f0cf0d53481901f88a0991dc3bd4a2";

        /// <summary>
        /// Reads the active Xcode major version (<c>xcodebuild -version</c>) and classifies it against
        /// the supported envelope, recording the outcome on the ambient
        /// <see cref="InputResolutionReport"/>:
        /// <list type="bullet">
        /// <item>in range [<see cref="MinXcodeMajor"/>, <see cref="MaxXcodeMajor"/>] → <c>RecordInfo</c>;</item>
        /// <item>below the floor or above the tested ceiling → SWIFTBIND055 warning +
        /// <c>RecordDegradation(Toolchain)</c>, which <c>--strict-inputs</c> escalates to a hard failure;</item>
        /// <item>unobservable (no queryable Xcode, or an unparseable banner) → SWIFTBIND055 warning
        /// <em>only</em>, never a degradation — "could not verify" is not "verified out of range", and
        /// we must not fail-close <c>--strict-inputs</c> on a toolchain we simply could not read.</item>
        /// </list>
        /// </summary>
        internal static void AssertSupported(ICommandRunner commandRunner, ILogger logger)
        {
            int? xcodeMajor;
            try
            {
                var (exitCode, stdOut, _) = commandRunner.Run("xcodebuild", "-version");
                xcodeMajor = exitCode == 0 ? ParseXcodeMajor(stdOut) : null;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "xcodebuild -version probe failed");
                xcodeMajor = null;
            }

            if (xcodeMajor is null)
            {
                // Unobservable ≠ unsupported. Warn so the gap is visible, but record NO degradation —
                // a host without a queryable Xcode (or an unrecognized version banner) must not turn a
                // --strict-inputs run red.
                logger.LogWarning(
                    "SWIFTBIND055: could not determine the active Xcode version (`xcodebuild -version`); "
                    + "the supported toolchain envelope (Xcode {Min}–{Max}) could not be verified for this "
                    + "generation.",
                    MinXcodeMajor, MaxXcodeMajor);
                return;
            }

            if (xcodeMajor.Value < MinXcodeMajor || xcodeMajor.Value > MaxXcodeMajor)
            {
                var relation = xcodeMajor.Value < MinXcodeMajor
                    ? "older than the minimum supported"
                    : "newer than the max-tested";
                logger.LogWarning(
                    "SWIFTBIND055: active Xcode major {Actual} is {Relation} Xcode {Min}–{Max}; generated "
                    + "bindings may not match this generator's calibrated ABI. Under --strict-inputs this "
                    + "fails the generation.",
                    xcodeMajor.Value, relation, MinXcodeMajor, MaxXcodeMajor);
                InputResolutionReport.RecordDegradation(
                    InputResolutionCategory.Toolchain,
                    $"active Xcode major {xcodeMajor.Value} is {relation} supported range {MinXcodeMajor}–{MaxXcodeMajor}");
            }
            else
            {
                InputResolutionReport.RecordInfo(
                    InputResolutionCategory.Toolchain,
                    $"active Xcode major {xcodeMajor.Value} within supported range {MinXcodeMajor}–{MaxXcodeMajor}");
            }
        }

        /// <summary>
        /// Extracts the integer major version from <c>xcodebuild -version</c> output. The first line is
        /// <c>"Xcode 26.3"</c> (or <c>"Xcode 26"</c>); returns the integer before the first dot, or
        /// <c>null</c> when the banner is empty/unrecognized.
        /// </summary>
        internal static int? ParseXcodeMajor(string? xcodebuildVersionOutput)
        {
            if (string.IsNullOrWhiteSpace(xcodebuildVersionOutput))
                return null;

            var firstLine = xcodebuildVersionOutput.Split('\n', '\r')[0].Trim();
            var match = Regex.Match(firstLine, @"Xcode\s+(\d+)");
            return match.Success && int.TryParse(match.Groups[1].Value, out var major) ? major : null;
        }
    }
}
