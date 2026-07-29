// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// SwiftUI Views whose initializer takes an ObjC-ROOTED Swift class.
//
// An NSObject-derived Swift class is emitted by the generator itself, so it is neither
// ObjC-bridged nor native-remapped — but it still exposes its native pointer as a bare `.Handle`
// inherited from NSObject, and carries no ISwiftObject `.Payload` SafeHandle. A bridge-init
// analyzer that decides "does this parameter use `.Handle`?" from the bridged/bridgeable flags
// alone answers FALSE for it and emits `.Payload`, a member the projected type does not have, so
// the generated bridge fails to compile. Every other class-typed View parameter in the corpus is
// a plain (non-NSObject) Swift class, whose `.Payload` access is correct — which is why this
// half of the question was uncovered.

// SwiftUI types (View, Text, etc.) are not accessible in the Mac Catalyst
// compiler environment despite the module importing successfully.
#if !targetEnvironment(macCatalyst)
import SwiftUI

/// View whose init parameter is an ObjC-rooted (NSObject-derived) Swift class.
public struct ObjCRootedModelView: View {
    public let item: LabeledItem

    public init(item: LabeledItem) {
        self.item = item
    }

    public var body: some View {
        Text("ObjCRootedParam: \(item.displayName)")
    }
}

/// Mixed shape: an ObjC-rooted class alongside a plain scalar, so the analyzer must classify
/// per-parameter rather than per-initializer.
public struct ObjCRootedModelWithScalarView: View {
    public let item: SimpleNSObject
    public let repeatCount: Int32

    public init(item: SimpleNSObject, repeatCount: Int32) {
        self.item = item
        self.repeatCount = repeatCount
    }

    public var body: some View {
        Text("ObjCRootedScalar: \(item.describe()) x\(repeatCount)")
    }
}

#endif
