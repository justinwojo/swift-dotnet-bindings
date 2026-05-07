// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Foundation;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ErrorHandling;

/// <summary>
/// Regression coverage for the Stripe FlowController shape: a nested public
/// class whose public surface includes a static factory taking
/// <c>(Result&lt;Self, any Error&gt;) -&gt; Void</c>, a sibling factory taking
/// <c>((any Error)?) -&gt; Void</c>, and an instance method taking
/// <c>((any Error)?) -&gt; Void</c>.
///
/// Pre-fix the binding-report listed all four wrapper symbols as
/// <c>MissingWrapperSymbol</c> and the C# binding silently dropped the
/// methods — `PaymentSheet.FlowController.create(...)` had no callable
/// surface at all. The closure-context owner-token mechanism (Session B,
/// commit 9f02a9b7) wraps the C# <see cref="System.Runtime.InteropServices.GCHandle"/>
/// in a Swift-ARC-owned <c>_SBClosureCtx</c> box exported from
/// <c>libSwiftBindingsRuntime.dylib</c>, so the MCB pipeline now emits a
/// stable <c>@_cdecl</c> wrapper for these closure-arg methods.
///
/// Each test pins a different end-to-end property:
///   1. The wrapper symbols are reachable (no <see cref="EntryPointNotFoundException"/>).
///   2. The success branch of <c>Result&lt;Self, Error&gt;</c> delivers a
///      live <c>SessionController</c> instance that round-trips its
///      configuration.
///   3. The failure branch surfaces an <c>AnyError</c> whose
///      <c>LocalizedDescription</c> identifies the original
///      <c>MathError.divisionByZero</c> case — proving the existential
///      container survives the MCB callback round-trip.
///   4. The <c>Optional&lt;any Error&gt;</c> closure variants distinguish
///      None / Some(MathError) cleanly.
///   5. The instance-method variant (<c>update</c>) mutates the receiver
///      *and* delivers the optional-error completion, exercising the
///      MCB-with-self-pointer path.
/// </summary>
public class NestedResultClosureTests : TestBase
{
    public NestedResultClosureTests(TestResults results) : base(results) { }

    /// <summary>
    /// Result-success branch: factory delivers a live SessionController
    /// whose configuration round-trips and whose token matches what we
    /// passed in. No EntryPointNotFoundException — the
    /// SBW_MCB_*_create wrapper is in the dylib.
    /// </summary>
    public void TestSessionController_Create_Success_DeliversInstance()
    {
        var configuration = new OnboardingFlow.Configuration(theme: 7);

        SessionControllerSnapshot? captured = null;
        OnboardingFlow.SessionController.Create(
            token: "tok_alpha",
            configuration: configuration,
            shouldFail: false,
            completion: result =>
            {
                // SwiftResult<TSuccess, TFailure> is IDisposable — its SafeHandle
                // wraps a heap copy of the Swift Result payload. Dispose it before
                // returning so the callback doesn't leak a SafeHandle per call.
                // The captured class payload (`session`) is retained independently
                // of this handle, so it remains live after the using block exits.
                using (result)
                {
                    if (result.IsSuccess && result.TryGetSuccess(out var session))
                    {
                        captured = new SessionControllerSnapshot(
                            Token: session!.Token.ToString(),
                            Theme: session.Configuration.Theme);
                        session.Dispose();
                    }
                }
            });

        AssertTrue(captured.HasValue, "completion should fire with a Success-case Result");
        AssertEqual("tok_alpha", captured!.Value.Token, "Token round-trips through the success payload");
        AssertEqual(7, captured.Value.Theme, "Configuration.theme round-trips through the success payload");
    }

    /// <summary>
    /// Result-failure branch: factory delivers an AnyError whose
    /// LocalizedDescription identifies the original MathError case.
    /// Pre-fix the wrapper symbol was missing, so this call would
    /// EntryPointNotFoundException before the closure ever fired.
    /// </summary>
    public void TestSessionController_Create_Failure_DeliversAnyError()
    {
        var configuration = new OnboardingFlow.Configuration(theme: 0);

        string? capturedDescription = null;
        bool firedFailure = false;
        OnboardingFlow.SessionController.Create(
            token: "tok_beta",
            configuration: configuration,
            shouldFail: true,
            completion: result =>
            {
                // Dispose the SwiftResult SafeHandle once we've copied out the
                // information we need — the AnyError snapshot lives on its
                // own and doesn't depend on the result's storage.
                using (result)
                {
                    if (result.IsFailure && result.TryGetFailure(out var raw))
                    {
                        firedFailure = true;
                        var anyError = new AnyError(raw);
                        capturedDescription = anyError.LocalizedDescription;
                    }
                }
            });

        AssertTrue(firedFailure, "completion should fire with a Failure-case Result");
        AssertTrue(capturedDescription is not null, "AnyError surfaced from existential container");
        AssertTrue(capturedDescription!.Contains("divisionByZero"),
            $"Expected 'divisionByZero' in error description, got: \"{capturedDescription}\"");
    }

    /// <summary>
    /// Optional-Error variant, success branch: completion fires with
    /// <c>null</c> when the underlying Swift call passes <c>nil</c> to
    /// the closure. Mirrors the Stripe overloads of <c>create</c> that
    /// report failure-only via an Optional Error completion.
    /// </summary>
    public void TestSessionController_CreateWithOptionalError_NoFailure_NullCompletion()
    {
        var configuration = new OnboardingFlow.Configuration(theme: 1);

        bool fired = false;
        AnyError? captured = null;
        OnboardingFlow.SessionController.CreateWithOptionalError(
            token: "tok_gamma",
            configuration: configuration,
            shouldFail: false,
            completion: error =>
            {
                fired = true;
                captured = error;
            });

        AssertTrue(fired, "completion should fire on the success branch");
        AssertFalse(captured.HasValue, "Optional-Error completion should be null on success");
    }

    /// <summary>
    /// Optional-Error variant, failure branch: completion fires with a
    /// non-null AnyError whose LocalizedDescription identifies the
    /// MathError case. Confirms the
    /// <c>Optional&lt;ExistentialContainer1&gt;</c> projection unwraps
    /// the discriminator before constructing the C# AnyError.
    /// </summary>
    public void TestSessionController_CreateWithOptionalError_Failure_DeliversAnyError()
    {
        var configuration = new OnboardingFlow.Configuration(theme: 1);

        string? capturedDescription = null;
        OnboardingFlow.SessionController.CreateWithOptionalError(
            token: "tok_delta",
            configuration: configuration,
            shouldFail: true,
            completion: error =>
            {
                capturedDescription = error?.LocalizedDescription;
            });

        AssertTrue(capturedDescription is not null,
            "Optional-Error completion should deliver a non-null AnyError on failure");
        AssertTrue(capturedDescription!.Contains("divisionByZero"),
            $"Expected 'divisionByZero' in error description, got: \"{capturedDescription}\"");
    }

    /// <summary>
    /// Instance-method variant: <c>update</c> mutates the receiver's
    /// configuration AND fires the optional-error completion. Pre-fix
    /// the SBW_MCB_*_update wrapper was missing, so the method had no
    /// C# surface at all. Verifies the MCB-with-self-pointer path
    /// (instance closure-arg method) writes through the receiver and
    /// the completion runs cleanly on the no-failure branch.
    /// </summary>
    public void TestSessionController_Update_Success_MutatesAndCompletes()
    {
        var initialConfig = new OnboardingFlow.Configuration(theme: 1);

        // Construct via the static factory's success branch so we have a live
        // SessionController to call Update on. Without this we'd be relying on
        // a public constructor; the factory path is what the Stripe consumer
        // actually uses.
        OnboardingFlow.SessionController? session = null;
        OnboardingFlow.SessionController.Create(
            token: "tok_epsilon",
            configuration: initialConfig,
            shouldFail: false,
            completion: result =>
            {
                // Dispose the SwiftResult SafeHandle inside the callback —
                // the extracted class payload (`s`) is retained independently
                // and remains live after the using block exits.
                using (result)
                {
                    if (result.IsSuccess && result.TryGetSuccess(out var s))
                    {
                        session = s;
                    }
                }
            });
        AssertTrue(session is not null, "SessionController.Create should produce a live instance");

        try
        {
            var nextConfig = new OnboardingFlow.Configuration(theme: 42);
            bool fired = false;
            AnyError? captured = null;
            session!.Update(
                configuration: nextConfig,
                shouldFail: false,
                completion: error =>
                {
                    fired = true;
                    captured = error;
                });

            AssertTrue(fired, "Update completion should fire on the success branch");
            AssertFalse(captured.HasValue, "Update completion should be null on success");
            AssertEqual(42, session.Configuration.Theme,
                "Update should have mutated the receiver's configuration through the @_cdecl wrapper");
        }
        finally
        {
            session?.Dispose();
        }
    }

    /// <summary>
    /// Instance-method failure branch: <c>update</c>'s optional-error
    /// completion delivers an AnyError whose description carries the
    /// MathError case. Round-trips error reporting through the
    /// instance-MCB-with-self path.
    /// </summary>
    public void TestSessionController_Update_Failure_DeliversAnyError()
    {
        var initialConfig = new OnboardingFlow.Configuration(theme: 1);
        OnboardingFlow.SessionController? session = null;
        OnboardingFlow.SessionController.Create(
            token: "tok_zeta",
            configuration: initialConfig,
            shouldFail: false,
            completion: result =>
            {
                // Dispose the SwiftResult SafeHandle inside the callback —
                // the extracted class payload (`s`) is retained independently
                // and remains live after the using block exits.
                using (result)
                {
                    if (result.IsSuccess && result.TryGetSuccess(out var s))
                    {
                        session = s;
                    }
                }
            });
        AssertTrue(session is not null, "SessionController.Create should produce a live instance");

        try
        {
            var nextConfig = new OnboardingFlow.Configuration(theme: 99);
            string? capturedDescription = null;
            session!.Update(
                configuration: nextConfig,
                shouldFail: true,
                completion: error =>
                {
                    capturedDescription = error?.LocalizedDescription;
                });

            AssertTrue(capturedDescription is not null,
                "Update completion should deliver a non-null AnyError on failure");
            AssertTrue(capturedDescription!.Contains("divisionByZero"),
                $"Expected 'divisionByZero' in error description, got: \"{capturedDescription}\"");
        }
        finally
        {
            session?.Dispose();
        }
    }

    /// <summary>
    /// Captured snapshot of values copied out of a SessionController inside
    /// a closure callback. Lets the test outlive the SessionController scope
    /// (the Swift instance is freed when the closure returns) without
    /// touching freed payload pointers from the assert phase.
    /// </summary>
    private readonly record struct SessionControllerSnapshot(string Token, int Theme);
}
