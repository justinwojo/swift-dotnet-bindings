// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.FoundationInterop;

/// <summary>
/// Foreign-type-extension bug repros: members added to <c>NSObject</c> (a foreign,
/// module-external ObjC root class) route through <c>ForeignTypeExtensionEmitter</c>'s
/// self_-reconstruction path — a different emission path than same-module method/property
/// handling. Covers bug (a) sub-case a-2 (SimpleEnum parameter) and bug (c) (a parameter
/// literally named the contextual keyword `extension`).
///
/// Bug (h) (variadic parameter on a foreign-type extension method) has no runtime
/// coverage here by design: the fix declines the member outright (a clean skip — Swift's
/// `total(_:Int32...)` never emits a wrapper at all), so there is nothing to call from C#.
/// Verified via <c>binding-emission-report.json</c>'s <c>variadic_parameter</c> skip count and
/// by confirming <c>NSObjectSwiftBindingsTestLibExtensions</c> only exposes
/// <c>Classify</c>/<c>Tagged</c>, never a `Total`/`total` member.
/// </summary>
public class ForeignExtensionBugReprosTests : TestBase
{
    public ForeignExtensionBugReprosTests(TestResults results) : base(results) { }

    public void TestClassify_SimpleEnumParameterOnForeignExtension()
    {
        // classify(status:) on NSObject — pre-fix, a non-primitive (SimpleEnum) parameter
        // on a foreign-type extension emitted an illegal `Unmanaged<AnyObject>` cast.
        using var obj = new Foundation.NSObject();
        var result = obj.Classify(ForeignExtensionClassification.Unclassified);
        AssertEqual(ForeignExtensionClassification.Flagged, result, "classify(.unclassified) returns .flagged");

        var result2 = obj.Classify(ForeignExtensionClassification.Flagged);
        AssertEqual(ForeignExtensionClassification.Verified, result2, "classify(.flagged) returns .verified");
    }

    public void TestTagged_KeywordNamedParameterOnForeignExtension()
    {
        // tagged(extension:) on NSObject — pre-fix, the hand-rolled keyword table didn't
        // cover the contextual keyword `extension`, so the internal Swift wrapper binding
        // was emitted unescaped and rejected by swiftc.
        using var obj = new Foundation.NSObject();
        var result = obj.Tagged(41);
        AssertEqual(42, result, "tagged(extension: 41) returns 42");
    }
}
