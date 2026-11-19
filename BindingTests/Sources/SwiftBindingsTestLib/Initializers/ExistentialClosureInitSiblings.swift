// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Three initializers that share the label `entries:` behind a defaulted `version:`: one takes an
// array (and defaults it), one an existential of a module protocol, one a closure.
//
// The one-argument constructor trimmed from `init(version:entries:)` over the existential calls
// Swift as `init(entries: provider)`. The call pinner once counted the closure sibling as a rival
// for that call, though no conversion turns an existential into a function, and declined the
// constructor. It must be emitted and reach the existential initializer.
public protocol ProvidedCountSource {
    var providedCount: Int { get }
}

public struct FixedProvidedCountSource: ProvidedCountSource {
    public let providedCount: Int
    public init(providedCount: Int) { self.providedCount = providedCount }
}

public final class ExistentialClosureInitSiblings {
    public let source: String
    public let version: Int
    public let count: Int

    public init(version: Int = 1, entries: [Int] = []) {
        self.source = "array"
        self.version = version
        self.count = entries.count
    }

    public init(version: Int = 1, entries provider: any ProvidedCountSource) {
        self.source = "provider"
        self.version = version
        self.count = provider.providedCount
    }

    // `scale` keeps this initializer's C# signature apart from the existential one's; Swift still
    // offers it to the `init(entries:)` call, since every other parameter is defaulted.
    public init(version: Int = 1, scale: Int = 1, entries: @escaping () -> Int) {
        self.source = "closure"
        self.version = version
        self.count = entries() * scale
    }
}
