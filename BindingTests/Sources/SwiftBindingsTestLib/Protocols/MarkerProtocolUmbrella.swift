// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Marker-protocol + extension-default + umbrella struct (AppIntents AssistantSchemas analogue)
//
// AppIntents 0.12.0 uses this pattern for AssistantSchemas:
//   @_marker public protocol BooksEnum : Model { }              // empty body, no requirements
//   extension BooksEnum {                                       // extension defaults
//       @_alwaysEmitIntoClient public var contentType: some Enum { ... }
//       @_alwaysEmitIntoClient public var font: some Enum { ... }
//       ...
//   }
//   public struct EnumSchema { }
//   extension EnumSchema : BooksEnum, CameraEnum, ... { }       // umbrella conformance, no body
//
// The Swift ABI digester flattens the extension-default vars INTO the BooksEnum
// protocol's children with `isFromExtension=true, protocolReq=false`. If the generator
// treats those flattened children as abstract C# interface requirements, every umbrella
// struct's conformance produces a CS0535 cascade.
//
// This fixture locks in the fix: protocol-extension defaults are filtered at the
// parser-population site so the C# interface emission only sees real protocol
// requirements.

/// Empty marker protocol — no requirements in the body.
public protocol MarkerBooksEnum { }

/// Protocol-extension default. Not a requirement of MarkerBooksEnum.
extension MarkerBooksEnum {
    public var booksDefaultLabel: String { "books" }
    public func booksDefaultDescribe() -> String { "books-marker" }
}

/// Second empty marker protocol with its own extension default.
public protocol MarkerCameraEnum { }

extension MarkerCameraEnum {
    public var cameraDefaultLabel: String { "camera" }
}

/// Umbrella struct with NO members of its own, conforming to two marker
/// protocols via empty conformance-only extensions. Mirrors AppIntents'
/// EnumSchema umbrella shape — must compile cleanly with no CS0535.
public struct MarkerUmbrellaSchema { }

extension MarkerUmbrellaSchema: MarkerBooksEnum { }
extension MarkerUmbrellaSchema: MarkerCameraEnum { }

/// Factory so the test can construct the umbrella and exercise the
/// extension-default invocation paths via concrete dispatch (Swift inlines
/// AEIC bodies at call sites — these calls verify the struct compiles and
/// runs, not that any C# interface member exists).
public func makeMarkerUmbrella() -> MarkerUmbrellaSchema {
    return MarkerUmbrellaSchema()
}
