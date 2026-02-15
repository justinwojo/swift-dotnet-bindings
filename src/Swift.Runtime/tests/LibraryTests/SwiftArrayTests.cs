// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Swift;
using Swift.Runtime;
using Xunit;

// Disable xUnit collection size analyzer - we're testing Count property directly
#pragma warning disable xUnit2013

namespace BindingsGeneration.Tests;

public class SwiftArrayTests : IClassFixture<SwiftArrayTests.TestFixture>
{
    private readonly TestFixture _fixture;

    public SwiftArrayTests(TestFixture fixture)
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
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<SwiftArray<int>>();
        // sizeof(ArrayBuffer)
        Assert.Equal((nuint)8, metadata.Size);

        var array = new SwiftArray<int>();
        Assert.Equal(0, array.Count);
    }

    [Fact]
    public void Append()
    {
        var array = new SwiftArray<int>();
        array.Append(42);
        array.Append(17);
        var c = array.Count;
        Assert.Equal(2, c);
        Assert.Equal(17, array[1]);
    }

    [Fact]
    public void Insert()
    {
        var array = new SwiftArray<int>();
        array.Append(42);
        array.Append(17);
        var c = array.Count;
        Assert.Equal(2, c);
        array.Insert(1, 99);
        c = array.Count;
        Assert.Equal(3, c);
        Assert.Equal(17, array[2]);
    }

    [Fact]
    public void Replace()
    {
        var array = new SwiftArray<int>();
        array.Append(42);
        array.Append(17);
        var c = array.Count;
        Assert.Equal(2, c);
        Assert.Equal(17, array[1]);
        array[1] = 99;
        Assert.Equal(99, array[1]);
    }

    [Fact]
    public void Remove()
    {
        var array = new SwiftArray<int>();
        array.Append(42);
        array.Append(17);
        var c = array.Count;
        Assert.Equal(2, c);
        array.Remove(0);
        c = array.Count;
        Assert.Equal(1, c);
        Assert.Equal(17, array[0]);
    }

    [Fact]
    public void Clear()
    {
        var array = new SwiftArray<int>();
        array.Append(42);
        array.Append(17);
        var c = array.Count;
        Assert.Equal(2, c);
        array.RemoveAll();
        c = array.Count;
        Assert.Equal(0, c);
    }

    [Fact]
    public void LargeArray()
    {
        var array = new SwiftArray<int>();
        const int count = 1000000;
        for (int i = 0; i < count; i++)
        {
            array.Append(i);
        }
        Assert.Equal(count, array.Count);

        Assert.Equal(0, array[0]);
        Assert.Equal(1, array[1]);
        Assert.Equal(999999, array[count - 1]);
    }

    [Fact]
    public void FromEnumerable_Null_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => SwiftArray<int>.FromEnumerable(null!));
    }

    [Fact]
    public void FromEnumerable_CopiesItemsInOrder()
    {
        var source = new List<int> { 3, 1, 4 };
        var array = SwiftArray<int>.FromEnumerable(source);

        Assert.Equal(3, array.Count);
        Assert.Equal(3, array[0]);
        Assert.Equal(1, array[1]);
        Assert.Equal(4, array[2]);
    }

    [Fact]
    public void Enumerator_IteratesInOrder()
    {
        var array = new SwiftArray<int>();
        array.Append(10);
        array.Append(20);
        array.Append(30);

        var results = new List<int>();
        foreach (var item in array)
        {
            results.Add(item);
        }

        Assert.Equal(new List<int> { 10, 20, 30 }, results);
    }

    [Fact]
    public unsafe void ArrayDispose()
    {
        var array = new SwiftArray<int>();
        Assert.Equal(0, array.Count);
        // An empty array is singleton and it's count doesn't change with new instances
        // https://github.com/swiftlang/swift/blob/50a98d3055e5a636d80c376a99b4eea35387cd0d/stdlib/public/SwiftShims/swift/shims/GlobalObjects.h#L44
        Assert.True(Arc.RetainCount(*(IntPtr*)array.Payload.DangerousGetHandle()) > 1);

        array.Append(42);
        Assert.Equal(1, array.Count);
        Assert.Equal(1, Arc.RetainCount(*(IntPtr*)array.Payload.DangerousGetHandle()));
        Arc.Retain(*(IntPtr*)array.Payload.DangerousGetHandle());
        Assert.Equal(2, Arc.RetainCount(*(IntPtr*)array.Payload.DangerousGetHandle()));

        var handle = *(IntPtr*)array.Payload.DangerousGetHandle();
        array.Payload.Dispose();
        Assert.Equal(1, Arc.RetainCount(handle));
        Arc.Release(handle);
    }

    [Fact]
    public void GetProtocolConformanceDescriptor_UnknownProtocol_Throws()
    {
        Assert.Throws<SwiftRuntimeException>(() =>
            ProtocolConformanceDescriptorHelper<SwiftArray<int>, ITestProtocol>.GetProtocolConformanceDescriptor());
    }

    private void PrimitiveArrayTest<T>(T value1, T value2, T overwriteValue)
    {
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<SwiftArray<T>>();
        Assert.True(metadata.Size > 0);

        var array = new SwiftArray<T>();
        Assert.Equal(0, array.Count);

        array.Append(value1);
        Assert.Equal(1, array.Count);
        Assert.Equal(value1, array[0]);

        array.Append(value2);
        Assert.Equal(2, array.Count);
        Assert.Equal(value2, array[1]);

        array[0] = overwriteValue;
        Assert.Equal(overwriteValue, array[0]);

        array.Insert(1, value1);
        Assert.Equal(3, array.Count);

        array.Remove(1);
        Assert.Equal(2, array.Count);

        Assert.Equal(overwriteValue, array[0]);
        Assert.Equal(value2, array[1]);

        array.RemoveAll();
        Assert.Equal(0, array.Count);
    }

    [Fact] public void ArrayTestSByte() => PrimitiveArrayTest<sbyte>(42, 17, 100);
    [Fact] public void ArrayTestByte() => PrimitiveArrayTest<byte>(42, 17, 100);
    [Fact] public void ArrayTestShort() => PrimitiveArrayTest<short>(42, 17, 100);
    [Fact] public void ArrayTestUShort() => PrimitiveArrayTest<ushort>(42, 17, 100);
    [Fact] public void ArrayTestInt() => PrimitiveArrayTest<int>(42, 17, 100);
    [Fact] public void ArrayTestUInt() => PrimitiveArrayTest<uint>(42, 17, 100);
    [Fact] public void ArrayTestLong() => PrimitiveArrayTest<long>(42, 17, 100);
    [Fact] public void ArrayTestULong() => PrimitiveArrayTest<ulong>(42, 17, 100);
    [Fact] public void ArrayTestFloat() => PrimitiveArrayTest<float>(4.2f, 1.7f, 10.0f);
    [Fact] public void ArrayTestDouble() => PrimitiveArrayTest<double>(4.2, 1.7, 10.0);
    [Fact] public void ArrayTestBool() => PrimitiveArrayTest<bool>(true, false, true);

    [Fact]
    public void ToArray_Empty_ReturnsEmptyArray()
    {
        var array = new SwiftArray<int>();
        var result = array.ToArray();
        Assert.Empty(result);
    }

    [Fact]
    public void ToArray_PreservesOrderAndValues()
    {
        var array = new SwiftArray<int>();
        array.Append(10);
        array.Append(20);
        array.Append(30);
        Assert.Equal(new[] { 10, 20, 30 }, array.ToArray());
    }

    [Fact]
    public void ToList_Empty_ReturnsEmptyList()
    {
        var array = new SwiftArray<int>();
        var result = array.ToList();
        Assert.Empty(result);
    }

    [Fact]
    public void ToList_PreservesOrderAndValues()
    {
        var array = new SwiftArray<int>();
        array.Append(5);
        array.Append(15);
        array.Append(25);
        Assert.Equal(new List<int> { 5, 15, 25 }, array.ToList());
    }

    [Fact]
    public void ToString_Empty_ReturnsFormattedString()
    {
        var array = new SwiftArray<int>();
        Assert.Equal("SwiftArray<Int32>[0]", array.ToString());
    }

    [Fact]
    public void ToString_NonEmpty_ReturnsFormattedString()
    {
        var array = new SwiftArray<int>();
        array.Append(1);
        array.Append(2);
        Assert.Equal("SwiftArray<Int32>[2]", array.ToString());
    }

    [Fact]
    public void ToArray_WithSwiftString_PreservesValues()
    {
        var array = new SwiftArray<SwiftString>();
        array.Append(new SwiftString("Hello"));
        array.Append(new SwiftString("World"));

        var result = array.ToArray();
        Assert.Equal(2, result.Length);
        Assert.Equal("Hello", result[0].ToString());
        Assert.Equal("World", result[1].ToString());
    }

    [Fact]
    public void ToList_WithSwiftString_PreservesValues()
    {
        var array = new SwiftArray<SwiftString>();
        array.Append(new SwiftString("Foo"));
        array.Append(new SwiftString("Bar"));

        var result = array.ToList();
        Assert.Equal(2, result.Count);
        Assert.Equal("Foo", result[0].ToString());
        Assert.Equal("Bar", result[1].ToString());
    }

    [Fact]
    public void Constructor_FromArray_CopiesElements()
    {
        var array = new SwiftArray<int>(new[] { 1, 2, 3 });
        Assert.Equal(3, array.Count);
        Assert.Equal(1, array[0]);
        Assert.Equal(2, array[1]);
        Assert.Equal(3, array[2]);
    }

    [Fact]
    public void Constructor_FromEnumerable_CopiesElements()
    {
        var list = new List<int> { 10, 20, 30 };
        var array = new SwiftArray<int>(list);
        Assert.Equal(3, array.Count);
        Assert.Equal(10, array[0]);
        Assert.Equal(20, array[1]);
        Assert.Equal(30, array[2]);
    }

    [Fact]
    public void Constructor_NullArray_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SwiftArray<int>((int[])null!));
    }

    [Fact]
    public void Constructor_NullEnumerable_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SwiftArray<int>((IEnumerable<int>)null!));
    }

    [Fact]
    public void ImplicitConversion_FromArray()
    {
        SwiftArray<int> array = new[] { 1, 2, 3 };
        Assert.Equal(3, array.Count);
        Assert.Equal(1, array[0]);
        Assert.Equal(2, array[1]);
        Assert.Equal(3, array[2]);
    }

    [Fact]
    public void ImplicitConversion_NullArray_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            SwiftArray<int> array = (int[])null!;
        });
    }

    [Fact]
    public void Indexer_NegativeIndex_Throws()
    {
        var array = new SwiftArray<int>(new[] { 1, 2, 3 });
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = array[-1]);
    }

    [Fact]
    public void Indexer_IndexEqualToCount_Throws()
    {
        var array = new SwiftArray<int>(new[] { 1, 2, 3 });
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = array[3]);
    }

    [Fact]
    public void Indexer_SetNegativeIndex_Throws()
    {
        var array = new SwiftArray<int>(new[] { 1, 2, 3 });
        Assert.Throws<ArgumentOutOfRangeException>(() => array[-1] = 99);
    }

    [Fact]
    public void Indexer_SetIndexEqualToCount_Throws()
    {
        var array = new SwiftArray<int>(new[] { 1, 2, 3 });
        Assert.Throws<ArgumentOutOfRangeException>(() => array[3] = 99);
    }

    [Fact]
    public void IListIndexOf_FindsElement()
    {
        var array = new SwiftArray<int>(new[] { 10, 20, 30 });
        Assert.Equal(1, ((IList<int>)array).IndexOf(20));
    }

    [Fact]
    public void IListContains_FindsElement()
    {
        var array = new SwiftArray<int>(new[] { 10, 20, 30 });
        Assert.True(((ICollection<int>)array).Contains(20));
        Assert.False(((ICollection<int>)array).Contains(99));
    }

    [Fact]
    public void IListCopyTo_CopiesElements()
    {
        var array = new SwiftArray<int>(new[] { 10, 20, 30 });
        var dest = new int[5];
        ((ICollection<int>)array).CopyTo(dest, 1);
        Assert.Equal(new[] { 0, 10, 20, 30, 0 }, dest);
    }

    [Fact]
    public void AsProjected_NullSelector_Throws()
    {
        var array = new SwiftArray<int>(new[] { 1, 2, 3 });
        Assert.Throws<ArgumentNullException>(() => array.AsProjected<string>(null!));
    }

    [Fact]
    public void ArrayTestString()
    {
        var value1 = new SwiftString("Hello");
        var value2 = new SwiftString("World");
        var overwriteValue = new SwiftString("String");
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<SwiftArray<SwiftString>>();
        Assert.True(metadata.Size > 0);

        var array = new SwiftArray<SwiftString>();
        Assert.Equal(0, array.Count);

        array.Append(value1);
        Assert.Equal(1, array.Count);
        Assert.Equal(value1.ToString(), array[0].ToString());

        array.Append(value2);
        Assert.Equal(2, array.Count);
        Assert.Equal(value2.ToString(), array[1].ToString());

        array[0] = overwriteValue;
        Assert.Equal(overwriteValue.ToString(), array[0].ToString());

        array.Insert(1, value1);
        Assert.Equal(3, array.Count);

        array.Remove(1);
        Assert.Equal(2, array.Count);

        Assert.Equal(overwriteValue.ToString(), array[0].ToString());
        Assert.Equal(value2.ToString(), array[1].ToString());

        array.RemoveAll();
        Assert.Equal(0, array.Count);
    }
}
