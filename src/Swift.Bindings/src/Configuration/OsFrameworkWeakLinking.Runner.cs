// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BindingsGeneration
{
    // The command-runner half of OsFrameworkWeakLinking: the generator reads a linked wrapper's load
    // commands through ICommandRunner so the compile paths stay testable. The pure half is shared with
    // the build harness, which reads them through its own tool runner.
    internal static partial class OsFrameworkWeakLinking
    {
        private static OsSymbolInventory? s_runInventory;

        /// <summary>
        /// The OS symbol inventory every strong-load check of this process reads
        /// (<see cref="UnsafeStrongLoads"/>): the one the run loaded (<see cref="OsSymbolFloorContext.LoadForRun"/>,
        /// where a supplied binary inventory replaces the shipped one), else the shipped one. Process-wide,
        /// like the providers a supplied inventory registers (<see cref="TreatAsOsProviders"/>). Setting null
        /// returns it to the shipped one, decoded only when a check reads it.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.AllowNull]
        internal static OsSymbolInventory RunInventory
        {
            get => Volatile.Read(ref s_runInventory) ?? OsSymbolInventory.Shipped;
            set => Volatile.Write(ref s_runInventory, value);
        }

        /// <summary>
        /// Reads <paramref name="binary"/>'s dylib load commands with <c>otool -l</c>. Throws when the read
        /// fails: a wrapper whose load commands cannot be checked is not known to load.
        /// </summary>
        internal static IReadOnlyList<LoadedDylib> ReadLoadCommands(string binary, ICommandRunner commandRunner)
        {
            var (exitCode, stdout, stderr) = commandRunner.Run("xcrun", $"otool -l \"{binary}\"", timeoutMs: 60000);
            if (exitCode != 0)
                throw new InvalidOperationException(
                    $"Could not read the load commands of '{binary}' (otool exited {exitCode}): {stderr.Trim()}");
            return ParseLoadCommands(stdout);
        }

        /// <summary>Reads <paramref name="binary"/>'s imports with <c>dyld_info -imports</c>; throws when the read fails.</summary>
        internal static IReadOnlyList<ImportedSymbol> ReadImports(string binary, ICommandRunner commandRunner)
        {
            var (exitCode, stdout, stderr) = commandRunner.Run("xcrun", $"dyld_info -imports \"{binary}\"", timeoutMs: 120000);
            if (exitCode != 0)
                throw new InvalidOperationException(
                    $"Could not read the imports of '{binary}' (dyld_info exited {exitCode}): {stderr.Trim()}");
            return ParseImports(stdout);
        }

        /// <summary>
        /// The OS-resident dylibs <paramref name="binary"/> loads strongly that a missing symbol would still
        /// fail (<see cref="UnsafeStrongLoads"/>), by install name. Reads the imports only when a load is strong.
        /// </summary>
        internal static IReadOnlyList<string> UnsafeStrongOsResident(string binary, string? sdkPath, ICommandRunner commandRunner)
        {
            var strong = StrongOsResidentLoads(ReadLoadCommands(binary, commandRunner));
            return strong.Count == 0 ? strong : UnsafeStrongLoads(strong, ReadImports(binary, commandRunner), sdkPath, RunInventory);
        }

        /// <summary>
        /// The OS-resident dylibs <paramref name="binary"/> still loads strongly, split into the frameworks a
        /// relink can weaken and the rest (a <c>/System/</c> dylib that is not a framework, which the
        /// wrapper link never asks for by name).
        /// </summary>
        internal static (IReadOnlyList<string> Frameworks, IReadOnlyList<string> Other) StrongOsResident(
            string binary, ICommandRunner commandRunner)
        {
            var strong = StrongOsResidentLoads(ReadLoadCommands(binary, commandRunner));
            var frameworks = strong.Select(FrameworkName).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            var other = strong.Where(s => FrameworkName(s) is null).ToList();
            return (frameworks, other);
        }
    }
}
