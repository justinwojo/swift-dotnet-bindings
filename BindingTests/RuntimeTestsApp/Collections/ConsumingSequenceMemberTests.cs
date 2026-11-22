// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collections;

/// <summary>
/// <c>ConsumingTally</c>'s members are public whatever follows <c>public</c> in the interface:
/// <c>__consuming func makeIterator()</c> and the keyword-named <c>`repeat`(count:)</c> and
/// <c>`default`(extra:)</c> once read as module-internal and were withdrawn. Each call checks the
/// values Swift produced, and <c>_default</c>'s trimmed overload checks the default the interface
/// reader recovered for a keyword-named member.
/// </summary>
public class ConsumingSequenceMemberTests : TestBase
{
    public ConsumingSequenceMemberTests(TestResults results) : base(results) { }

    public void TestMakeIterator_WalksTheSequence()
    {
        using var tally = new ConsumingTally(3);
        using var iterator = tally.MakeIterator();
        AssertEqual((nint?)1, iterator.Next(), "first element");
        AssertEqual((nint?)2, iterator.Next(), "second element");
        AssertEqual((nint?)3, iterator.Next(), "third element");
        AssertEqual((nint?)null, iterator.Next(), "exhausted");
    }

    public void TestRepeat_KeywordNamedMethodRuns()
    {
        using var tally = new ConsumingTally(2);
        using var repeated = tally.Repeat(3);
        AssertEqual(6, repeated.Count, "repeat(count:) multiplies the count");
        AssertEqual(2, tally.Count, "receiver unchanged");
    }

    public void TestDefault_KeywordNamedMethodKeepsItsDefault()
    {
        using var tally = new ConsumingTally(4);
        using var explicitExtra = tally._default(5);
        AssertEqual(9, explicitExtra.Count, "default(extra:) adds the argument");
        using var defaulted = tally._default();
        AssertEqual(5, defaulted.Count, "default(extra:) fills extra = 1");
    }
}
