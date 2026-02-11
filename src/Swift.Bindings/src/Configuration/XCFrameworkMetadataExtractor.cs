// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BindingsGeneration
{
    /// <summary>
    /// Metadata extracted from an xcframework's inner framework Info.plist.
    /// </summary>
    public sealed class XCFrameworkMetadata
    {
        /// <summary>
        /// Raw CFBundleShortVersionString from the framework's Info.plist. Null if not found.
        /// </summary>
        public string? LibraryVersion { get; init; }

        /// <summary>
        /// Effective version for NuGet packaging. "0.0.0" if placeholder or missing.
        /// </summary>
        public required string PackageVersion { get; init; }

        /// <summary>
        /// True if the version is exactly "1.0" or "1.0.0" (Xcode defaults that don't reflect real versioning).
        /// </summary>
        public required bool IsVersionPlaceholder { get; init; }

        /// <summary>
        /// Raw MinimumOSVersion from the framework's Info.plist. Null if not found.
        /// </summary>
        public string? MinimumOSVersion { get; init; }

        /// <summary>
        /// Clamped to max(raw, "15.0") for .NET 10 iOS floor.
        /// </summary>
        public required string EffectiveMinimumOSVersion { get; init; }

        /// <summary>
        /// DTPlatformVersion from the framework's Info.plist. Null if not found.
        /// </summary>
        public string? SdkVersion { get; init; }

        /// <summary>
        /// The Swift module name (e.g., "Nuke").
        /// </summary>
        public required string ModuleName { get; init; }

        /// <summary>
        /// Platform identifiers from the outer xcframework Info.plist.
        /// </summary>
        public required List<string> Platforms { get; init; }
    }

    /// <summary>
    /// Extracts version and platform metadata from xcframework Info.plists.
    /// </summary>
    public static class XCFrameworkMetadataExtractor
    {
        private const string MinOSFloor = "15.0";

        /// <summary>
        /// Extracts metadata from an xcframework's inner framework Info.plist.
        /// </summary>
        /// <param name="dylibPath">Path to the dylib inside the framework bundle.</param>
        /// <param name="xcframeworkPath">Path to the .xcframework directory.</param>
        /// <param name="moduleName">The Swift module name.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="commandRunner">Optional command runner for testing.</param>
        public static XCFrameworkMetadata Extract(
            string dylibPath,
            string xcframeworkPath,
            string moduleName,
            ILogger logger,
            ICommandRunner? commandRunner = null)
        {
            // Read inner framework Info.plist (may be binary plist)
            string? libraryVersion = null;
            string? minimumOSVersion = null;
            string? sdkVersion = null;

            var frameworkDir = Path.GetDirectoryName(dylibPath);
            if (!string.IsNullOrEmpty(frameworkDir))
            {
                var infoPlistPath = Path.Combine(frameworkDir, "Info.plist");
                var plistData = PlistReader.ReadPlistDict(infoPlistPath, commandRunner, logger);
                if (plistData != null)
                {
                    if (plistData.TryGetValue("CFBundleShortVersionString", out var versionObj) && versionObj is string versionStr)
                        libraryVersion = versionStr;

                    if (plistData.TryGetValue("MinimumOSVersion", out var minOSObj) && minOSObj is string minOSStr)
                        minimumOSVersion = minOSStr;

                    if (plistData.TryGetValue("DTPlatformVersion", out var sdkObj) && sdkObj is string sdkStr)
                        sdkVersion = sdkStr;
                }
            }

            // Detect placeholder version
            var isPlaceholder = DetectVersionPlaceholder(libraryVersion);
            var packageVersion = isPlaceholder || string.IsNullOrEmpty(libraryVersion) ? "0.0.0" : libraryVersion;

            // Clamp minimum OS version
            var effectiveMinOS = ClampMinimumOSVersion(minimumOSVersion);

            // Read platforms from outer xcframework Info.plist
            var platforms = ReadPlatforms(xcframeworkPath, logger);

            logger.LogInformation("Extracted metadata: version={Version} (placeholder={IsPlaceholder}), minOS={MinOS} → {EffectiveMinOS}",
                libraryVersion ?? "(none)", isPlaceholder, minimumOSVersion ?? "(none)", effectiveMinOS);

            return new XCFrameworkMetadata
            {
                LibraryVersion = libraryVersion,
                PackageVersion = packageVersion,
                IsVersionPlaceholder = isPlaceholder,
                MinimumOSVersion = minimumOSVersion,
                EffectiveMinimumOSVersion = effectiveMinOS,
                SdkVersion = sdkVersion,
                ModuleName = moduleName,
                Platforms = platforms
            };
        }

        /// <summary>
        /// Detects whether a version string is a placeholder (Xcode default).
        /// "1.0" and "1.0.0" are treated as placeholders.
        /// </summary>
        public static bool DetectVersionPlaceholder(string? version)
        {
            if (string.IsNullOrEmpty(version))
                return true;
            return version == "1.0" || version == "1.0.0";
        }

        /// <summary>
        /// Clamps a raw minimum OS version to at least 15.0 (.NET 10 iOS floor).
        /// </summary>
        public static string ClampMinimumOSVersion(string? rawVersion)
        {
            if (string.IsNullOrEmpty(rawVersion))
                return MinOSFloor;

            if (TryParseVersion(rawVersion, out var rawParsed) && TryParseVersion(MinOSFloor, out var floorParsed))
            {
                return rawParsed >= floorParsed ? rawVersion : MinOSFloor;
            }

            return MinOSFloor;
        }

        /// <summary>
        /// Writes metadata to a JSON file in the output directory.
        /// </summary>
        public static void EmitMetadataJson(XCFrameworkMetadata metadata, string outputDirectory, ILogger logger)
        {
            var metadataPath = Path.Combine(outputDirectory, "binding-metadata.json");

            var jsonObj = new JObject
            {
                ["moduleName"] = metadata.ModuleName,
                ["libraryVersion"] = metadata.LibraryVersion,
                ["packageVersion"] = metadata.PackageVersion,
                ["isVersionPlaceholder"] = metadata.IsVersionPlaceholder,
                ["minimumOSVersion"] = metadata.MinimumOSVersion,
                ["effectiveMinimumOSVersion"] = metadata.EffectiveMinimumOSVersion,
                ["sdkVersion"] = metadata.SdkVersion,
                ["platforms"] = new JArray(metadata.Platforms.ToArray())
            };

            File.WriteAllText(metadataPath, jsonObj.ToString(Formatting.Indented));

            logger.LogInformation("Wrote binding metadata to {Path}", metadataPath);
        }

        private static List<string> ReadPlatforms(string xcframeworkPath, ILogger logger)
        {
            var platforms = new List<string>();

            try
            {
                var outerPlistPath = Path.Combine(xcframeworkPath, "Info.plist");
                if (!File.Exists(outerPlistPath))
                    return platforms;

                // Outer xcframework plist is always XML (generated by xcodebuild)
                var slices = XCFrameworkResolver.ParseInfoPlist(outerPlistPath);
                foreach (var slice in slices)
                {
                    var platform = slice.SupportedPlatform;
                    if (!string.IsNullOrEmpty(slice.SupportedPlatformVariant))
                        platform += $"-{slice.SupportedPlatformVariant}";
                    platforms.Add(platform);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to read platforms from xcframework Info.plist");
            }

            return platforms;
        }

        private static bool TryParseVersion(string versionStr, out Version version)
        {
            // Ensure at least two components for System.Version
            if (!versionStr.Contains('.'))
                versionStr += ".0";
            return Version.TryParse(versionStr, out version!);
        }
    }
}
