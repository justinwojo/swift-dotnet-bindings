// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if canImport(UIKit)
import UIKit
import Foundation

// MARK: - Members keyed by a UIKit NS_TYPED_ENUM string key
//
// `UIApplication.LaunchOptionsKey` and `UIApplication.OpenURLOptionsKey` are
// NS_TYPED_ENUM string keys: the Swift importer surfaces each as a nested struct wrapping
// an NSString, exactly like `NSAttributedString.Key` and `FileAttributeKey` in Foundation.
// Without a type-database record for them the whole dictionary is unresolvable, and every
// member whose only unbindable ingredient is the key type is silently dropped — an entire
// app-delegate overload family disappears while the sibling members of the same type bind.
//
// Shape observed in a social-login SDK's app-delegate forwarding surface, whose
// launch-options and open-URL overloads were the members that vanished.
//
// Shapes exercised: a dictionary parameter keyed by each of the two key types, a dictionary
// return keyed by one of them, and an Int32 sibling that binds either way so the fixture can
// tell "member dropped" apart from "type dropped".

public final class AppDelegateOptionRelay {
    public let forwardedCount: Int32

    public init(forwardedCount: Int32) {
        self.forwardedCount = forwardedCount
    }

    /// Counts the launch options handed to an app-delegate `didFinishLaunchingWithOptions:`.
    public func countLaunchOptions(_ options: [UIApplication.LaunchOptionsKey: Any]) -> Int32 {
        return Int32(options.count)
    }

    /// Counts the open-URL options handed to an app-delegate `open:options:`.
    public func countOpenURLOptions(_ options: [UIApplication.OpenURLOptionsKey: Any]) -> Int32 {
        return Int32(options.count)
    }

    /// Returns a launch-options dictionary so the key type is exercised in the return
    /// direction as well as the parameter direction.
    public func makeLaunchOptions(sourceApplication: String) -> [UIApplication.LaunchOptionsKey: Any] {
        return [UIApplication.LaunchOptionsKey.sourceApplication: sourceApplication]
    }
}

/// Free function keyed by the open-URL key type — the same shape outside a nominal type.
public func describeOpenURLOptions(_ options: [UIApplication.OpenURLOptionsKey: Any]) -> String {
    return options.isEmpty ? "empty" : "\(options.count) options"
}
#endif
