// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Maps each generated Swift protocol interface to its proxy class and its Swift protocol
/// descriptor, so a single-protocol existential built from a proxy always carries the witness
/// table of the protocol the call site asks for. Generated bindings register every protocol whose
/// proxy they emit from their module initializer.
/// </summary>
/// <remarks>
/// <para>
/// A refined protocol <c>R: P</c> projects to <c>IR : IP</c>, so C# accepts an <c>RProxy</c> wherever
/// an <c>IP</c> is expected. The proxy's container holds <c>R</c>'s witness table, while Swift reads an
/// <c>any P</c> argument through <c>P</c>'s table: a requirement called through the wrong table
/// lands on an unrelated entry (entry 1 of <c>R</c>'s table is its base-protocol entry, not
/// <c>P</c>'s first requirement). <see cref="Retarget{TProtocol}"/> looks the value's conformance to
/// the requested protocol up from its dynamic type with <c>swift_conformsToProtocol</c> and rebuilds
/// the container in the requested protocol's layout (class-bound <c>[classRef][witnessTable]</c> in
/// Payload0/Payload1, or the opaque five-word layout). Only the witness table and the layout change;
/// the payload words, and so their ownership, are the proxy's.
/// </para>
/// <para>
/// When the requested protocol has no registered descriptor (a binding generated before this
/// registry, or a protocol whose defining library the generator could not resolve), the value is
/// refused with <see cref="InvalidOperationException"/> before any native call rather than handed to
/// Swift with another protocol's witness table.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SwiftProtocolRegistry
{
    private sealed class ProtocolEntry
    {
        public required Type Interface { get; init; }
        public required bool IsClassBound { get; init; }
        public required bool IsObjC { get; init; }
        public required Lazy<ProtocolDescriptor?> Descriptor { get; init; }
    }

    private static readonly ConcurrentDictionary<Type, ProtocolEntry> s_byInterface = new();
    private static readonly ConcurrentDictionary<Type, ProtocolEntry> s_byProxy = new();

    /// <summary>
    /// Registers a generated protocol interface, the proxy class that carries Swift-backed values of
    /// it, whether the protocol is class-bound, and where its protocol descriptor is exported.
    /// Called from generated module initializers; a later registration of the same types replaces the
    /// earlier one.
    /// </summary>
    /// <param name="protocolInterface">The generated C# interface (<c>IFoo</c>).</param>
    /// <param name="proxyType">The generated proxy class (<c>FooProxy</c>).</param>
    /// <param name="isClassBound">Whether <c>any Foo</c> uses the two-word class-bound layout.</param>
    /// <param name="libraryName">The library exporting the protocol descriptor, or null when unknown.</param>
    /// <param name="descriptorSymbol">The protocol descriptor symbol (<c>$s…Mp</c>), or null when unknown.</param>
    public static void Register(Type protocolInterface, Type proxyType, bool isClassBound, string? libraryName, string? descriptorSymbol)
    {
        ArgumentNullException.ThrowIfNull(protocolInterface);
        ArgumentNullException.ThrowIfNull(proxyType);
        var entry = new ProtocolEntry
        {
            Interface = protocolInterface,
            IsClassBound = isClassBound,
            IsObjC = false,
            Descriptor = new Lazy<ProtocolDescriptor?>(() =>
            {
                if (string.IsNullOrEmpty(libraryName) || string.IsNullOrEmpty(descriptorSymbol))
                    return null;
                try
                {
                    return ProtocolDescriptor.LoadFromSymbol(libraryName, descriptorSymbol);
                }
                catch (SwiftRuntimeException)
                {
                    return null;
                }
            }),
        };
        s_byInterface[protocolInterface] = entry;
        s_byProxy[proxyType] = entry;
    }

    /// <summary>
    /// Registers a generated Objective-C protocol interface and its proxy. An <c>any P</c> of an
    /// Objective-C protocol is the object alone, with no witness table, so a value passed as one
    /// is never retargeted.
    /// </summary>
    public static void RegisterObjC(Type protocolInterface, Type proxyType)
    {
        ArgumentNullException.ThrowIfNull(protocolInterface);
        ArgumentNullException.ThrowIfNull(proxyType);
        var entry = new ProtocolEntry
        {
            Interface = protocolInterface,
            IsClassBound = true,
            IsObjC = true,
            Descriptor = new Lazy<ProtocolDescriptor?>(() => null),
        };
        s_byInterface[protocolInterface] = entry;
        s_byProxy[proxyType] = entry;
    }

    /// <summary>
    /// Returns <paramref name="container"/>, read from <paramref name="convertible"/>, rebuilt to
    /// carry <typeparamref name="TProtocol"/>'s witness table when the convertible is the proxy of
    /// a protocol that refines <typeparamref name="TProtocol"/>. Returns it unchanged when the
    /// convertible is <typeparamref name="TProtocol"/>'s own proxy, or not a registered proxy.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The convertible is a refining protocol's proxy and <typeparamref name="TProtocol"/>'s witness
    /// table cannot be obtained (no registered descriptor, or the dynamic type does not conform).
    /// </exception>
    public static ExistentialContainer1 Retarget<TProtocol>(object convertible, ExistentialContainer1 container)
        where TProtocol : class
    {
        if (!TryLookup(s_byProxy, convertible.GetType(), out var source))
            return container;
        var target = typeof(TProtocol);
        if (source.Interface == target || !target.IsAssignableFrom(source.Interface))
            return container;

        if (!TryLookup(s_byInterface, target, out var destination))
            throw Refusal(source.Interface, target, "the binding registered no protocol descriptor for it");
        if (destination.IsObjC)
            return container;
        var descriptor = destination.Descriptor.Value;
        if (descriptor is null)
            throw Refusal(source.Interface, target, "its protocol descriptor could not be loaded");

        IntPtr classRef = IntPtr.Zero;
        TypeMetadata dynamicType;
        if (source.IsClassBound)
        {
            classRef = container.Payload0;
            if (classRef == IntPtr.Zero)
                throw Refusal(source.Interface, target, "the proxy holds no instance");
            dynamicType = swift_getObjectType(classRef);
        }
        else
        {
            dynamicType = container.ObjectMetadata;
        }

        if (!SwiftConformance.TryGetWitnessTable(dynamicType, descriptor.Value, out var witnessTable) || witnessTable is null)
            throw Refusal(source.Interface, target, "the value's dynamic type does not conform to it");

        if (destination.IsClassBound)
        {
            // The class-bound proxy layout: instance in Payload0, witness table in Payload1.
            // A class-bound target is only refined by class-bound protocols, so the source is
            // class-bound too and Payload0 is its instance.
            return new ExistentialContainer1 { Payload0 = container.Payload0, Payload1 = witnessTable.Value.Handle };
        }

        if (source.IsClassBound)
        {
            // A class-bound refinement of an opaque protocol: the instance becomes the inline
            // payload of the opaque layout, described by its dynamic type.
            var widened = new ExistentialContainer1 { Payload0 = classRef, ObjectMetadata = dynamicType };
            widened[0] = witnessTable.Value.Handle;
            return widened;
        }

        var retargeted = container;
        retargeted[0] = witnessTable.Value.Handle;
        return retargeted;
    }

    /// <summary>
    /// Whether <paramref name="proxyType"/> is the registered proxy of a class-bound or
    /// Objective-C protocol, whose container holds the instance in <c>Payload0</c>.
    /// </summary>
    internal static bool IsClassBoundProxy(Type proxyType) =>
        TryLookup(s_byProxy, proxyType, out var entry) && entry.IsClassBound;

    // Registrations come from each binding's module initializer. A runtime runs it before the
    // module's first method call, which a type's first appearance here does not imply (a base
    // interface of another module may never have had a member called), so a miss runs the owning
    // module's initializer (a no-op once it has run, and it waits for one another thread is running)
    // and looks again.
    private static bool TryLookup(ConcurrentDictionary<Type, ProtocolEntry> map, Type type, [NotNullWhen(true)] out ProtocolEntry? entry)
    {
        if (map.TryGetValue(type, out entry))
            return true;
        try
        {
            RuntimeHelpers.RunModuleConstructor(type.Module.ModuleHandle);
        }
        catch (NotSupportedException)
        {
            // A runtime that runs every module initializer at startup has nothing left to run.
        }
        return map.TryGetValue(type, out entry);
    }

    private static InvalidOperationException Refusal(Type source, Type target, string reason) =>
        new($"A Swift value bound as {source.FullName} cannot be passed as {target.FullName}: {reason}, " +
            $"so its witness table for {target.Name} is unknown. Passing it would call {target.Name}'s " +
            $"requirements through {source.Name}'s witness table.");

    [DllImport(KnownLibraries.SwiftCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern TypeMetadata swift_getObjectType(IntPtr instance);
}
