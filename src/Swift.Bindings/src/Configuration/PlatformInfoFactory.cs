// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Single source of truth for all per-platform constants.
    /// Creates PlatformInfo instances with correct SliceVariants for each Apple platform.
    /// </summary>
    public static class PlatformInfoFactory
    {
        /// <summary>
        /// Create PlatformInfo for a given platform. Optional <paramref name="platformVersion"/>
        /// overrides <see cref="PlatformInfo.DefaultPlatformVersion"/> for both
        /// <see cref="PlatformInfo.PackTfm"/> and the generator-emitted
        /// <c>&lt;TargetFramework&gt;</c> element. Pass null (or omit) to keep the
        /// build-machine default.
        /// </summary>
        public static PlatformInfo Create(ApplePlatform platform, string? platformVersion = null)
        {
            var pi = platform switch
            {
                ApplePlatform.iOS => CreateiOS(),
                ApplePlatform.macOS => CreatemacOS(),
                ApplePlatform.tvOS => CreatetvOS(),
                ApplePlatform.MacCatalyst => CreateMacCatalyst(),
                _ => throw new ArgumentOutOfRangeException(nameof(platform)),
            };
            // IsNullOrWhiteSpace (not IsNullOrEmpty): a `--platform-version "   "` from a
            // poorly-quoted shell invocation would otherwise propagate as the literal
            // whitespace string into <TargetFramework>net10.0-ios   </TargetFramework>,
            // producing a project that fails restore with no clear pointer back to the
            // CLI argument. Fall back to DefaultPlatformVersion in that case.
            return string.IsNullOrWhiteSpace(platformVersion)
                ? pi
                : pi with { PlatformVersion = platformVersion };
        }

        /// <summary>
        /// Validates the requested framework and infers its TPV when no explicit override is supplied.
        /// A versionless TFM retains the platform default. Explicit conflicting TPVs are rejected.
        /// </summary>
        public static PlatformInfo Create(ApplePlatform platform, string? platformVersion, string? targetFramework)
        {
            var pi = Create(platform, platformVersion);
            if (string.IsNullOrWhiteSpace(targetFramework))
                return pi;

            // Parse the complete supported grammar, including the platform. Prefix matching
            // would also accept malformed targets such as net10.00-ios or iosSimulator.
            var match = System.Text.RegularExpressions.Regex.Match(targetFramework,
                @"\Anet(?<major>10|11)\.0-(?<platform>ios|macos|tvos|maccatalyst)(?<tpv>[0-9]+\.[0-9]+)?\z",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success || ParsePlatform(match.Groups["platform"].Value) != platform)
                throw new ArgumentException($"Unsupported target framework '{targetFramework}'. " +
                    "This generator supports net10.0 and net11.0 Apple targets; supply a matching --platform and --platform-version.");

            var major = match.Groups["major"].Value;
            var tpv = match.Groups["tpv"].Value;
            if (tpv.Length != 0 && !string.IsNullOrWhiteSpace(platformVersion) && tpv != platformVersion)
                throw new ArgumentException($"Target framework '{targetFramework}' conflicts with --platform-version '{platformVersion}'.");
            return pi with
            {
                Tfm = $"net{major}.0-{match.Groups["platform"].Value.ToLowerInvariant()}",
                PlatformVersion = tpv.Length != 0 ? tpv : !string.IsNullOrWhiteSpace(platformVersion)
                    ? platformVersion : major == "11" ? "27.0" : PlatformInfo.DefaultPlatformVersion,
                DefaultMinimumOS = major == "11" ? platform switch
                {
                    ApplePlatform.macOS => "14.0",
                    ApplePlatform.MacCatalyst => "17.0",
                    _ => pi.DefaultMinimumOS,
                } : pi.DefaultMinimumOS,
            };
        }

        /// <summary>
        /// Parse platform from CLI string. Accepts: "ios", "macos", "tvos", "maccatalyst".
        /// Returns null for unrecognized input.
        /// </summary>
        public static ApplePlatform? ParsePlatform(string? value) => value?.ToLowerInvariant() switch
        {
            "ios" or null => ApplePlatform.iOS,
            "macos" => ApplePlatform.macOS,
            "tvos" => ApplePlatform.tvOS,
            "maccatalyst" or "mac-catalyst" => ApplePlatform.MacCatalyst,
            _ => null,
        };

        /// <summary>
        /// Detect platform from xcframework plist SupportedPlatform/SupportedPlatformVariant.
        /// </summary>
        public static ApplePlatform DetectFromPlistPlatform(
            string supportedPlatform, string? supportedPlatformVariant)
        {
            if (string.Equals(supportedPlatformVariant, "maccatalyst", StringComparison.OrdinalIgnoreCase))
                return ApplePlatform.MacCatalyst;
            return supportedPlatform.ToLowerInvariant() switch
            {
                "ios" => ApplePlatform.iOS,
                "macos" => ApplePlatform.macOS,
                "tvos" => ApplePlatform.tvOS,
                _ => ApplePlatform.iOS,
            };
        }

        private static PlatformInfo CreateiOS()
        {
            var simulator = new SliceVariant
            {
                Platform = ApplePlatform.iOS,
                IsSimulator = true,
                SdkName = "iphonesimulator",
                SliceId = "ios-arm64-simulator",
                PlistPlatformName = "iPhoneSimulator",
                XCFrameworkPlatformString = "ios",
                XCFrameworkPlatformVariant = "simulator",
            };
            var device = new SliceVariant
            {
                Platform = ApplePlatform.iOS,
                IsSimulator = false,
                SdkName = "iphoneos",
                SliceId = "ios-arm64",
                PlistPlatformName = "iPhoneOS",
                XCFrameworkPlatformString = "ios",
                XCFrameworkPlatformVariant = null,
            };
            return new PlatformInfo
            {
                Platform = ApplePlatform.iOS,
                Tfm = "net10.0-ios",
                // PackTfm is derived: Tfm + PlatformInfo.DefaultPlatformVersion.
                NuGetRid = "ios-arm64",
                SwiftPackageIdSuffix = ".Swift.iOS",
                ObjCPackageIdSuffix = ".ObjC.iOS",
                PlistPlatformString = "ios",
                DefaultMinimumOS = "15.0",
                HasSimulatorVariant = true,
                SimulatorSlice = simulator,
                DeviceSlice = device,
            };
        }

        private static PlatformInfo CreatemacOS()
        {
            var device = new SliceVariant
            {
                Platform = ApplePlatform.macOS,
                IsSimulator = false,
                SdkName = "macosx",
                SliceId = "macos-arm64",
                PlistPlatformName = "MacOSX",
                XCFrameworkPlatformString = "macos",
                XCFrameworkPlatformVariant = null,
            };
            return new PlatformInfo
            {
                Platform = ApplePlatform.macOS,
                Tfm = "net10.0-macos",
                // PackTfm is derived: Tfm + PlatformInfo.DefaultPlatformVersion.
                NuGetRid = "osx-arm64",
                SwiftPackageIdSuffix = ".Swift.macOS",
                ObjCPackageIdSuffix = ".ObjC.macOS",
                PlistPlatformString = "macos",
                DefaultMinimumOS = "12.0",
                HasSimulatorVariant = false,
                SimulatorSlice = null,
                DeviceSlice = device,
            };
        }

        private static PlatformInfo CreatetvOS()
        {
            var simulator = new SliceVariant
            {
                Platform = ApplePlatform.tvOS,
                IsSimulator = true,
                SdkName = "appletvsimulator",
                SliceId = "tvos-arm64-simulator",
                PlistPlatformName = "AppleTVSimulator",
                XCFrameworkPlatformString = "tvos",
                XCFrameworkPlatformVariant = "simulator",
            };
            var device = new SliceVariant
            {
                Platform = ApplePlatform.tvOS,
                IsSimulator = false,
                SdkName = "appletvos",
                SliceId = "tvos-arm64",
                PlistPlatformName = "AppleTVOS",
                XCFrameworkPlatformString = "tvos",
                XCFrameworkPlatformVariant = null,
            };
            return new PlatformInfo
            {
                Platform = ApplePlatform.tvOS,
                Tfm = "net10.0-tvos",
                // PackTfm is derived: Tfm + PlatformInfo.DefaultPlatformVersion.
                NuGetRid = "tvos-arm64",
                SwiftPackageIdSuffix = ".Swift.tvOS",
                ObjCPackageIdSuffix = ".ObjC.tvOS",
                PlistPlatformString = "tvos",
                DefaultMinimumOS = "15.0",
                HasSimulatorVariant = true,
                SimulatorSlice = simulator,
                DeviceSlice = device,
            };
        }

        private static PlatformInfo CreateMacCatalyst()
        {
            var device = new SliceVariant
            {
                Platform = ApplePlatform.MacCatalyst,
                IsSimulator = false,
                SdkName = "macosx",
                SliceId = "ios-arm64-maccatalyst",
                PlistPlatformName = "MacOSX",
                XCFrameworkPlatformString = "ios",
                XCFrameworkPlatformVariant = "maccatalyst",
            };
            return new PlatformInfo
            {
                Platform = ApplePlatform.MacCatalyst,
                Tfm = "net10.0-maccatalyst",
                // PackTfm is derived: Tfm + PlatformInfo.DefaultPlatformVersion.
                NuGetRid = "maccatalyst-arm64",
                SwiftPackageIdSuffix = ".Swift.MacCatalyst",
                ObjCPackageIdSuffix = ".ObjC.MacCatalyst",
                PlistPlatformString = "ios",
                DefaultMinimumOS = "15.0",
                HasSimulatorVariant = false,
                SimulatorSlice = null,
                DeviceSlice = device,
            };
        }
    }
}
