// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Lane-identity fixture for `nuke binding-tests --interp-identity`. The generator binds this
// module exactly as it binds a third-party library, and a small .NET 11 app calls the binding on
// the iOS-simulator and Mac Catalyst cells. Two things are measured per call:
//
// - Which code made the managed-to-Swift transition. Every entry point below records the return
//   addresses on its own stack (`backtrace`), resolves them with `dladdr` and writes them to
//   stderr before doing anything else, so a call that later crashes still leaves its receipt.
//   The first frame outside this module and its generated wrapper is the transition's caller:
//   the interpreter's call stub in libcoreclr, or precompiled ReadyToRun code.
// - Whether the transition delivered its arguments. Each shape below is one that today's
//   emission binds as a direct Swift-convention transition, chosen because the interpreter is
//   known (arm64) or suspected (x86_64) to mishandle it. They are negative controls: a cell that
//   really runs the interpreter must reproduce the failure, and a ReadyToRun cell must not.
//
// Nothing here reads `self` or a stored property on the paths that measure placement, so a
// receiver that arrives in the wrong register is reported instead of dereferenced.

import Darwin

// MARK: - Caller capture

private let iifFrameCapacity: Int32 = 16
nonisolated(unsafe) private let iifFrames =
    UnsafeMutablePointer<UnsafeMutableRawPointer?>.allocate(capacity: 16)
nonisolated(unsafe) private var iifCaptureSerial: Int32 = 0

// Writes `IIF CAPTURE <serial> <index> 0x<address> <image> <symbol|->` per frame, innermost
// first. Frame 0 lies inside this helper, so its image is this module.
@inline(never)
private func iifCapture() {
    let total = backtrace(iifFrames, iifFrameCapacity)
    iifCaptureSerial &+= 1
    for index in 0..<Int(total) {
        guard let frame = iifFrames[index] else { continue }
        var image = "?"
        var symbol = "-"
        var info = Dl_info()
        // A return address can sit one past the end of its caller; resolve the byte before it.
        if dladdr(frame - 1, &info) != 0 {
            if let file = info.dli_fname {
                image = String(cString: file).split(separator: "/").last.map(String.init) ?? "?"
            }
            if let name = info.dli_sname { symbol = String(cString: name) }
        }
        var line = "IIF CAPTURE \(iifCaptureSerial) \(index) 0x\(String(UInt(bitPattern: frame), radix: 16)) \(image) \(symbol)\n"
        line.withUTF8 { _ = write(2, $0.baseAddress, $0.count) }
    }
}

// MARK: - Positive controls (primitive and wrapper transitions)

// Primitive-only free function: the transition every runtime places correctly.
public func iifIdentityProbe(_ k: Int) -> Int {
    iifCapture()
    return k &* 3 &+ 1
}

// String round trip through the generated @_cdecl wrapper and Swift.Runtime's string carriers.
public func iifGreeting(_ name: String) -> String {
    iifCapture()
    return "hello, " + name
}

// Final class whose plain members the generator routes through the @_cdecl wrapper.
public final class IifCounter {
    public private(set) var count: Int
    public init(start: Int) { count = start }
    public func increment(by step: Int) -> Int {
        iifCapture()
        count &+= step
        return count
    }
}

// MARK: - Negative controls

// Non-frozen, so an `inout` of it reaches Swift by address. A member taking one keeps the
// receiver on the direct Swift-convention lane today.
public struct IifPoint {
    public var x: Int
    public var y: Int
    public init(x: Int, y: Int) { self.x = x; self.y = y }
}

public enum IifError: Error { case requested }

// Receiver for the self-register controls: a non-frozen struct, so Swift takes it by address in
// the self register (x20 / r13). Its first stored property is a small Int, so the word four
// bytes into the receiver is zero; the arm64 interpreter's transition code treats x20 as its
// Thread* after the call and writes that word. `stamp` records the address it received as self
// in `point.y`, so a receiver that arrived elsewhere shows up as a different value without
// being dereferenced. Neither member reads or writes the receiver's stored properties.
public struct IifSlot {
    public var tag: Int
    public var spare: Int
    public init(tag: Int) { self.tag = tag; spare = 0 }

    public mutating func stamp(_ point: inout IifPoint) {
        iifCapture()
        point.y = withUnsafeMutablePointer(to: &self) { Int(bitPattern: $0) }
        point.x = point.x &+ 1
    }

    // Typed self and error together: the error must come back in the error register
    // (x21 / r12), and the receiver in the self register.
    public mutating func stampThrowing(_ point: inout IifPoint, fail: Bool) throws {
        iifCapture()
        point.y = withUnsafeMutablePointer(to: &self) { Int(bitPattern: $0) }
        if fail { throw IifError.requested }
        point.x = point.x &+ 1
    }
}

// Failable initializer of a resilient struct: Swift returns it through the indirect-result
// register (x8 / rax).
public struct IifFailable {
    public let value: Int32
    public init?(value: Int32) {
        iifCapture()
        guard value >= 0 else { return nil }
        self.value = value &* 2
    }
}

// Swift invokes the closure the binding hands it. The binding's callback for a throwing closure
// is a Swift-convention UnmanagedCallersOnly that takes its context in the self register and
// reports failure through the error register. The forward call carries only the two-word
// closure, which every x86_64 calling convention places in the same two registers, so a failure
// here is the reverse transition's own. Returns the set's count, or -1 if the callback threw.
public func iifCallThrowing(_ callback: @escaping (Set<String>) throws -> Void) -> Int32 {
    iifCapture()
    let members: Set<String> = ["alpha", "beta", "gamma"]
    do {
        try callback(members)
        return Int32(members.count)
    } catch {
        return -1
    }
}

// Failable initializer that invokes a non-throwing closure over a non-frozen struct. The
// callback takes the struct's address and its context in the self register.
public struct IifVisitorFactory {
    public let number: Int32
    public init?(_ body: (IifPoint) -> Void, valid: Bool) {
        iifCapture()
        body(IifPoint(x: 7, y: 9))
        if !valid { return nil }
        number = 76
    }
}
