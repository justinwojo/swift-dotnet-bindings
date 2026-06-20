// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Signals that a member's emission attempted to <em>construct</em> a protocol-proxy
/// class (<c>new {Proxy}(…)</c>) whose EveryProtocol conformance was not emitted, so the
/// proxy type does not exist in the output. Thrown from the PRODUCE-path proxy-name
/// chokepoint (<see cref="ExistentialHandler.GetRequiredProxyClassName"/>) and caught at a
/// member-emit boundary, which rolls the partial body back and re-emits the member as a
/// no-op stub (vtable receiver), a throw stub (public / interface member), or skips it
/// entirely (private implementation detail) — the in-band replacement for the regex
/// post-pass that once rewrote suppressed-proxy references across emitted C#.
/// </summary>
/// <remarks>
/// CONSUME-path references — the <c>GetOrCreate&lt;T&gt;(value, static __v =&gt; new {Proxy}(__v))</c>
/// wrap fallback — do NOT throw: the member stays and the local site drops just the
/// fallback lambda when <see cref="ExistentialHandler.TryGetConsumableProxyClassName"/>
/// returns <c>null</c>. Only constructions that are the sole way to produce the value
/// raise this exception.
/// </remarks>
public sealed class SuppressedProxyReferenceException : Exception
{
    /// <summary>The (possibly cross-module-qualified) proxy class name that could not be constructed.</summary>
    public string ProxyClassName { get; }

    public SuppressedProxyReferenceException(string proxyClassName)
        : base($"Protocol proxy '{proxyClassName}' is unavailable: its EveryProtocol conformance was not emitted, " +
               "so a member that constructs it cannot be produced.")
    {
        ProxyClassName = proxyClassName;
    }
}
