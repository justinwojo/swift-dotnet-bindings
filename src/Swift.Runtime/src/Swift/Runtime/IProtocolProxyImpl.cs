// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// Common entry point for receivers to retrieve the user-supplied C# implementation
/// wrapped by a protocol proxy. Every generated <c>XxxProxy</c> implements
/// <c>IProtocolProxyImpl&lt;IXxx&gt;</c>.
///
/// Covariance (<c>out TInterface</c>) lets a child proxy satisfy lookups for an
/// ancestor protocol: a <c>KidozInitDelegateProxy</c> implementing
/// <c>IProtocolProxyImpl&lt;IKidozInitDelegate&gt;</c> is castable to
/// <c>IProtocolProxyImpl&lt;ISDKInitDelegate&gt;</c> because
/// <c>IKidozInitDelegate : ISDKInitDelegate</c>. This is how inherited-protocol
/// callbacks reach the user's implementation when only a child proxy was registered.
/// </summary>
public interface IProtocolProxyImpl<out TInterface> where TInterface : class
{
    /// <summary>The user's C# implementation, or null for Swift-backed proxies / collected impls.</summary>
    TInterface? UserImpl { get; }
}
