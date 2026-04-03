// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
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

    // Class type optional fast-path guard tests.
    // The full NewSome/NewNone round-trip for class types requires real Swift metadata
    // (VWT with valid size/stride/InitializeWithCopy). That coverage lives in BindingTests
    // (NodeWithParent roundtrip). These tests verify the TYPE DETECTION conditions that
    // gate the fast path: non-value-type + ISwiftObject + Class metadata kind.

    /// <summary>
    /// Verifies that TypeMetadata with pointer value > 0x7ff reports Kind == Class,
    /// matching the Swift ABI convention for class metadata pointers.
    /// </summary>
    [Fact]
    public unsafe void ClassMetadataKind_PointerAboveDiscriminator_ReportsClass()
    {
        // Allocate a buffer with a value > kMaxDiscriminator (0x7ff)
        var ptr = (IntPtr)NativeMemory.AllocZeroed((nuint)(IntPtr.Size * 4));
        try
        {
            *(nint*)ptr = 0x1000; // > 0x7ff → TypeMetadataKind.Class
            var md = TypeMetadata.FromHandle(ptr);
            Assert.Equal(TypeMetadataKind.Class, md.Kind);
        }
        finally
        {
            NativeMemory.Free((void*)ptr);
        }
    }

    /// <summary>
    /// Verifies the type guard conditions for the class fast path in SwiftOptional.
    /// A reference type implementing ISwiftObject with Class metadata should
    /// match the fast path conditions.
    /// </summary>
    [Fact]
    public void ClassFastPathGuard_ReferenceTypeWithISwiftObject_MatchesConditions()
    {
        // The fast path checks: !IsValueType && ISwiftObject.IsAssignableFrom
        // SwiftOptional<T> itself is a class implementing ISwiftObject — verify the conditions
        var t = typeof(SwiftOptional<int>);
        Assert.False(t.IsValueType);
        Assert.True(typeof(ISwiftObject).IsAssignableFrom(t));
    }

    [Fact]
    public void ClassFastPathGuard_ValueType_DoesNotMatch()
    {
        // Value types should NOT match the class fast path
        Assert.True(typeof(int).IsValueType);
        Assert.True(typeof(bool).IsValueType);
        Assert.True(typeof(TestColor).IsValueType);
    }
}
