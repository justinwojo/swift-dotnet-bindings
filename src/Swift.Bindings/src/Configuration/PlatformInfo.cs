// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Platform-level composition. Holds TFM, NuGet RID, package naming, and references
    /// to its 1-2 slice variants. Does NOT carry a single Rid/SlicePrefix — those live on SliceVariant.
    /// </summary>
    public sealed record PlatformInfo
    {
        public required ApplePlatform Platform { get; init; }

        /// <summary>"net10.0-ios", "net10.0-macos", etc.</summary>
        public required string Tfm { get; init; }

        /// <summary>NuGet RID for native pack paths: "ios-arm64", "osx-arm64", etc.</summary>
        public required string NuGetRid { get; init; }

        /// <summary>".Swift.iOS", ".Swift.macOS", etc.</summary>
        public required string SwiftPackageIdSuffix { get; init; }

        /// <summary>".ObjC.iOS", ".ObjC.macOS", etc.</summary>
        public required string ObjCPackageIdSuffix { get; init; }

        /// <summary>ObjCRuntime.PlatformName enum value name: "iOS", "MacOSX", "TvOS", "MacCatalyst".</summary>
        public required string ObjCRuntimePlatformName { get; init; }

        /// <summary>Plist SupportedPlatform for xcframework filtering: "ios", "macos", "tvos".</summary>
        public required string PlistPlatformString { get; init; }

        /// <summary>ObjC availability annotation platform: "ios", "macos", "tvos", "maccatalyst".</summary>
        public required string AvailabilityPlatformString { get; init; }

        /// <summary>Default minimum OS version fallback.</summary>
        public required string DefaultMinimumOS { get; init; }

        /// <summary>Whether this platform has distinct simulator and device slices.</summary>
        public required bool HasSimulatorVariant { get; init; }

        /// <summary>Simulator slice, or null for macOS/Catalyst.</summary>
        public SliceVariant? SimulatorSlice { get; init; }

        /// <summary>Device slice. Always non-null. For macOS/Catalyst, this is the only slice.</summary>
        public required SliceVariant DeviceSlice { get; init; }

        public SliceVariant GetSlice(bool isSimulator) =>
            (isSimulator && SimulatorSlice != null) ? SimulatorSlice : DeviceSlice;

        public IReadOnlyList<SliceVariant> AllSlices =>
            SimulatorSlice != null ? new[] { SimulatorSlice, DeviceSlice } : new[] { DeviceSlice };

        public string GetDefaultSwiftPackageId(string moduleName) => $"{moduleName}{SwiftPackageIdSuffix}";
        public string GetDefaultObjCPackageId(string moduleName) => $"{moduleName}{ObjCPackageIdSuffix}";
        public string GetNativePackPath(string frameworkName) => $"runtimes/{NuGetRid}/native/{frameworkName}/";
        public string GetBuildTransitivePath() => $"buildTransitive/{Tfm}/";
    }
}
