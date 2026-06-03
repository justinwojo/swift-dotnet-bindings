// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Swift parameter value-ownership convention, parsed from the ABI JSON
    /// <c>paramValueOwnership</c> field (Swift's <c>ParamSpecifier</c>). The string values are
    /// emitted by <c>swift-frontend -emit-abi-descriptor-path</c> and were confirmed empirically:
    /// <list type="bullet">
    /// <item><see cref="Default"/> — no explicit specifier (ABI JSON omits the field). Swift's
    /// implicit borrowing convention.</item>
    /// <item><see cref="InOut"/> — <c>inout</c> (ABI string <c>"InOut"</c>).</item>
    /// <item><see cref="Shared"/> — <c>borrowing</c> / <c>__shared</c> (ABI string <c>"Shared"</c>);
    /// passed at +0, caller retains ownership.</item>
    /// <item><see cref="Owned"/> — <c>consuming</c> / <c>__owned</c> (ABI string <c>"Owned"</c>);
    /// passed at +1, callee takes ownership.</item>
    /// </list>
    /// <see cref="Owned"/> is load-bearing for correctness: a <c>consuming</c> parameter of a
    /// non-copyable type transfers ownership into Swift, so the C# caller must not also release it
    /// (double-free) and a native thunk must not forward it at +0.
    /// </summary>
    public enum ParameterOwnership
    {
        /// <summary>No explicit ownership specifier (Swift's implicit borrowing).</summary>
        Default,

        /// <summary><c>inout</c> — pass-by-reference, caller observes mutations.</summary>
        InOut,

        /// <summary><c>borrowing</c> / <c>__shared</c> — +0 borrow, caller retains ownership.</summary>
        Shared,

        /// <summary><c>consuming</c> / <c>__owned</c> — +1 transfer, callee takes ownership.</summary>
        Owned,
    }
}
