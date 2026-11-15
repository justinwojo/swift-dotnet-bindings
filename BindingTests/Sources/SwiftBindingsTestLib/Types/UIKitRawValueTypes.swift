// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if canImport(UIKit)
import UIKit

// MARK: - UIKit raw-value types on every route
//
// Swift imports these UIKit typedefs as structs over their raw value: UIFont.Weight over a
// CGFloat, UILayoutPriority over a Float, UIFont.TextStyle and UIContentSizeCategory over an
// NSString. The platform binding's types of the same names are int enums that number the
// constants instead, so a binding that projected onto them passed an int where Swift reads a
// CGFloat, a Float or an object, and sized a 4-byte buffer for an 8-byte weight. They project onto
// their raw carriers: double, float and NSString.
//
// The plain struct reaches Swift through @_cdecl wrappers; the generic box, whose parent the
// wrappers cannot specialize, through direct Swift-convention transitions that a native bridge
// carries. Each member computes from what it was passed, so a value read from the wrong register
// or the wrong bytes shows.

public struct UIKitRawValues {
    public init() {}

    /// A stored weight, read and written through the property wrappers.
    public var weight: UIFont.Weight = .regular

    /// The weight it was passed.
    public func echoWeight(_ weight: UIFont.Weight) -> UIFont.Weight { weight }

    /// The weight's raw value, as a Double.
    public func rawValue(ofWeight weight: UIFont.Weight) -> Double { Double(weight.rawValue) }

    /// `.bold`, built in Swift.
    public func boldWeight() -> UIFont.Weight { .bold }

    /// `.bold` when asked for one, otherwise nil.
    public func maybeBoldWeight(_ present: Bool) -> UIFont.Weight? { present ? .bold : nil }

    /// The weight's raw value, or -9 when there is none.
    public func rawValue(ofOptionalWeight weight: UIFont.Weight?) -> Double { Double(weight?.rawValue ?? -9) }

    /// The priority lowered by one.
    public func lowered(_ priority: UILayoutPriority) -> UILayoutPriority { UILayoutPriority(priority.rawValue - 1) }

    /// The style it was passed.
    public func echoStyle(_ style: UIFont.TextStyle) -> UIFont.TextStyle { style }

    /// `.title1` when asked for one, otherwise nil.
    public func maybeTitleStyle(_ present: Bool) -> UIFont.TextStyle? { present ? .title1 : nil }

    /// The style's raw value, or "none" when there is none.
    public func name(ofStyle style: UIFont.TextStyle?) -> String { style?.rawValue ?? "none" }

    /// The category one step larger than the one passed, or the one passed when Swift has no larger.
    public func larger(_ category: UIContentSizeCategory) -> UIContentSizeCategory {
        category == .large ? .extraLarge : category
    }
}

/// The same members on a struct generic over a protocol-constrained parameter and an Error.
public struct UIKitRawValueBox<Element: KeyedValue, Failure: Error> {
    var element: Element

    public init(element: Element) { self.element = element }

    /// The weight raised by a hundredth of the element's key.
    public func raised(_ weight: UIFont.Weight) -> UIFont.Weight {
        UIFont.Weight(rawValue: weight.rawValue + CGFloat(element.key) / 100)
    }

    /// The weight's raw value, as a Double.
    public func rawValue(ofWeight weight: UIFont.Weight) -> Double { Double(weight.rawValue) }

    /// `.bold`, built in Swift.
    public func boldWeight() -> UIFont.Weight { .bold }

    /// The priority lowered by the element's key.
    public func lowered(_ priority: UILayoutPriority) -> UILayoutPriority {
        UILayoutPriority(priority.rawValue - Float(element.key))
    }

    /// `.headline` for `.body`, `.body` for anything else.
    public func toggled(_ style: UIFont.TextStyle) -> UIFont.TextStyle { style == .body ? .headline : .body }

    /// `.title1` when asked for one, otherwise nil.
    public func maybeTitleStyle(_ present: Bool) -> UIFont.TextStyle? { present ? .title1 : nil }

    /// The style's raw value, or "none" when there is none.
    public func name(ofStyle style: UIFont.TextStyle?) -> String { style?.rawValue ?? "none" }

    /// The category one step larger than the one passed, or the one passed when Swift has no larger.
    public func larger(_ category: UIContentSizeCategory) -> UIContentSizeCategory {
        category == .large ? .extraLarge : category
    }
}
#endif
