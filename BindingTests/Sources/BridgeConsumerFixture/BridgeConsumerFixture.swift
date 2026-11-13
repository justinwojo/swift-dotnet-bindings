// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Ordinary-consumer fixture for `nuke binding-tests --bridge-consumer`. A third-party-shaped module
// the SDK binds with its default settings: the SDK's own wrapper compile collects the swiftc lowering
// receipt and routes what it authorizes through native bridges (SBSR_) instead of a direct import.
// An ordinary app consumes the binding (as a package, as the app's own SDK binding, or through a
// ProjectReference) and calls it on the CoreCLR interpreter, Mono or NativeAOT.
//
// Two transitions are measured per call:
// - BridgeAccount.settled: self, a Swift error and a scalar result, through the binding's own
//   bridge. BridgeAccount is isolated to a global actor, and a synchronous nonisolated wrapper
//   cannot read an isolated property, so the binding reaches the getter directly through the Swift
//   calling convention: the transition only a bridge can make safely for the interpreter and Mono.
// - Array's subscript getter ($sSayxSicig), through SwiftBindings.Runtime's bridge. The array the
//   binding hands out is backed by an NSArray subclass, so every element read runs object(at:)
//   inside the getter, where the stack still shows who called it.
//
// Each measured call records its own stack (`backtrace` + `dladdr`): the address, the image's load
// address and the symbol of every frame. The host checks that a bridge frame sits under the Swift
// code and that the runtime's managed-to-native transition sits under the bridge. A device app is
// stripped, so the load address lets the host resolve the stub against the build's unstripped copy
// of the runtime.

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

/// The fixture's global actor. Its executor is the main actor's, so the app's main-thread calls run
/// on the actor's executor.
@globalActor
public actor BridgeConsumerActor {
    public static let shared = BridgeConsumerActor()

    public nonisolated var unownedExecutor: UnownedSerialExecutor {
        MainActor.sharedUnownedExecutor
    }
}

public enum BridgeAccountError: Error {
    case negative(Int64)
}

@BridgeConsumerActor
public final class BridgeAccount {
    public private(set) var total: Int64

    /// The delta the next read of `settled` adds to the total.
    public var pending: Int64 = 0

    nonisolated public init(total: Int64) {
        self.total = total
        bcfLive += 1
    }

    deinit {
        bcfLive -= 1
    }

    /// Adds the pending delta to the total, clears it and returns the new total. A negative pending
    /// delta throws and leaves both as they were, so the next read shows whether the receiver
    /// survived the error.
    public var settled: Int64 {
        get throws {
            bcfCapture("settle")
            if pending < 0 {
                throw BridgeAccountError.negative(pending)
            }
            total += pending
            pending = 0
            return total
        }
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
