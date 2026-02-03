// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Selector Types
// Tests: Selector parameter, #selector usage, @objc method referenced by selector
// Expected C#: IntPtr or Selector type for selector parameters
// Limitation: Selector type is not yet fully supported by the generator

/// Class with @objc methods that can be referenced by selector.
public class SelectorTarget: NSObject {
    public var lastAction: String = ""

    public override init() {
        super.init()
    }

    /// Method that can be used as a selector target.
    @objc public func handleAction() {
        lastAction = "handleAction"
    }

    /// Method with a sender parameter for selector targeting.
    @objc public func handleActionWithSender(_ sender: Any) {
        lastAction = "handleActionWithSender"
    }

    /// Returns the selector for handleAction.
    public func actionSelector() -> Selector {
        return #selector(handleAction)
    }

    /// Returns the selector for handleActionWithSender.
    public func actionWithSenderSelector() -> Selector {
        return #selector(handleActionWithSender(_:))
    }
}

// MARK: - Functions with Selector Parameters

/// Accepts a Selector parameter and returns its description.
public func selectorName(_ selector: Selector) -> String {
    return NSStringFromSelector(selector)
}

/// Checks whether an object responds to a given selector.
public func objectRespondsTo(_ object: NSObject, selector: Selector) -> Bool {
    return object.responds(to: selector)
}

// MARK: - Free Functions

/// Creates a SelectorTarget instance.
public func createSelectorTarget() -> SelectorTarget {
    return SelectorTarget()
}
