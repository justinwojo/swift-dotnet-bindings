// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;
// The test library exports a public `Type` on purpose, to prove the generator qualifies the
// BCL names it emits. That makes a bare `Type` ambiguous here, where the reflection type is meant.
using Type = System.Type;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Coverage for the Sendable-annotation gap.
/// .NET has no built-in equivalent of Swift's <c>Sendable</c> marker, so the
/// generator surfaces it via <see cref="SwiftSendableAttribute"/>. The contract:
///
///   • Every Swift type whose conformance list includes <c>Sendable</c> must
///     be marked with <c>[SwiftSendable]</c> on the generated C# type.
///   • Types without Sendable conformance must NOT be marked.
///
/// This test asserts the projection at the metadata level so a regression
/// (predicate widening, attribute drop, handler refactor) fails immediately
/// rather than silently reverting to "0% projected" 0.10.0 behaviour.
/// </summary>
public class SendableAnnotationTests : TestBase
{
    public SendableAnnotationTests(TestResults results) : base(results) { }

    public void TestSendableStructEmitsAttribute()
    {
        AssertHasSendable(typeof(SendablePoint));
    }

    public void TestSendableClassEmitsAttribute()
    {
        AssertHasSendable(typeof(SendableConfig));
    }

    public void TestSendableEnumEmitsAttribute()
    {
        AssertHasSendable(typeof(SendableSeverity));
    }

    public void TestSendableBareStructEmitsAttribute()
    {
        // Negative-control twin: SendableTokenOnly has no other conformances,
        // so the only reason it could carry the attribute is the Sendable
        // projection itself.
        AssertHasSendable(typeof(SendableTokenOnly));
    }

    public void TestNonSendableStructDoesNotEmitAttribute()
    {
        var type = typeof(NotSendablePlain);
        var attr = type.GetCustomAttribute<SwiftSendableAttribute>(inherit: false);
        AssertTrue(attr == null,
            $"{type.Name} has no Swift Sendable conformance; [SwiftSendable] must NOT be emitted.");
    }

    public void TestSendableValueRoundTripsAcrossThreads()
    {
        // Behavioural sanity: a Sendable value type really is safe to read
        // from multiple .NET threads. The attribute is informational, but the
        // round-trip ensures the test fixture itself doesn't degenerate into
        // a doc-only check.
        var p = new SendablePoint(x: 3, y: 4);
        AssertEqual(7, p.GetManhattanDistance(), "manhattanDistance baseline");

        int observed = -1;
        Parallel.For(0, 64, _ =>
        {
            var d = p.GetManhattanDistance();
            Volatile.Write(ref observed, d);
        });
        AssertEqual(7, observed, "manhattanDistance read concurrently");
    }

    private void AssertHasSendable(Type type)
    {
        var attr = type.GetCustomAttribute<SwiftSendableAttribute>(inherit: false);
        AssertTrue(attr != null,
            $"{type.Name} conforms to Swift.Sendable; expected [SwiftSendable] on the generated type.");
    }
}
