// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;

namespace Swift.Runtime;

/// <summary>
/// Registry of <c>ISwiftComparable</c> protocol conformance descriptors for known scalar
/// types. Enables NativeAOT-safe witness table resolution for types lacking
/// <c>ISwiftObject</c> constraint (e.g., <c>Bound</c> in <c>SwiftClosedRange</c>) by
/// avoiding <c>MakeGenericType</c> reflection. Mirrors
/// <see cref="HashableConformanceRegistry"/> shape — direct symbol lookup for known
/// primitives, <see cref="ProtocolWitnessTable.GetOrThrow{TConformer,TProtocol}"/>
/// fallback for everything else.
/// </summary>
internal static class ComparableConformanceRegistry
{
    // Maps C# types to their Swift Comparable conformance descriptor mangled symbols in libswiftCore.
    // Bool is intentionally excluded — Swift.Bool does not conform to Comparable.
    private static readonly Dictionary<Type, string> _knownComparableSymbols = new()
    {
        { typeof(nint), "$sSiSLsMc" },        // Swift.Int : Comparable
        { typeof(nuint), "$sSuSLsMc" },       // Swift.UInt : Comparable
        { typeof(float), "$sSfSLsMc" },       // Swift.Float : Comparable
        { typeof(double), "$sSdSLsMc" },      // Swift.Double : Comparable
        { typeof(sbyte), "$ss4Int8VSLsMc" },  // Swift.Int8 : Comparable
        { typeof(byte), "$ss5UInt8VSLsMc" },  // Swift.UInt8 : Comparable
        { typeof(short), "$ss5Int16VSLsMc" },   // Swift.Int16 : Comparable
        { typeof(ushort), "$ss6UInt16VSLsMc" }, // Swift.UInt16 : Comparable
        { typeof(int), "$ss5Int32VSLsMc" },     // Swift.Int32 : Comparable
        { typeof(uint), "$ss6UInt32VSLsMc" },   // Swift.UInt32 : Comparable
        { typeof(long), "$ss5Int64VSLsMc" },    // Swift.Int64 : Comparable
        { typeof(ulong), "$ss6UInt64VSLsMc" },  // Swift.UInt64 : Comparable
        { typeof(SwiftString), "$sSSSLsMc" },   // Swift.String : Comparable
    };

    private static readonly ConcurrentDictionary<Type, ProtocolWitnessTable> _cache = new();

    /// <summary>
    /// Gets the <c>ISwiftComparable</c> witness table for the given bound type. Uses
    /// direct symbol lookup for known primitives (NativeAOT-safe, no
    /// <c>MakeGenericType</c>); falls back to
    /// <see cref="ProtocolWitnessTable.GetOrThrow{TConformer,TProtocol}"/> for everything
    /// else.
    /// </summary>
    public static ProtocolWitnessTable GetComparableWitnessTable<T>()
    {
        return _cache.GetOrAdd(typeof(T), type =>
        {
            if (_knownComparableSymbols.TryGetValue(type, out var symbol))
            {
                var metadata = TypeMetadata.GetTypeMetadataOrThrow<T>();
                var conformance = ProtocolConformanceDescriptor.LoadFromSymbol(
                    "/usr/lib/swift/libswiftCore.dylib", symbol);
                return ProtocolWitnessTable.GetProtocolWitnessTable(conformance, metadata);
            }

            return ProtocolWitnessTable.GetOrThrow<T, ISwiftComparable>();
        });
    }
}
