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

    // RID for the .NET app build + native-exe path (macOS/Catalyst host runners).
    // Null for simulator-deployed platforms (iOS/tvOS), which never build a host exe.
    public string? Rid { get; init; }

    // CPU architecture string used for xcframework plist SupportedArchitectures
    // and to select which generated thunk-assembly file ({ns}.{arch}.s) to compile.
    // "arm64" for the default platforms; "x86_64" for the Intel/Rosetta variants.
    public string SliceArchitecture { get; init; } = "arm64";

    // When true, the native test exe is launched under `arch -x86_64` so the
    // Mono-x86_64 runtime is exercised under Rosetta on an Apple Silicon host.
    public bool RunUnderRosetta { get; init; }

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
        Rid = "osx-arm64",
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

    public static ApplePlatform MacCatalyst { get; } = new()
    {
        Name = "maccatalyst",
        SimulatorSdkName = "macosx",
        SimulatorTarget = "arm64-apple-ios15.0-macabi",
        SimulatorSliceId = "ios-arm64-maccatalyst",
        SimulatorModuleSuffix = "arm64-apple-ios-macabi",
        SimulatorPlistPlatform = "MacOSX",
        DeviceSdkName = null,
        DeviceTarget = null,
        DeviceSliceId = null,
        DeviceModuleSuffix = null,
        DevicePlistPlatform = null,
        MinOsVersion = "15.0",
        TfmSuffix = "maccatalyst",
        PackageSuffix = "MacCatalyst",
        SupportedPlatform = "ios",
        SimulatorPlistVariant = "maccatalyst",
        Rid = "maccatalyst-arm64",
    };

    // Intel/x86_64 macOS-workload variant. Builds x86_64-only test/dep/wrapper
    // frameworks under a distinct "macos-x86_64" slice id, so the arm64
    // "macos-arm64" artifacts the default MacOS cell produces stay untouched.
    // Name stays "macos" so the generator --platform argument is unchanged.
    public static ApplePlatform MacOSX64 { get; } = MacOS with
    {
        SimulatorTarget = "x86_64-apple-macos12.0",
        SimulatorSliceId = "macos-x86_64",
        SimulatorModuleSuffix = "x86_64-apple-macos",
        SliceArchitecture = "x86_64",
        Rid = "osx-x64",
        RunUnderRosetta = true,
    };

    // Intel/x86_64 Mac Catalyst variant. Builds the ios-x86_64-maccatalyst slice
    // (distinct from the arm64 ios-arm64-maccatalyst) and runs under Rosetta.
    public static ApplePlatform MacCatalystX64 { get; } = MacCatalyst with
    {
        SimulatorTarget = "x86_64-apple-ios15.0-macabi",
        SimulatorSliceId = "ios-x86_64-maccatalyst",
        SimulatorModuleSuffix = "x86_64-apple-ios-macabi",
        SliceArchitecture = "x86_64",
        Rid = "maccatalyst-x64",
        RunUnderRosetta = true,
    };

    public static ApplePlatform FromName(string name) => name.ToLowerInvariant() switch
    {
        "ios" => IOS,
        "macos" => MacOS,
        "tvos" => TvOS,
        "maccatalyst" => MacCatalyst,
        _ => throw new ArgumentException($"Unknown platform: {name}")
    };
}
