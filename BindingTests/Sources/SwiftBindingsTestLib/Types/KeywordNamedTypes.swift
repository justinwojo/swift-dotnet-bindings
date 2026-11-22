// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Keyword-named public types
//
// Public Swift types, members and cases whose names are C# keywords (and, for some, Swift keywords
// spelled with backticks). The parser stores a C#-keyword type name with an underscore in front
// (`event` as `_event`), and the generator once read that stored name as Swift's
// underscore-prefixed implementation-detail convention and dropped every one of these types.

/// A struct named after a C#-only keyword, with a keyword-named property.
public struct event {
    public var count: Int
    public init(count: Int) { self.count = count }
    public func doubled() -> Int { count * 2 }
    public var `checked`: Bool { count > 0 }
}

/// A class named after a keyword of both languages, with a keyword-named method.
public final class `class` {
    public var value: Int
    public init(value: Int) { self.value = value }
    public func `default`(_ extra: Int) -> Int { value + extra }
    public func take(_ e: event) -> Int { e.count + value }
    public func make() -> event { event(count: value) }
}

/// A payload-free enum named after a keyword of both languages, with keyword-named cases.
public enum `default` {
    case by
    case `switch`
    case `event`

    public var code: Int {
        switch self {
        case .by: return 1
        case .switch: return 2
        case .event: return 3
        }
    }
}

/// A raw-value enum named after a C#-only keyword, with keyword-named cases.
public enum fixed: Int {
    case `readonly` = 4
    case `static` = 5
}

/// Hosts nested keyword-named types: one named after a C#-only keyword, one after a keyword of
/// both languages.
public struct KeywordTypeHost {
    public struct object {
        public var tag: Int
        public init(tag: Int) { self.tag = tag }
    }

    public struct `struct` {
        public var width: Int
        public init(width: Int) { self.width = width }
    }

    public init() {}
    public func wrap(_ tag: Int) -> object { object(tag: tag) }
    public func shape(_ width: Int) -> `struct` { `struct`(width: width) }
    public func area(_ shape: `struct`) -> Int { shape.width * shape.width }
}

/// A protocol named after a C#-only keyword, conformed to in Swift and implementable from C#.
public protocol readonly {
    func level() -> Int
}

public struct KeywordTypeConformer: readonly {
    public init() {}
    public func level() -> Int { 7 }
}

public func keywordTypeMake(_ count: Int) -> event { event(count: count) }
public func keywordTypeCode(_ value: `default`) -> Int { value.code }
public func keywordTypeRaw(_ value: fixed) -> Int { value.rawValue }
public func keywordTypeLevel(_ value: any readonly) -> Int { value.level() }
public func keywordTypeHostedTag(_ value: KeywordTypeHost.object) -> Int { value.tag }
