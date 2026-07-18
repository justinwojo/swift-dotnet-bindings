// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// Thrown when a bound member exists in the generated surface for compile-time reasons —
/// typically to satisfy an interface conformance — but has no working native implementation
/// behind it, so it cannot be invoked.
///
/// This is the <see cref="PlatformNotSupportedException"/> pattern applied to binding
/// degradation. The generator emits a member whose body throws this exception instead of
/// keeping a P/Invoke whose native symbol was stripped: the surface still compiles and still
/// conforms, but the first call fails loudly and attributably at the exact member rather than
/// surfacing as an opaque <see cref="EntryPointNotFoundException"/> or
/// <see cref="DllNotFoundException"/> from the interop layer.
///
/// Both <see cref="Member"/> and <see cref="Reason"/> are also folded into
/// <see cref="System.Exception.Message"/>, so a caller that only logs the message still gets
/// the full attribution.
/// </summary>
public sealed class SwiftBindingUnavailableException : SwiftRuntimeException
{
    /// <summary>
    /// The degraded member, as spelled in the generated binding (for example
    /// <c>MyModule.Widget.Resize</c>). Null when the exception was constructed from a
    /// pre-composed message.
    /// </summary>
    public string? Member { get; }

    /// <summary>
    /// Why the member has no native implementation (for example
    /// <c>wrapper symbol 'swiftbind_widget_resize' was stripped</c>). Null when the
    /// exception was constructed from a pre-composed message.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// Creates an exception attributing the unavailability to a specific member and cause.
    /// </summary>
    /// <param name="member">The degraded member, as spelled in the generated binding.</param>
    /// <param name="reason">Why the member has no native implementation.</param>
    public SwiftBindingUnavailableException(string member, string reason)
        : base(BuildMessage(member, reason))
    {
        Member = member;
        Reason = reason;
    }

    /// <summary>
    /// Creates an exception from a pre-composed message. <see cref="Member"/> and
    /// <see cref="Reason"/> are left null.
    /// </summary>
    public SwiftBindingUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates an exception from a pre-composed message wrapping an underlying failure.
    /// <see cref="Member"/> and <see cref="Reason"/> are left null.
    /// </summary>
    public SwiftBindingUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private static string BuildMessage(string member, string reason) =>
        $"The Swift binding for '{member}' is unavailable and cannot be called: {reason}. " +
        "The member was emitted so the surrounding type still compiles and conforms, but it has " +
        "no native implementation behind it. See the binding report for the full list of " +
        "degraded members.";
}
