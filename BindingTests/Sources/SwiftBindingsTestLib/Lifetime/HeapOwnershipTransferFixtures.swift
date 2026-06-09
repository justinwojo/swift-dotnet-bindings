// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Lifetime/leak/UAF regression fixtures
//
// Backs BindingTests/RuntimeTestsApp/Lifetime/Session4LifetimeTests.cs.
// One Swift symbol per fix so the C# test can hammer the exact emitter shape
// that was broken before the fix.

// MARK: - Frozen-struct-with-ref-fields closure arg

/// Drives a closure whose single parameter is `FrozenStructWithRef`
/// (frozen + ref fields → `IsFrozenStructProjectedAsClass`). The C# side
/// receives the value via `MarshalFromSwift<T>` → `NewFromPayload` →
/// `InitializeWithCopy` into a fresh `NativeMemory.Alloc` buffer.
///
/// Pre-fix the Swift adapter forgot to defer-deallocate the source heap
/// allocation, so every invocation leaked one buffer + one DeinitTracker.
/// Post-fix Swift emits `defer { ... deinitialize ... deallocate }` for
/// each call, so a tight loop is constant-memory.
public func runFrozenWithRefClosure(callback: (FrozenStructWithRef) -> Void) {
    let value = FrozenStructWithRef(b: 17)
    callback(value)
}

/// Multi-arg variant — confirms each `__heap_N` gets its own defer.
public func runTwoFrozenWithRefClosure(callback: (FrozenStructWithRef, FrozenStructWithRef) -> Void) {
    let a = FrozenStructWithRef(b: 1)
    let b = FrozenStructWithRef(b: 2)
    callback(a, b)
}

// MARK: - Async + existential param (any Protocol)

// AsyncSkipPolicyExistential.validateAsync(using: any SkipPolicyValidator)
// already covers this shape from BindingTests/Sources/.../Async/AsyncSkipPolicyShapes.swift.
// The runtime test hammers that existing entry point under load; no new fixture
// is needed here.

// MARK: - Nullable struct setter SafeHandlePin
//
// ShapeHolder.currentShape (Shape?) from
// BindingTests/Sources/.../WrapperCoverage/OptionalPropertyPaths.swift
// already covers this shape — the runtime test exercises that path under
// GC pressure to detect the original use-after-free window.
