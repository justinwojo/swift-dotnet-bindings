// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Swift argument labels and enum-case payload labels that spell a C# reserved
// keyword. The emitter escapes those into verbatim identifiers (`object` ->
// `@object`), which is valid wherever the name itself is written. The hazard is
// the scratch locals the marshalling paths derive from that name: `@` is only
// legal as the FIRST character of an identifier, so a temp built by prepending
// onto the escaped spelling (`_@object_raw`, `__@object_meta`) does not parse at
// all. The generated file then fails to compile as a whole, which withdraws every
// member of the binding rather than just the one that could not be marshalled.
//
// The shapes below drive each family of derived local:
//   * a multi-payload enum case, which is the only enum shape that names its
//     scratch locals after the payload LABEL (a single payload marshals under the
//     emitter's own `value`), including a bare `Any` payload on the zero-witness
//     existential path, a container payload, and a Date payload;
//   * keyword-named function parameters that take the string / container
//     conversion paths;
//   * a keyword-named subscript parameter;
//   * the interaction with the marshalling-base-name machinery: a keyword-named
//     parameter whose sibling is spelled exactly like one of its own derived
//     locals, so the name is BOTH escaped and moved aside. De-escaping and
//     collision avoidance have to compose, not replace each other.

import Foundation

// MARK: - Enum payload labels

/// Multi-payload cases whose labels are C# keywords. `object: Any` is the shape
/// that reaches the zero-witness existential extraction; the others cover the
/// container, Date and plain-value payload paths off the same tuple walk.
public enum KeywordLabelPayload {
    case failed(object: Any, reason: String)
    case listed(params: [String], checked: Bool)
    case stamped(event: String, base: Date)
    case empty
}

public func makeKeywordLabelFailed(reason: String, value: Int32) -> KeywordLabelPayload {
    .failed(object: value, reason: reason)
}

public func makeKeywordLabelListed(items: [String], flag: Bool) -> KeywordLabelPayload {
    .listed(params: items, checked: flag)
}

public func makeKeywordLabelStamped(name: String, secondsSince1970: Double) -> KeywordLabelPayload {
    .stamped(event: name, base: Date(timeIntervalSince1970: secondsSince1970))
}

/// Round-trips a case built on the C# side back through Swift, so the payload is
/// checked by the language that owns the layout rather than by the binding alone.
public func describeKeywordLabelPayload(_ payload: KeywordLabelPayload) -> String {
    switch payload {
    case let .failed(object, reason):
        return "failed(\(object)|\(reason))"
    case let .listed(params, checked):
        return "listed(\(params.joined(separator: ","))|\(checked))"
    case let .stamped(event, base):
        return "stamped(\(event)|\(Int(base.timeIntervalSince1970)))"
    case .empty:
        return "empty"
    }
}

// MARK: - Member parameter labels

public struct KeywordLabelCarrier {
    public var tag: Int32

    public init(tag: Int32) {
        self.tag = tag
    }

    /// Keyword-named parameters on the string conversion path.
    public func describe(object: String, event: Int32) -> String {
        return "\(object)#\(event)#\(tag)"
    }

    /// Keyword-named parameter carrying a container, which spawns a buffer local.
    public func combine(string: String, params: [String]) -> String {
        return "\(string)[\(params.joined(separator: ","))]"
    }

    /// Both mechanisms at once: `object` must be de-escaped to name its scratch
    /// locals, AND moved aside because `objectBuffer` — a real sibling parameter —
    /// is spelled exactly like the container buffer local `object` would spawn.
    /// Stripping alone would redeclare the sibling; moving aside alone would still
    /// leave a verbatim prefix in the middle of the scratch local's name.
    public func pack(object: [String], objectBuffer: Int32) -> String {
        return "\(object.joined(separator: "+"))@\(objectBuffer)"
    }

    /// The same interaction one suffix over: `objectSwift` is the string
    /// conversion local's spelling.
    public func merge(object: String, objectSwift: String) -> String {
        return "\(object)/\(objectSwift)"
    }

    /// Keyword-named subscript parameter.
    public subscript(object: String) -> Int32 {
        return Int32(object.count) + tag
    }
}

/// Free function, so the keyword label is also covered off the instance path.
public func keywordLabelJoin(string: String, params: [String]) -> String {
    return "\(string)!\(params.joined(separator: ","))"
}
