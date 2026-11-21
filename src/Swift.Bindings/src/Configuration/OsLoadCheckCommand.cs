// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// The <c>--check-os-loads</c> verb: load safety (P1) for a binary the generator did not link. The SDK
    /// links the second slice of an Apple-framework wrapper, and of its SwiftUI bridge, itself, and asks
    /// this verb whether that slice loads an OS-resident dylib strongly in a way a symbol missing from an
    /// older OS would fail. The answer is the generator's own (<see cref="OsFrameworkWeakLinking.UnsafeStrongLoads"/>),
    /// against the same OS symbol inventory, so an umbrella the linker keeps strong passes only when the
    /// inventory's OS has it on the slice's own platform variant, as on every slice the generator links.
    /// </summary>
    internal static class OsLoadCheckCommand
    {
        /// <summary>
        /// Writes the unsafe strong loads of <paramref name="binary"/> to <paramref name="stdout"/>,
        /// space-separated (nothing when the binary is safe), and returns 0. A read that fails or shows
        /// nothing is an error (exit 1), never a safe binary.
        /// </summary>
        internal static int Run(
            string binary, string? sdkPath, string? inventoryPath, ICommandRunner commandRunner, TextWriter stdout, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(sdkPath))
            {
                logger.LogError("Error: --check-os-loads requires --check-os-loads-sdk <the SDK the binary was linked against>.");
                return 1;
            }
            try
            {
                OsSymbolFloorContext.LoadForRun(inventoryPath);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                logger.LogError("Error: could not read --os-symbol-inventory '{Path}': {Message}", inventoryPath, ex.Message);
                return 1;
            }

            try
            {
                var (loadExit, loadCommands, loadError) = commandRunner.Run("xcrun", $"otool -l \"{binary}\"", timeoutMs: 60000);
                if (loadExit != 0)
                    throw new InvalidOperationException($"otool -l failed on '{binary}' (exit {loadExit}): {loadError.Trim()}");
                if (!loadCommands.Contains("cmd LC_", StringComparison.Ordinal))
                    throw new InvalidOperationException($"otool -l shows no load commands for '{binary}'.");
                var strong = OsFrameworkWeakLinking.StrongOsResidentLoads(OsFrameworkWeakLinking.ParseLoadCommands(loadCommands));
                if (strong.Count > 0)
                {
                    var (importExit, imports, importError) = commandRunner.Run("xcrun", $"dyld_info -imports \"{binary}\"", timeoutMs: 120000);
                    if (importExit != 0)
                        throw new InvalidOperationException($"dyld_info -imports failed on '{binary}' (exit {importExit}): {importError.Trim()}");
                    if (!imports.Contains("-imports:", StringComparison.Ordinal))
                        throw new InvalidOperationException($"dyld_info -imports shows no import table for '{binary}'.");
                    strong = OsFrameworkWeakLinking.UnsafeStrongLoads(
                        strong, OsFrameworkWeakLinking.ParseImports(imports), sdkPath, OsFrameworkWeakLinking.RunInventory);
                }
                stdout.Write(string.Join(" ", strong));
                return 0;
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError("Error: {Message}", ex.Message);
                return 1;
            }
        }
    }
}
