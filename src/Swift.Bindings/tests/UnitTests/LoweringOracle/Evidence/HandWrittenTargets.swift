// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Calls every standard-library and Foundation function the hand-written transition tables bind
// (the runtime's and the Apple supplement's), so swiftc declares each one with the signature it
// calls it by. Compiled unoptimized by the lowering oracle; the evidence it records is what a plan
// of these targets is held to. A target added to a table needs its call here.

import Foundation

// Swift.Runtime: Array and Dictionary subscripts, Dictionary and Set iterators.
public func arraySubscript<Element>(_ array: [Element], _ index: Int) -> Element { array[index] }
public func dictionarySubscript<Key: Hashable, Value>(_ dictionary: [Key: Value], _ key: Key) -> Value? { dictionary[key] }
public func dictionaryIterator<Key: Hashable, Value>(_ dictionary: [Key: Value]) -> Dictionary<Key, Value>.Iterator { dictionary.makeIterator() }
public func setIterator<Element: Hashable>(_ set: Set<Element>) -> Set<Element>.Iterator { set.makeIterator() }

// SwiftBindings.Apple: Data.
public func dataInit(_ bytes: UnsafeRawPointer, _ count: Int) -> Data { Data(bytes: bytes, count: count) }
public func dataCount(_ data: Data) -> Int { data.count }
public func dataCopyBytes(_ data: Data, _ to: UnsafeMutablePointer<UInt8>, _ count: Int) { data.copyBytes(to: to, count: count) }
