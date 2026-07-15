// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime regression for the parent-only ASYNC CSM gap — async sibling of
/// <see cref="PatParentOnlyMethodsTests"/>.
/// <para>
/// <c>AsyncBag&lt;Item: AsyncBagItem&gt;</c> declares two parent-only async
/// instance methods (no method-own generic parameters): <c>respond()</c>
/// (non-throwing) and <c>tryRespond()</c> (throwing). Before the parent-only
/// async CSM session, three rejection sites in the async pipeline blocked
/// these from emitting at all (the generic-parent guard in
/// <c>PassesAsyncMethodLevelGuards</c>, the <c>method.IsAsync</c> skip in the
/// per-conformer extension emission loop, and the <c>ownParamCount == 0</c>
/// rejection in <c>IsCsmAsyncEligible</c>).
/// </para>
/// <para>
/// After the fix, each closed conformer (<c>MockStringItem</c>,
/// <c>MockIntItem</c>) gets its own static extension class with a per-conformer
/// substituted return type (<c>StringResponse</c> / <c>IntResponse</c>). These
/// tests assert: (1) the methods emit at all, (2) the awaited result carries
/// the per-conformer payload, (3) the two conformers produce independent C#
/// extension methods that can run interleaved without aliasing, and (4) the
/// throwing variant's success path completes normally.
/// </para>
/// </summary>
public class PatParentAsyncMethodsTests : TestBase
{
    public PatParentAsyncMethodsTests(TestResults results) : base(results) { }

    public async Task TestAsyncBagMockStringItem_RespondAsyncReturnsStringResponse()
    {
        // Closed-conformer per-pairing substitution: the parent type closes
        // Item = MockStringItem, so Item.Response substitutes to StringResponse
        // BEFORE the async harness emits. The extension method's return type
        // must therefore be Task<StringResponse>, not Task<Item.Response>.
        using var bag = Functions.MakeAsyncBagMockStringItem();
        var response = await WithTimeout(bag.RespondAsync(), DefaultAsyncTimeout);
        AssertNotNull(response, "RespondAsync result is not null");
        AssertEqual("ok", response.S.ToString(), "StringResponse.s round-trips");
    }

    public async Task TestAsyncBagMockIntItem_RespondAsyncReturnsIntResponse()
    {
        // Second conformer pairing: Item = MockIntItem substitutes
        // Item.Response → IntResponse, yielding Task<IntResponse>. This must be
        // a DIFFERENT extension method (distinct cdecl symbol, distinct
        // generated extension class) from the StringResponse pairing — not a
        // shared method that switches on metadata at runtime.
        using var bag = Functions.MakeAsyncBagMockIntItem();
        var response = await WithTimeout(bag.RespondAsync(), DefaultAsyncTimeout);
        AssertNotNull(response, "RespondAsync result is not null");
        AssertEqual((nint)42, response.N, "IntResponse.n round-trips");
    }

    public async Task TestAsyncBag_CrossConformerInterleavedAwaitsCompleteIndependently()
    {
        // Two-conformer separation under interleaved continuation: kicking off
        // both awaits before either completes catches a class of bugs where
        // the per-conformer success callback would dispatch to the wrong
        // TaskCompletionSource (e.g. via a shared static field rather than
        // the per-invocation GCHandle holder). Each Task must resolve with its
        // own conformer-substituted payload.
        using var stringBag = Functions.MakeAsyncBagMockStringItem();
        using var intBag = Functions.MakeAsyncBagMockIntItem();

        var stringTask = stringBag.RespondAsync();
        var intTask = intBag.RespondAsync();

        var stringResponse = await WithTimeout(stringTask, DefaultAsyncTimeout);
        var intResponse = await WithTimeout(intTask, DefaultAsyncTimeout);

        AssertEqual("ok", stringResponse.S.ToString(), "StringResponse.s survives interleaving");
        AssertEqual((nint)42, intResponse.N, "IntResponse.n survives interleaving");
    }

    public async Task TestAsyncBagMockStringItem_TryRespondAsyncSuccessPath()
    {
        // Throwing-variant success path: the cdecl wrapper installs both a
        // success callback and an error callback, but the body never throws
        // here. The error-callback delegate must be reachable (so the wrapper
        // emission isn't malformed) but stay unfired (so the success path
        // completes normally). Distinct from RespondAsync in that this
        // exercises the throwing-overload Swift wrapper (extra errorCallback
        // parameter, do/catch around the body, errorPtr GCHandle holder).
        using var bag = Functions.MakeAsyncBagMockStringItem();
        var response = await WithTimeout(bag.TryRespondAsync(), DefaultAsyncTimeout);
        AssertNotNull(response, "TryRespondAsync result is not null");
        AssertEqual("ok", response.S.ToString(), "Throwing-variant StringResponse.s round-trips");
    }

    public async Task TestAsyncBagMockIntItem_TryRespondAsyncSuccessPath()
    {
        // Same throwing-variant shape on the second conformer: confirms the
        // throwing async harness emits independently per closed pairing and
        // returns the correctly substituted Task<IntResponse> (not a shared
        // task type aliased across conformers).
        using var bag = Functions.MakeAsyncBagMockIntItem();
        var response = await WithTimeout(bag.TryRespondAsync(), DefaultAsyncTimeout);
        AssertNotNull(response, "TryRespondAsync result is not null");
        AssertEqual((nint)42, response.N, "Throwing-variant IntResponse.n round-trips");
    }

    public async Task TestAsyncBagMockStringItem_CancelRespondAsyncSurfacesCancellation()
    {
        // Cancellation classification on the CSM parent-only async ERROR path.
        // The Swift member throws CancellationError from inside its async body
        // WITHOUT the caller passing a cancellation token, so the cancellation
        // must travel through the error callback (not the token-registration
        // path, which is not even wired here). A Swift CancellationError must
        // surface as a *cancelled* Task — an awaiter sees OperationCanceledException
        // and Task.IsCanceled is true. The faulted-Task behaviour (an awaiter
        // seeing a SwiftException) is the bug this pins against.
        using var bag = Functions.MakeAsyncBagMockStringItem();
        var task = bag.CancelRespondAsync();

        bool observedCancellation = false;
        try
        {
            await WithTimeout(task, DefaultAsyncTimeout);
        }
        catch (global::System.OperationCanceledException)
        {
            observedCancellation = true;
        }

        AssertTrue(observedCancellation,
            "CancelRespondAsync awaiter observes OperationCanceledException, not a SwiftException");
        AssertTrue(task.IsCanceled, "CancelRespondAsync Task ends in the Canceled state");
    }

    public async Task TestAsyncBagMockIntItem_CancelRespondAsyncSurfacesCancellation()
    {
        // Same cancellation-classification assertion on the second closed
        // conformer, confirming the error-path cancellation mapping emits
        // independently per pairing.
        using var bag = Functions.MakeAsyncBagMockIntItem();
        var task = bag.CancelRespondAsync();

        bool observedCancellation = false;
        try
        {
            await WithTimeout(task, DefaultAsyncTimeout);
        }
        catch (global::System.OperationCanceledException)
        {
            observedCancellation = true;
        }

        AssertTrue(observedCancellation,
            "CancelRespondAsync awaiter observes OperationCanceledException, not a SwiftException");
        AssertTrue(task.IsCanceled, "CancelRespondAsync Task ends in the Canceled state");
    }
}
