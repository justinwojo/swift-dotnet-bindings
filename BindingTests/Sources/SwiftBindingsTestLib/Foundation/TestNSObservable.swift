// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - KVO observable target

/// NSObject subclass with @objc dynamic stored properties so the runtime
/// KVO machinery can observe them. Each property exercises a different
/// value-type shape through the change handler: Int (primitive 8B),
/// String (Foundation-bridged).
public class TestNSObservable: NSObject {
    @objc dynamic public var counter: Int = 0
    @objc dynamic public var name: String = ""
    @objc dynamic public var enabled: Bool = false

    public override init() {
        super.init()
    }
}

/// Factory — C# test obtains an instance through this rather than
/// constructing TestNSObservable directly. Lets the C# binding land
/// without needing a public ctor surface.
public func makeTestNSObservable() -> TestNSObservable {
    return TestNSObservable()
}

/// Mutator helpers — C# test uses these instead of writing the
/// properties from C# (Tj dispatch for @objc dynamic properties is
/// the variable we are NOT trying to validate here).
public func mutateCounter(_ obj: TestNSObservable, _ value: Int) {
    obj.counter = value
}

public func mutateName(_ obj: TestNSObservable, _ value: String) {
    obj.name = value
}

public func mutateEnabled(_ obj: TestNSObservable, _ value: Bool) {
    obj.enabled = value
}
