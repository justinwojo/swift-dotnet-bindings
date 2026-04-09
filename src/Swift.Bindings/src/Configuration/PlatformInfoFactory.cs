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
        /// <summary>Create PlatformInfo for a given platform.</summary>
        public static PlatformInfo Create(ApplePlatform platform) => platform switch
        {
            ApplePlatform.iOS => CreateiOS(),
            ApplePlatform.macOS => CreatemacOS(),
            ApplePlatform.tvOS => CreatetvOS(),
            ApplePlatform.MacCatalyst => CreateMacCatalyst(),
            _ => throw new ArgumentOutOfRangeException(nameof(platform)),
        };

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
                ObjCRuntimePlatformName = "iOS",
                PlistPlatformString = "ios",
                AvailabilityPlatformString = "ios",
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
                ObjCRuntimePlatformName = "MacOSX",
                PlistPlatformString = "macos",
                AvailabilityPlatformString = "macos",
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
                ObjCRuntimePlatformName = "TvOS",
                PlistPlatformString = "tvos",
                AvailabilityPlatformString = "tvos",
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
                ObjCRuntimePlatformName = "MacCatalyst",
                PlistPlatformString = "ios",
                AvailabilityPlatformString = "maccatalyst",
                DefaultMinimumOS = "15.0",
                HasSimulatorVariant = false,
                SimulatorSlice = null,
                DeviceSlice = device,
            };
        }
    }
}
