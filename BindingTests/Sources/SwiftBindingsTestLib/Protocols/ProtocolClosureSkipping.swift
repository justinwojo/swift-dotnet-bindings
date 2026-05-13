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

    /// Drives Session 4a closure-param dispatch: calls `onComplete(handler:)` on the
    /// delegate with a Swift-built closure. When the delegate is a C# proxy, this
    /// exercises Swift → C# closure-parameter marshalling via the expanded vtable.
    /// The closure mutates `lastHandlerTag` so test code can observe that the C# impl
    /// stored the closure and either invoked or held onto it.
    public var lastHandlerTag: String = ""

    public func fireOnComplete(tag: String) {
        let captured = tag
        delegate?.onComplete(handler: { [weak self] in
            self?.lastHandlerTag = captured
        })
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
/// onDataLoaded uses a String arg in its closure — String is not invoke-thunk-compatible
/// (only primitives/enums/class-returns are), so this remains routed through a fatalError
/// stub and exercises the "non-dispatchable closure shape" path.
public class DataLoader {
    public var delegate: (any DataLoadingDelegate)?

    public init() {
        self.delegate = nil
    }

    public func getSourceId() -> String {
        return delegate?.sourceIdentifier() ?? "unknown"
    }
}

// MARK: - Protocol with Primitives-Only Multi-Arg Closure (Session 4b)

/// Like DataLoadingDelegate but with primitives-only closure args, so the invoke-thunk
/// gate accepts it. Drives Swift→C# multi-arg closure dispatch.
public protocol NumericDataDelegate {
    func onNumericData(handler: @escaping (Int32, Int32, Bool) -> Void)
    func sourceTag() -> String
}

public class NumericDataLoader {
    public var delegate: (any NumericDataDelegate)?
    public var lastA: Int32 = -1
    public var lastB: Int32 = -1
    public var lastFlag: Bool = false

    public init() {
        self.delegate = nil
    }

    public func fireOnNumericData() {
        delegate?.onNumericData(handler: { [weak self] a, b, flag in
            self?.lastA = a
            self?.lastB = b
            self?.lastFlag = flag
        })
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
    /// Non-closure method: dispatched through vtable.
    /// Named `taskLabel` (not `taskName`) so the EveryProtocol conformance doesn't
    /// collide with `TaskDescriptor.taskName: String { get }` — Swift forbids a func
    /// and a var with the same bare name on the same conforming type.
    func taskLabel() -> String
}

/// Consumer class for CompletionDelegate.
public class TaskRunner {
    public var delegate: (any CompletionDelegate)?

    public init() {
        self.delegate = nil
    }

    public func getTaskName() -> String {
        return delegate?.taskLabel() ?? "idle"
    }

    /// Drives Swift→C# Optional<Closure> dispatch. Mutates `completionFiredCount`
    /// when the closure is invoked; the test can observe whether `execute(nil)`
    /// and `execute(non-nil)` round-trip correctly.
    public var completionFiredCount: Int32 = 0

    public func fireExecute(withCompletion: Bool) {
        if withCompletion {
            delegate?.execute(completion: { [weak self] in
                self?.completionFiredCount += 1
            })
        } else {
            delegate?.execute(completion: nil)
        }
    }
}

// MARK: - Protocol with Return-Typed Closure (Session 4b)

/// Protocol with a closure that returns a value: `() -> Int32`.
/// Tests Swift→C# dispatch of return-typed closures via the invoke-thunk path.
public protocol IntFactoryDelegate {
    func makeIntFactory(factory: @escaping () -> Int32)
}

public class IntFactoryRouter {
    public var delegate: (any IntFactoryDelegate)?
    public var lastReturnedValue: Int32 = -1

    public init() {
        self.delegate = nil
    }

    /// Drives Swift→C# return-typed closure dispatch: passes a Swift closure that
    /// returns a fixed Int32. The C# impl can invoke the captured handler and the
    /// returned Int32 round-trips back to Swift via the @_cdecl thunk.
    public func fireMakeFactory(returning value: Int32) {
        let v = value
        delegate?.makeIntFactory(factory: { [weak self] in
            self?.lastReturnedValue = v
            return v
        })
    }
}
