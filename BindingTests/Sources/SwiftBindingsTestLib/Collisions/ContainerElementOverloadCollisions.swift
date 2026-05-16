// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Container element-type overload disambiguation (N-1)
//
// Two methods that differ only in the generic element type of an Array
// parameter project to distinct C# signatures (`IEnumerable<A>` vs
// `IEnumerable<B>`), so both should emit as natural C# overloads. Pre-fix,
// the generator's primary dedup key resolved `Array<T>` to the bare container
// name `Swift.SwiftArray` regardless of element type, collapsing both
// overloads onto the same key and silently dropping the second. The
// `startPrefetching(with: [URL]) / startPrefetching(with: [ImageRequest])`
// pair on `Nuke.ImagePrefetcher` is the consumer-visible repro.

public class FetchItemA {
    public let label: String
    public init(label: String) { self.label = label }
}

public class FetchItemB {
    public let priority: Int32
    public init(priority: Int32) { self.priority = priority }
}

/// Two overloads of `enqueue(_:)` that differ only in the element type of
/// their `Array` parameter. Both must emit; the C# names should be
/// `Enqueue(IEnumerable<FetchItemA>)` and `Enqueue(IEnumerable<FetchItemB>)`.
public class ItemEnqueuer {
    public private(set) var lastTag: String = ""
    public private(set) var lastCount: Int32 = 0

    public init() {}

    public func enqueue(_ items: [FetchItemA]) {
        lastTag = "A"
        lastCount = Int32(items.count)
    }

    public func enqueue(_ items: [FetchItemB]) {
        lastTag = "B"
        lastCount = Int32(items.count)
    }
}

/// Mirror of the Nuke `startPrefetching([URL]) / startPrefetching([ImageRequest])`
/// shape — one overload takes an ObjC-bridgeable element (`URL` → `NSUrl`),
/// the other a custom Swift class element. The ObjC-bridge container is the
/// shape called out in the N-1 evidence cite; both should emit cleanly as
/// `Prefetch(IEnumerable<NSUrl>)` and `Prefetch(IEnumerable<FetchItemA>)`.
public class UrlPrefetcher {
    public private(set) var lastSource: String = ""
    public private(set) var lastCount: Int32 = 0

    public init() {}

    public func prefetch(_ urls: [URL]) {
        lastSource = "url"
        lastCount = Int32(urls.count)
    }

    public func prefetch(_ items: [FetchItemA]) {
        lastSource = "item"
        lastCount = Int32(items.count)
    }
}

// Free-function variant exercises the ModuleHandler primary-dedup path.
public func enqueueItems(_ items: [FetchItemA]) -> String {
    return "A:\(items.count)"
}

public func enqueueItems(_ items: [FetchItemB]) -> String {
    return "B:\(items.count)"
}
