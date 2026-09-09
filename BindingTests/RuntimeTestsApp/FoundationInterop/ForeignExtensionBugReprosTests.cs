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
/// by confirming <c>NSObjectSwiftBindingsTestLibExtensions</c> carries no `Total`/`total`
/// member — the declined one is the only member of that extension absent from the binding.
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

    public void TestSwiftClassReturnOutlivesTheWrapperThatBuiltIt()
    {
        // A Swift class returned from a foreign-type extension is constructed inside the emitted
        // Swift wrapper, so that wrapper's own local is the only thing holding it when the function
        // returns. The managed peer adopts the reference it is handed, so the wrapper has to pass
        // ownership across rather than lend it — reading a field back afterwards is what proves the
        // object was still there to read.
        using var obj = new Foundation.NSObject();

        using var fromMethod = obj.Receipt(4);
        AssertEqual(12, fromMethod.Code, "a class returned from an extension method survives the call");

        using var fromProperty = obj.GetReceiptStamp();
        AssertEqual(7, fromProperty.Code, "a class returned from an extension property survives the call");
    }
}
