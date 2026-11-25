// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// `nuke fetch` archives a package-only library by declaring its product dynamic, because the
/// MACH_O_TYPE=mh_dylib override links the package's object-file target without libSystem. The
/// declaration is appended to the manifest and proven by SwiftPM's own reading of it.
/// </summary>
public class SwiftPackageArchiveTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "spm-archive-" + Guid.NewGuid().ToString("N"));

    public SwiftPackageArchiveTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // Manifest shapes a text edit of the `.library(...)` call gets wrong: a computed type argument,
    // a commented-out example holding a comma inside its type argument, an explicit `nil`, a
    // qualified constant and a product name held in a constant. Each must come out dynamic for the
    // product the scheme names and leave every other product as it was.
    public static TheoryData<string, string> ManifestShapes => new()
    {
        { "computed type", """
            func linkage() -> Product.Library.LibraryType? { .static }
            let package = Package(
                name: "Player",
                products: [
                    .library(name: "Player", type: linkage(), targets: ["Player"]),
                    .library(name: "PlayerExtras", targets: ["PlayerExtras"]),
                ],
                targets: [.target(name: "Player"), .target(name: "PlayerExtras")]
            )
            """ },
        { "commented example", """
            /* .library(name: "Player", type: /* static, for now */ .static, targets: ["Player"]), */
            let package = Package(
                name: "Player",
                products: [
                    .library(
                        name: "Player",
                        targets: ["Player"]
                    ),
                    .library(name: "PlayerExtras", targets: ["PlayerExtras"]),
                ],
                targets: [.target(name: "Player"), .target(name: "PlayerExtras")]
            )
            """ },
        { "nil and qualified", """
            let package = Package(
                name: "Player",
                products: [
                    .library(name: "Player", type: nil, targets: ["Player"]),
                    .library(name: "PlayerExtras", type: Product.Library.LibraryType.static, targets: ["PlayerExtras"]),
                ],
                targets: [.target(name: "Player"), .target(name: "PlayerExtras")]
            )
            """ },
        { "name in a constant", """
            let productName = "Player"
            let package = Package(
                name: "Player",
                products: [
                    .library(name: productName, type: .static, targets: [productName]),
                    .library(name: "PlayerExtras", targets: ["PlayerExtras"]),
                ],
                targets: [.target(name: "Player"), .target(name: "PlayerExtras")]
            )
            """ },
    };

    [Theory]
    [MemberData(nameof(ManifestShapes))]
    public void AppendDynamicProductDeclaration_SwiftPmReadsTheProductDynamic(string shape, string body)
    {
        var expectedExtras = shape == "nil and qualified" ? "static" : "automatic";
        WritePackage("// swift-tools-version:5.7\nimport PackageDescription\n" + body + "\n");
        var manifest = Path.Combine(_dir, "Package.swift");
        var original = File.ReadAllText(manifest);
        var patched = SwiftPackageArchive.AppendDynamicProductDeclaration(original, "Player");
        File.WriteAllText(manifest, patched);

        var dump = DumpPackage();
        Assert.Equal("dynamic", SwiftPackageArchive.LibraryTypeOf(dump, "Player"));
        Assert.Equal(expectedExtras, SwiftPackageArchive.LibraryTypeOf(dump, "PlayerExtras"));
        // The manifest above the appended block is untouched, so a comment is never edited.
        Assert.StartsWith(original, patched);
    }

    [Fact]
    public void AppendDynamicProductDeclaration_IsIdempotentAndLeavesAnUndeclaredProductAbsent()
    {
        const string manifest = "let package = Package(name: \"Player\")\n";
        var once = SwiftPackageArchive.AppendDynamicProductDeclaration(manifest, "Player");
        Assert.StartsWith(manifest, once);
        Assert.Equal(once, SwiftPackageArchive.AppendDynamicProductDeclaration(once, "Player"));
        Assert.Null(SwiftPackageArchive.LibraryTypeOf("""{"products":[{"name":"Other","type":{"library":["automatic"]}}]}""", "Player"));
        Assert.Null(SwiftPackageArchive.LibraryTypeOf("""{"products":[{"name":"Player","type":{"executable":null}}]}""", "Player"));
    }

    [Fact]
    public void AppendDynamicProductDeclaration_EscapesTheProductName()
    {
        var patched = SwiftPackageArchive.AppendDynamicProductDeclaration("", "Pla\"y\\er");
        Assert.Contains("product.name == \"Pla\\\"y\\\\er\"", patched);
    }

    void WritePackage(string manifest)
    {
        File.WriteAllText(Path.Combine(_dir, "Package.swift"), manifest);
        foreach (var target in new[] { "Player", "PlayerExtras" })
        {
            Directory.CreateDirectory(Path.Combine(_dir, "Sources", target));
            File.WriteAllText(Path.Combine(_dir, "Sources", target, "a.swift"), "public func f() {}\n");
        }
    }

    string DumpPackage()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("xcrun", "swift package dump-package")
        {
            WorkingDirectory = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"dump-package failed: {stderr.Result}");
        return stdout;
    }

    [Fact]
    public void IsPackageBuild_OnlyAManifestAtTheRoot()
    {
        Assert.False(SwiftPackageArchive.IsPackageBuild(_dir, project: null));
        File.WriteAllText(Path.Combine(_dir, "Package.swift"), "let package = Package(name: \"Player\")\n");
        // An example app's project below the root is not what xcodebuild builds.
        Directory.CreateDirectory(Path.Combine(_dir, "Example", "Example.xcodeproj"));
        Assert.True(SwiftPackageArchive.IsPackageBuild(_dir, project: null));
        Assert.False(SwiftPackageArchive.IsPackageBuild(_dir, project: "Player.xcodeproj"));
        Directory.CreateDirectory(Path.Combine(_dir, "Player.xcodeproj"));
        Assert.False(SwiftPackageArchive.IsPackageBuild(_dir, project: null));
    }

    [Fact]
    public void Manifests_IncludeVersionSpecificVariantsOnly()
    {
        foreach (var name in new[] { "Package.swift", "Package@swift-5.9.swift", "PackageHelpers.swift" })
            File.WriteAllText(Path.Combine(_dir, name), "");
        var names = SwiftPackageArchive.Manifests(_dir);
        Assert.Equal(new[] { "Package.swift", "Package@swift-5.9.swift" }, names.Select(Path.GetFileName));
    }
}
