// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Automatically extracts symbol graph JSON from an xcframework's Swift module
    /// using <c>xcrun swift-symbolgraph-extract</c>. Used for doc comment generation
    /// when no explicit <c>--symbolgraph</c> path is provided.
    /// </summary>
    public static class SymbolGraphExtractor
    {
        /// <summary>
        /// Extracts symbol graph JSON files from the resolved xcframework.
        /// Returns the output directory path on success, or <c>null</c> on failure (graceful — docs are supplementary).
        /// </summary>
        /// <param name="resolution">The resolved xcframework inputs.</param>
        /// <param name="outputDirectory">Base output directory for generated bindings.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="commandRunner">Optional command runner for testing.</param>
        /// <param name="platformInfo">Optional platform info; defaults to iOS.</param>
        public static string? Extract(
            XCFrameworkResolution resolution,
            string outputDirectory,
            ILogger logger,
            ICommandRunner? commandRunner = null,
            PlatformInfo? platformInfo = null)
        {
            commandRunner ??= new SystemCommandRunner();

            try
            {
                // 1. Resolve SDK path
                var pi = platformInfo ?? PlatformInfoFactory.Create(ApplePlatform.iOS);
                var sliceVariant = pi.GetSlice(resolution.IsSimulatorSlice);
                var sdkName = sliceVariant.SdkName;
                var (sdkExit, sdkPath, sdkErr) = commandRunner.Run("xcrun", $"--sdk {sdkName} --show-sdk-path");
                if (sdkExit != 0 || string.IsNullOrWhiteSpace(sdkPath))
                {
                    logger.LogWarning("Failed to resolve SDK path for symbol graph extraction: {Error}", sdkErr);
                    return null;
                }

                // 2. Build target triple (use actual architecture from resolved slice, not default arm64)
                var minOS = SwiftWrapperCompiler.ResolveDeploymentTarget(resolution.DylibPath, logger, commandRunner);
                var effectiveSlice = !string.IsNullOrEmpty(resolution.SelectedArchitecture)
                    && resolution.SelectedArchitecture != sliceVariant.Architecture
                    ? sliceVariant with { Architecture = resolution.SelectedArchitecture }
                    : sliceVariant;
                var targetTriple = effectiveSlice.GetTargetTriple(minOS);

                // 3. Clean output directory to prevent stale file contamination
                var symbolgraphDir = Path.Combine(outputDirectory, "symbolgraph");
                if (Directory.Exists(symbolgraphDir))
                    Directory.Delete(symbolgraphDir, true);
                Directory.CreateDirectory(symbolgraphDir);

                // 4. Run swift-symbolgraph-extract
                var args = $"swift-symbolgraph-extract " +
                           $"-module-name {resolution.ModuleName} " +
                           $"-target {targetTriple} " +
                           $"-sdk \"{sdkPath}\" " +
                           $"-F \"{resolution.FrameworkSearchPath}\" " +
                           $"-output-dir \"{symbolgraphDir}\" " +
                           $"-minimum-access-level public";

                logger.LogInformation("Extracting symbol graph for {Module}...", resolution.ModuleName);

                var (exitCode, stdout, stderr) = commandRunner.Run("xcrun", args, timeoutMs: 60000);
                if (exitCode != 0)
                {
                    logger.LogWarning(
                        "Symbol graph extraction failed (exit {ExitCode}): {Error}. Doc comments will not be generated.",
                        exitCode, stderr.Length > 300 ? stderr.Substring(0, 300) + "..." : stderr);
                    return null;
                }

                // 5. Verify at least one .symbols.json file exists
                var symbolFiles = Directory.GetFiles(symbolgraphDir, "*.symbols.json", SearchOption.AllDirectories);
                if (symbolFiles.Length == 0)
                {
                    logger.LogWarning(
                        "Symbol graph extraction produced no output files. Doc comments will not be generated.");
                    return null;
                }

                logger.LogInformation("Extracted {Count} symbol graph file(s) for {Module}.",
                    symbolFiles.Length, resolution.ModuleName);
                return symbolgraphDir;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Symbol graph extraction failed: {Message}. Doc comments will not be generated.", ex.Message);
                return null;
            }
        }
    }
}
