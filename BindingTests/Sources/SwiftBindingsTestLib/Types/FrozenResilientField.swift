// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - A frozen struct storing a value resilient to its clients
//
// A `@frozen` struct fixes its own field order, but not the size of a field whose type is not frozen:
// under library evolution a client cannot know a non-frozen enum's size, so the struct storing it is
// address-only to the client. Swift returns it through the indirect-result register and takes it as
// an argument by address, however small it is in the defining module. Each function below either
// hands such a struct to C# or takes one from it, and Swift reports every stored value it sees, so a
// crossing that moves the struct in registers shows as a wrong value or a fault.
//
// The frozen-enum twin is the control: its layout is fixed to clients and it stays loadable.

/// Not `@frozen`: resilient to clients.
public enum ResilientFieldMode {
    case idle
    case running
    case stopped
}

/// `@frozen`: one byte to every client.
@frozen public enum FrozenFieldMode {
    case idle
    case running
    case stopped
}

public func resilientFieldModeName(_ mode: ResilientFieldMode) -> String {
    switch mode {
    case .idle: return "idle"
    case .running: return "running"
    case .stopped: return "stopped"
    }
}

/// One stored field, one byte in the defining module.
@frozen public struct ResilientFieldCell {
    public var mode: ResilientFieldMode
    public init(mode: ResilientFieldMode) { self.mode = mode }
}

public func makeResilientFieldCell(_ mode: ResilientFieldMode) -> ResilientFieldCell { ResilientFieldCell(mode: mode) }
public func resilientFieldCellName(_ cell: ResilientFieldCell) -> String { resilientFieldModeName(cell.mode) }

/// Two stored fields, sixteen bytes in the defining module.
@frozen public struct ResilientFieldPair {
    public var mode: ResilientFieldMode
    public var count: Int
    public init(mode: ResilientFieldMode, count: Int) { self.mode = mode; self.count = count }

    /// Swift's reading of both fields, through `self`.
    public var summary: String { "\(resilientFieldModeName(mode))x\(count)" }
    public mutating func advance(by step: Int) { count += step }
    public func scaled(by factor: Int) -> ResilientFieldPair { ResilientFieldPair(mode: mode, count: count * factor) }
}

public func makeResilientFieldPair(_ mode: ResilientFieldMode, _ count: Int) -> ResilientFieldPair {
    ResilientFieldPair(mode: mode, count: count)
}
public func resilientFieldPairName(_ pair: ResilientFieldPair) -> String { pair.summary }
public func resilientFieldPairEcho(_ pair: ResilientFieldPair) -> ResilientFieldPair { pair }

/// The resilient enum as an Optional's payload, which the struct stores inline.
@frozen public struct ResilientFieldMaybe {
    public var mode: ResilientFieldMode?
    public var count: Int
    public init(mode: ResilientFieldMode?, count: Int) { self.mode = mode; self.count = count }
}

public func makeResilientFieldMaybe(_ mode: ResilientFieldMode?, _ count: Int) -> ResilientFieldMaybe {
    ResilientFieldMaybe(mode: mode, count: count)
}
public func resilientFieldMaybeName(_ maybe: ResilientFieldMaybe) -> String {
    "\(maybe.mode.map(resilientFieldModeName) ?? "none")x\(maybe.count)"
}

/// The control: the same shape over a frozen enum, loadable to every client.
@frozen public struct FrozenFieldPair {
    public var mode: FrozenFieldMode
    public var count: Int
    public init(mode: FrozenFieldMode, count: Int) { self.mode = mode; self.count = count }
}

public func makeFrozenFieldPair(_ mode: FrozenFieldMode, _ count: Int) -> FrozenFieldPair { FrozenFieldPair(mode: mode, count: count) }
public func frozenFieldPairName(_ pair: FrozenFieldPair) -> String {
    let name: String
    switch pair.mode {
    case .idle: name = "idle"
    case .running: name = "running"
    case .stopped: name = "stopped"
    }
    return "\(name)x\(pair.count)"
}
public func frozenFieldPairEcho(_ pair: FrozenFieldPair) -> FrozenFieldPair { pair }
