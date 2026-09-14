// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Build.RuntimeNativeExports.cs — packaged SwiftBindingsRuntime export gate

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Xml.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

partial class Build
{
    const string RuntimeNativeLibraryName = "SwiftBindingsRuntime";
    const string RuntimeXcframeworkRoot = "native/SwiftBindingsRuntime.xcframework";
    const string RuntimeSwiftUiExportPrefix = "SBW_SwiftUI_";
    const string MacCatalystVariant = "maccatalyst";
    const string RuntimeFrameworkBinaryPath = "SwiftBindingsRuntime.framework/SwiftBindingsRuntime";

    static readonly IReadOnlyList<RuntimeNativeSlice> RequiredRuntimeNativeSlices =
    [
        new("ios-arm64", RuntimeFrameworkBinaryPath, "ios", null, ["arm64"]),
        new("ios-arm64_x86_64-simulator", RuntimeFrameworkBinaryPath, "ios", "simulator", ["arm64", "x86_64"]),
        new("ios-arm64_x86_64-maccatalyst", RuntimeFrameworkBinaryPath, "ios", MacCatalystVariant, ["arm64", "x86_64"]),
        new("macos-arm64_x86_64", RuntimeFrameworkBinaryPath, "macos", null, ["arm64", "x86_64"]),
        new("tvos-arm64", RuntimeFrameworkBinaryPath, "tvos", null, ["arm64"]),
        new("tvos-arm64_x86_64-simulator", RuntimeFrameworkBinaryPath, "tvos", "simulator", ["arm64", "x86_64"]),
    ];

    internal sealed record RuntimeNativeSlice(
        string Identifier,
        string BinaryPath,
        string Platform,
        string? PlatformVariant,
        IReadOnlyList<string> Architectures);

    internal sealed record RuntimeNativeExportAnalysis(
        IReadOnlyList<string> MissingSlices,
        IReadOnlyList<string> UnexpectedSlices,
        IReadOnlyList<string> SliceMetadataMismatches,
        IReadOnlyList<string> ArchitectureMismatches,
        IReadOnlyList<string> MissingExports)
    {
        internal bool IsSuccess =>
            MissingSlices.Count == 0 &&
            UnexpectedSlices.Count == 0 &&
            SliceMetadataMismatches.Count == 0 &&
            ArchitectureMismatches.Count == 0 &&
            MissingExports.Count == 0;
    }

    /// <summary>
    /// Verifies the native runtime exactly as it ships. The expected symbol set comes from every
    /// <c>SBW_*</c> P/Invoke targeting SwiftBindingsRuntime in the packed Swift.Runtime
    /// assemblies; the required slice and architecture inventory is an independent package contract.
    /// The packed XCFramework plist is treated only as actual inventory. Every actual Mach-O architecture
    /// must export every entry point applicable to that
    /// slice. The only platform exception mirrors the native source's explicit Mac Catalyst guard
    /// around the SwiftUI bridge.
    ///
    /// This deliberately proves symbol presence only. It does not claim ABI agreement or source /
    /// binary equivalence; runtime gates remain responsible for actually calling the wrappers.
    /// </summary>
    static void AssertRuntimeNativeExports(AbsolutePath nupkgPath)
    {
        if (!File.Exists(nupkgPath))
            Assert.Fail($"Runtime native-export gate: package does not exist: {nupkgPath}");

        var scratch = (AbsolutePath)Path.Combine(
            Path.GetTempPath(), $"swift-runtime-native-exports-{Guid.NewGuid():N}");
        scratch.CreateDirectory();

        try
        {
            using var archive = ZipFile.OpenRead(nupkgPath);
            var expectedSymbols = CollectRuntimeNativeImports(archive, scratch);
            if (expectedSymbols.Count == 0)
            {
                Assert.Fail(
                    $"Runtime native-export gate: {nupkgPath} contains no SBW_* imports targeting " +
                    $"{RuntimeNativeLibraryName}. A zero-sized expectation cannot certify exports.");
            }

            var actualSlices = ReadRuntimeNativeSlices(archive, scratch);
            if (actualSlices.Count == 0)
            {
                Assert.Fail(
                    $"Runtime native-export gate: {nupkgPath} declares no AvailableLibraries in " +
                    $"{RuntimeXcframeworkRoot}/Info.plist.");
            }

            var actualArchitectures = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var actualExports = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

            foreach (var slice in actualSlices)
            {
                var packagedBinaryPath =
                    $"{RuntimeXcframeworkRoot}/{slice.Identifier}/{slice.BinaryPath}";
                var binaryEntry = archive.GetEntry(packagedBinaryPath)
                    ?? throw new InvalidOperationException(
                        $"Runtime native-export gate: plist slice '{slice.Identifier}' names missing " +
                        $"binary '{packagedBinaryPath}'.");
                var binary = scratch / "native" / slice.Identifier / RuntimeNativeLibraryName;
                binary.Parent.CreateDirectory();
                binaryEntry.ExtractToFile(binary, overwrite: true);

                var archs = ReadMachOArchitectures(binary);
                actualArchitectures[slice.Identifier] = archs;
                foreach (var arch in archs)
                {
                    actualExports[SliceArchitectureKey(slice.Identifier, arch)] =
                        ReadMachOExports(binary, arch);
                }
            }

            var analysis = AnalyzeRuntimeNativeExports(
                expectedSymbols, RequiredRuntimeNativeSlices, actualSlices, actualArchitectures, actualExports);
            FailRuntimeNativeExportAnalysis(nupkgPath, analysis);
            AssertRuntimeNativeExportNegativeControls(
                expectedSymbols, RequiredRuntimeNativeSlices, actualSlices, actualArchitectures, actualExports);

            var packageSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(nupkgPath)))
                .ToLowerInvariant();
            var pairCount = RequiredRuntimeNativeSlices.Sum(s => s.Architectures.Count);
            var requirementCount = RequiredRuntimeNativeSlices.Sum(slice =>
                slice.Architectures.Count * expectedSymbols.Count(symbol =>
                    IsRuntimeNativeImportExpectedForSlice(symbol, slice)));
            Log.Information(
                "Runtime native-export gate OK — {Symbols} packed managed SBW_* import(s) satisfied " +
                "{Requirements} applicable export requirement(s) across {Pairs} packaged " +
                "slice/architecture pair(s) and {Slices} XCFramework slice(s); package SHA256 {Sha}",
                expectedSymbols.Count, requirementCount, pairCount, RequiredRuntimeNativeSlices.Count, packageSha);
            Log.Information(
                "Runtime native-export negative controls OK — missing-symbol, wrong-slice, and " +
                "missing-plist-slice mutations were rejected");
        }
        finally
        {
            if (Directory.Exists(scratch))
                scratch.DeleteDirectory();
        }
    }

    static SortedSet<string> CollectRuntimeNativeImports(ZipArchive archive, AbsolutePath scratch)
    {
        var symbols = new SortedSet<string>(StringComparer.Ordinal);
        var runtimeDlls = archive.Entries
            .Where(e => e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith("/Swift.Runtime.dll", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.Ordinal)
            .ToList();

        if (runtimeDlls.Count == 0)
            Assert.Fail("Runtime native-export gate: package contains no lib/<tfm>/Swift.Runtime.dll entries.");

        foreach (var entry in runtimeDlls)
        {
            var dll = scratch / "managed" / entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            dll.Parent.CreateDirectory();
            entry.ExtractToFile(dll, overwrite: true);

            using var stream = File.OpenRead(dll);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
                Assert.Fail($"Runtime native-export gate: packed assembly has no metadata: {entry.FullName}");

            var metadata = peReader.GetMetadataReader();
            foreach (var methodHandle in metadata.MethodDefinitions)
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0)
                    continue;

                var import = method.GetImport();
                if (import.Module.IsNil || import.Name.IsNil)
                    continue;

                var library = metadata.GetString(metadata.GetModuleReference(import.Module).Name);
                var entryPoint = metadata.GetString(import.Name);
                if (PackGateNormalizeLibraryName(library) == RuntimeNativeLibraryName
                    && entryPoint.StartsWith("SBW_", StringComparison.Ordinal))
                {
                    symbols.Add(entryPoint);
                }
            }
        }

        return symbols;
    }

    static List<RuntimeNativeSlice> ReadRuntimeNativeSlices(ZipArchive archive, AbsolutePath scratch)
    {
        var plistEntry = archive.GetEntry($"{RuntimeXcframeworkRoot}/Info.plist")
            ?? throw new InvalidOperationException(
                $"Runtime native-export gate: package is missing {RuntimeXcframeworkRoot}/Info.plist.");
        var plistPath = scratch / "Info.plist";
        plistEntry.ExtractToFile(plistPath, overwrite: true);

        var document = XDocument.Load(plistPath);
        var rootDict = document.Root?.Element("dict")
            ?? throw new InvalidOperationException("Runtime native-export gate: XCFramework plist has no root dict.");
        var availableLibraries = PlistDictionaryValue(rootDict, "AvailableLibraries") as XElement;
        if (availableLibraries?.Name.LocalName != "array")
            throw new InvalidOperationException(
                "Runtime native-export gate: XCFramework plist AvailableLibraries is missing or is not an array.");

        var slices = new List<RuntimeNativeSlice>();
        foreach (var dict in availableLibraries.Elements("dict"))
        {
            var identifier = PlistString(dict, "LibraryIdentifier");
            var binaryPath = PlistString(dict, "BinaryPath");
            var platform = PlistString(dict, "SupportedPlatform");
            var platformVariant = PlistOptionalString(dict, "SupportedPlatformVariant");
            var architecturesElement = PlistDictionaryValue(dict, "SupportedArchitectures") as XElement;
            var architectures = architecturesElement?.Elements("string")
                .Select(e => e.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();

            if (architectures.Length == 0)
                throw new InvalidOperationException(
                    $"Runtime native-export gate: plist slice '{identifier}' declares no architectures.");

            slices.Add(new RuntimeNativeSlice(
                identifier, binaryPath, platform, platformVariant, architectures));
        }

        var duplicate = slices.GroupBy(s => s.Identifier, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() != 1);
        if (duplicate != null)
            throw new InvalidOperationException(
                $"Runtime native-export gate: duplicate slice identifier '{duplicate.Key}'.");

        return slices.OrderBy(s => s.Identifier, StringComparer.Ordinal).ToList();
    }

    static XObject? PlistDictionaryValue(XElement dict, string key)
    {
        var nodes = dict.Elements().ToList();
        for (var i = 0; i + 1 < nodes.Count; i++)
        {
            if (nodes[i].Name.LocalName == "key" && nodes[i].Value == key)
                return nodes[i + 1];
        }
        return null;
    }

    static string PlistString(XElement dict, string key)
    {
        var value = PlistDictionaryValue(dict, key) as XElement;
        if (value?.Name.LocalName != "string" || string.IsNullOrWhiteSpace(value.Value))
            throw new InvalidOperationException(
                $"Runtime native-export gate: plist key '{key}' is missing or is not a non-empty string.");
        return value.Value;
    }

    static string? PlistOptionalString(XElement dict, string key)
    {
        var value = PlistDictionaryValue(dict, key) as XElement;
        if (value == null)
            return null;
        if (value.Name.LocalName != "string" || string.IsNullOrWhiteSpace(value.Value))
            throw new InvalidOperationException(
                $"Runtime native-export gate: optional plist key '{key}' is present but is not a non-empty string.");
        return value.Value;
    }

    static bool IsRuntimeNativeImportExpectedForSlice(
        string symbol, RuntimeNativeSlice slice)
    {
        // SwiftBindingsRuntime.swift places the entire SwiftUI bridge behind
        // !targetEnvironment(macCatalyst) because SwiftUI.Text is absent from the Catalyst SDK
        // interface. All other packed managed SBW_* imports are universal package requirements.
        return !string.Equals(slice.PlatformVariant, MacCatalystVariant, StringComparison.Ordinal)
               || !symbol.StartsWith(RuntimeSwiftUiExportPrefix, StringComparison.Ordinal);
    }

    static IReadOnlyList<string> ReadMachOArchitectures(AbsolutePath binary)
    {
        var process = ProcessTasks.StartProcess(
            "lipo", ArgumentEscaper.Join(new[] { "-archs", binary.ToString() }), logOutput: false);
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Runtime native-export gate: lipo could not read '{binary}': " +
                string.Join("\n", process.Output.Select(o => o.Text)));

        return string.Join(" ", process.Output.Select(o => o.Text))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToArray();
    }

    static IReadOnlySet<string> ReadMachOExports(AbsolutePath binary, string architecture)
    {
        var process = ProcessTasks.StartProcess(
            "xcrun",
            ArgumentEscaper.Join(new[] { "nm", "-gU", "-arch", architecture, binary.ToString() }),
            logOutput: false);
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Runtime native-export gate: nm could not read '{binary}' ({architecture}): " +
                string.Join("\n", process.Output.Select(o => o.Text)));

        return process.Output
            .Select(o => o.Text.Trim())
            .Where(line => line.Length > 0)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Last())
            .Select(symbol => symbol.StartsWith('_') ? symbol[1..] : symbol)
            .ToHashSet(StringComparer.Ordinal);
    }

    internal static RuntimeNativeExportAnalysis AnalyzeRuntimeNativeExports(
        IReadOnlySet<string> expectedSymbols,
        IReadOnlyList<RuntimeNativeSlice> expectedSlices,
        IReadOnlyList<RuntimeNativeSlice> actualSlices,
        IReadOnlyDictionary<string, IReadOnlyList<string>> actualArchitectures,
        IReadOnlyDictionary<string, IReadOnlySet<string>> actualExports)
    {
        var actualSlicesById = actualSlices.ToDictionary(slice => slice.Identifier, StringComparer.Ordinal);
        var missingSlices = expectedSlices
            .Where(slice => !actualSlicesById.ContainsKey(slice.Identifier))
            .Select(slice => slice.Identifier)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
        var expectedSliceIds = expectedSlices.Select(s => s.Identifier).ToHashSet(StringComparer.Ordinal);
        var unexpectedSlices = actualSlicesById.Keys
            .Where(id => !expectedSliceIds.Contains(id))
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
        var architectureMismatches = new List<string>();
        var sliceMetadataMismatches = new List<string>();
        var missingExports = new List<string>();

        foreach (var slice in expectedSlices)
        {
            if (!actualSlicesById.TryGetValue(slice.Identifier, out var actualSlice))
                continue;

            if (!string.Equals(slice.BinaryPath, actualSlice.BinaryPath, StringComparison.Ordinal)
                || !string.Equals(slice.Platform, actualSlice.Platform, StringComparison.Ordinal)
                || !string.Equals(slice.PlatformVariant, actualSlice.PlatformVariant, StringComparison.Ordinal)
                || !slice.Architectures.OrderBy(a => a, StringComparer.Ordinal).SequenceEqual(
                    actualSlice.Architectures.OrderBy(a => a, StringComparer.Ordinal), StringComparer.Ordinal))
            {
                sliceMetadataMismatches.Add(
                    $"{slice.Identifier}: expected {DescribeSlice(slice)}, actual {DescribeSlice(actualSlice)}");
            }

            if (!actualArchitectures.TryGetValue(slice.Identifier, out var actualArchs))
            {
                architectureMismatches.Add($"{slice.Identifier}: packaged binary architecture inventory is missing");
                continue;
            }

            var expectedArchs = slice.Architectures.OrderBy(a => a, StringComparer.Ordinal).ToArray();
            var normalizedActualArchs = actualArchs.OrderBy(a => a, StringComparer.Ordinal).ToArray();
            if (!expectedArchs.SequenceEqual(normalizedActualArchs, StringComparer.Ordinal))
            {
                architectureMismatches.Add(
                    $"{slice.Identifier}: expected [{string.Join(",", expectedArchs)}], " +
                    $"actual [{string.Join(",", normalizedActualArchs)}]");
            }

            foreach (var arch in expectedArchs)
            {
                var key = SliceArchitectureKey(slice.Identifier, arch);
                if (!actualExports.TryGetValue(key, out var exports))
                {
                    missingExports.Add($"{key}: <unreadable architecture export set>");
                    continue;
                }

                foreach (var symbol in expectedSymbols.Where(symbol =>
                             IsRuntimeNativeImportExpectedForSlice(symbol, slice)
                             && !exports.Contains(symbol)))
                    missingExports.Add($"{key}: {symbol}");
            }
        }

        return new RuntimeNativeExportAnalysis(
            missingSlices, unexpectedSlices,
            sliceMetadataMismatches.OrderBy(v => v, StringComparer.Ordinal).ToList(),
            architectureMismatches.OrderBy(v => v, StringComparer.Ordinal).ToList(),
            missingExports.OrderBy(v => v, StringComparer.Ordinal).ToList());
    }

    static string SliceArchitectureKey(string slice, string architecture) => $"{slice}/{architecture}";

    static string DescribeSlice(RuntimeNativeSlice slice)
        => $"platform={slice.Platform}, variant={slice.PlatformVariant ?? "none"}, " +
           $"binary={slice.BinaryPath}, architectures=[{string.Join(',', slice.Architectures.OrderBy(a => a, StringComparer.Ordinal))}]";

    static void FailRuntimeNativeExportAnalysis(
        AbsolutePath nupkgPath, RuntimeNativeExportAnalysis analysis)
    {
        if (analysis.IsSuccess)
            return;

        var details = analysis.MissingSlices.Select(v => $"missing slice: {v}")
            .Concat(analysis.UnexpectedSlices.Select(v => $"unexpected slice: {v}"))
            .Concat(analysis.SliceMetadataMismatches.Select(v => $"slice metadata mismatch: {v}"))
            .Concat(analysis.ArchitectureMismatches.Select(v => $"architecture mismatch: {v}"))
            .Concat(analysis.MissingExports.Select(v => $"missing export: {v}"))
            .ToList();
        foreach (var detail in details)
            Log.Error("  {Detail}", detail);
        Assert.Fail(
            $"Runtime native-export gate: {details.Count} defect(s) in {nupkgPath}; see log for exact " +
            "slice/architecture/symbol identities.");
    }

    static void AssertRuntimeNativeExportNegativeControls(
        IReadOnlySet<string> expectedSymbols,
        IReadOnlyList<RuntimeNativeSlice> expectedSlices,
        IReadOnlyList<RuntimeNativeSlice> actualSlices,
        IReadOnlyDictionary<string, IReadOnlyList<string>> architectures,
        IReadOnlyDictionary<string, IReadOnlySet<string>> exports)
    {
        var firstSlice = expectedSlices.First();
        var firstArch = firstSlice.Architectures.First();
        var firstSymbol = expectedSymbols
            .Where(symbol => IsRuntimeNativeImportExpectedForSlice(symbol, firstSlice))
            .OrderBy(s => s, StringComparer.Ordinal)
            .First();
        var firstKey = SliceArchitectureKey(firstSlice.Identifier, firstArch);

        var missingSymbolExports = exports.ToDictionary(
            pair => pair.Key,
            pair => pair.Key == firstKey
                ? (IReadOnlySet<string>)pair.Value.Where(s => s != firstSymbol).ToHashSet(StringComparer.Ordinal)
                : pair.Value,
            StringComparer.Ordinal);
        var missingSymbol = AnalyzeRuntimeNativeExports(
            expectedSymbols, expectedSlices, actualSlices, architectures, missingSymbolExports);
        var expectedMissing = $"{firstKey}: {firstSymbol}";
        if (missingSymbol.MissingExports.Count != 1
            || missingSymbol.MissingExports[0] != expectedMissing
            || missingSymbol.MissingSlices.Count != 0
            || missingSymbol.UnexpectedSlices.Count != 0
            || missingSymbol.SliceMetadataMismatches.Count != 0
            || missingSymbol.ArchitectureMismatches.Count != 0)
        {
            Assert.Fail(
                "Runtime native-export gate missing-symbol negative control did not reject exactly " +
                $"'{expectedMissing}'.");
        }

        var wrongSliceArchitectures = architectures
            .Where(pair => pair.Key != firstSlice.Identifier)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var wrongSliceId = firstSlice.Identifier + "-wrong-slice";
        wrongSliceArchitectures[wrongSliceId] = architectures[firstSlice.Identifier];
        var wrongSlices = actualSlices.Select(slice => slice.Identifier == firstSlice.Identifier
            ? slice with { Identifier = wrongSliceId }
            : slice).ToList();
        var wrongSlice = AnalyzeRuntimeNativeExports(
            expectedSymbols, expectedSlices, wrongSlices, wrongSliceArchitectures, exports);
        if (!wrongSlice.MissingSlices.SequenceEqual(new[] { firstSlice.Identifier }, StringComparer.Ordinal)
            || !wrongSlice.UnexpectedSlices.SequenceEqual(new[] { wrongSliceId }, StringComparer.Ordinal))
        {
            Assert.Fail(
                "Runtime native-export gate wrong-slice negative control did not reject the moved " +
                $"'{firstSlice.Identifier}' inventory as one missing and one unexpected slice.");
        }
        var missingSliceRows = actualSlices.Where(slice => slice.Identifier != firstSlice.Identifier).ToList();
        var missingSliceArchitectures = architectures
            .Where(pair => pair.Key != firstSlice.Identifier)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var missingSliceExports = exports
            .Where(pair => !pair.Key.StartsWith(firstSlice.Identifier + "/", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var missingSlice = AnalyzeRuntimeNativeExports(
            expectedSymbols, expectedSlices, missingSliceRows, missingSliceArchitectures, missingSliceExports);
        if (!missingSlice.MissingSlices.SequenceEqual(new[] { firstSlice.Identifier }, StringComparer.Ordinal)
            || missingSlice.UnexpectedSlices.Count != 0
            || missingSlice.SliceMetadataMismatches.Count != 0)
        {
            Assert.Fail(
                "Runtime native-export gate missing-plist-slice negative control did not reject exactly " +
                $"the removed '{firstSlice.Identifier}' slice.");
        }
    }
}
