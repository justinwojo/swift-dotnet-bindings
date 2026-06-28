// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - LocalizedStringResource projection (iOS 16+)
//
// Foundation.LocalizedStringResource is auto-bridged by Swift but is absent from
// the .NET Foundation assembly. The generator projects a bare top-level scalar
// LocalizedStringResource on the simple concrete wire path to a C# `string`:
// a parameter is rebuilt with `LocalizedStringResource(stringLiteral:)` and a
// return is resolved with `String(localized:)`. A resource constructed from a
// string literal with no localization table resolves back to that literal, so a
// String -> LocalizedStringResource -> String hop is an identity round-trip and is
// the durable runtime gate for the projection.
//
// Scalar-only: container / optional positions are NOT projected (they reference the
// unbindable type through a generic argument), so members carrying them stay dropped.

// LocalizedStringResource as a method parameter; the String return verifies the
// resource resolved on the Swift side.
@available(iOS 16.0, macOS 13.0, tvOS 16.0, watchOS 9.0, *)
public func localizedResourceToString(_ resource: LocalizedStringResource) -> String {
    String(localized: resource)
}

// LocalizedStringResource as a method return; the String parameter builds the resource.
@available(iOS 16.0, macOS 13.0, tvOS 16.0, watchOS 9.0, *)
public func makeLocalizedResource(from text: String) -> LocalizedStringResource {
    LocalizedStringResource(stringLiteral: text)
}

// A class with a LocalizedStringResource stored property (get + set) and a
// LocalizedStringResource constructor parameter.
@available(iOS 16.0, macOS 13.0, tvOS 16.0, watchOS 9.0, *)
public class LocalizedBanner {
    public var headline: LocalizedStringResource

    public init(headline: LocalizedStringResource) {
        self.headline = headline
    }

    // Resolves the current headline to a plain String for verification.
    public func headlineString() -> String {
        String(localized: headline)
    }
}

// Container/optional LocalizedStringResource positions must stay DROPPED: an
// Optional<LocalizedStringResource> parameter references the unbindable type
// through a generic argument, so the whole member is gate-dropped with an accurate
// NetUnavailableType reason. This member must NOT appear in the generated C#.
@available(iOS 16.0, macOS 13.0, tvOS 16.0, watchOS 9.0, *)
public func optionalLocalizedResource(_ resource: LocalizedStringResource?) -> String {
    guard let resource else { return "" }
    return String(localized: resource)
}
