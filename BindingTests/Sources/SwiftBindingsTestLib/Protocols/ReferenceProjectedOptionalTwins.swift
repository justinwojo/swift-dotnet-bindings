// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Optional twins of value types projected as CLR reference types
//
// A decoder-style protocol declares the same requirement for a value and for its Optional:
// `decodeBytes(value: inout Data)` beside `decodeBytes(value: inout Data?)`. Data projects to
// `byte[]`, a CLR reference type, so both requirements erase to one C# overload and must be
// disambiguated by name. Each case reaches the C# conformer through the existential from Swift,
// and the return values differ per requirement so a collapsed member surfaces as a wrong value.

public protocol OptionalBytesFieldDecoder {
    mutating func decodeBytes(value: inout Data) -> Int
    mutating func decodeBytes(value: inout Data?) -> Int
    func measure(_ value: Data) -> Int
    func measure(_ value: Data?) -> Int
    func collect(_ values: [Data]) -> Int
    func collect(_ values: [Data?]) -> Int
}

/// Decodes through both inout requirements and reports what each wrote back:
/// `nonOptional * 1000 + optional`, where each part is the written byte count plus
/// the requirement's own return value.
public func optionalBytesFieldDecoderRoundTrip(_ decoder: OptionalBytesFieldDecoder) -> Int {
    var copy = decoder
    var bytes = Data([1, 2])
    var optionalBytes: Data? = nil
    let plain = copy.decodeBytes(value: &bytes) + bytes.count
    let optional = copy.decodeBytes(value: &optionalBytes) + (optionalBytes?.count ?? -1)
    return plain * 1000 + optional
}

public func optionalBytesFieldDecoderMeasure(_ decoder: OptionalBytesFieldDecoder) -> Int {
    decoder.measure(Data([7, 7, 7])) * 1000 + decoder.measure(nil)
}

public func optionalBytesFieldDecoderCollect(_ decoder: OptionalBytesFieldDecoder) -> Int {
    decoder.collect([Data([1]), Data([2, 3])]) * 1000 + decoder.collect([Data([4]), nil])
}
