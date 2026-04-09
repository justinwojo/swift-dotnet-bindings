// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

public class SwiftFrameworkResolverTests
{
    [Fact]
    public void DiagnoseResolution_ReturnsAllSearchPaths()
    {
        var result = SwiftFrameworkResolver.DiagnoseResolution("TestLibrary");

        Assert.Contains("TestLibrary", result);
        Assert.Contains("@rpath/TestLibrary.framework/TestLibrary", result);
        Assert.Contains("@rpath/libTestLibrary.dylib", result);
        Assert.Contains("@rpath/TestLibrary.dylib", result);
        Assert.Contains("@executable_path/libTestLibrary.dylib", result);
        Assert.Contains("@executable_path/TestLibrary.dylib", result);
    }

    [Fact]
    public void DiagnoseResolution_ShowsOkOrFail()
    {
        var result = SwiftFrameworkResolver.DiagnoseResolution("NonExistentLibrary");

        // All paths should fail for a non-existent library
        Assert.Contains("FAIL", result);
        Assert.DoesNotContain("OK", result);
    }

    [Theory]
    [InlineData("@rpath/StoreKit.framework/StoreKit")]
    [InlineData("@executable_path/libFoo.dylib")]
    [InlineData("@loader_path/Bar.dylib")]
    [InlineData("/System/Library/Frameworks/StoreKit.framework/StoreKit")]
    public void DiagnoseResolution_DyldStylePath_TriesVerbatimOnly(string libraryName)
    {
        // Regression guard: Apple-framework target bindings emit DllImport entries
        // whose library name is already a dyld-style path. The resolver must try
        // that string verbatim and MUST NOT prepend "@rpath/{name}.framework/{name}"
        // (which would produce nonsense like "@rpath/@rpath/StoreKit.framework/...").
        var result = SwiftFrameworkResolver.DiagnoseResolution(libraryName);

        Assert.Contains(libraryName, result);
        Assert.Contains("dyld-style path", result);
        // The prefix-based candidates must not appear for dyld-style inputs.
        Assert.DoesNotContain($"@rpath/{libraryName}.framework/", result);
        Assert.DoesNotContain($"@rpath/lib{libraryName}.dylib", result);
    }

    [Theory]
    [InlineData("@rpath/StoreKit.framework/StoreKit", true)]
    [InlineData("@executable_path/libFoo.dylib", true)]
    [InlineData("@loader_path/Bar.dylib", true)]
    [InlineData("/System/Library/Frameworks/StoreKit.framework/StoreKit", true)]
    [InlineData("/absolute/path/to/libfoo.dylib", true)]
    // The detection is intentionally strict: only the three documented dyld load-command
    // tokens are accepted. Malformed @-prefixed inputs fall through to the normal search.
    [InlineData("@foo/bar", false)]
    [InlineData("@rpathtypo", false)]
    [InlineData("TestLibrary", false)]
    [InlineData("libTestLibrary", false)]
    [InlineData("", false)]
    public void IsDyldStylePath_MatchesOnlyDocumentedTokens(string libraryName, bool expected)
    {
        Assert.Equal(expected, SwiftFrameworkResolver.IsDyldStylePath(libraryName));
    }

    [Theory]
    [InlineData("@rpath/StoreKit.framework/StoreKit")]
    [InlineData("@executable_path/libFoo.dylib")]
    [InlineData("@loader_path/Bar.dylib")]
    [InlineData("/Some/Nonexistent/Absolute/Path/libfoo.dylib")]
    public void ResolveSwiftFramework_DyldStylePath_DoesNotPrefix(string libraryName)
    {
        // Direct regression guard against the double-prefix bug inside the resolver
        // hot path (not the diagnostic helper): feed a nonexistent dyld-style path
        // and assert the method returns IntPtr.Zero (deferring to the default .NET
        // resolver) rather than attempting the prefix-based candidates and loading
        // something unintended. Paired with the IsDyldStylePath_* test this pins
        // down both the detection predicate and the resolver's consumption of it.
        var result = SwiftFrameworkResolver.ResolveSwiftFramework(
            libraryName, Assembly.GetExecutingAssembly(), searchPath: null);
        Assert.Equal(IntPtr.Zero, result);
    }

    [Fact]
    public void ResolveSwiftFramework_BareName_DoesNotThrowAndFailsClean()
    {
        // Non-dyld-style bare names take the prefix-based search path. None of the
        // candidate paths exist for an unknown library, so the resolver should
        // return IntPtr.Zero (letting the .NET default resolver take over).
        var result = SwiftFrameworkResolver.ResolveSwiftFramework(
            "NonExistentLibrary_8e7a3", Assembly.GetExecutingAssembly(), searchPath: null);
        Assert.Equal(IntPtr.Zero, result);
    }
}
