// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Swift;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

public class SwiftOptionalTests : IClassFixture<SwiftOptionalTests.TestFixture>
{
    private readonly TestFixture _fixture;

    public SwiftOptionalTests(TestFixture fixture)
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
    public static void TestIntOptionalSome()
    {
        var optional = SwiftOptional<int>.NewSome(42);
        Assert.Equal(SwiftOptionalCases.Some, optional.Case);
        Assert.Equal(42, optional.Some);
    }

    [Fact]
    public static void TestIntOptionalNone()
    {
        var optional = SwiftOptional<int>.NewNone();
        Assert.Equal(SwiftOptionalCases.None, optional.Case);
    }

    [Fact]
    public static void TestOptionIntOptionSome()
    {
        var opt0 = SwiftOptional<int>.NewSome(42);
        var opt1 = SwiftOptional<SwiftOptional<int>>.NewSome(opt0);
        Assert.Equal(SwiftOptionalCases.Some, opt1.Case);
        Assert.True(opt1.HasValue);
        var opt2 = opt1.Value;
        Assert.NotNull(opt2);
        Assert.Equal(SwiftOptionalCases.Some, opt2?.Case);
        Assert.Equal(42, opt2?.Some);
    }

    [Fact]
    public static void TestHasValueAndValue_OnSome()
    {
        var optional = SwiftOptional<int>.NewSome(123);
        Assert.True(optional.HasValue);
        Assert.Equal(123, optional.Value);
        Assert.Equal(123, optional.ToNullable());
    }

    [Fact]
    public static void TestHasValueAndValue_OnNone()
    {
        var optional = SwiftOptional<int>.NewNone();
        Assert.False(optional.HasValue);
        Assert.Equal(0, optional.Value);
        Assert.Equal(0, optional.ToNullable());
    }

    [Fact]
    public static void TestSome_ThrowsOnNone()
    {
        var optional = SwiftOptional<int>.NewNone();
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = optional.Some;
        });
    }

    [Fact]
    public static void FromNullable_ValueType_ReturnsSome()
    {
        var value = 42;
        var optional = SwiftOptional<int>.FromNullable(value);
        Assert.Equal(SwiftOptionalCases.Some, optional.Case);
        Assert.Equal(42, optional.Some);
    }

    [Fact]
    public static void ImplicitConversions_RoundTrip()
    {
        SwiftOptional<int> optional = 7;
        Assert.Equal(SwiftOptionalCases.Some, optional.Case);
        int? roundTrip = optional;
        Assert.Equal(7, roundTrip);
    }

    [Fact]
    public static void ImplicitConversion_NullOptional_ValueType_ReturnsDefault()
    {
        SwiftOptional<int>? optional = null;
        int? value = optional!;
        Assert.Equal(0, value);
    }
}
