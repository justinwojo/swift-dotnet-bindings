// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Self-contained nullable context so this file compiles identically whether built in the Nuke
// build assembly or link-compiled into the unit-test project.
#nullable enable

/// <summary>
/// The attempt budget of the resume-on-crash loops, in one place.
///
/// <para>Two independent things can end an attempt, and they are budgeted separately:</para>
/// <list type="bullet">
///   <item><description>The app RAN and crashed/timed out. That consumes a crash-recovery retry —
///   the loop excludes the crashing class and tries again, at most <c>maxRetries</c> times.</description></item>
///   <item><description>The LAUNCHER aborted before the app's process ever started. No test executed,
///   so there is nothing to recover and nothing to exclude; the attempt must not cost a
///   crash-recovery retry. Instead it extends the loop by one, and is capped by its own separate
///   launcher-abort budget (see <see cref="LaunchDiagnostics.LauncherAbortBudgetExhausted"/>), which
///   throws long before the extension can run away.</description></item>
/// </list>
///
/// <para><b>The defect this closes.</b> The loops already widened their bound to
/// <c>maxRetries + launchAbortCount</c>, but every stop condition inside them compared the bare
/// attempt index against <c>maxRetries</c> — so a launcher abort still silently ate a crash-recovery
/// retry, and the widened bound only kept the loop spinning past the point where the recovery paths
/// had already given up. One launcher abort followed by five recoverable product crashes stopped
/// recovery after four product attempts instead of five, leaving real test classes unrun and making a
/// flaky deploy read as a product failure. The arithmetic was recomputed inline at seven sites and six
/// of them were wrong, which is why it lives here now: one definition, both loops, all stop
/// conditions.</para>
/// </summary>
public static class CrashRecoveryBudget
{
    /// <summary>
    /// The index of the final attempt the loop may make. Attempts are zero-based, so the first
    /// attempt is index 0 and a run with no launcher aborts gets <c>maxRetries</c> retries after it.
    /// Every other member here derives from this, so the budget arithmetic exists exactly once.
    /// </summary>
    public static int LastAttemptIndex(int maxRetries, int launchAbortCount) =>
        maxRetries + launchAbortCount;

    /// <summary>
    /// The loop guard: whether <paramref name="attempt"/> is still inside the budget and may run.
    /// </summary>
    public static bool CanAttempt(int attempt, int maxRetries, int launchAbortCount) =>
        attempt <= LastAttemptIndex(maxRetries, launchAbortCount);

    /// <summary>
    /// The stop condition: true when the attempt just taken was the last one, so no crash-recovery
    /// retry remains and the caller must stop and report what it has.
    ///
    /// <para><c>&gt;=</c> rather than <c>==</c> on purpose. <paramref name="launchAbortCount"/> only
    /// ever increments today, but an equality test against a bound that moves during the loop is
    /// fragile by construction: one future path that advances the attempt index by more than one, or
    /// that lowers the bound, would step straight over an <c>==</c> and spin to the loop guard
    /// instead of reporting exhaustion.</para>
    /// </summary>
    public static bool IsExhausted(int attempt, int maxRetries, int launchAbortCount) =>
        attempt >= LastAttemptIndex(maxRetries, launchAbortCount);

    /// <summary>
    /// How many attempts the loop may make in total, for operator-facing logging. The loops print
    /// "attempt N/Total"; computing the ceiling from <c>maxRetries</c> alone understates it once a
    /// launcher abort has extended the budget, so the reader sees "attempt 6/6" while the loop
    /// legitimately keeps going.
    /// </summary>
    public static int TotalAttempts(int maxRetries, int launchAbortCount) =>
        LastAttemptIndex(maxRetries, launchAbortCount) + 1;
}
