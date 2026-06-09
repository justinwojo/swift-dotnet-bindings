// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Namespace-facade fixture
//
// Mirrors the BlinkID 7.7.0 `BlinkIDSDK` shape locally so the
// namespace-facade emission can be observed end-to-end inside
// SwiftBindingsTestLib without depending on a third-party framework.
//
// The expected emission is a real C# nested namespace
// (`namespace SwiftBindingsTestLib.LocalFacade { … }`) instead of a
// `partial class LocalFacade { … nested types … }`. Pre-fix, the C#
// consumer had to either fully-qualify every nested type or reach for
// `using static SwiftBindingsTestLib.LocalFacade;`. Post-fix,
// `using SwiftBindingsTestLib.LocalFacade;` resolves the nested types
// natively.
//
// Pre-fix, the namespace facade was emitted as a `partial static class` rather than a real C# namespace.

/// Top-level public struct used purely to scope nested types — no inits,
/// no stored properties, no instance/static members. Matches the strict
/// detection criteria in `NamespaceFacadeDetector.IsNamespaceFacade`.
public struct LocalFacade {
    /// Nested struct inside the facade. Emits as
    /// `SwiftBindingsTestLib.LocalFacade.FacadeMessage` post-fix.
    /// Stored property is `messageValue` (not `payload`) so it doesn't
    /// collide with the runtime's reserved `_payload` / `Payload`
    /// SafeHandle accessor on every emitted struct wrapper.
    public struct FacadeMessage {
        public let messageValue: Int32
        public init(messageValue: Int32) {
            self.messageValue = messageValue
        }
    }

    /// Nested simple-enum inside the facade. Emits as
    /// `SwiftBindingsTestLib.LocalFacade.FacadeStatus`. Verifies the
    /// nested-type emission still routes through the simple-enum
    /// path inside the lifted namespace.
    public enum FacadeStatus: Int32 {
        case idle = 0
        case running = 1
        case done = 2
    }
}

/// Top-level caseless public enum used purely to scope nested types.
/// Pre-fix this would emit as `public static partial class LocalFacadeEnum`.
/// Post-fix it emits as `namespace SwiftBindingsTestLib.LocalFacadeEnum`.
public enum LocalFacadeEnum {
    public struct InnerHolder {
        public let labelValue: Int32
        public init(labelValue: Int32) {
            self.labelValue = labelValue
        }
    }
}

/// Free function returning a value typed by the nested struct. Forces
/// the consumer-facing type reference `SwiftBindingsTestLib.LocalFacade.FacadeMessage`
/// to resolve through the lifted-namespace path.
public func makeFacadeMessage(messageValue: Int32) -> LocalFacade.FacadeMessage {
    return LocalFacade.FacadeMessage(messageValue: messageValue)
}

/// Same shape but routed through the caseless-enum facade variant.
public func makeFacadeEnumHolder(labelValue: Int32) -> LocalFacadeEnum.InnerHolder {
    return LocalFacadeEnum.InnerHolder(labelValue: labelValue)
}
