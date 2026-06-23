// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Legacy blocking async receiver: protocol-path async CancellationToken inclusion (AF05 ruling b)
//
// Companion to AsyncOverloadKeys, but for the LEGACY blocking reverse-dispatch receiver instead of the
// S13 Pillar C real reverse-async witness. The async requirement returns a NON-blittable `String`, which
// the real-async-witness predicate (EveryProtocolEmitter.EmitsRealAsyncWitness) rejects — so its witness
// is satisfied through the sync-ABI slot and the receiver blocks the impl's Task
// (`.GetAwaiter().GetResult()`).
//
// As with AsyncOverloadKeys, every async method emits a trailing C# `CancellationToken`, so
// `func bar(_:) async -> String` projects to `Task<string> BarAsync(int, CancellationToken)` while the
// sibling sync `func barAsync(_:) -> String` projects to `string BarAsync(int)`. AF05 ruling b keeps BOTH
// distinct overloads. The legacy receiver forwards `impl.BarAsync(args).GetAwaiter().GetResult()`: a BARE
// argument list binds the SYNC `string BarAsync(int)` overload (exact arity), whose return is not
// awaitable — so `.GetAwaiter()` is a CS1061 and the generated proxy fails to COMPILE. The receiver must
// pass the trailing token explicitly (`impl.BarAsync(arg, default(CancellationToken)).GetAwaiter()...`) so
// the call binds the async `Task<string>` overload.
//
// This fixture proves it end to end: the C# impl implements BOTH overloads, the generated proxy compiles,
// and both reverse-dispatch drivers round-trip to their respective members (the sync result is tagged
// "sync:" and the async "async:" so a mis-dispatch between the two is caught). `: AnyObject` for a
// class-backed proxy.
public protocol AsyncBlockingOverloadKeys: AnyObject {
    func bar(_ x: Int32) async -> String
    func barAsync(_ x: Int32) -> String
}

// ASYNC driver — reverse-dispatches the `bar(_:) async` requirement (C# `Task<string> BarAsync(int,
// CancellationToken)`) through the legacy blocking witness slot. Pre-fix the proxy receiver's bare impl
// call binds the sync overload and the proxy never compiles; post-fix it reaches the C# impl's async
// `BarAsync`.
public func callAsyncBlockingOverloadBar(_ k: any AsyncBlockingOverloadKeys, _ x: Int32) async -> String {
    return await k.bar(x)
}

// SYNC driver — reverse-dispatches the `barAsync(_:)` requirement (C# `string BarAsync(int)`), the
// distinct-overload sibling that makes the bare-call binding ambiguous.
public func callAsyncBlockingOverloadBarSync(_ k: any AsyncBlockingOverloadKeys, _ x: Int32) -> String {
    return k.barAsync(x)
}
