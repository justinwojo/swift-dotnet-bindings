// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Internal receiver forces the existing native-thunk fallback. The C exports
// only construct/observe; no C helper calls the operation under test.
private var nativeThunkCalls: Int32 = 0
private var nativeThunkDeinits: Int32 = 0

@usableFromInline
internal class NativeThunkReceiver {
    private let seed: Int32

    @usableFromInline
    internal init(seed: Int32) { self.seed = seed }

    public func add(_ delta: Int32) -> Int32 {
        nativeThunkCalls += 1
        return seed + delta
    }

    deinit { nativeThunkDeinits += 1 }
}

private final class NativeThunkChild: NativeThunkReceiver {
    public override func add(_ delta: Int32) -> Int32 { super.add(delta) + 100 }
}

@_cdecl("bt_native_thunk_create")
public func btNativeThunkCreate(_ child: Int32) -> UnsafeMutableRawPointer {
    let receiver: NativeThunkReceiver = child == 0
        ? NativeThunkReceiver(seed: 17) : NativeThunkChild(seed: 17)
    return Unmanaged.passRetained(receiver).toOpaque()
}

@_cdecl("bt_native_thunk_calls")
public func btNativeThunkCalls() -> Int32 { nativeThunkCalls }

@_cdecl("bt_native_thunk_deinits")
public func btNativeThunkDeinits() -> Int32 { nativeThunkDeinits }
