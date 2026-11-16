// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// <see cref="SwiftProtocolRegistry"/> over a real refinement pair from the host Swift runtime:
/// <c>Hashable</c> refines <c>Equatable</c>. A stand-in "Hashable proxy" holds an <c>Int</c> with
/// <c>Hashable</c>'s witness table, exactly like a generated refined proxy holds its own protocol's
/// table; passed as the "Equatable" interface it must come out carrying <c>Equatable</c>'s table.
/// </summary>
public class SwiftProtocolRegistryTests
{
    private const string SwiftCore = "/usr/lib/swift/libswiftCore.dylib";
    private const string HashableMp = "$sSHMp";
    private const string EquatableMp = "$sSQMp";

    // Each test registers its own interface family: the registry is process-wide.
    public interface IEqA { }
    public interface IHashA : IEqA { }
    public interface IEqB { }
    public interface IHashB : IEqB { }
    public interface IEqC { }
    public interface IHashC : IEqC { }
    public interface IEqD { }
    public interface IHashD : IEqD { }
    public interface IEqE { }
    public interface IHashE : IEqE { }

    private sealed class Proxy<TInterface> : ISwiftExistentialConvertible<ExistentialContainer1>
    {
        public required ExistentialContainer1 Container { get; init; }
        public ExistentialContainer1 GetExistentialContainer() => Container;
    }

    private sealed class EqAProxy : IEqA, ISwiftExistentialConvertible<ExistentialContainer1>
    {
        public ExistentialContainer1 GetExistentialContainer() => default;
    }

    private sealed class HashAProxy : IHashA, ISwiftExistentialConvertible<ExistentialContainer1>
    {
        public required ExistentialContainer1 Container { get; init; }
        public ExistentialContainer1 GetExistentialContainer() => Container;
    }

    private static ProtocolWitnessTable IntWitnessTable(string protocolSymbol)
    {
        var descriptor = ProtocolDescriptor.LoadFromSymbol(SwiftCore, protocolSymbol);
        return SwiftConformance.GetWitnessTableOrThrow(TypeMetadata.GetTypeMetadataOrThrow<nint>(), descriptor);
    }

    private static ExistentialContainer1 IntAsHashable()
    {
        var container = new ExistentialContainer1
        {
            Payload0 = 42,
            ObjectMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>(),
        };
        container[0] = IntWitnessTable(HashableMp).Handle;
        return container;
    }

    [Fact]
    public void RefinedProxyPassedAsBase_GetOrCreateCarriesTheBaseWitnessTable()
    {
        SwiftProtocolRegistry.Register(typeof(IEqA), typeof(EqAProxy), false, SwiftCore, EquatableMp);
        SwiftProtocolRegistry.Register(typeof(IHashA), typeof(HashAProxy), false, SwiftCore, HashableMp);
        var proxy = new HashAProxy { Container = IntAsHashable() };

        IEqA asBase = proxy;
        var container = ExistentialContainerFactory.GetOrCreate(asBase, out var owns);

        Assert.False(owns);
        Assert.Equal(IntWitnessTable(EquatableMp).Handle, container[0]);
        Assert.NotEqual(IntWitnessTable(HashableMp).Handle, container[0]);
        Assert.Equal((nint)42, container.Payload0);
        Assert.Equal(TypeMetadata.GetTypeMetadataOrThrow<nint>(), container.ObjectMetadata);

        // Passed as itself the container is the proxy's own.
        IHashA asSelf = proxy;
        Assert.Equal(IntWitnessTable(HashableMp).Handle, ExistentialContainerFactory.GetOrCreate(asSelf)[0]);
    }

    [Fact]
    public void RefinedProxyPassedAsBase_WrapFallbackOverloadIsRetargetedToo()
    {
        SwiftProtocolRegistry.Register(typeof(IEqB), typeof(Proxy<IEqB>), false, SwiftCore, EquatableMp);
        SwiftProtocolRegistry.Register(typeof(IHashB), typeof(HashBValue), false, SwiftCore, HashableMp);
        var proxy = new HashBValue { Container = IntAsHashable() };

        var container = ExistentialContainerFactory.GetOrCreate<IEqB>(proxy, static _ => throw new InvalidOperationException("no wrap"));

        Assert.Equal(IntWitnessTable(EquatableMp).Handle, container[0]);
    }

    private sealed class HashBValue : IHashB, ISwiftExistentialConvertible<ExistentialContainer1>
    {
        public required ExistentialContainer1 Container { get; init; }
        public ExistentialContainer1 GetExistentialContainer() => Container;
    }

    private sealed class HashCValue : IHashC, ISwiftExistentialConvertible<ExistentialContainer1>
    {
        public required ExistentialContainer1 Container { get; init; }
        public ExistentialContainer1 GetExistentialContainer() => Container;
    }

    [Fact]
    public void RefinedProxyPassedAsUnregisteredBase_IsRefusedBeforeAnyNativeCall()
    {
        SwiftProtocolRegistry.Register(typeof(IHashC), typeof(HashCValue), false, SwiftCore, HashableMp);
        IEqC asBase = new HashCValue { Container = IntAsHashable() };

        var refusal = Assert.Throws<InvalidOperationException>(() => ExistentialContainerFactory.GetOrCreate(asBase));
        Assert.Contains(nameof(IEqC), refusal.Message);
    }

    private sealed class HashDValue : IHashD, ISwiftExistentialConvertible<ExistentialContainer1>
    {
        public required ExistentialContainer1 Container { get; init; }
        public ExistentialContainer1 GetExistentialContainer() => Container;
    }

    [Fact]
    public void BaseWithoutALoadableDescriptor_IsRefused()
    {
        SwiftProtocolRegistry.Register(typeof(IEqD), typeof(Proxy<IEqD>), false, null, null);
        SwiftProtocolRegistry.Register(typeof(IHashD), typeof(HashDValue), false, SwiftCore, HashableMp);
        IEqD asBase = new HashDValue { Container = IntAsHashable() };

        Assert.Throws<InvalidOperationException>(() => ExistentialContainerFactory.GetOrCreate(asBase));
    }

    private sealed class HashEValue : IHashE, ISwiftExistentialConvertible<ExistentialContainer1>
    {
        public required ExistentialContainer1 Container { get; init; }
        public ExistentialContainer1 GetExistentialContainer() => Container;
    }

    [Fact]
    public void ObjCBase_PassesTheObjectUnchanged()
    {
        // An Objective-C protocol's existential is the object alone: nothing to retarget.
        SwiftProtocolRegistry.RegisterObjC(typeof(IEqE), typeof(Proxy<IEqE>));
        SwiftProtocolRegistry.Register(typeof(IHashE), typeof(HashEValue), true, SwiftCore, HashableMp);
        var original = new ExistentialContainer1 { Payload0 = 0x1234, Payload1 = 0x5678 };
        IEqE asBase = new HashEValue { Container = original };

        var container = ExistentialContainerFactory.GetOrCreate(asBase);

        Assert.Equal(original.Payload0, container.Payload0);
        Assert.Equal(original.Payload1, container.Payload1);
    }

    [Fact]
    public void UnregisteredConvertible_PassesUnchanged()
    {
        var original = IntAsHashable();
        var proxy = new Proxy<IEqA> { Container = original };

        var container = SwiftProtocolRegistry.Retarget<IEqA>(proxy, original);

        Assert.Equal(original[0], container[0]);
    }
}
