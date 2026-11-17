// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - A subclass conforming through a property its base renamed in C#
//
// A Swift property can be emitted off its projected C# name: on an NSObject-rooted
// class `classHandle` becomes `ClassHandleValue` (NSObject owns `ClassHandle`), and a
// property named like its class becomes `<Name>Value` (CS0542). A subclass adopting a
// protocol whose requirement that inherited property witnesses lists the C# interface
// but inherited only the renamed member, so C# found no implementation (CS0738 for the
// NSObject name, CS0535 for the class-name one) and the whole module failed.
//
// The subclass now forwards the interface member to the inherited one. Each protocol
// also has a conformer that declares the property itself, so the requirement is not
// mistaken for an invisible Swift default; a variant with only the inheriting conformer
// proves the requirement still reaches the inherited property.

public protocol ClassHandleCarrying: AnyObject {
    var classHandle: Int32 { get }
}

open class ClassHandleBase: NSObject {
    public var classHandle: Int32
    public init(classHandle: Int32) {
        self.classHandle = classHandle
        super.init()
    }
}

public class ClassHandleChild: ClassHandleBase, ClassHandleCarrying {
    public override init(classHandle: Int32) { super.init(classHandle: classHandle) }
}

public final class ClassHandleOwnCarrier: ClassHandleCarrying {
    public let classHandle: Int32
    public init(classHandle: Int32) { self.classHandle = classHandle }
}

public func readClassHandleCarrying(_ value: ClassHandleCarrying) -> Int32 { value.classHandle }

public protocol RecordTagged: AnyObject {
    var recordTag: Int32 { get set }
}

open class RecordTag {
    public var recordTag: Int32
    public init(recordTag: Int32) { self.recordTag = recordTag }
}

public class RecordTagChild: RecordTag, RecordTagged {
    public override init(recordTag: Int32) { super.init(recordTag: recordTag) }
    /// Unrelated to the requirement, but its C# name is the one the inherited witness was renamed to,
    /// so the forwarding must reach the ancestor's member, not this one.
    public var recordTagValue: Int32 = 99
}

/// An intermediate class between the declaring base and the conformer, whose own member takes the
/// C# name the inherited witness was renamed to: a `base.` forward from the conformer would reach it.
open class RecordTagMiddle: RecordTag {
    public override init(recordTag: Int32) { super.init(recordTag: recordTag) }
    public var recordTagValue: Int32 = 77
}

public class RecordTagGrandchild: RecordTagMiddle, RecordTagged {
    public override init(recordTag: Int32) { super.init(recordTag: recordTag) }
}

public final class RecordTagOwnCarrier: RecordTagged {
    public var recordTag: Int32
    public init(recordTag: Int32) { self.recordTag = recordTag }
}

/// Reads the requirement, writes it back incremented, and returns what it read.
public func bumpRecordTagged(_ value: RecordTagged) -> Int32 {
    let read = value.recordTag
    value.recordTag = read + 1
    return read
}

/// Only an inheriting conformer: the requirement must not be read as a Swift default.
public protocol InheritedOnlyTagged: AnyObject {
    var inheritedOnlyTag: Int32 { get }
}

open class InheritedOnlyTag: NSObject {
    public var inheritedOnlyTag: Int32
    public init(inheritedOnlyTag: Int32) {
        self.inheritedOnlyTag = inheritedOnlyTag
        super.init()
    }
}

public class InheritedOnlyTagChild: InheritedOnlyTag, InheritedOnlyTagged {
    public override init(inheritedOnlyTag: Int32) { super.init(inheritedOnlyTag: inheritedOnlyTag) }
}

public func readInheritedOnlyTagged(_ value: InheritedOnlyTagged) -> Int32 { value.inheritedOnlyTag }

// MARK: - A subclass property on the C# name an ancestor's renamed property took
//
// `recordSize` on class `RecordSize` is emitted as `RecordSizeValue` (CS0542), with a
// `RecordSizeValueNative` companion for its native width. A final subclass's own `recordSizeValue`
// projects onto both names, so its members hide the ancestor's: without `new` they were CS0114 and
// CS0108, which fail a warnings-as-errors build of the binding. The two stay distinct members.

open class RecordSize {
    public var recordSize: Int
    public init(recordSize: Int) { self.recordSize = recordSize }
}

public final class RecordSizeChild: RecordSize {
    public override init(recordSize: Int) { super.init(recordSize: recordSize) }
    public var recordSizeValue: Int = 5
}

/// The ancestor's renamed property is an `Int32`, so it has no companion: the subclass's `Int` hides
/// the property, and its companion is a new member that must not claim to hide anything (CS0109).
open class RecordCount {
    public var recordCount: Int32
    public init(recordCount: Int32) { self.recordCount = recordCount }
}

public final class RecordCountChild: RecordCount {
    public override init(recordCount: Int32) { super.init(recordCount: recordCount) }
    public var recordCountValue: Int = 2
}
