// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Projected-key builder: protocol-path async CancellationToken inclusion (AF05 ruling b)
//
// Every async method emits a trailing C# `CancellationToken` parameter, so `func foo(_:) async`
// projects to `Task<int> FooAsync(int, CancellationToken)` while a sibling sync `func fooAsync(_:)`
// projects to `int FooAsync(int)`. These are two DISTINCT C# overloads. The projected-C# overload
// key must include that trailing CancellationToken for async methods — otherwise the two collapse to
// the same key and the protocol's requirement-dedup SILENTLY DROPS the second member.
//
// Pre-fix bug: the CLASS/default-overload key builders included the CancellationToken; the PROTOCOL
// key builder did NOT. So a protocol declaring BOTH `foo(_:) async` and `fooAsync(_:)` keyed both to
// `FooAsync(int)`, and only ONE `FooAsync` member emitted — the other was dropped from the interface
// AND from the proxy's witness forwarding, so reverse-dispatching the dropped requirement mis-routes.
//
// Fix (ruling b): the merged key builder appends CancellationToken for async on ALL paths, so the two
// requirements key apart and BOTH `FooAsync` overloads emit. This fixture proves it: the C# impl
// implements BOTH overloads, and both reverse-dispatch drivers below round-trip to their respective
// members (the async result is offset by +1000 so a mis-dispatch between the two is caught). The async
// requirement is non-throwing `async -> Int32`, routed through the S13 Pillar C real reverse-async
// witness (suspends on `withCheckedContinuation`, resumes from C#). `: AnyObject` for a class-backed
// proxy.
public protocol AsyncOverloadKeys: AnyObject {
    func foo(_ x: Int32) async -> Int32
    func fooAsync(_ x: Int32) -> Int32
}

// ASYNC driver — reverse-dispatches the `foo(_:) async` requirement (C# `Task<int> FooAsync(int,
// CancellationToken)`). If this requirement was the one dropped pre-fix, its proxy witness is missing
// and the await mis-routes; post-fix it reaches the C# impl's async `FooAsync`.
public func callAsyncOverloadFoo(_ k: any AsyncOverloadKeys, _ x: Int32) async -> Int32 {
    return await k.foo(x)
}

// SYNC driver — reverse-dispatches the `fooAsync(_:)` requirement (C# `int FooAsync(int)`). The
// distinct-overload sibling; pre-fix one of the two FooAsync members was dropped, so driving both is
// what proves BOTH requirements now emit and dispatch.
public func callAsyncOverloadFooSync(_ k: any AsyncOverloadKeys, _ x: Int32) -> Int32 {
    return k.fooAsync(x)
}
