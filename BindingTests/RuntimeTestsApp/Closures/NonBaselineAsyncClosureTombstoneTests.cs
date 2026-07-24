// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Runtime coverage for the non-baseline async-closure tombstone (the rive-ios
/// <c>RiveUIView.init(rive:)</c> shape). A sync member taking an escaping
/// <c>() async throws -&gt; SomeClass</c> closure is not a baseline async closure
/// (class return is non-blittable), so it must surface as an SB0005 tombstone: the
/// C# API exists but its body throws <see cref="NotSupportedException"/> — rather
/// than emitting a broken body that references an undeclared closure box / a
/// never-emitted trampoline and passes the raw delegate to a <c>Swift.AnyType</c>
/// P/Invoke parameter (the pre-fix CS0103 / CS1503 triple fault).
/// </summary>
public class NonBaselineAsyncClosureTombstoneTests : TestBase
{
    public NonBaselineAsyncClosureTombstoneTests(TestResults results) : base(results) { }

    /// <summary>
    /// The tombstoned constructor exists at the surface (compiles) and throws
    /// <see cref="NotSupportedException"/> when invoked — proving the member is
    /// reachable-but-unreachable, not a wholesale-dropped API.
    /// </summary>
    public void TestTombstonedConstructorThrowsNotSupported()
    {
        // Deliberately invoke the SB0005 tombstone: the whole point of the test is that
        // the obsolete surface EXISTS (compiles against the real (object?) signature) and
        // throws at runtime. TreatWarningsAsErrors (Directory.Build.props) would otherwise
        // promote the SB0005 obsolete warning to a build error here — suppress it at exactly
        // this call, leaving SB0005 active everywhere else so an unintended tombstone call
        // still fails the build (same pattern CdeclWrapperCohesionTests uses for SB0001).
#pragma warning disable SB0005
        AssertThrows<NotSupportedException>(
            () => { var _ = new NonBaselineAsyncClosureFactory((object?)null); },
            "Non-baseline async-closure constructor must throw NotSupportedException (SB0005 tombstone)");
#pragma warning restore SB0005
    }

    /// <summary>
    /// The tombstoned instance method exists and throws
    /// <see cref="NotSupportedException"/> when invoked. The surrounding class still
    /// constructs normally through its ordinary parameterless init.
    /// </summary>
    public void TestTombstonedMethodThrowsNotSupported()
    {
        var consumer = new NonBaselineAsyncClosureConsumer();
        AssertNotNull(consumer, "Consumer with an ordinary init still constructs");
        // Suppress SB0005 only around the tombstoned Configure invocation (the ordinary init
        // above is not obsolete); see the note in TestTombstonedConstructorThrowsNotSupported.
#pragma warning disable SB0005
        AssertThrows<NotSupportedException>(
            () => consumer.Configure((object?)null),
            "Non-baseline async-closure method must throw NotSupportedException (SB0005 tombstone)");
#pragma warning restore SB0005
    }
}
