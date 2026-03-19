// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.InteropServices;
using Swift;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

public class SwiftSetTests : IClassFixture<SwiftSetTests.TestFixture>
{
    private readonly TestFixture _fixture;

    public SwiftSetTests(TestFixture fixture)
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
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<SwiftSet<SwiftIntMock>>();
        // sizeof (Variant)
        Assert.True(metadata.Size == 8);

        var set = new SwiftSet<SwiftIntMock>();
        int count = set.Count;
        Assert.Equal(0, count);
    }

    [Fact]
    public unsafe void SetDispose()
    {
        var set = new SwiftSet<SwiftIntMock>();
        int count = set.Count;
        Assert.Equal(0, count);
        // An empty set is singleton and its count doesn't change with new instances
        Assert.True(Arc.RetainCount(*(IntPtr*)set.Payload.DangerousGetHandle()) > 1);
    }

    [Fact]
    public void GetProtocolConformanceDescriptor_UnknownProtocol_Throws()
    {
        // .NET 10 may wrap in TargetInvocationException or throw SwiftRuntimeException directly
        var ex = Record.Exception(() =>
            ProtocolConformanceDescriptorHelper<SwiftSet<SwiftIntMock>, ITestProtocol>.GetProtocolConformanceDescriptor());
        Assert.NotNull(ex);
        if (ex is System.Reflection.TargetInvocationException tie)
            Assert.IsType<SwiftRuntimeException>(tie.InnerException);
        else
            Assert.IsType<SwiftRuntimeException>(ex);
    }

    [Fact]
    public void Contains_EmptySet_ReturnsFalse()
    {
        using var set = new SwiftSet<SwiftIntMock>();
        bool found = set.Contains(new SwiftIntMock(99));
        Assert.False(found);
    }

    [Fact]
    public void Add_SingleElement_IncreasesCount()
    {
        using var set = new SwiftSet<SwiftIntMock>();
        bool inserted = set.Add(new SwiftIntMock(42));
        Assert.True(inserted);
        int count = set.Count;
        Assert.Equal(1, count);
    }

    [Fact]
    public void Add_DuplicateElement_ReturnsFalse()
    {
        using var set = new SwiftSet<SwiftIntMock>();
        bool first = set.Add(new SwiftIntMock(42));
        Assert.True(first);
        bool second = set.Add(new SwiftIntMock(42));
        Assert.False(second);
        int count = set.Count;
        Assert.Equal(1, count);
    }

    [Fact]
    public void Contains_AfterAdd_ReturnsTrue()
    {
        using var set = new SwiftSet<SwiftIntMock>();
        set.Add(new SwiftIntMock(7));
        bool found = set.Contains(new SwiftIntMock(7));
        Assert.True(found);
        bool notFound = set.Contains(new SwiftIntMock(99));
        Assert.False(notFound);
    }

    [Fact]
    public void Remove_ExistingElement_ReturnsTrue()
    {
        using var set = new SwiftSet<SwiftIntMock>();
        set.Add(new SwiftIntMock(42));
        bool removed = set.Remove(new SwiftIntMock(42));
        Assert.True(removed);
        int count = set.Count;
        Assert.Equal(0, count);
    }

    [Fact]
    public void Remove_NonExistingElement_ReturnsFalse()
    {
        using var set = new SwiftSet<SwiftIntMock>();
        set.Add(new SwiftIntMock(42));
        bool removed = set.Remove(new SwiftIntMock(99));
        Assert.False(removed);
        int count = set.Count;
        Assert.Equal(1, count);
    }

    [Fact]
    public void RemoveAll_ClearsSet()
    {
        using var set = new SwiftSet<SwiftIntMock>();
        set.Add(new SwiftIntMock(1));
        set.Add(new SwiftIntMock(2));
        set.Add(new SwiftIntMock(3));
        int countBefore = set.Count;
        Assert.Equal(3, countBefore);
        set.RemoveAll();
        int countAfter = set.Count;
        Assert.Equal(0, countAfter);
    }

    [Fact]
    public void GetEnumerator_EmptySet_NoElements()
    {
        using var set = new SwiftSet<SwiftIntMock>();
        var list = set.ToList();
        int count = list.Count;
        Assert.Equal(0, count);
    }

    [Fact]
    public void GetEnumerator_NonEmptySet_ReturnsAllElements()
    {
        using var set = new SwiftSet<SwiftIntMock>();
        set.Add(new SwiftIntMock(10));
        set.Add(new SwiftIntMock(20));
        set.Add(new SwiftIntMock(30));

        var values = new HashSet<int>();
        foreach (var elem in set)
        {
            values.Add(elem.Value);
        }
        Assert.Contains(10, values);
        Assert.Contains(20, values);
        Assert.Contains(30, values);
        int count = values.Count;
        Assert.Equal(3, count);
    }

    [Fact]
    public void FromEnumerable_CreatesSet()
    {
        var source = new List<SwiftIntMock>
        {
            new SwiftIntMock(1),
            new SwiftIntMock(2),
            new SwiftIntMock(3),
            new SwiftIntMock(2), // duplicate
        };
        using var set = SwiftSet<SwiftIntMock>.FromEnumerable(source);
        int count = set.Count;
        Assert.Equal(3, count);
        bool has1 = set.Contains(new SwiftIntMock(1));
        bool has2 = set.Contains(new SwiftIntMock(2));
        bool has3 = set.Contains(new SwiftIntMock(3));
        Assert.True(has1);
        Assert.True(has2);
        Assert.True(has3);
    }

    [Fact]
    public void ToArray_ReturnsAllElements()
    {
        using var set = new SwiftSet<SwiftIntMock>();
        set.Add(new SwiftIntMock(5));
        set.Add(new SwiftIntMock(10));
        set.Add(new SwiftIntMock(15));
        var array = set.ToArray();
        int length = array.Length;
        Assert.Equal(3, length);
        var values = new HashSet<int>(array.Select(e => e.Value));
        Assert.Contains(5, values);
        Assert.Contains(10, values);
        Assert.Contains(15, values);
    }

    [Fact]
    public void Constructor_FromEnumerable()
    {
        var source = new List<SwiftIntMock>
        {
            new SwiftIntMock(100),
            new SwiftIntMock(200),
        };
        using var set = new SwiftSet<SwiftIntMock>(source);
        int count = set.Count;
        Assert.Equal(2, count);
        bool has100 = set.Contains(new SwiftIntMock(100));
        bool has200 = set.Contains(new SwiftIntMock(200));
        Assert.True(has100);
        Assert.True(has200);
    }

    [Fact]
    public void LargeSet_StressTest()
    {
        using var set = new SwiftSet<SwiftIntMock>();
        for (int i = 0; i < 1000; i++)
        {
            bool inserted = set.Add(new SwiftIntMock(i));
            Assert.True(inserted);
        }
        int count = set.Count;
        Assert.Equal(1000, count);

        for (int i = 0; i < 1000; i++)
        {
            bool found = set.Contains(new SwiftIntMock(i));
            Assert.True(found);
        }

        bool notFound = set.Contains(new SwiftIntMock(9999));
        Assert.False(notFound);

        var elements = set.ToArray();
        int length = elements.Length;
        Assert.Equal(1000, length);
    }

    [Fact]
    public void ToString_ReturnsExpectedFormat()
    {
        using var set = new SwiftSet<SwiftIntMock>();
        set.Add(new SwiftIntMock(1));
        set.Add(new SwiftIntMock(2));
        string str = set.ToString();
        Assert.Equal("SwiftSet<SwiftIntMock>[2]", str);
    }
}
