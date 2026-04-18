// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Xml.Linq;
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
        /// Extracts metadata from an xcframework by searching for the framework binary
        /// inside the first iOS slice. Used by ObjC pipeline when no dylib path is available.
        /// </summary>
        public static XCFrameworkMetadata ExtractFromFrameworkPath(
            string xcframeworkPath,
            string moduleName,
            ILogger logger,
            ICommandRunner? commandRunner = null)
        {
            // Find the framework binary: {xcfw}/{slice}/{Module}.framework/{Module}
            string? frameworkDir = null;
            var plistPath = Path.Combine(xcframeworkPath, "Info.plist");
            if (File.Exists(plistPath))
            {
                var slices = XCFrameworkResolver.ParseInfoPlist(plistPath);
                var iosSlice = slices.FirstOrDefault(s =>
                    s.SupportedPlatform.Equals("ios", StringComparison.OrdinalIgnoreCase));
                if (iosSlice != null)
                {
                    frameworkDir = Path.Combine(xcframeworkPath, iosSlice.LibraryIdentifier,
                        $"{moduleName}.framework");
                    if (!Directory.Exists(frameworkDir))
                    {
                        // Try LibraryPath from plist
                        frameworkDir = Path.Combine(xcframeworkPath, iosSlice.LibraryIdentifier,
                            iosSlice.LibraryPath);
                    }
                }
            }

            if (frameworkDir != null && Directory.Exists(frameworkDir))
            {
                var binaryPath = Path.Combine(frameworkDir, moduleName);
                if (File.Exists(binaryPath))
                {
                    return Extract(binaryPath, xcframeworkPath, moduleName, logger, commandRunner);
                }

                // Binary may not exist for ObjC-only stubs, try extracting from plist directly
                var innerPlist = Path.Combine(frameworkDir, "Info.plist");
                if (File.Exists(innerPlist))
                {
                    return ExtractFromInnerPlist(innerPlist, xcframeworkPath, moduleName, logger, commandRunner);
                }
            }

            // Fallback defaults
            var platforms = ReadPlatforms(xcframeworkPath, logger);
            return new XCFrameworkMetadata
            {
                LibraryVersion = null,
                PackageVersion = "1.0.0",
                IsVersionPlaceholder = true,
                MinimumOSVersion = null,
                EffectiveMinimumOSVersion = "15.0",
                SdkVersion = null,
                ModuleName = moduleName,
                Platforms = platforms
            };
        }

        private static XCFrameworkMetadata ExtractFromInnerPlist(
            string innerPlistPath,
            string xcframeworkPath,
            string moduleName,
            ILogger logger,
            ICommandRunner? commandRunner)
        {
            string? libraryVersion = null;
            string? minimumOSVersion = null;
            string? sdkVersion = null;

            var plistData = PlistReader.ReadPlistDict(innerPlistPath, commandRunner, logger);
            if (plistData != null)
            {
                if (plistData.TryGetValue("CFBundleShortVersionString", out var versionObj) && versionObj is string versionStr)
                    libraryVersion = versionStr;
                if (plistData.TryGetValue("MinimumOSVersion", out var minOSObj) && minOSObj is string minOSStr)
                    minimumOSVersion = minOSStr;
                if (plistData.TryGetValue("DTPlatformVersion", out var sdkObj) && sdkObj is string sdkStr)
                    sdkVersion = sdkStr;
            }

            var isPlaceholder = DetectVersionPlaceholder(libraryVersion);
            var packageVersion = isPlaceholder || string.IsNullOrEmpty(libraryVersion) ? "0.0.0" : libraryVersion;
            var effectiveMinOS = ClampMinimumOSVersion(minimumOSVersion);
            var platforms = ReadPlatforms(xcframeworkPath, logger);

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
        /// Writes metadata as an MSBuild .props file (XML) for SDK consumption via XmlPeek.
        /// </summary>
        /// <param name="metadata">Extracted xcframework metadata.</param>
        /// <param name="outputDirectory">Directory to write binding-metadata.props.</param>
        /// <param name="hasWrapperXCFramework">Whether a wrapper xcframework was compiled.</param>
        /// <param name="wrapperModuleName">The wrapper module name (e.g., "NukeSwiftBindings").</param>
        /// <param name="wrapperSliceCount">Number of architecture slices in the wrapper xcframework.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="dependencies">Auto-detected framework dependencies (from BinaryDependencyAnalyzer).</param>
        public static void EmitMetadataProps(
            XCFrameworkMetadata metadata,
            string outputDirectory,
            bool hasWrapperXCFramework,
            string wrapperModuleName,
            int wrapperSliceCount,
            ILogger logger,
            IReadOnlyList<FrameworkDependencyInfo>? dependencies = null,
            string? frameworkType = null,
            string? objcProjectName = null,
            PlatformInfo? platformInfo = null,
            bool hasBridgeSwift = false,
            string? bridgeModuleName = null,
            bool needsAppleSupplement = false,
            string? appleSupplementVersion = null,
            string? appleSupplementPrototypeCsprojPath = null)
        {
            var propsPath = Path.Combine(outputDirectory, "binding-metadata.props");

            // Build dependency string: semicolon-delimited ModuleName|PackageId|Version|XCFrameworkPath
            // Delimiter-escape each field (%; |; ;) then XML-escape for the XML layer.
            var depEntries = dependencies?
                .Where(d => !d.IsObjCOnly)
                .Select(d => $"{XmlEscape(DelimiterEscape(d.ModuleName))}|{XmlEscape(DelimiterEscape(d.GetEffectivePackageId(platformInfo)))}|{XmlEscape(DelimiterEscape(d.EffectiveVersion))}|{XmlEscape(DelimiterEscape(d.XCFrameworkPath))}")
                .ToList();
            var depsProperty = depEntries != null && depEntries.Count > 0
                ? $"\n    <_SwiftBindingDependencies>{string.Join(";", depEntries)}</_SwiftBindingDependencies>"
                : "";

            var effectiveFrameworkType = frameworkType ?? "Swift";
            var frameworkTypeProp = $"\n    <_SwiftBindingFrameworkType>{effectiveFrameworkType}</_SwiftBindingFrameworkType>";
            var objcProjProp = !string.IsNullOrEmpty(objcProjectName)
                ? $"\n    <_SwiftBindingObjCProjectName>{objcProjectName}</_SwiftBindingObjCProjectName>"
                : "";

            var bridgeProps = "";
            if (hasBridgeSwift)
            {
                var effectiveBridgeModuleName = bridgeModuleName ?? $"{metadata.ModuleName}Bridge";
                bridgeProps = $"\n    <_SwiftBindingHasBridgeSwift>True</_SwiftBindingHasBridgeSwift>" +
                              $"\n    <_SwiftBindingBridgeModuleName>{effectiveBridgeModuleName}</_SwiftBindingBridgeModuleName>" +
                              $"\n    <_SwiftBindingHasBridgeXCFramework>False</_SwiftBindingHasBridgeXCFramework>" +
                              $"\n    <_SwiftBindingBridgeSliceCount>0</_SwiftBindingBridgeSliceCount>";
            }

            // Apple-supplement handoff to the SDK. <_SwiftBindingNeedsAppleSupplement> drives the
            // PackageReference injection in Sdk.targets (target 4f); the optional prototype csproj
            // path takes precedence and becomes a ProjectReference so iterative supplement changes
            // don't require a NuGet publish. Both properties stay absent on non-Apple consumers so
            // unrelated projects don't pick up phantom references.
            var supplementProps = "";
            if (needsAppleSupplement)
            {
                var effectiveVersion = appleSupplementVersion ?? "26.0.0";
                supplementProps = $"\n    <_SwiftBindingNeedsAppleSupplement>True</_SwiftBindingNeedsAppleSupplement>" +
                                  $"\n    <_SwiftBindingAppleSupplementVersion>{XmlEscape(effectiveVersion)}</_SwiftBindingAppleSupplementVersion>";
                if (!string.IsNullOrEmpty(appleSupplementPrototypeCsprojPath))
                {
                    supplementProps += $"\n    <_SwiftBindingAppleSupplementPrototypeCsproj>{XmlEscape(appleSupplementPrototypeCsprojPath)}</_SwiftBindingAppleSupplementPrototypeCsproj>";
                }
            }

            var content = $"""
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingPackageVersion>{metadata.PackageVersion}</_SwiftBindingPackageVersion>
                    <_SwiftBindingMinimumOSVersion>{metadata.EffectiveMinimumOSVersion}</_SwiftBindingMinimumOSVersion>
                    <_SwiftBindingModuleName>{metadata.ModuleName}</_SwiftBindingModuleName>
                    <_SwiftBindingIsVersionPlaceholder>{metadata.IsVersionPlaceholder}</_SwiftBindingIsVersionPlaceholder>
                    <_SwiftBindingHasWrapperXCFramework>{hasWrapperXCFramework}</_SwiftBindingHasWrapperXCFramework>
                    <_SwiftBindingWrapperModuleName>{wrapperModuleName}</_SwiftBindingWrapperModuleName>
                    <_SwiftBindingWrapperSliceCount>{wrapperSliceCount}</_SwiftBindingWrapperSliceCount>{frameworkTypeProp}{objcProjProp}{bridgeProps}{supplementProps}{depsProperty}
                  </PropertyGroup>
                </Project>
                """;

            File.WriteAllText(propsPath, content);
            logger.LogInformation("Wrote binding metadata props to {Path}", propsPath);
        }

        // Writes a standalone apple-supplement.props next to binding-metadata.props. The direct-
        // mode SDK path writes binding-metadata.props via shell heredoc inside Sdk.targets and
        // has no visibility into the generator's AppleSupplementReferences state; the heredoc
        // <Import>s this file so the supplement signals (_SwiftBindingNeedsAppleSupplement,
        // version, optional prototype csproj) still reach the PackageReference injection target.
        // Emitted unconditionally (even when the supplement isn't needed) so the Import in
        // Sdk.targets has a deterministic shape and doesn't have to probe for file existence.
        public static void EmitAppleSupplementPropsFragment(
            string outputDirectory,
            bool needsAppleSupplement,
            string appleSupplementVersion,
            string? appleSupplementPrototypeCsprojPath,
            ILogger logger)
        {
            var fragmentPath = Path.Combine(outputDirectory, "apple-supplement.props");
            string body;
            if (!needsAppleSupplement)
            {
                body = "    <!-- Apple supplement not referenced by this module. -->";
            }
            else
            {
                var proto = !string.IsNullOrEmpty(appleSupplementPrototypeCsprojPath)
                    ? $"\n    <_SwiftBindingAppleSupplementPrototypeCsproj>{XmlEscape(appleSupplementPrototypeCsprojPath)}</_SwiftBindingAppleSupplementPrototypeCsproj>"
                    : "";
                body = "    <_SwiftBindingNeedsAppleSupplement>True</_SwiftBindingNeedsAppleSupplement>" +
                       $"\n    <_SwiftBindingAppleSupplementVersion>{XmlEscape(appleSupplementVersion)}</_SwiftBindingAppleSupplementVersion>" +
                       proto;
            }
            var content = $"""
                <Project>
                  <PropertyGroup>
                {body}
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(fragmentPath, content);
            logger.LogInformation("Wrote Apple supplement props fragment to {Path}", fragmentPath);
        }

        /// <summary>
        /// Updates an existing binding-metadata.props file in-place to reflect wrapper compilation status.
        /// Used by --compile-wrapper-only mode which runs after initial generation has already emitted the props file.
        /// </summary>
        /// <param name="outputDirectory">Directory containing binding-metadata.props.</param>
        /// <param name="hasWrapper">Whether a wrapper xcframework was successfully compiled.</param>
        /// <param name="wrapperModuleName">The wrapper module name (e.g., "NukeSwiftBindings").</param>
        /// <param name="sliceCount">Number of architecture slices in the wrapper xcframework.</param>
        /// <param name="logger">Logger instance.</param>
        public static void UpdateMetadataPropsWrapperStatus(
            string outputDirectory,
            bool hasWrapper,
            string wrapperModuleName,
            int sliceCount,
            ILogger logger)
        {
            var propsPath = Path.Combine(outputDirectory, "binding-metadata.props");
            if (!File.Exists(propsPath))
            {
                logger.LogWarning("Cannot update wrapper status: binding-metadata.props not found at {Path}", propsPath);
                return;
            }

            var doc = XDocument.Load(propsPath);
            var propertyGroup = doc.Root?.Element("PropertyGroup");
            if (propertyGroup == null)
            {
                logger.LogWarning("Cannot update wrapper status: no PropertyGroup in binding-metadata.props");
                return;
            }

            SetOrAddElement(propertyGroup, "_SwiftBindingHasWrapperXCFramework", hasWrapper.ToString());
            SetOrAddElement(propertyGroup, "_SwiftBindingWrapperSliceCount", sliceCount.ToString());

            doc.Save(propsPath);
            logger.LogInformation("Updated wrapper status in binding-metadata.props: hasWrapper={HasWrapper}, sliceCount={SliceCount}",
                hasWrapper, sliceCount);
        }

        /// <summary>
        /// Updates an existing binding-metadata.props file in-place to reflect bridge compilation status.
        /// Used by --compile-bridge-only mode which runs after wrapper compilation.
        /// </summary>
        public static void UpdateMetadataPropsBridgeStatus(
            string outputDirectory,
            bool hasBridge,
            string bridgeModuleName,
            int sliceCount,
            ILogger logger)
        {
            var propsPath = Path.Combine(outputDirectory, "binding-metadata.props");
            if (!File.Exists(propsPath))
            {
                logger.LogWarning("Cannot update bridge status: binding-metadata.props not found at {Path}", propsPath);
                return;
            }

            var doc = XDocument.Load(propsPath);
            var propertyGroup = doc.Root?.Element("PropertyGroup");
            if (propertyGroup == null)
            {
                logger.LogWarning("Cannot update bridge status: no PropertyGroup in binding-metadata.props");
                return;
            }

            SetOrAddElement(propertyGroup, "_SwiftBindingHasBridgeXCFramework", hasBridge.ToString());
            SetOrAddElement(propertyGroup, "_SwiftBindingBridgeSliceCount", sliceCount.ToString());

            doc.Save(propsPath);
            logger.LogInformation("Updated bridge status in binding-metadata.props: hasBridge={HasBridge}, sliceCount={SliceCount}",
                hasBridge, sliceCount);
        }

        private static void SetOrAddElement(XElement parent, string name, string value)
        {
            var element = parent.Element(name);
            if (element != null)
                element.Value = value;
            else
                parent.Add(new XElement(name, value));
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

        /// <summary>
        /// Percent-encodes the custom delimiter characters (| and ;) so that
        /// field values containing them survive the split-based parsing in Sdk.targets.
        /// Encode order: % first (so existing %xx aren't double-decoded), then | and ;.
        /// </summary>
        private static string DelimiterEscape(string value)
        {
            return value
                .Replace("%", "%25")
                .Replace("|", "%7C")
                .Replace(";", "%3B");
        }

        private static string XmlEscape(string value)
        {
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }
    }
}
