// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Classifies public vs access-qualified <c>.swiftinterface</c> path variants.
/// Only the unqualified public surface is valid input for parsing and precompile.
/// </summary>
public class SwiftInterfaceVariantTests
{
    [Theory]
    [InlineData("arm64-apple-ios.swiftinterface")]
    [InlineData("arm64-apple-ios-simulator.swiftinterface")]
    [InlineData("/path/to/Mod.swiftmodule/x86_64-apple-ios-simulator.swiftinterface")]
    [InlineData("Foo.SWIFTINTERFACE")]
    [InlineData("/tmp/Mod/ARM64-APPLE-IOS.SWIFTINTERFACE")]
    public void IsPublic_PublicVariant_ReturnsTrue(string path)
    {
        Assert.True(SwiftInterfaceVariant.IsPublic(path));
    }

    [Theory]
    [InlineData("arm64-apple-ios.private.swiftinterface")]
    [InlineData("arm64-apple-ios.package.swiftinterface")]
    [InlineData("/path/to/Mod.swiftmodule/arm64-apple-ios.private.swiftinterface")]
    [InlineData("/path/to/Mod.swiftmodule/arm64-apple-ios.package.swiftinterface")]
    [InlineData("arm64-apple-ios.PRIVATE.swiftinterface")]
    [InlineData("arm64-apple-ios.PACKAGE.SWIFTINTERFACE")]
    public void IsPublic_AccessQualifiedVariant_ReturnsFalse(string path)
    {
        Assert.False(SwiftInterfaceVariant.IsPublic(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("arm64-apple-ios.swiftmodule")]
    [InlineData("arm64-apple-ios.swiftinterface.bak")]
    [InlineData("readme.txt")]
    [InlineData("/path/to/something")]
    public void IsPublic_NullEmptyOrNonSwiftInterface_ReturnsFalse(string? path)
    {
        Assert.False(SwiftInterfaceVariant.IsPublic(path!));
    }
}
