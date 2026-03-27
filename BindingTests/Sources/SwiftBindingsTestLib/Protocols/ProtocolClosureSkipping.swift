// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Protocol with Closure Method (Skip Test)

/// Protocol with a mix of closure and non-closure methods.
/// Generator should skip `onComplete(handler:)` (closure in protocol method)
/// but still emit `didReceiveEvent(name:)` and `delegateName` property.
/// Pattern from Starscream, RxSwift, StripeUICore.
public protocol EventDelegate {
    func didReceiveEvent(name: String) -> Bool
    func onComplete(handler: @escaping () -> Void)
    var delegateName: String { get }
}

// MARK: - Consumer Class

/// Class that uses EventDelegate — tests that the protocol proxy works
/// even when some methods are skipped.
public class EventRouter {
    public var delegate: (any EventDelegate)?

    public init() {
        self.delegate = nil
    }

    public func routeEvent(name: String) -> Bool {
        return delegate?.didReceiveEvent(name: name) ?? false
    }

    public func getDelegateName() -> String {
        return delegate?.delegateName ?? "none"
    }
}
