// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Swift;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

public static unsafe class MemoryExtensions
{
    /// <summary>
    /// Reads a pointer stored at the given index.
    /// </summary>
    public static IntPtr At(this IntPtr ptr, int index)
    {
        byte* bytePtr = (byte*)ptr.ToPointer();
        return *(IntPtr*)(bytePtr + index * IntPtr.Size);
    }
}

public class SwiftStringTests : IClassFixture<SwiftStringTests.TestFixture>
{
    private readonly TestFixture _fixture;

    public SwiftStringTests(TestFixture fixture)
    {
        _fixture = fixture;
    }

    public class TestFixture
    {
        static TestFixture()
        {
        }

        private static void InitializeResources()
        {
        }
    }

    [Fact]
    public void SmokeTest()
    {
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<SwiftString>();
        // sizeof(Data)
        Assert.Equal((nuint)16, metadata.Size);

        var str = new SwiftString(string.Empty);
        Assert.Equal(0, str.Length);

        string text = "Hello world!";
        str = new SwiftString(text);

        Assert.Equal(text.Length, str.Length);
        Assert.Equal(text, str.ToString());
    }

    [Fact]
    public void ImplicitConversion_FromString_Works()
    {
        SwiftString swiftString = "Hello";
        Assert.Equal("Hello", swiftString.ToString());
    }

    [Fact]
    public void ImplicitConversion_ToString_Works()
    {
        var swiftString = new SwiftString("World");
        string value = swiftString;
        Assert.Equal("World", value);
    }

    [Fact]
    public void ImplicitConversion_FromNullString_Throws()
    {
        string? value = null;
        Assert.Throws<ArgumentNullException>(() =>
        {
            SwiftString _ = (string)value!;
        });
    }

    [Fact]
    public void ImplicitConversion_FromNullSwiftString_Throws()
    {
        SwiftString? value = null;
        Assert.Throws<ArgumentNullException>(() =>
        {
            string _ = value!;
        });
    }
}
