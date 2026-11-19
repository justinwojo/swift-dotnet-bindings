// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Two initializers whose trimmed default-parameter forms share an arity but not their types.
//
// `init(url:label:config:)` trims (dropping `config`, whose `.init()` default C# cannot spell)
// to a two-argument (URL, String?) form. `init(data:config:)` also takes two arguments, but
// (Data, config) is another C# signature: no call binds both. The overload emitter once declined
// every trimmed constructor that met a same-named sibling of equal arity, so the (URL, String?)
// form vanished whenever a type gained such a sibling, as an Apple framework's resource type did
// in a later SDK. Both trims must stay, and each must reach its own initializer.
public struct SameArityInitConfig {
    public var scale: Int
    public init(scale: Int = 3) { self.scale = scale }
}

public final class SameArityInitSiblings {
    public let source: String
    public let label: String
    public let scale: Int

    public init(url: URL, label: String? = nil, config: SameArityInitConfig = .init()) {
        self.source = "url:" + url.lastPathComponent
        self.label = label ?? "none"
        self.scale = config.scale
    }

    public init(data: Data, config: SameArityInitConfig = .init()) {
        self.source = "data:\(data.count)"
        self.label = "none"
        self.scale = config.scale
    }
}
