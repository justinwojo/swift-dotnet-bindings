// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

// ═══════════════════════════════════════════════════════════════════════
// BindingsGeneratorCommand: small CLI-level helpers
// ═══════════════════════════════════════════════════════════════════════

#region IsSystemFrameworkTarget gate

/// <summary>
/// The direct-mode wrapper-compile and csproj-emit branches in
/// <see cref="BindingsGeneratorCommand.Execute"/> are gated on
/// <c>IsSystemFrameworkTarget</c>. The gate must say "yes" only when
/// the user is targeting an Apple system framework via <c>-l @rpath/...</c>
/// or <c>-l /System/Library/...</c> AND has not supplied an
/// <c>--xcframework</c>. Anything else (xcframework mode, plain user
/// dylibs, missing library name) must keep the legacy "emit C# only,
/// don't try to package or compile a wrapper" behavior — that contract
/// is what keeps non-system manual workflows from breaking.
/// </summary>
public class IsSystemFrameworkTargetTests
{
    [Theory]
    [InlineData("@rpath/StoreKit.framework/StoreKit")]
    [InlineData("@rpath/Foundation.framework/Foundation")]
    [InlineData("/System/Library/Frameworks/StoreKit.framework/StoreKit")]
    [InlineData("/System/Library/PrivateFrameworks/Whatever.framework/Whatever")]
    public void RecognizesSystemFrameworkLibraryNames(string libraryName)
    {
        Assert.True(BindingsGeneratorCommand.IsSystemFrameworkTarget(hasXcframework: false, libraryName));
    }

    [Theory]
    [InlineData("/usr/local/lib/libfoo.dylib")]
    [InlineData("libfoo.dylib")]
    [InlineData("MyCustomLib")]
    [InlineData("/Users/me/build/MyLib.framework/MyLib")]
    public void RejectsNonSystemLibraryNames(string libraryName)
    {
        Assert.False(BindingsGeneratorCommand.IsSystemFrameworkTarget(hasXcframework: false, libraryName));
    }

    [Fact]
    public void XcframeworkAlwaysWinsOverLibraryName()
    {
        // --xcframework mode is the canonical packaged-binary path; even if the
        // user *also* passes -l @rpath/... we must stay in xcframework mode and
        // not flip into the direct-mode system-framework branch (which would
        // emit a system-framework csproj over the top of an xcframework one).
        Assert.False(BindingsGeneratorCommand.IsSystemFrameworkTarget(
            hasXcframework: true,
            libraryName: "@rpath/StoreKit.framework/StoreKit"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmptyLibraryNameIsNotSystemFramework(string? libraryName)
    {
        Assert.False(BindingsGeneratorCommand.IsSystemFrameworkTarget(hasXcframework: false, libraryName));
    }
}

#endregion
