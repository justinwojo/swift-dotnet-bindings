// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Closes the gap left open after the original class-constraint fix: the bilateral pairing
/// filter previously passed through <c>ConformanceKind.Protocol</c> entries when
/// the constraint target was itself a protocol. <c>HashSink</c> declares
/// <c>sumHashes&lt;S: Sequence&gt;(_ source: S) -&gt; Int where S.Element : HashLike</c>
/// — a method-level generic with a protocol bound on the associated type.
///
/// Pre-fix: every Sequence conformer in the engine's pool — including the
/// non-conforming <c>NonHashableBox</c> — paired with this method, and the
/// generator stamped wrappers whose Swift bodies failed to compile with
/// <c>'NonHashableBox' does not conform to protocol 'HashLike'</c>.
///
/// Post-fix: when the constraint target resolves to a protocol, the filter
/// looks up the conformer's <c>Element</c> TypeRecord and (transitively)
/// verifies the target appears in its declared <c>ProtocolConformances</c>.
/// Conformers without the recorded conformance fail closed. The presence of
/// a single <c>SumHashes(SwiftArray&lt;HashableBox&gt;, ...)</c> overload — and
/// the absence of a <c>NonHashableBox</c> overload — is the structural proof.
///
/// Build success itself is the regression detector: without the fix, the
/// generator emits a <c>NonHashableBox</c> wrapper whose body calls
/// <c>sumHashes</c> and Swift compilation rejects it before any runtime test
/// runs. These tests verify (1) the surviving Hashable overload is callable,
/// and (2) the per-element <c>hashCode</c> reads round-trip through the
/// <c>@_cdecl</c> wrapper.
/// </summary>
public class HashSinkTests : TestBase
{
    public HashSinkTests(TestResults results) : base(results) { }

    public void TestHashSink_SumHashes_HashableBoxArray_ReturnsSum()
    {
        // HashableBox.hashCode returns Int(value), so the expected sum is
        // just the sum of the input Int32 values widened to nint.
        using var sink = new HashSink();
        using var boxes = new SwiftArray<HashableBox>();
        boxes.Append(new HashableBox(value: 7));
        boxes.Append(new HashableBox(value: 35));

        var sum = (int)sink.SumHashes(boxes);

        AssertEqual(42, sum, "Sum of hashCodes should equal sum of underlying values");
    }

    public void TestHashSink_SumHashes_EmptyArray_ReturnsZero()
    {
        // Zero-length input must traverse the @_cdecl wrapper without
        // dereferencing past the buffer. Underlying Swift loop short-circuits.
        using var sink = new HashSink();
        using var empty = new SwiftArray<HashableBox>();

        var sum = (int)sink.SumHashes(empty);

        AssertEqual(0, sum, "Empty source should yield 0");
    }

    public void TestHashSink_SumHashes_PreservesElementCount()
    {
        // Every element must be visited inside the Swift `for` loop. With
        // hashCode == value, the sum 1+2+3+4 = 10 verifies count and
        // ordering simultaneously: a missed element would shift the total.
        using var sink = new HashSink();
        using var boxes = new SwiftArray<HashableBox>();
        boxes.Append(new HashableBox(value: 1));
        boxes.Append(new HashableBox(value: 2));
        boxes.Append(new HashableBox(value: 3));
        boxes.Append(new HashableBox(value: 4));

        var sum = (int)sink.SumHashes(boxes);

        AssertEqual(10, sum, "Sum of hashCodes for 1..4 should be 10");
    }
}
