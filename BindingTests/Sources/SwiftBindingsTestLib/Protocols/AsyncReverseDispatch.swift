// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Real async reverse-dispatch witness (S13 Pillar C, Finding 36)
//
// A class-bound protocol with a single `async throws` requirement whose value/return
// shape is a blittable primitive. This is the first fixture that drives a reverse-dispatch
// protocol witness through a GENUINE continuation handoff rather than the legacy
// thread-blocking sync witness.
//
// Legacy path (what this REPLACES): the EveryProtocol witness for an async requirement was
// emitted SYNCHRONOUS, and the C# receiver blocked the impl's Task via
// `.GetAwaiter().GetResult()` on the reverse-dispatch slot. That is the upstream Mono Issue-1
// shape — a synchronously-blocked CallConvSwift reverse-dispatch — and it has no Swift error
// channel, so a faulted/cancelled C# producer could only FailFast the process.
//
// Real path (this fixture): `EveryProtocolEmitter.EmitsRealAsyncWitness` recognises the
// primitive-shaped `async throws` requirement and emits a real
// `func compute(_:) async throws -> Int32` that suspends on `withCheckedThrowingContinuation`
// and hands the continuation to C# through a widened Start-thunk vtable slot (+3 trailing
// pointers: continuation box, success FP, error FP). The C# receiver spawns the impl's Task and
// resumes the Swift continuation box exactly once — with the value on success, or with the error
// message on a fault (including `OperationCanceledException`). One code path, no per-runtime
// branch: the real witness runs on Mono (simulator), CoreCLR (macOS), and NativeAOT (device).
//
// `: AnyObject` so the C# proxy is class-backed (the reverse-dispatch vtable is class-keyed).
public protocol AsyncReverseCompute: AnyObject {
    func compute(_ n: Int32) async throws -> Int32
}

// Forward async driver: a C#→Swift `async throws` call that, inside Swift, reverse-dispatches
// back into the C# conformer's `compute(_:)`. The full round-trip under test is
//   C# test  →[forward async bridge]→  Swift callAsyncReverseCompute
//            →[real reverse-async witness]→  C# impl.ComputeAsync
//            →[continuation resume]→  back up to the awaiting C# test.
// The value (or thrown error) the C# impl produces flows back through the suspended Swift
// continuation, proving the witness actually suspended-and-resumed rather than blocked.
public func callAsyncReverseCompute(_ x: any AsyncReverseCompute, _ n: Int32) async throws -> Int32 {
    return try await x.compute(n)
}
