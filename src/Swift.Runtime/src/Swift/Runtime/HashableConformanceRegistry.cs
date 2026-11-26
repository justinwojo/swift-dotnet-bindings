// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;

namespace Swift.Runtime;

/// <summary>
/// Registry of ISwiftHashable protocol conformance descriptors for known types.
/// Enables NativeAOT-safe witness table resolution for types lacking ISwiftObject constraint
/// (e.g., Element in SwiftSet, TKey in SwiftDictionary) by avoiding MakeGenericType reflection.
/// </summary>
internal static class HashableConformanceRegistry
{
    // Maps C# types to their Swift Hashable conformance descriptor mangled symbols in libswiftCore.
    private static readonly Dictionary<Type, string> _knownHashableSymbols = new()
    {
        { typeof(nint), "$sSiSHsMc" },       // Swift.Int : Hashable
        { typeof(nuint), "$sSuSHsMc" },      // Swift.UInt : Hashable
        { typeof(bool), "$sSbSHsMc" },       // Swift.Bool : Hashable
        { typeof(float), "$sSfSHsMc" },      // Swift.Float : Hashable
        { typeof(double), "$sSdSHsMc" },     // Swift.Double : Hashable
        { typeof(sbyte), "$ss4Int8VSHsMc" }, // Swift.Int8 : Hashable
        { typeof(byte), "$ss5UInt8VSHsMc" }, // Swift.UInt8 : Hashable
        { typeof(short), "$ss5Int16VSHsMc" },  // Swift.Int16 : Hashable
        { typeof(ushort), "$ss6UInt16VSHsMc" }, // Swift.UInt16 : Hashable
        { typeof(int), "$ss5Int32VSHsMc" },    // Swift.Int32 : Hashable
        { typeof(uint), "$ss6UInt32VSHsMc" },  // Swift.UInt32 : Hashable
        { typeof(long), "$ss5Int64VSHsMc" },   // Swift.Int64 : Hashable
        { typeof(ulong), "$ss6UInt64VSHsMc" }, // Swift.UInt64 : Hashable
        { typeof(SwiftString), "$sSSSHsMc" },  // Swift.String : Hashable
        { typeof(string), "$sSSSHsMc" },       // Swift.String : Hashable, a managed string carrier
    };

    private static readonly ConcurrentDictionary<Type, ProtocolWitnessTable> _cache = new();

    /// <summary>
    /// Gets the ISwiftHashable witness table for the given element type.
    /// Uses direct symbol lookup for known types (NativeAOT-safe, no MakeGenericType).
    /// Falls back to ProtocolWitnessTable.GetOrThrow for unknown types.
    /// </summary>
    public static ProtocolWitnessTable GetHashableWitnessTable<T>()
    {
        return _cache.GetOrAdd(typeof(T), type =>
        {
            // For known primitive/scalar types, resolve directly from symbol — no reflection needed.
            if (_knownHashableSymbols.TryGetValue(type, out var symbol))
            {
                var metadata = TypeMetadata.GetTypeMetadataOrThrow<T>();
                var conformance = ProtocolConformanceDescriptor.LoadFromSymbol(
                    "/usr/lib/swift/libswiftCore.dylib", symbol);
                return ProtocolWitnessTable.GetProtocolWitnessTable(conformance, metadata);
            }

            // Optional<Wrapped> is Hashable only through the standard library's conditional
            // conformance (where Wrapped: Hashable), whose witness table must be instantiated for
            // the wrapped type: the Swift runtime's own conformance lookup resolves the condition
            // and instantiates it, as Swift does for [Int?: V] or Set<String?>.
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SwiftOptional<>))
                return OptionalHashableWitnessTable<T>();

            // For all other types (including ISwiftObject types), use the standard resolution path.
            // On NativeAOT, ISwiftObject types are resolved via GetOrThrowDirect (no reflection).
            // On Mono JIT, MakeGenericType works for all types.
            return ProtocolWitnessTable.GetOrThrow<T, ISwiftHashable>();
        });
    }

    private static ProtocolWitnessTable OptionalHashableWitnessTable<T>()
    {
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<T>();
        var hashable = ProtocolDescriptor.LoadFromSymbol(KnownLibraries.SwiftCore, HashableProtocolSymbol);
        if (!SwiftConformance.TryGetWitnessTable(metadata, hashable, out var witnessTable) || witnessTable is null)
        {
            throw new SwiftRuntimeException(
                $"{typeof(T)} is not Hashable: its wrapped type has no Hashable conformance the Swift runtime can find.");
        }
        return witnessTable.Value;
    }

    // Swift.Hashable's protocol descriptor.
    private const string HashableProtocolSymbol = "$sSHMp";
}
