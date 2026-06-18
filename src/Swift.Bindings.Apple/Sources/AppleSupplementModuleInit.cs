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

        // Pre-register each reference-type Apple-supplement ISwiftObject's declared payload-construction
        // semantics (Finding 11) so the unconstrained marshal seam reads its ownership contract from the
        // by-Type cache (Runtime cannot — circular package dep). Generic types register their OPEN form
        // once; the dispatcher resolves closed instantiations via its open-generic fallback. Foundation.Data
        // is a value-type struct, so the seam short-circuits it to Inline before the cache — not registered.
        SwiftMarshal.RegisterPayloadSemantics(typeof(Swift.Foundation.URL), PayloadConstructionSemantics.Copy);
        SwiftMarshal.RegisterPayloadSemantics(typeof(Swift.Foundation.URLRequest), PayloadConstructionSemantics.Copy);
        SwiftMarshal.RegisterPayloadSemantics(typeof(Swift.Foundation.AttributedString), PayloadConstructionSemantics.Copy);
        SwiftMarshal.RegisterPayloadSemantics(typeof(Swift.Foundation.AnyError), PayloadConstructionSemantics.Inline);
        SwiftMarshal.RegisterPayloadSemantics(typeof(Swift.Foundation.Measurement<>), PayloadConstructionSemantics.Copy);
        SwiftMarshal.RegisterPayloadSemantics(typeof(Swift.ManagedSettings.Token<>), PayloadConstructionSemantics.Copy);
        SwiftMarshal.RegisterPayloadSemantics(typeof(Swift.SwiftUI.Text), PayloadConstructionSemantics.Adopt);
    }
}
