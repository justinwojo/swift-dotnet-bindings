// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Computed Properties

/// Frozen struct with multiple computed getters backed by stored fields.
@frozen
public struct ComputedProps {
    public var width: Double
    public var height: Double

    public init(width: Double, height: Double) {
        self.width = width
        self.height = height
    }

    /// Computed read-only: area.
    public var area: Double {
        return width * height
    }

    /// Computed read-only: perimeter.
    public var perimeter: Double {
        return 2.0 * (width + height)
    }

    /// Computed read-only: is square.
    public var isSquare: Bool {
        return width == height
    }

    /// Computed read-only: diagonal length.
    public var diagonal: Double {
        return (width * width + height * height).squareRoot()
    }
}
