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

    static void SmokeTest<T>()
    {
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<SwiftSet<T>>();
        Assert.True(metadata.Size > 0);

        var set = new SwiftSet<T>();
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public unsafe void SetDispose()
    {
        var array = new SwiftSet<int>();
        var payload = *(IntPtr*)array.Payload.rawValue;

        // Retain the payload to ensure it stays alive after the dispose
        Arc.Retain(payload);
        var count = Arc.RetainCount(payload);

        array.Dispose();

        Assert.Equal(count - 1, Arc.RetainCount(payload));
        // Release the payload after the assertion
        Arc.Release(payload);
    }

    [Fact] public void SetTestSByte() => SmokeTest<sbyte>();
    [Fact] public void SetTestByte() => SmokeTest<byte>();
    [Fact] public void SetTestShort() => SmokeTest<short>();
    [Fact] public void SetTestUShort() => SmokeTest<ushort>();
    [Fact] public void SetTestInt() => SmokeTest<int>();
    [Fact] public void SetTestUInt() => SmokeTest<uint>();
    [Fact] public void SetTestLong() => SmokeTest<long>();
    [Fact] public void SetTestULong() => SmokeTest<ulong>();
    [Fact] public void SetTestFloat() => SmokeTest<float>();
    [Fact] public void SetTestDouble() => SmokeTest<double>();
    [Fact] public void SetTestBool() => SmokeTest<bool>();
}
