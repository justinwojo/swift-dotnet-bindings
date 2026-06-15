// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Regression coverage for the Alamofire <c>URLEncoding(arrayEncoding:)</c> SIGSEGV
/// (bare-closure-payload enum mis-sized as a simple Int32 enum). See
/// <c>BareClosurePayloadEnumConstructorParam.swift</c>.
///
/// <para>Root cause: <c>ArrayBracketEncoding</c> has a BARE (non-optional) closure
/// payload case (<c>.custom((String, Int) -&gt; String)</c>), which swift-api-digester
/// encodes as a TypeFunc node — neither a nominal nor a tuple. A parser that matched
/// only those two kinds recorded zero associated values for the case and lowered the
/// whole enum to a 4-byte simple enum. Passing a tag-only case (<c>.brackets</c>) into
/// <c>BracketEncodingConfig</c>'s by-value initializer then marshalled a 4-byte tag,
/// while the Swift wrapper read the real multi-word enum out of that undersized buffer
/// — an OOB read that crashed on ARC release.</para>
///
/// <para>The fix makes the enum a complex (associated-value) enum sized from its real
/// metadata. These tests prove the value round-trips by value through BOTH cdecl
/// surfaces — a struct initializer and a free function — without crashing, and that
/// the tag metadata is read correctly. The <c>.custom</c> closure case is not
/// constructible from C# (no factory is emitted for it); that is expected and
/// orthogonal — the regression was about the NON-closure cases being mis-sized.</para>
/// </summary>
public class BareClosurePayloadEnumConstructorParamTests : TestBase
{
    public BareClosurePayloadEnumConstructorParamTests(TestResults results) : base(results) { }

    public void TestEnumTagReadsCorrectlyForTagOnlyCases()
    {
        // Proves the enum is a complex enum whose metadata/value-witness table is
        // resolved (GetEnumTag) — not a degenerate 4-byte simple enum.
        AssertEqual(ArrayBracketEncoding.CaseTag.Brackets, ArrayBracketEncoding.Brackets.Tag,
            "ArrayBracketEncoding.Brackets reads Tag == Brackets");
        AssertEqual(ArrayBracketEncoding.CaseTag.NoBrackets, ArrayBracketEncoding.NoBrackets.Tag,
            "ArrayBracketEncoding.NoBrackets reads Tag == NoBrackets");
    }

    public void TestBracketsCaseConstructsConfigByValueWithoutCrash()
    {
        // The exact crash surface: the bare-closure-payload enum passed BY VALUE into a
        // struct initializer. Pre-fix this SIGSEGV'd (undersized 4-byte buffer); now the
        // enum is sized from real metadata and the value crosses intact.
        using var config = new BracketEncodingConfig(arrayEncoding: ArrayBracketEncoding.Brackets);
        AssertNotNull(config, "BracketEncodingConfig constructed from .brackets without crash");
        AssertEqual(true, config.UsesBrackets, "config.UsesBrackets is true for .brackets");
        AssertEqual("brackets:true", config.GetDescribe(), "describe() round-trips the .brackets case");
    }

    public void TestNoBracketsCaseConstructsConfigByValueWithoutCrash()
    {
        using var config = new BracketEncodingConfig(arrayEncoding: ArrayBracketEncoding.NoBrackets);
        AssertNotNull(config, "BracketEncodingConfig constructed from .noBrackets without crash");
        AssertEqual(false, config.UsesBrackets, "config.UsesBrackets is false for .noBrackets");
        AssertEqual("noBrackets:false", config.GetDescribe(), "describe() round-trips the .noBrackets case");
    }

    public void TestEnumPassesByValueThroughFreeFunction()
    {
        // Second, independent cdecl surface for the same undersized-buffer regression:
        // the enum value passed by value to a free function, returning a string.
        AssertEqual("brackets", TestLibFunctions.BracketEncodingLabel(ArrayBracketEncoding.Brackets),
            "bracketEncodingLabel(.brackets) == brackets");
        AssertEqual("noBrackets", TestLibFunctions.BracketEncodingLabel(ArrayBracketEncoding.NoBrackets),
            "bracketEncodingLabel(.noBrackets) == noBrackets");
    }
}
