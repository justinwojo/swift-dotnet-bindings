// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// What <see cref="OsSymbolFloorInference"/> needs to floor one module's members: the platform, the
    /// OS version the inventory describes, the floor to infer, and the presence test for the module's
    /// provider on that OS.
    /// </summary>
    internal sealed record OsSymbolFloorContext(
        string ModuleName,
        string AvailabilityPlatform,
        string InventoryOsVersion,
        string Floor,
        Func<string, bool> ShipsOnInventoryOs,
        string Source)
    {
        /// <summary>The platform variants an <see cref="ApplePlatform"/> binding runs on, as inventory variant names.</summary>
        internal static IReadOnlyList<string> VariantsOf(ApplePlatform platform) => platform switch
        {
            ApplePlatform.iOS => new[] { "ios", "ios-simulator" },
            ApplePlatform.tvOS => new[] { "tvos", "tvos-simulator" },
            ApplePlatform.macOS => new[] { "macos" },
            ApplePlatform.MacCatalyst => new[] { "maccatalyst" },
            _ => Array.Empty<string>(),
        };

        /// <summary>The Swift <c>@available</c> platform name an <see cref="ApplePlatform"/> binding's floors use.</summary>
        internal static string? AvailabilityPlatformOf(ApplePlatform platform) => platform switch
        {
            ApplePlatform.iOS => "iOS",
            ApplePlatform.tvOS => "tvOS",
            ApplePlatform.macOS => "macOS",
            ApplePlatform.MacCatalyst => "macCatalyst",
            _ => null,
        };

        /// <summary>
        /// The inventory a run uses: the shipped one, or <paramref name="inventoryPath"/> loaded over it,
        /// whose providers are then weak-linked like OS frameworks by every wrapper link of the run, and
        /// which then decides every strong-load check of the run (<see cref="OsFrameworkWeakLinking.RunInventory"/>),
        /// as it decides the floors.
        /// </summary>
        internal static OsSymbolInventory LoadForRun(string? inventoryPath)
        {
            if (string.IsNullOrEmpty(inventoryPath))
            {
                OsFrameworkWeakLinking.RunInventory = null;
                return OsSymbolInventory.Shipped;
            }
            var inventory = OsSymbolInventory.Load(inventoryPath, OsSymbolInventory.Shipped);
            OsFrameworkWeakLinking.TreatAsOsProviders(inventory.Providers.Select(p => p.InstallName));
            OsFrameworkWeakLinking.RunInventory = inventory;
            return inventory;
        }

        /// <summary>
        /// The context for one generation run: loads <paramref name="inventoryPath"/> over the shipped
        /// inventory when given, registers a supplied file's providers for weak-linking, reads the
        /// generating SDK's version, and logs why floors are not inferred when they are not.
        /// </summary>
        internal static OsSymbolFloorContext? ResolveForGeneration(
            string? inventoryPath, string? moduleName, PlatformInfo platformInfo, bool isOsFramework,
            ICommandRunner commandRunner, ILogger logger)
        {
            if (string.IsNullOrEmpty(moduleName))
            {
                OsFrameworkWeakLinking.RunInventory = null;
                return null;
            }
            // A module that is not an OS framework is floored only as a supplied provider, and only a
            // supplied file names providers, so without one the shipped inventory stays undecoded.
            if (!isOsFramework && string.IsNullOrEmpty(inventoryPath))
            {
                OsFrameworkWeakLinking.RunInventory = null;
                return null;
            }
            var inventory = LoadForRun(inventoryPath);
            if (!isOsFramework && inventory.FindProvider(moduleName) is null)
                return null;

            string? sdkVersion = null;
            var (exitCode, stdout, _) = commandRunner.Run("xcrun", $"--sdk {platformInfo.DeviceSlice.SdkName} --show-sdk-version");
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
                sdkVersion = stdout.Trim();

            var context = TryCreate(inventory, moduleName, platformInfo.Platform, isOsFramework, sdkVersion, out var reason);
            if (context is null)
                logger.LogInformation(
                    "OS symbol inventory: no floors inferred for {Module} on {Platform}: {Reason}.",
                    moduleName, platformInfo.Platform, reason);
            return context;
        }

        /// <summary>
        /// The context for binding <paramref name="moduleName"/> on <paramref name="platform"/>, or null
        /// (with the reason) when floors are not inferred: the module is neither a supplied provider nor
        /// an OS framework, the inventory does not cover the platform, the generating SDK's version is
        /// unknown, or the floor would not exceed the inventory's OS version.
        /// </summary>
        internal static OsSymbolFloorContext? TryCreate(
            OsSymbolInventory inventory, string moduleName, ApplePlatform platform, bool isOsFramework,
            string? generatingSdkVersion, out string reason)
        {
            var availabilityPlatform = AvailabilityPlatformOf(platform);
            var variantNames = VariantsOf(platform);
            if (availabilityPlatform is null || variantNames.Count == 0)
            {
                reason = $"the inventory does not cover {platform}";
                return null;
            }

            if (inventory.FindProvider(moduleName) is { } provider
                && (provider.Variants is null || provider.Variants.Any(variantNames.Contains)))
            {
                var floor = provider.Floor ?? generatingSdkVersion;
                if (string.IsNullOrEmpty(floor))
                {
                    reason = "the supplied provider declares no floor and the generating SDK's version is unknown";
                    return null;
                }
                if (AvailabilityHelpers.CompareOsVersions(floor, provider.OsVersion) <= 0)
                {
                    reason = $"the floor {floor} does not exceed the supplied provider's OS version {provider.OsVersion}";
                    return null;
                }
                reason = string.Empty;
                return new OsSymbolFloorContext(moduleName, availabilityPlatform, provider.OsVersion, floor,
                    provider.Symbols.Contains, $"supplied provider {provider.InstallName} ({inventory.Provenance})");
            }

            if (!isOsFramework)
            {
                reason = "the module is not an OS framework and no supplied inventory lists it";
                return null;
            }
            var platforms = inventory.Platforms.Where(p => variantNames.Contains(p.Variant)).ToList();
            if (platforms.Count != variantNames.Count)
            {
                reason = $"the inventory lacks a variant of {platform} ({string.Join(", ", variantNames)})";
                return null;
            }
            if (string.IsNullOrEmpty(generatingSdkVersion))
            {
                reason = "the generating SDK's version is unknown";
                return null;
            }
            var inventoryOs = platforms.Select(p => p.OsVersion)
                .OrderBy(v => v, Comparer<string>.Create(AvailabilityHelpers.CompareOsVersions)).First();
            if (AvailabilityHelpers.CompareOsVersions(generatingSdkVersion, inventoryOs) <= 0)
            {
                reason = $"the generating SDK {generatingSdkVersion} is not newer than the inventory's OS {inventoryOs}";
                return null;
            }
            reason = string.Empty;
            return new OsSymbolFloorContext(moduleName, availabilityPlatform, inventoryOs, generatingSdkVersion,
                symbol => inventory.ShipsOnAll(platforms, symbol),
                $"{inventory.Provenance}: {string.Join(", ", platforms.Select(p => $"{p.Variant} {p.OsVersion} ({p.SdkBuild})"))}");
        }
    }
}
