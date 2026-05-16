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

    /// Drives closure-param dispatch: calls `onComplete(handler:)` on the
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

// MARK: - Protocol with Primitives-Only Multi-Arg Closure

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

// MARK: - Protocol with Return-Typed Closure

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

// MARK: - Protocol with Throwing Closure (Shape 1)

/// Error type used by ThrowingIntDelegate's throwing closure so the C# side can
/// observe a real `Error` reference (not a marshalled value) via SwiftResult.Failure.
public struct ThrowingProcessorError: Error {
    public let code: Int32
    public init(code: Int32) { self.code = code }
}

/// Protocol with a throwing closure parameter: `(Int32) throws -> Int32`.
/// Tests Shape 1 — Cdecl @_cdecl invoke thunk with explicit
/// `_errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>` error-out
/// parameter on the wrapper side, surfaced as `SwiftResult<T, SwiftError>`
/// on the C# delegate signature.
public protocol ThrowingIntDelegate {
    func processInt(callback: @escaping (Int32) throws -> Int32)
}

/// Consumer for ThrowingIntDelegate. fireProcessInt builds a Swift throwing
/// closure that succeeds for non-negative input and throws for negative input,
/// then passes it to the delegate. The C# impl captures the closure so the
/// test can drive both success and failure paths and observe SwiftResult.
public class ThrowingIntRouter {
    public var delegate: (any ThrowingIntDelegate)?

    public init() {
        self.delegate = nil
    }

    public func fireProcessInt() {
        delegate?.processInt(callback: { input throws -> Int32 in
            if input < 0 {
                throw ThrowingProcessorError(code: 42)
            }
            return input &* 2
        })
    }
}

// MARK: - Protocol with Optional Closure Property (Shape 3)

/// Protocol with an `Optional<Closure>` property, `var handler: (() -> Void)?`.
/// Tests Shape 3 — closure-property setter/getter round-trip through
/// the EveryProtocol vtable. Setter unwraps the (fnPtr, ctx) pair the C# proxy
/// receives into a managed `Action` via `SwiftEscapingClosure`; getter
/// materialises a Swift closure that calls back into the C# delegate via a
/// per-(protocol, property) @_cdecl invoke thunk on the runtime side.
public protocol HasCallbackDelegate {
    var handler: (() -> Void)? { get set }
}

/// Driver for HasCallbackDelegate. The test sets a C# Action onto `delegate.handler`,
/// then `invokeHandler()` reads it back from Swift and fires it — exercising the
/// Swift→C# materialisation path. `setHandlerFromSwift` runs the inverse: Swift
/// constructs a closure and assigns it into the delegate, exercising the C# setter
/// receiver that wraps (fnPtr, ctx) back into a managed Action.
public class CallbackRouter {
    public var delegate: (any HasCallbackDelegate)?
    public var swiftHandlerFiredCount: Int32 = 0

    public init() {
        self.delegate = nil
    }

    public func invokeHandler() {
        delegate?.handler?()
    }

    public func setHandlerFromSwift(toNil: Bool) {
        if toNil {
            delegate?.handler = nil
        } else {
            delegate?.handler = { [weak self] in
                self?.swiftHandlerFiredCount += 1
            }
        }
    }

    public func clearHandlerOnDelegate() {
        delegate?.handler = nil
    }
}

// MARK: - Protocol with Closure-Returning Method (Shape 4)

/// Protocol with a method that *returns* a closure (Shape 4):
/// `func makeHandler() -> () -> Void`. The Swift caller invokes the method
/// through the EveryProtocol vtable, the C# proxy returns a (fnPtr, ctx) pair
/// describing a managed Action, and Swift wraps the pair into a real
/// `() -> Void` it can hold and call later. Pattern from delegate types that
/// vend per-event handlers on demand (Nuke `ImagePipeline.IDelegate`-style).
public protocol HandlerFactoryDelegate {
    func makeHandler() -> () -> Void
}

/// Driver for HandlerFactoryDelegate. `fetchAndInvokeHandler` invokes the
/// closure returned by the C# proxy — exercising Swift → C# method dispatch
/// that *returns* a closure, plus the C# → Swift back-call when the returned
/// closure is fired.
public class HandlerFactoryRouter {
    public var delegate: (any HandlerFactoryDelegate)?
    public var lastHandlerFiredCount: Int32 = 0

    public init() {
        self.delegate = nil
    }

    /// Drives Swift→C# dispatch that materialises a closure. After the call
    /// completes, immediately invokes the returned closure once so the test
    /// can observe that the C# Action ran through the materialisation thunk.
    public func fetchAndInvokeHandler() {
        guard let handler = delegate?.makeHandler() else { return }
        handler()
        lastHandlerFiredCount += 1
    }

    /// Holds onto the closure across an extension-frame boundary, then fires it.
    /// Sentinel against the same partial-application reabstraction trap
    /// hit on Optional<Closure> — the materialised closure must survive past the
    /// Swift extension frame that produced it.
    public func fetchHoldAndFireLater() {
        let captured = delegate?.makeHandler()
        captured?()
        lastHandlerFiredCount += 1
    }
}

// MARK: - Protocol with Async Closure Parameter (Shape 2)

/// Protocol with an `@escaping () async -> Int32` closure parameter. Tests
/// Shape 2 — the C# proxy receives a Swift async closure and surfaces it as a
/// `Func<Task<Int32>>` so the C# impl can `await` it. The cdecl invoke thunk on the
/// Swift side spawns a `Task` to drive `await closure()` and signals completion to
/// C# via a function-pointer completion callback (TaskCompletionSource bridge).
public protocol AsyncIntDelegate {
    func runAsync(handler: @escaping () async -> Int32)
}

/// Driver for AsyncIntDelegate. `fireRunAsync` builds a Swift async closure that
/// returns a fixed Int32 and passes it through the delegate. When the delegate
/// is a C# proxy, this exercises Swift → C# async closure-parameter dispatch.
public class AsyncIntRouter {
    public var delegate: (any AsyncIntDelegate)?
    public var lastValueProduced: Int32 = -1

    public init() {
        self.delegate = nil
    }

    /// Drives Swift→C# async closure-param dispatch. Hands the delegate a Swift
    /// async closure that returns `value`. The C# impl is expected to invoke
    /// (await) the handler and observe the returned Int32.
    public func fireRunAsync(returning value: Int32) {
        let v = value
        delegate?.runAsync(handler: { [weak self] in
            self?.lastValueProduced = v
            return v
        })
    }
}

// MARK: - Multi-shape composite

/// Multi-method protocol composing every supported closure/property/method
/// shape into a single delegate, mirroring the richness of real consumer
/// protocols (Nuke `ImagePipelineDelegate`, BlinkIDUX `CameraModel`). Every
/// member must dispatch via a real vtable receiver — no `EveryProtocol:
/// closure method` fatalError, no SB0003-obsolete throw stubs.
public protocol MultiShapeDelegate {
    var pipelineState: Int32 { get }
    var isTorchEnabled: Bool { get set }
    var onPipelineStateChange: (() -> Void)? { get set }
    func makePipelineStateReader() -> () -> Void
    func runDiagnosticsAsync(handler: @escaping () async -> Int32)
    func processPipelineStateThrowing(handler: @escaping (Int32) throws -> Int32)
}

/// Driver exercising every member of `MultiShapeDelegate`. Each fire-method
/// drives one Swift→C# dispatch shape so the C# test can assert all six
/// dispatch paths land in real receivers.
public class MultiShapeRouter {
    public var delegate: (any MultiShapeDelegate)?
    public var lastReadPipelineState: Int32 = -1
    public var lastAsyncValue: Int32 = -1
    public var pipelineStateChangeFireCount: Int32 = 0

    public init() {
        self.delegate = nil
    }

    public func readPipelineState() -> Int32 {
        return delegate?.pipelineState ?? -1
    }

    public func toggleTorch(on value: Bool) {
        delegate?.isTorchEnabled = value
    }

    public func drivePipelineStateChange() {
        if let cb = delegate?.onPipelineStateChange {
            cb()
            pipelineStateChangeFireCount += 1
        }
    }

    public func driveReadViaFactory() {
        if let reader = delegate?.makePipelineStateReader() {
            reader()
            lastReadPipelineState = delegate?.pipelineState ?? -1
        }
    }

    public func driveDiagnostics(returning value: Int32) {
        let v = value
        delegate?.runDiagnosticsAsync(handler: { [weak self] in
            self?.lastAsyncValue = v
            return v
        })
    }

    public func driveProcessThrowing(shouldThrow: Bool) {
        let mustThrow = shouldThrow
        delegate?.processPipelineStateThrowing(handler: { v in
            if mustThrow {
                throw NSError(domain: "MultiShape", code: 7, userInfo: [NSLocalizedDescriptionKey: "diagnostic failure"])
            }
            return v &+ 1
        })
    }

    /// Writes the optional closure property from Swift through the delegate. When
    /// the delegate is the C# proxy this drives the Swift → C# Optional<Closure>
    /// setter receiver — the path that wraps an inbound Swift closure as a
    /// managed Action on the C# impl. Mirrors `CallbackRouter.setHandlerFromSwift`
    /// so the multi-shape composite asserts the setter path through actual proxy
    /// dispatch, not direct C# property assignment.
    public func setOnPipelineStateChangeFromSwift(toNil: Bool) {
        if toNil {
            delegate?.onPipelineStateChange = nil
        } else {
            delegate?.onPipelineStateChange = { [weak self] in
                self?.pipelineStateChangeFireCount += 1
            }
        }
    }
}

// MARK: - S-2: Multi-arg method with value param + closure (Stripe shape)
//
// `STPIssuingCardEphemeralKeyProvider.createIssuingCardKey(withAPIVersion: String,
// completion: @escaping STPJSONResponseCompletionBlock)` is a pure-Swift protocol
// whose only method takes a non-closure (String) param followed by a dispatchable
// closure. Pre-fix, `IsDispatchableClosureMethod` rejected multi-arg signatures,
// so the proxy emitted the field but never assigned it — and the EveryProtocol
// extension generated a `fatalError` stub instead of a real witness. Both halves
// silently broke C#→Swift dispatch.

/// Provides keys via a completion handler. Mirrors the Stripe ephemeral-key
/// provider shape: leading non-closure param + trailing escaping closure.
public protocol EphemeralKeyProvider {
    func createKey(withAPIVersion version: String,
                   completion: @escaping (String) -> Void)
}

/// Calls the provider's `createKey` and surfaces the last completion payload
/// so test code can assert that the C# impl's completion call round-tripped
/// back through the Swift trampoline.
public class EphemeralKeyConsumer {
    public var provider: (any EphemeralKeyProvider)?
    public var lastVersion: String = ""
    public var lastKey: String = ""
    public var completionFireCount: Int32 = 0

    public init() {
        self.provider = nil
    }

    public func requestKey(version: String) {
        lastVersion = version
        provider?.createKey(withAPIVersion: version, completion: { [weak self] key in
            self?.lastKey = key
            self?.completionFireCount += 1
        })
    }
}

/// Three-arg variant — two non-closure params + closure — to lock the gate
/// behaviour beyond the two-arg Stripe shape.
public protocol RetryingKeyProvider {
    func fetchKey(version: String,
                  attempt: Int32,
                  completion: @escaping (Int32) -> Void)
}

public class RetryingKeyConsumer {
    public var provider: (any RetryingKeyProvider)?
    public var lastVersion: String = ""
    public var lastAttempt: Int32 = -1
    public var lastResult: Int32 = -1

    public init() {
        self.provider = nil
    }

    public func request(version: String, attempt: Int32) {
        lastVersion = version
        lastAttempt = attempt
        provider?.fetchKey(version: version, attempt: attempt, completion: { [weak self] r in
            self?.lastResult = r
        })
    }
}
