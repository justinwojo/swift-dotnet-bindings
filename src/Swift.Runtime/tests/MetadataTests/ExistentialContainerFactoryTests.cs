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

    #region GetOrCreate Tests

    [Fact]
    public void GetOrCreate_WithISwiftExistentialConvertible_ReturnsFastPathContainer()
    {
        // Proxy types implement ISwiftExistentialConvertible<ExistentialContainer1> and
        // should hit the fast path in GetOrCreate (no boxing needed).
        var expectedContainer = new ExistentialContainer1
        {
            Payload0 = (IntPtr)0x42,
            Payload1 = IntPtr.Zero,
            Payload2 = IntPtr.Zero,
            ObjectMetadata = TypeMetadata.Zero
        };
        var proxy = new ExistentialConvertibleMock(expectedContainer);

        var result = ExistentialContainerFactory.GetOrCreate<IProtocolMock>(proxy);

        Assert.Equal(expectedContainer.Payload0, result.Payload0);
    }

    [Fact]
    public void GetOrCreate_WithIExistentialBoxable_CallsBoxAsExistential1()
    {
        // Concrete types that implement IExistentialBoxable should hit the second path
        // in GetOrCreate (boxing via BoxAsExistential1).
        var expectedContainer = new ExistentialContainer1
        {
            Payload0 = (IntPtr)0x99,
            Payload1 = IntPtr.Zero,
            Payload2 = IntPtr.Zero,
            ObjectMetadata = TypeMetadata.Zero
        };
        var boxable = new ExistentialBoxableMock(expectedContainer);

        var result = ExistentialContainerFactory.GetOrCreate<IProtocolMock>(boxable);

        Assert.Equal(expectedContainer.Payload0, result.Payload0);
    }

    [Fact]
    public void GetOrCreate_WithNeitherInterface_ThrowsInvalidCastException()
    {
        // Types that implement neither ISwiftExistentialConvertible nor IExistentialBoxable
        // should throw InvalidCastException from GetOrCreate.
        var plainObject = new PlainProtocolMock();

        Assert.Throws<InvalidCastException>(() =>
            ExistentialContainerFactory.GetOrCreate<IProtocolMock>(plainObject));
    }

    [Fact]
    public void GetOrCreate_WithNeitherInterface_ExceptionMessageContainsTypeName()
    {
        var plainObject = new PlainProtocolMock();

        var ex = Assert.Throws<InvalidCastException>(() =>
            ExistentialContainerFactory.GetOrCreate<IProtocolMock>(plainObject));

        Assert.Contains("PlainProtocolMock", ex.Message);
        Assert.Contains("ISwiftExistentialConvertible", ex.Message);
        Assert.Contains("IExistentialBoxable", ex.Message);
    }

    [Fact]
    public void GetOrCreate_PrefersISwiftExistentialConvertible_OverIExistentialBoxable()
    {
        // When a type implements BOTH interfaces, ISwiftExistentialConvertible should be
        // preferred (it's checked first in the implementation).
        var convertibleContainer = new ExistentialContainer1
        {
            Payload0 = (IntPtr)0xAA,
        };
        var boxableContainer = new ExistentialContainer1
        {
            Payload0 = (IntPtr)0xBB,
        };
        var dual = new DualInterfaceMock(convertibleContainer, boxableContainer);

        var result = ExistentialContainerFactory.GetOrCreate<IProtocolMock>(dual);

        // Should use the ISwiftExistentialConvertible path (0xAA), not IExistentialBoxable (0xBB)
        Assert.Equal((IntPtr)0xAA, result.Payload0);
    }

    #endregion

    #region GetOrCreate Mock Types

    /// <summary>
    /// Mock protocol interface for testing GetOrCreate.
    /// </summary>
    private interface IProtocolMock { }

    /// <summary>
    /// Mock that implements ISwiftExistentialConvertible (the proxy/fast path).
    /// </summary>
    private class ExistentialConvertibleMock : IProtocolMock, ISwiftExistentialConvertible<ExistentialContainer1>
    {
        private readonly ExistentialContainer1 _container;

        public ExistentialConvertibleMock(ExistentialContainer1 container) => _container = container;

        public ExistentialContainer1 GetExistentialContainer() => _container;
    }

    /// <summary>
    /// Mock that implements IExistentialBoxable (the concrete type boxing path).
    /// </summary>
    private class ExistentialBoxableMock : IProtocolMock, IExistentialBoxable
    {
        private readonly ExistentialContainer1 _container;

        public ExistentialBoxableMock(ExistentialContainer1 container) => _container = container;

        public ExistentialContainer1 BoxAsExistential1<TProtocol>() where TProtocol : class => _container;
    }

    /// <summary>
    /// Mock that implements the protocol interface but neither boxing interface.
    /// Used to test the InvalidCastException path.
    /// </summary>
    private class PlainProtocolMock : IProtocolMock { }

    /// <summary>
    /// Mock that implements BOTH ISwiftExistentialConvertible and IExistentialBoxable
    /// to verify priority ordering.
    /// </summary>
    private class DualInterfaceMock : IProtocolMock, ISwiftExistentialConvertible<ExistentialContainer1>, IExistentialBoxable
    {
        private readonly ExistentialContainer1 _convertibleContainer;
        private readonly ExistentialContainer1 _boxableContainer;

        public DualInterfaceMock(ExistentialContainer1 convertibleContainer, ExistentialContainer1 boxableContainer)
        {
            _convertibleContainer = convertibleContainer;
            _boxableContainer = boxableContainer;
        }

        public ExistentialContainer1 GetExistentialContainer() => _convertibleContainer;
        public ExistentialContainer1 BoxAsExistential1<TProtocol>() where TProtocol : class => _boxableContainer;
    }

    #endregion
}
