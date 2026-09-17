// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Inout requirement write-back through reverse dispatch
//
// When a C# class conforms to a protocol whose requirement takes an `inout` parameter, the
// generated Swift conformance hands the C# implementation a copy of the value and assigns the copy
// back once the call returns. Whatever the implementation stores into its `ref` parameter must
// reach that copy, for every lowering an `inout` value can take: a trivial scalar, a narrow enum
// discriminator, a reference-counted string or collection, an Optional, and a class reference.
//
// The opposite direction matters as much: C# holding an existential over a Swift conformer lends
// Swift its `ref` storage, and the mutation Swift makes there must come back, including when the
// requirement also returns a value or throws after mutating.

/// Payload-less enum whose discriminator is one byte wide, narrower than its C# backing type.
public enum InOutSignal {
    case idle
    case armed
    case fired
}

/// Class instance swapped through an `inout` reference.
public final class InOutToken {
    public let id: Int
    public init(id: Int) { self.id = id }
}

/// One requirement per `inout` lowering.
public protocol InOutWriteBackMatrix: AnyObject {
    func bumpCount(_ value: inout Int)
    func appendSuffix(_ value: inout String)
    func fillOptionalName(_ value: inout String?)
    func extendList(_ values: inout [Int])
    func advanceSignal(_ signal: inout InOutSignal)
    func swapToken(_ token: inout InOutToken)
    func clearToken(_ token: inout InOutToken?)
    func tagScores(_ scores: inout [String: Int])
}

/// Calls every requirement on a Swift-owned value and reports what each caller observed afterwards.
public func driveInOutWriteBackMatrix(_ matrix: any InOutWriteBackMatrix) -> String {
    var count = 41
    matrix.bumpCount(&count)

    var text = "swift"
    matrix.appendSuffix(&text)

    var name: String? = nil
    matrix.fillOptionalName(&name)

    var list = [1, 2]
    matrix.extendList(&list)

    var signal = InOutSignal.idle
    matrix.advanceSignal(&signal)

    var token = InOutToken(id: 1)
    matrix.swapToken(&token)

    var maybeToken: InOutToken? = InOutToken(id: 2)
    matrix.clearToken(&maybeToken)

    var scores = ["a": 1]
    matrix.tagScores(&scores)

    let scoreText = scores.sorted { $0.key < $1.key }.map { "\($0.key)=\($0.value)" }.joined(separator: ",")
    let listText = list.map(String.init).joined(separator: ",")
    let tokenText = maybeToken.map { String($0.id) } ?? "nil"
    return "\(count)|\(text)|\(name ?? "nil")|\(listText)|\(signal)|\(token.id)|\(tokenText)|\(scoreText)"
}

/// Drives an `inout` Int requirement beside a by-value sibling through a (possibly C#) conformer.
public func driveCursorAdvancer(_ advancer: any CursorAdvancer, start: Int, step: Int) -> Int {
    var cursor = start
    advancer.advance(&cursor, by: step)
    return cursor
}

/// Swift conformer reached from C# only through the existential, never as a concrete class.
final class SwiftInOutWriteBackMatrix: InOutWriteBackMatrix {
    func bumpCount(_ value: inout Int) { value += 1 }
    func appendSuffix(_ value: inout String) { value += "?" }
    func fillOptionalName(_ value: inout String?) { if value == nil { value = "swift-named" } }
    func extendList(_ values: inout [Int]) { values.append(9) }
    func advanceSignal(_ signal: inout InOutSignal) { signal = .fired }
    func swapToken(_ token: inout InOutToken) { token = InOutToken(id: token.id + 100) }
    func clearToken(_ token: inout InOutToken?) { token = nil }
    func tagScores(_ scores: inout [String: Int]) { scores["swift"] = 5 }
}

public func makeSwiftInOutWriteBackMatrix() -> any InOutWriteBackMatrix {
    SwiftInOutWriteBackMatrix()
}

public struct InOutCounterFailure: Error {
    public init() {}
}

/// `inout` beside a return value and a throw: the mutation happens before either exit.
public protocol InOutFallibleCounter: AnyObject {
    func bump(_ value: inout Int, by step: Int, failing: Bool) throws -> Int
    func rename(_ name: inout String, failing: Bool) throws -> Bool
}

final class SwiftInOutFallibleCounter: InOutFallibleCounter {
    func bump(_ value: inout Int, by step: Int, failing: Bool) throws -> Int {
        value += step
        if failing { throw InOutCounterFailure() }
        return value * 2
    }

    func rename(_ name: inout String, failing: Bool) throws -> Bool {
        name = name.uppercased()
        if failing { throw InOutCounterFailure() }
        return true
    }
}

public func makeSwiftInOutFallibleCounter() -> any InOutFallibleCounter {
    SwiftInOutFallibleCounter()
}

/// Swift conformers of the neighbouring inout protocols, reached only through the existential.
public func makeSwiftCursorAdvancer() -> any CursorAdvancer {
    SteppingAdvancer()
}

public func makeSwiftPointMutator(dx: Double, dy: Double) -> any PointMutator {
    OriginShifter(dx: dx, dy: dy)
}
