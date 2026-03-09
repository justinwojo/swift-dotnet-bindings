// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Describes a framework dependency for wrapper compilation and NuGet packaging.
    /// Each dependency provides additional -F search paths for swiftc and a
    /// PackageReference entry in the emitted .csproj.
    /// </summary>
    public sealed class FrameworkDependencyInfo
    {
        /// <summary>
        /// Absolute path to the dependency xcframework directory.
        /// </summary>
        public required string XCFrameworkPath { get; init; }

        /// <summary>
        /// The Swift module name extracted from the dependency xcframework.
        /// </summary>
        public required string ModuleName { get; init; }

        /// <summary>
        /// Version extracted from the dependency xcframework Info.plist.
        /// Null if extraction fails or version is not available.
        /// </summary>
        public string? PackageVersion { get; init; }

        /// <summary>
        /// Package ID override. If null, uses the convention {ModuleName}.Swift.iOS.
        /// </summary>
        public string? PackageId { get; init; }

        /// <summary>
        /// Resolved framework search path for the simulator slice.
        /// </summary>
        public string? SimulatorFrameworkSearchPath { get; init; }

        /// <summary>
        /// Resolved framework search path for the device slice.
        /// </summary>
        public string? DeviceFrameworkSearchPath { get; init; }

        /// <summary>
        /// True when the dependency is an ObjC-only framework (no .swiftmodule).
        /// ObjC-only deps provide -F search paths for module resolution but
        /// do not emit PackageReference entries or require binding generation.
        /// </summary>
        public bool IsObjCOnly { get; init; }

        /// <summary>
        /// Path to the resolved dylib. Used internally for recursive dependency graph analysis.
        /// Null for ObjC-only dependencies.
        /// </summary>
        public string? DylibPath { get; init; }

        /// <summary>
        /// Path to the dependency's ABI JSON file.
        /// Used by GenerateBindings to parse dependency types into the type database,
        /// enabling cross-module type resolution (dependency types resolve instead of AnyType).
        /// Null for ObjC-only dependencies.
        /// </summary>
        public string? AbiJsonPath { get; init; }

        /// <summary>
        /// Path to the dependency's TBD file.
        /// Used alongside AbiJsonPath for demangling during dependency type database loading.
        /// Null for ObjC-only dependencies.
        /// </summary>
        public string? TbdPath { get; init; }

        /// <summary>
        /// True when this dependency was auto-detected by BinaryDependencyAnalyzer (otool -L),
        /// false when explicitly provided via --framework-dependency.
        /// Auto-detected dependencies use warn-only error handling during ABI parsing;
        /// explicit dependencies fail hard.
        /// </summary>
        public bool IsAutoDetected { get; init; }

        /// <summary>
        /// Effective package ID: explicit override or convention (defaults to iOS).
        /// </summary>
        public string EffectivePackageId => GetEffectivePackageId();

        /// <summary>
        /// Effective package ID with platform-aware default.
        /// </summary>
        public string GetEffectivePackageId(PlatformInfo? platformInfo = null)
        {
            if (PackageId != null) return PackageId;
            var pi = platformInfo ?? PlatformInfoFactory.Create(ApplePlatform.iOS);
            return pi.GetDefaultSwiftPackageId(ModuleName);
        }

        /// <summary>
        /// Effective version: extracted or "0.0.0" placeholder.
        /// </summary>
        public string EffectiveVersion => PackageVersion ?? "0.0.0";
    }
}
