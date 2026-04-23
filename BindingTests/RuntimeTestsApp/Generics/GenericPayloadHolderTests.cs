// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Regression coverage for Issue E.1 — bare-generic-type-parameter payload
/// extraction on a generic enum. Mirrors StoreKit2.VerificationResult&lt;T&gt;
/// at the ABI level: payload case <c>.wrapped(T)</c> and empty case <c>.empty</c>.
/// EnumHandler.Marshalling.cs now has an explicit branch for τ_0_0 in all
/// three EmitPayloadMarshal overloads, matching the factory direction's use
/// of TypeMetadata&lt;T&gt;.Size + MarshalToSwift&lt;T&gt;.
///
/// Exercised across both a frozen-struct T (SwiftString — fixed-size VWT copy)
/// and a class T (IntBox — ARC retain/release). Must pass on Mono JIT (sim) AND
/// NativeAOT (device) because generic-enum payload marshalling differs enough
/// between runtimes that single-runtime coverage is insufficient.
/// </summary>
public class GenericPayloadHolderTests : TestBase
{
    public GenericPayloadHolderTests(TestResults results) : base(results) { }

    public void TestHolderOfString_Wrapped_ExtractsPayload()
    {
        using var holder = TestLibFunctions.MakeWrappedString("hello");
        AssertEqual(Holder<SwiftString>.CaseTag.Wrapped, holder.Tag, "Tag == Wrapped");

        AssertTrue(holder.TryGetWrapped(out var payload), "TryGetWrapped returns true");
        using (payload)
        {
            AssertEqual("hello", payload!.ToString(), "Extracted payload round-trips");
        }
    }

    public void TestHolderOfString_Empty_TryGetFails()
    {
        using var holder = TestLibFunctions.MakeEmptyString();
        AssertEqual(Holder<SwiftString>.CaseTag.Empty, holder.Tag, "Tag == Empty");

        AssertFalse(holder.TryGetWrapped(out var payload), "TryGetWrapped returns false on Empty");
        payload?.Dispose();
    }

    public void TestHolderOfIntBox_Wrapped_ExtractsPayload()
    {
        using var holder = TestLibFunctions.MakeWrappedIntBox(42);
        AssertEqual(Holder<IntBox>.CaseTag.Wrapped, holder.Tag, "Tag == Wrapped");

        AssertTrue(holder.TryGetWrapped(out var payload), "TryGetWrapped returns true");
        using (payload)
        {
            AssertEqual(42, payload!.Value, "Extracted IntBox.value round-trips");
        }
    }

    public void TestHolderOfIntBox_Empty_TryGetFails()
    {
        using var holder = TestLibFunctions.MakeEmptyIntBox();
        AssertEqual(Holder<IntBox>.CaseTag.Empty, holder.Tag, "Tag == Empty");

        AssertFalse(holder.TryGetWrapped(out var payload), "TryGetWrapped returns false on Empty");
        payload?.Dispose();
    }

    // AppleHolder<SignedType> mirrors VerificationResult<SignedType> from StoreKit2:
    // a multi-character SUGARED generic-parameter name that is NOT in the simple-letter
    // shortlist used by IsGenericTypeParameter. Without the sugared-name resolution in
    // TryGetGenericTypeParameterName, GetCSharpTypeNameForEnumCase falls through to
    // AnyType and EmitTryGetMethod silently skips — leaving the enum read-only.

    public void TestAppleHolderOfString_Verified_ExtractsPayload()
    {
        using var holder = TestLibFunctions.MakeVerifiedAppleString("hello");
        AssertEqual(AppleHolder<SwiftString>.CaseTag.Verified, holder.Tag, "Tag == Verified");

        AssertTrue(holder.TryGetVerified(out var payload), "TryGetVerified returns true");
        using (payload)
        {
            AssertEqual("hello", payload!.ToString(), "Extracted payload round-trips");
        }
    }

    public void TestAppleHolderOfString_Invalid_TryGetFails()
    {
        using var holder = TestLibFunctions.MakeInvalidAppleString();
        AssertEqual(AppleHolder<SwiftString>.CaseTag.Invalid, holder.Tag, "Tag == Invalid");

        AssertFalse(holder.TryGetVerified(out var payload), "TryGetVerified returns false on Invalid");
        payload?.Dispose();
    }

    public void TestAppleHolderOfIntBox_Verified_ExtractsPayload()
    {
        using var holder = TestLibFunctions.MakeVerifiedAppleIntBox(42);
        AssertEqual(AppleHolder<IntBox>.CaseTag.Verified, holder.Tag, "Tag == Verified");

        AssertTrue(holder.TryGetVerified(out var payload), "TryGetVerified returns true");
        using (payload)
        {
            AssertEqual(42, payload!.Value, "Extracted IntBox.value round-trips");
        }
    }

    public void TestAppleHolderOfIntBox_Invalid_TryGetFails()
    {
        using var holder = TestLibFunctions.MakeInvalidAppleIntBox();
        AssertEqual(AppleHolder<IntBox>.CaseTag.Invalid, holder.Tag, "Tag == Invalid");

        AssertFalse(holder.TryGetVerified(out var payload), "TryGetVerified returns false on Invalid");
        payload?.Dispose();
    }
}
