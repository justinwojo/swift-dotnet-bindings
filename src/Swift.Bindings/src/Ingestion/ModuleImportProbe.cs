// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>How a module-import probe resolved.</summary>
public enum ImportProbeStatus
{
    /// <summary>The module imported cleanly against the given search roots + SDK.</summary>
    Resolvable,

    /// <summary>swiftc reported "no such module" for the probed module (a confirmed absence).</summary>
    MissingModule,

    /// <summary>The probe could not be run, or failed for a reason other than a missing module —
    /// the closure question is left unanswered (fail-open: never a hard failure).</summary>
    Inconclusive,
}

/// <summary>The outcome of one probe: its status and, when <see cref="ImportProbeStatus.MissingModule"/>,
/// the exact module name swiftc reported missing (which may be a transitive of the probed module).</summary>
public readonly record struct ImportProbeOutcome(ImportProbeStatus Status, string? MissingModuleName)
{
    public static ImportProbeOutcome Resolvable => new(ImportProbeStatus.Resolvable, null);
    public static ImportProbeOutcome Inconclusive => new(ImportProbeStatus.Inconclusive, null);
    public static ImportProbeOutcome Missing(string name) => new(ImportProbeStatus.MissingModule, name);
}

/// <summary>
/// Adjudicates whether <c>import &lt;module&gt;</c> resolves against a given set of <c>-F</c> roots and the
/// platform SDK — the same question the generated wrapper's compile asks. Injectable so the closure
/// preflight is unit-testable without a Swift toolchain.
/// </summary>
public interface IModuleImportProbe
{
    /// <summary>
    /// Probes whether <paramref name="moduleName"/> is importable given <paramref name="frameworkSearchRoots"/>.
    /// Implementations MUST fail open: any inability to run the probe returns
    /// <see cref="ImportProbeStatus.Inconclusive"/>, never a false <see cref="ImportProbeStatus.MissingModule"/>.
    /// </summary>
    ImportProbeOutcome Probe(string moduleName, IReadOnlyList<string> frameworkSearchRoots);
}

/// <summary>
/// The production probe: a short-lived <c>swift-frontend -typecheck</c> of a one-line <c>import</c> file,
/// against the SDK + target triple the wrapper compile uses, with the supplied <c>-F</c> roots. Cloned from
/// the <see cref="ModuleNameShadowProbe"/> skeleton (same SDK/triple resolution, same fail-open discipline).
/// </summary>
public sealed class SwiftFrontendImportProbe : IModuleImportProbe
{
    // swiftc's missing-module diagnostic. Same shape DiagnosticAttributor keys on, so the two agree.
    private static readonly Regex NoSuchModule = new(
        @"no such module '([^']+)'", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly PlatformInfo _platformInfo;
    private readonly ICommandRunner _commandRunner;
    private readonly ILogger _logger;

    public SwiftFrontendImportProbe(PlatformInfo platformInfo, ICommandRunner commandRunner, ILogger logger)
    {
        _platformInfo = platformInfo ?? throw new ArgumentNullException(nameof(platformInfo));
        _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ImportProbeOutcome Probe(string moduleName, IReadOnlyList<string> frameworkSearchRoots)
    {
        if (!IsSwiftIdentifier(moduleName))
            return ImportProbeOutcome.Inconclusive;

        string? probeDir = null;
        try
        {
            var slice = _platformInfo.GetSlice(isSimulator: true);
            var sdkPath = SwiftWrapperCompiler.ResolveSdkPath(slice.SdkName, _commandRunner);
            if (string.IsNullOrEmpty(sdkPath))
                return ImportProbeOutcome.Inconclusive;

            probeDir = Path.Combine(Path.GetTempPath(), "sbw-import-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(probeDir);
            var probeFile = Path.Combine(probeDir, "ImportProbe.swift");
            File.WriteAllText(probeFile, $"import {moduleName}\n");

            var triple = slice.GetTargetTriple(_platformInfo.DefaultMinimumOS);
            var args = $"swift-frontend -typecheck -sdk \"{sdkPath}\" -target {triple}";
            foreach (var root in frameworkSearchRoots)
            {
                if (!string.IsNullOrEmpty(root))
                    args += $" -F \"{root}\"";
            }
            args += $" \"{probeFile}\"";

            var (exitCode, stdout, stderr) = _commandRunner.Run("xcrun", args);
            if (exitCode == 0)
                return ImportProbeOutcome.Resolvable;

            // Only a genuine "no such module" is a confirmed absence. Any other non-zero exit
            // (broken interface, toolchain fault) is inconclusive — never a hard failure.
            var match = NoSuchModule.Match((stderr ?? "") + "\n" + (stdout ?? ""));
            return match.Success
                ? ImportProbeOutcome.Missing(match.Groups[1].Value)
                : ImportProbeOutcome.Inconclusive;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not import-probe module '{Module}': {Message}", moduleName, ex.Message);
            return ImportProbeOutcome.Inconclusive;
        }
        finally
        {
            if (probeDir != null)
            {
                try { Directory.Delete(probeDir, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static bool IsSwiftIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        if (!char.IsLetter(name[0]) && name[0] != '_')
            return false;
        foreach (var c in name)
            if (!char.IsLetterOrDigit(c) && c != '_')
                return false;
        return true;
    }
}
