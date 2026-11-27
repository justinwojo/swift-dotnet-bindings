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

// MARK: Sub-word fields after a word keep Swift's packed offsets
//
// A one-byte Optional or a tag byte packs right after the field before it, so the field after it can
// start off a word boundary. The value's managed Buffer has to place each field where Swift does and
// be no wider than Swift's stride. `storedLayout…Layout()` reports Swift's own numbers.

/// `StoredLayoutWrap<Int>`, a `UInt8?` and a `UInt8`: size 11, the tail at 10.
@frozen public struct StoredLayoutWrappedFlags {
    public var wrapped: StoredLayoutWrap<Int>
    public var flag: UInt8?
    public var tail: UInt8
    public init(wrapped: StoredLayoutWrap<Int>, flag: UInt8?, tail: UInt8) { self.wrapped = wrapped; self.flag = flag; self.tail = tail }
    public mutating func bumpTail() { tail &+= 1 }
}

public func makeStoredLayoutWrappedFlags(_ value: Int, _ flag: UInt8?, _ tail: UInt8) -> StoredLayoutWrappedFlags {
    StoredLayoutWrappedFlags(wrapped: StoredLayoutWrap(value: value), flag: flag, tail: tail)
}
public func storedLayoutWrappedFlagsName(_ holder: StoredLayoutWrappedFlags) -> String {
    "\(holder.wrapped.value)/\(holder.flag.map(String.init) ?? "nil")/\(holder.tail)"
}
public func storedLayoutWrappedFlagsEcho(_ holder: StoredLayoutWrappedFlags) -> StoredLayoutWrappedFlags { holder }
public func storedLayoutWrappedFlagsBump(_ holder: inout StoredLayoutWrappedFlags) { holder.bumpTail() }
public func storedLayoutWrappedFlagsLayout() -> String {
    "\(MemoryLayout<StoredLayoutWrappedFlags>.size)/\(MemoryLayout<StoredLayoutWrappedFlags>.stride)/\(MemoryLayout<StoredLayoutWrappedFlags>.offset(of: \.tail)!)"
}

/// A `String`, an `Int16?` and a `UInt8`: size 20, the optional at 16, the tail at 19.
@frozen public struct StoredLayoutTextFlags {
    public var text: String
    public var flag: Int16?
    public var tail: UInt8
    public init(text: String, flag: Int16?, tail: UInt8) { self.text = text; self.flag = flag; self.tail = tail }
}

public func makeStoredLayoutTextFlags(_ text: String, _ flag: Int16?, _ tail: UInt8) -> StoredLayoutTextFlags {
    StoredLayoutTextFlags(text: text, flag: flag, tail: tail)
}
public func storedLayoutTextFlagsName(_ holder: StoredLayoutTextFlags) -> String {
    "\(holder.text)/\(holder.flag.map(String.init) ?? "nil")/\(holder.tail)"
}
public func storedLayoutTextFlagsEcho(_ holder: StoredLayoutTextFlags) -> StoredLayoutTextFlags { holder }
public func storedLayoutTextFlagsLayout() -> String {
    "\(MemoryLayout<StoredLayoutTextFlags>.size)/\(MemoryLayout<StoredLayoutTextFlags>.stride)/\(MemoryLayout<StoredLayoutTextFlags>.offset(of: \.tail)!)"
}

/// One byte, then an Int: aligned to 8 though it starts with a byte.
@frozen public struct StoredLayoutByteThenWord {
    public var lead: UInt8
    public var word: Int
    public init(lead: UInt8, word: Int) { self.lead = lead; self.word = word }
}

/// A byte, the struct above at 8, then a `String` at 24: Swift leaves padding ahead of the inner
/// struct that its own first byte does not reach.
@frozen public struct StoredLayoutPaddedText {
    public var lead: UInt8
    public var inner: StoredLayoutByteThenWord
    public var text: String
    public init(lead: UInt8, inner: StoredLayoutByteThenWord, text: String) { self.lead = lead; self.inner = inner; self.text = text }
}

public func makeStoredLayoutPaddedText(_ lead: UInt8, _ innerLead: UInt8, _ word: Int, _ text: String) -> StoredLayoutPaddedText {
    StoredLayoutPaddedText(lead: lead, inner: StoredLayoutByteThenWord(lead: innerLead, word: word), text: text)
}
public func storedLayoutPaddedTextName(_ holder: StoredLayoutPaddedText) -> String {
    "\(holder.lead)/\(holder.inner.lead)/\(holder.inner.word)/\(holder.text)"
}
public func storedLayoutPaddedTextEcho(_ holder: StoredLayoutPaddedText) -> StoredLayoutPaddedText { holder }
public func storedLayoutPaddedTextLayout() -> String {
    "\(MemoryLayout<StoredLayoutPaddedText>.size)/\(MemoryLayout<StoredLayoutPaddedText>.stride)/\(MemoryLayout<StoredLayoutPaddedText>.offset(of: \.text)!)"
}

// MARK: A stored class reference is a strong reference the value owns
//
// A `@frozen` struct storing a class reference, a class-bound existential, an `Error` or a tuple
// holding a class owns a +1 on that object. C# must give the value an owner that releases it, so
// each holder below counts the objects it keeps alive and Swift checks it still holds the very
// object C# handed it.

/// Counts its live instances, so a holder that leaks or over-releases shows as a wrong count.
public final class StoredLayoutTrackedRef: StoredLayoutTagged, StoredLayoutLabelled {
    private static var live: Int32 = 0
    public let value: Int
    public init(value: Int) { self.value = value; Self.live += 1 }
    deinit { Self.live -= 1 }
    public static var liveCount: Int32 { live }
    public var trackedTag: Int { value * 10 }
    public var trackedLabel: String { "ref\(value)" }
}

// Requirement names stay unique in the module: protocols share one reverse-dispatch class, so a
// same-named requirement of another type elsewhere would drop that class.
public protocol StoredLayoutTagged: AnyObject { var trackedTag: Int { get } }
public protocol StoredLayoutLabelled { var trackedLabel: String { get } }

/// Carries a counted object, so the boxed error's lifetime shows in the live count.
public struct StoredLayoutFailure: Error {
    public let code: Int
    let token: StoredLayoutTrackedRef
    public init(code: Int) { self.code = code; self.token = StoredLayoutTrackedRef(value: code) }
}

/// `AnyObject`: one pointer, which C# also reads and writes through the field's own accessors.
@frozen public struct StoredLayoutObjectHolder {
    public var object: AnyObject
    public var count: Int
    public init(ref: StoredLayoutTrackedRef, count: Int) { self.object = ref; self.count = count }
}

/// A class-bound existential: the object and one witness table.
@frozen public struct StoredLayoutTaggedHolder {
    public var item: any StoredLayoutTagged
    public var count: Int
    public init(item: any StoredLayoutTagged, count: Int) { self.item = item; self.count = count }
}

/// A class-bound composition: the object and two witness tables.
@frozen public struct StoredLayoutLabelledHolder {
    public var item: any StoredLayoutTagged & StoredLayoutLabelled
    public var count: Int
    public init(item: any StoredLayoutTagged & StoredLayoutLabelled, count: Int) { self.item = item; self.count = count }
}

/// `any Error`: one pointer to a boxed error.
@frozen public struct StoredLayoutErrorHolder {
    public var failure: any Error
    public var count: Int
    public init(failure: any Error, count: Int) { self.failure = failure; self.count = count }
}

/// A tuple whose first element is a class reference. It is private, so only its storage reaches
/// C#: the reference still has to be released with the value.
@frozen public struct StoredLayoutRefPairHolder {
    private var pair: (StoredLayoutTrackedRef, Int)
    public init(ref: StoredLayoutTrackedRef, count: Int) { self.pair = (ref, count) }
    public var summary: String { "\(pair.0.value)/\(pair.1)" }
    public func holds(_ ref: StoredLayoutTrackedRef) -> Bool { pair.0 === ref }
}

public func makeStoredLayoutObjectHolder(_ value: Int, _ count: Int) -> StoredLayoutObjectHolder {
    StoredLayoutObjectHolder(ref: StoredLayoutTrackedRef(value: value), count: count)
}
public func storedLayoutObjectHolderName(_ holder: StoredLayoutObjectHolder) -> String {
    "\((holder.object as? StoredLayoutTrackedRef)?.value ?? -1)/\(holder.count)"
}
public func storedLayoutObjectHolderHolds(_ holder: StoredLayoutObjectHolder, _ ref: StoredLayoutTrackedRef) -> Bool {
    holder.object === ref
}
public func storedLayoutObjectHolderEcho(_ holder: StoredLayoutObjectHolder) -> StoredLayoutObjectHolder { holder }
public func storedLayoutObjectHolderLayout() -> String {
    "\(MemoryLayout<StoredLayoutObjectHolder>.size)/\(MemoryLayout<StoredLayoutObjectHolder>.stride)/\(MemoryLayout<StoredLayoutObjectHolder>.offset(of: \.count)!)"
}

public func makeStoredLayoutTaggedHolder(_ value: Int, _ count: Int) -> StoredLayoutTaggedHolder {
    StoredLayoutTaggedHolder(item: StoredLayoutTrackedRef(value: value), count: count)
}
public func storedLayoutTaggedHolderName(_ holder: StoredLayoutTaggedHolder) -> String { "\(holder.item.trackedTag)/\(holder.count)" }
public func storedLayoutTaggedHolderHolds(_ holder: StoredLayoutTaggedHolder, _ ref: StoredLayoutTrackedRef) -> Bool {
    holder.item === ref
}
public func storedLayoutTaggedHolderEcho(_ holder: StoredLayoutTaggedHolder) -> StoredLayoutTaggedHolder { holder }
public func storedLayoutTaggedHolderLayout() -> String {
    "\(MemoryLayout<StoredLayoutTaggedHolder>.size)/\(MemoryLayout<StoredLayoutTaggedHolder>.stride)/\(MemoryLayout<StoredLayoutTaggedHolder>.offset(of: \.count)!)"
}

public func makeStoredLayoutLabelledHolder(_ value: Int, _ count: Int) -> StoredLayoutLabelledHolder {
    StoredLayoutLabelledHolder(item: StoredLayoutTrackedRef(value: value), count: count)
}
public func storedLayoutLabelledHolderName(_ holder: StoredLayoutLabelledHolder) -> String {
    "\(holder.item.trackedLabel)/\(holder.item.trackedTag)/\(holder.count)"
}
public func storedLayoutLabelledHolderEcho(_ holder: StoredLayoutLabelledHolder) -> StoredLayoutLabelledHolder { holder }
public func storedLayoutLabelledHolderLayout() -> String {
    "\(MemoryLayout<StoredLayoutLabelledHolder>.size)/\(MemoryLayout<StoredLayoutLabelledHolder>.stride)/\(MemoryLayout<StoredLayoutLabelledHolder>.offset(of: \.count)!)"
}

public func makeStoredLayoutErrorHolder(_ code: Int, _ count: Int) -> StoredLayoutErrorHolder {
    StoredLayoutErrorHolder(failure: StoredLayoutFailure(code: code), count: count)
}
public func storedLayoutErrorHolderName(_ holder: StoredLayoutErrorHolder) -> String {
    "\((holder.failure as? StoredLayoutFailure)?.code ?? -1)/\(holder.count)"
}
public func storedLayoutErrorHolderEcho(_ holder: StoredLayoutErrorHolder) -> StoredLayoutErrorHolder { holder }
public func storedLayoutErrorHolderLayout() -> String {
    "\(MemoryLayout<StoredLayoutErrorHolder>.size)/\(MemoryLayout<StoredLayoutErrorHolder>.stride)/\(MemoryLayout<StoredLayoutErrorHolder>.offset(of: \.count)!)"
}

public func makeStoredLayoutRefPairHolder(_ value: Int, _ count: Int) -> StoredLayoutRefPairHolder {
    StoredLayoutRefPairHolder(ref: StoredLayoutTrackedRef(value: value), count: count)
}
public func storedLayoutRefPairHolderName(_ holder: StoredLayoutRefPairHolder) -> String { holder.summary }
public func storedLayoutRefPairHolderEcho(_ holder: StoredLayoutRefPairHolder) -> StoredLayoutRefPairHolder { holder }
public func storedLayoutRefPairHolderLayout() -> String {
    "\(MemoryLayout<StoredLayoutRefPairHolder>.size)/\(MemoryLayout<StoredLayoutRefPairHolder>.stride)/0"
}
