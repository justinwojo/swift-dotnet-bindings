// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// Holds the state for an async+throwing closure passed from C# to Swift.
/// This is used with the Swift continuation wrapper pattern where:
/// 1. Swift calls a synchronous "start" function from C#
/// 2. C# spawns Task.Run to execute the async delegate
/// 3. C# calls Swift's success/error callback when the Task completes
/// </summary>
/// <typeparam name="T">The return type of the async operation.</typeparam>
public sealed class AsyncThrowingClosureState<T>
{
    /// <summary>
    /// The user-provided async function that returns Task&lt;T&gt;.
    /// </summary>
    public required Func<Task<T>> AsyncFunc { get; init; }

    /// <summary>
    /// Optional cancellation token source for supporting Swift Task cancellation.
    /// When Swift's Task is cancelled, this can be used to signal the C# Task.
    /// </summary>
    public CancellationTokenSource? CancellationSource { get; set; }
}

/// <summary>
/// Holds the state for an async+throwing closure with void return type.
/// </summary>
public sealed class AsyncThrowingClosureStateVoid
{
    /// <summary>
    /// The user-provided async function that returns Task (void).
    /// </summary>
    public required Func<Task> AsyncFunc { get; init; }

    /// <summary>
    /// Optional cancellation token source for supporting Swift Task cancellation.
    /// </summary>
    public CancellationTokenSource? CancellationSource { get; set; }
}

/// <summary>
/// Async-throwing closure state for a single-arg closure <c>(A0) async throws -&gt; TResult</c>.
/// The Start thunk marshals the Swift-owned argument synchronously before <c>Task.Run</c>
/// and captures the managed value inside the closure that calls <see cref="AsyncFunc"/>.
/// </summary>
public sealed class AsyncThrowingClosureState<A0, TResult>
{
    public required Func<A0, Task<TResult>> AsyncFunc { get; init; }
    public CancellationTokenSource? CancellationSource { get; set; }
}

/// <summary>Single-arg void-returning async-throwing closure state.</summary>
public sealed class AsyncThrowingClosureStateVoid<A0>
{
    public required Func<A0, Task> AsyncFunc { get; init; }
    public CancellationTokenSource? CancellationSource { get; set; }
}

/// <summary>Two-arg async-throwing closure state.</summary>
public sealed class AsyncThrowingClosureState<A0, A1, TResult>
{
    public required Func<A0, A1, Task<TResult>> AsyncFunc { get; init; }
    public CancellationTokenSource? CancellationSource { get; set; }
}

/// <summary>Two-arg void-returning async-throwing closure state.</summary>
public sealed class AsyncThrowingClosureStateVoid<A0, A1>
{
    public required Func<A0, A1, Task> AsyncFunc { get; init; }
    public CancellationTokenSource? CancellationSource { get; set; }
}

/// <summary>Three-arg async-throwing closure state.</summary>
public sealed class AsyncThrowingClosureState<A0, A1, A2, TResult>
{
    public required Func<A0, A1, A2, Task<TResult>> AsyncFunc { get; init; }
    public CancellationTokenSource? CancellationSource { get; set; }
}

/// <summary>Three-arg void-returning async-throwing closure state.</summary>
public sealed class AsyncThrowingClosureStateVoid<A0, A1, A2>
{
    public required Func<A0, A1, A2, Task> AsyncFunc { get; init; }
    public CancellationTokenSource? CancellationSource { get; set; }
}

/// <summary>Four-arg async-throwing closure state.</summary>
public sealed class AsyncThrowingClosureState<A0, A1, A2, A3, TResult>
{
    public required Func<A0, A1, A2, A3, Task<TResult>> AsyncFunc { get; init; }
    public CancellationTokenSource? CancellationSource { get; set; }
}

/// <summary>Four-arg void-returning async-throwing closure state.</summary>
public sealed class AsyncThrowingClosureStateVoid<A0, A1, A2, A3>
{
    public required Func<A0, A1, A2, A3, Task> AsyncFunc { get; init; }
    public CancellationTokenSource? CancellationSource { get; set; }
}
