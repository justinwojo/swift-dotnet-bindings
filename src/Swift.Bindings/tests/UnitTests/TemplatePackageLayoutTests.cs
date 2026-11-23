// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="TemplatePackageLayout"/>, the pack-time rule that the
/// <c>SwiftBindings.Templates</c> nupkg ships each template file once, under <c>content/</c>. A
/// second copy makes <c>dotnet new install</c> report the template identity twice.
/// </summary>
public class TemplatePackageLayoutTests
{
    static readonly string[] PackageMetadata =
    {
        "_rels/.rels", "SwiftBindings.Templates.nuspec", "README.md", "LICENSE", "NOTICE.md", "icon.png",
        "[Content_Types].xml", "package/services/metadata/core-properties/0123.psmdcp",
    };

    static readonly string[] SingleCopy =
    {
        "content/content/swift-binding/ProjectName.csproj",
        "content/content/swift-binding/.template.config/template.json",
    };

    // The entries of a Templates nupkg packed with NuGet's default content;contentFiles target folders.
    static readonly string[] DefaultContentTargetFolders =
    {
        "content/content/swift-binding/ProjectName.csproj",
        "contentFiles/any/netstandard2.0/content/swift-binding/ProjectName.csproj",
        "content/content/swift-binding/.template.config/template.json",
        "contentFiles/any/netstandard2.0/content/swift-binding/.template.config/template.json",
    };

    [Fact]
    public void SingleCopyUnderContent_Passes()
    {
        Assert.Empty(TemplatePackageLayout.Violations([.. PackageMetadata, .. SingleCopy]));
    }

    [Fact]
    public void DefaultContentAndContentFilesLayout_Fails()
    {
        var violations = TemplatePackageLayout.Violations([.. PackageMetadata, .. DefaultContentTargetFolders]);

        Assert.Contains(violations, v => v.Contains("expected exactly one '.template.config/template.json', found 2"));
        Assert.Contains(violations, v => v.Contains("contentFiles/any/netstandard2.0/content/swift-binding/ProjectName.csproj")
                                         && v.Contains("ships under contentFiles/"));
        Assert.Contains(violations, v => v.Contains("template file 'content/swift-binding/ProjectName.csproj' ships 2 times"));
        Assert.Contains(violations, v => v.Contains("template file 'content/swift-binding/.template.config/template.json' ships 2 times"));
    }

    [Fact]
    public void ContentFilesOnly_FailsEvenWithOneTemplate()
    {
        var violations = TemplatePackageLayout.Violations(
        [
            "contentFiles/any/netstandard2.0/content/swift-binding/ProjectName.csproj",
            "contentFiles/any/netstandard2.0/content/swift-binding/.template.config/template.json",
        ]);

        Assert.Equal(2, violations.Count);
        Assert.All(violations, v => Assert.Contains("ships under contentFiles/", v));
    }

    [Fact]
    public void SameTemplateFileUnderTwoContentFilesLanguages_Fails()
    {
        var violations = TemplatePackageLayout.Violations(
        [
            .. SingleCopy,
            "contentFiles/cs/netstandard2.0/content/swift-binding/ProjectName.csproj",
        ]);

        Assert.Contains(violations, v => v.Contains("template file 'content/swift-binding/ProjectName.csproj' ships 2 times"));
    }

    [Fact]
    public void NoTemplate_Fails()
    {
        var violations = TemplatePackageLayout.Violations(PackageMetadata);

        Assert.Equal(["expected exactly one '.template.config/template.json', found 0"], violations);
    }
}
