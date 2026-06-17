// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.EdgeCases;

/// <summary>
/// Forward witness-dispatch index lockstep (regression R5-1a).
///
/// <c>WitnessIndexProto</c> declares two <c>consume</c> overloads whose parameters are
/// distinct parameterized-PAT existentials (each degrades to the same C# projection)
/// followed by the dispatchable required <c>tag(_:Int32) -&gt; Int32</c>. Both overloads
/// are witness-dispatch eligible, so they consume slot indices 0 and 1; <c>tag</c> must
/// therefore occupy index 2 in the <c>SBW_WitnessIndexProto_method_tag_2</c> symbol on the
/// Swift @_cdecl producer.
///
/// The defect: the C# consumer walks allocated the slot index from a key gated on the
/// PROJECTED C# type, which collapses the two <c>consume(object)</c> overloads to a single
/// index — so <c>tag</c> resolved <c>SBW_..._tag_1</c> while the producer exported
/// <c>SBW_..._tag_2</c>, throwing <see cref="System.EntryPointNotFoundException"/> at the
/// first call. The fix routes the slot-index key through the raw-Swift producer key on all
/// three walks. A green run here proves the index realigned: the proxy obtains a Swift-vended
/// <c>any WitnessIndexProto</c> and calls <c>tag</c> through the witness path.
/// </summary>
public class WitnessDispatchIndexLockstepTests : TestBase
{
    public WitnessDispatchIndexLockstepTests(TestResults results) : base(results) { }

    public void TestForwardDispatchTagIndexUnshiftedAfterAnyTypeOverloads()
    {
        var proto = Functions.MakeWitnessIndexConformer();
        AssertNotNull(proto, "Swift vended a non-null any WitnessIndexProto");

        // Forward dispatch through SBW_WitnessIndexProto_method_tag_2. If the consumer's
        // index had collapsed the two unresolvable consume overloads, this would resolve a
        // nonexistent SBW_..._tag_1 and throw EntryPointNotFoundException before returning.
        var result = proto.Tag(41);
        AssertEqual(42, result,
            "Forward witness dispatch landed on tag() — SBW index not shifted by AnyType-collapsing overloads");
    }
}
