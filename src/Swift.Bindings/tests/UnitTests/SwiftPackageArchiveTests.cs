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
/// MACH_O_TYPE=mh_dylib override links the package's object-file target without libSystem.
/// </summary>
public class SwiftPackageArchiveTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "spm-archive-" + Guid.NewGuid().ToString("N"));

    public SwiftPackageArchiveTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    const string Manifest = """
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
        """;

    [Fact]
    public void DeclareProductDynamic_AutomaticProduct_GainsDynamicTypeAndLeavesOthers()
    {
        var patched = SwiftPackageArchive.DeclareProductDynamic(Manifest, "Player", out var declared);
        Assert.True(declared);
        Assert.Contains("name: \"Player\", type: .dynamic,", patched);
        Assert.Contains(".library(name: \"PlayerExtras\", targets: [\"PlayerExtras\"])", patched);
    }

    [Theory]
    [InlineData(".static")]
    [InlineData(".dynamic")]
    [InlineData("nil")]
    [InlineData("Product.Library.LibraryType.static")]
    public void DeclareProductDynamic_ExplicitType_BecomesDynamicOnce(string type)
    {
        var manifest = $".library(name: \"Player\", type: {type}, targets: [\"Player\"])";
        var patched = SwiftPackageArchive.DeclareProductDynamic(manifest, "Player", out var declared);
        Assert.True(declared);
        Assert.Equal(".library(name: \"Player\", type: .dynamic, targets: [\"Player\"])", patched);
    }

    [Fact]
    public void DeclareProductDynamic_ProductNotDeclared_ReportsItAndChangesNothing()
    {
        var patched = SwiftPackageArchive.DeclareProductDynamic(Manifest, "Play", out var declared);
        Assert.False(declared);
        Assert.Equal(Manifest, patched);
    }

    [Fact]
    public void IsPackageBuild_OnlyAManifestAtTheRoot()
    {
        Assert.False(SwiftPackageArchive.IsPackageBuild(_dir, project: null));
        File.WriteAllText(Path.Combine(_dir, "Package.swift"), Manifest);
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
