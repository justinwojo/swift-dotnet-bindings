// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ErrorHandling;

/// <summary>
/// F42 error-identity carriage. Two directions, both previously identity-lossy:
///
/// <para><b>Forward (Swift throws → C# catches).</b> The untyped throw path
/// (<see cref="SwiftMarshal.ThrowSwiftError"/>) used to read the description and then
/// <i>eagerly release</i> the Swift error box before throwing a message-only
/// <see cref="SwiftException"/> — so the live error identity was gone by the time the
/// consumer's catch ran. Now the box is retained and carried on
/// <see cref="SwiftException.ErrorHandle"/> (released on finalization under the
/// process-exit guard). The load-bearing assertion re-derives the description from the
/// carried handle <i>after</i> the catch: under the old eager-release behavior that read
/// would be a use-after-free.</para>
///
/// <para><b>Reverse (C# throws → Swift recovers).</b> A C# throwing closure that throws a
/// plain managed exception must not unwind into native Swift; the generated callback mints
/// a Swift <c>NSError</c> (domain "SwiftBindings") whose userInfo carries the originating
/// CLR type name under "SwiftBindingsManagedExceptionType" and the message under
/// NSLocalizedDescriptionKey. The fixtures recover both on the Swift side and hand them
/// back, proving the managed exception's identity — not just a flattened string — survived
/// the boundary.</para>
/// </summary>
public class ErrorIdentityCarriageTests : TestBase
{
    public ErrorIdentityCarriageTests(TestResults results) : base(results) { }

    // The per-module description reader, exported by the generated wrapper framework. Re-deriving
    // the description from the carried handle proves the box is still alive post-throw. The library
    // name and entry point mirror the generated binding's own LibraryImport ("SwiftBindings" is the
    // wrapper framework; the symbol is module-suffixed).
    [DllImport("SwiftBindings", EntryPoint = "SBW_GetErrorDescription_SwiftBindingsTestLib")]
    private static extern IntPtr SBW_GetErrorDescription(IntPtr error);

    #region Forward path — untyped throw carries the live error box

    /// <summary>
    /// An untyped Swift throw surfaces as <see cref="SwiftException"/> carrying a non-zero,
    /// still-live <see cref="SwiftException.ErrorHandle"/>; the description re-derived from
    /// that handle after the catch still resolves (old eager-release behavior = UAF).
    /// </summary>
    public void TestUntypedThrowCarriesLiveErrorHandle()
    {
        try
        {
            TestLibFunctions.Divide(10, 0);
            throw new AssertionException("Divide by zero should throw");
        }
        catch (SwiftException ex)
        {
            AssertTrue(ex.ErrorHandle != IntPtr.Zero,
                "SwiftException must carry the live Swift error box (non-zero ErrorHandle)");
            AssertTrue(ex.Message.Contains("divisionByZero"),
                $"Flattened message must still resolve, got: {ex.Message}");

            // Re-derive from the STILL-LIVE handle. ReadErrorDescription frees the returned
            // C string; the error box itself is owned by `ex` and released on finalization.
            var reDerived = SwiftMarshal.ReadErrorDescription(SBW_GetErrorDescription(ex.ErrorHandle));
            TestLogger.Info($"Re-derived description from carried handle: \"{reDerived}\"");
            AssertTrue(reDerived.Contains("divisionByZero"),
                $"Re-derived description from the carried handle must resolve, got: {reDerived}");
        }
    }

    /// <summary>
    /// The message-only construction path (no native box) reports
    /// <see cref="IntPtr.Zero"/> — the guard that keeps <see cref="ErrorHandle"/> honest for
    /// consumers and suppresses finalization when there is nothing to release.
    /// </summary>
    public void TestMessageOnlyExceptionHasZeroHandle()
    {
        var ex = new SwiftException("no native box here");
        AssertTrue(ex.ErrorHandle == IntPtr.Zero,
            "A message-only SwiftException must report ErrorHandle == Zero");
    }

    /// <summary>
    /// A throw from a method routed through the <c>GenericClosureBridge</c> (a method-generic,
    /// throwing-closure-bearing method) also carries the live error box. Before F42 this path
    /// hand-rolled an eager release + message-only <c>SwiftRuntimeException</c>; it now routes
    /// through the single <see cref="SwiftMarshal.ThrowSwiftError"/> source like the canonical path.
    /// </summary>
    public void TestGenericClosureBridgeThrowCarriesLiveErrorHandle()
    {
        using var reader = new DatabaseReader("outer");
        using var source = new DatabaseReader("primary");
        try
        {
            // readThenThrow invokes the closure successfully, then throws DatabaseReadError.afterRead.
            reader.ReadThenThrow<DatabaseReader>(db => db, source);
            throw new AssertionException("readThenThrow should throw");
        }
        catch (SwiftException ex)
        {
            AssertTrue(ex.ErrorHandle != IntPtr.Zero,
                "GenericClosureBridge throw must carry the live Swift error box (non-zero ErrorHandle)");
            var reDerived = SwiftMarshal.ReadErrorDescription(SBW_GetErrorDescription(ex.ErrorHandle));
            TestLogger.Info($"GenericClosureBridge re-derived description: \"{reDerived}\"");
            AssertTrue(reDerived.Contains("afterRead"),
                $"Re-derived description from the carried handle must resolve, got: {reDerived}");
        }
    }

    /// <summary>
    /// A throw from a Swift protocol requirement dispatched through the generated witness proxy
    /// (<c>ThrowingWitnessProxy</c>) carries the live error box. The shared proxy error helper now
    /// routes through <see cref="SwiftMarshal.ThrowSwiftError"/> instead of eagerly releasing the box
    /// and throwing a message-only exception.
    /// </summary>
    public void TestWitnessDispatchThrowCarriesLiveErrorHandle()
    {
        var witness = TestLibFunctions.MakeThrowingWitness();
        AssertEqual(6, witness.TagOrThrow(5), "non-throwing witness call returns value + 1");
        try
        {
            witness.TagOrThrow(-1);
            throw new AssertionException("negative input should throw");
        }
        catch (SwiftException ex)
        {
            AssertTrue(ex.ErrorHandle != IntPtr.Zero,
                "Witness-dispatch throw must carry the live Swift error box (non-zero ErrorHandle)");
            var reDerived = SwiftMarshal.ReadErrorDescription(SBW_GetErrorDescription(ex.ErrorHandle));
            TestLogger.Info($"Witness-dispatch re-derived description: \"{reDerived}\"");
            AssertTrue(reDerived.Contains("negative"),
                $"Re-derived description from the carried handle must resolve, got: {reDerived}");
        }
    }

    #endregion

    #region Reverse path — C# exception identity round-trips through NSError userInfo

    /// <summary>
    /// A C# closure throwing a plain managed exception: Swift recovers the originating CLR
    /// type name from the minted NSError's userInfo.
    /// </summary>
    public void TestReversePathCarriesManagedExceptionType()
    {
        // The ergonomic Action overload (catch-and-mint path) is an instance method on Functions;
        // the raw Func<SwiftResult<…>> overload is static. Explicitly typing the local also avoids
        // overload ambiguity (a throw-expression lambda is convertible to both).
        var functions = new SwiftBindingsTestLib.Functions();
        Action thrower = () => throw new InvalidOperationException("reverse-boom");
        var recoveredType = functions.RecoverManagedExceptionType(thrower);
        TestLogger.Info($"Swift recovered managed exception type: \"{recoveredType}\"");
        AssertEqual("System.InvalidOperationException", recoveredType,
            "Swift must recover the originating CLR type name from userInfo");
    }

    /// <summary>
    /// The same reverse path also round-trips the exception's message through
    /// NSLocalizedDescriptionKey.
    /// </summary>
    public void TestReversePathCarriesManagedExceptionMessage()
    {
        var functions = new SwiftBindingsTestLib.Functions();
        Action thrower = () => throw new InvalidOperationException("reverse-boom");
        var recoveredMessage = functions.RecoverManagedExceptionMessage(thrower);
        TestLogger.Info($"Swift recovered managed exception message: \"{recoveredMessage}\"");
        AssertTrue(recoveredMessage.Contains("reverse-boom"),
            $"Swift must recover the exception message, got: {recoveredMessage}");
    }

    /// <summary>
    /// A distinct exception type round-trips its own identity — the carriage is not
    /// hard-coded to one type.
    /// </summary>
    public void TestReversePathDistinguishesExceptionType()
    {
        var functions = new SwiftBindingsTestLib.Functions();
        Action thrower = () => throw new NotSupportedException("nope");
        var recoveredType = functions.RecoverManagedExceptionType(thrower);
        AssertEqual("System.NotSupportedException", recoveredType,
            "Distinct exception type must round-trip distinctly");
    }

    /// <summary>
    /// The success branch: a non-throwing closure yields the no-throw sentinel, confirming
    /// the recovery only fires on the error path.
    /// </summary>
    public void TestReversePathNoThrow()
    {
        var functions = new SwiftBindingsTestLib.Functions();
        Action noop = () => { };
        var result = functions.RecoverManagedExceptionType(noop);
        AssertEqual("<no-throw>", result,
            "A non-throwing closure must hit the no-throw branch");
    }

    #endregion
}
