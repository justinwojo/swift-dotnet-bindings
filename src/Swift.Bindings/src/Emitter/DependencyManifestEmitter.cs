// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BindingsGeneration
{
    /// <summary>
    /// Emits dependency-manifest.json describing all detected and resolved dependencies.
    /// Always emitted in xcframework mode for debugging and tooling.
    /// </summary>
    public static class DependencyManifestEmitter
    {
        /// <summary>
        /// Emits the dependency manifest to the output directory.
        /// </summary>
        /// <param name="outputDirectory">Directory to write the manifest.</param>
        /// <param name="primaryModuleName">The primary module name.</param>
        /// <param name="primaryXCFrameworkPath">Path to the primary xcframework.</param>
        /// <param name="primaryDylibPath">Path to the primary dylib (for graph building).</param>
        /// <param name="analysisResult">Auto-detection result (null if --no-auto-detect).</param>
        /// <param name="effectiveDeps">Final merged dependency list (auto + manual).</param>
        /// <param name="manualDependencyPaths">Original --framework-dependency paths (for override tracking).</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="commandRunner">Optional command runner for testing.</param>
        public static void Emit(
            string outputDirectory,
            string primaryModuleName,
            string primaryXCFrameworkPath,
            string primaryDylibPath,
            DependencyAnalysisResult? analysisResult,
            List<FrameworkDependencyInfo>? effectiveDeps,
            string[]? manualDependencyPaths,
            ILogger logger,
            ICommandRunner? commandRunner = null)
        {
            var manifestPath = Path.Combine(outputDirectory, "dependency-manifest.json");

            var manifest = new JObject
            {
                ["primary"] = new JObject
                {
                    ["moduleName"] = primaryModuleName,
                    ["xcframeworkPath"] = primaryXCFrameworkPath
                }
            };

            // Build effective dependencies array
            var effectiveArray = new JArray();
            if (effectiveDeps != null)
            {
                // Determine which modules were auto-detected
                var autoDetectedModules = new HashSet<string>(StringComparer.Ordinal);
                if (analysisResult != null)
                {
                    foreach (var dep in analysisResult.AllDetected)
                        autoDetectedModules.Add(dep.FrameworkName);
                }

                // Determine which modules are manual (from --framework-dependency paths)
                var manualModules = new HashSet<string>(StringComparer.Ordinal);
                if (manualDependencyPaths != null)
                {
                    foreach (var dep in effectiveDeps)
                    {
                        foreach (var manualPath in manualDependencyPaths)
                        {
                            if (string.Equals(
                                Path.GetFullPath(dep.XCFrameworkPath),
                                Path.GetFullPath(manualPath),
                                StringComparison.OrdinalIgnoreCase))
                            {
                                manualModules.Add(dep.ModuleName);
                                break;
                            }
                        }
                    }
                }

                foreach (var dep in effectiveDeps)
                {
                    var source = manualModules.Contains(dep.ModuleName) ? "manual" : "binary-linkage";
                    effectiveArray.Add(new JObject
                    {
                        ["moduleName"] = dep.ModuleName,
                        ["xcframeworkPath"] = dep.XCFrameworkPath,
                        ["source"] = source,
                        ["packageId"] = dep.EffectivePackageId,
                        ["version"] = dep.EffectiveVersion,
                        ["isObjCOnly"] = dep.IsObjCOnly
                    });
                }
            }
            manifest["effectiveDependencies"] = effectiveArray;

            // Build unresolved array
            var unresolvedArray = new JArray();
            if (analysisResult != null)
            {
                foreach (var dep in analysisResult.UnresolvedDependencies)
                {
                    unresolvedArray.Add(new JObject
                    {
                        ["frameworkName"] = dep.FrameworkName,
                        ["installName"] = dep.InstallName
                    });
                }
            }
            manifest["detectedButUnresolved"] = unresolvedArray;

            // Build overridden array
            // Maps both module name and xcframework-derived framework name to the override path,
            // so detection works even when framework binary name != Swift module name.
            var overriddenArray = new JArray();
            if (analysisResult != null && manualDependencyPaths != null && effectiveDeps != null)
            {
                var overrideLookup = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var dep in effectiveDeps)
                {
                    foreach (var manualPath in manualDependencyPaths)
                    {
                        if (string.Equals(
                            Path.GetFullPath(dep.XCFrameworkPath),
                            Path.GetFullPath(manualPath),
                            StringComparison.OrdinalIgnoreCase))
                        {
                            // Register by module name
                            overrideLookup[dep.ModuleName] = dep.XCFrameworkPath;
                            // Also register by xcframework-derived framework name
                            var fwName = Path.GetFileNameWithoutExtension(dep.XCFrameworkPath);
                            if (!string.IsNullOrEmpty(fwName))
                                overrideLookup[fwName] = dep.XCFrameworkPath;
                            break;
                        }
                    }
                }

                foreach (var detected in analysisResult.AllDetected)
                {
                    if (overrideLookup.TryGetValue(detected.FrameworkName, out var overridePath))
                    {
                        overriddenArray.Add(new JObject
                        {
                            ["frameworkName"] = detected.FrameworkName,
                            ["installName"] = detected.InstallName,
                            ["overriddenByPath"] = overridePath
                        });
                    }
                }
            }
            manifest["detectedButOverridden"] = overriddenArray;

            // Build dependency graph and topological sort
            var graphWarnings = new List<string>();
            List<string> buildOrder;

            try
            {
                var (graph, warnings) = BinaryDependencyAnalyzer.BuildDependencyGraph(
                    primaryModuleName, primaryDylibPath, effectiveDeps, commandRunner,
                    primaryXCFrameworkPath);
                graphWarnings.AddRange(warnings);

                try
                {
                    buildOrder = TopologicalSort.Sort(graph);
                }
                catch (InvalidOperationException)
                {
                    // Cycle detected — fall back to alphabetical
                    graphWarnings.Add("Dependency cycle detected — build order is alphabetical fallback");
                    var allModules = new SortedSet<string>(StringComparer.Ordinal) { primaryModuleName };
                    if (effectiveDeps != null)
                    {
                        foreach (var dep in effectiveDeps)
                            allModules.Add(dep.ModuleName);
                    }
                    buildOrder = allModules.ToList();
                }
            }
            catch (Exception ex)
            {
                // Graph building failed entirely — alphabetical fallback
                graphWarnings.Add($"Could not build dependency graph: {ex.Message}");
                var allModules = new SortedSet<string>(StringComparer.Ordinal) { primaryModuleName };
                if (effectiveDeps != null)
                {
                    foreach (var dep in effectiveDeps)
                        allModules.Add(dep.ModuleName);
                }
                buildOrder = allModules.ToList();
            }

            manifest["buildOrder"] = new JArray(buildOrder.ToArray());
            manifest["graphWarnings"] = new JArray(graphWarnings.ToArray());

            File.WriteAllText(manifestPath, manifest.ToString(Formatting.Indented));

            logger.LogInformation("Wrote dependency manifest to {Path}", manifestPath);
        }
    }
}
