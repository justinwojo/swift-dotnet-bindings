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
            // Register test enum metadata for SwiftOptional<TestColor> tests.
            // In production, the generated module initializer does this via P/Invoke
            // to a @_cdecl Swift wrapper. In unit tests, we use Int32's metadata
            // since tests don't cross the Swift boundary.
            if (TypeMetadata.TryGetTypeMetadata<int>(out var intMd))
                TypeMetadata.RegisterMetadata(typeof(TestColor), intMd.Value);
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

    // Bool extra-inhabitant encoding tests

    [Fact]
    public static void TestBoolOptionalSomeTrue()
    {
        var optional = SwiftOptional<bool>.NewSome(true);
        Assert.Equal(SwiftOptionalCases.Some, optional.Case);
        Assert.True(optional.HasValue);
        Assert.True(optional.Some);
    }

    [Fact]
    public static void TestBoolOptionalSomeFalse()
    {
        var optional = SwiftOptional<bool>.NewSome(false);
        Assert.Equal(SwiftOptionalCases.Some, optional.Case);
        Assert.True(optional.HasValue);
        Assert.False(optional.Some);
    }

    [Fact]
    public static void TestBoolOptionalNone()
    {
        // Optional<Bool> uses extra-inhabitant encoding (1 byte total):
        // 0 = Some(false), 1 = Some(true), 2+ = None.
        // This test verifies the None case is correctly detected.
        var optional = SwiftOptional<bool>.NewNone();
        Assert.Equal(SwiftOptionalCases.None, optional.Case);
        Assert.False(optional.HasValue);
    }

    [Fact]
    public static void TestBoolOptionalFromNullable_RoundTrip()
    {
        // Round-trip: Some(true) → ToNullable → true
        var someTrue = SwiftOptional<bool>.FromNullable(true);
        Assert.True(someTrue.ToNullable());

        // Round-trip: Some(false) → ToNullable → false
        var someFalse = SwiftOptional<bool>.FromNullable(false);
        Assert.False(someFalse.ToNullable());

        // Round-trip: None → ToNullable → default(bool) = false
        var none = SwiftOptional<bool>.NewNone();
        Assert.False(none.HasValue);
    }

    // Simple enum optional tests

    private enum TestColor : int
    {
        Red = 0,
        Green = 1,
        Blue = 2,
    }

    [Fact]
    public static void TestEnumOptionalSome()
    {
        var optional = SwiftOptional<TestColor>.NewSome(TestColor.Green);
        Assert.Equal(SwiftOptionalCases.Some, optional.Case);
        Assert.True(optional.HasValue);
        Assert.Equal(TestColor.Green, optional.Some);
    }

    [Fact]
    public static void TestEnumOptionalNone()
    {
        var optional = SwiftOptional<TestColor>.NewNone();
        Assert.Equal(SwiftOptionalCases.None, optional.Case);
        Assert.False(optional.HasValue);
    }

    [Fact]
    public static void TestEnumOptionalFromNullable_RoundTrip()
    {
        var some = SwiftOptional<TestColor>.FromNullable(TestColor.Blue);
        Assert.Equal(TestColor.Blue, some.ToNullable());

        var none = SwiftOptional<TestColor>.NewNone();
        Assert.False(none.HasValue);
    }
}
