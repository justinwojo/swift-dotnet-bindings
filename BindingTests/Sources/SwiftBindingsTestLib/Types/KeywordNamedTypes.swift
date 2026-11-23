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

/// A raw-value enum named after a C#-only keyword, with keyword-named cases. The raw values are
/// the implicit ordinals: a module interface does not print explicit integer raw values, so the
/// binding carries ordinals for a Swift integer enum whatever its declared values.
public enum fixed: Int {
    case `readonly`
    case `static`
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

// MARK: - Members named like their keyword-named type
//
// A keyword-named type is declared in C# under its PascalCase Swift spelling (`checked` →
// `Checked`), but the collision rules once compared its members against the stored name
// (`_checked`). A member that really collides with the type, or with a nested type, was then
// emitted unrenamed, failed to compile (CS0542, CS0102) and was withdrawn.

/// A property named like its type: renamed `CheckedValue`, as for any other type.
public struct `checked` {
    public var `checked`: Int
    public init(checked: Int) { self.checked = checked }
}

/// A nested keyword-named type beside a property of the same name: the property is renamed.
public struct KeywordSiblingHost {
    public struct `goto` {
        public var hops: Int
        public init(hops: Int) { self.hops = hops }
    }

    public var `goto`: Int
    public init(goto: Int) { self.goto = goto }
    public func jump(_ hops: Int) -> KeywordSiblingHost.`goto` { KeywordSiblingHost.`goto`(hops: hops + self.goto) }
}

/// A payload enum (a C# class) with a payload case named like the enum.
public enum implicit {
    case implicit(Int)
    case none

    public var code: Int {
        switch self {
        case .implicit(let v): return v
        case .none: return 0
        }
    }
}

/// A payload enum with a payload-free case named like the enum (a static property in C#).
public enum stackalloc {
    case stackalloc
    case sized(Int)

    public var size: Int {
        switch self {
        case .stackalloc: return 0
        case .sized(let n): return n
        }
    }
}

/// A simple enum (a C# enum, whose members may share its name) with methods taking and returning
/// the enum itself.
public enum unchecked {
    case unchecked
    case other

    public var code: Int { self == .unchecked ? 1 : 2 }
    public func flipped() -> unchecked { self == .unchecked ? .other : .unchecked }
    public func same(_ other: unchecked) -> Bool { self == other }
}

/// A caseless keyword-named enum holding static members (a static class in C#).
public enum `sealed` {
    public static func value() -> Int { 42 }
}

/// A caseless keyword-named enum holding nothing (an empty C# enum).
public enum `extern` {}

/// A method named like its keyword-named type keeps its keyword spelling `_explicit`, which
/// cannot collide with the type `Explicit`, so it takes no `Get` prefix.
public final class explicit {
    public init() {}
    public func explicit(_ x: Int) -> Int { x * 3 }
}

/// A generic keyword-named type.
public struct `foreach`<T> {
    public var count: Int
    public init(count: Int) { self.count = count }
}

public func keywordGenericMake(_ count: Int) -> `foreach`<Int32> { `foreach`(count: count) }
public func keywordGenericCount(_ value: `foreach`<Int32>) -> Int { value.count }

/// A lowercase type that is not a keyword is declared `Vec`; its same-named property and method
/// are renamed as for any other type.
public struct vec {
    public var vec: Int
    public init(vec: Int) { self.vec = vec }
    public func vec(scaled by: Int) -> Int { vec * by }
}

// MARK: - A keyword-named type nested in a generic
//
// `KeywordNest<Int32>.class` must reach the wrapper with the keyword escaped after the generic
// argument list (`KeywordNest<Swift.Int32>.`class``), in every position: plain, optional, array and
// a stored property.

public struct KeywordNest<T> {
    public struct `class` {
        public var v: Int
        public init(v: Int) { self.v = v }
    }
}

public func keywordNestMake(_ v: Int) -> KeywordNest<Int32>.`class` { .init(v: v) }
public func keywordNestRead(_ c: KeywordNest<Int32>.`class`) -> Int { c.v }
public func keywordNestOptional(_ c: KeywordNest<Int32>.`class`?) -> Int { c?.v ?? -1 }
public func keywordNestCount(_ c: [KeywordNest<Int32>.`class`]) -> Int { c.reduce(0) { $0 + $1.v } }
public func keywordNestMaybe(_ v: Int) -> KeywordNest<Int32>.`class`? { v < 0 ? nil : .init(v: v) }

public struct KeywordNestHolder {
    public var item: KeywordNest<Int32>.`class`
    public init(item: KeywordNest<Int32>.`class`) { self.item = item }
}
