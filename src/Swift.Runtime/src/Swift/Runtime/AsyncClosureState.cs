// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// Holds the state for a non-throwing async closure passed from C# to Swift.
/// Mirrors <see cref="AsyncThrowingClosureState{T}"/> but for
/// <c>@escaping (...) async -&gt; T</c> closures. A C# exception from the user
/// delegate has no Swift error channel to surface on — the runtime helper
/// explicitly <c>Environment.FailFast</c>s instead of swallowing silently.
/// </summary>
/// <typeparam name="T">The return type of the async operation.</typeparam>
public sealed class AsyncClosureState<T>
{
    public required Func<Task<T>> AsyncFunc { get; init; }
    public CancellationTokenSource? CancellationSource { get; set; }
}

/// <summary>Single-arg non-throwing async closure state.</summary>
public sealed class AsyncClosureState<A0, TResult>
{
    public required Func<A0, Task<TResult>> AsyncFunc { get; init; }
    public CancellationTokenSource? CancellationSource { get; set; }
}

/// <summary>Two-arg non-throwing async closure state.</summary>
public sealed class AsyncClosureState<A0, A1, TResult>
{
    public required Func<A0, A1, Task<TResult>> AsyncFunc { get; init; }
    public CancellationTokenSource? CancellationSource { get; set; }
}

/// <summary>Three-arg non-throwing async closure state.</summary>
public sealed class AsyncClosureState<A0, A1, A2, TResult>
{
    public required Func<A0, A1, A2, Task<TResult>> AsyncFunc { get; init; }
    public CancellationTokenSource? CancellationSource { get; set; }
}

/// <summary>Four-arg non-throwing async closure state.</summary>
public sealed class AsyncClosureState<A0, A1, A2, A3, TResult>
{
    public required Func<A0, A1, A2, A3, Task<TResult>> AsyncFunc { get; init; }
    public CancellationTokenSource? CancellationSource { get; set; }
}
