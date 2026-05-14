// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Reproduces the Kingfisher `ImageDownloader.isValidStatusCode(_:)` shape: a
// protocol with an extension default method, where the conforming type lets the
// extension default stand. ProtocolExtensionEmitter injects a synthetic
// MethodDecl onto the conforming class; the standard
// MethodHandler -> MethodWrapperEmitter pipeline then runs over that synthetic
// decl AND ProtocolExtensionEmitter still flushes its buffered @_cdecl wrapper.
// Both target the same C symbol; without cross-kind dedup in
// ModuleEmissionContext, swiftc rejects the wrapper file with
// "multiple definitions of symbol".

public protocol PExtDupSymProtocol {
    var statusFloor: Int32 { get }
}

extension PExtDupSymProtocol {
    public func acceptsStatus(_ code: Int32) -> Bool {
        return code >= statusFloor && code < statusFloor + 100
    }
}

public final class PExtDupSymHolder: PExtDupSymProtocol {
    public let statusFloor: Int32
    public init(statusFloor: Int32) { self.statusFloor = statusFloor }
}
