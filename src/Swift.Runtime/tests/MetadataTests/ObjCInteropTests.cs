// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

public class ObjCInteropTests
{
    [Theory]
    [InlineData("NSObject")]
    [InlineData("NSString")]
    [InlineData("NSArray")]
    [InlineData("NSDictionary")]
    public void GetTypeMetadata_ReturnsValidMetadata_ForKnownObjCClasses(string className)
    {
        var metadata = ObjCInterop.GetTypeMetadata(className);

        Assert.True(metadata.IsValid);
        Assert.NotEqual(IntPtr.Zero, metadata.Handle);
    }

    [Theory]
    [InlineData("NSURLResponse")]
    [InlineData("NSOperationQueue")]
    [InlineData("CIContext")]
    [InlineData("OS_dispatch_queue")]
    public void GetTypeMetadata_ReturnsValidMetadata_ForMigratedTypes(string className)
    {
        var metadata = ObjCInterop.GetTypeMetadata(className);

        Assert.True(metadata.IsValid);
        Assert.NotEqual(IntPtr.Zero, metadata.Handle);
    }

    [Fact]
    public void GetTypeMetadata_ThrowsForNonexistentClass()
    {
        var ex = Assert.Throws<SwiftRuntimeException>(
            () => ObjCInterop.GetTypeMetadata("NonExistentClass_XYZ_12345"));

        Assert.Contains("NonExistentClass_XYZ_12345", ex.Message);
        Assert.Contains("objc_getClass", ex.Message);
    }

    [Fact]
    public void GetTypeMetadata_ReturnsSamePointerForSameClass()
    {
        var metadata1 = ObjCInterop.GetTypeMetadata("NSObject");
        var metadata2 = ObjCInterop.GetTypeMetadata("NSObject");

        Assert.Equal(metadata1.Handle, metadata2.Handle);
    }

    [Fact]
    public void GetTypeMetadata_ReturnsDifferentPointersForDifferentClasses()
    {
        var metadata1 = ObjCInterop.GetTypeMetadata("NSObject");
        var metadata2 = ObjCInterop.GetTypeMetadata("NSString");

        Assert.NotEqual(metadata1.Handle, metadata2.Handle);
    }

    [Theory]
    [InlineData("NSURLResponse")]
    [InlineData("NSOperationQueue")]
    [InlineData("CIContext")]
    [InlineData("OS_dispatch_queue")]
    public void GetTypeMetadata_HasAccessibleSize(string className)
    {
        var metadata = ObjCInterop.GetTypeMetadata(className);

        // ObjC class metadata should report pointer size (8 bytes on 64-bit)
        Assert.True((int)metadata.Size > 0, $"Size for {className} should be positive");
    }
}
