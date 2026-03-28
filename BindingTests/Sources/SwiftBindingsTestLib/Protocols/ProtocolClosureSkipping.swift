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

// MARK: - Protocol with Multi-Argument Closure (Tuple Unwrapping Test)

/// Protocol with a multi-argument closure method.
/// Tests that EveryProtocol closure stubs render multi-arg closures as
/// `(String, Int32, Bool) -> Void` instead of `((String, Int32, Bool)) -> Void`.
/// Pattern from Lottie (LottieURLSession), Nuke (DataLoading), GRDB (FTS5Tokenizer).
public protocol DataLoadingDelegate {
    /// Multi-arg closure: tests tuple unwrapping in EveryProtocol stub
    func onDataLoaded(handler: @escaping (String, Int32, Bool) -> Void)
    /// Non-closure method: dispatched through vtable
    func sourceIdentifier() -> String
}

/// Consumer class for DataLoadingDelegate.
public class DataLoader {
    public var delegate: (any DataLoadingDelegate)?

    public init() {
        self.delegate = nil
    }

    public func getSourceId() -> String {
        return delegate?.sourceIdentifier() ?? "unknown"
    }
}

// MARK: - Protocol with Optional Closure Parameter (@escaping Suppression Test)

/// Protocol with an optional closure parameter.
/// Tests that EveryProtocol closure stubs do NOT emit `@escaping` on
/// `Optional<Closure>` — optional closures are always escaping in Swift.
/// Pattern from Starscream (write(data:completion:)), Kingfisher.
public protocol CompletionDelegate {
    /// Optional closure param: tests @escaping suppression on Optional<Closure>
    func execute(completion: (() -> Void)?)
    /// Non-closure method: dispatched through vtable
    func taskName() -> String
}

/// Consumer class for CompletionDelegate.
public class TaskRunner {
    public var delegate: (any CompletionDelegate)?

    public init() {
        self.delegate = nil
    }

    public func getTaskName() -> String {
        return delegate?.taskName() ?? "idle"
    }
}
