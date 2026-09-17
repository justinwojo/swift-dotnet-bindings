// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Parent-generic specialization against the projected where clause
//
// A generic struct constrained to a message protocol and Hashable is specialized per concrete
// conformer (`DecodableWireField<WireNote>`), which C# can only name when the conformer's
// projection implements every interface the parent's `where` clause places on the parameter.
// `DecodableWireMessage` carries a generic requirement no concrete C# type can implement, so
// `WireNote` keeps the Swift conformance but its C# projection drops `IDecodableWireMessage` —
// `DecodableWireField<WireNote>` does not compile in C# and must not be specialized. The same
// conformer does implement `IPlainWireMessage`, so `PlainWireField<WireNote>` is specialized.

public protocol WireDecoder {
    mutating func nextTag() -> Int
}

public protocol DecodableWireMessage {
    var wireTag: Int { get }
    mutating func decodeMessage<D: WireDecoder>(decoder: inout D)
}

public protocol PlainWireMessage {
    var wireTag: Int { get }
}

public struct WireNote: DecodableWireMessage, PlainWireMessage, Hashable {
    public var wireTag: Int
    public init(wireTag: Int) { self.wireTag = wireTag }
    public mutating func decodeMessage<D: WireDecoder>(decoder: inout D) { wireTag = decoder.nextTag() }
}

public struct DecodableWireField<G: DecodableWireMessage & Hashable>: Hashable {
    public var value: G
    public init(value: G) { self.value = value }
    public var isTagged: Bool { value.wireTag != 0 }
}

public struct PlainWireField<G: PlainWireMessage & Hashable>: Hashable {
    public var value: G
    public init(value: G) { self.value = value }
    public var isTagged: Bool { value.wireTag != 0 }
}
