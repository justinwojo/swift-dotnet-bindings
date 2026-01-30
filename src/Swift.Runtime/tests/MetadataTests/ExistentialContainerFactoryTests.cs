// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

public class ExistentialContainerFactoryTests
{
    [Fact]
    public void CreateAny_CreatesContainerWithMetadata()
    {
        var value = new SwiftIntMock(42);
        var container = ExistentialContainerFactory.CreateAny(value);

        Assert.True(container.ObjectMetadata.IsValid);
        Assert.Equal(0, container.Count);
    }

    [Fact]
    public void Create_WithSingleProtocol_CreatesContainerWithWitnessTable()
    {
        var value = new SwiftIntMock(42);
        var container = ExistentialContainerFactory.Create<SwiftIntMock, ISwiftHashable>(value);

        Assert.True(container.ObjectMetadata.IsValid);
        Assert.Equal(1, container.Count);
        Assert.NotEqual(IntPtr.Zero, container[0]); // Witness table should be populated
    }

    [Fact]
    public void Create_WithSingleProtocol_ThrowsWhenTypeDoesNotConform()
    {
        var value = new AnyTypeMock();

        // AnyTypeMock returns invalid protocol conformance descriptors
        Assert.ThrowsAny<Exception>(() =>
            ExistentialContainerFactory.Create<AnyTypeMock, ISwiftHashable>(value));
    }

    [Fact]
    public void CreateWithWitnessTables_ZeroTables_CreatesContainer0()
    {
        var value = new SwiftIntMock(42);
        var container = ExistentialContainerFactory.CreateWithWitnessTables(value);

        Assert.IsType<ExistentialContainer0>(container);
        Assert.True(container.ObjectMetadata.IsValid);
        Assert.Equal(0, container.Count);
    }

    [Fact]
    public void CreateWithWitnessTables_OneTable_CreatesContainer1()
    {
        var value = new SwiftIntMock(42);
        var witnessTable = ProtocolWitnessTable.GetOrThrow<SwiftIntMock, ISwiftHashable>();
        var container = ExistentialContainerFactory.CreateWithWitnessTables(value, witnessTable);

        Assert.IsType<ExistentialContainer1>(container);
        Assert.True(container.ObjectMetadata.IsValid);
        Assert.Equal(1, container.Count);
        Assert.Equal(witnessTable.Handle, container[0]);
    }

    [Fact]
    public void CreateWithWitnessTables_TooManyTables_ThrowsArgumentException()
    {
        var value = new SwiftIntMock(42);
        var witnessTable = ProtocolWitnessTable.GetOrThrow<SwiftIntMock, ISwiftHashable>();

        // Create array with 9 tables (more than max of 8)
        var tables = Enumerable.Repeat(witnessTable, 9).ToArray();

        Assert.Throws<ArgumentException>(() =>
            ExistentialContainerFactory.CreateWithWitnessTables(value, tables));
    }

    [Fact]
    public void Container_CopyTo_CopiesAllFields()
    {
        var value = new SwiftIntMock(42);
        var container1 = ExistentialContainerFactory.Create<SwiftIntMock, ISwiftHashable>(value);

        var container2 = new ExistentialContainer1();
        container1.CopyTo(ref container2);

        Assert.Equal(container1.ObjectMetadata, container2.ObjectMetadata);
        Assert.Equal(container1.Payload0, container2.Payload0);
        Assert.Equal(container1[0], container2[0]);
    }

    [Fact]
    public void MaxInlinePayloadSize_IsCorrect()
    {
        // On 64-bit systems, 3 * 8 = 24 bytes
        Assert.Equal(ExistentialContainerFactory.MaxInlinePayloadSize, 3 * IntPtr.Size);
    }
}
