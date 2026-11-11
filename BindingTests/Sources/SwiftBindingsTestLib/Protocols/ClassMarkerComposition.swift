// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// A class composed only with marker protocols (`any SomeClass & Sendable`) is a
// one-word class existential: Swift passes the object reference alone, exactly as
// it passes a plain `SomeClass` parameter, because a marker protocol carries no
// witness table. The shape appears as the main entry point of session-style APIs
// that take an asset object (`init(asset: any SomeAsset & Sendable) async`), in
// both the pure-Swift-class and the NSObject-subclass flavour, so both are covered
// here, in parameter, async-initializer, return, optional, array, property and subscript
// positions, plus a specialized generic class participant.
//
// A class composed with a protocol that has a witness table (`any SomeClass &
// SomeProtocol`) has a two-word layout (object + witness table) and is a different
// shape; it is deliberately not fixtured here.

import Foundation

/// Open pure-Swift base class for the composition.
open class MarkerCompositionAsset: @unchecked Sendable {
    public let assetID: Int

    public init(assetID: Int) {
        self.assetID = assetID
    }

    open func describe() -> String {
        return "asset #\(assetID)"
    }
}

/// Subclass passed through the base-class composition, so dynamic dispatch on the
/// Swift side proves the callee received the real object.
public final class MarkerCompositionClip: MarkerCompositionAsset, @unchecked Sendable {
    public let clipName: String

    public init(assetID: Int, clipName: String) {
        self.clipName = clipName
        super.init(assetID: assetID)
    }

    public override func describe() -> String {
        return "clip \(clipName) #\(assetID)"
    }
}

/// NSObject subclass for the Objective-C-rooted flavour of the composition.
open class MarkerCompositionMedia: NSObject, @unchecked Sendable {
    public let title: String

    public init(title: String) {
        self.title = title
        super.init()
    }
}

/// Session-style API whose entry points take the composition.
public final class MarkerCompositionSession {
    public let summary: String

    public init(asset: any MarkerCompositionAsset & Sendable) {
        summary = asset.describe()
    }

    public init(media: any MarkerCompositionMedia & Sendable) {
        summary = "media \(media.title)"
    }

    public init(asyncAsset: any MarkerCompositionAsset & Sendable) async {
        await Task.yield()
        summary = "async " + asyncAsset.describe()
    }

    public func describe(_ asset: any MarkerCompositionAsset & Sendable) -> String {
        return asset.describe()
    }

    public func echo(_ asset: any MarkerCompositionAsset & Sendable) -> any MarkerCompositionAsset & Sendable {
        return asset
    }

    public func firstTitle(_ items: [any MarkerCompositionMedia & Sendable]) -> String {
        return items.first?.title ?? "<none>"
    }

    public func optionalDescribe(_ asset: (any MarkerCompositionAsset & Sendable)?) -> String {
        return asset?.describe() ?? "<nil>"
    }

    public func isSameObject(_ media: any MarkerCompositionMedia & Sendable, _ other: MarkerCompositionMedia) -> Bool {
        return media === other
    }
}

/// Stored, static and subscript members of the composition type. Their getters and setters
/// are signatures of their own, which must project the same way the member does.
public final class MarkerCompositionSlot {
    public var current: any MarkerCompositionAsset & Sendable
    public static var fallback: any MarkerCompositionAsset & Sendable = MarkerCompositionAsset(assetID: 0)

    public init(current: any MarkerCompositionAsset & Sendable) {
        self.current = current
    }

    public var currentDescription: String {
        return current.describe()
    }

    public subscript(slot: Int) -> any MarkerCompositionAsset & Sendable {
        get { slot == 0 ? current : MarkerCompositionSlot.fallback }
        set { if slot == 0 { current = newValue } else { MarkerCompositionSlot.fallback = newValue } }
    }
}

/// Generic class participant: a marker erases a specialized generic class exactly as it
/// erases a plain one.
open class MarkerCompositionBox<Value>: @unchecked Sendable {
    public let value: Value

    public init(value: Value) {
        self.value = value
    }
}

public func markerCompositionBoxValue(_ box: any MarkerCompositionBox<Int32> & Sendable) -> Int32 {
    return box.value
}

public func describeMarkerCompositionAsset(_ asset: any MarkerCompositionAsset & Sendable) -> String {
    return asset.describe()
}
