// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

namespace Swift;

/// <summary>
/// Registers NewFromPayload factories for non-generic hand-rolled Apple-package ISwiftObject types
/// (Foundation.Data, Foundation.URL, Foundation.URLRequest, Foundation.AttributedString,
/// SwiftUI.Text). On NativeAOT the trimmer may strip explicit interface implementations;
/// registering here keeps them alive and populates the factory cache before any marshalling
/// call. Runtime cannot perform this registration (would require a circular package
/// dependency). Generic types (Measurement&lt;T&gt;, ManagedSettings.Token&lt;T&gt;) self-register
/// per closed instantiation via a static readonly field. Foundation.AnyError uses its own
/// static-constructor metadata registration.
/// </summary>
internal static class AppleSupplementFactoryRegistration
{
#pragma warning disable CA2255 // ModuleInitializer is intentional — supplement self-registration
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        SwiftMarshal.RegisterSwiftObjectFactory<Swift.Foundation.Data>();
        SwiftMarshal.RegisterSwiftObjectFactory<Swift.Foundation.URL>();
        SwiftMarshal.RegisterSwiftObjectFactory<Swift.Foundation.URLRequest>();
        SwiftMarshal.RegisterSwiftObjectFactory<Swift.Foundation.AttributedString>();
        SwiftMarshal.RegisterSwiftObjectFactory<Swift.SwiftUI.Text>();
    }
}
