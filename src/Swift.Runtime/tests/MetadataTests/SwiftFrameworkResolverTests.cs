// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

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
}
