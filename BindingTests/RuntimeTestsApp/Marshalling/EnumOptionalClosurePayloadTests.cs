// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Runtime coverage for the enum-case <c>Optional&lt;Closure&gt;</c> payload shape
/// (audit item F16). The compile question — does the construction path emit
/// compilable C# for a case carrying <c>Optional&lt;Closure&gt;</c>? — is settled
/// (refuted) by the <see cref="ClosureCarrier"/> binding emitting and compiling;
/// see OptionalClosurePayloadEnum.swift.
///
/// This file pins the runtime surface that is actually reachable from C#:
/// the enum type itself resolves metadata, its no-payload <c>.None</c> case
/// constructs, and a <c>ClosureCarrier</c> value round-trips by VALUE through
/// the cdecl free functions (<c>invokeCarrierHandler</c>, <c>carrierLabel</c>).
///
/// NOT covered — and not coverable from C# today — are the
/// <c>.WithHandler</c> / <c>.Labeled</c> case factories with an actual payload.
/// Both take a <c>SwiftOptional&lt;Func&lt;int, string, bool&gt;&gt;</c>, and that
/// argument cannot be materialized: constructing (or even statically touching)
/// <c>SwiftOptional&lt;Func&lt;…&gt;&gt;</c> resolves <c>Optional</c> metadata
/// specialized on a function type, which requires Swift function-type metadata
/// for a C# delegate — <c>TypeMetadata.GetTypeMetadataOrThrow&lt;Func&lt;…&gt;&gt;</c>
/// returns false (no <c>swift_getFunctionTypeMetadata</c> bridge for arbitrary
/// managed delegates). This is a broad, pre-existing runtime limitation, not an
/// enum-specific defect; the payload cases are compile-verified only.
/// </summary>
public class EnumOptionalClosurePayloadTests : TestBase
{
    public EnumOptionalClosurePayloadTests(TestResults results) : base(results) { }

    public void TestNoneSingletonConstructs()
    {
        var carrier = ClosureCarrier.None;
        AssertNotNull(carrier, "ClosureCarrier.None singleton exists");
        AssertEqual(ClosureCarrier.CaseTag.None, carrier.Tag, "None tag");
    }

    public void TestNonePassesByValueThroughInvoke()
    {
        // Exercises the cdecl free-function ABI: the enum value crosses by value
        // (carrier.Payload.DangerousGetHandle()) and Swift's switch hits .none → false.
        var result = TestLibFunctions.InvokeCarrierHandler(ClosureCarrier.None, code: 7, message: "ignored");
        AssertEqual(false, result, "InvokeCarrierHandler(.none) returns false");
    }

    public void TestNoneCarrierLabelRoundTrips()
    {
        // Second free function: enum value in, UTF-8 string out (carrierLabel cdecl wrapper).
        var label = TestLibFunctions.CarrierLabel(ClosureCarrier.None);
        AssertEqual("<none>", label, "CarrierLabel(.none) returns <none>");
    }
}
