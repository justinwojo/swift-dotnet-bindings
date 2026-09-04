// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;

namespace Swift.Runtime;

/// <summary>
/// Symbol-keyed protocol-conformance lane for conforming types that cannot implement
/// <see cref="ISwiftObject"/>.
/// </summary>
/// <remarks>
/// <para>
/// A payload-less raw-value Swift enum is projected as a plain C# <c>enum</c>, and a C# enum
/// can neither implement an interface nor carry the static-abstract
/// <c>GetProtocolConformanceDescriptor</c> hook that
/// <see cref="ProtocolConformanceDescriptor.TryGet{TType, TProtocol}"/> dispatches through for
/// <see cref="ISwiftObject"/> types. Its Swift conformance descriptor is nonetheless a real
/// exported symbol in the declaring module, so the binding can name it up front and the runtime
/// can load it on demand — exactly the shape <see cref="HashableConformanceRegistry"/> already
/// uses for the primitives, generalized from a hard-coded stdlib table to entries a generated
/// module initializer contributes.
/// </para>
/// <para>
/// Registration is metadata-only (a dictionary write): no native call happens until the
/// conformance is first asked for, so a module initializer registering many enums costs nothing
/// at load. Resolution is AOT-safe — the key is a <see cref="Type"/> pair and the lookup never
/// reflects over the conforming type.
/// </para>
/// </remarks>
internal static class ConformanceSymbolRegistry
{
    private readonly record struct ConformanceKey(Type ConformingType, Type ProtocolType);

    /// <summary>
    /// One registration of a descriptor location. This is a reference type on purpose: each
    /// <see cref="Register"/> call mints a fresh instance, so instance identity is the version
    /// stamp that tells a resolution which registration it was computed from.
    /// </summary>
    internal sealed class Declaration
    {
        internal Declaration(string library, string symbol)
        {
            Library = library;
            Symbol = symbol;
        }

        internal string Library { get; }

        internal string Symbol { get; }
    }

    /// <summary>Declared conformance-descriptor locations, keyed by (conforming type, protocol).</summary>
    private static readonly ConcurrentDictionary<ConformanceKey, Declaration> _declared = new();

    /// <summary>
    /// Resolution results, including failures. A failed load is cached as
    /// <see cref="ProtocolConformanceDescriptor.Zero"/> so a missing or mis-named symbol costs
    /// one dlopen attempt rather than one per call site. Each entry carries the
    /// <see cref="Declaration"/> it was computed from so a resolution can never outlive the
    /// registration that produced it.
    /// </summary>
    private static readonly ConcurrentDictionary<ConformanceKey, (Declaration Declaration, ProtocolConformanceDescriptor Descriptor)> _resolved = new();

    /// <summary>
    /// Declares where the Swift protocol-conformance descriptor for a (type, protocol) pair lives.
    /// Last registration for a pair wins; re-registering the same pair is idempotent in practice
    /// because a module initializer runs once per assembly.
    /// </summary>
    /// <param name="conformingType">The C# type standing in for the Swift conforming type.</param>
    /// <param name="protocolType">The C# marker interface standing in for the Swift protocol.</param>
    /// <param name="libraryName">The library exporting the descriptor symbol.</param>
    /// <param name="symbolName">The mangled conformance-descriptor symbol.</param>
    internal static void Register(Type conformingType, Type protocolType, string libraryName, string symbolName)
    {
        ArgumentNullException.ThrowIfNull(conformingType);
        ArgumentNullException.ThrowIfNull(protocolType);

        // An empty library or symbol could only produce a LoadFromSymbol("lib", "") failure at
        // first use; reject it at registration so the miss is silent rather than a cached throw.
        if (string.IsNullOrEmpty(libraryName) || string.IsNullOrEmpty(symbolName))
            return;

        var key = new ConformanceKey(conformingType, protocolType);
        _declared[key] = new Declaration(libraryName, symbolName);
        // Drop any cached resolution so a re-registration is honoured rather than shadowed by an
        // earlier failure. This is hygiene, not the correctness mechanism: a resolution still in
        // flight against the superseded declaration would land after this removal, which is why
        // both the publish and the read side compare against the declaration in force.
        _resolved.TryRemove(key, out _);
    }

    /// <summary>
    /// Attempts to resolve a registered conformance descriptor, loading it from its symbol on
    /// first use.
    /// </summary>
    /// <returns>
    /// <c>true</c> when a descriptor was registered for the pair and its symbol loaded;
    /// otherwise <c>false</c>, leaving the caller's existing resolution path unchanged.
    /// </returns>
    internal static bool TryResolve(Type conformingType, Type protocolType, out ProtocolConformanceDescriptor descriptor)
    {
        var key = new ConformanceKey(conformingType, protocolType);

        // Bounded re-read loop, not a lock: the fast path (declaration present, resolution cached
        // against it) still costs two dictionary reads and no synchronization. An iteration is
        // spent only when a Register lands while this resolve was computing — the case where the
        // value in hand describes a registration that no longer exists. Handing that value back
        // would be worse than dropping it: callers cache what they get (witness-table caches), so
        // an obsolete descriptor outlives the resolve that produced it and the newer registration
        // never takes effect for them. Each pass re-reads the declaration, so the retry is against
        // the registration actually in force; the attempt cap keeps a pathological registration
        // storm from spinning here rather than bounding correctness.
        for (var attempt = 1; ; attempt++)
        {
            // The declaration is read first and is what the cached resolution is validated against:
            // a resolution only answers for the registration it was computed from, so "last
            // registration wins" holds even when a Register lands mid-resolution.
            if (!_declared.TryGetValue(key, out var declaration))
            {
                descriptor = ProtocolConformanceDescriptor.Zero;
                return false;
            }

            if (_resolved.TryGetValue(key, out var cached) && ReferenceEquals(cached.Declaration, declaration))
            {
                descriptor = cached.Descriptor;
                return descriptor.IsValid;
            }

            try
            {
                descriptor = ProtocolConformanceDescriptor.LoadFromSymbol(declaration.Library, declaration.Symbol);
            }
            catch (Exception)
            {
                // A declared-but-unloadable symbol must degrade to "no conformance registered", not to
                // an exception escaping an otherwise best-effort lookup: callers then report the same
                // missing-witness-table failure they reported before anything was registered.
                descriptor = ProtocolConformanceDescriptor.Zero;
            }

            ResolvedBeforePublish?.Invoke(declaration);

            if (PublishResolution(key, declaration, descriptor) || attempt >= MaxResolveAttempts)
                return descriptor.IsValid;
        }
    }

    /// <summary>
    /// How many times one <see cref="TryResolve"/> call will recompute after finding its answer
    /// superseded. Two covers the real shape — a module initializer re-registering a pair once —
    /// while leaving the loop bounded.
    /// </summary>
    private const int MaxResolveAttempts = 2;

    /// <summary>
    /// Test seam: invoked with the declaration a resolve computed against, in the window between
    /// computing the descriptor and publishing it. Null in production — nothing outside the tests
    /// assigns it — and exists so a test can re-register the pair exactly inside that window and
    /// drive the supersession retry without racing threads or sleeping.
    /// </summary>
    internal static Action<Declaration>? ResolvedBeforePublish;

    /// <summary>
    /// Caches <paramref name="descriptor"/> for the pair, but only while
    /// <paramref name="declaration"/> is still the registration in force. A resolution computed
    /// against a superseded declaration is dropped rather than published: the registration that
    /// replaced it owns the cache slot, and its own resolution is the one callers must see. A
    /// refusal is also what tells <see cref="TryResolve"/> to recompute, so the caller in flight
    /// is not handed the value that was just refused for the cache.
    /// </summary>
    /// <returns><c>true</c> when the resolution was cached; <c>false</c> when it was superseded.</returns>
    private static bool PublishResolution(
        ConformanceKey key, Declaration declaration, ProtocolConformanceDescriptor descriptor)
    {
        if (!_declared.TryGetValue(key, out var current) || !ReferenceEquals(current, declaration))
            return false;

        // A Register can still land between the check above and this write, which is why the
        // entry stores its declaration and the read side re-checks it: a slipped-through stale
        // entry is recomputed on the next resolve instead of being handed out.
        _resolved[key] = (declaration, descriptor);
        return true;
    }

    /// <summary>
    /// Test seam: publishes a resolution as if it had just been computed from
    /// <paramref name="declaration"/>, so the supersession rule can be exercised without a
    /// timing-dependent race.
    /// </summary>
    internal static bool PublishResolution(
        Type conformingType, Type protocolType, Declaration declaration, ProtocolConformanceDescriptor descriptor)
        => PublishResolution(new ConformanceKey(conformingType, protocolType), declaration, descriptor);

    /// <summary>
    /// Test seam: returns the declaration currently in force for the pair, the value a resolution
    /// in flight would have captured.
    /// </summary>
    internal static Declaration? PeekDeclaration(Type conformingType, Type protocolType)
        => _declared.TryGetValue(new ConformanceKey(conformingType, protocolType), out var declaration)
            ? declaration
            : null;

    /// <summary>
    /// Returns whether a conformance-descriptor location has been declared for the pair, without
    /// attempting to load it.
    /// </summary>
    internal static bool IsRegistered(Type conformingType, Type protocolType)
        => _declared.ContainsKey(new ConformanceKey(conformingType, protocolType));
}
