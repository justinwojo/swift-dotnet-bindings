// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Objects C# hands to Swift, or a closure C# implements hands back
//
// A reference C# passes reaches Swift as the object's bare pointer, and a reference a C# closure
// returns does too. Under a precise collector the C# wrapper that owns that object is unreachable
// once its pointer has been read, so a collection can release the object while Swift still uses it
// (an argument) or before Swift has taken its own reference (a closure result). Every function here
// gives a collector that window: it pauses, or calls back into C#, before it reads the object.

/// A Swift class whose live instances are counted, so a test can see one leak or be freed early.
public final class LivenessNode {
    nonisolated(unsafe) private static var liveCount = 0
    private static let lock = NSLock()

    public let name: String

    public init(name: String) {
        self.name = name
        LivenessNode.lock.lock(); LivenessNode.liveCount += 1; LivenessNode.lock.unlock()
    }

    deinit {
        LivenessNode.lock.lock(); LivenessNode.liveCount -= 1; LivenessNode.lock.unlock()
    }

    /// How many nodes are alive.
    public static var live: Int {
        lock.lock(); defer { lock.unlock() }
        return liveCount
    }
}

private func livenessPause() { usleep(1500) }

/// `object`'s description, read after a pause.
public func livenessDescribeObject(_ object: NSObject) -> String {
    livenessPause()
    return object.description
}

/// `object`'s description, or "nil", read after a pause.
public func livenessDescribeOptionalObject(_ object: NSObject?) -> String {
    livenessPause()
    return object?.description ?? "nil"
}

/// `node`'s name, read after a pause.
public func livenessNodeName(_ node: LivenessNode) -> String {
    livenessPause()
    return node.name
}

/// `node`'s name, or "nil", read after a pause.
public func livenessOptionalNodeName(_ node: LivenessNode?) -> String {
    livenessPause()
    return node?.name ?? "nil"
}

/// `url`'s last path component, read after a pause.
public func livenessURLName(_ url: URL) -> String {
    livenessPause()
    return url.lastPathComponent
}

/// `node`'s name, read after `between` runs.
public func livenessNodeNameAfter(_ node: LivenessNode, _ between: () -> Void) -> String {
    between()
    return node.name
}

/// `object`'s description, read after `between` runs.
public func livenessDescribeObjectAfter(_ object: NSObject, _ between: () -> Void) -> String {
    between()
    return object.description
}

/// Reads the objects C# hands it, after a pause, as methods, an initializer and a subscript.
public final class LivenessReader {
    public let first: String

    /// Reads `node`'s name after a pause.
    public init(node: LivenessNode) {
        livenessPause()
        first = node.name
    }

    /// `node`'s name and `object`'s description, read after a pause.
    public func read(_ node: LivenessNode, _ object: NSObject) -> String {
        livenessPause()
        return "\(node.name)|\(object.description)"
    }

    /// `node`'s name, read after a pause.
    public subscript(node: LivenessNode) -> String {
        livenessPause()
        return node.name
    }

    // MARK: Closures that return references
    //
    // `makeNodes` takes a non-escaping closure, whose result C# writes into Swift's result buffer;
    // the others take escaping ones, whose callbacks hand the result back.

    /// Asks `make` for nodes named `n0`…, calling `between` after each, and joins their names.
    public func makeNodes(_ count: Int, _ make: (String) -> LivenessNode, _ between: () -> Void) -> String {
        var nodes: [LivenessNode] = []
        for i in 0..<count {
            nodes.append(make("n\(i)"))
            between()
        }
        return nodes.map(\.name).joined(separator: ",")
    }

    /// `makeNodes` through an escaping closure.
    public func makeEscapingNodes(_ count: Int, _ make: @escaping (String) -> LivenessNode, _ between: () -> Void) -> String {
        var nodes: [LivenessNode] = []
        for i in 0..<count {
            nodes.append(make("n\(i)"))
            between()
        }
        return nodes.map(\.name).joined(separator: ",")
    }

    /// Asks `make` for optional nodes named `n0`…, calling `between` after each, and joins their
    /// names ("nil" for a nil).
    public func makeOptionalNodes(_ count: Int, _ make: @escaping (String) -> LivenessNode?, _ between: () -> Void) -> String {
        var nodes: [LivenessNode?] = []
        for i in 0..<count {
            nodes.append(make("n\(i)"))
            between()
        }
        return nodes.map { $0?.name ?? "nil" }.joined(separator: ",")
    }

    /// Asks `make` for objects named `n0`…, calling `between` after each, and joins their
    /// descriptions.
    public func makeObjects(_ count: Int, _ make: @escaping (String) -> NSObject, _ between: () -> Void) -> String {
        var objects: [NSObject] = []
        for i in 0..<count {
            objects.append(make("n\(i)"))
            between()
        }
        return objects.map(\.description).joined(separator: ",")
    }

    /// Asks `make` for optional objects named `n0`…, calling `between` after each, and joins their
    /// descriptions ("nil" for a nil).
    public func makeOptionalObjects(_ count: Int, _ make: @escaping (String) -> NSObject?, _ between: () -> Void) -> String {
        var objects: [NSObject?] = []
        for i in 0..<count {
            objects.append(make("n\(i)"))
            between()
        }
        return objects.map { $0?.description ?? "nil" }.joined(separator: ",")
    }
}
