// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - A frozen value's convention follows what it stores inline
//
// Whether Swift moves a `@frozen` struct or enum in registers or through memory depends only on
// what the value stores inline. A computed property stores nothing, so a resilient type it returns
// leaves the struct loadable. A stored payload or field that is resilient to clients, or an opaque
// existential (`Any`, `any P` for a P not bound to classes), makes the value address-only: Swift
// returns it through the indirect-result register and takes it by address. A payload kept in an
// `indirect` case's box is loadable. Each function below hands
// such a value to C# or takes one from it, and Swift reports what it sees, so a crossing that picks
// the other convention shows as a wrong value or a fault.

/// Not `@frozen`: resilient to clients.
public struct StoredLayoutDetail {
    public var value: Int
    public init(value: Int) { self.value = value }
}

// MARK: Computed properties store nothing

/// One stored Int: Swift passes it in one register. The computed and static members are not storage.
@frozen public struct StoredLayoutComputedCell {
    public var count: Int
    public init(count: Int) { self.count = count }
    public var detail: StoredLayoutDetail { StoredLayoutDetail(value: count * 10) }
    public static let unit = StoredLayoutDetail(value: 1)
}

public func makeStoredLayoutComputedCell(_ count: Int) -> StoredLayoutComputedCell { StoredLayoutComputedCell(count: count) }
public func storedLayoutComputedCellCount(_ cell: StoredLayoutComputedCell) -> Int { cell.count }
public func storedLayoutComputedCellEcho(_ cell: StoredLayoutComputedCell) -> StoredLayoutComputedCell { cell }

/// Two stored Ints: Swift passes it in two registers and returns it in two.
@frozen public struct StoredLayoutComputedPair {
    public var count: Int
    public var scale: Int
    public init(count: Int, scale: Int) { self.count = count; self.scale = scale }
    public var detail: StoredLayoutDetail { StoredLayoutDetail(value: count * scale) }
    /// Swift's reading of both fields, through `self`.
    public var summary: String { "\(count)x\(scale)" }
    public func scaled(by factor: Int) -> StoredLayoutComputedPair { StoredLayoutComputedPair(count: count, scale: scale * factor) }
}

public func makeStoredLayoutComputedPair(_ count: Int, _ scale: Int) -> StoredLayoutComputedPair {
    StoredLayoutComputedPair(count: count, scale: scale)
}
public func storedLayoutComputedPairName(_ pair: StoredLayoutComputedPair) -> String { pair.summary }
public func storedLayoutComputedPairEcho(_ pair: StoredLayoutComputedPair) -> StoredLayoutComputedPair { pair }

/// Stores the cell above, so it is as loadable as the cell: two registers.
@frozen public struct StoredLayoutComputedHolder {
    public var cell: StoredLayoutComputedCell
    public var tag: Int
    public init(cell: StoredLayoutComputedCell, tag: Int) { self.cell = cell; self.tag = tag }
}

public func makeStoredLayoutComputedHolder(_ count: Int, _ tag: Int) -> StoredLayoutComputedHolder {
    StoredLayoutComputedHolder(cell: StoredLayoutComputedCell(count: count), tag: tag)
}
public func storedLayoutComputedHolderName(_ holder: StoredLayoutComputedHolder) -> String { "\(holder.cell.count)/\(holder.tag)" }
public func storedLayoutComputedHolderEcho(_ holder: StoredLayoutComputedHolder) -> StoredLayoutComputedHolder { holder }

/// A `lazy` property is stored, as a hidden Optional of its resilient type: address-only.
@frozen public struct StoredLayoutLazyCell {
    public lazy var detail: StoredLayoutDetail = StoredLayoutDetail(value: count + 100)
    public var count: Int
    public init(count: Int) { self.count = count }
}

public func makeStoredLayoutLazyCell(_ count: Int) -> StoredLayoutLazyCell { StoredLayoutLazyCell(count: count) }
public func storedLayoutLazyCellCount(_ cell: StoredLayoutLazyCell) -> Int { cell.count }

// MARK: Frozen payload enums

/// A resilient payload stored inline: address-only, though the enum is `@frozen`.
@frozen public enum StoredLayoutChoice {
    case detail(StoredLayoutDetail)
    case none
}

public func makeStoredLayoutChoice(_ value: Int) -> StoredLayoutChoice { value == 0 ? .none : .detail(StoredLayoutDetail(value: value)) }
public func storedLayoutChoiceName(_ choice: StoredLayoutChoice) -> String {
    switch choice {
    case .detail(let detail): return "detail \(detail.value)"
    case .none: return "none"
    }
}
public func storedLayoutChoiceEcho(_ choice: StoredLayoutChoice) -> StoredLayoutChoice { choice }

/// Stores the enum above: address-only too.
@frozen public struct StoredLayoutChoiceHolder {
    public var choice: StoredLayoutChoice
    public var count: Int
    public init(choice: StoredLayoutChoice, count: Int) { self.choice = choice; self.count = count }
}

public func makeStoredLayoutChoiceHolder(_ value: Int, _ count: Int) -> StoredLayoutChoiceHolder {
    StoredLayoutChoiceHolder(choice: makeStoredLayoutChoice(value), count: count)
}
public func storedLayoutChoiceHolderName(_ holder: StoredLayoutChoiceHolder) -> String {
    "\(storedLayoutChoiceName(holder.choice))/\(holder.count)"
}
public func storedLayoutChoiceHolderEcho(_ holder: StoredLayoutChoiceHolder) -> StoredLayoutChoiceHolder { holder }

/// The same payload in an `indirect` case's box: the enum stores one reference and is loadable.
@frozen public enum StoredLayoutBoxedChoice {
    indirect case detail(StoredLayoutDetail)
    case none
}

public func makeStoredLayoutBoxedChoice(_ value: Int) -> StoredLayoutBoxedChoice { value == 0 ? .none : .detail(StoredLayoutDetail(value: value)) }
public func storedLayoutBoxedChoiceName(_ choice: StoredLayoutBoxedChoice) -> String {
    switch choice {
    case .detail(let detail): return "boxed \(detail.value)"
    case .none: return "none"
    }
}
public func storedLayoutBoxedChoiceEcho(_ choice: StoredLayoutBoxedChoice) -> StoredLayoutBoxedChoice { choice }

// MARK: Existential fields

/// An `Any` field alone: four words, but address-only, so never four registers.
@frozen public struct StoredLayoutAnyCell {
    public var value: Any
    public init(value: Any) { self.value = value }
}

public func makeStoredLayoutAnyCell(_ number: Int) -> StoredLayoutAnyCell { StoredLayoutAnyCell(value: number) }
public func makeStoredLayoutAnyTextCell(_ text: String) -> StoredLayoutAnyCell { StoredLayoutAnyCell(value: text) }
public func storedLayoutAnyCellName(_ cell: StoredLayoutAnyCell) -> String {
    if let number = cell.value as? Int { return "int \(number)" }
    if let text = cell.value as? String { return "text \(text)" }
    return "other"
}
public func storedLayoutAnyCellEcho(_ cell: StoredLayoutAnyCell) -> StoredLayoutAnyCell { cell }

public protocol StoredLayoutShape {
    var sides: Int { get }
}

public struct StoredLayoutPolygon: StoredLayoutShape {
    public var sides: Int
    public init(sides: Int) { self.sides = sides }
}

/// An opaque existential field: address-only.
@frozen public struct StoredLayoutShapeCell {
    public var shape: any StoredLayoutShape
    public var count: Int
    public init(shape: any StoredLayoutShape, count: Int) { self.shape = shape; self.count = count }
}

public func makeStoredLayoutShapeCell(_ sides: Int, _ count: Int) -> StoredLayoutShapeCell {
    StoredLayoutShapeCell(shape: StoredLayoutPolygon(sides: sides), count: count)
}
public func storedLayoutShapeCellName(_ cell: StoredLayoutShapeCell) -> String { "\(cell.shape.sides) sides/\(cell.count)" }
public func storedLayoutShapeCellEcho(_ cell: StoredLayoutShapeCell) -> StoredLayoutShapeCell { cell }

// MARK: Generic fields take the convention of their specialization

/// A native Swift class: an `unowned` reference to it is a single pointer.
public final class StoredLayoutAnchor {
    public let id: Int
    public init(id: Int) { self.id = id }
}

/// Unspecialized, the referent's class is unknown; specialized with a native class the box is one
/// pointer, and with an Objective-C class it would be address-only.
@frozen public struct StoredLayoutUnownedBox<T: AnyObject> {
    public unowned var reference: T
    public init(reference: T) { self.reference = reference }
}

/// The box specialized with a native class, then an Int: Swift passes it in two registers.
@frozen public struct StoredLayoutUnownedHolder {
    public var box: StoredLayoutUnownedBox<StoredLayoutAnchor>
    public var count: Int
    public init(box: StoredLayoutUnownedBox<StoredLayoutAnchor>, count: Int) { self.box = box; self.count = count }
}

public func makeStoredLayoutUnownedHolder(_ anchor: StoredLayoutAnchor, _ count: Int) -> StoredLayoutUnownedHolder {
    StoredLayoutUnownedHolder(box: StoredLayoutUnownedBox(reference: anchor), count: count)
}
public func storedLayoutUnownedHolderTotal(_ holder: StoredLayoutUnownedHolder) -> Int { holder.box.reference.id + holder.count }
public func storedLayoutUnownedHolderEcho(_ holder: StoredLayoutUnownedHolder) -> StoredLayoutUnownedHolder { holder }

/// A generic struct storing its parameter inline: as wide as what it is specialized with.
@frozen public struct StoredLayoutWrap<T> {
    public var value: T
    public init(value: T) { self.value = value }
}

/// `StoredLayoutWrap<Int>` then an Int: two registers.
@frozen public struct StoredLayoutWrappedNumber {
    public var wrapped: StoredLayoutWrap<Int>
    public var count: Int
    public init(wrapped: StoredLayoutWrap<Int>, count: Int) { self.wrapped = wrapped; self.count = count }
}

public func makeStoredLayoutWrappedNumber(_ value: Int, _ count: Int) -> StoredLayoutWrappedNumber {
    StoredLayoutWrappedNumber(wrapped: StoredLayoutWrap(value: value), count: count)
}
public func storedLayoutWrappedNumberTotal(_ holder: StoredLayoutWrappedNumber) -> Int { holder.wrapped.value * 100 + holder.count }
public func storedLayoutWrappedNumberEcho(_ holder: StoredLayoutWrappedNumber) -> StoredLayoutWrappedNumber { holder }

/// `StoredLayoutWrap<String>` then an Int: three registers, the middle one a reference.
@frozen public struct StoredLayoutWrappedText {
    public var wrapped: StoredLayoutWrap<String>
    public var count: Int
    public init(wrapped: StoredLayoutWrap<String>, count: Int) { self.wrapped = wrapped; self.count = count }
}

public func makeStoredLayoutWrappedText(_ text: String, _ count: Int) -> StoredLayoutWrappedText {
    StoredLayoutWrappedText(wrapped: StoredLayoutWrap(value: text), count: count)
}
public func storedLayoutWrappedTextName(_ holder: StoredLayoutWrappedText) -> String { "\(holder.wrapped.value)/\(holder.count)" }
public func storedLayoutWrappedTextCount(_ holder: StoredLayoutWrappedText) -> Int { holder.wrapped.value.count * 100 + holder.count }
public func storedLayoutWrappedTextEcho(_ holder: StoredLayoutWrappedText) -> StoredLayoutWrappedText { holder }
