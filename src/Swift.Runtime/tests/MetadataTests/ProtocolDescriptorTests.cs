// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Swift;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

public class ProtocolDescriptorTests : IClassFixture<ProtocolDescriptorTests.TestFixture>
{
    private readonly TestFixture _fixture;

    public ProtocolDescriptorTests(TestFixture fixture)
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
    public static void RetrieveIHashableProtocolDescriptor()
    {
        ProtocolDescriptor.TryGet<IHashable>(out var protocolDescriptor);
        Assert.True(protocolDescriptor.HasValue && protocolDescriptor.Value.IsValid);
    }
}
