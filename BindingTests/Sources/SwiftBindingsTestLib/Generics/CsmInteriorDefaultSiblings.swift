// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Generic methods over a protocol with an associated type, whose defaulted parameter sits BEFORE
// a required one, beside a `@_disfavoredOverload` sibling that differs only in that interior
// parameter's label and type. Each closes over the protocol's class conformers.
//
// The specialized primary omits every defaulted parameter, interior ones included, so it is the
// `(name, module)` call. The shorter overloads built on it (Swift fills the omitted defaults) must
// never re-declare that signature. One of them omits every default, which IS the primary's call.
// Seeding the duplicate check with the trailing trim `(name, options, module)` instead let that
// all-defaults overload through as a CS0111 duplicate, and declined the genuinely new
// `(name, options, module)` form.
//
// That form is the only way to pass `options`. For `create` the trailing default (`nil`) has a C#
// constant, but the specialized primary declares no inline default tail, so the form must be built
// all the same. For `register` the trailing default (`[]`) has no C# constant.

import Foundation

public protocol GridLayoutModule {
    associatedtype Definition
    var moduleTag: Int32 { get }
    func makeDefinition() -> Definition
}

public final class ColumnGridDefinition {
    public private(set) var columnCount: Int32 = 0
    public init() {}
    public func addColumn() { columnCount += 1 }
}

public final class RowGridDefinition {
    public private(set) var rowCount: Int32 = 0
    public init() {}
    public func addRow() { rowCount += 1 }
}

public final class ColumnGridModule: GridLayoutModule {
    public init() {}
    public var moduleTag: Int32 { 1 }
    public func makeDefinition() -> ColumnGridDefinition { ColumnGridDefinition() }
}

public final class RowGridModule: GridLayoutModule {
    public init() {}
    public var moduleTag: Int32 { 2 }
    public func makeDefinition() -> RowGridDefinition { RowGridDefinition() }
}

public struct GridLayoutOptions: OptionSet {
    public let rawValue: Int32
    public init(rawValue: Int32) { self.rawValue = rawValue }
    public static let temporary = GridLayoutOptions(rawValue: 1)
    public static let strict = GridLayoutOptions(rawValue: 2)
}

public final class GridLayoutRegistry {
    /// Which declaration ran last: 1 = `create(grid:options:using:_:)`,
    /// 2 = `create(grid:ifNotExists:using:_:)`, 3 = `register(grid:options:using:aliases:)`.
    public private(set) var lastOverload: Int32 = 0
    public private(set) var lastName: String = ""
    public private(set) var lastOptionsRaw: Int32 = -1
    public private(set) var lastIfNotExists: Bool = false
    public private(set) var lastModuleTag: Int32 = 0
    public private(set) var lastAliasCount: Int32 = -1
    public private(set) var lastBodyRan: Bool = false

    public init() {}

    public func create<Module: GridLayoutModule>(
        grid name: String,
        options: GridLayoutOptions = [],
        using module: Module,
        _ body: ((Module.Definition) throws -> Void)? = nil
    ) throws {
        record(1, name, module)
        lastOptionsRaw = options.rawValue
        if let body {
            try body(module.makeDefinition())
            lastBodyRan = true
        }
    }

    @_disfavoredOverload
    public func create<Module: GridLayoutModule>(
        grid name: String,
        ifNotExists: Bool = false,
        using module: Module,
        _ body: ((Module.Definition) throws -> Void)? = nil
    ) throws {
        record(2, name, module)
        lastIfNotExists = ifNotExists
        if let body {
            try body(module.makeDefinition())
            lastBodyRan = true
        }
    }

    public func register<Module: GridLayoutModule>(
        grid name: String,
        options: GridLayoutOptions = [],
        using module: Module,
        aliases: Set<Int> = []
    ) {
        record(3, name, module)
        lastOptionsRaw = options.rawValue
        lastAliasCount = Int32(aliases.count)
    }

    private func record<Module: GridLayoutModule>(_ overload: Int32, _ name: String, _ module: Module) {
        lastOverload = overload
        lastName = name
        lastModuleTag = module.moduleTag
        lastOptionsRaw = -1
        lastIfNotExists = false
        lastAliasCount = -1
        lastBodyRan = false
    }
}
