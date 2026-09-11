// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Wrapper shapes whose emitted code has to be spelled in the declared return type rather than a
/// convenient stand-in, plus one whose C# body has to keep two same-named locals apart.
///
/// <para>
/// Each of these is a compile assertion before it is a value assertion, and the compile failure is
/// silent: a Swift wrapper that does not build is withdrawn and the member drops off the wrapper
/// route entirely (for the C# one, the whole binding fails to build). So the load-bearing part of
/// every test here is that the member is REACHABLE at all — the value checks confirm the wrapper is
/// also wired to the right function once it exists.
/// </para>
/// </summary>
public class CdeclWrapperReturnShapeTests : TestBase
{
    public CdeclWrapperReturnShapeTests(TestResults results) : base(results) { }

    // MARK: Generic-static-dispatch wrapper, throwing, pointer-shaped return

    /// A member on a generic parent dispatches through the `_SBW_GSM_*` protocol wrapper. Throwing
    /// plus a class return makes the wrapper's catch arm produce a value of the declared `@_cdecl`
    /// return type — a raw pointer, which an integer sentinel does not convert to.
    public void TestGenericStaticDispatch_ThrowingClassReturn_Succeeds()
    {
        using var request = new TokenRequest<int>(code: 7);
        using var receipt = request.GetIssueReceipt();
        AssertEqual(7, receipt.Code, "TokenRequest<Int>.issueReceipt() throwing class return");
    }

    /// The same wrapper's failure path. The catch arm is only reached when the Swift call actually
    /// throws, which is what proves the arm compiled into something callable rather than having
    /// been dropped along with the wrapper. The thrown type also shows the error survived
    /// classification rather than arriving as a bare description.
    public void TestGenericStaticDispatch_ThrowingClassReturn_PropagatesError()
    {
        using var request = new TokenRequest<int>(code: -3);
        AssertThrows<SwiftException<TokenError>>(
            () => request.GetIssueReceipt().Dispose(),
            "issueReceipt() should surface TokenError.refused for a negative code");
    }

    /// The Optional-class arm takes a different sentinel (`nil`) from the non-optional one above,
    /// so it is a separate compile shape rather than a restatement of the same test.
    public void TestGenericStaticDispatch_ThrowingOptionalClassReturn_Succeeds()
    {
        using var present = new TokenRequest<int>(code: 11);
        using var receipt = present.GetIssueOptionalReceipt();
        AssertNotNull(receipt, "issueOptionalReceipt() should return a receipt for a positive code");
        AssertEqual(11, receipt!.Code, "TokenRequest<Int>.issueOptionalReceipt() payload");

        using var absent = new TokenRequest<int>(code: 0);
        using var none = absent.GetIssueOptionalReceipt();
        AssertNull(none, "issueOptionalReceipt() should return null for a zero code");
    }

    public void TestGenericStaticDispatch_ThrowingOptionalClassReturn_PropagatesError()
    {
        using var request = new TokenRequest<int>(code: -1);
        AssertThrows<SwiftException<TokenError>>(
            () => request.GetIssueOptionalReceipt()?.Dispose(),
            "issueOptionalReceipt() should surface TokenError.refused for a negative code");
    }

    // MARK: Closure returns, where the return type is written in a position that rejects `@escaping`

    /// A closure is not C-representable, so it leaves the wrapper through the indirect-result
    /// buffer, and that buffer is initialized through a METATYPE (`initializeMemory(as: X.self)`) —
    /// a position where `@escaping` is illegal Swift. The closure also needs an invoke thunk on
    /// both sides of the boundary, and the C# half of that thunk is a <c>[DllImport]</c>, which
    /// cannot be declared inside a generic type; on a generic parent it and its invoker class move
    /// into the non-generic <c>{Parent}_PInvoke</c> helper. Any of those going wrong withdraws the
    /// member silently, so reaching the call at all is the primary assertion.
    public void TestGenericStaticDispatch_ClosureReturn_Invokes()
    {
        using var request = new TokenRequest<int>(code: 42);

        var reader = request.MakeCodeReader();
        AssertNotNull(reader, "TokenRequest<Int>.makeCodeReader() should return a delegate");
        AssertEqual(42, reader(), "makeCodeReader()'s closure should report the captured code");
    }

    /// The same return-position question on the other reroute lane: a method-level generic opens
    /// into nested LOCAL generic functions, so the closure return is written into those functions'
    /// own RETURN CLAUSES rather than a metatype — also a position `@escaping` is illegal in. The
    /// parent is not generic here, so the invoke thunk's <c>[DllImport]</c> stays in place and the
    /// hoist above must not fire.
    public void TestMethodLevelGenericOpening_ClosureReturn_Invokes()
    {
        using var spec = new ColumnSpec(name: "created_at");
        using var seed = new SimpleDescribable(description: "abcdefg");

        var reader = spec.LengthReader(seed);
        AssertNotNull(reader, "ColumnSpec.lengthReader(seededBy:) should return a delegate");
        AssertEqual(7, reader(), "lengthReader(seededBy:)'s closure should report the seed's length");
    }

    // MARK: Method-level-generic opening wrapper returning `Self`

    /// The opened body of a method-level-generic wrapper is a LOCAL function, which has no
    /// enclosing type for `Self` to resolve against — it has to be written as the parent type. The
    /// receiver is reconstructed as the concrete parent before the call, so that substitution is
    /// also the static type the inner call produces.
    ///
    /// <para>The returned value aliases the receiver natively, but the binding still hands back its
    /// OWN managed wrapper holding its own reference — native aliasing is not shared managed
    /// ownership — so it is disposed independently of <c>spec</c>.</para>
    public void TestMethodLevelGenericOpening_SelfReturn_RoundTrips()
    {
        using var spec = new ColumnSpec(name: "created_at");
        using var value = new SimpleDescribable(description: "now()");

        using var defaulted = spec.Defaulting(value);

        AssertEqual("created_at", defaulted.Name, "ColumnSpec.defaulting(to:) -> Self identity");
        AssertEqual("now()", defaulted.DefaultDescription, "ColumnSpec.defaulting(to:) applied the value");
        AssertEqual("now()", spec.DefaultDescription, "defaulting(to:) mutated the receiver it returned");
    }

    // MARK: A parameter whose derived locals collide with the synthetic ones

    /// The wrapper body names its indirect-result buffer `resultPtr` by convention and ALSO derives
    /// `{param}Ptr` from each existential parameter. A parameter spelled `result` puts both in one
    /// method body; the synthetic one is the side that has to move.
    ///
    /// <para>Each return aliases an argument natively, but the binding marshals it into a fresh
    /// proxy that owns its own existential container, so each is disposed on its own.</para>
    public void TestParameterDerivedLocalCollision_ExistentialResultParameter()
    {
        using var first = new FixedMeasure(value: 3);
        using var second = new FixedMeasure(value: 8);

        // The protocol itself is not IDisposable — the proxy the binding returns is — so the
        // release goes through a pattern match rather than a `using` on the interface type.
        var preferred = Functions.ChooseMeasurable(preferFirst: true, result: first, fallback: second);
        try
        {
            AssertEqual(3, preferred.Measure(), "chooseMeasurable(preferFirst: true) returned the 'result' argument");
        }
        finally
        {
            (preferred as IDisposable)?.Dispose();
        }

        var fallback = Functions.ChooseMeasurable(preferFirst: false, result: first, fallback: second);
        try
        {
            AssertEqual(8, fallback.Measure(), "chooseMeasurable(preferFirst: false) returned the fallback argument");
        }
        finally
        {
            (fallback as IDisposable)?.Dispose();
        }
    }
}
