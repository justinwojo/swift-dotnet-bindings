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
    public void Create_UsesDirectPathWithoutReflection()
    {
        // Create now uses GetOrThrowDirect (no MakeGenericType) internally.
        // This test verifies the full path works end-to-end.
        var value = new SwiftIntMock(42);
        var container = ExistentialContainerFactory.Create<SwiftIntMock, ISwiftHashable>(value);

        Assert.True(container.ObjectMetadata.IsValid);
        Assert.Equal(1, container.Count);
        Assert.NotEqual(IntPtr.Zero, container[0]);

        // Verify result matches what GetOrThrowDirect returns
        var expectedWitness = ProtocolWitnessTable.GetOrThrowDirect<SwiftIntMock, ISwiftHashable>();
        Assert.Equal(expectedWitness.Handle, container[0]);
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

    [Fact]
    public void GetOrCreate_WithAlreadyMarshalledContainer_RoundTripsAsBorrowed()
    {
        // A degraded `object` PAT-existential getter hands out the raw ExistentialContainer1 it
        // marshalled from Swift. Reading it as `object` and feeding it straight back to a settable
        // property's setter routes through GetOrCreate<object> — the container is neither a proxy
        // (ISwiftExistentialConvertible) nor a boxable conformer (IExistentialBoxable), so without the
        // round-trip branch this would throw InvalidCastException (the MutableAttributeHolder.Current
        // round-trip failure). The container must pass straight through as a BORROWED container: the
        // boxed value still owns the payload's +1, so the setter must not destroy it.
        var marshalled = new ExistentialContainer1
        {
            Payload0 = (IntPtr)0x1234,
            Payload1 = (IntPtr)0x5678,
            Payload2 = IntPtr.Zero,
            ObjectMetadata = TypeMetadata.Zero
        };

        object boxed = marshalled;
        var result = ExistentialContainerFactory.GetOrCreate<object>(boxed, out var ownsContainer, out var keepAlive);

        Assert.Equal(marshalled.Payload0, result.Payload0);
        Assert.Equal(marshalled.Payload1, result.Payload1);
        Assert.False(ownsContainer, "Round-tripped container is borrowed — the boxed value owns the +1, the setter must not destroy it.");
        Assert.Same(boxed, keepAlive);
    }

    #endregion

    #region GetOrCreateConsumerOwned Tests

    [Fact]
    public void GetOrCreateConsumerOwned_AutoWrap_ReusesOneCarrierWhileImplLives()
    {
        // The consumer-owned lane still has to be a memo: assigning the same implementation to the
        // same non-retaining slot twice must not mint a second Swift box.
        var impl = new PlainProtocolMock();
        var wrapCount = 0;

        var first = ExistentialContainerFactory.GetOrCreateConsumerOwned<IProtocolMock>(
            impl, v => { wrapCount++; return new ConsumerOwnedProxyMock(v, (IntPtr)0x11); },
            out var ownsFirst, out var keepAliveFirst);
        var second = ExistentialContainerFactory.GetOrCreateConsumerOwned<IProtocolMock>(
            impl, v => { wrapCount++; return new ConsumerOwnedProxyMock(v, (IntPtr)0x22); },
            out var ownsSecond, out var keepAliveSecond);

        Assert.Equal(1, wrapCount);
        Assert.Same(keepAliveFirst, keepAliveSecond);
        Assert.Equal(first.Payload0, second.Payload0);
        // An auto-wrapped proxy is borrowed: it owns the +1 itself, so the setter must not destroy
        // the container it hands back.
        Assert.False(ownsFirst);
        Assert.False(ownsSecond);
        GC.KeepAlive(impl);
    }

    [Fact]
    public void GetOrCreateConsumerOwned_KeepsItsOwnMemo_SoOneImplCanCarryBothLanes()
    {
        // One implementation assigned to BOTH a retaining and a non-retaining Swift slot needs two
        // carriers: they have opposite ownership, so a shared memo would force one lane's rooting
        // onto the other. Distinct carriers is the intended, observable consequence.
        var impl = new PlainProtocolMock();

        var swiftRooted = ExistentialContainerFactory.GetOrCreate<IProtocolMock>(
            impl, v => new ConsumerOwnedProxyMock(v, (IntPtr)0xA1),
            out _, out var swiftRootedKeepAlive);
        var consumerOwned = ExistentialContainerFactory.GetOrCreateConsumerOwned<IProtocolMock>(
            impl, v => new ConsumerOwnedProxyMock(v, (IntPtr)0xB2),
            out _, out var consumerOwnedKeepAlive);

        Assert.NotSame(swiftRootedKeepAlive, consumerOwnedKeepAlive);
        Assert.Equal((IntPtr)0xA1, swiftRooted.Payload0);
        Assert.Equal((IntPtr)0xB2, consumerOwned.Payload0);

        // Asking each lane again still returns that lane's own carrier — neither memo evicted the
        // other's entry for this implementation.
        ExistentialContainerFactory.GetOrCreate<IProtocolMock>(
            impl, v => new ConsumerOwnedProxyMock(v, (IntPtr)0xDEAD), out _, out var swiftRootedAgain);
        ExistentialContainerFactory.GetOrCreateConsumerOwned<IProtocolMock>(
            impl, v => new ConsumerOwnedProxyMock(v, (IntPtr)0xDEAD), out _, out var consumerOwnedAgain);
        Assert.Same(swiftRootedKeepAlive, swiftRootedAgain);
        Assert.Same(consumerOwnedKeepAlive, consumerOwnedAgain);
        GC.KeepAlive(impl);
    }

    [Fact]
    public void GetOrCreateConsumerOwned_MemoIsNotARootForTheImplOrItsCarrier()
    {
        // The lane holds its carrier STRONGLY from the memo value, and the carrier holds the
        // implementation strongly back. That pair is only safe because the memo is keyed weakly on
        // the same implementation: an entry whose key is reachable only from its own value is not
        // reachable, so the implementation and its carrier are collected together once the consumer
        // lets go. If this went red the lane would be a process-lifetime leak per implementation.
        var (implRef, proxyRef) = ConsumerOwnedWrapWithoutKeepingRefs();

        for (int i = 0; i < 8; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(implRef.IsAlive, "consumer-owned memo must not root the implementation");
        Assert.False(proxyRef.IsAlive, "the carrier must fall away with the implementation it wraps");
    }

    // No-inline so neither the implementation nor its carrier is kept alive by the caller's frame:
    // after this returns the only references are the ones inside the memo.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (WeakReference Impl, WeakReference Proxy) ConsumerOwnedWrapWithoutKeepingRefs()
    {
        var impl = new PlainProtocolMock();
        ExistentialContainerFactory.GetOrCreateConsumerOwned<IProtocolMock>(
            impl, v => new ConsumerOwnedProxyMock(v, (IntPtr)0x99), out _, out var keepAlive);
        Assert.NotNull(keepAlive);
        return (new WeakReference(impl), new WeakReference(keepAlive!));
    }

    [Fact]
    public void GetOrCreateConsumerOwned_WithExistingProxy_MatchesGetOrCreate()
    {
        // A value that already IS a carrier is one the consumer holds directly, so there is nothing
        // to re-lane: the consumer-owned entry point must behave exactly like the default one for
        // the convertible, boxable and round-trip branches.
        var proxyContainer = new ExistentialContainer1 { Payload0 = (IntPtr)0xC1 };
        var proxy = new ExistentialConvertibleMock(proxyContainer);
        var fromProxy = ExistentialContainerFactory.GetOrCreateConsumerOwned<IProtocolMock>(
            proxy, static _ => throw new InvalidOperationException("wrap fallback must not run"),
            out var proxyOwns, out var proxyKeepAlive);
        Assert.Equal((IntPtr)0xC1, fromProxy.Payload0);
        Assert.False(proxyOwns);
        Assert.Same(proxy, proxyKeepAlive);

        var boxableContainer = new ExistentialContainer1 { Payload0 = (IntPtr)0xC2 };
        var boxable = new ExistentialBoxableMock(boxableContainer);
        var fromBoxable = ExistentialContainerFactory.GetOrCreateConsumerOwned<IProtocolMock>(
            boxable, static _ => throw new InvalidOperationException("wrap fallback must not run"),
            out var boxableOwns, out var boxableKeepAlive);
        Assert.Equal((IntPtr)0xC2, fromBoxable.Payload0);
        Assert.True(boxableOwns, "the boxable branch transfers a fresh +1 the caller must destroy");
        Assert.Null(boxableKeepAlive);

        var marshalled = new ExistentialContainer1 { Payload0 = (IntPtr)0xC3 };
        object boxed = marshalled;
        var roundTripped = ExistentialContainerFactory.GetOrCreateConsumerOwned<object>(
            boxed, static _ => throw new InvalidOperationException("wrap fallback must not run"),
            out var roundTripOwns, out var roundTripKeepAlive);
        Assert.Equal((IntPtr)0xC3, roundTripped.Payload0);
        Assert.False(roundTripOwns);
        Assert.Same(boxed, roundTripKeepAlive);
    }

    [Fact]
    public void GetOrCreateConsumerOwned_ShortOverload_SharesTheMemoAndVendsAUsablePayload()
    {
        // The arm that hands Swift a bare object pointer reads Payload0 off the container and has
        // no owns-bit or keep-alive local to thread, so it calls the short overload. That overload
        // must be the same lane, not a second one: the same implementation has to resolve to the
        // same carrier whichever overload asks, and the payload it vends has to be the carrier's
        // conformer box rather than a default-initialized container.
        var impl = new PlainProtocolMock();
        var wrapCount = 0;

        var viaShort = ExistentialContainerFactory.GetOrCreateConsumerOwned<IProtocolMock>(
            impl, v => { wrapCount++; return new ConsumerOwnedProxyMock(v, (IntPtr)0x5150); });
        var viaLong = ExistentialContainerFactory.GetOrCreateConsumerOwned<IProtocolMock>(
            impl, v => { wrapCount++; return new ConsumerOwnedProxyMock(v, (IntPtr)0xBEEF); },
            out var owns, out var keepAlive);

        Assert.Equal(1, wrapCount);
        Assert.Equal((IntPtr)0x5150, viaShort.Payload0);
        Assert.Equal(viaShort.Payload0, viaLong.Payload0);
        // Dropping the out-parameters is only sound because the auto-wrap branch never transfers a
        // +1 the caller would have to destroy, and because the memo holds the carrier for as long
        // as the implementation the caller is passing lives.
        Assert.False(owns);
        Assert.NotNull(keepAlive);
        GC.KeepAlive(impl);
    }

    /// <summary>
    /// Stands in for a generated proxy constructed in consumer-owned mode: it holds the
    /// implementation strongly, the way <c>_csharpImplStrong</c> does, so the memo's ephemeron
    /// behaviour is exercised against the real reference shape.
    /// </summary>
    private sealed class ConsumerOwnedProxyMock : IProtocolMock, ISwiftExistentialConvertible<ExistentialContainer1>
    {
        private readonly object _implStrong;
        private readonly ExistentialContainer1 _container;

        public ConsumerOwnedProxyMock(object impl, IntPtr payload)
        {
            _implStrong = impl;
            _container = new ExistentialContainer1 { Payload0 = payload };
        }

        public ExistentialContainer1 GetExistentialContainer() => _container;

        public object Impl => _implStrong;
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

    #region Container Layout Verification Tests

    [Fact]
    public void ExistentialContainer1_HasCorrectMemoryLayout_ForSwiftInterop()
    {
        // ExistentialContainer1 must have the exact layout Swift expects:
        // [0-2]: payload (3 words), [3]: metadata, [4]: witness table
        // Total: 5 * IntPtr.Size = 40 bytes on 64-bit
        Assert.Equal(5 * IntPtr.Size, System.Runtime.InteropServices.Marshal.SizeOf<ExistentialContainer1>());
    }

    [Fact]
    public void ExistentialContainer1_FieldOffsets_MatchSwiftLayout()
    {
        // Verify the field offsets match Swift's existential container layout.
        // Use real metadata so all 5 fields have distinct, verifiable values.
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
        var container = new ExistentialContainer1
        {
            Payload0 = (IntPtr)0x1111,
            Payload1 = (IntPtr)0x2222,
            Payload2 = (IntPtr)0x3333,
            ObjectMetadata = metadata
        };
        container[0] = (IntPtr)0x5555; // witness table

        // Read back all 5 fields to verify no overlap
        Assert.Equal((IntPtr)0x1111, container.Payload0);
        Assert.Equal((IntPtr)0x2222, container.Payload1);
        Assert.Equal((IntPtr)0x3333, container.Payload2);
        Assert.Equal(metadata, container.ObjectMetadata);
        Assert.Equal((IntPtr)0x5555, container[0]);
    }

    [Fact]
    public void Create_ClassType_StoresReferenceInPayload0()
    {
        // For class types (size == IntPtr.Size, inline), the class reference
        // should be stored in Payload0, with Payload1/2 remaining zero.
        var value = new SwiftIntMock(42);
        var container = ExistentialContainerFactory.Create<SwiftIntMock, ISwiftHashable>(value);

        // Payload0 should be non-zero (the marshalled value)
        Assert.NotEqual(IntPtr.Zero, container.Payload0);
        // Payload1/2 should be zero for inline small types (SwiftInt fits in one word)
        Assert.Equal(IntPtr.Zero, container.Payload1);
        Assert.Equal(IntPtr.Zero, container.Payload2);
        // Metadata and witness table should be valid
        Assert.True(container.ObjectMetadata.IsValid);
        Assert.NotEqual(IntPtr.Zero, container[0]);
    }

    [Fact]
    public void Create_WitnessTable_MatchesDirectLookup()
    {
        // The witness table in the container should match what
        // ProtocolWitnessTable.GetOrThrowDirect returns directly.
        // This catches NativeAOT dispatch path divergence.
        var value = new SwiftIntMock(42);
        var container = ExistentialContainerFactory.Create<SwiftIntMock, ISwiftHashable>(value);

        var directWitness = ProtocolWitnessTable.GetOrThrowDirect<SwiftIntMock, ISwiftHashable>();

        Assert.Equal(directWitness.Handle, container[0]);
    }

    [Fact]
    public void Create_TwoContainers_SameType_HaveMatchingMetadataAndWitness()
    {
        // Multiple containers for the same type+protocol should have identical
        // metadata and witness tables (even though payloads may differ).
        var value1 = new SwiftIntMock(42);
        var value2 = new SwiftIntMock(99);

        var container1 = ExistentialContainerFactory.Create<SwiftIntMock, ISwiftHashable>(value1);
        var container2 = ExistentialContainerFactory.Create<SwiftIntMock, ISwiftHashable>(value2);

        Assert.Equal(container1.ObjectMetadata, container2.ObjectMetadata);
        Assert.Equal(container1[0], container2[0]); // same witness table
    }

    [Fact]
    public void Create_Container_MetadataMatchesTypeMetadata()
    {
        // The existential container's ObjectMetadata should match the type's
        // own metadata from GetTypeMetadata(). This is critical for Swift
        // runtime's existential opening (existential.type must match).
        var value = new SwiftIntMock(42);
        var container = ExistentialContainerFactory.Create<SwiftIntMock, ISwiftHashable>(value);

        var typeMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>(); // same as SwiftIntMock's impl
        Assert.Equal(typeMetadata, container.ObjectMetadata);
    }

    #endregion

    #region ILLink Preservation Verification Tests

    [Fact]
    public void ExistentialContainerFactory_IsPreservedInILLinkDescriptors()
    {
        // ILLink.Descriptors.xml must preserve ExistentialContainerFactory for NativeAOT.
        // Previous test only checked reflection accessibility which passes even without
        // the ILLink entry. This reads the actual embedded XML to verify the entry exists.
        var assembly = typeof(ExistentialContainerFactory).Assembly;
        var resourceNames = assembly.GetManifestResourceNames();
        var descriptorName = resourceNames.FirstOrDefault(n => n.Contains("ILLink.Descriptors"));
        Assert.NotNull(descriptorName);

        using var stream = assembly.GetManifestResourceStream(descriptorName!);
        Assert.NotNull(stream);
        using var reader = new System.IO.StreamReader(stream!);
        var content = reader.ReadToEnd();
        Assert.Contains("ExistentialContainerFactory", content);
    }

    [Fact]
    public void ProtocolWitnessTable_AndConformanceDescriptor_PreservedInILLinkDescriptors()
    {
        // ILLink.Descriptors.xml must preserve ProtocolWitnessTable and
        // ProtocolConformanceDescriptor for NativeAOT existential container boxing.
        // Previous test only checked reflection accessibility which passes even without
        // the ILLink entries. This reads the actual embedded XML to verify both entries.
        var assembly = typeof(ProtocolWitnessTable).Assembly;
        var resourceNames = assembly.GetManifestResourceNames();
        var descriptorName = resourceNames.FirstOrDefault(n => n.Contains("ILLink.Descriptors"));
        Assert.NotNull(descriptorName);

        using var stream = assembly.GetManifestResourceStream(descriptorName!);
        Assert.NotNull(stream);
        using var reader = new System.IO.StreamReader(stream!);
        var content = reader.ReadToEnd();
        Assert.Contains("ProtocolWitnessTable", content);
        Assert.Contains("ProtocolConformanceDescriptor", content);
    }

    #endregion

    #region EditorBrowsable Attribute Tests

    [Theory]
    [InlineData(typeof(ExistentialContainer0))]
    [InlineData(typeof(ExistentialContainer1))]
    [InlineData(typeof(ExistentialContainer2))]
    [InlineData(typeof(ExistentialContainer3))]
    [InlineData(typeof(ExistentialContainer4))]
    [InlineData(typeof(ExistentialContainer5))]
    [InlineData(typeof(ExistentialContainer6))]
    [InlineData(typeof(ExistentialContainer7))]
    [InlineData(typeof(ExistentialContainer8))]
    public void ExistentialContainerTypes_AreEditorBrowsableNever(Type containerType)
    {
        var attr = containerType.GetCustomAttributes(typeof(System.ComponentModel.EditorBrowsableAttribute), false)
            .Cast<System.ComponentModel.EditorBrowsableAttribute>()
            .FirstOrDefault();

        Assert.NotNull(attr);
        Assert.Equal(System.ComponentModel.EditorBrowsableState.Never, attr.State);
    }

    [Fact]
    public void ExistentialContainerFactory_IsEditorBrowsableNever()
    {
        var attr = typeof(ExistentialContainerFactory)
            .GetCustomAttributes(typeof(System.ComponentModel.EditorBrowsableAttribute), false)
            .Cast<System.ComponentModel.EditorBrowsableAttribute>()
            .FirstOrDefault();

        Assert.NotNull(attr);
        Assert.Equal(System.ComponentModel.EditorBrowsableState.Never, attr.State);
    }

    [Fact]
    public void ISwiftExistentialConvertible_IsEditorBrowsableNever()
    {
        var attr = typeof(ISwiftExistentialConvertible<ExistentialContainer1>)
            .GetCustomAttributes(typeof(System.ComponentModel.EditorBrowsableAttribute), false)
            .Cast<System.ComponentModel.EditorBrowsableAttribute>()
            .FirstOrDefault();

        Assert.NotNull(attr);
        Assert.Equal(System.ComponentModel.EditorBrowsableState.Never, attr.State);
    }

    [Fact]
    public void IExistentialContainer_IsEditorBrowsableNever()
    {
        var attr = typeof(IExistentialContainer)
            .GetCustomAttributes(typeof(System.ComponentModel.EditorBrowsableAttribute), false)
            .Cast<System.ComponentModel.EditorBrowsableAttribute>()
            .FirstOrDefault();

        Assert.NotNull(attr);
        Assert.Equal(System.ComponentModel.EditorBrowsableState.Never, attr.State);
    }

    [Fact]
    public void IExistentialBoxable_IsEditorBrowsableNever()
    {
        var attr = typeof(IExistentialBoxable)
            .GetCustomAttributes(typeof(System.ComponentModel.EditorBrowsableAttribute), false)
            .Cast<System.ComponentModel.EditorBrowsableAttribute>()
            .FirstOrDefault();

        Assert.NotNull(attr);
        Assert.Equal(System.ComponentModel.EditorBrowsableState.Never, attr.State);
    }

    #endregion
}
