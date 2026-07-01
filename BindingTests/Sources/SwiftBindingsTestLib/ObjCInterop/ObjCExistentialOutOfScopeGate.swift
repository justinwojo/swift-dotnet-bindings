// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Durable fail-closed regression gate for `@objc` protocol existentials in out-of-scope
// positions. An `@objc` protocol's existential has a single 8-byte ObjC object-pointer ABI
// (no witness table). Only bare `any P` / `Optional<any P>` in a synchronous
// param/return/property position marshal correctly — those live in ObjCClassBoundExistential.swift
// and are runtime-exercised. The shapes below wrap that existential in a container/tuple/closure
// or an async return, where routing it through the ExistentialContainer1 carrier would mis-emit
// or crash. The generator MUST drop them (fail-closed), so the compile gate stays green with NO
// C# member to invoke. This fixture exists so a regression that lets any of these shapes emit
// again surfaces as a compile-gate failure (e.g. CS0030) instead of a runtime crash in a
// consumer. Uses the `ObjCClassBoundShape` @objc protocol from ObjCClassBoundExistential.swift.

import Foundation

// Container element positions — Array / Optional-Array / Dictionary value.
public func outOfScopeArrayObjC(_ xs: [any ObjCClassBoundShape]) -> [any ObjCClassBoundShape] { xs }
public func outOfScopeDictionaryObjC(_ d: [String: any ObjCClassBoundShape]) -> [String: any ObjCClassBoundShape] { d }

// Tuple return position.
public func outOfScopeTupleReturnObjC() -> (any ObjCClassBoundShape, Int32) { (ObjCShapeThing(tag: 1), 1) }

// Closure parameter / return positions.
public func outOfScopeClosureParamObjC(_ f: @escaping ((any ObjCClassBoundShape)?) -> Void) { }

// Async return position — even the bare existential is unsupported here.
public func outOfScopeAsyncReturnObjC() async -> any ObjCClassBoundShape { ObjCShapeThing(tag: 1) }

// Container property position.
public class ObjCExistentialOutOfScopeGateBox {
    public var arrayProp: [any ObjCClassBoundShape] = []
    public var dictProp: [String: any ObjCClassBoundShape] = [:]
}
