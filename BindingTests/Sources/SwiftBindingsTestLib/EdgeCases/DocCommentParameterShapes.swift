// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Documented members whose C# declaration does not list the same parameters as the Swift one.
// CompileCheck builds the binding with a documentation file and warnings as errors, so a
// `<param>` tag naming a parameter the C# declaration lacks (CS1572), or a parameter left
// untagged beside a tagged one (CS1573), fails the compile gate here exactly as it fails a
// consumer who builds with `GenerateDocumentationFile`.
//
// The doc comments are the fixture: each one describes the Swift parameters, and the binding
// has to keep only the tags its own declaration can carry.

/// A class-bound protocol, so the generic members below take the generic-bridge path. Its
/// requirement has a name no other protocol in the module uses: one EveryProtocol proxy conforms
/// to every protocol at once, so a same-named requirement of another type would block it.
public protocol DocParamTagged: AnyObject {
    var docParamRank: Int { get }
}

public final class DocParamItem: DocParamTagged {
    public init() {}
    public var docParamRank: Int { 1 }
}

public final class DocParamHost {
    public init() {}

    /// Describes only the first of its two parameters.
    /// - Parameter dx: The horizontal offset.
    public func shift(dx: Int, dy: Int) -> Int { dx + dy }

    /// An async generic member whose trailing defaults are dropped one at a time by the
    /// trimmed overloads, so each overload carries fewer parameters than the comment names.
    /// - Parameters:
    ///   - item: The item.
    ///   - retries: How many retries.
    ///   - delays: The delays between retries.
    public func refresh<T: DocParamTagged>(_ item: T, retries: Int = 2, delays: Set<Int> = [1]) async throws -> Int {
        item.docParamRank + retries + delays.count
    }

    /// A sync generic member whose bridge leaves the defaulted parameter out.
    /// - Parameters:
    ///   - item: The item.
    ///   - bonus: The bonus.
    public func score<T: DocParamTagged>(_ item: T, bonus: Int = 1) -> Int { item.docParamRank + bonus }

    /// A closure member with a documented default.
    /// - Parameters:
    ///   - limit: The number of values.
    ///   - body: Receives the values.
    public func collect(limit: Int = 3, _ body: @escaping ([Int]) -> Void) { body(Array(0..<limit)) }

    /// Fails for a negative code. The comment has no `Returns:`, yet the projected factory
    /// adds an `out` result.
    /// - Parameter code: The code.
    public init?(code: Int) {
        if code < 0 { return nil }
    }

    /// Fails for a negative value. The Swift parameter is itself called `result`, so the
    /// factory's `out` parameter takes another name, and `Returns:` describes that one.
    /// - Parameter result: The value to check.
    /// - Returns: The host when the value is not negative.
    public init?(result: Int) {
        if result < 0 { return nil }
    }
}

public struct DocParamValue {
    public var seed: Int

    /// Fails for a negative seed. The comment has no `Returns:`.
    /// - Parameter seed: The seed.
    public init?(seed: Int) {
        if seed < 0 { return nil }
        self.seed = seed
    }
}
