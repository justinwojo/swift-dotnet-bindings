// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// A parent generic is specialized only for conformers whose C# projection satisfies the parent's
/// <c>where</c> clause. <c>WireNote</c> implements <c>IPlainWireMessage</c> but not
/// <c>IDecodableWireMessage</c> (its generic requirement has no C# implementation), so only
/// <c>PlainWireField&lt;WireNote&gt;</c> is specialized; a <c>DecodableWireField&lt;WireNote&gt;</c>
/// specialization would not compile, which the compile gate over this binding proves.
/// </summary>
public class WhereClauseConformerSpecializationTests : TestBase
{
    public WhereClauseConformerSpecializationTests(TestResults results) : base(results) { }

    public void TestSatisfyingConformerSpecializationConstructsAndReads()
    {
        using var tagged = PlainWireFieldSwiftBindingsTestLib_WireNoteCsmExtensions
            .FromSwiftBindingsTestLibWireNote(new WireNote(7));
        AssertTrue(tagged.IsTagged, "specialized PlainWireField<WireNote> carried its non-zero tag");

        using var untagged = PlainWireFieldSwiftBindingsTestLib_WireNoteCsmExtensions
            .FromSwiftBindingsTestLibWireNote(new WireNote(0));
        AssertFalse(untagged.IsTagged, "specialized PlainWireField<WireNote> carried its zero tag");
    }
}
