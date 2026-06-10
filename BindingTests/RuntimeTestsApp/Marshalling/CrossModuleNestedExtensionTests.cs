// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLibDependency;
// Pin DependencyService/DependencyPoint to the SwiftBindingsTestLib mirror
// so DependencyService.HostedPayload / DependencyPoint.HostedTag resolve against
// the partial-class wrapper that hosts the nested types.
using DependencyService = SwiftBindingsTestLib.DependencyService;
using DependencyPoint = SwiftBindingsTestLib.DependencyPoint;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// SDK 0.11.0 R2 — regression coverage for nested types declared INSIDE
/// extensions of foreign-module types. A cross-module extension declares a nested
/// struct inside a class-receiver extension (e.g.
/// `extension SomeModule.ServiceClass { struct NestedSession {} }`),
/// then references that nested type from enum-case payloads in the same module.
///
/// Before the emitter fix, `CrossModuleExtensionEmitter` only recursed nested
/// types on the struct-receiver path, so class-receiver extensions silently dropped
/// the nested-type definitions. Downstream enum cases then lost their factories
/// (no `Completed(...)`) and extractors (no `TryGetCompleted`).
///
/// These tests assert both that the nested type IS emitted as a usable C# type
/// (constructor + property access) and that the downstream enum case factories
/// + extractors land for both single-payload and labeled-outer-tuple shapes
/// (the latter exercises <c>EnumCaseDecl.OuterTupleLabel</c>).
/// </summary>
public class CrossModuleNestedExtensionTests : TestBase
{
    public CrossModuleNestedExtensionTests(TestResults results) : base(results) { }

    // MARK: - Nested type emission (struct + property access)

    /// Locks the nested type itself: `DependencyService.HostedPayload` must be
    /// emitted in the current module, constructible via `new`, and its properties
    /// must round-trip. Compile-time success of `new DependencyService.HostedPayload(...)`
    /// is the gate — a missing nested type would fail to compile this test.
    public void TestHostedPayload_ConstructAndReadProperties()
    {
        using var payload = new DependencyService.HostedPayload("label-x", 42);
        AssertEqual("label-x", payload.Label.ToString(), "HostedPayload.Label round-trips");
        AssertEqual(42, payload.Count, "HostedPayload.Count round-trips");
    }

    /// Same as above for the frozen-struct receiver branch.
    public void TestHostedTag_ConstructAndReadValue()
    {
        var tag = new DependencyPoint.HostedTag(99);
        AssertEqual(99, tag.Value, "HostedTag.Value round-trips");
    }

    /// Methods on the mirrored nested type must also be reachable — emitting
    /// the partial-class host without its members would silently drop the
    /// method surface (the very regression shape that hides from a
    /// property-only test).
    public void TestHostedPayload_Describe_RoundTripsThroughMethod()
    {
        // Swift `describe() -> String` is emitted as `GetDescribe()` per the
        // generator's parameterless-return-value naming policy. The point of
        // this test is the *member surface* — proving the mirrored partial
        // carries methods, not just the constructor + properties.
        using var payload = new DependencyService.HostedPayload("describe-me", 7);
        AssertEqual("describe-me#7", payload.GetDescribe().ToString(),
            "HostedPayload.describe() round-trips through the mirrored method");
    }

    // MARK: - Single-payload labeled case (`.completed(payload:)`)

    public void TestCrossModuleNestedHostedResult_Completed_ExtractsNestedPayload()
    {
        using var result = TestLibFunctions.MakeCrossModuleNestedHostedCompleted("sess", 7);
        AssertEqual(CrossModuleNestedHostedResult.CaseTag.Completed, result.Tag,
            "Tag == Completed");

        AssertTrue(result.TryGetCompleted(out var payload),
            "TryGetCompleted on nested-payload result returns true");
        using (payload)
        {
            AssertEqual("sess", payload!.Label.ToString(), "Nested payload Label round-trips");
            AssertEqual(7, payload.Count, "Nested payload Count round-trips");
        }
    }

    public void TestCrossModuleNestedHostedResult_Canceled_TryGetsFail()
    {
        using var result = TestLibFunctions.MakeCrossModuleNestedHostedCanceled();
        AssertEqual(CrossModuleNestedHostedResult.CaseTag.Canceled, result.Tag,
            "Tag == Canceled");
        AssertFalse(result.TryGetCompleted(out var bogus),
            "TryGetCompleted returns false on Canceled");
        bogus?.Dispose();
    }

    public void TestCrossModuleNestedHostedResult_Failed_TryGetFailedSucceeds()
    {
        using var result = TestLibFunctions.MakeCrossModuleNestedHostedFailed("oops");
        AssertEqual(CrossModuleNestedHostedResult.CaseTag.Failed, result.Tag, "Tag == Failed");
        AssertTrue(result.TryGetFailed(out _), "TryGetFailed returns true on Failed");
        AssertFalse(result.TryGetCompleted(out var bogus),
            "TryGetCompleted returns false on Failed");
        bogus?.Dispose();
    }

    /// C#-side factory: `Completed(new DependencyService.HostedPayload(...))` must
    /// round-trip through Tag + TryGet. The factory's existence is itself the gate —
    /// it would not be emitted without nested-type mirror registration in the
    /// current module's TypeDatabase.
    public void TestCrossModuleNestedHostedResult_Completed_FactoryRoundTrip()
    {
        using var payload = new DependencyService.HostedPayload("cs", 5);
        using var result = CrossModuleNestedHostedResult.Completed(payload);
        AssertEqual(CrossModuleNestedHostedResult.CaseTag.Completed, result.Tag,
            "C#-built Completed has correct tag");

        AssertTrue(result.TryGetCompleted(out var roundTrip),
            "TryGetCompleted on C#-built Completed returns true");
        using (roundTrip)
        {
            AssertEqual("cs", roundTrip!.Label.ToString(), "C#-side factory round-trips Label");
            AssertEqual(5, roundTrip.Count, "C#-side factory round-trips Count");
        }
    }

    // MARK: - Two-top-level-associated-value case (`.completed(session:, token:)`)
    // Does NOT exercise EnumCaseDecl.OuterTupleLabel — Swift prints this as two
    // separate labeled associated values, so the emitter does not consult
    // OuterTupleLabel here. The OuterTupleLabel path is covered below in the
    // TestCrossModuleNestedOuterTupleResult_* block.

    public void TestCrossModuleNestedTokenResult_Completed_ExtractsBothPayloads()
    {
        using var result = TestLibFunctions.MakeCrossModuleNestedTokenCompleted("sess", 3, 99);
        AssertEqual(CrossModuleNestedTokenResult.CaseTag.Completed, result.Tag,
            "Tag == Completed");

        AssertTrue(result.TryGetCompleted(out var session, out var token),
            "TryGetCompleted(out session, out token) returns true");
        using (session)
        {
            AssertEqual("sess", session!.Label.ToString(),
                "Multi-payload session round-trips Label");
            AssertEqual(3, session.Count, "Multi-payload session round-trips Count");
        }
        using (token)
        {
            AssertNotNull(token, "Multi-payload token is non-null");
            AssertEqual(99, token!.Identifier,
                "Multi-payload token round-trips Identifier");
        }
    }

    public void TestCrossModuleNestedTokenResult_Completed_NoToken_ExtractsNullToken()
    {
        using var result = TestLibFunctions.MakeCrossModuleNestedTokenCompletedNoToken("sess", 1);
        AssertEqual(CrossModuleNestedTokenResult.CaseTag.Completed, result.Tag,
            "Tag == Completed (token = nil)");

        AssertTrue(result.TryGetCompleted(out var session, out var token),
            "TryGetCompleted returns true even when Optional token is nil");
        using (session)
        {
            AssertEqual("sess", session!.Label.ToString(),
                "Session payload still round-trips when token is nil");
        }
        AssertNull(token, "Token is null when Swift passed nil");
        token?.Dispose();
    }

    public void TestCrossModuleNestedTokenResult_Canceled_TryGetsFail()
    {
        using var result = TestLibFunctions.MakeCrossModuleNestedTokenCanceled();
        AssertEqual(CrossModuleNestedTokenResult.CaseTag.Canceled, result.Tag,
            "Tag == Canceled");

        AssertFalse(result.TryGetCompleted(out var bogusSession, out var bogusToken),
            "TryGetCompleted returns false on Canceled");
        bogusSession?.Dispose();
        bogusToken?.Dispose();
    }

    /// C#-side factory for the two-top-level-associated-value shape. The Swift
    /// case has two separate associated values, so this case does NOT exercise
    /// `EnumCaseDecl.OuterTupleLabel`; the OuterTupleLabel path is covered by
    /// `TestCrossModuleNestedOuterTupleResult_Completed_FactoryRoundTrip` below.
    public void TestCrossModuleNestedTokenResult_Completed_FactoryRoundTrip()
    {
        using var session = new DependencyService.HostedPayload("cs-sess", 11);
        using var token = new DependencyService.HostedToken(77);
        using var result = CrossModuleNestedTokenResult.Completed(session, token);
        AssertEqual(CrossModuleNestedTokenResult.CaseTag.Completed, result.Tag,
            "C#-built multi-payload Completed has correct tag");

        AssertTrue(result.TryGetCompleted(out var roundSession, out var roundToken),
            "TryGetCompleted on C#-built multi-payload returns true");
        using (roundSession)
        using (roundToken)
        {
            AssertEqual("cs-sess", roundSession!.Label.ToString(),
                "C#-side multi-payload factory round-trips session Label");
            AssertNotNull(roundToken, "C#-side multi-payload factory round-trips non-null token");
            AssertEqual(77, roundToken!.Identifier,
                "C#-side multi-payload factory round-trips token Identifier");
        }
    }

    // MARK: - True labeled-outer-tuple case (`.completed(payload: (session:, token:))`)
    // This is the case that actually exercises EnumCaseDecl.OuterTupleLabel:
    // the Swift ABI prints `completed(payload: (session:, token:))` so the
    // parser must capture `payload` as the outer label and the emitter must
    // rebuild the surface call positionally as `Completed(session, token)`.
    // CrossModuleNestedTokenResult above uses two top-level associated values
    // and does NOT hit this path.

    public void TestCrossModuleNestedOuterTupleResult_Completed_ExtractsInnerTuple()
    {
        using var result = TestLibFunctions.MakeCrossModuleNestedOuterTupleCompleted("outer", 4, 88);
        AssertEqual(CrossModuleNestedOuterTupleResult.CaseTag.Completed, result.Tag,
            "Tag == Completed (outer-tuple payload)");

        AssertTrue(result.TryGetCompleted(out var session, out var token),
            "TryGetCompleted on outer-tuple payload unwraps inner tuple positionally");
        using (session)
        using (token)
        {
            AssertEqual("outer", session!.Label.ToString(),
                "Outer-tuple session round-trips Label through OuterTupleLabel path");
            AssertEqual(4, session.Count,
                "Outer-tuple session round-trips Count through OuterTupleLabel path");
            AssertNotNull(token, "Outer-tuple token is non-null");
            AssertEqual(88, token!.Identifier,
                "Outer-tuple token round-trips Identifier through OuterTupleLabel path");
        }
    }

    public void TestCrossModuleNestedOuterTupleResult_Canceled_TryGetsFail()
    {
        using var result = TestLibFunctions.MakeCrossModuleNestedOuterTupleCanceled();
        AssertEqual(CrossModuleNestedOuterTupleResult.CaseTag.Canceled, result.Tag,
            "Tag == Canceled");
        AssertFalse(result.TryGetCompleted(out var bogusSession, out var bogusToken),
            "TryGetCompleted returns false on Canceled");
        bogusSession?.Dispose();
        bogusToken?.Dispose();
    }

    /// C#-side factory round-trip for the labeled-outer-tuple shape. Exercises the
    /// construction path through `EnumCaseWrapperEmitter.BuildCaseConstructionExpr`
    /// that emits `completed(payload: (session:, token:))` — i.e. the generator must
    /// wrap the two C# parameters into a labeled inner tuple under the `payload:`
    /// outer label, NOT pass them as two top-level associated values. A regression
    /// that emitted the wrong call shape (flattened, or omitting the outer label)
    /// would only be caught here; the Swift-side factory tests above do not.
    public void TestCrossModuleNestedOuterTupleResult_Completed_FactoryRoundTrip()
    {
        using var session = new DependencyService.HostedPayload("cs-outer", 13);
        using var token = new DependencyService.HostedToken(99);
        using var result = CrossModuleNestedOuterTupleResult.Completed(session, token);
        AssertEqual(CrossModuleNestedOuterTupleResult.CaseTag.Completed, result.Tag,
            "C#-built outer-tuple Completed has correct tag");

        AssertTrue(result.TryGetCompleted(out var roundSession, out var roundToken),
            "TryGetCompleted on C#-built outer-tuple returns true");
        using (roundSession)
        using (roundToken)
        {
            AssertEqual("cs-outer", roundSession!.Label.ToString(),
                "C#-built outer-tuple factory round-trips session Label");
            AssertEqual(13, roundSession.Count,
                "C#-built outer-tuple factory round-trips session Count");
            AssertNotNull(roundToken, "C#-built outer-tuple factory round-trips non-null token");
            AssertEqual(99, roundToken!.Identifier,
                "C#-built outer-tuple factory round-trips token Identifier");
        }
    }

    // MARK: - Frozen-struct receiver branch

    public void TestCrossModuleNestedFrozenStructResult_Completed_ExtractsNestedTag()
    {
        using var result = TestLibFunctions.MakeCrossModuleNestedFrozenStructCompleted(123);
        AssertEqual(CrossModuleNestedFrozenStructResult.CaseTag.Completed, result.Tag,
            "Tag == Completed (frozen-struct receiver)");

        AssertTrue(result.TryGetCompleted(out var tag),
            "TryGetCompleted on frozen-struct-receiver nested type returns true");
        AssertEqual(123, tag!.Value,
            "Frozen-struct-receiver nested type round-trips Value");
    }

    public void TestCrossModuleNestedFrozenStructResult_Canceled_TryGetsFail()
    {
        using var result = TestLibFunctions.MakeCrossModuleNestedFrozenStructCanceled();
        AssertEqual(CrossModuleNestedFrozenStructResult.CaseTag.Canceled, result.Tag,
            "Tag == Canceled");
        AssertFalse(result.TryGetCompleted(out _),
            "TryGetCompleted returns false on Canceled");
    }
}
