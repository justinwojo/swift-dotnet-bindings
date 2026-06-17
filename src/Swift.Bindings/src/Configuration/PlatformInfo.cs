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
        /// <summary>
        /// Fallback Apple-workload platform version used when no <c>--platform-version</c>
        /// CLI override is supplied. Appended to <see cref="Tfm"/> to produce
        /// <see cref="PackTfm"/>. NuGet rejects items under <c>buildTransitive/</c> with a
        /// platform-version-less TFM (NU1012), so the generator-emitted csproj's pack paths
        /// must be platform-versioned. The same value also drives the explicit
        /// <c>&lt;TargetFramework&gt;net10.0-iosX.Y&lt;/TargetFramework&gt;</c> emission so a
        /// generated library project doesn't fall victim to .NET 10's "libraries default to
        /// the oldest installed TPV" rule on multi-workload machines.
        ///
        /// Per-instance overrides flow in via <see cref="PlatformVersion"/>, populated by
        /// <see cref="PlatformInfoFactory.Create(ApplePlatform, string?)"/>. The SwiftBindings.Sdk
        /// pack target still resolves <c>$(TargetPlatformVersion)</c> dynamically at
        /// SDK-consumer build time; the generator-emitted csproj does NOT (it's a static
        /// template), so it relies on the value baked in here at generator-runtime.
        /// </summary>
        public const string DefaultPlatformVersion = "26.0";

        public required ApplePlatform Platform { get; init; }

        /// <summary>"net10.0-ios", "net10.0-macos", etc.</summary>
        public required string Tfm { get; init; }

        /// <summary>
        /// Apple-workload platform version this PlatformInfo was created with (e.g. "26.0",
        /// "26.2"). Defaults to <see cref="DefaultPlatformVersion"/>; overridden by the CLI
        /// <c>--platform-version</c> flag via <see cref="PlatformInfoFactory.Create(ApplePlatform, string?)"/>.
        /// Both <see cref="PackTfm"/> and the generator-emitted <c>&lt;TargetFramework&gt;</c>
        /// element source from this single value, so they cannot drift.
        /// </summary>
        public string PlatformVersion { get; init; } = DefaultPlatformVersion;

        /// <summary>
        /// Platform-versioned form of <see cref="Tfm"/> (e.g. "net10.0-ios26.0") used both
        /// for the generator-emitted csproj's <c>&lt;TargetFramework&gt;</c> element and for
        /// <c>&lt;None Pack="true" PackagePath="buildTransitive/{PackTfm}/" /&gt;</c>
        /// items. Derived from <see cref="Tfm"/> + <see cref="PlatformVersion"/> — do NOT
        /// set independently in <see cref="PlatformInfoFactory"/>, or the two can drift.
        /// Was previously named <c>LibTfm</c> and assigned per-platform; the rename +
        /// derivation collapses the drift surface down to a single source. The CLI
        /// flag plumbing was added for the Apple-framework
        /// publishing release.
        /// </summary>
        public string PackTfm => $"{Tfm}{PlatformVersion}";

        /// <summary>NuGet RID for native pack paths: "ios-arm64", "osx-arm64", etc.</summary>
        public required string NuGetRid { get; init; }

        /// <summary>".Swift.iOS", ".Swift.macOS", etc.</summary>
        public required string SwiftPackageIdSuffix { get; init; }

        /// <summary>".ObjC.iOS", ".ObjC.macOS", etc.</summary>
        public required string ObjCPackageIdSuffix { get; init; }

        /// <summary>Plist SupportedPlatform for xcframework filtering: "ios", "macos", "tvos".</summary>
        public required string PlistPlatformString { get; init; }

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
        public string GetBuildTransitivePath() => $"buildTransitive/{PackTfm}/";
    }
}
