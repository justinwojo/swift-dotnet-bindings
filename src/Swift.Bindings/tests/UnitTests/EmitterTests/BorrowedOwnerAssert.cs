// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Xunit;

#nullable enable

namespace BindingsGeneration.Tests;

/// <summary>
/// Asserts that generated C# holds the wrapper that owns a borrowed native pointer until the
/// native call that borrowed it has returned. Reading <c>.Handle</c> or a payload's raw pointer is
/// the wrapper's last use, so without a later <c>GC.KeepAlive</c> (or a <c>finally</c> holding it)
/// a precise collector can finalize the wrapper, releasing the object, while Swift still uses it.
/// </summary>
internal static class BorrowedOwnerAssert
{
    /// <summary>
    /// The first occurrence of <paramref name="callMarker"/> (at or after <paramref name="from"/>)
    /// is followed by <c>GC.KeepAlive(<paramref name="owner"/>)</c>.
    /// </summary>
    public static void HeldAfter(string cs, string callMarker, string owner, string? from = null)
    {
        var start = from is null ? 0 : cs.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' not found in:\n{cs}");
        var call = cs.IndexOf(callMarker, start, StringComparison.Ordinal);
        Assert.True(call >= 0, $"'{callMarker}' not found in:\n{cs}");
        var hold = cs.IndexOf($"GC.KeepAlive({owner});", call, StringComparison.Ordinal);
        Assert.True(hold > call, $"'{owner}' is not held after '{callMarker}' in:\n{cs}");
    }
}
