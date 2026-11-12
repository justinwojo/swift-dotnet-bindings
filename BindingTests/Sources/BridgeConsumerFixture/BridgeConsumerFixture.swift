// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Packaged Debug consumer fixture for `nuke binding-tests --bridge-consumer`. A third-party-shaped
// module the SDK binds with the swiftc lowering evidence, so its Swift-convention members are called
// through native bridges (SBSR_) instead of a direct import. An ordinary .NET 11 Debug app consumes
// the packed binding, which runs on the CoreCLR interpreter, and calls it.
//
// Two transitions are measured per call:
// - BridgeAccount.accumulate: self, a Swift error and a scalar result, through the binding's own
//   bridge. The @convention(c) parameter keeps the member off the @_cdecl wrapper, so it stays a
//   Swift-convention transition that only a bridge can make for the interpreter.
// - Array's subscript getter ($sSayxSicig), through SwiftBindings.Runtime's bridge. The array the
//   binding hands out is backed by an NSArray subclass, so every element read runs object(at:)
//   inside the getter, where the stack still shows who called it.
//
// Each measured call records its own stack (`backtrace` + `dladdr`): the address, the image's load
// address and the symbol of every frame. The host checks that a bridge frame sits under the Swift
// code and that the interpreter's call stub sits under the bridge. A device app is stripped, so the
// load address lets the host resolve the stub against the build's unstripped copy of the runtime.

import Darwin
import Foundation

// MARK: - Stack capture

private let bcfFrameCapacity: Int32 = 48
nonisolated(unsafe) private var bcfCaptures: [String] = []
nonisolated(unsafe) private var bcfCaptureSerial = 0

// One `BCF FRAME <serial> <tag> <index> 0x<address> 0x<image base> <image> <symbol|->` line per
// frame, innermost first. Frame 0 lies inside this helper.
@inline(never)
private func bcfCapture(_ tag: String) {
    let frames = UnsafeMutablePointer<UnsafeMutableRawPointer?>.allocate(capacity: Int(bcfFrameCapacity))
    defer { frames.deallocate() }
    let total = Int(backtrace(frames, bcfFrameCapacity))
    bcfCaptureSerial += 1
    for index in 0..<total {
        guard let frame = frames[index] else { continue }
        var image = "?"
        var symbol = "-"
        var base: UInt = 0
        var info = Dl_info()
        // A return address can sit one past the end of its caller; resolve the byte before it.
        if dladdr(frame - 1, &info) != 0 {
            if let file = info.dli_fname {
                image = String(cString: file).split(separator: "/").last.map(String.init) ?? "?"
            }
            if let name = info.dli_sname { symbol = String(cString: name) }
            if let start = info.dli_fbase { base = UInt(bitPattern: start) }
        }
        bcfCaptures.append("BCF FRAME \(bcfCaptureSerial) \(tag) \(index) 0x\(String(UInt(bitPattern: frame), radix: 16)) " +
            "0x\(String(base, radix: 16)) \(image) \(symbol)")
    }
}

/// Every frame line recorded since the last call, then forgets them.
public func bridgeConsumerTakeCaptures() -> String {
    let lines = bcfCaptures.joined(separator: "\n")
    bcfCaptures.removeAll()
    return lines
}

// MARK: - Ownership

nonisolated(unsafe) private var bcfLive = 0

/// How many of this module's objects are alive: accounts, items and the arrays that hold them.
public func bridgeConsumerLiveObjects() -> Int {
    bcfLive
}

// MARK: - self + error + scalar result

public enum BridgeAccountError: Error {
    case negative(Int64)
}

public final class BridgeAccount {
    public private(set) var total: Int64

    public init(total: Int64) {
        self.total = total
        bcfLive += 1
    }

    deinit {
        bcfLive -= 1
    }

    /// Adds the mapped delta to the total and returns it. A negative mapped delta throws and leaves
    /// the total as it was, so the next call shows whether the receiver survived the error.
    public func accumulate(_ delta: Int64, through map: @convention(c) (Int64) -> Int64) throws -> Int64 {
        bcfCapture("accumulate")
        let mapped = map(delta)
        if mapped < 0 {
            throw BridgeAccountError.negative(mapped)
        }
        total += mapped
        return total
    }
}

// MARK: - Array subscript getter

public final class BridgeItem {
    public let value: Int64

    public init(value: Int64) {
        self.value = value
        bcfLive += 1
    }

    deinit {
        bcfLive -= 1
    }
}

nonisolated(unsafe) private var bcfElementReads = 0

/// How many element reads have reached the NSArray. A read that does not raise it was served from
/// native storage, so the array is no longer the NSArray-backed one and its stack proves nothing.
public func bridgeConsumerElementReads() -> Int {
    bcfElementReads
}

// An NSArray whose elements live in Swift. Bridging copies an NSArray with copy(with:) before
// wrapping it, so returning self keeps this class, and its object(at:), behind the Swift array.
private final class BridgeItemStorage: NSArray {
    private let items: [BridgeItem]

    init(items: [BridgeItem]) {
        self.items = items
        bcfLive += 1
        super.init()
    }

    // NSArray's designated initializers, so the subclass inherits its required convenience ones.
    override convenience init() {
        self.init(items: [])
    }

    override convenience init(objects: UnsafePointer<AnyObject>?, count: Int) {
        self.init(items: (0..<count).map { objects![$0] as! BridgeItem })
    }

    required init?(coder: NSCoder) {
        nil
    }

    deinit {
        bcfLive -= 1
    }

    override var count: Int { items.count }

    override func object(at index: Int) -> Any {
        bcfElementReads += 1
        bcfCapture("element")
        return items[index]
    }

    override func copy(with zone: NSZone? = nil) -> Any { self }
}

/// `count` items valued `first`, `first + 1`, ..., in an array Swift reads through NSArray, so each
/// element read runs inside the subscript getter.
public func bridgeConsumerItems(count: Int, first: Int64) -> [BridgeItem] {
    let items = (0..<count).map { BridgeItem(value: first + Int64($0)) }
    return BridgeItemStorage(items: items) as! [BridgeItem]
}
