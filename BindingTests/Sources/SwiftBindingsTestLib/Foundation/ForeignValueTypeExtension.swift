// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Foreign extension on a Swift value type in an ObjC framework module
//
// `Foundation.AttributeScopes` is a caseless Swift enum, not an ObjC class, even though
// Foundation is an ObjC framework module. A module that extends it (the shape an Apple
// framework uses to publish its attributed-string scope: a nested scope type plus an
// instance property naming it) must not take ForeignTypeExtensionEmitter's class-receiver
// path, whose wrapper reads `self` through `Unmanaged<AttributeScopes>` and does not compile
// ("'Unmanaged' requires that 'AttributeScopes' be a class type"). The receiver's declared
// kind in the ABI dump decides; the member is then left unbound, and the module still
// compiles. Compile gate only: there is no instance of a caseless enum to call it on.

@available(iOS 15, macOS 12, tvOS 15, macCatalyst 15, *)
extension AttributeScopes {
    public enum SBProbeAttributes {
        case marked
    }

    public var sbProbeDepth: Int { 1 }
}
