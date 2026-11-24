// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Collections whose elements or values are object references
//
// An array element or dictionary value of an Objective-C class (NSUUID), of a Swift class rooted
// in NSObject, of a pure Swift class or of a string-backed Objective-C key type
// (NSAttributedString.Key) is one object reference. A collection C# builds is Swift's once it
// crosses: Swift may keep it past the call, mutate it, or hand it to Objective-C as an NSArray,
// so its storage must be the element type's own, holding a reference on every element, as a
// Swift-built one does. A collection Swift builds may be backed by an Objective-C collection
// (an NSArray bridged lazily), which C# must read as the element type's array, not as an array
// of integers.

/// A Swift class rooted in NSObject.
public final class ObjectElementTracked: NSObject {
    public let tag: Int

    public init(tag: Int) {
        self.tag = tag
        super.init()
    }
}

/// A pure Swift class.
public final class ObjectElementRef {
    public let tag: Int

    public init(tag: Int) { self.tag = tag }
}

/// Keeps the collections it is handed, and reads them back later.
public final class ObjectElementStore {
    private var list: [NSUUID] = []
    private var values: [String: NSUUID] = [:]
    private var maybeList: [NSUUID]? = nil
    private var nested: [[NSUUID]] = []
    private var tracked: [ObjectElementTracked] = []
    private var refs: [ObjectElementRef] = []
    private var keys: [NSAttributedString.Key] = []

    public init() {}

    public func keep(_ list: [NSUUID]) { self.list = list }
    public func keepValues(_ values: [String: NSUUID]) { self.values = values }
    public func keepOptional(_ list: [NSUUID]?) { maybeList = list }
    public func keepNested(_ lists: [[NSUUID]]) { nested = lists }
    public func keepTracked(_ list: [ObjectElementTracked]) { tracked = list }
    public func keepRefs(_ list: [ObjectElementRef]) { refs = list }
    public func keepKeys(_ list: [NSAttributedString.Key]) { keys = list }

    /// The kept list, as a property C# can set.
    public var items: [NSUUID] {
        get { list }
        set { list = newValue }
    }

    /// The kept optional list, as a property C# can set.
    public var maybeItems: [NSUUID]? {
        get { maybeList }
        set { maybeList = newValue }
    }

    /// The kept UUIDs in order: reads every element, so one whose object was freed after C# let
    /// go of it shows up here.
    public func keptStrings() -> String { list.map { $0.uuidString }.joined(separator: ",") }

    /// The kept dictionary as `key=uuid` pairs, sorted by key.
    public func keptValueStrings() -> String {
        values.sorted { $0.key < $1.key }.map { "\($0.key)=\($0.value.uuidString)" }.joined(separator: ",")
    }

    /// The kept optional list, or `nil`.
    public func keptOptionalStrings() -> String {
        guard let maybeList else { return "nil" }
        return maybeList.map { $0.uuidString }.joined(separator: ",")
    }

    /// The kept nested lists, `|` between lists.
    public func keptNestedStrings() -> String {
        nested.map { $0.map { $0.uuidString }.joined(separator: ",") }.joined(separator: "|")
    }

    public func keptTrackedTags() -> String { tracked.map { String($0.tag) }.joined(separator: ",") }
    public func keptRefTags() -> String { refs.map { String($0.tag) }.joined(separator: ",") }
    public func keptKeyNames() -> String { keys.map { $0.rawValue }.joined(separator: ",") }

    /// The kept list as Objective-C sees it: bridged to an NSArray and read through it.
    public func keptThroughNSArray() -> String {
        let array = list as NSArray
        return array.compactMap { ($0 as? NSUUID)?.uuidString }.joined(separator: ",")
    }

    /// Swift replaces the first kept element; the storage releases the one it held.
    public func replaceFirst(with uuid: NSUUID) { list[0] = uuid }

    public func clear() {
        list = []
        values = [:]
        maybeList = nil
        nested = []
        tracked = []
        refs = []
        keys = []
    }
}

/// The retain count of an object C# hands over. Only differences between two reads mean
/// anything: the call itself may hold references of its own.
public func objectElementRetainCount(_ object: NSUUID) -> Int {
    return CFGetRetainCount(object)
}

/// The retain count of an NSObject-rooted Swift object C# hands over.
public func objectElementTrackedRetainCount(_ object: ObjectElementTracked) -> Int {
    return CFGetRetainCount(object)
}

/// The retain count of a string C# hands over, the storage of an `NSAttributedString.Key`.
public func objectElementStringRetainCount(_ object: NSString) -> Int {
    return CFGetRetainCount(object)
}

/// Hands a list C# built to Objective-C, within the call.
public func objectElementThroughNSArray(_ list: [NSUUID]) -> String {
    return (list as NSArray).compactMap { ($0 as? NSUUID)?.uuidString }.joined(separator: ",")
}

/// A list Swift builds natively.
public func objectElementMakeList(_ uuids: [String]) -> [NSUUID] {
    return uuids.compactMap { NSUUID(uuidString: $0) }
}

/// A list backed by an NSArray, as one an Objective-C API returns: bridged lazily, its storage the
/// NSArray itself.
public func objectElementMakeBridgedList(_ uuids: [String]) -> [NSUUID] {
    let array = NSArray(array: uuids.compactMap { NSUUID(uuidString: $0) })
    return array as! [NSUUID]
}

/// A dictionary backed by an NSDictionary, bridged lazily.
public func objectElementMakeBridgedValues(_ uuids: [String]) -> [NSUUID: NSUUID] {
    let dictionary = NSMutableDictionary()
    for text in uuids {
        if let uuid = NSUUID(uuidString: text) { dictionary[uuid] = uuid }
    }
    return dictionary as! [NSUUID: NSUUID]
}

/// A set backed by an NSSet, bridged lazily.
public func objectElementMakeBridgedSet(_ uuids: [String]) -> Set<NSUUID> {
    let set = NSSet(array: uuids.compactMap { NSUUID(uuidString: $0) })
    return set as! Set<NSUUID>
}

/// A dictionary of object values Swift builds natively.
public func objectElementMakeValues(_ uuids: [String]) -> [String: NSUUID] {
    var result: [String: NSUUID] = [:]
    for (index, text) in uuids.enumerated() {
        if let uuid = NSUUID(uuidString: text) { result["k\(index)"] = uuid }
    }
    return result
}

/// Hands C# a list Swift built, and returns what C# made of it.
public func objectElementCallWithList(_ uuids: [String], _ body: ([NSUUID]) -> String) -> String {
    return body(objectElementMakeList(uuids))
}
