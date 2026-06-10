// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Complex-enum completion heap-ownership pin
//
// Mirrors the closure-adapter shape adjacent to payment-card-scan SDK's completion
// wrappers: a class instance method with non-closure prelude params plus a
// trailing `@escaping (ComplexEnum) -> Void` completion closure whose enum has
// ARC-bearing payloads. Because the closure has a complex-enum arg, MCB's
// IsEligible gate accepts the method and the closure adapter routes through
// `MethodClosureBridge` (the generated wrapper symbol is `_sbw_mcb_*_present`,
// not the `_sbw_method_*` form used by payment-card-scan SDK's direct ClosureEmitter
// path). MCB's Swift wrapper still emits `UnsafeMutableRawPointer.allocate`
// + `initializeMemory` WITHOUT a Swift-side `defer { __heap_N.deallocate() }`
// — the C# callback takes ownership via `MarshalFromSwift<T>` ->
// `NewFromPayload` -> `SwiftSafeHandle<T>(handle)`, and
// `SwiftSafeHandle.ReleaseHandle` pairs `VWT.Destroy + NativeMemory.Free`.
// Adding a Swift-side `defer` would be a double-free.
//
// Two failure modes this fixture catches:
//   (a) MCB callback ownership regression — emitter reverts to
//       `MarshalBorrowedFromSwift` (suppresses finalization, leaks the heap
//       buffer). Symptom: `deinitCount < iterationCount` on the finalizer-only
//       path (no explicit dispose).
//   (b) Generator regression that adds a Swift-side `defer` for this category.
//       Symptom: double-free crash inside the bulk loop, or `deinitCount`
//       advances early before C# dispose.

public final class CompletionDeinitProbe {
    public let counterPtr: UnsafeMutablePointer<Int64>

    public init(counterPtr: UnsafeMutablePointer<Int64>) {
        self.counterPtr = counterPtr
    }

    deinit {
        counterPtr.pointee += 1
    }
}

public enum CompletionProbeOutcome {
    case completed(probe: CompletionDeinitProbe)
    case canceled(probe: CompletionDeinitProbe)
    case failed(code: Int32)
}

public final class CompletionProbePresenter {
    private let counterPtr: UnsafeMutablePointer<Int64>

    public init() {
        counterPtr = UnsafeMutablePointer<Int64>.allocate(capacity: 1)
        counterPtr.initialize(to: 0)
    }

    deinit {
        counterPtr.deinitialize(count: 1)
        counterPtr.deallocate()
    }

    public var deinitCount: Int64 {
        return counterPtr.pointee
    }

    public func resetDeinitCount() {
        counterPtr.pointee = 0
    }

    public func present(
        label: Int32,
        completion: @escaping (CompletionProbeOutcome) -> Void,
        animated: Bool
    ) {
        let probe = CompletionDeinitProbe(counterPtr: counterPtr)
        if animated {
            completion(.completed(probe: probe))
        } else {
            completion(.canceled(probe: probe))
        }
    }
}
