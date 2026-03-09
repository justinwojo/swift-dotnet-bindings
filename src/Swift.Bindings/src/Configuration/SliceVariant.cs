// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Per-slice properties for a specific build target (e.g., "iOS Simulator" or "macOS Device").
    /// Replaces the hardcoded SDK/triple/slice strings scattered across the compiler and resolver.
    /// </summary>
    public sealed record SliceVariant
    {
        public required ApplePlatform Platform { get; init; }
        public required bool IsSimulator { get; init; }

        /// <summary>xcrun SDK name: "iphonesimulator", "iphoneos", "macosx", etc.</summary>
        public required string SdkName { get; init; }

        /// <summary>Architecture (always "arm64" for now).</summary>
        public string Architecture { get; init; } = "arm64";

        /// <summary>xcframework slice directory: "ios-arm64-simulator", "macos-arm64", etc.</summary>
        public required string SliceId { get; init; }

        /// <summary>CFBundleSupportedPlatforms plist value: "iPhoneSimulator", "MacOSX", etc.</summary>
        public required string PlistPlatformName { get; init; }

        /// <summary>xcframework Info.plist SupportedPlatform: "ios", "macos", "tvos".</summary>
        public required string XCFrameworkPlatformString { get; init; }

        /// <summary>xcframework Info.plist SupportedPlatformVariant: "simulator", "maccatalyst", or null.</summary>
        public string? XCFrameworkPlatformVariant { get; init; }

        /// <summary>
        /// Build the swiftc/swift-frontend target triple.
        /// Example: "arm64-apple-ios17.0-simulator", "arm64-apple-macos12.0".
        /// </summary>
        public string GetTargetTriple(string minOSVersion)
        {
            return Platform switch
            {
                ApplePlatform.iOS when IsSimulator    => $"{Architecture}-apple-ios{minOSVersion}-simulator",
                ApplePlatform.iOS                     => $"{Architecture}-apple-ios{minOSVersion}",
                ApplePlatform.macOS                   => $"{Architecture}-apple-macos{minOSVersion}",
                ApplePlatform.tvOS when IsSimulator    => $"{Architecture}-apple-tvos{minOSVersion}-simulator",
                ApplePlatform.tvOS                    => $"{Architecture}-apple-tvos{minOSVersion}",
                ApplePlatform.MacCatalyst              => $"{Architecture}-apple-ios{minOSVersion}-macabi",
                _ => throw new ArgumentOutOfRangeException(nameof(Platform)),
            };
        }

        public string DisplayName => IsSimulator ? $"{Platform} Simulator" : $"{Platform} Device";
    }
}
