// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;

public record ApplePlatform
{
    public required string Name { get; init; }

    // Simulator
    public required string SimulatorSdkName { get; init; }
    public required string SimulatorTarget { get; init; }
    public required string SimulatorSliceId { get; init; }
    public required string SimulatorModuleSuffix { get; init; }
    public required string SimulatorPlistPlatform { get; init; }

    // Device (null for macOS)
    public string? DeviceSdkName { get; init; }
    public string? DeviceTarget { get; init; }
    public string? DeviceSliceId { get; init; }
    public string? DeviceModuleSuffix { get; init; }
    public string? DevicePlistPlatform { get; init; }

    public required string MinOsVersion { get; init; }
    public required string TfmSuffix { get; init; }
    public required string PackageSuffix { get; init; }

    // Xcframework plist fields
    public required string SupportedPlatform { get; init; }
    public string? SimulatorPlistVariant { get; init; }

    public bool HasDeviceSlice => DeviceSdkName != null;
    public bool HasSimulatorPlistVariant => SimulatorPlistVariant != null;

    public const string BaseTfm = "net10.0";
    public string GetTfm() => $"{BaseTfm}-{TfmSuffix}";

    public static ApplePlatform IOS { get; } = new()
    {
        Name = "ios",
        SimulatorSdkName = "iphonesimulator",
        SimulatorTarget = "arm64-apple-ios15.0-simulator",
        SimulatorSliceId = "ios-arm64-simulator",
        SimulatorModuleSuffix = "arm64-apple-ios-simulator",
        SimulatorPlistPlatform = "iPhoneSimulator",
        DeviceSdkName = "iphoneos",
        DeviceTarget = "arm64-apple-ios15.0",
        DeviceSliceId = "ios-arm64",
        DeviceModuleSuffix = "arm64-apple-ios",
        DevicePlistPlatform = "iPhoneOS",
        MinOsVersion = "15.0",
        TfmSuffix = "ios",
        PackageSuffix = "iOS",
        SupportedPlatform = "ios",
        SimulatorPlistVariant = "simulator",
    };

    public static ApplePlatform MacOS { get; } = new()
    {
        Name = "macos",
        SimulatorSdkName = "macosx",
        SimulatorTarget = "arm64-apple-macos12.0",
        SimulatorSliceId = "macos-arm64",
        SimulatorModuleSuffix = "arm64-apple-macos",
        SimulatorPlistPlatform = "MacOSX",
        DeviceSdkName = null,
        DeviceTarget = null,
        DeviceSliceId = null,
        DeviceModuleSuffix = null,
        DevicePlistPlatform = null,
        MinOsVersion = "12.0",
        TfmSuffix = "macos",
        PackageSuffix = "macOS",
        SupportedPlatform = "macos",
        SimulatorPlistVariant = null,
    };

    public static ApplePlatform TvOS { get; } = new()
    {
        Name = "tvos",
        SimulatorSdkName = "appletvsimulator",
        SimulatorTarget = "arm64-apple-tvos15.0-simulator",
        SimulatorSliceId = "tvos-arm64-simulator",
        SimulatorModuleSuffix = "arm64-apple-tvos-simulator",
        SimulatorPlistPlatform = "AppleTVSimulator",
        DeviceSdkName = "appletvos",
        DeviceTarget = "arm64-apple-tvos15.0",
        DeviceSliceId = "tvos-arm64",
        DeviceModuleSuffix = "arm64-apple-tvos",
        DevicePlistPlatform = "AppleTVOS",
        MinOsVersion = "15.0",
        TfmSuffix = "tvos",
        PackageSuffix = "tvOS",
        SupportedPlatform = "tvos",
        SimulatorPlistVariant = "simulator",
    };

    public static ApplePlatform FromName(string name) => name.ToLowerInvariant() switch
    {
        "ios" => IOS,
        "macos" => MacOS,
        "tvos" => TvOS,
        _ => throw new ArgumentException($"Unknown platform: {name}")
    };
}
