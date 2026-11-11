// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration.AppleTypesManifest;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Every import of a Swift type metadata accessor (<c>$s…Ma</c>) must pass the metadata request.
/// The accessor reads the request from its first argument register; an import declared with no
/// parameter hands it whatever the caller left there, and a stray non-blocking or abstract-state
/// bit makes the accessor return metadata that is not yet complete.
/// </summary>
public class MetadataAccessorRequestTests
{
    // The hand-written trees and the number of accessor imports each declares today. The floors
    // red when the scan stops reaching a tree's imports, rather than passing over none; removing
    // an import on purpose is the one reason to lower one.
    [Theory]
    [InlineData("src/Swift.Runtime/src/Swift", 21)]
    [InlineData("src/Swift.Bindings.Apple/Sources", 4)]
    public void HandWrittenAccessorImports_PassTheRequest(string tree, int floor)
    {
        var root = Path.Combine(FindRepoRoot(), tree);
        var imports = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(root, path))
            .SelectMany(path => MetadataAccessorImportAudit.Find(File.ReadAllText(path))
                .Select(import => (File: Path.GetRelativePath(root, path), Import: import)))
            .ToList();

        var missing = imports.Where(found => !found.Import.PassesRequest)
            .Select(found => $"{found.File}: {found.Import}")
            .ToList();
        Assert.True(missing.Count == 0,
            $"Metadata accessor imports with no request parameter:{Environment.NewLine}" +
            string.Join(Environment.NewLine, missing));
        Assert.True(imports.Count >= floor,
            $"Found {imports.Count} metadata accessor import(s) under {tree}, expected at least {floor}.");
    }

    [Fact]
    public void AppleSupplementAccessorImports_PassTheRequest()
    {
        var manifestPath = Path.Combine(
            FindRepoRoot(), "src", "Swift.Bindings.Sdk", "tools", "apple-types-manifest", "manifest.json");
        var manifest = JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(manifestPath))!;
        var entryCount = manifest.Modules.Values.Sum(module => module.Types.Count);

        var imports = EmitAppleTypes(manifest, SequentialLayoutWhitelist.Empty());

        Assert.Equal(entryCount, imports.Count);
        Assert.All(imports, import => Assert.True(import.PassesRequest, import.ToString()));
    }

    [Fact]
    public void AppleSupplementSequentialLayoutAccessorImport_PassesTheRequest()
    {
        var manifest = new Manifest { SdkTrain = new SdkTrain { Major = 18 } };
        manifest.Modules["Foundation"] = new Module
        {
            Types = new List<TypeEntry>
            {
                new()
                {
                    SwiftIdentity = "Foundation.SomeStruct",
                    Kind = "struct",
                    Frozen = true,
                    Size = 16,
                    Alignment = 8,
                    StorageStrategy = "sequential",
                    SequentialLayoutWhitelisted = true,
                    SequentialLayoutEvidence = new SequentialLayoutEvidence
                    {
                        StoredFieldsKnown = true,
                        CopyDestroyHandling = "trivial",
                        RoundtripValidated = true,
                    },
                    ManagedProjection = new ManagedRef { Namespace = "Swift.Foundation", DeclarationPath = new List<string> { "SomeStruct" } },
                    AbiCarrier = new ManagedRef { Namespace = "Swift.Foundation", DeclarationPath = new List<string> { "SomeStruct" } },
                    MetadataAccessor = new MetadataAccessor
                    {
                        Symbol = "$s10Foundation10SomeStructVMa",
                        Library = "Foundation",
                        Availability = new Availability(),
                    },
                },
            },
        };
        var whitelist = new SequentialLayoutWhitelist { ApprovedIdentities = new List<string> { "Foundation.SomeStruct" } };

        var import = Assert.Single(EmitAppleTypes(manifest, whitelist));

        Assert.True(import.PassesRequest, import.ToString());
    }

    private static List<MetadataAccessorImportAudit.AccessorImport> EmitAppleTypes(
        Manifest manifest, SequentialLayoutWhitelist whitelist)
    {
        var dir = Path.Combine(Path.GetTempPath(), "apple-cs-accessor-request-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var emitter = new AppleTypesCsEmitter(whitelist, NullLogger.Instance);
            emitter.Emit(manifest, dir);
            return emitter.EmittedFiles
                .SelectMany(path => MetadataAccessorImportAudit.Find(File.ReadAllText(path)))
                .ToList();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static bool IsBuildOutput(string root, string path)
    {
        var segments = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
        return segments.Contains("obj") || segments.Contains("bin");
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }
}
