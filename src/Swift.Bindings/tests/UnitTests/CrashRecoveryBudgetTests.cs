// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="CrashRecoveryBudget"/> — how many attempts a resume-on-crash loop gets, and
/// whose failures pay for them.
///
/// <para><b>The defect these pin.</b> The simulator and device loops budget two kinds of failed
/// attempt separately. An attempt in which the app RAN and crashed consumes a crash-recovery retry.
/// An attempt the LAUNCHER aborted before the app's process started produced no test signal at all,
/// so it must not: it extends the loop by one and is capped by its own launcher-abort budget. Both
/// loops widened their bound to <c>maxRetries + launchAbortCount</c> accordingly — but every stop
/// condition inside them compared the bare attempt index against <c>maxRetries</c>, so the widening
/// bought nothing. One launcher abort followed by five recoverable product crashes gave up after four
/// product attempts instead of five, leaving real test classes unrun and reporting a flaky deploy as
/// a product failure.</para>
///
/// <para>The load-bearing assertion is therefore the one with a NON-ZERO abort count: a case that
/// only exercises <c>launchAbortCount == 0</c> passes against the buggy code and proves nothing.</para>
/// </summary>
public class CrashRecoveryBudgetTests
{
    // The value both loops use.
    const int MaxRetries = 5;

    // ===================================================================
    //  Budget arithmetic
    // ===================================================================

    [Fact]
    public void WithNoLauncherAborts_TheBudgetIsTheFirstAttemptPlusTheRetries()
    {
        Assert.Equal(MaxRetries, CrashRecoveryBudget.LastAttemptIndex(MaxRetries, launchAbortCount: 0));
        Assert.Equal(MaxRetries + 1, CrashRecoveryBudget.TotalAttempts(MaxRetries, launchAbortCount: 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EachLauncherAbortExtendsTheBudgetByExactlyOne(int aborts)
    {
        Assert.Equal(MaxRetries + aborts, CrashRecoveryBudget.LastAttemptIndex(MaxRetries, aborts));
        Assert.Equal(MaxRetries + aborts + 1, CrashRecoveryBudget.TotalAttempts(MaxRetries, aborts));
    }

    // ===================================================================
    //  Stop conditions
    // ===================================================================

    /// <summary>
    /// THE case that fails against the old bare-<c>maxRetries</c> comparisons. Attempt index 5 with
    /// one launcher abort already counted: the old <c>attempt == maxRetries</c> stopped recovery here,
    /// making the aborted attempt pay for a product crash. The abort extended the budget, so attempt 5
    /// is not the last one and recovery must continue.
    /// </summary>
    [Fact]
    public void LauncherAbortsDoNotConsumeCrashRecoveryRetries()
    {
        Assert.False(CrashRecoveryBudget.IsExhausted(attempt: MaxRetries, MaxRetries, launchAbortCount: 1));
        Assert.True(CrashRecoveryBudget.CanAttempt(attempt: MaxRetries + 1, MaxRetries, launchAbortCount: 1));

        // The blind-skip guard is the negation of the same predicate and must move with it — that is
        // the pair that drifted apart before, one site using == and the other <.
        Assert.True(CrashRecoveryBudget.IsExhausted(attempt: MaxRetries + 1, MaxRetries, launchAbortCount: 1));
        Assert.False(CrashRecoveryBudget.CanAttempt(attempt: MaxRetries + 2, MaxRetries, launchAbortCount: 1));
    }

    [Fact]
    public void TheFinalAttemptReportsExhaustionWhenNothingExtendedTheBudget()
    {
        Assert.False(CrashRecoveryBudget.IsExhausted(attempt: MaxRetries - 1, MaxRetries, launchAbortCount: 0));
        Assert.True(CrashRecoveryBudget.IsExhausted(attempt: MaxRetries, MaxRetries, launchAbortCount: 0));
        Assert.False(CrashRecoveryBudget.CanAttempt(attempt: MaxRetries + 1, MaxRetries, launchAbortCount: 0));
    }

    /// <summary>
    /// <c>&gt;=</c>, not <c>==</c>: an attempt index past the bound must still read as exhausted. An
    /// equality test against a bound that moves during the loop steps over the stop condition and
    /// spins to the loop guard instead of reporting that the budget ran out.
    /// </summary>
    [Theory]
    [InlineData(MaxRetries + 1)]
    [InlineData(MaxRetries + 7)]
    public void AnAttemptBeyondTheBoundIsExhausted_NotMerelyUnequal(int attempt)
    {
        Assert.True(CrashRecoveryBudget.IsExhausted(attempt, MaxRetries, launchAbortCount: 0));
    }

    [Fact]
    public void TheLoopGuardAndTheStopConditionDisagreeOnlyOnTheFinalAttempt()
    {
        // The loop must be able to RUN its last attempt (CanAttempt) while that attempt reports it has
        // no retry left afterwards (IsExhausted). Every other index has to agree, or a give-up point
        // fires early (classes left unrun) or late (the loop spins past its budget).
        for (int aborts = 0; aborts <= 3; aborts++)
        {
            var last = CrashRecoveryBudget.LastAttemptIndex(MaxRetries, aborts);
            for (int attempt = 0; attempt <= last + 2; attempt++)
            {
                var canAttempt = CrashRecoveryBudget.CanAttempt(attempt, MaxRetries, aborts);
                var exhausted = CrashRecoveryBudget.IsExhausted(attempt, MaxRetries, aborts);

                if (attempt == last)
                    Assert.True(canAttempt && exhausted);
                else
                    Assert.NotEqual(canAttempt, exhausted);
            }
        }
    }

    // ===================================================================
    //  End-to-end: the sequence the defect was found on
    // ===================================================================

    /// <summary>
    /// Replays the loop the way both legs drive it — the launcher aborts once, then the app runs and
    /// crashes recoverably every time — and asserts the run still gets its full complement of PRODUCT
    /// attempts. Against the old bare-<c>maxRetries</c> stop condition this yields 5, not 6: the
    /// aborted attempt silently consumed a crash-recovery retry and a real test class went unrun.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ProductAttemptCountIsUnaffectedByHowManyTimesTheLauncherAborted(int abortsUpFront)
    {
        var launchAbortCount = 0;
        var productAttempts = new List<int>();

        for (int attempt = 0; CrashRecoveryBudget.CanAttempt(attempt, MaxRetries, launchAbortCount); attempt++)
        {
            // The launcher aborts on the first `abortsUpFront` attempts: no app output, no results,
            // nothing to recover. The loop settles and re-attempts the LAUNCH without touching
            // crash-recovery state.
            if (attempt < abortsUpFront)
            {
                launchAbortCount++;
                continue;
            }

            // From here the app runs and crashes in a way recovery can make progress on.
            productAttempts.Add(attempt);

            if (CrashRecoveryBudget.IsExhausted(attempt, MaxRetries, launchAbortCount))
                break;
        }

        // First attempt + MaxRetries retries, every time — the aborts cost the product nothing.
        Assert.Equal(MaxRetries + 1, productAttempts.Count);
        Assert.Equal(abortsUpFront, launchAbortCount);
    }
}
