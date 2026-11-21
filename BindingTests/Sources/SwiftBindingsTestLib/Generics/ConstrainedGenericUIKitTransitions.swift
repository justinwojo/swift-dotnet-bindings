// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if canImport(UIKit)
import UIKit

// MARK: - UIKit values through the direct transitions of a constrained generic struct
//
// The members of a struct generic over a protocol-constrained parameter and an Error reach Swift
// through direct Swift-convention imports (see ConstrainedGenericDirectTransitions.swift). A bridge
// carries such a transition only when the call facts know every type in it. These UIKit types are
// not in the bundled UIKit database: Objective-C classes (UIFont, UIBezierPath), whose declaration
// USR says they are one object reference, and UIKit's imported C structs and option sets, whose
// layouts are fixed.

public struct UIKitTransitionBox<Element: KeyedValue, Failure: Error> {
    var element: Element

    public init(element: Element) { self.element = element }

    /// A system font whose point size is the element's key.
    public func font() -> UIFont { UIFont.systemFont(ofSize: CGFloat(element.key)) }

    /// A system font whose point size is the element's key, or nil when not asked for one.
    public func font(present: Bool) -> UIFont? { present ? font() : nil }

    /// The font resized by the element's key: takes and returns an Objective-C class.
    public func scaled(_ font: UIFont) -> UIFont { font.withSize(font.pointSize + CGFloat(element.key)) }

    /// A font's point size plus the element's key.
    public func pointSize(of font: UIFont) -> Double { Double(font.pointSize) + Double(element.key) }

    /// A rectangle path as wide as the element's key and half as tall.
    public func path() -> UIBezierPath {
        UIBezierPath(rect: CGRect(x: 0, y: 0, width: CGFloat(element.key), height: CGFloat(element.key) / 2))
    }

    /// A rectangle path as wide as the given path plus the element's key, and as tall.
    public func widened(_ path: UIBezierPath) -> UIBezierPath {
        let b = path.bounds
        return UIBezierPath(rect: CGRect(x: 0, y: 0, width: b.width + CGFloat(element.key), height: b.height))
    }

    /// The width of a path's bounds.
    public func width(of path: UIBezierPath) -> Double { Double(path.bounds.width) }

    /// Insets built from the element's key: top = key, left = 2·key, bottom = 3·key, right = 4·key.
    public func insets() -> UIEdgeInsets {
        let k = CGFloat(element.key)
        return UIEdgeInsets(top: k, left: 2 * k, bottom: 3 * k, right: 4 * k)
    }

    /// The four insets weighted by position: top + 10·left + 100·bottom + 1000·right.
    public func weigh(_ insets: UIEdgeInsets) -> Double {
        Double(insets.top + 10 * insets.left + 100 * insets.bottom + 1000 * insets.right)
    }

    /// An offset of (key, -key).
    public func offset() -> UIOffset { UIOffset(horizontal: CGFloat(element.key), vertical: -CGFloat(element.key)) }

    /// The highlighted state, plus selected when asked.
    public func state(selected: Bool) -> UIControl.State { selected ? [.highlighted, .selected] : .highlighted }

    /// An option set's raw value.
    public func rawValue(of state: UIControl.State) -> UInt { state.rawValue }

    /// Touch-up-inside, plus value-changed when asked.
    public func events(valueChanged: Bool) -> UIControl.Event { valueChanged ? [.touchUpInside, .valueChanged] : .touchUpInside }

    /// An event set's raw value.
    public func rawEventValue(of events: UIControl.Event) -> UInt { events.rawValue }
}
#endif
