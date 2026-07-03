// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Enum case / computed-property name collision (FB-1 regression coverage)

/// A Swift enum may legally declare an associated-value case *and* a computed property that
/// share the same identifier. This is exactly the shape of Facebook's `SharePhoto.Source`
/// (`.image`/`.url`/`.asset` cases alongside `image`/`url`/`asset` accessors): both the case
/// constructor and the property project to the same C# name, and the property used to be dropped
/// entirely as a `DuplicateSignature`.
///
/// The generator now disambiguates the *property* side with the `Value` suffix
/// (`Image` → `ImageValue`) while the case constructor keeps the bare name (`Image(...)`), so a
/// consumer can both build a case and read the accessor. The rename is property-only — routing it
/// through the shared rename dict would also rename the case and recreate the collision.
public enum ShareSource {
    case image(Int32)
    case link(String)
    case blob(Int32)

    /// Colliding computed property: the image dimension for an `.image` case, else 0.
    /// Projects to `Image`, which collides with the `Image(...)` case constructor →
    /// recovered as `ImageValue`.
    public var image: Int32 {
        if case let .image(v) = self { return v }
        return 0
    }

    /// Colliding computed property: the link text for a `.link` case, else "".
    /// Recovered as `LinkValue`.
    public var link: String {
        if case let .link(s) = self { return s }
        return ""
    }

    /// Colliding computed property: the blob size for a `.blob` case, else -1.
    /// Recovered as `BlobValue`.
    public var blob: Int32 {
        if case let .blob(v) = self { return v }
        return -1
    }
}
